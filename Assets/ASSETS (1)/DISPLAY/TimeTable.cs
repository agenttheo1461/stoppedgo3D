using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  TimetableOverlay  —  v3.2
//
//  CHANGES vs v3.1
//  ─────────────
//  · FIX: FindPlayerStopIndex depended on PlayerShiftDirector /
//    BusSchedulerPlayerService — classes from a from-scratch shift-system
//    rebuild that was reverted. Rewired onto the real, current owner of
//    shift state (PlayerHandoff) + BusScheduler for the actual TimetableSlot
//    (same lookup pattern used elsewhere in the project:
//    BusScheduler.TryGetAssignedSlot(PlayerBusID, ...)).
//
//  CHANGES vs v2
//  ─────────────
//  · Variant-aware: [V] cycles Mainline → Variant A → Variant B → ... → back.
//    Only slots matching the selected variant populate the grid, and the stop
//    column resolves via route.GetStops(outbound, variant) so branch-specific
//    stop lists display correctly instead of always showing mainline stops.
//  · Delay-aware: every column header shows live lateness.
//      - InService slots show actual latenessMinutes (locked at departure).
//      - AssignedNPC/AssignedPlayer slots still at the terminal past their
//        scheduled time show a live "waiting +Nm" computed against game clock.
//    Color-coded green (early/on-time) → amber (minor) → red (major).
//  · Corner box and footer both reflect the active variant.
// ═══════════════════════════════════════════════════════════════════════════════
public class TimetableOverlay : MonoBehaviour
{
    public static TimetableOverlay Instance { get; private set; }
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    [Header("Toggle")]
    public KeyCode toggleKey => KeyBindings.Current.timetable;

    [Header("Layout")]
    public float stopColWidth = 200f;
    public float depColWidth  = 88f;
    public float rowHeight    = 28f;
    public float headerHeight = 58f; // +6 vs v2 to fit the delay line
    public float marginTop  = 16f;
    public float marginSide = 16f;

    [Header("Font Sizes")]
    public int fontSizeClock  = 13;
    public int fontSizeTitle  = 11;
    public int fontSizeColDep = 12;
    public int fontSizeColID  = 9;
    public int fontSizeCell   = 11;
    public int fontSizeStop   = 10;
    public int fontSizeDelay  = 8;
    private bool   _futureOnly          = false;
private bool   _compactMode         = false;
private int    _hoveredRowIndex     = -1;
private string _fleetSearch         = "";
private int    _fleetSearchMatchCol = -1;

private float  _normalStopColWidth, _normalDepColWidth, _normalRowHeight, _normalHeaderHeight;
private bool   _compactCached = false;

    // ── Runtime state ──────────────────────────────────────────────────────────
    private bool  _visible    = false;
    public  bool  IsVisible => _visible;

    /// <summary>[MOBILE] Same body the keyboard toggleKey handler used to
    /// run inline — lets a touch "TIME" button drive the same show/hide.</summary>
    public void HandleTogglePressed()
    {
        _visible   = !_visible;
        _dataDirty = true;
        ResetScroll();
    }
    private int   _routeIndex = 0;
    private bool  _outbound   = true;

    /// <summary>"" = mainline. Cycled with [V].</summary>
    private string _variantLetter = "";

    private float _scrollX, _scrollY, _maxScrollX, _maxScrollY;
    private const float ScrollSpeed = 40f;
    private bool  _dataDirty = true;

    private BusRouteData        _route;
    private RouteVariantData    _variant;
    private List<TimetableSlot> _allSlots = new List<TimetableSlot>(64);
    private List<BusStopData>   _stops    = new List<BusStopData>(32);

    private bool _stylesBuilt = false;

    private const int   TOPBAR_H  = 42;
    private const int   INFO_STRIP_H = 46;
    private const int   FOOTER_H  = 22;
    private const float SCROLLBAR_W = 14f;
    private const float SCROLLBAR_H = 14f;

private void Update()
{
    if (Input.GetKeyDown(toggleKey))
        HandleTogglePressed();

    if (!_visible) return;

    var routes = BusScheduler.Instance?.managedRoutes;
    if (routes == null || routes.Length == 0) return;

    if (Input.GetKeyDown(KeyBindings.Current.timetableNextRoute))
    {
        _routeIndex = (_routeIndex + 1) % routes.Length;
        _variantLetter = "";
        _dataDirty  = true;
        ResetScroll();
    }
    if (Input.GetKeyDown(KeyBindings.Current.timetablePrevRoute))
    {
        _routeIndex = (_routeIndex - 1 + routes.Length) % routes.Length;
        _variantLetter = "";
        _dataDirty  = true;
        ResetScroll();
    }

    if (Input.GetKeyDown(KeyBindings.Current.timetableVariant))
    {
        CycleVariant();
        _dataDirty = true;
        ResetScroll();
    }

    // QoL: jump vertically to the player's current stop row
    if (Input.GetKeyDown(KeyBindings.Current.timetableMyStop))
        JumpToPlayerRow();

    // QoL: hide already-completed departures
    if (Input.GetKeyDown(KeyBindings.Current.timetableFutureOnly))
    {
        _futureOnly = !_futureOnly;
        _dataDirty  = true;
        ResetScroll();
    }

}
// ═════════════════════════════════════════════════════════════════════════
//  QoL — COMPACT MODE (scales existing layout fields; no draw-method changes needed)
// ═════════════════════════════════════════════════════════════════════════
private void ApplyCompactMode()
{
    if (!_compactCached)
    {
        _normalStopColWidth = stopColWidth;
        _normalDepColWidth  = depColWidth;
        _normalRowHeight    = rowHeight;
        _normalHeaderHeight = headerHeight;
        _compactCached = true;
    }

    if (_compactMode)
    {
        stopColWidth = _normalStopColWidth * 0.82f;
        depColWidth  = _normalDepColWidth  * 0.72f;
        rowHeight    = _normalRowHeight    * 0.75f;
        headerHeight = _normalHeaderHeight * 0.85f;
    }
    else
    {
        stopColWidth = _normalStopColWidth;
        depColWidth  = _normalDepColWidth;
        rowHeight    = _normalRowHeight;
        headerHeight = _normalHeaderHeight;
    }
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — JUMP TO NOW / JUMP TO PLAYER ROW
// ═════════════════════════════════════════════════════════════════════════
private void JumpToNow()
{
    if (_allSlots.Count == 0) return;
    float now = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeMinutes : 0f;

    int best = -1;
    float bestDiff = float.MaxValue;
    for (int i = 0; i < _allSlots.Count; i++)
    {
        float diff = Mathf.Abs(_allSlots[i].scheduledDeparture - now);
        if (diff < bestDiff) { bestDiff = diff; best = i; }
    }
    if (best < 0) return;

    _scrollX = Mathf.Clamp(best * depColWidth - 120f, 0f, _maxScrollX);
}

private void JumpToPlayerRow()
{
    int idx = FindPlayerStopIndex();
    if (idx < 0) return;

    _scrollY = Mathf.Clamp(idx * rowHeight - 100f, 0f, _maxScrollY);
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — FLEET SEARCH & FOCUS
// ═════════════════════════════════════════════════════════════════════════
private void DrawFleetSearchBox(float winX, float winY, float winW)
{
    var r = new Rect(winX + winW - 168f, winY + TOPBAR_H + 4f, 156f, 20f);
    MDT_UITheme.DrawRect(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), MDT_UITheme.TextCyan * new Color(1, 1, 1, 0.35f));
    MDT_UITheme.DrawRect(r, MDT_UITheme.BGDeep);

    GUIStyle fieldStyle = MDT_UITheme.MakeLabel(9, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
    string newVal = GUI.TextField(new Rect(r.x + 4, r.y + 2, r.width - 26, r.height - 4), _fleetSearch, fieldStyle);

    if (string.IsNullOrEmpty(_fleetSearch) && Event.current.type == EventType.Repaint)
        GUI.Label(new Rect(r.x + 6, r.y + 2, r.width - 30, r.height - 4), "fleet #…",
            MDT_UITheme.MakeLabel(8, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim));

    if (newVal != _fleetSearch)
    {
        _fleetSearch = newVal;
        ResolveFleetSearchColumn();
    }

    if (GUI.Button(new Rect(r.xMax - 20, r.y + 1, 18, r.height - 2), "→",
        MDT_UITheme.MakeLabel(9, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextCyan)))
    {
        JumpToFleetSearchMatch();
    }
}

private void ResolveFleetSearchColumn()
{
    _fleetSearchMatchCol = -1;
    if (string.IsNullOrEmpty(_fleetSearch) || !int.TryParse(_fleetSearch, out int fn)) return;

    for (int i = 0; i < _allSlots.Count; i++)
    {
        if (ResolveDisplayFleetNumber(_allSlots[i]) == fn)
        {
            _fleetSearchMatchCol = i;
            break;
        }
    }
}

private void JumpToFleetSearchMatch()
{
    if (_fleetSearchMatchCol < 0 || _fleetSearchMatchCol >= _allSlots.Count) return;
    _scrollX = Mathf.Clamp(_fleetSearchMatchCol * depColWidth - 120f, 0f, _maxScrollX);

    var slot = _allSlots[_fleetSearchMatchCol];
    int fn = ResolveDisplayFleetNumber(slot);
    bool isPlayer = BusScheduler.IsPlayer(slot.assignedBusID);
    MDT_FocusBus.RequestFollow(fn, isPlayer); // also focus the live map
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — FOOTER STATS
// ═════════════════════════════════════════════════════════════════════════
private void DrawFooterStats(float winX, float winY, float winW, float winH)
{
    if (_route == null) return;
    float fy = winY + winH - FOOTER_H;

    int tripCount = _allSlots.Count;
    float avgHeadway = -1f;
    if (_allSlots.Count > 1)
    {
        var sorted = new List<TimetableSlot>(_allSlots);
        sorted.Sort((a, b) => a.scheduledDeparture.CompareTo(b.scheduledDeparture));
        float span = sorted[sorted.Count - 1].scheduledDeparture - sorted[0].scheduledDeparture;
        avgHeadway = span / Mathf.Max(1, sorted.Count - 1);
    }

    string text = avgHeadway >= 0f
        ? $"{tripCount} trips  ·  ~{avgHeadway:F0}m headway"
        : $"{tripCount} trips";

    GUI.Label(new Rect(winX + winW * 0.46f, fy + 4, 220f, 14),
        text, MDT_UITheme.MakeLabel(8, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextCyan));
}
    private void CycleVariant()
    {
        var variants = _route?.variants;
        if (variants == null || variants.Count == 0) { _variantLetter = ""; return; }

        int idx = variants.FindIndex(v => v != null && v.variantLetter == _variantLetter);
        idx++; // -1 (mainline) → 0 is first variant; last variant → -1 wraps to mainline
        if (idx >= variants.Count) { _variantLetter = ""; return; }
        _variantLetter = variants[idx].variantLetter;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  OnGUI
    // ═════════════════════════════════════════════════════════════════════════
    private void OnGUI()
    {
        if (!_visible) return;
        if (BusScheduler.Instance == null) return;
        if (!_stylesBuilt) BuildStyles();

        var routes = BusScheduler.Instance.managedRoutes;
        if (routes == null || routes.Length == 0) return;
        _routeIndex = Mathf.Clamp(_routeIndex, 0, routes.Length - 1);
        _route      = routes[_routeIndex];
        if (_route == null) return;

        _variant = string.IsNullOrEmpty(_variantLetter) ? null : _route.GetVariant(_variantLetter);
        // Variant letter pointed at something that no longer exists on this route — reset.
        if (!string.IsNullOrEmpty(_variantLetter) && _variant == null)
        {
            _variantLetter = "";
            _dataDirty = true;
        }

        if (_dataDirty)
        {
            LoadStaticSchedule();
            _dataDirty = false;
        }

        float winX = marginSide;
        float winY = marginTop;
        float winW = Screen.width  - marginSide * 2f;
        float winH = Screen.height - marginTop  - marginSide;
        var panelRect = new Rect(winX, winY, winW, winH);

        var ev = Event.current;
        if (ev.type == EventType.ScrollWheel && panelRect.Contains(ev.mousePosition))
        {
            if (ev.shift) _scrollX = Mathf.Clamp(_scrollX + ev.delta.y * ScrollSpeed, 0f, _maxScrollX);
            else          _scrollY = Mathf.Clamp(_scrollY + ev.delta.y * ScrollSpeed, 0f, _maxScrollY);
            ev.Use();
        }

        MDT_UITheme.DrawPanel(panelRect);
        DrawTopBar(winX, winY, winW, routes);
        DrawInfoStrip(winX, winY + TOPBAR_H, winW);

        float tblY = winY + TOPBAR_H + INFO_STRIP_H;
        float tblW = winW - SCROLLBAR_W - 2f;
        float tblH = winH - TOPBAR_H - INFO_STRIP_H - FOOTER_H - SCROLLBAR_H - 2f;

        DrawGrid(winX, tblY, tblW, tblH);
        DrawScrollbars(winX, winY, winW, winH, tblW, tblH);
        DrawFooter(winX, winY, winW, winH);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  TOP BAR
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawTopBar(float winX, float winY, float winW, BusRouteData[] routes)
    {
        MDT_UITheme.DrawHeader(new Rect(winX, winY, winW, TOPBAR_H));

        float bx = winX + 8f;
        float by = winY + 7f;

        for (int i = 0; i < routes.Length; i++)
        {
            if (routes[i] == null) continue;

            bool  active   = (i == _routeIndex);
            Color routeCol = routes[i].routeColor;
            Color bgColor  = active ? Color.Lerp(routeCol, Color.black, 0.42f) : new Color(0.07f, 0.10f, 0.16f, 1f);
            bgColor.a = 1f;
            Color borderCol = active ? Color.Lerp(routeCol, Color.white, 0.30f) : new Color(0.15f, 0.22f, 0.32f, 1f);

            var pr = new Rect(bx, by, 80f, 26f);
            MDT_UITheme.DrawRect(new Rect(pr.x + 2, pr.y + 2, pr.width, pr.height), new Color(0, 0, 0, 0.35f));
            MDT_UITheme.DrawRect(new Rect(pr.x - 1, pr.y - 1, pr.width + 2, pr.height + 2), borderCol);
            MDT_UITheme.DrawRect(pr, bgColor);
            MDT_UITheme.DrawRect(new Rect(pr.x, pr.y, 3, pr.height), routeCol);
            MDT_UITheme.DrawRect(new Rect(pr.x, pr.y, pr.width, 1), new Color(1,1,1,0.10f));

            Color textCol = active ? MDT_UITheme.TextCyan : MDT_UITheme.TextDim;
            GUI.Label(pr, routes[i].routeNumber, MDT_UITheme.MakeLabel(11, FontStyle.Bold, TextAnchor.MiddleCenter, textCol));

            if (GUI.Button(pr, GUIContent.none, GUIStyle.none) && _routeIndex != i)
            {
                _routeIndex = i;
                _variantLetter = "";
                _dataDirty  = true;
                ResetScroll();
            }
            bx += 86f;
        }

        bx += 10f;
        DrawDirButton(new Rect(bx,      by, 92f, 26f), "↗  A → Z",  _outbound,  () => { _outbound = true;  _dataDirty = true; ResetScroll(); });
        DrawDirButton(new Rect(bx + 96, by, 92f, 26f), "↙  Z → A", !_outbound, () => { _outbound = false; _dataDirty = true; ResetScroll(); });

        // Flip-direction button (replaces the old Tab key).
        DrawDirButton(new Rect(bx + 192, by, 30f, 26f), "⇄", false, () => { _outbound = !_outbound; _dataDirty = true; ResetScroll(); });

        // Jump-to-now and compact-mode buttons (replace the old N / M keys).
        DrawDirButton(new Rect(bx + 228, by, 46f, 26f), "NOW", false, () => JumpToNow());
        DrawDirButton(new Rect(bx + 278, by, 72f, 26f), _compactMode ? "COMPACT ✓" : "COMPACT", _compactMode,
                      () => { _compactMode = !_compactMode; ApplyCompactMode(); });

        // Variant pill (only if the route has any)
        bx += 354f;
        if (_route != null && _route.variants != null && _route.variants.Count > 0)
        {
            string vLabel = string.IsNullOrEmpty(_variantLetter) ? "MAIN" : (_variantLetter == BusRouteData.ShortTurnSymbol ? "SHORT TURN ~" : $"VAR {_variantLetter}");
            var vr = new Rect(bx, by, 104f, 26f);
            MDT_UITheme.DrawRect(new Rect(vr.x + 2, vr.y + 2, vr.width, vr.height), new Color(0,0,0,0.30f));
            MDT_UITheme.DrawRect(new Rect(vr.x - 1, vr.y - 1, vr.width + 2, vr.height + 2), MDT_UITheme.TextAmber * 0.6f);
            MDT_UITheme.DrawRect(vr, new Color(0.10f, 0.08f, 0.02f, 1f));
            GUI.Label(vr, vLabel, MDT_UITheme.MakeLabel(10, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextAmber));
            if (GUI.Button(vr, GUIContent.none, GUIStyle.none))
            {
                CycleVariant();
                _dataDirty = true;
                ResetScroll();
            }
        }

        if (_route != null)
        {
            string vTag  = string.IsNullOrEmpty(_variantLetter) ? "" : $" [{_variantLetter}]";
            string title = $"{_route.routeNumber}{vTag}  ·  {(_route.routeName ?? _route.routeNumber)}  ·  Master Schedule";
            GUI.Label(new Rect(winX, winY + 6, winW - 160f, 20), title,
                      MDT_UITheme.MakeLabel(fontSizeTitle, FontStyle.Bold, TextAnchor.MiddleRight, MDT_UITheme.TextPrimary));
        }

        string clock = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeString : "--:--";
        GUI.Label(new Rect(winX + winW - 148f, winY + 6, 130f, 20), clock,
                  MDT_UITheme.MakeLabel(fontSizeClock, FontStyle.Bold, TextAnchor.MiddleRight, MDT_UITheme.TextCyan));

        MDT_UITheme.DrawDivider(winX, winY + TOPBAR_H - 1, winW);
    }

    private void DrawDirButton(Rect r, string label, bool active, System.Action onClick)
    {
        Color bg = active ? MDT_UITheme.BGDirSel : MDT_UITheme.BGPill;
        bg.a = 1f;
        MDT_UITheme.DrawRect(new Rect(r.x + 2, r.y + 2, r.width, r.height), new Color(0,0,0,0.30f));
        MDT_UITheme.DrawRect(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2),
                             active ? MDT_UITheme.TextCyan * 0.5f : new Color(0.15f, 0.22f, 0.32f, 1f));
        MDT_UITheme.DrawRect(r, bg);
        Color textCol = active ? MDT_UITheme.TextWhite : MDT_UITheme.TextDim;
        GUI.Label(r, label, MDT_UITheme.MakeLabel(10, FontStyle.Bold, TextAnchor.MiddleCenter, textCol));
        if (GUI.Button(r, GUIContent.none, GUIStyle.none)) onClick?.Invoke();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  INFO STRIP — a small row of stat cards (timing, headway, fleet
    //  coverage) sitting between the topbar and the grid. Gives the route's
    //  vitals at a glance instead of making you infer them from the grid.
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawInfoStrip(float winX, float winY, float winW)
    {
        MDT_UITheme.DrawRect(new Rect(winX, winY, winW, INFO_STRIP_H), new Color(0.045f, 0.065f, 0.100f, 1f));
        MDT_UITheme.DrawDivider(winX, winY + INFO_STRIP_H - 1, winW);

        if (_route == null) return;

        SlotTripRange(out float tripMin, out float tripMax);
        OperatingSpan(out float opStart, out float opEnd);
        string tripText = Mathf.Abs(tripMax - tripMin) < 0.5f ? $"{tripMax:F0} min" : $"{tripMin:F0}–{tripMax:F0} min";

        float avgHeadway = -1f;
        if (_allSlots.Count > 1)
        {
            float span = _allSlots[_allSlots.Count - 1].scheduledDeparture - _allSlots[0].scheduledDeparture;
            avgHeadway = span / Mathf.Max(1, _allSlots.Count - 1);
        }

        int liveCount = 0, delayedCount = 0;
        foreach (var s in _allSlots)
        {
            if (s.state == SlotState.InService) liveCount++;
            if (s.state == SlotState.InService && s.latenessMinutes > 5f) delayedCount++;
        }
        int cap = _route.maxBusesAllowed;

        float cardW = 148f, cardH = INFO_STRIP_H - 12f, gap = 8f;
        float cx = winX + 10f, cy = winY + 6f;

        DrawStatCard(new Rect(cx, cy, cardW, cardH), "OPERATING",
            $"{MinToStr(opStart)} – {MinToStr(opEnd)}", MDT_UITheme.TextPrimary);
        cx += cardW + gap;

        DrawStatCard(new Rect(cx, cy, cardW * 0.9f, cardH), "ONE-WAY TRIP",
            tripText, MDT_UITheme.TextPrimary);
        cx += cardW * 0.9f + gap;

        DrawStatCard(new Rect(cx, cy, cardW * 0.7f, cardH), "HEADWAY",
            avgHeadway >= 0f ? $"~{avgHeadway:F0} min" : "—", MDT_UITheme.TextPrimary);
        cx += cardW * 0.7f + gap;

        Color coverageCol = liveCount >= cap ? MDT_UITheme.TextGreen
                           : liveCount > 0    ? MDT_UITheme.TextAmber
                                               : MDT_UITheme.TextDim;
        DrawStatCard(new Rect(cx, cy, cardW * 0.6f, cardH), "ON ROUTE NOW",
            $"{liveCount} / {cap}", coverageCol);
        cx += cardW * 0.6f + gap;

        if (delayedCount > 0)
            DrawStatCard(new Rect(cx, cy, cardW * 0.6f, cardH), "DELAYED",
                $"⚠ {delayedCount}", new Color(1f, 0.45f, 0.35f));
    }

    private void DrawStatCard(Rect r, string label, string value, Color valueColor)
    {
        MDT_UITheme.DrawRoundedRect(new Rect(r.x + 1, r.y + 1, r.width, r.height), MDT_UITheme.RadiusPill, new Color(0, 0, 0, 0.30f));
        MDT_UITheme.DrawRoundedRect(r, MDT_UITheme.RadiusPill, MDT_UITheme.BGPill);
        MDT_UITheme.DrawRoundedRect(new Rect(r.x, r.y, 4, r.height), MDT_UITheme.RadiusChip, MDT_UITheme.TextCyan * new Color(1, 1, 1, 0.45f));
        GUI.Label(new Rect(r.x + 8, r.y + 3, r.width - 12, 12), label,
            MDT_UITheme.MakeLabel(8, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextDim));
        GUI.Label(new Rect(r.x + 8, r.y + 15, r.width - 12, 20), value,
            MDT_UITheme.MakeLabel(12, FontStyle.Bold, TextAnchor.MiddleLeft, valueColor));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  GRID
    // ═════════════════════════════════════════════════════════════════════════
    /// <summary>One-way trip minutes for a single slot: the variant's own
    /// trip time + windows when it overrides the schedule, else the route's,
    /// scaled by whichever schedule window contains the slot's departure
    /// (tripTimeMultiplierPercent). Mirrors BusScheduler.TripMinutesForSlot.</summary>
    private float SlotTripMinutes(TimetableSlot slot)
    {
        if (_route == null) return 60f;
        float m = ((slot.scheduledDeparture % 1440f) + 1440f) % 1440f;
        if (_variant != null && _variant.overrideSchedule)
            return _variant.EffectiveTripMinutes(_route.oneWayTripMinutes, m);
        return _route.EffectiveTripMinutes(m);
    }

    /// <summary>Min/max scaled trip time across the slots currently shown.</summary>
    private void SlotTripRange(out float min, out float max)
    {
        min = float.MaxValue; max = 0f;
        foreach (var s in _allSlots)
        {
            float t = SlotTripMinutes(s);
            if (t < min) min = t;
            if (t > max) max = t;
        }
        if (min == float.MaxValue) { min = max = _route != null ? _route.oneWayTripMinutes : 0f; }
    }

    /// <summary>Operating span of what's actually shown: first/last departure
    /// window when time-of-day windows are used (the flat operatingStart/End
    /// fields are ignored by the scheduler in that case), else the flat pair
    /// (or the variant's own).</summary>
    private void OperatingSpan(out float start, out float end)
    {
        var wins = (_variant != null && _variant.overrideSchedule && _variant.UsesTimeOfDayWindows)
            ? _variant.scheduleWindows
            : (_variant != null && _variant.overrideSchedule ? null : (_route.UsesTimeOfDayWindows ? _route.scheduleWindows : null));
        if (wins != null && wins.Count > 0)
        {
            start = float.MaxValue; end = float.MinValue;
            foreach (var w in wins)
            {
                if (w.windowStartMinutes < start) start = w.windowStartMinutes;
                if (w.windowEndMinutes   > end)   end   = w.windowEndMinutes;
            }
            return;
        }
        if (_variant != null && _variant.overrideSchedule)
        { start = _variant.operatingStartMinutes; end = _variant.operatingEndMinutes; return; }
        start = _route.operatingStartMinutes; end = _route.operatingEndMinutes;
    }

    // Night service: departures 22:00-03:59 (minute-of-day 1320-1439 and 0-239).
    private static readonly Color NightOutline = new Color(0.015f, 0.03f, 0.11f, 1f); // dark blue, almost black
    private const float NightStartMin = 22f * 60f;
    private const float NightEndMin   = 4f * 60f;

    private static bool IsNightDeparture(TimetableSlot slot)
    {
        float m = ((slot.scheduledDeparture % 1440f) + 1440f) % 1440f;
        return m >= NightStartMin || m < NightEndMin;
    }

    /// <summary>Outlines every night-service column (header through last stop
    /// row) with a dark navy border so overnight runs read at a glance.</summary>
    private void DrawNightOutlines(int colCount, int rowCount, float gw, float gh)
    {
        float bottom = Mathf.Min(gh, headerHeight + rowCount * rowHeight - _scrollY);
        const float t = 2f;
        for (int c = 0; c < colCount; c++)
        {
            float cx = stopColWidth + c * depColWidth - _scrollX;
            if (cx + depColWidth < stopColWidth) continue;
            if (cx > gw) break;
            if (!IsNightDeparture(_allSlots[c])) continue;

            float x0 = cx, x1 = cx + depColWidth;
            float h  = bottom - 0f;
            MDT_UITheme.DrawRect(new Rect(x0,     0,        depColWidth, t), NightOutline); // top
            MDT_UITheme.DrawRect(new Rect(x0,     h - t,    depColWidth, t), NightOutline); // bottom
            MDT_UITheme.DrawRect(new Rect(x0,     0,        t,           h), NightOutline); // left
            MDT_UITheme.DrawRect(new Rect(x1 - t, 0,        t,           h), NightOutline); // right
        }
    }

    private void DrawGrid(float gx, float gy, float gw, float gh)
    {
        MDT_UITheme.DrawInset(new Rect(gx, gy, gw, gh), MDT_UITheme.BGMid);

        int colCount = _allSlots.Count;
        int rowCount = _stops.Count;

        if (colCount == 0 || rowCount == 0)
        {
            string vTag = string.IsNullOrEmpty(_variantLetter) ? "" : $" (variant {_variantLetter})";
            GUI.Label(new Rect(gx + stopColWidth + 20f, gy + headerHeight + 20f, 460f, 28f),
                $"No scheduled runs found for this route{vTag} and direction.",
                MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim));
            return;
        }

        float totalColsW = colCount * depColWidth;
        float availW     = gw - stopColWidth;
        _maxScrollX       = Mathf.Max(0f, totalColsW - availW);

        float totalRowsH = rowCount * rowHeight;
        float availH     = gh - headerHeight;
        _maxScrollY       = Mathf.Max(0f, totalRowsH - availH);

        float now          = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeMinutes : -1f;
        int   liveColIdx   = FindLiveColumnIndex(now);
        int   playerRowIdx = FindPlayerStopIndex();

        GUI.BeginClip(new Rect(gx, gy, gw, gh));

        DrawColumnHighlights(colCount, liveColIdx);
        DrawRowBands(rowCount, playerRowIdx, gh);
        DrawColumnHeaders(colCount, liveColIdx, gw, now);
        DrawCells(colCount, rowCount, liveColIdx, gh);
        DrawNightOutlines(colCount, rowCount, gw, gh);
        DrawStopColumn(rowCount, playerRowIdx, gh);
        DrawCornerBox();
        DrawColumnDividers(colCount, gw, gh);

        GUI.EndClip();
    }

    private void DrawColumnHighlights(int colCount, int liveColIdx)
    {
        for (int c = 0; c < colCount; c++)
        {
            float cx = stopColWidth + c * depColWidth - _scrollX;
            if (cx + depColWidth < stopColWidth) continue;
            if (cx > Screen.width) break;

            if (c == liveColIdx)
            {
                MDT_UITheme.DrawRect(new Rect(cx, 0, depColWidth, Screen.height), new Color(0.08f, 0.45f, 0.60f, 0.12f));
                MDT_UITheme.DrawRect(new Rect(cx, 0, 2, Screen.height), new Color(0.22f, 0.82f, 1f, 0.55f));
            }
        }
    }

    private void DrawRowBands(int rowCount, int playerRowIdx, float gh)
    {
        for (int r = 0; r < rowCount; r++)
        {
            float ry = headerHeight + r * rowHeight - _scrollY;
            if (ry + rowHeight < headerHeight) continue;
            if (ry > gh) break;

            Color rowBg = (r % 2 == 0) ? MDT_UITheme.BGRowEven : MDT_UITheme.BGRowOdd;
            MDT_UITheme.DrawRect(new Rect(0, ry, Screen.width, rowHeight), rowBg);

            if (r == playerRowIdx)
            {
                MDT_UITheme.DrawRect(new Rect(0, ry, Screen.width, rowHeight), new Color(0.95f, 0.72f, 0.12f, 0.07f));
                MDT_UITheme.DrawRect(new Rect(0, ry, 2, rowHeight), new Color(0.95f, 0.72f, 0.12f, 0.90f));
            }

            MDT_UITheme.DrawRect(new Rect(0, ry + rowHeight - 1, Screen.width, 1), new Color(0.10f, 0.16f, 0.25f, 0.70f));
        }
    }

    // ── Column headers — now with a live delay line ─────────────────────────────
    //
    //  ┌──────────────────┐
    //  │    06:30         │  ← departure time (large)
    //  │    #1805         │  ← fleet ID (small, dimmed)
    //  │    +3m late      │  ← NEW: delay/wait indicator
    //  └──────────────────┘
    //
    private void DrawColumnHeaders(int colCount, int liveColIdx, float gw, float now)
    {
        MDT_UITheme.DrawHeader(new Rect(0, 0, gw, headerHeight));

        for (int c = 0; c < colCount; c++)
        {
            var   slot = _allSlots[c];
            float cx   = stopColWidth + c * depColWidth - _scrollX;
            if (cx + depColWidth < stopColWidth) continue;
            if (cx > gw) break;

            bool isLive = (c == liveColIdx);

            Color colBg = isLive ? new Color(0.07f, 0.30f, 0.42f, 1f) : new Color(0.055f, 0.085f, 0.145f, 1f);
            MDT_UITheme.DrawRect(new Rect(cx + 1, 1, depColWidth - 2, headerHeight - 1), colBg);
            // subtle top highlight per column so the header reads as a row of
            // distinct cards rather than one flat strip
            MDT_UITheme.DrawRect(new Rect(cx + 1, 1, depColWidth - 2, 1), new Color(1, 1, 1, isLive ? 0.14f : 0.05f));

            Color depColor = GetSlotTextColor(slot, isLive);

            // Departure time as a small pill rather than bare text — gives
            // every column a clear focal point instead of three stacked
            // labels competing for attention.
            var pillR = new Rect(cx + 6, 5, depColWidth - 12, 20);
            Color pillBg = isLive ? new Color(0.10f, 0.45f, 0.60f, 0.55f) : new Color(0f, 0f, 0f, 0.22f);
            MDT_UITheme.DrawRect(pillR, pillBg);
            MDT_UITheme.DrawRect(new Rect(pillR.x, pillR.y, pillR.width, 1), new Color(1, 1, 1, 0.08f));
            GUI.Label(pillR, MinToStr(slot.scheduledDeparture),
                MDT_UITheme.MakeLabel(fontSizeColDep, FontStyle.Bold, TextAnchor.MiddleCenter, depColor));

            string idStr = slot.state == SlotState.Unassigned ? "TBD"
                : slot.state == SlotState.AssignedPlayer ? $"#{ResolveDisplayFleetNumber(slot)}"
                : slot.assignedBusID < 0 ? "—"   // completed slot nothing ever ran (e.g. startup gap) — not a real bus ID
                : $"#{ResolveDisplayFleetNumber(slot)}";
            Color idColor = slot.state == SlotState.AssignedPlayer ? MDT_UITheme.TextAmber : MDT_UITheme.TextDim;
            GUI.Label(new Rect(cx, 27, depColWidth, 16), idStr,
                MDT_UITheme.MakeLabel(fontSizeColID, FontStyle.Bold, TextAnchor.MiddleCenter, idColor));

            // ── NEW: delay / wait line ──────────────────────────────────────
            DrawDelayLine(slot, cx, now);

            if (slot.state == SlotState.AssignedPlayer)
            {
                var badgeR = new Rect(cx + depColWidth - 26, 2, 24, 14);
                MDT_UITheme.DrawRect(badgeR, new Color(0.90f, 0.65f, 0.08f, 0.90f));
                GUI.Label(badgeR, "YOU", MDT_UITheme.MakeLabel(7, FontStyle.Bold, TextAnchor.MiddleCenter, Color.black));
            }

            if (isLive)
                GUI.Label(new Rect(cx, headerHeight - 12, depColWidth, 11), "▼ NOW",
                    MDT_UITheme.MakeLabel(7, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextCyan));

            MDT_UITheme.DrawRect(new Rect(cx + depColWidth - 1, 0, 1, headerHeight), MDT_UITheme.Divider);
        }

        MDT_UITheme.DrawRect(new Rect(0, headerHeight - 1, gw, 1), MDT_UITheme.Divider);
        MDT_UITheme.DrawRect(new Rect(0, headerHeight,     gw, 1), new Color(1,1,1,0.04f));
    }

    /// <summary>Draws the small lateness/wait readout under the fleet ID.
    /// InService: uses locked-in latenessMinutes from departure.
    /// AssignedNPC/AssignedPlayer still at terminal: computes live wait against
    /// the current game clock, since actualDeparture hasn't happened yet.</summary>
    private void DrawDelayLine(TimetableSlot slot, float cx, float now)
    {
        string text = null;
        Color  col  = MDT_UITheme.TextDim;

        if (slot.state == SlotState.InService || slot.state == SlotState.Completed)
        {
            float late = slot.latenessMinutes;
            if (Mathf.Abs(late) < 1f) { text = "on time"; col = MDT_UITheme.TextGreen; }
            else if (late > 0f)
            {
                text = $"+{late:F0}m late";
                col  = late > 5f ? new Color(1f, 0.35f, 0.30f) : MDT_UITheme.TextAmber;
            }
            else { text = $"{-late:F0}m early"; col = MDT_UITheme.TextGreen; }
        }
        else if ((slot.state == SlotState.AssignedNPC || slot.state == SlotState.AssignedPlayer) && now >= 0f)
        {
            float wait = now - slot.scheduledDeparture;
            if (wait > 1f)
            {
                text = $"waiting +{wait:F0}m";
                col  = wait > 8f ? new Color(1f, 0.35f, 0.30f) : MDT_UITheme.TextAmber;
            }
        }

        if (text != null)
            GUI.Label(new Rect(cx, 42, depColWidth, 14), text,
                MDT_UITheme.MakeLabel(fontSizeDelay, FontStyle.Bold, TextAnchor.MiddleCenter, col));
    }

    /// <summary>Resolves a fleet-facing number for a slot's assigned bus,
    /// preferring BusRegistry's real fleet number over the raw busID (matches
    /// BusTrackerService.ResolveFleetLabel so the timetable and tracker agree).</summary>
    private int ResolveDisplayFleetNumber(TimetableSlot slot)
    {
        // ROOT-CAUSE FIX: this used to check `assignedBusID < 0` FIRST and
        // return immediately — but PLAYER_BUS_ID is ALSO negative (-2), so
        // the player-specific check below (which correctly resolves the
        // real fleet number) was permanently unreachable dead code. Check
        // for the player explicitly before the generic negative-ID
        // fallback, not after it.
        var player = PlayerHandoff.Instance;
        if (player != null && slot.assignedBusID == player.PlayerBusID)
            return player.FleetNumber;

        if (slot.assignedBusID < 0) return slot.assignedBusID;

        if (BusRegistry.ActiveBuses.TryGetValue(slot.assignedBusID, out var ctrl) && ctrl != null)
            return ctrl.fleetNumber;

        return slot.assignedBusID;
    }

    // ── Cell matrix ───────────────────────────────────────────────────────────
    private void DrawCells(int colCount, int rowCount, int liveColIdx, float gh)
    {
        // One GUIStyle for the whole grid (recolored per cell) instead of a
        // fresh allocation per visible cell per repaint -- that GC churn was
        // the main cause of the timetable feeling slow to open/scroll.
        var cellStyle = MDT_UITheme.MakeLabel(fontSizeCell, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
        for (int r = 0; r < rowCount; r++)
        {
            float ry = headerHeight + r * rowHeight - _scrollY;
            if (ry + rowHeight < headerHeight) continue;
            if (ry > gh) break;

            float frac = rowCount > 1 ? (float)r / (rowCount - 1) : 0f;

            for (int c = 0; c < colCount; c++)
            {
                var   slot = _allSlots[c];
                float cx   = stopColWidth + c * depColWidth - _scrollX;
                if (cx + depColWidth < stopColWidth) continue;
                if (cx > Screen.width) break;

                // Base schedule time plus any locked-in lateness so the body
                // of the grid reflects reality, not just the plan.
                float lateOffset = slot.state == SlotState.InService || slot.state == SlotState.Completed
                    ? slot.latenessMinutes : 0f;
                // Window-scaled trip time (BusRouteData.tripTimeMultiplierPercent) for THIS
                // column's departure, so an overnight run shows its faster real timings.
                float arrival = slot.scheduledDeparture + SlotTripMinutes(slot) * frac + lateOffset;

                Color cellBg = GetSlotCellBg(slot, c == liveColIdx);
                if (cellBg.a > 0.01f)
                    MDT_UITheme.DrawRect(new Rect(cx + 1, ry, depColWidth - 2, rowHeight - 1), cellBg);

                Color textCol = GetSlotCellTextColor(slot, c == liveColIdx);
                bool completed = (slot.state == SlotState.Completed);

                if (completed)
                    MDT_UITheme.DrawRect(new Rect(cx + 4, ry + rowHeight * 0.5f - 0.5f, depColWidth - 8, 1),
                        new Color(1,1,1,0.20f));

                cellStyle.normal.textColor = textCol;
                GUI.Label(new Rect(cx + 2, ry, depColWidth - 4, rowHeight), MinToStr(arrival), cellStyle);
            }
        }
    }

    // ── Stop column (sticky left) ─────────────────────────────────────────────
    private void DrawStopColumn(int rowCount, int playerRowIdx, float gh)
    {
        MDT_UITheme.DrawRect(new Rect(0, headerHeight, stopColWidth - 1, gh - headerHeight),
                             new Color(0.038f, 0.055f, 0.085f, 1f));
        MDT_UITheme.DrawRect(new Rect(stopColWidth - 1, headerHeight, 1, gh - headerHeight), MDT_UITheme.Divider);

        for (int r = 0; r < rowCount; r++)
        {
            float ry = headerHeight + r * rowHeight - _scrollY;
            if (ry + rowHeight < headerHeight) continue;
            if (ry > gh) break;

            var stop = _stops[r];
            if (stop == null) continue;

            bool isTerminal = stop.isTerminal;
            bool isPlayer   = (r == playerRowIdx);

            if (isPlayer)
                MDT_UITheme.DrawRect(new Rect(0, ry, stopColWidth - 1, rowHeight), new Color(0.95f, 0.72f, 0.12f, 0.06f));

            if (isTerminal)
            {
                // glow + solid core instead of a flat bar — matches the
                // terminal dots used on the live map for visual consistency
                MDT_UITheme.DrawRect(new Rect(0, ry, 6, rowHeight), MDT_UITheme.TermGlow);
                MDT_UITheme.DrawRect(new Rect(0, ry, 3, rowHeight), MDT_UITheme.TermDot);
            }

            Color nameColor = isPlayer ? MDT_UITheme.TextAmber : isTerminal ? MDT_UITheme.TermDot : MDT_UITheme.TextPrimary;

            GUI.Label(new Rect(12, ry + 1, stopColWidth - 18, rowHeight * 0.55f), stop.stopName ?? stop.stopCode,
                MDT_UITheme.MakeLabel(fontSizeStop, FontStyle.Bold, TextAnchor.MiddleLeft, nameColor));

            // Road event closing this stop for the route being viewed: show the code line as a red CLOSED tag.
            bool closed = _route != null && RoadEventRegistry.Instance != null
                          && RoadEventRegistry.Instance.IsStopClosed(stop.stopCode, _route.routeNumber, _outbound);
            GUI.Label(new Rect(12, ry + rowHeight * 0.52f, stopColWidth - 18, rowHeight * 0.48f),
                closed ? stop.stopCode + "  ⛔ CLOSED — DETOUR" : stop.stopCode,
                MDT_UITheme.MakeLabel(fontSizeStop - 1, closed ? FontStyle.Bold : FontStyle.Normal, TextAnchor.MiddleLeft,
                                      closed ? new Color(1f, 0.35f, 0.25f, 1f) : MDT_UITheme.TextDim));

            if (isPlayer)
                GUI.Label(new Rect(stopColWidth - 18, ry, 14, rowHeight), "◀",
                    MDT_UITheme.MakeLabel(9, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextAmber));
        }
    }

    private void DrawColumnDividers(int colCount, float gw, float gh)
    {
        for (int c = 0; c <= colCount; c++)
        {
            float cx = stopColWidth + c * depColWidth - _scrollX;
            if (cx < stopColWidth || cx > gw) continue;
            MDT_UITheme.DrawRect(new Rect(cx - 1, headerHeight, 1, gh - headerHeight), new Color(0.10f, 0.16f, 0.26f, 0.60f));
        }
    }

    private void DrawCornerBox()
    {
        MDT_UITheme.DrawHeader(new Rect(0, 0, stopColWidth - 1, headerHeight));
        MDT_UITheme.DrawRect(new Rect(0, 0, stopColWidth - 1, headerHeight), new Color(0.025f, 0.040f, 0.070f, 1f));
        MDT_UITheme.DrawRect(new Rect(stopColWidth - 1, 0, 1, headerHeight), MDT_UITheme.Divider);
        MDT_UITheme.DrawRect(new Rect(0, headerHeight - 1, stopColWidth - 1, 1), MDT_UITheme.Divider);

        string dirStr = _outbound ? "A → Z" : "Z → A";
        string vStr   = string.IsNullOrEmpty(_variantLetter) ? "" : $"  ·  Var {_variantLetter}";
        GUI.Label(new Rect(6, 4, stopColWidth - 12, 16),
                  "STOP", MDT_UITheme.MakeLabel(9, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextDim));
        GUI.Label(new Rect(6, 19, stopColWidth - 12, 16),
                  dirStr + vStr, MDT_UITheme.MakeLabel(10, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextCyan));
        GUI.Label(new Rect(6, 34, stopColWidth - 12, 12),
                  $"{_stops.Count} stops  ·  {_allSlots.Count} runs",
                  MDT_UITheme.MakeLabel(8, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim));

        int delayed = 0;
        foreach (var s in _allSlots)
            if (s.state == SlotState.InService && s.latenessMinutes > 1f) delayed++;
        if (delayed > 0)
            GUI.Label(new Rect(6, 46, stopColWidth - 12, 10), $"⚠ {delayed} delayed",
                MDT_UITheme.MakeLabel(8, FontStyle.Bold, TextAnchor.MiddleLeft, new Color(1f, 0.45f, 0.35f)));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  SCROLLBARS / FOOTER
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawScrollbars(float winX, float winY, float winW, float winH, float tblW, float tblH)
    {
        float availW = tblW - stopColWidth;
        float availH = tblH - headerHeight;

        if (_maxScrollX > 0f)
        {
            float sbY = winY + TOPBAR_H + tblH + 2f;
            _scrollX = GUI.HorizontalScrollbar(new Rect(winX + stopColWidth, sbY, availW, SCROLLBAR_H),
                _scrollX, Mathf.Max(1, availW), 0f, Mathf.Max(1, _allSlots.Count * depColWidth));
        }
        if (_maxScrollY > 0f)
        {
            float sbX = winX + tblW + 2f;
            _scrollY = GUI.VerticalScrollbar(new Rect(sbX, winY + TOPBAR_H + headerHeight, SCROLLBAR_W, availH),
                _scrollY, Mathf.Max(1, availH), 0f, Mathf.Max(1, _stops.Count * rowHeight));
        }
    }

    private void DrawFooter(float winX, float winY, float winW, float winH)
    {
        float fy = winY + winH - FOOTER_H;
        MDT_UITheme.DrawRect(new Rect(winX, fy, winW, FOOTER_H), MDT_UITheme.BGFooter);
        MDT_UITheme.DrawDivider(winX, fy, winW);

        GUI.Label(new Rect(winX + 8, fy + 4, winW * 0.7f, 14),
                  "[T] close  ·  [←][→] route  ·  [V] variant  ·  [J] my stop  ·  [O] future only  ·  scroll=pan  ·  Shift+scroll=horizontal",
                  MDT_UITheme.MakeLabel(8, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim));

        DrawLegendDot(winX + winW - 340f, fy + 4, MDT_UITheme.TextGreen,  "On time");
        DrawLegendDot(winX + winW - 250f, fy + 4, new Color(1f,0.45f,0.35f), "Delayed");
        DrawLegendDot(winX + winW - 155f, fy + 4, MDT_UITheme.TextAmber,  "Player");
        DrawLegendDot(winX + winW - 82f,  fy + 4, MDT_UITheme.TextDim,    "TBD");
    }

    private void DrawLegendDot(float x, float y, Color col, string label)
    {
        MDT_UITheme.DrawRect(new Rect(x, y + 4, 6, 6), col);
        GUI.Label(new Rect(x + 9, y, 62f, 14), label, MDT_UITheme.MakeLabel(8, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  SLOT STATE → VISUAL HELPERS
    // ═════════════════════════════════════════════════════════════════════════
    private Color GetSlotTextColor(TimetableSlot slot, bool isLive)
    {
        if (slot.state == SlotState.Completed)      return MDT_UITheme.TextDim;
        if (slot.state == SlotState.Unassigned)     return new Color(0.45f, 0.52f, 0.65f, 0.80f);
        if (slot.state == SlotState.AssignedPlayer) return MDT_UITheme.TextAmber;
        if (isLive)                                  return MDT_UITheme.TextCyan;
        return MDT_UITheme.TextPrimary;
    }

    private Color GetSlotCellBg(TimetableSlot slot, bool isLiveCol)
    {
        if (slot.state == SlotState.AssignedPlayer) return new Color(0.55f, 0.40f, 0.04f, 0.12f);
        if (slot.state == SlotState.Completed)      return new Color(0f, 0f, 0f, 0f);
        if (slot.state == SlotState.Unassigned)     return new Color(0f, 0f, 0f, 0f);
        if (slot.state == SlotState.InService && slot.latenessMinutes > 5f)
            return new Color(0.55f, 0.10f, 0.06f, 0.14f); // delayed tint takes priority
        if (_route != null)
        {
            Color rc = _route.routeColor;
            return new Color(rc.r, rc.g, rc.b, isLiveCol ? 0.12f : 0.07f);
        }
        return new Color(0f, 0f, 0f, 0f);
    }

    private Color GetSlotCellTextColor(TimetableSlot slot, bool isLiveCol)
    {
        if (slot.state == SlotState.Completed)      return new Color(0.35f, 0.42f, 0.55f, 0.55f);
        if (slot.state == SlotState.Unassigned)     return new Color(0.40f, 0.48f, 0.62f, 0.70f);
        if (slot.state == SlotState.AssignedPlayer) return MDT_UITheme.TextAmber;
        if (slot.state == SlotState.InService && slot.latenessMinutes > 5f) return new Color(1f, 0.5f, 0.4f);
        if (isLiveCol)                               return MDT_UITheme.TextCyan;
        return MDT_UITheme.TextSecond;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  LIVE COLUMN / PLAYER ROW DETECTION
    // ═════════════════════════════════════════════════════════════════════════
    private int FindLiveColumnIndex(float nowMinutes)
    {
        if (nowMinutes < 0f || _allSlots.Count == 0) return -1;
        int   best     = -1;
        float bestDiff = float.MaxValue;
        for (int i = 0; i < _allSlots.Count; i++)
        {
            float diff = Mathf.Abs(_allSlots[i].scheduledDeparture - nowMinutes);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        float window = 90f;
        if (_route != null) { SlotTripRange(out _, out float tripMax); window = tripMax * 1.5f; }
        return bestDiff <= window ? best : -1;
    }

    /// <summary>FIX: this used to depend on PlayerShiftDirector.Phase and
    /// BusSchedulerPlayerService.CurrentLeg — classes from a from-scratch
    /// shift-system rebuild that was reverted. Rewired onto the real,
    /// current owner of shift state (PlayerHandoff) + BusScheduler for the
    /// actual TimetableSlot, same lookup pattern PlayerHandoff.HasPendingChainLeg
    /// and PlayerDestinationBoard already use elsewhere in the project.</summary>
    private int FindPlayerStopIndex()
    {
        var ph = PlayerHandoff.Instance;
        if (ph == null || ph.ShiftState != PlayerHandoff.PlayerShiftState.InService) return -1;

        if (BusScheduler.Instance == null || !BusScheduler.Instance.TryGetAssignedSlot(ph.PlayerBusID, out var currentLeg))
            return -1;
        if (currentLeg == null) return -1;
        if (_route == null || currentLeg.routeNumber != _route.routeNumber) return -1;
        if ((currentLeg.variantLetter ?? "") != _variantLetter) return -1; // must match selected variant too
        if (currentLeg.isOutbound != _outbound) return -1;

        int stopIdx = ph.CurrentStopIndex;
        if (stopIdx < 0 || stopIdx >= _stops.Count) return -1;
        return stopIdx;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DATA LOADING
    // ═════════════════════════════════════════════════════════════════════════
    private void LoadStaticSchedule()
    {
        _stops.Clear();
        var resolved = _route?.GetStops(_outbound, _variant);
        if (resolved != null) _stops.AddRange(resolved);

        _allSlots.Clear();
        var all = BusScheduler.Instance?.AllSlots;
        if (all == null) return;

        for (int i = 0; i < all.Count; i++)
        {
            var s = all[i];
            if (s.routeNumber != _route?.routeNumber) continue;
            if (s.isOutbound  != _outbound)           continue;
            if ((s.variantLetter ?? "") != _variantLetter) continue; // ← variant filter
            _allSlots.Add(s);
        }

        _allSlots.Sort((a, b) => a.scheduledDeparture.CompareTo(b.scheduledDeparture));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STYLE BUILDER / UTILITY
    // ═════════════════════════════════════════════════════════════════════════
    private void BuildStyles() { _stylesBuilt = true; }
    private void ResetScroll() { _scrollX = 0f; _scrollY = 0f; }

    private static string MinToStr(float m)
    {
        int h  = Mathf.FloorToInt(m / 60f) % 24;
        int mn = Mathf.FloorToInt(m % 60f);
        return $"{h:D2}:{mn:D2}";
    }
}