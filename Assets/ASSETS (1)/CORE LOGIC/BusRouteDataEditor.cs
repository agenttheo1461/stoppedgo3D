#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS ROUTE DATA — CUSTOM INSPECTOR DRAWERS
//
//  Problem this fixes: RouteVariantData (which also covers short turns) carries a full
//  set of path overrides, stop overrides, schedule windows, and vehicle
//  restriction fields — Unity's default Inspector renders ALL of that,
//  ALWAYS expanded, for every variant in the list, with no way to
//  just get to the schedule windows without scrolling past dozens of
//  RouteNode/RouteStopBinding entries first. That's exactly what made
//  authoring Route 34/87's Variant A painful enough that "just make a
//  separate route" looked easier than actually using the variant system.
//
//  Fix: each variant now collapses to a single summary line by
//  default (variant letter + whether it overrides the schedule + how many
//  windows it has), matching Unity's own foldout-persistence behavior
//  (property.isExpanded), so you only pay the scroll cost for the ONE
//  variant you're actually editing right now, not all of them at once.
//
//  Separately, ScheduleWindow itself (used by the mainline, every variant,
//  AND every variant, short turns included) gets a compact 3-line layout instead of Unity's
//  default 7-full-height-rows-per-window rendering, with a live clock-time
//  readout next to the raw minute fields so you're not doing (240 = 4:00?)
//  math in your head every time — this is "the separate window scroll
//  thing" specifically.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>Compact 3-row layout for a single ScheduleWindow entry: label on
/// its own row, start/end (with a live HH:MM readout) on the second row,
/// headway/trip-percent/departure-offset packed into the third. Used
/// automatically for every ScheduleWindow list Unity draws -- the mainline's
/// own scheduleWindows, every RouteVariantData's scheduleWindows (short turns included), since they're all this same type.</summary>
[CustomPropertyDrawer(typeof(ScheduleWindow))]
public class ScheduleWindowDrawer : PropertyDrawer
{
    const float Line = 18f;
    const float Pad = 2f;

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
        Line * 3f + Pad * 2f + 4f; // label row + start/end row + hw/pct/offset row, plus a little breathing room

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);

        var labelProp = property.FindPropertyRelative("label");
        var startProp = property.FindPropertyRelative("windowStartMinutes");
        var endProp   = property.FindPropertyRelative("windowEndMinutes");
        var hwAProp   = property.FindPropertyRelative("headwayFromAMinutes");
        var hwZProp   = property.FindPropertyRelative("headwayFromZMinutes");
        var pctProp   = property.FindPropertyRelative("tripTimeMultiplierPercent");
        var offProp   = property.FindPropertyRelative("departureOffsetMinutes");

        float x = position.x, w = position.width, y = position.y + 2f;

        if (labelProp != null)
            EditorGUI.PropertyField(new Rect(x, y, w, Line), labelProp, new GUIContent("Window"));
        y += Line + Pad;

        float halfW = (w - 6f) / 2f;
        if (startProp != null) DrawMinuteField(new Rect(x, y, halfW, Line), startProp, "Start");
        if (endProp != null)   DrawMinuteField(new Rect(x + halfW + 6f, y, halfW, Line), endProp, "End");
        y += Line + Pad;

        float quarterW = (w - 18f) / 4f;
        float qx = x;
        if (hwAProp != null) { DrawLabeledFloat(new Rect(qx, y, quarterW, Line), hwAProp, "hwA"); qx += quarterW + 6f; }
        if (hwZProp != null) { DrawLabeledFloat(new Rect(qx, y, quarterW, Line), hwZProp, "hwZ"); qx += quarterW + 6f; }
        if (pctProp != null) { DrawLabeledFloat(new Rect(qx, y, quarterW, Line), pctProp, "Trip%"); qx += quarterW + 6f; }
        if (offProp != null) { DrawLabeledFloat(new Rect(qx, y, quarterW, Line), offProp, "Offset"); }

        EditorGUI.EndProperty();
    }

    static void DrawMinuteField(Rect r, SerializedProperty prop, string label)
    {
        EditorGUI.BeginChangeCheck();
        float newVal = EditorGUI.FloatField(r, $"{label} ({MinutesToClock(prop.floatValue)})", prop.floatValue);
        if (EditorGUI.EndChangeCheck()) prop.floatValue = newVal;
    }

    static void DrawLabeledFloat(Rect r, SerializedProperty prop, string label)
    {
        const float labelW = 42f;
        EditorGUI.LabelField(new Rect(r.x, r.y, labelW, r.height), label);
        EditorGUI.PropertyField(new Rect(r.x + labelW, r.y, r.width - labelW, r.height), prop, GUIContent.none);
    }

    static string MinutesToClock(float minutes)
    {
        int m = ((int)minutes) % 1440;
        if (m < 0) m += 1440;
        return $"{m / 60:00}:{m % 60:00}";
    }
}

/// <summary>Shared collapse-to-one-line-by-default behavior for
/// RouteVariantData. Draws children one at a time via
/// SerializedProperty.NextVisible instead of calling EditorGUI.PropertyField
/// on the whole property again -- doing THAT from inside a PropertyDrawer
/// for the same type would just re-invoke this same drawer and recurse.</summary>
internal static class CollapsibleDrawerHelper
{
    const float HeaderHeight = 18f;
    const float RowSpacing = 2f;

    public static float GetHeight(SerializedProperty property, string headerText)
    {
        if (!property.isExpanded) return HeaderHeight;

        float total = HeaderHeight + 4f;
        foreach (var child in VisibleChildren(property))
            total += EditorGUI.GetPropertyHeight(child, true) + RowSpacing;
        return total;
    }

    public static void Draw(Rect position, SerializedProperty property, string headerText)
    {
        var headerRect = new Rect(position.x, position.y, position.width, HeaderHeight);
        property.isExpanded = EditorGUI.Foldout(headerRect, property.isExpanded, headerText, true, EditorStyles.foldoutHeader);

        if (!property.isExpanded) return;

        float y = position.y + HeaderHeight + 4f;
        EditorGUI.indentLevel++;
        foreach (var child in VisibleChildren(property))
        {
            float h = EditorGUI.GetPropertyHeight(child, true);
            EditorGUI.PropertyField(new Rect(position.x, y, position.width, h), child, true);
            y += h + RowSpacing;
        }
        EditorGUI.indentLevel--;
    }

    /// <summary>Walks this property's direct children (not grandchildren --
    /// each child's own PropertyField call handles drawing ITS children,
    /// including using ScheduleWindowDrawer for nested scheduleWindows lists
    /// automatically).</summary>
    static System.Collections.Generic.IEnumerable<SerializedProperty> VisibleChildren(SerializedProperty property)
    {
        var iterator = property.Copy();
        var end = iterator.GetEndProperty();
        bool enterChildren = true;
        while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
        {
            yield return iterator.Copy();
            enterChildren = false; // only descend into the FIRST level -- don't also expand each child's own children here
        }
    }
}

/// <summary>Collapses each RouteVariantData list entry to "Variant A (custom
/// schedule, 2 window(s))" / "Variant A (inherits mainline schedule)" by
/// default -- expand only the one you're actually working on instead of
/// scrolling past every variant's full path/stop/schedule/vehicle-override
/// field set at once.</summary>
[CustomPropertyDrawer(typeof(RouteVariantData))]
public class RouteVariantDataDrawer : PropertyDrawer
{
    public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
        CollapsibleDrawerHelper.GetHeight(property, HeaderText(property));

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) =>
        CollapsibleDrawerHelper.Draw(position, property, HeaderText(property));

    static string HeaderText(SerializedProperty property)
    {
        var letterProp = property.FindPropertyRelative("variantLetter");
        var overrideProp = property.FindPropertyRelative("overrideSchedule");
        var windowsProp = property.FindPropertyRelative("scheduleWindows");

        string letter = letterProp != null ? letterProp.stringValue : "?";

        var shortProp = property.FindPropertyRelative("isShortTurn");
        if (shortProp != null && shortProp.boolValue)
        {
            var turnProp = property.FindPropertyRelative("turnbackStopCode");
            string turn = turnProp != null && !string.IsNullOrEmpty(turnProp.stringValue) ? turnProp.stringValue : "set Turnback Stop Code";
            return $"Short Turn  {BusRouteData.ShortTurnSymbol}  (turns back at {turn})";
        }

        if (overrideProp != null && overrideProp.boolValue)
        {
            int count = windowsProp != null ? windowsProp.arraySize : 0;
            return count > 0
                ? $"Variant {letter}  (custom schedule, {count} window{(count == 1 ? "" : "s")})"
                : $"Variant {letter}  (custom schedule, flat headway)";
        }
        return $"Variant {letter}  (inherits mainline schedule)";
    }
}

#endif
