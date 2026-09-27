#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > City Building > Bridge Case Generator.
/// One-time generation of a "phone case" mesh for a road / bridge: a U-shaped shell that runs under the road along
/// its whole spline, with side walls that rise slightly ABOVE the road surface and a chamfered (bevelled) top edge.
///
///        ___                        ___          <- lip: walls end a little above the road surface (bevelled)
///       |   \______________________/   |
///       |      road surface (flat)     |         <- the road itself sits inside the U
///       |______________________________|         <- plate under the road
///
/// The mesh is built in WORLD space from the CityManager road definition (same spline + auto-flatten height the
/// visible road mesh uses), saved as a Mesh asset, and placed in the open scene as "BridgeCase_&lt;roadCode&gt;".
/// Running it again replaces both.
/// </summary>
public class BridgeCaseGenerator : EditorWindow
{
    private string roadCode = "R087";
    private float plateThickness = 0.8f;   // slab under the road
    private float gapUnderRoad = 0.05f;    // clearance between the road surface and the top of the plate (avoids z-fighting)
    private float wallThickness = 0.6f;
    private float overhang = 0.6f;         // extra room each side beyond the road edge (curb / sidewalk)
    private float lipAboveRoad = 0.35f;    // how far the walls stand above the road surface
    private float bevel = 0.15f;           // chamfer size on the wall tops
    private float spacing = 1.0f;          // metres between rings along the road
    private float t0 = 0f, t1 = 1f;        // portion of the road to cover (whole road by default)

    [MenuItem("Tools/City Building/Bridge Case Generator")]
    private static void Open() => GetWindow<BridgeCaseGenerator>("Bridge Case");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("Builds a U-shaped case under the road (plate + side walls that rise slightly above the road, with a bevelled top edge) along the road's whole spline. One-time: re-running replaces the previous result.", MessageType.Info);
        roadCode = EditorGUILayout.TextField("Road code", roadCode);
        plateThickness = EditorGUILayout.Slider("Plate thickness", plateThickness, 0.2f, 4f);
        gapUnderRoad = EditorGUILayout.Slider("Gap under road", gapUnderRoad, 0f, 0.5f);
        wallThickness = EditorGUILayout.Slider("Wall thickness", wallThickness, 0.2f, 3f);
        overhang = EditorGUILayout.Slider("Room beyond road edge", overhang, 0f, 4f);
        lipAboveRoad = EditorGUILayout.Slider("Wall height above road", lipAboveRoad, 0.05f, 2f);
        bevel = EditorGUILayout.Slider("Top bevel", bevel, 0f, 0.5f);
        spacing = EditorGUILayout.Slider("Ring spacing (m)", spacing, 0.25f, 4f);
        EditorGUILayout.MinMaxSlider(new GUIContent($"Road range {t0:0.00} - {t1:0.00}"), ref t0, ref t1, 0f, 1f);
        if (GUILayout.Button("Generate bridge case")) Generate();
    }

    private void Generate()
    {
        var city = Object.FindObjectOfType<CityManager>();
        if (city == null) { Debug.LogError("[BridgeCase] No CityManager in the open scene."); return; }
        var def = city.roadDefinitions.Find(d => d.roadCode == roadCode);
        if (def == null) { Debug.LogError($"[BridgeCase] Road '{roadCode}' not found in CityManager.roadDefinitions."); return; }

        var road = new RoadSegment { roadName = def.roadName, roadCode = def.roadCode, roadWidth = def.roadWidth, controlPoints = def.BuildControlPoints() };
        if (road.controlPoints == null || road.controlPoints.Length < 2) { Debug.LogError("[BridgeCase] Road has fewer than 2 control points."); return; }

        // ── Cross-section (x = right, y = up, relative to the road surface) ─────────────────────────────
        float xi = def.roadWidth * 0.5f + overhang;          // inner half width (cavity)
        float xo = xi + wallThickness;                        // outer half width
        float yp = -gapUnderRoad;                             // top of the plate = cavity floor
        float yb = yp - plateThickness;                       // bottom of the plate
        float yt = lipAboveRoad;                              // top of the walls
        float bv = Mathf.Min(bevel, wallThickness * 0.45f, (yt - yp) * 0.45f);
        Vector2[] prof =
        {
            new Vector2(-xo, yb), new Vector2( xo, yb),                       // bottom, left -> right
            new Vector2( xo, yt - bv), new Vector2( xo - bv, yt),            // right wall, outer chamfer
            new Vector2( xi + bv, yt), new Vector2( xi, yt - bv),            // right wall top, inner chamfer
            new Vector2( xi, yp), new Vector2(-xi, yp),                       // down the inner wall, across the cavity floor
            new Vector2(-xi, yt - bv), new Vector2(-xi - bv, yt),            // up the left inner wall, chamfer
            new Vector2(-xo + bv, yt), new Vector2(-xo, yt - bv),            // left wall top, outer chamfer
        };
        int P = prof.Length;

        // ── Sample the road ─────────────────────────────────────────────────────────────────────────────
        float length = road.ApproximateLength() * Mathf.Max(0.01f, t1 - t0);
        int rings = Mathf.Max(2, Mathf.CeilToInt(length / spacing)) + 1;
        var pos = new Vector3[rings]; var right = new Vector3[rings]; var up = new Vector3[rings]; var along = new float[rings];
        for (int i = 0; i < rings; i++)
        {
            float t = Mathf.Lerp(t0, t1, i / (float)(rings - 1));
            pos[i] = RoadSegment.ApplyAutoFlatten(road.EvaluatePosition(t));   // the road's real surface height
            Vector3 tan = road.EvaluateTangent(t).normalized;
            right[i] = Vector3.Cross(Vector3.up, tan).normalized;
            up[i] = Vector3.Cross(tan, right[i]).normalized;                    // follows the slope along the road
            along[i] = i == 0 ? 0f : along[i - 1] + Vector3.Distance(pos[i], pos[i - 1]);
        }
        Vector3 At(int ring, Vector2 p) => pos[ring] + right[ring] * p.x + up[ring] * p.y;

        // ── Mesh: one strip per profile edge (separate vertices -> crisp corners, smooth along the road) ─
        var verts = new List<Vector3>(); var uvs = new List<Vector2>(); var tris = new List<int>();
        for (int e = 0; e < P; e++)
        {
            int a = e, b = (e + 1) % P;
            int start = verts.Count;
            for (int r = 0; r < rings; r++)
            {
                verts.Add(At(r, prof[a])); uvs.Add(new Vector2(0f, along[r]));
                verts.Add(At(r, prof[b])); uvs.Add(new Vector2(1f, along[r]));
            }
            for (int r = 0; r < rings - 1; r++)
            {
                int i0 = start + r * 2, i1 = i0 + 1, i2 = i0 + 2, i3 = i0 + 3;
                // The profile is counter-clockwise (x right, y up), extruded forward: outward-facing winding.
                tris.Add(i0); tris.Add(i1); tris.Add(i2);
                tris.Add(i1); tris.Add(i3); tris.Add(i2);
            }
        }
        // End caps: plate rectangle + two convex wall polygons (the U itself is concave).
        void Cap(int ring, bool front, Vector2[] poly)
        {
            int s = verts.Count;
            foreach (var p in poly) { verts.Add(At(ring, p)); uvs.Add(p * 0.1f); }
            for (int k = 1; k < poly.Length - 1; k++)
            {
                if (front) { tris.Add(s); tris.Add(s + k + 1); tris.Add(s + k); }
                else       { tris.Add(s); tris.Add(s + k); tris.Add(s + k + 1); }
            }
        }
        var plate = new[] { new Vector2(-xo, yb), new Vector2(xo, yb), new Vector2(xo, yp), new Vector2(-xo, yp) };
        var rightWall = new[] { new Vector2(xi, yp), new Vector2(xo, yp), new Vector2(xo, yt - bv), new Vector2(xo - bv, yt), new Vector2(xi + bv, yt), new Vector2(xi, yt - bv) };
        var leftWall = new[] { new Vector2(-xo, yp), new Vector2(-xi, yp), new Vector2(-xi, yt - bv), new Vector2(-xi - bv, yt), new Vector2(-xo + bv, yt), new Vector2(-xo, yt - bv) };
        foreach (var poly in new[] { plate, rightWall, leftWall }) { Cap(0, true, poly); Cap(rings - 1, false, poly); }

        var mesh = new Mesh { name = $"BridgeCase_{roadCode}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();

        // ── Save asset + place in scene ────────────────────────────────────────────────────────────────
        string assetPath = $"Assets/ASSETS (1)/ROADS/BridgeCase_{roadCode}.asset";
        if (AssetDatabase.LoadAssetAtPath<Mesh>(assetPath) != null) AssetDatabase.DeleteAsset(assetPath);
        AssetDatabase.CreateAsset(mesh, assetPath);

        string goName = $"BridgeCase_{roadCode}";
        var old = GameObject.Find(goName);
        if (old != null) Undo.DestroyObjectImmediate(old);
        var go = new GameObject(goName);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = GetMaterial();
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        go.isStatic = true;
        Undo.RegisterCreatedObjectUndo(go, "Generate bridge case");
        Selection.activeGameObject = go;
        Debug.Log($"[BridgeCase] {roadCode}: {rings} rings x {P} profile edges = {verts.Count} vertices, {length:0} m of road. Asset: {assetPath}. " +
                  $"Cavity width {xi * 2f:0.0} m, walls rise {lipAboveRoad:0.00} m above the road surface.");
    }

    private static Material GetMaterial()
    {
        const string path = "Assets/ASSETS (1)/SHADER/BridgeCase.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m != null) return m;
        var peach = AssetDatabase.LoadAssetAtPath<Material>("Assets/ASSETS (1)/SHADER/Custom_ToonRampLit 2.mat");
        m = peach != null ? new Material(peach) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", new Color(0.62f, 0.62f, 0.6f, 1f));   // concrete grey
        AssetDatabase.CreateAsset(m, path);
        return m;
    }
}
#endif
