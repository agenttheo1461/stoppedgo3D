using System;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  MANAGER ACTIONS — the things you can do, each one checked, saved and undoable.
//  Every action returns a plain sentence saying what happened (or why it didn't).
// ═══════════════════════════════════════════════════════════════════════════════
public struct ActionResult
{
    public bool ok;
    public string message;
    public static ActionResult Ok(string m)   => new ActionResult { ok = true,  message = m };
    public static ActionResult Fail(string m) => new ActionResult { ok = false, message = m };
}

public static class ManagerActions
{
    private class UndoStep { public string label; public Func<string> undo; }
    private static readonly List<UndoStep> _undo = new List<UndoStep>();
    private const int UndoLimit = 30;

    public static bool CanUndo => _undo.Count > 0;
    public static string NextUndoLabel => _undo.Count > 0 ? _undo[_undo.Count - 1].label : "";

    private static void Push(string label, Func<string> undo)
    {
        _undo.Add(new UndoStep { label = label, undo = undo });
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
    }

    public static ActionResult Undo()
    {
        if (_undo.Count == 0) return ActionResult.Fail("Nothing to undo.");
        var step = _undo[_undo.Count - 1];
        _undo.RemoveAt(_undo.Count - 1);
        string r = step.undo?.Invoke();
        return ActionResult.Ok(r ?? ("Undid: " + step.label));
    }

    public static void ClearUndo() => _undo.Clear();

    private static int FleetOf(int busID) => BusManager.Instance?.GetRecord(busID)?.controller?.fleetNumber ?? -1;

    // ── 1. Put a parked bus on a trip ────────────────────────────────────────
    public static ActionResult AssignBus(int busID, TimetableSlot trip)
    {
        var sched = BusScheduler.Instance;
        if (sched == null || trip == null) return ActionResult.Fail("The game isn't ready yet.");
        int fleet = FleetOf(busID);
        var route = sched.GetRouteData(trip.routeNumber);

        if (!RouteRules.Check(fleet, route, trip.variantLetter, trip.scheduledDeparture % 1440f, out string reason))
            return ActionResult.Fail(reason);
        if (!sched.ManagerAssignTrip(trip, busID, out reason))
            return ActionResult.Fail(reason);

        ManagerPlan.Add(ManagerPlan.FromSlot(trip, fleet));
        var tripRef = trip;
        Push($"Put bus {fleet} on route {trip.routeNumber}", () =>
        {
            sched.ManagerFreeTrip(tripRef, out _);
            ManagerPlan.RemoveTrip(tripRef.dayNumber, tripRef.routeNumber, tripRef.variantLetter, tripRef.isOutbound, Mathf.RoundToInt(tripRef.scheduledDeparture % 1440f));
            return $"Removed bus {fleet} from route {tripRef.routeNumber}.";
        });
        return ActionResult.Ok($"Bus {fleet} will start route {trip.routeNumber} ({ManagerWords.Direction(trip.isOutbound)}) at {ManagerWords.Clock(trip.scheduledDeparture % 1440f)}.");
    }

    // ── 2. Remove a bus from a trip that hasn't started ──────────────────────
    public static ActionResult FreeTrip(TimetableSlot trip)
    {
        var sched = BusScheduler.Instance;
        if (sched == null || trip == null) return ActionResult.Fail("The game isn't ready yet.");
        int busID = trip.assignedBusID;
        int fleet = FleetOf(busID);
        float dep = trip.scheduledDeparture;
        var snapshot = new List<TimetableSlot>();
        foreach (var s in sched.ManagerFutureTripsOf(busID))
            if (s.routeNumber == trip.routeNumber && s.scheduledDeparture >= trip.scheduledDeparture) snapshot.Add(s);

        int freed = sched.ManagerFreeTrip(trip, out string reason);
        if (freed == 0) return ActionResult.Fail(reason ?? "Nothing was freed.");

        foreach (var s in snapshot)
            ManagerPlan.RemoveTrip(s.dayNumber, s.routeNumber, s.variantLetter, s.isOutbound, Mathf.RoundToInt(s.scheduledDeparture % 1440f));

        var first = snapshot.Count > 0 ? snapshot[0] : trip;
        Push($"Removed bus {fleet} from route {trip.routeNumber}", () =>
        {
            if (first.state != SlotState.Unassigned) return "That trip has changed, so it can't be put back.";
            if (!sched.ManagerAssignTrip(first, busID, out string why)) return "Couldn't put it back: " + why;
            ManagerPlan.Add(ManagerPlan.FromSlot(first, fleet));
            return $"Put bus {fleet} back on route {first.routeNumber}.";
        });
        return ActionResult.Ok($"Bus {fleet} is removed from route {trip.routeNumber} from {ManagerWords.Clock(dep % 1440f)} on.");
    }

    // ── 2b. Depart now ───────────────────────────────────────────────────────
    public static ActionResult DepartNow(TimetableSlot trip)
    {
        var sched = BusScheduler.Instance;
        if (sched == null || trip == null) return ActionResult.Fail("The game isn't ready yet.");
        int fleet = FleetOf(trip.assignedBusID);
        if (!sched.ManagerDepartNow(trip, out string reason)) return ActionResult.Fail(reason);
        return ActionResult.Ok($"Bus {fleet} is leaving now on route {trip.routeNumber} ({ManagerWords.Direction(trip.isOutbound)}), ahead of its {ManagerWords.Clock(trip.scheduledDeparture % 1440f)} time.");
    }

    // ── 3. Swap two buses ────────────────────────────────────────────────────
    public static ActionResult SwapBuses(int busA, int busB)
    {
        var sched = BusScheduler.Instance;
        if (sched == null) return ActionResult.Fail("The game isn't ready yet.");
        int fa = FleetOf(busA), fb = FleetOf(busB);

        // Remember the trips so the plan file can follow the swap.
        var aTrips = sched.ManagerFutureTripsOf(busA);
        var bTrips = sched.ManagerFutureTripsOf(busB);

        if (!sched.ManagerSwapBuses(busA, busB, out string reason)) return ActionResult.Fail(reason);

        foreach (var s in aTrips) { ManagerPlan.RemoveTrip(s.dayNumber, s.routeNumber, s.variantLetter, s.isOutbound, Mathf.RoundToInt(s.scheduledDeparture % 1440f)); }
        foreach (var s in bTrips) { ManagerPlan.RemoveTrip(s.dayNumber, s.routeNumber, s.variantLetter, s.isOutbound, Mathf.RoundToInt(s.scheduledDeparture % 1440f)); }
        foreach (var s in aTrips) FirstOnly(s, fb);
        foreach (var s in bTrips) FirstOnly(s, fa);

        Push($"Swapped buses {fa} and {fb}", () =>
        {
            if (!sched.ManagerSwapBuses(busA, busB, out string why)) return "Couldn't swap them back: " + why;
            return $"Swapped {fa} and {fb} back.";
        });
        return ActionResult.Ok($"Buses {fa} and {fb} swapped their upcoming trips.");
    }

    // Only the first trip of a chain goes in the plan file (the rest are chained by the scheduler).
    private static void FirstOnly(TimetableSlot s, int newFleet)
    {
        if (s.chainLegIndex > 1) return;
        ManagerPlan.Add(ManagerPlan.FromSlot(s, newFleet));
    }

    // ── 4. Hold a bus ────────────────────────────────────────────────────────
    /// <summary>Hold the bus the next time it is waiting at a stop or the end of the line.</summary>
    public static ActionResult HoldAtNextStop(int busID)
    {
        var rec = BusManager.Instance?.GetRecord(busID);
        if (rec?.controller == null) return ActionResult.Fail("That bus isn't there.");
        int fleet = rec.controller.fleetNumber;
        rec.controller.ManagerHoldAtNextStop = true;
        ManagerLocks.LockBus(busID);
        Push($"Hold bus {fleet} at its next stop", () =>
        {
            var r = BusManager.Instance?.GetRecord(busID);
            if (r?.controller != null) { r.controller.ManagerHoldAtNextStop = false; r.controller.ManagerHold = false; }
            return $"Bus {fleet} won't be held.";
        });
        return ActionResult.Ok($"Bus {fleet} will be held at its next stop.");
    }

    public static ActionResult SetHold(int busID, bool hold)
    {
        var rec = BusManager.Instance?.GetRecord(busID);
        if (rec?.controller == null) return ActionResult.Fail("That bus isn't there.");
        if (!hold) rec.controller.ManagerHoldAtNextStop = false;
        if (rec.controller.ManagerHold == hold) return ActionResult.Ok(hold ? "Already holding that bus." : "That bus isn't being held.");
        rec.controller.ManagerHold = hold;
        if (hold) ManagerLocks.LockBus(busID);
        int fleet = rec.controller.fleetNumber;
        Push(hold ? $"Held bus {fleet}" : $"Let bus {fleet} go", () =>
        {
            var r = BusManager.Instance?.GetRecord(busID);
            if (r?.controller != null) r.controller.ManagerHold = !hold;
            return hold ? $"Bus {fleet} is moving again." : $"Bus {fleet} is held again.";
        });
        return ActionResult.Ok(hold ? $"Bus {fleet} is held where it is." : $"Bus {fleet} is moving again.");
    }

    // ── 5. Fixed / not fixed ─────────────────────────────────────────────────
    public static ActionResult SetFixed(int busID, bool fix)
    {
        int fleet = FleetOf(busID);
        if (fix) ManagerLocks.LockBus(busID); else ManagerLocks.UnlockBus(busID);
        var sched = BusScheduler.Instance;
        if (sched != null)
            foreach (var s in sched.ManagerFutureTripsOf(busID)) { if (fix) ManagerLocks.LockSlot(s); else ManagerLocks.UnlockSlot(s); }
        Push(fix ? $"Fixed bus {fleet}" : $"Unfixed bus {fleet}", () =>
        {
            SetFixed(busID, !fix);
            _undo.RemoveAt(_undo.Count - 1);
            return fix ? $"Bus {fleet} is no longer fixed." : $"Bus {fleet} is fixed again.";
        });
        return ActionResult.Ok(fix
            ? $"Bus {fleet} is fixed. The automatic scheduler won't move it."
            : $"Bus {fleet} is free for the automatic scheduler again.");
    }

    // ── 6. Replace a broken bus ──────────────────────────────────────────────
    public static ActionResult ReplaceBroken(int brokenBusID, int replacementID)
    {
        var sched = BusScheduler.Instance;
        if (sched == null) return ActionResult.Fail("The game isn't ready yet.");
        int fb = FleetOf(brokenBusID), fr = FleetOf(replacementID);
        if (!sched.ManagerReplaceBus(brokenBusID, replacementID, out string reason)) return ActionResult.Fail(reason);
        ClearUndoFor(brokenBusID);
        ManagerScore.Award(30, 60, "covered a broken bus");
        return ActionResult.Ok($"Bus {fr} is taking over from bus {fb}.");
    }

    // ── 7. Move a bus on the road to another route after its current trip ────
    private class PendingMove { public int busID, fleet; public TimetableSlot trip; }
    private static readonly List<PendingMove> _pending = new List<PendingMove>();
    public static event Action<string, bool> Info;

    public static bool HasPendingMove(int busID) => _pending.Exists(p => p.busID == busID);

    public static ActionResult MoveAfterTrip(int busID, TimetableSlot target)
    {
        var sched = BusScheduler.Instance;
        if (sched == null || target == null) return ActionResult.Fail("The game isn't ready yet.");
        if (!sched.TryGetAssignedSlot(busID, out var cur) || cur == null || cur.state != SlotState.InService)
            return ActionResult.Fail("That bus isn't on a trip. Use \"Put on a route\" for a parked bus.");
        if (target.state != SlotState.Unassigned) return ActionResult.Fail("That trip already has a bus.");

        int fleet = FleetOf(busID);
        var route = sched.GetRouteData(target.routeNumber);
        if (!RouteRules.Check(fleet, route, target.variantLetter, target.scheduledDeparture % 1440f, out string reason))
            return ActionResult.Fail(reason);

        // It has to finish its trip, drive to the garage and park first, so leave it plenty of time.
        float freeAt = cur.scheduledDeparture + sched.GetSlotTripMinutes(cur) + 25f;
        if (target.scheduledDeparture < freeAt)
            return ActionResult.Fail($"That trip starts too soon. This bus is busy until about {ManagerWords.Clock(freeAt % 1440f)}. Pick a later trip.");

        // Take it off the trips it was going to do next, and send it to the garage when this one ends.
        var future = sched.ManagerFutureTripsOf(busID);
        if (future.Count > 0) sched.ManagerFreeTrip(future[0], out _);
        ManagerLocks.LockBus(busID);
        ManagerLocks.RequestParkAfterTrip(busID);
        _pending.RemoveAll(p => p.busID == busID);
        _pending.Add(new PendingMove { busID = busID, fleet = fleet, trip = target });

        Push($"Move bus {fleet} to route {target.routeNumber} after its trip", () =>
        {
            _pending.RemoveAll(p => p.busID == busID);
            ManagerLocks.CancelParkAfterTrip(busID);
            return $"Bus {fleet} will carry on as before (its coming trips on the old route may need putting back).";
        });
        return ActionResult.Ok($"Bus {fleet} will finish its trip, park, then take route {target.routeNumber} at {ManagerWords.Clock(target.scheduledDeparture % 1440f)}.");
    }

    /// <summary>Called every second or so: gives waiting buses their new trip once they are parked.</summary>
    public static void Tick()
    {
        if (_pending.Count == 0 || BusScheduler.Instance == null || SimClock.Instance == null) return;
        float now = SimClock.Instance.AbsoluteGameMinutes;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (p.trip.state != SlotState.Unassigned)
            { _pending.RemoveAt(i); Info?.Invoke($"Bus {p.fleet}: the trip it was waiting for was taken by another bus.", false); continue; }
            if (p.trip.scheduledDeparture < now + 1f)
            { _pending.RemoveAt(i); Info?.Invoke($"Bus {p.fleet} didn't get parked in time for route {p.trip.routeNumber} at {ManagerWords.Clock(p.trip.scheduledDeparture % 1440f)}.", false); continue; }

            var rec = BusManager.Instance?.GetRecord(p.busID);
            if (rec == null || !ManagerPlan.IsParked(rec) || BusScheduler.Instance.ManagerBusOnTripNow(p.busID)) continue; // still on its way

            _pending.RemoveAt(i);
            if (BusScheduler.Instance.ManagerAssignTrip(p.trip, p.busID, out string why))
            {
                ManagerPlan.Add(ManagerPlan.FromSlot(p.trip, p.fleet));
                Info?.Invoke($"Bus {p.fleet} is parked and now starts route {p.trip.routeNumber} at {ManagerWords.Clock(p.trip.scheduledDeparture % 1440f)}.", true);
            }
            else Info?.Invoke($"Bus {p.fleet} couldn't take route {p.trip.routeNumber}: {why}", false);
        }
    }

    private static void ClearUndoFor(int busID) { /* a replacement can't be rolled back safely, so earlier steps stay as they are */ }
}
