using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTE GUIDE ARROWS
//
//  A static, route-coloured ribbon laid just above the road along the player's
//  current route/direction, with lighter chevrons (>>>) pointing the way the bus
//  is going and a bigger circle at every stop.
//
//      >>>•>>>>>>>>•>>>>
//
//  · Static mesh, one per (route, variant, direction), built once and cached.
//    Nothing animates or updates per frame.
//  · Follows the route's own nodes/curves (BusRouteData.BuildSegments), so it is
//    exactly the line the bus is meant to drive.
//  · Ribbon + chevrons are ~50% transparent. Chevrons use a lighter, more
//    saturated tint of the route colour so they stay visible on any colour
//    (white routes get a grey-blue tint, yellow leans orange, and so on).
//  · Stop circles are larger than the ribbon so stops read from a distance.
//  · Shown only while the player is on a route. Toggle with KeyBindings.guideArrows.
//
//  Auto-creates itself after scene load — nothing to place in the scene.
// ═══════════════════════════════════════════════════════════════════════════════
public class RouteGuideArrows : MonoBehaviour
{
    public static RouteGuideArrows Instance { get; private set; }

    [Header("Behaviour")]
    public bool showArrows = true;
    [Tooltip("Seconds between checks of which route/direction the player is on.")]
    public float refreshInterval = 0.25f;

    [Header("Shape (metres)")]
    public float ribbonWidth      = 2.2f;
    public float sampleStep       = 2.0f;
    public float chevronSpacing   = 9.0f;
    public float chevronHalfWidth = 0.78f;
    public float chevronDepth     = 1.1f;   // how far the arms sweep back from the tip
    public float chevronThickness = 0.55f;
    public float stopRadius       = 2.8f;   // bigger than ribbonWidth/2 on purpose
    public float stopInnerRadius  = 1.5f;
    // [FIX] Was 0.35 (35cm) -- visibly floating above the road. Brought down
    // close to flush; not all the way to ~0.001 as literally requested since
    // at that thinness the road mesh and ribbon would z-fight (flicker)
    // depending on camera distance and world-position floating point
    // precision -- 0.02 (2cm) reads as flush from driving height while
    // staying a safe margin above the mesh.
    public float heightAboveRoad  = 0.02f;

    [Header("Look")]
    [Range(0f, 1f)] public float ribbonAlpha  = 0.50f;
    [Range(0f, 1f)] public float chevronAlpha = 0.50f;
    [Range(0f, 1f)] public float stopAlpha    = 0.55f;

    [Header("Guide to target (start terminal / idle bay / first stop)")]
    public bool  showTargetGuide      = true;
    public float guideWidth           = 2.0f;
    public float guideOutlineWidth    = 0.22f;
    public float guideSampleStep      = 5f;
    [Tooltip("Rebuild the guide path when the bus has moved this far since the last build (also trims the part already driven).")]
    public float guideRebuildDistance = 15f;
    [Tooltip("Stop guiding once the bus is this close to the target.")]
    public float guideArriveDistance  = 12f;
    public Color guideGrey    = new Color(0.55f, 0.56f, 0.60f, 0.80f);
    public Color guideOutline = new Color(1f, 1f, 1f, 0.95f);

    private GameObject   _guideGo;
    private MeshFilter   _guideMf;
    private Mesh         _guideMesh;
    private Vector3      _guideTarget;
    private bool         _guideHasTarget;
    private Vector3      _guideBuiltAt;
    private float        _guideBuiltTime;

    // ── Runtime ───────────────────────────────────────────────────────────────
    private readonly Dictionary<string, Mesh> _cache = new Dictionary<string, Mesh>();
    private GameObject   _go;
    private MeshFilter   _mf;
    private MeshRenderer _mr;
    private Material     _mat;
    private string       _shownKey = "";
    private float        _nextRefresh;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        var host = new GameObject("RouteGuideArrows");
        host.AddComponent<RouteGuideArrows>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        // [FIX] Was briefly Hidden/RouteGuideOverlay (ZTest Always) to punch
        // through WINDOW_1/WINDOW_2 -- but that also punched through opaque
        // geometry (buildings, buses), which isn't what a route guide should
        // do. The real bug was on the window's side: ToonRampLit hardcoded
        // ZWrite On regardless of its own exposed _ZWrite property, so a
        // transparent window always wrote depth. That's fixed at the shader
        // (Blend/ZWrite now driven by the material's real properties) and at
        // WINDOW_1/WINDOW_2 (_ZWrite set to 0, like any transparent material).
        // With the window no longer writing depth, normal ZTest LEqual is
        // correct again -- opaque objects still occlude the guide properly,
        // and windows no longer falsely block it.
        var shader = Shader.Find("Sprites/Default");
        if (shader != null)
        {
            _mat = new Material(shader) { name = "RouteGuideArrows (runtime)", renderQueue = 3100 };
        }

        _go = new GameObject("GuideMesh");
        _go.transform.SetParent(transform, false);
        _mf = _go.AddComponent<MeshFilter>();
        _mr = _go.AddComponent<MeshRenderer>();
        _mr.sharedMaterial = _mat;
        _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _mr.receiveShadows = false;
        _go.SetActive(false);

        _guideGo = new GameObject("GuideToTarget");
        _guideGo.transform.SetParent(transform, false);
        _guideMf = _guideGo.AddComponent<MeshFilter>();
        var gmr = _guideGo.AddComponent<MeshRenderer>();
        gmr.sharedMaterial = _mat;
        gmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        gmr.receiveShadows = false;
        _guideMesh = new Mesh { name = "GuideToTarget" };
        _guideMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        _guideMf.sharedMesh = _guideMesh;
        _guideGo.SetActive(false);

        RoadEvent.Activated += OnEventChanged;
        RoadEvent.Cleared   += OnEventChanged;
    }

    // A road event starting/ending changes which stops are closed and where the detour runs -> rebuild lazily.
    private void OnEventChanged(RoadEvent ev) => InvalidateCache();

    private void OnDestroy()
    {
        RoadEvent.Activated -= OnEventChanged;
        RoadEvent.Cleared   -= OnEventChanged;
        foreach (var kv in _cache) if (kv.Value != null) Destroy(kv.Value);
        _cache.Clear();
        if (_mat != null) Destroy(_mat);
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyBindings.Current.guideArrows) && KeyBindings.Current.guideArrows != KeyCode.None)
            showArrows = !showArrows;

        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + refreshInterval;

        UpdateTargetGuide();

        var ph = PlayerHandoff.Instance;
        bool want = showArrows && _mat != null && ph != null && ph.IsOnDuty
                    && !string.IsNullOrEmpty(ph.ActiveRoute) && BusScheduler.Instance != null;
        if (!want) { Hide(); return; }

        string key = ph.ActiveRoute + "|" + ph.ActiveVariant + "|" + (ph.IsOutbound ? "O" : "I");
        if (key == _shownKey && _go.activeSelf) return;

        if (!_cache.TryGetValue(key, out var mesh))
        {
            mesh = Build(ph.ActiveRoute, ph.ActiveVariant, ph.IsOutbound);
            _cache[key] = mesh;   // null cached too, so a bad route doesn't rebuild every tick
        }

        if (mesh == null) { Hide(); return; }
        _mf.sharedMesh = mesh;
        _go.SetActive(true);
        _shownKey = key;
    }

    private void Hide()
    {
        if (_go != null && _go.activeSelf) _go.SetActive(false);
        _shownKey = "";
    }

    // ── Guide to target ───────────────────────────────────────────────────────
    // Grey ribbon with a white outline from the bus to where it should be going right now (start terminal,
    // idle bay, or the first stop after leaving the idle zone), following the road graph. Rebuilt as the bus moves,
    // so it also re-routes if the driver leaves the suggested path.
    private void UpdateTargetGuide()
    {
        var ph = PlayerHandoff.Instance;
        var bus = ph != null ? ph.playerBus : null;
        if (!showArrows || !showTargetGuide || _mat == null || ph == null || bus == null
            || !ph.TryGetGuideTarget(out Vector3 target, out _))
        {
            HideGuide();
            return;
        }

        Vector3 from = bus.transform.position;
        Vector3 flat = from - target; flat.y = 0f;
        if (flat.magnitude < guideArriveDistance) { HideGuide(); return; }

        bool targetChanged = !_guideHasTarget || (target - _guideTarget).sqrMagnitude > 4f;
        float moved = (from - _guideBuiltAt).magnitude;
        if (!targetChanged && moved < guideRebuildDistance && Time.unscaledTime - _guideBuiltTime < 6f)
        {
            if (!_guideGo.activeSelf) _guideGo.SetActive(true);
            return;
        }

        _guideTarget = target; _guideHasTarget = true;
        _guideBuiltAt = from; _guideBuiltTime = Time.unscaledTime;

        var pts = FindGuidePath(from, target);
        if (pts == null || pts.Count < 2) { HideGuide(); return; }

        SnapToSurface(pts);
        BuildGuideMesh(pts, target);
        _guideGo.SetActive(true);
    }

    private void HideGuide()
    {
        _guideHasTarget = false;
        if (_guideGo != null && _guideGo.activeSelf) _guideGo.SetActive(false);
    }

    private List<Vector3> FindGuidePath(Vector3 from, Vector3 to)
    {
        List<Vector3> raw = null;
        var cm = CityManager.Instance;
        if (cm != null && cm.Graph != null)
        {
            try { raw = RoadGraphPathfinder.FindWaypoints(cm.Graph, from, to, guideSampleStep); } catch { raw = null; }
        }
        if (raw == null || raw.Count < 2)
        {
            try { raw = BusPathfinder.BuildPath(from, to); } catch { raw = null; }
        }
        if (raw == null || raw.Count < 2) raw = new List<Vector3> { from, to };   // last resort: straight line

        // Resample at a fixed step, and drop the part of the path already behind the bus.
        int startIdx = 0; float best = float.MaxValue;
        for (int i = 0; i < raw.Count; i++)
        {
            float dx = raw[i].x - from.x, dz = raw[i].z - from.z, d = dx * dx + dz * dz;
            if (d < best) { best = d; startIdx = i; }
        }
        var pts = new List<Vector3>();
        pts.Add(from);
        for (int i = startIdx; i < raw.Count - 1; i++)
        {
            float len = Vector3.Distance(raw[i], raw[i + 1]);
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(1f, guideSampleStep)));
            for (int k = (i == startIdx ? 0 : 1); k <= steps; k++)
            {
                Vector3 p = Vector3.Lerp(raw[i], raw[i + 1], k / (float)steps);
                if ((p - pts[pts.Count - 1]).sqrMagnitude > 1f) pts.Add(p);
            }
        }
        if ((to - pts[pts.Count - 1]).sqrMagnitude > 1f) pts.Add(to);
        return pts;
    }

    private void BuildGuideMesh(List<Vector3> pts, Vector3 target)
    {
        int n = pts.Count;
        var verts = new List<Vector3>(n * 6 + 128);
        var cols  = new List<Color>(n * 6 + 128);
        var tris  = new List<int>(n * 18 + 256);

        var tan = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 a = pts[Mathf.Max(i - 1, 0)], b = pts[Mathf.Min(i + 1, n - 1)];
            Vector3 t = b - a; t.y = 0f;
            tan[i] = t.sqrMagnitude > 1e-6f ? t.normalized : (i > 0 ? tan[i - 1] : Vector3.forward);
        }

        float lift = heightAboveRoad + 0.10f;   // above the route ribbon so it reads on top of it
        float hw = guideWidth * 0.5f;
        float ow = guideOutlineWidth;

        // Three strips per sample: left outline, grey body, right outline.
        int b0 = verts.Count;
        for (int i = 0; i < n; i++)
        {
            Vector3 right = Vector3.Cross(Vector3.up, tan[i]);
            Vector3 c = pts[i] + Vector3.up * lift;
            verts.Add(c - right * (hw + ow)); cols.Add(guideOutline);   // 0 outer left
            verts.Add(c - right * hw);        cols.Add(guideOutline);   // 1 inner left / body left
            verts.Add(c + right * hw);        cols.Add(guideOutline);   // 2 inner right
            verts.Add(c + right * (hw + ow)); cols.Add(guideOutline);   // 3 outer right
        }
        int b1 = verts.Count;
        for (int i = 0; i < n; i++)
        {
            Vector3 right = Vector3.Cross(Vector3.up, tan[i]);
            Vector3 c = pts[i] + Vector3.up * (lift + 0.02f);
            verts.Add(c - right * hw); cols.Add(guideGrey);
            verts.Add(c + right * hw); cols.Add(guideGrey);
        }
        for (int i = 1; i < n; i++)
        {
            int a = b0 + (i - 1) * 4, c = b0 + i * 4;
            // left outline (0-1), right outline (2-3)
            tris.Add(a);     tris.Add(c);     tris.Add(a + 1);   tris.Add(a + 1); tris.Add(c);     tris.Add(c + 1);
            tris.Add(a + 2); tris.Add(c + 2); tris.Add(a + 3);   tris.Add(a + 3); tris.Add(c + 2); tris.Add(c + 3);
            int g = b1 + (i - 1) * 2, h = b1 + i * 2;
            tris.Add(g); tris.Add(h); tris.Add(g + 1);           tris.Add(g + 1); tris.Add(h); tris.Add(h + 1);
        }

        // Target marker: white ring with a grey centre.
        Vector3 tc = pts[n - 1] + Vector3.up * (lift + 0.04f);
        AddDisc(verts, cols, tris, tc, stopRadius + 0.4f, guideOutline, 28);
        AddDisc(verts, cols, tris, tc + Vector3.up * 0.02f, stopRadius - 0.3f, guideGrey, 28);

        _guideMesh.Clear();
        _guideMesh.SetVertices(verts);
        _guideMesh.SetColors(cols);
        _guideMesh.SetTriangles(tris, 0);
        _guideMesh.RecalculateBounds();
    }

    /// <summary>Drop cached meshes (call after routes/nodes change at runtime).</summary>
    public void InvalidateCache()
    {
        foreach (var kv in _cache) if (kv.Value != null) Destroy(kv.Value);
        _cache.Clear();
        _shownKey = "";
    }

    // ── Colour ────────────────────────────────────────────────────────────────
    /// <summary>
    /// Lighter, more saturated tint of the route colour for the chevrons.
    /// Whites/greys become a grey-blue; yellows lean orange; everything else gets
    /// pushed toward a brighter, richer version of itself.
    /// </summary>
    public static Color ChevronTint(Color route)
    {
        Color.RGBToHSV(route, out float h, out float s, out float v);

        if (s < 0.14f)   // white / grey / black routes
        {
            Color grey = new Color(0.62f, 0.68f, 0.74f);
            Color blue = new Color(0.50f, 0.72f, 1.00f);
            return Color.Lerp(grey, blue, 0.45f);
        }

        if (h > 0.11f && h < 0.20f) h -= 0.045f;               // yellow -> a touch of orange
        s = Mathf.Clamp01(s * 1.25f + 0.10f);
        v = v > 0.85f ? Mathf.Clamp01(v * 0.92f) : Mathf.Clamp01(v * 1.12f + 0.06f);
        return Color.HSVToRGB(h, s, v);
    }

    // ── Mesh build ────────────────────────────────────────────────────────────
    private Mesh Build(string routeNumber, string variantLetter, bool outbound)
    {
        var route = BusScheduler.Instance.GetRouteData(routeNumber);
        if (route == null) return null;
        var variant = string.IsNullOrEmpty(variantLetter) ? null : route.GetVariant(variantLetter);

        var nodes = route.GetNodes(outbound, variant);
        if (nodes == null || nodes.Count < 2) return null;

        // 1) Sample the route path.
        var pts = new List<Vector3>();
        foreach (var seg in route.BuildSegments(nodes))
        {
            int steps = Mathf.Max(2, Mathf.CeilToInt(seg.Length / Mathf.Max(0.5f, sampleStep)));
            for (int i = 0; i <= steps; i++)
            {
                Vector3 p = seg.Evaluate(i / (float)steps);
                if (pts.Count == 0 || (p - pts[pts.Count - 1]).sqrMagnitude > 0.04f) pts.Add(p);
            }
        }
        if (pts.Count < 2) return null;

        // 2) Snap to the road surface, following it continuously (so bridges/ramps work).
        SnapToSurface(pts);

        Color rc = route.routeColor; rc.a = 1f;
        Color ribbonCol = new Color(rc.r, rc.g, rc.b, ribbonAlpha);
        Color tint = ChevronTint(rc);
        Color chevCol  = new Color(tint.r, tint.g, tint.b, chevronAlpha);
        Color stopOuter = new Color(rc.r, rc.g, rc.b, stopAlpha);
        Color stopInner = new Color(Mathf.Lerp(tint.r, 1f, 0.35f), Mathf.Lerp(tint.g, 1f, 0.35f), Mathf.Lerp(tint.b, 1f, 0.35f), Mathf.Min(1f, stopAlpha + 0.30f));

        var verts = new List<Vector3>(pts.Count * 2 + 1024);
        var cols  = new List<Color>(pts.Count * 2 + 1024);
        var tris  = new List<int>(pts.Count * 6 + 4096);

        AppendPath(pts, ribbonCol, chevCol, verts, cols, tris, out var tan);
        int n = pts.Count;
        float lift = heightAboveRoad;

        // 5b) Active road-event detours for this route/direction: an orange guide through the detour nodes.
        var reg = RoadEventRegistry.Instance;
        if (reg != null)
        {
            foreach (var ev in reg.GetAllActiveEvents())
            {
                if (ev == null || ev.severity == RoadEventSeverity.Minor || !ev.AppliesToRoute(routeNumber)) continue;
                var dn = ev.GetDetourPositions(outbound, 0f);
                if (dn == null || dn.Count < 2) continue;

                var dpts = new List<Vector3>();
                for (int i = 0; i < dn.Count - 1; i++)
                {
                    float len = Vector3.Distance(dn[i], dn[i + 1]);
                    int steps = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.5f, sampleStep)));
                    for (int k = 0; k < steps; k++) dpts.Add(Vector3.Lerp(dn[i], dn[i + 1], k / (float)steps));
                }
                dpts.Add(dn[dn.Count - 1]);
                SnapToSurface(dpts);
                var warn = new Color(1.00f, 0.55f, 0.10f, 1f);
                AppendPath(dpts, new Color(warn.r, warn.g, warn.b, ribbonAlpha), new Color(1f, 0.80f, 0.35f, chevronAlpha), verts, cols, tris, out _);
            }
        }

        // 6) Stop circles, placed on the path point nearest each stop.
        var cm = CityManager.Instance;
        var codes = route.GetStopCodes(outbound, variant);
        if (cm != null && codes != null)
        {
            foreach (var code in codes)
            {
                var st = cm.GetStop(code);
                if (st == null) continue;
                Vector3 sp = st.GetWorldPosition();
                int best = 0; float bestD = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    float dx = pts[i].x - sp.x, dz = pts[i].z - sp.z;
                    float dd = dx * dx + dz * dz;
                    if (dd < bestD) { bestD = dd; best = i; }
                }
                Vector3 c = pts[best] + Vector3.up * (lift + 0.05f);
                bool closed = reg != null && reg.IsStopClosed(code, routeNumber, outbound);
                AddDisc(verts, cols, tris, c, stopRadius, closed ? new Color(0.35f, 0.10f, 0.10f, stopAlpha + 0.15f) : stopOuter, 24);
                AddDisc(verts, cols, tris, c + Vector3.up * 0.02f, stopInnerRadius, closed ? new Color(0.85f, 0.25f, 0.20f, 0.85f) : stopInner, 24);
            }
        }

        var mesh = new Mesh { name = $"GuideArrows_{routeNumber}{variantLetter}_{(outbound ? "out" : "in")}" };
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetColors(cols);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>Appends a ribbon + evenly spaced chevrons along the given (already surface-snapped) points.</summary>
    private void AppendPath(List<Vector3> pts, Color ribbonCol, Color chevCol,
                            List<Vector3> verts, List<Color> cols, List<int> tris, out Vector3[] tan)
    {
        int n = pts.Count;
        var dist = new float[n];
        for (int i = 1; i < n; i++) dist[i] = dist[i - 1] + Vector3.Distance(pts[i - 1], pts[i]);
        tan = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 a = pts[Mathf.Max(i - 1, 0)], b = pts[Mathf.Min(i + 1, n - 1)];
            Vector3 t = b - a; t.y = 0f;
            tan[i] = t.sqrMagnitude > 1e-6f ? t.normalized : (i > 0 ? tan[i - 1] : Vector3.forward);
        }

        float lift = heightAboveRoad;
        float hw = ribbonWidth * 0.5f;
        int baseIdx = verts.Count;
        for (int i = 0; i < n; i++)
        {
            Vector3 right = Vector3.Cross(Vector3.up, tan[i]);
            Vector3 c = pts[i] + Vector3.up * lift;
            verts.Add(c - right * hw); cols.Add(ribbonCol);
            verts.Add(c + right * hw); cols.Add(ribbonCol);
            if (i > 0)
            {
                int b = baseIdx + (i - 1) * 2;
                tris.Add(b); tris.Add(b + 2); tris.Add(b + 1);
                tris.Add(b + 1); tris.Add(b + 2); tris.Add(b + 3);
            }
        }

        float total = dist[n - 1];
        int cursor = 0;
        for (float d = chevronSpacing * 0.5f; d < total - 1f; d += chevronSpacing)
        {
            while (cursor < n - 2 && dist[cursor + 1] < d) cursor++;
            float seg = Mathf.Max(0.0001f, dist[cursor + 1] - dist[cursor]);
            float f = Mathf.Clamp01((d - dist[cursor]) / seg);
            Vector3 p = Vector3.Lerp(pts[cursor], pts[cursor + 1], f) + Vector3.up * (lift + 0.03f);
            Vector3 fwd = Vector3.Slerp(tan[cursor], tan[cursor + 1], f);
            if (fwd.sqrMagnitude < 1e-6f) fwd = tan[cursor];
            fwd.Normalize();
            AddChevron(verts, cols, tris, p, fwd, chevCol);
        }
    }

    private void SnapToSurface(List<Vector3> pts)
    {
        // First point: search from high above. After that, follow the surface from just above
        // the previous point so a bridge deck over another road isn't picked up by mistake.
        float prevY = 0f;
        bool havePrev = false;
        for (int i = 0; i < pts.Count; i++)
        {
            Vector3 p = pts[i];
            float startY = havePrev ? prevY + 6f : 300f;
            if (Physics.Raycast(new Vector3(p.x, startY, p.z), Vector3.down, out var hit, startY + 300f,
                                ~0, QueryTriggerInteraction.Ignore))
            {
                p.y = hit.point.y;
                prevY = p.y; havePrev = true;
            }
            else
            {
                p.y = havePrev ? prevY : 0f;
            }
            pts[i] = p;
        }
    }

    // ── Geometry helpers ──────────────────────────────────────────────────────
    private void AddChevron(List<Vector3> v, List<Color> c, List<int> t, Vector3 pos, Vector3 fwd, Color col)
    {
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        float w = chevronHalfWidth, d = chevronDepth, k = chevronThickness;
        float h = (d + k) * 0.5f;   // centre the chevron on pos

        // Outline: tip -> right arm -> notch -> left arm.
        Vector3 T  = pos + fwd * h;
        Vector3 R  = pos + right * w + fwd * (h - d);
        Vector3 R2 = pos + right * w + fwd * (h - d - k);
        Vector3 N  = pos + fwd * (h - k);
        Vector3 L2 = pos - right * w + fwd * (h - d - k);
        Vector3 L  = pos - right * w + fwd * (h - d);

        int b = v.Count;
        v.Add(T); v.Add(R); v.Add(R2); v.Add(N); v.Add(L2); v.Add(L);
        for (int i = 0; i < 6; i++) c.Add(col);

        // (T,R,N) (R,R2,N) (T,N,L) (N,L2,L) — wound to face up.
        t.Add(b + 0); t.Add(b + 1); t.Add(b + 3);
        t.Add(b + 1); t.Add(b + 2); t.Add(b + 3);
        t.Add(b + 0); t.Add(b + 3); t.Add(b + 5);
        t.Add(b + 3); t.Add(b + 4); t.Add(b + 5);
    }

    private void AddDisc(List<Vector3> v, List<Color> c, List<int> t, Vector3 centre, float radius, Color col, int segments)
    {
        int b = v.Count;
        v.Add(centre); c.Add(col);
        for (int i = 0; i < segments; i++)
        {
            float a = i / (float)segments * Mathf.PI * 2f;
            v.Add(centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius);
            c.Add(col);
        }
        for (int i = 0; i < segments; i++)
        {
            int cur = b + 1 + i, nxt = b + 1 + (i + 1) % segments;
            t.Add(b); t.Add(nxt); t.Add(cur);
        }
    }
}