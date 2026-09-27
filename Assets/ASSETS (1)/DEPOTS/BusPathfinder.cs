using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusPathfinder  —  static road-aware waypoint builder
//
//  WHAT IT DOES
//  ────────────
//  Given a start and end world position, finds the best road(s) from
//  CityManager and builds a list of waypoints that:
//    · Stay on the correct lane based on travel direction
//    · Apply the same auto-offset rules as the Route Editor:
//        N→S  (moving in -Z)  →  X offset = -1.5  (road width 7)  / -2.5  (width 9+)
//        S→N  (moving in +Z)  →  X offset = +1.5  / +2.5
//        W→E  (moving in +X)  →  Z offset = -1.5  / -2.5
//        E→W  (moving in -X)  →  Z offset = +1.5  / +2.5
//    · Sample the road at enough resolution that the bus steers smoothly
//    · Can chain multiple road segments together for depot → terminal paths
//
//  USAGE
//  ─────
//  // Build a path from depot gate to terminal stop world position:
//  var waypoints = BusPathfinder.BuildPath(depotGatePos, terminalPos);
//  // Then feed into NPCBusController.StartDepotEgress(waypoints, targetSlot);
//
//  DESIGN NOTES
//  ────────────
//  · This is intentionally simple — not A*, not NavMesh.  It finds the road
//    whose sampled polyline passes closest to both start and end, then walks
//    that polyline between those two T values.  For a transit sim where buses
//    follow fixed roads this is plenty.
//  · If start and end are on DIFFERENT roads, it chains them via the closest
//    shared sample point (poor man's junction routing).
//  · Y is always kept at the caller's start height so the bus stays on ground.
// ═══════════════════════════════════════════════════════════════════════════════

public static class BusPathfinder
{
    // ── Config ────────────────────────────────────────────────────────────────
    private const int   ROAD_SAMPLES    = 64;   // samples per road for path building
    private const float ARRIVAL_RADIUS  = 10f;  // metres — "close enough" to terminal
    private const float MIN_STEP_DIST   = 3f;   // cull waypoints closer than this

    // ── Lane offset rules ─────────────────────────────────────────────────────
    // Road width < 8.5 → 1.5 unit offset; ≥ 8.5 → 2.5 unit offset
    private static float LaneOffset(float roadWidth) => roadWidth >= 8.5f ? 2.5f : 1.5f;

    // Given a road tangent (normalised, Y=0) and travel direction, return the
    // perpendicular right-hand offset vector that puts the bus in the correct lane.
    //
    // Convention (matches Route Editor):
    //   Travelling in road's FORWARD direction  → right side  → negative right offset
    //   Travelling in road's REVERSE direction  → left  side  → positive right offset
    private static Vector3 ComputeLaneOffset(Vector3 tangent, Vector3 travelDir, float roadWidth)
    {
        tangent.y   = 0f; tangent.Normalize();
        travelDir.y = 0f; travelDir.Normalize();

        // dot > 0 means we're travelling with the road's natural direction
        float dot   = Vector3.Dot(tangent, travelDir);
        float sign  = dot >= 0f ? -1f : 1f;

        Vector3 right = Vector3.Cross(Vector3.up, tangent).normalized;
        return right * (sign * LaneOffset(roadWidth));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC: BuildPath
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build a lane-correct waypoint list from <paramref name="startWorld"/> to
    /// <paramref name="endWorld"/> using CityManager road geometry.
    /// Returns an empty list if no road is found — caller should fall back to
    /// direct DriveTowardSmooth.
    /// </summary>
    public static List<Vector3> BuildPath(Vector3 startWorld, Vector3 endWorld)
    {
        var cm = CityManager.Instance;
        if (cm == null || cm.roadDefinitions == null) return new List<Vector3>();

        float groundY = startWorld.y;

        // ── Find which road is closest to start and which to end ──────────────
        RoadMatch startMatch = FindBestRoadMatch(startWorld, cm);
        RoadMatch endMatch   = FindBestRoadMatch(endWorld,   cm);

        if (startMatch.road == null) return new List<Vector3>();

        // ── Travel direction: start → end projected flat ──────────────────────
        Vector3 travelDir = endWorld - startWorld;
        travelDir.y = 0f;
        if (travelDir == Vector3.zero) return new List<Vector3>();
        travelDir.Normalize();

        // ── Case A: same road ─────────────────────────────────────────────────
        if (endMatch.road != null && startMatch.roadCode == endMatch.roadCode)
        {
            return WalkRoad(startMatch, endMatch.t, travelDir, startMatch.roadWidth, groundY, cm);
        }

        // ── Case B: different roads — chain via closest junction point ─────────
        // Walk start road to the end that is closest to endWorld, then walk end
        // road from that junction point to the destination.
        if (endMatch.road != null)
        {
            // Determine which end of startMatch road is nearer to endWorld
            Vector3 roadEnd0 = SampleRoad(startMatch.road, 0f, groundY);
            Vector3 roadEnd1 = SampleRoad(startMatch.road, 1f, groundY);
            float   d0       = Vector3.Distance(roadEnd0, endWorld);
            float   d1       = Vector3.Distance(roadEnd1, endWorld);
            float   exitT    = d0 < d1 ? 0f : 1f;

            var leg1 = WalkRoad(startMatch, exitT, travelDir, startMatch.roadWidth, groundY, cm);

            // Travel direction for second leg
            Vector3 legStart = leg1.Count > 0 ? leg1[leg1.Count - 1] : startWorld;
            Vector3 dir2     = endWorld - legStart;
            dir2.y = 0f;
            if (dir2 != Vector3.zero) dir2.Normalize();

            var leg2 = WalkRoad(endMatch, endMatch.t, dir2, endMatch.roadWidth, groundY, cm);

            var combined = new List<Vector3>(leg1);
            combined.AddRange(leg2);
            return Cull(combined, MIN_STEP_DIST);
        }

        // ── Case C: only start road found — walk toward end along it ──────────
        // Find which T value on the start road gets closest to endWorld
        float bestT   = startMatch.t;
        float bestDist = float.MaxValue;
        for (int i = 0; i <= ROAD_SAMPLES; i++)
        {
            float  t   = i / (float)ROAD_SAMPLES;
            Vector3 pt = SampleRoad(startMatch.road, t, groundY);
            float  d   = Vector3.Distance(pt, endWorld);
            if (d < bestDist) { bestDist = d; bestT = t; }
        }

        return WalkRoad(startMatch, bestT, travelDir, startMatch.roadWidth, groundY, cm);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC: FindNearestRoadPoint
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Snap a world position to the nearest road sample, applying the lane
    /// offset for the given travel direction.  Used by depot gate placement.
    /// </summary>
    public static Vector3 SnapToRoad(Vector3 worldPos, Vector3 travelDir)
    {
        var cm = CityManager.Instance;
        if (cm == null) return worldPos;

        RoadMatch m = FindBestRoadMatch(worldPos, cm);
        if (m.road == null) return worldPos;

        Vector3 tangent = m.road.EvaluateTangent(m.t);
        Vector3 offset  = ComputeLaneOffset(tangent, travelDir, m.roadWidth);
        Vector3 pos     = SampleRoad(m.road, m.t, worldPos.y);
        return pos + offset;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  INTERNAL HELPERS
    // ═════════════════════════════════════════════════════════════════════════

    private struct RoadMatch
    {
        public RoadSegment road;
        public string      roadCode;
        public float       t;
        public float       roadWidth;
    }

    private static RoadMatch FindBestRoadMatch(Vector3 worldPos, CityManager cm)
    {
        var   result   = new RoadMatch();
        float bestDist = float.MaxValue;

        for (int ri = 0; ri < cm.roadDefinitions.Count; ri++)
        {
            var def  = cm.roadDefinitions[ri];
            if (def == null) continue;
            var road = cm.GetRoad(def.roadCode);
            if (road == null) continue;

            for (int i = 0; i <= ROAD_SAMPLES; i++)
            {
                float   t   = i / (float)ROAD_SAMPLES;
                Vector3 pt  = road.EvaluatePosition(t);
                pt.y        = worldPos.y;
                float   d   = Vector3.Distance(pt, worldPos);
                if (d < bestDist)
                {
                    bestDist       = d;
                    result.road    = road;
                    result.roadCode = def.roadCode;
                    result.t       = t;
                    result.roadWidth = def.roadWidth;
                }
            }
        }

        return result;
    }

    // Walk a road from startT toward endT, applying lane offset at each sample.
    private static List<Vector3> WalkRoad(
        RoadMatch match, float endT, Vector3 travelDir, float roadWidth, float groundY, CityManager cm)
    {
        var result = new List<Vector3>();

        float startT = match.t;
        bool  forward = endT >= startT;
        int   steps   = Mathf.Max(4, Mathf.RoundToInt(Mathf.Abs(endT - startT) * ROAD_SAMPLES));

        for (int i = 0; i <= steps; i++)
        {
            float   t       = Mathf.Lerp(startT, endT, i / (float)steps);
            Vector3 tangent = match.road.EvaluateTangent(t);
            Vector3 offset  = ComputeLaneOffset(tangent, travelDir, roadWidth);
            Vector3 pt      = match.road.EvaluatePosition(t);
            pt.y            = groundY;
            result.Add(pt + offset);
        }

        return Cull(result, MIN_STEP_DIST);
    }

    private static Vector3 SampleRoad(RoadSegment road, float t, float groundY)
    {
        Vector3 p = road.EvaluatePosition(t);
        p.y = groundY;
        return p;
    }

    // Remove waypoints that are too close together to bother steering toward
    private static List<Vector3> Cull(List<Vector3> pts, float minDist)
    {
        if (pts.Count < 2) return pts;
        var result = new List<Vector3> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            if (Vector3.Distance(result[result.Count - 1], pts[i]) >= minDist)
                result.Add(pts[i]);
        }
        return result;
    }
}