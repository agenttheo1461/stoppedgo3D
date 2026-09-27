using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  DIORAMA GRID BOX
//
//  The physical "box" the mini city sits in: a floor grid + grid walls on the
//  sides, no roof (open top, since you're looking down into it from orbit).
//  Built as actual line-quad geometry (thin rectangles per grid line) rather
//  than a shader trick, so it draws fine with a plain unlit material and reads
//  as clean line-art regardless of render pipeline settings.
// ═══════════════════════════════════════════════════════════════════════════════
public class DioramaGridBox : MonoBehaviour
{
    [Header("Style")]
    public Material lineMaterial;
    public float lineThickness = 0.15f;
    public float cellSize = 20f;
    public float wallHeight = 60f;
    public float floorMargin = 40f; // extra room around the city bounds

    private Mesh _floorMesh;
    private Mesh _wallMesh;
    private Bounds _lastBounds;
    private bool _built;

    /// Rebuilds the box only if the source bounds actually changed meaningfully.
    public void EnsureBuilt(Bounds cityBounds)
    {
        if (_built && (cityBounds.center - _lastBounds.center).sqrMagnitude < 1f &&
            Mathf.Abs(cityBounds.size.x - _lastBounds.size.x) < 1f &&
            Mathf.Abs(cityBounds.size.z - _lastBounds.size.z) < 1f)
            return;

        _lastBounds = cityBounds;
        _built = true;

        float halfX = cityBounds.extents.x + floorMargin;
        float halfZ = cityBounds.extents.z + floorMargin;
        Vector3 center = cityBounds.center;
        float floorY = cityBounds.min.y - 0.05f;

        _floorMesh = BuildGridPlane(center, halfX, halfZ, floorY);
        _wallMesh  = BuildWalls(center, halfX, halfZ, floorY);
    }

    private Mesh BuildGridPlane(Vector3 center, float halfX, float halfZ, float y)
    {
        var verts = new List<Vector3>();
        var tris  = new List<int>();

        void AddLineQuad(Vector3 a, Vector3 b, Vector3 thicknessDir)
        {
            Vector3 t = thicknessDir * (lineThickness * 0.5f);
            int b0 = verts.Count;
            verts.Add(a - t); verts.Add(a + t); verts.Add(b + t); verts.Add(b - t);
            tris.Add(b0); tris.Add(b0 + 1); tris.Add(b0 + 2);
            tris.Add(b0); tris.Add(b0 + 2); tris.Add(b0 + 3);
        }

        for (float x = -halfX; x <= halfX + 0.01f; x += cellSize)
        {
            Vector3 a = center + new Vector3(x, y - center.y, -halfZ);
            Vector3 b = center + new Vector3(x, y - center.y, halfZ);
            AddLineQuad(a, b, Vector3.right);
        }
        for (float z = -halfZ; z <= halfZ + 0.01f; z += cellSize)
        {
            Vector3 a = center + new Vector3(-halfX, y - center.y, z);
            Vector3 b = center + new Vector3(halfX, y - center.y, z);
            AddLineQuad(a, b, Vector3.forward);
        }

        var mesh = new Mesh { name = "DioramaFloorGrid" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private Mesh BuildWalls(Vector3 center, float halfX, float halfZ, float floorY)
    {
        var verts = new List<Vector3>();
        var tris  = new List<int>();

        void AddLineQuad(Vector3 a, Vector3 b, Vector3 thicknessDir)
        {
            Vector3 t = thicknessDir * (lineThickness * 0.5f);
            int b0 = verts.Count;
            verts.Add(a - t); verts.Add(a + t); verts.Add(b + t); verts.Add(b - t);
            tris.Add(b0); tris.Add(b0 + 1); tris.Add(b0 + 2);
            tris.Add(b0); tris.Add(b0 + 2); tris.Add(b0 + 3);
        }

        // 3 walls only — back, left, right. Front (nearest default orbit view)
        // stays open along with the whole top, so it reads as a diorama you're
        // looking into rather than a closed box.
        void BuildWallGrid(Vector3 corner0, Vector3 corner1, Vector3 up, Vector3 along, float length)
        {
            int vSteps = Mathf.Max(1, Mathf.RoundToInt(wallHeight / cellSize));
            int hSteps = Mathf.Max(1, Mathf.RoundToInt(length / cellSize));

            for (int i = 0; i <= vSteps; i++)
            {
                float h = (i / (float)vSteps) * wallHeight;
                Vector3 a = corner0 + up * h;
                Vector3 b = corner1 + up * h;
                AddLineQuad(a, b, along.normalized);
            }
            for (int i = 0; i <= hSteps; i++)
            {
                float f = i / (float)hSteps;
                Vector3 basePos = Vector3.Lerp(corner0, corner1, f);
                Vector3 a = basePos;
                Vector3 b = basePos + up * wallHeight;
                AddLineQuad(a, b, up);
            }
        }

        Vector3 floorCenter = new Vector3(center.x, floorY, center.z);

        // back wall (+Z)
        BuildWallGrid(floorCenter + new Vector3(-halfX, 0, halfZ), floorCenter + new Vector3(halfX, 0, halfZ),
                      Vector3.up, Vector3.right, halfX * 2f);
        // left wall (-X)
        BuildWallGrid(floorCenter + new Vector3(-halfX, 0, -halfZ), floorCenter + new Vector3(-halfX, 0, halfZ),
                      Vector3.up, Vector3.forward, halfZ * 2f);
        // right wall (+X)
        BuildWallGrid(floorCenter + new Vector3(halfX, 0, -halfZ), floorCenter + new Vector3(halfX, 0, halfZ),
                      Vector3.up, Vector3.forward, halfZ * 2f);

        var mesh = new Mesh { name = "DioramaWallGrid" };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private void LateUpdate()
    {
        if (lineMaterial == null) return;
        if (_floorMesh != null) Graphics.DrawMesh(_floorMesh, Matrix4x4.identity, lineMaterial, 0);
        if (_wallMesh  != null) Graphics.DrawMesh(_wallMesh,  Matrix4x4.identity, lineMaterial, 0);
    }
}