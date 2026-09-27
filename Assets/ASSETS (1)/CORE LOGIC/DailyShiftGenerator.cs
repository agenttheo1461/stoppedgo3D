using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[System.Serializable]
public class DailyShiftBlock
{
    // These are ABSOLUTE game-minutes:
    // dayNumber * 1440 + minute-of-day.
    public float windowStartMinutes;
    public float windowEndMinutes;

    public string routeNumber;

    public bool isOvernight;
    public bool isEvening;

    public bool completed;
    public bool missed;

    public string TimeRangeLabel =>
        $"{BusScheduler.MinutesToTimeString(windowStartMinutes)}-{BusScheduler.MinutesToTimeString(windowEndMinutes)}";

    public float LengthMinutes =>
        windowEndMinutes - windowStartMinutes;
}


// ═════════════════════════════════════════════════════════════════════
// ROUTE CHANCE CONFIGURATION
// ═════════════════════════════════════════════════════════════════════

[System.Serializable]
public class RouteChance
{
    public string routeNumber;

    [Min(0)]
    public int weight = 1;
}


[System.Serializable]
public class RouteChanceInterval
{
    public string intervalName;

    [Range(0, 23)]
    public int startHour;

    [Range(1, 24)]
    public int endHour;

    public List<RouteChance> routes = new();
}


/// <summary>
/// v4.0
///
/// Deterministic daily shift generation with time-based weighted route
/// selection.
///
/// Same:
///     dayNumber + busID + preferredRoute + route chance settings
///
/// produces the same calendar every time.
///
/// Route weights:
///     0  = never selected
///     1  = normal chance
///     5  = five times the chance of weight 1
///     10 = ten times the chance of weight 1
///
/// Routes that are not explicitly listed in a matching interval receive
/// weight 1.
///
/// Route eligibility is still checked first, meaning a route with a high
/// weight cannot be selected if it does not operate during the block.
/// </summary>
public class DailyShiftGenerator
{
    const float DAY_MINUTES = 1440f;

    const int MIN_BLOCK_HOURS = 2;
    const int MAX_BLOCK_HOURS = 4;

    const int OVERNIGHT_HOURS = 4;
    const int EVENING_LENGTH_HOURS = 3;

    const int MIN_TOTAL_BLOCKS = 3;
    const int MAX_TOTAL_BLOCKS = 6;

private readonly List<RouteChanceInterval> _chanceIntervals;

public DailyShiftGenerator()
{
    _chanceIntervals = new List<RouteChanceInterval>();
}

public DailyShiftGenerator(List<RouteChanceInterval> chanceIntervals)
{
    _chanceIntervals = chanceIntervals ?? new List<RouteChanceInterval>();
}


    // ═════════════════════════════════════════════════════════════════
    // GENERATION
    // ═════════════════════════════════════════════════════════════════

    public List<DailyShiftBlock> Generate(
        int dayNumber,
        int busID,
        string preferredRoute = null)
    {
        var rng = new System.Random(
            SeedFor(dayNumber, busID, preferredRoute));

        int targetCount =
            RangeInt(
                rng,
                MIN_TOTAL_BLOCKS,
                MAX_TOTAL_BLOCKS + 1);

        int fillerCount =
            Mathf.Max(0, targetCount - 2);

        int overnightHours = OVERNIGHT_HOURS;
        int eveningHours = EVENING_LENGTH_HOURS;

        var fillerHours = new List<int>();

        for (int i = 0; i < fillerCount; i++)
        {
            fillerHours.Add(
                RangeInt(
                    rng,
                    MIN_BLOCK_HOURS,
                    MAX_BLOCK_HOURS + 1));
        }

        int totalRequiredHours =
            overnightHours + eveningHours;

        foreach (var h in fillerHours)
            totalRequiredHours += h;

        int dayHours =
            Mathf.FloorToInt(DAY_MINUTES / 60f);

        if (totalRequiredHours > dayHours)
        {
            int excess =
                totalRequiredHours - dayHours;

            for (int i = 0;
                 i < fillerHours.Count && excess > 0;
                 i++)
            {
                int shrink =
                    Mathf.Min(
                        excess,
                        fillerHours[i] - MIN_BLOCK_HOURS);

                if (shrink <= 0)
                    continue;

                fillerHours[i] -= shrink;
                excess -= shrink;
            }
        }

        totalRequiredHours =
            overnightHours + eveningHours;

        foreach (var h in fillerHours)
            totalRequiredHours += h;

        int leftoverHoursForGaps =
            Mathf.Max(
                0,
                dayHours - totalRequiredHours);


        // ═════════════════════════════════════════════════════════════
        // TURN UNUSED SPACE INTO EXTRA BLOCKS
        // ═════════════════════════════════════════════════════════════

        int blocksSoFar =
            2 + fillerHours.Count;

        while (
            leftoverHoursForGaps >= MIN_BLOCK_HOURS &&
            blocksSoFar < MAX_TOTAL_BLOCKS)
        {
            int extraLen =
                Mathf.Min(
                    MAX_BLOCK_HOURS,
                    leftoverHoursForGaps);

            if (
                leftoverHoursForGaps - extraLen > 0 &&
                leftoverHoursForGaps - extraLen < MIN_BLOCK_HOURS)
            {
                extraLen =
                    leftoverHoursForGaps - MIN_BLOCK_HOURS;
            }

            if (extraLen < MIN_BLOCK_HOURS)
                break;

            fillerHours.Add(extraLen);

            leftoverHoursForGaps -= extraLen;
            blocksSoFar++;
        }


        // ═════════════════════════════════════════════════════════════
        // GAPS
        // ═════════════════════════════════════════════════════════════

        const int MAX_GAP_MINUTES = 30;

        int gapCount =
            fillerHours.Count + 1;

        int leftoverMinutesForGaps =
            leftoverHoursForGaps * 60;

        int cappedTotalGapMinutes =
            Mathf.Min(
                leftoverMinutesForGaps,
                gapCount * MAX_GAP_MINUTES);

        int baseGapMinutes =
            cappedTotalGapMinutes / gapCount;

        int extraGapMinutes =
            cappedTotalGapMinutes % gapCount;

        int leftoverMinutesAfterGaps =
            leftoverMinutesForGaps -
            cappedTotalGapMinutes;


        // ═════════════════════════════════════════════════════════════
        // BUILD BLOCKS
        // ═════════════════════════════════════════════════════════════

        var blocks =
            new List<DailyShiftBlock>();

        float dayBase =
            dayNumber * DAY_MINUTES;

        int cursorMinutes = 0;
        int gapIdx = 0;

        int NextGapMinutes()
        {
            return
                baseGapMinutes +
                (gapIdx++ < extraGapMinutes ? 1 : 0);
        }


        // ═════════════════════════════════════════════════════════════
        // OVERNIGHT
        // ═════════════════════════════════════════════════════════════

        float overnightStart =
            dayBase + cursorMinutes;

        float overnightEnd =
            overnightStart +
            overnightHours * 60f;

        string overnightRoute =
            ChooseOvernightRoute(
                rng,
                preferredRoute,
                overnightStart,
                overnightEnd);

        blocks.Add(
            BuildBlock(
                overnightStart,
                overnightEnd,
                overnightRoute,
                isOvernight: true));

        cursorMinutes +=
            overnightHours * 60 +
            NextGapMinutes();


        // ═════════════════════════════════════════════════════════════
        // FILLER BLOCKS
        // ═════════════════════════════════════════════════════════════

        string lastRoute =
            overnightRoute;

        var placedFillers =
            new List<DailyShiftBlock>();

        foreach (var lenHours in fillerHours)
        {
            float blockStart =
                dayBase + cursorMinutes;

            float blockEnd =
                dayBase +
                cursorMinutes +
                lenHours * 60f;

            string route =
                ChooseRoute(
                    rng,
                    preferredRoute,
                    lastRoute,
                    blockStart,
                    blockEnd);

            var block =
                BuildBlock(
                    blockStart,
                    blockEnd,
                    route);

            placedFillers.Add(block);

            lastRoute = route;

            cursorMinutes +=
                lenHours * 60 +
                NextGapMinutes();
        }


        // ═════════════════════════════════════════════════════════════
        // EVENING
        // ═════════════════════════════════════════════════════════════

        float eveningStart =
            dayBase + cursorMinutes;

        float eveningEnd =
            dayBase +
            cursorMinutes +
            eveningHours * 60f +
            leftoverMinutesAfterGaps;

        string eveningRoute =
            ChooseRoute(
                rng,
                preferredRoute,
                lastRoute,
                eveningStart,
                eveningEnd);

        var eveningBlock =
            BuildBlock(
                eveningStart,
                eveningEnd,
                eveningRoute,
                isEvening: true);

        blocks.AddRange(placedFillers);
        blocks.Add(eveningBlock);

        blocks.Sort(
            (a, b) =>
                a.windowStartMinutes.CompareTo(
                    b.windowStartMinutes));


        // ═════════════════════════════════════════════════════════════
        // DEBUG
        // ═════════════════════════════════════════════════════════════

        if (
            BusScheduler.Instance != null &&
            BusScheduler.Instance.logDispatches)
        {
            var sb =
                new System.Text.StringBuilder(
                    $"[DailyShiftGenerator] Day {dayNumber} " +
                    $"Bus#{busID} calendar " +
                    $"(seed {SeedFor(dayNumber, busID, preferredRoute)}):\n");

            foreach (var b in blocks)
            {
                sb.AppendLine(
                    $"  {b.TimeRangeLabel}  " +
                    $"Route {b.routeNumber}" +
                    $"{(b.isOvernight ? "  [overnight]" : "")}" +
                    $"{(b.isEvening ? "  [evening]" : "")}");
            }

            Debug.Log(sb.ToString());
        }

        return blocks;
    }


    // ═════════════════════════════════════════════════════════════════
    // DETERMINISTIC SEED
    // ═════════════════════════════════════════════════════════════════

    private static int SeedFor(
        int dayNumber,
        int busID,
        string preferredRoute)
    {
        unchecked
        {
            int hash = 17;

            hash =
                hash * 31 +
                dayNumber;

            hash =
                hash * 31 +
                busID;

            if (!string.IsNullOrEmpty(preferredRoute))
            {
                foreach (char c in preferredRoute)
                    hash =
                        hash * 31 +
                        c;
            }

            return hash;
        }
    }


    private static int RangeInt(
        System.Random rng,
        int minInclusive,
        int maxExclusive)
    {
        return rng.Next(
            minInclusive,
            maxExclusive);
    }


    // ═════════════════════════════════════════════════════════════════
    // ROUTE OPERATING WINDOW
    // ═════════════════════════════════════════════════════════════════

    private static bool RouteCoversWindow(
        BusRouteData route,
        float blockStart,
        float blockEnd)
    {
        if (route == null)
            return false;

        float relStart =
            blockStart % DAY_MINUTES;

        float relEnd =
            blockEnd % DAY_MINUTES;

        if (relStart < 0f)
            relStart += DAY_MINUTES;

        if (relEnd < 0f)
            relEnd += DAY_MINUTES;

        if (relEnd <= relStart)
            relEnd += DAY_MINUTES;


        float opStart =
            route.operatingStartMinutes;

        float opEnd =
            route.operatingEndMinutes;

        if (opEnd <= opStart)
            opEnd += DAY_MINUTES;


        bool overlaps =
            relStart < opEnd &&
            relEnd > opStart;

        bool overlapsShifted =
            (relStart - DAY_MINUTES) < opEnd &&
            (relEnd - DAY_MINUTES) > opStart;

        bool overlapsForward =
            (relStart + DAY_MINUTES) < opEnd &&
            (relEnd + DAY_MINUTES) > opStart;

        return
            overlaps ||
            overlapsShifted ||
            overlapsForward;
    }


    // ═════════════════════════════════════════════════════════════════
    // TIME INTERVAL / ROUTE WEIGHT
    // ═════════════════════════════════════════════════════════════════

    private int GetRouteWeight(
        string routeNumber,
        float blockStart,
        float blockEnd)
    {
        if (string.IsNullOrEmpty(routeNumber))
            return 0;

        if (
            _chanceIntervals == null ||
            _chanceIntervals.Count == 0)
        {
            return 1;
        }


        // IMPORTANT:
        // The block's START determines its chance interval.
        //
        // Example:
        // 06:00-10:00 block
        // uses whatever interval contains 06:00.
        //
        float minuteOfDay =
            blockStart % DAY_MINUTES;

        if (minuteOfDay < 0f)
            minuteOfDay += DAY_MINUTES;

        int hour =
            Mathf.FloorToInt(
                minuteOfDay / 60f);


        foreach (var interval in _chanceIntervals)
        {
            if (interval == null)
                continue;

            bool inside;


            // Normal interval:
            // 04:00 -> 08:00
            if (interval.endHour > interval.startHour)
            {
                inside =
                    hour >= interval.startHour &&
                    hour < interval.endHour;
            }
            else
            {
                // Overnight interval:
                // 22:00 -> 04:00
                inside =
                    hour >= interval.startHour ||
                    hour < interval.endHour;
            }

            if (!inside)
                continue;


            foreach (var route in interval.routes)
            {
                if (
                    route != null &&
                    string.Equals(
                        route.routeNumber,
                        routeNumber,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Mathf.Max(
                        0,
                        route.weight);
                }
            }


            // Route wasn't specifically configured
            // in this interval.
            //
            // Default:
            // weight 1.
            return 1;
        }


        // No matching interval.
        return 1;
    }


    // ═════════════════════════════════════════════════════════════════
    // WEIGHTED RANDOM
    // ═════════════════════════════════════════════════════════════════

    private string WeightedRandom(
        System.Random rng,
        List<string> candidates,
        List<int> weights)
    {
        if (
            candidates == null ||
            weights == null ||
            candidates.Count == 0 ||
            candidates.Count != weights.Count)
        {
            return null;
        }


        long totalWeight = 0;

        for (int i = 0;
             i < weights.Count;
             i++)
        {
            totalWeight +=
                Mathf.Max(
                    0,
                    weights[i]);
        }


        if (totalWeight <= 0)
            return null;


        // System.Random.Next(int) can't handle
        // arbitrarily large totals, so keep the
        // normal case simple and use a double for
        // the roll.
        double roll =
            rng.NextDouble() *
            totalWeight;

        long accumulated = 0;


        for (int i = 0;
             i < candidates.Count;
             i++)
        {
            accumulated +=
                Mathf.Max(
                    0,
                    weights[i]);

            if (roll < accumulated)
                return candidates[i];
        }


        // Safety fallback.
        for (int i = candidates.Count - 1;
             i >= 0;
             i--)
        {
            if (weights[i] > 0)
                return candidates[i];
        }

        return null;
    }


    // ═════════════════════════════════════════════════════════════════
    // OVERNIGHT ROUTE
    // ═════════════════════════════════════════════════════════════════

    private string ChooseOvernightRoute(
        System.Random rng,
        string preferredRoute,
        float blockStart,
        float blockEnd)
    {
        var overnightCapable =
            GetOvernightCapableRoutes();

        var candidates =
            new List<string>();

        var weights =
            new List<int>();


        foreach (string routeNumber in overnightCapable)
        {
            if (string.IsNullOrEmpty(routeNumber))
                continue;

            int weight =
                GetRouteWeight(
                    routeNumber,
                    blockStart,
                    blockEnd);

            if (weight <= 0)
                continue;

            candidates.Add(routeNumber);
            weights.Add(weight);
        }


        // Preferred route does NOT automatically override
        // the weighting system.
        //
        // It is simply one of the eligible routes.
        if (candidates.Count > 0)
        {
            string selected =
                WeightedRandom(
                    rng,
                    candidates,
                    weights);

            if (!string.IsNullOrEmpty(selected))
                return selected;
        }


        // If every overnight-capable route has weight 0,
        // use preferred route if it exists and is actually
        // overnight-capable.
        if (
            !string.IsNullOrEmpty(preferredRoute) &&
            overnightCapable.Contains(preferredRoute))
        {
            Debug.LogWarning(
                $"[DailyShiftGenerator] All overnight routes " +
                $"have weight 0 for {BusScheduler.MinutesToTimeString(blockStart)}-" +
                $"{BusScheduler.MinutesToTimeString(blockEnd)}. " +
                $"Using preferred Route {preferredRoute} as fallback.");

            return preferredRoute;
        }


        // Last resort.
        if (overnightCapable.Count > 0)
        {
            string fallback =
                overnightCapable[
                    RangeInt(
                        rng,
                        0,
                        overnightCapable.Count)];

            Debug.LogWarning(
                $"[DailyShiftGenerator] All overnight route weights " +
                $"are 0. Falling back to Route {fallback}.");

            return fallback;
        }


        // No genuine overnight route exists.
        if (!string.IsNullOrEmpty(preferredRoute))
        {
            Debug.LogWarning(
                $"[DailyShiftGenerator] No overnight-capable routes found — " +
                $"using preferred route '{preferredRoute}' anyway.");

            return preferredRoute;
        }


        var anyRoute =
            BusScheduler.Instance?
                .managedRoutes?
                .FirstOrDefault(
                    r => r != null);


        if (anyRoute != null)
        {
            Debug.LogWarning(
                $"[DailyShiftGenerator] No overnight-capable routes found — " +
                $"falling back to Route {anyRoute.routeNumber}.");

            return anyRoute.routeNumber;
        }


        Debug.LogWarning(
            "[DailyShiftGenerator] No overnight-capable routes " +
            "AND no managed routes at all.");

        return null;
    }


    // ═════════════════════════════════════════════════════════════════
    // NORMAL ROUTE SELECTION
    // ═════════════════════════════════════════════════════════════════

    private string ChooseRoute(
        System.Random rng,
        string preferredRoute,
        string excludeRoute,
        float blockStart,
        float blockEnd)
    {
        var candidates =
            new List<string>();

        var weights =
            new List<int>();


        foreach (
            var route in
            BusScheduler.Instance?.managedRoutes
            ?? Enumerable.Empty<BusRouteData>())
        {
            if (route == null)
                continue;

            if (
                route.routeNumber ==
                excludeRoute)
            {
                continue;
            }

            if (
                !RouteCoversWindow(
                    route,
                    blockStart,
                    blockEnd))
            {
                continue;
            }


            int weight =
                GetRouteWeight(
                    route.routeNumber,
                    blockStart,
                    blockEnd);


            // Weight 0 means:
            // NEVER select this route.
            if (weight <= 0)
                continue;


            candidates.Add(
                route.routeNumber);

            weights.Add(weight);
        }


        // Preferred route is deliberately NOT forced.
        //
        // It participates using its normal configured
        // weight.
        //
        // This means preferredRoute does not break the
        // weighted system anymore.


        if (candidates.Count > 0)
        {
            string selected =
                WeightedRandom(
                    rng,
                    candidates,
                    weights);

            if (!string.IsNullOrEmpty(selected))
                return selected;
        }


        // ═════════════════════════════════════════════════════════════
        // WEIGHTED FALLBACK
        // ═════════════════════════════════════════════════════════════
        //
        // If every eligible route has weight 0, we don't want
        // to create a completely blank block.
        //
        // Ignore the weights ONLY as a final fallback.
        //

        Debug.LogWarning(
            $"[DailyShiftGenerator] No weighted route available during " +
            $"{BusScheduler.MinutesToTimeString(blockStart)}-" +
            $"{BusScheduler.MinutesToTimeString(blockEnd)}. " +
            "Falling back to any eligible route.");


        candidates.Clear();


        foreach (
            var route in
            BusScheduler.Instance?.managedRoutes
            ?? Enumerable.Empty<BusRouteData>())
        {
            if (route == null)
                continue;

            if (
                route.routeNumber ==
                excludeRoute)
            {
                continue;
            }

            if (
                !RouteCoversWindow(
                    route,
                    blockStart,
                    blockEnd))
            {
                continue;
            }

            candidates.Add(
                route.routeNumber);
        }


        if (candidates.Count > 0)
        {
            return candidates[
                RangeInt(
                    rng,
                    0,
                    candidates.Count)];
        }


        // ═════════════════════════════════════════════════════════════
        // ABSOLUTE LAST RESORT
        // ═════════════════════════════════════════════════════════════

        Debug.LogWarning(
            $"[DailyShiftGenerator] No route operates during " +
            $"{BusScheduler.MinutesToTimeString(blockStart)}-" +
            $"{BusScheduler.MinutesToTimeString(blockEnd)}. " +
            "Falling back to any managed route.");


        foreach (
            var route in
            BusScheduler.Instance?.managedRoutes
            ?? Enumerable.Empty<BusRouteData>())
        {
            if (
                route != null &&
                route.routeNumber != excludeRoute)
            {
                candidates.Add(
                    route.routeNumber);
            }
        }


        return candidates.Count > 0
            ? candidates[
                RangeInt(
                    rng,
                    0,
                    candidates.Count)]
            : excludeRoute;
    }


    // ═════════════════════════════════════════════════════════════════
    // OVERNIGHT CAPABLE ROUTES
    // ═════════════════════════════════════════════════════════════════

    private List<string> GetOvernightCapableRoutes()
    {
        var result =
            new List<string>();


        if (BusScheduler.Instance == null)
        {
            Debug.LogWarning(
                "[DailyShiftGenerator] BusScheduler.Instance is null " +
                "during GetOvernightCapableRoutes.");

            return result;
        }


        foreach (
            var route in
            BusScheduler.Instance.managedRoutes)
        {
            if (
                route != null &&
                IsOvernightRoute(route))
            {
                result.Add(
                    route.routeNumber);
            }
        }


        return result;
    }


    private bool IsOvernightRoute(
        BusRouteData route)
    {
        if (route == null)
            return false;


        float opStart =
            route.operatingStartMinutes %
            DAY_MINUTES;

        float opEnd =
            route.operatingEndMinutes %
            DAY_MINUTES;


        if (opEnd <= opStart)
            opEnd += DAY_MINUTES;


        // 02:45 -> 03:15
        const float windowStart = 165f;
        const float windowEnd = 195f;


        return
            (windowStart >= opStart &&
             windowEnd <= opEnd)
            ||
            (windowStart + DAY_MINUTES >= opStart &&
             windowEnd + DAY_MINUTES <= opEnd);
    }


    // ═════════════════════════════════════════════════════════════════
    // BLOCK BUILDER
    // ═════════════════════════════════════════════════════════════════

    private DailyShiftBlock BuildBlock(
        float start,
        float end,
        string route,
        bool isOvernight = false,
        bool isEvening = false)
    {
        return new DailyShiftBlock
        {
            windowStartMinutes = start,
            windowEndMinutes = end,
            routeNumber = route,
            isOvernight = isOvernight,
            isEvening = isEvening
        };
    }
}