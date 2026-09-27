#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  CITY MANAGER INSPECTOR (Regular Style - Fixed Nesting)
// ═══════════════════════════════════════════════════════════════════════════════
[CustomEditor(typeof(CityManager))]
public class CityManagerEditor : Editor
{
    // Foldout state
    private bool _showRoads         = true;
    private bool _showStops         = true;
    private bool _showRoutes        = false;
    private bool _showPlayerBus     = true;
    private bool _showIntersections = true; // Added foldout state
    private bool _showCenterLines   = false;

    private ReorderableList _roadList;
    private ReorderableList _stopList;

    private Dictionary<int, bool> _roadFoldouts = new();
    private Dictionary<int, bool> _stopFoldouts = new();

    private SerializedProperty _roadDefsProp;
    private SerializedProperty _stopDefsProp;

    // [PERF FIX] Was: IsDuplicateStopCode() did a full O(n) scan over every
    // stop, and it ran once per row inside BOTH elementHeightCallback and
    // drawElementCallback — which ReorderableList invokes for every element
    // on every single repaint (mouse move, animation, anything). That's
    // O(n²) work recomputed many times a second — with a couple hundred
    // stops this alone was enough to make the Inspector crawl. Now the
    // duplicate set is built once per OnInspectorGUI call (O(n)) and every
    // row does an O(1) HashSet lookup instead.
    private readonly HashSet<string> _duplicateStopCodes = new();
 
    private void RefreshDuplicateStopCodes()
    {
        _duplicateStopCodes.Clear();
        if (_stopDefsProp == null) return;

        var counts = new Dictionary<string, int>();
        for (int i = 0; i < _stopDefsProp.arraySize; i++)
        {
            var c = _stopDefsProp.GetArrayElementAtIndex(i).FindPropertyRelative("stopCode")?.stringValue;
            if (string.IsNullOrEmpty(c)) continue;
            counts.TryGetValue(c, out int n);
            counts[c] = n + 1;
        }

        foreach (var kv in counts)
            if (kv.Value > 1) _duplicateStopCodes.Add(kv.Key);
    }

private void OnEnable()
{
    _roadDefsProp = serializedObject.FindProperty("roadDefinitions");
    _stopDefsProp = serializedObject.FindProperty("stopDefinitions");

    _intersectionDefsProp =
        serializedObject.FindProperty("manualIntersections");

    BuildRoadList();
    BuildStopList();
    BuildIntersectionList();
}

    // ═════════════════════════════════════════════════════════════════════════
    //  ROAD LIST
    // ═════════════════════════════════════════════════════════════════════════
private void BuildRoadList()
    {
        _roadList = new ReorderableList(serializedObject, _roadDefsProp,
            draggable: true, displayHeader: true,
            displayAddButton: true, displayRemoveButton: true);

        _roadList.drawHeaderCallback = rect =>
        {
            EditorGUI.LabelField(rect, "Road Segments", EditorStyles.boldLabel);
        };

        _roadList.elementHeightCallback = index =>
        {
            if (index >= _roadDefsProp.arraySize) return 0f;
            
            float baseLine = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            if (!_roadFoldouts.TryGetValue(index, out bool open) || !open)
                return baseLine;

            var el = _roadDefsProp.GetArrayElementAtIndex(index);
            float h = baseLine; 

            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("roadName"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("roadCode"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("roadWidth"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("meshResolution"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("roadMaterial"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("curveMode"), true) + EditorGUIUtility.standardVerticalSpacing;
            
            // Traffic Flow properties
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("canHaveIntersection"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("isOneWay"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("reverseFlow"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("hideCenterLine"), true) + EditorGUIUtility.standardVerticalSpacing;

            var modeProp = el.FindPropertyRelative("curveMode");
            var mode = (RoadCurveMode)modeProp.enumValueIndex;
            if (mode == RoadCurveMode.ControlPoints)
            {
                h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("controlPoints"), true) + EditorGUIUtility.standardVerticalSpacing;
            }
            else
            {
                h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("mathStart"), true) + EditorGUIUtility.standardVerticalSpacing;
                h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("mathEnd"), true) + EditorGUIUtility.standardVerticalSpacing;
                h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("waveform"), true) + EditorGUIUtility.standardVerticalSpacing;
                h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("amplitude"), true) + EditorGUIUtility.standardVerticalSpacing;
                h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("frequency"), true) + EditorGUIUtility.standardVerticalSpacing;
                h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("phase"), true) + EditorGUIUtility.standardVerticalSpacing;
                h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("sampleCount"), true) + EditorGUIUtility.standardVerticalSpacing;
                h += (EditorGUIUtility.singleLineHeight * 2f) + EditorGUIUtility.standardVerticalSpacing; 
            }

            return h + 6f; 
        };

        _roadList.drawElementCallback = (rect, index, isActive, isFocused) =>
        {
            if (index >= _roadDefsProp.arraySize) return;
            var el = _roadDefsProp.GetArrayElementAtIndex(index);
            
            if (!_roadFoldouts.ContainsKey(index)) _roadFoldouts[index] = false;

            float y = rect.y + 2f;
            float w = rect.width;
            float x = rect.x;

            var nameProp = el.FindPropertyRelative("roadName");
            var codeProp = el.FindPropertyRelative("roadCode");
            var modeProp = el.FindPropertyRelative("curveMode");
            var mode     = (RoadCurveMode)modeProp.enumValueIndex;

            string displayLabel = string.IsNullOrEmpty(nameProp.stringValue) ? "Unnamed Road" : nameProp.stringValue;
            _roadFoldouts[index] = EditorGUI.Foldout(
                new Rect(x, y, w, EditorGUIUtility.singleLineHeight),
                _roadFoldouts[index],
                $"[{codeProp.stringValue}] {displayLabel} ({mode})",
                true
            );

            y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            if (!_roadFoldouts[index]) return;

            x += 12f;
            w -= 12f;

            DrawField(x, ref y, w, nameProp, "Road Name");
            DrawField(x, ref y, w, codeProp, "Road Code");
            DrawField(x, ref y, w, el.FindPropertyRelative("roadWidth"), "Road Width (m)");
            DrawField(x, ref y, w, el.FindPropertyRelative("meshResolution"), "Mesh Resolution");
            DrawField(x, ref y, w, el.FindPropertyRelative("roadMaterial"), "Road Material");
            DrawField(x, ref y, w, modeProp, "Curve Mode");
            
            // Traffic Flow Checkboxes
            DrawField(x, ref y, w, el.FindPropertyRelative("canHaveIntersection"), "Can Have Intersection");
            DrawField(x, ref y, w, el.FindPropertyRelative("isOneWay"), "Is One-Way");
            DrawField(x, ref y, w, el.FindPropertyRelative("reverseFlow"), "Reverse Flow");
            DrawField(x, ref y, w, el.FindPropertyRelative("hideCenterLine"), "Hide Center Line");

            if (mode == RoadCurveMode.ControlPoints)
            {
                DrawField(x, ref y, w, el.FindPropertyRelative("controlPoints"), "Control Points");
            }
            else
            {
                DrawField(x, ref y, w, el.FindPropertyRelative("mathStart"), "Start Point");
                DrawField(x, ref y, w, el.FindPropertyRelative("mathEnd"), "End Point");
                DrawField(x, ref y, w, el.FindPropertyRelative("waveform"), "Waveform");
                DrawField(x, ref y, w, el.FindPropertyRelative("amplitude"), "Amplitude (m)");
                DrawField(x, ref y, w, el.FindPropertyRelative("frequency"), "Frequency (cycles)");
                DrawField(x, ref y, w, el.FindPropertyRelative("phase"), "Phase (radians)");
                DrawField(x, ref y, w, el.FindPropertyRelative("sampleCount"), "Sample Count");

                float hintH = EditorGUIUtility.singleLineHeight * 2f;
                EditorGUI.LabelField(new Rect(x, y, w, hintH), 
                    "lateral(t)  =  Amplitude × Waveform( t × Frequency × 2π + Phase )", EditorStyles.miniLabel);
                y += hintH + EditorGUIUtility.standardVerticalSpacing;
            }
        };

        _roadList.onAddCallback = list =>
        {
            _roadDefsProp.arraySize++;
            var el = _roadDefsProp.GetArrayElementAtIndex(_roadDefsProp.arraySize - 1);
            el.FindPropertyRelative("roadName").stringValue      = "New Road";
            el.FindPropertyRelative("roadCode").stringValue      = $"R{_roadDefsProp.arraySize:D3}";
            el.FindPropertyRelative("roadWidth").floatValue      = 7f;
            el.FindPropertyRelative("meshResolution").floatValue = 0.5f;
            el.FindPropertyRelative("curveMode").enumValueIndex  = 0;
            // Set defaults for new fields
            el.FindPropertyRelative("canHaveIntersection").boolValue = true;
            el.FindPropertyRelative("isOneWay").boolValue = false;
            el.FindPropertyRelative("reverseFlow").boolValue = false;
            el.FindPropertyRelative("hasSidewalks").boolValue = true;
            el.FindPropertyRelative("hideCenterLine").boolValue = false; // false = show the line, which is the desired default

            el.FindPropertyRelative("greenTime").floatValue      = 10f;
            el.FindPropertyRelative("laneCount").intValue        = 0;
            el.FindPropertyRelative("laneWidth").floatValue      = 0f;
            el.FindPropertyRelative("speedLimit").floatValue     = 0f;

            var cp = el.FindPropertyRelative("controlPoints");
            cp.ClearArray();
            cp.arraySize = 2;
            cp.GetArrayElementAtIndex(0).vector3Value = Vector3.zero;
            cp.GetArrayElementAtIndex(1).vector3Value = new Vector3(0, 0, 100);
            serializedObject.ApplyModifiedProperties();
        };
    }
private ReorderableList _intersectionList;
private SerializedProperty _intersectionDefsProp;


private void BuildIntersectionList()
{
    if (_intersectionDefsProp == null)
        return;

    _intersectionList = new ReorderableList(
        serializedObject,
        _intersectionDefsProp,
        true,
        true,
        true,
        true);

    _intersectionList.drawHeaderCallback = rect =>
    {
        EditorGUI.LabelField(rect, "Manual Intersections");
    };

    _intersectionList.elementHeightCallback = index =>
    {
        if (index >= _intersectionDefsProp.arraySize)
            return EditorGUIUtility.singleLineHeight;

        var el = _intersectionDefsProp.GetArrayElementAtIndex(index);

        return EditorGUI.GetPropertyHeight(el, true) + 6f;
    };

    _intersectionList.drawElementCallback =
        (rect, index, active, focused) =>
    {
        if (index >= _intersectionDefsProp.arraySize)
            return;

        var el = _intersectionDefsProp.GetArrayElementAtIndex(index);

        rect.y += 2f;

        EditorGUI.PropertyField(
            new Rect(
                rect.x,
                rect.y,
                rect.width,
                EditorGUI.GetPropertyHeight(el, true)),
            el,
            GUIContent.none,
            true);
    };
}
    // ═════════════════════════════════════════════════════════════════════════
    //  STOP LIST
    // ═════════════════════════════════════════════════════════════════════════
    private void BuildStopList()
    {
        _stopList = new ReorderableList(serializedObject, _stopDefsProp,
            draggable: true, displayHeader: true,
            displayAddButton: true, displayRemoveButton: true);

        _stopList.drawHeaderCallback = rect =>
        {
            EditorGUI.LabelField(rect, "Bus Stops", EditorStyles.boldLabel);
        };

        _stopList.elementHeightCallback = index =>
        {
            if (index >= _stopDefsProp.arraySize) return 0f;

            float baseLine = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            // The duplicate-code warning box renders regardless of foldout
            // state (it's the whole point — a collapsed row shouldn't hide
            // the fact that its code collides with another stop), so its
            // height has to be reserved here too or rows overlap.
            var code = _stopDefsProp.GetArrayElementAtIndex(index).FindPropertyRelative("stopCode")?.stringValue;
            float dupWarnH = 0f;
            if (!string.IsNullOrEmpty(code) && _duplicateStopCodes.Contains(code))
                dupWarnH = EditorGUIUtility.singleLineHeight * 1.6f + EditorGUIUtility.standardVerticalSpacing;

            if (!_stopFoldouts.TryGetValue(index, out bool open) || !open)
                return baseLine + dupWarnH;

            var el = _stopDefsProp.GetArrayElementAtIndex(index);
            float h = baseLine + dupWarnH;

            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("stopCode"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("stopName"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("parentRoadCode"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("tValue"), true) + EditorGUIUtility.standardVerticalSpacing;
            
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("hasShelter"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("isTerminal"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("isLayover"), true) + EditorGUIUtility.standardVerticalSpacing;
            h += EditorGUI.GetPropertyHeight(el.FindPropertyRelative("isAccessible"), true) + EditorGUIUtility.standardVerticalSpacing;

            return h + 6f;
        };

        // Duplicate-code membership is precomputed once per OnInspectorGUI
        // pass into _duplicateStopCodes (see RefreshDuplicateStopCodes) —
        // no per-row scanning here anymore.

        _stopList.drawElementCallback = (rect, index, isActive, isFocused) =>
        {
            if (index >= _stopDefsProp.arraySize) return;
            var el       = _stopDefsProp.GetArrayElementAtIndex(index);
            var codeProp = el.FindPropertyRelative("stopCode");
            var nameProp = el.FindPropertyRelative("stopName");
            var termProp = el.FindPropertyRelative("isTerminal");
            var layProp  = el.FindPropertyRelative("isLayover");

            if (!_stopFoldouts.ContainsKey(index)) _stopFoldouts[index] = false;

            float y = rect.y + 2f;
            float w = rect.width;
            float x = rect.x;

            if (!string.IsNullOrEmpty(codeProp.stringValue) && _duplicateStopCodes.Contains(codeProp.stringValue))
            {
                float warnH = EditorGUIUtility.singleLineHeight * 1.6f;
                EditorGUI.HelpBox(new Rect(x, y, w, warnH),
                    $"Duplicate stopCode '{codeProp.stringValue}' — another stop in this list already uses it.",
                    MessageType.Error);
                y += warnH + EditorGUIUtility.standardVerticalSpacing;
            }

            string tag = termProp.boolValue ? " [TERMINAL]" : (layProp.boolValue ? " [LAYOVER]" : "");
            string displayLabel = string.IsNullOrEmpty(nameProp.stringValue) ? "Unnamed Stop" : nameProp.stringValue;
            
            _stopFoldouts[index] = EditorGUI.Foldout(
                new Rect(x, y, w, EditorGUIUtility.singleLineHeight),
                _stopFoldouts[index],
                $"[{codeProp.stringValue}] {displayLabel}{tag}",
                true
            );

            y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            if (!_stopFoldouts[index]) return;

            x += 12f;
            w -= 12f;

            DrawField(x, ref y, w, codeProp, "Stop Code");
            DrawField(x, ref y, w, nameProp, "Stop Name");
            DrawField(x, ref y, w, el.FindPropertyRelative("parentRoadCode"), "Road Code");
            DrawField(x, ref y, w, el.FindPropertyRelative("tValue"), "T Value (0 – 1)");

            DrawField(x, ref y, w, el.FindPropertyRelative("hasShelter"), "Has Shelter");
            DrawField(x, ref y, w, el.FindPropertyRelative("isTerminal"), "Is Terminal");
            DrawField(x, ref y, w, el.FindPropertyRelative("isLayover"), "Is Layover");
            DrawField(x, ref y, w, el.FindPropertyRelative("isAccessible"), "Accessible (ADA)");
        };

        _stopList.onAddCallback = list =>
        {
            _stopDefsProp.arraySize++;
            var el = _stopDefsProp.GetArrayElementAtIndex(_stopDefsProp.arraySize - 1);
            // [FIX] Was `$"s{_stopDefsProp.arraySize:D4}"` — derived from the
            // array's CURRENT size at Add time, not from what codes actually
            // exist. Delete a stop (array shrinks) and add a new one and the
            // size-based code can land right back on a code already used by
            // an earlier stop — that's exactly how a duplicate like 244/245
            // showing up twice happens, silently, with nothing catching it.
            // Now scans every existing stopCode and picks the first one that
            // isn't taken.
            el.FindPropertyRelative("stopCode").stringValue       = GenerateUniqueStopCode();
            el.FindPropertyRelative("stopName").stringValue       = "New Stop";
            el.FindPropertyRelative("parentRoadCode").stringValue = "";
            el.FindPropertyRelative("tValue").floatValue          = 0.5f;
            el.FindPropertyRelative("isAccessible").boolValue     = true;
            serializedObject.ApplyModifiedProperties();
        };

        // [FIX] displayRemoveButton: true previously fell through to
        // ReorderableList's bare default remove — it deletes whatever index
        // is currently considered selected/focused with zero confirmation.
        // An accidental click on the wrong row (or a stray click anywhere in
        // the list) silently deleted a real stop. Now requires an explicit
        // confirm naming the exact stop about to go away.
        _stopList.onRemoveCallback = list =>
        {
            int index = list.index;
            if (index < 0 || index >= _stopDefsProp.arraySize) return;

            var el    = _stopDefsProp.GetArrayElementAtIndex(index);
            string code  = el.FindPropertyRelative("stopCode")?.stringValue;
            string name  = el.FindPropertyRelative("stopName")?.stringValue;
            string label = string.IsNullOrEmpty(code) ? $"stop #{index}" : $"'{code}' ({name})";

            bool confirmed = EditorUtility.DisplayDialog(
                "Delete Bus Stop?",
                $"Are you sure you want to delete {label}?\n\n" +
                "Make sure this is actually the stop you meant to select — a stray click " +
                "on the wrong row here silently deletes it with no undo prompt after this.",
                "Delete", "Cancel");

            if (confirmed)
            {
                ReorderableList.defaultBehaviours.DoRemoveButton(list);
                serializedObject.ApplyModifiedProperties();
            }
        };
    }

    // Scans every existing stopCode in the list and returns the first
    // "sNNNN"-style code not already taken, instead of trusting arraySize
    // (which drifts out of sync with real content the moment anything gets
    // deleted).
    private string GenerateUniqueStopCode()
    {
        var existing = new HashSet<string>();
        for (int i = 0; i < _stopDefsProp.arraySize; i++)
        {
            var c = _stopDefsProp.GetArrayElementAtIndex(i).FindPropertyRelative("stopCode")?.stringValue;
            if (!string.IsNullOrEmpty(c)) existing.Add(c);
        }

        int n = 1;
        string candidate;
        do { candidate = $"s{n:D4}"; n++; }
        while (existing.Contains(candidate));
        return candidate;
    }

    private int CountDuplicateStopCodes()
    {
        var codes = new List<string>();
        for (int i = 0; i < _stopDefsProp.arraySize; i++)
        {
            var c = _stopDefsProp.GetArrayElementAtIndex(i).FindPropertyRelative("stopCode")?.stringValue;
            if (!string.IsNullOrEmpty(c)) codes.Add(c);
        }
        return codes.Count - new HashSet<string>(codes).Count;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  MAIN GUI
    // ═════════════════════════════════════════════════════════════════════════
    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        RefreshDuplicateStopCodes();

        DrawSection("Player Bus & Detection", ref _showPlayerBus, () =>
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("busTransform"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("busController"));
            EditorGUILayout.Space(2f);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("stopPrefab"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("stopDetectRadius"));
        });

        // ── NEW SECTION FOR INTERSECTIONS & TRAFFIC LIGHT PROPERTIES ──────────
DrawSection("Manual Intersections & Traffic Lights",
    ref _showIntersections,
    () =>
{
    EditorGUILayout.PropertyField(
        serializedObject.FindProperty("spawnIntersections"));

    EditorGUILayout.PropertyField(
        serializedObject.FindProperty("includeTrafficLights"));

    EditorGUILayout.PropertyField(
        serializedObject.FindProperty("junctionPavementSize"));

    EditorGUILayout.Space();

    if (_intersectionList != null)
        _intersectionList.DoLayoutList();

    EditorGUILayout.Space();

    EditorGUILayout.LabelField(
        "Traffic Light Materials",
        EditorStyles.boldLabel);

    EditorGUILayout.PropertyField(
        serializedObject.FindProperty("lightMatRed"));

    EditorGUILayout.PropertyField(
        serializedObject.FindProperty("lightMatYellow"));

    EditorGUILayout.PropertyField(
        serializedObject.FindProperty("lightMatGreen"));

    EditorGUILayout.PropertyField(
        serializedObject.FindProperty("lightMatPavement"));

    EditorGUILayout.PropertyField(
        serializedObject.FindProperty("lightMatPole"));

    EditorGUILayout.Space(6f);
    EditorGUILayout.LabelField("Night Flashing (0-5 AM, via SimClock)", EditorStyles.boldLabel);
    EditorGUILayout.PropertyField(
        serializedObject.FindProperty("majorIntersectionRoadPairs"), true);
    EditorGUILayout.HelpBox(
        "Every intersection flashes 4-way overnight (yellow one axis, red the other) EXCEPT " +
        "ones marked MAJOR. Manual junctions: use the 'Is Major Intersection' checkbox on that " +
        "entry in the list above. Auto-detected junctions: add the two road codes here instead.",
        MessageType.Info);
});

        DrawSection("Road Center Line Markings", ref _showCenterLines, () =>
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("yellowLinePrefab"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("yellowLinePrefabLength"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxSegmentsPerRun"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("targetSegmentLength"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("lineChopFactor"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("dashedCenterLine"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("lineYOffset"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("intersectionLineClearance"));
            EditorGUILayout.HelpBox(
                "Prefab length + pivot are measured automatically from its mesh bounds (leave " +
                "Yellow Line Prefab Length at 0). Each clear stretch between intersections is " +
                "filled by at most Max Segments Per Run pieces, each stretched to exactly cover " +
                "its share. Use the per-road 'Hide Center Line' checkbox below to opt roads out. " +
                "Lines Chop Factor only applies in dashed mode.",
                MessageType.Info);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Intersection Cover", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("spawnCenterLineCoverDiamonds"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("centerLineCoverMaterial"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("centerLineCoverYOffset"));
            EditorGUILayout.HelpBox(
                "Spawns a plain pavement diamond at every auto-detected crossing/T-junction -- " +
                "same shape logic as the manual junction system, but no traffic lights, and " +
                "painted with the crossing roads' own material instead of pavement gray. Runs " +
                "regardless of Spawn Intersections / traffic light settings, since its only job " +
                "is guaranteeing no yellow line remnant ever shows through.",
                MessageType.Info);
        });

        DrawSection("Road Network", ref _showRoads, () =>
        {
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("paintAllRoadsMaterial"),
                new GUIContent("Paint All Roads Material"));

            if (GUILayout.Button("Paint All Roads"))
            {
                var cm = (CityManager)target;
                cm.PaintAllRoads();

                // [FIX] PaintAllRoads mutates roadDefinitions directly on
                // the target object, bypassing SerializedProperty entirely.
                // The Inspector's SerializedObject keeps its own cached
                // snapshot of the data -- without forcing a re-sync here,
                // the road list keeps showing each road's OLD color swatch
                // until some unrelated repaint happens to refresh it, even
                // though the real underlying data is already correct.
                serializedObject.Update();
                Repaint();
            }

            EditorGUILayout.Space(4f);

            _roadList.DoLayoutList();
        });

        DrawSection("Bus Stops", ref _showStops, () =>
        {
            int dupCount = CountDuplicateStopCodes();
            if (dupCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{dupCount} duplicate stopCode(s) found in this list. Fix these before building " +
                    "routes/schedules — a duplicate code means route/stop lookups can silently resolve " +
                    "to the wrong stop.",
                    MessageType.Warning);
            }
            _stopList.DoLayoutList();
        });

        DrawSection("Routes & Materials", ref _showRoutes, () =>
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("routes"), true);
            EditorGUILayout.Space(4f);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("regularStopMaterial"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("terminalStopMaterial"));
        });

        serializedObject.ApplyModifiedProperties();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═════════════════════════════════════════════════════════════════════════

    private static void DrawField(float x, ref float y, float w, SerializedProperty prop, string label)
    {
        float h = EditorGUI.GetPropertyHeight(prop, true);
        EditorGUI.PropertyField(new Rect(x, y, w, h), prop, new GUIContent(label), true);
        y += h + EditorGUIUtility.standardVerticalSpacing;
    }

    private static void DrawSection(string title, ref bool foldout, System.Action content)
    {
        EditorGUILayout.Space(4f);
        foldout = EditorGUILayout.Foldout(foldout, title, true, EditorStyles.foldoutHeader);
        if (foldout)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Space(2f);
            content();
            EditorGUILayout.Space(4f);
            EditorGUI.indentLevel--;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  FALLBACK PROPERTY DRAWER
// ═══════════════════════════════════════════════════════════════════════════════
[CustomPropertyDrawer(typeof(RoadSegmentDefinition))]
public class RoadSegmentDefinitionDrawer : PropertyDrawer
{
    public override void OnGUI(Rect pos, SerializedProperty prop, GUIContent label)
    {
        var nameProp = prop.FindPropertyRelative("roadName");
        string labelText = nameProp != null && !string.IsNullOrEmpty(nameProp.stringValue) 
            ? nameProp.stringValue 
            : label.text;

        EditorGUI.PropertyField(pos, prop, new GUIContent(labelText), true);
    }

    public override float GetPropertyHeight(SerializedProperty prop, GUIContent label)
        => EditorGUI.GetPropertyHeight(prop, true);
}
#endif