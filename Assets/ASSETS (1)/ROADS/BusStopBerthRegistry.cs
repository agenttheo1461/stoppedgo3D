using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS STOP BERTH REGISTRY
//
//  Nothing tracked "who's currently at this stop" before this — only depot
//  parking spots had occupancy (DepotParkingSpot.occupied). This gives bus
//  stops the same idea, but as a queue instead of fixed bays: the first bus
//  at a stop sits at the stop's real coordinate; each additional bus that
//  arrives while the stop's still occupied gets pushed 2.5m further along
//  the same perpendicular-to-road direction, stacking cleanly instead of
//  overlapping on the same point.
//
//  INTEGRATION: register a bus when it commits to a stop (NPCBusController's
//  StopDwell, right where State becomes AtStop), unregister when it leaves
//  (the same coroutine's tail, alongside _nextStopIdx++). Query
//  GetBerthPosition for the actual position to approach/park at.
// ═══════════════════════════════════════════════════════════════════════════════
public static class BusStopBerthRegistry
{
    private const float BerthSpacing = 2.5f;

    // stopCode -> ordered list of busIDs currently registered there.
    // Order matters: first-in gets the real stop position, each subsequent
    // bus gets pushed one more berth-spacing back.
    private static readonly Dictionary<string, List<int>> _busesAtStop = new();

    /// <summary>Call when a bus commits to a stop (e.g. entering AtStop).
    /// Safe to call multiple times for the same bus — it won't double-add.</summary>
    public static void Register(string stopCode, int busID)
    {
        if (string.IsNullOrEmpty(stopCode)) return;

        if (!_busesAtStop.TryGetValue(stopCode, out var list))
            _busesAtStop[stopCode] = list = new List<int>();

        if (!list.Contains(busID)) list.Add(busID);
    }

    /// <summary>Call when a bus leaves a stop (departing StopDwell).</summary>
    public static void Unregister(string stopCode, int busID)
    {
        if (string.IsNullOrEmpty(stopCode)) return;
        if (_busesAtStop.TryGetValue(stopCode, out var list))
        {
            list.Remove(busID);
            if (list.Count == 0) _busesAtStop.Remove(stopCode);
        }
    }

    /// <summary>How many buses are already at this stop AHEAD of the given
    /// bus (i.e. registered before it) — 0 if it's first-in or not yet
    /// registered. Used to compute how many berth-spacings to push back.</summary>
    public static int GetQueuePosition(string stopCode, int busID)
    {
        if (string.IsNullOrEmpty(stopCode)) return 0;
        if (!_busesAtStop.TryGetValue(stopCode, out var list)) return 0;

        int idx = list.IndexOf(busID);
        return idx < 0 ? list.Count : idx; // not registered yet -> assume it'll land at the back
    }

    /// <summary>The actual position this bus should approach/park at, given
    /// its queue position at this stop. First-in (queue position 0) gets the
    /// stop's real road-surface coordinate exactly; each bus behind that
    /// gets pushed BerthSpacing further along the road's own perpendicular
    /// (same "right" direction GetLanePosition uses), so a full stop queues
    /// cleanly along the curb instead of everyone stacking on one point.</summary>
    public static Vector3 GetBerthPosition(RoadSegment road, float t, string stopCode, int busID)
    {
        Vector3 basePos = road != null ? road.EvaluateSurfacePosition(t) : Vector3.zero;

        int queuePos = GetQueuePosition(stopCode, busID);
        if (queuePos <= 0 || road == null) return basePos;

        Vector3 tangent = road.EvaluateTangent(t);
        Vector3 right   = Vector3.Cross(Vector3.up, tangent).normalized;
        return basePos + right * (BerthSpacing * queuePos);
    }

    /// <summary>Clears all registrations — call on scene reload / new session,
    /// same idea as RouteUsageTracker's ResetAllUsage but for live occupancy
    /// rather than persisted stats (this is never saved, it's purely a live
    /// "who's here right now" list).</summary>
    public static void ClearAll() => _busesAtStop.Clear();
}
