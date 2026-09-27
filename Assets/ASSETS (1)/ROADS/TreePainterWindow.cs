using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;

// ═══════════════════════════════════════════════════════════════════════════════
//  TREE PAINTER  (Tools > City Building)
//
//  Paints prefab instances (built by TreeBuilderWindow) into the scene,
//  parented under a Paint Root you assign -- no auto-guessing, since the
//  existing 38 hand-placed trees turned out to be scattered under whatever
//  GameObject happened to be selected at the time, not one consistent root.
//
//  Three separate modes (Paint / Edit Ruler / Edit Border), never active at
//  once, so a ruler or border drag-handle can never eat a paint click by
//  accident:
//   - Paint:       click-drag scatters `Trees Per Stroke` instances inside
//                  `Brush Radius` per tick, each with random Y rotation and
//                  a randomized height scale around `Avg Height Scale`.
//                  Shift-drag erases (removes this root's own children only).
//   - Edit Ruler:  drag two endpoints (a straight Vector3 line). When
//                  "Align To Ruler" is on, every placed tree's ground point
//                  gets projected onto that segment first and (if rotation
//                  isn't randomized) faces along it -- for lining trees up
//                  along a road edge instead of scattering near it.
//   - Edit Border: drag polygon corner handles, or paste "x,y,z x,y,z ..."
//                  triples directly (typed coordinates, not clicked) and
//                  hit Parse. "Fill Border" scatters `Fill Count` trees
//                  through the whole enclosed area in one click instead of
//                  brushing it by hand.
// ═══════════════════════════════════════════════════════════════════════════════
public class TreePainterWindow : EditorWindow
{
    private enum Mode { None, Paint, EditRuler, EditBorder }

    [MenuItem("Tools/City Building/Tree Painter")]
    public static void ShowWindow()
    {
        GetWindow<TreePainterWindow>("Tree Painter");
    }

    private Mode mode = Mode.None;

    private Transform paintRoot;
    private readonly List<GameObject> prefabPool = new List<GameObject> { null };

    private float avgHeightScale = 1f;
    private float heightVariance = 0.2f;
    private bool randomizeRotation = true;

    private float brushRadius = 3f;
    private int treesPerStroke = 3;
    private float minSpacing = 1.2f;
    private double lastStrokeTime;
    private const double StrokeInterval = 0.12; // seconds between scatter ticks while dragging

    private Vector3 rulerStart = new Vector3(-5f, 0f, 0f);
    private Vector3 rulerEnd = new Vector3(5f, 0f, 0f);
    private bool alignToRuler = false;

    private readonly List<Vector3> borderPoints = new List<Vector3>();
    private string borderText = "50,0,50 0,0,50 0,0,0 50,0,0";
    private int fillCount = 40;

    private void OnEnable() => SceneView.duringSceneGui += OnSceneGUI;
    private void OnDisable() => SceneView.duringSceneGui -= OnSceneGUI;

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Tree Painter", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Paints prefab instances under Paint Root. Pick a mode below -- only one is ever active, so ruler/border handles never eat a paint click.", MessageType.None);

        paintRoot = (Transform)EditorGUILayout.ObjectField("Paint Root", paintRoot, typeof(Transform), true);
        if (paintRoot == null && GUILayout.Button("Create New Root"))
        {
            var rootGO = new GameObject("PaintedTrees");
            Undo.RegisterCreatedObjectUndo(rootGO, "Create Paint Root");
            paintRoot = rootGO.transform;
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Prefab Pool", EditorStyles.boldLabel);
        for (int i = 0; i < prefabPool.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            prefabPool[i] = (GameObject)EditorGUILayout.ObjectField(prefabPool[i], typeof(GameObject), false);
            if (GUILayout.Button("-", GUILayout.Width(22)) && prefabPool.Count > 1)
                prefabPool.RemoveAt(i);
            EditorGUILayout.EndHorizontal();
        }
        if (GUILayout.Button("+ Add Prefab Slot"))
            prefabPool.Add(null);
        EditorGUILayout.HelpBox("Add both SingleCanopy and TightCluster prefabs here and each painted tree picks one at random.", MessageType.None);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Height", EditorStyles.boldLabel);
        avgHeightScale = EditorGUILayout.Slider("Avg Height Scale", avgHeightScale, 0.3f, 3f);
        heightVariance = EditorGUILayout.Slider("Height Variance (±)", heightVariance, 0f, 0.8f);
        randomizeRotation = EditorGUILayout.Toggle("Randomize Rotation", randomizeRotation);

        EditorGUILayout.Space(10);
        DrawModeToolbar();

        switch (mode)
        {
            case Mode.Paint: DrawPaintGUI(); break;
            case Mode.EditRuler: DrawRulerGUI(); break;
            case Mode.EditBorder: DrawBorderGUI(); break;
        }

        SceneView.RepaintAll();
    }

    private void DrawModeToolbar()
    {
        EditorGUILayout.BeginHorizontal();
        DrawModeButton("Paint", Mode.Paint);
        DrawModeButton("Edit Ruler", Mode.EditRuler);
        DrawModeButton("Edit Border", Mode.EditBorder);
        EditorGUILayout.EndHorizontal();
    }

    private void DrawModeButton(string label, Mode m)
    {
        bool active = mode == m;
        GUI.backgroundColor = active ? new Color(0.55f, 0.85f, 0.55f) : Color.white;
        if (GUILayout.Button(label, GUILayout.Height(26)))
            mode = active ? Mode.None : m;
        GUI.backgroundColor = Color.white;
    }

    private void DrawPaintGUI()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Brush", EditorStyles.boldLabel);
        brushRadius = EditorGUILayout.Slider("Brush Radius", brushRadius, 0.5f, 15f);
        treesPerStroke = EditorGUILayout.IntSlider("Trees Per Stroke", treesPerStroke, 1, 12);
        minSpacing = EditorGUILayout.Slider("Min Spacing", minSpacing, 0.1f, 5f);
        EditorGUILayout.HelpBox("Click-drag in Scene View to paint, scattering Trees Per Stroke instances inside the brush each tick (not one-per-click). Shift-drag to erase this root's own children.", MessageType.None);

        EditorGUILayout.Space(6);
        alignToRuler = EditorGUILayout.Toggle("Align To Ruler", alignToRuler);
        if (alignToRuler)
            EditorGUILayout.HelpBox("Placed trees snap onto the ruler segment (set in Edit Ruler mode) instead of the raw brush point.", MessageType.None);
    }

    private void DrawRulerGUI()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Ruler (Vector3 line)", EditorStyles.boldLabel);
        rulerStart = EditorGUILayout.Vector3Field("Start", rulerStart);
        rulerEnd = EditorGUILayout.Vector3Field("End", rulerEnd);
        EditorGUILayout.HelpBox("Drag the two handles in Scene View, or type exact points above. Switch to Paint mode + Align To Ruler to use it.", MessageType.None);
    }

    private void DrawBorderGUI()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Border (Vector3 polygon)", EditorStyles.boldLabel);
        borderText = EditorGUILayout.TextField("Points (x,y,z x,y,z ...)", borderText);
        if (GUILayout.Button("Parse Points Above"))
            ParseBorderText();

        for (int i = 0; i < borderPoints.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            borderPoints[i] = EditorGUILayout.Vector3Field($"Point {i}", borderPoints[i]);
            bool remove = GUILayout.Button("-", GUILayout.Width(22));
            EditorGUILayout.EndHorizontal();
            if (remove)
            {
                borderPoints.RemoveAt(i);
                break;
            }
        }
        if (GUILayout.Button("+ Add Point"))
            borderPoints.Add(borderPoints.Count > 0 ? borderPoints[borderPoints.Count - 1] : Vector3.zero);

        EditorGUILayout.Space(6);
        fillCount = EditorGUILayout.IntField("Fill Count", fillCount);
        if (GUILayout.Button("Fill Border", GUILayout.Height(28)))
            FillBorder();
        EditorGUILayout.HelpBox("Drag corner handles in Scene View, or paste points as \"x,y,z x,y,z ...\" and Parse. Fill Border scatters Fill Count trees through the whole enclosed area at once.", MessageType.None);
    }

    private void ParseBorderText()
    {
        borderPoints.Clear();
        var tokens = borderText.Split(new[] { ' ', '\n', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        foreach (var tok in tokens)
        {
            var parts = tok.Split(',');
            if (parts.Length != 3) continue;
            if (float.TryParse(parts[0], out float x) && float.TryParse(parts[1], out float y) && float.TryParse(parts[2], out float z))
                borderPoints.Add(new Vector3(x, y, z));
        }
        Debug.Log($"[TreePainter] Parsed {borderPoints.Count} border point(s).");
    }

    // ── Scene View ───────────────────────────────────────────────────────────
    private void OnSceneGUI(SceneView sv)
    {
        Handles.color = new Color(0.6f, 1f, 0.3f, 1f);
        Handles.DrawLine(rulerStart, rulerEnd);
        Handles.SphereHandleCap(0, rulerStart, Quaternion.identity, 0.4f, EventType.Repaint);
        Handles.SphereHandleCap(0, rulerEnd, Quaternion.identity, 0.4f, EventType.Repaint);

        if (borderPoints.Count >= 2)
        {
            Handles.color = new Color(1f, 0.6f, 0.1f, 1f);
            for (int i = 0; i < borderPoints.Count; i++)
                Handles.DrawLine(borderPoints[i], borderPoints[(i + 1) % borderPoints.Count]);
        }

        switch (mode)
        {
            case Mode.Paint: HandlePaintMode(sv); break;
            case Mode.EditRuler: HandleRulerMode(); break;
            case Mode.EditBorder: HandleBorderMode(); break;
        }
    }

    private void HandlePaintMode(SceneView sv)
    {
        Event e = Event.current;
        if (!TryGetGroundPoint(e.mousePosition, out Vector3 groundPoint))
            return;

        Handles.color = e.shift ? new Color(1f, 0.3f, 0.2f, 0.6f) : new Color(0.3f, 0.9f, 1f, 0.6f);
        Handles.DrawWireDisc(groundPoint, Vector3.up, brushRadius);

        bool dragging = e.type == EventType.MouseDrag || e.type == EventType.MouseDown;
        if (dragging && e.button == 0 && paintRoot != null)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - lastStrokeTime >= StrokeInterval)
            {
                lastStrokeTime = now;
                if (e.shift) EraseNear(groundPoint);
                else ScatterStroke(groundPoint);
            }
            HandleUtility.AddDefaultControl(0);
            e.Use();
        }
    }

    private void HandleRulerMode()
    {
        EditorGUI.BeginChangeCheck();
        Vector3 newStart = Handles.PositionHandle(rulerStart, Quaternion.identity);
        Vector3 newEnd = Handles.PositionHandle(rulerEnd, Quaternion.identity);
        if (EditorGUI.EndChangeCheck())
        {
            rulerStart = newStart;
            rulerEnd = newEnd;
            Repaint();
        }
    }

    private void HandleBorderMode()
    {
        for (int i = 0; i < borderPoints.Count; i++)
        {
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(borderPoints[i], Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                borderPoints[i] = moved;
                Repaint();
            }
            Handles.Label(borderPoints[i] + Vector3.up * 1.2f, $"Border {i}");
        }
    }

    // ── Placement ────────────────────────────────────────────────────────────
    private void ScatterStroke(Vector3 brushCenter)
    {
        for (int i = 0; i < treesPerStroke; i++)
        {
            Vector2 offset = Random.insideUnitCircle * brushRadius;
            float x = brushCenter.x + offset.x, z = brushCenter.z + offset.y;
            Vector3 groundPoint = GetGroundPointAtXZ(x, z);

            Vector3 finalPoint = groundPoint;
            bool alignedRotation = false;
            float rotY = randomizeRotation ? Random.Range(0f, 360f) : 0f;

            if (alignToRuler)
            {
                Vector3 projected = ProjectOntoRuler(groundPoint);
                finalPoint = GetGroundPointAtXZ(projected.x, projected.z);
                if (!randomizeRotation)
                {
                    rotY = Quaternion.LookRotation(rulerEnd - rulerStart, Vector3.up).eulerAngles.y;
                    alignedRotation = true;
                }
            }

            if (!alignedRotation && !randomizeRotation) rotY = 0f;
            PlaceOne(finalPoint, rotY);
        }
    }

    private void FillBorder()
    {
        if (borderPoints.Count < 3)
        {
            Debug.LogWarning("[TreePainter] Fill Border needs at least 3 points.");
            return;
        }
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in borderPoints)
        {
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
        }

        int placed = 0, attempts = 0, maxAttempts = fillCount * 25;
        while (placed < fillCount && attempts < maxAttempts)
        {
            attempts++;
            float x = Random.Range(minX, maxX), z = Random.Range(minZ, maxZ);
            if (!PointInPolygonXZ(x, z, borderPoints)) continue;
            Vector3 groundPoint = GetGroundPointAtXZ(x, z);
            if (TooCloseToExisting(groundPoint)) continue;

            float rotY = randomizeRotation ? Random.Range(0f, 360f) : 0f;
            PlaceOne(groundPoint, rotY);
            placed++;
        }
        Debug.Log($"[TreePainter] Fill Border placed {placed}/{fillCount} tree(s) after {attempts} attempt(s).");
    }

    private void PlaceOne(Vector3 position, float rotationY)
    {
        if (paintRoot == null || TooCloseToExisting(position)) return;

        GameObject prefab = PickRandomPrefab();
        if (prefab == null) return;

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, paintRoot);
        Undo.RegisterCreatedObjectUndo(instance, "Paint Tree");
        instance.transform.position = position;
        instance.transform.rotation = Quaternion.Euler(0f, rotationY, 0f);

        float scaleY = avgHeightScale * Random.Range(1f - heightVariance, 1f + heightVariance);
        instance.transform.localScale = new Vector3(1f, Mathf.Max(0.05f, scaleY), 1f);
    }

    private GameObject PickRandomPrefab()
    {
        var valid = new List<GameObject>();
        foreach (var p in prefabPool) if (p != null) valid.Add(p);
        if (valid.Count == 0) return null;
        return valid[Random.Range(0, valid.Count)];
    }

    private bool TooCloseToExisting(Vector3 position)
    {
        if (paintRoot == null) return false;
        float sqrMin = minSpacing * minSpacing;
        foreach (Transform child in paintRoot)
            if ((child.position - position).sqrMagnitude < sqrMin)
                return true;
        return false;
    }

    private void EraseNear(Vector3 center)
    {
        if (paintRoot == null) return;
        for (int i = paintRoot.childCount - 1; i >= 0; i--)
        {
            Transform child = paintRoot.GetChild(i);
            if ((child.position - center).sqrMagnitude <= brushRadius * brushRadius)
                Undo.DestroyObjectImmediate(child.gameObject);
        }
    }

    private Vector3 ProjectOntoRuler(Vector3 point)
    {
        Vector3 ab = rulerEnd - rulerStart;
        float lenSqr = ab.sqrMagnitude;
        if (lenSqr < 0.0001f) return rulerStart;
        float t = Mathf.Clamp01(Vector3.Dot(point - rulerStart, ab) / lenSqr);
        return rulerStart + ab * t;
    }

    /// <summary>Standard even-odd ray-casting point-in-polygon test, run on
    /// the X/Z plane (Y ignored -- the border is a footprint, not a volume).</summary>
    private static bool PointInPolygonXZ(float x, float z, List<Vector3> poly)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float xi = poly[i].x, zi = poly[i].z;
            float xj = poly[j].x, zj = poly[j].z;
            bool intersects = ((zi > z) != (zj > z)) &&
                (x < (xj - xi) * (z - zi) / (zj - zi) + xi);
            if (intersects) inside = !inside;
        }
        return inside;
    }

    /// <summary>Raycasts straight down against real scene colliders so trees
    /// land on actual ground; falls back to the y=0 plane if nothing's hit.</summary>
    private static bool TryGetGroundPoint(Vector2 mouseOrGuiPos, out Vector3 point)
    {
        Ray ray = HandleUtility.GUIPointToWorldRay(mouseOrGuiPos);
        if (Physics.Raycast(ray, out RaycastHit hit, 2000f))
        {
            point = hit.point;
            return true;
        }
        Plane ground = new Plane(Vector3.up, Vector3.zero);
        if (ground.Raycast(ray, out float dist))
        {
            point = ray.GetPoint(dist);
            return true;
        }
        point = Vector3.zero;
        return false;
    }

    /// <summary>Used by ScatterStroke/FillBorder, which already have an XZ
    /// target and just need the ground height under it -- casts straight
    /// down from well above the whole scene, falling back to y=0 if nothing
    /// is hit (no ambiguity about "how high is high enough" like a shared
    /// probe-point overload would have).</summary>
    private static Vector3 GetGroundPointAtXZ(float x, float z)
    {
        Vector3 from = new Vector3(x, 2000f, z);
        if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, 4000f))
            return hit.point;
        return new Vector3(x, 0f, z);
    }
}
#endif
