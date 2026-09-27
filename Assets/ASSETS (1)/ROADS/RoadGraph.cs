using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROAD GRAPH  —  auto-built connectivity graph over every RoadSegment
//
//  KEY POINT (per your ask): nodes are created from GEOMETRIC intersections
//  between road splines — crossings, T-junctions, dead-ends — NOT from
//  CityManager's manual/traffic-light junction list. A road with no traffic
//  light still becomes a full node with edges in every direction traffic
//  can legally go. The traffic-light junction system (ProceduralJunctionController)
//  is a separate, purely-visual/timing layer that can optionally be looked up
//  by a node's world position when a driver reaches it — see RoadGraphBuilder
//  notes at the bottom.
// ═══════════════════════════════════════════════════════════════════════════════

public class RoadNode
{
    public int      id;
    public Vector3  position;
    public List<RoadEdge> outgoing = new();

    public override string ToString() => $"Node#{id} @ {position}";
}

public class RoadEdge
{
    public RoadNode        from;
    public RoadNode        to;
    public RoadSegment     segment;     // underlying spline (shared across both directions/lanes)
    public float           tStart, tEnd;  // sub-range of the segment this edge covers (may run backward: tStart > tEnd)
    public int             laneIndex;   // signed lane index — see RoadLaneUtility
    public float           laneWidth;
    public float           speedLimit;
    public bool             isOneWay;

    public bool Forward => tEnd >= tStart;

    private float? _cachedLength;
    public float Length
    {
        get
        {
            if (_cachedLength.HasValue) return _cachedLength.Value;
            float len = 0f;
            const int steps = 12;
            Vector3 prev = segment.EvaluatePosition(tStart);
            for (int i = 1; i <= steps; i++)
            {
                float t = Mathf.Lerp(tStart, tEnd, i / (float)steps);
                Vector3 next = segment.EvaluatePosition(t);
                len += Vector3.Distance(prev, next);
                prev = next;
            }
            _cachedLength = len;
            return len;
        }
    }

    /// Time-weighted cost used by the pathfinder (seconds to traverse at speed limit).
    public float Cost => Length / Mathf.Max(1f, speedLimit);

    public override string ToString() =>
        $"Edge {from.id}->{to.id} lane {laneIndex} [{tStart:0.00}-{tEnd:0.00}] len={Length:0.0}";
}

public class RoadGraph
{
    public readonly List<RoadNode> nodes = new();
    private int _nextNodeId = 0;

    /// Snap tolerance — intersection points within this distance are merged into one node.
    public float nodeSnapDistance = 3.0f;

    /// Resolution used when sampling splines for crossing detection (matches
    /// CityManager.intersectionCheckResolution in spirit, but done per-edge here).
    public int sampleResolution = 60;

    private readonly Dictionary<(RoadSegment seg, int laneIndex), List<RoadEdge>> _edgesBySegmentLane = new();

    // ─────────────────────────────────────────────────────────────────────
    //  BUILD
    // ─────────────────────────────────────────────────────────────────────
    public static RoadGraph Build(IReadOnlyList<RoadSegment> roads, IReadOnlyList<RoadSegmentDefinition> defs)
    {
        var graph = new RoadGraph();
        graph.BuildInternal(roads, defs);
        return graph;
    }

    private void BuildInternal(IReadOnlyList<RoadSegment> roads, IReadOnlyList<RoadSegmentDefinition> defs)
    {
        int n = roads.Count;

        // Step 1: for every road, collect all "cut points" (t-values where a node
        // must exist) — its own two endpoints, plus every geometric crossing with
        // every other road, plus any other road's endpoint that happens to touch it
        // (T-junctions). Traffic lights are irrelevant here on purpose.
        var cutPointsPerRoad = new List<List<float>>(n);
        for (int i = 0; i < n; i++) cutPointsPerRoad.Add(new List<float> { 0f, 1f });

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                FindCrossings(roads[i], roads[j], cutPointsPerRoad[i], cutPointsPerRoad[j]);
            }
        }

        // Also snap any road-endpoint that lands close to a point ON another road
        // (classic T-junction: minor road stops at a major road's middle).
        for (int i = 0; i < n; i++)
        {
            Vector3 startPos = roads[i].EvaluatePosition(0f);
            Vector3 endPos   = roads[i].EvaluatePosition(1f);
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                TryAddEndpointCut(roads[j], startPos, cutPointsPerRoad[j]);
                TryAddEndpointCut(roads[j], endPos,   cutPointsPerRoad[j]);
            }
        }

        // Step 2: build/merge nodes at every cut point (in world space), then
        // create directed per-lane edges between consecutive cut points on each road.
        var nodeLookup = new List<(Vector3 pos, RoadNode node)>();

        RoadNode GetOrCreateNode(Vector3 pos)
        {
            for (int k = 0; k < nodeLookup.Count; k++)
                if (Vector3.Distance(nodeLookup[k].pos, pos) <= nodeSnapDistance)
                    return nodeLookup[k].node;

            var node = new RoadNode { id = _nextNodeId++, position = pos };
            nodes.Add(node);
            nodeLookup.Add((pos, node));
            return node;
        }

        for (int i = 0; i < n; i++)
        {
            var seg = roads[i];
            var def = defs[i];
            var cuts = cutPointsPerRoad[i];
            cuts.Sort();
            // de-dup near-identical t values
            for (int k = cuts.Count - 1; k > 0; k--)
                if (Mathf.Abs(cuts[k] - cuts[k - 1]) < 0.001f) cuts.RemoveAt(k);

            if (cuts.Count < 2) continue;

            int totalLanes = Mathf.Max(1, def.laneCountOrDefault);
            int forwardLanes, reverseLanes;
            if (def.isOneWay)
            {
                forwardLanes = totalLanes;
                reverseLanes = 0;
            }
            else if (totalLanes <= 1)
            {
                // [FIX] totalLanes/2 rounds a single lane down to 0 reverse
                // lanes, which silently made every two-way road whose width
                // rounds to 1 lane (laneCountOrDefault: roadWidth/3.5, so
                // ~1.75-5.25 wide with no manual laneCount override) into a
                // graph-level one-way street -- nothing marked it isOneWay,
                // but the pathfinder could only ever traverse it forward. A
                // real two-way road, however narrow, always needs an edge
                // each direction; a genuinely one-way road already took the
                // branch above and never reaches this one.
                forwardLanes = 1;
                reverseLanes = 1;
            }
            else
            {
                forwardLanes = totalLanes / 2;
                reverseLanes = totalLanes - forwardLanes;
            }
            float laneWidth  = def.laneWidthOrDefault;
            float speedLimit = def.speedLimitOrDefault;

            for (int k = 0; k < cuts.Count - 1; k++)
            {
                float tA = cuts[k];
                float tB = cuts[k + 1];
                RoadNode nodeA = GetOrCreateNode(seg.EvaluatePosition(tA));
                RoadNode nodeB = GetOrCreateNode(seg.EvaluatePosition(tB));
                if (nodeA == nodeB) continue;

                // Forward lanes: nodeA -> nodeB, lane indices 0..forwardLanes-1
                for (int lane = 0; lane < forwardLanes; lane++)
                    AddEdge(nodeA, nodeB, seg, tA, tB, lane, laneWidth, speedLimit, def.isOneWay);

                // Reverse lanes: nodeB -> nodeA, lane indices -1..-reverseLanes
                for (int lane = 0; lane < reverseLanes; lane++)
                    AddEdge(nodeB, nodeA, seg, tB, tA, -(lane + 1), laneWidth, speedLimit, def.isOneWay);
            }
        }
    }

    private void AddEdge(RoadNode from, RoadNode to, RoadSegment seg, float tStart, float tEnd,
                         int laneIndex, float laneWidth, float speedLimit, bool oneWay)
    {
        var edge = new RoadEdge
        {
            from = from, to = to, segment = seg,
            tStart = tStart, tEnd = tEnd,
            laneIndex = laneIndex, laneWidth = laneWidth,
            speedLimit = speedLimit, isOneWay = oneWay
        };
        from.outgoing.Add(edge);

        var key = (seg, laneIndex);
        if (!_edgesBySegmentLane.TryGetValue(key, out var list))
            _edgesBySegmentLane[key] = list = new List<RoadEdge>();
        list.Add(edge);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  GEOMETRIC CROSSING DETECTION (segment-vs-segment, sampled polylines)
    // ─────────────────────────────────────────────────────────────────────
    private void FindCrossings(RoadSegment a, RoadSegment b, List<float> cutsA, List<float> cutsB)
    {
        int res = sampleResolution;
        var ptsA = new Vector3[res + 1];
        var ptsB = new Vector3[res + 1];
        for (int i = 0; i <= res; i++)
        {
            ptsA[i] = a.EvaluatePosition(i / (float)res);
            ptsB[i] = b.EvaluatePosition(i / (float)res);
        }

        for (int i = 0; i < res; i++)
        {
            for (int j = 0; j < res; j++)
            {
                if (SegmentsIntersectXZ(ptsA[i], ptsA[i + 1], ptsB[j], ptsB[j + 1], out Vector2 hit))
                {
                    float tA = (i + Frac(ptsA[i], ptsA[i + 1], hit)) / res;
                    float tB = (j + Frac(ptsB[j], ptsB[j + 1], hit)) / res;
                    cutsA.Add(Mathf.Clamp01(tA));
                    cutsB.Add(Mathf.Clamp01(tB));
                }
            }
        }
    }

    private void TryAddEndpointCut(RoadSegment road, Vector3 point, List<float> cuts)
    {
        // Find the closest t on `road` to `point`; if it's within snap distance
        // and not basically at road's own endpoints, register a T-junction cut.
        const int res = 40;
        float bestT = -1f;
        float bestDist = nodeSnapDistance;
        for (int i = 0; i <= res; i++)
        {
            float t = i / (float)res;
            float d = Vector3.Distance(road.EvaluatePosition(t), point);
            if (d < bestDist) { bestDist = d; bestT = t; }
        }
        if (bestT >= 0f) cuts.Add(bestT);
    }

    private static bool SegmentsIntersectXZ(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4, out Vector2 hit)
    {
        hit = Vector2.zero;
        Vector2 a1 = new(p1.x, p1.z), a2 = new(p2.x, p2.z);
        Vector2 b1 = new(p3.x, p3.z), b2 = new(p4.x, p4.z);

        Vector2 r = a2 - a1;
        Vector2 s = b2 - b1;
        float rxs = r.x * s.y - r.y * s.x;
        if (Mathf.Abs(rxs) < 1e-6f) return false; // parallel

        Vector2 qp = b1 - a1;
        float t = (qp.x * s.y - qp.y * s.x) / rxs;
        float u = (qp.x * r.y - qp.y * r.x) / rxs;

        if (t < 0f || t > 1f || u < 0f || u > 1f) return false;
        hit = a1 + t * r;
        return true;
    }

    private static float Frac(Vector3 a, Vector3 b, Vector2 hitXZ)
    {
        Vector2 a2 = new(a.x, a.z), b2 = new(b.x, b.z);
        float len = Vector2.Distance(a2, b2);
        if (len < 1e-5f) return 0f;
        return Vector2.Distance(a2, hitXZ) / len;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  QUERIES
    // ─────────────────────────────────────────────────────────────────────
    public RoadNode FindNearestNode(Vector3 pos)
    {
        RoadNode best = null;
        float bestDist = float.MaxValue;
        foreach (var n in nodes)
        {
            float d = Vector3.Distance(n.position, pos);
            if (d < bestDist) { bestDist = d; best = n; }
        }
        return best;
    }

    /// Finds the edge (and t on it) nearest to a world position — used to drop
    /// a vehicle onto the graph from an arbitrary starting point.
    public bool FindNearestEdgePoint(Vector3 pos, out RoadEdge edge, out float t)
    {
        edge = null; t = 0f;
        float bestDist = float.MaxValue;
        foreach (var node in nodes)
        {
            foreach (var e in node.outgoing)
            {
                const int steps = 20;
                for (int i = 0; i <= steps; i++)
                {
                    float sampleT = Mathf.Lerp(e.tStart, e.tEnd, i / (float)steps);
                    float d = Vector3.Distance(e.segment.EvaluatePosition(sampleT), pos);
                    if (d < bestDist) { bestDist = d; edge = e; t = sampleT; }
                }
            }
        }
        return edge != null;
    }
}