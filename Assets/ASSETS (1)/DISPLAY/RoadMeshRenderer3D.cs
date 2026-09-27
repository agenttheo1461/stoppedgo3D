using System.Collections.Generic;
using System;
using UnityEngine;


// ═══════════════════════════════════════════════════════════════════════════════
//  ROAD MESH RENDERER 3D
//
//  Shared renderer used by MDT_Live3DCamera (LiveMap 3D mode) and, later, the
//  road authoring tool's live preview. One cached Mesh per RoadSegmentDefinition
//  — only the segment that actually changed gets rebuilt, everything else is a
//  cheap Graphics.DrawMesh call. This replaces the old "rebuild everything every
//  frame with Gizmos/immediate GL calls" approach that was causing the lag.
//
//  Depth is fixed at roadDepth (default 0.25) — an extruded flat slab per road,
//  width = roadWidth (or lane-derived), with a thin arrow strip per lane drawn
//  as a second, unlit material batch so direction reads clearly from orbit.
// ═══════════════════════════════════════════════════════════════════════════════
public class RoadMeshRenderer3D : MonoBehaviour
{
    [Header("Source")]
    public CityManager cityManager;

    [Header("Style")]
    public float roadDepth      = 0.25f;
    public Material roadMaterial;
    public Material arrowMaterial;
    public Color forwardLaneColor = new Color(0.35f, 0.85f, 1f);
    public Color reverseLaneColor = new Color(1f, 0.55f, 0.35f);

    [Header("Perf")]
    [Tooltip("How many segments this pool will re-tessellate per frame, at most, when marked dirty. Keeps edits from stalling a frame on big roads.")]
    public int maxRebuildsPerFrame = 2;

    private class RoadEntry
    {
        public RoadSegmentDefinition def;
        public Mesh roadMesh;
        public Mesh arrowMesh;
        public bool dirty = true;
    }

    private readonly Dictionary<RoadSegmentDefinition, RoadEntry> _entries = new();
    private readonly Queue<RoadEntry> _rebuildQueue = new();

    private void OnEnable()
    {
        if (cityManager == null) cityManager = FindFirstObjectByType<CityManager>();
        RebuildAllTracked();
    }

    /// Call when a road definition is added/removed/edited externally (authoring tool, etc).
    public void MarkDirty(RoadSegmentDefinition def)
    {
        if (def == null) return;
        if (!_entries.TryGetValue(def, out var entry))
        {
            entry = new RoadEntry { def = def };
            _entries[def] = entry;
        }
        if (!entry.dirty)
        {
            entry.dirty = true;
            _rebuildQueue.Enqueue(entry);
        }
    }

    public void MarkAllDirty()
    {
        if (cityManager == null) return;
        foreach (var def in cityManager.AllRoadDefinitions) MarkDirty(def);
    }

    private void RebuildAllTracked()
    {
        if (cityManager == null) return;
        foreach (var def in cityManager.AllRoadDefinitions)
            MarkDirty(def);
    }

    private void LateUpdate()
    {
        int budget = maxRebuildsPerFrame;
        while (budget > 0 && _rebuildQueue.Count > 0)
        {
            var entry = _rebuildQueue.Dequeue();
            if (!entry.dirty) continue; // already rebuilt via a later mark before its turn came up
            RebuildEntry(entry);
            entry.dirty = false;
            budget--;
        }

        DrawAll();
    }

    private void RebuildEntry(RoadEntry entry)
    {
        var def = entry.def;
        var pts = def.BuildControlPoints();
        if (pts == null || pts.Length < 2) return;

        var seg = new RoadSegment { controlPoints = pts };
        int steps = Mathf.Max(4, Mathf.RoundToInt(64 * Mathf.Clamp01(def.meshResolution)));
        float width = def.roadWidth;

        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var uvs   = new List<Vector2>();
        var tris  = new List<int>();

        Vector3 prevPos = seg.EvaluatePosition(0f);
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            Vector3 pos = seg.EvaluatePosition(t);
            Vector3 fwd = (i == steps)
                ? (pos - prevPos).normalized
                : (seg.EvaluatePosition(Mathf.Min(1f, t + 1f / steps)) - pos).normalized;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;

            Vector3 topL = pos - right * (width * 0.5f);
            Vector3 topR = pos + right * (width * 0.5f);

            int baseIdx = verts.Count;
            // top face pair
            verts.Add(topL); verts.Add(topR);
            // bottom face pair (extrude down by roadDepth)
            verts.Add(topL + Vector3.down * roadDepth);
            verts.Add(topR + Vector3.down * roadDepth);

            norms.Add(Vector3.up); norms.Add(Vector3.up);
            norms.Add(Vector3.down); norms.Add(Vector3.down);

            uvs.Add(new Vector2(0, t)); uvs.Add(new Vector2(1, t));
            uvs.Add(new Vector2(0, t)); uvs.Add(new Vector2(1, t));

            if (i > 0)
            {
                int p = baseIdx - 4;
                // top strip
                tris.Add(p + 0); tris.Add(baseIdx + 0); tris.Add(p + 1);
                tris.Add(p + 1); tris.Add(baseIdx + 0); tris.Add(baseIdx + 1);
                // bottom strip (reversed winding)
                tris.Add(p + 3); tris.Add(baseIdx + 2); tris.Add(p + 2);
                tris.Add(p + 3); tris.Add(baseIdx + 3); tris.Add(baseIdx + 2);
                // side walls (left edge, right edge)
                tris.Add(p + 0); tris.Add(p + 2); tris.Add(baseIdx + 0);
                tris.Add(baseIdx + 0); tris.Add(p + 2); tris.Add(baseIdx + 2);
                tris.Add(p + 1); tris.Add(baseIdx + 1); tris.Add(p + 3);
                tris.Add(baseIdx + 1); tris.Add(baseIdx + 3); tris.Add(p + 3);
            }

            prevPos = pos;
        }

        var mesh = entry.roadMesh != null ? entry.roadMesh : new Mesh { name = $"Road_{def.roadCode}" };
        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        entry.roadMesh = mesh;

        entry.arrowMesh = BuildArrowMesh(seg, def, steps);
    }

    private Mesh BuildArrowMesh(RoadSegment seg, RoadSegmentDefinition def, int steps)
    {
        int laneCount = def.laneCountOrDefault;
        float laneWidth = def.laneWidthOrDefault;
        int forwardLanes = def.isOneWay ? laneCount : Mathf.Max(1, laneCount / 2);
        int reverseLanes = def.isOneWay ? 0 : laneCount - forwardLanes;

        var verts = new List<Vector3>();
        var tris  = new List<int>();
        var cols  = new List<Color>();

        void AddArrowAt(Vector3 pos, Vector3 fwd, bool forward)
        {
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            float len = laneWidth * 0.4f, wid = laneWidth * 0.25f;
            Vector3 tip  = pos + fwd * len * 0.5f + Vector3.up * 0.03f;
            Vector3 backL = pos - fwd * len * 0.5f - right * wid * 0.5f + Vector3.up * 0.03f;
            Vector3 backR = pos - fwd * len * 0.5f + right * wid * 0.5f + Vector3.up * 0.03f;
            int b = verts.Count;
            verts.Add(tip); verts.Add(backL); verts.Add(backR);
            tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
            Color c = forward ? Color.cyan : new Color(1f, 0.55f, 0.35f);
            cols.Add(c); cols.Add(c); cols.Add(c);
        }

        // sample a handful of arrow positions along the road per lane
        int arrowSamples = Mathf.Max(1, steps / 10);
        for (int i = 1; i < arrowSamples; i++)
        {
            float t = i / (float)arrowSamples;
            Vector3 pos = seg.EvaluatePosition(t);
            Vector3 fwd = (seg.EvaluatePosition(Mathf.Min(1f, t + 0.01f)) - pos).normalized;
            if (fwd.sqrMagnitude < 1e-6f) continue;
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;

            for (int lane = 0; lane < forwardLanes; lane++)
            {
                float offset = (lane - (forwardLanes - 1) * 0.5f) * laneWidth * 0.5f + (reverseLanes > 0 ? laneWidth * 0.25f : 0f);
                AddArrowAt(pos + right * offset, fwd, true);
            }
            for (int lane = 0; lane < reverseLanes; lane++)
            {
                float offset = -(lane - (reverseLanes - 1) * 0.5f) * laneWidth * 0.5f - laneWidth * 0.25f;
                AddArrowAt(pos + right * offset, -fwd, false);
            }
        }

        if (verts.Count == 0) return null;
        var mesh = new Mesh { name = $"Arrows_{def.roadCode}" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.SetColors(cols);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private void DrawAll()
    {
        if (roadMaterial == null || !roadMaterial.enableInstancing) { /* fine either way, DrawMesh works without instancing */ }
        foreach (var kv in _entries)
        {
            var entry = kv.Value;
            if (entry.roadMesh != null && roadMaterial != null)
                Graphics.DrawMesh(entry.roadMesh, Matrix4x4.identity, roadMaterial, 0);
            if (entry.arrowMesh != null && arrowMaterial != null)
                Graphics.DrawMesh(entry.arrowMesh, Matrix4x4.identity, arrowMaterial, 0);
        }
    }

    /// World-space bounds across every road, used by the 3D camera rig to
    /// clamp orbit distance so the whole network stays in frustum.
    public Bounds ComputeCityBounds()
    {
        bool any = false;
        var bounds = new Bounds(Vector3.zero, Vector3.zero);
        foreach (var kv in _entries)
        {
            if (kv.Value.roadMesh == null) continue;
            var b = kv.Value.roadMesh.bounds;
            if (!any) { bounds = b; any = true; }
            else bounds.Encapsulate(b);
        }
        return bounds;
    }
}
