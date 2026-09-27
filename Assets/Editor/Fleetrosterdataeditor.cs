#if UNITY_EDITOR
// ═══════════════════════════════════════════════════════════════════════════════
//  FLEET ROSTER DATA — CUSTOM EDITOR
//  [REDO] Previous version grouped series into per-PowertrainCategory foldout
//  sections with full-row color banding. That's gone: this is one flat table,
//  every series on its own row, sorted by startFleetNumber. Columns are fixed
//  (Series / Range / Count / Bus Type / Engine / Transmission / Artic), so
//  everything lines up and you can scan the whole roster at once instead of
//  hunting through five collapsible groups.
//
//  Color is now used ONLY as a correctness signal, not decoration:
//    - a small category dot (still using PowertrainCategoryColors, so it
//      still matches BusSelectMenu's chip accents) just tells you at a
//      glance what drivetrain family a row is in
//    - the Transmission cell turns red and shows a ⚠ ONLY when the saved
//      value isn't legal for that row's engine -- same
//      LegalTransmissionsFor(engineType) restriction as before, so illegal
//      combos still can't be picked from the dropdown; the color only
//      flags a value that got illegal some other way (engine changed
//      after tx was set, imported data, etc).
//  No other cell is ever colored. That was the "weird things happen"
//  problem: color used for grouping made it look like color meant
//  validity, when most of it didn't.
//
//  Row expands (▶ button) to show every remaining field via normal
//  PropertyField, plus the same read-only resolved-state strip as before
//  (GetTxFor / ResolveRatedPowerTier / ResolveCenterAxleType).
//
//  NOTE: must live under an "Editor" folder so Unity excludes it from
//  player builds. #if UNITY_EDITOR is a second layer of safety.
// ═══════════════════════════════════════════════════════════════════════════════
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(FleetRosterData))]
public class FleetRosterDataEditor : Editor
{
    private SerializedProperty _seriesProp;
    private readonly Dictionary<int, bool> _expanded = new();

    private static readonly Color kIllegalBg = new Color(0.55f, 0.15f, 0.15f, 0.35f);
    private static readonly Color kHeaderBg  = new Color(0f, 0f, 0f, 0.15f);
    private static readonly Color kRowAltBg  = new Color(1f, 1f, 1f, 0.03f);

    // Column widths -- fixed so rows actually line up like a table instead
    // of each summary string wrapping/eliding differently.
    private const float wDot    = 14f;
    private const float wExpand = 18f;
    private const float wName   = 150f;
    private const float wRange  = 90f;
    private const float wCount  = 42f;
    private const float wType   = 90f;
    private const float wEngine = 110f;
    private const float wTx     = 140f;
    private const float wArtic  = 40f;
    private const float wMove   = 22f;

    private void OnEnable()
    {
        _seriesProp = serializedObject.FindProperty("series");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        var data = (FleetRosterData)target;

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Fleet Roster", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"{_seriesProp.arraySize} series total", EditorStyles.miniLabel);
        EditorGUILayout.Space(6);

        // Sort indices by fleet number only. No grouping, no bucketing --
        // one straight list. Underlying serialized array order is untouched
        // unless the ▲▼ buttons are used.
        var order = Enumerable.Range(0, _seriesProp.arraySize)
            .Where(i => data.series[i] != null)
            .OrderBy(i => data.series[i].startFleetNumber)
            .ToList();

        DrawTableHeader();

        for (int row = 0; row < order.Count; row++)
        {
            int i = order[row];
            if (row % 2 == 1)
            {
                Rect stripe = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true));
                // reserved for alt-row shading, applied per-row below instead
                // (GetRect(0,0) here just keeps layout stable pre-draw)
            }
            DrawSeriesRow(i, row % 2 == 1);
        }

        EditorGUILayout.Space(8);
        if (GUILayout.Button("+ Add Series", GUILayout.Height(26)))
        {
            _seriesProp.InsertArrayElementAtIndex(_seriesProp.arraySize);
        }

        serializedObject.ApplyModifiedProperties();
    }

    private void DrawTableHeader()
    {
        Rect r = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(r, kHeaderBg);
        float x = r.x;

        x += wDot + wExpand;
        Label(ref x, r.y, r.height, wName, "Series");
        Label(ref x, r.y, r.height, wRange, "Range");
        Label(ref x, r.y, r.height, wCount, "Qty");
        Label(ref x, r.y, r.height, wType, "Bus Type");
        Label(ref x, r.y, r.height, wEngine, "Engine");
        Label(ref x, r.y, r.height, wTx, "Transmission");
        Label(ref x, r.y, r.height, wArtic, "Artic");
    }

    private void Label(ref float x, float y, float h, float w, string text)
    {
        GUI.Label(new Rect(x, y, w, h), text, EditorStyles.miniBoldLabel);
        x += w;
    }

    private void DrawSeriesRow(int index, bool altShade)
    {
        var elem = _seriesProp.GetArrayElementAtIndex(index);
        var data = (FleetRosterData)target;
        var def = data.series[index];
        if (def == null) return;

        bool expanded = _expanded.TryGetValue(index, out var e) && e;

        string tx = SafeGetTx(def);
        var engineType = def.engineType;
        var legalTx = FleetSeriesDefinition.LegalTransmissionsFor((BusSimulationController.EngineType)engineType);
        bool txIllegal = !legalTx.Contains(def.transmission);

        int total = EstimateBusCount(def);
        int endNum = def.startFleetNumber + Mathf.Max(total, 1) - 1;
        string range = total <= 1 ? $"#{def.startFleetNumber}" : $"#{def.startFleetNumber}-{endNum}";

        var cat = PowertrainCategoryColors.GetCategory(def.engineType, tx);
        Color dotColor = PowertrainCategoryColors.CategoryColor.TryGetValue(cat, out var c) ? c : Color.gray;

        Rect r = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
        if (altShade) EditorGUI.DrawRect(r, kRowAltBg);
        if (txIllegal) EditorGUI.DrawRect(new Rect(r.x + wDot + wExpand + wName + wRange + wCount + wType + wEngine, r.y, wTx, r.height), kIllegalBg);

        float x = r.x;

        // Category dot -- ONLY signal in this cell, no other meaning.
        EditorGUI.DrawRect(new Rect(x + 3, r.y + r.height / 2f - 3, 6, 6), dotColor);
        x += wDot;

        bool newExpanded = GUI.Toggle(new Rect(x, r.y, wExpand, r.height), expanded, expanded ? "▼" : "▶", EditorStyles.miniButton);
        _expanded[index] = newExpanded;
        x += wExpand;

        GUI.Label(new Rect(x, r.y, wName, r.height), def.seriesName);
        x += wName;
        GUI.Label(new Rect(x, r.y, wRange, r.height), range);
        x += wRange;
        GUI.Label(new Rect(x, r.y, wCount, r.height), total.ToString());
        x += wCount;
        GUI.Label(new Rect(x, r.y, wType, r.height), def.busType.ToString());
        x += wType;
        GUI.Label(new Rect(x, r.y, wEngine, r.height), def.engineType.ToString());
        x += wEngine;

        string txLabel = txIllegal ? $"⚠ {def.transmission}" : def.transmission.ToString();
        GUI.Label(new Rect(x, r.y, wTx, r.height), txLabel);
        x += wTx;

        GUI.Label(new Rect(x, r.y, wArtic, r.height), def.isArticulated ? "Y" : "");
        x += wArtic;

        GUI.enabled = index > 0;
        if (GUI.Button(new Rect(x, r.y, wMove, r.height), "▲")) _seriesProp.MoveArrayElement(index, index - 1);
        x += wMove;
        GUI.enabled = index < _seriesProp.arraySize - 1;
        if (GUI.Button(new Rect(x, r.y, wMove, r.height), "▼")) _seriesProp.MoveArrayElement(index, index + 1);
        x += wMove;
        GUI.enabled = true;
        if (GUI.Button(new Rect(x, r.y, wMove, r.height), "✕"))
        {
            bool confirmed = EditorUtility.DisplayDialog(
                "Remove Series",
                $"Delete \"{def.seriesName}\" ({range}, {total} buses)? This can't be undone from this dialog.",
                "Delete",
                "Cancel");
            if (confirmed)
            {
                _seriesProp.DeleteArrayElementAtIndex(index);
                return;
            }
        }

        if (newExpanded)
        {
            EditorGUI.indentLevel++;
            DrawFilteredEngineTxFields(elem, def);
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }
    }

    // Same job as before: draws every field EXCEPT "transmission" via
    // PropertyField, then replaces the transmission popup with one
    // restricted to FleetSeriesDefinition.LegalTransmissionsFor(engineType)
    // -- this is what actually prevents illegal combos from being picked;
    // the red cell above only flags a value that's already illegal.
    private void DrawFilteredEngineTxFields(SerializedProperty elem, FleetSeriesDefinition def)
    {
        var engineProp = elem.FindPropertyRelative("engineType");
        var articProp  = elem.FindPropertyRelative("isArticulated");

        var iterator = elem.Copy();
        var end = iterator.GetEndProperty();
        int depth = iterator.depth;
        bool enterChildren = true;
        while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
        {
            enterChildren = false;
            if (iterator.depth != depth + 1) continue;
            if (iterator.name == "transmission")
            {
                DrawFilteredTransmissionPopup(iterator, (BusSimulationController.EngineType)engineProp.enumValueIndex);
                continue;
            }
            EditorGUILayout.PropertyField(iterator, true);
        }

        EditorGUILayout.Space(2);
        var engineType = (BusSimulationController.EngineType)engineProp.enumValueIndex;
        bool isArtic   = articProp.boolValue;
        string resolvedTx = SafeGetTx(def);
        var resolvedTier  = FleetSeriesDefinition.ResolveRatedPowerTier(engineType, isArtic);
        var resolvedAxle  = FleetSeriesDefinition.ResolveCenterAxleType(engineType, resolvedTier);

        EditorGUILayout.HelpBox(
            $"Resolved TX string: \"{resolvedTx}\"\n" +
            $"Rated tier: {(int)resolvedTier} hp{(isArtic ? "" : "  (locked -- not articulated)")}\n" +
            $"Center axle: {resolvedAxle}",
            MessageType.None);
    }

    private void DrawFilteredTransmissionPopup(SerializedProperty txProp, BusSimulationController.EngineType engineType)
    {
        var legal = FleetSeriesDefinition.LegalTransmissionsFor(engineType);
        var names = new string[legal.Length];
        int currentIndex = 0;
        var currentValue = (TransmissionType)txProp.enumValueIndex;
        for (int i = 0; i < legal.Length; i++)
        {
            names[i] = legal[i].ToString();
            if (legal[i] == currentValue) currentIndex = i;
        }

        bool currentIsIllegal = !legal.Contains(currentValue);
        string label = currentIsIllegal
            ? $"Transmission  ⚠ \"{currentValue}\" not valid for {engineType}"
            : "Transmission";

        int newIndex = EditorGUILayout.Popup(label, currentIsIllegal ? 0 : currentIndex, names);
        if (!currentIsIllegal || newIndex != 0)
            txProp.enumValueIndex = (int)legal[newIndex];
    }

    private static string SafeGetTx(FleetSeriesDefinition def)
    {
        try { return def.GetTxFor(0); }
        catch { return "?"; }
    }

    private static int EstimateBusCount(FleetSeriesDefinition def)
    {
        if (def.depotAllocations != null && def.depotAllocations.Count > 0)
        {
            int sum = 0;
            foreach (var a in def.depotAllocations)
                if (a != null && a.busCount > 0) sum += a.busCount;
            return sum;
        }
        return def.busCount;
    }
}
#endif