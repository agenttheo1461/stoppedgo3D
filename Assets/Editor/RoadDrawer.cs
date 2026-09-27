#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROAD DRAWER — click-to-place road tool
//
//  Usage:
//    1. Open via Tools > Transit > Road Drawer
//    2. Click "Start Drawing"
//    3. Click points in the Scene view (on the ground plane, y=0) to lay down
//       control points. Hold Shift+Click to place a "curve" point (isCurve=true
//       equivalent — for RoadSegmentDefinition this just means Bezier mode if
//       3+ points are curve points in a row).
//    4. Press Enter or click "Finish Road" to bake it into a new
//       RoadSegmentDefinition and add it to the selected CityManager.
//    5. Press Escape or "Cancel" to discard.
// ═══════════════════════════════════════════════════════════════════════════════
public class RoadDrawer : EditorWindow
{
    private CityManager _targetCity;
    private List<Vector3> _points = new();
    private bool _drawing = false;

    // New road settings
    private string _roadName  = "New Road";
    private string _roadCode  = "RD01";
    private float  _roadWidth = 7f;
    private float  _greenTime = 10f;
    private bool   _isOneWay  = false;
    private bool   _reverseFlow = false;
    private bool   _canHaveIntersection = true;
    [Range(0.1f, 1f)] private float _meshResolution = 0.5f;

    private float _placementY = 0f;
    private float _snapGrid   = 0f; // 0 = no snapping

    [MenuItem("Tools/Transit/Road Drawer")]
    public static void ShowWindow()
    {
        GetWindow<RoadDrawer>("Road Drawer");
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Road Drawer", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        _targetCity = (CityManager)EditorGUILayout.ObjectField(
            "Target CityManager", _targetCity, typeof(CityManager), true);

        if (_targetCity == null)
        {
            EditorGUILayout.HelpBox("Assign a CityManager to add roads to.", MessageType.Info);
            var found = FindObjectOfType<CityManager>();
            if (found != null && GUILayout.Button($"Use '{found.name}' in scene"))
                _targetCity = found;
            return;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Road Settings", EditorStyles.boldLabel);
        _roadName  = EditorGUILayout.TextField("Road Name", _roadName);
        _roadCode  = EditorGUILayout.TextField("Road Code", _roadCode);
        _roadWidth = EditorGUILayout.FloatField("Road Width", _roadWidth);
        _meshResolution = EditorGUILayout.Slider("Mesh Resolution", _meshResolution, 0.1f, 1f);
        _greenTime = EditorGUILayout.FloatField("Green Time (intersections)", _greenTime);
        _canHaveIntersection = EditorGUILayout.Toggle("Can Have Intersection", _canHaveIntersection);
        _isOneWay    = EditorGUILayout.Toggle("One-Way", _isOneWay);
        if (_isOneWay)
            _reverseFlow = EditorGUILayout.Toggle("Reverse Flow", _reverseFlow);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Placement", EditorStyles.boldLabel);
        _placementY = EditorGUILayout.FloatField("Placement Y", _placementY);
        _snapGrid   = EditorGUILayout.FloatField("Snap Grid (0 = off)", _snapGrid);

        EditorGUILayout.Space();

        if (!_drawing)
        {
            if (GUILayout.Button("Start Drawing", GUILayout.Height(30)))
            {
                _drawing = true;
                _points.Clear();
                SceneView.RepaintAll();
            }
        }
        else
        {
            EditorGUILayout.HelpBox(
                "Click in the Scene view to place points.\n" +
                "Click on an existing point to remove it.\n" +
                "Enter = Finish & Bake Road\n" +
                "Escape = Cancel",
                MessageType.Info);

            EditorGUILayout.LabelField($"Points placed: {_points.Count}");

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Undo Last Point") && _points.Count > 0)
                _points.RemoveAt(_points.Count - 1);
            if (GUILayout.Button("Clear All Points"))
                _points.Clear();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            GUI.enabled = _points.Count >= 2;
            if (GUILayout.Button("Finish Road & Add to CityManager", GUILayout.Height(30)))
                FinishRoad();
            GUI.enabled = true;

            if (GUILayout.Button("Cancel"))
                CancelDrawing();
        }
    }

    private void OnSceneGUI(SceneView sceneView)
    {
        if (!_drawing) return;

        Event e = Event.current;
        HandleUtility.Repaint();

        // Project mouse to a horizontal plane at _placementY
        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        Plane plane = new Plane(Vector3.up, new Vector3(0, _placementY, 0));

        Vector3 hitPoint = Vector3.zero;
        bool hasHit = false;
        if (plane.Raycast(ray, out float dist))
        {
            hitPoint = ray.GetPoint(dist);
            if (_snapGrid > 0.001f)
            {
                hitPoint.x = Mathf.Round(hitPoint.x / _snapGrid) * _snapGrid;
                hitPoint.z = Mathf.Round(hitPoint.z / _snapGrid) * _snapGrid;
            }
            hasHit = true;
        }

        // ── Draw existing points & path ──
        Handles.color = Color.cyan;
        for (int i = 0; i < _points.Count; i++)
        {
            float handleSize = HandleUtility.GetHandleSize(_points[i]) * 0.15f;
            Handles.SphereHandleCap(0, _points[i], Quaternion.identity, handleSize, EventType.Repaint);
            Handles.Label(_points[i] + Vector3.up * 1.5f, $"P{i}");
        }
        Handles.color = Color.yellow;
        for (int i = 0; i < _points.Count - 1; i++)
            Handles.DrawLine(_points[i], _points[i + 1]);

        // ── Preview line to cursor ──
if (hasHit && _points.Count > 0)
{
    Handles.color = new Color(1f, 1f, 0f, 0.4f);
    DrawDashedLineManual(_points[_points.Count - 1], hitPoint, 0.5f);
}

        // ── Preview cursor point ──
        if (hasHit)
        {
            Handles.color = Color.green;
            float previewSize = HandleUtility.GetHandleSize(hitPoint) * 0.12f;
            Handles.SphereHandleCap(0, hitPoint, Quaternion.identity, previewSize, EventType.Repaint);
        }

        // ── Input handling ──
        if (e.type == EventType.MouseDown && e.button == 0 && hasHit)
        {
            // Check if clicking near an existing point → remove it
            int clickedIdx = -1;
            for (int i = 0; i < _points.Count; i++)
            {
                float screenDist = HandleUtility.DistanceToCircle(_points[i], 0.01f);
                float worldDist  = Vector3.Distance(_points[i], hitPoint);
                float removeRadius = HandleUtility.GetHandleSize(_points[i]) * 0.3f;
                if (worldDist < removeRadius) { clickedIdx = i; break; }
            }

            if (clickedIdx >= 0)
            {
                _points.RemoveAt(clickedIdx);
            }
            else
            {
                _points.Add(hitPoint);
            }

            e.Use();
            Repaint();
        }

        if (e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                if (_points.Count >= 2) FinishRoad();
                e.Use();
            }
            else if (e.keyCode == KeyCode.Escape)
            {
                CancelDrawing();
                e.Use();
            }
        }

        // Prevent clicks from selecting/deselecting objects in the scene
        if (e.type == EventType.Layout)
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
    }
private static void DrawDashedLineManual(Vector3 from, Vector3 to, float dashLength)
{
    float distance = Vector3.Distance(from, to);
    if (distance < 0.001f) return;

    Vector3 dir = (to - from).normalized;
    int segments = Mathf.Max(1, Mathf.FloorToInt(distance / dashLength));

    for (int i = 0; i < segments; i += 2)
    {
        Vector3 segStart = from + dir * (i * dashLength);
        float endDist = Mathf.Min((i + 1) * dashLength, distance);
        Vector3 segEnd = from + dir * endDist;
        Handles.DrawLine(segStart, segEnd);
    }
}
    private void FinishRoad()
    {
        if (_points.Count < 2)
        {
            EditorUtility.DisplayDialog("Road Drawer", "Need at least 2 points to make a road.", "OK");
            return;
        }

        var newRoad = new RoadSegmentDefinition
        {
            roadName            = _roadName,
            roadCode            = _roadCode,
            roadWidth           = _roadWidth,
            meshResolution      = _meshResolution,
            canHaveIntersection = _canHaveIntersection,
            isOneWay            = _isOneWay,
            reverseFlow         = _reverseFlow,
            greenTime           = _greenTime,
            curveMode           = RoadCurveMode.ControlPoints,
            controlPoints       = new List<Vector3>(_points),
        };

        Undo.RecordObject(_targetCity, "Add Road via Road Drawer");
        _targetCity.roadDefinitions.Add(newRoad);
        EditorUtility.SetDirty(_targetCity);

        Debug.Log($"[RoadDrawer] Added road '{_roadName}' ({_roadCode}) with {_points.Count} points to {_targetCity.name}.");

        // Auto-increment road code for next road (e.g. RD01 -> RD02)
        _roadCode = IncrementCode(_roadCode);

        _points.Clear();
        _drawing = false;
        SceneView.RepaintAll();
        Repaint();
    }

    private void CancelDrawing()
    {
        _points.Clear();
        _drawing = false;
        SceneView.RepaintAll();
        Repaint();
    }

    private static string IncrementCode(string code)
    {
        // Find trailing digits and increment them, preserving leading zeros
        int i = code.Length;
        while (i > 0 && char.IsDigit(code[i - 1])) i--;

        string prefix = code.Substring(0, i);
        string digits = code.Substring(i);

        if (digits.Length == 0) return code + "2";

        int num = int.Parse(digits) + 1;
        return prefix + num.ToString().PadLeft(digits.Length, '0');
    }
}
#endif