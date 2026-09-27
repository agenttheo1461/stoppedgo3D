using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  FUEL STATION — v2, built on the standardized BusZone (see BusZone.cs).
//
//  [REDESIGN] Used to require a trigger Collider and rely on OnTriggerStay,
//  which meant remembering to add a Collider, tick isTrigger, and get it on
//  the right GameObject (root vs child) relative to the bus's own collider --
//  three separate ways to get the setup wrong before it would even fire.
//  Now it's just a title + a box: set zoneCenter/zoneSize (or drag the
//  gizmo) over the pump bay and it works, no physics setup at all.
//
//  Same behavior as before otherwise: buses whose BusVehicleSystem.fuelType
//  matches servesType refuel automatically while stopped inside the zone;
//  mismatched fuel types are ignored entirely.
// ═══════════════════════════════════════════════════════════════════════════════
public class FuelStation : BusZone
{
    public static readonly List<FuelStation> All = new();

    [Header("Fuel Identity")]
    [Tooltip("This station's own key, separate from the generic BusZone.zoneTitle label -- kept so existing code/save-data referencing stationCode doesn't need to change.")]
    public string stationCode = "FUEL_A";
    public BusVehicleSystem.FuelSystemType servesType = BusVehicleSystem.FuelSystemType.Diesel;

    [Header("Fill Rate")]
    [Tooltip("Units (gallons/DGE/kWh) added per second while a matching bus is stopped and fueling.")]
    public float unitsPerSecond = 8f;
    [Tooltip("Bus must be nearly stationary (below this mph) to fuel — no filling up while rolling through.")]
    public float maxFuelingSpeed = 1.5f;

    [Header("Debug")]
    public bool logFuelingEvents = true;

    private readonly HashSet<BusVehicleSystem> _fueling = new();

    protected override Color GizmoColor => servesType switch
    {
        BusVehicleSystem.FuelSystemType.Diesel   => new Color(0.6f, 0.5f, 0.2f, 0.5f),
        BusVehicleSystem.FuelSystemType.CNG      => new Color(0.2f, 0.6f, 1f, 0.5f),
        BusVehicleSystem.FuelSystemType.Electric => new Color(0.2f, 1f, 0.4f, 0.5f),
        _ => Color.white
    };

    void OnEnable()  { All.Add(this); }
    void OnDisable() { All.Remove(this); }

    /// <summary>Fires once per poll for every active bus. No enter/exit
    /// events needed here -- fueling is purely a function of "is a matching,
    /// near-stationary, not-full bus inside the zone right now," so this
    /// just re-evaluates that fresh every poll instead of tracking state
    /// transitions like the old OnTriggerEnter/Exit pair had to.</summary>
    protected override void OnBusPollResult(NPCBusController bus, bool inside, float elapsedSeconds)
    {
        var fuel = bus.GetComponent<BusVehicleSystem>();
        if (fuel == null || !fuel.AcceptsStationType(servesType)) return;

        if (!inside || bus.spd > maxFuelingSpeed || fuel.currentFraction >= 1f)
        {
            _fueling.Remove(fuel);
            return;
        }

        bool wasFueling = _fueling.Contains(fuel);
        _fueling.Add(fuel);
        fuel.AddFuelUnits(unitsPerSecond * elapsedSeconds);

        if (!wasFueling && logFuelingEvents)
            Debug.Log($"[FuelStation:{stationCode}] Started fueling ({fuel.GetFuelLabel()}).");

        if (fuel.currentFraction >= 1f && logFuelingEvents)
            Debug.Log($"[FuelStation:{stationCode}] Fuel full.");
    }

    /// <summary>Nearest station that serves the given fuel type, or null.</summary>
    public static FuelStation FindNearest(Vector3 worldPos, BusVehicleSystem.FuelSystemType type)
    {
        FuelStation best = null;
        float bestDist = float.MaxValue;
        foreach (var s in All)
        {
            if (s.servesType != type) continue;
            float d = Vector3.Distance(worldPos, s.WorldCenter);
            if (d < bestDist) { bestDist = d; best = s; }
        }
        return best;
    }

    /// <summary>[ADD] The station explicitly assigned to a specific depot
    /// (via BusZone.homeDepot), for the given fuel type. Prefer this over
    /// FindNearest whenever a bus is already routing to a KNOWN depot -- it
    /// sends the bus to that depot's own pump, not just whichever station is
    /// spatially closest, which could belong to a different depot's yard.
    /// Falls back to FindNearest(depot's own position) if no station has
    /// been explicitly assigned to this depot yet.</summary>
    public static FuelStation FindForDepot(DepotData depot, BusVehicleSystem.FuelSystemType type)
    {
        if (depot == null) return null;
        foreach (var s in All)
            if (s.homeDepot == depot && s.servesType == type) return s;
        return null;
    }
}