using UnityEngine;
using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusTrackerService — v8.10
//
//  BUGS FIXED vs v8.8
//  ──────────────────
//  [TRACK-7]  Pipeline slots (state=AssignedNPC, in _busItineraries, NOT in
//             _slotByBus) now show correctly as future scheduled arrivals.
//             Previously, HandleSlotDue was firing OnAssignedDeparture for them
//             and promoting them to InService prematurely, causing Section 3 to
//             skip them (isSlotLiveInService guard) and Section 2 to miss them
//             (bus physically absent on that route).  Scheduler v8 [S8-3] keeps
//             pipeline slots in AssignedNPC state; the tracker now sees them and
//             shows them with the correct fleet number and timetable ETA.
//
//  [TRACK-8]  Fleet number for pipeline slots was inconsistently resolved.
//             Section 3 now uses a helper ResolveFleetLabel(slot) that looks up
//             BusRegistry.ActiveBuses[slot.assignedBusID] first (gives fleet
//             number), then falls back to slot.assignedBusID.  Both sections
//             now produce labels in the same "Bus #FLEET" format.
//
//  [TRACK-9]  Dedup between a live bus (Section 2) and its own pipeline future
//             trips (Section 3) was fragile.  liveNPCLabels key is now
//             busID-based (not label-based) so it works regardless of how the
//             fleet number is formatted.  A pipeline slot whose scheduled
//             departure matches the CURRENT live trip is still suppressed;
//             future trips (different scheduledDeparture) correctly pass through.
//
//  [TRACK-10] Section 3 was including pre-InService slots for buses still at a
//             terminal waiting to depart (state=AssignedNPC, in _slotByBus).
//             Those are now detected via the new IsActiveSlot() helper and
//             handled the same as InService: Section 2 will pick them up.
//
//  [TRACK-11] Section 3 used to hard-skip SlotState.AssignedPlayer entirely
//             (comment: "Skips: ... AssignedPlayer (Section 1)") — referencing
//             a player pre-departure handling path that got merged away when
//             Section 1/2 were combined, without anything replacing it. Net
//             effect: from the moment a player reserved a slot until they
//             actually typed `depart` (DeadrunToStart / WaitingToDepart /
//             ArrivedAtTerminal — all still SlotState.AssignedPlayer under the
//             hood), the player was invisible to BOTH Section 2 (requires
//             InService) AND Section 3 (explicitly skipped AssignedPlayer).
//             AssignedPlayer is now treated the same as AssignedNPC here —
//             shown as a scheduled arrival via timetable ETA, labeled through
//             the same ResolveFleetLabel path (which already has a player
//             branch). The only thing still excluded is InService (that's
//             Section 2's job) and Completed.
//
//  All v8.8 fixes ([TRACK-1] through [TRACK-6]) are preserved.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusTrackerService : MonoBehaviour
{
    public static BusTrackerService Instance;

    [Header("Settings")]
    public float maxLookaheadMinutes      = 120f;
    [Tooltip("A live bus (player or NPC) further than this many metres from its route's stop-to-stop line is dropped from the arrivals list -- it's off route (detour, wrong turn, dead run), so a timetable-based ETA would be meaningless. 0 = never hide.")]
    public float offRouteHideMeters       = 250f;
    public int   maxResults               = 3;
    public float minsPerStop              = 2f;
    public float arrivingThresholdMinutes = 1f;
    [Tooltip("A live bus within this many metres of its trip's first stop is treated as not yet departed: its ETA is the timetable plus how late it is, and its controller's stale next-stop index is ignored. 0 = off.")]
    public float originRadiusMeters = 120f;

    [Header("ETA Smoothing")]
    [Range(0f, 1f)] public float etaSmoothWeight         = 0.22f;
    public float                  etaJumpToleranceMinutes = 2.5f;

    [Header("Direction Cull")]
    [Range(-0.5f, 0.5f)] public float directionDotThreshold = -0.1f;

    [Header("Stop Mode (all-routes-at-a-stop)")]
    [Tooltip("Max arrivals shown per direction when MULTIPLE compass directions have service at this stop (typically 2 - north+south or east+west).")]
    public int maxResultsPerDirectionWhenMultiple = 3;
    [Tooltip("Max arrivals shown when only ONE compass direction has service at this stop — gets the full budget instead of being capped down to match a direction that isn't there.")]
    public int maxResultsWhenSingleDirection = 6;
    // Legacy field, still used by the older outbound/inbound-only GetArrivalsForStop API.
    public int maxResultsPerStopDirection = 6;

    [Header("Serving-Routes Index")]
    [Tooltip("Safety-net full rebuild interval for the stop->serving-routes index (seconds). BusRouteData.OnValidate also triggers an immediate rebuild on any hand-edit, so this timer mainly covers routes/stops changing through paths that don't go through the Inspector.")]
    public float stopIndexRebuildInterval = 5f;
    private float _stopIndexTimer = 0f;

    // ── Caches ────────────────────────────────────────────────────────────────
    private readonly Dictionary<string, float> _etaCache = new();

    // ── Working buffers ───────────────────────────────────────────────────────
    private readonly List<int> _fleetOrder = new(16);

    private void Awake()
    {
        if (Instance != null && Instance != this)
            Debug.LogWarning($"[BusTrackerService] Duplicate instance detected on '{gameObject.name}' — " +
                              $"the existing Instance is on '{Instance.gameObject.name}'. Only one should exist; " +
                              "if the wrong one wins, EVERY stop will show 'No scheduled service' regardless of data.");
        Instance = this;
    }

    private void Start()
    {
        StartCoroutine(DelayedInitialIndexBuild());
    }

    // CityManager/BusScheduler resolve their own data in their own Start()
    // calls, which may run before or after this one depending on script
    // execution order — wait a frame so managedRoutes/AllStops are guaranteed
    // populated before the first index build.
    private System.Collections.IEnumerator DelayedInitialIndexBuild()
    {
        yield return null;
        RebuildStopServiceIndex();
    }

    private void Update()
    {
        _stopIndexTimer -= Time.deltaTime;
        if (_stopIndexTimer <= 0f)
        {
            _stopIndexTimer = stopIndexRebuildInterval;
            RebuildStopServiceIndex();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  NESTED RESULT TYPES — Stop Mode
    // ═════════════════════════════════════════════════════════════════════════

public class StopArrivalEntry
{
    public string routeNumber;
    public Color  routeColor;
    public string variantLetter;
    public string busLabel;
    public float  minutesAway;
    public string minutesLabel;
    public int    waitingPax;   // QoL — riders currently waiting at this stop
    public bool   isBunched;    // QoL — arriving suspiciously close behind another bus
}

    public class StopDirectionArrivals
    {
        public bool hasService;
        public List<StopArrivalEntry> arrivals = new();
        /// <summary>Routes that normally serve this stop in this direction but are not right now because a road event closes it.</summary>
        public List<string> closedRoutes = new();
    }

    // ── Compass-direction grouping (reverse-lookup stop mode) ─────────────────
    // A route's own "outbound"/"inbound" labeling is internal and NOT
    // consistent direction-to-direction across routes — one route's outbound
    // might run north, another's might run south. A rider standing at a stop
    // cares which physical way the bus is actually going, not which internal
    // leg it is on that route. So instead of bucketing by outbound/inbound,
    // every (route, direction) pair gets classified by real compass heading
    // from its own path geometry, and all routes/legs heading the same
    // physical way are merged into one bucket.
    public enum CompassDirection { North, South, East, West }

    public class CompassDirectionArrivals
    {
        public CompassDirection direction;
        public string label;
        public List<StopArrivalEntry> arrivals = new();
    }

    private readonly Dictionary<(string route, bool outbound, string stop), CompassDirection> _compassCache = new();

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC API — single route + direction
    // ═════════════════════════════════════════════════════════════════════════
    // ── Road-event closures ────────────────────────────────────────────────────
    /// <summary>True if an active road event closes this stop for the route + direction (buses are on the detour).</summary>
    public bool IsStopClosed(BusRouteData route, bool outbound, string stopCode)
    {
        var reg = RoadEventRegistry.Instance;
        return reg != null && route != null && reg.IsStopClosed(stopCode, route.routeNumber, outbound);
    }

    /// <summary>Route numbers whose service at this stop is currently suspended by a road event (both directions, de-duplicated).</summary>
    public List<string> GetClosedRoutesAtStop(string stopCode)
    {
        var result = new List<string>();
        var stopData = CityManager.Instance != null ? CityManager.Instance.GetStop(stopCode) : null;
        if (stopData == null) return result;
        foreach (var sr in stopData.servingRoutes)
            if (sr.route != null && IsStopClosed(sr.route, sr.outbound, stopCode) && !result.Contains(sr.route.routeNumber))
                result.Add(sr.route.routeNumber);
        return result;
    }

    public List<string> GetNextArrivals(BusRouteData route, bool outbound, string stopCode)
    {
        float now = BusScheduler.Instance.GameTimeMinutes;

        if (IsStopClosed(route, outbound, stopCode))
            return new List<string> { "Stop closed — detour" };

        if (!RouteServesStop(route, outbound, stopCode))
            return new List<string> { "Stop Not Found" };

        var sorted = ComputeArrivalsRaw(route, outbound, stopCode);

        var result = new List<string>(maxResults);
        for (int i = 0; i < sorted.Count && result.Count < maxResults; i++)
        {
            float minsAway = sorted[i].arrivalTime - now;
            if (minsAway < 0f || minsAway > maxLookaheadMinutes) continue;

            string mins    = minsAway < arrivingThresholdMinutes ? "<1 min" : $"{Mathf.CeilToInt(minsAway)} min";
            string vSuffix = string.IsNullOrEmpty(sorted[i].variant) ? "" : $"[{sorted[i].variant}] ";
            string busLabel = sorted[i].label.StartsWith("TBD-") ? vSuffix + "TBD"
                                                                   : vSuffix + sorted[i].label.Replace("Bus #", "");
            result.Add($"{mins} — {busLabel}");
        }

        PruneArrivedEntries(now);
        return result;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC API — stop mode, all routes, both directions  [TRACK-6]
    // ═════════════════════════════════════════════════════════════════════════
    public (StopDirectionArrivals outbound, StopDirectionArrivals inbound) GetArrivalsForStop(string stopCode)
{
    var outboundResult = new StopDirectionArrivals();
    var inboundResult  = new StopDirectionArrivals();

    var scheduler = BusScheduler.Instance;
    var city      = CityManager.Instance;
    if (scheduler == null || city == null || string.IsNullOrEmpty(stopCode))
        return (outboundResult, inboundResult);

    var stopData = city.GetStop(stopCode);
    if (stopData == null) return (outboundResult, inboundResult);

    float now = scheduler.GameTimeMinutes;

    var allOutbound = new List<(float arrivalTime, string label, string variant, BusRouteData route)>();
    var allInbound  = new List<(float arrivalTime, string label, string variant, BusRouteData route)>();

    // Read directly off the stop's own registry — populated proactively by
    // RebuildStopServiceIndex — instead of scanning every managed route.
    foreach (var sr in stopData.servingRoutes)
    {
        if (sr.route == null) continue;

        if (IsStopClosed(sr.route, sr.outbound, stopCode))
        {
            (sr.outbound ? outboundResult : inboundResult).hasService = true;
            (sr.outbound ? outboundResult : inboundResult).closedRoutes.Add(sr.route.routeNumber);
            continue;
        }

        if (sr.outbound)
        {
            outboundResult.hasService = true;
            foreach (var e in ComputeArrivalsRaw(sr.route, true, stopCode))
                allOutbound.Add((e.arrivalTime, e.label, e.variant, sr.route));
        }
        else
        {
            inboundResult.hasService = true;
            foreach (var e in ComputeArrivalsRaw(sr.route, false, stopCode))
                allInbound.Add((e.arrivalTime, e.label, e.variant, sr.route));
        }
    }

    allOutbound.Sort((a, b) => a.arrivalTime.CompareTo(b.arrivalTime));
    allInbound.Sort((a, b)  => a.arrivalTime.CompareTo(b.arrivalTime));

    FillStopDirectionResult(outboundResult, allOutbound, now, stopCode);
    FillStopDirectionResult(inboundResult,  allInbound,  now, stopCode);

    PruneArrivedEntries(now);
    return (outboundResult, inboundResult);
}

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC API — stop mode, grouped by REAL compass direction, not
    //  outbound/inbound. This is what a rider standing at a stop actually
    //  wants: "buses heading north" / "buses heading south", regardless of
    //  which route calls that leg its outbound or inbound.
    // ═════════════════════════════════════════════════════════════════════════
    public List<CompassDirectionArrivals> GetArrivalsForStopByCompass(string stopCode)
    {
        var result = new List<CompassDirectionArrivals>();

        var scheduler = BusScheduler.Instance;
        var city      = CityManager.Instance;
        if (scheduler == null || city == null || string.IsNullOrEmpty(stopCode))
            return result;

        var stopData = city.GetStop(stopCode);
        if (stopData == null) return result;

        float now = scheduler.GameTimeMinutes;

        var buckets = new Dictionary<CompassDirection, List<(float arrivalTime, string label, string variant, BusRouteData route)>>
        {
            { CompassDirection.North, new() },
            { CompassDirection.South, new() },
            { CompassDirection.East,  new() },
            { CompassDirection.West,  new() },
        };

        // Read directly off the stop's own registry — populated proactively
        // by RebuildStopServiceIndex — instead of scanning every managed
        // route and re-checking membership each time.
        foreach (var sr in stopData.servingRoutes)
        {
            if (sr.route == null) continue;

            if (IsStopClosed(sr.route, sr.outbound, stopCode)) continue;   // road event: not served right now

            var dir = ComputeCompassDirection(sr.route, sr.outbound, stopCode);
            foreach (var e in ComputeArrivalsRaw(sr.route, sr.outbound, stopCode))
                buckets[dir].Add((e.arrivalTime, e.label, e.variant, sr.route));
        }

        // How many compass directions actually have anything in them decides
        // the per-direction budget: split 3/3 across the usual 2 (north+south
        // or east+west), but if this stop genuinely only has ONE direction of
        // service, that direction gets the full 6 instead of being capped
        // down to match a second direction that doesn't exist here.
        int activeDirCount = 0;
        foreach (var dir in new[] { CompassDirection.North, CompassDirection.South, CompassDirection.East, CompassDirection.West })
            if (buckets[dir].Count > 0) activeDirCount++;
        int perDirectionCap = activeDirCount <= 1 ? maxResultsWhenSingleDirection : maxResultsPerDirectionWhenMultiple;

        foreach (var dir in new[] { CompassDirection.North, CompassDirection.South, CompassDirection.East, CompassDirection.West })
        {
            var raw = buckets[dir];
            if (raw.Count == 0) continue;
            raw.Sort((a, b) => a.arrivalTime.CompareTo(b.arrivalTime));

            var group = new CompassDirectionArrivals { direction = dir, label = CompassLabel(dir) };
            FillCompassDirectionResult(group, raw, now, stopCode, perDirectionCap);
            if (group.arrivals.Count > 0) result.Add(group);
        }

        PruneArrivedEntries(now);
        return result;
    }

    private void FillCompassDirectionResult(
        CompassDirectionArrivals target,
        List<(float arrivalTime, string label, string variant, BusRouteData route)> raw,
        float now, string stopCode, int perDirectionCap)
    {
        int waitingHere = PaxSimManager.Instance != null ? PaxSimManager.Instance.CountWaiting(stopCode) : -1;
        float lastMinsAway = float.MinValue;

        for (int i = 0; i < raw.Count && target.arrivals.Count < perDirectionCap; i++)
        {
            float minsAway = raw[i].arrivalTime - now;
            if (minsAway < 0f || minsAway > maxLookaheadMinutes) continue;

            string busLabel = raw[i].label.StartsWith("TBD-") ? "TBD"
                                                                : raw[i].label.Replace("Bus #", "");

            bool bunched = lastMinsAway > float.MinValue && (minsAway - lastMinsAway) < 2f;
            lastMinsAway = minsAway;

            target.arrivals.Add(new StopArrivalEntry
            {
                routeNumber   = raw[i].route.routeNumber,
                routeColor    = raw[i].route.routeColor,
                variantLetter = raw[i].variant,
                busLabel      = busLabel,
                minutesAway   = minsAway,
                minutesLabel  = minsAway < arrivingThresholdMinutes ? "<1 min" : $"{Mathf.CeilToInt(minsAway)} min",
                waitingPax    = waitingHere,
                isBunched     = bunched,
            });
        }
    }

    /// <summary>Classifies a (route, direction) pair's real-world compass
    /// heading AT the given stop, from actual STOP-TO-STOP geometry — not
    /// raw path nodes, and not the route's own outbound/inbound labeling
    /// (which is internal and not consistent direction-to-direction: one
    /// route's "outbound" might run north, another's might run south past
    /// the very same stop).
    ///
    /// [FIX v3] Sampling raw path nodes (even with multiple distances/votes)
    /// was still wrong on some legs — not just noisy, but reading the wrong
    /// AXIS entirely (a north/south road reading WEST). Root cause: a
    /// route's node list isn't guaranteed to be a simple, non-crossing path.
    /// Parallel one-way street pairs, dogleg stop approaches, and loops that
    /// pass close to themselves mean "nearest node by raw distance" can
    /// land on a completely different pass of the path than the one that
    /// actually serves this stop — giving a direction that's perpendicular
    /// or backwards, not just imprecise.
    ///
    /// Fix: use the direction between this stop and the NEXT stop the route
    /// actually serves in its own ordered stop sequence (or the previous
    /// stop, if this is the last one) — real named stops, not path samples.
    /// Two consecutive stops in a route's own sequence can't have the
    /// "which pass of the path" ambiguity a raw node list can.</summary>
    private CompassDirection ComputeCompassDirection(BusRouteData route, bool outbound, string stopCode)
    {
        var key = (route.routeNumber, outbound, stopCode);
        if (_compassCache.TryGetValue(key, out var cached)) return cached;

        CompassDirection result = CompassDirection.North; // sane fallback if data is missing
        var city = CityManager.Instance;

        if (city != null)
        {
            // [FIX] This used to always pass variant=null, i.e. only ever look at the
            // MAINLINE stop sequence. A stop that exists ONLY on a lettered variant's
            // override list (never on route.outboundStops/inboundStops) was never found
            // (idx stayed -1), so its direction silently fell back to North and got
            // cached that way forever — wrong bucket, or missing from the direction a
            // rider actually needed. Now resolves whichever list (mainline or the
            // specific variant) the stop is really served on before looking up neighbors.
            string servingVariant = FindServingVariantLetter(route, outbound, stopCode);
            var codes = GetStopCodesFresh(route, outbound, servingVariant);
            int idx = codes.IndexOf(stopCode);

            if (idx >= 0 && codes.Count >= 2)
            {
                bool   otherIsNext = idx < codes.Count - 1;
                string otherCode   = otherIsNext ? codes[idx + 1] : codes[idx - 1];

                var thisStop  = city.GetStop(stopCode);
                var otherStop = city.GetStop(otherCode);

                if (thisStop != null && otherStop != null)
                {
                    Vector3 delta = otherIsNext
                        ? otherStop.GetWorldPosition() - thisStop.GetWorldPosition()
                        : thisStop.GetWorldPosition() - otherStop.GetWorldPosition();

                    if (delta.sqrMagnitude > 0.0001f)
                    {
                        result = Mathf.Abs(delta.z) >= Mathf.Abs(delta.x)
                            ? (delta.z >= 0f ? CompassDirection.North : CompassDirection.South)
                            : (delta.x >= 0f ? CompassDirection.East  : CompassDirection.West);
                    }
                }
            }
        }

        _compassCache[key] = result;
        return result;
    }

    private static string CompassLabel(CompassDirection d) => d switch
    {
        CompassDirection.North => "NORTH",
        CompassDirection.South => "SOUTH",
        CompassDirection.East  => "EAST",
        _                       => "WEST",
    };

private void FillStopDirectionResult(
    StopDirectionArrivals target,
    List<(float arrivalTime, string label, string variant, BusRouteData route)> raw,
    float now, string stopCode)
{
    int waitingHere = PaxSimManager.Instance != null ? PaxSimManager.Instance.CountWaiting(stopCode) : -1;
    float lastMinsAway = float.MinValue;

    for (int i = 0; i < raw.Count && target.arrivals.Count < maxResultsPerStopDirection; i++)
    {
        float minsAway = raw[i].arrivalTime - now;
        if (minsAway < 0f || minsAway > maxLookaheadMinutes) continue;

        string busLabel = raw[i].label.StartsWith("TBD-") ? "TBD"
                                                            : raw[i].label.Replace("Bus #", "");

        bool bunched = lastMinsAway > float.MinValue && (minsAway - lastMinsAway) < 2f;
        lastMinsAway = minsAway;

        target.arrivals.Add(new StopArrivalEntry
        {
            routeNumber   = raw[i].route.routeNumber,
            routeColor    = raw[i].route.routeColor,
            variantLetter = raw[i].variant,
            busLabel      = busLabel,
            minutesAway   = minsAway,
            minutesLabel  = minsAway < arrivingThresholdMinutes ? "<1 min" : $"{Mathf.CeilToInt(minsAway)} min",
            waitingPax    = waitingHere,
            isBunched     = bunched,
        });
    }
}
// ═════════════════════════════════════════════════════════════════════════
//  QoL — ROUTE FREQUENCY
// ═════════════════════════════════════════════════════════════════════════
public float GetRouteFrequencyMinutes(BusRouteData route, bool outbound)
{
    if (route == null) return -1f;
    return outbound ? route.headwayFromAMinutes : route.headwayFromZMinutes;
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — ROUTE SUMMARY
// ═════════════════════════════════════════════════════════════════════════
public struct RouteSummary
{
    public int   liveBuses;
    public int   cap;
    public float onTimePercent;
    public float frequencyOutMinutes;
    public float frequencyInMinutes;
}

public RouteSummary GetRouteSummary(string routeNumber)
{
    var scheduler = BusScheduler.Instance;
    var summary = new RouteSummary();
    if (scheduler == null) return summary;

    var route = scheduler.GetRouteData(routeNumber);
    summary.liveBuses = scheduler.CountActiveBusesOnRoute(routeNumber);
    summary.cap       = scheduler.GetRouteCap(routeNumber);

    int total = 0, onTime = 0;
    foreach (var slot in scheduler.AllSlots)
    {
        if (slot.routeNumber != routeNumber) continue;
        if (slot.state != SlotState.InService && slot.state != SlotState.Completed) continue;
        if (slot.actualDeparture < 0f) continue;
        total++;
        if (Mathf.Abs(slot.latenessMinutes) <= 2f) onTime++;
    }
    summary.onTimePercent = total == 0 ? -1f : (onTime / (float)total) * 100f;

    if (route != null)
    {
        summary.frequencyOutMinutes = route.headwayFromAMinutes;
        summary.frequencyInMinutes  = route.headwayFromZMinutes;
    }
    return summary;
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — MISSED TRIP DETECTION
// ═════════════════════════════════════════════════════════════════════════
public List<TimetableSlot> GetMissedTrips(string routeNumber, float graceMinutes = 5f)
{
    var scheduler = BusScheduler.Instance;
    var result = new List<TimetableSlot>();
    if (scheduler == null) return result;

    float now = scheduler.GameTimeMinutes;
    foreach (var slot in scheduler.AllSlots)
    {
        if (routeNumber != null && slot.routeNumber != routeNumber) continue;
        if (slot.state != SlotState.Unassigned) continue;
        if (now - slot.scheduledDeparture > graceMinutes)
            result.Add(slot);
    }
    return result;
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — ROLLING RELIABILITY
// ═════════════════════════════════════════════════════════════════════════
public float GetRouteRollingAvgLateness(string routeNumber, int sampleSize = 12)
{
    var scheduler = BusScheduler.Instance;
    if (scheduler == null) return -1f;

    var completed = new List<TimetableSlot>();
    foreach (var slot in scheduler.AllSlots)
        if (slot.routeNumber == routeNumber && slot.state == SlotState.Completed && slot.actualDeparture >= 0f)
            completed.Add(slot);

    if (completed.Count == 0) return -1f;
    completed.Sort((a, b) => b.scheduledDeparture.CompareTo(a.scheduledDeparture));

    int take = Mathf.Min(sampleSize, completed.Count);
    float sum = 0f;
    for (int i = 0; i < take; i++) sum += Mathf.Abs(completed[i].latenessMinutes);
    return sum / take;
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — LAST TRIP / NEXT-DAY ROLLOVER
// ═════════════════════════════════════════════════════════════════════════
public bool IsLastTripOfDay(TimetableSlot slot)
{
    var scheduler = BusScheduler.Instance;
    if (scheduler == null || slot == null) return false;

    foreach (var s in scheduler.AllSlots)
    {
        if (s == slot) continue;
        if (s.routeNumber != slot.routeNumber || s.isOutbound != slot.isOutbound) continue;
        if (s.dayNumber != slot.dayNumber) continue;
        if (s.scheduledDeparture > slot.scheduledDeparture) return false;
    }
    return true;
}

public float GetMinutesUntilNextDayFirstTrip(string routeNumber, bool outbound)
{
    var scheduler = BusScheduler.Instance;
    if (scheduler == null) return -1f;

    float now = scheduler.GameTimeMinutes;
    float best = float.MaxValue;
    foreach (var s in scheduler.AllSlots)
    {
        if (s.routeNumber != routeNumber || s.isOutbound != outbound) continue;
        if (s.scheduledDeparture <= now) continue;
        if (s.scheduledDeparture < best) best = s.scheduledDeparture;
    }
    return best == float.MaxValue ? -1f : best - now;
}
    // ═════════════════════════════════════════════════════════════════════════
    //  GetResolvedStopsForVariant  (used by NPCBusController)
    // ═════════════════════════════════════════════════════════════════════════
    public List<BusStopData> GetResolvedStopsForVariant(
        BusRouteData route, bool outbound, string variantLetter)
    {
        var bindings = GetStopBindingsForVariant(route, outbound, variantLetter);
        var result   = new List<BusStopData>(bindings.Count);

        if (CityManager.Instance == null)
        {
            Debug.LogWarning("[BusTrackerService] CityManager not available.");
            return result;
        }

        foreach (var binding in bindings)
        {
            if (string.IsNullOrEmpty(binding.stopCode)) continue;
            var stopData = CityManager.Instance.GetStop(binding.stopCode);
            if (stopData != null)
                result.Add(stopData);
            else
                Debug.LogWarning($"[BusTrackerService] Stop '{binding.stopCode}' not found in CityManager. " +
                                 $"Route={route.routeNumber} variant='{variantLetter}' outbound={outbound}");
        }
        return result;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  [07-30] OWN-BUS ETA — for a bus's OWN interior board ("how long until
    //  MY bus reaches this stop"), as opposed to everything above this line
    //  which answers "who's arriving at THIS stop" for riders waiting there.
    //  Reuses the exact same hybrid GPS+scheduler math (CalculateHybridLiveEta)
    //  so the two boards can never disagree with each other.
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Minutes until this bus reaches the stop at stopIndex along its own
    /// resolved stop sequence (same index space as GetResolvedStopsForVariant /
    /// NPCBusController._stopSequence / PlayerHandoff.GetActiveStops). Returns
    /// false (and 0) if the route/variant can't be resolved to stop codes.
    ///
    /// [07-30 REWRITE #1] Dropped the CalculateHybridLiveEta blend that made
    /// the number leap instead of counting down.
    ///
    /// [07-30 REWRITE #2] Rewrite #1 still had a real bug: it worked out each
    /// stop's scheduled time as `route.oneWayTripMinutes * (stopIndex /
    /// totalStops)` -- i.e. it assumed every stop is spaced an EQUAL amount
    /// of time apart across the trip. Real stops aren't evenly spaced (a
    /// terminal or a cluster of close-together stops can sit right after each
    /// other with almost no scheduled gap between them), so two adjacent
    /// stops could land on nearly identical scheduled times and both read
    /// "Due" simultaneously -- exactly the "two DUEs" bug. This version uses
    /// each stop's REAL scheduled offset (RouteStopBinding.minutesFromStart),
    /// the same authoritative per-stop timing the scheduled-slot path in
    /// ComputeArrivalsRaw already uses for non-live arrivals -- no more
    /// uniform-spacing guess anywhere in this method.
    /// </summary>
    // [FIX 07-31] This whole custom reimplementation is gone. Every version
    // of it (the schedule-anchored rewrite, the degenerate-collapse fix, the
    // jitter-smoothing fix) was chasing bugs one at a time in a parallel
    // system, when the exterior stop-side tracker already has a proven,
    // battle-tested version of exactly this computation two feet away in
    // this same file: CalculateHybridLiveEta + SmoothedEta, the same pipeline
    // that produces the tracker's own "<1 min" / "2 min" / "55 min" numbers.
    // GetOwnBusEtaLabel below now calls that SAME pipeline directly, with the
    // SAME ResolveFleetLabel-based cache key SmoothedEta already uses for the
    // rider-side view of this exact bus+stop -- meaning the interior board
    // and the exterior tracker aren't just using similar logic, they're
    // reading and writing the SAME cached value. They can't drift apart
    // because there's only one number, not two independently-computed ones
    // that happen to agree.

    /// <summary>
    /// Own-bus ETA to stopIndex along the bus's own resolved stop sequence,
    /// formatted exactly like the exterior tracker: "&lt;1 min" once inside
    /// arrivingThresholdMinutes, otherwise "N min" (ceiling, never floor/round
    /// -- same convention as ComputeArrivalsRaw, so a bus 1.2 minutes out
    /// reads "2 min" here exactly like it would on the tracker, not "1 min").
    /// </summary>
    /// <summary>Public accessor for a single stop's real scheduled offset
    /// (minutesFromStart) from its timetable binding -- for callers that want
    /// a pure schedule-based ETA (scheduledDeparture + this - now) rather
    /// than the kinematic distance/speed pipeline, e.g. a bus that hasn't
    /// actually departed yet, where "distance to stop" isn't a meaningful
    /// question regardless of how far away it is on the map.</summary>
    public bool TryGetScheduledOffsetMinutes(BusRouteData route, bool outbound, string variantLetter,
                                              int stopIndex, out float minutesFromStart)
    {
        minutesFromStart = 0f;
        if (route == null || stopIndex < 0) return false;
        var bindings = GetStopBindingsForVariant(route, outbound, variantLetter);
        if (bindings == null || stopIndex >= bindings.Count) return false;
        minutesFromStart = bindings[stopIndex].minutesFromStart;
        return true;
    }

    public string GetOwnBusEtaLabel(int busID, BusRouteData route, bool outbound, string variantLetter,
                                     Vector3 busPos, float scheduledDeparture, int stopIndex)
    {
        if (route == null || stopIndex < 0) return "--";

        var stopCodes = GetStopCodesFresh(route, outbound, variantLetter);
        if (stopCodes == null || stopIndex >= stopCodes.Count) return "--";

        float now = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeMinutes : 0f;

        float rawArrival = CalculateHybridLiveEta(now, scheduledDeparture, busPos, stopCodes, stopIndex, route, outbound, variantLetter);

        // Same cache key shape SmoothedEta already uses for the rider-side
        // tracker view of this bus+stop -- ResolveFleetLabel(busID) resolves
        // the player correctly too, so a player's own board and the exterior
        // tracker's view of the player's bus share one cached value.
        string label = ResolveFleetLabel(busID);
        float smoothedArrival = SmoothedEta(label, stopCodes[stopIndex], outbound, rawArrival, now);

        float minutesAway = Mathf.Max(0f, smoothedArrival - now);
        return minutesAway < arrivingThresholdMinutes ? "<1 min" : $"{Mathf.CeilToInt(minutesAway)} min";
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  SHARED ARRIVAL COMPUTATION  [TRACK-5]
    // ═════════════════════════════════════════════════════════════════════════

    private bool RouteServesStop(BusRouteData route, bool outbound, string stopCode)
    {
        if (route == null || string.IsNullOrEmpty(stopCode)) return false;

        if (route.GetStopBindings(outbound, null).Exists(b => b.stopCode == stopCode))
            return true;

        if (route.variants != null)
            foreach (var v in route.variants)
            {
                var vBindings = GetBindingsForVariantDirect(route, outbound, v);
                if (vBindings.Exists(b => b.stopCode == stopCode)) return true;
            }
        return false;
    }

    /// <summary>
    /// [TRACK-10] Returns true if this AssignedNPC slot is the bus's currently ACTIVE
    /// slot (in _slotByBus), meaning the bus is waiting at the terminal to depart.
    /// Pipeline slots are also AssignedNPC but are NOT in _slotByBus.
    /// </summary>
    private bool IsActiveSlot(TimetableSlot slot)
    {
        if (slot.assignedBusID < 0) return false;
        return BusScheduler.Instance.TryGetAssignedSlot(slot.assignedBusID, out var active)
               && active == slot;
    }

    /// <summary>
    /// [TRACK-8] Resolves a display label "Bus #FLEET" for any assigned slot,
    /// looking up fleet number from BusRegistry.  Falls back to busID if not found.
    /// </summary>
    private string ResolveFleetLabel(int busID)
{
    if (busID < 0)
    {
        // [FIX 2] The first fix here was still wrong -- "-2 means MY OWN local player" is only
        // true on the machine that's actually hosting. -2 is BusScheduler.PLAYER_BUS_ID's DEFAULT
        // value (what it resolves to on every machine when no BeginPlayerContext is active), so a
        // CLIENT asked to resolve "-2" for a REMOTE (the HOST's own) bus was matching its own
        // PlayerHandoff.Instance by pure coincidence of both being -2, not because it was actually
        // the same bus -- showed the client's OWN fleet number for the host's bus instead.
        // Confirmed exactly by repro: host driving fleet 1651, a client's tracker showed the
        // CLIENT's own fleet (1006) for that same arrival. Compare against THIS machine's actual
        // PlayerBusID instead of hardcoding -2 -- correct in every direction, since PlayerBusID
        // already IS whatever this machine's own sentinel really is (-2 almost always, since
        // BeginPlayerContext is only ever entered transiently inside a host-side RPC handler, not
        // as this process's ambient state).
        if (PlayerHandoff.Instance != null && busID == PlayerHandoff.Instance.PlayerBusID)
            return $"Bus #{PlayerHandoff.Instance.FleetNumber}";

        // Not mine -- resolve via NetworkGameBridge's possession registry regardless of whether
        // the sentinel is -2 (the HOST's own bus, from a client's point of view) or a per-client
        // one (some OTHER client's bus). SentinelToClientId handles both encodings.
        if (NetworkGameBridge.Instance != null)
        {
            ulong clientId = NetworkGameBridge.SentinelToClientId(busID);
            int physicalBusID = NetworkGameBridge.Instance.GetPossessedBusID(clientId);
            var remoteCtrl = physicalBusID >= 0 ? BusManager.Instance?.GetRecord(physicalBusID)?.controller : null;
            if (remoteCtrl != null) return $"Bus #{remoteCtrl.fleetNumber}";
        }
        return null;
    }
    // [FIX] Was BusRegistry.ActiveBuses -- a possessed bus (local OR remote) is deliberately
    // REMOVED from that dictionary the moment NetworkGameBridge registers it, so this lookup
    // failed for any possessed bus and fell all the way through to the last-resort
    // "Bus #{busID}" fallback below, showing the raw internal busID (e.g. 96) instead of the
    // actual fleet number (e.g. 1909). BusManager's own records persist regardless of possession.
    var rec = BusManager.Instance?.GetRecord(busID);
    if (rec?.controller != null)
        return $"Bus #{rec.controller.fleetNumber}";
    return $"Bus #{busID}";
}

    /// <summary>
    /// Core arrival computation for ONE route + direction.
    /// Returns unformatted (arrivalTime, label, variant) tuples sorted by arrivalTime.
    /// Callers apply lookahead filtering, formatting, and result caps.
    ///
    /// Three sources, deduplicated:
    ///   1. Player bus (live physical position)
    ///   2. Live NPC buses (InService, physically moving)
    ///   3. Scheduled slots (AssignedNPC/AssignedPlayer at terminal or in
    ///      pipeline, Unassigned/TBD)
    /// </summary>
    private List<(float arrivalTime, string label, string variant)> ComputeArrivalsRaw(
        BusRouteData route, bool outbound, string stopCode)
    {
        float now      = BusScheduler.Instance.GameTimeMinutes;
        var   allSlots = BusScheduler.Instance.AllSlots;
        var   player   = PlayerHandoff.Instance;
        int   playerBusID = player != null ? player.PlayerBusID : -9999;

        Vector3 targetStopPos = GetWorldPositionFromCode(stopCode);

        BuildFleetOrder(route.routeNumber, outbound);

        // bestByBus: key = (busID, isTBD, scheduledDeparture) — unique per trip
        var bestByBus     = new Dictionary<(int id, bool isTBD, float schedDep), (float arrivalTime, string label, string variant)>();
        // [TRACK-9] Keyed by (busID, scheduledDeparture) — busID-based, not label-based
        var liveNPCTrips  = new HashSet<(int busID, float schedDep)>();
        var playerLiveKey = (id: playerBusID, isTBD: false);
        bool playerIsLive = false;

        void RegisterArrival(int id, bool isTBD, float schedDep,
                             float arrivalTime, string label, string variant,
                             bool isLivePlayer = false)
        {
            // Player live tracking wins over any scheduled slot for the same bus+trip
            if (playerIsLive && id == playerBusID && !isLivePlayer) return;

            var key = (id, isTBD, schedDep);
            if (!bestByBus.ContainsKey(key) || arrivalTime < bestByBus[key].arrivalTime)
                bestByBus[key] = (arrivalTime, label, variant);

            if (isLivePlayer) playerIsLive = true;
        }
// ── LIVE BUSES (InService, physically moving) — player + NPCs, ONE loop ──
        // Player is no longer special-cased: same ETA math, same label resolution
        // (ResolveFleetLabel handles PLAYER_BUS_ID), same liveNPCTrips suppression
        // against Section 3. This replaces the old separate "Section 1 / Section 2".
        var liveBuses = new List<(int id, Transform t, bool isOutbound, string routeNum, string variant, int nextStopIdx)>();

        // [FIX] Local player + every remote possessed bus used to be two separately-written
        // blocks here (one reading PlayerHandoff.Instance directly, one manually scanning
        // NetworkGameBridge.GetPossessedBusIDs() with its own copy of the sentinel-resolution
        // logic) -- exactly the kind of duplicated, slightly-different lookup that kept causing
        // the "-2 ambiguity"/fleet-label bugs to reappear in different places. PlayerRegistry is
        // the one canonical source now (in single-player this is just the local player, same as
        // before -- the list has at most 1 entry, nothing lost).
        foreach (var p in PlayerRegistry.GetAll())
        {
            if (p.IsLocal && player.ShiftState != PlayerHandoff.PlayerShiftState.InService) continue;
            liveBuses.Add((p.Sentinel, p.Transform, p.IsOutbound, p.RouteNumber, p.VariantLetter, p.NextStopIndex));
        }

        foreach (var kv in BusRegistry.ActiveBuses)
        {
            var bus = kv.Value;
            if (bus == null) continue;

            liveBuses.Add((
                kv.Key,
                bus.transform,
                bus.IsOutbound,
                bus.CurrentRoute?.routeNumber,
                bus.variantLetter,
                Mathf.Max(0, bus.NextStopIndex)
            ));
        }

        foreach (var lb in liveBuses)
        {
            if (lb.routeNum != route.routeNumber) continue;
            if (lb.isOutbound != outbound) continue;

            // Direction-dot cull
            Vector3 travelDir = -lb.t.forward;
            Vector3 toStop    = targetStopPos - lb.t.position;
            toStop.y = 0f;
            float dot = toStop.sqrMagnitude > 0.01f
                ? Vector3.Dot(travelDir.normalized, toStop.normalized) : 1f;
            if (dot <= directionDotThreshold) continue;

            // Only track buses physically in service (not waiting at terminal —
            // that's Section 3's job). Works identically for player or NPC busID
            // since TryGetAssignedSlot is keyed the same way for both.
            if (!BusScheduler.Instance.TryGetAssignedSlot(lb.id, out var busSlot)) continue;
            if (busSlot.state != SlotState.InService) continue;

            string variant     = lb.variant;
            float  scheduledDep = busSlot.scheduledDeparture;
            if (!string.IsNullOrEmpty(busSlot.variantLetter) && string.IsNullOrEmpty(variant))
                variant = busSlot.variantLetter;

            var stops       = GetStopCodesFresh(route, outbound, variant);
            int targetIndex = stops.IndexOf(stopCode);
            if (targetIndex < 0) continue;

            // [PRE-DEPARTURE] A bus still sitting at its origin has not started this trip, whatever its
            // controller's next-stop index says. NPCs flip InService at the scheduled departure time but only
            // reset their stop index when they actually pull out, so at a terminal that index is still the PREVIOUS
            // trip's (the last stop) -- which made the bus look like it was nearly finished and showed
            // "2 min" to a terminal that is a whole one-way trip (e.g. 34 min) away.
            bool atOrigin = stops.Count > 0
                && originRadiusMeters > 0f
                && Vector2.Distance(new Vector2(lb.t.position.x, lb.t.position.z),
                                    new Vector2(GetWorldPositionFromCode(stops[0]).x, GetWorldPositionFromCode(stops[0]).z)) <= originRadiusMeters;
            int liveNextIdx = atOrigin ? 0 : lb.nextStopIdx;

            if (liveNextIdx > targetIndex) continue;

            // Off route by a lot -> not a valid arrival for this route any more: drop it from the list.
            if (offRouteHideMeters > 0f && DistanceToStopPolylineXZ(lb.t.position, stops) > offRouteHideMeters) continue;

            string label      = ResolveFleetLabel(lb.id);
            float rawArrival  = CalculateHybridLiveEta(now, scheduledDep, lb.t.position,
                                                        stops, targetIndex, route, outbound, variant, liveNextIdx, atOrigin);
            float arrivalTime = SmoothedEta(label, stopCode, outbound, rawArrival, now);

            // [TRACK-9] Register this trip as live (suppresses Section 3 for same bus+trip)
            liveNPCTrips.Add((lb.id, scheduledDep));

            RegisterArrival(lb.id, false, scheduledDep, arrivalTime, label, variant,
                             isLivePlayer: BusScheduler.IsPlayer(lb.id));
        }
        // ── 3. SCHEDULED SLOTS ───────────────────────────────────────────────
        // Covers: AssignedNPC/AssignedPlayer (waiting at terminal, in pipeline,
        // OR reserved-but-not-yet-departed — see [TRACK-11]), Unassigned (TBD)
        // Skips:  InService (Section 2), Completed
        for (int i = 0; i < allSlots.Count; i++)
        {
            var slot = allSlots[i];
            if (slot.routeNumber != route.routeNumber
                || slot.isOutbound != outbound
                || slot.state      == SlotState.Completed
                || slot.state      == SlotState.InService) continue;

            bool isUnassigned = slot.state == SlotState.Unassigned;
            // [TRACK-11] AssignedPlayer used to be hard-skipped here entirely
            // (see the class-level fix note) — a reserved player slot was
            // invisible from the moment it was claimed until BeginLeg() ran
            // RecordActualDeparture and flipped it to InService. Treated
            // identically to AssignedNPC now: shown as a scheduled arrival
            // via timetable ETA, same as any NPC bus still at its terminal.
            bool isAssigned   = slot.state == SlotState.AssignedNPC || slot.state == SlotState.AssignedPlayer;

            if (!isUnassigned && !isAssigned) continue;

            // [TRACK-9] Skip if Section 2 already tracked this bus's current trip live
            if (isAssigned && slot.assignedBusID >= 0
                && liveNPCTrips.Contains((slot.assignedBusID, slot.scheduledDeparture)))
                continue;
            // Player's own busID is negative (PLAYER_BUS_ID) so the >= 0 guard
            // above never applies to it — check the same suppression by ID
            // directly so a player slot that Section 2 already registered
            // live this same trip isn't double-counted here either.
            if (isAssigned && slot.assignedBusID < 0
                && liveNPCTrips.Contains((slot.assignedBusID, slot.scheduledDeparture)))
                continue;

            // AssignedNPC/AssignedPlayer at terminal (in _slotByBus, waiting to
            // depart) or still mid-dead-run: treat as a scheduled arrival using
            // timetable ETA (bus/player is not yet physically progressing along
            // the route). Pipeline slots (AssignedNPC, NOT in _slotByBus) are
            // also handled here.

            string slotVariant = slot.variantLetter;
            var    bindings    = GetStopBindingsForVariant(route, outbound, slotVariant);

            int   targetIndex    = -1;
            float stopOffsetMins = 0f;

            for (int b = 0; b < bindings.Count; b++)
            {
                if (bindings[b].stopCode == stopCode)
                {
                    targetIndex    = b;
                    stopOffsetMins = bindings[b].minutesFromStart;
                    break;
                }
            }
            if (targetIndex < 0) continue;

            // Use static timetable ETA for all non-live slots (prevents jumpy ETAs)
            float rawArrival = slot.scheduledDeparture + stopOffsetMins;
            if (rawArrival < now) continue;

            // [TRACK-8] Resolve fleet label
            string label;
            int    trackingID;

            if (isUnassigned)
            {
                label       = $"TBD-{i}";
                trackingID  = i; // slot array index as unique key for TBD entries
            }
            else
            {
                label      = ResolveFleetLabel(slot.assignedBusID);
                trackingID = slot.assignedBusID;
            }

            bool isTerminal = targetIndex == 0 || targetIndex == bindings.Count - 1;
            RegisterArrival(trackingID, isUnassigned, slot.scheduledDeparture, rawArrival, label, slotVariant);
        }

        // ── 4. FINAL DEDUP: one entry per unique trip (bus + scheduled departure) ──
        // Multiple sections can register for the same trip; keep the best (earliest) ETA.
        var byTrip = new Dictionary<string, (float arrivalTime, string label, string variant)>();

        foreach (var kv in bestByBus)
        {
            var entry        = kv.Value;
            // Include schedDep in trip key so same bus's future trips aren't squashed
            string tripKey = $"{entry.label}|{kv.Key.schedDep:F0}";

            if (!byTrip.TryGetValue(tripKey, out var existing)
                || entry.arrivalTime < existing.arrivalTime)
            {
                byTrip[tripKey] = entry;
            }
        }

        var sorted = new List<(float arrivalTime, string label, string variant)>(byTrip.Values);
        sorted.Sort((a, b) => a.arrivalTime.CompareTo(b.arrivalTime));
        return sorted;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  BINDING RESOLVER
    // ═════════════════════════════════════════════════════════════════════════

    // [TRACK-1] No longer requires overrideRoute=true for variant stop list to apply.
    // [TRACK-2] `outbound` is always passed through so inbound/outbound never mix.
    private List<RouteStopBinding> GetStopBindingsForVariant(
        BusRouteData route, bool outbound, string variantLetter)
    {
        if (!string.IsNullOrEmpty(variantLetter) && route.variants != null)
        {
            var variant = route.variants.Find(v => v?.variantLetter == variantLetter);
            if (variant != null)
            {
                var overrides = outbound
                    ? variant.outboundStopsOverride
                    : variant.inboundStopsOverride;
                if (overrides != null && overrides.Count > 0)
                    return overrides;
            }
        }
        var mainline = outbound ? route.outboundStops : route.inboundStops;
        return mainline ?? new List<RouteStopBinding>();
    }

    private List<RouteStopBinding> GetBindingsForVariantDirect(
        BusRouteData route, bool outbound, RouteVariantData variant)
    {
        if (variant == null) return route.GetStopBindings(outbound, null);
        var overrides = outbound ? variant.outboundStopsOverride : variant.inboundStopsOverride;
        return (overrides != null && overrides.Count > 0)
            ? overrides
            : route.GetStopBindings(outbound, null);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  [REWRITE] STOP -> SERVING-ROUTES INDEX
    //
    //  Old design: GetCachedStops lazily cached each route's resolved stop-code
    //  list per (route, direction, variant), only forgotten by an explicit
    //  InvalidateStopCache call. Nothing reliably made that call whenever a
    //  route's stop bindings changed by hand in the Inspector (including
    //  mid-Play-Session edits), so the cache would go stale: RouteServesStop
    //  (uncached) correctly says a route serves a stop, but the cached lookup
    //  used to actually find arrivals still had the pre-edit list, found no
    //  match, and reported "No scheduled service" for a stop a bus was
    //  visibly running through.
    //
    //  New design: every route's stop codes get pushed directly onto each
    //  BusStopData.servingRoutes list (see CityManager.cs) via a full,
    //  proactive rebuild — triggered immediately by BusRouteData.OnValidate
    //  on any hand-edit, plus a periodic timer here as a safety net for any
    //  other path that changes route data. A stop click now reads directly
    //  off the stop itself instead of scanning every managed route.
    // ═════════════════════════════════════════════════════════════════════════
    public void RebuildStopServiceIndex()
    {
        var scheduler = BusScheduler.Instance;
        var city      = CityManager.Instance;

        if (scheduler == null || scheduler.managedRoutes == null)
        {
            Debug.LogWarning("[BusTrackerService] RebuildStopServiceIndex aborted — BusScheduler.Instance or " +
                              "its managedRoutes array is null. No stop will show any service until this exists.");
            return;
        }
        if (city == null || city.AllStops == null)
        {
            Debug.LogWarning("[BusTrackerService] RebuildStopServiceIndex aborted — CityManager.Instance or " +
                              "its AllStops is null. No stop will show any service until this exists.");
            return;
        }

        foreach (var s in city.AllStops) s?.servingRoutes.Clear();

        int missingCodeCount = 0;
        int routeCount = 0;
        foreach (var route in scheduler.managedRoutes)
        {
            if (route == null) continue;
            routeCount++;
            missingCodeCount += RegisterRouteDirection(route, true,  city);
            missingCodeCount += RegisterRouteDirection(route, false, city);
        }

        int registeredStops = 0, totalRegistrations = 0;
        foreach (var s in city.AllStops)
        {
            if (s == null || s.servingRoutes.Count == 0) continue;
            registeredStops++;
            totalRegistrations += s.servingRoutes.Count;
        }

        Debug.Log($"[BusTrackerService] Stop service index rebuilt: {routeCount} route(s) scanned, " +
                  $"{registeredStops} stop(s) got at least one serving route, {totalRegistrations} total " +
                  $"(route, direction) registration(s)." +
                  (missingCodeCount > 0
                      ? $" {missingCodeCount} stop code(s) referenced by a route's bindings did NOT resolve to " +
                        "any BusStopData — see the warnings above for exactly which codes, that's almost always " +
                        "a typo/case mismatch between the route's stop list and CityManager.stopDefinitions."
                      : ""));
    }

    /// <summary>Registers a route/direction's stops onto each stop's servingRoutes
    /// list. Covers the mainline, every lettered variant's override list, AND
    /// every short turn's full stop sequence (mainline truncation + its own
    /// off-mainline turnaround loop stops) — a stop reachable ONLY via a short
    /// turn's loop was previously invisible to this index entirely.</summary>
    private int RegisterRouteDirection(BusRouteData route, bool outbound, CityManager city)
    {
        int missingCodeCount = 0;

        void RegisterCodes(List<string> codes, string sourceLabel)
        {
            if (codes == null) return;
            foreach (var code in codes)
            {
                if (string.IsNullOrEmpty(code)) continue;
                var stop = city.GetStop(code);
                if (stop == null)
                {
                    missingCodeCount++;
                    Debug.LogWarning($"[BusTrackerService] Route '{route.routeNumber}' ({sourceLabel}, " +
                                      $"{(outbound ? "outbound" : "inbound")}) references stop code '{code}' — " +
                                      "no BusStopData with that exact code exists in CityManager.stopDefinitions. " +
                                      "Check for a typo, extra whitespace, or a case mismatch.");
                    continue;
                }

                bool already = false;
                foreach (var sr in stop.servingRoutes)
                    if (sr.route == route && sr.outbound == outbound) { already = true; break; }
                if (!already)
                    stop.servingRoutes.Add(new BusStopData.ServingRoute { route = route, outbound = outbound });
            }
        }

        RegisterCodes(route.GetStopCodes(outbound), "mainline");

        if (route.variants != null)
            foreach (var v in route.variants)
                RegisterCodes(route.GetStopCodes(outbound, v), $"variant {v?.variantLetter}");

        return missingCodeCount;
    }

    /// <summary>Ordered stop-code list for a route/direction/variant — used
    /// where POSITION along the route matters (ETA math needs IndexOf), not
    /// just membership. Computed fresh every call (no caching) — this list
    /// is cheap to build (a direct conversion of already-in-memory bindings)
    /// so there's no real cost to never letting it go stale.</summary>
    /// <summary>Returns null if `stopCode` is on the route's own MAINLINE stop
    /// sequence for this direction. Otherwise searches each lettered variant's
    /// override list and returns the first one that actually contains the stop —
    /// this is how a variant-only stop (never on the mainline) gets correctly
    /// matched back to the specific variant whose stop list it lives on.</summary>
    private string FindServingVariantLetter(BusRouteData route, bool outbound, string stopCode)
    {
        var mainlineCodes = route.GetStopCodes(outbound);
        if (mainlineCodes != null && mainlineCodes.Contains(stopCode))
            return null;

        if (route.variants != null)
            foreach (var v in route.variants)
            {
                if (v == null) continue;
                var vCodes = route.GetStopCodes(outbound, v);
                if (vCodes != null && vCodes.Contains(stopCode))
                    return v.variantLetter;
            }

        return null;
    }

    private List<string> GetStopCodesFresh(BusRouteData route, bool outbound, string variantLetter)
    {
        var bindings  = GetStopBindingsForVariant(route, outbound, variantLetter);
        var stopsList = new List<string>(bindings.Count);
        for (int i = 0; i < bindings.Count; i++)
            if (!string.IsNullOrEmpty(bindings[i].stopCode))
                stopsList.Add(bindings[i].stopCode);
        return stopsList;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ETA SMOOTHING
    // ═════════════════════════════════════════════════════════════════════════
    private static string EtaKey(string busLabel, string stopCode, bool outbound)
        => $"{busLabel}|{stopCode}|{(outbound ? 'O' : 'I')}";

    private float SmoothedEta(string busLabel, string stopCode, bool outbound,
                               float newRawEta, float now)
    {
        string key = EtaKey(busLabel, stopCode, outbound);
        if (!_etaCache.TryGetValue(key, out float cached))
        { _etaCache[key] = newRawEta; return newRawEta; }

        float cachedMins = cached    - now;
        float newMins    = newRawEta - now;

        // If ETA jumped forward significantly (bus lagged), reset to raw
        if (newMins - cachedMins > etaJumpToleranceMinutes)
        { _etaCache[key] = newRawEta; return newRawEta; }

        float smoothed = Mathf.Lerp(cached, newRawEta, etaSmoothWeight);
        _etaCache[key] = smoothed;
        return smoothed;
    }

    private void PruneArrivedEntries(float now)
    {
        var toRemove = new List<string>();
        foreach (var kv in _etaCache)
            if (kv.Value - now <= 0f) toRemove.Add(kv.Key);
        foreach (var k in toRemove) _etaCache.Remove(k);
    }

    public void InvalidateEtaCacheForDirection(string busLabel, bool outbound)
    {
        string suffix = outbound ? "|O" : "|I";
        string prefix = busLabel + "|";
        var toRemove  = new List<string>();
        foreach (var k in _etaCache.Keys)
            if (k.StartsWith(prefix) && k.EndsWith(suffix)) toRemove.Add(k);
        foreach (var k in toRemove) _etaCache.Remove(k);
    }

    public void InvalidateEtaCache(string busLabel)
    {
        var toRemove = new List<string>();
        foreach (var k in _etaCache.Keys)
            if (k.StartsWith(busLabel + "|")) toRemove.Add(k);
        foreach (var k in toRemove) _etaCache.Remove(k);
    }

    public void InvalidateAllEtaCaches() => _etaCache.Clear();

    // ═════════════════════════════════════════════════════════════════════════
    //  FLEET ORDER ENGINE
    // ═════════════════════════════════════════════════════════════════════════
    private void BuildFleetOrder(string routeNumber, bool isOutbound)
    {
        _fleetOrder.Clear();
        var allSlots = BusScheduler.Instance.AllSlots;

        for (int i = 0; i < allSlots.Count; i++)
        {
            var slot = allSlots[i];
            if (slot.routeNumber   != routeNumber) continue;
            if (slot.isOutbound    != isOutbound)  continue;
            if (slot.assignedBusID < 0)            continue;
            if (slot.state == SlotState.Completed ||
                slot.state == SlotState.Unassigned ||
                slot.state == SlotState.AssignedPlayer) continue;

            bool dup = false;
            for (int j = 0; j < _fleetOrder.Count; j++)
                if (_fleetOrder[j] == slot.assignedBusID) { dup = true; break; }
            if (!dup) _fleetOrder.Add(slot.assignedBusID);
        }

        foreach (var kv in BusRegistry.ActiveBuses)
        {
            var ctrl = kv.Value;
            if (ctrl == null
                || ctrl.CurrentRoute?.routeNumber != routeNumber
                || ctrl.IsOutbound != isOutbound) continue;

            bool dup = false;
            for (int j = 0; j < _fleetOrder.Count; j++)
                if (_fleetOrder[j] == kv.Key) { dup = true; break; }
            if (!dup) _fleetOrder.Add(kv.Key);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  HYBRID ETA CALCULATION
    // ═════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// ETA (absolute game minutes) of a LIVE bus reaching stop <c>targetIndex</c>.
    ///
    /// SCHEDULE + DELAY, not a distance/speed guess:  arrival = (trip's scheduled departure + the target stop's real
    /// scheduled offset) + how late the bus is RIGHT NOW. "How late" is measured where the bus actually is: its
    /// position along the stop-to-stop polyline is turned into the time the timetable expected it to be there, and the
    /// difference to the clock is the delay. So a bus that is 2 min late shows "scheduled time for that stop + 2 min"
    /// whether the stop is 200 m or 20 km away.
    ///
    /// [FIX] This used to average the timetable estimate with a purely physical one that assumed a fixed 1.5 m/s
    /// (5.4 km/h) for the remaining straight-line distance -- so every far-away stop was dragged out to a huge ETA
    /// (a stop 2 km ahead read ~15 min slower than the timetable, a bus 2-3 min late showed 30+ min). Stop times also
    /// came from oneWayTripMinutes * (stopIndex / stopCount) (evenly spaced), ignoring each stop's real offset.
    /// </summary>
    private float CalculateHybridLiveEta(
        float now, float scheduledDeparture, Vector3 busPos,
        List<string> stopCodes, int targetIndex, BusRouteData route,
        bool outbound = true, string variant = "", int nextStopIdx = -1, bool preDeparture = false)
    {
        if (stopCodes == null || stopCodes.Count == 0) return now;
        int last = Mathf.Clamp(targetIndex, 0, stopCodes.Count - 1);
        var bindings = GetStopBindingsForVariant(route, outbound, variant);
        int totalStops = Mathf.Max(1, stopCodes.Count - 1);

        // scheduled minutes from the trip's departure to stopCodes[idx]: the stop's real binding offset if it has one,
        // else the old even-spacing estimate.
        float OffsetFor(int idx)
        {
            if (bindings != null)
                for (int b = 0; b < bindings.Count; b++)
                    if (bindings[b].stopCode == stopCodes[idx]) return bindings[b].minutesFromStart;
            return route.oneWayTripMinutes * ((float)idx / totalStops);
        }

        // Not pulled out yet: the trip hasn't begun, so it can only be late (never "early"). Same rule the
        // bus's own board uses (late-only offset before departure).
        if (preDeparture)
            return Mathf.Max(now, scheduledDeparture + OffsetFor(last) + Mathf.Max(0f, now - scheduledDeparture));

        // Where is the bus along the stops up to the target? -> the scheduled time the timetable expected it to be there.
        float expectedOffsetNow = 0f;
        if (last >= 1)
        {
            int segA;
            if (nextStopIdx >= 1 && nextStopIdx <= last) segA = nextStopIdx - 1;   // the bus's real "next stop"
            else                                                                    // otherwise: nearest stop-to-stop segment
            {
                segA = 0; float best = float.MaxValue;
                for (int i = 0; i < last; i++)
                {
                    Vector3 pa = GetWorldPositionFromCode(stopCodes[i]), pb = GetWorldPositionFromCode(stopCodes[i + 1]);
                    float d = DistancePointToSegmentXZ(busPos, pa, pb);
                    if (d < best) { best = d; segA = i; }
                }
            }
            Vector3 a = GetWorldPositionFromCode(stopCodes[segA]);
            Vector3 c = GetWorldPositionFromCode(stopCodes[segA + 1]);
            Vector3 ac = c - a; ac.y = 0f;
            Vector3 ab = busPos - a; ab.y = 0f;
            float len2 = ac.sqrMagnitude;
            float t = len2 > 1f ? Mathf.Clamp01(Vector3.Dot(ab, ac) / len2) : 0f;
            expectedOffsetNow = Mathf.Lerp(OffsetFor(segA), OffsetFor(segA + 1), t);
        }

        // Early is a negative delay and is honoured in full (a bus 25 min early arrives 25 min before its timetable).
        // The clamp is only a sanity guard against a nonsense position (bus far off its route), not a lateness limit.
        float delay = Mathf.Clamp(now - (scheduledDeparture + expectedOffsetNow), -240f, 480f);
        float arrival = scheduledDeparture + OffsetFor(last) + delay;
        return Mathf.Max(now, arrival);
    }

    /// <summary>Shortest ground distance from a position to the polyline through a route's stops.</summary>
    private float DistanceToStopPolylineXZ(Vector3 pos, List<string> stopCodes)
    {
        if (stopCodes == null || stopCodes.Count == 0) return 0f;
        if (stopCodes.Count == 1) return Vector3.Distance(pos, GetWorldPositionFromCode(stopCodes[0]));
        float best = float.MaxValue;
        Vector3 prev = GetWorldPositionFromCode(stopCodes[0]);
        for (int i = 1; i < stopCodes.Count; i++)
        {
            Vector3 cur = GetWorldPositionFromCode(stopCodes[i]);
            best = Mathf.Min(best, DistancePointToSegmentXZ(pos, prev, cur));
            prev = cur;
        }
        return best;
    }

    private static float DistancePointToSegmentXZ(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector2 P = new Vector2(p.x, p.z), A = new Vector2(a.x, a.z), B = new Vector2(b.x, b.z);
        Vector2 ab = B - A; float l2 = ab.sqrMagnitude;
        float t = l2 > 0.0001f ? Mathf.Clamp01(Vector2.Dot(P - A, ab) / l2) : 0f;
        return Vector2.Distance(P, A + ab * t);
    }

    private int GetNearestStopIndex(Vector3 busPos, List<string> stopCodes, int maxIndex = -1)
    {
        int upper = maxIndex >= 0 ? Mathf.Min(maxIndex, stopCodes.Count - 1) : stopCodes.Count - 1;
        int   index   = 0;
        float minDist = float.MaxValue;
        for (int i = 0; i <= upper; i++)
        {
            float d = Vector3.Distance(busPos, GetWorldPositionFromCode(stopCodes[i]));
            if (d < minDist) { minDist = d; index = i; }
        }
        return index;
    }

    private Vector3 GetWorldPositionFromCode(string stopCode)
    {
        if (CityManager.Instance == null) return Vector3.zero;
        var stopData = CityManager.Instance.GetStop(stopCode);
        return stopData != null ? stopData.GetWorldPosition() : Vector3.zero;
    }
}