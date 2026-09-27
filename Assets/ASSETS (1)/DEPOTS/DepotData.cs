using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  DEPOT DATA — defines a depot's location, parking layout, and route ownership
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class DepotParkingSpot
{
    public Vector3 position;
    public Vector3 rotationEuler;
    [System.NonSerialized] public bool occupied;
    // [FIX] Renamed from occupiedByBusID -- the field never actually held a
    // busID (the disposable, per-scene-instance id). By DepotManager's own
    // established convention it always holds fleetNumber, the persistent
    // roster identity. The old name actively invited exactly the bug it
    // caused more than once: code reaching for ctrl.busID here because
    // that's what the field name says, when fleetNumber was always correct.
    [System.NonSerialized] public int  occupiedByFleetNumber = -1;
}

[CreateAssetMenu(menuName = "Transit/Depot", fileName = "Depot_XX")]
public class DepotData : ScriptableObject
{
    [Header("Identity")]
    public string depotCode;     // e.g. "NORTH", "CENTRAL"
    public string depotName;     // e.g. "North Depot"

    [Header("Routes Served")]
    [Tooltip("Route numbers (without variant letters) this depot may dispatch buses for. " +
             "e.g. '1' covers both '1' and any '1A'/'1B' variants.")]
    public List<string> servedRouteNumbers = new();

    [Header("Parking")]
    public List<DepotParkingSpot> parkingSpots = new();

    [Header("Fleet Preferences")]
    [Tooltip("If set, this depot only accepts buses whose busType is in this list. " +
             "Leave empty to accept any type.")]
    public List<string> preferredBusTypes = new(); // e.g. "XD40", "XN40", "XDE60"

    // [ADD] Was completely missing -- despite fuel infrastructure (hydrogen
    // specifically) being a real planning topic, DepotData had zero
    // awareness of which fuel types a depot can actually service. A depot
    // with no hydrogen fueling station has no business being assigned buses
    // that need one, same logic as preferredBusTypes above but for the
    // fuel/energy side instead of body type.
    [Header("Fuel Infrastructure")]
    [Tooltip("Which fuel types this depot can actually service (fueling stations physically present). Leave empty to accept any -- matches preferredBusTypes' empty-means-any convention.")]
    public List<BusVehicleSystem.FuelSystemType> fuelInfrastructure = new();
    [Tooltip("If true, this depot's hydrogen infrastructure (if Electric is in fuelInfrastructure) specifically serves fuel-cell buses, not just battery-electric. Purely informational -- BusVehicleSystem doesn't distinguish battery vs fuel-cell at the fuelType level, both are Electric.")]
    public bool servesHydrogenFuelCell = false;

    public bool AcceptsFuelType(BusVehicleSystem.FuelSystemType type)
    {
        if (fuelInfrastructure == null || fuelInfrastructure.Count == 0) return true;
        return fuelInfrastructure.Contains(type);
    }

    [Tooltip("If true, articulated buses (XDE60 etc.) are preferred/required here.")]
    public bool articulatedOnly = false;

    public enum BusLengthCategory { Ft35, Ft40, Ft60 }

    [Tooltip("Preferred bus length for this depot's parking spot generator. " +
             "Ft60 is locked to articulatedOnly — picking Ft60 forces articulatedOnly on, " +
             "and turning articulatedOnly on forces this to Ft60 — the two are always kept " +
             "consistent so the spot-spacing math (which still keys off articulatedOnly) " +
             "never disagrees with the selected length.")]
    public BusLengthCategory preferredLengthCategory = BusLengthCategory.Ft40;

    public bool ServesRoute(string routeNumber)
    {
        // Strip trailing variant letters: "1A" -> "1"
        string baseRoute = StripVariantLetter(routeNumber);
        foreach (var r in servedRouteNumbers)
            if (StripVariantLetter(r) == baseRoute) return true;
        return false;
    }

    public static string StripVariantLetter(string route)
    {
        if (string.IsNullOrEmpty(route)) return route;
        int i = route.Length - 1;
        while (i >= 0 && (char.IsLetter(route[i]) || route[i] == '~')) i--;
        return route.Substring(0, i + 1);
    }

    public bool AcceptsBusType(string busType)
    {
        if (preferredBusTypes == null || preferredBusTypes.Count == 0) return true;
        return preferredBusTypes.Contains(busType);
    }

    public DepotParkingSpot GetFreeSpot()
    {
        foreach (var s in parkingSpots)
            if (!s.occupied) return s;
        return null;
    }

// In DepotData.cs — replace the existing OnValidate block with this:
#if UNITY_EDITOR
[Header("Parking Spot Generator")]
[Tooltip("World position of the first spot's centre.")]
public Vector3 genOrigin      = Vector3.zero;
[Tooltip("Direction buses face when parked (world space).")]
public Vector3 genFacing      = Vector3.forward;
[Tooltip("Direction rows spread laterally (world space).")]
public Vector3 genLateral     = Vector3.right;
public int     genRows        = 1;
public int     genSpotsPerRow = 5;
[Tooltip("Gap between spot centres along the row (auto-set from bus length if 0).")]
public float   genSpotGap     = 0f;   // 0 = auto
[Tooltip("Gap between row centres laterally (auto-set from bus width if 0).")]
public float   genRowGap      = 0f;   // 0 = auto

[ContextMenu("Generate Parking Spots")]
private void GenerateParkingSpots()
{
    float busLen = preferredLengthCategory switch
    {
        BusLengthCategory.Ft35 => 6f,   // 5 + 1 gap
        BusLengthCategory.Ft60 => 14f,  // 13 + 1 gap
        _                      => 8f,   // Ft40 — 7 + 1 gap
    };
    float busWidth = 3.0f;                          // 2.5 + 0.5 gap

    float spotStep = genSpotGap > 0f ? genSpotGap : busLen;
    float rowStep  = genRowGap  > 0f ? genRowGap  : busWidth;

    Vector3 facing  = genFacing.sqrMagnitude  > 0f ? genFacing.normalized  : Vector3.forward;
    Vector3 lateral = genLateral.sqrMagnitude > 0f ? genLateral.normalized : Vector3.right;
    Quaternion rot  = Quaternion.LookRotation(facing, Vector3.up);

    parkingSpots.Clear();
    for (int row = 0; row < genRows; row++)
        for (int s = 0; s < genSpotsPerRow; s++)
            parkingSpots.Add(new DepotParkingSpot
            {
                position     = genOrigin + lateral * (row * rowStep) + facing * (s * spotStep),
                rotationEuler = rot.eulerAngles
            });

    UnityEditor.EditorUtility.SetDirty(this);
    Debug.Log($"[DepotData] {depotCode}: generated {parkingSpots.Count} spots " +
              $"({genRows} row × {genSpotsPerRow}) — spacing {spotStep:0.0} × {rowStep:0.0}m");
}

[ContextMenu("Clear Parking Spots")]
private void ClearParkingSpots()
{
    parkingSpots.Clear();
    UnityEditor.EditorUtility.SetDirty(this);
}

private BusLengthCategory _lastLengthCategory;
private bool _lastArticulatedOnly;

private void OnValidate()
{
    if (string.IsNullOrEmpty(depotCode) && !string.IsNullOrEmpty(name))
        depotCode = name.ToUpper();

    // Keep preferredLengthCategory and articulatedOnly locked together — whichever
    // one just changed in the Inspector wins, so either field can drive the other.
    if (preferredLengthCategory != _lastLengthCategory)
    {
        articulatedOnly = preferredLengthCategory == BusLengthCategory.Ft60;
    }
    else if (articulatedOnly != _lastArticulatedOnly)
    {
        if (articulatedOnly && preferredLengthCategory != BusLengthCategory.Ft60)
            preferredLengthCategory = BusLengthCategory.Ft60;
        else if (!articulatedOnly && preferredLengthCategory == BusLengthCategory.Ft60)
            preferredLengthCategory = BusLengthCategory.Ft40;
    }

    _lastLengthCategory  = preferredLengthCategory;
    _lastArticulatedOnly = articulatedOnly;
}
#endif
}