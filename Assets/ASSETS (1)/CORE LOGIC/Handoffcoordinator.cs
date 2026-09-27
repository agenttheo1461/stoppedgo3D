using System;
using System.Collections.Generic;

/// <summary>
/// Every path that moves a slot's ownership from one busID to another
/// (player possession, relief handoff, NPC-to-NPC handoff) routes through
/// here. This is deliberate: every bug found in the old scheduler lived in
/// this exact seam — an orphaned pipeline not released, a reservation not
/// freed, a double ClaimBusForHandoff call. One coordinator, one audit
/// surface, instead of four half-duplicated versions scattered across
/// TransferSlotToPlayer / TransferSlotToBus / TryClaimNextReliefSlot... .
/// </summary>
public class HandoffCoordinator
{
    private readonly SlotPool _pool;
    private readonly ItineraryPipeline _pipeline;
    private readonly Func<string, float> _tripMinutesForRoute;
    private readonly Func<float> _gameTimeMinutes;

    public HandoffCoordinator(SlotPool pool, ItineraryPipeline pipeline, Func<string, float> tripMinutesForRoute, Func<float> gameTimeMinutes)
    {
        _pool = pool;
        _pipeline = pipeline;
        _tripMinutesForRoute = tripMinutesForRoute;
        _gameTimeMinutes = gameTimeMinutes;
    }

    /// <summary>Straight transfer of an already-known active slot (fast path —
    /// used when the caller has the busID and it's currently in _slotByBus).</summary>
    public TimetableSlot Transfer(int fromBusID, int toBusID, SlotState newState)
    {
        if (!_pool.TryGetAssignedSlot(fromBusID, out var slot)) return null;
        return DoTransfer(slot, fromBusID, toBusID, newState);
    }

    /// <summary>Fallback for when the fast lookup misses (stale/alternate ID) —
    /// scans all slots by assignedBusID instead.</summary>
    public TimetableSlot TransferByScan(int fromBusID, int toBusID, SlotState newState, IReadOnlyList<TimetableSlot> allSlots)
    {
        foreach (var candidate in allSlots)
        {
            if (candidate.assignedBusID != fromBusID) continue;
            if (candidate.state == SlotState.Unassigned) continue;
            return DoTransfer(candidate, fromBusID, toBusID, newState);
        }
        return null;
    }

    private TimetableSlot DoTransfer(TimetableSlot slot, int fromBusID, int toBusID, SlotState newState)
    {
        _pool.UnassignFromBus(fromBusID);
        _pipeline.ReleaseAll(fromBusID, _pool); // old owner's remaining legs → back to free pool, not leaked

        slot.assignedBusID = toBusID;
        slot.state = newState;
        _pool.ForceAttach(toBusID, slot); // old owner already detached above — no hard-block needed

        _pipeline.TopUp(toBusID, slot.routeNumber, slot.variantLetter, slot.isOutbound, slot.scheduledDeparture,
                         _pool, _gameTimeMinutes(), _tripMinutesForRoute(slot.routeNumber), newState == SlotState.AssignedPlayer);
        return slot;
    }

    /// <summary>Relief handoff: claims fromBusID's active-or-next-pipelined
    /// leg and hands it to toBusID, failing cleanly (no side effects) if
    /// toBusID turns out to already be busy.</summary>
    public bool TryReliefTransfer(int fromBusID, int toBusID, SlotState toState, out TimetableSlot resultSlot)
    {
        resultSlot = null;

        TimetableSlot claimed;
        if (_pool.TryGetAssignedSlot(fromBusID, out claimed)) { /* active slot */ }
        else if (_pipeline.TryDequeue(fromBusID, out claimed)) { /* next pipelined leg */ }
        else return false;

        if (_pool.TryGetAssignedSlot(toBusID, out _))
        {
            _pool.ReleaseToFree(claimed); // put it back exactly as found — no half-mutated state
            return false;
        }

        _pool.UnassignFromBus(fromBusID);
        claimed.assignedBusID = toBusID;
        claimed.state = toState;
        _pool.ForceAttach(toBusID, claimed);

        _pipeline.ReleaseAll(fromBusID, _pool);

        _pipeline.TopUp(toBusID, claimed.routeNumber, claimed.variantLetter, claimed.isOutbound, claimed.scheduledDeparture,
                         _pool, _gameTimeMinutes(), _tripMinutesForRoute(claimed.routeNumber), toState == SlotState.AssignedPlayer);

        resultSlot = claimed;
        return true;
    }
}