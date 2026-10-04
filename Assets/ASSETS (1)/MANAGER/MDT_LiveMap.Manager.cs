using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// ═══════════════════════════════════════════════════════════════════════════════
//  MDT_LiveMap — Route Manager view.
//
//  Reuses the live map's own data (road geometry and widths, route paths, stops and
//  the world snapshot) but draws it full-screen in the manager's look:
//    · pale roads, as wide as the real roads, on a dark muted blue-green background
//    · each bus is a picture of its fleet series with "#1234" and its lateness below
//    · zoomed out, nearby buses merge into numbered dots
//    · click a bus to select it, double-click (or the card's Follow button) to follow it
//
//  Speed: the roads are drawn with GL into a RenderTexture, only when the view
//  changes, then shown as one picture. The old way (one IMGUI line per road segment)
//  was the main reason the map crawled.
//  The normal map is untouched: its Update/OnGUI step aside while this is active.
// ═══════════════════════════════════════════════════════════════════════════════
public partial class MDT_LiveMap
{
    // ── Look ─────────────────────────────────────────────────────────────────
    private static readonly Color MgrBg      = new Color(0.115f, 0.205f, 0.215f, 1f);   // dark, muted blue-green
    private static readonly Color MgrRoad    = new Color(0.86f, 0.90f, 0.90f, 1f);      // soft white, easy on the eyes
    private static readonly Color MgrRoute   = new Color(1f, 0.78f, 0.25f, 1f);
    private static readonly Color MgrStop    = new Color(0.07f, 0.17f, 0.18f, 1f);
    private static readonly Color MgrBroken  = new Color(1f, 0.38f, 0.34f, 1f);
    private static readonly Color MgrPick    = Color.white;
    private const float MgrClusterBelowZoom  = 0.9f;
    private const float MgrClusterCellPx     = 64f;

    // ── State ────────────────────────────────────────────────────────────────
    private bool  _mgr;
    private bool  _mgrNeedFit;
    private float _mgrBusRefresh;
    private float _mgrGeoRefresh;
    private bool  _mgrDown, _mgrDragging;
    private Vector2 _mgrDownPos;

    /// <summary>Route number whose path is drawn in gold ("" = none).</summary>
    public string ManagerFocusRoute = "";
    /// <summary>Bus (busID) with the white outline, or -1.</summary>
    public int ManagerSelectedBus = -1;
    /// <summary>Bus (busID) the map is following, or -1. Dragging the map stops it.</summary>
    public int ManagerFollowBus = -1;

    private int    _mgrClickBus = int.MinValue;
    private string _mgrClickStop;

    private struct MgrBus
    {
        public Vector3 pos;
        public int busID, fleet;
        public string route, variant, series;
        public bool outbound, west, broken, held, fixedByManager, hasLate, offService, hasWait;
        public float late, waitMin;
        public Color color;
    }

    /// <summary>Stop code with the gold ring and name on the map (set by the Tracker tab), or null.</summary>
    public string ManagerSelectedStop;

    // ── Touch ────────────────────────────────────────────────────────────────
    /// <summary>The map's rectangle in the same (scaled) GUI space the manager screen draws in. Set every draw.</summary>
    public Rect ManagerArea;
    /// <summary>How many real pixels one GUI unit is (the manager screen scales its whole UI up on phones).</summary>
    public float ManagerPixelScale = 1f;
    /// <summary>True on a touch screen: bigger tap targets.</summary>
    public bool ManagerTouchMode;
    private bool _mgrPinching;
    private float _mgrPinchPrev;
    private Vector2 _mgrPinchCenterPrev;
    private float _mgrNoClickUntil;
    private readonly List<MgrBus> _mgrBuses = new List<MgrBus>(512);
    private struct MgrHit { public Rect r; public int busID; public bool cluster; }
    private readonly List<MgrHit> _mgrHits = new List<MgrHit>(256);
    private readonly Dictionary<long, List<int>> _mgrCells = new Dictionary<long, List<int>>();
    private readonly Dictionary<string, Texture2D> _mgrIcons = new Dictionary<string, Texture2D>();
    private readonly Dictionary<string, Texture2D> _mgrIconsGray = new Dictionary<string, Texture2D>();
    private GUIStyle _mgrCount, _mgrTip, _mgrLabel, _mgrFleet, _mgrTag, _mgrSub;

    // Roads are drawn into this, only when the view changed.
    private RenderTexture _mgrRT;
    private Material _mgrGL;
    private bool _mgrGLFailed;
    private int _mgrViewHash = int.MinValue;
    private bool _mgrRoadsDirty = true;

    public bool ManagerActive => _mgr;
    public int  ManagerBusCount => _mgrBuses.Count;

    // ── Lifecycle ────────────────────────────────────────────────────────────
    public void ManagerBegin()
    {
        _mgr = true;
        _followPlayer = false; _followBusID = -1;
        _routeFilterIndex = -2; _directionFilter = -1;
        _etaPopupFleet = -1; _selectedStopCode = "";
        _panOffset = Vector2.zero;
        ManagerFocusRoute = ""; ManagerSelectedBus = -1; ManagerFollowBus = -1;
        _dataDirty = true; _mgrRoadsDirty = true; _mgrViewHash = int.MinValue;
        SnapshotWorld();
        RefreshMgrBuses();
        _mgrNeedFit = true;
    }

    public void ManagerEnd()
    {
        _mgr = false;
        _followPlayer = true; _followBusID = -1;
        _panOffset = Vector2.zero; _zoom = 1f;
        _dataDirty = true;
        ManagerFollowBus = -1;
        if (_mgrRT != null) { _mgrRT.Release(); Destroy(_mgrRT); _mgrRT = null; }
    }

    private Vector2 MgrTouchToGui(Vector2 screenPos) => new Vector2(screenPos.x, Screen.height - screenPos.y) / Mathf.Max(0.01f, ManagerPixelScale);

    /// <summary>Two fingers: pinch to zoom and drag to pan. One finger arrives as the usual mouse events (drag = pan, tap = click).</summary>
    private void MgrTouch()
    {
        if (Input.touchCount >= 2)
        {
            Vector2 p0 = MgrTouchToGui(Input.GetTouch(0).position), p1 = MgrTouchToGui(Input.GetTouch(1).position);
            Vector2 center = (p0 + p1) * 0.5f;
            float dist = Vector2.Distance(p0, p1);
            if (!_mgrPinching)
            {
                if (ManagerArea.Contains(center)) { _mgrPinching = true; _mgrPinchPrev = dist; _mgrPinchCenterPrev = center; }
            }
            else if (dist > 1f && _mgrPinchPrev > 1f)
            {
                int w = Mathf.Max(10, (int)ManagerArea.width), mh = Mathf.Max(10, (int)ManagerArea.height);
                Vector2 local = center - ManagerArea.position;
                // While following a bus, zoom around the middle so the bus stays put and the map doesn't slide.
                Vector2 about = ManagerFollowBus >= 0 ? new Vector2(w * 0.5f, mh * 0.5f) : local;
                MgrZoomAbout(about, dist / _mgrPinchPrev, w, mh);
                if (ManagerFollowBus < 0) _panOffset += center - _mgrPinchCenterPrev;
                _mgrPinchPrev = dist; _mgrPinchCenterPrev = center;
            }
            _mgrNoClickUntil = Time.unscaledTime + 0.3f;
            _mgrDown = false; _mgrDragging = false;
        }
        else if (_mgrPinching)
        {
            _mgrPinching = false;
            _mgrNoClickUntil = Time.unscaledTime + 0.3f; // the fingers lifting must not count as a tap
        }
    }

    /// <summary>Zoom in or out from the middle of the map (the on-screen + and − buttons).</summary>
    public void ManagerZoomBy(float factor)
    {
        int w = Mathf.Max(10, (int)ManagerArea.width), mh = Mathf.Max(10, (int)ManagerArea.height);
        MgrZoomAbout(new Vector2(w * 0.5f, mh * 0.5f), factor, w, mh);
    }

    private void ManagerUpdate()
    {
        MgrTouch();
        _mgrBusRefresh -= Time.unscaledDeltaTime;
        if (_mgrBusRefresh <= 0f) { _mgrBusRefresh = 0.35f; RefreshMgrBuses(); }

        // Roads never change; road events and detours can, so redo the geometry only now and then.
        _mgrGeoRefresh -= Time.unscaledDeltaTime;
        if (_mgrGeoRefresh <= 0f) { _mgrGeoRefresh = 15f; SnapshotWorld(); _mgrRoadsDirty = true; }
    }

    private void RefreshMgrBuses()
    {
        _mgrBuses.Clear();
        var sched = BusScheduler.Instance;
        foreach (var kv in BusRegistry.ActiveBuses)
        {
            var c = kv.Value;
            if (c == null) continue;
            var st = c.State;
            // Parked, in the garage or on a maintenance stop: not on the map.
            if (st == NPCBusController.BusState.Idle || st == NPCBusController.BusState.WaitingAtDepot
                || st == NPCBusController.BusState.AtMaintenanceBay || st == NPCBusController.BusState.AtFuelStation) continue;

            bool off = st == NPCBusController.BusState.DepotIngress || st == NPCBusController.BusState.DrivingToMaintenanceBay
                       || st == NPCBusController.BusState.DrivingToFuelStation;

            float late = 0f, waitMin = 0f; bool hasLate = false, hasWait = false;
            if (sched != null && !off && sched.TryGetAssignedSlot(kv.Key, out var slot) && slot != null)
            {
                if (slot.state == SlotState.InService) { late = slot.latenessMinutes; hasLate = true; }
                else if (slot.state == SlotState.AssignedNPC && SimClock.Instance != null)
                {
                    // Waiting for its next departure (at the end of the line, or on its way to the start).
                    float untilDep = slot.scheduledDeparture - SimClock.Instance.AbsoluteGameMinutes;
                    if (untilDep > 0f) { waitMin = untilDep; hasWait = true; }
                }
            }

            _mgrBuses.Add(new MgrBus
            {
                pos = c.transform.position,
                busID = kv.Key,
                fleet = c.fleetNumber,
                route = c.CurrentRoute != null ? c.CurrentRoute.routeNumber : "",
                variant = c.variantLetter,
                series = FleetMetadata.Get(c.fleetNumber)?.seriesName,
                outbound = c.IsOutbound,
                west = (-c.transform.forward).x < 0f,
                broken = BusBreakdownSystem.Instance != null && BusBreakdownSystem.Instance.IsBusLocked(kv.Key),
                held = c.ManagerHold,
                fixedByManager = ManagerLocks.IsBusLocked(kv.Key),
                late = late, hasLate = hasLate, waitMin = waitMin, hasWait = hasWait, offService = off,
                color = off ? new Color(0.7f, 0.75f, 0.78f) : (c.CurrentRoute != null ? c.CurrentRoute.routeColor : Color.white),
            });
        }
    }

    // ── Public helpers for the manager screen ────────────────────────────────
    public bool ManagerFindBus(int busID, out Vector3 pos)
    {
        foreach (var b in _mgrBuses) if (b.busID == busID) { pos = b.pos; return true; }
        pos = default; return false;
    }

    public void ManagerCenterOn(Vector3 world)
    {
        _worldCentre = world; _panOffset = Vector2.zero;
        if (_zoom < 1.2f) _zoom = 1.2f;
    }

    public void ManagerFitAll() { _mgrNeedFit = true; ManagerFollowBus = -1; }

    /// <summary>Latest click on the map: a bus (busID), nothing (-1 = empty ground) or no click (int.MinValue).</summary>
    public void ManagerConsumeClick(out int busID, out string stopCode)
    {
        busID = _mgrClickBus; stopCode = _mgrClickStop;
        _mgrClickBus = int.MinValue; _mgrClickStop = null;
    }

    // ── Drawing ──────────────────────────────────────────────────────────────
    public void ManagerDraw(Rect area)
    {
        if (!_mgr) return;
        BuildStyles();
        EnsureMgrStyles();

        int w = Mathf.Max(10, (int)area.width), mh = Mathf.Max(10, (int)area.height);
        ManagerArea = area;

        // Follow: keep the followed bus in the middle.
        if (ManagerFollowBus >= 0)
        {
            if (ManagerFindBus(ManagerFollowBus, out var fp)) { _worldCentre = fp; _panOffset = Vector2.zero; }
            else ManagerFollowBus = -1;
        }

        GUI.BeginGroup(area);
        if (_mgrNeedFit) { MgrFit(w, mh); _mgrNeedFit = false; _mgrRoadsDirty = true; }
        MgrInput(w, mh);
        GUI.EndGroup();

        if (Event.current.type == EventType.Repaint)
        {
            RenderRoads(w, mh);
            GUI.BeginGroup(area);
            MgrPaint(w, mh);
            GUI.EndGroup();
        }
    }

    private void EnsureMgrStyles()
    {
        if (_mgrCount != null) return;
        _mgrCount = MDT_UITheme.MakeLabel(14, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
        _mgrTip   = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.MiddleLeft, Color.white);
        _mgrLabel = MDT_UITheme.MakeLabel(11, FontStyle.Bold, TextAnchor.MiddleCenter, new Color(0.04f, 0.14f, 0.15f, 1f));
        _mgrFleet = MDT_UITheme.MakeLabel(11, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
        _mgrTag   = MDT_UITheme.MakeLabel(13, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
        _mgrSub   = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.MiddleCenter, Color.white);
    }

    private void MgrFit(int w, int mh)
    {
        Vector2 mn = new Vector2(float.MaxValue, float.MaxValue), mx = new Vector2(float.MinValue, float.MinValue);
        foreach (var rl in _roadLines)
        {
            if (rl.layer != 0) continue;
            mn = Vector2.Min(mn, rl.minB); mx = Vector2.Max(mx, rl.maxB);
        }
        if (mn.x > mx.x) return;
        _worldCentre = new Vector3((mn.x + mx.x) * 0.5f, _worldCentre.y, (mn.y + mx.y) * 0.5f);
        float radius = Mathf.Max((mx.x - mn.x), (mx.y - mn.y)) * 0.5f;
        _panOffset = Vector2.zero;
        // WorldToScreen: baseScale = min(w, mh) * 0.45 / viewRadius; fit radius into ~0.48 of the short side.
        _zoom = Mathf.Clamp(viewRadius / Mathf.Max(radius, 1f) * (0.48f / 0.45f), 0.04f, 14f);
    }

    private void MgrZoomAbout(Vector2 cursor, float factor, int w, int mh)
    {
        Vector3 wp = ScreenToWorld(cursor, w, mh);
        _zoom = Mathf.Clamp(_zoom * factor, 0.04f, 14f);
        Vector2 sp = WorldToScreen(wp, w, mh);
        _panOffset += cursor - sp;
    }

    private void MgrInput(int w, int mh)
    {
        var e = Event.current;
        var local = new Rect(0, 0, w, mh);
        Vector2 m = e.mousePosition;

        if (e.type == EventType.ScrollWheel && local.Contains(m))
        {
            // While following, zoom around the middle so the followed bus stays put.
            Vector2 about = ManagerFollowBus >= 0 ? new Vector2(w * 0.5f, mh * 0.5f) : m;
            MgrZoomAbout(about, e.delta.y < 0f ? 1.18f : 1f / 1.18f, w, mh);
            e.Use();
        }
        else if (e.type == EventType.MouseDown && local.Contains(m))
        {
            if (!_mgrPinching && Input.touchCount < 2) { _mgrDown = true; _mgrDragging = false; _mgrDownPos = m; }
            e.Use();
        }
        else if (e.type == EventType.MouseDrag && _mgrDown)
        {
            // A finger that moves a little is still a tap; a thumb needs more room than a mouse.
            float slop = ManagerTouchMode ? 14f : 4f;
            if (!_mgrDragging && (m - _mgrDownPos).sqrMagnitude > slop * slop) { _mgrDragging = true; ManagerFollowBus = -1; }
            if (_mgrDragging && !_mgrPinching) _panOffset += e.delta;
            e.Use();
        }
        else if (e.type == EventType.MouseUp && _mgrDown)
        {
            _mgrDown = false;
            if (!_mgrDragging && e.button == 0 && !_mgrPinching && Time.unscaledTime >= _mgrNoClickUntil) MgrClick(m, w, mh, e.clickCount >= 2);
            _mgrDragging = false;
            e.Use();
        }
    }

    private void MgrClick(Vector2 p, int w, int mh, bool doubleClick)
    {
        for (int i = _mgrHits.Count - 1; i >= 0; i--)
        {
            var h = _mgrHits[i];
            if (!h.r.Contains(p)) continue;
            if (h.cluster) { MgrZoomAbout(h.r.center, 1.9f, w, mh); return; }
            _mgrClickBus = h.busID;
            if (doubleClick) ManagerFollowBus = h.busID;
            return;
        }

        // A stop?
        string bestCode = null; float best = ManagerTouchMode ? 24f : 11f;
        foreach (var sd in _stopDots)
        {
            var sp = WorldToScreen(sd.worldPos, w, mh);
            float d = Vector2.Distance(sp, p);
            if (d < best) { best = d; bestCode = sd.stopCode; }
        }
        if (bestCode != null) { _mgrClickStop = bestCode; return; }
        _mgrClickBus = -1; // empty ground
    }

    // ── Icons: smooth, with mipmaps (the stock ones looked jagged when drawn small) ────
    private Texture2D MgrIcon(string series)
    {
        if (string.IsNullOrEmpty(series)) return null;
        if (_mgrIcons.TryGetValue(series, out var t)) return t;
        var src = Resources.Load<Texture2D>("SeriesIcons/" + series);
        t = src != null ? MakeSmooth(src) : null;
        _mgrIcons[series] = t; // a missing icon is remembered too, so it isn't searched for every frame
        return t;
    }

    // A black-and-white copy of the same picture, for buses that are not in service.
    private Texture2D MgrIconGray(string series)
    {
        if (string.IsNullOrEmpty(series)) return null;
        if (_mgrIconsGray.TryGetValue(series, out var g)) return g;
        var color = MgrIcon(series);
        if (color != null)
        {
            try
            {
                var px = color.GetPixels32();
                for (int i = 0; i < px.Length; i++)
                {
                    byte y = (byte)Mathf.Clamp(Mathf.RoundToInt(px[i].r * 0.299f + px[i].g * 0.587f + px[i].b * 0.114f), 0, 255);
                    px[i].r = y; px[i].g = y; px[i].b = y;
                }
                g = new Texture2D(color.width, color.height, TextureFormat.RGBA32, true, false) { hideFlags = HideFlags.HideAndDontSave };
                g.SetPixels32(px); g.Apply(true);
                g.filterMode = FilterMode.Trilinear; g.anisoLevel = 4; g.wrapMode = TextureWrapMode.Clamp;
            }
            catch (Exception) { g = color; }
        }
        _mgrIconsGray[series] = g;
        return g;
    }

    private static Texture2D MakeSmooth(Texture2D src)
    {
        const int maxSide = 512;
        float k = Mathf.Min(1f, maxSide / (float)Mathf.Max(src.width, src.height));
        int tw = Mathf.Max(8, Mathf.RoundToInt(src.width * k)), th = Mathf.Max(8, Mathf.RoundToInt(src.height * k));
        var rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var prev = RenderTexture.active;
        try
        {
            src.filterMode = FilterMode.Bilinear;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var t = new Texture2D(tw, th, TextureFormat.RGBA32, true, false) { hideFlags = HideFlags.HideAndDontSave };
            t.ReadPixels(new Rect(0, 0, tw, th), 0, 0);
            t.Apply(true);                       // builds the mipmap chain
            t.filterMode = FilterMode.Trilinear;
            t.anisoLevel = 4;
            t.wrapMode = TextureWrapMode.Clamp;
            return t;
        }
        catch (Exception) { return src; }
        finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); }
    }

    // ── Roads, drawn with GL into a RenderTexture ────────────────────────────
    private bool EnsureGL()
    {
        if (_mgrGL != null) return true;
        if (_mgrGLFailed) return false;
        var sh = Shader.Find("Hidden/Internal-Colored");
        if (sh == null) { _mgrGLFailed = true; return false; }
        _mgrGL = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        _mgrGL.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        _mgrGL.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        _mgrGL.SetInt("_Cull", (int)CullMode.Off);
        _mgrGL.SetInt("_ZWrite", 0);
        return true;
    }

    private void RenderRoads(int w, int mh)
    {
        if (!EnsureGL()) return;

        // The picture is made in real pixels (the screen may be scaled up on a phone) so it stays sharp.
        int pw = Mathf.Clamp(Mathf.RoundToInt(w * ManagerPixelScale), 16, 4096), ph = Mathf.Clamp(Mathf.RoundToInt(mh * ManagerPixelScale), 16, 4096);
        if (_mgrRT == null || _mgrRT.width != pw || _mgrRT.height != ph)
        {
            if (_mgrRT != null) { _mgrRT.Release(); Destroy(_mgrRT); }
            _mgrRT = new RenderTexture(pw, ph, 0, RenderTextureFormat.ARGB32) { antiAliasing = ManagerTouchMode ? 2 : 4, hideFlags = HideFlags.HideAndDontSave };
            _mgrRT.Create();
            _mgrRoadsDirty = true;
        }

        int hash = ((_zoom.GetHashCode() * 397 ^ _panOffset.x.GetHashCode()) * 397 ^ _panOffset.y.GetHashCode()) * 397
                   ^ _worldCentre.x.GetHashCode() * 31 ^ _worldCentre.z.GetHashCode() ^ (ManagerFocusRoute ?? "").GetHashCode();
        if (!_mgrRoadsDirty && hash == _mgrViewHash) return;
        _mgrViewHash = hash; _mgrRoadsDirty = false;

        float baseScale = Mathf.Min(w, mh) * 0.45f / Mathf.Max(viewRadius, 1f);
        float scale = baseScale * _zoom;

        const float pad = 40f;
        Vector3 vA = ScreenToWorld(new Vector2(-pad, -pad), w, mh), vB = ScreenToWorld(new Vector2(w + pad, mh + pad), w, mh);
        Vector2 vMin = new Vector2(Mathf.Min(vA.x, vB.x), Mathf.Min(vA.z, vB.z)), vMax = new Vector2(Mathf.Max(vA.x, vB.x), Mathf.Max(vA.z, vB.z));

        var prev = RenderTexture.active;
        try
        {
            RenderTexture.active = _mgrRT;
            GL.Clear(false, true, MgrBg);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, w, 0, mh);
            _mgrGL.SetPass(0);
            GL.Begin(GL.QUADS);

            foreach (var rl in _roadLines)
            {
                if (rl.layer != 0) continue;
                if (rl.maxB.x < vMin.x || rl.minB.x > vMax.x || rl.maxB.y < vMin.y || rl.minB.y > vMax.y) continue;
                float px = Mathf.Max(2.2f, (rl.worldWidth > 0.1f ? rl.worldWidth : 7f) * scale);
                GLPolyline(rl.pts, px, MgrRoad, w, mh);
            }

            // The focused route's path, as a gold stripe down the middle of its roads.
            if (!string.IsNullOrEmpty(ManagerFocusRoute))
            {
                float stripe = Mathf.Max(3f, 3.4f * scale);
                if (_sampleCache.TryGetValue(ManagerFocusRoute + ":out", out var o)) GLPolyline(o, stripe, MgrRoute, w, mh);
                if (_sampleCache.TryGetValue(ManagerFocusRoute + ":in", out var i2)) GLPolyline(i2, stripe, MgrRoute, w, mh);
            }

            GL.End();
            GL.PopMatrix();
        }
        finally { RenderTexture.active = prev; }
    }

    // One thick line per segment (a quad), with a little extra at each end so corners stay joined.
    private void GLPolyline(Vector3[] pts, float px, Color col, int w, int mh)
    {
        if (pts == null || pts.Length < 2) return;
        float hw = px * 0.5f;
        GL.Color(col);
        Vector2 prev = WorldToScreen(pts[0], w, mh);
        for (int i = 1; i < pts.Length; i++)
        {
            Vector2 cur = WorldToScreen(pts[i], w, mh);
            if (!((prev.x < -hw && cur.x < -hw) || (prev.x > w + hw && cur.x > w + hw) ||
                  (prev.y < -hw && cur.y < -hw) || (prev.y > mh + hw && cur.y > mh + hw)))
            {
                Vector2 d = cur - prev; float len = d.magnitude;
                if (len > 0.001f)
                {
                    d /= len;
                    Vector2 n = new Vector2(-d.y, d.x) * hw;
                    Vector2 a = prev - d * hw, b = cur + d * hw;
                    // screen y points down, the RenderTexture's y points up
                    GL.Vertex3(a.x - n.x, mh - (a.y - n.y), 0);
                    GL.Vertex3(a.x + n.x, mh - (a.y + n.y), 0);
                    GL.Vertex3(b.x + n.x, mh - (b.y + n.y), 0);
                    GL.Vertex3(b.x - n.x, mh - (b.y - n.y), 0);
                }
            }
            prev = cur;
        }
    }

    // ── Painting (everything except the roads) ───────────────────────────────
    private void MgrPaint(int w, int mh)
    {
        if (_mgrRT != null && !_mgrGLFailed) GUI.DrawTexture(new Rect(0, 0, w, mh), _mgrRT, ScaleMode.StretchToFill, false);
        else
        {
            // Fallback if the GL shader isn't available in this build: plain IMGUI lines (works, but slower).
            MDT_UITheme.DrawRect(new Rect(0, 0, w, mh), MgrBg);
            float sc = Mathf.Min(w, mh) * 0.45f / Mathf.Max(viewRadius, 1f) * _zoom;
            const float fpad = 30f;
            Vector3 fa = ScreenToWorld(new Vector2(-fpad, -fpad), w, mh), fb = ScreenToWorld(new Vector2(w + fpad, mh + fpad), w, mh);
            Vector2 fMin = new Vector2(Mathf.Min(fa.x, fb.x), Mathf.Min(fa.z, fb.z)), fMax = new Vector2(Mathf.Max(fa.x, fb.x), Mathf.Max(fa.z, fb.z));
            foreach (var rl in _roadLines)
            {
                if (rl.layer != 0 || rl.pts == null || rl.pts.Length < 2) continue;
                if (rl.maxB.x < fMin.x || rl.minB.x > fMax.x || rl.maxB.y < fMin.y || rl.minB.y > fMax.y) continue;
                float px = Mathf.Clamp((rl.worldWidth > 0.1f ? rl.worldWidth : 7f) * sc, 2.2f, 24f);
                Vector2 prevP = WorldToScreen(rl.pts[0], w, mh);
                for (int i = 1; i < rl.pts.Length; i += 2)
                {
                    int j = Mathf.Min(i + 1, rl.pts.Length - 1);
                    Vector2 cur = WorldToScreen(rl.pts[j], w, mh);
                    MDT_UITheme.DrawLine(prevP, cur, MgrRoad, px);
                    prevP = cur;
                }
            }
        }

        const float pad = 30f;
        var cull = new Rect(-pad, -pad, w + pad * 2f, mh + pad * 2f);

        // Stops
        bool showAllStops = _zoom > 1.4f;
        foreach (var sd in _stopDots)
        {
            if (!showAllStops && !sd.isTerminal) continue;
            Vector2 sp = WorldToScreen(sd.worldPos, w, mh);
            if (!cull.Contains(sp)) continue;
            float r = sd.isTerminal ? 6f : 3.5f;
            if (sd.isTerminal)
                MDT_UITheme.DrawRect(new Rect(sp.x - r - 2, sp.y - r - 2, (r + 2) * 2, (r + 2) * 2), MgrRoute);
            MDT_UITheme.DrawRect(new Rect(sp.x - r, sp.y - r, r * 2, r * 2), sd.isTerminal ? MgrStop : new Color(MgrStop.r, MgrStop.g, MgrStop.b, 0.9f));
            if (sd.isTerminal && _zoom > 0.9f)
            {
                var lr = new Rect(sp.x - 80, sp.y - r - 22, 160, 16);
                GUI.Label(new Rect(lr.x + 1, lr.y + 1, lr.width, lr.height), sd.stopName, _mgrLabel);
                GUI.Label(lr, sd.stopName, _mgrFleet);
            }
        }

        // The stop picked in the Tracker tab: a gold ring and its name, always visible.
        if (!string.IsNullOrEmpty(ManagerSelectedStop))
            foreach (var sd in _stopDots)
            {
                if (sd.stopCode != ManagerSelectedStop) continue;
                Vector2 sp = WorldToScreen(sd.worldPos, w, mh);
                MDT_UITheme.DrawRoundedRectBordered(new Rect(sp.x - 13, sp.y - 13, 26, 26), 13f, new Color(1f, 0.8f, 0.25f, 0.22f), MgrRoute, 3);
                var nameR = new Rect(sp.x - 110, sp.y - 38, 220, 18);
                GUI.Label(new Rect(nameR.x + 1, nameR.y + 1, nameR.width, nameR.height), sd.stopName, _mgrLabel);
                GUI.Label(nameR, sd.stopName, _mgrFleet);
                break;
            }

        // Buses
        _mgrHits.Clear();
        MgrBus? hover = null;
        Vector2 mouse = Event.current.mousePosition;
        bool cluster = _zoom < MgrClusterBelowZoom;
        float size = Mathf.Clamp(40f + _zoom * 28f, 44f, 110f);

        if (cluster)
        {
            _mgrCells.Clear();
            for (int i = 0; i < _mgrBuses.Count; i++)
            {
                Vector2 sp = WorldToScreen(_mgrBuses[i].pos, w, mh);
                if (!cull.Contains(sp)) continue;
                long key = ((long)Mathf.FloorToInt(sp.x / MgrClusterCellPx) << 32) ^ (uint)Mathf.FloorToInt(sp.y / MgrClusterCellPx);
                if (!_mgrCells.TryGetValue(key, out var l)) { l = new List<int>(4); _mgrCells[key] = l; }
                l.Add(i);
            }
            foreach (var kv in _mgrCells)
            {
                var l = kv.Value;
                if (l.Count == 1) { DrawMgrBus(_mgrBuses[l[0]], size * 0.8f, w, mh, mouse, ref hover, false); continue; }
                Vector2 c = Vector2.zero; bool anyBroken = false, anyPick = false;
                foreach (int idx in l) { c += WorldToScreen(_mgrBuses[idx].pos, w, mh); anyBroken |= _mgrBuses[idx].broken; anyPick |= _mgrBuses[idx].busID == ManagerSelectedBus; }
                c /= l.Count;
                float d = 32f + Mathf.Min(16f, l.Count);
                var r = new Rect(c.x - d * 0.5f, c.y - d * 0.5f, d, d);
                MDT_UITheme.DrawRoundedRectBordered(r, d * 0.5f, new Color(0.05f, 0.12f, 0.13f, 0.96f), anyBroken ? MgrBroken : (anyPick ? MgrPick : new Color(0.85f, 0.92f, 0.92f)), 2);
                GUI.Label(r, l.Count.ToString(), _mgrCount);
                _mgrHits.Add(new MgrHit { r = r, cluster = true });
            }
        }
        else
        {
            // Selected bus last, so its picture and label are on top.
            for (int i = 0; i < _mgrBuses.Count; i++) if (_mgrBuses[i].busID != ManagerSelectedBus) DrawMgrBus(_mgrBuses[i], size, w, mh, mouse, ref hover, true);
            for (int i = 0; i < _mgrBuses.Count; i++) if (_mgrBuses[i].busID == ManagerSelectedBus) DrawMgrBus(_mgrBuses[i], size, w, mh, mouse, ref hover, true);
        }

        if (ManagerFollowBus >= 0)
        {
            var fr = new Rect(w * 0.5f - 90, 10, 180, 26);
            MDT_UITheme.DrawRoundedRectBordered(fr, 13f, new Color(0.05f, 0.12f, 0.13f, 0.92f), MgrRoute, 1);
            GUI.Label(fr, "Following · drag to stop", _mgrSub);
        }

        if (hover.HasValue)
        {
            var b = hover.Value;
            string t = b.offService
                ? $"Bus {b.fleet}   ·   Not in service"
                : $"Bus {b.fleet}   ·   Route {b.route}{b.variant} ({ManagerWords.Direction(b.outbound)})" +
                  (b.broken ? "   ·   Broken down" : b.held ? "   ·   Held" : "");
            float tw = 24f + t.Length * 6.8f;
            var tr = new Rect(Mathf.Min(mouse.x + 14f, w - tw - 4f), Mathf.Min(mouse.y + 18f, mh - 30f), tw, 26f);
            MDT_UITheme.DrawRoundedRectBordered(tr, 6f, new Color(0.04f, 0.12f, 0.13f, 0.97f), new Color(1f, 1f, 1f, 0.45f), 1);
            GUI.Label(new Rect(tr.x + 10, tr.y, tr.width - 10, tr.height), t, _mgrTip);
        }
    }

    private static string LateText(MgrBus b, out Color col)
    {
        if (!b.hasLate) { col = new Color(0.85f, 0.92f, 0.92f); return ""; }
        int m = Mathf.RoundToInt(b.late);
        if (m >= 1) { col = m >= 5 ? new Color(1f, 0.55f, 0.50f) : new Color(1f, 0.82f, 0.45f); return m + " min late"; }
        if (m <= -1) { col = new Color(0.65f, 0.85f, 1f); return (-m) + " min early"; }
        col = new Color(0.62f, 0.95f, 0.72f); return "On time";
    }

    private void DrawMgrBus(MgrBus b, float size, int w, int mh, Vector2 mouse, ref MgrBus? hover, bool withLabels)
    {
        Vector2 sp = WorldToScreen(b.pos, w, mh);
        if (sp.x < -size * 2 || sp.x > w + size * 2 || sp.y < -size * 2 || sp.y > mh + size * 2) return;

        bool dim = !string.IsNullOrEmpty(ManagerFocusRoute) && b.route != ManagerFocusRoute && b.busID != ManagerSelectedBus;
        var tex = b.offService ? MgrIconGray(b.series) : MgrIcon(b.series);
        float bw = tex != null ? Mathf.Clamp(size * tex.width / Mathf.Max(1f, tex.height), size * 0.8f, size * 2.4f) : size * 1.3f;
        var r = new Rect(sp.x - bw * 0.5f, sp.y - size * 0.5f, bw, size);

        bool picked = b.busID == ManagerSelectedBus;
        if (b.broken)
        {
            float pulse = 0.5f + 0.5f * Mathf.Abs(Mathf.Sin(Time.realtimeSinceStartup * 3f));
            float ring = size * (0.75f + 0.2f * pulse);
            MDT_UITheme.DrawRoundedRectBordered(new Rect(sp.x - ring, sp.y - ring, ring * 2f, ring * 2f), ring, new Color(1f, 0.3f, 0.28f, 0.18f), new Color(MgrBroken.r, MgrBroken.g, MgrBroken.b, 0.9f), 2);
        }
        if (picked)
            MDT_UITheme.DrawRoundedRectBordered(new Rect(r.x - 5, r.y - 5, r.width + 10, r.height + 10 + (withLabels ? 34f : 0f)), 8f, new Color(1f, 1f, 1f, 0.14f), MgrPick, 2);

        Color prev = GUI.color;
        GUI.color = dim ? new Color(1f, 1f, 1f, 0.38f) : Color.white;
        if (tex != null)
        {
            // Pictures face right; mirror them when the bus is heading west.
            var uv = b.west ? new Rect(1f, 0f, -1f, 1f) : new Rect(0f, 0f, 1f, 1f);
            GUI.DrawTextureWithTexCoords(r, tex, uv, true);
        }
        else
        {
            MDT_UITheme.DrawRoundedRectBordered(r, 6f, new Color(b.color.r * 0.5f, b.color.g * 0.5f, b.color.b * 0.5f, 1f), Color.white, 1);
        }
        GUI.color = prev;

        if (withLabels)
        {
            // Line 1: "1  #1903" (route, then bus). A bus that is not in service has no route to show.
            string l1 = b.offService || string.IsNullOrEmpty(b.route) ? "#" + b.fleet : b.route + b.variant + "  #" + b.fleet;
            // Line 2: why it is where it is.
            string l2; Color c2;
            if (b.offService) { l2 = "Not in service"; c2 = new Color(0.78f, 0.80f, 0.80f); }
            else if (b.hasWait) { l2 = "Waiting: " + Mathf.Max(1, Mathf.CeilToInt(b.waitMin)) + " min"; c2 = new Color(1f, 0.50f, 0.46f); }
            else l2 = LateText(b, out c2);

            var nr = new Rect(sp.x - 70, r.yMax - 2, 140, 18);
            var col = dim ? new Color(1f, 1f, 1f, 0.45f) : (b.offService ? new Color(0.85f, 0.87f, 0.87f) : Color.white);
            GUI.contentColor = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(nr.x + 1, nr.y + 1, nr.width, nr.height), l1, _mgrTag);
            GUI.contentColor = col;
            GUI.Label(nr, l1, _mgrTag);
            if (l2.Length > 0)
            {
                var lr = new Rect(sp.x - 70, nr.yMax - 3, 140, 16);
                GUI.contentColor = new Color(0f, 0f, 0f, 0.8f);
                GUI.Label(new Rect(lr.x + 1, lr.y + 1, lr.width, lr.height), l2, _mgrSub);
                GUI.contentColor = dim ? new Color(c2.r, c2.g, c2.b, 0.5f) : c2;
                GUI.Label(lr, l2, _mgrSub);
            }
            GUI.contentColor = Color.white;
        }

        if (b.fixedByManager || b.held)
        {
            var badge = new Rect(r.xMax - 9, r.y - 5, 15, 15);
            MDT_UITheme.DrawRoundedRectBordered(badge, 7.5f, b.held ? MgrRoute : Color.white, MgrStop, 1);
            GUI.Label(badge, b.held ? "II" : "F", _mgrLabel);
        }

        float pad = ManagerTouchMode ? 12f : 0f;
        var hit = new Rect(r.x - pad, r.y - pad, r.width + pad * 2f, r.height + (withLabels ? 30f : 0f) + pad * 2f);
        _mgrHits.Add(new MgrHit { r = hit, busID = b.busID });
        if (hit.Contains(mouse)) hover = b;
    }
}
