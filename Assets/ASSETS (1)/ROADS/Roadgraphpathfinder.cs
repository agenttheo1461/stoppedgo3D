using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROAD GRAPH PATHFINDER  —  A* over RoadNode/RoadEdge, weighted by travel time
// ═══════════════════════════════════════════════════════════════════════════════
public static class RoadGraphPathfinder
{
    private class NodeRecord
    {
        public RoadNode node;
        public float    gScore = float.MaxValue;
        public float    fScore = float.MaxValue;
        public RoadEdge cameFromEdge;
    }

    /// Returns an ordered list of edges from `start` to `goal`, or null if unreachable.
    /// Average speed limit across the graph is used to convert straight-line distance
    /// into an admissible time-based heuristic.
    public static List<RoadEdge> FindPath(RoadGraph graph, RoadNode start, RoadNode goal, float avgSpeedLimit = 13.4f)
    {
        if (start == null || goal == null) return null;
        if (start == goal) return new List<RoadEdge>();

        var records = new Dictionary<RoadNode, NodeRecord>();
        NodeRecord Rec(RoadNode n)
        {
            if (!records.TryGetValue(n, out var r))
                records[n] = r = new NodeRecord { node = n };
            return r;
        }

        var open = new List<NodeRecord>();
        var closed = new HashSet<RoadNode>();

        var startRec = Rec(start);
        startRec.gScore = 0f;
        startRec.fScore = Heuristic(start, goal, avgSpeedLimit);
        open.Add(startRec);

        while (open.Count > 0)
        {
            // simple O(n) lowest-fScore pick — fine for city-scale node counts;
            // swap for a binary heap if the graph gets into the thousands of nodes
            int bestIdx = 0;
            for (int i = 1; i < open.Count; i++)
                if (open[i].fScore < open[bestIdx].fScore) bestIdx = i;

            var current = open[bestIdx];
            open.RemoveAt(bestIdx);

            if (current.node == goal)
                return ReconstructPath(records, goal);

            closed.Add(current.node);

            foreach (var edge in current.node.outgoing)
            {
                if (closed.Contains(edge.to)) continue;

                float tentativeG = current.gScore + edge.Cost;
                var neighborRec = Rec(edge.to);

                if (tentativeG < neighborRec.gScore)
                {
                    neighborRec.gScore = tentativeG;
                    neighborRec.fScore = tentativeG + Heuristic(edge.to, goal, avgSpeedLimit);
                    neighborRec.cameFromEdge = edge;

                    if (!open.Contains(neighborRec))
                        open.Add(neighborRec);
                }
            }
        }

        return null; // no path — caller should fall back to off-road behavior
    }

    private static float Heuristic(RoadNode a, RoadNode b, float avgSpeedLimit) =>
        Vector3.Distance(a.position, b.position) / Mathf.Max(1f, avgSpeedLimit);

    private static List<RoadEdge> ReconstructPath(Dictionary<RoadNode, NodeRecord> records, RoadNode goal)
    {
        var path = new List<RoadEdge>();
        var current = records[goal];
        while (current.cameFromEdge != null)
        {
            path.Add(current.cameFromEdge);
            current = records[current.cameFromEdge.from];
        }
        path.Reverse();
        return path;
    }

    /// Convenience overload — snaps arbitrary world positions onto the nearest graph edges,
    /// then paths between the nearest endpoint nodes of those edges.
    public static List<RoadEdge> FindPath(RoadGraph graph, Vector3 fromWorldPos, Vector3 toWorldPos)
    {
        RoadNode start = graph.FindNearestNode(fromWorldPos);
        RoadNode goal  = graph.FindNearestNode(toWorldPos);
        return FindPath(graph, start, goal);
    }

    /// Converts an edge path into a flat List<Vector3> of lane-centered waypoints,
    /// sampled at ~waypointSpacing meters. Drop-in replacement anywhere old code
    /// expects a List<Vector3> road path (e.g. StartDepotIngress/StartDepotEgress),
    /// so depot returns can target an actual free bay's position instead of a
    /// fixed gate, without touching those methods' signatures.
    public static List<Vector3> FindWaypoints(RoadGraph graph, Vector3 fromWorldPos, Vector3 toWorldPos,
                                               float waypointSpacing = 5f)
    {
        var waypoints = new List<Vector3>();
        var edges = FindPath(graph, fromWorldPos, toWorldPos);
        if (edges == null) return waypoints; // caller should treat empty as "no road path"

        foreach (var edge in edges)
        {
            float len = Mathf.Max(edge.Length, 0.01f);
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / waypointSpacing));
            for (int i = 0; i <= steps; i++)
            {
                float t = Mathf.Lerp(edge.tStart, edge.tEnd, i / (float)steps);
                // [FIX] Was GetLanePosition(t, edge.laneIndex, edge.laneWidth) --
                // the per-physical-lane offset, correct for general traffic but
                // not what a BUS route wants. Now uses the same width-tiered
                // bus-offset convention BusRouteMaker uses for hand-clicked
                // nodes (see RoadSegment.GetBusOffsetPosition), so a
                // graph-derived path (depot routing, cross-road hops) lands on
                // the exact same side/distance a manually-placed node would.
                waypoints.Add(edge.segment.GetBusOffsetPosition(t, edge.isOneWay, !edge.Forward));
            }
        }

        // Final approach into the exact destination (e.g. the depot bay itself)
        waypoints.Add(toWorldPos);
        return waypoints;
    }
} 