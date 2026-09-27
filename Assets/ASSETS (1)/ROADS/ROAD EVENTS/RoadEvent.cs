using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROAD EVENT  v2
//
//  WHAT'S NEW vs v1
//  ────────────────
//  [1] Severity system
//      · Minor   — one lane blocked; buses slow to a crawl through the zone,
//                  no diversion, no stop skipping.
//      · Moderate — full diversion; buses routed around via waypoints.
//      · Major    — full diversion at reduced speed cap (emergency vehicles
//                  present); buses approach slower and drive detour slower.
//
//  [2] Lifecycle
//      · Events have a duration in game-minutes drawn from a per-severity
//        range. They auto-clear when time expires.
//      · Optional: RoadEventSpawner can re-activate events after a cooldown.
//
//  [3] Authorized spawn locations
//      · isActive is now driven by RoadEventSpawner; hand-placed events
//        still work by leaving spawnedDynamically = false.
//      · The event stores its own spawn time for duration tracking.
//
//  [4] Skipped stop tracking
//      · GetSkippedStopCodes(bool outbound) returns the stop codes that
//        fall between the event detection point and the reentry point.
//        NPCBusController calls this on diversion start to advance
//        _nextStopIdx past stops the bus cannot physically serve.
//
//  [5] Facing fix
//      · GetDetourPositions now returns positions with Y matched to the
//        calling bus's position (passed in). No more hardcoded Y.
//      · Waypoints stored as Transforms so scene authoring is visual;
//        Y override applied at call time not bake time.
//
//  [6] Speed caps per severity
//      · GetSpeedCapFraction() returns a 0–1 multiplier. NPCBusController
//        applies this to baseTargetSpeed during detour traversal.
//        Minor: 0.35 (crawl through), Moderate: 0.75, Major: 0.45.
// ═══════════════════════════════════════════════════════════════════════════════

public enum RoadEventSeverity
{
    Minor,    // slow through, no diversion
    Moderate, // divert at normal detour speed
    Major     // divert at reduced speed (emergency vehicles)
}

public enum RoadEventType
{
    TrafficCrash,
    WaterMainBreak,
    RoadWork,
    PowerLinDown,
    FloodingRoad,
    FireIncident,
    PoliceActivity,
    DebrisOnRoad
}

/// <summary>An object that lives with a road event: spawned when it activates, destroyed when it clears.</summary>
[System.Serializable]
public class RoadEventProp
{
    public GameObject prefab;
    [Tooltip("Offset from the event's world position.")]
    public Vector3 localOffset;
    public float   yawDegrees;
    public float   scale = 1f;
    [System.NonSerialized] public GameObject instance;
}

[System.Serializable]
public class RoadEvent : MonoBehaviour
{
    [Header("World Position")]
[Tooltip("The actual world position of the incident. " +
         "This drives detection, the map dot, and gizmos. " +
         "The GameObject transform position is ignored.")]
public Vector3 eventWorldPosition;
    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Event Identity")]
    public string       eventLabel  = "Road Event";
    public RoadEventType eventType  = RoadEventType.TrafficCrash;
    public RoadEventSeverity severity = RoadEventSeverity.Moderate;

    [Header("Activation")]
    [Tooltip("Toggle manually for hand-placed events. Driven by RoadEventSpawner for dynamic ones.")]
    public bool  isActive          = true;
    public bool  spawnedDynamically = false;

    [Header("Duration (game-minutes). Ignored if durationMinutes = 0 (permanent until toggled).")]
    public float durationMinutes   = 0f;   // 0 = manual control only
    private float _spawnedAtGameMinutes = -1f;

    [Header("Detection")]
    public float detectionRadius   = 20f;

    [Header("Affected Stops")]
    [Tooltip("Any stop within this many metres of the event is treated as closed for routes this event applies to " +
             "(non-Minor events only). 0 = only the hand-listed skipped stop codes below. " +
             "This is what the scheduler-side systems, tracker, timetable and driver HUD read.")]
    public float affectedStopRadius = 0f;

    [Tooltip("Extra minutes a diverted trip takes. Added to tracker ETAs while the event is active.")]
    public float detourExtraMinutes = 3f;

    [Header("Housed Objects")]
    [Tooltip("Scene objects that belong to this event (cones, barriers, a crashed car...). " +
             "Shown while the event is active, hidden when it clears.")]
    public List<GameObject> housedObjects = new();

    [Tooltip("Prefabs spawned at the event when it activates and destroyed when it clears.")]
    public List<RoadEventProp> spawnedProps = new();

    /// <summary>Fired for every event that activates / clears, so any system can react without polling.</summary>
    public static event System.Action<RoadEvent> Activated;
    public static event System.Action<RoadEvent> Cleared;

    [Header("Route Filter")]
    [Tooltip("Leave empty = applies to all routes.")]
    public List<string> allowedRoutes = new();

[Header("Outbound Detour  (A → Z)")]
public List<Vector3> outboundDetourNodes  = new();
public Vector3       outboundReentryPoint;
public bool          outboundReentrySet   = false;

[Header("Inbound Detour  (Z → A)")]
public List<Vector3> inboundDetourNodes   = new();
public Vector3       inboundReentryPoint;
public bool          inboundReentrySet    = false;

    [Header("Stop Skip — codes to skip when bus is diverted outbound")]
    [Tooltip("List the stopCodes that are physically inside or past the blockage " +
             "but before the reentry point. NPCBusController will advance _nextStopIdx " +
             "past these automatically on diversion start.")]
    public List<string> outboundSkippedStopCodes = new();
    public List<string> inboundSkippedStopCodes  = new();

    // ── Runtime ───────────────────────────────────────────────────────────────
    private readonly HashSet<int> _divertedBusIDs = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private bool _lastActive;
    private bool _syncedOnce;

    private void Start() => SyncActiveState();

    private void OnDestroy() => DestroyProps();

    private void Update()
    {
        // Picks up manual toggles of isActive too (hand-placed events), not just Activate()/Deactivate().
        if (!_syncedOnce || _lastActive != isActive) SyncActiveState();

        if (!isActive || durationMinutes <= 0f || _spawnedAtGameMinutes < 0f) return;
        if (BusScheduler.Instance == null) return;

        float elapsed = BusScheduler.Instance.GameTimeMinutes - _spawnedAtGameMinutes;
        if (elapsed >= durationMinutes)
            Deactivate();
    }

    private void SyncActiveState()
    {
        bool first = !_syncedOnce;
        _syncedOnce = true;
        _lastActive = isActive;

        foreach (var go in housedObjects) if (go != null) go.SetActive(isActive);
        if (isActive) SpawnProps(); else DestroyProps();

        if (!first) { if (isActive) Activated?.Invoke(this); else Cleared?.Invoke(this); }
        else if (isActive) Activated?.Invoke(this);
    }

    private void SpawnProps()
    {
        foreach (var pr in spawnedProps)
        {
            if (pr == null || pr.prefab == null || pr.instance != null) continue;
            var rot = Quaternion.Euler(0f, pr.yawDegrees, 0f);
            pr.instance = Instantiate(pr.prefab, eventWorldPosition + pr.localOffset, rot, transform);
            pr.instance.transform.localScale *= pr.scale;
        }
    }

    private void DestroyProps()
    {
        foreach (var pr in spawnedProps)
        {
            if (pr == null || pr.instance == null) continue;
            Destroy(pr.instance);
            pr.instance = null;
        }
    }

    public void Activate(float gameMinutesNow)
    {
        isActive              = true;
        _spawnedAtGameMinutes = gameMinutesNow;
        _divertedBusIDs.Clear();
        Debug.Log($"[RoadEvent] '{eventLabel}' ({severity} {eventType}) ACTIVATED. " +
                  $"Duration: {(durationMinutes > 0 ? durationMinutes + " game-min" : "manual")}");
        SyncActiveState();
    }

    public void Deactivate()
    {
        isActive = false;
        _divertedBusIDs.Clear();
        Debug.Log($"[RoadEvent] '{eventLabel}' CLEARED.");
        SyncActiveState();

        // Notify spawner so it can start cooldown
        RoadEventSpawner.Instance?.OnEventCleared(this);
    }

    // ── Stop closure ──────────────────────────────────────────────────────────
    /// <summary>
    /// True if this event closes the given stop for the given route/direction. Minor events never close stops.
    /// A stop is closed if it is on the hand-listed skip list for that direction, or lies within affectedStopRadius.
    /// </summary>
    public bool ClosesStop(string stopCode, string routeNumber, bool outbound)
    {
        if (!isActive || severity == RoadEventSeverity.Minor || string.IsNullOrEmpty(stopCode)) return false;
        if (!AppliesToRoute(routeNumber)) return false;

        var listed = outbound ? outboundSkippedStopCodes : inboundSkippedStopCodes;
        if (listed != null && listed.Contains(stopCode)) return true;

        if (affectedStopRadius > 0f && CityManager.Instance != null)
        {
            var st = CityManager.Instance.GetStop(stopCode);
            if (st != null)
            {
                Vector3 p = st.GetWorldPosition();
                float dx = p.x - eventWorldPosition.x, dz = p.z - eventWorldPosition.z;
                if (dx * dx + dz * dz <= affectedStopRadius * affectedStopRadius) return true;
            }
        }
        return false;
    }

    public float RemainingMinutes()
    {
        if (!isActive || durationMinutes <= 0f || _spawnedAtGameMinutes < 0f) return -1f;
        if (BusScheduler.Instance == null) return -1f;
        return Mathf.Max(0f, durationMinutes - (BusScheduler.Instance.GameTimeMinutes - _spawnedAtGameMinutes));
    }

    // ── Speed cap ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Fraction of baseTargetSpeed the bus should use while traversing this event.
    /// For Minor events this is the crawl-through cap; for diversions it's the
    /// detour traversal cap.
    /// </summary>
    public float GetSpeedCapFraction()
    {
        return severity switch
        {
            RoadEventSeverity.Minor    => 0.30f,
            RoadEventSeverity.Moderate => 0.75f,
            RoadEventSeverity.Major    => 0.42f,
            _                          => 0.75f
        };
    }

public bool IsInRange(Vector3 worldPos)
{
    if (!isActive) return false;
    Vector3 flat   = new Vector3(worldPos.x, eventWorldPosition.y, worldPos.z);
    float   distSq = (flat - eventWorldPosition).sqrMagnitude;
    return distSq <= detectionRadius * detectionRadius;
}


    public bool AppliesToRoute(string routeNumber)
    {
        if (allowedRoutes == null || allowedRoutes.Count == 0) return true;
        return allowedRoutes.Contains(routeNumber);
    }

    // ── Diversion tracking ────────────────────────────────────────────────────
    public bool IsBusDiverted(int busID)     => _divertedBusIDs.Contains(busID);
    public void RegisterDiversion(int busID) => _divertedBusIDs.Add(busID);
    public void ClearDiversion(int busID)    => _divertedBusIDs.Remove(busID);

    public void ClearAllDiversionsForBus(int busID)
    {
        bool was = _divertedBusIDs.Remove(busID);
        if (was)
            Debug.Log($"[RoadEvent '{eventLabel}'] Cleared stale diversion for Bus#{busID}.");
    }

    // ── Waypoints ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Returns detour world positions with Y matched to the bus's current Y
    /// so buses don't face backward due to Y mismatch.
    /// </summary>
public List<Vector3> GetDetourPositions(bool outbound, float busY = 0f)
{
    var src = outbound ? outboundDetourNodes : inboundDetourNodes;
    if (src == null || src.Count == 0) return null;

    var result = new List<Vector3>(src.Count);
    foreach (var p in src)
    {
        Vector3 wp = p;
        wp.y = busY;
        result.Add(wp);
    }
    return result.Count > 0 ? result : null;
}

public Vector3? GetReentryPosition(bool outbound, float busY = 0f)
{
    bool set = outbound ? outboundReentrySet : inboundReentrySet;
    if (!set) return null;

    Vector3 p = outbound ? outboundReentryPoint : inboundReentryPoint;
    p.y = busY;
    return p;
}
    // ── Stop skipping ─────────────────────────────────────────────────────────
    /// <summary>
    /// Returns the stop codes that the bus cannot serve because they are
    /// physically behind/inside the blockage. NPCBusController advances
    /// _nextStopIdx past these on diversion start so the tracker and
    /// scheduler don't get stuck waiting for arrivals that never come.
    /// </summary>
    public List<string> GetSkippedStopCodes(bool outbound)
    {
        return outbound ? outboundSkippedStopCodes : inboundSkippedStopCodes;
    }

    // ── Player notification text ──────────────────────────────────────────────
    /// <summary>
    /// Short string for DriverConsole radio chatter.
    /// e.g. "DISPATCH: Road closure — Water Main Break ahead on your route."
    /// </summary>
    public string GetDriverConsoleMessage()
    {
        string typeStr = eventType switch
        {
            RoadEventType.TrafficCrash    => "Traffic Crash",
            RoadEventType.WaterMainBreak  => "Water Main Break",
            RoadEventType.RoadWork        => "Road Works",
            RoadEventType.PowerLinDown    => "Power Line Down",
            RoadEventType.FloodingRoad    => "Road Flooding",
            RoadEventType.FireIncident    => "Fire Incident",
            RoadEventType.PoliceActivity  => "Police Activity",
            RoadEventType.DebrisOnRoad    => "Debris on Road",
            _                             => "Road Incident"
        };

        string sevStr = severity switch
        {
            RoadEventSeverity.Minor    => "Expect delays.",
            RoadEventSeverity.Moderate => "Diversion in effect.",
            RoadEventSeverity.Major    => "Major diversion — proceed with caution.",
            _                          => ""
        };

        return $"DISPATCH: {typeStr} on {eventLabel}. {sevStr}";
    }

    /// <summary>Short label for MDT_LiveMap overlay chip.</summary>
    public string GetMapLabel()
    {
        string icon = severity switch
        {
            RoadEventSeverity.Minor    => "⚠",
            RoadEventSeverity.Moderate => "🚧",
            RoadEventSeverity.Major    => "🚨",
            _                          => "⚠"
        };
        return $"{icon} {eventType}";
    }

    // ── Gizmos ────────────────────────────────────────────────────────────────
    private void OnDrawGizmos()
    {
        Color col = severity switch
        {
            RoadEventSeverity.Minor    => new Color(1f, 1f, 0f, isActive ? 0.30f : 0.10f),
            RoadEventSeverity.Moderate => new Color(1f, 0.5f, 0f, isActive ? 0.30f : 0.10f),
            RoadEventSeverity.Major    => new Color(1f, 0.1f, 0.1f, isActive ? 0.35f : 0.10f),
            _                          => new Color(1f, 1f, 1f, 0.20f)
        };

        Gizmos.color = col;
        Gizmos.DrawSphere(transform.position, detectionRadius);
        col.a = isActive ? 0.85f : 0.30f;
        Gizmos.color = col;
        Gizmos.DrawWireSphere(transform.position, detectionRadius);
DrawDetourGizmo(outboundDetourNodes, outboundReentryPoint, outboundReentrySet, Color.cyan);
DrawDetourGizmo(inboundDetourNodes,  inboundReentryPoint,  inboundReentrySet,  Color.magenta);
#if UNITY_EDITOR
        string durStr = durationMinutes > 0
            ? $"{RemainingMinutes():F0}min left"
            : "manual";
        UnityEditor.Handles.Label(
    eventWorldPosition + Vector3.up * (detectionRadius + 1f),
            $"[{eventLabel}] {severity} {eventType}\n" +
            $"{(isActive ? "ACTIVE" : "inactive")} | {durStr} | diverted: {_divertedBusIDs.Count}");
#endif
    }

private void DrawDetourGizmo(List<Vector3> nodes, Vector3 reentry, bool reentrySet, Color col)
{
    if (nodes == null || nodes.Count == 0) return;
    Gizmos.color = col;
    for (int i = 0; i < nodes.Count - 1; i++)
        Gizmos.DrawLine(nodes[i], nodes[i + 1]);
    foreach (var n in nodes)
Gizmos.DrawSphere(eventWorldPosition, detectionRadius);
    if (reentrySet)
    {
        Gizmos.color = Color.white;
Gizmos.DrawWireSphere(eventWorldPosition, detectionRadius);
        if (nodes.Count > 0)
            Gizmos.DrawLine(nodes[nodes.Count - 1], reentry);
    }
}
}