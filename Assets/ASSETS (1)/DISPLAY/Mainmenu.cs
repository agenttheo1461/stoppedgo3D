using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// v2.0 — Full-screen redesign. Previously a small popup panel that just
/// showed name/level/PLAY and called into ShiftBoardMenu.OpenBoard() to
/// actually pick a route. Per explicit instruction, this now HAS ITS OWN
/// integrated route + bus selection instead of spawning a separate menu --
/// route list and bus list logic below is ported directly from
/// ShiftBoardMenu.cs's own RouteOption/DepotBusOption classes and their
/// Refresh methods (proven, already-working code, not reinvented).
///
/// ShiftBoardMenu.cs itself is DELIBERATELY left fully intact and
/// untouched -- it still owns the mid-shift terminal-decision popup
/// (OpenAsTerminalDecision, triggered while actively driving when a leg
/// ends). That's a different situation from "player is off duty, picking
/// their first route of the session" and shouldn't be replaced by a
/// full-screen takeover mid-drive.
///
/// Bus selection here is deliberately simple per instruction -- pick a
/// specific bus from whatever the depot has idle for that route, no
/// livery/customization at all (that's BusSelectMenu's job, a completely
/// separate flow for a completely separate purpose).
/// </summary>
public class MainMenu : MonoBehaviour
{
    public static MainMenu Instance { get; private set; }

    [Header("First Launch")]
    public bool autoOpenOnFirstLaunchIfNoProfile = true;

    private bool _open;
    public bool IsOpen => _open;

    // [ADD Bug 43 fix] Nothing else in the project checked this before
    // acting -- debug hotkeys (Ctrl+F7 reset, Ctrl+F4 schedule dump,
    // Ctrl+Shift+F12 NPC toggle) all fired even while this menu was open,
    // letting a player poke at live game state (or just get confused by
    // console spam) before ever picking a route. Single place to check.
    public static bool BlocksInput => Instance != null && Instance.IsOpen;

    private string _nameInput = "";
    private string _createError = null;

    // [ADD] Item 14 -- entrance animation. Every element that reads this was
    // previously just placed at its final Rect every frame with no
    // transition at all. Stamped whenever the menu actually opens (not just
    // while it stays open), so re-opening after a session plays the same
    // brief ease-in again instead of only firing once ever.
    private float _openedAtRealtime = -999f;
    private const float OpenAnimDuration = 0.28f;
    private float OpenAnimFrac
    {
        get
        {
            float t = Mathf.Clamp01((Time.realtimeSinceStartup - _openedAtRealtime) / OpenAnimDuration);
            return 1f - Mathf.Pow(1f - t, 3f); // ease-out cubic
        }
    }

    // ── Route/bus selection state -- ported shape from ShiftBoardMenu ──────
    private class RouteOption
    {
        public BusRouteData route;
        public string routeNumber;
        public string variantLetter;
        public bool outbound;
        public float departureAbsMin;
        public int laps;
        public string requiredDepotLabel;
        public int idleEligibleCount;
        // [ADD] Shift Maker integration -- true when this option came from the
        // player's own saved plan (ShiftMakerData) rather than the normal
        // auto-picked live routes, so the card can call that out.
        public bool isCustomPick;
    }

    private class DepotBusOption
    {
        public int fleetNumber;
        public string busType;
        public string seriesName;   // per-series render icon (Resources/SeriesIcons/<seriesName>.png), busType icon is the fallback
        public string depotName;
        public bool isArticulated;
    }

    private readonly List<RouteOption> _options = new List<RouteOption>();
    private readonly List<DepotBusOption> _depotBuses = new List<DepotBusOption>();
    private RouteOption _selectedRoute;
    private Vector2 _routeScroll, _busScroll;
    private string _pickError;
    private float _pickErrorAt = -999f;
    private const string LAST_FLEET_PREF_KEY = "HEADWAY_LAST_FLEET_PICKED";

    // [ADD] Series icon -- a small hand-drawn image per bus SERIES (not per
    // individual bus), shown when a bus row is selected. Loaded from
    // Resources/SeriesIcons/<busType>.png -- drop a PNG in there named
    // after the series (matching whatever DepotBusOption.busType shows,
    // e.g. "XD40.png") and it shows up automatically, no code changes
    // needed per series. Cached so repeated selections of the same series
    // don't re-hit Resources.Load every time. Missing icon = just shows
    // nothing, not an error, so you can add these incrementally.
    private readonly Dictionary<string, Texture2D> _seriesIconCache = new Dictionary<string, Texture2D>();
    private Texture2D GetSeriesIcon(string busType, string seriesName = null)
    {
        string key = (seriesName ?? "") + "|" + (busType ?? "");
        if (_seriesIconCache.TryGetValue(key, out var cached)) return cached;
        // Prefer the transparent 3D render for THIS series (Tools > Bus > Generate Main Menu Bus Icons), else the old per-busType icon.
        Texture2D tex = null;
        if (!string.IsNullOrEmpty(seriesName)) tex = Resources.Load<Texture2D>("SeriesIcons/" + seriesName);
        if (tex == null && !string.IsNullOrEmpty(busType)) tex = Resources.Load<Texture2D>("SeriesIcons/" + busType);
        _seriesIconCache[key] = tex; // caches the null too, so a missing icon isn't retried every frame
        return tex;
    }

    private bool _stylesReady;
    private GUIStyle _lblTitle, _lblSub, _lblDim, _lblBody, _lblBig, _lblError, _lblCyan, _lblAmber;
    private GUIStyle _btnPrimary, _btnSecond;
    private GUIStyle _textField;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        // Forced on at all times -- no longer gated behind
        // autoOpenOnFirstLaunchIfNoProfile / HasProfile, so this is always
        // the first thing shown on launch regardless of profile state.
        _open = true;
        _openedAtRealtime = Time.realtimeSinceStartup;

        // [FIX] "doesn't show anything on init" -- ToggleOpen()/Open() both
        // call RefreshRouteOptions(), but Start() was setting _open = true
        // directly and skipping it. _options stayed empty forever on a
        // fresh launch (DrawRouteColumn just shows "Nothing operating right
        // now"), since nothing else ever calls RefreshRouteOptions() until
        // the player manually closes and reopens the menu.
        RefreshRouteOptions();
    }

    public void ToggleOpen()
    {
        _open = !_open;
        if (_open) { _openedAtRealtime = Time.realtimeSinceStartup; FullResetOnOpen(); ResnapFleetOnOpen(); RefreshRouteOptions(); }
    }

    public void Open()
    {
        _open = true;
        _openedAtRealtime = Time.realtimeSinceStartup;
        FullResetOnOpen();
        ResnapFleetOnOpen();
        RefreshRouteOptions();
    }
    public void Close() => _open = false;

    /// <summary>[ADD Bug 38 fix] Opening this menu used to only resnap NPC
    /// positions and refresh the route list -- everything else from a prior
    /// session (a still-possessed bus, other popups left open, accumulated
    /// garbage) just sat there. This is the actual full stop: releases any
    /// possessed bus back to NPC control (EndShiftFully no-ops safely if
    /// already off duty, so this is always safe to call), force-closes
    /// every other toggleable popup/window, and prompts a GC pass so
    /// nothing from the previous session lingers into this one.</summary>
    private void FullResetOnOpen()
    {
        PlayerHandoff.Instance?.EndShiftFully();

        ShiftBoardMenu.Instance?.Close();
        DispatchConsole.Instance?.Close();
        MDT_LiveMap.Instance?.Close();
        TimetableOverlay.Instance?.Close();
        RouteSnapshotViewerUI.Instance?.Close();
        ShiftMakerWindow.Instance?.Close();

        System.GC.Collect();
    }

    // [ADD] The reset NPC buses used to have for testing real-time routes —
    // re-derives every active NPC bus's correct position from the timetable
    // and snaps it there, same math DebugResetController's Key 7 already
    // exposes (BusScheduler.ResnapAllBusesToSchedule, itself the same
    // AssignRouteWithProgress call FleetDispatcher uses to resolve mid-trip
    // buses on load). Never touched the player's own bus (ResnapAllBusesToSchedule
    // skips it internally). Fixes drift accumulated while off screen, not
    // buses frozen in a dead coroutine or a depot/terminal misalignment —
    // those are separate fixes.
    private void ResnapFleetOnOpen()
    {
        if (BusScheduler.Instance != null)
            BusScheduler.Instance.ResnapAllBusesToSchedule();
    }

    private void OnGUI()
    {
        if (!_open) return;
        EnsureStyles();

        // Full screen, no dimmed-backdrop-over-gameplay treatment -- this
        // IS the screen now, not a popup over it.
        GUI.color = new Color(0.06f, 0.07f, 0.09f, 1f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUI.Label(new Rect(24, 16, 400, 44), "HEADWAY!", _lblTitle);

        bool hasProfile = DriverProfile.Instance != null && DriverProfile.Instance.HasProfile;
        if (!hasProfile) DrawFirstLaunch();
        else DrawMainScreen();
    }

    // ═════════════════════════════════════════════════════════════════════
    //  FIRST LAUNCH
    // ═════════════════════════════════════════════════════════════════════
    private void DrawFirstLaunch()
    {
        // [ADD] Item 14 -- slides up + settles into place instead of just
        // appearing at its final Rect. MDT_UITheme's rounded-rect draws bake
        // color into the texture and reset GUI.color internally (see
        // DrawRoundedRect), so a GUI.color alpha fade wouldn't actually
        // reach the panel itself -- a position offset works uniformly
        // everywhere regardless of how each element is drawn, so that's
        // what both entrance animations in this file use instead.
        float slideOffset = (1f - OpenAnimFrac) * 24f;
        float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f + slideOffset;
        var panel = new Rect(cx - 210, cy - 90, 420, 180);

        MDT_UITheme.DrawSoftShadow(panel, 18f);
        MDT_UITheme.DrawRoundedRectBordered(panel, 18f, MDT_UITheme.BGDeep, MDT_UITheme.BorderAccent, 1);

        float innerX = panel.x + 18, innerW = panel.width - 36;
        GUI.Label(new Rect(innerX, panel.y + 14, innerW, 24), "Welcome — what should we call you?", _lblSub);
        GUI.SetNextControlName("DriverNameField");
        _nameInput = GUI.TextField(new Rect(innerX, panel.y + 44, innerW, 32), _nameInput, 24, _textField);

        float ey = panel.y + 86;
        if (!string.IsNullOrEmpty(_createError))
        {
            GUI.Label(new Rect(innerX, ey, innerW, 22), _createError, _lblError);
            ey += 26f;
        }

        if (GUI.Button(new Rect(innerX, ey, innerW, 40), "CREATE PROFILE", _btnPrimary))
        {
            string trimmed = _nameInput.Trim();
            if (string.IsNullOrEmpty(trimmed)) _createError = "Enter a name first.";
            else if (DriverProfile.Instance == null) _createError = "Driver profile system unavailable — try again in a moment.";
            else { DriverProfile.Instance.CreateProfile(trimmed); _createError = null; }
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  MAIN SCREEN — header strip, route list (left), bus preview/pick (right)
    // ═════════════════════════════════════════════════════════════════════
    private void DrawMainScreen()
    {
        var profile = DriverProfile.Instance;
        var pm = PointsManager.Instance;

        // [ADD] Item 14 -- whole screen eases up into place on open, same
        // slide-only approach DrawFirstLaunch uses (see its comment for why
        // not a GUI.color fade).
        float headerY = 70f + (1f - OpenAnimFrac) * 18f;
        GUI.Label(new Rect(24, headerY, 500, 28), profile.driverName, _lblBig);
        // [FIX] Was one combined "Level X · Y pts lifetime" line -- now
        // three explicit, separately-labeled stats per instruction.
        if (pm != null)
        {
            GUI.Label(new Rect(24, headerY + 32, 150, 18), $"LEVEL: {pm.level}", _lblAmber);
            GUI.Label(new Rect(150, headerY + 32, 150, 18), $"XP: {pm.xp:N0} / {pm.xpToNextLevel:N0}", _lblAmber);
            GUI.Label(new Rect(340, headerY + 32, 200, 18), $"POINTS: {pm.lifetimePoints:N0}", _lblAmber);

            float barX = 24, barW = 300, barY = headerY + 54, barH = 6f;
            MDT_UITheme.DrawRoundedRect(new Rect(barX, barY, barW, barH), 3f, MDT_UITheme.BGButton);
            float frac = pm.xpToNextLevel > 0 ? Mathf.Clamp01(pm.xp / (float)pm.xpToNextLevel) : 0f;
            MDT_UITheme.DrawRoundedRect(new Rect(barX, barY, barW * frac, barH), 3f, MDT_UITheme.TextGreen);
        }
        GUI.Label(new Rect(Screen.width - 424, headerY, 400, 20),
            $"{profile.totalShiftsCompleted} shifts  ·  {profile.totalLapsCompleted} laps  ·  {profile.OnTimePercentage:0}% on-time",
            _lblDim);

        MDT_UITheme.DrawDivider(24, headerY + 76, Screen.width - 48);

        float bodyY = headerY + 96f;
        float bodyH = Screen.height - bodyY - 90f;
        float colGap = 24f;
        float leftW = Screen.width * 0.42f;
        float rightX = 24 + leftW + colGap;
        float rightW = Screen.width - rightX - 24f;

        DrawRouteColumn(new Rect(24, bodyY, leftW, bodyH));
        DrawBusColumn(new Rect(rightX, bodyY, rightW, bodyH));

        float footerY = Screen.height - 66f;
        bool canPlay = _selectedRoute != null;
        // [ADD] Item 14 -- a subtle pulsing ring once route+bus are BOTH
        // picked (not just route -- clicking PLAY with only a route chosen
        // still works, TryClaimSelected already handles "no bus" with its
        // own error message, but there's nothing to visually celebrate
        // until the pick is actually complete).
        bool readyToPlay = canPlay && _selectedBusFleet >= 0;
        var playRect = new Rect(Screen.width - 224, footerY, 200, 48);
        if (readyToPlay)
        {
            float pulse = Mathf.Abs(Mathf.Sin(Time.realtimeSinceStartup * 2.2f));
            MDT_UITheme.DrawRoundedRectBordered(
                new Rect(playRect.x - 3, playRect.y - 3, playRect.width + 6, playRect.height + 6),
                14f, new Color(0, 0, 0, 0), MDT_UITheme.TextGreen * new Color(1, 1, 1, 0.35f + pulse * 0.4f), 2);
        }
        var prevEnabled = GUI.enabled;
        GUI.enabled = canPlay;
        if (GUI.Button(playRect, "PLAY", _btnPrimary) && canPlay)
            TryClaimSelected();
        // [ADD] Jumps straight into the full RouteSnapshotViewerUI sheet for
        // whichever route is selected -- the mini preview on the card is
        // just a glance; this is "show everything" (full stop list count,
        // headway table, change history) without leaving the picker or
        // hunting for it behind F9.
        if (GUI.Button(new Rect(Screen.width - 434, footerY, 200, 48), "SERVICE SHEET", _btnSecond) && canPlay)
            RouteSnapshotViewerUI.Instance?.ShowRoute(_selectedRoute.routeNumber);
        GUI.enabled = prevEnabled;

        if (!string.IsNullOrEmpty(_pickError) && Time.realtimeSinceStartup - _pickErrorAt < 6f)
            GUI.Label(new Rect(24, footerY + 12, Screen.width - 260, 24), _pickError, _lblError);

        if (GUI.Button(new Rect(24, footerY, 100, 34), "CLOSE", _btnSecond))
            _open = false;
        // [ADD] Shift Maker entry point -- opens the draggable day-plan window.
        if (GUI.Button(new Rect(132, footerY, 140, 34), "PLAN MY DAY", _btnSecond))
            ShiftMakerWindow.Instance?.Open();
    }

    private void DrawRouteColumn(Rect area)
    {
        GUI.Label(new Rect(area.x, area.y, area.width, 22), "AVAILABLE ROUTES", _lblSub);
        var listArea = new Rect(area.x, area.y + 28, area.width, area.height - 28);

        if (_options.Count == 0)
        {
            GUI.Label(new Rect(listArea.x, listArea.y, listArea.width, 30), "Nothing operating right now — check back later.", _lblDim);
            return;
        }

        float cardH = 96f, gap = 10f;
        float contentH = Mathf.Max(listArea.height, _options.Count * (cardH + gap));
        _routeScroll = GUI.BeginScrollView(listArea, _routeScroll, new Rect(0, 0, listArea.width - 20, contentH));
        for (int i = 0; i < _options.Count; i++)
            DrawRouteCard(_options[i], new Rect(0, i * (cardH + gap), listArea.width - 24, cardH));
        GUI.EndScrollView();
    }

    private void DrawRouteCard(RouteOption opt, Rect r)
    {
        // [ADD] Item 14 -- elevation + hover + a real accent border on the
        // selected card instead of just a flat background swap, so picking
        // a route reads as "this one lifted off the stack" rather than
        // "this rect changed color."
        bool selected = _selectedRoute == opt;
        bool hover    = Event.current.type == EventType.Repaint && r.Contains(Event.current.mousePosition);
        if (selected || hover) MDT_UITheme.DrawSoftShadow(r, 16f, offsetY: selected ? 5f : 3f, spread: selected ? 12f : 6f);

        Color fill = selected ? new Color(0.10f, 0.22f, 0.14f, 1f)
                   : hover    ? MDT_UITheme.BGRowHover
                   : MDT_UITheme.BGRowEven;
        if (selected)
            MDT_UITheme.DrawRoundedRectBordered(r, 16f, fill, MDT_UITheme.TextGreen * new Color(1, 1, 1, 0.55f), 2);
        else
            MDT_UITheme.DrawRoundedRect(r, 16f, fill);

        // [ADD] Mini route preview -- pulls the matching RouteDataSnapshot
        // (if one's been exported and is loaded into RouteSnapshotViewerUI)
        // and renders it with the exact same fit-to-bounds line-map code the
        // full service sheet uses, just small. If no snapshot data exists
        // for this route yet, DrawRouteMap already renders a quiet
        // "(no snapshot)" placeholder instead of leaving a blank hole.
        float mapSize = 76f;
        var mapRect = new Rect(r.xMax - mapSize - 10f, r.y + 10f, mapSize, mapSize - 12f);
        var snap = RouteSnapshotViewerUI.Instance != null ? RouteSnapshotViewerUI.Instance.FindRoute(opt.routeNumber) : null;
        RouteSnapshotViewerUI.DrawRouteMap(mapRect, snap, MDT_UITheme.BGDeep, MDT_UITheme.TextCyan, MDT_UITheme.TextAmber, 1.5f);

        // [ADD] Variant badge -- previously nothing on the card distinguished
        // Route 87A from Route 87B, they both just said "Route 87".
        string routeLabel = string.IsNullOrEmpty(opt.variantLetter) ? $"Route {opt.routeNumber}" : $"Route {opt.routeNumber}{opt.variantLetter}";
        if (opt.isCustomPick) routeLabel = "★ " + routeLabel; // [ADD] Shift Maker pick, called out on the card
        string dirLabel = opt.outbound ? "A → Z" : "Z → A";
        float textW = r.width - mapSize - 24f; // leave room for the preview thumbnail on the right
        GUI.Label(new Rect(r.x + 14, r.y + 8, textW, 24), routeLabel, _lblCyan);
        GUI.Label(new Rect(r.x + 14, r.y + 32, textW, 18), dirLabel, _lblDim);
        string depStr = BusScheduler.MinutesToTimeString(opt.departureAbsMin % 1440f);

        // [ADD] Live headway/window label. GetActiveSchedule already falls
        // back to the route's flat headwayFromA/ZMinutes whenever no
        // scheduleWindows are defined (or none of them cover right now), so
        // this never shows a blank/broken value for routes that don't use
        // time-of-day windows at all.
        var variant = opt.route != null ? opt.route.GetVariant(opt.variantLetter) : null;
        var active = opt.route != null
            ? opt.route.GetActiveSchedule(opt.departureAbsMin % 1440f, variant)
            : default;
        string headwayStr = opt.route != null ? BusRouteData.FormatScheduleLabel(active) : "";

        GUI.Label(new Rect(r.x + 14, r.y + 52, textW, 18), $"Departs {depStr}  ·  {LapPlanText(opt.route, opt.laps)}  ·  {headwayStr}", _lblBody);
        string idleTag = opt.idleEligibleCount > 0 ? $"{opt.idleEligibleCount} idle at {opt.requiredDepotLabel}" : "0 idle — none available";
        GUI.Label(new Rect(r.x + 14, r.y + 72, textW, 18), idleTag, opt.idleEligibleCount > 0 ? _lblAmber : _lblError);

        if (GUI.Button(r, GUIContent.none, GUIStyle.none))
        {
            _selectedRoute = opt;
            _selectedBusFleet = -1;
            RefreshDepotBuses(opt);
            _busScroll = Vector2.zero;
        }
    }

    private int _selectedBusFleet = -1;
    private string _selectedBusType = null;
    private string _selectedSeriesName = null;

    private void DrawBusColumn(Rect area)
    {
        GUI.Label(new Rect(area.x, area.y, area.width, 22),
            _selectedRoute == null ? "SELECT A ROUTE" : $"BUSES — ROUTE {_selectedRoute.routeNumber}", _lblSub);
        var listArea = new Rect(area.x, area.y + 28, area.width, area.height - 28);

        if (_selectedRoute == null) return;
        if (_depotBuses.Count == 0)
        {
            GUI.Label(new Rect(listArea.x, listArea.y, listArea.width, 30), "No idle buses currently satisfy this route's requirements.", _lblDim);
            return;
        }

        // [ADD] Series icon panel -- reserves space on the right of the bus
        // list whenever a bus is actually selected, so the list doesn't
        // jump around as you click different rows.
        float iconPanelW = _selectedBusFleet >= 0 ? 132f : 0f;
        var listRect = new Rect(listArea.x, listArea.y, listArea.width - iconPanelW - (iconPanelW > 0 ? 12f : 0f), listArea.height);

        float rowH = 64f, gap = 8f;
        float contentH = Mathf.Max(listRect.height, _depotBuses.Count * (rowH + gap));
        _busScroll = GUI.BeginScrollView(listRect, _busScroll, new Rect(0, 0, listRect.width - 20, contentH));
        for (int i = 0; i < _depotBuses.Count; i++)
            DrawBusRow(_depotBuses[i], new Rect(0, i * (rowH + gap), listRect.width - 24, rowH));
        GUI.EndScrollView();

        if (_selectedBusFleet >= 0)
        {
            var icon = GetSeriesIcon(_selectedBusType, _selectedSeriesName);
            var iconRect = new Rect(listArea.xMax - iconPanelW, listArea.y, iconPanelW, iconPanelW);
            MDT_UITheme.DrawRoundedRect(iconRect, 12f, MDT_UITheme.BGRowEven);
            if (icon != null)
                GUI.DrawTexture(new Rect(iconRect.x + 4, iconRect.y + 4, iconRect.width - 8, iconRect.height - 8), icon, ScaleMode.ScaleToFit);
            else
                GUI.Label(iconRect, "?", _lblDim);
            GUI.Label(new Rect(iconRect.x, iconRect.yMax + 4, iconRect.width, 18), _selectedSeriesName ?? _selectedBusType ?? "", _lblDim);
        }
    }

    private void DrawBusRow(DepotBusOption bus, Rect r)
    {
        // [ADD] Item 14 -- same elevation/hover/border treatment as
        // DrawRouteCard, applied here too for consistency between the two
        // columns.
        bool selected = _selectedBusFleet == bus.fleetNumber;
        bool hover    = Event.current.type == EventType.Repaint && r.Contains(Event.current.mousePosition);
        if (selected || hover) MDT_UITheme.DrawSoftShadow(r, 14f, offsetY: selected ? 4f : 2f, spread: selected ? 10f : 5f);

        Color fill = selected ? new Color(0.10f, 0.22f, 0.14f, 1f)
                   : hover    ? MDT_UITheme.BGRowHover
                   : MDT_UITheme.BGRowEven;
        if (selected)
            MDT_UITheme.DrawRoundedRectBordered(r, 14f, fill, MDT_UITheme.TextGreen * new Color(1, 1, 1, 0.55f), 2);
        else
            MDT_UITheme.DrawRoundedRect(r, 14f, fill);

        string sizeTag = bus.isArticulated ? " · Articulated (60ft)" : "";
        GUI.Label(new Rect(r.x + 12, r.y + 6, r.width - 24, 20), $"Fleet #{bus.fleetNumber}", _lblCyan);
        GUI.Label(new Rect(r.x + 12, r.y + 30, r.width - 24, 18), $"{bus.busType}{sizeTag}  ·  {bus.depotName}", _lblDim);

        if (GUI.Button(r, GUIContent.none, GUIStyle.none))
        {
            _selectedBusFleet = bus.fleetNumber;
            _selectedBusType = bus.busType;
            _selectedSeriesName = bus.seriesName;
        }
    }

    private void TryClaimSelected()
    {
        if (_selectedRoute == null) return;
        if (_selectedBusFleet < 0)
        {
            _pickError = "Pick a bus first.";
            _pickErrorAt = Time.realtimeSinceStartup;
            return;
        }

        var realSlot = ResolveRealSlot(_selectedRoute);
        if (realSlot == null)
        {
            _pickError = $"Couldn't claim that departure for Route {_selectedRoute.routeNumber} — it may have just been taken or already departed. Refreshing routes.";
            _pickErrorAt = Time.realtimeSinceStartup;
            RefreshRouteOptions();
            return;
        }

        ShiftRunner.Instance?.ClaimRouteAndBus(_selectedRoute.routeNumber, _selectedRoute.variantLetter, realSlot, _selectedBusFleet);
        PlayerPrefs.SetInt(LAST_FLEET_PREF_KEY, _selectedBusFleet);
        _pickError = null;
        _open = false;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Route/bus data -- ported directly from ShiftBoardMenu.cs
    // ═════════════════════════════════════════════════════════════════════
    private void RefreshRouteOptions()
    {
        var prev = _selectedRoute;
        _options.Clear();
        _selectedRoute = null;
        if (BusScheduler.Instance == null || SimClock.Instance == null) return;

        float now = SimClock.Instance.AbsoluteGameMinutes;
        int day = SimClock.Instance.GameDayNumber;

        var liveRoutes = BusScheduler.Instance.managedRoutes.Where(r => r != null && RouteCoversNow(r, now)).ToList();

        var scored = new List<(BusRouteData route, BusScheduler.RouteBusEntry entry)>();
        foreach (var r in liveRoutes)
        {
            var upcoming = BusScheduler.Instance.GetTodaysBusesForRoute(r.routeNumber, day)
                .Where(e => e.scheduledDeparture >= now + BusScheduler.BoardingLeadFor(now))
                .OrderBy(e => e.scheduledDeparture)
                .FirstOrDefault();
            if (upcoming.scheduledDeparture > 0f) scored.Add((r, upcoming));
        }

        foreach (var pair in scored.OrderBy(s => s.entry.scheduledDeparture).Take(8))
        {
            var route = pair.route;
            var entry = pair.entry;
            var depot = DepotManager.Instance != null ? DepotManager.Instance.GetDepotForRoute(route.routeNumber) : null;

            _options.Add(new RouteOption
            {
                route = route,
                routeNumber = route.routeNumber,
                outbound = entry.isOutbound,
                variantLetter = entry.variantLetter ?? "",
                departureAbsMin = entry.scheduledDeparture,
                laps = EstimateLapsForBlock(route, entry),
                requiredDepotLabel = depot != null ? depot.depotName : "Any depot",
                idleEligibleCount = CountIdleEligibleBuses(route.routeNumber),
            });
        }

        AppendResolvedCustomEntries(now);

        // Keep the player's pick across the once-a-minute refresh if that
        // exact departure is still on offer.
        if (prev != null)
            _selectedRoute = _options.FirstOrDefault(o => o.routeNumber == prev.routeNumber && o.outbound == prev.outbound
                && (o.variantLetter ?? "") == (prev.variantLetter ?? "") && Mathf.Approximately(o.departureAbsMin, prev.departureAbsMin));
        if (_selectedRoute == null) _selectedBusFleet = -1;
        _lastRefreshMinute = SimClock.Instance != null ? Mathf.FloorToInt(SimClock.Instance.AbsoluteGameMinutes) : -1;
    }

    private int _lastRefreshMinute = -1;
    private void Update()
    {
        if (!_open || SimClock.Instance == null) return;
        if (Mathf.FloorToInt(SimClock.Instance.AbsoluteGameMinutes) != _lastRefreshMinute)
        {
            var sel = _selectedRoute;
            RefreshRouteOptions();
            if (_selectedRoute != null && sel != null) RefreshDepotBuses(_selectedRoute);
        }
    }

    private static int CountIdleEligibleBuses(string routeNumber)
    {
        if (BusManager.Instance == null || DepotManager.Instance == null) return 0;
        int count = 0;
        foreach (var record in BusManager.Instance.GetAllRecords())
        {
            if (record == null || record.controller == null || !record.isIdle) continue;
            if (!DepotManager.Instance.CanServeRoute(record.controller.fleetNumber, routeNumber)) continue;
            count++;
        }
        return count;
    }

    /// <summary>[ADD] Shift Maker integration -- resolves each of the player's
    /// saved plan entries against whatever's actually scheduled, picking the
    /// Unassigned departure on that route/direction/day whose time-of-day
    /// falls in the entry's saved window (not an exact match -- the entry is
    /// a flexible wish, not a literal slot, since slots are regenerated
    /// fresh each day). dayOffset is relative to TODAY so a plan replays
    /// sensibly on a different playthrough's own day numbering. Skips an
    /// entry if nothing matches, or if the match is already in _options
    /// (already offered as a normal pick).</summary>
    private void AppendResolvedCustomEntries(float now)
    {
        if (ShiftMakerData.Instance == null || BusScheduler.Instance == null || SimClock.Instance == null) return;
        int today = SimClock.Instance.GameDayNumber;

        foreach (var custom in ShiftMakerData.Instance.Entries)
        {
            var route = BusScheduler.Instance.managedRoutes?.FirstOrDefault(r => r != null && r.routeNumber == custom.routeNumber);
            if (route == null) continue;

            int targetDay = today + Mathf.Max(0, custom.dayOffset);
            float winStart = custom.windowStartMinutes, winEnd = custom.windowEndMinutes;

            var candidate = BusScheduler.Instance.AllSlots
                .Where(s => s.routeNumber == custom.routeNumber
                            && (s.variantLetter ?? "") == (custom.variantLetter ?? "")
                            && s.isOutbound == custom.outbound
                            && s.state == SlotState.Unassigned
                            && s.dayNumber == targetDay
                            && s.scheduledDeparture >= now + BusScheduler.BoardingLeadFor(now))
                .Where(s => WithinWindow(s.scheduledDeparture % 1440f, winStart, winEnd))
                .OrderBy(s => s.scheduledDeparture)
                .FirstOrDefault();
            if (candidate == null) continue;
            if (_options.Any(o => o.routeNumber == candidate.routeNumber && o.outbound == candidate.isOutbound
                && (o.variantLetter ?? "") == (candidate.variantLetter ?? "") && Mathf.Approximately(o.departureAbsMin, candidate.scheduledDeparture)))
                continue;

            var depot = DepotManager.Instance != null ? DepotManager.Instance.GetDepotForRoute(route.routeNumber) : null;
            _options.Add(new RouteOption
            {
                route = route,
                routeNumber = route.routeNumber,
                outbound = candidate.isOutbound,
                variantLetter = candidate.variantLetter ?? "",
                departureAbsMin = candidate.scheduledDeparture,
                laps = EstimateLapsForBlock(route, new BusScheduler.RouteBusEntry { scheduledDeparture = candidate.scheduledDeparture, isOutbound = candidate.isOutbound, variantLetter = candidate.variantLetter, busID = -1 }),
                requiredDepotLabel = depot != null ? depot.depotName : "Any depot",
                idleEligibleCount = CountIdleEligibleBuses(route.routeNumber),
                isCustomPick = true,
            });
        }
    }

    private static TimetableSlot ResolveRealSlot(RouteOption opt)
    {
        if (BusScheduler.Instance == null || opt == null) return null;
        string wantVariant = opt.variantLetter ?? "";
        foreach (var s in BusScheduler.Instance.AllSlots)
        {
            if (s.routeNumber != opt.routeNumber) continue;
            if (s.isOutbound != opt.outbound) continue;
            if ((s.variantLetter ?? "") != wantVariant) continue;
            if (s.state == SlotState.Completed) continue;
            if (!Mathf.Approximately(s.scheduledDeparture, opt.departureAbsMin)) continue;
            return s;
        }
        return null;
    }

    /// <summary>[ADD] Shift Maker time-window check -- handles a window that
    /// wraps past midnight (e.g. 22:00-01:00) the same as one that doesn't.</summary>
    private static bool WithinWindow(float timeOfDay, float start, float end)
    {
        start = ((start % 1440f) + 1440f) % 1440f;
        end = ((end % 1440f) + 1440f) % 1440f;
        return start <= end ? (timeOfDay >= start && timeOfDay <= end) : (timeOfDay >= start || timeOfDay <= end);
    }

    private static bool RouteCoversNow(BusRouteData route, float nowAbsolute)
    {
        float rel = nowAbsolute % 1440f;
        float opStart = route.operatingStartMinutes;
        float opEnd = route.operatingEndMinutes;
        if (opEnd <= opStart) opEnd += 1440f;
        float relAdj = rel < opStart ? rel + 1440f : rel;
        return relAdj >= opStart && relAdj <= opEnd;
    }


    /// <summary>"1 lap" / "2 laps", plus interlined follow-on routes with
    /// their own per-route lap counts, e.g. "1 lap  ·  Interlined: 87 ×2 laps".</summary>
    private static string LapPlanText(BusRouteData route, int laps)
    {
        string s = laps == 1 ? "1 lap" : $"{laps} laps";
        var sched = BusScheduler.Instance;
        if (sched == null || route == null) return s;
        var parts = new System.Collections.Generic.List<string>();
        foreach (var rn in sched.GetInterlinedRoutes(route.routeNumber))
        {
            var r = sched.GetRouteData(rn);
            if (r == null) continue;
            int l = BusScheduler.LapsForRoute(r);
            parts.Add($"{rn} ×{l} lap{(l == 1 ? "" : "s")}");
        }
        return parts.Count > 0 ? s + "  ·  Interlined: " + string.Join(", ", parts) : s;
    }

    private int EstimateLapsForBlock(BusRouteData route, BusScheduler.RouteBusEntry entry)
    {
        // [FIX] Was GetEffectiveLapThresholdPublic(entry.busID) (see
        // ShiftBoardMenu's identical method for the full explanation) — route
        // is already known here, so use the route-aware overload directly.
        int threshold = BusScheduler.Instance != null
            ? BusScheduler.Instance.GetEffectiveLapThresholdForRoute(entry.busID, route.routeNumber)
            : BusScheduler.LapsForRoute(route);
        if (threshold <= 0) threshold = BusScheduler.LapsForRoute(route); // retirement disabled — display-only figure
        if (entry.busID <= 0) return threshold;
        // Service-minute based: what this bus has actually driven so far,
        // not a leg-count guess.
        return BusScheduler.RemainingLapsForRoute(route, entry.serviceMinutesBefore, entry.tripMinutes);
    }

    private void RefreshDepotBuses(RouteOption forRoute)
    {
        _depotBuses.Clear();
        if (BusManager.Instance == null || forRoute == null) return;

        foreach (var record in BusManager.Instance.GetAllRecords())
        {
            if (record == null || record.controller == null || !record.isIdle) continue;
            int fleetNumber = record.controller.fleetNumber;
            bool depotOk = DepotManager.Instance == null || DepotManager.Instance.CanServeRoute(fleetNumber, forRoute.routeNumber);
            if (!depotOk) continue;

            var meta = FleetMetadata.Get(fleetNumber);
            _depotBuses.Add(new DepotBusOption
            {
                fleetNumber = fleetNumber,
                busType = meta != null ? meta.busType : "Unknown",
                seriesName = meta != null ? meta.seriesName : null,
                depotName = meta != null && meta.homeDepot != null ? meta.homeDepot.depotName : "Unknown depot",
                isArticulated = meta != null && meta.isArticulated,
            });
        }
        _depotBuses.Sort((a, b) => a.fleetNumber.CompareTo(b.fleetNumber));
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _lblTitle   = MDT_UITheme.MakeLabel(28, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblSub     = MDT_UITheme.MakeLabel(13, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextAmber);
        _lblDim     = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);
        _lblBody    = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.UpperLeft,  MDT_UITheme.TextPrimary);
        _lblBig     = MDT_UITheme.MakeLabel(22, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _lblError   = MDT_UITheme.MakeLabel(12, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextRed);
        _lblCyan    = MDT_UITheme.MakeLabel(15, FontStyle.Bold,   TextAnchor.UpperLeft,  MDT_UITheme.TextCyan);
        _lblAmber   = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.UpperLeft,  MDT_UITheme.TextAmber);
        _btnPrimary = MDT_UITheme.MakeButton(new Color(0.05f, 0.30f, 0.14f, 1f), MDT_UITheme.TextGreen, 16, FontStyle.Bold);
        _btnSecond  = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 12);
        _textField  = new GUIStyle(GUI.skin.textField) { fontSize = 14, alignment = TextAnchor.MiddleLeft };
    }
}