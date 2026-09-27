using UnityEngine;
using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusInteriorLCDBoard
//
//  [07-30] LAYOUT REBUILT to spec. Top 4/5 of the panel is flat black; bottom
//  1/5 is light gray. Three zones inside the black area:
//
//    ┌────────────────────────────────────────┐
//    │109      DOWNTOWN TERMINAL              │ <- raw number, zero padding,
//    │                                        │    dest text alongside (no ETA)
//    │ ┌─────────────────────────────────┐    │
//    │ │ Main St & 4th          4  mins  │    │ <- alternating light/dark
//    │ ├─────────────────────────────────┤    │    black rows, one per
//    │ │ Central Terminal      11  mins  │    │    upcoming stop, big ETA
//    │ └─────────────────────────────────┘    │    number + small unit
//    │                                        │
//    ├────────────────────────────────────────┤
//    │  ROUTE 109 · Central Terminal · 4:12   │ <- gray strip, same rotation
//    └────────────────────────────────────────┘    idea as before, in-game time
//
//  The stop-row stack SHRINKS on its own as the bus consumes stops -- 3 rows
//  while there's a next stop, a stop-after, and a terminal; 2 once the next
//  stop is the one right before the terminal; 1 once only the terminal is
//  left; 0 (nothing drawn there) once the trip's out of upcoming stops. This
//  falls straight out of IBusDisplaySource.GetUpcomingStopsWithEta shrinking
//  its own return list -- no extra state needed here.
//
//  ETA numbers come from BusTrackerService.GetOwnBusEtaLabel -- the SAME
//  GPS-position + scheduler hybrid math the exterior stop-side boards use
//  (BusTrackerService.CalculateHybridLiveEta), just answering "when do I
//  reach this stop" instead of "who's arriving at this stop."
//
//  POWER: the board goes fully black (nothing drawn, not even the housing
//  glass content) whenever IsEngineRunning is false. On the Off->Running
//  transition it runs a ~7s boot sequence (well under the 15s budget):
//  white -> red -> green -> blue -> black -> fast QWERTY-row alphabet type-on
//  -> "FLEET ####" -> normal operation. See BootSequence().
//
//  [07-30] Keeps the physical housing (real depth, bezel lip, accent strip)
//  and screen overlay (scanlines/vignette) from the previous pass -- see
//  BuildHousing()/DrawScreenOverlay(), unchanged in spirit, just reused under
//  the new panel content.
//
//  [07-30] CONSOLE HOOK. BusBoardDebugChannel: "boardmsg"/"ms" (gray-strip
//  message), "boardrt" (override the black-zone number), "boardestination
//  des1|des2 <route>" (override the destination text), "boardclear". See
//  Driver.cs. SourceDescription feeds the "boards" diagnostic command.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusInteriorLCDBoard : MonoBehaviour
{
    [Header("Data Source")]
    [Tooltip("Assign the BusSimulationController or NPCBusController on this same bus. Must implement IBusDisplaySource.")]
    public MonoBehaviour dataSourceBehaviour;
    private IBusDisplaySource _source;

    [Header("Board (world space)")]
    public Vector2 boardSizeMetres = new Vector2(0.6f, 0.35f);
    [Tooltip("Physical depth of the housing block. This is what stops the board reading as a flat decal -- even a few millimetres shows a real edge from the side.")]
    public float boardDepthMetres = 0.030f;
    [Range(0.01f, 0.12f)] public float bezelLipFraction = 0.045f;
    public Color housingColor = new Color(0.045f, 0.048f, 0.055f, 1f);
    public Color accentStripColor = new Color(0.15f, 0.62f, 0.85f, 1f);

    [Header("Render Texture")]
    [Tooltip("Match this to boardSizeMetres' aspect ratio (X/Y) or the render texture stretches relative to the physical screen -- e.g. a 0.7 x 0.175m board (4:1) wants something like 640 x 160, not a taller ratio like 640 x 384.")]
    public Vector2Int textureResolution = new Vector2Int(640, 160);
    public float dataRefreshIntervalSeconds = 1f;
    [Tooltip("How often just the ETA numbers re-pull, separately from the slower full refresh above. Needs to be faster than 1s -- sampling the game clock only once per second, in lockstep with a clock that advances in fixed steps, was why the countdown only ever showed even numbers and skipped straight past odd ones (1, 3, 5...). Sampling more often catches the in-between values.")]
    public float etaRefreshIntervalSeconds = 0.15f;

    [Header("Panel Colors")]
    public Color blackZoneColor      = Color.black;
    public Color rowColorLight       = new Color(0.10f, 0.10f, 0.11f, 1f);
    public Color rowColorDark        = new Color(0.045f, 0.045f, 0.05f, 1f);
    public Color grayZoneColor       = new Color(0.62f, 0.62f, 0.60f, 1f);

    [Header("Gray Strip Flash Timing")]
    [Tooltip("How long each rotation entry holds before flashing to the next -- ~1 second.")]
    public float flashHoldSeconds = 1f;

    [Header("Screen Look")]
    [Range(0f, 0.5f)] public float scanlineStrength = 0.10f;
    [Range(0f, 0.6f)] public float vignetteStrength  = 0.30f;

    [Header("Performance")]
    [Tooltip("Board stops refreshing data and redrawing entirely once its quad isn't visible/is beyond this distance from Camera.main. This is the most expensive of the four interior boards per-frame (fast ETA sub-timer on top of the regular refresh), so this matters more here than on the others. Set to 0 to disable the distance backstop and rely on renderer visibility alone.")]
    public float cullDistanceMetres = 40f;

    // ── Runtime ───────────────────────────────────────────────────────────────
    private RenderTexture _rt;
    private Material      _quadMaterial, _housingMaterial, _stripMaterial;
    // [FIX] Same fix as the overlay textures above, applied to the three
    // materials this board also creates fresh per-instance. Confirmed via
    // Instruments: 10,579 Material objects / 38.9MB across the project --
    // absurd for a texture-free toon-shader-only material setup. Every
    // board instance (all 5 board classes across the project) was creating
    // its own unique housing/strip/quad materials, every bus, forever.
    private static readonly Dictionary<Color, Material> _sharedHousingMaterial = new Dictionary<Color, Material>();
    private static readonly Dictionary<Color, Material> _sharedStripMaterial  = new Dictionary<Color, Material>();
    private static Material _sharedQuadMaterial;
    private MaterialPropertyBlock _quadPropBlock;
    private Texture2D     _scanlineTex, _vignetteTex;
    // [FIX] These were being created FRESH per-instance (BuildOverlayTextures
    // ran once per board, every board got its own copy) despite being purely
    // cosmetic screen-overlay effects with IDENTICAL content across every
    // board of this class at the same resolution -- no per-bus content at
    // all. Confirmed via an Instruments memory capture: 17,203 texture
    // objects / 1.43GB total, an absurd number for a project with no bus
    // livery textures at all (materials-only toon shader). At fleet scale,
    // with this board type sitting on every bus, that's hundreds of
    // redundant duplicate textures for something that should exist ONCE.
    // Shared via a static cache keyed by resolution -- the first board ever
    // built at a given resolution builds it, every later board (any
    // resolution match) just reuses the same reference.
    private static readonly Dictionary<Vector2Int, Texture2D> _sharedScanlineTex  = new Dictionary<Vector2Int, Texture2D>();
    private static readonly Dictionary<Vector2Int, Texture2D> _sharedVignetteTex  = new Dictionary<Vector2Int, Texture2D>();
    private BusBoardVisibilityGate _visGate;

    private GUIStyle _rawRouteStyle, _destTopStyle, _rowNameStyle, _rowNameTermStyle,
                     _etaBigStyle, _etaSubStyle, _grayStripStyle, _grayStripGlowStyle, _bootStyle;
    private bool  _stylesBuilt;
    private float _dataRefreshTimer;
    private float _etaRefreshTimer;
    private BusPassengerDisplayState _state = new BusPassengerDisplayState();

    private List<string> _rotation = new List<string> { "--" };
    private int   _flashIndex;
    private float _flashTimer;

    // Route-number autofit -- recomputed only when the text actually changes,
    // not every frame, so a long/short route number never overflows its
    // column or gets left tiny after a short one.
    private int    _routeFontSizeCache;
    private string _routeTextSizedFor;

    // Power / boot state -- polled every frame directly from _source (not
    // gated behind dataRefreshIntervalSeconds) so the boot sequence starts
    // the instant the engine actually catches, not up to a second late.
    private bool  _wasRunning;
    private bool  _booting;
    private float _bootTimer;

    // Boot phase durations, seconds. Sum ~6.9s, comfortably under the 15s cap.
    private const float PH_WHITE = 0.15f, PH_RED = 0.15f, PH_GREEN = 0.15f, PH_BLUE = 0.15f,
                         PH_BLANK = 0.40f, PH_ALPHA = 3.20f, PH_FLEET = 2.20f, PH_SETTLE = 0.30f;
    private const string ALPHA_ROW = "QWERTYUIOPASDFGHJKLZXCVBNM";

    /// <summary>Human-readable line for the console's "boards" command.</summary>
    public string SourceDescription =>
        dataSourceBehaviour != null
            ? $"{dataSourceBehaviour.GetType().Name} on '{dataSourceBehaviour.name}'" + (_source == null ? " (does NOT implement IBusDisplaySource!)" : "")
            : "no dataSourceBehaviour assigned";

    private void Awake()
    {
        // [ADD] Isolation-test kill switch -- see BusBoardTestSwitch.cs.
        if (BusBoardTestSwitch.DisableAllBoards) return;

        _source = dataSourceBehaviour as IBusDisplaySource;
        if (_source == null)
            Debug.LogWarning($"[{name}] dataSourceBehaviour does not implement IBusDisplaySource -- board will show placeholder text only.");

        BuildRenderTexture();
        BuildOverlayTextures();
        BuildHousing();
    }

    private void OnDestroy()
    {
        if (_rt != null) { _rt.Release(); Destroy(_rt); }
        // [FIX] No longer destroyed here -- these are now shared static
        // textures potentially still in use by OTHER board instances. Only
        // the truly per-instance RenderTexture gets cleaned up on destroy;
        // the shared overlay cache intentionally outlives any single board
        // and just stays resident for the app's lifetime (a few hundred KB
        // total, shared once, is negligible -- the actual win is not
        // multiplying it by hundreds of buses).
    }

    private void Update()
    {
        bool runningNow = _source != null && _source.IsEngineRunning;

        // Off -> Running edge: kick the boot sequence. Any other transition
        // (Running -> Off, or starting up already off) just goes dark.
        if (runningNow && !_wasRunning)
        {
            _booting   = true;
            _bootTimer = 0f;
        }
        _wasRunning = runningNow;

        if (_booting)
        {
            _bootTimer += Time.deltaTime;
            float total = PH_WHITE + PH_RED + PH_GREEN + PH_BLUE + PH_BLANK + PH_ALPHA + PH_FLEET + PH_SETTLE;
            if (_bootTimer >= total) _booting = false;
        }

        if (!runningNow) return; // fully dark -- don't even bother refreshing data

        // [PERF] This board's two-tier refresh (fast ETA sub-timer + slower
        // full refresh) is the most expensive of the four interior boards
        // per-frame -- and was running that cost for every bus in the fleet
        // regardless of whether the player was anywhere near it. See
        // BusBoardVisibilityGate.cs. Boot animation timing above still runs
        // even off-screen (cheap, and a bus shouldn't silently skip its boot
        // sequence just because nobody's currently looking at it) -- only
        // the actual data pulls below are gated.
        if (_visGate != null && !_visGate.ShouldRender) return;

        // [FIX 07-31] Fast, ETA-only refresh. This used to be gated behind
        // the same 1-second dataRefreshIntervalSeconds as everything else --
        // route, destination, stop list, the works. If the underlying game
        // clock advances in fixed steps (e.g. always some flat number of
        // game-minutes per real-second), sampling it only once per second
        // means EVERY sample lands on the same phase of that step, so the
        // displayed ETA only ever shows every-other integer and skips the
        // rest outright (never "1", never "3", straight from "2" to "Due").
        // This re-pulls just the ETA-bearing part of the state on its own,
        // much faster timer, so it samples the clock at enough different
        // phases to actually catch the in-between values. The heavier full
        // refresh below (route/destination/stop names, fleet number, etc.)
        // stays on its slower cadence -- none of that needs to update this
        // often.
        _etaRefreshTimer -= Time.deltaTime;
        if (_etaRefreshTimer <= 0f && _source != null)
        {
            _etaRefreshTimer = etaRefreshIntervalSeconds;
            _state.upcomingStopsWithEta.Clear();
            _state.upcomingStopsWithEta.AddRange(_source.GetUpcomingStopsWithEta(3));
        }

        _dataRefreshTimer -= Time.deltaTime;
        if (_dataRefreshTimer <= 0f)
        {
            _dataRefreshTimer = dataRefreshIntervalSeconds;
            if (_source != null)
            {
                // In-GAME time, not real-world time -- pull from BusScheduler's
                // sim clock via its own formatter so it matches every other
                // in-game timestamp (timetables, the console, etc).
                string gameTime = BusScheduler.Instance != null
                    ? BusScheduler.MinutesToTimeString(BusScheduler.Instance.GameTimeMinutes)
                    : "--:--";
                _state.RefreshFrom(_source, gameTime);
                var newRotation = _state.BuildRotation();
                if (newRotation.Count != _rotation.Count) _flashIndex = 0;
                _rotation = newRotation;
            }
        }

        _flashTimer -= Time.deltaTime;
        if (_flashTimer <= 0f)
        {
            _flashTimer = flashHoldSeconds;
            if (_rotation.Count > 0)
                _flashIndex = (_flashIndex + 1) % _rotation.Count;
        }
    }

    private void OnGUI()
    {
        // [07-30] IMGUI fires OnGUI once per EVENT, not once per frame -- a
        // KeyDown, MouseDown, or MouseUp during input is its own separate
        // OnGUI pass with Event.current.type != Repaint. This method was
        // swapping RenderTexture.active and issuing GL/GUI draws on EVERY one
        // of those passes, not just the real repaint -- so holding a key or
        // clicking anywhere spawned extra partial draws racing the real one,
        // which is exactly what read as the board flashing/going black while
        // a key or mouse button was held. Only touch the render texture on
        // an actual Repaint event; every other event type is a no-op here.
        if (Event.current == null || Event.current.type != EventType.Repaint) return;

        // [PERF] Skip the actual RenderTexture redraw when off-screen/far --
        // the texture just holds whatever it last drew, which is correct
        // since nobody could see it change anyway.
        if (_visGate != null && !_visGate.ShouldRender) return;

        if (_rt == null) return;
        if (!_stylesBuilt) { BuildStyles(); _stylesBuilt = true; }

        RenderTexture prevActive = RenderTexture.active;
        RenderTexture.active = _rt;
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, textureResolution.x, textureResolution.y, 0);

        bool runningNow = _source != null && _source.IsEngineRunning;

        if (_booting)
        {
            GL.Clear(true, true, Color.black);
            DrawBoot();
        }
        else if (!runningNow)
        {
            // Fully off. Nothing drawn -- board reads as a dead black panel.
            GL.Clear(true, true, Color.black);
        }
        else
        {
            GL.Clear(true, true, blackZoneColor);
            DrawPanel();
            DrawScreenOverlay();
        }

        GL.PopMatrix();
        RenderTexture.active = prevActive;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  BOOT SEQUENCE — white/red/green/blue flash, blank, fast alphabet
    //  type-on, fleet number readout. All timings are constants above; total
    //  runs well inside the 15-second budget.
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawBoot()
    {
        float w = textureResolution.x, h = textureResolution.y;
        float t = _bootTimer;

        float tWhite = PH_WHITE;
        float tRed   = tWhite + PH_RED;
        float tGreen = tRed   + PH_GREEN;
        float tBlue  = tGreen + PH_BLUE;
        float tBlank = tBlue  + PH_BLANK;
        float tAlpha = tBlank + PH_ALPHA;
        float tFleet = tAlpha + PH_FLEET;
        // tSettle = tFleet + PH_SETTLE (implicit end)

        if (t < tWhite)      { GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture); return; }
        if (t < tRed)        { DrawSolid(new Color(0.9f, 0.05f, 0.05f)); return; }
        if (t < tGreen)      { DrawSolid(new Color(0.05f, 0.85f, 0.15f)); return; }
        if (t < tBlue)       { DrawSolid(new Color(0.10f, 0.25f, 0.95f)); return; }
        if (t < tBlank)      { return; } // already cleared black by caller

        if (t < tAlpha)
        {
            // Fast type-on: reveal one more letter of the QWERTY row at a
            // steady clip across the whole alpha phase.
            float phaseT = Mathf.InverseLerp(tBlank, tAlpha, t);
            int   count  = Mathf.Clamp(Mathf.CeilToInt(phaseT * ALPHA_ROW.Length), 0, ALPHA_ROW.Length);
            string shown = ALPHA_ROW.Substring(0, count);
            GUI.Label(new Rect(0, 0, w, h), shown, _bootStyle);
            return;
        }

        if (t < tFleet)
        {
            int fleet = _source != null ? _source.FleetNumber : -1;
            string label = fleet >= 0 ? $"FLEET {fleet}" : "FLEET --";
            GUI.Label(new Rect(0, 0, w, h), label, _bootStyle);
            return;
        }

        // Settle: brief blank breath before normal operation picks up next frame.
    }

    private void DrawSolid(Color c)
    {
        Color prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(new Rect(0, 0, textureResolution.x, textureResolution.y), Texture2D.whiteTexture);
        GUI.color = prev;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PANEL LAYOUT — normal operation
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawPanel()
    {
        float w = textureResolution.x;
        float h = textureResolution.y;

        float blackH = h * (4f / 5f);
        float grayH  = h - blackH;

        // ── BLACK ZONE ──────────────────────────────────────────────────────
        var blackRect = new Rect(0, 0, w, blackH);
        DrawFlat(blackRect, blackZoneColor);

        // Raw route/variant number, top-left, ZERO padding -- literally the
        // first pixel of the panel. Destination sits directly beside it so
        // you always know where the bus terminates without hunting for an
        // ETA -- this line never shows time, only identity.
        float headerH = blackH * 0.26f;
        string rt = string.IsNullOrEmpty(_state.routeNumber) ? "--" : _state.routeNumber;

        // Autofit: shrink the font until "109", "87A", or whatever else fits
        // inside its column instead of clipping. Only re-measures when the
        // text changes, not every frame.
        float maxNumWidth = w * 0.40f;
        EnsureRouteFontFits(rt, maxNumWidth);
        float numW = _rawRouteStyle.CalcSize(new GUIContent(rt)).x;
        GUI.Label(new Rect(0, 0, numW, headerH), rt, _rawRouteStyle);

        string dest = (_state.destinationHeadsign ?? "").ToUpperInvariant();
        GUI.Label(new Rect(numW + headerH * 0.12f, 0, w - numW - headerH * 0.12f, headerH), dest, _destTopStyle);

        // ── STOP-ROW STACK — shrinks on its own as GetUpcomingStopsWithEta's
        //    list gets shorter (3 -> 2 -> 1 -> 0), which is exactly the "2
        //    rectangles near the terminal, then just the terminal" behavior.
        var rows = _state.upcomingStopsWithEta;
        float inset    = w * 0.02f;
        float stackTop = headerH + blackH * 0.03f;
        float stackH   = blackH - stackTop - blackH * 0.03f;

        if (rows != null && rows.Count > 0)
        {
            float rowGap = 3f;
            float rowH   = stackH / rows.Count;

            for (int i = 0; i < rows.Count; i++)
            {
                var rowRect = new Rect(inset, stackTop + rowH * i, w - inset * 2f, rowH - rowGap);
                Color bg = (i % 2 == 0) ? rowColorLight : rowColorDark;
                DrawFlat(rowRect, bg);

                string nameText = rows[i].stopName + (rows[i].isTerminal ? "  (TERMINAL)" : "");
                var nameRect = new Rect(rowRect.x + 12f, rowRect.y, rowRect.width * 0.62f, rowRect.height);
                GUI.Label(nameRect, nameText, rows[i].isTerminal ? _rowNameTermStyle : _rowNameStyle);

                SplitEta(rows[i].etaLabel, out string big, out string sub);
                float etaColX = nameRect.xMax;
                float etaColW = rowRect.xMax - etaColX - 10f;
                var bigRect = new Rect(etaColX, rowRect.y, etaColW * 0.62f, rowRect.height);
                var subRect = new Rect(bigRect.xMax, rowRect.y, etaColW - bigRect.width, rowRect.height);
                GUI.Label(bigRect, big, _etaBigStyle);
                GUI.Label(subRect, sub, _etaSubStyle);
            }
        }
        // rows.Count == 0 (trip's out of upcoming stops): area stays flat
        // black -- the "then finally off" collapse for this section, without
        // powering down the whole board.

        // ── GRAY ZONE ───────────────────────────────────────────────────────
        var grayRect = new Rect(0, blackH, w, grayH);
        DrawFlat(grayRect, grayZoneColor);

        string flashText = (_rotation != null && _rotation.Count > 0)
            ? _rotation[Mathf.Clamp(_flashIndex, 0, _rotation.Count - 1)]
            : "";
        // Faux bold: GUIStyle.fontStyle = Bold silently no-ops on some fonts
        // (Unity's built-in default has no distinct bold glyphs unless a TTF
        // with a bold face is assigned) -- stack a few 1px-offset passes so
        // it reads bold regardless of what font ends up assigned.
        DrawFauxBold(grayRect, flashText, _grayStripStyle);
    }

    /// <summary>Splits an ETA label into a big number and a small unit
    /// suffix. Input now comes straight from BusTrackerService in its own
    /// format -- "&lt;1 min" or "N min" (always singular "min", matching the
    /// exterior tracker's own convention) -- plus "--" for no data. Handles:
    /// "&lt;1 min" -> "&lt;1"/"min", "5 min" -> "5"/"min", "--" -> "--"/"".</summary>
    private static void SplitEta(string etaLabel, out string big, out string sub)
    {
        if (string.IsNullOrEmpty(etaLabel) || etaLabel == "--") { big = "--"; sub = ""; return; }

        // [FIX 07-31] Own-bus ETA now comes directly from BusTrackerService's
        // shared "<1 min" / "N min" format (same one the exterior stop-side
        // tracker uses) instead of a bare number this method used to append
        // its own "min"/"mins" suffix to. Strip the trailing " min" the
        // source string already includes and just split what's left.
        const string suffix = " min";
        if (etaLabel.EndsWith(suffix))
        {
            big = etaLabel.Substring(0, etaLabel.Length - suffix.Length); // "<1" or "5"
            sub = "min";
            return;
        }

        // Fallback for any caller still passing a bare number.
        big = etaLabel;
        sub = (etaLabel == "1") ? "min" : "mins";
    }

    private static void DrawFlat(Rect r, Color c)
    {
        Color prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = prev;
    }

    /// <summary>Shrinks _rawRouteStyle's font size until `text` fits inside
    /// maxWidth. Cached by text so it only re-measures when the route number
    /// actually changes, not every OnGUI call.</summary>
    private void EnsureRouteFontFits(string text, float maxWidth)
    {
        if (text == _routeTextSizedFor && _routeFontSizeCache > 0)
        {
            _rawRouteStyle.fontSize = _routeFontSizeCache;
            return;
        }

        int baseSize = Mathf.RoundToInt(textureResolution.y * 0.19f);
        int size     = baseSize;
        _rawRouteStyle.fontSize = size;
        var content = new GUIContent(text);

        while (size > 12 && _rawRouteStyle.CalcSize(content).x > maxWidth)
        {
            size -= 2;
            _rawRouteStyle.fontSize = size;
        }

        _routeFontSizeCache = size;
        _routeTextSizedFor  = text;
    }

    /// <summary>Draws `text` four times at 1px offsets plus once centered so
    /// it reads bold even if the active font has no real bold glyphs.</summary>
    private static void DrawFauxBold(Rect r, string text, GUIStyle style)
    {
        GUI.Label(new Rect(r.x - 1, r.y, r.width, r.height), text, style);
        GUI.Label(new Rect(r.x + 1, r.y, r.width, r.height), text, style);
        GUI.Label(new Rect(r.x, r.y - 1, r.width, r.height), text, style);
        GUI.Label(new Rect(r.x, r.y + 1, r.width, r.height), text, style);
        GUI.Label(r, text, style);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  SCREEN OVERLAY — scanlines + vignette, drawn last so they sit over
    //  everything else and read as glass rather than a flat printed graphic.
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawScreenOverlay()
    {
        var full = new Rect(0, 0, textureResolution.x, textureResolution.y);
        if (scanlineStrength > 0f && _scanlineTex != null)
        {
            GUI.color = new Color(1f, 1f, 1f, scanlineStrength);
            GUI.DrawTexture(full, _scanlineTex);
            GUI.color = Color.white;
        }
        if (vignetteStrength > 0f && _vignetteTex != null)
        {
            GUI.color = new Color(1f, 1f, 1f, vignetteStrength);
            GUI.DrawTexture(full, _vignetteTex);
            GUI.color = Color.white;
        }
    }

    private void BuildOverlayTextures()
    {
        int w = textureResolution.x, h = textureResolution.y;
        var key = new Vector2Int(w, h);

        // [FIX] Check the shared cache first -- only the very first board
        // built at this resolution actually generates the pixels; every
        // board after that (the overwhelming majority, at fleet scale)
        // just grabs the existing reference for free.
        if (_sharedScanlineTex.TryGetValue(key, out var cachedScan) && cachedScan != null)
        {
            _scanlineTex = cachedScan;
        }
        else
        {
            _scanlineTex = new Texture2D(1, h, TextureFormat.Alpha8, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
            var scanCols = new Color[h];
            for (int y = 0; y < h; y++) scanCols[y] = new Color(0, 0, 0, (y % 2 == 0) ? 1f : 0f);
            _scanlineTex.SetPixels(scanCols);
            _scanlineTex.Apply(false, true);
            _sharedScanlineTex[key] = _scanlineTex;
        }

        if (_sharedVignetteTex.TryGetValue(key, out var cachedVig) && cachedVig != null)
        {
            _vignetteTex = cachedVig;
            return;
        }

        _vignetteTex = new Texture2D(w, h, TextureFormat.Alpha8, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var vigCols = new Color[w * h];
        Vector2 centre = new Vector2(w * 0.5f, h * 0.5f);
        float maxDist = centre.magnitude;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), centre) / maxDist;
                vigCols[y * w + x] = new Color(0, 0, 0, Mathf.Clamp01(Mathf.Pow(d, 2.2f)));
            }
        _vignetteTex.SetPixels(vigCols);
        _sharedVignetteTex[key] = _vignetteTex;
        _vignetteTex.Apply(false, true);
    }

    private void BuildStyles()
    {
        int h = textureResolution.y;

        _rawRouteStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = Mathf.RoundToInt(h * 0.19f), // overridden per-frame by EnsureRouteFontFits
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            padding   = new RectOffset(0, 0, 0, 0),
            margin    = new RectOffset(0, 0, 0, 0),
        };
        _rawRouteStyle.normal.textColor = Color.white;

        _destTopStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = Mathf.RoundToInt(h * 0.075f),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            wordWrap  = true,
        };
        _destTopStyle.normal.textColor = new Color(0.75f, 0.78f, 0.80f);

        _rowNameStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = Mathf.RoundToInt(h * 0.065f),
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.MiddleLeft,
        };
        _rowNameStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);

        _rowNameTermStyle = new GUIStyle(_rowNameStyle) { fontStyle = FontStyle.Bold };
        _rowNameTermStyle.normal.textColor = new Color(0.35f, 0.75f, 1f);

        _etaBigStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = Mathf.RoundToInt(h * 0.11f),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleRight,
        };
        _etaBigStyle.normal.textColor = Color.white;

        _etaSubStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = Mathf.RoundToInt(h * 0.045f),
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.LowerLeft,
        };
        _etaSubStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f);

        _grayStripStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = Mathf.RoundToInt(h * 0.06f),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };
        _grayStripStyle.normal.textColor = new Color(0.08f, 0.08f, 0.08f);

        _bootStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = Mathf.RoundToInt(h * 0.16f),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };
        _bootStyle.normal.textColor = new Color(0.2f, 0.9f, 1f);
    }

    private void BuildRenderTexture()
    {
        // [ADD] Destroy-before-create guard -- if _rt already exists when
        // this runs (BuildRenderTexture called a second time on the SAME
        // instance, for whatever reason), the old GPU allocation is fully
        // released and destroyed before a new one is minted. Makes this
        // method self-healing against double-invocation instead of quietly
        // orphaning the previous texture. NOTE: this does NOT help if the
        // real leak is duplicate GameObjects being spawned rather than this
        // method being called twice on one instance -- that needs a fix at
        // the spawn/pooling site instead, see BusBoardTestSwitch's isolation
        // test for telling the two apart.
        if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }

        _rt = new RenderTexture(textureResolution.x, textureResolution.y, 0, RenderTextureFormat.ARGB32)
        {
            name       = $"LCDBoardRT_{name}",
            filterMode = FilterMode.Bilinear,
            useMipMap  = false,
            wrapMode   = TextureWrapMode.Clamp,
        };
        _rt.Create();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PHYSICAL HOUSING — real depth, not a flat quad. See the class header;
    //  unchanged from the previous pass.
    // ═════════════════════════════════════════════════════════════════════════
    private void BuildHousing()
    {
        float depth = Mathf.Max(0.003f, boardDepthMetres);
        float lip   = Mathf.Min(boardSizeMetres.x, boardSizeMetres.y) * bezelLipFraction;

        var housing = GameObject.CreatePrimitive(PrimitiveType.Cube);
        housing.name = "LCDHousing";
        Destroy(housing.GetComponent<Collider>());
        housing.transform.SetParent(transform, false);
        housing.transform.localRotation = Quaternion.identity;
        housing.transform.localPosition = new Vector3(0f, 0f, depth * 0.5f);
        housing.transform.localScale    = new Vector3(boardSizeMetres.x + lip * 2f, boardSizeMetres.y + lip * 2f, depth);

        var hr = housing.GetComponent<MeshRenderer>();
        if (!_sharedHousingMaterial.TryGetValue(housingColor, out _housingMaterial) || _housingMaterial == null)
        {
            _housingMaterial = new Material(GetBestOpaqueShader());
            SetMatColor(_housingMaterial, housingColor);
            _sharedHousingMaterial[housingColor] = _housingMaterial;
        }
        hr.sharedMaterial    = _housingMaterial;
        hr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        hr.receiveShadows    = true;

        var strip = GameObject.CreatePrimitive(PrimitiveType.Cube);
        strip.name = "LCDAccentStrip";
        Destroy(strip.GetComponent<Collider>());
        strip.transform.SetParent(transform, false);
        float stripH = boardSizeMetres.y * 0.035f;
        strip.transform.localRotation = Quaternion.identity;
        strip.transform.localPosition = new Vector3(0f, -boardSizeMetres.y * 0.5f - lip * 0.4f, -0.0025f);
        strip.transform.localScale    = new Vector3(boardSizeMetres.x + lip * 1.2f, stripH, 0.004f);

        var sr = strip.GetComponent<MeshRenderer>();
        if (!_sharedStripMaterial.TryGetValue(accentStripColor, out _stripMaterial) || _stripMaterial == null)
        {
            _stripMaterial = new Material(GetBestOpaqueShader());
            SetMatColor(_stripMaterial, accentStripColor);
            if (_stripMaterial.HasProperty("_EmissionColor"))
            {
                _stripMaterial.EnableKeyword("_EMISSION");
                _stripMaterial.SetColor("_EmissionColor", accentStripColor * 1.6f);
            }
            _sharedStripMaterial[accentStripColor] = _stripMaterial;
        }
        sr.sharedMaterial    = _stripMaterial;
        sr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "LCDScreen";
        Destroy(quad.GetComponent<Collider>());
        quad.transform.SetParent(transform, false);
        quad.transform.localPosition = new Vector3(0f, 0f, -0.0015f);
        quad.transform.localRotation = Quaternion.identity;
        quad.transform.localScale    = new Vector3(boardSizeMetres.x, boardSizeMetres.y, 1f);

        var mr = quad.GetComponent<MeshRenderer>();
        if (_sharedQuadMaterial == null)
            _sharedQuadMaterial = new Material(Shader.Find("Unlit/Texture"));
        mr.sharedMaterial = _sharedQuadMaterial;
        _quadPropBlock = new MaterialPropertyBlock();
        _quadPropBlock.SetTexture("_MainTex", _rt);
        mr.SetPropertyBlock(_quadPropBlock);
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows    = false;

        // [PERF] Gate lives on the quad, since that's the actual renderer --
        // OnBecameVisible/Invisible only fire on the GameObject holding the
        // Renderer component itself.
        _visGate = quad.AddComponent<BusBoardVisibilityGate>();
        _visGate.maxDistance = cullDistanceMetres;
    }

    private static Shader GetBestOpaqueShader()
    {
        Shader s = Shader.Find("Universal Render Pipeline/Lit");
        if (s == null) s = Shader.Find("Standard");
        if (s == null) s = Shader.Find("Diffuse");
        if (s == null) s = Shader.Find("Unlit/Color");
        return s;
    }

    private static void SetMatColor(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        else if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        else m.color = c;
    }
}