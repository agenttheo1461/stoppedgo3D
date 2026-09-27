using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROAD EVENT SPAWNER
//
//  Place this on a persistent manager GameObject (e.g. alongside CityManager).
//  Author a list of RoadEventSpawnPoint components in the scene — these are the
//  AUTHORIZED locations where events CAN happen. At runtime the spawner rolls
//  which location activates, what type, what severity, and how long it lasts.
//
//  Flow:
//    Start → begin SpawnLoop coroutine
//    SpawnLoop → every checkIntervalSeconds, roll whether to spawn
//    If spawn → pick a random eligible SpawnPoint → activate the RoadEvent on it
//    RoadEvent counts down its own duration → calls Deactivate()
//    Deactivate() → notifies this spawner → spawner starts cooldown on that point
//    After cooldown → point is eligible again
// ═══════════════════════════════════════════════════════════════════════════════

[System.Serializable]
public class RoadEventSpawnPoint
{
    [Tooltip("The RoadEvent component that lives at this location. " +
             "Author it fully in the Inspector (waypoints, skipped stops, route filter etc). " +
             "The spawner controls when it activates and with what severity/type/duration.")]
    public RoadEvent roadEvent;

    [Tooltip("Override the severity roll for this specific location. " +
             "Leave as Minor to use the global spawner roll.")]
    public bool overrideSeverity = false;
    public RoadEventSeverity forcedSeverity = RoadEventSeverity.Moderate;

    [Tooltip("Override which event types can happen here. Empty = any type.")]
    public List<RoadEventType> allowedTypes = new();

    [Tooltip("Minimum real-world seconds between activations at this location.")]
    public float cooldownSeconds = 120f;

    // Runtime
    [System.NonSerialized] public bool  onCooldown      = false;
    [System.NonSerialized] public float cooldownEndsAt   = 0f;  // Time.time
}

public class RoadEventSpawner : MonoBehaviour
{
    public static RoadEventSpawner Instance { get; private set; }

    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Spawn Points")]
    [Tooltip("All authorized locations where road events can be dynamically activated.")]
    public List<RoadEventSpawnPoint> spawnPoints = new();

    [Header("Spawn Timing")]
    [Tooltip("Real seconds between each roll for a new event.")]
    public float checkIntervalSeconds  = 45f;

    [Tooltip("Probability (0–1) of spawning an event each check interval. " +
             "0 = never, 1 = always.")]
    [Range(0f, 1f)]
    public float spawnChance           = 0.35f;

    [Tooltip("Maximum number of events active at the same time.")]
    public int   maxConcurrentEvents   = 2;

    [Header("Severity Weights (must sum to 1)")]
    [Range(0f, 1f)] public float minorWeight    = 0.50f;
    [Range(0f, 1f)] public float moderateWeight = 0.35f;
    [Range(0f, 1f)] public float majorWeight    = 0.15f;

    [Header("Duration Ranges (game-minutes)")]
    public Vector2 minorDurationRange    = new Vector2(8f,  20f);
    public Vector2 moderateDurationRange = new Vector2(15f, 40f);
    public Vector2 majorDurationRange    = new Vector2(25f, 60f);

    [Header("Debug")]
    public bool logSpawns = true;

    // ── Runtime ───────────────────────────────────────────────────────────────
    private int _activeCount = 0;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        // Deactivate all events at start — spawner controls when they fire
        foreach (var sp in spawnPoints)
            if (sp.roadEvent != null && sp.roadEvent.spawnedDynamically)
                sp.roadEvent.isActive = false;

        StartCoroutine(SpawnLoop());
        StartCoroutine(CooldownWatch());
    }

    // ── Spawn loop ────────────────────────────────────────────────────────────
    private IEnumerator SpawnLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(checkIntervalSeconds);

            if (_activeCount >= maxConcurrentEvents) continue;
            if (Random.value > spawnChance)          continue;

            TrySpawnEvent();
        }
    }

    private void TrySpawnEvent()
    {
        // Build list of eligible points
        var eligible = new List<RoadEventSpawnPoint>();
        foreach (var sp in spawnPoints)
        {
            if (sp.roadEvent == null)     continue;
            if (sp.roadEvent.isActive)    continue;
            if (sp.onCooldown)            continue;
            eligible.Add(sp);
        }

        if (eligible.Count == 0)
        {
            if (logSpawns) Debug.Log("[RoadEventSpawner] No eligible spawn points.");
            return;
        }

        var chosen   = eligible[Random.Range(0, eligible.Count)];
        var severity = chosen.overrideSeverity ? chosen.forcedSeverity : RollSeverity();
        var type     = RollType(chosen);
        float dur    = RollDuration(severity);

        // Apply rolled values to the event
        chosen.roadEvent.severity      = severity;
        chosen.roadEvent.eventType     = type;
        chosen.roadEvent.durationMinutes = dur;
        chosen.roadEvent.eventLabel    = $"{type} on {chosen.roadEvent.name}";

        float now = BusScheduler.Instance != null
            ? BusScheduler.Instance.GameTimeMinutes : 0f;
        chosen.roadEvent.Activate(now);

        _activeCount++;

        // Notify registry so it picks up the newly active event
        RoadEventRegistry.Instance?.RefreshEvents();

        // Notify live map
        MDT_LiveMap.Instance?.OnRoadEventActivated(chosen.roadEvent);

        if (logSpawns)
            Debug.Log($"[RoadEventSpawner] Spawned {severity} {type} at '{chosen.roadEvent.name}' " +
                      $"for {dur:F0} game-min.");
    }

    // ── Called by RoadEvent when it self-clears ───────────────────────────────
    public void OnEventCleared(RoadEvent ev)
    {
        _activeCount = Mathf.Max(0, _activeCount - 1);

        // Find the spawn point and start its cooldown
        foreach (var sp in spawnPoints)
        {
            if (sp.roadEvent != ev) continue;
            sp.onCooldown    = true;
            sp.cooldownEndsAt = Time.time + sp.cooldownSeconds;
            break;
        }

        // Notify live map
        MDT_LiveMap.Instance?.OnRoadEventCleared(ev);
        RoadEventRegistry.Instance?.RefreshEvents();
    }

    // ── Cooldown watch ────────────────────────────────────────────────────────
    private IEnumerator CooldownWatch()
    {
        while (true)
        {
            yield return new WaitForSeconds(5f);
            foreach (var sp in spawnPoints)
                if (sp.onCooldown && Time.time >= sp.cooldownEndsAt)
                    sp.onCooldown = false;
        }
    }

    // ── Rolls ─────────────────────────────────────────────────────────────────
    private RoadEventSeverity RollSeverity()
    {
        float r = Random.value;
        if (r < minorWeight)                        return RoadEventSeverity.Minor;
        if (r < minorWeight + moderateWeight)       return RoadEventSeverity.Moderate;
        return RoadEventSeverity.Major;
    }

    private RoadEventType RollType(RoadEventSpawnPoint sp)
    {
        var pool = (sp.allowedTypes != null && sp.allowedTypes.Count > 0)
            ? sp.allowedTypes
            : new List<RoadEventType>((RoadEventType[])System.Enum.GetValues(typeof(RoadEventType)));
        return pool[Random.Range(0, pool.Count)];
    }

    private float RollDuration(RoadEventSeverity sev)
    {
        Vector2 range = sev switch
        {
            RoadEventSeverity.Minor    => minorDurationRange,
            RoadEventSeverity.Moderate => moderateDurationRange,
            RoadEventSeverity.Major    => majorDurationRange,
            _                          => moderateDurationRange
        };
        return Random.Range(range.x, range.y);
    }

    // ── Public API ────────────────────────────────────────────────────────────
    /// <summary>Force-spawn an event at a specific index for testing.</summary>
    public void ForceSpawnAt(int spawnPointIndex)
    {
        if (spawnPointIndex < 0 || spawnPointIndex >= spawnPoints.Count) return;
        var sp = spawnPoints[spawnPointIndex];
        if (sp.roadEvent == null) return;

        sp.onCooldown = false;
        _activeCount  = Mathf.Max(0, _activeCount - 1); // allow it through cap
        TrySpawnEvent();
    }

    public int ActiveEventCount => _activeCount;
}