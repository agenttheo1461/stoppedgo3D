using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  MAINTENANCE BAY — v2, built on the standardized BusZone (see BusZone.cs).
//
//  [BREAKDOWN 3.0] Updated for the new BusVehicleSystem stat block
//  (Engine/FuelBattery/Transmission/Pneumatics/AC) and the condition-CAP
//  repair model: a repair no longer resets every part to a flat authored
//  number (the old repairedCondition = 100 approach) -- it calls
//  vehicle.RepairToMax(), which restores every stat to THIS bus's own
//  maxCondition ceiling (set from its BusCondition tier — Neglected/Worn/
//  Standard/Pristine). A Neglected bus gets repaired back to its own 50%
//  cap, not to a Pristine bus's 100%.
//
//  Behavior: a bus that BusBreakdownSystem.IsLimpingHome() and sits inside
//  this zone for repairDwellSeconds gets RepairToMax() called and
//  BusBreakdownSystem.OnReachedMaintenanceBay() fires to clear its
//  limping-home flag and hand it back to normal scheduling.
// ═══════════════════════════════════════════════════════════════════════════════
public class MaintenanceBay : BusZone
{
    // [ADD] Same pattern FuelStation.All already uses -- lets dispatch code
    // find "the maintenance bay assigned to depot X" instead of only being
    // able to react to a bus already standing inside one.
    public static readonly List<MaintenanceBay> All = new();

    [Header("Identity")]
    public string bayCode = "MAINT_A";

    [Header("Repair")]
    [Tooltip("Seconds a limping bus must sit inside this zone before its condition is reset and it's released back to service.")]
    public float repairDwellSeconds = 20f;

    [Header("Debug")]
    public bool logRepairs = true;

    protected override Color GizmoColor => new Color(1f, 0.7f, 0.1f, 0.6f);

    void OnEnable()  { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    /// <summary>[ADD] The bay explicitly assigned to a specific depot (via
    /// BusZone.homeDepot). Same reasoning as FuelStation.FindForDepot --
    /// sends a bus to ITS depot's own bay, not just whichever one happens to
    /// be nearest.</summary>
    public static MaintenanceBay FindForDepot(DepotData depot)
    {
        if (depot == null) return null;
        foreach (var b in All)
            if (b.homeDepot == depot) return b;
        return null;
    }

    // Per-bus dwell accumulator — reset to 0 the instant a bus leaves the
    // zone or stops needing repair, same "re-evaluate fresh" approach
    // FuelStation uses rather than a stateful enter/exit event pair.
    private readonly Dictionary<int, float> _dwellSeconds = new();

    // [FIX] Repair-need threshold, matching the fuel system's 30% rule
    // (repair below 70% condition == the same "don't bother if it's fine"
    // logic as "don't refuel above 30% fuel"). Was ONLY IsLimpingHome --
    // meaning a bus that simply wore down over normal service (never hit a
    // requiresReplacement breakdown) could sit at 40% condition forever,
    // never repaired, since nothing besides a replace-tier breakdown ever
    // flagged it as needing one.
    private const float MAINTENANCE_THRESHOLD_FRACTION = 0.70f;

    protected override void OnBusPollResult(NPCBusController bus, bool inside, float elapsedSeconds)
    {
        var vehicle = bus.GetComponent<BusVehicleSystem>();
        bool limpingHome = BusBreakdownSystem.Instance != null && BusBreakdownSystem.Instance.IsLimpingHome(bus.busID);
        bool worn = vehicle != null && vehicle.maxCondition > 0f
            && (vehicle.WorstCondition / vehicle.maxCondition) < MAINTENANCE_THRESHOLD_FRACTION;
        bool needsRepair = limpingHome || worn;

        if (!inside || !needsRepair)
        {
            _dwellSeconds.Remove(bus.busID);
            return;
        }

        float t = (_dwellSeconds.TryGetValue(bus.busID, out var existing) ? existing : 0f) + elapsedSeconds;
        _dwellSeconds[bus.busID] = t;
        if (t < repairDwellSeconds) return;

        float repairedTo = -1f;
        if (vehicle != null)
        {
            vehicle.RepairToMax();
            repairedTo = vehicle.maxCondition;
        }

        BusBreakdownSystem.Instance?.OnReachedMaintenanceBay(bus.busID);
        _dwellSeconds.Remove(bus.busID);

        if (logRepairs)
        {
            string pct = repairedTo >= 0f ? $"{repairedTo:0}%" : "unknown%";
            Debug.Log($"[MaintenanceBay:{bayCode}] Bus#{bus.busID} repaired to {pct} (its condition ceiling) and released back to service.");
        }
    }
}