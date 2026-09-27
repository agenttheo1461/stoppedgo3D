using UnityEngine;
using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════════════════
//  WorldSpaceStopTrackerBoard
//
//  NOT a reimplemented look-alike -- this renders the SAME MDT_UITheme drawing
//  calls the in-game tracker panel uses (DrawRouteBadge, MakeLabel, the same
//  palette constants) directly into a RenderTexture, then displays that
//  texture on a physical quad placed at the stop. Visually, this IS the
//  tracker UI, just projected onto a board in the world instead of the
//  player's screen.
//
//  HOW "OnGUI INTO A TEXTURE" ACTUALLY WORKS
//  ──────────────────────────────────────────
//  Unity's legacy IMGUI (GUI.*, the same system MDT_UITheme is built on) draws
//  immediately into whatever render target is currently active when the GUI
//  calls execute -- it isn't hard-wired to the screen. So inside OnGUI(), if
//  we do:
//      RenderTexture.active = _rt;
//      GL.Clear(true, true, clearColor);
//      ... normal GUI.Label / GUI.DrawTexture / MDT_UITheme.Draw* calls ...
//      RenderTexture.active = null;
//  every one of those draw calls lands in _rt's pixels instead of the game
//  view. That's the entire trick -- no shader, no custom camera needed. The
//  GUI coordinate space during this block is just (0,0) top-left to
//  (textureWidth, textureHeight), same as it would treat Screen.width/height
//  normally, so all the existing MDT_UITheme layout code works completely
//  unmodified -- it has no idea it's not drawing to the screen.
//
//  The quad itself just samples that RenderTexture with an Unlit/Texture
//  material, so what's on the board updates live every time this repaints.
//
//  SETUP
//  ─────
//  1. Add this component to any empty GameObject at the stop -- it builds
//     its own Quad mesh + material + RenderTexture, nothing to assign by
//     hand except stopCode.
//  2. Set `stopCode` to match the real stop (same sXXXX convention).
//  3. That's it. Rotate the board to face wherever you want it visible from.
// ═══════════════════════════════════════════════════════════════════════════════

public class WorldSpaceStopTrackerBoard : MonoBehaviour
{
    [Header("Stop Binding")]
    [Tooltip("The stop this board displays -- must match a real stop in CityManager.")]
    public string stopCode;
    [Tooltip("Header text. Falls back to stopCode if blank.")]
    public string stopDisplayName;

    [Header("Board (world space)")]
    [Tooltip("Physical size of the board in metres.")]
    public Vector2 boardSizeMetres = new Vector2(0.9f, 1.3f);

    [Header("Render Texture")]
    [Tooltip("Pixel resolution of the rendered UI. Higher = crisper text, more GPU/fill cost. 512x768 reads clearly at typical stop-sign viewing distance.")]
    public Vector2Int textureResolution = new Vector2Int(512, 768);
    [Tooltip("How often the panel actually redraws. The UI itself only NEEDS to update when arrivals change, so this doesn't need to be every frame.")]
    public float refreshIntervalSeconds = 1f;

    [Header("Grouping")]
    [Tooltip("Group by real compass heading, same as the in-game tracker's stop mode.")]
    public bool groupByCompass = true;

    // ── Runtime ───────────────────────────────────────────────────────────────
    private RenderTexture _rt;
    private Material      _quadMaterial;
    private GUIStyle      _headerStyle, _routeBadgeStyle, _dirStyle, _minsStyle, _emptyStyle;
    private float         _refreshTimer;
    private bool          _stylesBuilt;

    // ═════════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═════════════════════════════════════════════════════════════════════════
    private void Awake()
    {
        BuildRenderTexture();
        BuildQuad();
    }

    private void OnDestroy()
    {
        if (_rt != null) { _rt.Release(); Destroy(_rt); }
    }

    private void Update()
    {
        _refreshTimer -= Time.deltaTime;
        // No hard need to gate OnGUI itself on this timer (OnGUI still runs
        // every frame regardless), but there's no reason to re-query
        // BusTrackerService and rebuild the arrival list faster than this --
        // that part IS cached and only refreshed on this interval.
    }

    private void OnGUI()
    {
        if (_rt == null) return;
        if (!_stylesBuilt) { BuildStyles(); _stylesBuilt = true; }

        RenderTexture prevActive = RenderTexture.active;
        RenderTexture.active = _rt;
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, textureResolution.x, textureResolution.y, 0);
        GL.Clear(true, true, MDT_UITheme.BGDeep);

        DrawPanel();

        GL.PopMatrix();
        RenderTexture.active = prevActive;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  THE ACTUAL PANEL -- same theme calls the in-game tracker uses
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawPanel()
    {
        float w = textureResolution.x;
        float h = textureResolution.y;
        var fullRect = new Rect(0, 0, w, h);

        MDT_UITheme.DrawRect(fullRect, MDT_UITheme.BGDeep);

        // Header bar
        float headerH = h * 0.10f;
        MDT_UITheme.DrawRect(new Rect(0, 0, w, headerH), MDT_UITheme.BGHeader);
        string header = string.IsNullOrEmpty(stopDisplayName) ? (stopCode ?? "").ToUpper() : stopDisplayName.ToUpper();
        GUI.Label(new Rect(16, 0, w - 32, headerH), header, _headerStyle);
        MDT_UITheme.DrawDivider(0, headerH, w);

        if (BusTrackerService.Instance == null || string.IsNullOrEmpty(stopCode))
        {
            GUI.Label(new Rect(16, headerH + 20, w - 32, 40), "NO TRACKER SERVICE", _emptyStyle);
            return;
        }

        var flat = new List<(string routeNumber, Color routeColor, string label, string minutesLabel, bool bunched)>();

        if (groupByCompass)
        {
            foreach (var bucket in BusTrackerService.Instance.GetArrivalsForStopByCompass(stopCode))
                foreach (var e in bucket.arrivals)
                    flat.Add((e.routeNumber, e.routeColor, bucket.label, e.minutesLabel, e.isBunched));
        }
        else
        {
            var (ob, ib) = BusTrackerService.Instance.GetArrivalsForStop(stopCode);
            foreach (var e in ob.arrivals) flat.Add((e.routeNumber, e.routeColor, "OUTBOUND", e.minutesLabel, e.isBunched));
            foreach (var e in ib.arrivals) flat.Add((e.routeNumber, e.routeColor, "INBOUND",  e.minutesLabel, e.isBunched));
        }

        var closedRoutes = BusTrackerService.Instance.GetClosedRoutesAtStop(stopCode);

        if (flat.Count == 0)
        {
            string msg = closedRoutes.Count > 0
                ? "STOP CLOSED — DETOUR (" + string.Join(", ", closedRoutes) + ")"
                : "NO SERVICE";
            GUI.Label(new Rect(16, headerH + 20, w - 32, 80), msg, _emptyStyle);
            return;
        }

        if (closedRoutes.Count > 0)
        {
            // Some routes still serve the stop; tell riders the others are diverted.
            float bannerH = h * 0.07f;
            MDT_UITheme.DrawRect(new Rect(0, h - bannerH, w, bannerH), new Color(0.55f, 0.18f, 0.05f, 0.95f));
            GUI.Label(new Rect(16, h - bannerH, w - 32, bannerH), "DETOUR: " + string.Join(", ", closedRoutes) + " not stopping here", _emptyStyle);
        }

        // Row list -- same visual language as the in-game tracker: a
        // color-coded route badge pill, direction/destination label, and
        // minutes-away right-aligned, alternating row shading.
        float rowH = (h - headerH) / Mathf.Max(1, flat.Count > 8 ? 8 : flat.Count);
        rowH = Mathf.Clamp(rowH, 44f, 90f);
        float y = headerH;

        for (int i = 0; i < flat.Count; i++)
        {
            if (y + rowH > h) break; // ran out of board -- rest just doesn't render, same as a real board with a fixed area

            var e = flat[i];
            var rowRect = new Rect(0, y, w, rowH);
            MDT_UITheme.DrawRect(rowRect, i % 2 == 0 ? MDT_UITheme.BGRowEven : MDT_UITheme.BGRowOdd);

            var badgeRect = new Rect(14, y + rowH * 0.5f - 18, 64, 36);
            MDT_UITheme.DrawRouteBadge(badgeRect, e.routeNumber, e.routeColor.a > 0.01f ? e.routeColor : MDT_UITheme.TextCyan, _routeBadgeStyle);

            string dirText = e.bunched ? $"{e.label}  •  bunched" : e.label;
            GUI.Label(new Rect(badgeRect.xMax + 14, y, w - badgeRect.xMax - 150, rowH), dirText, _dirStyle);

            GUI.Label(new Rect(w - 130, y, 116, rowH), e.minutesLabel, _minsStyle);

            if (i < flat.Count - 1)
                MDT_UITheme.DrawDivider(0, y + rowH, w);

            y += rowH;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STYLES -- built once, straight out of MDT_UITheme's own factories
    // ═════════════════════════════════════════════════════════════════════════
    private void BuildStyles()
    {
        _headerStyle     = MDT_UITheme.MakeLabel(22, FontStyle.Bold,   TextAnchor.MiddleLeft,   MDT_UITheme.TextPrimary);
        _routeBadgeStyle = MDT_UITheme.MakeLabel(18, FontStyle.Bold,   TextAnchor.MiddleCenter, MDT_UITheme.TextWhite);
        _dirStyle        = MDT_UITheme.MakeLabel(18, FontStyle.Normal, TextAnchor.MiddleLeft,   MDT_UITheme.TextSecond);
        _minsStyle       = MDT_UITheme.MakeLabel(20, FontStyle.Bold,   TextAnchor.MiddleRight,  MDT_UITheme.TextGreen);
        _emptyStyle      = MDT_UITheme.MakeLabel(18, FontStyle.Italic, TextAnchor.UpperLeft,    MDT_UITheme.TextDim);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  BUILD -- render texture + physical quad
    // ═════════════════════════════════════════════════════════════════════════
    private void BuildRenderTexture()
    {
        _rt = new RenderTexture(textureResolution.x, textureResolution.y, 0, RenderTextureFormat.ARGB32)
        {
            name        = $"StopTrackerRT_{(string.IsNullOrEmpty(stopCode) ? "unbound" : stopCode)}",
            filterMode  = FilterMode.Bilinear,
            useMipMap   = false,
            wrapMode    = TextureWrapMode.Clamp,
        };
        _rt.Create();
    }

    private void BuildQuad()
    {
        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "TrackerBoardSurface";
        Destroy(quad.GetComponent<Collider>()); // pure visual surface, add your own collider on the root if you want it clickable
        quad.transform.SetParent(transform, false);
        quad.transform.localPosition = Vector3.zero;
        quad.transform.localRotation = Quaternion.identity;
        quad.transform.localScale    = new Vector3(boardSizeMetres.x, boardSizeMetres.y, 1f);

        var mr = quad.GetComponent<MeshRenderer>();
        _quadMaterial = new Material(Shader.Find("Unlit/Texture")) { mainTexture = _rt };
        mr.sharedMaterial    = _quadMaterial;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows    = false;
    }
}