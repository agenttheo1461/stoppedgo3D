using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public enum SlotState { Unassigned, AssignedNPC, AssignedPlayer, InService, Completed }

[Serializable]
public class TimetableSlot
{
    public string routeNumber;
    public string variantLetter = "";
    public bool isOutbound;
    public float scheduledDeparture; // ABSOLUTE minutes: dayNumber * 1440 + minute-of-day
    public int dayNumber;

    public SlotState state = SlotState.Unassigned;
    public int assignedBusID = -1;

    public int chainLegIndex = 0;
    /// <summary>Cumulative in-service (on-route trip) minutes of this bus's chain through and including this leg. 0 = not stamped; see BusScheduler.AccumOf.</summary>
    public float serviceMinutesAccum = 0f;

    public float actualDeparture = -1f;
    public float latenessMinutes = 0f;

    /// <summary>True for the route's short turn: a variant whose letter is the short-turn symbol '~' ("116~").
    /// Purely informational; a short turn is scheduled, assigned and driven exactly like any other variant.</summary>
    public bool IsShortTurn => variantLetter == BusRouteData.ShortTurnSymbol;

    public bool isPlayerSlot => state == SlotState.AssignedPlayer;
    public string FullRouteLabel => BusRouteData.RouteLabel(routeNumber, variantLetter);
    public string DirectionLabel => isOutbound ? "A→Z" : "Z→A";
}

public partial class BusScheduler : MonoBehaviour
{
    public static BusScheduler Instance { get; private set; }

    // [CHANGE] Multiplayer: was `public const int PLAYER_BUS_ID = -2;` -- a single hardcoded
    // sentinel used as one shared dictionary key (_slotByBus[PLAYER_BUS_ID]) throughout this
    // file, with zero way to tell two different human players apart. Converted to a computed
    // property driven by an ambient "who is this scheduler call actually for" context
    // (BeginPlayerContext), which is ONLY ever set by NetworkGameBridge's server-side RPC handlers
    // while processing a specific remote client's request. Outside that scope -- which covers
    // every single-player call site and every call the HOST makes for its own local play,
    // completely unchanged -- this still returns exactly -2, the same value it always has.
    // Confirmed safe to convert from const: grepped the whole project for `case ... PLAYER_BUS_ID`
    // or `case -2:` (which would require a compile-time constant) -- no hits.
    private static ulong? _networkPlayerContext;

    public static int PLAYER_BUS_ID => _networkPlayerContext.HasValue
        ? (int)(-1000L - (long)_networkPlayerContext.Value)
        : -2;

    /// <summary>Wrap a scheduler call made ON THE SERVER on behalf of a specific remote client in
    /// this (see NetworkGameBridge's adopt-slot ServerRpc), so PLAYER_BUS_ID resolves to that
    /// client's own unique sentinel for the duration instead of colliding with -2 (the host's own
    /// local player) or with any other remote client. Never used outside that one RPC handler.</summary>
    public readonly struct PlayerContextScope : System.IDisposable
    {
        private readonly ulong? _previous;
        public PlayerContextScope(ulong clientId) { _previous = _networkPlayerContext; _networkPlayerContext = clientId; }
        public void Dispose() => _networkPlayerContext = _previous;
    }
    public static PlayerContextScope BeginPlayerContext(ulong clientId) => new PlayerContextScope(clientId);

    /// <summary>-2 (single-player/host) or any per-client sentinel from BeginPlayerContext (always
    /// &lt;= -1000 by construction) both count as "a player," not just the original -2.</summary>
    public static bool IsPlayer(int busID) => busID == -2 || busID <= -1000;

    /// <summary>[ADD] The REAL bug behind "teleported to a terminal idle zone for a later shift"
    /// that long predates multiplayer: a physical bus (e.g. Bus#96, fleet 1909) that a player picks
    /// up via the schedule board -- for a completely different route than whatever it was already
    /// pre-assigned to run -- keeps its OWN independent, day-generation-time NPC schedule sitting
    /// in _allSlots (state == AssignedNPC, assignedBusID == that same real busID), totally unaware
    /// the physical bus is now secretly a human's. Nothing ever cancelled/reassigned it, because
    /// the player's session lives under a completely different key (PLAYER_BUS_ID's sentinel, not
    /// the bus's own real busID) in _slotByBus. When that leftover NPC slot's own departure (or
    /// the early-dispatch window before it) comes due, the dispatcher finds `!_slotByBus.
    /// ContainsKey(96)` true (nothing's there under the REAL id) and happily dispatches/teleports
    /// the physical GameObject a human is currently sitting in.
    ///
    /// Fix: every place that's about to treat a slot's real assignedBusID as "just an NPC" (the
    /// early-dispatch loop and HandleSlotDue's AssignedNPC case) checks this first and skips if the
    /// physical bus is secretly possessed by anyone right now. Two sources, by design (the user's
    /// own proposed shape): PlayerHandoff.Instance.PossessedPhysicalBusID catches the host's own
    /// local possession without needing any networking involved at all (this is what makes it work
    /// correctly in single-player, where SchedulerTick runs the exact same code); NetworkGameBridge's
    /// possession registry is checked as a second, independent source so a REMOTE client's
    /// physically-possessed bus is caught too (that registry is authoritative host-side regardless
    /// of whose local PlayerHandoff it belongs to). Either one matching is enough.</summary>
    public static bool IsPhysicalBusSecretlyPossessed(int physicalBusID)
    {
        if (PlayerHandoff.Instance != null && PlayerHandoff.Instance.PossessedPhysicalBusID == physicalBusID) return true;
        if (NetworkGameBridge.Instance != null && NetworkGameBridge.Instance.IsPossessed(physicalBusID)) return true;
        return false;
    }

    /// <summary>Reverses BeginPlayerContext's encoding. Only meaningful when busID &lt;= -1000
    /// (a real per-client sentinel, not the classic -2).</summary>
    public static ulong DecodeClientId(int playerBusID) => (ulong)(-1000L - (long)playerBusID);

    /// <summary>[ADD] Delivers a relief-search result to whoever actually asked for it. Both
    /// relief-search call sites (the real player flow via OnReplacementNeeded, and the
    /// debug/CmdBreakdown flow via InitiateReliefSearch) used to bare-call
    /// PlayerHandoff.Instance?.OnReplacementFound/OnReplacementSearchFailed directly -- which,
    /// on the HOST, always means the HOST's OWN local player, never an actual remote client that
    /// requested relief through NetworkGameBridge's RequestRelief RPC. For -2 (single-player/host,
    /// completely unchanged) this still calls PlayerHandoff.Instance directly; for a per-client
    /// sentinel it decodes the real clientId and sends a targeted ClientRpc instead.</summary>
    public static void NotifyReplacementFound(int playerBusID, int replacementBusID)
    {
        if (playerBusID <= -1000)
        {
            NetworkGameBridge.Instance?.SendReplacementFoundToClient(DecodeClientId(playerBusID), replacementBusID);
            return;
        }
        PlayerHandoff.Instance?.OnReplacementFound(replacementBusID);
    }

    /// <summary>Counterpart to NotifyReplacementFound for the no-eligible-bus case.</summary>
    public static void NotifyReplacementSearchFailed(int playerBusID)
    {
        if (playerBusID <= -1000)
        {
            NetworkGameBridge.Instance?.SendReplacementSearchFailedToClient(DecodeClientId(playerBusID));
            return;
        }
        PlayerHandoff.Instance?.OnReplacementSearchFailed();
    }

    /// <summary>Bus IDs currently possessed by a free-agent brain (AIBusController /
    /// CHIP) instead of running the normal scheduled fleet. Checked in CanAssign
    /// so slot generation/chain-building/handoff never tries to claim them —
    /// they're driving off their own judgment, not a TimetableSlot.</summary>
    public static readonly HashSet<int> FreeAgentBusIDs = new();

    [Header("Routes")]
    public BusRouteData[] managedRoutes;

    [Header("Schedule Horizon")]
    [Range(1, 14)] public int scheduleDaysAhead = 3;
    public float scheduleRefillThresholdMinutes = 120f;

    [Header("Lateness Recovery")]
    public float latenessDwellCutoffMinutes = 3f;
    public float expressDeadRunThresholdMinutes = 480f;
    [Range(0.1f, 1f)] public float minLateDwellMultiplier = 0.3f;

    [Header("Dynamic Lateness Tracking")]
    public float dynamicLatenessTickInterval = 2f;
    private float _dynamicLatenessTimer = 0f;

    [Header("Bus Cycling")]
    // Retirement is defined in SERVICE MINUTES, converted to laps PER ROUTE
    // (laps = round(serviceMinutes / that route's trip minutes), min 1).
    // There is no flat lap-count fallback any more. Set
    // targetServiceMinutesBeforeRetirement <= 0 to disable retirement.
    public const float DefaultServiceMinutesBeforeRetirement = 180f;
    public const float DefaultTripMinutes = 45f;

    /// <summary>Service minutes THIS route gets out of the shared budget: the
    /// global target split evenly across the route plus its configured
    /// interlined routes (so an interlined block shares one budget, e.g. 180
    /// over Route 1 + 87 + 136 = 60 each). No interlining = the full target.</summary>
    public static float ServiceShareFor(string routeNumber)
    {
        float total = Instance != null && Instance.targetServiceMinutesBeforeRetirement > 0f
            ? Instance.targetServiceMinutesBeforeRetirement : DefaultServiceMinutesBeforeRetirement;
        if (Instance == null || routeNumber == null || !Instance.enableInterlining) return total;
        int n = 1;
        foreach (var rn in Instance.GetInterlinedRoutes(routeNumber))
            if (!string.IsNullOrEmpty(rn) && rn != routeNumber && Instance.GetRouteData(rn) != null) n++;
        return total / n;
    }

    /// <summary>The service-minutes -> laps converter (min 1 lap).</summary>
    public static int ServiceMinutesToLaps(float serviceMinutes, float tripMinutes)
    {
        if (serviceMinutes <= 0f) serviceMinutes = DefaultServiceMinutesBeforeRetirement;
        if (tripMinutes <= 0f) tripMinutes = DefaultTripMinutes;
        return Mathf.Max(1, Mathf.RoundToInt(serviceMinutes / tripMinutes));
    }

    /// <summary>Laps still to run INCLUDING the leg about to depart, from the
    /// service minutes the bus has already driven on this rotation.</summary>
    public static int RemainingLapsForRoute(BusRouteData route, float serviceMinutesBefore, float tripMinutes)
    {
        int full = LapsForRoute(route, tripMinutes);
        if (serviceMinutesBefore <= 0f) return full;
        float budget = ServiceShareFor(route != null ? route.routeNumber : null);
        return Mathf.Clamp(ServiceMinutesToLaps(budget - serviceMinutesBefore, tripMinutes), 1, full);
    }

    /// <summary>Minimum minutes between "now" and a departure offered on the
    /// shift / main menu, so the player has time to reach the terminal.
    /// Waived 00:00-04:00 (sparse night service).</summary>
    public const float BoardingLeadMinutes = 20f;
    public static float BoardingLeadFor(float nowAbsolute)
    {
        float rel = nowAbsolute % 1440f;
        return rel < 240f ? 0f : BoardingLeadMinutes;
    }

    /// <summary>Laps for a route from the scheduler's service-minute target. Static so UI works with no
    /// scheduler instance.</summary>
    public static int LapsForRoute(BusRouteData route, float tripMinutesOverride = 0f)
    {
        if (route == null) return ServiceMinutesToLaps(0f, 0f);
        float service = ServiceShareFor(route.routeNumber);
        float trip = tripMinutesOverride > 0f ? tripMinutesOverride : route.oneWayTripMinutes;
        return ServiceMinutesToLaps(service, trip);
    }

    [Tooltip("Target in-service driving time (minutes) a bus stays on its route -- ONLY time actually on the route (trip minutes); layover, deadhead and depot time never count. A bus stays in service before it's eligible to retire from its current route. Each bus's effective lap count is derived from this divided by its current route's oneWayTripMinutes (rounded, minimum 1), so a short high-frequency route and a long route both retire on a comparable real-time cadence instead of the same raw lap number regardless of trip length.")]
    public float targetServiceMinutesBeforeRetirement = 180f;

    [Header("Schedule Speed Scaling (Count II-b)")]
    [Tooltip("Physical driving-speed multiplier a bus applies for the schedule window it's currently in, derived as 100 / tripTimeMultiplierPercent (e.g. a 50% overnight window wants ~2x speed on empty roads, a 105% peak window wants ~0.95x). Clamped to [minScheduleSpeedMultiplier, maxScheduleSpeedMultiplier] before NPCBusController ever sees it -- the raw inverse can go well past 1.5x for an aggressively-scaled overnight window, which is untested against junction-approach timing, collision avoidance, and audio at that speed. Read via GetScheduleSpeedMultiplier.")]
    public float minScheduleSpeedMultiplier = 0.85f;
    public float maxScheduleSpeedMultiplier = 1.35f;

    [Header("Interlined Routes")]
    [Tooltip("When a bus retires (or its chain runs dry) on its current route, try handing it straight onto another route its OWN depot also serves instead of sending it idle. This is what lets one physical bus cover Route 1 in the morning and Route 87 in the afternoon off the same shared depot pool, instead of idling between them.")]
    [UnityEngine.Serialization.FormerlySerializedAs("enableInterlining")]
    public bool enableInterlining = true;
    [Tooltip("Interlined routes: if the just-completed route has an entry here, those routes are tried FIRST, in listed order, before falling back to trying every other managed route. Leave empty to just try every managed route (declaration order).")]
    [UnityEngine.Serialization.FormerlySerializedAs("handoffPairings")]
    public List<InterlinedRoutes> interlinedRoutes = new();
    [Tooltip("Minutes added on top of the normal layover before a handed-off bus can depart on its NEW route — represents deadhead travel time between the two routes' terminals. Real inter-terminal distance isn't modeled, so this is a flat estimate; raise it for depots whose paired routes are geographically far apart.")]
    public float crossRouteDeadheadMinutes = 8f;

    [Serializable]
    public class InterlinedRoutes
    {
        public string routeNumber;
        [UnityEngine.Serialization.FormerlySerializedAs("preferredHandoffRoutes")]
        public string[] interlinedWith;
    }

    public enum TerminalEnd { Any, A, Z, SameAsFrom }

    [Serializable]
    public class InterlineTerminalRule
    {
        [Tooltip("Route the bus is finishing a trip on.")]
        public string fromRoute;
        [Tooltip("Terminal of fromRoute the bus must finish at. Any = A or Z.")]
        public TerminalEnd fromEnd = TerminalEnd.Any;
        [Tooltip("Route the bus continues onto.")]
        public string toRoute;
        [Tooltip("Terminal of toRoute it departs from. SameAsFrom = A->A and Z->Z (e.g. 1/87/201 at A-A-A or Z-Z-Z). Any = A or Z.")]
        public TerminalEnd toEnd = TerminalEnd.SameAsFrom;
        [Tooltip("Also apply the mirror (toRoute -> fromRoute). Off for one-way links like 199 (Z) -> 101 (A).")]
        public bool bothWays = true;
    }

    [Tooltip("Optional terminal restrictions. If ANY rule exists for a from->to route pair, ONLY the terminals the rules allow may be interlined for that pair; pairs with no rule stay unrestricted. Terminal A = the route's A end (outbound trips depart from it), Z = the other end.")]
    public List<InterlineTerminalRule> interlineTerminalRules = new();

    // Expands rules into concrete (routeX, endX) -> (routeY, endY) links. end: true = A, false = Z.
    private IEnumerable<(string r1, bool e1, string r2, bool e2)> ExpandTerminalRules()
    {
        foreach (var r in interlineTerminalRules)
        {
            if (r == null || string.IsNullOrEmpty(r.fromRoute) || string.IsNullOrEmpty(r.toRoute)) continue;
            foreach (bool fe in new[] { true, false })
            {
                if (r.fromEnd == TerminalEnd.A && !fe) continue;
                if (r.fromEnd == TerminalEnd.Z && fe) continue;
                foreach (bool te in new[] { true, false })
                {
                    if (r.toEnd == TerminalEnd.A && !te) continue;
                    if (r.toEnd == TerminalEnd.Z && te) continue;
                    if (r.toEnd == TerminalEnd.SameAsFrom && te != fe) continue;
                    yield return (r.fromRoute, fe, r.toRoute, te);
                    if (r.bothWays) yield return (r.toRoute, te, r.fromRoute, fe);
                }
            }
        }
    }

    /// <summary>True if the terminal rules explicitly allow r1's end e1 -> r2's end e2
    /// (true = A, false = Z). Used by the Interline Editor window.</summary>
    public bool HasExplicitInterlineLink(string r1, bool e1, string r2, bool e2)
    {
        foreach (var l in ExpandTerminalRules())
            if (l.r1 == r1 && l.e1 == e1 && l.r2 == r2 && l.e2 == e2) return true;
        return false;
    }

    /// <summary>Which directions of `to` a bus finishing `from` (at the end its
    /// completed trip left it: outbound = Z, inbound = A) may depart. Pairs with
    /// no rule are unrestricted (both true).</summary>
    private bool ResolveInterlineDepartureDirs(string from, bool? completedOutbound, string to, out bool allowOutbound, out bool allowInbound)
    {
        allowOutbound = allowInbound = false;
        bool restricted = false;
        foreach (var l in ExpandTerminalRules())
        {
            if (l.r1 != from || l.r2 != to) continue;
            restricted = true;
            // Completed outbound = finished at Z (e1 false); inbound = finished at A (e1 true).
            if (completedOutbound.HasValue && l.e1 == completedOutbound.Value) continue;
            if (l.e2) allowOutbound = true; else allowInbound = true; // departing from A = outbound trip
        }
        if (!restricted) { allowOutbound = allowInbound = true; return true; }
        return allowOutbound || allowInbound;
    }

    /// <summary>Interlined routes for a route: the explicit list plus any
    /// route a terminal rule links it to (empty if none configured).</summary>
    public string[] GetInterlinedRoutes(string routeNumber)
    {
        var set = new List<string>();
        var p = interlinedRoutes.Find(x => x.routeNumber == routeNumber);
        if (p?.interlinedWith != null) foreach (var rn in p.interlinedWith) if (!set.Contains(rn)) set.Add(rn);
        foreach (var l in ExpandTerminalRules())
            if (l.r1 == routeNumber && !set.Contains(l.r2)) set.Add(l.r2);
        return set.ToArray();
    }

    [Header("Terminal Hold")]
    [Tooltip("A bus that finishes a leg is only kept at the terminal for a next departure this many minutes away or less. Anything later sends it to the depot instead (a slot already pre-assigned to it stays assigned and is dispatched when due), so it neither parks for hours nor counts toward the route cap while doing so.")]
    public float maxTerminalHoldMinutes = 90f;

    [Header("Slot Selection")]
    [Tooltip("How close to departure a bus may be launched early when claiming a near-future slot.")]
    public float earlyDispatchWindowMinutes = 10f;

    [Header("Layover")]
    [Tooltip("Minimum minutes a bus must sit at the terminal after finishing a trip before it can be chained onto its next departure. Without this, TopUpChain/CompleteSlot will happily hand a bus a next leg scheduled for the exact instant it arrives — zero rest, looks like the bus 'never stays' at the terminal.")]
    public float minLayoverMinutes = 5f;

    [Header("Debug")]
    public bool logDispatches = true;
    public float scheduleReportInterval = 15f;
    public float latenessThreshold = 0.5f;

    public int LastBuiltSlotCount { get; private set; }
    public int LastBuiltRouteCount { get; private set; }
    public int LastBuiltDays { get; private set; }

    private float _reportTimer;
    private int _dayAnchor;

    /// <summary>Single source of truth for "now" — ABSOLUTE, matching
    /// TimetableSlot.scheduledDeparture's scale. Kept under the old property
    /// names for source compatibility; there is no wrapped-time value exposed
    /// anywhere in this class anymore.</summary>
    public float GameTimeMinutes    => SimClock.Instance.AbsoluteGameMinutes;
    public float RealTimeElapsed    => SimClock.Instance.RealTimeElapsed;
    public float CurrentGameMinutes => SimClock.Instance.AbsoluteGameMinutes;
    public string GameTimeString    => SimClock.Instance.GameTimeString;
    public string RealTimeString    => SimClock.Instance.RealTimeString;

    // ── State — one list, one dictionary, nothing else ─────────────────────
    private readonly List<TimetableSlot> _allSlots = new();
    private readonly Dictionary<int, TimetableSlot> _slotByBus = new();
    // (removed) _lapCount — laps are now derived from TimetableSlot.chainLegIndex,
    // so there is no per-bus counter to reseed or desync.
    private readonly HashSet<int> _completedThisFrame = new();
    private readonly HashSet<int> _generatedDayNumbers = new(); // per-route not needed: keyed globally per (route,day)
    private readonly HashSet<(string route, int day)> _generatedRouteDays = new();
    private int _processedUpTo = 0;

    // LatenessTracker is an existing, separate dependency — untouched, not
    // part of this rewrite (its source wasn't provided). Wired exactly as it
    // always was.
    private LatenessTracker _lateness;

    public event Action<TimetableSlot> OnDispatchBus;
    /// <summary>Fires when any bus (player or NPC) completes a trip. bool = was the player. int = physical bus ID.</summary>
    public event Action<TimetableSlot, bool, int> OnSlotCompleted;
    public event Action<TimetableSlot> OnAssignedDeparture;
    public event Action<TimetableSlot> OnPlayerSlotReady;
    public event Action<TimetableSlot> OnRotateBus;
    public event Action<TimetableSlot> OnIdleBus;
    public event Action<int, string> OnBusRetiredFromRoute;
    public event Action<int> OnReplacementNeeded;

    public List<TimetableSlot> AllSlots => _allSlots;

    // ═════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═════════════════════════════════════════════════════════════════════
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        _reportTimer = scheduleReportInterval;

        // The route list can carry the same route twice (the prefab's first entry plus a scene override
        // both pointed at route 1). Keep the first of each route number; nulls go too.
        if (managedRoutes != null)
        {
            var seenRoutes = new HashSet<string>();
            managedRoutes = managedRoutes.Where(r => r != null && seenRoutes.Add(r.routeNumber)).ToArray();
        }

        _lateness = new LatenessTracker
        {
            dwellCutoffMinutes = latenessDwellCutoffMinutes,
            minLateDwellMultiplier = minLateDwellMultiplier,
            expressDeadRunThresholdMinutes = expressDeadRunThresholdMinutes
        };
    }

    private void Start()
    {
        if (SimClock.Instance != null)
            SimClock.Instance.OnGameDayRolled += HandleDayRolled;

        // THE starting point: anchor to whatever "today" actually is right now,
        // derived straight from SimClock (which is itself pure UTC). This is
        // correct on a fresh install, after a five-minute break, or after the
        // app was closed for three real-world weeks — there is no session
        // state to have gone stale.
        _dayAnchor = SimClock.Instance != null ? SimClock.Instance.GameDayNumber : 0;

        foreach (var route in managedRoutes)
        {
            if (route == null) continue;
            ResolveVariantStops(route);
        }

        // Build yesterday through the forward horizon. "Yesterday" matters:
        // a bus that departed at 23:50 on an 8-hour route is still mid-trip
        // well into "today" — without it, launching mid-afternoon would find
        // a timetable with nothing behind it to explain why buses are already
        // out on the road.
        for (int d = _dayAnchor - 1; d < _dayAnchor + scheduleDaysAhead; d++)
            BuildDayForAllRoutes(d);

        SortAll();
        LastBuiltSlotCount = _allSlots.Count;
        LastBuiltRouteCount = managedRoutes.Length;
        LastBuiltDays = scheduleDaysAhead + 1;
        if (logDispatches)
            Debug.Log($"[BusScheduler] Anchor day {_dayAnchor}. Built {LastBuiltSlotCount} slots across {LastBuiltRouteCount} routes.");

        BootstrapLiveSlots();

        StartCoroutine(SchedulerTick());
        StartCoroutine(ScheduleRefillWatch());
    }

    private void OnDestroy()
    {
        if (SimClock.Instance != null)
            SimClock.Instance.OnGameDayRolled -= HandleDayRolled;
    }

    /// <summary>Scans everything already due at startup. Anything still within
    /// its own trip duration is "live" (a bus should already be out there
    /// mid-route) and gets deferred one frame so subscribers can hook up
    /// before OnDispatchBus fires. Anything older than that is just marked
    /// Completed — it's history, not something to dispatch into.</summary>
    private void BootstrapLiveSlots()
    {
        float now = SimClock.Instance.AbsoluteGameMinutes;
        var toDispatch = new List<TimetableSlot>();

        _processedUpTo = 0;
        while (_processedUpTo < _allSlots.Count && _allSlots[_processedUpTo].scheduledDeparture <= now)
        {
            var slot = _allSlots[_processedUpTo];
            float tripMinutes = TripMinutesForSlot(slot);
            float elapsed = now - slot.scheduledDeparture;

            // <= tripMinutes, not <, so a bus sitting at exactly 100%
            // progress still spawns instead of falling into the gap between
            // "still counts as live" and "next departure hasn't come up yet."
            bool stillLive = (slot.state == SlotState.Unassigned || slot.assignedBusID >= 0) && elapsed <= tripMinutes;

            if (stillLive)
            {

                toDispatch.Add(slot);
            }
            else if (slot.state == SlotState.Unassigned || slot.state == SlotState.AssignedNPC)
            {
                // History, not something to run. Pre-assigned legs that already
                // finished before load used to stay AssignedNPC forever and
                // read as "waiting +Nm" / late in the timetable. assignedBusID
                // is kept so the day's assignment record (and IsBusBusyDuring)
                // still reflects who was rostered on it.
                slot.state = SlotState.Completed;
            }
            _processedUpTo++;
        }

        // Also spawn (parked at terminal) any pre-assigned bus whose slot
        // hasn't departed yet but is due soon — covers the "sitting at
        // exactly 0% progress, waiting for the next lap" case, which the
        // loop above can't catch since scheduledDeparture > now for these.
// In BootstrapLiveSlots(), change:
foreach (var slot in _allSlots.Where(s => s.assignedBusID >= 0 && s.state == SlotState.AssignedNPC
                                           && s.scheduledDeparture > now
                                           && s.scheduledDeparture - now <= earlyDispatchWindowMinutes))
        {
            toDispatch.Add(slot);
        }

        StartCoroutine(DispatchLiveSlotsDeferred(toDispatch));
    }

    private IEnumerator DispatchLiveSlotsDeferred(List<TimetableSlot> slots)
    {
        yield return null; // let BusManager.Start()'s subscription land first
        foreach (var slot in slots)
            OnDispatchBus?.Invoke(slot);
    }

    private void Update()
    {
        _completedThisFrame.Clear();

        // [FIX Bug 43] don't allow debug actions behind the main menu, or if developer mode is off
        if (!MainMenu.BlocksInput && SettingsData.DeveloperMode && KeyBindings.DebugModifierHeld && Input.GetKeyDown(KeyBindings.Current.debugDumpSchedule))
            PrintFullDaySchedule();

        _dynamicLatenessTimer += Time.deltaTime;
        if (_dynamicLatenessTimer >= dynamicLatenessTickInterval)
        {
            _dynamicLatenessTimer = 0f;
            _lateness.Tick(_slotByBus, SimClock.Instance.AbsoluteGameMinutes,
                busID => BusManager.Instance?.GetRecord(busID)?.controller as NPCBusController,
                GetRouteData,
                TripMinutesForSlot);
        }

        // [FIX M2] This ran unconditionally every scheduleReportInterval
        // seconds for the whole session, building a fresh O(routes × active
        // buses) string every interval regardless of whether anyone was
        // watching. Gated behind logDispatches now, same flag every other
        // BusScheduler debug log already reuses.
        if (logDispatches && scheduleReportInterval > 0f)
        {
            _reportTimer -= Time.deltaTime;
            if (_reportTimer <= 0f) { _reportTimer = scheduleReportInterval; PrintScheduleReport(); }
        }
    }

    private float TripMinutesForRoute(string routeNumber) => GetRouteData(routeNumber)?.oneWayTripMinutes ?? 45f;

    /// <summary>Time-of-day-aware overload (Count II-a) -- scales
    /// oneWayTripMinutes by whichever scheduleWindow contains minuteOfDay
    /// (via BusRouteData.EffectiveTripMinutes), e.g. a genuine overnight
    /// window running faster on empty roads. Falls back to the flat value
    /// for routes that don't use scheduleWindows.</summary>
    private float TripMinutesForRoute(string routeNumber, float minuteOfDay)
    {
        var route = GetRouteData(routeNumber);
        return route != null ? route.EffectiveTripMinutes(minuteOfDay) : 45f;
    }

    private bool IsBusBusyDuring(int busID, int dayNumber, float start, float end)
    {
        foreach (var s in _allSlots)
        {

            if (s.dayNumber != dayNumber || s.assignedBusID != busID) continue;
            float sEnd = s.scheduledDeparture + TripMinutesForSlot(s);
            if (start < sEnd && s.scheduledDeparture < end) return true; // overlap
        }
        return false;
    }

    /// <summary>Window-scaled trip minutes for a slot -- the same value the
    /// scheduler uses to decide whether a slot is "live", exposed so a bus
    /// spawned mid-trip is placed with the SAME duration.</summary>
    public float GetSlotTripMinutes(TimetableSlot slot) => TripMinutesForSlot(slot);

    private float TripMinutesForSlot(TimetableSlot slot)
    {
        if (slot == null) return TripMinutesForRoute(null);
        var route = GetRouteData(slot.routeNumber);
        if (route == null) return TripMinutesForRoute(slot.routeNumber);
        float minuteOfDay = slot.scheduledDeparture % 1440f;

        // [Count V/II-a] A slot belonging to an overrideSchedule variant runs
        // on that variant's OWN trip time and windows, not the mainline's --
        // e.g. Route 34/87's variant A has a different oneWayTripMinutes and
        // its own morning/day split now that GenerateVariantPattern actually
        // reads it (Count V).
        if (!string.IsNullOrEmpty(slot.variantLetter))
        {
            var variant = route.GetVariant(slot.variantLetter);
            if (variant != null && variant.overrideSchedule)
                return variant.EffectiveTripMinutes(route.oneWayTripMinutes, minuteOfDay);
        }
        return route.EffectiveTripMinutes(minuteOfDay);
    }

    private void ResolveVariantStops(BusRouteData route)
    {
        if (route.variants == null) return;
        foreach (var v in route.variants)
        {
            if (v == null) continue;
            v.resolvedOutboundStops ??= new List<BusStopData>();
            v.resolvedInboundStops ??= new List<BusStopData>();
            v.resolvedOutboundStops.Clear();
            v.resolvedInboundStops.Clear();

            if (v.outboundStopCodesOverride != null)
                foreach (string code in v.outboundStopCodesOverride)
                { var s = FindStopByCode(code); if (s != null) v.resolvedOutboundStops.Add(s); }

            if (v.inboundStopCodesOverride != null)
                foreach (string code in v.inboundStopCodesOverride)
                { var s = FindStopByCode(code); if (s != null) v.resolvedInboundStops.Add(s); }
        }
    }

    private BusStopData FindStopByCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        foreach (var route in managedRoutes)
        {
            if (route == null) continue;
            if (route.resolvedOutboundStops != null)
                foreach (var s in route.resolvedOutboundStops)
                    if (s != null && string.Equals(s.stopCode, code, StringComparison.OrdinalIgnoreCase)) return s;
            if (route.resolvedInboundStops != null)
                foreach (var s in route.resolvedInboundStops)
                    if (s != null && string.Equals(s.stopCode, code, StringComparison.OrdinalIgnoreCase)) return s;
        }
        Debug.LogWarning($"[BusScheduler] Stop code not found: '{code}'");
        return null;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  TIMETABLE BUILDING — inlined, no separate ITimetableSource class.
    //  Absolute-minute output only: dayNumber*1440 + minute-of-day.
    // ═════════════════════════════════════════════════════════════════════
    private void BuildDayForAllRoutes(int dayNumber)
    {
        bool builtAny = false;
        // [NEW] Process routes scarcest-pool-first rather than in declaration
        // order. With a shared cross-route depot pool, whichever route got
        // processed first used to have first pick regardless of how tight
        // its own bus availability actually was — a route with plenty of
        // eligible buses could still grab from a route that barely has
        // enough, just by coming first in the managedRoutes array. Now the
        // route with the fewest eligible buses for its own policy/depot
        // claims first.
        foreach (var route in RoutesByPoolScarcity())
        {
            if (route == null) continue;
            if (!_generatedRouteDays.Add((route.routeNumber, dayNumber))) continue; // already built
            BuildRouteDay(route, dayNumber);
            PreAssignBusesForRouteDay(route, dayNumber);
            builtAny = true;
        }
        if (builtAny) PersistDayAssignments(dayNumber);
    }

    /// <summary>Rough eligible-pool-size estimate per route (depot + mainline
    /// vehicle policy only — variant-specific overrides aren't worth the cost
    /// here, this is just an ordering heuristic, not a real allocation).
    /// Lower count = scarcer = processed first.</summary>
    // [FIX] Ordering by raw pool size alone still let a route get starved by a
    // shared-pool neighbor: a route can have a perfectly respectable ABSOLUTE
    // eligible-bus count and still lose every contested slot if a neighbor
    // sharing most of that same pool needs several times as many buses
    // concurrently. Confirmed real via a live repro: Route 140 (needs ~2
    // concurrent buses, series pool overlaps heavily with two much
    // hungrier neighbors) still got double-booked with Route 136 (needs ~7,
    // same two series) even with this scarcity sort already in place,
    // because 140's raw pool size wasn't small enough to sort it ahead of
    // 136 under the old key — despite 140 being the one that actually runs
    // dry first once 136 (and others earlier in line) take their share.
    // Dividing pool size by the route's own bus cap turns this into a
    // genuine PRESSURE ratio (how many times over would this route's need
    // exhaust its own eligible pool) instead of a bare headcount — a route
    // that's both small-pool AND high-need now sorts ahead of one that's
    // merely small-pool, matching which one is actually more likely to come
    // up empty.
    private IEnumerable<BusRouteData> RoutesByPoolScarcity()
    {
        return managedRoutes
            .Where(r => r != null)
            .OrderBy(r => EstimateEligiblePoolSize(r) / (float)Mathf.Max(1, r.GetCurrentBusCap()));
    }

    private int EstimateEligiblePoolSize(BusRouteData route)
    {
        if (BusManager.Instance == null) return int.MaxValue;
        int count = 0;
        foreach (var record in BusManager.Instance.GetAllRecords())
        {
            int fleetNum = record?.controller?.fleetNumber ?? -1;
            if (fleetNum < 0) continue;
            bool depotOk = DepotManager.Instance == null || DepotManager.Instance.CanServeRoute(fleetNum, route.routeNumber);
            if (depotOk && route.IsBusAllowed(fleetNum)) count++;
        }
        return count;
    }

    /// <summary>Assigns every slot generated for (route, dayNumber) to a
    /// specific busID right now, at generation time — no TBD slots. If a
    /// save already exists for this exact day, that saved mapping wins
    /// slot-for-slot (matched by route+variant+direction+departure, which is
    /// stable and doesn't depend on list ordering), so reloading mid-day
    /// keeps the same buses instead of re-rolling. Otherwise a fresh
    /// deterministic shuffle is used, seeded off (day, route) so re-running
    /// this in the same session without a save is at least stable too.
    ///
    /// Buses cycle to the next one in the pool every lapRetirementThreshold
    /// legs — this is "after 4 laps it's a different bus," baked into the
    /// generated timetable itself instead of emerging at runtime from
    /// whichever idle bus happened to be free.
    ///
    /// A short turn is just another variant, so it is pre-assigned by this same pass (its own
    /// vehicle group under the '~' letter).</summary>
    /// <summary>Re-runs PreAssignBusesForRouteDay for every already-built
    /// (route, day) that still has Unassigned slots sitting in it. Exists
    /// because BusScheduler.Start() and BusManager.Start() race — Unity
    /// gives no cross-script ordering guarantee — so the very first
    /// pre-assignment pass can run before the fleet has registered, leaving
    /// the whole day's pool empty and every slot stuck Unassigned ("TBD"
    /// forever, and past slots force-completed with assignedBusID = -1).
    /// BusManager calls this once it's done registering its initial fleet.
    /// Safe to call repeatedly — a route/day that's already fully assigned
    /// costs one LINQ scan and does nothing.</summary>
    public void RebuildPendingPreAssignments()
    {
        var pending = _generatedRouteDays
            .Where(rd => _allSlots.Any(s => s.routeNumber == rd.route && s.dayNumber == rd.day && s.state == SlotState.Unassigned))
            .ToList();

        var touchedDays = new HashSet<int>();
        foreach (var (routeNumber, day) in pending)
        {
            var route = GetRouteData(routeNumber);
            if (route == null) continue;
            PreAssignBusesForRouteDay(route, day);
            touchedDays.Add(day);
        }
        foreach (int day in touchedDays) PersistDayAssignments(day);

        if (logDispatches && touchedDays.Count > 0)
            Debug.Log($"[BusScheduler] RebuildPendingPreAssignments: filled in {pending.Count} route/day(s) that were still TBD (startup ordering race).");
    }

    /// <summary>Deterministic string hash — string.GetHashCode() is randomized
    /// per-process in modern .NET (hash-flood protection), so it produced a
    /// DIFFERENT PreAssignBusesForRouteDay shuffle every single playmode
    /// session even for the exact same (dayNumber, routeNumber). This is the
    /// actual reason bus-to-slot assignment looked "random" on every replay
    /// with no save file present. Same input, same output, every run.</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            int hash = 23;
            foreach (char c in s) hash = hash * 31 + c;
            return hash;
        }
    }

    /// <summary>[FIX] The old version grouped ALL slots for a variant (both
    /// directions merged) purely by variantLetter, then walked them in raw
    /// chronological order handing consecutive slots to "the current bus" —
    /// completely blind to direction or travel time. Since outbound and
    /// inbound headway loops both start at the same operatingStartMinutes,
    /// they land on identical timestamps, so the naive walk would hand a
    /// bus an A→Z leg and a Z→A leg at the SAME MINUTE, or an immediate
    /// U-turn with zero travel time in between — physically impossible, and
    /// the actual reason a bus's chain looked scrambled/overlapping instead
    /// of a clean alternating A→Z/Z→A rotation.
    ///
    /// This version simulates actual concurrent vehicles instead: each
    /// vehicle tracks when it's next free (departure + oneWayTripMinutes)
    /// and which direction it owes next. A slot only goes to a vehicle that
    /// is both free in time AND expecting that exact direction (or hasn't
    /// started a chain yet). When no existing vehicle qualifies, the next
    /// bus is pulled from the shuffled pool as a brand-new concurrent
    /// vehicle — this is what naturally produces "N buses running at once"
    /// whenever headway is shorter than trip time, instead of one bus
    /// trying to be in two places simultaneously. Retirement (lapRetirementThreshold)
    /// removes a vehicle from the active set after its Nth leg so the next
    /// slot that needs a bus pulls a fresh identity from the pool.</summary>
    private void PreAssignBusesForRouteDay(BusRouteData route, int dayNumber)
    {
        var saved = SaveService.LoadDayAssignments(dayNumber);
        Dictionary<(string variant, bool outbound, float dep), int> savedLookup = null;
        if (saved != null)
        {
            savedLookup = new Dictionary<(string, bool, float), int>();
            foreach (var e in saved.entries)
                if (e.routeNumber == route.routeNumber)
                {
                    int busID = BusManager.Instance?.GetBusIDByFleetNumber(e.fleetNumber) ?? -1;
                    if (busID >= 0)
                        savedLookup[(e.variantLetter ?? "", e.isOutbound, e.scheduledDeparture)] = busID;
                }
        }

        // [FIX] Pool used to be built ONCE for the whole route using only
        // route.IsBusAllowed() (mainline policy), then reused as-is for every
        // variant group below — so a variant's overrideVehicleRestrictions
        // (e.g. an overnight variant restricted to older, non-articulated
        // buses) was never actually consulted here: the pool handed to that
        // variant's slots was already filtered by mainline policy before the
        // variant was even known. Now builds a fresh, correctly-filtered pool
        // per variant (and one for the mainline, variant == null).
        List<int> BuildPool(RouteVariantData variant)
        {
            var p = new List<int>();
            if (BusManager.Instance == null) return p;

            foreach (var record in BusManager.Instance.GetAllRecords())
            {
                int fleetNum = record?.controller?.fleetNumber ?? -1;
                // [FIX] Guard against orphaned BusRecords. If BusManager ever
                // registers the same controller twice (handoff, re-registration),
                // the OLD record is left behind pointing at a valid controller
                // under a busID that controller no longer reports as its own
                // (record.busID != record.controller.busID). That record still
                // passes every eligibility check below -- the fleet number and
                // depot/route policy are all still genuinely valid for this real
                // bus -- so it silently got handed a real slot under a dead ID
                // (slot.assignedBusID = <stale id>). The bus itself never
                // recognizes that slot as its own (its busID has moved on), and
                // BusTrackerService.ResolveFleetLabel can't resolve the stale ID
                // through BusRegistry either -- so it shows up as a phantom
                // "trip" under a raw, non-fleet number that never actually
                // exists. Skip any record whose busID has drifted from its own
                // controller's current busID -- that's the live/current record
                // for this bus, not this one.
                if (record?.controller != null && record.busID != record.controller.busID) continue;
                // Depot's route split (e.g. Depot A: 5 buses, Depot B: 20,
                // Depot C: 3 all on the same route) enforced regardless of
                // variant — depot assignment isn't a per-variant concept.
                bool depotOk = DepotManager.Instance == null ||
                               DepotManager.Instance.CanServeRoute(fleetNum, route.routeNumber);
                bool allowed = variant != null ? route.IsBusAllowedForVariant(fleetNum, variant) : route.IsBusAllowed(fleetNum);
                // A bus that is only legal AT NIGHT (nightFleetSeries names a series that allowedFleetSeries
                // doesn't) fails the time-less check above, so it never entered the pool: every night slot was
                // then handed a day-series bus by the forced fallback, which can't continue past its first leg
                // (not night-legal), so each night departure got its own bus. Probe the night window too; the
                // per-slot checks below still keep each bus to the hours it's actually allowed.
                if (!allowed && route.nightFleetSeries != null && route.nightFleetSeries.Count > 0)
                {
                    float nightProbe = BusRouteData.NightWindowStartMinutes;
                    allowed = variant != null ? route.IsBusAllowedForVariant(fleetNum, variant, nightProbe) : route.IsBusAllowed(fleetNum, nightProbe);
                }
                if (fleetNum >= 0 && allowed && depotOk) p.Add(record.busID);
            }
            return p;
        }

        var rng = new System.Random(unchecked(dayNumber * 397 ^ StableHash(route.routeNumber)));
        void Shuffle(List<int> p)
        {
            for (int i = p.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (p[i], p[j]) = (p[j], p[i]);
            }
        }

        var daySlots = _allSlots.Where(s => s.routeNumber == route.routeNumber && s.dayNumber == dayNumber);

        foreach (var variantGroup in daySlots.GroupBy(s => s.variantLetter ?? ""))
        {
            string groupLetter = variantGroup.Key;
            var groupVariant = !string.IsNullOrEmpty(groupLetter) ? route.GetVariant(groupLetter) : null;
            var pool = BuildPool(groupVariant);
            Shuffle(pool);
            if (pool.Count == 0)
            {
                if (logDispatches)
                    Debug.LogWarning($"[BusScheduler] Route {route.routeNumber}{groupLetter}: no eligible buses for this variant's vehicle policy — its slots stay Unassigned.");
                continue;
            }
            var ordered = variantGroup
                .OrderBy(s => s.scheduledDeparture)
                .ThenByDescending(s => s.isOutbound)
                .ToList();

            // (busID, freeAt, expectedOutbound, legsSinceRetirement).
            // expectedOutbound == null means this vehicle hasn't run a leg
            // yet this chain and can start in either direction.
            var vehicles = new List<(int busID, float freeAt, bool? expectedOutbound, int legs)>();
            var chainService = new Dictionary<int, float>(); // per-bus cumulative on-route trip minutes
            int poolIdx = 0;
            bool warnedExhausted = false;

            foreach (var slot in ordered)
            {
                // [Count II-a/V] Window-scaled trip time for THIS slot's own
                // departure time, not a flat route-wide constant — a slot
                // landing in a scaled-down overnight window needs less
                // occupancy (freeAt/neededEnd) than one in a scaled-up peak
                // window, even on the same route. groupVariant's own trip
                // time/windows are used instead of the mainline's when this
                // slot belongs to an overrideSchedule variant.
                float tripMinutes = groupVariant != null && groupVariant.overrideSchedule
                    ? groupVariant.EffectiveTripMinutes(route.oneWayTripMinutes, slot.scheduledDeparture % 1440f)
                    : route.EffectiveTripMinutes(slot.scheduledDeparture % 1440f);
                var key = (slot.variantLetter ?? "", slot.isOutbound, slot.scheduledDeparture);

                if (savedLookup != null && savedLookup.TryGetValue(key, out int savedBusID)
                    && !IsBusBusyDuring(savedBusID, dayNumber, slot.scheduledDeparture, slot.scheduledDeparture + tripMinutes))
                {
                    slot.assignedBusID = savedBusID;
                    slot.state = SlotState.AssignedNPC;

                    float savedFreeAt = slot.scheduledDeparture + tripMinutes;
                    bool? savedNextDir = !slot.isOutbound;
                    int existingIdx = vehicles.FindIndex(v => v.busID == savedBusID);
                    if (existingIdx >= 0)
                    {
                        int contLegs = vehicles[existingIdx].legs + 1;
                        slot.chainLegIndex = contLegs;
                        chainService.TryGetValue(savedBusID, out float prevSvc);
                        chainService[savedBusID] = slot.serviceMinutesAccum = prevSvc + tripMinutes;
                        vehicles[existingIdx] = (savedBusID, savedFreeAt, savedNextDir, contLegs);
                    }
                    else
                    {
                        // Fresh vehicle entering via a restored/pinned assignment.
                        // Its chain-local lap index starts at 1 here; every
                        // subsequent leg the walk hands this vehicle increments it.
                        slot.chainLegIndex = 1;
                        chainService[savedBusID] = slot.serviceMinutesAccum = tripMinutes;
                        vehicles.Add((savedBusID, savedFreeAt, savedNextDir, 1));
                    }
                    continue;
                }

                // [FIX] BuildPool's fleet-series filter is computed ONCE per
                // variant group and reused for every slot in it — fine for a
                // static allowedFleetSeries, but nightFleetSeries only gates
                // during 00:00-05:00, so a vehicle that's a perfectly legal
                // continuation for a daytime leg may not be legal for a leg
                // that lands inside the night window (or vice versa). Recheck
                // per-slot here so a chain can't carry a non-night-eligible
                // bus straight through the overnight window untouched.
                // [FIX] Was FindIndex, which stops at the FIRST vehicle in list
                // order that qualifies rather than the one that's been free
                // longest. Since new vehicles are appended to the end of the
                // list as they're pulled from the pool, that meant whichever
                // buses were introduced earliest (e.g. during a low-headway
                // window) got reused on almost every subsequent slot, while
                // later-pulled buses sat fully eligible but never selected —
                // "stacking" idle at the terminal for hours despite the route
                // needing them on paper. Now scans the whole list and picks
                // the qualifying vehicle with the EARLIEST freeAt, so buses
                // rotate round-robin by longest-idle instead of by list order.
                int vIdx = -1;
                float bestFreeAt = float.MaxValue;
                for (int vi = 0; vi < vehicles.Count; vi++)
                {
                    var cand = vehicles[vi];
                    if (cand.freeAt <= slot.scheduledDeparture
                        && (cand.expectedOutbound == null || cand.expectedOutbound == slot.isOutbound)
                        && route.IsBusAllowedForVariant(ResolveFleetNumber(cand.busID), groupVariant, slot.scheduledDeparture % 1440f)
                        && cand.freeAt < bestFreeAt)
                    {
                        bestFreeAt = cand.freeAt;
                        vIdx = vi;
                    }
                }

                if (vIdx < 0)
                {
                    // No existing vehicle can physically take this leg —
                    // bring in the next bus from the pool as a brand-new
                    // concurrent vehicle.
                    //
                    // [FIX] Was `pool[poolIdx++]` unconditionally — grabbed
                    // whichever bus was next in this route's shuffle with no
                    // regard for whether another route sharing the same
                    // depot had already committed that same bus to an
                    // overlapping time window this day (see IsBusBusyDuring
                    // above). Now scans forward through the pool for the
                    // first bus that's actually free for this slot's window
                    // before falling back to the old force-reuse behavior.
                    int newBusID = -1;
                    float neededEnd = slot.scheduledDeparture + tripMinutes;
                    float slotMinuteOfDay = slot.scheduledDeparture % 1440f;
                    if (pool.Count > 0)
                    {
                        int startIdx = poolIdx < pool.Count ? poolIdx : 0;
                        for (int attempt = 0; attempt < pool.Count; attempt++)
                        {
                            int candidateIdx = (startIdx + attempt) % pool.Count;
                            int candidate = pool[candidateIdx];
                            // BuildPool's eligibility was computed with no time
                            // context, so it can't reflect nightFleetSeries —
                            // recheck per-slot here so a bus that's fine outside
                            // the 00:00-05:00 window but not in it (or vice versa)
                            // isn't handed a leg it isn't actually allowed to run.
                            if (!route.IsBusAllowedForVariant(ResolveFleetNumber(candidate), groupVariant, slotMinuteOfDay)) continue;
                            if (!IsBusBusyDuring(candidate, dayNumber, slot.scheduledDeparture, neededEnd))
                            {
                                newBusID = candidate;
                                poolIdx = candidateIdx + 1;
                                break;
                            }
                        }
                    }

                    if (newBusID < 0)
                    {
                        // [FIX] Every bus in this route's pool is already committed elsewhere
                        // for this window — genuinely not enough buses to cover it without
                        // double-booking. This used to force a reuse anyway ("forced a
                        // double-booking... rather than leave the slot unassigned") — but a
                        // double-booked bus is scheduling fiction (a physical bus can't
                        // actually be in two places), and it's exactly what a live repro
                        // confirmed: Route 140 (small pool, shares its depot series with much
                        // hungrier Route 136) showed the SAME bus, at the SAME time, assigned
                        // to two completely different routes' timetables ("YOU" on both boards
                        // at once). RoutesByPoolScarcity (see its own updated comment) now
                        // weighs need against pool size instead of raw pool size alone, which
                        // should make this fallback fire far less often — but when the fleet
                        // genuinely is short for this window even with fairer ordering, leaving
                        // the slot Unassigned (shown as TBD) is the honest outcome. A visible
                        // service gap beats a bus silently pretending to run two trips at once.
                        if (logDispatches)
                        {
                            Debug.LogWarning($"[BusScheduler] Route {route.routeNumber}{(string.IsNullOrEmpty(slot.variantLetter) ? "" : slot.variantLetter)}: " +
                                              $"every eligible bus for this depot/policy is already committed to another route during this window — " +
                                              $"leaving this slot Unassigned (TBD) instead of double-booking.");
                        }
                        continue;
                    }
                    else if (poolIdx > pool.Count && logDispatches && !warnedExhausted)
                    {
                        Debug.LogWarning($"[BusScheduler] Route {route.routeNumber}{(string.IsNullOrEmpty(slot.variantLetter) ? "" : slot.variantLetter)}: " +
                                          $"needs more than {pool.Count} concurrent buses to run this headway — reusing bus identities, which may double-book a physical bus.");
                        warnedExhausted = true;
                    }

                    // Brand-new concurrent vehicle — it begins a fresh chain here.
                    // legs starts at 0; the stamp below turns its first assigned
                    // leg into chainLegIndex 1.
                    vehicles.Add((newBusID, 0f, null, 0));
                    chainService[newBusID] = 0f;
                    vIdx = vehicles.Count - 1;
                }

                var veh = vehicles[vIdx];
                slot.assignedBusID = veh.busID;
                slot.state = SlotState.AssignedNPC;

                int legs = veh.legs + 1;
                slot.chainLegIndex = legs;
                chainService.TryGetValue(veh.busID, out float svcSoFar);
                float svcNow = svcSoFar + tripMinutes;
                chainService[veh.busID] = slot.serviceMinutesAccum = svcNow;
                if (ServiceBudgetReached(route.routeNumber, svcNow, tripMinutes))
                {
                    // Retires after this leg — remove the vehicle so the
                    // next slot needing a bus at/after this point pulls a
                    // fresh identity instead of continuing this one.
                    vehicles.RemoveAt(vIdx);
                }
                else
                {
                    vehicles[vIdx] = (veh.busID, slot.scheduledDeparture + tripMinutes, !slot.isOutbound, legs);
                }
            }
        }
    }

    /// <summary>Writes out the full pre-assigned mapping for every slot
    /// generated so far on this day, across all routes. Called once per
    /// BuildDayForAllRoutes pass (not per-route) so a single file holds the
    /// whole day.</summary>
    private void PersistDayAssignments(int dayNumber)
    {
        var data = new DayAssignmentSave { dayNumber = dayNumber };
        foreach (var s in _allSlots.Where(s => s.dayNumber == dayNumber && s.assignedBusID >= 0 && !IsPlayer(s.assignedBusID)))
        {
var record = BusManager.Instance?.GetRecord(s.assignedBusID);
if (record?.controller == null)
    continue;

data.entries.Add(new DayAssignmentEntry
{
    routeNumber = s.routeNumber,
    variantLetter = s.variantLetter ?? "",
    isOutbound = s.isOutbound,
    scheduledDeparture = s.scheduledDeparture,
    fleetNumber = record.controller.fleetNumber
});
        }
        SaveService.SaveDayAssignments(data);
    }

    private void BuildRouteDay(BusRouteData route, int dayNumber)
    {
        float dayBase = dayNumber * 1440f;
        GenerateVariantPattern(route, null, dayNumber, dayBase);

        if (route.variants != null)
            foreach (var v in route.variants)
                if (v != null) GenerateVariantPattern(route, v, dayNumber, dayBase);
    }

    /// <summary>[FIX] Overnight windows (e.g. start 1230, end 330) previously produced
    /// zero slots — every generation loop below is a plain `for (t = start; t < end; ...)`,
    /// which is immediately false when end &lt;= start. Any window that wraps past midnight
    /// needs its end pushed into the "next day" by adding a full 1440 minutes so the loop
    /// actually has a positive span to iterate over.</summary>
    private static float ResolveWrappedEnd(float start, float end) => end <= start ? end + 1440f : end;

    private void GenerateVariantPattern(BusRouteData route, RouteVariantData variant, int dayNumber, float dayBase)
    {
        bool useV = variant != null && variant.overrideSchedule;
        string vLetter = variant?.variantLetter ?? "";

        if (!useV && route.UsesTimeOfDayWindows)
        {
            GenerateFromWindows(route.scheduleWindows, route.routeNumber, vLetter, dayNumber, dayBase);
            return;
        }

        // [Count V] Was unconditionally falling through to the flat
        // headwayFromA/ZMinutes below even when the variant had its own
        // scheduleWindows populated and overrideSchedule=true — that data was
        // authored (e.g. Route 34/87's variant A both have real morning/day
        // window splits) but silently never read. Same pattern as the
        // mainline branch above, just for the variant's own windows.
        if (useV && variant.UsesTimeOfDayWindows)
        {
            GenerateFromWindows(variant.scheduleWindows, route.routeNumber, vLetter, dayNumber, dayBase);
            return;
        }

        float startRaw = useV ? variant.operatingStartMinutes : route.operatingStartMinutes;
        float endRaw   = useV ? variant.operatingEndMinutes   : route.operatingEndMinutes;
        float start = dayBase + startRaw;
        float end   = dayBase + ResolveWrappedEnd(startRaw, endRaw);
        float hwA   = useV ? variant.headwayFromAMinutes : route.headwayFromAMinutes;
        float hwZ   = useV ? variant.headwayFromZMinutes : route.headwayFromZMinutes;

        if (hwA > 0f) for (float t = start; t < end; t += hwA) EmitSlot(route.routeNumber, vLetter, true, t, dayNumber);
        if (hwZ > 0f) for (float t = start; t < end; t += hwZ) EmitSlot(route.routeNumber, vLetter, false, t, dayNumber);
    }

    private void GenerateFromWindows(List<ScheduleWindow> windows, string routeNumber, string vLetter, int dayNumber, float dayBase)
    {
        float lastOut = float.NegativeInfinity;
        float lastIn  = float.NegativeInfinity;

        foreach (var w in windows)
        {
            lastOut = EmitWindowDirection(w.windowStartMinutes, w.windowEndMinutes, w.headwayFromAMinutes, routeNumber, vLetter, true,  dayNumber, dayBase, lastOut, w.departureOffsetMinutes);
            lastIn  = EmitWindowDirection(w.windowStartMinutes, w.windowEndMinutes, w.headwayFromZMinutes, routeNumber, vLetter, false, dayNumber, dayBase, lastIn, w.departureOffsetMinutes);
        }
    }

    /// <summary>[Alternating-variant support] phaseOffsetMinutes shifts the
    /// departure grid so a variant/short-turn window can interleave with a
    /// same-headway mainline window instead of landing on the exact same
    /// clock minutes -- windowStartMinutes alone can't do this, since `first`
    /// always snapped to an absolute-time multiple of headway regardless of
    /// where the window starts (0 offset = old, unchanged behavior).</summary>
    private float EmitWindowDirection(float winStart, float winEnd, float headway, string routeNumber, string vLetter, bool outbound, int dayNumber, float dayBase, float lastEmitted, float phaseOffsetMinutes = 0f)
    {
        if (headway <= 0f) return lastEmitted;

        // Live-event hook: a ReducedService/NoService/AdditionalService event covering this
        // route+direction+day scales headway (or, at 0, skips the window outright) -- see
        // LiveEventManager.GetServiceMultiplier. ModifiedService/Detour never reach here.
        float liveMultiplier = LiveEventManager.Instance != null
            ? LiveEventManager.Instance.GetServiceMultiplier(routeNumber, outbound, dayNumber) : 1f;
        if (liveMultiplier <= 0f) return lastEmitted;
        if (liveMultiplier != 1f) headway = Mathf.Max(1f, headway / liveMultiplier);

        float wEnd = ResolveWrappedEnd(winStart, winEnd);
        float phase = ((phaseOffsetMinutes % headway) + headway) % headway; // normalize into [0, headway)
        float first = Mathf.Ceil((winStart - phase) / headway) * headway + phase;
        for (float t = first; t <= wEnd; t += headway)
        {
            float absolute = dayBase + t;
            if (absolute <= lastEmitted) continue;
            EmitSlot(routeNumber, vLetter, outbound, absolute, dayNumber);
            lastEmitted = absolute;
        }
        return lastEmitted;
    }

    private void EmitSlot(string routeNumber, string variantLetter, bool outbound, float absoluteDeparture, int dayNumber)
    {
        _allSlots.Add(new TimetableSlot
        {
            routeNumber = routeNumber,
            variantLetter = variantLetter ?? "",
            isOutbound = outbound,
            scheduledDeparture = absoluteDeparture,
            dayNumber = dayNumber,
            state = SlotState.Unassigned,
            assignedBusID = -1
        });
    }

    private void SortAll() => _allSlots.Sort((a, b) => a.scheduledDeparture.CompareTo(b.scheduledDeparture));

    /// <summary>Fires on every GTST day rollover — extends every route's
    /// timetable one more day forward immediately, so the forward horizon
    /// never depends on the 30s poll below catching up in time.</summary>
    private void HandleDayRolled(int newDay)
    {
        BuildDayForAllRoutes(newDay + scheduleDaysAhead - 1);
        SortAll();
        if (logDispatches) Debug.Log($"[BusScheduler] Day rolled to {newDay} — timetable extended.");
    }

    private IEnumerator ScheduleRefillWatch()
    {
        while (true)
        {
            yield return new WaitForSeconds(30f);
            float now = SimClock.Instance.AbsoluteGameMinutes;
            int today = SimClock.Instance.GameDayNumber;

            foreach (var route in managedRoutes)
            {
                if (route == null) continue;
                float lastEnd = (today + scheduleDaysAhead - 1) * 1440f + route.operatingEndMinutes;
                if (lastEnd - now < scheduleRefillThresholdMinutes)
                    BuildDayForAllRoutes(today + scheduleDaysAhead); // BuildDayForAllRoutes no-ops routes already built via the HashSet guard
            }
            SortAll();
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  CONSTRAINTS — the ONE place vehicle policy / route caps are checked.
    //  Every assignment path (claim, live-claim, direct assign, handoff,
    //  reschedule) routes through this. Player always bypasses, matching the
    //  original design intent (player can drive any bus).
    // ═════════════════════════════════════════════════════════════════════
    private bool CanAssign(int busID, BusRouteData route, out string denialReason, float minuteOfDay = -1f) =>
        CanAssign(busID, route, (RouteVariantData)null, out denialReason, minuteOfDay);

    /// <summary>[FIX] CanAssign/CanContinueRoute is documented as "the ONE place
    /// vehicle policy is checked" but originally only branched on short turns — a
    /// variant's own overrideVehicleRestrictions (IsBusAllowedForVariant) was
    /// never consulted anywhere in this file, so an overnight/older-fleet-only
    /// variant silently fell back to the mainline's policy on every live claim,
    /// handoff, and rotation-continuation path. Now takes the variant too.
    /// [FIX] Also now takes minuteOfDay and threads it through to
    /// IsBusAllowed*/IsFleetSeriesAllowed* — route.nightFleetSeries had a fully
    /// wired minuteOfDay parameter that literally no call site in this file ever
    /// passed, so the 00:00-05:00 night-fleet restriction never actually gated
    /// anything regardless of which path (fresh claim, continuation, handoff)
    /// a bus came through. Pass -1 (default) to skip night gating, matching the
    /// old behavior for any caller that genuinely has no time context.</summary>
    private bool CanContinueRoute(int busID, BusRouteData route, RouteVariantData variant, out string denialReason, float minuteOfDay = -1f)
    {
        denialReason = null;
        if (IsPlayer(busID)) return true;
        if (route == null) return true;

        int fleetNum = ResolveFleetNumber(busID);
        bool allowed = variant != null ? route.IsBusAllowedForVariant(fleetNum, variant, minuteOfDay)
                                       : route.IsBusAllowed(fleetNum, minuteOfDay);
        if (fleetNum >= 0 && !allowed) { denialReason = "vehicle policy"; return false; }
        return true; // deliberately no cap check — see summary above
    }

    /// <summary>Variant-aware overload — pass the RouteVariantData when the slot in question is a lettered
    /// variant (a short turn is one too) so ITS OWN vehicle-restriction override (if any) is checked instead of
    /// the mainline's policy.</summary>
    private bool CanAssign(int busID, BusRouteData route, RouteVariantData variant, out string denialReason, float minuteOfDay = -1f)
    {
        denialReason = null;
        if (IsPlayer(busID)) return true;
        if (FreeAgentBusIDs.Contains(busID)) { denialReason = "AI free agent — not in fleet rotation"; return false; }
        if (route == null) return true;

        int fleetNum = ResolveFleetNumber(busID);
        bool allowed = variant != null ? route.IsBusAllowedForVariant(fleetNum, variant, minuteOfDay)
                                       : route.IsBusAllowed(fleetNum, minuteOfDay);
        if (fleetNum >= 0 && !allowed) { denialReason = "vehicle policy"; return false; }
        if (CountActiveBusesOnRoute(route.routeNumber) >= route.maxBusesAllowed) { denialReason = "route at cap"; return false; }
        return true;
    }

    /// <summary>Resolved directly off BusManager's own record — the same
    /// source of truth BusManager itself uses for its own constraint checks
    /// in GetIdleBusForRoute. The old scheduler went through a separate
    /// BusRegistry.ActiveBuses lookup that could be a frame behind during
    /// startup/handoff races, which is the likely reason constraint checks
    /// silently no-opped for "random" buses.</summary>
    private int ResolveFleetNumber(int busID)
    {
        if (IsPlayer(busID)) return -1;
        return BusManager.Instance?.GetRecord(busID)?.controller?.fleetNumber ?? -1;
    }

    /// <summary>Per-bus, per-route retirement threshold. [FIX] Was a flat
    /// pass-through of lapRetirementThreshold regardless of route -- see the
    /// [Header("Bus Cycling")] comment above for why that's wrong. Now scales
    /// the lap count so a bus retires after roughly
    /// targetServiceMinutesBeforeRetirement of real driving time regardless
    /// of whether its route's trip is 18 minutes or 86. routeNumberHint/
    /// tripMinutesHint let call sites that already have this data (most of
    /// them do -- PreAssignBusesForRouteDay, TopUpChain, CompleteSlot) pass it
    /// straight through instead of paying for a _slotByBus lookup, and matter
    /// for correctness in CompleteSlot specifically, which calls this AFTER
    /// _slotByBus.Remove(busID) has already run -- the busID-only lookup
    /// would silently fall back to the flat default there otherwise.
    /// lapRetirementThreshold &lt;= 0 remains the designer kill-switch for
    /// retirement entirely and is checked first, before any route math.</summary>
    private int GetEffectiveLapThreshold(int busID, string routeNumberHint = null, float? tripMinutesHint = null)
    {
        if (targetServiceMinutesBeforeRetirement <= 0f) return 0; // retirement disabled

        string rn = routeNumberHint;
        TimetableSlot slot = null;
        if (rn == null && _slotByBus.TryGetValue(busID, out slot)) rn = slot.routeNumber;

        float trip = tripMinutesHint ?? (slot != null ? TripMinutesForSlot(slot)
                   : rn != null ? TripMinutesForRoute(rn) : 0f);

        return LapsForRoute(rn != null ? GetRouteData(rn) : null, trip);
    }

    // ── Service-minute retirement (accumulated real trip minutes) ────────
    private float ServiceBudgetFor(string route)
    {
        if (targetServiceMinutesBeforeRetirement <= 0f) return 0f; // retirement disabled
        return ServiceShareFor(route);
    }

    /// <summary>True when the bus has driven its route's service-minute budget
    /// after a leg (accum includes that leg). Half-a-leg rounding matches
    /// ServiceMinutesToLaps' round(): retire once the NEXT leg would overshoot
    /// the budget by more than it undershoots it.</summary>
    private bool ServiceBudgetReached(string route, float accum, float legTrip)
    {
        float b = ServiceBudgetFor(route);
        return b > 0f && accum + 0.5f * legTrip >= b;
    }

    private float AccumOf(TimetableSlot s) =>
        s.serviceMinutesAccum > 0f ? s.serviceMinutesAccum
                                   : Mathf.Max(1, s.chainLegIndex) * TripMinutesForSlot(s);

    /// <summary>How many MORE legs this bus already has queued on its current route after its current leg
    /// (the chain TopUpChain filled up to the service-minute budget). 0 = this is the last leg of the rotation.
    /// Used by the player's "Lap X of Y" popup so Y is what is actually left, not the route's full lap count.</summary>
    public int GetPendingLegsAfterCurrent(int busID)
    {
        if (!_slotByBus.TryGetValue(busID, out var cur) || cur == null) return 0;
        int n = 0;
        foreach (var s in _allSlots)
        {
            if (s.assignedBusID != busID || s.state == SlotState.Completed) continue;
            if (s.routeNumber != cur.routeNumber) continue;
            if (s.scheduledDeparture > cur.scheduledDeparture) n++;
        }
        return n;
    }

    /// <summary>Public wrapper — lets UI (ShiftBoardMenu, Mainmenu, ShiftRunner)
    /// show the REAL number of laps left on a bus's rotation instead of a
    /// guessed/fixed number, using the exact same math CompleteSlot/TopUpChain
    /// already retire buses against. Resolves route via this bus's current
    /// slot if one exists.</summary>
    public int GetEffectiveLapThresholdPublic(int busID) => GetEffectiveLapThreshold(busID);

    /// <summary>Same as GetEffectiveLapThresholdPublic, but for callers that
    /// already know the route (e.g. a Shift Board row being drawn for a
    /// specific route) and shouldn't have to rely on this bus already holding
    /// a live slot in _slotByBus.</summary>
    public int GetEffectiveLapThresholdForRoute(int busID, string routeNumber) => GetEffectiveLapThreshold(busID, routeNumber);

    // ═════════════════════════════════════════════════════════════════════
    //  CROSS-ROUTE HANDOFF (NEW) — tried at retirement / chain-exhaustion
    //  before a bus is sent idle. Same depot pool, different route: exactly
    //  the "shared buses go from one route to the next" behavior. This is a
    //  genuinely fresh claim on the new route (full CanAssign — vehicle
    //  policy AND cap), never a continuation, so it can never bypass a
    //  route's maxBusesAllowed the way CanContinueRoute intentionally does
    //  for a bus's own existing rotation.
    // ═════════════════════════════════════════════════════════════════════
    private IEnumerable<BusRouteData> GetInterlinedCandidateRoutes(string completedRoute)
    {
        var seen = new HashSet<string> { completedRoute };
        foreach (var rn in GetInterlinedRoutes(completedRoute))
        {
            var r = GetRouteData(rn);
            if (r != null && seen.Add(r.routeNumber)) yield return r;
        }
        foreach (var r in managedRoutes)
        {
            if (r == null || !seen.Add(r.routeNumber)) continue;
            yield return r;
        }
    }

    private bool TryCrossRouteHandoff(int busID, string completedRoute, float earliestDeparture, bool? completedWasOutbound, out TimetableSlot claimedSlot)
    {
        claimedSlot = null;
        if (!enableInterlining) return false;

        bool isPlayer = IsPlayer(busID);
        // The player's bus interlines exactly like an NPC's (same terminal
        // rules, same shared service budget); only the depot/vehicle-policy
        // gate is skipped since they're already driving a bus.
        int fleetNum = isPlayer ? 0 : ResolveFleetNumber(busID);
        if (!isPlayer && fleetNum < 0) return false;

        float notBefore = earliestDeparture + crossRouteDeadheadMinutes;

        foreach (var candidateRoute in GetInterlinedCandidateRoutes(completedRoute))
        {
            if (!isPlayer)
            {
                bool depotOk = DepotManager.Instance == null
                            || DepotManager.Instance.CanServeRoute(fleetNum, candidateRoute.routeNumber);
                if (!depotOk) continue;

                if (!CanAssign(busID, candidateRoute, out string denyReason, notBefore % 1440f)) continue;
            }

            // Terminal rules: only depart from the terminal(s) this pair allows.
            if (!ResolveInterlineDepartureDirs(completedRoute, completedWasOutbound, candidateRoute.routeNumber, out bool okOut, out bool okIn)) continue;
            var next = (okOut && okIn)
                ? GetNextUnassignedSlot(candidateRoute.routeNumber, "", notBefore)
                : FindEarliestSlotAfter(notBefore, sl =>
                    sl.routeNumber == candidateRoute.routeNumber && string.IsNullOrEmpty(sl.variantLetter)
                    && sl.isOutbound == okOut && sl.state == SlotState.Unassigned && sl.scheduledDeparture > notBefore);
            if (next == null) continue;
            if (!isPlayer && IsTooFarAhead(next)) continue; // an NPC shouldn't park at a terminal for hours waiting on it

            if (!isPlayer && !CommitAssignment(next, busID, SlotState.AssignedNPC)) continue;
            if (isPlayer)
            {
                // CommitAssignment deliberately doesn't stamp a player's chain
                // index, so do it here: a fresh chain on the new route.
                next.chainLegIndex = 1;
                next.serviceMinutesAccum = TripMinutesForSlot(next);
                AttachToBus(next, busID, SlotState.AssignedPlayer);
            }
            // Fresh chain on the new route — CommitAssignment already
            // stamps this as lap 1, correctly resetting the retirement clock.
            TopUpChain(busID, next);
            claimedSlot = next;

            if (logDispatches)
                Debug.Log($"[BusScheduler][HANDOFF] Bus#{busID} retired from Route {completedRoute} → handed off to Route " +
                          $"{next.FullRouteLabel} {next.DirectionLabel} @ {MinutesToTimeString(next.scheduledDeparture)} " +
                          $"(same depot, +{crossRouteDeadheadMinutes:0}min deadhead).");
            return true;
        }
        return false;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  QUERIES
    // ═════════════════════════════════════════════════════════════════════
// BusScheduler.cs
private readonly HashSet<int> _gapWaitingBuses = new HashSet<int>();
public void MarkGapWaiting(int busID)  => _gapWaitingBuses.Add(busID);
public void ClearGapWaiting(int busID) => _gapWaitingBuses.Remove(busID);

public int CountActiveBusesOnRoute(string routeNumber)
{
    int c = 0;
    foreach (var kv in _slotByBus)
        if (kv.Value.routeNumber == routeNumber && !_gapWaitingBuses.Contains(kv.Key)) c++;
    return c;
}

    public int GetRouteCap(string routeNumber) => GetRouteData(routeNumber)?.maxBusesAllowed ?? 5;
    public bool TryGetAssignedSlot(int busID, out TimetableSlot slot) => _slotByBus.TryGetValue(busID, out slot);

    /// <summary>Real, concrete fleet-number-to-slot assignments for a route,
    /// today — the same ground truth PreAssignBusesForRouteDay already baked.
    /// Added for ShiftBoardMenu so the shift picker shows buses that actually
    /// exist in the timetable instead of DailyShiftGenerator's separate,
    /// route-only "you should be on Route X sometime this block" guess. One
    /// entry per assigned slot; a bus running multiple legs in the window
    /// shows up once per leg, in departure order.</summary>
    public struct RouteBusEntry
    {
        public int fleetNumber;
        public int busID;
        public bool isOutbound;
        public float scheduledDeparture;
        public bool isLive;
        public string variantLetter;
        public int chainLegIndex;
        public float serviceMinutesBefore; // on-route trip minutes this bus drove BEFORE this leg
        public float tripMinutes;          // this leg's own window-scaled trip minutes
    }

    public List<RouteBusEntry> GetTodaysBusesForRoute(string routeNumber, int dayNumber = -1)
    {
        var result = new List<RouteBusEntry>();
        if (string.IsNullOrEmpty(routeNumber)) return result;
        int day = dayNumber >= 0 ? dayNumber : (SimClock.Instance != null ? SimClock.Instance.GameDayNumber : 0);

        foreach (var slot in _allSlots)
        {
            if (slot.routeNumber != routeNumber || slot.dayNumber != day) continue;

            // ROOT-CAUSE FIX: `if (slot.assignedBusID < 0) continue;` used to
            // skip this slot entirely whenever assignedBusID was negative —
            // but PLAYER_BUS_ID is ALSO negative (-2), same as the genuinely-
            // unassigned sentinel (-1). That meant this method (which
            // ShiftBoardMenu uses to build the board's own route cards) only
            // ever showed departures that already had a real NPC bus
            // assigned — any slot that was still genuinely free (Unassigned,
            // nobody dispatched yet) was invisible to the board entirely,
            // and so was the player's own current assignment. A route's
            // actual NEXT upcoming departure could easily be a still-free
            // slot the board would never offer, while an older, already-late
            // NPC-assigned slot stayed visible — exactly backwards from what
            // "next departure" should mean.
            //
            // Fleet number resolution now branches three ways instead of
            // requiring a registered NPC controller unconditionally:
            //   · Unassigned  → -1 (caller/UI shows "TBD")
            //   · Player      → PlayerHandoff.FleetNumber
            //   · NPC         → BusManager record lookup, same as before
            int fleetNum;
            if (slot.state == SlotState.Unassigned)
            {
                fleetNum = -1;
            }
            else if (IsPlayer(slot.assignedBusID))
            {
                // [FIX 2] The first fix here was still wrong -- "-2 means MY OWN local player" is
                // only true on the machine actually hosting. -2 is PLAYER_BUS_ID's plain DEFAULT
                // value (what it reads as on every machine outside a BeginPlayerContext scope), so
                // a CLIENT resolving a slot showing "-2" (the HOST's own bus) would match its own
                // PlayerHandoff.Instance by coincidence, not because it's really the same bus --
                // see BusTrackerService.ResolveFleetLabel's identical bug/fix for the confirmed
                // repro. Compare against THIS machine's actual PlayerBusID instead of hardcoding -2.
                if (PlayerHandoff.Instance != null && slot.assignedBusID == PlayerHandoff.Instance.PlayerBusID)
                {
                    fleetNum = PlayerHandoff.Instance.FleetNumber;
                }
                else if (NetworkGameBridge.Instance != null)
                {
                    ulong clientId = NetworkGameBridge.SentinelToClientId(slot.assignedBusID);
                    int physicalBusID = NetworkGameBridge.Instance.GetPossessedBusID(clientId);
                    var ctrl = physicalBusID >= 0 ? BusManager.Instance?.GetRecord(physicalBusID)?.controller : null;
                    fleetNum = ctrl != null ? ctrl.fleetNumber : -1;
                }
                else
                {
                    fleetNum = -1;
                }
            }
            else
            {
                fleetNum = BusManager.Instance?.GetRecord(slot.assignedBusID)?.controller?.fleetNumber ?? -1;
                if (fleetNum < 0) continue; // a real NPC busID that doesn't resolve to a live controller — genuinely skip
            }

            result.Add(new RouteBusEntry
            {
                fleetNumber = fleetNum,
                busID = slot.assignedBusID,
                isOutbound = slot.isOutbound,
                scheduledDeparture = slot.scheduledDeparture,
                isLive = IsSlotLive(slot),
                variantLetter = slot.variantLetter,
                chainLegIndex = slot.chainLegIndex,
                tripMinutes = TripMinutesForSlot(slot),
                serviceMinutesBefore = slot.assignedBusID >= 0 ? Mathf.Max(0f, AccumOf(slot) - TripMinutesForSlot(slot)) : 0f
            });
        }

        result.Sort((a, b) => a.scheduledDeparture.CompareTo(b.scheduledDeparture));
        return result;
    }

    public string GetBusVariantLetter(int busID) =>
        _slotByBus.TryGetValue(busID, out var slot) && slot.state != SlotState.Completed ? (slot.variantLetter ?? "") : "";

    public bool HasImminentPipelinedSlot(int busID, float withinMinutes)
    {
        if (!_slotByBus.TryGetValue(busID, out var slot)) return false;
        return (slot.state == SlotState.AssignedNPC || slot.state == SlotState.AssignedPlayer)
            && (slot.scheduledDeparture - SimClock.Instance.AbsoluteGameMinutes) <= withinMinutes;
    }

    public List<TimetableSlot> PeekUpcomingSlots(string routeNumber, string variantLetter, bool isOutbound, int count)
    {
        float now = SimClock.Instance.AbsoluteGameMinutes;
        return _allSlots
            .Where(s => s.routeNumber == routeNumber && (s.variantLetter ?? "") == (variantLetter ?? "")
                        && s.isOutbound == isOutbound && s.state == SlotState.Unassigned && s.scheduledDeparture > now)
            .OrderBy(s => s.scheduledDeparture)
            .Take(count)
            .ToList();
    }

    public TimetableSlot GetNextUnassignedSlot(string routeNumber, string variantLetter = "", float notBefore = -1f, bool? preferredDirection = null)
    {
        float floor = notBefore >= 0f ? notBefore : SimClock.Instance.AbsoluteGameMinutes;
        bool[] order = preferredDirection.HasValue
            ? new[] { preferredDirection.Value, !preferredDirection.Value }
            : new[] { true, false };
        foreach (bool dir in order)
        {
            var found = FindEarliestSlotAfter(floor, s =>
                s.routeNumber == routeNumber && (s.variantLetter ?? "") == (variantLetter ?? "")
                && s.isOutbound == dir && s.state == SlotState.Unassigned && s.scheduledDeparture > floor);
            if (found != null) return found;
        }
        return null;
    }

    public List<TimetableSlot> GetSlotsForRoute(string routeNumber) =>
        _allSlots.Where(s => s.routeNumber == routeNumber).ToList();

    public TimetableSlot GetNextDeparture(string routeNumber, bool fromA)
    {
        float now = SimClock.Instance.AbsoluteGameMinutes;
        return FindEarliestSlotAfter(now, s =>
            s.routeNumber == routeNumber && s.isOutbound == fromA && s.scheduledDeparture > now);
    }

    public int GetLapCount(int busID) =>
        _slotByBus.TryGetValue(busID, out var s) ? Mathf.Max(0, s.chainLegIndex) : 0;
    public float GetLatenessMinutes(int busID) => _slotByBus.TryGetValue(busID, out var s) ? s.latenessMinutes : 0f;

    /// <summary>[Count II-b] Physical driving-speed multiplier for busID's
    /// CURRENT slot's schedule window, clamped to [minScheduleSpeedMultiplier,
    /// maxScheduleSpeedMultiplier]. Resolves the same variant-vs-mainline
    /// window (a short turn is a variant, so its own window) that
    /// TripMinutesForSlot uses for scheduling math, so the two stay
    /// consistent -- a bus scheduled to run an overnight leg faster also
    /// physically drives faster during it, instead of the schedule saying
    /// one thing and the bus visibly doing another. Returns 1f (no change)
    /// for a bus with no current slot (idle, depot, player not yet
    /// dispatched, etc.) -- there's no window to resolve.</summary>
    public float GetScheduleSpeedMultiplier(int busID)
    {
        if (!_slotByBus.TryGetValue(busID, out var slot)) return 1f;

        float minuteOfDay = slot.scheduledDeparture % 1440f;
        var route = GetRouteData(slot.routeNumber);
        if (route == null) return 1f;
        var variant = !string.IsNullOrEmpty(slot.variantLetter) ? route.GetVariant(slot.variantLetter) : null;
        float pct = (variant != null && variant.overrideSchedule)
            ? BusRouteData.ResolveTripTimeMultiplierPercent(variant.scheduleWindows, minuteOfDay)
            : BusRouteData.ResolveTripTimeMultiplierPercent(route.scheduleWindows, minuteOfDay);

        if (pct <= 0f) return 1f; // guard against a misconfigured 0% window -- never divide into an infinite/negative speed
        float raw = 100f / pct;
        return Mathf.Clamp(raw, minScheduleSpeedMultiplier, maxScheduleSpeedMultiplier);
    }

    public float GetLatenessAdjustedDwell(int busID, float baseDwell)
    {
        _slotByBus.TryGetValue(busID, out var slot);
        return _lateness.GetAdjustedDwell(slot, baseDwell);
    }

    public BusRouteData GetRouteData(string routeNumber)
    {
        foreach (var r in managedRoutes) if (r != null && r.routeNumber == routeNumber) return r;
        return null;
    }

    public static string MinutesToTimeString(float minutes)
    {
        int h = Mathf.FloorToInt(minutes / 60f) % 24;
        int m = Mathf.FloorToInt(minutes % 60f);
        return $"{h:D2}:{m:D2}";
    }

    public void SignalRotation(TimetableSlot slot) => OnRotateBus?.Invoke(slot);
    public void ResnapAllBusesToSchedule()
    {
        if (BusManager.Instance == null)
        {
            Debug.LogWarning("[BusScheduler] ResnapAllBusesToSchedule called but BusManager.Instance is null.");
            return;
        }

        int resnapped = 0;
        foreach (var record in BusManager.Instance.GetAllRecords())
        {
            if (record?.controller == null) continue;
            if (IsPlayer(record.busID)) continue; // player's own bus is never touched
            if (!record.isActive) continue;
            if (!_slotByBus.TryGetValue(record.busID, out var slot)) continue;
            // A bus holding a slot that hasn't departed yet is correctly parked
            // at its terminal. AssignRouteWithProgress would flip it straight to
            // InService (departure stamped in the future -> huge negative
            // lateness), launching every waiting bus at once.
            if (!IsSlotLive(slot)) continue;

            var route = GetRouteData(slot.routeNumber);
            if (route == null) continue;

            record.controller.AssignRouteWithProgress(route, slot.isOutbound, slot);
            resnapped++;
        }

        if (logDispatches) Debug.Log($"[BusScheduler] Resnapped {resnapped} NPC bus(es) to their timetable-derived positions.");
    }

    // ═════════════════════════════════════════════════════════════════════
    //  CLAIMS — every non-player path here calls CanAssign first.
    // ═════════════════════════════════════════════════════════════════════
    public TimetableSlot ClaimInitialSlot(string routeNumber, string variantLetter, int busID)
    {
        var routeData = GetRouteData(routeNumber);
        var variant = !string.IsNullOrEmpty(variantLetter) ? routeData?.GetVariant(variantLetter) : null;
        float now = SimClock.Instance.AbsoluteGameMinutes;
        if (!CanAssign(busID, routeData, variant, out string reason, now % 1440f))
        {
            if (logDispatches) Debug.Log($"[BusScheduler] Bus#{busID} DENIED initial slot on Route {routeNumber}{variantLetter} ({reason}).");
            return null;
        }

        float tripMinutes = TripMinutesForRoute(routeNumber, now % 1440f);
        TimetableSlot liveSlot = null;
        foreach (bool dir in new[] { true, false })
        {
            var live = FindLiveSlot(routeNumber, variantLetter, dir, now, tripMinutes);
            if (live != null && (liveSlot == null || live.scheduledDeparture > liveSlot.scheduledDeparture))
                liveSlot = live;
        }

        TimetableSlot chosen;
        if (liveSlot != null)
        {
            chosen = liveSlot;
        }
        else
        {
            var eligible = new List<TimetableSlot>();
            foreach (bool dir in new[] { true, false })
            {
                var peek = PeekUpcomingSlots(routeNumber, variantLetter, dir, 1);
                if (peek.Count > 0) eligible.Add(peek[0]);
            }
            if (eligible.Count == 0)
            {
                if (logDispatches) Debug.LogWarning($"[BusScheduler] No future slot for Bus#{busID} on Route {routeNumber}{variantLetter}.");
                return null;
            }

            eligible.Sort((a, b) => a.scheduledDeparture.CompareTo(b.scheduledDeparture));
            chosen = eligible[0];
        }

        var state = IsPlayer(busID) ? SlotState.AssignedPlayer : SlotState.AssignedNPC;
        if (!CommitAssignment(chosen, busID, state)) return null;
        TopUpChain(busID, chosen);
        return chosen;
    }
    public bool IsSlotLive(TimetableSlot slot) =>
        slot != null && slot.scheduledDeparture <= SimClock.Instance.AbsoluteGameMinutes;

    public TimetableSlot ClaimInitialSlot(string routeNumber, int busID) => ClaimInitialSlot(routeNumber, "", busID);

    /// <summary>Non-destructive scan for a slot that's currently mid-trip
    /// (departed in the past but still within tripMinutes of now).</summary>
    public TimetableSlot TryClaimLiveSlotPublic(string routeNumber, bool outbound, float now, float tripMinutes) =>
        FindLiveSlot(routeNumber, "", outbound, now, tripMinutes);

    private TimetableSlot FindLiveSlot(string routeNumber, string variantLetter, bool outbound, float now, float tripMinutes)
    {
        return _allSlots
            .Where(s => s.routeNumber == routeNumber && (s.variantLetter ?? "") == (variantLetter ?? "")
                        && s.isOutbound == outbound && s.state == SlotState.Unassigned
                        && s.scheduledDeparture <= now && (now - s.scheduledDeparture) < tripMinutes)
            .OrderBy(s => s.scheduledDeparture)
            .FirstOrDefault();
    }

    public TimetableSlot ClaimNextAvailableSlot(string routeNumber, bool isOutbound, int busID)
    {
        var routeData = GetRouteData(routeNumber);
        if (routeData == null) { Debug.LogWarning($"[BusScheduler] Unknown route '{routeNumber}'"); return null; }

        float now = SimClock.Instance.AbsoluteGameMinutes;
        if (!CanAssign(busID, routeData, out string reason, now % 1440f))
        {
            if (logDispatches) Debug.LogWarning($"[BusScheduler] Bus#{busID} rejected on Route {routeNumber} ({reason}).");
            return null;
        }

        float tripMinutes = TripMinutesForRoute(routeNumber, now % 1440f);

        var slot = FindLiveSlot(routeNumber, "", isOutbound, now, tripMinutes);
        if (slot == null)
        {
            var near = PeekUpcomingSlots(routeNumber, "", isOutbound, 1).FirstOrDefault();
            if (near != null && (near.scheduledDeparture - now) <= earlyDispatchWindowMinutes)
                slot = near;
        }

        if (slot == null)
        {
            if (logDispatches) Debug.LogWarning($"[BusScheduler] No live/near-due slot for Bus#{busID} on Route {routeNumber}.");
            return null;
        }

        bool isPlayerClaim = IsPlayer(busID);
        var state = isPlayerClaim ? SlotState.AssignedPlayer : SlotState.AssignedNPC;
        if (!CommitAssignment(slot, busID, state)) return null;
        // [FIX] Same missing step as ReservePlayerSlot -- CommitAssignment deliberately skips
        // resetting chainLegIndex for a player (see its own comment); the NPC branch already
        // resets it internally, so only the player case needs it done here.
        if (isPlayerClaim)
        {
            slot.chainLegIndex = 1;
            slot.serviceMinutesAccum = TripMinutesForSlot(slot);
        }
        TopUpChain(busID, slot);
        return slot;
    }

    public TimetableSlot ReservePlayerSlot(string routeNumber, bool isOutbound)
    {
        var slot = PeekUpcomingSlots(routeNumber, "", isOutbound, 1).FirstOrDefault();
        if (slot == null) return null;
        if (!CommitAssignment(slot, PLAYER_BUS_ID, SlotState.AssignedPlayer)) return null;
        // [FIX] CommitAssignment deliberately doesn't stamp a player's chain index (see its own
        // comment, and the same pattern already followed correctly by TryCrossRouteHandoff and
        // CompleteSlot's fresh-claim continuation) -- every player-assigning caller is expected to
        // set it explicitly. This one (a genuinely fresh board join, not a continuation of
        // anything) was missing that step entirely, so it inherited whatever chainLegIndex the
        // slot happened to carry from initial day-generation instead of starting a new chain at 1.
        slot.chainLegIndex = 1;
        slot.serviceMinutesAccum = TripMinutesForSlot(slot);
        TopUpChain(PLAYER_BUS_ID, slot);
        return slot;
    }

    public TimetableSlot ReservePlayerSlotSpecific(TimetableSlot target)
    {
        if (target == null || target.state != SlotState.Unassigned) return null;
        if (!CommitAssignment(target, PLAYER_BUS_ID, SlotState.AssignedPlayer)) return null;
        // [FIX] Same missing step as ReservePlayerSlot above -- see that comment.
        target.chainLegIndex = 1;
        target.serviceMinutesAccum = TripMinutesForSlot(target);
        TopUpChain(PLAYER_BUS_ID, target);
        return target;
    }
    public TimetableSlot GetCurrentSlotForBus(int busID)
        => _slotByBus.TryGetValue(busID, out var s) ? s : null;

    /// <summary>Hands an NPC bus's work to the player. With <paramref name="chosen"/> (the departure the
    /// player actually picked, still waiting to run), only that slot and the bus's LATER legs move; any
    /// earlier leg -- including one the bus is driving right now -- stays with it, same rule the relief
    /// handoff uses. Without it (in-service takeover), the bus's current/earliest leg and its whole chain move.</summary>
    /// <summary>Moves <paramref name="from"/> and the rest of ITS rotation (the following legs of the same
    /// chain) from an NPC bus to the player. Pre-assignment can give one bus a second, separate rotation
    /// later in the day (its first leg is chainLegIndex 1); that later rotation stays with the bus. Moving
    /// it too meant that when the player's shift ended, ReleaseChain freed it and hours-away departures
    /// turned into TBD.</summary>
    private void MoveChainToPlayer(int npcBusID, TimetableSlot from)
    {
        foreach (var s in _allSlots) // sorted by departure
        {
            if (s.assignedBusID != npcBusID || s.scheduledDeparture < from.scheduledDeparture) continue;
            if (s.state != SlotState.AssignedNPC && s.state != SlotState.InService) continue;
            if (s != from && s.chainLegIndex <= 1) break; // a new rotation starts here -- not part of this one
            s.assignedBusID = PLAYER_BUS_ID;
            s.state = SlotState.AssignedPlayer;
        }
    }

    public TimetableSlot TransferSlotToPlayer(int npcBusID, TimetableSlot chosen = null)
    {
        if (chosen != null && chosen.assignedBusID == npcBusID && chosen.state == SlotState.AssignedNPC)
        {
            MoveChainToPlayer(npcBusID, chosen);
            // If the bus's own current slot just moved to the player, it has nothing left to hold.
            if (_slotByBus.TryGetValue(npcBusID, out var held) && held.assignedBusID != npcBusID)
                DetachFromBus(npcBusID);
            _slotByBus[PLAYER_BUS_ID] = chosen;
            BusManager.Instance?.ReleaseBusReservation(PLAYER_BUS_ID);
            return chosen;
        }

        var slot = _allSlots
            .Where(s => s.assignedBusID == npcBusID && s.state != SlotState.Unassigned && s.state != SlotState.Completed)
            .OrderByDescending(s => s.state == SlotState.InService)
            .ThenBy(s => s.scheduledDeparture)
            .FirstOrDefault();

        if (slot == null)
        {
            if (logDispatches) Debug.LogWarning($"[BusScheduler] TransferSlotToPlayer failed: no slot found for busID {npcBusID}.");
            return null;
        }
        DetachFromBus(npcBusID);
        MoveChainToPlayer(npcBusID, slot); // this rotation's remaining legs move with it (not the bus's later ones)
        _slotByBus[PLAYER_BUS_ID] = slot;
        BusManager.Instance?.ReleaseBusReservation(PLAYER_BUS_ID);
        return slot;
    }

    public TimetableSlot TransferSlotToBus(int fromBusID, int toBusID)
    {
        if (IsPlayer(fromBusID) || IsPlayer(toBusID))
        {
            Debug.LogWarning("[BusScheduler] TransferSlotToBus is NPC-only — use TransferSlotToPlayer for player handoffs.");
            return null;
        }
        if (!_slotByBus.TryGetValue(fromBusID, out var slot)) return null;

        var routeData = GetRouteData(slot.routeNumber);
        var variant = !string.IsNullOrEmpty(slot.variantLetter) ? routeData?.GetVariant(slot.variantLetter) : null;
        if (!CanAssign(toBusID, routeData, variant, out string reason, slot.scheduledDeparture % 1440f))
        {
            if (logDispatches) Debug.LogWarning($"[BusScheduler] TransferSlotToBus denied for Bus#{toBusID} ({reason}).");
            return null;
        }

        DetachFromBus(fromBusID);
        ReassignChain(fromBusID, toBusID, SlotState.AssignedNPC); // whole day's remaining legs move with it
        _slotByBus[toBusID] = slot;
        BusManager.Instance?.ReleaseBusReservation(toBusID);
        if (logDispatches) Debug.Log($"[BusScheduler] Handoff: Bus#{fromBusID} → Bus#{toBusID} on Route {slot.FullRouteLabel}.");
        return slot;
    }

    public void RequestRelief(int playerBusID, string routeNumber, bool currentOutbound)
    {
        if (logDispatches) Debug.Log($"[BusScheduler] {GameTimeString} Player relief requested on Route {routeNumber}.");
        OnReplacementNeeded?.Invoke(playerBusID);
    }

    /// <summary>[FIX] Used to call ReassignChain(fromBusID, replacementBusID, ...)
    /// unconditionally — ReassignChain moves every slot for fromBusID whose
    /// state isn't Unassigned/Completed, which includes InService: the leg
    /// currently being physically driven, not just the pre-filled future
    /// ones. RequestRelief() has no terminal check, so confirming a relief
    /// swap while still mid-route handed the replacement bus your CURRENT,
    /// unfinished leg — its assignedBusID flipped away from you instantly,
    /// while you kept driving the same physical bus toward the same
    /// terminal with nothing recorded under your busID anymore (dispSlot ==
    /// null everywhere that reads _slotByBus[PLAYER_BUS_ID] — lateness
    /// display, ETA board, schedule-adherence all silently went blank).
    ///
    /// Now: if the bus is still InService on its current leg, only the
    /// FUTURE pre-filled legs (GetPromotableChainSlot onward) are handed to
    /// the replacement — the current leg stays yours until you actually
    /// finish it (CompleteSlot/CompleteSlotAndRetire, called from arrival),
    /// exactly like a normal handoff is supposed to work. If nothing's
    /// pre-filled yet to hand over, the swap is refused rather than silently
    /// doing nothing — the caller (ConfirmReliefSwap) already has a retry
    /// path for that.</summary>
    public bool TryClaimNextReliefSlotForReplacement(int fromBusID, int replacementBusID)
    {
        if (_slotByBus.TryGetValue(replacementBusID, out _))
        {
            Debug.LogWarning($"[BusScheduler] Failed: Bus#{replacementBusID} is busy.");
            return false;
        }
        if (!_slotByBus.TryGetValue(fromBusID, out var currentSlot)) return false;

        bool stillDrivingCurrentLeg = currentSlot.state == SlotState.InService;
        TimetableSlot handoffSlot = stillDrivingCurrentLeg
            ? GetPromotableChainSlot(fromBusID, currentSlot.scheduledDeparture)
            : currentSlot;

        if (handoffSlot == null)
        {
            Debug.LogWarning($"[BusScheduler] Relief denied: Bus#{fromBusID} is still finishing its current leg and has no future leg queued yet for Bus#{replacementBusID} to take over.");
            return false;
        }

        var routeData = GetRouteData(handoffSlot.routeNumber);
        var variant = !string.IsNullOrEmpty(handoffSlot.variantLetter) ? routeData?.GetVariant(handoffSlot.variantLetter) : null;
        if (!CanAssign(replacementBusID, routeData, variant, out string reason, handoffSlot.scheduledDeparture % 1440f))
        {
            Debug.LogWarning($"[BusScheduler] Relief denied: Bus#{replacementBusID} ({reason}).");
            return false;
        }

        var state = IsPlayer(replacementBusID) ? SlotState.AssignedPlayer : SlotState.AssignedNPC;

        if (stillDrivingCurrentLeg)
        {
            // Move only handoffSlot and everything scheduled after it — the
            // current InService leg stays under fromBusID untouched.
            foreach (var s in _allSlots.Where(s => s.assignedBusID == fromBusID
                                                    && s.state != SlotState.Unassigned
                                                    && s.state != SlotState.Completed
                                                    && s.scheduledDeparture >= handoffSlot.scheduledDeparture))
            {
                s.assignedBusID = replacementBusID;
                s.state = state;
            }
        }
        else
        {
            DetachFromBus(fromBusID);
            ReassignChain(fromBusID, replacementBusID, state); // whole day's remaining legs move with it, including the not-yet-started current one
        }

        _slotByBus[replacementBusID] = handoffSlot;
        BusManager.Instance?.ReleaseBusReservation(replacementBusID);

        BusManager.Instance?.ClaimBusForHandoff(replacementBusID);
        return true;
    }

    private void DetachFromBus(int busID) => _slotByBus.Remove(busID);

    private void AttachToBus(TimetableSlot slot, int busID, SlotState state)
    {
        slot.assignedBusID = busID;
        slot.state = state;
        _slotByBus[busID] = slot;
        BusManager.Instance?.ReleaseBusReservation(busID);
    }
    private void TopUpChain(int busID, TimetableSlot fromSlot)
    {
        if (fromSlot == null) return;
        bool isPlayer = IsPlayer(busID);
        var state = isPlayer ? SlotState.AssignedPlayer : SlotState.AssignedNPC;

        string route = fromSlot.routeNumber;
        string variant = fromSlot.variantLetter ?? "";
        bool dir = fromSlot.isOutbound;
        float dep = fromSlot.scheduledDeparture;
        var routeData = GetRouteData(route);
        var variantData = !string.IsNullOrEmpty(variant) ? routeData?.GetVariant(variant) : null;
        // [Count II-a/V] Window-scaled for this leg's own departure time;
        // recomputed per iteration below as dep advances, since a chain can
        // cross window boundaries (e.g. daytime service running into the
        // overnight window). Uses the variant's own trip time/windows instead
        // of the mainline's when this chain is on an overrideSchedule variant.
        float EffectiveTripMinutesAt(float atDep) =>
            variantData != null && variantData.overrideSchedule
                ? variantData.EffectiveTripMinutes(routeData?.oneWayTripMinutes ?? 45f, atDep % 1440f)
                : TripMinutesForRoute(route, atDep % 1440f);
        float tripMinutes = EffectiveTripMinutesAt(dep);

        int fromLap = fromSlot.chainLegIndex > 0 ? fromSlot.chainLegIndex : 1;
        int lapCursor = fromLap;
        float accum = AccumOf(fromSlot);
        fromSlot.serviceMinutesAccum = accum;
        bool unlimited = ServiceBudgetFor(route) <= 0f;

        // [FIX Bug 4] The service-minute budget above only counts time
        // actually driving the route -- layover/deadhead/depot time between
        // legs is free, so a chain of short, low-frequency legs could rack
        // up hours of real wall-clock commitment while still comfortably
        // under budget (the reported case: a 27-min route chained 6 legs
        // spanning 5:00-8:40, ~2 real hours). Cap the number of legs
        // actually pre-chained, independent of the service-minute math,
        // scaled down for longer trips since each one eats more real time.
        int legsAdded = 0;
        int maxLegs = MaxPrechainedLegsFor(tripMinutes);

        // Fill up to the service-minute budget (real on-route trip minutes only).
        while (legsAdded < maxLegs && (unlimited || !ServiceBudgetReached(route, accum, tripMinutes)))
        {
            bool nextDir = !dir;
            // [FIX Bug 5] Was purely dep + tripMinutes + minLayoverMinutes —
            // the ORIGINAL plan's math, computed from scheduledDeparture with
            // no regard for whether the bus is actually running late. A bus
            // 2 hours late still got a notBefore computed as if it departed
            // exactly on time, so it would happily self-claim a next slot
            // that's already in the past relative to when it really finished.
            // Floor it against the actual current clock so lateness is
            // reflected in what "earliest eligible" even means.
            float notBefore = Mathf.Max(dep + tripMinutes + minLayoverMinutes,
                                         SimClock.Instance.AbsoluteGameMinutes);
            var next = _allSlots
                .Where(s => s.routeNumber == route && (s.variantLetter ?? "") == variant
                            && s.isOutbound == nextDir && s.state == SlotState.Unassigned
                            && s.scheduledDeparture >= notBefore)
                .OrderBy(s => s.scheduledDeparture)
                .FirstOrDefault();
            if (next == null)
            {
                if (logDispatches)
                    Debug.Log($"[BusScheduler][TOPUP] Bus#{busID}: TopUpChain stopped early on Route {route}{variant} — no Unassigned {(nextDir ? "outbound" : "inbound")} slot found >= {MinutesToTimeString(notBefore)}. " +
                              $"more service minutes wanted but no slot generated (horizon edge). This bus will hit the fresh-claim fallback in CompleteSlot when it gets here.");
                break;
            }

            lapCursor++;
            legsAdded++;
            next.chainLegIndex = lapCursor;
            next.assignedBusID = busID;
            next.state = state;
            dir = nextDir;
            dep = next.scheduledDeparture;
            tripMinutes = EffectiveTripMinutesAt(dep);
            accum += tripMinutes;
            next.serviceMinutesAccum = accum;
        }
    }

    /// <summary>[ADD Bug 4 fix] How many legs TopUpChain will pre-chain onto
    /// one rotation, independent of the service-minute budget. Tuned so a
    /// typical ~20-30min one-way route lands in the requested 4-5 range,
    /// scaling down for longer routes (each leg is a bigger real-time
    /// commitment) and up for short ones, clamped to [2, 5] either way.</summary>
    private static int MaxPrechainedLegsFor(float tripMinutes)
    {
        if (tripMinutes <= 0f) tripMinutes = DefaultTripMinutes;
        return Mathf.Clamp(Mathf.RoundToInt(110f / tripMinutes), 2, 5);
    }
    private bool IsTooFarAhead(TimetableSlot s) =>
        s != null && s.scheduledDeparture - SimClock.Instance.AbsoluteGameMinutes > maxTerminalHoldMinutes;

    private TimetableSlot GetPromotableChainSlot(int busID, float afterDeparture)
    {
        return FindEarliestSlotAfter(afterDeparture, s =>
            s.assignedBusID == busID
            && (s.state == SlotState.AssignedNPC || s.state == SlotState.AssignedPlayer)
            && s.scheduledDeparture > afterDeparture);
    }

    private void ReassignChain(int fromBusID, int toBusID, SlotState newState)
    {
        for (int i = 0; i < _allSlots.Count; i++)
        {
            var s = _allSlots[i];
            if (s.assignedBusID == fromBusID && s.state != SlotState.Unassigned && s.state != SlotState.Completed)
            {
                s.assignedBusID = toBusID;
                s.state = newState;
            }
        }
    }

    /// <summary>Frees a bus's ENTIRE remaining pre-filled chain back to
    /// Unassigned — used when a bus goes idle/retires so it stops hoarding
    /// future legs it will never actually run.</summary>
    private void ReleaseChain(int busID)
    {
        int freed = 0;
        TimetableSlot first = null, last = null;
        for (int i = 0; i < _allSlots.Count; i++)
        {
            var s = _allSlots[i];
            if (s.assignedBusID == busID && s.state != SlotState.Unassigned && s.state != SlotState.Completed)
            {
                // Route Manager: a trip the manager fixed by hand stays with its bus.
                if (s.state == SlotState.AssignedNPC && ManagerLocks.IsSlotLocked(s)) continue;
                s.assignedBusID = -1;
                s.state = SlotState.Unassigned;
                freed++;
                if (first == null) first = s;
                last = s;
            }
        }
        // Freed slots show as TBD until something is dispatched onto them, so say exactly what went and why.
        if (freed > 0 && logDispatches)
            Debug.LogWarning($"[BusScheduler][RELEASE] {(IsPlayer(busID) ? "PLAYER" : $"Bus#{busID}")} released {freed} slot(s) back to TBD: " +
                             $"Route {first.FullRouteLabel} {MinutesToTimeString(first.scheduledDeparture)} -> {last.FullRouteLabel} {MinutesToTimeString(last.scheduledDeparture)}.");
    }

    // ═════════════════════════════════════════════════════════════════════
    //  ASSIGNMENT / DEPARTURE / COMPLETION
    // ═════════════════════════════════════════════════════════════════════
    public bool CommitAssignment(TimetableSlot slot, int busID, SlotState state)
    {
        if (slot.assignedBusID != -1 && slot.assignedBusID != busID)
        {
            Debug.LogWarning($"[BusScheduler] CRITICAL: Slot {slot.FullRouteLabel} already has Bus#{slot.assignedBusID}.");
            return false;
        }
        if (!IsPlayer(busID) && slot.assignedBusID != busID)
        {
            slot.chainLegIndex = 1;
            slot.serviceMinutesAccum = TripMinutesForSlot(slot);
        }

        AttachToBus(slot, busID, state);
        return true;
    }

    /// <summary>Returns false when the bus was refused (vehicle policy, route full, slot already taken) — the caller
    /// must NOT go on to send that bus out on the route, or the road fills with buses the timetable doesn't know.</summary>
    public bool AssignBusToSlot(TimetableSlot slot, int busID)
    {
        var routeData = GetRouteData(slot.routeNumber);
        var variant = !string.IsNullOrEmpty(slot.variantLetter) ? routeData?.GetVariant(slot.variantLetter) : null;
        if (!CanAssign(busID, routeData, variant, out string reason, slot.scheduledDeparture % 1440f))
        {
            Debug.LogError($"[BusScheduler] AssignBusToSlot BLOCKED: Bus#{busID} on Route {slot.FullRouteLabel} ({reason}).");
            return false;
        }
        if (!CommitAssignment(slot, busID, IsPlayer(busID) ? SlotState.AssignedPlayer : SlotState.AssignedNPC)) return false;
        TopUpChain(busID, slot);
        return true;
    }

    public void RecordActualDeparture(int busID, float gameTimeMinutes, TimetableSlot knownSlot = null)
    {

        var slot = knownSlot ?? (_slotByBus.TryGetValue(busID, out var looked) ? looked : null);
        if (slot == null) return;
        if (knownSlot != null)
        {
            // Repair any drift, not just avoid it this once. If the map was pointing at an EARLIER leg of this
            // same bus, that leg is behind it now -- close it instead of leaving it InService/Assigned forever.
            if (_slotByBus.TryGetValue(busID, out var stale) && stale != knownSlot
                && stale.assignedBusID == busID && stale.state != SlotState.Completed
                && stale.scheduledDeparture < knownSlot.scheduledDeparture)
            {
                stale.state = SlotState.Completed;
                if (logDispatches)
                    Debug.LogWarning($"[BusScheduler] Bus#{busID} departed {knownSlot.FullRouteLabel} {MinutesToTimeString(knownSlot.scheduledDeparture)} while the map still held earlier leg {MinutesToTimeString(stale.scheduledDeparture)} — closed it.");
            }
            _slotByBus[busID] = knownSlot;
        }
        slot.state = SlotState.InService;
        slot.actualDeparture = gameTimeMinutes;
        slot.latenessMinutes = gameTimeMinutes - slot.scheduledDeparture;
    }
    public void CompleteSlot(int busID)
    {
        if (_completedThisFrame.Contains(busID))
        {
            Debug.LogWarning($"[BusScheduler] Bus#{busID} tried to CompleteSlot twice this frame — ignored.");
            return;
        }
        _completedThisFrame.Add(busID);

        if (!_slotByBus.TryGetValue(busID, out var slot)) return;
        bool isPlayer = IsPlayer(busID);
        string completedRoute = slot.routeNumber;
        float completedDep = slot.scheduledDeparture;
        bool completedWasOutbound = slot.isOutbound;

        slot.state = SlotState.Completed;
        _slotByBus.Remove(busID);
        OnSlotCompleted?.Invoke(slot, isPlayer, busID);

        // Route Manager: "move after this trip" — this bus finishes here and goes to the garage,
        // so the manager can give it its new trip once it is parked.
        if (!isPlayer && ManagerLocks.TakeParkAfterTrip(busID))
        {
            ReleaseChain(busID);
            OnBusRetiredFromRoute?.Invoke(busID, completedRoute);
            OnIdleBus?.Invoke(slot);
            return;
        }

int completedLap = slot.chainLegIndex > 0 ? slot.chainLegIndex : 1;
// [FIX] _slotByBus.Remove(busID) already ran above -- the busID-only lookup
// GetEffectiveLapThreshold falls back to would silently miss this bus's route
// and return the flat default instead of the trip-scaled value. Pass
// completedRoute explicitly since it's already captured.
int myLapThreshold = GetEffectiveLapThreshold(busID, completedRoute); // display/log only
bool serviceBudgetReached = ServiceBudgetReached(completedRoute, AccumOf(slot), TripMinutesForSlot(slot));

if (isPlayer && serviceBudgetReached)
{
    if (logDispatches)
        Debug.Log($"[BusScheduler][LAP] Player Bus#{busID} hit lap threshold ({completedLap}/{myLapThreshold}) on Route {completedRoute}{slot.variantLetter} — ending rotation here. No auto-continuation or fresh claim for the player.");
    // Interlined route available (terminal rules honored)? Carry the player straight onto it.
    if (TryCrossRouteHandoff(busID, completedRoute, completedDep + TripMinutesForSlot(slot), completedWasOutbound, out var playerInterline))
    {
        _slotByBus[busID] = playerInterline;
        return;
    }
    ReleaseChain(busID);
    OnIdleBus?.Invoke(slot);
    return;
}

if (!isPlayer && serviceBudgetReached)
{
    if (logDispatches)
        Debug.Log($"[BusScheduler][LAP] Bus#{busID} hit retirement threshold: completedLap={completedLap} >= threshold={myLapThreshold} (target {targetServiceMinutesBeforeRetirement:F0}min/service) on Route {completedRoute}{slot.variantLetter}. Checking for a genuine open slot before retiring.");
    var chainedNext = GetPromotableChainSlot(busID, completedDep);
    // A leg pre-assigned to this bus but hours away (a second rotation the pool gave the same identity)
    // isn't a reason to sit at the terminal: send the bus home and keep the slot for when it's due.
    bool farChain = IsTooFarAhead(chainedNext);
    if (chainedNext == null || farChain)
    {
        var routeDataForRetirement = GetRouteData(completedRoute);
        var variantForRetirement = !string.IsNullOrEmpty(slot.variantLetter)
            ? routeDataForRetirement?.GetVariant(slot.variantLetter)
            : null;
        // [ADD] A road event still active on this route/direction means the NEXT bus into that same
        // detour is just as delayed as this one was -- push the earliest-next-assignment time out by
        // however much extra driving time RoadEventRegistry says that detour currently costs, instead
        // of scheduling as if the road were clear again the instant this trip ended.
        float roadEventExtra = RoadEventRegistry.Instance != null
            ? RoadEventRegistry.Instance.GetExtraMinutes(completedRoute, completedWasOutbound) : 0f;
        float retirementNotBefore = completedDep + TripMinutesForSlot(slot) + minLayoverMinutes + roadEventExtra;
        var freshNext = GetNextUnassignedSlot(completedRoute, slot.variantLetter, retirementNotBefore, !completedWasOutbound);
        if (IsTooFarAhead(freshNext)) freshNext = null; // leave it for dispatch when it's due instead of parking for hours

        string continueDenial = null;
        if (freshNext != null && CanContinueRoute(busID, routeDataForRetirement, variantForRetirement, out continueDenial, freshNext.scheduledDeparture % 1440f))
        {
            CommitAssignment(freshNext, busID, SlotState.AssignedNPC);
            _slotByBus[busID] = freshNext;
            // CommitAssignment stamped freshNext as lap 1 — a brand-new chain,
            // which resets the retirement clock structurally.
            TopUpChain(busID, freshNext);
            return;
        }

        // Nothing left to run on THIS route — try another route the same
        // depot serves before giving up and going idle.
        if (TryCrossRouteHandoff(busID, completedRoute, retirementNotBefore, completedWasOutbound, out var handoffSlot))
        {
            _slotByBus[busID] = handoffSlot;
            return;
        }
        if (logDispatches)
        {
            if (freshNext == null)
            {
                var nearest = _allSlots
                    .Where(s => s.routeNumber == completedRoute && (s.variantLetter ?? "") == (slot.variantLetter ?? "")
                                && s.scheduledDeparture > completedDep)
                    .OrderBy(s => s.scheduledDeparture)
                    .FirstOrDefault();
                string nearestInfo = nearest == null ? "no further slots generated on this route/variant"
                    : $"nearest is {MinutesToTimeString(nearest.scheduledDeparture)} {nearest.DirectionLabel}, state={nearest.state}, " +
                      $"owned by {(nearest.assignedBusID < 0 ? "nobody (should have matched!)" : $"Bus#{nearest.assignedBusID}")}";
                Debug.LogWarning($"[BusScheduler][DEPOT-CAUSE] Bus#{busID} retiring from Route {completedRoute}: no Unassigned slot found >= {MinutesToTimeString(retirementNotBefore)}. {nearestInfo}");
            }
            else
            {
                Debug.LogWarning($"[BusScheduler][DEPOT-CAUSE] Bus#{busID} retiring from Route {completedRoute}: found {MinutesToTimeString(freshNext.scheduledDeparture)} but CanContinueRoute denied it ({continueDenial}).");
            }
        }

        if (!farChain) ReleaseChain(busID); // a far pre-assigned rotation stays with the bus; it is dispatched when due
        OnBusRetiredFromRoute?.Invoke(busID, completedRoute);
        OnIdleBus?.Invoke(slot);
        return;
    }
}

        var nextSlot = GetPromotableChainSlot(busID, completedDep);
        // Same rule as the retirement branch: a pre-assigned leg hours away is not promoted (NPC only).
        bool farNextChain = !isPlayer && IsTooFarAhead(nextSlot);
        if (farNextChain) nextSlot = null;

        if (logDispatches)
        {
            Debug.Log($"[BusScheduler][LAP] Bus#{busID} completed lap {completedLap}/{myLapThreshold} on Route {completedRoute}{slot.variantLetter} " +
                      $"dep={MinutesToTimeString(completedDep)} outbound={completedWasOutbound}.");
            Debug.Log(nextSlot == null
                ? $"[BusScheduler][CHAIN] Bus#{busID}: GetPromotableChainSlot found NOTHING pre-filled after {MinutesToTimeString(completedDep)} — falling through to fresh-claim path."
                : $"[BusScheduler][CHAIN] Bus#{busID}: pre-filled next leg found → {nextSlot.FullRouteLabel} {MinutesToTimeString(nextSlot.scheduledDeparture)} (chainLegIndex={nextSlot.chainLegIndex}).");
        }

        if (nextSlot != null)
        {
            _slotByBus[busID] = nextSlot; // promote the pre-filled leg to "current"
            if (logDispatches)
            {
                float parkMin = nextSlot.scheduledDeparture - SimClock.Instance.AbsoluteGameMinutes;
                if (parkMin > 60f)
                    Debug.LogWarning($"[BusScheduler][LONG-PARK] Bus#{busID} promoted to {nextSlot.FullRouteLabel} {MinutesToTimeString(nextSlot.scheduledDeparture)} " +
                                     $"(chainLegIndex={nextSlot.chainLegIndex}) — will sit at the terminal {parkMin:0} min and count toward the route cap.");
            }
        }
        else
        {
            // No pre-filled leg — this IS a fresh claim, so the cap/policy
            // gate applies here.
            var routeData = GetRouteData(completedRoute);
            var variant = !string.IsNullOrEmpty(slot.variantLetter)
                ? routeData?.GetVariant(slot.variantLetter)
                : null;

            // [FIX Bug 5] Same fix as TopUpChain — this is
            // the "fresh claim" fallback path an early/on-time bus should
            // rarely even reach once Bug 5 is fixed elsewhere, but it has the
            // identical stale-time-basis bug in its own right: floor against
            // the actual current clock, not just completedDep + trip + layover.
            // [ADD] Same road-event allowance as the retirement path above.
            float continuationRoadEventExtra = RoadEventRegistry.Instance != null
                ? RoadEventRegistry.Instance.GetExtraMinutes(completedRoute, completedWasOutbound) : 0f;
            float continuationNotBefore = Mathf.Max(completedDep + TripMinutesForSlot(slot) + minLayoverMinutes + continuationRoadEventExtra,
                                                      SimClock.Instance.AbsoluteGameMinutes);

            if (!CanContinueRoute(busID, routeData, variant, out string denyReason, continuationNotBefore % 1440f))
            {

                Debug.LogWarning($"[BusScheduler][DEPOT-CAUSE] Bus#{busID} DENIED continuation on Route {completedRoute}{slot.variantLetter} " +
                                  $"after lap {completedLap}: CanContinueRoute() said NO (vehicle policy only — cap is NOT checked here since this is a rotation continuation, not a fresh claim) — reason: \"{denyReason}\". " +
                                  $"Bus will retire/idle from here. (No pre-filled chain slot existed either.)");

                if (TryCrossRouteHandoff(busID, completedRoute, completedDep + TripMinutesForSlot(slot), completedWasOutbound, out var handoffSlot))
                {
                    _slotByBus[busID] = handoffSlot;
                    return;
                }

                if (!farNextChain) ReleaseChain(busID); // a far pre-assigned rotation stays with the bus
                OnIdleBus?.Invoke(slot);
                if (!isPlayer) OnBusRetiredFromRoute?.Invoke(busID, completedRoute);
                return;
            }
            float freshNotBefore = continuationNotBefore;

            if (logDispatches)
                Debug.Log($"[BusScheduler][FRESH-CLAIM] Bus#{busID}: CanContinueRoute passed. Searching for a fresh {(string.IsNullOrEmpty(slot.variantLetter) ? "mainline" : $"variant '{slot.variantLetter}'")} " +
                          $"slot on Route {completedRoute}{slot.variantLetter}, direction-continues-from-outbound={completedWasOutbound}, " +
                          $"scheduledDeparture >= {MinutesToTimeString(freshNotBefore)} (= dep {MinutesToTimeString(completedDep)} + trip {TripMinutesForSlot(slot):0}min + layover {minLayoverMinutes:0}min).");

            nextSlot = GetNextUnassignedSlot(completedRoute, slot.variantLetter, freshNotBefore, !completedWasOutbound);
            if (!isPlayer && IsTooFarAhead(nextSlot)) nextSlot = null; // don't park at the terminal for hours

            if (nextSlot != null)
            {
                CommitAssignment(nextSlot, busID, isPlayer ? SlotState.AssignedPlayer : SlotState.AssignedNPC);

                nextSlot.chainLegIndex = completedLap + 1;
                nextSlot.serviceMinutesAccum = AccumOf(slot) + TripMinutesForSlot(nextSlot);
                if (logDispatches)
                    Debug.Log($"[BusScheduler][FRESH-CLAIM] Bus#{busID}: found and claimed {nextSlot.FullRouteLabel} {MinutesToTimeString(nextSlot.scheduledDeparture)} " +
                              $"as lap {nextSlot.chainLegIndex}.");
            }
            else if (logDispatches)
            {
                var nearest = _allSlots
                    .Where(s => s.routeNumber == completedRoute && (s.variantLetter ?? "") == (slot.variantLetter ?? ""))
                    .Where(s => s.scheduledDeparture >= completedDep)
                    .OrderBy(s => s.scheduledDeparture)
                    .Take(4)
                    .Select(s => $"[{MinutesToTimeString(s.scheduledDeparture)} {s.DirectionLabel} state={s.state} owner={(s.assignedBusID < 0 ? "none" : $"Bus#{s.assignedBusID}")}]");
                Debug.LogWarning($"[BusScheduler][DEPOT-CAUSE] Bus#{busID}: NO fresh slot found on Route {completedRoute}{slot.variantLetter} " +
                                  $">= {MinutesToTimeString(freshNotBefore)}. Nearest slots on this route/variant: {string.Join(" ", nearest)}");
            }
        }

        if (nextSlot == null)
        {
            if (TryCrossRouteHandoff(busID, completedRoute, completedDep + TripMinutesForSlot(slot), completedWasOutbound, out var handoffSlot))
            {
                _slotByBus[busID] = handoffSlot;
                return;
            }

            if (logDispatches) Debug.LogWarning($"[BusScheduler][DEPOT-CAUSE] Bus#{busID}: no return slot for Route {completedRoute} at all — going idle/depot.");
            OnIdleBus?.Invoke(slot);
            if (!isPlayer) OnBusRetiredFromRoute?.Invoke(busID, completedRoute);
            return;
        }

        if (logDispatches)
            Debug.Log($"[BusScheduler][CHAIN] Bus#{busID}: current leg promoted to {nextSlot.FullRouteLabel} {MinutesToTimeString(nextSlot.scheduledDeparture)} (lap {nextSlot.chainLegIndex}). Topping up chain behind it.");

        TopUpChain(busID, nextSlot); // keep the rest of the day filled in behind it
    }

    public void CompleteSlotAndRetire(int busID)
    {
        if (_slotByBus.TryGetValue(busID, out var slot))
        {
            slot.state = SlotState.Completed;
            slot.assignedBusID = -1;
            _slotByBus.Remove(busID);
        }
        ReleaseChain(busID); // any further pre-filled legs shouldn't sit reserved to a retired bus
        if (logDispatches) Debug.Log($"[BusScheduler] Bus#{busID} completed and terminated after relief.");
    }

    public bool RescheduleBus(int busID, string routeNumber, bool isOutbound)
    {
        CompleteSlot(busID);
        var routeData = GetRouteData(routeNumber);
        if (!CanAssign(busID, routeData, out string reason, SimClock.Instance.AbsoluteGameMinutes % 1440f))
        {
            Debug.LogWarning($"[BusScheduler] RescheduleBus denied for Bus#{busID} ({reason}).");
            return false;
        }

        var slot = PeekUpcomingSlots(routeNumber, "", isOutbound, 1).FirstOrDefault()
                   ?? PeekUpcomingSlots(routeNumber, "", !isOutbound, 1).FirstOrDefault();
        if (slot == null) { Debug.LogWarning($"[BusScheduler] No future slot to reschedule Bus#{busID}."); return false; }

        if (!CommitAssignment(slot, busID, IsPlayer(busID) ? SlotState.AssignedPlayer : SlotState.AssignedNPC)) return false;
        TopUpChain(busID, slot);
        return true;
    }
    public void ReleasePlayerSlot(int playerBusID) => ReleaseSlotWithoutComplete(playerBusID);

    public void ReleaseSlotWithoutComplete(int busID)
    {
        if (!_slotByBus.TryGetValue(busID, out var slot)) return;
        _slotByBus.Remove(busID);
        ReleaseChain(busID); // frees this leg AND every pre-filled leg still ahead of it
        if (logDispatches) Debug.Log($"[BusScheduler] Bus#{busID} released slot on Route {slot.FullRouteLabel}.");
    }

    /// <summary>[FIX] Was picking the FIRST idle/at-terminal/depot-ingress
    /// NPC in fleet iteration order with NO eligibility check at all --
    /// terminalCode wasn't even used anywhere in the body despite being the
    /// only parameter. That meant this could "find" a bus from a completely
    /// different depot with zero policy eligibility for the route in
    /// question, which the REAL assignment paths (AssignReplacementForPlayer/
    /// TryClaimNextReliefSlotForReplacement) then correctly rejected --
    /// exactly the "found a replacement, then couldn't assign it" symptom.
    /// Now takes the route explicitly and runs every candidate through the
    /// same CanAssign() check the actual assignment call uses, so a bus
    /// only ever gets offered here if it would actually be accepted later.
    /// Also now genuinely prefers the nearest candidate to terminalCode
    /// instead of ignoring it.</summary>
    /// <summary>playerBusID (optional, defaults to -2) identifies who this search is actually
    /// for -- pass BusScheduler.PLAYER_BUS_ID from the caller's own context so the result routes
    /// back to the right player via NotifyReplacementFound/NotifyReplacementSearchFailed instead
    /// of always landing on the host's own local PlayerHandoff.Instance.</summary>
    public void InitiateReliefSearch(string terminalCode, string routeNumber, int playerBusID = -2)
    {
        var routeData = GetRouteData(routeNumber);
        var terminalStop = FindStopByCode(terminalCode);

        NPCBusController best = null;
        float bestDist = float.MaxValue;

        if (BusManager.Instance != null)
        {
            foreach (var record in BusManager.Instance.GetAllRecords())
            {
                var ctrl = record.controller as NPCBusController;
                if (ctrl == null) continue;
                if (ctrl.State != NPCBusController.BusState.AtTerminal
                    && ctrl.State != NPCBusController.BusState.Idle
                    && ctrl.State != NPCBusController.BusState.DepotIngress) continue;

                // [FIX] The actual eligibility check the assignment step
                // will run anyway -- reject here instead of offering a bus
                // that's just going to fail moments later.
                if (!CanAssign(ctrl.busID, routeData, out string _, SimClock.Instance != null ? SimClock.Instance.AbsoluteGameMinutes % 1440f : -1f))
                    continue;

                float dist = terminalStop != null
                    ? Vector3.Distance(ctrl.transform.position, terminalStop.GetWorldPosition())
                    : 0f;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = ctrl;
                }
            }
        }

        if (best != null)
        {
            if (best.State == NPCBusController.BusState.DepotIngress) best.AbortDepotIngress();
            if (logDispatches) Debug.Log($"[BusScheduler] Relief found: busID={best.busID} fleet#{best.fleetNumber}, eligible for Route {routeNumber}.");
            NotifyReplacementFound(playerBusID, best.busID);
        }
        else
        {
            Debug.LogWarning($"[BusScheduler] No eligible replacement bus found for Route {routeNumber} near {terminalCode}.");
            NotifyReplacementSearchFailed(playerBusID);
        }
    }

    private readonly HashSet<TimetableSlot> _earlyStaged = new();

    private readonly List<TimetableSlot> _starvedUnassigned = new();
    private int _starvedRetryCounter = 0;

    private IEnumerator SchedulerTick()
    {
        while (true)
        {
            yield return new WaitForSeconds(1f);

            // [ADD] Multiplayer: dispatch/scheduling decisions are server-authoritative -- a
            // network client must not independently decide which NPC buses launch, retire, or get
            // early-staged. It still has its own full local fleet (see NetworkGameBridge's header
            // comment), but its OWN dispatch decisions would never match the host's, so it just
            // displays whatever the host's broadcast says instead. Single-player and host both
            // keep ticking normally.
            if (!NetworkAuthority.ShouldSimulate) continue;

            float now = SimClock.Instance.AbsoluteGameMinutes;
            while (_processedUpTo < _allSlots.Count && _allSlots[_processedUpTo].scheduledDeparture <= now)
            {
                HandleSlotDue(_allSlots[_processedUpTo]);
                _processedUpTo++;
            }

            for (int i = _processedUpTo; i < _allSlots.Count; i++)
            {
                var s = _allSlots[i];
                if (s.scheduledDeparture - now > earlyDispatchWindowMinutes) break; // sorted by departure — nothing further qualifies yet
                if (s.state != SlotState.AssignedNPC || s.assignedBusID < 0) continue;
                if (_slotByBus.ContainsKey(s.assignedBusID)) continue; // already spawned/attached
                if (_earlyStaged.Contains(s)) continue;
                // [FIX] This slot still SAYS AssignedNPC, but the physical bus it names might have
                // been picked up by a player for a completely different route since this slot was
                // generated -- see IsPhysicalBusSecretlyPossessed's own comment for the full story.
                if (IsPhysicalBusSecretlyPossessed(s.assignedBusID)) continue;

                _earlyStaged.Add(s);
                OnDispatchBus?.Invoke(s);
                if (logDispatches) Debug.Log($"[BusScheduler] {GameTimeString} Early-staged Bus#{s.assignedBusID} at terminal for Route {s.FullRouteLabel} {s.DirectionLabel} @ {MinutesToTimeString(s.scheduledDeparture)}");
            }

            CheckForExpressDeadRunCandidates();

            _starvedRetryCounter++;
            if (_starvedRetryCounter >= 15 && _starvedUnassigned.Count > 0) // ~15s at this loop's 1s tick rate
            {
                _starvedRetryCounter = 0;
                for (int i = _starvedUnassigned.Count - 1; i >= 0; i--)
                {
                    var s = _starvedUnassigned[i];
                    if (s.state != SlotState.Unassigned) { _starvedUnassigned.RemoveAt(i); continue; } // claimed since
                    OnDispatchBus?.Invoke(s);
                }
            }
        }
    }

    private void HandleSlotDue(TimetableSlot slot)
    {
        switch (slot.state)
        {
            case SlotState.Unassigned:
                OnDispatchBus?.Invoke(slot);
                if (logDispatches) Debug.Log($"[BusScheduler] {GameTimeString} Dispatch (unassigned): Route {slot.FullRouteLabel} {slot.DirectionLabel} @ {MinutesToTimeString(slot.scheduledDeparture)}");

                if (slot.state == SlotState.Unassigned) _starvedUnassigned.Add(slot);
                break;

            case SlotState.AssignedNPC:
                // [FIX] Same exception as the early-dispatch loop above (see
                // IsPhysicalBusSecretlyPossessed's own comment) -- this slot still says
                // AssignedNPC, but the physical bus it names might have been picked up by a
                // player for a completely different route since this slot was generated. Retire
                // it quietly instead of dispatching/teleporting the bus a human is currently
                // sitting in.
                if (IsPhysicalBusSecretlyPossessed(slot.assignedBusID))
                {
                    slot.state = SlotState.Completed;
                    if (logDispatches) Debug.Log($"[BusScheduler] {GameTimeString} Skipped stale NPC slot for Bus#{slot.assignedBusID} -- that physical bus is secretly player-possessed now. Route {slot.FullRouteLabel} {slot.DirectionLabel} @ {MinutesToTimeString(slot.scheduledDeparture)}");
                    break;
                }
                if (slot.assignedBusID >= 0 && !_slotByBus.ContainsKey(slot.assignedBusID))
{
                    AttachToBus(slot, slot.assignedBusID, SlotState.AssignedNPC);
                    OnDispatchBus?.Invoke(slot);
                    slot.state = SlotState.InService;
                    OnAssignedDeparture?.Invoke(slot);
                    if (logDispatches) Debug.Log($"[BusScheduler] {GameTimeString} Dispatch+Departure (Bus#{slot.assignedBusID}): Route {slot.FullRouteLabel} {slot.DirectionLabel} @ {MinutesToTimeString(slot.scheduledDeparture)}");
                }
                else if (slot.assignedBusID >= 0 && _slotByBus.TryGetValue(slot.assignedBusID, out var activeSlot) && activeSlot == slot)
                {
                    slot.state = SlotState.InService;
                    OnAssignedDeparture?.Invoke(slot);
                    if (logDispatches) Debug.Log($"[BusScheduler] {GameTimeString} Departure (Bus#{slot.assignedBusID}): Route {slot.FullRouteLabel} {slot.DirectionLabel} @ {MinutesToTimeString(slot.scheduledDeparture)}");
                }
                else if (slot.assignedBusID >= 0 && _slotByBus.TryGetValue(slot.assignedBusID, out var activeNpcSlot)
                         && activeNpcSlot.state != SlotState.Completed
                         && activeNpcSlot.scheduledDeparture < slot.scheduledDeparture)
                {
                    // Bus is still finishing an EARLIER leg — it's running
                    // late, not orphaned. Leave this slot AssignedNPC so
                    // CompleteSlot promotes it once the bus actually arrives,
                    // same fix as the AssignedPlayer branch below.
                    if (logDispatches)
                        Debug.Log($"[BusScheduler] {GameTimeString} Bus#{slot.assignedBusID} still on earlier leg — holding {slot.FullRouteLabel} {slot.DirectionLabel} @ {MinutesToTimeString(slot.scheduledDeparture)} for it (running late).");
                }
                else if (slot.assignedBusID >= 0)
                {
                    slot.state = SlotState.Completed;
                    if (logDispatches)
                        Debug.Log($"[BusScheduler] {GameTimeString} Superseded (Bus#{slot.assignedBusID} already on a later leg): Route {slot.FullRouteLabel} {slot.DirectionLabel} @ {MinutesToTimeString(slot.scheduledDeparture)} marked Completed instead of left stuck.");
                }
                break;

            case SlotState.AssignedPlayer:
                if (slot.assignedBusID != PLAYER_BUS_ID) break;

                _slotByBus.TryGetValue(PLAYER_BUS_ID, out var activePlayer); // null if missing

                if (activePlayer == slot)
                {
                    OnPlayerSlotReady?.Invoke(slot);
                }
                else if (activePlayer != null
                         && activePlayer.state != SlotState.Completed
                         && activePlayer.scheduledDeparture < slot.scheduledDeparture)
                {
                    // Player is still finishing an EARLIER leg, so they're just
                    // late — leave this slot AssignedPlayer so CompleteSlot
                    // promotes it on arrival instead of it being superseded.
                    if (logDispatches)
                        Debug.Log($"[BusScheduler] {GameTimeString} Player still on earlier leg — holding {slot.FullRouteLabel} {slot.DirectionLabel} @ {MinutesToTimeString(slot.scheduledDeparture)} for them (running late).");
                }
                else
                {
                    // Player is on a later leg than this one, or has no active
                    // slot at all: a real orphan.
                    slot.state = SlotState.Completed;
                    if (logDispatches)
                        Debug.Log($"[BusScheduler] {GameTimeString} Superseded (player already on a later leg): Route {slot.FullRouteLabel} {slot.DirectionLabel} @ {MinutesToTimeString(slot.scheduledDeparture)} marked Completed instead of left stuck.");
                }
                break;
        }
    }

    private void CheckForExpressDeadRunCandidates()
    {
        foreach (int busID in _lateness.FindExpressCandidates(_slotByBus).ToList())
        {
            if (!_slotByBus.TryGetValue(busID, out var slot)) continue;
            var newSlot = FindFittableSlot(slot.routeNumber);
            if (newSlot == null) continue;

            var ctrl = BusManager.Instance?.GetRecord(busID)?.controller as NPCBusController;
            if (ctrl == null) continue;

            // Claim the new slot FIRST: if it can't be committed the bus must keep its old slot, not lose both.
            if (!CommitAssignment(newSlot, busID, SlotState.AssignedNPC)) continue;
            _lateness.MarkExpressDeadRun(busID);
            slot.state = SlotState.Completed;
            slot.assignedBusID = -1;
            // (CommitAssignment's AttachToBus already repointed _slotByBus[busID] at newSlot.)
            ctrl.StartExpressDeadRun(newSlot, !newSlot.isOutbound);

            if (logDispatches)
                Debug.LogWarning($"[BusScheduler] {GameTimeString} Bus#{busID} → EXPRESS DEAD-RUN → {MinutesToTimeString(newSlot.scheduledDeparture)}.");
        }
    }

    /// <summary>Binary-searches _allSlots (kept sorted ascending by
    /// scheduledDeparture by SortAll -- same invariant SchedulerTick's
    /// _processedUpTo walk relies on) for the first index at/after
    /// `notBefore`, then scans forward and returns the first slot `match`
    /// accepts. Same result as the
    /// `.Where(...).OrderBy(s => s.scheduledDeparture).FirstOrDefault()`
    /// pattern every "find the next slot meeting some condition" query below
    /// used to duplicate, but without allocating/sorting every match across
    /// potentially thousands of slots (scheduleDaysAhead x every route) just
    /// to keep the single earliest one. `notBefore` only needs to be a safe
    /// lower bound -- callers whose real condition is a strict `>` still
    /// pass their exact floor here; any exact-tie slots the search doesn't
    /// skip just fail `match` and get scanned past for free.</summary>
    private TimetableSlot FindEarliestSlotAfter(float notBefore, Func<TimetableSlot, bool> match)
    {
        int lo = 0, hi = _allSlots.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_allSlots[mid].scheduledDeparture < notBefore) lo = mid + 1;
            else hi = mid;
        }
        for (int i = lo; i < _allSlots.Count; i++)
            if (match(_allSlots[i])) return _allSlots[i];
        return null;
    }

    private TimetableSlot FindFittableSlot(string routeNumber)
    {
        float earliest = SimClock.Instance.AbsoluteGameMinutes + 5f;
        return FindEarliestSlotAfter(earliest, s =>
            s.routeNumber == routeNumber && s.state == SlotState.Unassigned && s.scheduledDeparture >= earliest);
    }

    public void NotifyExpressDeadRunComplete(int busID)
    {
        _lateness.ClearExpressDeadRun(busID);
        float now = SimClock.Instance.AbsoluteGameMinutes;
        if (_slotByBus.TryGetValue(busID, out var slot))
        {
            slot.state = SlotState.InService;
            slot.actualDeparture = now;
            slot.latenessMinutes = now - slot.scheduledDeparture;
        }
        if (logDispatches) Debug.Log($"[BusScheduler] {GameTimeString} Bus#{busID} dead-run complete, resuming service.");
    }
    private Dictionary<string, int> CalculateRequiredBuses()
    {
        var result = new Dictionary<string, int>();
        int today = SimClock.Instance.GameDayNumber;
        foreach (var route in managedRoutes)
        {
            if (route == null) continue;
            float cycleMinutes = Mathf.Max(1f, route.CycleTimeMinutes);
            var departures = _allSlots
                .Where(s => s.routeNumber == route.routeNumber && s.dayNumber == today)
                .Select(s => s.scheduledDeparture)
                .OrderBy(x => x)
                .ToList();

            int required = 1, r = 0;
            for (int l = 0; l < departures.Count; l++)
            {
                float windowEnd = departures[l] + cycleMinutes;
                while (r < departures.Count && departures[r] < windowEnd) r++;
                required = Mathf.Max(required, r - l);
            }
            result[route.routeNumber] = Mathf.Clamp(required, 1, route.maxBusesAllowed);
        }
        return result;
    }

    public Dictionary<int, (string routeNumber, bool startOutbound)> GetShuffledInitialAssignments(int[] busIDs)
    {
        var result = new Dictionary<int, (string, bool)>();
        var requiredBuses = CalculateRequiredBuses();
        var assignedCount = new Dictionary<string, int>();
        foreach (var route in managedRoutes) if (route != null) assignedCount[route.routeNumber] = 0;

        var buses = new List<int>(busIDs);
        for (int i = buses.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (buses[i], buses[j]) = (buses[j], buses[i]);
        }

        foreach (int busID in buses)
        {
            int fleetNum = ResolveFleetNumber(busID);
            var pool = new List<string>();

            foreach (var route in managedRoutes)
            {
                if (route == null) continue;
                if (DepotManager.Instance != null && !DepotManager.Instance.CanServeRoute(busID, route.routeNumber)) continue;
                if (fleetNum >= 0 && !route.IsBusAllowed(fleetNum)) continue;

                int freeOut = _allSlots.Count(s => s.routeNumber == route.routeNumber && (s.variantLetter ?? "") == "" && s.isOutbound && s.state == SlotState.Unassigned);
                int freeIn  = _allSlots.Count(s => s.routeNumber == route.routeNumber && (s.variantLetter ?? "") == "" && !s.isOutbound && s.state == SlotState.Unassigned);
                int baseWeight = Mathf.Max(1, (freeOut + freeIn) / 8);
                int score = fleetNum >= 0 ? route.GetAssignmentScore(fleetNum) : 0;
                int weight = score > 0 ? baseWeight * 2 : baseWeight;

                for (int i = 0; i < weight; i++) pool.Add(route.routeNumber);
            }
            if (pool.Count == 0) continue;

            var underfilled = pool.Where(rn => requiredBuses.ContainsKey(rn) && assignedCount[rn] < requiredBuses[rn]).ToList();

            string chosen = underfilled.Count > 0
                ? underfilled[UnityEngine.Random.Range(0, underfilled.Count)]
                : pool[UnityEngine.Random.Range(0, pool.Count)];

            assignedCount[chosen]++;
            result[busID] = (chosen, UnityEngine.Random.value > 0.5f);
        }

        if (logDispatches) Debug.Log($"[BusScheduler] Shuffled initial assignments for {result.Count}/{busIDs.Length} buses.");
        return result;
    }

    private void PrintScheduleReport()
    {
        var r = new System.Text.StringBuilder();
        r.AppendLine("╔══════════════════════════════════════════════════════════╗");
        r.AppendLine("║  LIVE OPERATIONAL SUMMARY");
        r.AppendLine($"║  Game: {GameTimeString}   Real: {RealTimeString}   Day anchor: {_dayAnchor}");
        r.AppendLine("╠══════════════════════════════════════════════════════════╣");

        foreach (var route in managedRoutes)
        {
            if (route == null) continue;
            string rn = route.routeNumber;
            int cap = GetRouteCap(rn), live = CountActiveBusesOnRoute(rn);
            int freeOut = _allSlots.Count(s => s.routeNumber == rn && s.isOutbound && s.state == SlotState.Unassigned);
            int freeIn  = _allSlots.Count(s => s.routeNumber == rn && !s.isOutbound && s.state == SlotState.Unassigned);

            int running = 0, parked = 0;
            foreach (var kv in _slotByBus)
            {
                if (kv.Value.routeNumber != rn || _gapWaitingBuses.Contains(kv.Key)) continue;
                if (kv.Value.state == SlotState.InService) running++;
                else if (kv.Value.state == SlotState.AssignedNPC && kv.Value.actualDeparture < 0f) parked++;
            }
            r.AppendLine($"  ┌─ ROUTE {rn}  [{live}/{cap} active: {running} running, {parked} parked]  free: {freeOut} out / {freeIn} in ─");

            foreach (var kv in _slotByBus)
            {
                int busID = kv.Key;
                var slot = kv.Value;
                if (slot.routeNumber != rn) continue;
                if (slot.state != SlotState.InService && slot.state != SlotState.AssignedNPC && slot.state != SlotState.AssignedPlayer) continue;

                string who = IsPlayer(busID) ? "PLAYER" : $"Bus#{busID}";
                string express = _lateness.IsExpressDeadRun(busID) ? "  🚌 EXPRESS" : "";
                int laps = slot.chainLegIndex;
                string stTag = "";

                string late;
                if (slot.state == SlotState.AssignedNPC && slot.actualDeparture < 0f)
                {
                    float wait = SimClock.Instance.AbsoluteGameMinutes - slot.scheduledDeparture;
                    late = wait > latenessThreshold ? $"  ⚠ waiting +{wait:0.0}m" : "  (at terminal)";
                }
                else
                {
                    late = Mathf.Abs(slot.latenessMinutes) > latenessThreshold ? $"  ⚠ live {slot.latenessMinutes:+0.0;-0.0}m" : "  ✓ on time";
                }

                r.AppendLine($"  │  [{slot.state,-12}] {MinutesToTimeString(slot.scheduledDeparture)} {slot.DirectionLabel}{(string.IsNullOrEmpty(slot.variantLetter) ? "" : " [" + slot.variantLetter + "]")} [{who}]{late}{express}{stTag}  lap {laps}");
            }
        }
        r.AppendLine("  └──────────────────────────────────────────────────────");
        Debug.LogWarning(r.ToString());
    }

    public void PrintDetailedRouteLog(string targetRoute)
    {
        float now = SimClock.Instance.AbsoluteGameMinutes;
        var lines = new List<string> { $"╔══════ MANIFEST: ROUTE {targetRoute.ToUpper()}  ({GameTimeString}) ══════╗" };
        bool found = false;

        foreach (var slot in _allSlots.Where(s => s.routeNumber.Equals(targetRoute, StringComparison.OrdinalIgnoreCase)))
        {
            found = true;
            string sched = MinutesToTimeString(slot.scheduledDeparture);
            string who = IsPlayer(slot.assignedBusID) ? "PLAYER" : (slot.assignedBusID < 0 ? "unassigned" : $"Bus#{slot.assignedBusID}");
            string ctx = slot.scheduledDeparture < now ? "<color=#aaaaaa>[PAST]</color>" : "<color=#66ff99>[PLAN]</color>";
            string stTag = "";

            string late;
            if (slot.actualDeparture >= 0f)
            {
                float l = slot.latenessMinutes;
                late = Mathf.Abs(l) >= latenessThreshold ? (l > 0 ? $" ⚠ +{l:0.0}m LATE" : $" ✓ -{Mathf.Abs(l):0.0}m EARLY") : " ✓ ON TIME";
            }
            else if (slot.state == SlotState.Completed) late = " (completed)";
            else if (slot.state == SlotState.Unassigned && slot.scheduledDeparture < now) late = " ✗ NO BUS";
            else late = " (pending)";

            int lapInfo = slot.assignedBusID > 0 ? slot.chainLegIndex : -1;
            string lapTag = lapInfo >= 0 ? $" [lap {lapInfo}]" : "";

            lines.Add($"  │ {ctx} Day{slot.dayNumber} {sched} {slot.DirectionLabel}{(string.IsNullOrEmpty(slot.variantLetter) ? "" : " [" + slot.variantLetter + "]")}{stTag} [{who,-10}] [{slot.state}]{late}{lapTag}");
        }

        if (!found) lines.Add($"  │ ✗ Route '{targetRoute}' not found.");
        lines.Add("  └──────────────────────────────────────────────────────");

        int chunkSize = 90;
        for (int i = 0; i < Mathf.CeilToInt((float)lines.Count / chunkSize); i++)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"<b>[ROUTE {targetRoute.ToUpper()} – {i + 1}]</b>");
            int lo = i * chunkSize, hi = Mathf.Min(lo + chunkSize, lines.Count);
            for (int j = lo; j < hi; j++) sb.AppendLine(lines[j]);
            Debug.LogWarning(sb.ToString());
        }
    }
    public void PrintAllocationConflictReport(int dayNumber)
    {
        var lines = new List<string> { $"╔══════ ALLOCATION CONFLICT CHECK — Day {dayNumber} ══════╗" };
        bool anyIssue = false;
        var byBus = _allSlots
            .Where(s => s.dayNumber == dayNumber && s.assignedBusID >= 0 && !IsPlayer(s.assignedBusID))
            .GroupBy(s => s.assignedBusID);

        foreach (var g in byBus)
        {
            var ordered = g.OrderBy(s => s.scheduledDeparture).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                for (int j = i + 1; j < ordered.Count; j++)
                {
                    var a = ordered[i]; var b = ordered[j];
                    if (a.routeNumber == b.routeNumber) continue; // same-route back-to-back is fine
                    float aEnd = a.scheduledDeparture + TripMinutesForSlot(a);
                    if (b.scheduledDeparture >= aEnd) break; // sorted — no further overlap possible for this a
                    anyIssue = true;
                    lines.Add($"  │ ✗ Bus#{g.Key} DOUBLE-BOOKED: Route {a.FullRouteLabel} " +
                              $"{MinutesToTimeString(a.scheduledDeparture)}-{MinutesToTimeString(aEnd)} " +
                              $"overlaps Route {b.FullRouteLabel} {MinutesToTimeString(b.scheduledDeparture)}");
                }
            }
        }
        var starved = _allSlots
            .Where(s => s.dayNumber == dayNumber && s.state == SlotState.Unassigned)
            .GroupBy(s => s.routeNumber);

        foreach (var g in starved)
        {
            anyIssue = true;
            lines.Add($"  │ ⚠ Route {g.Key}: {g.Count()} slot(s) unassigned — pool likely drained by a route processed earlier this day.");
        }

        if (!anyIssue) lines.Add("  │ ✓ No conflicts, no starved routes.");
        lines.Add("  └──────────────────────────────────────────────────────");
        Debug.LogWarning(string.Join("\n", lines));
    }
    public void PrintFullDaySchedule(int dayNumber = -1)
    {
        if (dayNumber < 0) dayNumber = SimClock.Instance.GameDayNumber;

        foreach (var route in managedRoutes)
        {
            if (route == null) continue;

            var daySlots = _allSlots
                .Where(s => s.routeNumber == route.routeNumber && s.dayNumber == dayNumber)
                .OrderBy(s => s.scheduledDeparture)
                .ToList();

            var lines = new List<string>
            {
                $"╔══════ FULL SCHEDULE: ROUTE {route.routeNumber}  (Day {dayNumber}) ══════╗"
            };

            if (daySlots.Count == 0)
            {
                lines.Add("  │ (no slots generated for this day)");
            }
            else
            {
                foreach (var slot in daySlots)
                {
                    string sched = MinutesToTimeString(slot.scheduledDeparture);
                    string who = IsPlayer(slot.assignedBusID) ? "PLAYER"
                                 : (slot.assignedBusID < 0 ? "unassigned" : $"Bus#{slot.assignedBusID}");
                    string stTag = "";
                    lines.Add($"  │ {sched}  {slot.DirectionLabel}{(string.IsNullOrEmpty(slot.variantLetter) ? "" : " [" + slot.variantLetter + "]")}{stTag}  [{who,-10}]  [{slot.state}]");
                }
            }
            lines.Add("  └──────────────────────────────────────────────────────");

            int chunkSize = 100;
            int chunkCount = Mathf.CeilToInt((float)lines.Count / chunkSize);
            for (int i = 0; i < chunkCount; i++)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"<b>[ROUTE {route.routeNumber} — Day {dayNumber} — part {i + 1}/{chunkCount}]</b>");
                int lo = i * chunkSize, hi = Mathf.Min(lo + chunkSize, lines.Count);
                for (int j = lo; j < hi; j++) sb.AppendLine(lines[j]);
                Debug.LogWarning(sb.ToString());
            }
        }
    }
}