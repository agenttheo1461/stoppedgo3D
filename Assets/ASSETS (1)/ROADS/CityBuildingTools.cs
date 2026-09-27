#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > City Building
///   Create Triangle Prism (mesh + prefab)   -- the triangle Unity has no primitive for: a unit prism mesh asset
///                                              (base 1 on X, rise 1 on Y, depth 1 on Z, origin at the bottom centre)
///                                              plus a TrianglePrism prefab. Scale it to (width, rise, depth).
///   Create Courthouse Front prefab           -- the portico from the Building Kit preview: porch floor, round pillars,
///                                              a beam across them and the triangle on top. Origin = centre of the porch
///                                              floor, front faces +Z. Sizes match the preview's defaults (metres).
///   Place Courthouse Front in Scene          -- drops the prefab at the Scene view's pivot.
///
/// Material: a WHITE copy of Custom_ToonRampLit 2 (the peach wall material) is created on first use.
/// </summary>
public static class CityBuildingTools
{
    private const string Dir = "Assets/ASSETS (1)/DEPOTS/";
    private const string PeachMatPath = "Assets/ASSETS (1)/SHADER/Custom_ToonRampLit 2.mat";
    private const string WhiteMatPath = "Assets/ASSETS (1)/SHADER/Custom_ToonRampLit White.mat";
    private const string MeshPath = Dir + "TrianglePrism.asset";
    private const string PrismPrefabPath = Dir + "TrianglePrism.prefab";
    private const string FrontPrefabPath = Dir + "CourthouseFront.prefab";

    // Preview defaults ("Courthouse Front" tab)
    private const float Width = 28f, PillarHeight = 10f, PorchDepth = 5.6f, TriangleHeight = 4f;
    private const int Pillars = 6;

    // ───────────────────────────── menu items ─────────────────────────────
    [MenuItem("Tools/City Building/Create Triangle Prism (mesh + prefab)")]
    private static void CreatePrism()
    {
        var mesh = GetOrCreatePrismMesh();
        var mat = GetOrCreateWhiteMat();

        var go = new GameObject("TrianglePrism");
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        go.transform.localScale = new Vector3(Width, TriangleHeight, PorchDepth);

        var prefab = PrefabUtility.SaveAsPrefabAsset(go, PrismPrefabPath);
        Object.DestroyImmediate(go);
        Selection.activeObject = prefab;
        EditorGUIUtility.PingObject(prefab);
        Debug.Log($"[CityBuilding] Triangle prism mesh -> {MeshPath}\nPrefab -> {PrismPrefabPath} (scale = width, rise, depth; origin at the bottom centre).");
    }

    [MenuItem("Tools/City Building/Create Courthouse Front prefab")]
    private static void CreateCourthouseFront()
    {
        var mesh = GetOrCreatePrismMesh();
        var mat = GetOrCreateWhiteMat();

        var root = new GameObject("CourthouseFront");
        Piece(root, "Floor", PrimitiveType.Cube, new Vector3(0, 0.25f, 0), new Vector3(Width, 0.5f, PorchDepth), mat);
        for (int i = 0; i < Pillars; i++)
        {
            float x = -(Width - 2f) / 2f + i * ((Width - 2f) / (Pillars - 1));
            // Unity's Cylinder primitive is 1 wide and 2 tall, so scale = (diameter, height / 2, diameter)
            Piece(root, $"Pillar {i + 1}", PrimitiveType.Cylinder, new Vector3(x, 0.5f + PillarHeight / 2f, 0), new Vector3(1.4f, PillarHeight / 2f, 1.4f), mat);
        }
        Piece(root, "Beam", PrimitiveType.Cube, new Vector3(0, 0.5f + PillarHeight + 0.4f, 0), new Vector3(Width, 0.8f, PorchDepth), mat);

        var tri = new GameObject("Triangle");
        tri.transform.SetParent(root.transform, false);
        tri.transform.localPosition = new Vector3(0, 0.5f + PillarHeight + 0.8f, 0);
        tri.transform.localScale = new Vector3(Width, TriangleHeight, PorchDepth);
        tri.AddComponent<MeshFilter>().sharedMesh = mesh;
        tri.AddComponent<MeshRenderer>().sharedMaterial = mat;
        tri.AddComponent<MeshCollider>().sharedMesh = mesh;

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, FrontPrefabPath);
        Object.DestroyImmediate(root);
        Selection.activeObject = prefab;
        EditorGUIUtility.PingObject(prefab);
        Debug.Log($"[CityBuilding] Courthouse front prefab -> {FrontPrefabPath}");
    }

    [MenuItem("Tools/City Building/Place Courthouse Front in Scene")]
    private static void PlaceCourthouseFront()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FrontPrefabPath);
        if (prefab == null) { CreateCourthouseFront(); prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FrontPrefabPath); }
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        var sv = SceneView.lastActiveSceneView;
        if (sv != null) inst.transform.position = sv.pivot;
        Undo.RegisterCreatedObjectUndo(inst, "Place Courthouse Front");
        Selection.activeGameObject = inst;
    }

    // ───────────────────────────── helpers ─────────────────────────────
    private static void Piece(GameObject root, string name, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat)
    {
        var g = GameObject.CreatePrimitive(type);
        g.name = name;
        g.transform.SetParent(root.transform, false);
        g.transform.localPosition = pos;
        g.transform.localScale = scale;
        g.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    private static Material GetOrCreateWhiteMat()
    {
        var white = AssetDatabase.LoadAssetAtPath<Material>(WhiteMatPath);
        if (white != null) return white;
        var peach = AssetDatabase.LoadAssetAtPath<Material>(PeachMatPath);
        white = peach != null ? new Material(peach) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        if (white.HasProperty("_BaseColor")) white.SetColor("_BaseColor", new Color(0.95f, 0.94f, 0.91f, 1f));
        AssetDatabase.CreateAsset(white, WhiteMatPath);
        AssetDatabase.SaveAssets();
        return white;
    }

    private static Mesh GetOrCreatePrismMesh()
    {
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
        if (existing != null) return existing;
        var mesh = BuildPrism();
        AssetDatabase.CreateAsset(mesh, MeshPath);
        AssetDatabase.SaveAssets();
        return mesh;
    }

    /// <summary>Unit triangular prism, flat shaded: base 1 (X), rise 1 (Y), depth 1 (Z), origin bottom-centre.</summary>
    private static Mesh BuildPrism()
    {
        var v = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<int>();
        void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f) { var s = b; b = c; c = s; } // Unity: clockwise = front
            int i = v.Count; v.Add(a); v.Add(b); v.Add(c); n.Add(normal); n.Add(normal); n.Add(normal);
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
        }
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal) { Tri(a, b, c, normal); Tri(a, c, d, normal); }

        Vector3 lf = new Vector3(-0.5f, 0, 0.5f), rf = new Vector3(0.5f, 0, 0.5f), tf = new Vector3(0, 1, 0.5f);   // front (+Z)
        Vector3 lb = new Vector3(-0.5f, 0, -0.5f), rb = new Vector3(0.5f, 0, -0.5f), tb = new Vector3(0, 1, -0.5f); // back (-Z)
        Tri(lf, tf, rf, Vector3.forward);
        Tri(rb, tb, lb, Vector3.back);
        Quad(lb, rb, rf, lf, Vector3.down);
        Quad(lb, lf, tf, tb, new Vector3(-1f, 0.5f, 0f).normalized);   // left slope
        Quad(rf, rb, tb, tf, new Vector3(1f, 0.5f, 0f).normalized);    // right slope

        var m = new Mesh { name = "TrianglePrism" };
        m.SetVertices(v); m.SetNormals(n); m.SetTriangles(t, 0);
        m.RecalculateBounds();
        return m;
    }
}
#endif
