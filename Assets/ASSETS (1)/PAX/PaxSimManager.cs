using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  PAX AGENT  —  pure simulation data, NOT a MonoBehaviour, NOT a GameObject.
//
//  Hundreds of these can exist with near-zero cost. A PaxAgent only gets a
//  visible cube (PaxVisualPool) when it's within render distance of the
//  camera. When it walks out of range, the cube is returned to the pool but
//  the PaxAgent keeps simulating its position/state in the background.
// ═══════════════════════════════════════════════════════════════════════════════
public enum PaxState
{
    WalkingToStop,      // walking the sidewalk graph toward a chosen stop
    WaitingAtStop,       // queued at a stop, waiting for a usable bus
    BoardingBus,         // mid single-file walk into a stopped bus's door
    OnBus,                // riding — no position simulation, owned by the bus
    AlightingBus,         // mid single-file walk out of a stopped bus's door
    WalkingAway,          // walked off after alighting, heading to despawn point
    Despawned
}

public class PaxAgent
{
    public int       id;
    public Vector3   position;
    public PaxState  state;

    // Sidewalk path currently being walked (world points)
    public List<Vector3> path;
    public int            pathIdx;

    // Stop intent
    public string targetStopCode;     // stop we're walking to / waiting at
    public string destinationStopCode; // stop we intend to alight at once boarded

    // Bus linkage while boarding/riding/alighting
    public int    boardedBusID = -1;   // NPC busID, or -1 if none / -2 for player bus
    public bool    isOnPlayerBus = false;

    public float  walkSpeed = 1.35f;   // m/s, roughly human walking pace
    public float  stateTimer = 0f;     // generic per-state timer (used for despawn delay etc.)

    // Visual binding — null when off-screen / not currently rendered
    public PaxVisual visual;

    public bool IsVisible => visual != null;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  PAX SIM MANAGER  —  the city-wide brain.
//
//  · Spawns pax at random sidewalk points over time (population target).
//  · Each pax walks the sidewalk graph to a randomly chosen nearby stop.
//  · Maintains per-stop queues (StopPaxQueue) that piles up while no bus is
//    present and drains as buses board people.
//  · After alighting, pax walk away from the stop and despawn.
//  · Visibility culling: every tick, pax within visualRadius of the camera
//    get a pooled cube; others release theirs back to the pool.
//
//  NPCBusController and PlayerHandoff talk to this via:
//    GetQueue(stopCode)                  → StopPaxQueue
//    BeginBoardingSequence(...)          → starts single-file walk-on, returns handle
//    BeginAlightingSequence(...)         → starts single-file walk-off, returns handle
// ═══════════════════════════════════════════════════════════════════════════════
public class PaxSimManager : MonoBehaviour
{
    public static PaxSimManager Instance { get; private set; }

    [Header("Population")]
    [Tooltip("Target number of simultaneously-simulated pax across the whole city.")]
    public int populationTarget = 400;
    [Tooltip("How many new pax can spawn per second while under the population target.")]
    public float spawnRatePerSecond = 3f;

    [Header("Boarding Timing")]
    [Tooltip("Seconds for one passenger to physically step onto/off a bus.")]
    public float perPaxBoardSeconds = 1.4f;
    [Tooltip("Extra gap between consecutive pax in the single-file line.")]
    public float paxFileGap = 0.55f;

    [Header("Visual Culling")]
    public float visualRadius = 9999f;
    [Tooltip("How often (seconds) we re-scan for visibility changes. Doesn't need to be every frame.")]
    public float visibilityScanInterval = 0.4f;

    [Header("Despawn")]
    public float despawnWalkAwayDuration = 6f;

    [Header("Debug")]
    public bool logSpawns = false;

    private readonly List<PaxAgent> _agents = new List<PaxAgent>();
    private readonly Dictionary<string, StopPaxQueue> _stopQueues = new Dictionary<string, StopPaxQueue>();
    private int _nextPaxID = 0;
    private float _spawnAccum = 0f;
    private float _visScanTimer = 0f;
    private System.Random _rng = new System.Random();

    private CityManager _city;
    private Transform _cameraTransform;

    public IReadOnlyList<PaxAgent> Agents => _agents;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        _city = CityManager.Instance;
        if (Camera.main != null) _cameraTransform = Camera.main.transform;

        if (SidewalkNetwork.Instance != null && _city != null)
            SidewalkNetwork.Instance.BuildFromCity(_city);

        // Pre-create a queue entry for every known stop so callers never get null.
        if (_city != null && _city.AllStops != null)
            foreach (var s in _city.AllStops)
                if (s != null) _stopQueues[s.stopCode] = new StopPaxQueue(s.stopCode);
    }

    private void Update()
    {
        float dt = Time.deltaTime;

        SpawnTick(dt);
        SimulationTick(dt);

        _visScanTimer -= dt;
        if (_visScanTimer <= 0f)
        {
            _visScanTimer = visibilityScanInterval;
            VisibilityScan();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  SPAWNING
    // ═════════════════════════════════════════════════════════════════════════
    private void SpawnTick(float dt)
    {
        if (SidewalkNetwork.Instance == null || !SidewalkNetwork.Instance.IsBuilt) return;
        if (_agents.Count >= populationTarget) return;

        _spawnAccum += dt * spawnRatePerSecond;
        while (_spawnAccum >= 1f && _agents.Count < populationTarget)
        {
            _spawnAccum -= 1f;
            SpawnOnePax();
        }
    }

    private void SpawnOnePax()
    {
        var net = SidewalkNetwork.Instance;
        int startNode = net.GetRandomNode(_rng);
        if (startNode < 0) return;

        string destStop = ChooseRandomStop();
        if (destStop == null) return;

        var agent = new PaxAgent
        {
            id              = _nextPaxID++,
            position        = net.GetNode(startNode).position,
            state           = PaxState.WalkingToStop,
            targetStopCode  = destStop,
            walkSpeed       = Mathf.Lerp(1.1f, 1.6f, (float)_rng.NextDouble()),
        };

        var accessNodes = net.GetStopAccessNodes(destStop);
        int goalNode = (accessNodes != null && accessNodes.Count > 0)
            ? accessNodes[_rng.Next(accessNodes.Count)]
            : net.GetRandomNode(_rng);

        agent.path    = net.FindPath(startNode, goalNode);
        agent.pathIdx = 0;

        // Also decide where they intend to go once they board — any other stop
        agent.destinationStopCode = ChooseRandomStop(exclude: destStop);

        _agents.Add(agent);
        if (logSpawns) Debug.Log($"[PaxSim] Spawned pax#{agent.id} → {destStop}");
    }

    private string ChooseRandomStop(string exclude = null)
    {
        if (_city == null || _city.AllStops == null || _city.AllStops.Length == 0) return null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var s = _city.AllStops[_rng.Next(_city.AllStops.Length)];
            if (s == null) continue;
            if (exclude != null && s.stopCode == exclude) continue;
            return s.stopCode;
        }
        return _city.AllStops[0]?.stopCode;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  SIMULATION
    // ═════════════════════════════════════════════════════════════════════════
    private void SimulationTick(float dt)
    {
        for (int i = _agents.Count - 1; i >= 0; i--)
        {
            var a = _agents[i];
            switch (a.state)
            {
                case PaxState.WalkingToStop: TickWalking(a, dt, arrivedState: PaxState.WaitingAtStop); break;
                case PaxState.WaitingAtStop: /* handled by StopPaxQueue + bus boarding sequence */ break;
                case PaxState.BoardingBus:    /* driven externally by BusBoardingSequencer */ break;
                case PaxState.OnBus:          /* no position sim while riding */ break;
                case PaxState.AlightingBus:   /* driven externally by BusBoardingSequencer */ break;
                case PaxState.WalkingAway:
                    TickWalking(a, dt, arrivedState: PaxState.Despawned);
                    break;
                case PaxState.Despawned:
                    ReleaseVisual(a);
                    _agents.RemoveAt(i);
                    break;
            }

            if (a.state == PaxState.WaitingAtStop)
                EnsureQueued(a);
        }
    }

    private void TickWalking(PaxAgent a, float dt, PaxState arrivedState)
    {
        if (a.path == null || a.path.Count == 0 || a.pathIdx >= a.path.Count)
        {
            a.state = arrivedState;
            return;
        }

        Vector3 target = a.path[a.pathIdx];
        Vector3 dir    = target - a.position; dir.y = 0f;
        float   dist   = dir.magnitude;
        float   step    = a.walkSpeed * dt;

        if (dist <= step)
        {
            a.position = new Vector3(target.x, a.position.y, target.z);
            a.pathIdx++;
            if (a.pathIdx >= a.path.Count) a.state = arrivedState;
        }
        else
        {
            a.position += dir.normalized * step;
        }

        if (a.visual != null) a.visual.SyncTransform(a, dir);
    }

    private void EnsureQueued(PaxAgent a)
    {
        var q = GetQueue(a.targetStopCode);
        q?.EnsureWaiting(a);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  VISIBILITY CULLING
    // ═════════════════════════════════════════════════════════════════════════
private void VisibilityScan()
{
    Debug.Log($"[SCAN START] Checking {_agents.Count} agents. Pool exists? {PaxVisualPool.Instance != null}");
    
    foreach (var a in _agents)
    {
        bool shouldBeVisible = a.state != PaxState.OnBus && a.state != PaxState.Despawned;

        if (shouldBeVisible && a.visual == null)
        {
            if (PaxVisualPool.Instance == null) 
            {
                Debug.LogError("[CRITICAL] Pool instance is NULL! The manager can't fetch cubes!");
                continue;
            }

            a.visual = PaxVisualPool.Instance.Acquire(a);
            
            if (a.visual == null)
            {
                Debug.LogError($"[CRITICAL] Pool returned a NULL visual for Pax#{a.id}! Is the pool out of ammo?");
                continue;
            }

            // We successfully grabbed one. Force it awake.
            a.visual.gameObject.SetActive(true);
            a.visual.SnapTo(a.position);
            
            Debug.Log($"[SUCCESS] Pax#{a.id} put on suit '{a.visual.gameObject.name}' and warped to {a.position}!");
            
            if (!a.visual.gameObject.activeInHierarchy)
            {
                Debug.LogError($"[GHOST] {a.visual.gameObject.name} was turned on, but Unity says it's STILL inactive! Parent: {a.visual.transform.parent?.name}");
            }
        }
    }
}

    private void ReleaseVisual(PaxAgent a)
    {
        if (a.visual == null) return;
        PaxVisualPool.Instance?.Release(a.visual);
        a.visual = null;
    }

    /// <summary>Public hook for BusBoardingSequencer — releases a pax's cube once they
    /// board (riding pax don't need a free-standing visual; the bus represents them).</summary>
    public void ReleaseVisualPublic(PaxAgent a) => ReleaseVisual(a);

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC API — consumed by NPCBusController / PlayerHandoff
    // ═════════════════════════════════════════════════════════════════════════
    public StopPaxQueue GetQueue(string stopCode)
    {
        if (string.IsNullOrEmpty(stopCode)) return null;
        if (!_stopQueues.TryGetValue(stopCode, out var q))
            _stopQueues[stopCode] = q = new StopPaxQueue(stopCode);
        return q;
    }

    /// <summary>Pull up to `count` waiting pax from a stop's queue, claimed for boarding.</summary>
    public List<PaxAgent> ClaimBoardingPax(string stopCode, int count)
    {
        var q = GetQueue(stopCode);
        return q != null ? q.ClaimForBoarding(count) : new List<PaxAgent>();
    }

    /// <summary>Spawn `count` onboard pax for alighting at this stop (used when the
    /// bus's own onboard counter has more pax than visually tracked — keeps the
    /// abstract counters in PlayerHandoff/NPC dwell logic and the visual sim in sync).</summary>
    public List<PaxAgent> SpawnAlightingPax(string stopCode, Vector3 doorWorldPos, int count)
    {
        var result = new List<PaxAgent>();
        var net    = SidewalkNetwork.Instance;
        if (net == null) return result;

        for (int i = 0; i < count; i++)
        {
            var a = new PaxAgent
            {
                id        = _nextPaxID++,
                position  = doorWorldPos,
                state     = PaxState.AlightingBus,
                walkSpeed = Mathf.Lerp(1.1f, 1.6f, (float)_rng.NextDouble()),
            };
            _agents.Add(a);
            result.Add(a);
        }
        return result;
    }

    /// <summary>Called once a pax has fully exited the bus — sends them walking away to despawn.</summary>
    public void ReleaseAfterAlighting(PaxAgent a, Vector3 fromPos)
    {
        var net = SidewalkNetwork.Instance;
        int startNode = net != null ? net.FindNearestNode(fromPos, 40f) : -1;

        a.state    = PaxState.WalkingAway;
        a.stateTimer = 0f;

        if (net != null && startNode >= 0)
        {
            int wanderTarget = net.GetRandomNode(_rng);
            a.path    = net.FindPath(startNode, wanderTarget);
            a.pathIdx = 0;
        }
        else
        {
            a.path = new List<Vector3> { fromPos + Random.insideUnitSphere * 10f };
            a.pathIdx = 0;
        }
    }

    public int CountWaiting(string stopCode) => GetQueue(stopCode)?.WaitingCount ?? 0;
}