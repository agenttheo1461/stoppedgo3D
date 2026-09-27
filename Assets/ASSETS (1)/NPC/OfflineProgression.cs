/*
 OfflineProgression.cs
 Computes where a bus SHOULD be positioned given how much time has
 elapsed since a slot's scheduled departure, without simulating the
 elapsed time frame-by-frame. Used whenever a bus is dispatched into a
 slot that's already in progress — scene load, session reload, or any
 dispatch where "now" is later than "scheduledDeparture".
*/
using UnityEngine;
using System.Collections.Generic;

public static class OfflineProgression
{
    /// <summary>Given a trip's total stop sequence and how many minutes
    /// have elapsed since departure, returns which stop index the bus
    /// should currently be approaching, and a 0-1 fraction of progress
    /// through the CURRENT leg between stops (for segment/T placement).
    /// Assumes roughly even time distribution between stops — good enough
    /// for "looks right on load," not meant to be perfectly precise.</summary>
    public static (int stopIndex, float legProgress) ResolveStopProgress(
        List<BusStopData> stops, float tripMinutes, float elapsedMinutes)
    {
        if (stops == null || stops.Count == 0 || tripMinutes <= 0f)
            return (0, 0f);

        float clampedElapsed = Mathf.Clamp(elapsedMinutes, 0f, tripMinutes);
        float overallFraction = clampedElapsed / tripMinutes;

        float stopSpacing = tripMinutes / stops.Count;
        int stopIndex = Mathf.Clamp(Mathf.FloorToInt(clampedElapsed / stopSpacing), 0, stops.Count - 1);
        float intoThisLeg = clampedElapsed - (stopIndex * stopSpacing);
        float legProgress = Mathf.Clamp01(intoThisLeg / Mathf.Max(0.01f, stopSpacing));

        return (stopIndex, legProgress);
    }

    /// <summary>Maps a 0-1 overall trip fraction onto (segmentIndex, T)
    /// within a bus's built segment list — the actual physical placement
    /// FollowRoute reads every frame.</summary>
    public static (int segmentIdx, float segmentT) ResolveSegmentProgress(
        List<IRouteSegment> segments, float overallFraction)
    {
        if (segments == null || segments.Count == 0) return (0, 0f);

        float totalLength = 0f;
        foreach (var seg in segments) totalLength += seg.Length;
        if (totalLength <= 0f) return (0, 0f);

        float targetDistance = Mathf.Clamp01(overallFraction) * totalLength;
        float accumulated = 0f;

        for (int i = 0; i < segments.Count; i++)
        {
            float segLen = Mathf.Max(0.01f, segments[i].Length);
            if (accumulated + segLen >= targetDistance)
            {
                float t = (targetDistance - accumulated) / segLen;
                return (i, Mathf.Clamp01(t));
            }
            accumulated += segLen;
        }
        return (segments.Count - 1, 1f);
    }
}