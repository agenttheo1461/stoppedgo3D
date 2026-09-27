using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  RIVER SEGMENT DEFINITION  —  one river's authored data (control points, width,
//  water level). Mirrors RoadSegmentDefinition's shape on purpose — same spline
//  math, same "just a handful of control points" authoring model — but simpler:
//  no lanes, no traffic, no intersections. Just a ribbon of water.
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class RiverSegmentDefinition
{
    [Header("Identity")]
    public string riverName = "New River";
    public string riverCode = "RIVER_CODE";

    [Header("Shape")]
    [Tooltip("2 pts = straight | 3 pts = quadratic Bezier | 4 pts = cubic Bezier | 5+ = Catmull-Rom through all of them (guaranteed to pass through every point you place — good for a rough, wandering river path).")]
public List<Vector3> points = new List<Vector3>
{
    Vector3.zero,
    new Vector3(0,0,200)
};
    [Header("Water")]
    [Tooltip("Width of the water ribbon in world units. Widen this a lot right where it needs to pass under a bridge.")]
    public float width = 40f;
    [Tooltip("Y height of the water SURFACE. Set this comfortably below the bridge deck height wherever the river passes under one.")]
    public float waterSurfaceY = -3f;
    [Range(0.1f, 2f)] public float meshResolution = 1f;

    [Header("Visuals")]
    public Material waterMaterial;
    [Tooltip("If your water shader scrolls a texture for flow, this is the UV scroll speed passed to it (purely cosmetic bookkeeping here — wire it into your shader's _FlowSpeed or equivalent).")]
    public float flowSpeed = 0.3f;

public Vector3[] BuildControlPoints() =>
    points != null && points.Count >= 2
        ? points.ToArray()
        : new[] { Vector3.zero, new Vector3(0, 0, 200) };
}

// ═══════════════════════════════════════════════════════════════════════════════
//  RIVER SEGMENT  —  runtime spline math + ribbon mesh generation. Same curve
//  math as RoadSegment (2/3/4/5+ point handling, Catmull-Rom fallback) so a
//  river authored with a handful of rough points behaves the same familiar way
//  a road does — just flat, wide, and underwater-colored instead of paved.
// ═══════════════════════════════════════════════════════════════════════════════
public class RiverSegment
{
    public string riverName;
    public string riverCode;
    public Vector3[] controlPoints;
    public float width = 40f;
    public float waterSurfaceY = -3f;
    public float meshResolution = 1f;
    public Material waterMaterial;

    public Vector3 EvaluatePosition(float t)
    {
        t = Mathf.Clamp01(t);
        if (controlPoints == null || controlPoints.Length < 2) return Vector3.zero;
        int n = controlPoints.Length;

        if (n == 2) return Vector3.Lerp(controlPoints[0], controlPoints[1], t);

        if (n == 3)
        {
            float u = 1f - t;
            return u * u * controlPoints[0] + 2f * u * t * controlPoints[1] + t * t * controlPoints[2];
        }

        if (n == 4)
            return CubicBezier(controlPoints[0], controlPoints[1], controlPoints[2], controlPoints[3], t);

        return CatmullRomPosition(t);
    }

    public Vector3 EvaluateTangent(float t)
    {
        t = Mathf.Clamp01(t);
        if (controlPoints == null || controlPoints.Length < 2) return Vector3.forward;
        float eps = 0.001f;
        Vector3 a = EvaluatePosition(Mathf.Clamp01(t - eps));
        Vector3 b = EvaluatePosition(Mathf.Clamp01(t + eps));
        Vector3 d = b - a;
        return d == Vector3.zero ? Vector3.forward : d.normalized;
    }

    private static Vector3 CubicBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1f - t;
        return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
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

        float t2 = localT * localT, t3 = t2 * localT;
        return 0.5f * (
              (-t3 + 2f * t2 - localT) * p0
            + (3f * t3 - 5f * t2 + 2f) * p1
            + (-3f * t3 + 4f * t2 + localT) * p2
            + (t3 - t2) * p3);
    }

    public float ApproximateLength(int steps = 60)
    {
        float len = 0f;
        Vector3 prev = EvaluatePosition(0f);
        for (int i = 1; i <= steps; i++)
        {
            Vector3 next = EvaluatePosition(i / (float)steps);
            len += Vector3.Distance(prev, next);
            prev = next;
        }
        return len;
    }

    /// <summary>Builds a flat ribbon mesh at waterSurfaceY, following the
    /// river's curve — same "sample along the spline, extrude a width" idea
    /// as a road, just a single flat plane instead of a lane-marked surface.</summary>
    public GameObject BuildMesh(Transform parent)
    {
        float length  = ApproximateLength();
        int   samples = Mathf.Max(2, Mathf.CeilToInt(length / Mathf.Max(0.1f, meshResolution) / 10f));
        float half    = width * 0.5f;

        var verts = new List<Vector3>();
        var uvs   = new List<Vector2>();
        var tris  = new List<int>();
        float uvAlong = 0f;
        Vector3 prevPos = Vector3.zero;

        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            Vector3 centre = EvaluatePosition(t);
            centre.y = waterSurfaceY;
            Vector3 tangent = EvaluateTangent(t);
            Vector3 right   = Vector3.Cross(Vector3.up, tangent).normalized;

            verts.Add(centre - right * half - parent.position);
            verts.Add(centre + right * half - parent.position);
            uvs.Add(new Vector2(0f, uvAlong));
            uvs.Add(new Vector2(1f, uvAlong));

            if (i > 0) uvAlong += Vector3.Distance(centre, prevPos);
            prevPos = centre;
        }

        for (int r = 0; r < samples; r++)
        {
            int b = r * 2;
            tris.AddRange(new[] { b, b + 2, b + 1 });
            tris.AddRange(new[] { b + 1, b + 2, b + 3 });
        }

        var go = new GameObject($"River_{riverCode}");
        go.transform.SetParent(parent, false);

        var mesh = new Mesh { name = $"{riverCode}_water" };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = waterMaterial;
        // No collider by default — rivers are visual, not something buses/pax
        // path against. Add one manually if you ever need boats/collision.

        return go;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  RIVER MANAGER  —  the CityManager-equivalent for rivers: holds the list of
//  authored RiverSegmentDefinitions and builds their meshes at Start(). Fully
//  separate from CityManager/roads on purpose — a river doesn't need lanes,
//  intersections, or pathfinding, so there's no reason to entangle it with the
//  road graph at all.
// ═══════════════════════════════════════════════════════════════════════════════
public class RiverManager : MonoBehaviour
{
    public static RiverManager Instance;

    [Header("Rivers")]
    public List<RiverSegmentDefinition> riverDefinitions = new List<RiverSegmentDefinition>();

    private RiverSegment[] _rivers;
    private Dictionary<string, RiverSegment> _riverByCode = new();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        BuildRivers();
    }

    private void BuildRivers()
    {
        _rivers = new RiverSegment[riverDefinitions.Count];
        for (int i = 0; i < riverDefinitions.Count; i++)
        {
            var def = riverDefinitions[i];
            var seg = new RiverSegment
            {
                riverName      = def.riverName,
                riverCode      = def.riverCode,
                controlPoints  = def.BuildControlPoints(),
                width          = def.width,
                waterSurfaceY  = def.waterSurfaceY,
                meshResolution = def.meshResolution,
                waterMaterial  = def.waterMaterial
            };
            _rivers[i] = seg;

            if (!string.IsNullOrEmpty(def.riverCode)) _riverByCode[def.riverCode] = seg;

            var container = new GameObject($"River_{def.riverCode}_Container");
            container.transform.SetParent(transform, false);
            seg.BuildMesh(container.transform);
        }
    }

    public RiverSegment GetRiver(string code) => _riverByCode.TryGetValue(code, out var r) ? r : null;

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (riverDefinitions == null) return;
        foreach (var def in riverDefinitions)
        {
            if (def == null) continue;
            var pts = def.BuildControlPoints();
            if (pts == null || pts.Length < 2) continue;
            var tempSeg = new RiverSegment { controlPoints = pts };
            Gizmos.color = new Color(0.2f, 0.5f, 0.9f, 0.8f);
            Vector3 prev = tempSeg.EvaluatePosition(0f);
            prev.y = def.waterSurfaceY;
            for (int i = 1; i <= 48; i++)
            {
                Vector3 next = tempSeg.EvaluatePosition(i / 48f);
                next.y = def.waterSurfaceY;
                Gizmos.DrawLine(prev, next);
                prev = next;
            }
        }
    }
#endif
}