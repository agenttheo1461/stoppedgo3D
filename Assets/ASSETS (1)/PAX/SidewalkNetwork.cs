using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  SIDEWALK NETWORK
//
//  Auto-builds a walkable waypoint graph from CityManager's road definitions —
//  no navmesh, no manual placement. For each road we sample two curb-offset
//  polylines (left + right side, mirroring BusStopData's curb offset logic),
//  and at every detected road/road intersection we drop a small junction hub
//  that all four curb-line ends near that point connect through (this is the
//  "cross at intersections" behaviour — pax must pass through a hub node to
//  switch from one road's sidewalk to another, or to cross to the opposite side).
//
//  Coordinate model:
//    SidewalkNode  — a single waypoint, world position + small metadata
//    SidewalkEdge  — a walkable connection between two nodes (adjacency list)
//
//  This graph is built once at Start (after CityManager finishes its own
//  Start) and is read-only at runtime. It's intentionally simple — straight
//  line segments between samples — because pax are small cubes and don't need
//  smooth spline walking.
// ═══════════════════════════════════════════════════════════════════════════════
public class SidewalkNode
{
    public int      id;
    public Vector3  position;
    public bool      isJunction;
    public bool      isStopAccess;     // node sits right next to a bus stop
    public string    nearestStopCode;  // set if isStopAccess
    public string    roadCode;         // which road this curb node belongs to ("" for junction hubs)
    public List<int> neighbours = new List<int>();
}

public class SidewalkNetwork : MonoBehaviour
{
    public static SidewalkNetwork Instance { get; private set; }

    [Header("Generation")]
    [Tooltip("Lateral distance from road centreline to the sidewalk curb line.")]
    public float curbOffset = 4.0f;
    [Tooltip("World-unit spacing between sidewalk sample nodes along a road.")]
    public float sampleSpacing = 12f;
    [Tooltip("Junction hub radius — curb nodes within this distance of a detected road/road crossing get folded into the hub instead of kept as separate curb nodes.")]
    public float junctionMergeRadius = 14f;
    [Tooltip("Max distance a bus stop will search for its nearest sidewalk node.")]
    public float stopAccessSearchRadius = 25f;

    [Header("Debug")]
    public bool drawGizmos = false;

    private readonly List<SidewalkNode> _nodes = new List<SidewalkNode>();
    private readonly Dictionary<string, List<int>> _stopAccessNodes = new Dictionary<string, List<int>>();
    private bool _built = false;

    public bool IsBuilt => _built;
    public IReadOnlyList<SidewalkNode> Nodes => _nodes;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    // Called explicitly by PaxSimManager.Start() after CityManager has resolved
    // its roads/stops, rather than racing it via Unity's own Start ordering.
    public void BuildFromCity(CityManager city)
    {
        if (_built || city == null) return;
        _built = true;

        _nodes.Clear();
        _stopAccessNodes.Clear();

        // ── Step 1: junction hubs from CityManager's already-computed intersection list ──
        var junctionHubs = new List<(Vector3 pos, int nodeId)>();
        List<Vector3> intersections = city.GetSpawnedIntersectionPositions();

        if (intersections != null)
        {
            foreach (var pos in intersections)
            {
                var hub = NewNode(pos, isJunction: true, roadCode: "");
                junctionHubs.Add((pos, hub.id));
            }
        }

        // ── Step 2: curb polylines per road, both sides ──
// ── Step 2: curb polylines per road, both sides ──
if (city.roadDefinitions != null)
{
    foreach (var def in city.roadDefinitions)
    {
        if (def == null || string.IsNullOrEmpty(def.roadCode)) continue;
        var road = city.GetRoad(def.roadCode);
        if (road == null) continue;

        if (!def.hasSidewalks) continue; 

        // YOUR MATH: Half the width to reach the edge, plus a 0.5m buffer
        float dynamicCurbOffset = (def.roadWidth / 2f) + 0.5f; 
BuildCurbLine(road, def.roadCode, dynamicCurbOffset, +1f, junctionHubs);
        BuildCurbLine(road, def.roadCode, dynamicCurbOffset, -1f, junctionHubs);
    }
}

        // ── Step 3: connect every bus stop to its nearest sidewalk node ──
        if (city.AllStops != null)
        {
            foreach (var stop in city.AllStops)
            {
                if (stop == null || stop.resolvedRoad == null) continue;
                Vector3 stopPos = stop.GetWorldPosition();

                int nearest = FindNearestNode(stopPos, stopAccessSearchRadius, n => !n.isJunction);
                if (nearest < 0) continue;

                _nodes[nearest].isStopAccess    = true;
                _nodes[nearest].nearestStopCode = stop.stopCode;

                if (!_stopAccessNodes.TryGetValue(stop.stopCode, out var list))
                    _stopAccessNodes[stop.stopCode] = list = new List<int>();
                list.Add(nearest);
            }
        }

        Debug.Log($"[SidewalkNetwork] Built {_nodes.Count} nodes " +
                  $"({junctionHubs.Count} junction hubs) for {_stopAccessNodes.Count} stops.");
    }

 private void BuildCurbLine(RoadSegment road, string roadCode, float currentOffset, float side, List<(Vector3 pos, int nodeId)> hubs)
{
    float length = EstimateRoadLength(road);
    int   steps  = Mathf.Max(2, Mathf.CeilToInt(length / sampleSpacing));

    int prevNodeId = -1;
    for (int i = 0; i <= steps; i++)
    {
        float t = i / (float)steps;
        Vector3 centre  = road.EvaluatePosition(t);
        Vector3 tangent = road.EvaluateTangent(t);
        Vector3 right   = Vector3.Cross(Vector3.up, tangent).normalized;
        
        // 🟢 Uses your new dynamic offset instead of the deleted universal one
        Vector3 curbPos = centre + right * (currentOffset * side);

        // If close enough to a junction hub, snap into it instead of a fresh node
        int hubMatch = FindNearbyHub(curbPos, hubs);
        int thisNodeId;

        if (hubMatch >= 0)
        {
            thisNodeId = hubMatch;
        }
        else
        {
            var node = NewNode(curbPos, isJunction: false, roadCode: roadCode);
            thisNodeId = node.id;
        }

        if (prevNodeId >= 0 && prevNodeId != thisNodeId)
            LinkNodes(prevNodeId, thisNodeId);

        prevNodeId = thisNodeId;
    }
}

    private int FindNearbyHub(Vector3 pos, List<(Vector3 pos, int nodeId)> hubs)
    {
        for (int i = 0; i < hubs.Count; i++)
        {
            float d = Vector3.Distance(
                new Vector3(pos.x, 0, pos.z),
                new Vector3(hubs[i].pos.x, 0, hubs[i].pos.z));
            if (d <= junctionMergeRadius) return hubs[i].nodeId;
        }
        return -1;
    }

    private float EstimateRoadLength(RoadSegment road)
    {
        float len = 0f;
        const int N = 16;
        Vector3 prev = road.EvaluatePosition(0f);
        for (int i = 1; i <= N; i++)
        {
            Vector3 next = road.EvaluatePosition(i / (float)N);
            len += Vector3.Distance(prev, next);
            prev = next;
        }
        return len;
    }

    private SidewalkNode NewNode(Vector3 pos, bool isJunction, string roadCode)
    {
        var node = new SidewalkNode
        {
            id         = _nodes.Count,
            position   = pos,
            isJunction = isJunction,
            roadCode   = roadCode,
        };
        _nodes.Add(node);
        return node;
    }

    private void LinkNodes(int a, int b)
    {
        if (a == b) return;
        if (!_nodes[a].neighbours.Contains(b)) _nodes[a].neighbours.Add(b);
        if (!_nodes[b].neighbours.Contains(a)) _nodes[b].neighbours.Add(a);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  QUERIES
    // ═════════════════════════════════════════════════════════════════════════

    public int FindNearestNode(Vector3 worldPos, float maxDist = float.MaxValue, System.Func<SidewalkNode, bool> filter = null)
    {
        int   best     = -1;
        float bestDist = maxDist;
        for (int i = 0; i < _nodes.Count; i++)
        {
            if (filter != null && !filter(_nodes[i])) continue;
            float d = Vector3.Distance(worldPos, _nodes[i].position);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    public List<int> GetStopAccessNodes(string stopCode) =>
        _stopAccessNodes.TryGetValue(stopCode, out var list) ? list : null;

    public SidewalkNode GetNode(int id) => (id >= 0 && id < _nodes.Count) ? _nodes[id] : null;

    public int GetRandomNode(System.Random rng)
    {
        if (_nodes.Count == 0) return -1;
        return rng.Next(_nodes.Count);
    }

    // ── A* pathfinding between two sidewalk nodes ──────────────────────────────
    private static readonly Dictionary<int, float> _gScore = new Dictionary<int, float>();
    private static readonly Dictionary<int, int>   _cameFrom = new Dictionary<int, int>();

    public List<Vector3> FindPath(int startNode, int goalNode, int maxExpansions = 4000)
    {
        var result = new List<Vector3>();
        if (startNode < 0 || goalNode < 0 || startNode >= _nodes.Count || goalNode >= _nodes.Count)
            return result;

        if (startNode == goalNode)
        {
            result.Add(_nodes[startNode].position);
            return result;
        }

        _gScore.Clear();
        _cameFrom.Clear();

        var open      = new List<int> { startNode };
        var openSet   = new HashSet<int> { startNode };
        var closed    = new HashSet<int>();
        _gScore[startNode] = 0f;

        Vector3 goalPos = _nodes[goalNode].position;
        int expansions = 0;

        while (open.Count > 0 && expansions++ < maxExpansions)
        {
            // pick lowest f = g + heuristic
            int bestIdx = 0;
            float bestF = float.MaxValue;
            for (int i = 0; i < open.Count; i++)
            {
                int   id = open[i];
                float g  = _gScore.GetValueOrDefault(id, float.MaxValue);
                float h  = Vector3.Distance(_nodes[id].position, goalPos);
                float f  = g + h;
                if (f < bestF) { bestF = f; bestIdx = i; }
            }

            int current = open[bestIdx];
            open.RemoveAt(bestIdx);
            openSet.Remove(current);

            if (current == goalNode)
            {
                // reconstruct
                var path = new List<int> { current };
                while (_cameFrom.TryGetValue(current, out int prev))
                {
                    path.Add(prev);
                    current = prev;
                }
                path.Reverse();
                foreach (int id in path) result.Add(_nodes[id].position);
                return result;
            }

            closed.Add(current);

            foreach (int neighbour in _nodes[current].neighbours)
            {
                if (closed.Contains(neighbour)) continue;
                float tentativeG = _gScore[current] +
                    Vector3.Distance(_nodes[current].position, _nodes[neighbour].position);

                if (tentativeG < _gScore.GetValueOrDefault(neighbour, float.MaxValue))
                {
                    _cameFrom[neighbour] = current;
                    _gScore[neighbour]   = tentativeG;
                    if (!openSet.Contains(neighbour))
                    {
                        open.Add(neighbour);
                        openSet.Add(neighbour);
                    }
                }
            }
        }

        // No path found — fall back to straight line so pax don't get stuck forever
        result.Add(_nodes[startNode].position);
        result.Add(_nodes[goalNode].position);
        return result;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!drawGizmos || _nodes == null) return;
        foreach (var n in _nodes)
        {
            Gizmos.color = n.isJunction ? Color.red : (n.isStopAccess ? Color.cyan : new Color(0, 1, 0, 0.4f));
            Gizmos.DrawSphere(n.position + Vector3.up * 0.3f, n.isJunction ? 1.0f : 0.4f);
            foreach (int nb in n.neighbours)
            {
                if (nb < n.id) continue; // draw each edge once
                Gizmos.color = new Color(1f, 1f, 1f, 0.25f);
                Gizmos.DrawLine(n.position + Vector3.up * 0.3f, _nodes[nb].position + Vector3.up * 0.3f);
            }
        }
    }
#endif
}