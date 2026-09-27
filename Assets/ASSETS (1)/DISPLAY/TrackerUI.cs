using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  MDT_UI_Controller  —  v4  (Unified MDT_UITheme, QoL Pass)
//
//  CHANGES vs v3
//  ─────────────
//  · Uses MDT_UITheme for all colors, textures, and draw helpers — no more
//    per-class MakeTex() duplication.
//  · Panel chrome matches LiveMap exactly (outer accent ring, bevel hi/shadow,
//    inner sub-highlight, drop shadow offset).
//  · Header: route number badge now tinted with actual route color; clock
//    displayed with a teal LED that pulses on each second tick.
//  · Route list: pills now colored with each route's own routeColor; selected
//    pill shows a left-edge color bar.
//  · Stop list: stop names shown alongside stop codes (code dimmed); stops that
//    exist only in a variant are labeled with a [V] badge.
//  · Arrival board: fleet number shown in route color; time badge colored
//    by urgency (green < 2 min, amber < 10 min, white otherwise).
//  · Keyboard shortcuts displayed in footer bar.
//  · Empty states: descriptive placeholder instead of bare "No arrivals".
//  · Direction toggle is a segmented control, not two separate buttons.
//  · Scrollview clip rect inset 2px so scroll content doesn't bleed into
//    the bevel border.
//  · Texture cache moved to MDT_UITheme (shared, avoids duplicates across panels).
// ═══════════════════════════════════════════════════════════════════════════════
public class MDT_UI_Controller : MonoBehaviour
{
    [HideInInspector] public bool dockedMode = false;

    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Input")]
    public KeyCode toggleKey => KeyBindings.Current.trackerUI;

    [Header("Layout")]
    public float panelX      = 40f;
    public float panelY      = 40f;
    public float panelWidth  = 320f;
    public float panelHeight = 560f;

    [Header("Refresh")]
    public float refreshInterval = 1f;

    // ── State ─────────────────────────────────────────────────────────────────
    private enum Screen { Routes, Stops, Board, StopBoard }
    private Screen _screen = Screen.Routes;

    private bool         _open         = false;
    private BusRouteData _selectedRoute;
    private bool         _isOutbound   = true;
    private string       _selectedStop = "";
    private string       _selectedVariantLetter = ""; // 💡 ADD THIS LINE
    private List<string> _arrivals     = new List<string>();
    private float        _refreshTimer = 0f;
    private float        _clockPulse   = 0f;

    // ── Stop-board state (reverse lookup: all routes serving one stop) ──────
    // This is what MDT_LiveMap now forwards a stop click into, instead of
    // drawing a floating popup directly over the map — see ShowStopBoard.
    private string _stopBoardCode = "";
    private string _stopBoardName = "";
    private List<BusTrackerService.CompassDirectionArrivals> _stopBoardGroups = new();

    private Vector2 _routeScroll = Vector2.zero;
    private Vector2 _stopScroll  = Vector2.zero;
    private Vector2 _boardScroll = Vector2.zero;

    // ── Styles ────────────────────────────────────────────────────────────────
    private bool     _stylesReady = false;
    private GUIStyle _styleClock;
    private GUIStyle _styleBreadcrumb;
    private GUIStyle _styleSection;
    private GUIStyle _styleBackBtn;
    private GUIStyle _styleDirActive;
    private GUIStyle _styleDirInactive;
    private GUIStyle _styleStopCode;
    private GUIStyle _styleStopName;
    private GUIStyle _styleStopVariantBadge;
    private GUIStyle _styleArrivalTime;
    private GUIStyle _styleArrivalBus;
    private GUIStyle _styleFooter;
    private GUIStyle _styleEmptyState;
    private GUIStyle _styleRouteName;
    private GUIStyle _styleRouteNameSel;

    // ── Constants ─────────────────────────────────────────────────────────────
    private const int HEADER_H = 52;
    private const int FOOTER_H = 22;

    // ═════════════════════════════════════════════════════════════════════════
    //  STOP BOARD — entry point for MDT_LiveMap's stop-click forwarding
    // ═════════════════════════════════════════════════════════════════════════
    /// <summary>Called by MDT_LiveMap when a stop is clicked on the map.
    /// Arrivals for that stop (all routes, grouped by real compass direction)
    /// now render HERE, in the docked tracker panel, instead of as a floating
    /// popup drawn directly over the map canvas.</summary>
    public void ShowStopBoard(string stopCode, string stopName)
    {
        _stopBoardCode = stopCode;
        _stopBoardName = stopName;
        _screen        = Screen.StopBoard;
        _open          = true;
        RefreshStopBoard();
    }

    private void RefreshStopBoard()
    {
        _stopBoardGroups.Clear();

        if (BusTrackerService.Instance == null)
        {
            Debug.LogWarning("[MDT_UI_Controller] ShowStopBoard called but BusTrackerService.Instance is null — " +
                              "is a BusTrackerService active in the scene?");
            return;
        }
        if (string.IsNullOrEmpty(_stopBoardCode)) return;

        _stopBoardGroups = BusTrackerService.Instance.GetArrivalsForStopByCompass(_stopBoardCode);

        int totalArrivals = 0;
        foreach (var g in _stopBoardGroups) totalArrivals += g.arrivals.Count;
        Debug.Log($"[MDT_UI_Controller] Stop board for '{_stopBoardCode}' ({_stopBoardName}): " +
                  $"{_stopBoardGroups.Count} direction group(s), {totalArrivals} total arrival(s).");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═════════════════════════════════════════════════════════════════════════
    public bool IsOpen => _open;

    /// <summary>Open/close from an on-screen button (the pause strip's TRACKER).</summary>
    public void HandleTogglePressed() { if (!dockedMode) _open = !_open; }

    private void Update()
    {
        if (dockedMode) return;

        if (Input.GetKeyDown(toggleKey)) _open = !_open;

        if (!_open) return;

        _clockPulse += Time.deltaTime;
        if (_clockPulse >= 1f) _clockPulse = 0f;

        _refreshTimer -= Time.deltaTime;
        if (_refreshTimer <= 0f)
        {
            _refreshTimer = refreshInterval;
            if (_screen == Screen.Board) RefreshArrivals();
            else if (_screen == Screen.StopBoard) RefreshStopBoard();
        }
    }

    public void TickDocked()
    {
        _clockPulse += Time.deltaTime;
        if (_clockPulse >= 1f) _clockPulse = 0f;

        _refreshTimer -= Time.deltaTime;
        if (_refreshTimer <= 0f)
        {
            _refreshTimer = refreshInterval;
            if (_screen == Screen.Board) RefreshArrivals();
            else if (_screen == Screen.StopBoard) RefreshStopBoard();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  OnGUI
    // ═════════════════════════════════════════════════════════════════════════
    private void OnGUI()
    {
        if (dockedMode) return;
        if (!_open) return;
        if (!_stylesReady) BuildStyles();

        var panelRect = new Rect(panelX, panelY, panelWidth, panelHeight);

        MDT_UITheme.DrawPanel(panelRect);

        GUILayout.BeginArea(panelRect);
        DrawHeader();
        MDT_UITheme.DrawDivider(0, HEADER_H, panelWidth);

        float contentH = panelHeight - HEADER_H - FOOTER_H - 1;
        GUILayout.BeginArea(new Rect(0, HEADER_H + 1, panelWidth, contentH));

        switch (_screen)
        {
            case Screen.Routes:    DrawRouteScreen();    break;
            case Screen.Stops:     DrawStopScreen();     break;
            case Screen.Board:     DrawBoardScreen();    break;
            case Screen.StopBoard: DrawStopBoardScreen(); break;
        }

        GUILayout.EndArea();

        DrawFooter();
        GUILayout.EndArea();
    }

    public void DrawDocked(Rect area)
    {
        if (!_stylesReady) BuildStyles();
        float dockedW = area.width;
        float dockedH = area.height;

        float savedPanelWidth = panelWidth;
        panelWidth = dockedW;

        GUI.BeginGroup(area);

        DrawHeader();
        MDT_UITheme.DrawDivider(0, HEADER_H, dockedW);

        float contentH = dockedH - HEADER_H - FOOTER_H - 1;
        GUILayout.BeginArea(new Rect(0, HEADER_H + 1, dockedW, contentH));
        switch (_screen)
        {
            case Screen.Routes:    DrawRouteScreen();    break;
            case Screen.Stops:     DrawStopScreen();     break;
            case Screen.Board:     DrawBoardScreen();    break;
            case Screen.StopBoard: DrawStopBoardScreen(); break;
        }
        GUILayout.EndArea();

        DrawFooter();
        GUI.EndGroup();

        panelWidth = savedPanelWidth;
    }

    // ── Header ────────────────────────────────────────────────────────────────
    private void DrawHeader()
    {
        MDT_UITheme.DrawHeader(new Rect(0, 0, panelWidth, HEADER_H));

        // Clock
        string clock = BusScheduler.Instance != null
            ? BusScheduler.Instance.GameTimeString : "--:--";
        GUI.Label(new Rect(12, 6, 180, 22), $"MDT TRACKER  {clock}", _styleClock);

        // Clock LED (pulses once per second)
        bool ledOn = _clockPulse < 0.5f;
        MDT_UITheme.DrawLED(new Vector2(panelWidth - 14, 14),
            4f, ledOn ? MDT_UITheme.TextCyan : MDT_UITheme.LEDOff);

        // Breadcrumb
        string crumb;
        if (_screen == Screen.Routes)
            crumb = "SELECT ROUTE";
        else if (_screen == Screen.Stops)
            crumb = $"ROUTE  {_selectedRoute?.routeNumber ?? "?"}  ›  SELECT STOP";
        else if (_screen == Screen.StopBoard)
            crumb = $"STOP  {_stopBoardCode}  ›  ALL ROUTES";
        else
            crumb = $"ROUTE  {_selectedRoute?.routeNumber ?? "?"}  ›  {_selectedStop}  ›  " +
                    (_isOutbound ? "A → Z" : "Z → A");

        GUI.Label(new Rect(12, 28, panelWidth - 90, 18), crumb, _styleBreadcrumb);

        // Back button
        if (_screen != Screen.Routes)
        {
            if (GUI.Button(new Rect(panelWidth - 78, HEADER_H - 28, 68, 22), "◀  BACK", _styleBackBtn))
                GoBack();
        }
    }

    // ── Footer ────────────────────────────────────────────────────────────────
    private void DrawFooter()
    {
        float fy = panelHeight - FOOTER_H;
        MDT_UITheme.DrawRect(new Rect(0, fy, panelWidth, FOOTER_H), MDT_UITheme.BGFooter);
        MDT_UITheme.DrawDivider(0, fy, panelWidth);

        string hint = _screen == Screen.Routes
            ? $"[{toggleKey}] close"
            : _screen == Screen.Stops
                ? $"[{toggleKey}] close  ·  [◀ BACK] return"
                : _screen == Screen.StopBoard
                    ? $"[{toggleKey}] close  ·  auto-refresh {refreshInterval:F0}s  ·  click another stop to switch"
                    : $"[{toggleKey}] close  ·  auto-refresh {refreshInterval:F0}s";

        GUI.Label(new Rect(8, fy + 4, panelWidth - 16, 16), hint, _styleFooter);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ROUTE SCREEN
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawRouteScreen()
    {
        GUILayout.Space(8);
        DrawSectionLabel("ROUTES");
        GUILayout.Space(4);

        if (BusScheduler.Instance == null)
        {
            DrawEmptyState("BusScheduler not found.");
            return;
        }

        _routeScroll = GUILayout.BeginScrollView(_routeScroll,
            GUILayout.Width(panelWidth), GUILayout.ExpandHeight(true));

        foreach (var route in BusScheduler.Instance.managedRoutes)
        {
            if (route == null) continue;
            bool selected = (_selectedRoute == route);

            var rowRect = GUILayoutUtility.GetRect(panelWidth - 16, 42);
            Color bg = selected
                ? Color.Lerp(route.routeColor, Color.black, 0.60f)
                : MDT_UITheme.BGPill;
            bg.a = 1f;
            MDT_UITheme.DrawRoundedRect(rowRect, MDT_UITheme.RadiusRow, bg);

            // Left color bar (route color strip, always visible)
            MDT_UITheme.DrawRect(new Rect(rowRect.x, rowRect.y, 4, rowRect.height), route.routeColor);

            // Route number badge
            var badgeRect = new Rect(rowRect.x + 10, rowRect.y + 8, 36, 22);
            Color badgeBg = Color.Lerp(route.routeColor, Color.black, 0.40f);
            badgeBg.a = 1f;
            MDT_UITheme.DrawRect(badgeRect, badgeBg);
            GUI.Label(badgeRect,
                route.routeNumber,
                MDT_UITheme.MakeLabel(11, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white));

            // Route display name
            GUIStyle nameStyle = selected ? _styleRouteNameSel : _styleRouteName;
            GUI.Label(new Rect(rowRect.x + 54, rowRect.y, rowRect.width - 64, rowRect.height),
                      route.name ?? route.routeNumber, nameStyle);

            // Tap anywhere on row
            if (GUI.Button(new Rect(rowRect.x, rowRect.y, rowRect.width, rowRect.height),
                           GUIContent.none, GUIStyle.none))
            {
                _selectedRoute = route;
                _selectedStop  = "";
                _selectedVariantLetter = ""; // 💡 ADD THIS LINE
                _isOutbound    = true;
                _screen        = Screen.Stops;
            }

            GUILayout.Space(3);
        }

        GUILayout.EndScrollView();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STOP SCREEN
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawStopScreen()
    {
        GUILayout.Space(6);

        // ── Direction segmented control ────────────────────────────────────
        GUILayout.BeginHorizontal();
        GUILayout.Space(8);

        float segW = (panelWidth - 24) * 0.5f;

        // A→Z segment
        DrawSegmentButton("↗  A → Z (Outbound)", _isOutbound, segW, () =>
        { _isOutbound = true; _selectedStop = ""; });

        GUILayout.Space(2);

        // Z→A segment
        DrawSegmentButton("↙  Z → A (Inbound)", !_isOutbound, segW, () =>
        { _isOutbound = false; _selectedStop = ""; });

        GUILayout.Space(8);
        GUILayout.EndHorizontal();
        GUILayout.Space(6);

        MDT_UITheme.DrawRect(
            GUILayoutUtility.GetRect(panelWidth, 1), MDT_UITheme.Divider);

        GUILayout.Space(4);
        DrawSectionLabel("SELECT STOP");
        GUILayout.Space(2);

        if (_selectedRoute == null) { DrawEmptyState("No route selected."); return; }

        _stopScroll = GUILayout.BeginScrollView(_stopScroll,
            GUILayout.Width(panelWidth), GUILayout.ExpandHeight(true));

        // Build merged stop list (main + variant exclusives)
// Build merged stop list (main + variant exclusives)
        var mainStops = _selectedRoute.GetStopCodes(_isOutbound, null);
        
        // 💡 FIX: Pull from state variable instead of activeSlot
        var variant = _selectedRoute.GetVariant(_selectedVariantLetter); 
        var variantStops = (variant != null)
            ? _selectedRoute.GetStopCodes(_isOutbound, variant)
            : new List<string>();
        var displayList = new List<string>(mainStops);
        var isVariantOnly = new HashSet<string>();
        foreach (string s in variantStops)
        {
            if (!displayList.Contains(s))
            {
                displayList.Add(s);
                isVariantOnly.Add(s);
            }
        }

        // Resolve stop names from CityManager if available
        // [PERF FIX] Was FindObjectOfType<CityManager>() -- CityManager.Instance is free.
        var city = CityManager.Instance;

        for (int i = 0; i < displayList.Count; i++)
        {
            string stopCode  = displayList[i];
            bool   isVariant = isVariantOnly.Contains(stopCode);

            var rowRect = GUILayoutUtility.GetRect(panelWidth - 4, 34);
            MDT_UITheme.DrawRoundedRect(rowRect, MDT_UITheme.RadiusRow,
                i % 2 == 0 ? MDT_UITheme.BGRowEven : MDT_UITheme.BGRowOdd);

            // Index number
            GUI.Label(new Rect(rowRect.x + 4, rowRect.y, 20, rowRect.height),
                      (i + 1).ToString("D2"),
                      MDT_UITheme.MakeLabel(8, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim));

            // Stop code
            GUI.Label(new Rect(rowRect.x + 26, rowRect.y, 52, rowRect.height),
                      stopCode, _styleStopCode);

            // Stop name (from city data if available)
            string stopName = ResolveStopName(city, stopCode);
            if (!string.IsNullOrEmpty(stopName))
                GUI.Label(new Rect(rowRect.x + 82, rowRect.y, rowRect.width - 118, rowRect.height),
                          stopName, _styleStopName);

            // Variant badge
            if (isVariant)
                GUI.Label(new Rect(rowRect.xMax - 34, rowRect.y, 30, rowRect.height),
                          "[V]", _styleStopVariantBadge);

            // Hit area
            if (GUI.Button(new Rect(rowRect.x, rowRect.y, rowRect.width, rowRect.height),
                           GUIContent.none, GUIStyle.none))
            {
                _selectedStop = stopCode;
                _refreshTimer = 0f;
                _screen       = Screen.Board;
                RefreshArrivals();
            }

            GUILayout.Space(1);
        }

        GUILayout.EndScrollView();
    }

    // ── Segmented control helper ──────────────────────────────────────────────
    private void DrawSegmentButton(string label, bool active, float width, System.Action onClick)
    {
        var style = active ? _styleDirActive : _styleDirInactive;
        if (GUILayout.Button(label, style, GUILayout.Height(30), GUILayout.Width(width)))
            onClick?.Invoke();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ARRIVAL BOARD
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawBoardScreen()
    {
        GUILayout.Space(8);

        // Stop name header
        if (!string.IsNullOrEmpty(_selectedStop))
        {
            // [PERF FIX] Was FindObjectOfType<CityManager>() -- a full scene
            // scan -- every single OnGUI frame this board is open. CityManager
            // already exposes a singleton .Instance for free.
            string stopDisplayName = ResolveStopName(CityManager.Instance, _selectedStop);
            string header = string.IsNullOrEmpty(stopDisplayName)
                ? _selectedStop
                : $"{_selectedStop}  ·  {stopDisplayName}";
            GUI.Label(new Rect(8, 4, panelWidth - 16, 20),
                      header,
                      MDT_UITheme.MakeLabel(11, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextCyan));
        }

        GUILayout.Space(24);
        DrawSectionLabel("NEXT ARRIVALS");
        MDT_UITheme.DrawRect(
            GUILayoutUtility.GetRect(panelWidth, 1), MDT_UITheme.Divider);
        GUILayout.Space(6);

        _boardScroll = GUILayout.BeginScrollView(_boardScroll,
            GUILayout.Width(panelWidth), GUILayout.ExpandHeight(true));

        if (_arrivals.Count == 0)
        {
            DrawEmptyState("No arrivals in the lookahead window.\nCheck service times or select another stop.");
        }
        else
        {
            for (int i = 0; i < _arrivals.Count; i++)
            {
                string line = _arrivals[i];
                DrawArrivalRow(i, line);
                GUILayout.Space(2);
            }
        }

        GUILayout.EndScrollView();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STOP BOARD SCREEN — all routes serving one stop, grouped by real
    //  compass direction (NORTH/SOUTH/EAST/WEST). This is what MDT_LiveMap
    //  now forwards a stop click into, instead of drawing its own popup.
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawStopBoardScreen()
    {
        GUILayout.Space(8);

        string header = string.IsNullOrEmpty(_stopBoardName) ? _stopBoardCode : $"{_stopBoardCode}  ·  {_stopBoardName}";
        GUI.Label(new Rect(8, 4, panelWidth - 16, 20), header,
                  MDT_UITheme.MakeLabel(11, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextCyan));

        GUILayout.Space(24);

        // Compact summary line — "NORTH 3   SOUTH 3" — at a glance before
        // scrolling into the detail rows.
        if (_stopBoardGroups.Count > 0)
        {
            string summary = string.Join("   ", _stopBoardGroups.ConvertAll(g => $"{g.label} {g.arrivals.Count}"));
            var sumRect = GUILayoutUtility.GetRect(panelWidth, 16);
            GUI.Label(sumRect, summary, MDT_UITheme.MakeLabel(9, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextGreen));
            GUILayout.Space(4);
        }

        DrawSectionLabel("ALL ROUTES — BY DIRECTION");
        MDT_UITheme.DrawRect(GUILayoutUtility.GetRect(panelWidth, 1), MDT_UITheme.Divider);
        GUILayout.Space(6);

        _boardScroll = GUILayout.BeginScrollView(_boardScroll,
            GUILayout.Width(panelWidth), GUILayout.ExpandHeight(true));

        if (_stopBoardGroups.Count == 0)
        {
            DrawEmptyState("No scheduled service found for this stop in the lookahead window.\n" +
                           "If a bus IS visibly serving this stop, check the console for " +
                           "[BusTrackerService] warnings — that's where a stopCode mismatch or " +
                           "missing route registration will show up.");
        }
        else
        {
            foreach (var group in _stopBoardGroups)
            {
                DrawSectionLabel($"{group.label}  ({group.arrivals.Count})");
                for (int i = 0; i < group.arrivals.Count; i++)
                {
                    DrawStopArrivalRow(i, group.arrivals[i]);
                    GUILayout.Space(2);
                }
                GUILayout.Space(8);
            }
        }

        GUILayout.EndScrollView();
    }

    private void DrawStopArrivalRow(int idx, BusTrackerService.StopArrivalEntry a)
    {
        var rowRect = GUILayoutUtility.GetRect(panelWidth - 4, 40);
        MDT_UITheme.DrawRoundedRect(rowRect, MDT_UITheme.RadiusRow,
            idx % 2 == 0 ? MDT_UITheme.BGRowEven : MDT_UITheme.BGRowOdd);

        MDT_UITheme.DrawRect(new Rect(rowRect.x, rowRect.y, 3, rowRect.height), a.routeColor);

        string vSuffix = string.IsNullOrEmpty(a.variantLetter) ? "" : $"[{a.variantLetter}]";
        GUI.Label(new Rect(rowRect.x + 8, rowRect.y, 74, rowRect.height),
            $"RT {a.routeNumber}{vSuffix}",
            MDT_UITheme.MakeLabel(10, FontStyle.Bold, TextAnchor.MiddleLeft, a.routeColor));

        GUI.Label(new Rect(rowRect.x + 84, rowRect.y, rowRect.width - 140, rowRect.height),
            $"#{a.busLabel}", _styleArrivalBus);

        GUI.Label(new Rect(rowRect.xMax - 56, rowRect.y, 56, rowRect.height),
            a.minutesLabel, _styleArrivalTime);
    }
    //
    //  ┌────────────────────────────────────────┐
    //  │  ①  │  2 min  │  Bus #1801  [A]        │
    //  └────────────────────────────────────────┘
    //
    private void DrawArrivalRow(int idx, string line)
    {
        var rowRect = GUILayoutUtility.GetRect(panelWidth - 4, 46);
        MDT_UITheme.DrawRoundedRect(rowRect, MDT_UITheme.RadiusRow,
            idx % 2 == 0 ? MDT_UITheme.BGRowEven : MDT_UITheme.BGRowOdd);

        // Left edge accent line based on urgency
        Color urgencyColor = GetUrgencyColor(line);
        MDT_UITheme.DrawRect(new Rect(rowRect.x, rowRect.y, 3, rowRect.height), urgencyColor);

        // Index badge
        var badgeRect = new Rect(rowRect.x + 8, rowRect.y + 11, 24, 24);
        MDT_UITheme.DrawRect(badgeRect, urgencyColor * new Color(1,1,1,0.20f));
        GUI.Label(badgeRect, (idx + 1).ToString(),
                  MDT_UITheme.MakeLabel(10, FontStyle.Bold, TextAnchor.MiddleCenter, urgencyColor));

        // Parse line: "{time} — {busLabel}"
        int sep = line.IndexOf(" — ");
        string timePart = sep > 0 ? line.Substring(0, sep)   : line;
        string busPart  = sep > 0 ? line.Substring(sep + 3)  : "";

        // Time badge
        float timeBadgeW = 68f;
        var timeBadgeRect = new Rect(rowRect.x + 38, rowRect.y + 8, timeBadgeW, 28);
        MDT_UITheme.DrawRect(timeBadgeRect, urgencyColor * new Color(1,1,1,0.12f));
        MDT_UITheme.DrawRect(new Rect(timeBadgeRect.x, timeBadgeRect.y, timeBadgeRect.width, 1),
                             urgencyColor * new Color(1,1,1,0.50f));
        GUI.Label(timeBadgeRect, timePart, _styleArrivalTime);

        // Bus label
        GUI.Label(new Rect(rowRect.x + 114, rowRect.y, rowRect.width - 120, rowRect.height),
                  busPart, _styleArrivalBus);
    }

    // ── Urgency color ─────────────────────────────────────────────────────────
    private Color GetUrgencyColor(string line)
    {
        if (line.StartsWith("Arriving") || line.StartsWith("<1")) return MDT_UITheme.TextGreen;
        if (line.Contains("TBD"))                                  return MDT_UITheme.TextDim;

        int sp = line.IndexOf(' ');
        if (sp > 0 && int.TryParse(line.Substring(0, sp), out int mins))
        {
            if (mins <=  2) return MDT_UITheme.TextGreen;
            if (mins <= 10) return MDT_UITheme.TextAmber;
        }
        return MDT_UITheme.TextSecond;
    }

    // ── Section label ─────────────────────────────────────────────────────────
    private void DrawSectionLabel(string text)
    {
        var r = GUILayoutUtility.GetRect(panelWidth, 18);
        // Left accent tick
        MDT_UITheme.DrawRect(new Rect(r.x + 8, r.y + 4, 2, 10), MDT_UITheme.TextCyan);
        GUI.Label(new Rect(r.x + 14, r.y, r.width - 14, r.height), text, _styleSection);
    }

    // ── Empty state ───────────────────────────────────────────────────────────
    private void DrawEmptyState(string message)
    {
        GUILayout.Space(30);
        GUILayout.BeginHorizontal();
        GUILayout.Space(16);
        GUILayout.Label(message, _styleEmptyState, GUILayout.ExpandWidth(true));
        GUILayout.Space(16);
        GUILayout.EndHorizontal();
    }

    // ── Stop name resolver ────────────────────────────────────────────────────
    private readonly Dictionary<string, string> _stopNameCache = new Dictionary<string, string>();

    private string ResolveStopName(CityManager city, string stopCode)
    {
        if (string.IsNullOrEmpty(stopCode)) return "";
        if (_stopNameCache.TryGetValue(stopCode, out var cached)) return cached;

        string name = "";
        if (city != null && city.AllStops != null)
        {
            foreach (var s in city.AllStops)
            {
                if (s != null && s.stopCode == stopCode)
                {
                    name = s.stopName;
                    break;
                }
            }
        }
        _stopNameCache[stopCode] = name;
        return name;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ARRIVALS
    // ═════════════════════════════════════════════════════════════════════════
    private void RefreshArrivals()
    {
        _arrivals.Clear();
        if (_selectedRoute == null || string.IsNullOrEmpty(_selectedStop)) return;
        _arrivals = BusTrackerService.Instance.GetNextArrivals(
            _selectedRoute, _isOutbound, _selectedStop);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  NAVIGATION
    // ═════════════════════════════════════════════════════════════════════════
    private void GoBack()
    {
        _stopNameCache.Clear(); // Refresh on re-entry
        switch (_screen)
        {
            case Screen.Stops:     _screen = Screen.Routes; break;
            case Screen.Board:     _screen = Screen.Stops;  break;
            case Screen.StopBoard: _screen = Screen.Routes; break;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STYLE BUILDER
    // ═════════════════════════════════════════════════════════════════════════
    private void BuildStyles()
    {
        _styleClock = MDT_UITheme.MakeLabel(13, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);

        _styleBreadcrumb = MDT_UITheme.MakeLabel(9, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);

        _styleSection = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 9,
            fontStyle = FontStyle.Bold,
            padding   = new RectOffset(0, 0, 0, 0),
        };
        _styleSection.normal.textColor = MDT_UITheme.TextDim;

        _styleBackBtn = new GUIStyle(GUI.skin.button)
        {
            fontSize  = 9,
            fontStyle = FontStyle.Bold,
            border    = new RectOffset(2, 2, 2, 2),
            alignment = TextAnchor.MiddleCenter,
        };
        _styleBackBtn.normal.textColor = MDT_UITheme.TextSecond;
        _styleBackBtn.hover.textColor  = MDT_UITheme.TextWhite;
        MDT_UITheme.SetBg(_styleBackBtn, MDT_UITheme.BGButton,
                          MDT_UITheme.BGButton * 1.3f, MDT_UITheme.BGButton * 0.7f);

        // Direction segment buttons
        _styleDirActive = new GUIStyle(GUI.skin.button)
        {
            fontSize  = 10,
            fontStyle = FontStyle.Bold,
            border    = new RectOffset(2, 2, 2, 2),
            alignment = TextAnchor.MiddleCenter,
        };
        _styleDirActive.normal.textColor = MDT_UITheme.TextWhite;
        MDT_UITheme.SetBg(_styleDirActive, MDT_UITheme.BGDirSel,
                          MDT_UITheme.BGDirSel * 1.2f, MDT_UITheme.BGDirSel * 0.8f);

        _styleDirInactive = new GUIStyle(_styleDirActive);
        _styleDirInactive.normal.textColor = MDT_UITheme.TextDim;
        MDT_UITheme.SetBg(_styleDirInactive, MDT_UITheme.BGPill,
                          MDT_UITheme.BGPill * 1.3f, MDT_UITheme.BGPill * 0.7f);

        // Stop list
        _styleStopCode = MDT_UITheme.MakeLabel(10, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);

        _styleStopName = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextSecond);

        _styleStopVariantBadge = MDT_UITheme.MakeLabel(8, FontStyle.Bold, TextAnchor.MiddleRight, MDT_UITheme.TextAmber);

        // Route list
        _styleRouteName = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _styleRouteNameSel = MDT_UITheme.MakeLabel(11, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);

        // Arrival board
        _styleArrivalTime = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };
        _styleArrivalTime.normal.textColor = MDT_UITheme.TextWhite;

        _styleArrivalBus = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextSecond);

        _styleFooter = MDT_UITheme.MakeLabel(8, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);

        _styleEmptyState = new GUIStyle(GUI.skin.label)
        {
            fontSize   = 10,
            fontStyle  = FontStyle.Normal,
            alignment  = TextAnchor.UpperLeft,
            wordWrap   = true,
        };
        _styleEmptyState.normal.textColor = MDT_UITheme.TextDim;

        _stylesReady = true;
    }

    private void OnDestroy()
    {
        // MDT_UITheme owns the texture cache — nothing to clean up here.
    }
}