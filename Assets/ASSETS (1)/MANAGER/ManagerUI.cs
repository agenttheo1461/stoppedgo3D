using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTE MANAGER SCREEN
//
//  Left:   four tabs — Routes, Buses, Plan, Events.
//  Right:  the map (MDT_LiveMap.ManagerDraw), with a card for the selected bus.
//  Top:    clock, fleet numbers, on-time share, Undo and Menu.
//
//  Wording is deliberately plain. Every refusal says why, in a sentence.
// ═══════════════════════════════════════════════════════════════════════════════
/// <summary>How the manager screen sizes itself: bigger and touch-friendly on a phone or tablet, folded into one column when narrow.</summary>
public static class ManagerLayout
{
    /// <summary>Press F10 in the manager to pretend to be a phone (for testing on a computer).</summary>
    public static bool ForceTouch;
    public static bool Touch => Application.isMobilePlatform || ForceTouch;

    // Extra space kept clear on each side, on top of the device's own safe area (notches, rounded corners,
    // a thumb-hugging case, a TV's overscan). In UI units, so it is the same physical size on any screen.
    public const float MaxMargin = 200f;
    private static bool _marginsLoaded;
    private static float _mL, _mR, _mT, _mB;

    private static void LoadMargins()
    {
        if (_marginsLoaded) return;
        _marginsLoaded = true;
        _mL = PlayerPrefs.GetFloat("Headway.Mgr.MarginL", 0f); _mR = PlayerPrefs.GetFloat("Headway.Mgr.MarginR", 0f);
        _mT = PlayerPrefs.GetFloat("Headway.Mgr.MarginT", 0f); _mB = PlayerPrefs.GetFloat("Headway.Mgr.MarginB", 0f);
    }
    public static float MarginLeft   { get { LoadMargins(); return _mL; } set { LoadMargins(); _mL = Mathf.Clamp(value, 0f, MaxMargin); } }
    public static float MarginRight  { get { LoadMargins(); return _mR; } set { LoadMargins(); _mR = Mathf.Clamp(value, 0f, MaxMargin); } }
    public static float MarginTop    { get { LoadMargins(); return _mT; } set { LoadMargins(); _mT = Mathf.Clamp(value, 0f, MaxMargin); } }
    public static float MarginBottom { get { LoadMargins(); return _mB; } set { LoadMargins(); _mB = Mathf.Clamp(value, 0f, MaxMargin); } }

    public static void SaveMargins()
    {
        LoadMargins();
        try
        {
            PlayerPrefs.SetFloat("Headway.Mgr.MarginL", _mL); PlayerPrefs.SetFloat("Headway.Mgr.MarginR", _mR);
            PlayerPrefs.SetFloat("Headway.Mgr.MarginT", _mT); PlayerPrefs.SetFloat("Headway.Mgr.MarginB", _mB);
            PlayerPrefs.Save();
        }
        catch (System.Exception) { }
    }

    /// <summary>Whole-screen UI scale. A phone is dense (400+ dpi), so it needs a lot more than a monitor.</summary>
    public static float Scale
    {
        get
        {
            float byHeight = Screen.height / 1080f;
            if (!Touch) return Mathf.Clamp(byHeight, 1f, 2.4f);
            float dpi = Screen.dpi > 0f ? Screen.dpi : 320f;
            return Mathf.Clamp(Mathf.Max(dpi / 150f, byHeight), 1.6f, 3.4f);
        }
    }
}

public class ManagerUI : MonoBehaviour
{
    private enum Tab { Routes, Buses, Plan, Tracker, Events }
    private Tab _tab = Tab.Routes;

    private const float TopH = 52f;

    // ── Selection ────────────────────────────────────────────────────────────
    private int _selBus = -1;
    private float _vw, _vh;               // the screen in UI units (for the margin editor)
    private bool _marginsOpen;
    private bool _panelOpen;              // narrow screens only: the side panel takes over the screen when open
    private bool _compact;
    private BusRouteData _selRoute;
    private int _swapFirst = -1;          // set while choosing the second bus of a swap
    private int _handBus = -1;            // a bus "in hand" while picking a trip for it
    private bool _handQueue;              // true: the bus is on a trip, so it moves AFTER that trip
    private string _search = "";
    private int _busFilter;               // 0 all, 1 parked, 2 on the road, 3 broken

    // ── Plan tab ─────────────────────────────────────────────────────────────
    private int _dayOffset;
    private int _planRouteIdx;
    private List<TimetableSlot> _trips = new List<TimetableSlot>();
    private TimetableSlot _pickTrip;
    private List<Cand> _cands = new List<Cand>();
    private int _replaceBroken = -1;      // busID being replaced (Events tab picker)
    private float _tripsTimer;
    private List<string> _planProblems = new List<string>();

    // ── Scrolls ──────────────────────────────────────────────────────────────
    private Vector2 _scrollRoutes, _scrollBuses, _scrollPlan, _scrollEvents, _scrollPick;

    // ── Notes ────────────────────────────────────────────────────────────────
    private string _note; private bool _noteGood; private float _noteAt = -99f;
    private static string _pendingGood, _pendingWarn;
    public static void PostNote(string good, string warn) { _pendingGood = good; _pendingWarn = warn; }

    // ── Cached data ──────────────────────────────────────────────────────────
    private class BusRow
    {
        public int busID, fleet;
        public string series, state, route, depot;
        public bool parked, onRoad, broken, held, fix;
        public NPCBusController ctrl;
    }
    private readonly List<BusRow> _rows = new List<BusRow>();
    private readonly Dictionary<string, int> _runCount = new Dictionary<string, int>();
    private float _rowsTimer;
    private int _parkedCount, _roadCount, _brokenCount;
    private float _onTimePct = -1f;
    private BusRouteData[] _routes = new BusRouteData[0];

    private class Cand { public int busID, fleet; public string series; public bool ok; public string reason; }

    // ── Styles ───────────────────────────────────────────────────────────────
    private bool _styles;
    private GUIStyle _h1, _h2, _body, _dim, _bold, _btn, _btnPrimary, _btnGhost, _field, _chip, _chipOn, _good, _bad, _wrap;
    private Texture2D _tPanel, _tRow, _tRowHover, _tAccent, _tLight, _tGhost, _tLine;

    private static readonly Color CPanel  = new Color(0.04f, 0.17f, 0.19f, 0.98f);
    private static readonly Color CRow    = new Color(0.07f, 0.25f, 0.27f, 1f);
    private static readonly Color CRowHi  = new Color(0.10f, 0.33f, 0.35f, 1f);
    private static readonly Color CAccent = new Color(1f, 0.80f, 0.22f, 1f);
    private static readonly Color CText   = new Color(0.95f, 0.99f, 0.99f, 1f);
    private static readonly Color CDim    = new Color(0.66f, 0.82f, 0.83f, 1f);
    private static readonly Color CGood   = new Color(0.55f, 0.95f, 0.70f, 1f);
    private static readonly Color CBad    = new Color(1f, 0.55f, 0.50f, 1f);

    private static Texture2D Solid(Color c)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        t.SetPixel(0, 0, c); t.Apply(); return t;
    }

    private void EnsureStyles()
    {
        if (_styles) return;
        _styles = true;
        _tPanel = Solid(CPanel); _tRow = Solid(CRow); _tRowHover = Solid(CRowHi); _tAccent = Solid(CAccent);
        _tLight = Solid(new Color(0.93f, 0.98f, 0.98f, 1f)); _tGhost = Solid(new Color(1f, 1f, 1f, 0.10f)); _tLine = Solid(new Color(1f, 1f, 1f, 0.12f));

        GUIStyle L(int size, FontStyle fs, Color col, TextAnchor a = TextAnchor.MiddleLeft)
        {
            var s = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = fs, alignment = a, clipping = TextClipping.Clip, wordWrap = false };
            s.normal.textColor = col; return s;
        }
        _h1 = L(20, FontStyle.Bold, CText); _h2 = L(15, FontStyle.Bold, CText); _body = L(13, FontStyle.Normal, CText);
        _dim = L(12, FontStyle.Normal, CDim); _bold = L(13, FontStyle.Bold, CText);
        _good = L(12, FontStyle.Bold, CGood); _bad = L(12, FontStyle.Normal, CBad);
        _wrap = L(12, FontStyle.Normal, CDim, TextAnchor.UpperLeft); _wrap.wordWrap = true; _wrap.clipping = TextClipping.Overflow;

        GUIStyle B(Texture2D bg, Color text, int size = 13)
        {
            var s = new GUIStyle(GUI.skin.button) { fontSize = size, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            s.normal.background = bg; s.hover.background = bg; s.active.background = bg; s.focused.background = bg;
            s.normal.textColor = text; s.hover.textColor = text; s.active.textColor = text; s.focused.textColor = text;
            s.border = new RectOffset(0, 0, 0, 0); return s;
        }
        _btn = B(_tLight, new Color(0.04f, 0.2f, 0.22f)); _btnPrimary = B(_tAccent, new Color(0.25f, 0.16f, 0f));
        _btnGhost = B(_tGhost, CText); _chip = B(_tGhost, CDim, 12); _chipOn = B(_tAccent, new Color(0.25f, 0.16f, 0f), 12);

        _field = new GUIStyle(GUI.skin.textField) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
        _field.normal.textColor = CText; _field.focused.textColor = CText; _field.normal.background = _tRow; _field.focused.background = _tRowHover;
    }

    private void Awake() { ManagerScore.Earned += OnEarned; ManagerActions.Info += Note; }
    private void OnDestroy() { ManagerScore.Earned -= OnEarned; ManagerActions.Info -= Note; }
    private void OnEarned(string text) { Note(text, true); }

    private bool Show => GameMode.IsManage && !LoadingScreen.IsShowing && !(MainMenu.Instance != null && MainMenu.Instance.IsOpen);

    // ═════════════════════════════════════════════════════════════════════════
    //  UPDATE — data refresh, keys, map clicks
    // ═════════════════════════════════════════════════════════════════════════
    private void Update()
    {
        if (!GameMode.IsManage) { _swapFirst = -1; _handBus = -1; _pickTrip = null; _replaceBroken = -1; return; }

        if (_pendingGood != null || _pendingWarn != null)
        {
            if (_pendingGood != null) Note(_pendingGood, true);
            if (_pendingWarn != null) Note(_pendingWarn, false);
            _pendingGood = _pendingWarn = null;
        }

        if (!Show) return;

        if (Input.GetKeyDown(KeyCode.F10))
        {
            ManagerLayout.ForceTouch = !ManagerLayout.ForceTouch;
            Note(ManagerLayout.ForceTouch ? "Touch layout on (F10 turns it off)." : "Touch layout off.", true);
        }
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (_marginsOpen) { ManagerLayout.SaveMargins(); _marginsOpen = false; }
            else if (_pickTrip != null || _replaceBroken >= 0 || _swapFirst >= 0 || _handBus >= 0)
            { _pickTrip = null; _replaceBroken = -1; _swapFirst = -1; _handBus = -1; }
            else MainMenu.Instance?.Open();
        }
        if (Input.GetKeyDown(KeyCode.Z) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.RightCommand)))
            DoUndo();

        _rowsTimer -= Time.unscaledDeltaTime;
        if (_rowsTimer <= 0f) { _rowsTimer = 0.5f; RebuildRows(); }
        _tripsTimer -= Time.unscaledDeltaTime;
        if (_tripsTimer <= 0f) { _tripsTimer = 0.6f; RebuildTrips(); }
        _trkTimer -= Time.unscaledDeltaTime;
        if (_trkTimer <= 0f) { _trkTimer = 1f; if (_tab == Tab.Tracker) RebuildTracker(); }

        // Clicks on the map
        var map = MDT_LiveMap.Instance;
        if (map != null)
        {
            map.ManagerConsumeClick(out int clickedBus, out string clickedStop);
            if (clickedBus != int.MinValue) OnMapBus(clickedBus);
            else if (clickedStop != null) OpenTrackerStop(clickedStop);
            map.ManagerSelectedBus = _selBus;
        }
    }

    private string _stopPick;

    private void Note(string text, bool good) { _note = text; _noteGood = good; _noteAt = Time.realtimeSinceStartup; }
    private void Report(ActionResult r) { Note(r.message, r.ok); RebuildRows(); RebuildTrips(); }

    private void RebuildRows()
    {
        _rows.Clear(); _runCount.Clear(); _parkedCount = _roadCount = _brokenCount = 0;
        var bm = BusManager.Instance; var sched = BusScheduler.Instance;
        if (bm == null || sched == null) return;
        _routes = sched.managedRoutes != null
            ? sched.managedRoutes.Where(r => r != null).OrderBy(r => LeadingInt(r.routeNumber)).ThenBy(r => r.routeNumber).ToArray()
            : new BusRouteData[0];

        int late = 0, tracked = 0;
        foreach (var rec in bm.GetAllRecords())
        {
            var c = rec.controller; if (c == null) continue;
            bool broken = BusBreakdownSystem.Instance != null && BusBreakdownSystem.Instance.IsBusLocked(rec.busID);
            bool parked = ManagerPlan.IsParked(rec);
            string route = "";
            if (sched.TryGetAssignedSlot(rec.busID, out var slot) && slot != null && slot.state != SlotState.Completed) route = BusRouteData.RouteLabel(slot.routeNumber, slot.variantLetter);
            else if (c.CurrentRoute != null && !parked) route = BusRouteData.RouteLabel(c.CurrentRoute.routeNumber, c.variantLetter);

            var meta = FleetMetadata.Get(c.fleetNumber);
            var row = new BusRow
            {
                busID = rec.busID, fleet = c.fleetNumber, series = meta != null ? meta.seriesName : "",
                depot = meta != null && meta.homeDepot != null ? meta.homeDepot.depotName : "",
                state = ManagerWords.BusState(c, broken), route = route, parked = parked, broken = broken,
                onRoad = !parked && c.State == NPCBusController.BusState.InService,
                held = c.ManagerHold, fix = ManagerLocks.IsBusLocked(rec.busID), ctrl = c,
            };
            _rows.Add(row);
            if (parked) _parkedCount++;
            if (row.onRoad || (!parked && !string.IsNullOrEmpty(route))) _roadCount++;
            if (broken) _brokenCount++;
            if (row.onRoad && !string.IsNullOrEmpty(route))
            {
                string baseRoute = DepotData.StripVariantLetter(route);
                _runCount[baseRoute] = (_runCount.TryGetValue(baseRoute, out int n) ? n : 0) + 1;
                tracked++; if (Mathf.Abs(sched.GetLatenessMinutes(rec.busID)) <= 5f) late++;
            }
        }
        _rows.Sort((a, b) => a.fleet.CompareTo(b.fleet));
        _onTimePct = tracked > 0 ? 100f * late / tracked : -1f;
    }

    private void RebuildTrips()
    {
        var sched = BusScheduler.Instance;
        if (sched == null || SimClock.Instance == null || _routes.Length == 0) { _trips.Clear(); return; }
        _planRouteIdx = Mathf.Clamp(_planRouteIdx, 0, _routes.Length - 1);
        int day = SimClock.Instance.GameDayNumber + _dayOffset;
        _trips = sched.ManagerTripsFor(_routes[_planRouteIdx].routeNumber, day);
    }

    private static int LeadingInt(string s)
    {
        var m = Regex.Match(s ?? "", @"^\d+");
        return m.Success ? int.Parse(m.Value) : int.MaxValue;
    }

    private void OnMapBus(int busID)
    {
        if (busID < 0) { if (_swapFirst < 0) _selBus = -1; return; }
        if (_swapFirst >= 0) { DoSwap(_swapFirst, busID); return; }
        SelectBus(busID, false);
    }

    private void SelectBus(int busID, bool centre)
    {
        _selBus = busID;
        if (_compact) _panelOpen = false; // narrow screen: go and look at it
        var row = _rows.FirstOrDefault(r => r.busID == busID);
        if (row != null && !string.IsNullOrEmpty(row.route) && MDT_LiveMap.Instance != null)
            MDT_LiveMap.Instance.ManagerFocusRoute = DepotData.StripVariantLetter(row.route);
        if (centre && MDT_LiveMap.Instance != null && MDT_LiveMap.Instance.ManagerFindBus(busID, out var pos))
            MDT_LiveMap.Instance.ManagerCenterOn(pos);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ACTIONS
    // ═════════════════════════════════════════════════════════════════════════
    private void DoUndo() => Report(ManagerActions.Undo());

    private void DoSwap(int a, int b)
    {
        _swapFirst = -1;
        Report(ManagerActions.SwapBuses(a, b));
    }

    private void OpenPicker(TimetableSlot trip)
    {
        _pickTrip = trip; _replaceBroken = -1; _scrollPick = Vector2.zero;
        var sched = BusScheduler.Instance;
        _cands = BuildCands(sched.GetRouteData(trip.routeNumber), trip.variantLetter, trip.scheduledDeparture, sched.ManagerChainWindow(trip), true);
    }

    private void OpenReplacePicker(BusRow broken)
    {
        _replaceBroken = broken.busID; _pickTrip = null; _scrollPick = Vector2.zero;
        float nowAbs = SimClock.Instance != null ? SimClock.Instance.AbsoluteGameMinutes : 0f;
        string rn = DepotData.StripVariantLetter(broken.route);
        var route = BusScheduler.Instance.GetRouteData(rn);
        string variant = broken.route.Length > rn.Length ? broken.route.Substring(rn.Length) : "";
        float oneWay = route != null ? route.oneWayTripMinutes : 40f;
        _cands = BuildCands(route, variant, nowAbs, oneWay * 2f + 10f, false);
    }

    private List<Cand> BuildCands(BusRouteData route, string variant, float absStart, float window, bool checkLimit)
    {
        var list = new List<Cand>();
        var busyIndex = BusScheduler.Instance.ManagerBusyIndex();
        float minuteOfDay = absStart % 1440f;
        foreach (var r in _rows)
        {
            if (!r.parked) continue;
            var c = new Cand { busID = r.busID, fleet = r.fleet, series = r.series };
            c.ok = RouteRules.Check(r.fleet, route, variant, minuteOfDay, out c.reason, checkLimit);
            // Busy only counts if it overlaps this trip and the round trips that follow it, not the rest of the day.
            if (c.ok && !BusScheduler.ManagerBusFreeBetween(busyIndex, r.busID, absStart - 1f, absStart + window, out string clash))
            { c.ok = false; c.reason = "Not free then. " + clash; }
            list.Add(c);
        }
        list.Sort((a, b) => a.ok != b.ok ? (a.ok ? -1 : 1) : a.fleet.CompareTo(b.fleet));
        return list;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DRAW
    // ═════════════════════════════════════════════════════════════════════════
    private void OnGUI()
    {
        if (!Show) return;
        EnsureStyles();
        GUI.depth = 100;

        // Everything is drawn in "virtual" units and scaled up as a whole, so the same layout works on a phone.
        float ui = ManagerLayout.Scale;
        var oldMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(ui, ui, 1f));

        _vw = Screen.width / ui; _vh = Screen.height / ui;
        float VW = _vw, VH = _vh;
        Rect sa = Screen.safeArea; // keeps clear of notches and rounded corners
        float sl = sa.x / ui, sr = (Screen.width - sa.xMax) / ui, st = (Screen.height - sa.yMax) / ui, sb = sa.y / ui;
        // The device's safe area plus the margins you set (Screen button in the top bar).
        sl += ManagerLayout.MarginLeft; sr += ManagerLayout.MarginRight; st += ManagerLayout.MarginTop; sb += ManagerLayout.MarginBottom;
        var safe = new Rect(sl, st, Mathf.Max(200f, VW - sl - sr), Mathf.Max(200f, VH - st - sb));

        _compact = safe.width < 760f;
        float leftW = _compact ? (_panelOpen ? safe.width : 0f) : Mathf.Clamp(safe.width * 0.34f, 340f, 460f);

        var topRect  = new Rect(safe.x, safe.y, safe.width, TopH);
        var leftRect = new Rect(safe.x, safe.y + TopH, leftW, safe.height - TopH);
        var mapRect  = new Rect(safe.x + leftW, safe.y + TopH, safe.width - leftW, safe.height - TopH);
        bool mapVisible = mapRect.width > 40f;

        var live = MDT_LiveMap.Instance;
        if (live != null)
        {
            live.ManagerPixelScale = ui;
            live.ManagerTouchMode = ManagerLayout.Touch;
            if (!mapVisible) live.ManagerArea = Rect.zero;
        }

        // Painting: the map goes first so the panels sit on top of it.
        // Clicks: the panels and cards go first so they get the click before the map does.
        if (Event.current.type == EventType.Repaint)
        {
            if (mapVisible) live?.ManagerDraw(mapRect);
            DrawOverlays(topRect, leftRect, mapRect, mapVisible);
        }
        else
        {
            DrawOverlays(topRect, leftRect, mapRect, mapVisible);
            if (mapVisible) live?.ManagerDraw(mapRect);
        }

        GUI.matrix = oldMatrix;
    }

    private void DrawOverlays(Rect topRect, Rect leftRect, Rect mapRect, bool mapVisible)
    {
        if (_marginsOpen) { DrawMarginEditor(topRect, leftRect, mapRect); return; }
        DrawTop(topRect);
        if (leftRect.width > 0f) DrawLeft(leftRect);
        if (!mapVisible) return;
        DrawBusCard(mapRect);
        DrawMapButtons(mapRect);
        DrawNote(mapRect);
        if (_swapFirst >= 0)
            Banner(mapRect, "Pick the bus to swap with — tap it on the map or in the Buses list.");
        else if (_handBus >= 0)
            Banner(mapRect, "Pick a trip for this bus in the Plan tab.");
    }

    // ── Screen margins: how much of each edge to keep clear ─────────────────
    private void DrawMarginEditor(Rect topRect, Rect leftRect, Rect mapRect)
    {
        // The screen edge and the area the manager is using, so you can see what you are setting.
        float W = _vw, H = _vh;
        var usable = new Rect(ManagerLayout.MarginLeft, ManagerLayout.MarginTop, W - ManagerLayout.MarginLeft - ManagerLayout.MarginRight, H - ManagerLayout.MarginTop - ManagerLayout.MarginBottom);
        Fill(new Rect(0, 0, W, H), Solid(new Color(0.03f, 0.10f, 0.11f, 0.94f)));
        Fill(new Rect(usable.x, usable.y, usable.width, 2), _tAccent); Fill(new Rect(usable.x, usable.yMax - 2, usable.width, 2), _tAccent);
        Fill(new Rect(usable.x, usable.y, 2, usable.height), _tAccent); Fill(new Rect(usable.xMax - 2, usable.y, 2, usable.height), _tAccent);

        float bw = Mathf.Min(420f, usable.width - 40f);
        float bh = 330f;
        var box = new Rect(usable.x + (usable.width - bw) * 0.5f, usable.y + Mathf.Max(20f, (usable.height - bh) * 0.5f), bw, bh);
        Fill(box, _tPanel); Fill(new Rect(box.x, box.y, box.width, 3), _tAccent);
        GUI.Label(new Rect(box.x + 14, box.y + 8, box.width - 28, 26), "Screen margins", _h1);
        GUI.Label(new Rect(box.x + 14, box.y + 36, box.width - 28, 36), "Keep each edge clear by this much, on top of the device's own safe area. The gold line is the edge of what the manager will use.", _wrap);

        float y = box.y + 82f;
        void Slider(string name, float value, Action<float> set)
        {
            GUI.Label(new Rect(box.x + 14, y, 70, 30), name, _body);
            float nv = GUI.HorizontalSlider(new Rect(box.x + 88, y + 9, box.width - 88 - 70, 20), value, 0f, ManagerLayout.MaxMargin);
            nv = Mathf.Round(nv);
            if (!Mathf.Approximately(nv, value)) set(nv);
            GUI.Label(new Rect(box.xMax - 62, y, 50, 30), Mathf.RoundToInt(value).ToString(), new GUIStyle(_bold) { alignment = TextAnchor.MiddleRight });
            y += 40f;
        }
        Slider("Left",   ManagerLayout.MarginLeft,   v => ManagerLayout.MarginLeft = v);
        Slider("Right",  ManagerLayout.MarginRight,  v => ManagerLayout.MarginRight = v);
        Slider("Top",    ManagerLayout.MarginTop,    v => ManagerLayout.MarginTop = v);
        Slider("Bottom", ManagerLayout.MarginBottom, v => ManagerLayout.MarginBottom = v);

        float half = (box.width - 14f * 2 - 8f) / 2f;
        if (GUI.Button(new Rect(box.x + 14, box.yMax - 46, half, 34), "Reset", _btnGhost))
        { ManagerLayout.MarginLeft = ManagerLayout.MarginRight = ManagerLayout.MarginTop = ManagerLayout.MarginBottom = 0f; ManagerLayout.SaveMargins(); }
        if (GUI.Button(new Rect(box.x + 14 + half + 8f, box.yMax - 46, half, 34), "Done", _btnPrimary))
        { ManagerLayout.SaveMargins(); _marginsOpen = false; }
    }

    // Zoom buttons for touch (and handy with a mouse too).
    private void DrawMapButtons(Rect map)
    {
        var live = MDT_LiveMap.Instance;
        if (live == null) return;
        float s = ManagerLayout.Touch ? 48f : 40f;
        float x = map.xMax - s - 10f, y = map.y + map.height * 0.5f - (s * 3f + 12f) * 0.5f;
        if (GUI.Button(new Rect(x, y, s, s), "+", _btn)) live.ManagerZoomBy(1.45f);
        if (GUI.Button(new Rect(x, y + s + 6f, s, s), "−", _btn)) live.ManagerZoomBy(1f / 1.45f);
        if (GUI.Button(new Rect(x, y + (s + 6f) * 2f, s, s), "Fit", _btnGhost)) live.ManagerFitAll();
    }

    private void Fill(Rect r, Texture2D t) { var p = GUI.color; GUI.color = Color.white; GUI.DrawTexture(r, t); GUI.color = p; }

    private void Banner(Rect map, string text)
    {
        var r = new Rect(map.x + 16, map.y + 12, map.width - 32, 34);
        Fill(r, _tAccent);
        GUI.Label(new Rect(r.x + 12, r.y, r.width - 24, r.height), text, new GUIStyle(_bold) { normal = { textColor = new Color(0.25f, 0.16f, 0f) } });
    }

    private void DrawNote(Rect map)
    {
        if (string.IsNullOrEmpty(_note) || Time.realtimeSinceStartup - _noteAt > 7f) return;
        float nw = Mathf.Min(560f, map.width - 24f);
        var r = new Rect(map.x + map.width * 0.5f - nw * 0.5f, map.yMax - 54, nw, 38);
        Fill(r, _tPanel);
        Fill(new Rect(r.x, r.y, 4, r.height), _noteGood ? Solid(CGood) : Solid(CBad));
        GUI.Label(new Rect(r.x + 14, r.y, r.width - 20, r.height), _note, _noteGood ? _good : _bad);
    }

    // ── Top bar ──────────────────────────────────────────────────────────────
    private void DrawTop(Rect t)
    {
        Fill(t, _tPanel);
        string clock = SimClock.Instance != null ? SimClock.Instance.GameTimeString : "--:--";
        float bh = ManagerLayout.Touch ? 38f : 32f, by = t.y + (TopH - bh) * 0.5f;

        if (_compact)
        {
            // Narrow screen: a switch between the map and the panel, the clock, Undo and Menu.
            if (GUI.Button(new Rect(t.x + 8, by, 74, bh), _panelOpen ? "Map" : "Panel", _btnPrimary)) _panelOpen = !_panelOpen;
            if (t.width >= 330f) GUI.Label(new Rect(t.x + 88, t.y, 64, TopH), clock, _h2);
            if (GUI.Button(new Rect(t.xMax - 206, by, 64, bh), "Screen", _btnGhost)) _marginsOpen = true;
            GUI.enabled = ManagerActions.CanUndo;
            if (GUI.Button(new Rect(t.xMax - 138, by, 62, bh), "Undo", _btnGhost)) DoUndo();
            GUI.enabled = true;
            if (GUI.Button(new Rect(t.xMax - 70, by, 62, bh), "Menu", _btn)) MainMenu.Instance?.Open();
            return;
        }

        GUI.Label(new Rect(t.x + 16, t.y, 260, TopH), ManagerWords.ModeName, _h1);
        GUI.Label(new Rect(t.x + t.width * 0.5f - 330, t.y, 120, TopH), clock, _h2);
        GUI.Label(new Rect(t.x + t.width * 0.5f - 210, t.y, 520, TopH),
            $"{ManagerWords.OnRoad}: {_roadCount}    {ManagerWords.Parked}: {_parkedCount}    {ManagerWords.Broken}: {_brokenCount}", _body);

        // Same level, XP and points as the main menu.
        var pm = PointsManager.Instance;
        if (pm != null)
            GUI.Label(new Rect(t.xMax - 330 - 260 - 330, t.y, 322, TopH),
                $"Level {pm.level}   ·   {pm.lifetimePoints:N0} points" + (ManagerScore.SessionPoints > 0 ? $"   ·   +{ManagerScore.SessionPoints} today" : ""),
                new GUIStyle(_body) { alignment = TextAnchor.MiddleRight });

        if (GUI.Button(new Rect(t.xMax - 330, by, 74, bh), "Screen", _btnGhost)) _marginsOpen = true;
        GUI.enabled = ManagerActions.CanUndo;
        if (GUI.Button(new Rect(t.xMax - 250, by, 120, bh), "Undo", _btnGhost)) DoUndo();
        GUI.enabled = true;
        if (GUI.Button(new Rect(t.xMax - 120, by, 104, bh), "Menu", _btn)) MainMenu.Instance?.Open();
        if (ManagerActions.CanUndo)
            GUI.Label(new Rect(t.xMax - 330 - 260, t.y, 252, TopH), ManagerActions.NextUndoLabel, new GUIStyle(_dim) { alignment = TextAnchor.MiddleRight });
    }

    // ── Left panel ───────────────────────────────────────────────────────────
    private void DrawLeft(Rect r)
    {
        Fill(r, _tPanel);
        float tabW = (r.width - 24f) / 5f;
        string[] names = { "Routes", "Buses", "Plan", "Tracker", "Events" };
        for (int i = 0; i < 5; i++)
        {
            string label = names[i] + (i == 4 && _brokenCount > 0 ? $" ({_brokenCount})" : "");
            if (GUI.Button(new Rect(r.x + 12 + i * tabW, r.y + 10, tabW - 4, 30), label, _tab == (Tab)i ? _chipOn : _chip))
            { _tab = (Tab)i; _pickTrip = null; _replaceBroken = -1; }
        }
        var body = new Rect(r.x + 12, r.y + 50, r.width - 24, r.height - 58);
        switch (_tab)
        {
            case Tab.Routes: DrawRoutes(body); break;
            case Tab.Buses:  DrawBuses(body);  break;
            case Tab.Plan:   DrawPlan(body);   break;
            case Tab.Tracker: DrawTracker(body); break;
            case Tab.Events: DrawEvents(body); break;
        }
    }

    // Generic scrolling list that only draws the rows on screen.
    private bool _dragActive, _dragMoved;
    private Rect _dragArea;
    private Vector2 _dragStartMouse;
    private float _dragStartScrollY;

    private void List(Rect area, ref Vector2 scroll, int count, float rowH, Action<int, Rect> drawRow)
    {
        float contentH = Mathf.Max(area.height, count * rowH);

        // Touch: drag the list with a finger. A drag must not also "press" the row under it.
        if (ManagerLayout.Touch)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && area.Contains(e.mousePosition))
            { _dragActive = true; _dragMoved = false; _dragStartMouse = e.mousePosition; _dragStartScrollY = scroll.y; _dragArea = area; }
            else if (_dragActive && _dragArea == area && e.type == EventType.MouseDrag)
            {
                float dy = e.mousePosition.y - _dragStartMouse.y;
                if (!_dragMoved && Mathf.Abs(dy) > 10f) _dragMoved = true;
                if (_dragMoved) { scroll.y = Mathf.Clamp(_dragStartScrollY - dy, 0f, Mathf.Max(0f, contentH - area.height)); e.Use(); }
            }
            else if (_dragActive && _dragArea == area && e.type == EventType.MouseUp)
            {
                if (_dragMoved) { e.Use(); GUIUtility.hotControl = 0; }
                _dragActive = false; _dragMoved = false;
            }
        }

        var view = new Rect(0, 0, area.width - 16, contentH);
        scroll = GUI.BeginScrollView(area, scroll, view);
        int first = Mathf.Max(0, (int)(scroll.y / rowH) - 1);
        int last = Mathf.Min(count, first + (int)(area.height / rowH) + 3);
        for (int i = first; i < last; i++) drawRow(i, new Rect(0, i * rowH, view.width, rowH - 3));
        GUI.EndScrollView();
    }

    private bool RowButton(Rect r, bool selected)
    {
        bool hover = r.Contains(Event.current.mousePosition);
        Fill(r, selected ? _tRowHover : (hover ? _tRowHover : _tRow));
        if (selected) Fill(new Rect(r.x, r.y, 3, r.height), _tAccent);
        return GUI.Button(r, GUIContent.none, GUIStyle.none);
    }

    // ── Routes tab ───────────────────────────────────────────────────────────
    private void DrawRoutes(Rect a)
    {
        GUI.Label(new Rect(a.x, a.y, a.width, 24), $"{_routes.Length} routes", _dim);
        float rulesH = _selRoute != null ? 168f : 0f;
        var list = new Rect(a.x, a.y + 26, a.width, a.height - 28 - rulesH);

        List(list, ref _scrollRoutes, _routes.Length, 46f, (i, r) =>
        {
            var rt = _routes[i];
            bool sel = _selRoute == rt;
            if (RowButton(r, sel))
            {
                _selRoute = sel ? null : rt;
                if (MDT_LiveMap.Instance != null) MDT_LiveMap.Instance.ManagerFocusRoute = _selRoute != null ? _selRoute.routeNumber : "";
            }
            var chip = new Rect(r.x + 8, r.y + 8, 44, r.height - 16);
            Fill(chip, Solid(rt.routeColor));
            GUI.Label(chip, rt.routeNumber, new GUIStyle(_bold) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Lum(rt.routeColor) > 0.6f ? Color.black : Color.white } });
            GUI.Label(new Rect(r.x + 60, r.y + 2, r.width - 150, 22), string.IsNullOrEmpty(rt.routeName) ? "Route " + rt.routeNumber : rt.routeName, _bold);
            string dest = $"{rt.destinationNameOutbound}";
            GUI.Label(new Rect(r.x + 60, r.y + 22, r.width - 150, 18), dest, _dim);
            int running = _runCount.TryGetValue(rt.routeNumber, out int n) ? n : 0;
            GUI.Label(new Rect(r.xMax - 92, r.y, 86, r.height), $"{running} of {rt.maxBusesAllowed}", new GUIStyle(_body) { alignment = TextAnchor.MiddleRight });
        });

        if (_selRoute == null) return;
        var box = new Rect(a.x, a.yMax - rulesH + 4, a.width, rulesH - 4);
        Fill(box, _tRow);
        GUI.Label(new Rect(box.x + 10, box.y + 6, box.width - 20, 22), $"Route {_selRoute.routeNumber} rules", _h2);
        string rules = string.Join("\n", RouteRules.Summary(_selRoute).Select(s => "• " + s));
        GUI.Label(new Rect(box.x + 10, box.y + 30, box.width - 20, box.height - 72), rules, _wrap);
        if (GUI.Button(new Rect(box.x + 10, box.yMax - 38, 150, 30), "Plan this route", _btnPrimary))
        {
            _planRouteIdx = Array.IndexOf(_routes, _selRoute); if (_planRouteIdx < 0) _planRouteIdx = 0;
            _tab = Tab.Plan; _panelOpen = true; RebuildTrips();
        }
        if (GUI.Button(new Rect(box.x + 170, box.yMax - 38, 120, 30), "Show on map", _btnGhost)) MDT_LiveMap.Instance?.ManagerFitAll();
    }

    private static float Lum(Color c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;

    // ── Buses tab ────────────────────────────────────────────────────────────
    private void DrawBuses(Rect a)
    {
        string[] f = { "All", "Parked", "On the road", "Broken" };
        float cw = (a.width) / 4f;
        for (int i = 0; i < 4; i++)
            if (GUI.Button(new Rect(a.x + i * cw, a.y, cw - 4, 26), f[i], _busFilter == i ? _chipOn : _chip)) _busFilter = i;
        _search = GUI.TextField(new Rect(a.x, a.y + 32, a.width, 28), _search, 6, _field);
        if (string.IsNullOrEmpty(_search)) GUI.Label(new Rect(a.x + 8, a.y + 32, a.width, 28), "Type a bus number", _dim);

        var shown = _rows.Where(r =>
            (_busFilter == 0 || (_busFilter == 1 && r.parked) || (_busFilter == 2 && r.onRoad) || (_busFilter == 3 && r.broken)) &&
            (string.IsNullOrEmpty(_search) || r.fleet.ToString().StartsWith(_search))).ToList();

        var list = new Rect(a.x, a.y + 66, a.width, a.height - 68);
        List(list, ref _scrollBuses, shown.Count, 48f, (i, r) =>
        {
            var b = shown[i];
            if (RowButton(r, b.busID == _selBus))
            {
                if (_swapFirst >= 0) DoSwap(_swapFirst, b.busID);
                else SelectBus(b.busID, true);
            }
            GUI.Label(new Rect(r.x + 10, r.y + 2, 70, 24), b.fleet.ToString(), _h2);
            GUI.Label(new Rect(r.x + 74, r.y + 4, r.width - 150, 20), b.series, _body);
            string line = b.state + (string.IsNullOrEmpty(b.route) ? "" : "  ·  Route " + b.route);
            GUI.Label(new Rect(r.x + 10, r.y + 24, r.width - 20, 20), line, b.broken ? _bad : _dim);
            string tag = b.held ? "HELD" : b.fix ? "FIXED" : "";
            if (tag != "") GUI.Label(new Rect(r.xMax - 70, r.y + 2, 62, 22), tag, new GUIStyle(_good) { alignment = TextAnchor.MiddleRight, normal = { textColor = CAccent } });
        });
    }

    // ── Plan tab ─────────────────────────────────────────────────────────────
    private void DrawPlan(Rect a)
    {
        if (_routes.Length == 0 || BusScheduler.Instance == null) { GUI.Label(a, "The timetable isn't ready yet.", _dim); return; }
        _planRouteIdx = Mathf.Clamp(_planRouteIdx, 0, _routes.Length - 1);
        var route = _routes[_planRouteIdx];
        var sched = BusScheduler.Instance;

        // Route and day pickers
        if (GUI.Button(new Rect(a.x, a.y, 34, 30), "<", _btnGhost)) { _planRouteIdx = (_planRouteIdx + _routes.Length - 1) % _routes.Length; _pickTrip = null; RebuildTrips(); }
        if (GUI.Button(new Rect(a.xMax - 34, a.y, 34, 30), ">", _btnGhost)) { _planRouteIdx = (_planRouteIdx + 1) % _routes.Length; _pickTrip = null; RebuildTrips(); }
        GUI.Label(new Rect(a.x + 40, a.y, a.width - 80, 30), $"Route {route.routeNumber}  ·  {route.routeName}", new GUIStyle(_h2) { alignment = TextAnchor.MiddleCenter });

        int days = Mathf.Max(1, sched.scheduleDaysAhead);
        float dw = (a.width) / days;
        for (int d = 0; d < days; d++)
            if (GUI.Button(new Rect(a.x + d * dw, a.y + 36, dw - 3, 26), d == 0 ? "Today" : d == 1 ? "Tomorrow" : "+" + d, _dayOffset == d ? _chipOn : _chip))
            { _dayOffset = d; _pickTrip = null; RebuildTrips(); }

        float y = a.y + 68f;

        // Bus in hand
        if (_handBus >= 0)
        {
            var hb = _rows.FirstOrDefault(r => r.busID == _handBus);
            var hr = new Rect(a.x, y, a.width, 30);
            Fill(hr, _tAccent);
            GUI.Label(new Rect(hr.x + 10, hr.y, hr.width - 90, hr.height), _handQueue ? $"Moving #{(hb != null ? hb.fleet : 0)} after its trip: pick a later empty trip" : $"Placing #{(hb != null ? hb.fleet : 0)}: pick an empty trip", new GUIStyle(_bold) { normal = { textColor = new Color(0.25f, 0.16f, 0f) } });
            if (GUI.Button(new Rect(hr.xMax - 74, hr.y + 3, 68, 24), "Cancel", _btn)) _handBus = -1;
            y += 36;
        }

        // Rules in one line each (short)
        var rules = RouteRules.Summary(route);
        var rr = new Rect(a.x, y, a.width, 18 + rules.Count * 15);
        Fill(rr, _tRow);
        GUI.Label(new Rect(rr.x + 8, rr.y + 2, rr.width - 16, 16), "Rules for this route", _dim);
        for (int i = 0; i < rules.Count; i++) GUI.Label(new Rect(rr.x + 8, rr.y + 18 + i * 15, rr.width - 16, 15), "• " + rules[i], _dim);
        y += rr.height + 6;

        // Buttons
        int day = SimClock.Instance.GameDayNumber + _dayOffset;
        if (GUI.Button(new Rect(a.x, y, a.width * 0.5f - 3, 30), "Auto fill this day", _btnPrimary))
        {
            string msg = ManagerPlan.AutoSchedule(route, day, out int placed);
            Note(msg, placed > 0); RebuildRows(); RebuildTrips();
        }
        if (GUI.Button(new Rect(a.x + a.width * 0.5f + 3, y, a.width * 0.5f - 3, 30), "Clear my changes", _btnGhost))
        {
            int n = 0;
            foreach (var t in _trips.ToList())
                if (t.state == SlotState.AssignedNPC && ManagerLocks.IsSlotLocked(t)) { ManagerActions.FreeTrip(t); n++; }
            Note(n > 0 ? "Cleared your changes for this day." : "You haven't changed anything on this day.", n > 0);
            RebuildRows(); RebuildTrips();
        }
        y += 36;

        int saved = ManagerPlan.ForDay(day).Count(e => e.route == route.routeNumber);
        GUI.Label(new Rect(a.x, y, a.width, 18), $"{_trips.Count} trips  ·  {saved} placed by you on this day", _dim);
        y += 20;
        foreach (var p in ManagerPlan.LastProblems.Take(2))
        {
            GUI.Label(new Rect(a.x, y, a.width, 16), "! " + p, _bad);
            y += 16;
        }

        var listArea = new Rect(a.x, y, a.width, a.yMax - y);
        if (_pickTrip != null) { DrawPicker(listArea, true); return; }

        List(listArea, ref _scrollPlan, _trips.Count, 44f, (i, r) =>
        {
            var t = _trips[i];
            Fill(r, _tRow);
            GUI.Label(new Rect(r.x + 8, r.y + 2, 60, 22), ManagerWords.Clock(t.scheduledDeparture % 1440f), _h2);
            GUI.Label(new Rect(r.x + 8, r.y + 22, 70, 18), ManagerWords.Direction(t.isOutbound) + (string.IsNullOrEmpty(t.variantLetter) ? "" : " " + t.variantLetter), _dim);

            string busText; Color tc = CDim;
            if (t.state == SlotState.Unassigned || t.assignedBusID < 0) { busText = "No bus"; tc = CBad; }
            else
            {
                int fleet = BusManager.Instance?.GetRecord(t.assignedBusID)?.controller?.fleetNumber ?? -1;
                string fx = ManagerLocks.IsSlotLocked(t) ? "  ·  Fixed" : "";
                string st = t.state == SlotState.InService ? "On the road" : t.state == SlotState.Completed ? "Done" : t.chainLegIndex > 1 ? $"Round trip {t.chainLegIndex}" : "Waiting to start";
                busText = (fleet >= 0 ? $"Bus {fleet}" : "A bus") + "  ·  " + st + fx; tc = CText;
            }
            GUI.Label(new Rect(r.x + 84, r.y, r.width - 262, r.height), busText, new GUIStyle(_body) { normal = { textColor = tc } });

            if (t.state == SlotState.Unassigned)
            {
                if (_handBus >= 0)
                {
                    if (GUI.Button(new Rect(r.xMax - 84, r.y + 8, 78, 26), "Use here", _btnPrimary))
                    {
                        var res = _handQueue ? ManagerActions.MoveAfterTrip(_handBus, t) : ManagerActions.AssignBus(_handBus, t);
                        if (res.ok) _handBus = -1;
                        Report(res);
                    }
                }
                else if (t.scheduledDeparture > SimClock.Instance.AbsoluteGameMinutes + 1f)
                {
                    if (GUI.Button(new Rect(r.xMax - 84, r.y + 8, 78, 26), "Pick bus", _btn)) OpenPicker(t);
                }
            }
            else if (t.state == SlotState.AssignedNPC)
            {
                if (GUI.Button(new Rect(r.xMax - 172, r.y + 8, 82, 26), "Depart now", _btnPrimary)) Report(ManagerActions.DepartNow(t));
                if (GUI.Button(new Rect(r.xMax - 84, r.y + 8, 78, 26), "Remove bus", _btnGhost)) Report(ManagerActions.FreeTrip(t));
            }
        });
    }

    private void DrawPicker(Rect a, bool forTrip)
    {
        var t = _pickTrip;
        GUI.Label(new Rect(a.x, a.y, a.width - 90, 24),
            forTrip ? $"Pick a parked bus for {ManagerWords.Clock(t.scheduledDeparture % 1440f)} ({ManagerWords.Direction(t.isOutbound)})" : "Pick a replacement", _h2);
        if (GUI.Button(new Rect(a.xMax - 80, a.y, 80, 24), "Back", _btnGhost)) { _pickTrip = null; _replaceBroken = -1; return; }
        var list = new Rect(a.x, a.y + 30, a.width, a.height - 30);
        if (_cands.Count == 0) { GUI.Label(list, "There are no parked buses.", _dim); return; }

        List(list, ref _scrollPick, _cands.Count, 54f, (i, r) =>
        {
            var c = _cands[i];
            Fill(r, c.ok ? _tRow : Solid(new Color(0.05f, 0.18f, 0.20f, 1f)));
            GUI.Label(new Rect(r.x + 10, r.y + 2, r.width - 100, 22), $"Bus {c.fleet}   {c.series}", new GUIStyle(_bold) { normal = { textColor = c.ok ? CText : CDim } });
            GUI.Label(new Rect(r.x + 10, r.y + 24, r.width - 100, 28), c.ok ? "Allowed on this route." : c.reason, new GUIStyle(c.ok ? _good : _bad) { wordWrap = true, clipping = TextClipping.Clip, alignment = TextAnchor.UpperLeft });
            GUI.enabled = c.ok;
            if (GUI.Button(new Rect(r.xMax - 82, r.y + 12, 74, 28), "Use", _btnPrimary))
            {
                ActionResult res = forTrip ? ManagerActions.AssignBus(c.busID, t) : ManagerActions.ReplaceBroken(_replaceBroken, c.busID);
                if (res.ok) { _pickTrip = null; _replaceBroken = -1; }
                Report(res);
            }
            GUI.enabled = true;
        });
    }

    // ── Events tab ───────────────────────────────────────────────────────────
    private void DrawEvents(Rect a)
    {
        if (_replaceBroken >= 0) { DrawPicker(a, false); return; }

        var broken = _rows.Where(r => r.broken).ToList();
        var road = RoadEventRegistry.Instance != null ? RoadEventRegistry.Instance.GetAllActiveEvents() : new List<RoadEvent>();

        GUI.Label(new Rect(a.x, a.y, a.width, 24), $"Broken down ({broken.Count})", _h2);
        float y = a.y + 28;
        float brokenH = Mathf.Min(a.height * 0.58f, Mathf.Max(60f, broken.Count * 86f));
        var bArea = new Rect(a.x, y, a.width, brokenH);
        if (broken.Count == 0) GUI.Label(bArea, "Nothing is broken down right now.", _dim);
        else List(bArea, ref _scrollEvents, broken.Count, 86f, (i, r) =>
        {
            var b = broken[i];
            Fill(r, _tRow);
            GUI.Label(new Rect(r.x + 10, r.y + 2, r.width - 20, 22), $"Bus {b.fleet}   ·   {(string.IsNullOrEmpty(b.route) ? "no route" : "Route " + b.route)}", _bold);
            GUI.Label(new Rect(r.x + 10, r.y + 22, r.width - 20, 18), BreakdownText(b.busID), _bad);
            if (GUI.Button(new Rect(r.x + 10, r.y + 48, 134, 28), "Send replacement", _btnPrimary)) OpenReplacePicker(b);
            if (GUI.Button(new Rect(r.x + 152, r.y + 48, 100, 28), "Show on map", _btnGhost)) SelectBus(b.busID, true);
        });

        y += brokenH + 10;
        GUI.Label(new Rect(a.x, y, a.width, 24), $"Road problems ({road.Count})", _h2);
        y += 28;
        var rArea = new Rect(a.x, y, a.width, a.yMax - y);
        if (road.Count == 0) { GUI.Label(rArea, "The roads are clear.", _dim); return; }
        Vector2 sc = Vector2.zero;
        List(rArea, ref _scrollRoadEvents, road.Count, 40f, (i, r) =>
        {
            var ev = road[i];
            Fill(r, _tRow);
            GUI.Label(new Rect(r.x + 10, r.y + 2, r.width - 100, 20), string.IsNullOrEmpty(ev.eventLabel) ? PlainWords(ev.eventType.ToString()) : ev.eventLabel, _bold);
            GUI.Label(new Rect(r.x + 10, r.y + 20, r.width - 100, 18), PlainWords(ev.severity.ToString()) + " problem", _dim);
            if (GUI.Button(new Rect(r.xMax - 92, r.y + 7, 84, 26), "Show", _btnGhost)) MDT_LiveMap.Instance?.ManagerCenterOn(ev.eventWorldPosition);
        });
    }
    private Vector2 _scrollRoadEvents;

    private string BreakdownText(int busID)
    {
        var sys = BusBreakdownSystem.Instance;
        if (sys == null) return "Broken down";
        var parts = new List<string>();
        foreach (var bd in sys.GetBreakdownInfo(busID))
        {
            string name = PlainWords(bd.type.ToString());
            string when = bd.requiresReplacement ? "needs a replacement" : $"fixes itself in about {Mathf.CeilToInt(bd.remainingMinutes)} min";
            parts.Add($"{name} ({when})");
        }
        return parts.Count > 0 ? string.Join(", ", parts) : "Broken down";
    }

    private static string PlainWords(string pascal)
    {
        string s = Regex.Replace(pascal ?? "", "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;
    }

    // ── Selected bus card ────────────────────────────────────────────────────
    private string LateWords(int busID)
    {
        var sched = BusScheduler.Instance;
        if (sched == null || !sched.TryGetAssignedSlot(busID, out var slot) || slot == null || slot.state != SlotState.InService) return "";
        int m = Mathf.RoundToInt(slot.latenessMinutes);
        return m >= 1 ? $"{m} min late" : m <= -1 ? $"{-m} min early" : "On time";
    }

    private void DrawBusCard(Rect map)
    {
        if (_selBus < 0) return;
        var b = _rows.FirstOrDefault(r => r.busID == _selBus);
        if (b == null) { _selBus = -1; return; }
        var live = MDT_LiveMap.Instance;
        bool following = live != null && live.ManagerFollowBus == b.busID;
        var sched = BusScheduler.Instance;
        TimetableSlot slot = null;
        if (sched != null) sched.TryGetAssignedSlot(b.busID, out slot);
        var meta = FleetMetadata.Get(b.fleet);
        bool offService = b.ctrl != null && (b.ctrl.State == NPCBusController.BusState.DepotIngress
            || b.ctrl.State == NPCBusController.BusState.DrivingToMaintenanceBay || b.ctrl.State == NPCBusController.BusState.DrivingToFuelStation);

        float cardH = Mathf.Min(map.height - 24f, 452f);
        var card = new Rect(map.x + 14, map.yMax - cardH - 12f, Mathf.Min(460f, map.width - 28f), cardH);
        Fill(card, _tPanel);
        Fill(new Rect(card.x, card.y, card.width, 3), _tAccent);

        // Header: number, series, state
        GUI.Label(new Rect(card.x + 14, card.y + 8, 250, 28), $"#{b.fleet}", _h1);
        string late = offService ? "Not in service" : LateWords(b.busID);
        if (late != "")
            GUI.Label(new Rect(card.xMax - 190, card.y + 8, 150, 28), late, new GUIStyle(late.Contains("late") ? _bad : _good) { alignment = TextAnchor.MiddleRight });
        GUI.Label(new Rect(card.x + 14, card.y + 36, card.width - 28, 18), $"{b.series}   ·   {b.depot}", _dim);
        GUI.Label(new Rect(card.x + 14, card.y + 54, card.width - 28, 18),
            b.state + (b.held ? "   ·   Held" : "") + (b.fix ? "   ·   Fixed" : ""), b.broken ? _bad : _body);
        if (GUI.Button(new Rect(card.xMax - 34, card.y + 8, 26, 26), "×", _btnGhost))
        { _selBus = -1; if (live != null) { live.ManagerFocusRoute = ""; live.ManagerFollowBus = -1; } return; }

        // Facts
        float y = card.y + 78;
        void Fact(string k, string v)
        {
            GUI.Label(new Rect(card.x + 14, y, 110, 18), k, _dim);
            GUI.Label(new Rect(card.x + 124, y, card.width - 138, 18), v, _body);
            y += 18;
        }
        if (!b.parked && !offService && !string.IsNullOrEmpty(b.route) && b.ctrl != null)
        {
            var route = sched != null ? sched.GetRouteData(DepotData.StripVariantLetter(b.route)) : null;
            var variantData = route != null && !string.IsNullOrEmpty(b.ctrl.variantLetter) ? route.GetVariant(b.ctrl.variantLetter) : null;
            string dest = route != null ? route.GetDestinationName(b.ctrl.IsOutbound, variantData) : "";
            Fact("Route", $"{b.route}  ·  {ManagerWords.Direction(b.ctrl.IsOutbound)}" + (string.IsNullOrEmpty(dest) ? "" : "  ·  to " + dest));
        }
        bool tightHeight = card.height < 380f;
        if (!tightHeight && slot != null && slot.state != SlotState.Completed)
        {
            string trip = slot.state == SlotState.InService
                ? $"Round trip {Mathf.Max(1, slot.chainLegIndex)}  ·  left {ManagerWords.Clock((slot.actualDeparture >= 0 ? slot.actualDeparture : slot.scheduledDeparture) % 1440f)} (due {ManagerWords.Clock(slot.scheduledDeparture % 1440f)})"
                : $"Next trip leaves at {ManagerWords.Clock(slot.scheduledDeparture % 1440f)}";
            Fact(slot.state == SlotState.InService ? "Trip" : "Waiting", trip);
        }
        if (b.ctrl != null && !b.parked)
            Fact("On board", $"{b.ctrl.onboardPax} of {(meta != null ? meta.passengerCapacity : 0)} passengers  ·  {Mathf.RoundToInt(b.ctrl.spd)} km/h");
        if (meta != null && !tightHeight)
            Fact("Bus", $"{meta.busType}  ·  {meta.modelYear}  ·  {(meta.isArticulated ? "articulated" : meta.is35Ft ? "35 ft" : "40 ft")}");
        if (ManagerActions.HasPendingMove(b.busID))
        { GUI.Label(new Rect(card.x + 14, y, card.width - 28, 18), "Will move to another route after this trip.", _good); y += 18; }

        // Coming up (the bus's own stop names and times)
        float btnTop = card.yMax - 3 * 36f - 10f;
        if (b.ctrl != null && !b.parked && !offService)
        {
            y += 6;
            GUI.Label(new Rect(card.x + 14, y, card.width - 28, 18), "Coming up", _h2); y += 22;
            int room = Mathf.Max(1, Mathf.FloorToInt((btnTop - y - 4f) / 18f));
            var stops = ((IBusDisplaySource)b.ctrl).GetUpcomingStopsWithEta(Mathf.Min(6, room));
            for (int i = 0; i < stops.Count; i++)
            {
                var st = i == 0 ? _bold : _body;
                GUI.Label(new Rect(card.x + 14, y, card.width - 110, 18), (i == 0 ? "Next:  " : "") + stops[i].stopName, st);
                GUI.Label(new Rect(card.xMax - 96, y, 82, 18), stops[i].etaLabel, new GUIStyle(_bold) { alignment = TextAnchor.MiddleRight });
                y += 18;
            }
        }
        else if (b.parked) GUI.Label(new Rect(card.x + 14, y + 6, card.width - 28, 18), "Parked. Put it on a route to start it.", _dim);

        // Actions, three to a row
        float gap = 8f, bw = (card.width - 28f - gap * 2f) / 3f, bh = 30f;
        float r1 = btnTop, r2 = r1 + bh + 6f, r3 = r2 + bh + 6f;
        Rect B(int col, float yy, int span = 1) => new Rect(card.x + 14 + col * (bw + gap), yy, bw * span + gap * (span - 1), bh);

        if (b.parked)
        {
            if (GUI.Button(B(0, r1, 2), "Put on a route", _btnPrimary)) { _handBus = b.busID; _handQueue = false; _tab = Tab.Plan; _panelOpen = true; _pickTrip = null; }
        }
        else if (b.broken)
        {
            if (GUI.Button(B(0, r1, 2), "Send replacement", _btnPrimary)) { _tab = Tab.Events; _panelOpen = true; OpenReplacePicker(b); }
        }
        else
        {
            if (GUI.Button(B(0, r1), "Hold at next stop", _btnPrimary)) Report(ManagerActions.HoldAtNextStop(b.busID));
            if (GUI.Button(B(1, r1), b.held ? "Let go" : "Hold now", _btn)) Report(ManagerActions.SetHold(b.busID, !b.held));
        }
        if (GUI.Button(B(2, r1), "Swap with…", _btn)) { _swapFirst = b.busID; }

        if (GUI.Button(B(0, r2), following ? "Stop following" : "Follow", following ? _btnPrimary : _btn))
        {
            if (live != null) { live.ManagerFollowBus = following ? -1 : b.busID; if (!following && live.ManagerFindBus(b.busID, out var fp)) live.ManagerCenterOn(fp); }
        }
        if (GUI.Button(B(1, r2), b.fix ? "Unfix" : "Fix", _btnGhost)) Report(ManagerActions.SetFixed(b.busID, !b.fix));
        if (!b.parked && !b.broken && GUI.Button(B(2, r2), "Move after trip…", _btnGhost))
        { _handBus = b.busID; _handQueue = true; _tab = Tab.Plan; _panelOpen = true; _pickTrip = null; }

        var future = sched != null ? sched.ManagerFutureTripsOf(b.busID) : null;
        if (future != null && future.Count > 0)
        {
            if (GUI.Button(B(0, r3, 3), $"Remove {future.Count} coming trip{(future.Count == 1 ? "" : "s")}", _btnGhost)) Report(ManagerActions.FreeTrip(future[0]));
        }
        else if (live != null && GUI.Button(B(0, r3, 1), "Find on map", _btnGhost) && live.ManagerFindBus(b.busID, out var pos)) live.ManagerCenterOn(pos);
    }

    // ── Tracker tab: pick a route, look at its stops, see what's coming ──────
    private int _trkRoute;
    private bool _trkOut = true;
    private string _trkStop;
    private float _trkTimer;
    private Vector2 _scrollTrk;
    private List<BusStopData> _trkStops = new List<BusStopData>();
    private int _trkStopsKey = -1;
    private readonly List<string> _trkLines = new List<string>();
    private readonly List<bool> _trkLineHot = new List<bool>();

    private void OpenTrackerStop(string stopCode)
    {
        _trkStop = stopCode; _tab = Tab.Tracker; _panelOpen = true;
        if (MDT_LiveMap.Instance != null) MDT_LiveMap.Instance.ManagerSelectedStop = stopCode;
        RebuildTracker();
    }

    private void RebuildTracker()
    {
        var tracker = BusTrackerService.Instance;
        if (tracker == null || _routes.Length == 0) return;
        _trkRoute = Mathf.Clamp(_trkRoute, 0, _routes.Length - 1);
        var route = _routes[_trkRoute];

        int key = _trkRoute * 2 + (_trkOut ? 1 : 0);
        if (key != _trkStopsKey)
        {
            _trkStopsKey = key;
            _trkStops = tracker.GetResolvedStopsForVariant(route, _trkOut, "") ?? new List<BusStopData>();
        }

        _trkLines.Clear(); _trkLineHot.Clear();
        if (string.IsNullOrEmpty(_trkStop)) return;
        var groups = tracker.GetArrivalsForStopByCompass(_trkStop);
        if (groups == null) return;
        foreach (var g in groups)
        {
            if (g.arrivals == null || g.arrivals.Count == 0) continue;
            _trkLines.Add(g.label); _trkLineHot.Add(false);
            foreach (var e in g.arrivals.Take(4))
            {
                string pax = e.waitingPax > 0 ? $"  ·  {e.waitingPax} waiting" : "";
                _trkLines.Add($"   Route {BusRouteData.RouteLabel(e.routeNumber, e.variantLetter)}   ·   {e.minutesLabel}   ·   {e.busLabel}{pax}" + (e.isBunched ? "   ·   bunched" : ""));
                _trkLineHot.Add(e.routeNumber == route.routeNumber);
            }
        }
    }

    private void DrawTracker(Rect a)
    {
        if (_routes.Length == 0) { GUI.Label(a, "The timetable isn't ready yet.", _dim); return; }
        _trkRoute = Mathf.Clamp(_trkRoute, 0, _routes.Length - 1);
        var route = _routes[_trkRoute];

        if (GUI.Button(new Rect(a.x, a.y, 34, 30), "<", _btnGhost)) { _trkRoute = (_trkRoute + _routes.Length - 1) % _routes.Length; _trkStopsKey = -1; RebuildTracker(); SetTrackerRouteOnMap(); }
        if (GUI.Button(new Rect(a.xMax - 34, a.y, 34, 30), ">", _btnGhost)) { _trkRoute = (_trkRoute + 1) % _routes.Length; _trkStopsKey = -1; RebuildTracker(); SetTrackerRouteOnMap(); }
        GUI.Label(new Rect(a.x + 40, a.y, a.width - 80, 30), $"Route {route.routeNumber}  ·  {route.routeName}", new GUIStyle(_h2) { alignment = TextAnchor.MiddleCenter });

        float half = (a.width - 4f) / 2f;
        string toZ = string.IsNullOrEmpty(route.destinationNameOutbound) ? "A to Z" : "to " + route.destinationNameOutbound;
        string toA = string.IsNullOrEmpty(route.destinationNameInbound) ? "Z to A" : "to " + route.destinationNameInbound;
        if (GUI.Button(new Rect(a.x, a.y + 36, half, 28), toZ, _trkOut ? _chipOn : _chip)) { _trkOut = true; _trkStopsKey = -1; RebuildTracker(); }
        if (GUI.Button(new Rect(a.x + half + 4f, a.y + 36, half, 28), toA, !_trkOut ? _chipOn : _chip)) { _trkOut = false; _trkStopsKey = -1; RebuildTracker(); }

        float y = a.y + 72f;

        // Buses on this route and direction right now
        string rn = route.routeNumber;
        var buses = _rows.Where(r => !string.IsNullOrEmpty(r.route) && DepotData.StripVariantLetter(r.route) == rn && r.ctrl != null && r.ctrl.IsOutbound == _trkOut && !r.parked).ToList();
        GUI.Label(new Rect(a.x, y, a.width, 20), $"Buses on this route ({buses.Count})", _h2); y += 22;
        float busH = Mathf.Min(buses.Count * 38f, 118f);
        if (buses.Count == 0) { GUI.Label(new Rect(a.x, y, a.width, 18), "None right now.", _dim); y += 22; }
        else
        {
            var br = new Rect(a.x, y, a.width, busH);
            List(br, ref _scrollTrk, buses.Count, 38f, (i, r) =>
            {
                var bus = buses[i];
                if (RowButton(r, bus.busID == _selBus)) SelectBus(bus.busID, true);
                var nx = ((IBusDisplaySource)bus.ctrl).GetUpcomingStopsWithEta(1);
                string next = nx.Count > 0 ? $"Next: {nx[0].stopName}  ·  {nx[0].etaLabel}" : bus.state;
                GUI.Label(new Rect(r.x + 10, r.y + 2, 90, 20), $"#{bus.fleet}", _bold);
                string lw = LateWords(bus.busID);
                GUI.Label(new Rect(r.xMax - 120, r.y + 2, 110, 20), lw, new GUIStyle(lw.Contains("late") ? _bad : _good) { alignment = TextAnchor.MiddleRight });
                GUI.Label(new Rect(r.x + 10, r.y + 19, r.width - 20, 18), next, _dim);
            });
            y += busH + 6;
        }

        // The stops, in order
        float arrH = string.IsNullOrEmpty(_trkStop) ? 0f : Mathf.Min(200f, 46f + Mathf.Max(1, _trkLines.Count) * 18f);
        GUI.Label(new Rect(a.x, y, a.width, 20), $"Stops ({_trkStops.Count})  ·  click one", _h2); y += 22;
        var stopArea = new Rect(a.x, y, a.width, a.yMax - y - arrH - (arrH > 0 ? 6f : 0f));
        List(stopArea, ref _scrollTrkStops, _trkStops.Count, 32f, (i, r) =>
        {
            var st = _trkStops[i];
            bool sel = st.stopCode == _trkStop;
            if (RowButton(r, sel))
            {
                _trkStop = st.stopCode;
                if (MDT_LiveMap.Instance != null) { MDT_LiveMap.Instance.ManagerSelectedStop = st.stopCode; MDT_LiveMap.Instance.ManagerFocusRoute = rn; MDT_LiveMap.Instance.ManagerCenterOn(st.GetWorldPosition()); }
                RebuildTracker();
            }
            GUI.Label(new Rect(r.x + 10, r.y, 36, r.height), (i + 1).ToString(), _dim);
            GUI.Label(new Rect(r.x + 44, r.y, r.width - 140, r.height), st.stopName, st.isTerminal ? _bold : _body);
            if (i == _trkStops.Count - 1) GUI.Label(new Rect(r.xMax - 92, r.y, 84, r.height), "End of line", new GUIStyle(_dim) { alignment = TextAnchor.MiddleRight });
        });

        // What's coming to the picked stop
        if (arrH > 0f)
        {
            var box = new Rect(a.x, a.yMax - arrH, a.width, arrH);
            Fill(box, _tRow); Fill(new Rect(box.x, box.y, box.width, 3), _tAccent);
            var stop = CityManager.Instance != null ? CityManager.Instance.GetStop(_trkStop) : null;
            GUI.Label(new Rect(box.x + 10, box.y + 6, box.width - 50, 24), stop != null ? stop.stopName : _trkStop, _h2);
            if (GUI.Button(new Rect(box.xMax - 34, box.y + 6, 26, 24), "×", _btnGhost))
            { _trkStop = null; if (MDT_LiveMap.Instance != null) MDT_LiveMap.Instance.ManagerSelectedStop = null; return; }
            GUI.Label(new Rect(box.x + 10, box.y + 28, box.width - 20, 16), "Coming to this stop", _dim);
            if (_trkLines.Count == 0) GUI.Label(new Rect(box.x + 10, box.y + 46, box.width - 20, 18), "No buses are coming soon.", _body);
            for (int i = 0; i < _trkLines.Count && 46 + (i + 1) * 18 <= arrH; i++)
            {
                bool head = !_trkLines[i].StartsWith("   ");
                var style = head ? _bold : (_trkLineHot[i] ? new GUIStyle(_body) { normal = { textColor = CAccent } } : _body);
                GUI.Label(new Rect(box.x + 10, box.y + 46 + i * 18, box.width - 20, 18), _trkLines[i], style);
            }
        }
    }
    private Vector2 _scrollTrkStops;

    private void SetTrackerRouteOnMap()
    {
        if (MDT_LiveMap.Instance != null && _routes.Length > 0) MDT_LiveMap.Instance.ManagerFocusRoute = _routes[_trkRoute].routeNumber;
    }

    // ── Stop card (the tracker for a stop) ───────────────────────────────────
    private void DrawStopCard(Rect map)
    {
        if (string.IsNullOrEmpty(_stopPick)) return;
        var stop = CityManager.Instance != null ? CityManager.Instance.GetStop(_stopPick) : null;
        var groups = BusTrackerService.Instance != null ? BusTrackerService.Instance.GetArrivalsForStopByCompass(_stopPick) : null;
        var lines = new List<string>();
        if (groups != null)
            foreach (var g in groups)
            {
                if (g.arrivals == null || g.arrivals.Count == 0) continue;
                lines.Add(g.label);
                foreach (var e in g.arrivals.Take(3)) lines.Add($"   Route {BusRouteData.RouteLabel(e.routeNumber, e.variantLetter)}   ·   {e.minutesLabel}   ·   {e.busLabel}");
            }
        if (lines.Count == 0) lines.Add("No buses are coming soon.");

        var card = new Rect(map.xMax - 330, map.y + 14, 316, 54 + lines.Count * 18);
        Fill(card, _tPanel); Fill(new Rect(card.x, card.y, card.width, 3), _tAccent);
        GUI.Label(new Rect(card.x + 12, card.y + 8, card.width - 50, 24), stop != null ? stop.stopName : _stopPick, _h2);
        if (GUI.Button(new Rect(card.xMax - 34, card.y + 8, 26, 24), "×", _btnGhost)) { _stopPick = null; return; }
        for (int i = 0; i < lines.Count; i++)
            GUI.Label(new Rect(card.x + 12, card.y + 40 + i * 18, card.width - 20, 18), lines[i], lines[i].StartsWith("   ") ? _body : _dim);
    }
}
