using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  TRAFFIC MANAGER  —  builds the RoadGraph once from CityManager's roads and
//  owns the pool of AICarController traffic. Separate from BusScheduler/
//  CityManager on purpose — traffic doesn't need routes, stops, or scheduling,
//  just "a graph and a destination."
//
//  PERF SHAPE (this is the part that keeps a big city from frying):
//   · maxCars hard-caps how many are ever alive at once.
//   · Cars only spawn in a RING around the player (spawnRingInner..Outer) —
//     never right on top of them, never off somewhere they'll never see.
//   · Cars beyond despawnDistance are removed, freeing a slot for a fresh
//     spawn nearer the action instead of endlessly simulating traffic three
//     city blocks away that nobody's looking at.
//   · Spawns are staggered (spawnRatePerSecond) instead of dumping maxCars
//     worth of Instantiate + pathfind calls into a single frame.
// ═══════════════════════════════════════════════════════════════════════════════
public class TrafficManager : MonoBehaviour
{
    public static TrafficManager Instance { get; private set; }

    [Header("Car Prefabs")]
    [Tooltip("One or more car prefabs — a random one is picked per spawn for visual variety. Each needs a Collider (kinematic is fine) and ideally an AICarController already on it, though one will be added automatically if missing.")]
    public GameObject[] carPrefabs;

    [Header("Population")]
    [Tooltip("Max AI cars alive at once. Keep this conservative — each car pathfinds on spawn/arrival and does an obstacle SphereCast every frame.")]
    public int maxCars = 40;
    [Tooltip("Cars spawned per second while under maxCars, staggered so spawns/pathfinding don't all land on one frame.")]
    public float spawnRatePerSecond = 2f;

    [Header("Spawn / Despawn Range (relative to player)")]
    public Transform player;
    [Tooltip("Cars only spawn within this ring around the player — inner radius avoids spawning on top of them, outer radius keeps a spawn from being wasted somewhere off in the distance.")]
    public float spawnRingInner = 60f;
    public float spawnRingOuter = 220f;
    [Tooltip("Cars beyond this distance from the player are despawned and their slot freed.")]
    public float despawnDistance = 320f;

    [Header("Destination Picking")]
    [Tooltip("A freshly-picked destination node must be at least this far from the car's current position, so arrivals don't just turn into an immediate U-turn.")]
    public float minDestinationDistance = 250f;

    public RoadGraph Graph { get; private set; }

    private readonly List<AICarController> _activeCars = new();
    private float _spawnAccumulator;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private IEnumerator Start()
    {
        // Wait a frame so CityManager has finished building its roads first —
        // BuildGraph needs cm.GetRoad(...) to actually return something.
        yield return null;
        BuildGraph();
    }

    private void BuildGraph()
    {
        var cm = CityManager.Instance;
        if (cm == null || cm.roadDefinitions == null)
        {
            Debug.LogWarning("[TrafficManager] No CityManager/roads found — traffic disabled.");
            return;
        }

        var roads = new List<RoadSegment>();
        var defs  = new List<RoadSegmentDefinition>();
        foreach (var def in cm.roadDefinitions)
        {
            if (def == null) continue;
            var road = cm.GetRoad(def.roadCode);
            if (road == null) continue;
            roads.Add(road);
            defs.Add(def);
        }

        if (roads.Count == 0)
        {
            Debug.LogWarning("[TrafficManager] CityManager has no resolvable roads — traffic disabled.");
            return;
        }

        Graph = RoadGraph.Build(roads, defs);
        Debug.Log($"[TrafficManager] Road graph built: {Graph.nodes.Count} nodes from {roads.Count} roads.");
    }

    private void Update()
    {
        if (Graph == null || player == null || carPrefabs == null || carPrefabs.Length == 0) return;

        // ── Despawn anything too far from the player ────────────────────────
        for (int i = _activeCars.Count - 1; i >= 0; i--)
        {
            var car = _activeCars[i];
            if (car == null) { _activeCars.RemoveAt(i); continue; }
            if (Vector3.Distance(car.transform.position, player.position) > despawnDistance)
            {
                _activeCars.RemoveAt(i);
                Destroy(car.gameObject);
            }
        }

        // ── Staggered spawning up to maxCars ─────────────────────────────────
        if (_activeCars.Count >= maxCars) return;

        _spawnAccumulator += Time.deltaTime * spawnRatePerSecond;
        while (_spawnAccumulator >= 1f && _activeCars.Count < maxCars)
        {
            _spawnAccumulator -= 1f;
            TrySpawnCar();
        }
    }

    private void TrySpawnCar()
    {
        RoadNode spawnNode = PickRingNode();
        if (spawnNode == null) return;

        RoadNode destNode = PickFarNode(spawnNode.position);
        if (destNode == null) return;

        var prefab = carPrefabs[Random.Range(0, carPrefabs.Length)];
        var go = Instantiate(prefab, spawnNode.position, Quaternion.identity);

        var car = go.GetComponent<AICarController>();
        if (car == null) car = go.AddComponent<AICarController>();

        car.Initialize(Graph, destNode.position, this);
        _activeCars.Add(car);
    }

    /// <summary>Picks a random graph node within the spawn ring around the
    /// player. A handful of tries, not exhaustive — if the graph is sparse in
    /// that ring on a given attempt it just skips the spawn this tick and
    /// tries again next accumulator tick.</summary>
    private RoadNode PickRingNode()
    {
        const int attempts = 20;
        for (int i = 0; i < attempts; i++)
        {
            var node = Graph.nodes[Random.Range(0, Graph.nodes.Count)];
            float d = Vector3.Distance(node.position, player.position);
            if (d >= spawnRingInner && d <= spawnRingOuter) return node;
        }
        return null;
    }

    /// <summary>Public so AICarController can request a fresh destination the
    /// moment it arrives at its current one — "goal, then find another one
    /// across the map" per the brief. Perpetual traffic, no idle cars.</summary>
    public RoadNode PickFarNode(Vector3 fromPos)
    {
        const int attempts = 20;
        RoadNode best = null;
        float bestDist = 0f;
        for (int i = 0; i < attempts; i++)
        {
            var node = Graph.nodes[Random.Range(0, Graph.nodes.Count)];
            float d = Vector3.Distance(node.position, fromPos);
            if (d >= minDestinationDistance) return node; // first good-enough pick is fine
            if (d > bestDist) { bestDist = d; best = node; } // fallback: furthest one tried
        }
        return best;
    }
}
