using System;
using System.Collections.Generic;

/// <summary>Owns each bus's queued future legs. Knows nothing about
/// players/NPCs, route policy, or the clock beyond what's passed in —
/// just "keep N legs queued, dequeue on completion, release on
/// identity change."</summary>
public class ItineraryPipeline
{
    private readonly Dictionary<int, Queue<TimetableSlot>> _itineraries = new();

    public IEnumerable<TimetableSlot> GetSlots(int busID) =>
        _itineraries.TryGetValue(busID, out var q) ? q : Array.Empty<TimetableSlot>();

    public bool TryDequeue(int busID, out TimetableSlot slot)
    {
        slot = null;
        if (_itineraries.TryGetValue(busID, out var q) && q.Count > 0) { slot = q.Dequeue(); return true; }
        return false;
    }

    public void Clear(int busID) => _itineraries.Remove(busID);

    /// <summary>Releases every queued leg back to the pool's free queue
    /// instead of leaving them permanently stamped with a busID that no
    /// longer owns anything — used whenever identity changes mid-service
    /// (possession, relief, NPC handoff). This was the exact leak that
    /// caused slots to vanish from the schedule after a relief handoff.</summary>
    public void ReleaseAll(int busID, SlotPool pool)
    {
        if (!_itineraries.TryGetValue(busID, out var q)) return;
        foreach (var slot in q) pool.ReleaseToFree(slot);
        _itineraries.Remove(busID);
    }

    public void TopUp(int busID, string route, string variant, bool committedDir, float committedDep,
                       SlotPool pool, float gameTimeMinutes, float tripMinutes, bool isPlayer, int targetDepth = 2)
    {
        if (!_itineraries.TryGetValue(busID, out var q)) { q = new Queue<TimetableSlot>(); _itineraries[busID] = q; }

        bool lastDir = committedDir;
        float lastDep = committedDep;
        if (q.Count > 0)
        {
            var arr = q.ToArray();
            lastDir = arr[^1].isOutbound;
            lastDep = arr[^1].scheduledDeparture;
        }

        while (q.Count < targetDepth)
        {
            bool nextDir = !lastDir;
            float notBefore = lastDep + tripMinutes;
            var future = pool.DequeueStrictEarliest(route, variant, nextDir, notBefore, gameTimeMinutes);
            if (future == null) break;

            future.assignedBusID = busID;
            future.state = isPlayer ? SlotState.AssignedPlayer : SlotState.AssignedNPC;
            q.Enqueue(future);
            lastDir = future.isOutbound;
            lastDep = future.scheduledDeparture;
        }
    }
}