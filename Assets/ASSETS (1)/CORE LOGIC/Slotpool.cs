using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns the raw slot data: the master list, the free queues, and the
/// busID → active-slot lookup. Knows nothing about policy (caps, denial
/// chance, vehicle rules), the game clock, or who's a player vs NPC.
/// Everything here is mechanical: add, dequeue, commit, release.
///
/// Note: the old BusScheduler tracked a `_busesPerRoute` dictionary that
/// was updated everywhere but never actually read by any cap/query logic
/// (GetRouteCap reads route.maxBusesAllowed; CountActiveBusesOnRoute scans
/// _slotByBus fresh each call). It's dropped here as dead weight.
/// </summary>
public class SlotPool
{
    /// <summary>Fired whenever a slot is committed to a busID — the facade
    /// wires this to BusManager.ReleaseBusReservation so SlotPool never
    /// needs to know BusManager exists.</summary>
    public event Action<int> OnSlotCommitted;

    private readonly List<TimetableSlot> _allSlots = new();
    private readonly Dictionary<int, TimetableSlot> _slotByBus = new();
    private readonly Dictionary<(string route, string variant, bool outbound), LinkedList<TimetableSlot>> _freeQueues = new();

    public IReadOnlyList<TimetableSlot> AllSlots => _allSlots;
    public IReadOnlyDictionary<int, TimetableSlot> SlotByBusView => _slotByBus;

    private static (string, string, bool) Key(TimetableSlot s) => (s.routeNumber, s.variantLetter ?? "", s.isOutbound);
    private static (string, string, bool) Key(string r, string v, bool o) => (r, v ?? "", o);

    // ── Building ─────────────────────────────────────────────────────────
    public void AddNewSlot(TimetableSlot slot)
    {
        _allSlots.Add(slot);
        EnqueueFree(slot);
    }

    public void SortAll() => _allSlots.Sort((a, b) => a.scheduledDeparture.CompareTo(b.scheduledDeparture));

    public void ClearAll()
    {
        _allSlots.Clear();
        _freeQueues.Clear();
        // _slotByBus deliberately NOT cleared — RebuildSchedule needs live
        // assignments to survive a timetable regen; facade handles that
        // via ExtractKeepable/ReattachAfterRebuild below.
    }

    /// <summary>Slots still InService/Assigned survive a schedule rebuild.
    /// Everything else (Unassigned/Completed) is regenerated fresh.</summary>
    public List<TimetableSlot> ExtractKeepable()
    {
        var keep = new List<TimetableSlot>();
        foreach (var s in _allSlots)
            if (s.state == SlotState.InService || s.state == SlotState.AssignedNPC || s.state == SlotState.AssignedPlayer)
                keep.Add(s);
        return keep;
    }

    public void ReattachAfterRebuild(List<TimetableSlot> kept)
    {
        foreach (var s in kept)
        {
            _allSlots.Add(s);
            if (s.assignedBusID != -1) _slotByBus[s.assignedBusID] = s;
        }
    }

    // ── Free queue ───────────────────────────────────────────────────────
    public void EnqueueFree(TimetableSlot slot)
    {
        var key = Key(slot);
        if (!_freeQueues.TryGetValue(key, out var q)) { q = new LinkedList<TimetableSlot>(); _freeQueues[key] = q; }
        var node = q.Last;
        while (node != null && node.Value.scheduledDeparture > slot.scheduledDeparture) node = node.Previous;
        if (node == null) q.AddFirst(slot); else q.AddAfter(node, slot);
    }

public void RemoveFromFreeQueue(TimetableSlot slot)
{
    if (_freeQueues.TryGetValue(Key(slot), out var q)) q.Remove(slot);
}
    public int FreeCount(string route, string variant, bool outbound) =>
        _freeQueues.TryGetValue(Key(route, variant, outbound), out var q) ? q.Count : 0;

    /// <summary>Random-among-candidates dequeue (the old [G2] behavior).
    /// windowSize=1 reproduces fully deterministic "always earliest".</summary>
    public TimetableSlot DequeueRandomAmong(string route, string variant, bool outbound, float notBefore, float gameTimeMinutes, int windowSize)
    {
        var key = Key(route, variant, outbound);
        if (!_freeQueues.TryGetValue(key, out var q)) return null;

while (q.Count > 0 &&
       q.First.Value.scheduledDeparture <= gameTimeMinutes)
{
    q.RemoveFirst();
}
        var candidates = new List<LinkedListNode<TimetableSlot>>();
        var node = q.First;
        while (node != null && candidates.Count < windowSize)
        {
            if (node.Value.scheduledDeparture >= notBefore) candidates.Add(node);
            node = node.Next;
        }
        if (candidates.Count == 0) return null;

        var chosen = candidates[UnityEngine.Random.Range(0, candidates.Count)];
        q.Remove(chosen);
        return chosen.Value;
    }
/// <summary>Finds a slot that is currently mid-trip (departed in the past,
/// but still within tripMinutes of now) without triggering the destructive
/// stale-cleanup that DequeueRandomAmong/DequeueStrictEarliest perform on
/// their queue head. Used for reload/bootstrap resume, where the slot we
/// want IS the "stale" one those methods are designed to throw away.</summary>
public TimetableSlot TryClaimLiveSlot(string route, string variant, bool outbound, float now, float tripMinutes)
{
    var key = Key(route, variant ?? "", outbound);
    if (!_freeQueues.TryGetValue(key, out var q))
    {
        Debug.Log($"[SlotPool] TryClaimLiveSlot: no free queue at all for {route} outbound={outbound}");
        return null;
    }

    if (q.Count == 0)
        Debug.Log($"[SlotPool] TryClaimLiveSlot: queue empty for {route} outbound={outbound}");

    foreach (var slot in q)
    {
        if (slot.scheduledDeparture > now)
        {
            Debug.Log($"[SlotPool] TryClaimLiveSlot: earliest queued dep {slot.scheduledDeparture:0.0} is still in future (now={now:0.0}) — nothing live for {route} outbound={outbound}");
            break;
        }
        float elapsed = now - slot.scheduledDeparture;
        if (elapsed < tripMinutes) { RemoveFromFreeQueue(slot); return slot; }
        Debug.Log($"[SlotPool] TryClaimLiveSlot: slot dep={slot.scheduledDeparture:0.0} elapsed={elapsed:0.0} >= tripMinutes={tripMinutes:0.0} — too old, not live");
    }
    return null;
}
    /// <summary>Deterministic soonest-eligible dequeue — used for pipeline
    /// continuation, where headway integrity matters more than variety.</summary>
    public TimetableSlot DequeueStrictEarliest(string route, string variant, bool outbound, float notBefore, float gameTimeMinutes)
    {
        var key = Key(route, variant, outbound);
        if (!_freeQueues.TryGetValue(key, out var q)) return null;

        while (q.Count > 0 && q.First.Value.scheduledDeparture <= gameTimeMinutes) q.RemoveFirst();

        var node = q.First;
        while (node != null)
        {
            if (node.Value.scheduledDeparture >= notBefore) { q.Remove(node); return node.Value; }
            node = node.Next;
        }
        return null;
    }

    public List<TimetableSlot> PeekUpcoming(string route, string variant, bool outbound, float gameTimeMinutes, int count)
    {
        var result = new List<TimetableSlot>();
        if (!_freeQueues.TryGetValue(Key(route, variant, outbound), out var q)) return result;
        foreach (var s in q)
        {
            if (s.scheduledDeparture <= gameTimeMinutes) continue;
            result.Add(s);
            if (result.Count >= count) break;
        }
        return result;
    }

    public TimetableSlot GetNextUnassigned(string route, string variant, float gameTimeMinutes)
    {
        foreach (bool dir in new[] { true, false })
            if (_freeQueues.TryGetValue(Key(route, variant, dir), out var q))
                foreach (var s in q)
                    if (s.scheduledDeparture > gameTimeMinutes) return s;
        return null;
    }

    // ── Ownership ────────────────────────────────────────────────────────
    public bool TryGetAssignedSlot(int busID, out TimetableSlot slot) => _slotByBus.TryGetValue(busID, out slot);

    public int CountActiveOnRoute(string route)
    {
        int c = 0;
        foreach (var kv in _slotByBus) if (kv.Value.routeNumber == route) c++;
        return c;
    }

    /// <summary>Standard commit path — hard-blocks stealing a slot that
    /// already belongs to a *different* busID (the [G1] fix).</summary>
public bool CommitAssignment(TimetableSlot slot, int busID, SlotState state)
{
        if (slot.assignedBusID != -1 && slot.assignedBusID != busID)
        {
            Debug.LogWarning($"[SlotPool] CRITICAL: Slot {slot.FullRouteLabel} already has Bus#{slot.assignedBusID}.");
            return false;
        }
        slot.assignedBusID = busID;
        slot.state = state;
        _slotByBus[busID] = slot;
        OnSlotCommitted?.Invoke(busID);
        return true;
    }

    /// <summary>Attach without the hard-block check — only used by
    /// HandoffCoordinator after it has already detached the old owner,
    /// where "no prior claim" is guaranteed rather than checked.</summary>
    public void ForceAttach(int busID, TimetableSlot slot)
    {
        _slotByBus[busID] = slot;
        OnSlotCommitted?.Invoke(busID);
    }

    public void UnassignFromBus(int busID) => _slotByBus.Remove(busID);

    public void ReleaseToFree(TimetableSlot slot)
    {
        slot.assignedBusID = -1;
        slot.state = SlotState.Unassigned;
        EnqueueFree(slot);
    }

    public void CompleteAndDetach(TimetableSlot slot, int busID)
    {
        slot.state = SlotState.Completed;
        slot.assignedBusID = -1;
        _slotByBus.Remove(busID);
    }
}