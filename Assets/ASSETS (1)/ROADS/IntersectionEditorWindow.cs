using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;

// ═══════════════════════════════════════════════════════════════════════════════
//  INTERSECTION & TRAFFIC LIGHT EDITOR  (Tools > City Building)
//
//  Establishes a real "Tools > City Building" menu root for this project --
//  none existed before this. The only prior menu precedent anywhere in the
//  codebase was "Tools > River Maker" (a single static menu item, no
//  window); a full-project search for BusStopMaker/BusRouteMaker/
//  CityLineMaker -- referenced by name in CityManager.BuildCity()'s own doc
//  comment as "EditorWindows" -- found zero actual implementations of any
//  of them. So this is the first real tool under Tools/City Building, not a
//  port of something that already existed.
//
//  Was originally a section inside CityManagerEditor's Inspector (items
//  B/E), moved out here for two reasons: (1) matches how every other editor
//  tool in this project is reached -- a menu item, not buried in a
//  component's Inspector -- and (2) editing EXISTING intersections worked
//  fine in the Inspector, but there was no way to add a brand-new one
//  without typing raw X/Y/Z coordinates blind. This window adds that:
//  type the two road codes, click "place," then click anywhere in the
//  Scene view to drop it there.
// ═══════════════════════════════════════════════════════════════════════════════
public class IntersectionEditorWindow : EditorWindow
{
    [MenuItem("Tools/City Building/Intersection & Traffic Light Editor")]
    public static void ShowWindow()
    {
        var win = GetWindow<IntersectionEditorWindow>("Intersections");
        win.Show();
    }

    private CityManager _city;
    private Vector2 _scroll;
    private int _selectedIndex = -1;

    private bool _placingNew = false;
    private string _newRoadACode = "";
    private string _newRoadBCode = "";

    // [FIX] Was `new SerializedObject(_city)` created fresh inside DrawList()
    // every single OnGUI repaint. Unity tracks a SerializedProperty's
    // foldout/expanded state (needed for the nested headsA/headsB arrays
    // inside the PropertyField below) keyed off the SerializedObject
    // instance -- recreating that instance every frame threw the state
    // tracking off badly enough that clicking to expand Heads A/B could
    // throw a GUI layout-mismatch exception mid-repaint, which is what was
    // actually closing the window. Cached now, one instance per _city,
    // Update()'d at the top of OnGUI and applied at the bottom -- the
    // normal Unity custom-editor pattern.
    private SerializedObject _so;

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUIHandler;
        if (_city == null) _city = FindFirstObjectByType<CityManager>();
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUIHandler;
    }

    private void OnGUI()
    {
        var newCity = (CityManager)EditorGUILayout.ObjectField("City Manager", _city, typeof(CityManager), true);
        if (newCity != _city) { _city = newCity; _so = null; }
        if (_city == null)
        {
            EditorGUILayout.HelpBox("Assign (or open a scene containing) a CityManager to use this tool.", MessageType.Info);
            return;
        }

        if (_so == null || _so.targetObject != _city) _so = new SerializedObject(_city);
        _so.Update();

        EditorGUILayout.Space(6);
        DrawAddSection();

        EditorGUILayout.Space(10);
        DrawList();

        _so.ApplyModifiedProperties();
    }

    // ── B: add a new intersection ─────────────────────────────────────────────
    private void DrawAddSection()
    {
        EditorGUILayout.LabelField("Add New Intersection", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        _newRoadACode = EditorGUILayout.TextField("Road A", _newRoadACode);
        _newRoadBCode = EditorGUILayout.TextField("Road B", _newRoadBCode);
        EditorGUILayout.EndHorizontal();

        bool canPlace = !string.IsNullOrEmpty(_newRoadACode) && !string.IsNullOrEmpty(_newRoadBCode);
        GUI.enabled = canPlace;
        bool wasPlacing = _placingNew;
        _placingNew = GUILayout.Toggle(_placingNew, _placingNew ? "Click Scene View to Place..." : "Click Scene View to Place New Intersection", "Button");
        if (_placingNew && !wasPlacing) SceneView.RepaintAll();
        GUI.enabled = true;
        if (!canPlace)
            EditorGUILayout.HelpBox("Enter both road codes first.", MessageType.None);
    }

    // ── B: finder / list ───────────────────────────────────────────────────────
    private void DrawList()
    {
        if (_city.manualIntersections == null) return;
        EditorGUILayout.LabelField($"Manual Intersections ({_city.manualIntersections.Count})", EditorStyles.boldLabel);

        var listProp = _so.FindProperty("manualIntersections");
        int deleteIndex = -1;

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        for (int i = 0; i < listProp.arraySize; i++)
        {
            var el = listProp.GetArrayElementAtIndex(i);
            string codeA = el.FindPropertyRelative("roadACode")?.stringValue ?? "?";
            string codeB = el.FindPropertyRelative("roadBCode")?.stringValue ?? "?";

            EditorGUILayout.BeginHorizontal();
            bool selected = _selectedIndex == i;
            if (GUILayout.Toggle(selected, $"{codeA} × {codeB}", "Button"))
                _selectedIndex = i;
            else if (selected)
                _selectedIndex = -1;
            if (GUILayout.Button("Frame", GUILayout.Width(60)))
            {
                _selectedIndex = i;
                var posProp = el.FindPropertyRelative("position");
                if (posProp != null && SceneView.lastActiveSceneView != null)
                    SceneView.lastActiveSceneView.LookAt(posProp.vector3Value);
            }
            if (GUILayout.Button("Delete", GUILayout.Width(60)))
                deleteIndex = i;
            EditorGUILayout.EndHorizontal();

            if (_selectedIndex == i)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(el, GUIContent.none, true);
                EditorGUI.indentLevel--;
            }
        }
        EditorGUILayout.EndScrollView();

        if (deleteIndex >= 0)
        {
            listProp.DeleteArrayElementAtIndex(deleteIndex);
            if (_selectedIndex == deleteIndex) _selectedIndex = -1;
        }

        if (_selectedIndex >= 0 || _placingNew) SceneView.RepaintAll();
    }

    // ── Scene view: placement + item E head handles ─────────────────────────────
    private void OnSceneGUIHandler(SceneView sceneView)
    {
        if (_city == null) return;

        if (_placingNew)
        {
            HandlePlacementClick();
            return; // don't also draw/edit head handles while placing
        }

        if (_selectedIndex < 0 || _city.manualIntersections == null || _selectedIndex >= _city.manualIntersections.Count) return;
        var def = _city.manualIntersections[_selectedIndex];
        if (def == null) return;

        var roadA = ResolveRoad(def.roadACode);
        var roadB = ResolveRoad(def.roadBCode);
        // Near/Far are independent lists (see IntersectionDefinition) --
        // offset their handle clusters apart along the road so dragging one
        // doesn't visually overlap the other, same as the always-on gizmo.
        const float armOffset = 4f;
        if (roadA != null)
        {
            Vector3 dA = roadA.EvaluateTangent(0.5f).normalized;
            DrawHeadHandles(def.position - dA * armOffset, def.headsANear, dA, roadA.roadWidth, $"Road A Near ({def.roadACode})", false);
            DrawHeadHandles(def.position + dA * armOffset, def.headsAFar,  dA, roadA.roadWidth, $"Road A Far ({def.roadACode})", true);
        }
        if (roadB != null)
        {
            Vector3 dB = roadB.EvaluateTangent(0.5f).normalized;
            DrawHeadHandles(def.position - dB * armOffset, def.headsBNear, dB, roadB.roadWidth, $"Road B Near ({def.roadBCode})", false);
            DrawHeadHandles(def.position + dB * armOffset, def.headsBFar,  dB, roadB.roadWidth, $"Road B Far ({def.roadBCode})", true);
        }
    }

    private void HandlePlacementClick()
    {
        Handles.BeginGUI();
        GUILayout.BeginArea(new Rect(10, 10, 320, 24));
        GUILayout.Box("Click in the Scene view to place the new intersection.");
        GUILayout.EndArea();
        Handles.EndGUI();

        Event e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
        {
            Plane ground = new Plane(Vector3.up, _city.transform.position);
            Ray   ray    = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            if (ground.Raycast(ray, out float dist))
            {
                Vector3 hit = ray.GetPoint(dist);
                Undo.RecordObject(_city, "Add Manual Intersection");
                if (_city.manualIntersections == null) _city.manualIntersections = new List<IntersectionDefinition>();
                _city.manualIntersections.Add(new IntersectionDefinition
                {
                    roadACode = _newRoadACode,
                    roadBCode = _newRoadBCode,
                    position  = hit,
                });
                EditorUtility.SetDirty(_city);
                _placingNew    = false;
                _selectedIndex = _city.manualIntersections.Count - 1;
                e.Use();
                Repaint();
            }
        }
        HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
    }

    // ── Item E: head handles ────────────────────────────────────────────────────
    /// <summary>GetRoad() reads CityManager's internal _roadByCode, which is
    /// only populated once BuildCity() has actually run this session --
    /// falls back to a temp RoadSegment built straight from roadDefinitions
    /// so this still works in a fresh Edit Mode session before any build.</summary>
    private RoadSegment ResolveRoad(string code)
    {
        var r = _city.GetRoad(code);
        if (r != null) return r;
        if (_city.roadDefinitions == null) return null;
        var d = _city.roadDefinitions.Find(rd => rd != null && rd.roadCode == code);
        if (d == null) return null;
        var pts = d.BuildControlPoints();
        if (pts == null || pts.Length < 2) return null;
        // roadWidth matters here now -- it's what GetLateralOffset/
        // GetHeadWidth convert percent against -- so carry it over from the
        // definition instead of leaving it at RoadSegment's own 7m default.
        return new RoadSegment { controlPoints = pts, roadWidth = d.roadWidth };
    }

    /// <summary>Drags the whole head (its start-end SPAN stays fixed,
    /// only its position shifts) along the road's width -- resizing the
    /// span itself is precise-number territory (type exact values like
    /// 0/33/66/100 for an even 3-way split into the Heads A/B list in the
    /// Inspector, which the moved-out PropertyField above already exposes),
    /// dragging is for "roughly here" placement. Clamps so the span can't
    /// get pushed past either edge of the road.</summary>
    private void DrawHeadHandles(Vector3 centerPos, List<TrafficLightHead> heads, Vector3 roadDir, float roadWidth, string roadLabel, bool isFarSide)
    {
        if (heads == null) return;
        // [FIX] Canonicalized -- see TrafficLightHead.CanonicalRoadDir's own comment.
        Vector3 roadRight = Vector3.Cross(Vector3.up, TrafficLightHead.CanonicalRoadDir(roadDir)).normalized;
        float   halfWidth = Mathf.Max(0.01f, roadWidth * 0.5f);

        // Same 0%->100% direction arrow as the always-on gizmo (CityManager.
        // DrawHeadGizmos) -- confined to just this list's own half, mirrored
        // (see TrafficLightHead.GetLateralOffset's comment). Near's arrow
        // runs centerline(0%)->outer edge(100%); Far's runs the other
        // direction, outer edge(0%)->centerline(100%), on the OTHER half.
        Vector3 zeroPct    = centerPos + roadRight * (isFarSide ? -halfWidth : 0f) + Vector3.up * 0.9f;
        Vector3 hundredPct = centerPos + roadRight * (isFarSide ? 0f : halfWidth)  + Vector3.up * 0.9f;
        Handles.color = new Color(1f, 1f, 1f, 0.5f);
        Handles.DrawLine(zeroPct, hundredPct);
        Handles.ConeHandleCap(0, hundredPct, Quaternion.LookRotation(roadRight, Vector3.up), 0.5f, EventType.Repaint);
        Handles.Label(zeroPct    + Vector3.up * 0.5f, $"{roadLabel}\n0%");
        Handles.Label(hundredPct + Vector3.up * 0.5f, "100%");

        for (int i = 0; i < heads.Count; i++)
        {
            var head = heads[i];
            if (head == null) continue;
            Vector3 worldPos = centerPos + roadRight * head.GetLateralOffset(roadWidth, isFarSide) + Vector3.up * 0.6f;

            EditorGUI.BeginChangeCheck();
            Handles.color = head.enabled ? Color.white : Color.gray;
            // 1D slider along the road's right vector.
            Vector3 newPos = Handles.Slider(worldPos, roadRight, 1.2f, Handles.CubeHandleCap, 0.25f);
            if (EditorGUI.EndChangeCheck())
            {
                float newOffset     = Vector3.Dot(newPos - centerPos, roadRight);
                // Inverse of GetLateralOffset's confined+mirrored mapping.
                float newCenterPct  = isFarSide
                    ? 100f + (newOffset / halfWidth) * 100f
                    : (newOffset / halfWidth) * 100f;
                float oldCenterPct  = (head.startPercent + head.endPercent) * 0.5f;
                float deltaPct      = newCenterPct - oldCenterPct;
                float span          = head.endPercent - head.startPercent;

                // Clamp so the dragged span stays within [0, 100] instead of
                // letting either edge slide off the road.
                float newStart = Mathf.Clamp(head.startPercent + deltaPct, 0f, 100f - span);
                Undo.RecordObject(_city, "Move Traffic Light Head");
                head.startPercent = newStart;
                head.endPercent   = newStart + span;
                EditorUtility.SetDirty(_city);
            }
            Handles.Label(worldPos + Vector3.up * 1.2f, $"Head {i}: {head.symbol}  {head.startPercent:0}-{head.endPercent:0}%");
        }
    }
}
#endif
