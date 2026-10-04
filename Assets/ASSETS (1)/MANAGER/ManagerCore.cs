using System;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTE MANAGER — shared basics: the "fixed" lists, plain wording, and the
//  plain-language route rules.
//
//  "Fixed" means the manager placed it by hand, so the automatic scheduler must
//  leave it alone:
//    · a fixed TRIP keeps its bus (BusScheduler.ReleaseChain skips it)
//    · a fixed BUS is never picked as a spare by the automatic idle-bus search
// ═══════════════════════════════════════════════════════════════════════════════
public static class ManagerLocks
{
    private static readonly HashSet<string> _slots = new HashSet<string>();
    private static readonly HashSet<int>    _buses = new HashSet<int>();

    public static string KeyOf(TimetableSlot s) =>
        s == null ? "" : $"{s.dayNumber}|{s.routeNumber}|{s.variantLetter ?? ""}|{(s.isOutbound ? 1 : 0)}|{Mathf.RoundToInt(s.scheduledDeparture)}";

    private static readonly List<TimetableSlot> _slotRefs = new List<TimetableSlot>();
    /// <summary>The trips the manager fixed, for scoring. Do not modify.</summary>
    public static List<TimetableSlot> FixedTrips => _slotRefs;

    public static bool IsSlotLocked(TimetableSlot s) => s != null && _slots.Contains(KeyOf(s));
    public static bool IsBusLocked(int busID)        => _buses.Contains(busID);

    public static void LockSlot(TimetableSlot s)   { if (s != null && _slots.Add(KeyOf(s))) _slotRefs.Add(s); }
    public static void UnlockSlot(TimetableSlot s) { if (s != null && _slots.Remove(KeyOf(s))) _slotRefs.Remove(s); }
    public static void LockBus(int busID)   { _buses.Add(busID); }
    public static void UnlockBus(int busID) { _buses.Remove(busID); }

    // Buses that should finish their current trip and then go to the garage (Route Manager "move after this trip").
    private static readonly HashSet<int> _parkAfter = new HashSet<int>();
    public static void RequestParkAfterTrip(int busID) { _parkAfter.Add(busID); }
    public static void CancelParkAfterTrip(int busID)  { _parkAfter.Remove(busID); }
    public static bool TakeParkAfterTrip(int busID)    { return _parkAfter.Remove(busID); }

    public static void Clear() { _slots.Clear(); _slotRefs.Clear(); _buses.Clear(); _parkAfter.Clear(); }
    public static int FixedBusCount => _buses.Count;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  PLAIN WORDING — every label the manager shows goes through here, so the
//  vocabulary stays short and consistent.
// ═══════════════════════════════════════════════════════════════════════════════
public static class ManagerWords
{
    public const string ModeName   = "Route Manager"; // placeholder name
    public const string Trip       = "trip";
    public const string Trips      = "trips";
    public const string Fixed      = "Fixed";
    public const string OnRoad     = "On the road";
    public const string Parked     = "Parked";
    public const string Broken     = "Broken down";

    public static string Direction(bool outbound) => outbound ? "A to Z" : "Z to A";
    public static string Day(int offset) => offset == 0 ? "Today" : offset == 1 ? "Tomorrow" : $"In {offset} days";
    public static string Clock(float minuteOfDay)
    {
        int m = Mathf.FloorToInt(((minuteOfDay % 1440f) + 1440f) % 1440f);
        return $"{m / 60:00}:{m % 60:00}";
    }

    public static string BusState(NPCBusController c, bool broken)
    {
        if (c == null) return "Unknown";
        if (broken) return Broken;
        switch (c.State)
        {
            case NPCBusController.BusState.Idle:
            case NPCBusController.BusState.WaitingAtDepot: return Parked;
            case NPCBusController.BusState.DepotIngress:
            case NPCBusController.BusState.DrivingToMaintenanceBay: return "Heading to the garage";
            case NPCBusController.BusState.AtMaintenanceBay:
            case NPCBusController.BusState.AtFuelStation:
            case NPCBusController.BusState.DrivingToFuelStation: return "At the garage";
            case NPCBusController.BusState.AtTerminal:
            case NPCBusController.BusState.TerminalIngress:
            case NPCBusController.BusState.TerminalEgress: return "Waiting at the end of the line";
            case NPCBusController.BusState.AtStop: return "At a stop";
            case NPCBusController.BusState.DeadRunning:
            case NPCBusController.BusState.ExpressDeadRun:
            case NPCBusController.BusState.DepotEgress: return "Driving to its first stop";
            default: return OnRoad;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTE RULES — which buses may run a route, in plain words.
//  Mirrors what the scheduler enforces (series list, night buses, articulated and
//  mini-bus policy, the garage serving the route, and the route's bus limit) but
//  returns a sentence instead of a code, and never logs.
//  The scheduler's own check is still the last word (see BusScheduler.Manager.cs).
// ═══════════════════════════════════════════════════════════════════════════════
public static class RouteRules
{
    public static string BusLabel(int fleetNumber)
    {
        var meta = FleetMetadata.Get(fleetNumber);
        return meta != null ? $"#{fleetNumber} ({meta.seriesName})" : $"#{fleetNumber}";
    }

    /// <summary>True when the bus may run this route at this time. Otherwise `reason` says why, in plain words.
    /// Pass checkLimit=false to skip the "route is full" rule (swaps and replacements don't add a bus).</summary>
    public static bool Check(int fleetNumber, BusRouteData route, string variantLetter, float minuteOfDay,
                             out string reason, bool checkLimit = true)
    {
        reason = null;
        if (route == null) return true;

        var meta = FleetMetadata.Get(fleetNumber);
        string routeName = route.LabelFor(variantLetter);
        RouteVariantData variant = !string.IsNullOrEmpty(variantLetter) ? route.GetVariant(variantLetter) : null;

        bool seriesOk = variant != null
            ? route.IsFleetSeriesAllowedForVariant(fleetNumber, variant, minuteOfDay)
            : route.IsFleetSeriesAllowed(fleetNumber, minuteOfDay);
        if (!seriesOk)
        {
            reason = $"Route {routeName} doesn't take bus {BusLabel(fleetNumber)}. " + SeriesSentence(route, minuteOfDay);
            return false;
        }

        if (meta != null)
        {
            var artic = variant != null && variant.overrideVehicleRestrictions ? variant.articulatedPolicyOverride : route.articulatedPolicy;
            if (artic == ArticulatedRequirement.Prohibited && meta.isArticulated)
            { reason = $"Route {routeName} doesn't allow long (articulated) buses."; return false; }
            if (artic == ArticulatedRequirement.Mandatory && !meta.isArticulated)
            { reason = $"Route {routeName} only takes long (articulated) buses."; return false; }

            if (route.miniBusPolicy == ArticulatedRequirement.Prohibited && meta.is35Ft)
            { reason = $"Route {routeName} doesn't allow small 35 ft buses."; return false; }
            if (route.miniBusPolicy == ArticulatedRequirement.Mandatory && !meta.is35Ft)
            { reason = $"Route {routeName} only takes small 35 ft buses."; return false; }

            if (DepotManager.Instance != null && !DepotManager.Instance.CanServeRoute(fleetNumber, route.routeNumber))
            {
                string depot = meta.homeDepot != null ? meta.homeDepot.depotName : "its garage";
                reason = $"Bus {BusLabel(fleetNumber)} lives at {depot}, and that garage doesn't cover route {route.routeNumber}.";
                return false;
            }
        }

        if (checkLimit && BusScheduler.Instance != null)
        {
            int running = BusScheduler.Instance.CountActiveBusesOnRoute(route.routeNumber);
            if (running >= route.maxBusesAllowed)
            { reason = $"Route {route.routeNumber} is full: {running} of {route.maxBusesAllowed} buses are already running."; return false; }
        }
        return true;
    }

    private static string SeriesSentence(BusRouteData route, float minuteOfDay)
    {
        bool night = route.nightFleetSeries != null && route.nightFleetSeries.Count > 0
                     && minuteOfDay >= 0f && BusRouteData.IsNightWindow(minuteOfDay);
        var list = night ? route.nightFleetSeries : route.allowedFleetSeries;
        if (list == null || list.Count == 0) return "";
        var parts = new List<string>();
        foreach (int b in list)
            parts.Add(b == BusRouteData.OLD_BUS_DEPOT_WILDCARD ? "older buses from garages that cover it" : $"{b}s");
        return (night ? "At night it only takes " : "It only takes ") + string.Join(", ", parts) + ".";
    }

    /// <summary>Short list of this route's rules for the rules box, one plain sentence per line.</summary>
    public static List<string> Summary(BusRouteData route)
    {
        var lines = new List<string>();
        if (route == null) return lines;

        lines.Add($"Up to {route.maxBusesAllowed} buses at a time.");

        if (route.allowedFleetSeries != null && route.allowedFleetSeries.Count > 0)
        {
            var parts = new List<string>();
            foreach (int b in route.allowedFleetSeries)
                parts.Add(b == BusRouteData.OLD_BUS_DEPOT_WILDCARD ? "older buses from garages that cover it" : $"the {b}s");
            lines.Add("Takes " + string.Join(", ", parts) + ".");
        }
        else lines.Add("Any bus number.");

        if (route.nightFleetSeries != null && route.nightFleetSeries.Count > 0)
        {
            var parts = new List<string>();
            foreach (int b in route.nightFleetSeries) parts.Add($"the {b}s");
            lines.Add("At night only " + string.Join(", ", parts) + ".");
        }

        switch (route.articulatedPolicy)
        {
            case ArticulatedRequirement.Prohibited: lines.Add("No long (articulated) buses."); break;
            case ArticulatedRequirement.Mandatory:  lines.Add("Long (articulated) buses only."); break;
            case ArticulatedRequirement.Preferred:  lines.Add("Long (articulated) buses are preferred."); break;
        }
        switch (route.miniBusPolicy)
        {
            case ArticulatedRequirement.Prohibited: lines.Add("No small 35 ft buses."); break;
            case ArticulatedRequirement.Mandatory:  lines.Add("Small 35 ft buses only."); break;
        }
        lines.Add("The bus's own garage has to cover the route.");
        lines.Add($"One way takes about {Mathf.RoundToInt(route.oneWayTripMinutes)} min.");
        return lines;
    }
}
