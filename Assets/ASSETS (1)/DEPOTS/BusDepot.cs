using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusDepot  —  physical depot that buses spawn from and return to
//
//  WHAT IT DOES
//  ────────────
//  · Owns a list of numbered bay positions (set in Inspector or auto-generated).
//  · Buses registered here start the session parked in a bay.
//  · When the scheduler dispatches a bus, depot calls StartDepotEgress() on
//    the NPCBusController — the bus drives out to the road and finds the terminal.
//  · When a bus completes its final lap (RetirementDone signal from
//    NPCBusController), depot calls it home; the bus drives back and parks.
//  · If the scheduler needs the bus again before it reaches the depot,
//    CancelReturn() aborts the ingress and sends it straight back into service.
//
//  INSPECTOR SETUP
//  ───────────────
//  1. Place a BusDepot GameObject in the scene near the road network.
//  2. Set `gatePosition` to the world point where buses enter/exit the road.
//  3. Add NPCBusController references to `registeredBuses`.
//  4. Bays are generated automatically in a row from `bayOrigin` spaced by
//     `baySpacing`, or set them manually in `manualBays`.
//  5. Assign this depot to DepotManager if you use one, or leave standalone.
//
//  INTEGRATION WITH NPCBusController
//  ──────────────────────────────────
//  BusDepot calls two new public methods on NPCBusController:
//    · StartDepotEgress(List<Vector3> pathToRoad, TimetableSlot slot)
//    · StartDepotIngress(List<Vector3> pathToDepot, int bayIndex)
//  These are implemented in NPCBusController_DepotPatch.cs (companion file).
// ═══════════════════════════════════════════════════════════════════════════════

public class BusDepot : MonoBehaviour
{
    public static List<BusDepot> AllDepots { get; } = new List<BusDepot>();

    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Identity")]
    public string depotCode = "DEPOT_A";

    // [FIX Bug 2] BusDepot now points at the same DepotData asset
    // DepotManager uses for spawn placement, instead of maintaining its own
    // separate bay table. Assign the matching asset in the Inspector.
    [Tooltip("The DepotData asset this physical depot corresponds to. Must be the " +
             "same asset assigned to DepotManager's depots list, so both systems " +
             "read/write the same parking-spot occupancy table.")]
    public DepotData depotData;

    [Header("Gate — where buses enter the road network")]
    [Tooltip("World position of the depot gate. Buses path from here to the nearest road.")]
    public Transform gateTransform;

    [Header("Bays — legacy manual fallback")]
    [Tooltip("DEPRECATED as of the Bug 2 depot merge — occupancy and positions now " +
             "come from depotData.parkingSpots. These fields are read only as a " +
             "fallback if depotData is left unassigned (e.g. a standalone depot with " +
             "no DepotManager entry), so old scenes don't hard-break.")]
    public List<Transform> manualBays = new List<Transform>();

    [Tooltip("Auto-bay fallback: first bay origin (world space). Only used if depotData is unassigned.")]
    public Transform bayOrigin;

    [Tooltip("Auto-bay fallback: spacing between bays. Only used if depotData is unassigned.")]
    public float baySpacing = 8f;

    [Tooltip("Auto-bay fallback: number of bays to generate if manualBays is empty. Only used if depotData is unassigned.")]
    public int autoBayCount = 10;

    [Header("Buses registered to this depot")]
    public List<NPCBusController> registeredBuses = new List<NPCBusController>();

    [Header("Egress / Ingress")]
    [Tooltip("Speed multiplier while driving inside depot grounds.")]
    [Range(0.1f, 0.5f)] public float depotSpeedFraction = 0.25f;

    [Tooltip("How close (metres) to gate before the bus considers itself on the road.")]
    public float gateArrivalRadius = 8f;

    [Tooltip("How close (metres) to bay centre before the bus parks.")]
    public float bayArrivalRadius = 2.5f;

    [Header("Debug")]
    public bool logDepotEvents = true;

    // ── Runtime ───────────────────────────────────────────────────────────────
    // [FIX Bug 2] Legacy fallback-only table, used exclusively when depotData
    // is unassigned. When depotData IS assigned (the normal case), all
    // occupancy lives in depotData.parkingSpots via DepotManager — no
    // parallel table here, so it can't drift out of sync the way it used to.
    private int[] _fallbackBayOccupant;
    private Dictionary<int, int> _fallbackParkedAt = new Dictionary<int, int>();
    // busID → coroutine handle (so we can cancel ingress)
    private Dictionary<int, Coroutine> _activeCoroutines = new Dictionary<int, Coroutine>();

    private bool UsingDepotData => depotData != null && DepotManager.Instance != null;

    private Vector3 GatePos => gateTransform != null
        ? gateTransform.position
        : transform.position;

    // ═════════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═════════════════════════════════════════════════════════════════════════
    private void Awake()
    {
        AllDepots.Add(this);
        if (!UsingDepotData) InitFallbackBays();
    }

    private void OnDestroy() => AllDepots.Remove(this);

    private void Start()
    {
        // [FIX Bug 2] When depotData is assigned, DepotManager.BuildAndAssignFleet
        // has ALREADY spawned and placed every bus into its parkingSpots at
        // startup — that WAS the second, unsynced parking pass this Start()
        // used to duplicate (re-parking the same registeredBuses list into
        // BusDepot's own separate bay table, guaranteed to disagree with
        // DepotData's placement). Skip it entirely in that case; only run
        // the legacy startup-park loop for a standalone depot with no
        // DepotManager/DepotData wiring at all.
        if (UsingDepotData) return;

        for (int i = 0; i < registeredBuses.Count; i++)
        {
            var ctrl = registeredBuses[i];
            if (ctrl == null) continue;

            int bayIdx = ClaimNextFreeFallbackBay();
            if (bayIdx < 0)
            {
                Debug.LogWarning($"[BusDepot:{depotCode}] No free bay for Bus#{ctrl.fleetNumber} at startup.");
                continue;
            }

            ParkImmediateFallback(ctrl, bayIdx);
        }
    }

    private void InitFallbackBays()
    {
        int count = manualBays.Count > 0 ? manualBays.Count : autoBayCount;
        _fallbackBayOccupant = new int[count];
        for (int i = 0; i < count; i++) _fallbackBayOccupant[i] = -1;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC: EGRESS  (depot → road → terminal)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by BusScheduler/BusManager when a bus is dispatched from this depot.
    /// Builds the path from the bus's bay to the gate, then gate → terminal,
    /// and kicks off the egress coroutine on the controller.
    /// </summary>
    public void DispatchBus(NPCBusController ctrl, TimetableSlot slot)
    {
        if (ctrl == null || slot == null) return;

        int busID = ctrl.busID;

        // [FIX Bug 2] Release the claimed spot through DepotManager (sole
        // owner) instead of poking a local table. Keyed by fleetNumber, not
        // busID — see the ClaimSpotFor/ReleaseSpotFor comment in DepotManager.
        if (UsingDepotData)
        {
            DepotManager.Instance.ReleaseSpotFor(ctrl.fleetNumber);
        }
        else if (_fallbackParkedAt.TryGetValue(busID, out int bayIdx))
        {
            _fallbackBayOccupant[bayIdx] = -1;
            _fallbackParkedAt.Remove(busID);
        }

        // Build path: bay position → gate → terminal
        Vector3 startPos    = ctrl.transform.position;
        Vector3 gatePos     = GatePos;
        Vector3 terminalPos = ResolveTerminalPosition(slot);

        // Segment 1: bay to gate (straight; inside depot grounds)
        var depotPath = new List<Vector3> { startPos, gatePos };

        // Segment 2: gate to terminal using road pathfinder
        var roadPath = BusPathfinder.BuildPath(gatePos, terminalPos);
        if (roadPath.Count > 0) depotPath.AddRange(roadPath);
        else                     depotPath.Add(terminalPos); // fallback straight

        if (logDepotEvents)
            Debug.Log($"[BusDepot:{depotCode}] Dispatching Bus#{ctrl.fleetNumber} " +
                      $"→ Route {slot.FullRouteLabel} {slot.DirectionLabel}  " +
                      $"path={depotPath.Count} waypoints");

        // Tell the controller to go
        ctrl.StartDepotEgress(depotPath, slot, depotSpeedFraction);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC: INGRESS  (terminal → gate → bay)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called by NPCBusController when a bus finishes its final lap and is
    /// ready to return home.
    /// </summary>
    public void AcceptReturn(NPCBusController ctrl)
    {
        if (ctrl == null) return;

        int busID = ctrl.busID;
        Vector3 spotPos = Vector3.zero;
        int spotIndex = -1;
        bool hasSpot;

        // [FIX Bug 2] This used to always go through this depot's own
        // _bayOccupant/_parkedAt table, completely unsynced from
        // DepotData.parkingSpots. When that local table ran dry it fell back
        // to parking the bus "at the gate (untracked)" against gatePos — an
        // unset/default gateTransform explained the fixed (~300, 1500) dump
        // coordinate the doc describes. Now claim/release routes entirely
        // through DepotManager (sole owner of the ONE table), so there's no
        // second table left to disagree with it or run out independently.
        if (UsingDepotData)
        {
            spotIndex = DepotManager.Instance.ClaimSpotFor(depotData, ctrl.fleetNumber, out var spot);
            hasSpot = spot != null;
            if (hasSpot) spotPos = spot.position;

            if (!hasSpot && logDepotEvents)
                Debug.LogWarning($"[BusDepot:{depotCode}] No free parking spot in depotData for returning " +
                                  $"Bus#{ctrl.fleetNumber}; parking at gate (untracked) until one frees up.");
        }
        else
        {
            int bayIdx = ClaimNextFreeFallbackBay();
            hasSpot = bayIdx >= 0;
            spotIndex = bayIdx;

            if (!hasSpot)
            {
                // [FIX] This used to force bayIdx = 0 as a "best effort" fallback
                // WITHOUT checking whether bay 0 was already occupied — if it was,
                // this silently overwrote _fallbackBayOccupant[0] with the new
                // busID while the bus that was actually parked there kept its
                // own stale _fallbackParkedAt[oldBusID] = 0 entry, so two buses
                // ended up believing they owned the same bay. Instead: don't
                // claim any bay slot at all, just park at the gate and leave
                // the table untouched until a real bay frees up.
                if (logDepotEvents)
                    Debug.LogWarning($"[BusDepot:{depotCode}] No free fallback bay for returning Bus#{ctrl.fleetNumber}; parking at gate (untracked).");
            }
            else
            {
                _fallbackBayOccupant[bayIdx] = busID;
                _fallbackParkedAt[busID]     = bayIdx;
                spotPos = GetFallbackBayPosition(bayIdx);
            }
        }

        Vector3 gatePos  = GatePos;
        Vector3 startPos = ctrl.transform.position;

        var roadPath = BusPathfinder.BuildPath(startPos, gatePos);
        var fullPath = new List<Vector3>(roadPath.Count > 0 ? roadPath : new List<Vector3> { startPos });
        fullPath.Add(gatePos);
        if (hasSpot) fullPath.Add(spotPos);

        if (logDepotEvents)
            Debug.Log($"[BusDepot:{depotCode}] Bus#{ctrl.fleetNumber} returning to " +
                      (hasSpot ? "assigned spot" : "gate (no spot)") + $"  path={fullPath.Count} waypoints");

        ctrl.StartDepotIngress(fullPath, hasSpot ? spotIndex : -1, depotSpeedFraction);
    }

    /// <summary>
    /// Cancel a return-to-depot and re-dispatch the bus immediately.
    /// Called when the scheduler needs the bus back in service.
    /// </summary>
    public void CancelReturn(NPCBusController ctrl, TimetableSlot newSlot)
    {
        if (ctrl == null) return;

        int busID = ctrl.busID;

        // [FIX Bug 2] Release through DepotManager instead of a local table.
        if (UsingDepotData)
        {
            DepotManager.Instance.ReleaseSpotFor(ctrl.fleetNumber);
        }
        else if (_fallbackParkedAt.TryGetValue(busID, out int bayIdx))
        {
            _fallbackBayOccupant[bayIdx] = -1;
            _fallbackParkedAt.Remove(busID);
        }

        ctrl.AbortDepotIngress();
        DispatchBus(ctrl, newSlot);

        if (logDepotEvents)
            Debug.Log($"[BusDepot:{depotCode}] Bus#{ctrl.fleetNumber} return cancelled — re-dispatched.");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC: QUERY
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>True if this bus (by fleetNumber when depotData is assigned,
    /// else legacy busID lookup) currently holds a parked spot/bay.</summary>
    public bool IsBusParked(NPCBusController ctrl)
    {
        if (ctrl == null) return false;
        return UsingDepotData
            ? DepotManager.Instance.IsParked(ctrl.fleetNumber)
            : _fallbackParkedAt.ContainsKey(ctrl.busID);
    }

    public int  FreeBayCount => UsingDepotData
        ? DepotManager.Instance.FreeSpotCount(depotData)
        : CountFreeFallbackBays();
    public bool HasFreeBay   => FreeBayCount > 0;

    public Vector3 GetBayPosition(int bayIdx)
    {
        if (UsingDepotData && bayIdx >= 0 && bayIdx < depotData.parkingSpots.Count)
            return depotData.parkingSpots[bayIdx].position;

        return GetFallbackBayPosition(bayIdx);
    }

    public Quaternion GetBayRotation(int bayIdx)
    {
        if (UsingDepotData && bayIdx >= 0 && bayIdx < depotData.parkingSpots.Count)
            return Quaternion.Euler(depotData.parkingSpots[bayIdx].rotationEuler);

        return GetFallbackBayRotation(bayIdx);
    }

    private Vector3 GetFallbackBayPosition(int bayIdx)
    {
        if (manualBays.Count > 0 && bayIdx < manualBays.Count && manualBays[bayIdx] != null)
            return manualBays[bayIdx].position;

        if (bayOrigin != null)
            return bayOrigin.position + bayOrigin.right * (bayIdx * baySpacing);

        // Fallback: row along this depot's local X axis
        return transform.position + transform.right * (bayIdx * baySpacing);
    }

    private Quaternion GetFallbackBayRotation(int bayIdx)
    {
        if (manualBays.Count > 0 && bayIdx < manualBays.Count && manualBays[bayIdx] != null)
            return manualBays[bayIdx].rotation;
        return transform.rotation;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STATIC: find the depot a bus belongs to
    // ═════════════════════════════════════════════════════════════════════════

// ═════════════════════════════════════════════════════════════════════════════════
//  FIXED BusDepot.GetDepotForBus()  —  checks homeDepot field FIRST
// ═════════════════════════════════════════════════════════════════════════════════
//
// REPLACE these static methods in BusDepot.cs:
//

    /// <summary>
    /// Get the depot a bus was assigned to (via DepotManager when spawned).
    /// First checks the bus's homeDepot field (set by DepotManager), then
    /// falls back to checking registeredBuses list.
    /// </summary>
    public static BusDepot GetDepotForBus(NPCBusController ctrl)
    {
        if (ctrl == null) return null;

        // ── PRIMARY: check if bus has explicit homeDepot field set ──────────
        // (DepotManager sets this when spawning the fleet)
        if (ctrl.homeDepot != null)
        {
            // Find the BusDepot GameObject that wraps this DepotData
            foreach (var depot in AllDepots)
            {
                if (depot.depotCode == ctrl.homeDepot.depotCode)
                    return depot;
            }
        }

        // ── FALLBACK: legacy check in registeredBuses list ──────────────────
        foreach (var depot in AllDepots)
        {
            if (depot.registeredBuses != null && depot.registeredBuses.Contains(ctrl))
                return depot;
        }

        return null;
    }

    public static BusDepot GetDepotForBus(int busID)
    {
        // Try to find the NPCBusController by busID
        if (BusManager.Instance != null)
        {
            var record = BusManager.Instance.GetRecord(busID);
            var ctrl = record?.controller as NPCBusController;
            if (ctrl != null) return GetDepotForBus(ctrl);
        }

        // Fallback: search all registered buses
        foreach (var depot in AllDepots)
        {
            foreach (var ctrl in depot.registeredBuses)
            {
                if (ctrl != null && ctrl.busID == busID)
                    return depot;
            }
        }

        return null;
    }
    // ═════════════════════════════════════════════════════════════════════════
    //  PRIVATE HELPERS
    // ═════════════════════════════════════════════════════════════════════════

    private void ParkImmediateFallback(NPCBusController ctrl, int bayIdx)
    {
        _fallbackBayOccupant[bayIdx] = ctrl.busID;
        _fallbackParkedAt[ctrl.busID] = bayIdx;

        ctrl.transform.position     = GetFallbackBayPosition(bayIdx);
        ctrl.transform.rotation     = GetFallbackBayRotation(bayIdx);
        ctrl.SetIdle(true);

        if (logDepotEvents)
            Debug.Log($"[BusDepot:{depotCode}] Bus#{ctrl.fleetNumber} parked in fallback bay {bayIdx} at startup.");
    }

    private int ClaimNextFreeFallbackBay()
    {
        for (int i = 0; i < _fallbackBayOccupant.Length; i++)
            if (_fallbackBayOccupant[i] < 0) return i;
        return -1;
    }

    private int CountFreeFallbackBays()
    {
        int n = 0;
        for (int i = 0; i < _fallbackBayOccupant.Length; i++)
            if (_fallbackBayOccupant[i] < 0) n++;
        return n;
    }

    // Resolve the world position of the terminal the bus should drive to.
    // Uses the slot's route + direction to find terminal A or Z.
    private Vector3 ResolveTerminalPosition(TimetableSlot slot)
    {
        var scheduler = BusScheduler.Instance;
        if (scheduler == null) return GatePos;

        var route = scheduler.GetRouteData(slot.routeNumber);
        if (route == null) return GatePos;

        // Terminal the bus is heading TO first (outbound → terminal Z, inbound → A)
        string targetCode = slot.isOutbound ? route.terminalACode : route.terminalZCode;

        if (!string.IsNullOrEmpty(targetCode) && CityManager.Instance != null)
        {
            var stopData = CityManager.Instance.GetStop(targetCode);
            if (stopData != null) return stopData.GetWorldPosition();
        }

        // Fallback: first/last node of the route
        var nodes = route.GetNodes(slot.isOutbound);
        if (nodes != null && nodes.Count > 0)
            return nodes[0].position;

        return GatePos;
    }

    // ── Gizmos ────────────────────────────────────────────────────────────────
    private void OnDrawGizmos()
    {
        // Gate
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(GatePos, gateArrivalRadius);
        Gizmos.DrawRay(GatePos, Vector3.up * 3f);

        // Bays / spots
        if (depotData != null)
        {
            for (int i = 0; i < depotData.parkingSpots.Count; i++)
            {
                var s = depotData.parkingSpots[i];
                Vector3 bayPos = s.position;
                Gizmos.color = s.occupied ? new Color(1f, 0.3f, 0.3f, 0.6f) : new Color(0.3f, 1f, 0.5f, 0.5f);
                Gizmos.DrawWireCube(bayPos, new Vector3(3f, 1f, 8f));
                Gizmos.DrawRay(bayPos, Vector3.up * 0.5f);
            }
        }
        else
        {
            int count = manualBays.Count > 0 ? manualBays.Count : autoBayCount;
            for (int i = 0; i < count; i++)
            {
                Vector3 bayPos = GetFallbackBayPosition(i);
                bool occupied  = _fallbackBayOccupant != null && i < _fallbackBayOccupant.Length && _fallbackBayOccupant[i] >= 0;
                Gizmos.color = occupied ? new Color(1f, 0.3f, 0.3f, 0.6f) : new Color(0.3f, 1f, 0.5f, 0.5f);
                Gizmos.DrawWireCube(bayPos, new Vector3(3f, 1f, 8f));
                Gizmos.DrawRay(bayPos, Vector3.up * 0.5f);
            }
        }
    }
}