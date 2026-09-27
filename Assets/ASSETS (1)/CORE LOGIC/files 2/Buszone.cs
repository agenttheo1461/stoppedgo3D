using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS ZONE — standardized replacement for the Collider/isTrigger/OnTriggerStay
//  pattern FuelStation (and presumably MaintenanceBay) used to rely on.
//
//  Every place a bus needs to "be near" something (fuel pump, maintenance bay,
//  depot gate, whatever comes next) is now just: a title string + a world-
//  space box. No Collider component to add, no isTrigger checkbox to
//  remember, no physics layer matrix to configure, no guessing whether the
//  trigger needs to be on the bus's root or a child collider. Drag the box
//  gizmo (or type numbers) until it covers the spot, done.
//
//  Concrete zones (FuelStation, MaintenanceBay, ...) inherit this and
//  implement OnBusPollResult, which fires once per poll for every bus
//  currently registered in BusRegistry.ActiveBuses, with a plain "is it
//  inside" bool -- same information a trigger would have given you, from a
//  cheap box check instead of physics.
// ═══════════════════════════════════════════════════════════════════════════════
public abstract class BusZone : MonoBehaviour
{
    [Header("Zone Identity")]
    [Tooltip("Free-form label for this zone -- shown in the scene gizmo and in logs. Concrete subclasses (FuelStation.stationCode, MaintenanceBay.bayCode) can layer their own more specific key on top of this if needed.")]
    public string zoneTitle = "ZONE";

    [Header("Depot Assignment")]
    [Tooltip("Which depot this zone belongs to. Assign the same DepotData asset the depot's own buses use as their homeDepot -- this is how dispatch logic finds THIS depot's fuel/maintenance zone specifically, instead of just whichever one happens to be nearest by distance.")]
    public DepotData homeDepot;

    [Header("Zone Volume (world-space box, no Collider needed)")]
    [Tooltip("Offset for the zone's center. Treated as LOCAL to this transform when useTransformAsCenter is on (so moving the object moves the zone with it) -- otherwise treated as an absolute world position.")]
    public Vector3 zoneCenter = Vector3.zero;
    public bool useTransformAsCenter = true;
    [Tooltip("Full width / height / depth of the zone box, in world units.")]
    public Vector3 zoneSize = new Vector3(8f, 4f, 8f);

    [Header("Poll Rate")]
    [Tooltip("How often (seconds) this zone re-scans BusRegistry.ActiveBuses. 0 = every frame. Most zones don't need per-frame precision -- 0.2-0.5s is plenty and cheaper at fleet scale.")]
    public float pollInterval = 0.25f;

    private float _pollTimer;

    public Vector3 WorldCenter => useTransformAsCenter ? transform.TransformPoint(zoneCenter) : zoneCenter;

    /// <summary>The entire "is this bus here" test -- an axis-aligned box
    /// around WorldCenter. No physics involved.</summary>
    public bool Contains(Vector3 worldPos)
    {
        Vector3 c = WorldCenter;
        Vector3 h = zoneSize * 0.5f;
        return Mathf.Abs(worldPos.x - c.x) <= h.x
            && Mathf.Abs(worldPos.y - c.y) <= h.y
            && Mathf.Abs(worldPos.z - c.z) <= h.z;
    }

    protected virtual void Update()
    {
        _pollTimer -= Time.deltaTime;
        if (_pollTimer > 0f) return;

        // Elapsed includes any overshoot past pollInterval, so subclasses
        // doing continuous accumulation (fuel units, repair dwell time,
        // etc.) stay accurate even if a poll fires a frame or two late.
        float elapsed = pollInterval - _pollTimer;
        _pollTimer = pollInterval;

        if (BusRegistry.ActiveBuses == null) return;
        foreach (var kv in BusRegistry.ActiveBuses)
        {
            var bus = kv.Value;
            if (bus == null) continue;
            OnBusPollResult(bus, Contains(bus.transform.position), Mathf.Max(elapsed, 0.001f));
        }
    }

    /// <summary>Called once per poll for every registered bus, with whether
    /// it's currently inside this zone and how many seconds have elapsed
    /// since the last poll (for rate-based effects like fueling). Track
    /// your own "was inside last poll" state per busID if you need
    /// enter/exit edges -- see FuelStation for the reference pattern.</summary>
    protected abstract void OnBusPollResult(NPCBusController bus, bool inside, float elapsedSeconds);

    private void OnDrawGizmos()
    {
        Gizmos.color = GizmoColor;
        Gizmos.DrawWireCube(WorldCenter, zoneSize);
#if UNITY_EDITOR
        UnityEditor.Handles.Label(WorldCenter + Vector3.up * (zoneSize.y * 0.5f + 1f), zoneTitle);
#endif
    }

    /// <summary>Override to color-code the gizmo per subclass (e.g. FuelStation
    /// tints by fuel type). Defaults to a neutral teal.</summary>
    protected virtual Color GizmoColor => new Color(0.3f, 0.9f, 0.5f, 0.6f);
}