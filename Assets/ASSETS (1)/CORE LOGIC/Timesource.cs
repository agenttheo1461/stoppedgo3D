using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Answers "what departures should exist for this route on this
/// day" — no state, no ownership, just data generation. Swap in a
/// different implementation (e.g. a JSON-driven importer) without
/// touching SlotPool or BusScheduler at all.</summary>
public interface ITimetableSource
{
    void BuildDay(BusRouteData route, int dayOffset, Action<string, string, bool, float, int> emitSlot);
    // emitSlot(routeNumber, variantLetter, isOutbound, absoluteDepartureMinutes, dayOffset)
}

/// <summary>The existing flat-headway / time-of-day-window generation
/// logic, unchanged in behavior — just relocated and made to emit through
/// a callback instead of writing directly into scheduler internals.</summary>
public class DefaultTimetableSource : ITimetableSource
{
    public void BuildDay(BusRouteData route, int dayOffset, Action<string, string, bool, float, int> emit)
    {
        float dayBase = dayOffset * 1440f;
        GeneratePattern(route, null, dayOffset, dayBase, emit);

        if (route.variants != null)
            foreach (var v in route.variants)
                if (v != null) GeneratePattern(route, v, dayOffset, dayBase, emit);
    }

    private void GeneratePattern(BusRouteData route, RouteVariantData variant, int dayOffset, float dayBase, Action<string, string, bool, float, int> emit)
    {
        bool useV = variant != null && variant.overrideSchedule;
        string vLetter = variant?.variantLetter ?? "";

        if (!useV && route.UsesTimeOfDayWindows)
        {
            GenerateFromWindows(route.scheduleWindows, route.routeNumber, vLetter, dayOffset, dayBase, emit);
            return;
        }

        float start = dayBase + (useV ? variant.operatingStartMinutes : route.operatingStartMinutes);
        float end   = dayBase + (useV ? variant.operatingEndMinutes   : route.operatingEndMinutes);
        float hwA   = useV ? variant.headwayFromAMinutes : route.headwayFromAMinutes;
        float hwZ   = useV ? variant.headwayFromZMinutes : route.headwayFromZMinutes;

        for (float t = start; t < end; t += hwA) emit(route.routeNumber, vLetter, true, t, dayOffset);
        for (float t = start; t < end; t += hwZ) emit(route.routeNumber, vLetter, false, t, dayOffset);
    }

    private void GenerateFromWindows(List<ScheduleWindow> windows, string routeNumber, string vLetter, int dayOffset, float dayBase, Action<string, string, bool, float, int> emit)
    {
        float lastOut = float.NegativeInfinity;
        float lastIn  = float.NegativeInfinity;

        foreach (var w in windows)
        {
            lastOut = GenerateWindowDirection(w.windowStartMinutes, w.windowEndMinutes, w.headwayFromAMinutes, routeNumber, vLetter, true,  dayOffset, dayBase, lastOut, emit);
            lastIn  = GenerateWindowDirection(w.windowStartMinutes, w.windowEndMinutes, w.headwayFromZMinutes, routeNumber, vLetter, false, dayOffset, dayBase, lastIn,  emit);
        }
    }

    private float GenerateWindowDirection(float winStart, float winEnd, float headway, string routeNumber, string vLetter, bool outbound, int dayOffset, float dayBase, float lastEmitted, Action<string, string, bool, float, int> emit)
    {
        if (headway <= 0f) return lastEmitted;

        float first = Mathf.Ceil(winStart / headway) * headway;
        for (float t = first; t <= winEnd; t += headway)
        {
            float absolute = dayBase + t;
            if (absolute <= lastEmitted) continue;
            emit(routeNumber, vLetter, outbound, absolute, dayOffset);
            lastEmitted = absolute;
        }
        return lastEmitted;
    }
}