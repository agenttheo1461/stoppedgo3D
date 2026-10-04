using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusScheduler — Route Manager operations.
//  Every change the manager makes to the timetable goes through here, so it
//  uses the scheduler's own rules and bookkeeping instead of a copy of them.
//
//    · A "parked" bus (idle / waiting at the garage) can be given a trip; the
//      scheduler fills in its later round trips by itself (TopUpChain).
//    · A bus that is already on a trip is never pulled off it. Only trips that
//      have not started yet (state AssignedNPC) can be freed or swapped.
//    · Everything the manager places is marked Fixed (ManagerLocks), so the
//      automatic scheduler leaves it alone.
// ═══════════════════════════════════════════════════════════════════════════════
public partial class BusScheduler
{
    /// <summary>The scheduler's own rule check (vehicle policy + route limit) for a bus on a trip.</summary>
    public bool ManagerCanAssign(int busID, BusRouteData route, string variantLetter, float minuteOfDay, out string reason)
    {
        var variant = !string.IsNullOrEmpty(variantLetter) && route != null ? route.GetVariant(variantLetter) : null;
        return CanAssign(busID, route, variant, out reason, minuteOfDay);
    }

    /// <summary>Time ranges (absolute game minutes) each bus is already committed to: trips it is running or has waiting.</summary>
    public Dictionary<int, List<Vector2>> ManagerBusyIndex()
    {
        var d = new Dictionary<int, List<Vector2>>();
        for (int i = 0; i < _allSlots.Count; i++)
        {
            var s = _allSlots[i];
            if (s.assignedBusID < 0) continue;
            if (s.state != SlotState.AssignedNPC && s.state != SlotState.InService && s.state != SlotState.AssignedPlayer) continue;
            if (!d.TryGetValue(s.assignedBusID, out var list)) { list = new List<Vector2>(4); d[s.assignedBusID] = list; }
            list.Add(new Vector2(s.scheduledDeparture, s.scheduledDeparture + TripMinutesForSlot(s)));
        }
        return d;
    }

    /// <summary>True when the bus has nothing at all scheduled between `start` and `end`.</summary>
    public static bool ManagerBusFreeBetween(Dictionary<int, List<Vector2>> index, int busID, float start, float end, out string clash)
    {
        clash = null;
        if (index == null || !index.TryGetValue(busID, out var list)) return true;
        foreach (var v in list)
            if (start < v.y && v.x < end)
            { clash = $"It already has a trip at {ManagerWords.Clock(v.x % 1440f)}."; return false; }
        return true;
    }

    /// <summary>How long a bus started on this trip stays committed: the round trips the scheduler chains onto it.</summary>
    public float ManagerChainWindow(TimetableSlot trip)
    {
        float t = TripMinutesForSlot(trip);
        return MaxPrechainedLegsFor(t) * (t + minLayoverMinutes);
    }

    /// <summary>True only if the bus is on a trip right now (or was just staged for one).</summary>
    public bool ManagerBusOnTripNow(int busID) =>
        _slotByBus.TryGetValue(busID, out var cur) && cur != null && cur.state != SlotState.Completed;

    /// <summary>True if the bus is on a trip OR already has any trip waiting for it (the timetable pre-assigns
    /// whole days of trips to buses that are not in _slotByBus yet). Giving such a bus another trip would
    /// double-book it and make the scheduler throw its earlier trips away.</summary>
    public bool ManagerBusHasTrip(int busID)
    {
        if (_slotByBus.ContainsKey(busID)) return true;
        for (int i = 0; i < _allSlots.Count; i++)
        {
            var s = _allSlots[i];
            if (s.assignedBusID == busID && (s.state == SlotState.AssignedNPC || s.state == SlotState.InService || s.state == SlotState.AssignedPlayer))
                return true;
        }
        return false;
    }

    /// <summary>Every bus that is on a trip or has one waiting — computed in one pass, for lists of many buses.</summary>
    public HashSet<int> ManagerBusyBuses()
    {
        var set = new HashSet<int>(_slotByBus.Keys);
        for (int i = 0; i < _allSlots.Count; i++)
        {
            var s = _allSlots[i];
            if (s.assignedBusID >= 0 && (s.state == SlotState.AssignedNPC || s.state == SlotState.InService || s.state == SlotState.AssignedPlayer))
                set.Add(s.assignedBusID);
        }
        return set;
    }

    /// <summary>Every trip on this route and day, in time order.</summary>
    public List<TimetableSlot> ManagerTripsFor(string routeNumber, int dayNumber)
    {
        var list = new List<TimetableSlot>();
        for (int i = 0; i < _allSlots.Count; i++)
        {
            var s = _allSlots[i];
            if (s.routeNumber != routeNumber || s.dayNumber != dayNumber) continue;
            if (s.state == SlotState.Completed && s.assignedBusID < 0) continue;
            list.Add(s);
        }
        list.Sort((a, b) => a.scheduledDeparture.CompareTo(b.scheduledDeparture));
        return list;
    }

    /// <summary>Give an empty trip to a parked bus. Later round trips fill in by themselves.</summary>
    public bool ManagerAssignTrip(TimetableSlot slot, int busID, out string reason)
    {
        reason = null;
        if (slot == null) { reason = "That trip isn't there any more."; return false; }
        if (slot.state != SlotState.Unassigned)
        { reason = "That trip already has a bus. Take its bus off first."; return false; }
        if (slot.scheduledDeparture < SimClock.Instance.AbsoluteGameMinutes - 1f)
        { reason = "That trip has already left."; return false; }

        // Free for the whole stretch this trip and its round trips will take? A bus with trips lined up
        // LATER in the day (the timetable gives buses whole days) is still free now.
        if (ManagerBusOnTripNow(busID) && _slotByBus[busID].state != SlotState.AssignedNPC)
        { reason = "That bus is on a trip right now. Pick a parked bus, or wait until it's done."; return false; }
        if (!ManagerBusFreeBetween(ManagerBusyIndex(), busID, slot.scheduledDeparture - 1f, slot.scheduledDeparture + ManagerChainWindow(slot), out string clash))
        { reason = "That bus isn't free then. " + clash; return false; }

        var route = GetRouteData(slot.routeNumber);
        if (!ManagerCanAssign(busID, route, slot.variantLetter, slot.scheduledDeparture % 1440f, out string why))
        {
            reason = why == "route at cap" ? $"Route {slot.routeNumber} is full." :
                     why == "vehicle policy" ? $"Route {slot.routeNumber} doesn't take that bus." :
                     why ?? "The scheduler said no.";
            return false;
        }

        if (!CommitAssignment(slot, busID, SlotState.AssignedNPC)) { reason = "That trip was just taken."; return false; }
        TopUpChain(busID, slot);

        ManagerLocks.LockBus(busID);
        MarkChainFixed(busID, slot.routeNumber, slot.scheduledDeparture);
        // The manager keeps its own saved plan (ManagerPlan); the scheduler's day files are left alone.
        return true;
    }

    private void MarkChainFixed(int busID, string routeNumber, float fromDeparture)
    {
        for (int i = 0; i < _allSlots.Count; i++)
        {
            var s = _allSlots[i];
            if (s.assignedBusID == busID && s.routeNumber == routeNumber && s.scheduledDeparture >= fromDeparture
                && s.state == SlotState.AssignedNPC)
                ManagerLocks.LockSlot(s);
        }
    }

    /// <summary>The trips a bus still has to start (not on the road yet), in time order.</summary>
    public List<TimetableSlot> ManagerFutureTripsOf(int busID)
    {
        var list = new List<TimetableSlot>();
        for (int i = 0; i < _allSlots.Count; i++)
        {
            var s = _allSlots[i];
            if (s.assignedBusID == busID && s.state == SlotState.AssignedNPC) list.Add(s);
        }
        list.Sort((a, b) => a.scheduledDeparture.CompareTo(b.scheduledDeparture));
        return list;
    }

    /// <summary>Take a bus off a trip that hasn't started, and off the round trips after it on that route.</summary>
    public int ManagerFreeTrip(TimetableSlot slot, out string reason)
    {
        reason = null;
        if (slot == null) { reason = "That trip isn't there any more."; return 0; }
        if (slot.state == SlotState.InService || slot.state == SlotState.AssignedPlayer)
        { reason = "That bus is already on this trip."; return 0; }
        if (slot.state != SlotState.AssignedNPC) { reason = "That trip has no bus."; return 0; }

        int busID = slot.assignedBusID;
        int freed = 0;
        for (int i = 0; i < _allSlots.Count; i++)
        {
            var s = _allSlots[i];
            if (s.assignedBusID != busID || s.state != SlotState.AssignedNPC) continue;
            if (s.routeNumber != slot.routeNumber || s.scheduledDeparture < slot.scheduledDeparture) continue;
            ManagerLocks.UnlockSlot(s);
            s.assignedBusID = -1;
            s.state = SlotState.Unassigned;
            s.chainLegIndex = 0;
            freed++;
        }
        if (_slotByBus.TryGetValue(busID, out var cur) && cur != null && cur.state == SlotState.Unassigned)
            _slotByBus.Remove(busID);
        if (ManagerFutureTripsOf(busID).Count == 0 && !_slotByBus.ContainsKey(busID)) ManagerLocks.UnlockBus(busID);
        return freed;
    }

    /// <summary>Swap the trips two buses still have to start. Both must be allowed on the other's trips.</summary>
    public bool ManagerSwapBuses(int busA, int busB, out string reason)
    {
        reason = null;
        if (busA == busB) { reason = "Pick two different buses."; return false; }

        var aTrips = ManagerFutureTripsOf(busA);
        var bTrips = ManagerFutureTripsOf(busB);
        if (aTrips.Count == 0 && bTrips.Count == 0) { reason = "Neither bus has a trip left to swap."; return false; }

        int fleetA = ResolveFleetNumber(busA), fleetB = ResolveFleetNumber(busB);
        foreach (var s in aTrips)
            if (!RouteRules.Check(fleetB, GetRouteData(s.routeNumber), s.variantLetter, s.scheduledDeparture % 1440f, out reason, false)) return false;
        foreach (var s in bTrips)
            if (!RouteRules.Check(fleetA, GetRouteData(s.routeNumber), s.variantLetter, s.scheduledDeparture % 1440f, out reason, false)) return false;

        // Buses that have not started their first trip are tracked by _slotByBus too; move that pointer with the trip.
        _slotByBus.TryGetValue(busA, out var curA);
        _slotByBus.TryGetValue(busB, out var curB);

        foreach (var s in aTrips) { s.assignedBusID = busB; }
        foreach (var s in bTrips) { s.assignedBusID = busA; }

        if (curA != null && curA.state == SlotState.AssignedNPC) _slotByBus.Remove(busA);
        if (curB != null && curB.state == SlotState.AssignedNPC) _slotByBus.Remove(busB);
        if (curA != null && curA.state == SlotState.AssignedNPC) _slotByBus[busB] = curA;
        if (curB != null && curB.state == SlotState.AssignedNPC) _slotByBus[busA] = curB;

        ManagerLocks.LockBus(busA); ManagerLocks.LockBus(busB);
        foreach (var s in aTrips.Concat(bTrips)) ManagerLocks.LockSlot(s);
        return true;
    }

    /// <summary>A broken bus's remaining trips go to a replacement (same rules as any trip, except the route limit).</summary>
    public bool ManagerReplaceBus(int brokenBusID, int replacementID, out string reason)
    {
        reason = null;
        if (!_slotByBus.TryGetValue(brokenBusID, out var slot) || slot == null)
        { reason = "That bus has no trip to hand over."; return false; }
        if (_slotByBus.TryGetValue(replacementID, out var repSlot) && repSlot != null && repSlot.state != SlotState.Completed)
        { reason = "The replacement is already on a trip."; return false; }

        int rep = ResolveFleetNumber(replacementID);
        var route = GetRouteData(slot.routeNumber);
        if (!RouteRules.Check(rep, route, slot.variantLetter, slot.scheduledDeparture % 1440f, out reason, false)) return false;

        var moved = TransferSlotToBus(brokenBusID, replacementID);
        if (moved == null) { reason = "The scheduler couldn't hand the trips over."; return false; }

        BusManager.Instance?.ClaimBusForHandoff(replacementID);
        ManagerLocks.LockBus(replacementID);
        MarkChainFixed(replacementID, slot.routeNumber, slot.scheduledDeparture);
        return true;
    }

    /// <summary>Send a bus that is waiting for its trip off right now instead of at the timetable time.</summary>
    public bool ManagerDepartNow(TimetableSlot slot, out string reason)
    {
        reason = null;
        if (slot == null || slot.state != SlotState.AssignedNPC || slot.assignedBusID < 0)
        { reason = "That trip has no bus waiting to start."; return false; }

        int busID = slot.assignedBusID;
        var ctrl = BusManager.Instance?.GetRecord(busID)?.controller;
        if (ctrl == null) { reason = "That bus isn't there."; return false; }
        if (BusBreakdownSystem.Instance != null && BusBreakdownSystem.Instance.IsBusLocked(busID))
        { reason = "That bus is broken down."; return false; }
        if (slot.scheduledDeparture - SimClock.Instance.AbsoluteGameMinutes > 120f)
        { reason = "That trip is more than 2 hours away. Wait until it's closer."; return false; }
        if (_slotByBus.TryGetValue(busID, out var cur) && cur != null && cur != slot && cur.state != SlotState.Completed)
        { reason = "That bus is still on another trip."; return false; }

        // Not yet at the start of its trip? Bring it there the same way the timetable does a little before departure.
        bool attached = _slotByBus.TryGetValue(busID, out var attachedSlot) && attachedSlot == slot;
        if (!attached) AttachToBus(slot, busID, SlotState.AssignedNPC);
        if (_earlyStaged.Add(slot)) OnDispatchBus?.Invoke(slot);

        ctrl.ManagerDepartNow = true;
        return true;
    }

    /// <summary>The nearest empty trip on this route and day, starting at the given minute of the day.</summary>
    public TimetableSlot ManagerFindEmptyTrip(string routeNumber, string variantLetter, bool outbound, int dayNumber, int startMinute, int toleranceMinutes = 3)
    {
        TimetableSlot best = null; float bestDelta = float.MaxValue;
        for (int i = 0; i < _allSlots.Count; i++)
        {
            var s = _allSlots[i];
            if (s.routeNumber != routeNumber || s.dayNumber != dayNumber || s.isOutbound != outbound) continue;
            if ((s.variantLetter ?? "") != (variantLetter ?? "")) continue;
            if (s.state != SlotState.Unassigned) continue;
            float delta = Mathf.Abs((s.scheduledDeparture % 1440f) - startMinute);
            if (delta < bestDelta) { bestDelta = delta; best = s; }
        }
        return bestDelta <= toleranceMinutes ? best : null;
    }

}
