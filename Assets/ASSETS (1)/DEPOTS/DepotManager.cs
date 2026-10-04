using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  DEPOT MANAGER
//
//  · Spawns the fleet roster into depot parking spots (round-robin within each
//    depot's preferences/capacity).
//  · Tracks each bus's home depot for "return to depot" / relief logic.
//  · When a bus needs relief (lap handoff), DepotManager filters candidates to
//    only buses whose home depot serves the route in question.
// ═══════════════════════════════════════════════════════════════════════════════
public class DepotManager : MonoBehaviour
{
    public static DepotManager Instance { get; private set; }

    [Header("Depots")]
    public List<DepotData> depots = new();

    [Header("Fleet Roster")]
    [Tooltip("Legacy single-dataset field. If additionalFleetRosters is empty, this " +
             "asset is used exactly as before. If additionalFleetRosters has entries, " +
             "this field gets REPLACED at Start() with a runtime-merged instance " +
             "combining this asset (if set) plus every entry below, in list order -- " +
             "same merge helper used by BusSelectMenu, so a Gillig/SCT roster asset can " +
             "spawn alongside the main CBT one without DepotManager needing to know the " +
             "difference. See FleetRosterData.MergeInto().")]
    public FleetRosterData fleetRoster;

    [Tooltip("Extra fleet roster datasets to merge in alongside (or instead of) " +
             "fleetRoster above -- e.g. a separate SCT/Gillig roster asset kept apart " +
             "from the main CBT one. Leave empty for single-dataset mode.")]
    public List<FleetRosterData> additionalFleetRosters = new();

    [Header("Spawner Reference")]
    [Tooltip("BusSlots will be injected into this spawner's busSlots list at runtime.")]
    public BusSpawner busSpawner;

    [Header("Debug")]
    public bool logAssignments = true;

    // fleetNumber -> depot it's homed to
    private readonly Dictionary<int, DepotData> _homeDepotByFleet = new();
    // depotCode -> list of fleet numbers currently homed there
    private readonly Dictionary<string, List<int>> _fleetByDepot = new();

    // ═════════════════════════════════════════════════════════════════════════
    //  [ADD Bug 2 fix] SINGLE OCCUPANCY TABLE — spot claim/release
    //
    //  Previously BusDepot kept its own private _bayOccupant[]/_parkedAt
    //  table, entirely unsynced with DepotData.parkingSpots (which
    //  DepotManager already used for spawn-time placement). When BusDepot's
    //  own table ran out of free bays it fell back to parking the bus "at
    //  the gate (untracked)" against an unset/default gate position -- the
    //  fixed-coordinate dump bug. DepotManager is now the sole owner of
    //  spot claim/release for BOTH spawn and runtime ingress/egress; BusDepot
    //  calls these instead of touching any table of its own.
    //
    //  Keyed by fleetNumber, NOT busID -- busID is a disposable per-scene-
    //  instance ID (see BusManager.RegisterBusInternal: _nextBusID++), while
    //  fleetNumber is the persistent roster identity every other DepotManager
    //  table (_homeDepotByFleet, GetHomeDepot, CanServeRoute) already keys on.
    //  Mixing the two keys back together was part of what let the split
    //  happen in the first place, so callers must resolve ctrl.fleetNumber
    //  before calling into these, not ctrl.busID.
    // ═════════════════════════════════════════════════════════════════════════
    private readonly Dictionary<int, (DepotData depot, DepotParkingSpot spot, int index)> _spotByFleet = new();

    /// <summary>
    /// Claim a free parking spot for this bus at the given depot. If the bus
    /// already holds a spot anywhere, that spot is released first so it can
    /// never hold two at once. Returns -1 if the depot has no free spot.
    /// </summary>
    public int ClaimSpotFor(DepotData depot, int fleetNumber, out DepotParkingSpot spot)
    {
        spot = null;
        if (depot == null) return -1;

        ReleaseSpotFor(fleetNumber);

        int index = depot.parkingSpots.FindIndex(s => !s.occupied);
        if (index < 0) return -1;

        spot = depot.parkingSpots[index];
        spot.occupied              = true;
        spot.occupiedByFleetNumber = fleetNumber;
        _spotByFleet[fleetNumber] = (depot, spot, index);
        return index;
    }

    /// <summary>Release whatever spot this bus currently holds, if any.</summary>
    public void ReleaseSpotFor(int fleetNumber)
    {
        if (_spotByFleet.TryGetValue(fleetNumber, out var entry))
        {
            entry.spot.occupied              = false;
            entry.spot.occupiedByFleetNumber = -1;
            _spotByFleet.Remove(fleetNumber);
        }
    }

    public bool IsParked(int fleetNumber) => _spotByFleet.ContainsKey(fleetNumber);

    public DepotParkingSpot GetParkedSpot(int fleetNumber) =>
        _spotByFleet.TryGetValue(fleetNumber, out var e) ? e.spot : null;

    public int GetParkedSpotIndex(int fleetNumber) =>
        _spotByFleet.TryGetValue(fleetNumber, out var e) ? e.index : -1;

    public int FreeSpotCount(DepotData depot)
    {
        if (depot == null) return 0;
        int n = 0;
        foreach (var s in depot.parkingSpots) if (!s.occupied) n++;
        return n;
    }

    // [REMOVED 2026-09-29] DespawnLocalFleetForNetworkClient/RebuildLocalFleetAfterFailedJoin --
    // multiplayer, rebuilt architecture: a network client now keeps its own full local fleet,
    // spawned unconditionally here exactly like single-player/host. Nothing tears it down before
    // connecting any more; see NetworkGameBridge's header comment for why every process having the
    // SAME buses (deterministic busID assignment, nothing cloned/destroyed over the network) is
    // what makes this safe.

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

private void Start()
{
    if (busSpawner == null) busSpawner = BusSpawner.Instance;

    // [ADD] Multi-dataset support -- only kicks in if additionalFleetRosters has
    // anything in it, so a project with a single fleetRoster asset (and an empty
    // additionalFleetRosters list) behaves exactly as before. Same merge helper
    // as BusSelectMenu, so a series that only exists in a merged-in SCT/Gillig
    // asset still gets a depot assignment, home-depot tracking, and route
    // eligibility just like any CBT series would.
    if (additionalFleetRosters != null && additionalFleetRosters.Count > 0)
    {
        var sources = new List<FleetRosterData>();
        if (fleetRoster != null) sources.Add(fleetRoster);
        sources.AddRange(additionalFleetRosters);
        fleetRoster = FleetRosterData.MergeInto(null, sources);
    }

    if (fleetRoster == null) { Debug.LogError("[DepotManager] fleetRoster is NULL — assign the asset in Inspector."); return; }
    if (busSpawner  == null) { Debug.LogError("[DepotManager] busSpawner is NULL — BusSpawner.Instance not set yet (check execution order)."); return; }
    if (BusManager.Instance == null) { Debug.LogError("[DepotManager] BusManager.Instance is NULL — check execution order/scene setup."); return; }

    BuildAndAssignFleet();
}

// ═══════════════════════════════════════════════════════════════════════════════════
//  FIXED DepotManager.BuildAndAssignFleet()  —  assigns homeDepot to each bus
// ═══════════════════════════════════════════════════════════════════════════════════
//
// REPLACE the method body in DepotManager.cs:
//

private void BuildAndAssignFleet()
{
    if (fleetRoster == null)
    {
        Debug.LogError("[DepotManager] No FleetRosterData assigned.");
        return;
    }
    if (busSpawner == null)
    {
        Debug.LogError("[DepotManager] No BusSpawner found/assigned.");
        return;
    }
    if (BusManager.Instance == null)
    {
        Debug.LogError("[DepotManager] No BusManager found — cannot register spawned buses.");
        return;
    }

    FleetMetadata.Clear();
    var slots = fleetRoster.BuildSlots();
    Debug.Log($"[DepotManager] BuildSlots returned {slots.Count} slots. Series count: {fleetRoster.series?.Count ?? -1}");
    
    foreach (var d in depots)
    {
        foreach (var spot in d.parkingSpots)
        {
            spot.occupied              = false;
            spot.occupiedByFleetNumber = -1;
        }
    }
    _spotByFleet.Clear(); // keep the tracking dict in lockstep with the reset above

    foreach (var slot in slots)
    {
        var meta = FleetMetadata.Get(slot.fleetNumber);
        DepotData depot = ResolveHomeDepot(slot.fleetNumber, meta);

        if (depot == null)
        {
            Debug.LogWarning($"[DepotManager] No depot available for Fleet#{slot.fleetNumber} " +
                              $"({meta?.busType ?? "unknown"}) — bus will not be spawned.");
            continue;
        }

        var spot = depot.GetFreeSpot();
        if (spot == null)
        {
            Debug.LogWarning($"[DepotManager] Depot '{depot.depotName}' has no free parking " +
                              $"for Fleet#{slot.fleetNumber} — bus will not be spawned.");
            continue;
        }

        spot.occupied              = true;
        spot.occupiedByFleetNumber = slot.fleetNumber;
        // [FIX Bug 2] Register in the same _spotByFleet table ClaimSpotFor/
        // ReleaseSpotFor use, so a bus that never leaves its spawn spot is
        // still correctly tracked as "parked" for later BusDepot calls.
        _spotByFleet[slot.fleetNumber] = (depot, spot, depot.parkingSpots.IndexOf(spot));

        slot.spawnPosition = spot.position;
        slot.spawnRotation = spot.rotationEuler;

        _homeDepotByFleet[slot.fleetNumber] = depot;
        if (!_fleetByDepot.TryGetValue(depot.depotCode, out var list))
        {
            list = new List<int>();
            _fleetByDepot[depot.depotCode] = list;
        }
        list.Add(slot.fleetNumber);

        busSpawner.busSlots.Add(slot);

        if (logAssignments)
            Debug.Log($"[DepotManager] Fleet#{slot.fleetNumber} ({meta?.busType}) " +
                      $"→ {depot.depotName} spot @ {spot.position}");
    }

    busSpawner.SpawnAll();

    // ── NEW: register every spawned controller with BusManager ──────────────
    // AND assign homeDepot field so BusDepot can find them
    foreach (var slot in slots)
    {
        if (slot.spawnedController == null) continue;

        var meta = FleetMetadata.Get(slot.fleetNumber);

        // ── Assign the home depot (CRITICAL for depot return) ────────────────
        if (_homeDepotByFleet.TryGetValue(slot.fleetNumber, out var homeData))
        {
            slot.spawnedController.homeDepot = homeData;
            Debug.Log($"[DepotManager] Bus#{slot.fleetNumber} assigned homeDepot: {homeData.depotCode}");
        }

        // Apply articulation mode for articulated series before registering
        if (meta != null && meta.isArticulated &&
            slot.spawnedController.articulationMode == NPCBusController.ArticulationMode.None)
        {
            slot.spawnedController.articulationMode = NPCBusController.ArticulationMode.HingeConstraint;
        }

        BusManager.Instance.RegisterBus(slot.spawnedController);
    }

    // [FIX] This is the actual moment the fleet is guaranteed fully
    // registered — DepotManager.Start() vs BusManager.Start() has no
    // ordering guarantee, so relying on BusManager's own single
    // RebuildPendingPreAssignments() call raced and often lost, leaving
    // every slot Unassigned/"TBD" forever. Calling it here, right after
    // the last RegisterBus, means it always runs after the fleet exists.
    BusScheduler.Instance?.RebuildPendingPreAssignments();

    if (logAssignments)
    {
        foreach (var kv in _fleetByDepot)
            Debug.Log($"[DepotManager] Depot '{kv.Key}': {kv.Value.Count} buses homed.");
    }
}


    private DepotData ResolveHomeDepot(int fleetNumber, FleetMetadata.Entry meta)
    {
        // 1. Explicit home depot from the series definition
        if (meta?.homeDepot != null) return meta.homeDepot;

        // 2. Find a depot that prefers this bus type and has free space
        foreach (var depot in depots)
        {
            if (!depot.AcceptsBusType(meta?.busType ?? "")) continue;
            if (meta != null && depot.articulatedOnly && !meta.isArticulated) continue;
            if (depot.GetFreeSpot() != null) return depot;
        }

        // 3. Fallback: any depot with space
        foreach (var depot in depots)
            if (depot.GetFreeSpot() != null) return depot;

        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC API — used by BusScheduler / BusManager for route-restricted relief
    // ═════════════════════════════════════════════════════════════════════════

    public DepotData GetHomeDepot(int fleetNumber) =>
        _homeDepotByFleet.TryGetValue(fleetNumber, out var d) ? d : null;

    /// <summary>[ADD] Buses are spawned at their home depot (BuildAndAssignFleet,
    /// the sole writer to _homeDepotByFleet before this), so under normal
    /// operation NPCBusController.homeDepot -- a cached copy kept on the
    /// controller purely so hot paths don't need a dictionary lookup every
    /// time -- never actually needs to change. The one real exception is
    /// ResolveRetirementDepot's alternate-depot fallback (home depot full at
    /// retirement time): that reassigns the controller's own cached field
    /// directly, which used to leave this dictionary -- the actual source of
    /// truth -- still pointing at the old depot. Call this whenever the
    /// controller's cached copy changes for a real reason, so the two never
    /// actually diverge.</summary>
    public void SetHomeDepot(int fleetNumber, DepotData depot)
    {
        if (depot == null) return;
        _homeDepotByFleet[fleetNumber] = depot;
    }

    /// <summary>
    /// True if this bus's home depot is allowed to serve the given route.
    /// Buses with no registered home depot are treated as unrestricted.
    /// </summary>
    public bool CanServeRoute(int fleetNumber, string routeNumber)
    {
        var depot = GetHomeDepot(fleetNumber);
        if (depot == null) return true;
        return depot.ServesRoute(routeNumber);
    }

    /// <summary>
    /// All fleet numbers homed at a given depot.
    /// </summary>
    public List<int> GetFleetForDepot(string depotCode) =>
        _fleetByDepot.TryGetValue(depotCode, out var list) ? list : new List<int>();

    /// <summary>
    /// Find the depot (if any) that serves the given route.
    /// </summary>
    public DepotData GetDepotForRoute(string routeNumber)
    {
        foreach (var depot in depots)
            if (depot.ServesRoute(routeNumber)) return depot;
        return null;
    }
}