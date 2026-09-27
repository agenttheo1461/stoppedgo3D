using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;

// ═══════════════════════════════════════════════════════════════════════════════
//  CUSTOM TREE BUILDER  (Tools > City Building)
//
//  Assets/Tree.prefab is a SpeedTree asset still on the legacy Built-in RP
//  "Nature/SpeedTree" shaders (not SRP Batcher compatible) with GPU
//  instancing off on both materials -- fine at the 38 hand-placed instances
//  it has today, not fine once TreePainterWindow lets you drop in hundreds.
//  Rather than touch that asset's shader assignment, this generates a new,
//  low-poly tree from scratch on the project's OWN ToonRampLit shader
//  (already has #pragma multi_compile_instancing, matches the rest of the
//  game's art style) with GPU instancing explicitly enabled -- a real draw
//  call/batching win at scale, and visually consistent with everything
//  else instead of a mismatched SpeedTree look.
//
//  Two selectable shapes (Blocky and Palm were tried and cut -- read as a
//  microphone and a spider respectively, not worth salvaging):
//   - SingleCanopy: tapered trunk + one big sphere, no clustering.
//   - TightCluster: tapered trunk + 3 small spheres bunched close together.
//
//  Output: one combined Mesh (trunk submesh 0, foliage submesh 1) + two
//  ToonRampLit materials + a prefab, all saved under Assets/Trees/.
// ═══════════════════════════════════════════════════════════════════════════════
public class TreeBuilderWindow : EditorWindow
{
    private enum TreeType { SingleCanopy, TightCluster }

    [MenuItem("Tools/City Building/Custom Tree Builder")]
    public static void ShowWindow()
    {
        GetWindow<TreeBuilderWindow>("Tree Builder");
    }

    private const string ShaderPath = "Assets/ASSETS (1)/SHADER/ToonRampLit.shader";
    private const string OutputFolder = "Assets/Trees";

    private string treeName = "CustomTree";
    private TreeType treeType = TreeType.SingleCanopy;

    private float trunkHeight = 3.6f;
    private float trunkBaseRadius = 0.4f;
    private float trunkTopRadius = 0.2f;
    private int trunkSides = 6;

    private float singleCanopyRadius = 2f;

    private float clusterRadius = 1.3f;
    private float clusterSpread = 0.5f;

    private int foliageLonSegments = 6;
    private int foliageLatSegments = 4;

    private Color trunkColor = new Color(0.36f, 0.24f, 0.14f);
    private Color foliageColor = new Color(0.22f, 0.45f, 0.18f);

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Custom Tree Builder", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Generates a low-poly tree mesh + ToonRampLit materials (instancing on) + prefab, saved under " + OutputFolder + ". Doesn't touch the existing SpeedTree Tree.prefab.", MessageType.None);

        treeName = EditorGUILayout.TextField("Tree Name", treeName);
        treeType = (TreeType)EditorGUILayout.EnumPopup("Shape", treeType);

        EditorGUILayout.Space(8);
        DrawTaperedTrunkFields();

        EditorGUILayout.Space(8);
        switch (treeType)
        {
            case TreeType.SingleCanopy:
                EditorGUILayout.LabelField("Canopy (single sphere)", EditorStyles.boldLabel);
                singleCanopyRadius = EditorGUILayout.Slider("Radius", singleCanopyRadius, 0.3f, 8f);
                foliageColor = EditorGUILayout.ColorField("Color", foliageColor);
                DrawSphereSegmentFields();
                break;

            case TreeType.TightCluster:
                EditorGUILayout.LabelField("Canopy (3 tight clusters)", EditorStyles.boldLabel);
                clusterRadius = EditorGUILayout.Slider("Cluster Radius", clusterRadius, 0.2f, 5f);
                clusterSpread = EditorGUILayout.Slider("Cluster Spread", clusterSpread, 0f, 2.5f);
                foliageColor = EditorGUILayout.ColorField("Color", foliageColor);
                DrawSphereSegmentFields();
                break;
        }

        EditorGUILayout.Space(12);
        if (GUILayout.Button("Generate & Save Prefab", GUILayout.Height(32)))
            Generate();
    }

    private void DrawTaperedTrunkFields()
    {
        EditorGUILayout.LabelField("Trunk", EditorStyles.boldLabel);
        trunkHeight = EditorGUILayout.Slider("Height", trunkHeight, 1f, 14f);
        trunkBaseRadius = EditorGUILayout.Slider("Base Radius", trunkBaseRadius, 0.05f, 3f);
        trunkTopRadius = EditorGUILayout.Slider("Top Radius", trunkTopRadius, 0.02f, 3f);
        trunkSides = EditorGUILayout.IntSlider("Sides", trunkSides, 3, 12);
        trunkColor = EditorGUILayout.ColorField("Color", trunkColor);
    }

    private void DrawSphereSegmentFields()
    {
        foliageLonSegments = EditorGUILayout.IntSlider("Longitude Segments", foliageLonSegments, 3, 12);
        foliageLatSegments = EditorGUILayout.IntSlider("Latitude Segments", foliageLatSegments, 2, 10);
    }

    private void Generate()
    {
        if (!AssetDatabase.IsValidFolder(OutputFolder))
            AssetDatabase.CreateFolder("Assets", "Trees");

        var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        if (shader == null)
        {
            Debug.LogError($"[TreeBuilder] Couldn't load shader at {ShaderPath} -- is ToonRampLit still there?");
            return;
        }

        Mesh mesh = BuildCombinedMesh();
        string meshPath = $"{OutputFolder}/{treeName}_Mesh.asset";
        AssetDatabase.CreateAsset(mesh, AssetDatabase.GenerateUniqueAssetPath(meshPath));

        Material trunkMat = new Material(shader) { name = $"{treeName}_Bark" };
        trunkMat.SetColor("_BaseColor", trunkColor);
        trunkMat.enableInstancing = true;
        AssetDatabase.CreateAsset(trunkMat, AssetDatabase.GenerateUniqueAssetPath($"{OutputFolder}/{treeName}_Bark.mat"));

        Material foliageMat = new Material(shader) { name = $"{treeName}_Leaf" };
        foliageMat.SetColor("_BaseColor", foliageColor);
        foliageMat.enableInstancing = true;
        AssetDatabase.CreateAsset(foliageMat, AssetDatabase.GenerateUniqueAssetPath($"{OutputFolder}/{treeName}_Leaf.mat"));

        var go = new GameObject(treeName);
        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = new[] { trunkMat, foliageMat };

        string prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{OutputFolder}/{treeName}.prefab");
        PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
        DestroyImmediate(go);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[TreeBuilder] Generated '{treeName}' ({treeType}) -> {prefabPath} (instancing on, ToonRampLit).");
        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
    }

    // ── Mesh assembly ───────────────────────────────────────────────────────
    private Mesh BuildCombinedMesh()
    {
        var trunkVerts = new List<Vector3>();
        var trunkNormals = new List<Vector3>();
        var trunkUVs = new List<Vector2>();
        var trunkTris = new List<int>();
        BuildTaperedTrunk(trunkVerts, trunkNormals, trunkUVs, trunkTris, trunkHeight);

        var foliageVerts = new List<Vector3>();
        var foliageNormals = new List<Vector3>();
        var foliageUVs = new List<Vector2>();
        var foliageTris = new List<int>();

        if (treeType == TreeType.SingleCanopy)
        {
            AppendSphere(foliageVerts, foliageNormals, foliageUVs, foliageTris,
                new Vector3(0f, trunkHeight + singleCanopyRadius * 0.6f, 0f), singleCanopyRadius, 0f, 1f);
        }
        else
        {
            float clusterY = trunkHeight + clusterRadius * 0.7f;
            for (int i = 0; i < 3; i++)
            {
                float angle = (i / 3f) * Mathf.PI * 2f;
                Vector3 center = new Vector3(Mathf.Cos(angle) * clusterSpread, clusterY, Mathf.Sin(angle) * clusterSpread);
                AppendSphere(foliageVerts, foliageNormals, foliageUVs, foliageTris, center, clusterRadius, 0f, 1f);
            }
        }

        var mesh = new Mesh { name = treeName };
        var allVerts = new List<Vector3>(trunkVerts);
        allVerts.AddRange(foliageVerts);
        var allNormals = new List<Vector3>(trunkNormals);
        allNormals.AddRange(foliageNormals);
        var allUVs = new List<Vector2>(trunkUVs);
        allUVs.AddRange(foliageUVs);

        int vertOffset = trunkVerts.Count;
        var foliageTrisOffset = new List<int>(foliageTris.Count);
        foreach (var idx in foliageTris)
            foliageTrisOffset.Add(idx + vertOffset);

        mesh.SetVertices(allVerts);
        mesh.SetNormals(allNormals);
        mesh.SetUVs(0, allUVs);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(trunkTris, 0);
        mesh.SetTriangles(foliageTrisOffset, 1);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    private void BuildTaperedTrunk(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris, float height)
    {
        int sides = trunkSides;
        for (int i = 0; i <= sides; i++)
        {
            float t = (float)i / sides;
            float angle = t * Mathf.PI * 2f;
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            Vector3 bottomPos = new Vector3(cos * trunkBaseRadius, 0f, sin * trunkBaseRadius);
            Vector3 topPos = new Vector3(cos * trunkTopRadius, height, sin * trunkTopRadius);
            Vector3 normal = new Vector3(cos, 0f, sin).normalized;

            verts.Add(bottomPos); normals.Add(normal); uvs.Add(new Vector2(t, 0f));
            verts.Add(topPos); normals.Add(normal); uvs.Add(new Vector2(t, 1f));
        }
        for (int i = 0; i < sides; i++)
        {
            int baseIdx = i * 2;
            int bottomA = baseIdx, topA = baseIdx + 1, bottomB = baseIdx + 2, topB = baseIdx + 3;
            tris.Add(bottomA); tris.Add(topA); tris.Add(bottomB);
            tris.Add(topA); tris.Add(topB); tris.Add(bottomB);
        }
    }

    /// <summary>vStart/vEnd select a latitude band (0 = top pole, 1 = bottom
    /// pole) -- (0, 1) is a full sphere.</summary>
    private void AppendSphere(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<int> tris,
        Vector3 center, float radius, float vStart, float vEnd)
    {
        int lon = foliageLonSegments, lat = foliageLatSegments;
        int vertStart = verts.Count;

        for (int y = 0; y <= lat; y++)
        {
            float v = vStart + (vEnd - vStart) * ((float)y / lat);
            float phi = v * Mathf.PI;
            for (int x = 0; x <= lon; x++)
            {
                float u = (float)x / lon;
                float theta = u * Mathf.PI * 2f;
                Vector3 dir = new Vector3(
                    Mathf.Sin(phi) * Mathf.Cos(theta),
                    Mathf.Cos(phi),
                    Mathf.Sin(phi) * Mathf.Sin(theta));
                verts.Add(center + dir * radius);
                normals.Add(dir);
                uvs.Add(new Vector2(u, v));
            }
        }

        int rowStride = lon + 1;
        for (int y = 0; y < lat; y++)
        {
            for (int x = 0; x < lon; x++)
            {
                int a = vertStart + y * rowStride + x;
                int b = a + rowStride;
                int c = a + 1;
                int d = b + 1;
                tris.Add(a); tris.Add(b); tris.Add(c);
                tris.Add(c); tris.Add(b); tris.Add(d);
            }
        }
    }
}
#endif
