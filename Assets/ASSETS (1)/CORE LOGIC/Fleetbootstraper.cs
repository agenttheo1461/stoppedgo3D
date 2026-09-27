using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// v3.0 — Single authority for "what should every bus be doing right now."
///
/// Rebuilt around a deterministic calendar (see DailyShiftGenerator v3):
/// every bus's daily calendar is regenerated fresh from (dayNumber, busID)
/// whenever needed — on Start, and again whenever SimClock reports a day
/// rollover — and is GUARANTEED identical to whatever existed before,
/// because generation no longer touches the shared Random stream. That
/// means the calendar itself is disposable; only per-block completed/missed
/// flags need to persist, and those now live on the block objects themselves
/// plus a parallel saved list, kept in sync every time a block is crossed.
///
/// Time is still 100% owned by SimClock (UTC-derived, 2x multiplier, no
/// drift) — offline progress "just works" because GameDayNumber/AbsoluteGameMinutes
/// are pure functions of the wall clock, not something this class advances.
///
/// IMPORTANT:
/// DailyShiftGenerator supports both:
///     new DailyShiftGenerator()
/// and
///     new DailyShiftGenerator(chanceIntervals)
///
/// FleetDispatcher uses the parameterless version because its job is to
/// dispatch buses, while route-chance configuration belongs to the shift
/// generation system.
/// </summary>
public class FleetDispatcher : MonoBehaviour
{
    public static FleetDispatcher Instance { get; private set; }

    [Header("Timing")]
    public float startupDelaySeconds = 0.5f;
    public float dispatchTickInterval = 5f;

    [Header("Windows")]
    [Tooltip("How far ahead a bus may be parked at a terminal waiting for its next departure.")]
    public float terminalWaitWindowMinutes = 45f;

    [Tooltip("How close to departure a bus may be launched early.")]
    public float earlyDispatchWindowMinutes = 10f;

    [Header("Debug")]
    public bool logDispatch = true;

    private readonly Dictionary<int, List<DailyShiftBlock>> _calendarByBus = new();
    private readonly Dictionary<int, int> _calendarDayByBus = new();
    private readonly Dictionary<int, int> _currentBlockIndex = new();

    // DailyShiftGenerator now has a parameterless constructor as well as
    // the chance-interval constructor used by the player shift system.
    private readonly DailyShiftGenerator _shiftGen = new();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void Start()
    {
        if (SimClock.Instance != null)
            SimClock.Instance.OnGameDayRolled += HandleDayRolled;

        StartCoroutine(Bootstrap());
    }

    private void OnDestroy()
    {
        if (SimClock.Instance != null)
            SimClock.Instance.OnGameDayRolled -= HandleDayRolled;
    }

    private IEnumerator Bootstrap()
    {
        yield return new WaitForSeconds(startupDelaySeconds);

        LoadOrBuildAllCalendars();
        RunDispatchPass(isStartup: true);

        StartCoroutine(RecurringDispatch());
    }

    private IEnumerator RecurringDispatch()
    {
        while (true)
        {
            yield return new WaitForSeconds(dispatchTickInterval);

            RunDispatchPass(isStartup: false);
        }
    }

    private void HandleDayRolled(int newDay)
    {
        if (logDispatch)
            Debug.Log(
                $"[FleetDispatcher] Day rolled to {newDay} — rebuilding stale bus calendars.");

        if (BusManager.Instance == null)
            return;

        foreach (var record in BusManager.Instance.GetAllRecords())
        {
            EnsureCalendarForToday(record.busID, newDay);
        }

        SaveNow();
    }

    private void LoadOrBuildAllCalendars()
    {
        if (BusManager.Instance == null || SimClock.Instance == null)
        {
            Debug.LogWarning(
                "[FleetDispatcher] Cannot build calendars — BusManager or SimClock is null.");

            return;
        }

        var save = SaveService.Load();
        int today = SimClock.Instance.GameDayNumber;

        foreach (var record in BusManager.Instance.GetAllRecords())
        {
            ShiftAssignment saved =
                save?.assignments.Find(a => a.busID == record.busID);

            BuildCalendar(record.busID, today, saved);
        }

        if (logDispatch)
        {
            Debug.Log(
                $"[FleetDispatcher] Loaded/built calendars for {_calendarByBus.Count} buses.");
        }
    }

    private void EnsureCalendarForToday(int busID, int today)
    {
        if (_calendarDayByBus.TryGetValue(busID, out int cachedDay) &&
            cachedDay == today)
        {
            return;
        }

        BuildCalendar(busID, today, savedAssignment: null);
    }

    private void BuildCalendar(
        int busID,
        int day,
        ShiftAssignment savedAssignment)
    {
        string preferredRoute =
            savedAssignment?.preferredRouteAtGen;

        var blocks =
            _shiftGen.Generate(day, busID, preferredRoute);

        if (savedAssignment != null &&
            savedAssignment.shiftDayNumber == day &&
            savedAssignment.blockStatuses != null &&
            savedAssignment.blockStatuses.Count == blocks.Count)
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                blocks[i].completed =
                    savedAssignment.blockStatuses[i].completed;

                blocks[i].missed =
                    savedAssignment.blockStatuses[i].missed;
            }

            _currentBlockIndex[busID] =
                Mathf.Clamp(
                    savedAssignment.currentBlockIndex,
                    0,
                    Mathf.Max(0, blocks.Count - 1));
        }
        else
        {
            _currentBlockIndex[busID] = 0;
        }

        _calendarByBus[busID] = blocks;
        _calendarDayByBus[busID] = day;
    }

    public void SaveNow()
    {
        if (BusManager.Instance == null)
            return;

        var data =
            SaveService.Load() ?? new ShiftSaveData();

        data.assignments.RemoveAll(
            a => _calendarByBus.ContainsKey(a.busID));

        foreach (var kv in _calendarByBus)
        {
            int busID = kv.Key;
            var blocks = kv.Value;

            var statuses =
                new List<BlockStatus>(blocks.Count);

            foreach (var b in blocks)
            {
                statuses.Add(
                    new BlockStatus
                    {
                        completed = b.completed,
                        missed = b.missed
                    });
            }

            int fallbackDay =
                SimClock.Instance != null
                    ? SimClock.Instance.GameDayNumber
                    : 0;

            data.assignments.Add(
                new ShiftAssignment
                {
                    busID = busID,

                    fleetNumber =
                        BusManager.Instance
                            .GetRecord(busID)?
                            .controller?
                            .fleetNumber ?? -1,

                    shiftDayNumber =
                        _calendarDayByBus.GetValueOrDefault(
                            busID,
                            fallbackDay),

                    preferredRouteAtGen = null,

                    currentBlockIndex =
                        _currentBlockIndex.GetValueOrDefault(
                            busID,
                            0),

                    blockStatuses = statuses
                });
        }

        SaveService.Save(data);
    }

    private void OnApplicationQuit()
    {
        SaveNow();
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused)
            SaveNow();
    }

    private DailyShiftBlock GetCurrentBlock(int busID)
    {
        if (SimClock.Instance == null)
            return null;

        EnsureCalendarForToday(
            busID,
            SimClock.Instance.GameDayNumber);

        if (!_calendarByBus.TryGetValue(
                busID,
                out var blocks) ||
            blocks == null ||
            blocks.Count == 0)
        {
            return null;
        }

        int idx =
            _currentBlockIndex.GetValueOrDefault(
                busID,
                0);

        idx =
            Mathf.Clamp(
                idx,
                0,
                blocks.Count - 1);

        float now =
            SimClock.Instance.AbsoluteGameMinutes;

        bool advanced = false;

        while (idx < blocks.Count - 1 &&
               now > blocks[idx].windowEndMinutes)
        {
            if (!blocks[idx].completed)
                blocks[idx].missed = true;

            idx++;
            advanced = true;
        }

        _currentBlockIndex[busID] = idx;

        if (advanced)
            SaveNow();

        return blocks[idx];
    }

    private void RunDispatchPass(bool isStartup)
    {
        if (BusManager.Instance == null ||
            SimClock.Instance == null)
        {
            return;
        }

        int assigned = 0;
        int parkedAtTerminal = 0;
        int leftIdle = 0;
        int ownSlot = 0;

        foreach (var record in BusManager.Instance.GetAllRecords())
        {
            if (record == null)
                continue;

            if (record.isActive)
                continue;

            // [ADD] Hard, unambiguous guard alongside isActive -- isActive
            // is DERIVED from controller.State, which is just whatever it
            // was frozen at the instant a player/free-agent possession
            // disabled this component (see BusSelectMenu.ApplyFleetPossession).
            // That's usually not Idle, so isActive usually already catches
            // this, but .enabled is the actual, direct signal that nothing
            // is driving this NPC right now -- no state to get out of sync,
            // no possession path to remember to update it. A disabled
            // controller must never be dispatched: it can't run the coroutine
            // this would try to hand it, and forcing state on it directly
            // would reposition the bus a player might currently be sitting in.
            if (record.controller != null && !record.controller.enabled)
            {
                // [ADD] Left alone, a due slot this busID still legitimately
                // holds (see BusSelectMenu.ApplyFleetPossession's own
                // comment) would just never be served while the bus is tied
                // up -- no bus ever shows for that departure, a real trip
                // silently drops off the route. Hand it to a genuine spare
                // instead, same as a relief handoff would.
                TryReassignSlotAwayFromUnavailableBus(record);
                continue;
            }

            // First priority:
            // use a real scheduler-assigned slot belonging to this bus.
            if (TryDispatchPreAssignedSlot(record))
            {
                assigned++;
                ownSlot++;
                continue;
            }

            // A bus may have a real slot that isn't live or near departure
            // yet. Never hijack that bus with a calendar guess.
            if (BusScheduler.Instance != null &&
                BusScheduler.Instance.TryGetAssignedSlot(
                    record.busID,
                    out _))
            {
                leftIdle++;
                continue;
            }

            var block =
                GetCurrentBlock(record.busID);

            if (block == null)
            {
                leftIdle++;
                continue;
            }

            if (BusScheduler.Instance == null)
            {
                leftIdle++;
                continue;
            }

            var route =
                BusScheduler.Instance.GetRouteData(
                    block.routeNumber);

            if (route == null)
            {
                leftIdle++;
                continue;
            }

            if (!TryDispatchOntoRoute(
                    record,
                    route,
                    out string outcome))
            {
                leftIdle++;
            }
            else if (outcome == "midtrip" ||
                     outcome == "parked")
            {
                assigned++;

                if (outcome == "parked")
                    parkedAtTerminal++;
            }
        }

        if (logDispatch)
        {
            Debug.Log(
                $"[FleetDispatcher] Pass " +
                $"({(isStartup ? "startup" : "tick")}): " +
                $"{assigned} dispatched " +
                $"({ownSlot} via own pre-assigned slot, " +
                $"{parkedAtTerminal} parked at terminal), " +
                $"{leftIdle} idle.");
        }
    }

    /// <summary>[ADD] Covers the gap left by the possessed-bus fix in
    /// BusSelectMenu.ApplyFleetPossession/FreeAgentBusIDs: that fix correctly
    /// stops a disabled NPCBusController from ever being dispatched (it can't
    /// physically run the coroutine, and forcing state on it would reposition
    /// a bus the player might be sitting in) -- but on its own that just means
    /// whatever due slot the busID still legitimately holds silently never
    /// gets served. Same real-world gap as an unexpected breakdown: find a
    /// genuinely idle spare and hand the slot to it instead, via the same
    /// TransferSlotToBus machinery an NPC-to-NPC relief handoff would use.
    /// Only acts once the slot is actually due (IsSlotLive) -- no need to
    /// reshuffle a bus away early just because it's momentarily possessed;
    /// it may well be free again before its own departure time comes.</summary>
    private void TryReassignSlotAwayFromUnavailableBus(BusRecord record)
    {
        if (BusScheduler.Instance == null || SimClock.Instance == null) return;
        if (!BusScheduler.Instance.TryGetAssignedSlot(record.busID, out var slot) || slot == null) return;
        if (slot.state != SlotState.AssignedNPC) return; // not ours to touch (InService/AssignedPlayer/etc.)
        if (!BusScheduler.Instance.IsSlotLive(slot)) return; // not due yet -- the bus may be free again in time

        int spareBusID = BusManager.Instance.GetIdleBusForRoute(slot.routeNumber);
        if (spareBusID < 0)
        {
            if (logDispatch)
                Debug.LogWarning($"[FleetDispatcher] Bus#{record.busID} can't serve its due " +
                                  $"{slot.FullRouteLabel} {BusScheduler.MinutesToTimeString(slot.scheduledDeparture)} " +
                                  "slot (possessed/disabled) and no spare bus is available to cover it.");
            return;
        }

        if (BusScheduler.Instance.TransferSlotToBus(record.busID, spareBusID) != null)
        {
            BusManager.Instance.ClaimBusForHandoff(spareBusID); // same claim-out-of-idle-pool step a relief handoff does
            if (logDispatch)
                Debug.Log($"[FleetDispatcher] Bus#{record.busID} unavailable (possessed) for its due " +
                          $"{slot.FullRouteLabel} slot -- handed off to spare Bus#{spareBusID}.");
        }
    }

    private bool TryDispatchPreAssignedSlot(
        BusRecord record)
    {
        if (BusScheduler.Instance == null ||
            SimClock.Instance == null ||
            record == null)
        {
            return false;
        }

        if (!BusScheduler.Instance.TryGetAssignedSlot(
                record.busID,
                out var slot) ||
            slot == null)
        {
            return false;
        }

        var route =
            BusScheduler.Instance.GetRouteData(
                slot.routeNumber);

        if (route == null)
            return false;

        float now =
            SimClock.Instance.AbsoluteGameMinutes;

        // Slot is already active/live.
        if (BusScheduler.Instance.IsSlotLive(slot))
        {
            // isActive/isIdle/assignedRoute/activeSlot are gone -- see
            // BusRecord's own comment. AssignRouteWithProgress below is what
            // actually moves State off Idle.

            record.controller.variantLetter =
                slot.variantLetter;

            record.controller.AssignRouteWithProgress(
                route,
                slot.isOutbound,
                slot);

            record.controller.SetIdle(false);

            if (logDispatch)
            {
                Debug.Log(
                    $"[FleetDispatcher] Bus#{record.busID} → " +
                    $"OWN pre-assigned Route {route.routeNumber} " +
                    $"(mid-trip, resumed).");
            }

            return true;
        }

        // Slot is close enough to departure that we can park the bus
        // at the terminal.
        float untilDeparture =
            slot.scheduledDeparture - now;

        if (untilDeparture >= 0f &&
            untilDeparture <= terminalWaitWindowMinutes)
        {
            // isActive/isIdle/assignedRoute/activeSlot are gone -- see
            // BusRecord's own comment. SpawnParkedAtTerminal below sets
            // State = AtTerminal, which is what actually moves it off Idle.

            record.controller.SpawnParkedAtTerminal(
                route,
                slot.isOutbound,
                slot);

            if (logDispatch)
            {
                Debug.Log(
                    $"[FleetDispatcher] Bus#{record.busID} → " +
                    $"OWN pre-assigned Route {route.routeNumber} " +
                    $"(parked, dep @ " +
                    $"{BusScheduler.MinutesToTimeString(slot.scheduledDeparture)}).");
            }

            return true;
        }

        return false;
    }

    public bool TryDispatchIdleBus(
        BusRecord record)
    {
        if (record == null ||
            record.isActive ||
            BusScheduler.Instance == null ||
            SimClock.Instance == null)
        {
            return false;
        }

        // First use a real scheduler-assigned slot.
        if (TryDispatchPreAssignedSlot(record))
            return true;

        // Never hijack a bus that already owns a scheduler slot.
        if (BusScheduler.Instance.TryGetAssignedSlot(
                record.busID,
                out _))
        {
            return false;
        }

        var block =
            GetCurrentBlock(record.busID);

        BusRouteData preferredRoute =
            block != null
                ? BusScheduler.Instance.GetRouteData(
                    block.routeNumber)
                : null;

        // Try the route from the generated calendar first.
        if (preferredRoute != null &&
            TryDispatchOntoRoute(
                record,
                preferredRoute,
                out _))
        {
            return true;
        }

        // If the preferred route cannot accept this bus, find another
        // near-due route.
        BusRouteData bestRoute = null;
        float bestUntilDeparture = float.MaxValue;

        float now =
            SimClock.Instance.AbsoluteGameMinutes;

        foreach (var route in BusScheduler.Instance.managedRoutes)
        {
            if (route == null)
                continue;

            if (route == preferredRoute)
                continue;

            if (BusScheduler.Instance.CountActiveBusesOnRoute(
                    route.routeNumber) >=
                route.maxBusesAllowed)
            {
                continue;
            }

            var slot =
                BusScheduler.Instance.GetNextUnassignedSlot(
                    route.routeNumber,
                    "");

            if (slot == null)
                continue;

            float until =
                slot.scheduledDeparture - now;

            if (until < 0f ||
                until > terminalWaitWindowMinutes)
            {
                continue;
            }

            if (until < bestUntilDeparture)
            {
                bestUntilDeparture = until;
                bestRoute = route;
            }
        }

        if (bestRoute != null &&
            TryDispatchOntoRoute(
                record,
                bestRoute,
                out _))
        {
            if (logDispatch)
            {
                Debug.Log(
                    $"[FleetDispatcher] Bus#{record.busID} " +
                    $"overflow → Route {bestRoute.routeNumber} " +
                    $"(best near-due candidate).");
            }

            return true;
        }

        return false;
    }

    private bool TryDispatchOntoRoute(
        BusRecord record,
        BusRouteData route,
        out string outcome)
    {
        outcome = null;

        if (record == null ||
            route == null ||
            BusScheduler.Instance == null ||
            SimClock.Instance == null)
        {
            return false;
        }

        float now =
            SimClock.Instance.AbsoluteGameMinutes;

        float tripMinutes =
            route.oneWayTripMinutes > 0
                ? route.oneWayTripMinutes
                : 45f;

        // Try outbound and inbound.
        foreach (bool outbound in new[] { true, false })
        {
            int liveNow =
                BusScheduler.Instance.CountActiveBusesOnRoute(
                    route.routeNumber);

            if (liveNow >= route.maxBusesAllowed)
                continue;

            // First try to claim a slot that is already live.
            var liveSlot =
                BusScheduler.Instance.TryClaimLiveSlotPublic(
                    route.routeNumber,
                    outbound,
                    now,
                    tripMinutes);

            if (liveSlot != null)
            {
                BusScheduler.Instance.AssignBusToSlot(
                    liveSlot,
                    record.busID);

                if (!BusScheduler.Instance.TryGetAssignedSlot(
                        record.busID,
                        out var confirmed) ||
                    confirmed != liveSlot)
                {
                    continue;
                }

                // isActive/isIdle/assignedRoute/activeSlot are gone -- see
                // BusRecord's own comment. AssignRouteWithProgress below is
                // what actually moves State off Idle.

                record.controller.variantLetter =
                    liveSlot.variantLetter;

                record.controller.AssignRouteWithProgress(
                    route,
                    liveSlot.isOutbound,
                    liveSlot);

                record.controller.SetIdle(false);

                outcome = "midtrip";

                if (logDispatch)
                {
                    Debug.Log(
                        $"[FleetDispatcher] Bus#{record.busID} → " +
                        $"Route {route.routeNumber} " +
                        $"outbound={outbound} MID-TRIP.");
                }

                return true;
            }

            // Otherwise, try the next departure that is close enough
            // to park at the terminal.
            var nextSlot =
                BusScheduler.Instance.GetNextUnassignedSlot(
                    route.routeNumber,
                    "");

            if (nextSlot != null &&
                nextSlot.isOutbound == outbound &&
                (nextSlot.scheduledDeparture - now) <=
                    terminalWaitWindowMinutes &&
                (nextSlot.scheduledDeparture - now) > 0f)
            {
                BusScheduler.Instance.AssignBusToSlot(
                    nextSlot,
                    record.busID);

                if (!BusScheduler.Instance.TryGetAssignedSlot(
                        record.busID,
                        out var confirmedParked) ||
                    confirmedParked != nextSlot)
                {
                    continue;
                }

                // isActive/isIdle/assignedRoute/activeSlot are gone -- see
                // BusRecord's own comment. SpawnParkedAtTerminal below sets
                // State = AtTerminal, which is what actually moves it off Idle.

                record.controller.SpawnParkedAtTerminal(
                    route,
                    outbound,
                    nextSlot);

                outcome = "parked";

                if (logDispatch)
                {
                    Debug.Log(
                        $"[FleetDispatcher] Bus#{record.busID} → " +
                        $"Route {route.routeNumber} " +
                        $"outbound={outbound} PARKED AT TERMINAL, " +
                        $"dep @ " +
                        $"{BusScheduler.MinutesToTimeString(nextSlot.scheduledDeparture)}.");
                }

                return true;
            }
        }

        return false;
    }
}