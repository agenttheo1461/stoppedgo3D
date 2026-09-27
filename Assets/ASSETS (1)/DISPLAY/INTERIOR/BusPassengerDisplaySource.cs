using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  IBusDisplaySource
//
//  Thin read-only contract so BusInteriorScrollBoard / BusInteriorLCDBoard don't
//  care whether they're mounted on the player bus (BusSimulationController) or
//  an NPC bus (NPCBusController) -- same spirit as isNPC unification elsewhere,
//  just done via interface instead of a toggle since these boards are pure
//  readers and never need to branch behavior themselves.
//
//  Implement this on BOTH controllers. Nothing here should require new state --
//  every field maps to something the controllers already compute (route/dest
//  from _route, next stop from nextStopIndex/_nextStopIdx, stop-requested from
//  the existing alighting-count roll for the upcoming stop).
// ═══════════════════════════════════════════════════════════════════════════════
public interface IBusDisplaySource
{
    int BusID { get; }
    string RouteNumber { get; }
    string DestinationHeadsign { get; }

    /// <summary>The small "VIA EVERGREEN" / "LIMITED" / "MAX" qualifier line
    /// from BusRouteData.routeQualifierOutbound/Inbound for whichever direction
    /// this bus is currently running. Empty string when the route has none
    /// for this direction -- boards should just omit the line, not show blank
    /// space. NPCBusController / BusSimulationController: expose this the
    /// same way RouteNumber/DestinationHeadsign already pull from _route.</summary>
    string RouteQualifier { get; }

    string CurrentStopName { get; }
    string NextStopName { get; }

    /// <summary>Next up to 3 stop names ahead of the bus, in order. Fewer than 3
    /// near the terminal is fine -- boards just render however many are given.</summary>
    List<string> GetUpcomingStops(int count);

    /// <summary>True when the upcoming stop has a nonzero alighting roll --
    /// i.e. someone onboard is getting off there. This IS the "stop requested"
    /// state; there's no separate pull-cord system, alighting > 0 at the next
    /// stop is the request. Clears naturally once that stop becomes
    /// CurrentStopName and the bus moves on to a new NextStopName.</summary>
    bool IsNextStopRequested { get; }

    // ── [07-30] Added for BusInteriorLCDBoard's redesigned layout ───────────────

    /// <summary>The bus's physical fleet number (e.g. 17779), distinct from
    /// BusID (which on the player side is the possession-tracking ID, not the
    /// painted fleet number). Both controllers already have this under some
    /// name -- this just exposes it uniformly for the boot-sequence readout.</summary>
    int FleetNumber { get; }

    /// <summary>True only when the engine is actually Running. Cranking/
    /// ReadyToStart/Off all read false -- the board should be fully dark
    /// through cranking and only boot once the engine catches.</summary>
    bool IsEngineRunning { get; }

    /// <summary>True only while the bus is actively working a route --
    /// NPCBusController.State == InService, or PlayerHandoff's shift state
    /// == InService. False for dead-running/express-dead-run/idle/relieved/
    /// rotating/off-duty, even though the engine may well be running through
    /// all of those. Boards use this to show "NOT IN SERVICE" instead of a
    /// stale/blank route number and headsign whenever the engine is on but
    /// the bus isn't actually carrying a route right now.</summary>
    bool IsInService { get; }

    /// <summary>Same stops as GetUpcomingStops, but each one paired with a
    /// live ETA label from BusTrackerService's GPS+scheduler hybrid math --
    /// the same math riders see on exterior stop-side boards, just answering
    /// "when do I personally reach this stop" instead of "who's arriving
    /// here." isTerminal marks the last stop in the sequence so the board can
    /// collapse its layout down to it.</summary>
    List<(string stopName, string etaLabel, bool isTerminal)> GetUpcomingStopsWithEta(int count);
}

// ═══════════════════════════════════════════════════════════════════════════════
//  IBusDriverDisplaySource
//
//  [ADD] Extends IBusDisplaySource with driver-only data -- things a passenger
//  board never needs (raw speed, schedule adherence, distance to next stop)
//  but a driver's dash absolutely does. Kept as a SEPARATE interface rather
//  than adding these members directly to IBusDisplaySource so every existing
//  passenger-board implementer (NPCBusController, PlayerHandoff via
//  BusDisplaySourceResolver) keeps compiling untouched -- only whichever
//  controller(s) actually back the driver board need to implement this one.
//
//  Realistically only PlayerHandoff needs this (there's no driver to look at
//  an NPC bus's dash), but it's still a real bus, so NPCBusController is free
//  to implement it too later if you ever want an AI-debug view.
//
//  BusDriverLCDBoard checks `dataSourceBehaviour as IBusDriverDisplaySource`
//  separately from the base cast -- if the assigned source only implements
//  IBusDisplaySource (not this), the driver-only fields just render "--"
//  instead of the whole board refusing to work.
// ═══════════════════════════════════════════════════════════════════════════════
public interface IBusDriverDisplaySource : IBusDisplaySource
{
    /// <summary>Current road speed, km/h. Whatever the sim's speedometer
    /// already reads internally -- just expose it here.</summary>
    float SpeedKph { get; }

    /// <summary>Minutes ahead(+)/behind(-) schedule at the current position,
    /// from whatever adherence math BusScheduler/BusTrackerService already
    /// does for the exterior boards' "on time" logic. 0 = exactly on time.</summary>
    float ScheduleAdherenceMinutes { get; }

    /// <summary>Straight-line or route-distance to NextStopName, metres.
    /// Used for the driver's "next stop in X m" readout -- passengers get an
    /// ETA time, drivers get a distance since that's what's actually useful
    /// for judging when to signal/brake.</summary>
    float DistanceToNextStopMetres { get; }

    /// <summary>Run/block identifier if your scheduler tracks one (e.g.
    /// "RUN 4" or a block number) -- empty string if not applicable/not
    /// tracked, board just omits the line.</summary>
    string RunOrBlockLabel { get; }

    bool DoorsOpen { get; }
    bool ParkingBrakeSet { get; }

    /// <summary>The route as consoles and the 2D driver board write it, including the variant letter or the
    /// short-turn symbol ("34A", "116~"). IBusDisplaySource.RouteNumber is the LCD-board version (no '~').</summary>
    string RouteLabel { get; }

    /// <summary>Riders currently onboard.</summary>
    int OnboardPax { get; }

    /// <summary>Most riders this bus carries (seated + standing), from its fleet series.
    /// Always &gt; 0 so a percentage never divides by zero.</summary>
    int PassengerCapacity { get; }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  BusDriverDisplayState
//
//  Same plain-snapshot pattern as BusPassengerDisplayState, sized for the
//  driver board's denser always-on layout instead of a flashing rotation.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusDriverDisplayState
{
    public string busIdLabel;
    public string routeNumber;
    public string destinationHeadsign;
    public string routeQualifier;
    public string currentStopName;
    public string nextStopName;
    public bool   stopRequested;
    public string currentTimeLabel;
    public int    fleetNumber;
    public bool   isEngineRunning;
    public bool   isInService;

    // ── Driver-only ──────────────────────────────────────────────────────────
    public float  speedKph;
    public float  scheduleAdherenceMinutes;
    public float  distanceToNextStopMetres;
    public string runOrBlockLabel = "";
    public bool   doorsOpen;
    public bool   parkingBrakeSet;
    public int    onboardPax;
    public int    passengerCapacity = 1;
    public bool   hasDriverExtras; // false when the source only implements IBusDisplaySource

    /// <summary>Load as a whole percent of capacity ("35% FULL"). May exceed 100 if something over-boards.</summary>
    public int PercentFull => passengerCapacity > 0 ? Mathf.RoundToInt(100f * onboardPax / passengerCapacity) : 0;

    public void RefreshFrom(IBusDisplaySource source, string currentTimeLabel)
    {
        if (source == null) return;

        busIdLabel          = $"BUS {source.BusID}";
        routeNumber         = source.RouteNumber;
        destinationHeadsign = source.DestinationHeadsign;
        routeQualifier      = source.RouteQualifier ?? "";
        currentStopName     = source.CurrentStopName;
        nextStopName        = source.NextStopName;
        stopRequested       = source.IsNextStopRequested;
        fleetNumber         = source.FleetNumber;
        isEngineRunning     = source.IsEngineRunning;
        isInService         = source.IsInService;
        this.currentTimeLabel = currentTimeLabel;

        if (source is IBusDriverDisplaySource driverSource)
        {
            hasDriverExtras          = true;
            if (!string.IsNullOrEmpty(driverSource.RouteLabel)) routeNumber = driverSource.RouteLabel;
            speedKph                 = driverSource.SpeedKph;
            scheduleAdherenceMinutes = driverSource.ScheduleAdherenceMinutes;
            distanceToNextStopMetres = driverSource.DistanceToNextStopMetres;
            runOrBlockLabel          = driverSource.RunOrBlockLabel ?? "";
            doorsOpen                = driverSource.DoorsOpen;
            parkingBrakeSet          = driverSource.ParkingBrakeSet;
            onboardPax               = driverSource.OnboardPax;
            passengerCapacity        = Mathf.Max(1, driverSource.PassengerCapacity);
        }
        else
        {
            hasDriverExtras = false;
        }

        if (BusBoardDebugChannel.TryGetRouteOverride(out string rtOverride))
            routeNumber = rtOverride;
        if (BusBoardDebugChannel.TryGetDestinationOverride(out string destOverride))
            destinationHeadsign = destOverride;
    }

    /// <summary>+0.5/-1 minute etc -> "ON TIME" / "2 MIN EARLY" / "1 MIN LATE".
    /// Under 30s either direction reads as ON TIME rather than showing a
    /// near-meaningless "0 MIN" label.</summary>
    public string AdherenceLabel()
    {
        if (Mathf.Abs(scheduleAdherenceMinutes) < 0.5f) return "ON TIME";
        int mins = Mathf.RoundToInt(Mathf.Abs(scheduleAdherenceMinutes));
        return scheduleAdherenceMinutes > 0 ? $"{mins} MIN LATE" : $"{mins} MIN EARLY";
    }

    public string DistanceLabel()
    {
        if (distanceToNextStopMetres < 0f) return "--";
        return distanceToNextStopMetres < 1000f
            ? $"{Mathf.RoundToInt(distanceToNextStopMetres)} m"
            : $"{(distanceToNextStopMetres / 1000f):0.0} km";
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  BusPassengerDisplayState
//
//  Plain (non-MonoBehaviour) snapshot pulled from an IBusDisplaySource once per
//  refresh tick -- same pattern as BusAudioEngine: a shared plain class fed by
//  whichever controller owns this bus, not a component itself.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusPassengerDisplayState
{
    public string busIdLabel;
    public string routeNumber;
    public string destinationHeadsign;
    public string routeQualifier; // "" when the current direction has none
    public string currentStopName;
    public string nextStopName;
    public List<string> upcomingStops = new List<string>();
    public bool stopRequested;
    public string currentTimeLabel; // pre-formatted, e.g. "4:12 PM" -- pull from your sim clock

    // ── [07-30] LCD redesign additions ──────────────────────────────────────
    public int  fleetNumber;
    public bool isEngineRunning;
    public bool isInService;
    public List<(string stopName, string etaLabel, bool isTerminal)> upcomingStopsWithEta = new();

    public void RefreshFrom(IBusDisplaySource source, string currentTimeLabel)
    {
        if (source == null) return;

        busIdLabel          = $"BUS {source.BusID}";
        routeNumber         = source.RouteNumber;
        destinationHeadsign = source.DestinationHeadsign;
        routeQualifier      = source.RouteQualifier ?? "";
        currentStopName     = source.CurrentStopName;
        nextStopName        = source.NextStopName;
        stopRequested       = source.IsNextStopRequested;
        fleetNumber         = source.FleetNumber;
        isEngineRunning     = source.IsEngineRunning;
        isInService         = source.IsInService;
        this.currentTimeLabel = currentTimeLabel;

        upcomingStops.Clear();
        upcomingStops.AddRange(source.GetUpcomingStops(3));

        upcomingStopsWithEta.Clear();
        upcomingStopsWithEta.AddRange(source.GetUpcomingStopsWithEta(3));

        // Console overrides (see BusBoardDebugChannel / "boardrt" & "boardestination"
        // in Driver.cs) win over whatever the real controller just reported.
        if (BusBoardDebugChannel.TryGetRouteOverride(out string rtOverride))
            routeNumber = rtOverride;
        if (BusBoardDebugChannel.TryGetDestinationOverride(out string destOverride))
            destinationHeadsign = destOverride;
    }

    /// <summary>Builds the rotation strings both board types cycle through.
    /// "STOP REQUESTED" jumps to the front of the queue when active, same as
    /// a real Clever board interrupting its normal rotation for it.</summary>
    public List<string> BuildRotation()
    {
        List<string> list;

        // [FIX] Engine running but not actually working a route (dead-run,
        // express dead-run, idle, relieved, rotating, off-duty) -- show
        // NOT IN SERVICE instead of a stale/blank route+destination. Engine
        // being fully off is handled separately upstream (boards go dark).
        if (isEngineRunning && !isInService)
        {
            list = new List<string> { currentTimeLabel, busIdLabel, "NOT IN SERVICE" };
        }
        else
        {
            list = new List<string>
            {
                currentTimeLabel,
                busIdLabel,
                string.IsNullOrEmpty(nextStopName) ? "--" : nextStopName,
                string.IsNullOrEmpty(routeNumber) ? "--" : routeNumber,
            };
        }

        if (stopRequested) list.Insert(0, "STOP REQUESTED");

        // Console-pushed debug message (see BusBoardDebugChannel / "boardmsg"
        // in DriverConsole) always wins the front slot -- it's an explicit,
        // deliberate override, so it should never be buried behind a real
        // stop request while it's live.
        if (BusBoardDebugChannel.TryGetActive(out string dbg)) list.Insert(0, dbg);

        return list;
    }
}