#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Interline Editor. Pick a route, see the live A / Z terminal of it
/// and of every other managed route, and tick checkboxes for which terminal
/// pairs may be interlined. Writes BusScheduler.interlineTerminalRules
/// (explicit, two-way rules; undoable). In Play Mode it also shows the next
/// unassigned departure from each terminal.
/// </summary>
public class InterlineEditorWindow : EditorWindow
{
    private int _fromIdx;
    private Vector2 _scroll;
    private string _filter = "";

    [MenuItem("Tools/Interline Editor")]
    private static void Open() => GetWindow<InterlineEditorWindow>("Interline Editor");

    private static string TerminalLabel(BusRouteData r, bool aEnd)
    {
        var stops = r.GetStops(true);
        if (stops == null || stops.Count == 0) return "(no stops)";
        var st = aEnd ? stops[0] : stops[stops.Count - 1];
        return $"{st.stopName}  [{st.stopCode}]  {st.parentRoadCode}";
    }

    private static string NextDeparture(BusScheduler sched, BusRouteData r, bool aEnd)
    {
        if (!Application.isPlaying || SimClock.Instance == null) return "";
        var slot = sched.GetNextUnassignedSlot(r.routeNumber, "", -1f, aEnd);
        if (slot == null || slot.isOutbound != aEnd) return "  · next dep —";
        float m = ((slot.scheduledDeparture % 1440f) + 1440f) % 1440f;
        return $"  · next dep {Mathf.FloorToInt(m / 60f):D2}:{Mathf.FloorToInt(m % 60f):D2}";
    }

    private void OnGUI()
    {
        var sched = Object.FindObjectOfType<BusScheduler>();
        if (sched == null || sched.managedRoutes == null || sched.managedRoutes.Length == 0)
        {
            EditorGUILayout.HelpBox("No BusScheduler with managedRoutes found in the open scene.", MessageType.Info);
            return;
        }

        var routes = sched.managedRoutes.Where(r => r != null).ToArray();
        _fromIdx = Mathf.Clamp(_fromIdx, 0, routes.Length - 1);
        _fromIdx = EditorGUILayout.Popup("From route", _fromIdx, routes.Select(r => r.routeNumber).ToArray());
        var from = routes[_fromIdx];

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("A terminal", TerminalLabel(from, true) + NextDeparture(sched, from, true));
        EditorGUILayout.LabelField("Z terminal", TerminalLabel(from, false) + NextDeparture(sched, from, false));
        _filter = EditorGUILayout.TextField("Filter routes", _filter);
        EditorGUILayout.HelpBox("Tick = a bus finishing at the FROM terminal may continue from the TO terminal (two-way). " +
                                "If a route pair has any tick, ONLY ticked pairs are allowed; no ticks = unrestricted.", MessageType.None);

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        foreach (var to in routes)
        {
            if (to == from) continue;
            if (!string.IsNullOrEmpty(_filter) && !to.routeNumber.Contains(_filter)) continue;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField($"Route {to.routeNumber}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("  A", TerminalLabel(to, true) + NextDeparture(sched, to, true), EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  Z", TerminalLabel(to, false) + NextDeparture(sched, to, false), EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            foreach (bool fe in new[] { true, false })
                foreach (bool te in new[] { true, false })
                    LinkToggle(sched, from.routeNumber, fe, to.routeNumber, te);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndScrollView();
    }

    private void LinkToggle(BusScheduler sched, string fromRoute, bool fe, string toRoute, bool te)
    {
        bool on = sched.HasExplicitInterlineLink(fromRoute, fe, toRoute, te);
        string label = $"{(fe ? "A" : "Z")} → {(te ? "A" : "Z")}";
        bool now = EditorGUILayout.ToggleLeft(label, on, GUILayout.Width(80));
        if (now == on) return;

        Undo.RecordObject(sched, "Edit interline rules");
        var rules = sched.interlineTerminalRules;
        if (now)
        {
            rules.Add(new BusScheduler.InterlineTerminalRule
            {
                fromRoute = fromRoute, toRoute = toRoute, bothWays = true,
                fromEnd = fe ? BusScheduler.TerminalEnd.A : BusScheduler.TerminalEnd.Z,
                toEnd   = te ? BusScheduler.TerminalEnd.A : BusScheduler.TerminalEnd.Z,
            });
        }
        else
        {
            // Remove only simple explicit rules (either orientation). Links that
            // come from a compound rule (Any / SameAsFrom) must be edited in the list.
            int removed = rules.RemoveAll(r =>
                r != null && r.bothWays
                && ((r.fromRoute == fromRoute && r.toRoute == toRoute && Is(r.fromEnd, fe) && Is(r.toEnd, te))
                 || (r.fromRoute == toRoute && r.toRoute == fromRoute && Is(r.fromEnd, te) && Is(r.toEnd, fe))));
            if (removed == 0)
                Debug.LogWarning($"[InterlineEditor] {fromRoute} {(fe ? "A" : "Z")}→{toRoute} {(te ? "A" : "Z")} comes from a compound rule (Any/SameAsFrom) or a one-way rule — edit BusScheduler.interlineTerminalRules directly.");
        }
        EditorUtility.SetDirty(sched);
    }

    private static bool Is(BusScheduler.TerminalEnd e, bool isA) =>
        e == (isA ? BusScheduler.TerminalEnd.A : BusScheduler.TerminalEnd.Z);
}
#endif
