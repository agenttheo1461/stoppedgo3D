using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// v2.0 — REPLACES ShiftPickerMenu. Full shift board: driver profile
/// (level/XP/points) always visible at the top, plus up to 3 curated route
/// options below it, each showing requirements up front (fleet/series
/// eligibility, home depot, next departure, and lap structure) so the
/// player never deadheads to a bus that can't legally run what they picked.
///
/// [FIX] Rewired off DriverProgression (rank-based, retired) onto
/// PointsManager. No more rank bands / CanDriveArticulated / CanDriveFleet
/// Number gating anywhere in this file -- per the "levels only, drive
/// everything" design, EVERY idle bus is now eligible regardless of level,
/// so DepotBusOption.rankEligible and the "Rank too low" row branch are
/// gone entirely rather than kept around always-true.
///
/// Two-step flow, both entirely on screen:
///   Step 1 — ROUTE SELECT: 3 route cards (profile strip on top,
///            requirements shown per card).
///   Step 2 — BUS SELECT: pick a specific IDLE bus sitting at a depot that
///            satisfies that route's depot/fleet requirement. Selecting one
///            calls ShiftRunner.ClaimRouteAndBus, which possesses
///            the chosen bus and hands off to PlayerHandoff.JoinRouteWithSlot
///            (the real reservation entry point — no teleport, since the
///            bus is already sitting exactly where it's parked).
///
/// The terminal-decision popup (laps complete) skips Step 2 entirely —
/// the player is locked to one bus for the whole shift, so picking a route
/// there calls ShiftRunner.ContinueOnRoute (same slot reservation, no
/// possession change).
/// </summary>
public class ShiftBoardMenu : MonoBehaviour
{
    public static ShiftBoardMenu Instance { get; private set; }

    public KeyCode toggleKey => KeyBindings.Current.shiftBoard;
    [Range(520, 900)] public int panelW = 680;
    [Range(420, 820)] public int panelH = 560;

    [Header("Board")]
    [Tooltip("How many route options to surface at once.")]
    [Range(1, 3)] public int maxOptions = 3;

    [Tooltip("Log which block (if any) triggered OpenForBlock — debug only.")]
    public bool logOpenCalls = false;

    private bool _open;
    private float _panelX, _panelY;
    private Vector2 _scroll;

    private bool _stylesReady;
    private GUIStyle _lblTitle, _lblSub, _lblDim, _lblBody, _lblCyan, _lblAmber, _lblGreen, _lblBig, _lblLocked;
    private GUIStyle _btnPrimary, _btnSecond, _btnDisabled, _btnDanger;

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
    }

    private class DepotBusOption
    {
        public int fleetNumber;
        public string busType;
        public string depotName;
        public bool isArticulated;
    }

    private readonly List<RouteOption> _options = new List<RouteOption>();
    private readonly List<DepotBusOption> _depotBuses = new List<DepotBusOption>();

    private bool _isTerminalPopup = false;
    private DailyShiftBlock _pinnedBlock = null; // the block OpenForBlock was called with, if any

    // ── Day calendar view — vertical 0-24 timeline of todaysSchedule ──────────
    private Vector2 _calendarScroll;
    private const float HourPx          = 46f;
    private const float CalendarHeight  = HourPx * 24f;
    private const float HourLabelW      = 54f;

    private enum Step { RouteSelect, BusSelect, Calendar }
    private Step _step = Step.RouteSelect;
    private RouteOption _pendingRoute;
    private string _busSelectError = null;
    private float _busSelectErrorAt = -999f;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private int _lastRefreshMinute = -1;
    private void Update()
    {
        if (!MainMenu.BlocksInput && Input.GetKeyDown(toggleKey)) // [FIX Bug 43]
            HandleTogglePressed();

        // Departures roll off / come into range as the sim clock moves.
        if (_open && _step == Step.RouteSelect && SimClock.Instance != null
            && Mathf.FloorToInt(SimClock.Instance.AbsoluteGameMinutes) != _lastRefreshMinute)
            RefreshRouteOptions();
    }

    /// <summary>[MOBILE] Same body the keyboard toggleKey handler used to
    /// run inline — pulled out so the mobile shift-board button can invoke
    /// the exact same open/close/terminal-decision logic instead of
    /// duplicating (and inevitably drifting from) it.</summary>
    public void HandleTogglePressed()
    {
            // [FIX 07-30] Escape was bound here AND independently in
            // MDT_LiveMap with no arbitration between the two. If the live
            // map was open, pressing Escape closed IT (its own handler)
            // while this handler ALSO fired in the same frame — its only
            // guard was "am I on duty," not "is something else already
            // open" — so closing the map could simultaneously pop the
            // board open. Bail out entirely and let the map's own handler
            // own this keypress when it's the one currently visible.
            if (MDT_LiveMap.Instance != null && MDT_LiveMap.Instance.IsVisible)
                return;

            bool onDuty = PlayerHandoff.Instance != null && PlayerHandoff.Instance.IsOnDuty;
            if (onDuty)
            {
                if (_open) { _open = false; return; }

                // [FIX 07-30] This branch used to be close-only, full stop —
                // Escape could never reopen the board while on duty. That's
                // a dead end specifically at a terminal: the board pops open
                // on arrival (OpenAsTerminalDecision), but if you dismissed
                // it with Escape there was no way to get it back through the
                // same key, only through whatever re-triggers
                // OpenAsTerminalDecision on its own. Now Escape can reopen
                // it too, but ONLY while the board is genuinely waiting on a
                // decision from you (stopped at a terminal, not mid-drive) —
                // still won't casually pop open while you're InService.
                var st = PlayerHandoff.Instance.ShiftState;
                bool boardWantsAttention =
                    st == PlayerHandoff.PlayerShiftState.WaitingToDepart ||
                    st == PlayerHandoff.PlayerShiftState.ArrivedAtTerminal;
                if (boardWantsAttention)
                {
                    OpenAsTerminalDecision();
                }
                return;
            }

            _open = !_open;
            if (_open)
            {
                _isTerminalPopup = false;
                _pinnedBlock = null;
                _step = Step.RouteSelect;
                _panelX = (Screen.width - panelW) * 0.5f;
                _panelY = (Screen.height - panelH) * 0.5f;
                RefreshRouteOptions();
            }
    }

    public void OpenAsTerminalDecision()
    {
        _isTerminalPopup = true;
        _open = true;
        _step = Step.RouteSelect;
        _panelX = (Screen.width - panelW) * 0.5f;
        _panelY = (Screen.height - panelH) * 0.5f;
        RefreshRouteOptions();
    }

    public void Close() => _open = false;

    /// <summary>Console's "board" command — opens the pre-shift route
    /// select view directly, same as pressing the toggle key while off
    /// duty. No-op while on duty (the terminal prompt is the only mid-shift
    /// entry, opened automatically by PlayerHandoff).</summary>
    public void OpenBoard()
    {
        bool onDuty = PlayerHandoff.Instance != null && PlayerHandoff.Instance.IsOnDuty;
        if (onDuty)
        {
            DriverConsole.Instance?.PrintTagged("Already on duty — the board opens automatically at your next terminal decision.", "warn");
            return;
        }
        _isTerminalPopup = false;
        _pinnedBlock = null;
        _step = Step.RouteSelect;
        _panelX = (Screen.width - panelW) * 0.5f;
        _panelY = (Screen.height - panelH) * 0.5f;
        _open = true;
        RefreshRouteOptions();
    }

    public void OpenForBlock(int blockIndex)
    {
        DailyShiftBlock block = null;
        if (ShiftRunner.Instance != null && ShiftRunner.Instance.todaysSchedule != null
            && blockIndex >= 0
            && blockIndex < ShiftRunner.Instance.todaysSchedule.Count)
            block = ShiftRunner.Instance.todaysSchedule[blockIndex];

        OpenForBlock(block);
    }

    public void OpenForBlock(DailyShiftBlock block = null)
    {
        if (block != null && logOpenCalls)
            Debug.Log($"[ShiftBoardMenu] OpenForBlock: {block.TimeRangeLabel} Route {block.routeNumber}");
        _pinnedBlock = block;
        OpenAsTerminalDecision();
    }

    private void RefreshRouteOptions()
    {
        _options.Clear();
        if (BusScheduler.Instance == null || SimClock.Instance == null) return;

        float now = SimClock.Instance.AbsoluteGameMinutes;
        _lastRefreshMinute = Mathf.FloorToInt(now);
        int day = SimClock.Instance.GameDayNumber;

        // [FIX] This used to be every managedRoute currently operating,
        // system-wide, with zero connection to the bus's own generated
        // daily calendar (ShiftRunner.todaysSchedule / DailyShiftGenerator).
        // That's why the board could hand you 3 completely unrelated routes
        // whose only common trait was "has a departure in the next few
        // minutes" — any route running right now qualified, whether or not
        // it appeared anywhere in today's actual shift blocks. Now narrowed
        // to routeNumbers that exist in today's real calendar first; only
        // falls back to the old "any live route" behavior if that calendar
        // isn't available at all (e.g. ShiftRunner not initialized yet),
        // so the board never just goes blank.
        var todaysBlockRoutes = ShiftRunner.Instance != null && ShiftRunner.Instance.todaysSchedule != null
            ? new HashSet<string>(ShiftRunner.Instance.todaysSchedule
                .Where(b => b != null && !string.IsNullOrEmpty(b.routeNumber))
                .Select(b => b.routeNumber))
            : null;

        var liveRoutes = BusScheduler.Instance.managedRoutes
            .Where(r => r != null && RouteCoversNow(r, now)
                        && (todaysBlockRoutes == null || todaysBlockRoutes.Count == 0 || todaysBlockRoutes.Contains(r.routeNumber)))
            .ToList();

        // [FIX] "Continue" was offering the same direction the player just
        // finished (e.g. Z→A again right after arriving Z→A) because this
        // only ever looked at chronological order, never at which way the
        // bus is actually facing/where it just terminated. When we know the
        // direction just completed (terminal-decision popup, mid-shift),
        // prefer the next slot going the OPPOSITE way — that's what
        // "continue" should mean physically (you're sitting at the terminal
        // the opposite leg departs from). Only falls back to "whatever's
        // next regardless of direction" if no opposite-direction slot
        // exists in the window, so routes with lopsided schedules don't
        // just show nothing.
        bool haveDirectionPreference = _isTerminalPopup && PlayerHandoff.Instance != null && PlayerHandoff.Instance.IsOnDuty == false
            && PlayerHandoff.Instance.LastCompletedLegWasOutbound.HasValue;
        bool? wantOutbound = haveDirectionPreference
            ? !PlayerHandoff.Instance.LastCompletedLegWasOutbound.Value
            : (bool?)null;

        var scored = new List<(BusRouteData route, BusScheduler.RouteBusEntry entry)>();
        foreach (var r in liveRoutes)
        {
            IEnumerable<BusScheduler.RouteBusEntry> candidates = BusScheduler.Instance.GetTodaysBusesForRoute(r.routeNumber, day)
                .Where(e => e.scheduledDeparture >= now + (_isTerminalPopup ? 0f : BusScheduler.BoardingLeadFor(now)));

            var upcoming = wantOutbound.HasValue
                ? candidates.Where(e => e.isOutbound == wantOutbound.Value)
                            .OrderBy(e => e.scheduledDeparture)
                            .FirstOrDefault()
                : default;

            // Fallback: no opposite-direction slot found (or no preference
            // to begin with) — take the earliest upcoming slot regardless
            // of direction, same as before.
            if (upcoming.scheduledDeparture <= 0f)
                upcoming = candidates.OrderBy(e => e.scheduledDeparture).FirstOrDefault();

            if (upcoming.scheduledDeparture > 0f) scored.Add((r, upcoming));
        }

        // [FIX] Pinning by "find an upcoming (>= now) departure for this
        // route" silently failed for the common case: the block you opened
        // the board FOR is usually the block currently IN PROGRESS, whose
        // departure already happened. That's exactly why "199 (10:30-14:30,
        // IN)" never showed up — it has no future slot to match. Build the
        // pinned card straight from the block's own route/time data instead
        // of depending on a scheduled-slot lookup at all.
        string pinnedRouteNumber = _pinnedBlock?.routeNumber;
        if (!string.IsNullOrEmpty(pinnedRouteNumber) && !scored.Any(s => s.route.routeNumber == pinnedRouteNumber))
        {
            var pinnedRoute = BusScheduler.Instance.managedRoutes
                .FirstOrDefault(r => r != null && r.routeNumber == pinnedRouteNumber);
            if (pinnedRoute != null)
            {
                var pinnedUpcoming = BusScheduler.Instance.GetTodaysBusesForRoute(pinnedRoute.routeNumber, day)
                    .OrderBy(e => Mathf.Abs(e.scheduledDeparture - _pinnedBlock.windowStartMinutes))
                    .FirstOrDefault();
                float departure = pinnedUpcoming.scheduledDeparture > 0f
                    ? pinnedUpcoming.scheduledDeparture
                    : _pinnedBlock.windowStartMinutes; // no matching slot at all — fall back to the block's own start
                scored.Insert(0, (pinnedRoute, new BusScheduler.RouteBusEntry
                {
                    scheduledDeparture = departure,
                    isOutbound = pinnedUpcoming.scheduledDeparture > 0f ? pinnedUpcoming.isOutbound : true,
                    variantLetter = pinnedUpcoming.scheduledDeparture > 0f ? pinnedUpcoming.variantLetter : null,
                }));
            }
        }

        var ordered = scored.OrderBy(s => s.route.routeNumber == pinnedRouteNumber ? 0 : 1)
                             .ThenBy(s => s.entry.scheduledDeparture);

        foreach (var pair in ordered.Take(maxOptions))
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
    }

    /// <summary>How many idle buses right now would actually pass
    /// RefreshDepotBuses' filters for this route — shown on the card so a
    /// wasted trip to the bus-pick screen (0 eligible) is visible upfront.</summary>
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

    /// <summary>Resolves the REAL TimetableSlot object for a chosen route
    /// card, REGARDLESS of who currently owns it. This is deliberate: the
    /// board is meant to let the player take over an NPC's currently-
    /// assigned slot mid-chain (however many laps it has left — 1, 2, 3,
    /// whatever), not just pick from whatever's still sitting unclaimed.
    /// PeekUpcomingSlots only ever returns free/unassigned slots by design,
    /// so using it here meant SELECT could never succeed on a departure
    /// that was already correctly assigned to an NPC — which is the common
    /// case, not an edge case. Scans BusScheduler.AllSlots directly instead,
    /// matching on route/variant/direction/departure with no state filter
    /// beyond excluding Completed (a finished slot isn't reservable by
    /// anyone).</summary>
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

    private static bool RouteCoversNow(BusRouteData route, float nowAbsolute)
    {
        float rel = nowAbsolute % 1440f;
        float opStart = route.operatingStartMinutes;
        float opEnd = route.operatingEndMinutes;
        if (opEnd <= opStart) opEnd += 1440f;
        float relAdj = rel < opStart ? rel + 1440f : rel;
        return relAdj >= opStart && relAdj <= opEnd;
    }

    /// <summary>[FIX] This used to unconditionally return 3, regardless of
    /// whether the slot being offered already belongs to an NPC mid-
    /// rotation. Since the board deliberately lets you take over an NPC's
    /// currently-assigned slot "however many laps it has left" (see
    /// ResolveRealSlot's own comment above), showing a flat 3 was actively
    /// wrong for any bus that had already completed one or more legs —
    /// you'd pick what the card said was "3 laps" and actually inherit
    /// whatever was really left (1, 2, whatever). Now derives it from the
    /// entry's real chainLegIndex against BusScheduler's own effective
    /// per-bus threshold — the exact same numbers CompleteSlot/TopUpChain
    /// use to decide retirement, so the card can never lie relative to
    /// what actually happens once you claim it.</summary>

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
        // [FIX] Was GetEffectiveLapThresholdPublic(entry.busID), which resolves
        // route via _slotByBus and falls back to the flat default if this bus
        // has no live slot yet. route is already known here, so go straight to
        // the route-aware overload — reliable even for entry.busID <= 0 (fresh,
        // not-yet-assigned slots), which the old busID-only path couldn't handle.
        int threshold = BusScheduler.Instance != null
            ? BusScheduler.Instance.GetEffectiveLapThresholdForRoute(entry.busID, route.routeNumber)
            : BusScheduler.LapsForRoute(route);

        if (threshold <= 0) threshold = BusScheduler.LapsForRoute(route); // retirement disabled — display-only figure

        // entry.busID <= 0 means the slot is genuinely Unassigned (fresh
        // claim, not taking over an in-progress rotation) — full allotment.
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
            // [FIX] Rank/level gating removed entirely -- "drive everything"
            // means every idle, depot-eligible bus qualifies now, no
            // DriverProgression.CanDriveFleetNumber check at all.

            _depotBuses.Add(new DepotBusOption
            {
                fleetNumber = fleetNumber,
                busType = meta != null ? meta.busType : "Unknown",
                depotName = meta != null && meta.homeDepot != null ? meta.homeDepot.depotName : "Unknown depot",
                isArticulated = meta != null && meta.isArticulated,
            });
        }

        _depotBuses.Sort((a, b) => a.fleetNumber.CompareTo(b.fleetNumber));

        // Scroll straight to the fleet number picked last time, if it's
        // in this list — returning players aren't hunting for it again.
        int lastFleet = PlayerPrefs.GetInt(LAST_FLEET_PREF_KEY, -1);
        if (lastFleet >= 0)
        {
            int idx = _depotBuses.FindIndex(b => b.fleetNumber == lastFleet);
            if (idx >= 0) _scroll = new Vector2(0, idx * 62f); // 56 row height + 6 gap, matches DrawBusSelectStep's rowH
        }
    }

    private void OnGUI()
    {
        if (!_open) return;
        EnsureStyles();

        GUI.color = new Color(0, 0, 0, 0.75f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        var pr = new Rect(_panelX, _panelY, panelW, panelH);
        MDT_UITheme.DrawPanel(pr);
        GUI.BeginGroup(pr);

        MDT_UITheme.DrawHeader(new Rect(0, 0, panelW, 44f));
        string title = _step == Step.BusSelect ? "SELECT A BUS"
                      : _step == Step.Calendar ? "DAY CALENDAR"
                      : _isTerminalPopup ? "LAPS COMPLETE — WHAT'S NEXT?"
                      : "SHIFT BOARD";
        GUI.Label(new Rect(14, 0, panelW - 80, 44), title, _lblTitle);
        if (GUI.Button(new Rect(panelW - 46, 10, 34, 24), "✕", _btnSecond)) _open = false;

        if (_step == Step.BusSelect) DrawBusSelectStep();
        else if (_step == Step.Calendar) DrawCalendarStep();
        else DrawRouteSelectStep();

        GUI.EndGroup();
    }

    private void DrawRouteSelectStep()
    {
        float y = 54f;
        y = DrawProfileStrip(y);
        y += 10f;

        MDT_UITheme.DrawDivider(12, y, panelW - 24);
        y += 12f;

        if (!_isTerminalPopup && GUI.Button(new Rect(panelW - 172, y, 160, 26), "📅 DAY CALENDAR", _btnSecond))
        {
            _step = Step.Calendar;
            _calendarScroll = Vector2.zero;
            return;
        }

        // ── CONTINUE pill — only shown at the terminal prompt, only when
        // the scheduler still has this bus's next chain leg pre-assigned
        // and waiting. This is the ONE place ContinueAssignedChain() is
        // ever called — per design, nothing auto-continues, the player
        // always presses this explicitly, block-internal lap or not.
        if (_isTerminalPopup && PlayerHandoff.Instance != null && PlayerHandoff.Instance.HasPendingChainLeg)
        {
            var pillR = new Rect(12, y, panelW - 24, 64f);
            MDT_UITheme.DrawRoundedRect(pillR, 32f, new Color(0.06f, 0.22f, 0.11f, 1f));
            GUI.Label(new Rect(pillR.x + 20, pillR.y + 8, pillR.width - 220, 24), "NEXT LEG READY", _lblGreen);
            GUI.Label(new Rect(pillR.x + 20, pillR.y + 32, pillR.width - 220, 20),
                "Same bus, same block — continue straight into it.", _lblDim);
            if (GUI.Button(new Rect(pillR.xMax - 156, pillR.y + 14, 136, 36), "CONTINUE", _btnPrimary))
            {
                PlayerHandoff.Instance.ContinueAssignedChain();
                _open = false;
            }
            y += 74f;
        }

        GUI.Label(new Rect(12, y, panelW - 24, 20), "AVAILABLE BLOCKS", _lblSub);
        y += 24f;

        if (_options.Count == 0)
        {
            GUI.Label(new Rect(12, y, panelW - 24, 40), "Nothing operating right now — check back later.", _lblDim);
            return;
        }

        float cardH = 108f;
        float listH = panelH - y - 60f;
        float contentH = Mathf.Max(listH, _options.Count * (cardH + 10f));
        _scroll = GUI.BeginScrollView(new Rect(8, y, panelW - 16, listH), _scroll,
                                       new Rect(0, 0, panelW - 32, contentH));

        for (int i = 0; i < _options.Count; i++)
            DrawRouteCard(_options[i], i * (cardH + 10f), panelW - 32 - 16f, cardH);

        GUI.EndScrollView();

        float footerY = panelH - 44f;
        if (_isTerminalPopup)
        {
            if (!_endShiftConfirmArmed)
            {
                if (GUI.Button(new Rect(12, footerY, panelW - 24, 34), "END SHIFT — RETURN TO DEPOT", _btnSecond))
                {
                    _endShiftConfirmArmed = true;
                    _endShiftConfirmArmedAt = Time.realtimeSinceStartup;
                }
            }
            else
            {
                // Auto-disarm after a few seconds so an accidental re-open
                // of the board doesn't leave it primed indefinitely.
                if (Time.realtimeSinceStartup - _endShiftConfirmArmedAt > 4f)
                    _endShiftConfirmArmed = false;

                float halfW = (panelW - 24 - 8f) * 0.5f;
                if (GUI.Button(new Rect(12, footerY, halfW, 34), "← CANCEL", _btnSecond))
                    _endShiftConfirmArmed = false;
                if (GUI.Button(new Rect(12 + halfW + 8f, footerY, halfW, 34), "CONFIRM — END SHIFT", _btnDanger))
                {
                    _endShiftConfirmArmed = false;
                    // [FIX] Was CompleteCurrentBlock(), which only ever
                    // pauses between blocks -- this button is meant to
                    // genuinely end the shift and return to depot.
                    ShiftRunner.Instance?.EndShiftNow();
                    _open = false;
                }
            }
        }
    }
    private bool _endShiftConfirmArmed = false;
    private float _endShiftConfirmArmedAt = 0f;

    private void DrawRouteCard(RouteOption opt, float yOff, float w, float h)
    {
        var r = new Rect(0, yOff, w, h);
        MDT_UITheme.DrawRoundedRect(r, 20f, MDT_UITheme.BGRowEven);

        string dirLabel = opt.outbound ? "A → Z" : "Z → A";
        GUI.Label(new Rect(r.x + 14, r.y + 8, 200, 26), $"Route {opt.routeNumber}", _lblCyan);
        GUI.Label(new Rect(r.x + 14, r.y + 32, w - 28, 18), dirLabel, _lblDim);

        string depStr = BusScheduler.MinutesToTimeString(opt.departureAbsMin % 1440f);
        GUI.Label(new Rect(r.x + 14, r.y + 52, w - 28, 18), $"Departs {depStr}  ·  {LapPlanText(opt.route, opt.laps)}", _lblBody);
        string idleTag = opt.idleEligibleCount > 0
            ? $"{opt.idleEligibleCount} idle"
            : "0 idle — none available right now";
        GUI.Label(new Rect(r.x + 14, r.y + 72, w - 28, 18),
            $"Requires: {opt.requiredDepotLabel} fleet — {idleTag}", opt.idleEligibleCount > 0 ? _lblAmber : _lblLocked);

        var btnR = new Rect(w - 150, r.y + h - 40, 136, 32);
        string btnLabel = _isTerminalPopup ? "CONTINUE HERE" : "PICK BUS";

        // [FIX 07-30] This button used to be fully clickable with GUI.enabled
        // never touched anywhere in this file — a route card showing "0 idle
        // — none available right now" still rendered PICK BUS as a full,
        // primary-styled button, so clicking it looked like a real action
        // and led straight to an empty "No idle buses currently satisfy this
        // route's requirements" screen instead. Only gate the fresh-claim
        // case (PICK BUS) — CONTINUE HERE at a terminal is taking over the
        // SAME bus you're already driving, which doesn't depend on
        // idleEligibleCount at all.
        bool canProceed = _isTerminalPopup || opt.idleEligibleCount > 0;
        bool prevEnabled = GUI.enabled;
        GUI.enabled = canProceed;
        if (GUI.Button(btnR, btnLabel, _btnPrimary))
        {
            if (_isTerminalPopup)
            {
                var realSlot = ResolveRealSlot(opt);
                if (realSlot != null)
                {
                    ShiftRunner.Instance?.ContinueOnRoute(opt.routeNumber, opt.variantLetter, realSlot);
                    _open = false;
                }
                else
                {
                    Debug.LogWarning($"[ShiftBoardMenu] Could not resolve a live slot for Route {opt.routeNumber} — try again in a moment.");
                }
            }
            else
            {
                _pendingRoute = opt;
                _step = Step.BusSelect;
                _scroll = Vector2.zero;
                RefreshDepotBuses(opt);
            }
        }
        GUI.enabled = prevEnabled;
    }

    private void DrawBusSelectStep()
    {
        float y = 54f;

        if (!string.IsNullOrEmpty(_busSelectError))
        {
            if (Time.realtimeSinceStartup - _busSelectErrorAt > 6f) _busSelectError = null;
            else
            {
                var errR = new Rect(12, y, panelW - 24, 40f);
                MDT_UITheme.DrawRoundedRect(errR, 10f, new Color(0.32f, 0.08f, 0.08f, 1f));
                GUI.Label(new Rect(errR.x + 12, errR.y + 4, errR.width - 24, errR.height - 8),
                    _busSelectError, _lblLocked);
                y += 48f;
            }
        }

        if (_pendingRoute != null)
        {
            GUI.Label(new Rect(12, y, panelW - 24, 22),
                $"Route {_pendingRoute.routeNumber}  ·  {(_pendingRoute.outbound ? "A → Z" : "Z → A")}  ·  " +
                $"Departs {BusScheduler.MinutesToTimeString(_pendingRoute.departureAbsMin % 1440f)}", _lblSub);
            y += 26f;
        }

        GUI.Label(new Rect(12, y, panelW - 24, 18),
            "Pick an idle bus at an eligible depot — you'll drive it there yourself, no teleport.", _lblDim);
        y += 24f;

        if (GUI.Button(new Rect(12, y, 100, 26), "← Back", _btnSecond))
        {
            _step = Step.RouteSelect;
            _scroll = Vector2.zero;
            return;
        }
        y += 34f;

        if (_depotBuses.Count == 0)
        {
            GUI.Label(new Rect(12, y, panelW - 24, 40), "No idle buses currently satisfy this route's requirements.", _lblDim);
            return;
        }

        float rowH = 56f;
        float listH = panelH - y - 20f;
        float contentH = Mathf.Max(listH, _depotBuses.Count * (rowH + 6f));
        _scroll = GUI.BeginScrollView(new Rect(8, y, panelW - 16, listH), _scroll,
                                       new Rect(0, 0, panelW - 32, contentH));

        for (int i = 0; i < _depotBuses.Count; i++)
            DrawDepotBusRow(_depotBuses[i], i * (rowH + 6f), panelW - 32 - 16f, rowH);

        GUI.EndScrollView();
    }

    private void DrawDepotBusRow(DepotBusOption bus, float yOff, float w, float h)
    {
        var r = new Rect(0, yOff, w, h);
        // [FIX] Was conditionally BGRowOdd when rank-ineligible -- every
        // bus in this list is eligible now, so always the normal row color.
        MDT_UITheme.DrawRoundedRect(r, 16f, MDT_UITheme.BGRowEven);

        string sizeTag = bus.isArticulated ? " · Articulated (60ft)" : "";
        GUI.Label(new Rect(r.x + 12, r.y + 4, w - 160, 20), $"Fleet #{bus.fleetNumber}", _lblCyan);
        GUI.Label(new Rect(r.x + 12, r.y + 26, w - 160, 18), $"{bus.busType}{sizeTag}  ·  {bus.depotName}", _lblDim);

        // [FIX] "Rank too low to drive this bus" branch removed entirely --
        // dead code now that every idle/depot-eligible bus qualifies.

        var btnR = new Rect(w - 110, r.y + (h - 32) * 0.5f, 96, 32);
        if (GUI.Button(btnR, "SELECT", _btnPrimary))
        {
            var realSlot = ResolveRealSlot(_pendingRoute);
            if (realSlot != null)
            {
                ShiftRunner.Instance?.ClaimRouteAndBus(_pendingRoute.routeNumber, _pendingRoute.variantLetter, realSlot, bus.fleetNumber);
                PlayerPrefs.SetInt(LAST_FLEET_PREF_KEY, bus.fleetNumber);
                _busSelectError = null;
                _open = false;
            }
            else
            {
                // Was previously Console-only (Debug.LogWarning) — silent
                // from the player's point of view, exactly the "SELECT does
                // nothing" bug. Now shown directly on the panel, and the
                // route board is refreshed since the slot situation may
                // have changed (e.g. it just got dispatched to an NPC).
                _busSelectError = $"Couldn't claim that departure for Route {_pendingRoute.routeNumber} — " +
                                   "it may have just been taken or already departed. Refreshing routes.";
                _busSelectErrorAt = Time.realtimeSinceStartup;
                Debug.LogWarning($"[ShiftBoardMenu] {_busSelectError}");
                RefreshRouteOptions();
            }
        }
    }

    private const string LAST_FLEET_PREF_KEY = "HEADWAY_LAST_FLEET_PICKED";

    // ═════════════════════════════════════════════════════════════════════════
    //  DAY CALENDAR — vertical 0:00-24:00 timeline of todaysSchedule
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawCalendarStep()
    {
        float y = 54f;

        if (GUI.Button(new Rect(panelW - 108, y, 96, 24), "← BOARD", _btnSecond))
        {
            _step = Step.RouteSelect;
            return;
        }
        GUI.Label(new Rect(12, y + 2, panelW - 130, 20), "Today's full shift calendar — tap a block to pick it up.", _lblDim);
        y += 32f;

        var schedule = ShiftRunner.Instance != null ? ShiftRunner.Instance.todaysSchedule : null;
        if (schedule == null || schedule.Count == 0)
        {
            GUI.Label(new Rect(12, y, panelW - 24, 40), "No shift calendar generated for today yet.", _lblDim);
            return;
        }

        // Every block in todaysSchedule shares the same generation day, so the
        // first block's own day anchor is a reliable 00:00 origin for the
        // whole vertical axis — blocks store ABSOLUTE minutes (see
        // DailyShiftGenerator), this converts back to a 0-1440 relative
        // position within THIS calendar's day for drawing.
        float dayBase = Mathf.Floor(schedule[0].windowStartMinutes / 1440f) * 1440f;

        float listH   = panelH - y - 16f;
        float trackX  = HourLabelW + 10f;
        float trackW  = panelW - 32 - HourLabelW - 10f - 16f; // leave room for the scrollbar
        float contentH = CalendarHeight + 24f;

        _calendarScroll = GUI.BeginScrollView(new Rect(8, y, panelW - 16, listH), _calendarScroll,
                                               new Rect(0, 0, panelW - 32, contentH));

        // Hour gridlines, one per hour, 0 through 24, with a time label on
        // the left of each line.
        for (int h = 0; h <= 24; h++)
        {
            float ly = h * HourPx;
            MDT_UITheme.DrawDivider(trackX, ly, trackW);
            if (h < 24)
                GUI.Label(new Rect(0, ly - 7, HourLabelW, 16), BusScheduler.MinutesToTimeString(h * 60f), _lblDim);
        }

        // "NOW" marker — only meaningful when this calendar is for the
        // current in-game day (a look-ahead/look-back day just omits it).
        if (SimClock.Instance != null)
        {
            float nowRel = SimClock.Instance.AbsoluteGameMinutes - dayBase;
            if (nowRel >= 0f && nowRel <= 1440f)
            {
                float nowY = (nowRel / 1440f) * CalendarHeight;
                MDT_UITheme.DrawRoundedRect(new Rect(trackX, nowY - 1.5f, trackW, 3f), 1.5f, MDT_UITheme.TextAmber);
                GUI.Label(new Rect(trackX + 6, nowY - 15, 60, 16), "NOW", _lblAmber);
            }
        }

        for (int i = 0; i < schedule.Count; i++)
            DrawCalendarBlock(schedule[i], trackX, trackW, dayBase);

        GUI.EndScrollView();
    }

    private void DrawCalendarBlock(DailyShiftBlock block, float trackX, float trackW, float dayBase)
    {
        float relStart = Mathf.Clamp(block.windowStartMinutes - dayBase, 0f, 1440f);
        float relEnd   = Mathf.Clamp(block.windowEndMinutes   - dayBase, 0f, 1440f);
        if (relEnd <= relStart) return; // degenerate/clamped-away block — nothing to draw

        float blockY = (relStart / 1440f) * CalendarHeight;
        float blockH = Mathf.Max(20f, ((relEnd - relStart) / 1440f) * CalendarHeight);

        var r = new Rect(trackX + 2f, blockY + 1f, trackW - 4f, blockH - 2f);

        bool isActive = SimClock.Instance != null
            && SimClock.Instance.AbsoluteGameMinutes >= block.windowStartMinutes
            && SimClock.Instance.AbsoluteGameMinutes <  block.windowEndMinutes;

        Color baseColor = block.missed    ? new Color(0.30f, 0.10f, 0.10f, 1f)
                         : block.completed ? new Color(0.08f, 0.24f, 0.13f, 1f)
                         : isActive         ? new Color(0.10f, 0.20f, 0.30f, 1f)
                         : MDT_UITheme.BGRowEven;

        DrawBeveledBlock(r, baseColor, isActive);

        string statusTag = block.missed ? "MISSED" : block.completed ? "DONE" : isActive ? "IN PROGRESS" : "";
        var statusStyle  = block.missed ? _lblLocked : block.completed ? _lblGreen : _lblAmber;
        string typeTag    = block.isOvernight ? " · Overnight" : block.isEvening ? " · Evening" : "";
        bool tight        = blockH < 40f;

        GUI.Label(new Rect(r.x + 10, r.y + 4, r.width - 96, 18), $"Route {block.routeNumber}", _lblCyan);
        if (!string.IsNullOrEmpty(statusTag))
            GUI.Label(new Rect(r.xMax - 88, r.y + 5, 82, 16), statusTag, statusStyle);

        if (!tight)
            GUI.Label(new Rect(r.x + 10, r.y + 24, r.width - 20, 16), $"{block.TimeRangeLabel}{typeTag}", _lblDim);

        // Click anywhere on an upcoming block (not already finished or
        // missed) to jump straight into bus-select for that block's route —
        // same destination as picking a card off the normal board.
        if (!block.completed && !block.missed && GUI.Button(r, GUIContent.none, GUIStyle.none))
            JumpToBusSelectForBlock(block);
    }

    /// <summary>Cheap fake-3D "beveled chip" look for a calendar block —
    /// IMGUI has no real border stroke or lighting, so this layers a soft
    /// drop shadow behind the fill, then a light sliver along the top edge
    /// and a dark sliver along the bottom, which reads as a raised chip
    /// rather than a flat rectangle. Active blocks get an amber accent
    /// down the left edge to match the "NOW" marker.</summary>
    private void DrawBeveledBlock(Rect r, Color baseColor, bool isActive)
    {
        MDT_UITheme.DrawRoundedRect(new Rect(r.x + 2f, r.y + 3f, r.width, r.height), 10f, new Color(0f, 0f, 0f, 0.35f));
        MDT_UITheme.DrawRoundedRect(r, 10f, baseColor);

        var hi = new Color(1f, 1f, 1f, 0.10f);
        var lo = new Color(0f, 0f, 0f, 0.22f);
        MDT_UITheme.DrawRoundedRect(new Rect(r.x + 3f, r.y + 2f, r.width - 6f, 4f), 2f, hi);
        MDT_UITheme.DrawRoundedRect(new Rect(r.x + 3f, r.yMax - 6f, r.width - 6f, 4f), 2f, lo);

        if (isActive)
            MDT_UITheme.DrawRoundedRect(new Rect(r.x, r.y, 4f, r.height), 2f, MDT_UITheme.TextAmber);
    }

    /// <summary>Builds a RouteOption for this block's route/next live
    /// departure inside its own window and jumps straight to bus-select —
    /// the calendar's "tap a block to pick it up" entry point, sharing the
    /// exact same claim path as SELECT on the normal board.</summary>
    private void JumpToBusSelectForBlock(DailyShiftBlock block)
    {
        if (BusScheduler.Instance == null || SimClock.Instance == null) return;

        var route = BusScheduler.Instance.GetRouteData(block.routeNumber);
        if (route == null)
        {
            _busSelectError = $"Route {block.routeNumber} data not found.";
            _busSelectErrorAt = Time.realtimeSinceStartup;
            return;
        }

        float now = SimClock.Instance.AbsoluteGameMinutes;
        float searchFrom = Mathf.Max(now, block.windowStartMinutes);

        var candidates = BusScheduler.Instance.GetTodaysBusesForRoute(block.routeNumber, SimClock.Instance.GameDayNumber)
            .Where(e => e.scheduledDeparture >= searchFrom && e.scheduledDeparture <= block.windowEndMinutes);

        // [FIX] Same opposite-direction preference as RefreshRouteOptions —
        // see the comment there.
        bool? wantOutbound = (PlayerHandoff.Instance != null && !PlayerHandoff.Instance.IsOnDuty
                               && PlayerHandoff.Instance.LastCompletedLegWasOutbound.HasValue)
            ? !PlayerHandoff.Instance.LastCompletedLegWasOutbound.Value
            : (bool?)null;

        var upcoming = wantOutbound.HasValue
            ? candidates.Where(e => e.isOutbound == wantOutbound.Value).OrderBy(e => e.scheduledDeparture).FirstOrDefault()
            : default;

        if (upcoming.scheduledDeparture <= 0f)
            upcoming = candidates.OrderBy(e => e.scheduledDeparture).FirstOrDefault();

        if (upcoming.scheduledDeparture <= 0f)
        {
            _busSelectError = $"No live departure for Route {block.routeNumber} left in this block.";
            _busSelectErrorAt = Time.realtimeSinceStartup;
            return;
        }

        var depot = DepotManager.Instance != null ? DepotManager.Instance.GetDepotForRoute(route.routeNumber) : null;
        var opt = new RouteOption
        {
            route = route,
            routeNumber = route.routeNumber,
            outbound = upcoming.isOutbound,
            variantLetter = upcoming.variantLetter ?? "",
            departureAbsMin = upcoming.scheduledDeparture,
            laps = EstimateLapsForBlock(route, upcoming),
            requiredDepotLabel = depot != null ? depot.depotName : "Any depot",
            idleEligibleCount = CountIdleEligibleBuses(route.routeNumber),
        };

        _pendingRoute = opt;
        _step = Step.BusSelect;
        _scroll = Vector2.zero;
        RefreshDepotBuses(opt);
    }

    /// <summary>[FIX] Fully rewired off DriverProgression -- no rank label,
    /// no rank-band progress bar (PriorRankLevel/NextRankLevel are GONE,
    /// see below), no CanDriveArticulated line. Shows Level as the big
    /// label, a direct xp/xpToNextLevel progress fraction, and lifetime
    /// points as the secondary stat -- matches PointsManager's actual
    /// shape (simple escalating level threshold, no bands, no gating).</summary>
    private float DrawProfileStrip(float y)
    {
        var pm = PointsManager.Instance;
        float stripH = 64f;
        var stripR = new Rect(12, y, panelW - 24, stripH);
        MDT_UITheme.DrawRoundedRect(stripR, 20f, MDT_UITheme.BGMid);

        if (pm == null)
        {
            GUI.Label(new Rect(stripR.x + 12, stripR.y + 20, stripR.width - 24, 20), "Driver profile unavailable.", _lblDim);
            return y + stripH;
        }

        GUI.Label(new Rect(stripR.x + 14, stripR.y + 6, 260, 22), $"Level {pm.level}", _lblBig);
        GUI.Label(new Rect(stripR.x + 14, stripR.y + 30, 260, 18),
            $"{pm.lifetimePoints:N0} pts total", _lblDim);

        float barX = stripR.x + 280, barW = stripR.width - 300, barY = stripR.y + 34, barH = 10f;
        MDT_UITheme.DrawRoundedRect(new Rect(barX, barY, barW, barH), barH * 0.5f, MDT_UITheme.BGButton);

        float frac = pm.xpToNextLevel > 0 ? Mathf.Clamp01(pm.xp / (float)pm.xpToNextLevel) : 0f;
        MDT_UITheme.DrawRoundedRect(new Rect(barX, barY, barW * frac, barH), barH * 0.5f, MDT_UITheme.TextGreen);
        GUI.Label(new Rect(barX, stripR.y + 6, barW, 20), $"{pm.xp:N0} / {pm.xpToNextLevel:N0} XP to next level", _lblDim);

        return y + stripH;
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _lblTitle    = MDT_UITheme.MakeLabel(16, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblSub      = MDT_UITheme.MakeLabel(12, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextAmber);
        _lblDim      = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.UpperLeft,  MDT_UITheme.TextDim);
        _lblBody     = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.UpperLeft,  MDT_UITheme.TextPrimary);
        _lblCyan     = MDT_UITheme.MakeLabel(15, FontStyle.Bold,   TextAnchor.UpperLeft,  MDT_UITheme.TextCyan);
        _lblAmber    = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.UpperLeft,  MDT_UITheme.TextAmber);
        _lblGreen    = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.UpperLeft,  MDT_UITheme.TextGreen);
        _lblBig      = MDT_UITheme.MakeLabel(18, FontStyle.Bold,   TextAnchor.UpperLeft,  MDT_UITheme.TextPrimary);
        _lblLocked   = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.UpperLeft,  MDT_UITheme.TextRed);
        _btnPrimary  = MDT_UITheme.MakeButton(new Color(0.05f, 0.30f, 0.14f, 1f), MDT_UITheme.TextGreen, 12, FontStyle.Bold);
        _btnSecond   = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 11);
        _btnDisabled = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextDim, 11);
        _btnDanger   = MDT_UITheme.MakeButton(new Color(0.35f, 0.08f, 0.08f, 1f), MDT_UITheme.TextRed, 11, FontStyle.Bold);
    }
}