using UnityEngine;
using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusDisplaySourceResolver
//
//  Sits on the same GameObject as BusSimulationController + NPCBusController
//  (the "bus header"). Implements IBusDisplaySource itself, but every member
//  just forwards to WHICHEVER of the two controllers is actually driving this
//  bus right now -- NPC by default, PlayerHandoff.Instance whenever this bus
//  is the one currently possessed.
//
//  This is the thing you drag into BusInteriorScrollBoard/LCDBoard's
//  dataSourceBehaviour slot -- NOT NPCBusController or PlayerHandoff directly,
//  since neither one alone is correct across a possession swap.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusDisplaySourceResolver : MonoBehaviour, IBusDisplaySource, IBusDriverDisplaySource
{
    [Tooltip("Auto-found via GetComponent if left blank.")]
    public NPCBusController npc;
    [Tooltip("Auto-found via GetComponent if left blank -- only present on the player's bus prefab, so this can stay null on pure-NPC buses.")]
    public BusSimulationController playerSim;

    private void Awake()
    {
        if (npc == null)       npc       = GetComponent<NPCBusController>();
        if (playerSim == null) playerSim = transform.root.GetComponentInChildren<BusSimulationController>(true);
    }

    /// <summary>True when this specific bus is the one the player currently
    /// possesses. NPCBusController goes inert on possession, so its own state
    /// can't be trusted as the signal -- ask PlayerHandoff which bus it's on
    /// instead, and compare against this bus's own BusSimulationController.</summary>
    private bool IsPlayerDrivingThisBus =>
        playerSim != null
        && PlayerHandoff.Instance != null
        && PlayerHandoff.Instance.IsOnDuty
        && PlayerHandoff.Instance.playerBus == playerSim;

    private IBusDisplaySource Active =>
        IsPlayerDrivingThisBus ? (IBusDisplaySource)PlayerHandoff.Instance : npc;

    int          IBusDisplaySource.BusID                 => Active != null ? Active.BusID : -1;
    string       IBusDisplaySource.RouteNumber            => Active?.RouteNumber ?? "";
    string       IBusDisplaySource.DestinationHeadsign     => Active?.DestinationHeadsign ?? "";
    string       IBusDisplaySource.RouteQualifier          => Active?.RouteQualifier ?? "";
    string       IBusDisplaySource.CurrentStopName         => Active?.CurrentStopName ?? "—";
    string       IBusDisplaySource.NextStopName            => Active?.NextStopName ?? "—";
    bool         IBusDisplaySource.IsNextStopRequested      => Active != null && Active.IsNextStopRequested;
    List<string> IBusDisplaySource.GetUpcomingStops(int count) =>
        Active != null ? Active.GetUpcomingStops(count) : new List<string>();

    // ── [07-30] forwarded for the redesigned LCD board ──────────────────────
    int  IBusDisplaySource.FleetNumber     => Active != null ? Active.FleetNumber : -1;
    bool IBusDisplaySource.IsEngineRunning => Active != null && Active.IsEngineRunning;
    bool IBusDisplaySource.IsInService     => Active != null && Active.IsInService;
    List<(string, string, bool)> IBusDisplaySource.GetUpcomingStopsWithEta(int count) =>
        Active != null ? Active.GetUpcomingStopsWithEta(count) : new List<(string, string, bool)>();

    // ── [ADD] IBusDriverDisplaySource forwarding ────────────────────────────
    // Same pattern as every member above -- forward to whichever underlying
    // source is Active right now. The extra wrinkle: Active is typed as
    // IBusDisplaySource, not IBusDriverDisplaySource, so each of these needs
    // its own cast with a graceful fallback for whenever the active source
    // (almost always the NPC controller, since PlayerHandoff is the only
    // real implementer today) doesn't implement the driver-only interface.
    // This is exactly why BusDriverLCDBoard was showing "--" for every
    // driver-only field despite PlayerHandoff implementing them correctly --
    // this resolver is what boards actually get assigned to in the
    // Inspector, and it had no idea the driver interface existed until now.
    private IBusDriverDisplaySource ActiveDriver => Active as IBusDriverDisplaySource;

    string IBusDriverDisplaySource.RouteLabel                => ActiveDriver?.RouteLabel ?? Active?.RouteNumber ?? "";
    float  IBusDriverDisplaySource.SpeedKph                 => ActiveDriver?.SpeedKph ?? 0f;
    float  IBusDriverDisplaySource.ScheduleAdherenceMinutes  => ActiveDriver?.ScheduleAdherenceMinutes ?? 0f;
    float  IBusDriverDisplaySource.DistanceToNextStopMetres  => ActiveDriver?.DistanceToNextStopMetres ?? -1f;
    string IBusDriverDisplaySource.RunOrBlockLabel           => ActiveDriver?.RunOrBlockLabel ?? "";
    bool   IBusDriverDisplaySource.DoorsOpen                 => ActiveDriver != null && ActiveDriver.DoorsOpen;
    bool   IBusDriverDisplaySource.ParkingBrakeSet            => ActiveDriver != null && ActiveDriver.ParkingBrakeSet;
    int    IBusDriverDisplaySource.OnboardPax                 => ActiveDriver?.OnboardPax ?? 0;
    int    IBusDriverDisplaySource.PassengerCapacity          => ActiveDriver?.PassengerCapacity ?? BusCapacityDefaults.Standard40Total;
    string IBusDriverDisplaySource.RampStateLabel             => ActiveDriver?.RampStateLabel ?? "STOWED";
    bool   IBusDriverDisplaySource.AdaPaxEventPending          => ActiveDriver != null && ActiveDriver.AdaPaxEventPending;
    bool   IBusDriverDisplaySource.AdaAlightRequested          => ActiveDriver != null && ActiveDriver.AdaAlightRequested;
    int    IBusDriverDisplaySource.OnboardAdaPax               => ActiveDriver?.OnboardAdaPax ?? 0;
    int    IBusDriverDisplaySource.AdaCapacity                 => ActiveDriver?.AdaCapacity ?? 1;
}