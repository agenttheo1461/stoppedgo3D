using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROAD SEGMENT  ─  spline math + procedural mesh generation
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class RoadSegment
{
    [Header("Identity")]
    public string roadName;
    public string roadCode;

    [Header("Spline  (2 pts = straight | 3 pts = quadratic Bézier | 4 pts = cubic Bézier | 5+ = chained cubic segments)")]
    public Vector3[] controlPoints;

    [Header("Mesh Settings")]
    public float    roadWidth      = 7f;
    public float    meshResolution = 0.5f;
    public int      vertsPerChunk  = 200;
    public Material roadMaterial;
    // [ADD] Paint-all-roads support -- applied as a tint on an instanced
    // copy of roadMaterial in BuildMeshChunks below, so multiple roads can
    // share one base material asset while still each showing a different
    // color, and "paint all roads" doesn't require creating N unique
    // material assets on disk.
    public Color roadColor = Color.white;

    // Fully automatic, no per-road setup, no Inspector fields — every sampled
    // point along every road (before mesh/collider generation) whose real Y
    // falls within [0, 1] gets snapped to 0.7. A point that's genuinely
    // elevated (a bridge peak at Y=500) is left completely alone, even on the
    // same road as flat points either side of it, since this applies per
    // point, not per whole road.
    // [FIX] These were private, and the snap was only ever applied inline
    // inside BuildMeshChunks. Nothing else — like BusStopData.GetWorldPosition
    // — had any way to know the actual visual/collider height of the road at
    // a given t, so stops (and anything else built from EvaluatePosition
    // directly) sat at the road's RAW spline Y while the mesh right next to
    // them had been snapped to a different height. Made these public static
    // and added EvaluateSurfacePosition below so every consumer agrees on the
    // same "actual road surface height" instead of each computing its own.
    public const float AutoFlatMinY   = 0f;
    public const float AutoFlatMaxY   = 1f;
    public const float AutoFlatHeight = 0.7f;

    /// <summary>Applies the same near-ground height snap that BuildMeshChunks
    /// uses for the visible/collidable mesh. Use this (not EvaluatePosition)
    /// anywhere you need a point that actually sits on the road's real
    /// surface — stop placement, prop placement, anything spawned relative
    /// to a road — so it can never end up floating above or buried below the
    /// mesh built for the same t value.</summary>
    public static Vector3 ApplyAutoFlatten(Vector3 pos)
    {
        if (pos.y >= AutoFlatMinY && pos.y <= AutoFlatMaxY) pos.y = AutoFlatHeight;
        return pos;
    }

    /// <summary>EvaluatePosition(t) with the same auto-flatten snap applied
    /// that the visible road mesh gets. Anything placing an object "on the
    /// road" at a given t should call this instead of the raw
    /// EvaluatePosition.</summary>
    public Vector3 EvaluateSurfacePosition(float t) => ApplyAutoFlatten(EvaluatePosition(t));

    // ─────────────────────────────────────────────────────────────────────────
    //  SPLINE MATH
    // ─────────────────────────────────────────────────────────────────────────

    public Vector3 EvaluatePosition(float t)
    {
        t = Mathf.Clamp01(t);
        if (controlPoints == null || controlPoints.Length < 2) return Vector3.zero;
        int n = controlPoints.Length;

        // 2 pts: straight line
        if (n == 2)
            return Vector3.Lerp(controlPoints[0], controlPoints[1], t);

        // 3 pts: quadratic Bézier
        if (n == 3)
        {
            float u = 1f - t;
            return u*u*controlPoints[0] + 2f*u*t*controlPoints[1] + t*t*controlPoints[2];
        }

        // 4 pts: cubic Bézier
        if (n == 4)
            return CubicBezier(controlPoints[0], controlPoints[1],
                               controlPoints[2], controlPoints[3], t);

        // 5+ pts: chained cubic segments
        if ((n - 1) % 3 == 0)
            return ChainedCubicPosition(t);

        // Catmull-Rom fallback
        return CatmullRomPosition(t);
    }

    public Vector3 EvaluateTangent(float t)
    {
        t = Mathf.Clamp01(t);
        if (controlPoints == null || controlPoints.Length < 2) return Vector3.forward;
        int n = controlPoints.Length;

        if (n == 2)
            return (controlPoints[1] - controlPoints[0]).normalized;

        if (n == 3)
        {
            float u = 1f - t;
            Vector3 d = 2f*u*(controlPoints[1]-controlPoints[0])
                      + 2f*t*(controlPoints[2]-controlPoints[1]);
            return d == Vector3.zero ? Vector3.forward : d.normalized;
        }

        if (n == 4)
            return CubicBezierTangent(controlPoints[0], controlPoints[1],
                                      controlPoints[2], controlPoints[3], t);

        if ((n - 1) % 3 == 0)
            return ChainedCubicTangent(t);

        return CatmullRomTangent(t);
    }

    private static Vector3 CubicBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1f - t;
        return u*u*u*p0 + 3f*u*u*t*p1 + 3f*u*t*t*p2 + t*t*t*p3;
    }

    private static Vector3 CubicBezierTangent(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1f - t;
        Vector3 d = 3f*u*u*(p1-p0) + 6f*u*t*(p2-p1) + 3f*t*t*(p3-p2);
        return d == Vector3.zero ? Vector3.forward : d.normalized;
    }

    private Vector3 ChainedCubicPosition(float t)
    {
        int segCount = (controlPoints.Length - 1) / 3;
        float scaled = t * segCount;
        int   seg    = Mathf.Min((int)scaled, segCount - 1);
        float localT = scaled - seg;
        int   i      = seg * 3;
        return CubicBezier(controlPoints[i], controlPoints[i+1],
                           controlPoints[i+2], controlPoints[i+3], localT);
    }

    private Vector3 ChainedCubicTangent(float t)
    {
        int segCount = (controlPoints.Length - 1) / 3;
        float scaled = t * segCount;
        int   seg    = Mathf.Min((int)scaled, segCount - 1);
        float localT = scaled - seg;
        int   i      = seg * 3;
        return CubicBezierTangent(controlPoints[i], controlPoints[i+1],
                                  controlPoints[i+2], controlPoints[i+3], localT);
    }

    private Vector3 CatmullRomPosition(float t)
    {
        int n = controlPoints.Length;
        float scaled = t * (n - 1);
        int   i      = Mathf.Min((int)scaled, n - 2);
        float localT = scaled - i;

        Vector3 p0 = controlPoints[Mathf.Max(i - 1, 0)];
        Vector3 p1 = controlPoints[i];
        Vector3 p2 = controlPoints[Mathf.Min(i + 1, n - 1)];
        Vector3 p3 = controlPoints[Mathf.Min(i + 2, n - 1)];

        return CatmullRom(p0, p1, p2, p3, localT);
    }

    private Vector3 CatmullRomTangent(float t)
    {
        int n = controlPoints.Length;
        float scaled = t * (n - 1);
        int   i      = Mathf.Min((int)scaled, n - 2);
        float localT = scaled - i;

        Vector3 p0 = controlPoints[Mathf.Max(i - 1, 0)];
        Vector3 p1 = controlPoints[i];
        Vector3 p2 = controlPoints[Mathf.Min(i + 1, n - 1)];
        Vector3 p3 = controlPoints[Mathf.Min(i + 2, n - 1)];

        float t2 = localT * localT;
        Vector3 d = (-3f*t2 + 4f*localT - 1f) * p0 * 0.5f
                  + ( 9f*t2 - 10f*localT) * p1 * 0.5f
                  + (-9f*t2 +  8f*localT + 1f) * p2 * 0.5f
                  + ( 3f*t2 -  2f*localT) * p3 * 0.5f;
        return d == Vector3.zero ? Vector3.forward : d.normalized;
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (
              (-t3 + 2f*t2 - t)       * p0
            + (3f*t3 - 5f*t2 + 2f)   * p1
            + (-3f*t3 + 4f*t2 + t)   * p2
            + (t3 - t2)               * p3);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  LANE MATH  (used by RoadGraph / traffic AI)
    // ─────────────────────────────────────────────────────────────────────────

    /// World position at the given t, offset perpendicular to the spline by lane
    /// index. Lane 0 hugs the centerline on the "right" side (Cross(up, tangent)
    /// direction); increasing |laneIndex| moves further from centerline.
    /// Negative laneIndex = lanes on the opposite side (opposing traffic direction).
    public Vector3 GetLanePosition(float t, int laneIndex, float laneWidth)
    {
        Vector3 pos     = EvaluatePosition(t);
        Vector3 tangent = EvaluateTangent(t);
        Vector3 right   = Vector3.Cross(Vector3.up, tangent).normalized;

        int    sign   = laneIndex < 0 ? -1 : 1;
        int    magnitude = Mathf.Abs(laneIndex);
        float  offset  = sign * ((magnitude + 0.5f) * laneWidth);
        return pos + right * offset;
    }

    /// <summary>Shared bus-route offset convention — deliberately NOT the same
    /// thing as GetLanePosition's per-physical-lane offset (laneIndex/laneWidth).
    /// A bus doesn't need to track which of N traffic lanes it's in, just "the
    /// correct side of the road, by how much." offset = max(0, (roadWidth-4)/2)
    /// to the right of travel direction — 5 units on a 14-wide road, 1.5 on a
    /// 7-wide, 0.5 on a 5-wide — or dead-center (0 offset) on a one-way road,
    /// since there's no opposing traffic to stay clear of. Used by both
    /// RoadGraphPathfinder.FindWaypoints (depot routing, cross-road hops) and
    /// BusRouteMaker (route node authoring), so a graph-derived path and a
    /// hand-clicked one land on the exact same convention.</summary>
    public Vector3 GetBusOffsetPosition(float t, bool isOneWay, bool travelReversed = false)
    {
        Vector3 pos = EvaluatePosition(t);
        if (isOneWay) return pos;

        Vector3 tangent = EvaluateTangent(t);
        if (travelReversed) tangent = -tangent; // bus drives t-decreasing: its right is the spline's left
        Vector3 right   = Vector3.Cross(Vector3.up, tangent).normalized;
        float   offset  = Mathf.Max(0f, (roadWidth - 4f) / 2f);
        return pos + right * offset;
    }

    public Quaternion GetLaneRotation(float t, bool reversed = false)
    {
        Vector3 tangent = EvaluateTangent(t);
        if (reversed) tangent = -tangent;
        return tangent.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(tangent, Vector3.up) : Quaternion.identity;
    }

    public float ApproximateLength(int steps = 60)
    {
        float   len  = 0f;
        Vector3 prev = EvaluatePosition(0f);
        for (int i = 1; i <= steps; i++)
        {
            Vector3 next = EvaluatePosition(i / (float)steps);
            len += Vector3.Distance(prev, next);
            prev = next;
        }
        return len;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MESH GENERATION
    // ─────────────────────────────────────────────────────────────────────────
    public List<GameObject> BuildMeshChunks(Transform parent)
    {
        var chunks = new List<GameObject>();

        float length  = ApproximateLength();
        int   samples = Mathf.Max(2, Mathf.CeilToInt(length / meshResolution));
        float half    = roadWidth * 0.5f;

        var positions = new Vector3[samples + 1];
        var tangents  = new Vector3[samples + 1];
        for (int i = 0; i <= samples; i++)
        {
            float t      = i / (float)samples;
            // Fully automatic (see AutoFlatMinY/MaxY/Height above) — no
            // per-road setup for any of the 100+ roads. Near-ground points
            // snap to a clean flat height; anything genuinely elevated
            // (outside the range) keeps its real Y untouched, even on the
            // same road as flat points either side of it. Routed through the
            // shared ApplyAutoFlatten so this can never drift out of sync
            // with EvaluateSurfacePosition, which everything else (stops,
            // props, etc.) uses to match this same mesh's actual height.
            positions[i] = ApplyAutoFlatten(EvaluatePosition(t));
            tangents[i]  = EvaluateTangent(t);
        }

        int ringsPerChunk = Mathf.Max(2, vertsPerChunk / 2);
        int chunkIndex    = 0;
        int startRing     = 0;

        while (startRing < samples)
        {
            int endRing = Mathf.Min(startRing + ringsPerChunk, samples);

            var verts = new List<Vector3>();
            var uvs   = new List<Vector2>();
            var tris  = new List<int>();

            float uvAlong = 0f;

            for (int r = startRing; r <= endRing; r++)
            {
                Vector3 right = Vector3.Cross(Vector3.up, tangents[r]).normalized;

                verts.Add(positions[r] - right * half - parent.position);   // Left edge
                verts.Add(positions[r] + right * half - parent.position);   // Right edge

                uvs.Add(new Vector2(0f, uvAlong));
                uvs.Add(new Vector2(1f, uvAlong));

                if (r > startRing)
                    uvAlong += Vector3.Distance(positions[r], positions[r - 1]);
            }

            int rings = endRing - startRing + 1;
            for (int r = 0; r < rings - 1; r++)
            {
                int b = r * 2;
                tris.AddRange(new[] { b, b+2, b+1 });
                tris.AddRange(new[] { b+1, b+2, b+3 });
            }

            var go = new GameObject($"Road_{roadCode}_chunk{chunkIndex}");
            go.transform.SetParent(parent, false);

            var mesh = new Mesh { name = $"{roadCode}_mesh{chunkIndex}" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh       = mesh;
            // Instanced (not shared) material so each road/chunk gets its own
            // material instance even when several roads point at the same
            // base roadMaterial asset. No roadColor tint applied -- the
            // material's own texture/albedo renders untouched. Falls back to
            // a plain default-shader material if no roadMaterial was ever
            // assigned.
            var rend = go.AddComponent<MeshRenderer>();
            Material instanced = roadMaterial != null
                ? new Material(roadMaterial)
                : new Material(Shader.Find("Standard"));
            rend.sharedMaterial = instanced;
            
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = false;
            mc.sharedMesh = null; 
            mc.sharedMesh = mesh; 

            chunks.Add(go);
            chunkIndex++;
            startRing = endRing;  
        }

        return chunks;
    }
}