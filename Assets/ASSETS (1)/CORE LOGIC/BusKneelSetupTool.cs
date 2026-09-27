#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Bus > Kneel Setup. Copies the kneel rig from a bus prefab that
/// already has it (e.g. 3.4.5xd40) onto one that doesn't (e.g. 3.4.5xn40).
/// BusKneelBody lowers a "body" group, so the target's flat mesh list has to be
/// regrouped the same way the source is (body / wheels). This tool does that
/// regrouping BY OBJECT NAME, adds BusKneelBody with the source's settings, and
/// points its bodyGroup at the new "body" object. A backup copy of the target
/// prefab is only made if the window's backup box is ticked.
///
/// It also copies BusExteriorLightController (headlights / brake lights /
/// signals / hazards) from the source: materials are reused as-is, and every
/// Renderer / Light reference is re-pointed at the target's object with the
/// SAME NAME. A hand-made duplicate in the source ("Base (1)", e.g. the tilted
/// left signal) is re-created by cloning the target's "Base" with the source's
/// local transform; light objects only the source has are copied over. Anything
/// still unmatched is logged.
/// </summary>
public class BusKneelSetupTool : EditorWindow
{
    private GameObject _source, _target;
    private bool _articulated;
    private bool _makeBackup;   // off by default: no backup copies are written

    [MenuItem("Tools/Bus/Kneel Setup (copy from prefab)")]
    private static void Open() => GetWindow<BusKneelSetupTool>("Kneel Setup");

    [MenuItem("Tools/Bus/Fix Empty Light Slots (xde60 1)")]
    private static void FixEmptyLightSlots()
    {
        const string tgtPath = Dir + "3.4.5.1xde60 1.prefab";
        var art = PrefabUtility.LoadPrefabContents(Dir + "3.4.5.1xhe60 3.prefab");
        var tgt = PrefabUtility.LoadPrefabContents(tgtPath);
        try
        {
            CopyExteriorLights(art, tgt);
            PrefabUtility.SaveAsPrefabAsset(tgt, tgtPath);
            Debug.Log("[KneelSetup] xde60 1: empty exterior light slots filled from xhe60 (existing entries kept).");
        }
        finally { PrefabUtility.UnloadPrefabContents(art); PrefabUtility.UnloadPrefabContents(tgt); }
    }

    // ── One-click: fill every known gap in the 3.4.5 family, NO backup copies ─────────────
    private const string Dir = "Assets/ASSETS (1)/BUS MODELS/";

    [MenuItem("Tools/Bus/Fill 3.4.5 Gaps (no backups)")]
    private static void FillGaps()
    {
        if (!EditorUtility.DisplayDialog("Fill 3.4.5 gaps", "Edits 8 bus prefabs IN PLACE and makes NO backup copies (use git/undo if needed). Continue?", "Do it", "Cancel")) return;

        var std = PrefabUtility.LoadPrefabContents(Dir + "3.4.5xd40.prefab");
        var art = PrefabUtility.LoadPrefabContents(Dir + "3.4.5.1xhe60 3.prefab");
        try
        {
            var stdKnee = std.GetComponent<BusKneelBody>();
            var artKnee = art.GetComponent<BusKneelBody>() ?? stdKnee;

            // name, articulated?, wants kneel, wants lights
            var jobs = new (string name, bool artic, bool kneel, bool lights)[]
            {
                ("3.4.5.1xd60 2", true,  true, true),
                ("3.4.5.1xde60",  true,  true, true),
                ("3.4.5.1xde60 1",true,  true, false),
                ("3.4.5.1xe60 2", true,  true, false),
                ("3.4.5.1xn60",   true,  true, true),
                ("3.4.5xe40",     false, false, true),
                ("3.4.5xd40 1",   false, true, true),
            };
            foreach (var j in jobs)
            {
                string path = Dir + j.name + ".prefab";
                var tgt = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    bool ok = true;
                    if (j.kneel)
                        ok = j.artic ? ApplyArticulated(tgt, artKnee, art) : ApplyStandardKneel(std, tgt, stdKnee);
                    if (ok && j.lights) CopyExteriorLights(j.artic ? art : std, tgt);
                    if (ok) { PrefabUtility.SaveAsPrefabAsset(tgt, path); Debug.Log($"[KneelSetup] Saved {j.name}."); }
                    else Debug.LogWarning($"[KneelSetup] {j.name}: skipped (see error above).");
                }
                finally { PrefabUtility.UnloadPrefabContents(tgt); }
            }
        }
        finally { PrefabUtility.UnloadPrefabContents(std); PrefabUtility.UnloadPrefabContents(art); }
    }

    // Standard (single-section) kneel: regroup the flat model into body/wheels by matching names from the source.
    private static bool ApplyStandardKneel(GameObject src, GameObject tgt, BusKneelBody srcKnee)
    {
        var srcModel = ModelNode(src.transform);
        var tgtModel = ModelNode(tgt.transform);
        if (srcModel == null || tgtModel == null) { Debug.LogError($"[KneelSetup] {tgt.name}: couldn't find model node."); return false; }
        Transform bodyT = tgtModel.Find("body");
        if (bodyT == null)
        {
            var groupOf = new Dictionary<string, string>();
            var groupNames = new List<string>();
            foreach (Transform g in srcModel)
            {
                if (g.childCount == 0) continue;
                groupNames.Add(g.name);
                foreach (var t in g.GetComponentsInChildren<Transform>(true))
                    if (t != g && !groupOf.ContainsKey(t.name)) groupOf[t.name] = g.name;
            }
            var groups = groupNames.ToDictionary(gn => gn, gn => { var go = new GameObject(gn); go.transform.SetParent(tgtModel, false); return go.transform; });
            int moved = 0, left = 0;
            foreach (var c in tgtModel.Cast<Transform>().ToList())
            {
                if (groups.ContainsValue(c)) continue;
                if (groupOf.TryGetValue(c.name, out var gn)) { c.SetParent(groups[gn], true); moved++; } else left++;
            }
            groups.TryGetValue("body", out bodyT);
            Debug.Log($"[KneelSetup] {tgt.name}: moved {moved} objects into {string.Join("/", groupNames)}, {left} left in place.");
        }
        var knee = tgt.GetComponent<BusKneelBody>();
        if (knee == null) { knee = tgt.AddComponent<BusKneelBody>(); EditorUtility.CopySerialized(srcKnee, knee); }
        knee.bodyGroup = bodyT;
        return true;
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox("Source = a prefab that already kneels (e.g. 3.4.5xd40). Target = the prefab to add kneel to (e.g. 3.4.5xn40 1). A backup of the target is saved next to it first.", MessageType.Info);
        _source = (GameObject)EditorGUILayout.ObjectField("Source (has kneel)", _source, typeof(GameObject), false);
        _target = (GameObject)EditorGUILayout.ObjectField("Target (add kneel)", _target, typeof(GameObject), false);
        _makeBackup = EditorGUILayout.ToggleLeft("Make a backup copy of the target first (off = no extra files)", _makeBackup);
        _articulated = EditorGUILayout.ToggleLeft("Articulated bus (front + rear sections; wheels are named 'wheels...')", _articulated);
        using (new EditorGUI.DisabledScope(_source == null || _target == null || _source == _target))
            if (GUILayout.Button("Apply")) Apply();
    }

    // The model node = the root child that holds the mesh groups (most descendants).
    private static Transform ModelNode(Transform root) =>
        root.Cast<Transform>().OrderByDescending(t => t.GetComponentsInChildren<Transform>(true).Length).FirstOrDefault();

    private static void CopyExteriorLights(GameObject src, GameObject tgt)
    {
        var sl = src.GetComponentInChildren<BusExteriorLightController>(true);
        if (sl == null) { Debug.Log("[KneelSetup] Source has no BusExteriorLightController -- skipped lights."); return; }
        var existing = tgt.GetComponentInChildren<BusExteriorLightController>(true);

        // name -> object in the target hierarchy
        var byName = new Dictionary<string, Transform>();
        foreach (var t in tgt.GetComponentsInChildren<Transform>(true))
            if (!byName.ContainsKey(t.name)) byName[t.name] = t;
        var fallbackParent = ModelNode(tgt.transform) ?? tgt.transform;

        // Existing controller: keep it and only FILL the slots that are empty (arrays with no entries, materials that are null).
        var tl = existing ?? tgt.AddComponent<BusExteriorLightController>();
        if (existing == null) EditorUtility.CopySerialized(sl, tl);

        int matched = 0, cloned = 0, missing = 0;
        var notes = new List<string>();

        T Resolve<T>(T c) where T : Component
        {
            if (c == null) return null;
            if (byName.TryGetValue(c.name, out var hit) && hit.GetComponent<T>() is T found)
            {
                matched++;
                // The source may have MOVED/TILTED this object (e.g. the signal lenses were nudged
                // apart and angled in opposite directions), so carry its local transform over too.
                // For objects that weren't touched the values are identical, so this is a no-op.
                if (typeof(T) == typeof(Renderer))
                {
                    hit.localPosition = c.transform.localPosition;
                    hit.localRotation = c.transform.localRotation;
                    hit.localScale    = c.transform.localScale;
                }
                return found;
            }

            // Hand-made duplicate in the source (e.g. the left signal = a tilted copy of the right one,
            // named "Base (1)"): clone the target's own "Base" and give the clone the SOURCE's local
            // position / rotation / scale, so the tilt carries over.
            var m = System.Text.RegularExpressions.Regex.Match(c.name, @"^(.*) \(\d+\)$");
            if (typeof(T) == typeof(Renderer) && m.Success && byName.TryGetValue(m.Groups[1].Value, out var baseT))
            {
                var copy = Object.Instantiate(baseT.gameObject, baseT.parent);
                copy.name = c.name;
                copy.transform.localPosition = c.transform.localPosition;
                copy.transform.localRotation = c.transform.localRotation;
                copy.transform.localScale    = c.transform.localScale;
                byName[copy.name] = copy.transform;
                cloned++; notes.Add($"cloned '{baseT.name}' -> '{c.name}'");
                return copy.GetComponent<T>();
            }

            // Light objects the source had added by hand (no counterpart in the target): copy them over.
            if (typeof(T) == typeof(Light))
            {
                var parent = c.transform.parent != null && byName.TryGetValue(c.transform.parent.name, out var pt) ? pt : fallbackParent;
                var copy = Object.Instantiate(c.gameObject, parent);
                copy.name = c.name;
                copy.transform.localPosition = c.transform.localPosition;
                copy.transform.localRotation = c.transform.localRotation;
                copy.transform.localScale    = c.transform.localScale;
                byName[copy.name] = copy.transform;
                cloned++; notes.Add($"copied light '{c.name}' under '{parent.name}'");
                return copy.GetComponent<T>();
            }

            missing++; notes.Add($"UNMATCHED '{c.name}'");
            return null;
        }

        T[] Remap<T>(T[] arr) where T : Component
        {
            var list = new List<T>();
            if (arr != null) foreach (var c in arr) { var r = Resolve(c); if (r != null) list.Add(r); }
            return list.ToArray();
        }

        foreach (var f in typeof(BusExteriorLightController).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (existing != null)
            {
                // fill-empty mode
                if (f.FieldType == typeof(Renderer[]) && ((Renderer[])f.GetValue(tl) ?? new Renderer[0]).Length == 0)
                    f.SetValue(tl, Remap((Renderer[])f.GetValue(sl)));
                else if (f.FieldType == typeof(Light[]) && ((Light[])f.GetValue(tl) ?? new Light[0]).Length == 0)
                    f.SetValue(tl, Remap((Light[])f.GetValue(sl)));
                else if (f.FieldType == typeof(Material) && f.GetValue(tl) == null)
                    f.SetValue(tl, f.GetValue(sl));
                continue;
            }
            if (f.FieldType == typeof(Renderer[])) f.SetValue(tl, Remap((Renderer[])f.GetValue(sl)));
            else if (f.FieldType == typeof(Light[])) f.SetValue(tl, Remap((Light[])f.GetValue(sl)));
        }
        Debug.Log($"[KneelSetup] Exterior lights: {matched} matched by name, {cloned} cloned/copied, {missing} unmatched. " + string.Join("; ", notes));
    }

    // Articulated: each section (front / rear) gets its own "body" group holding everything except the
    // wheels (objects whose name starts with "wheels") and the articulation "pivot". BusKneelBody goes on the
    // section that has the driving components (NPCBusController); the other sections' bodies go in extraBodyGroups.
    // Wheel detection for prefabs whose wheels were NOT renamed: a Cylinder renderer that is round in the
    // Y/Z plane, thin along the axle (X), wheel-sized, and centred at axle height above the lowest point of the bus.
    private static bool LooksLikeWheel(Transform t, float busMinY)
    {
        if (!t.name.StartsWith("Cylinder")) return false;
        var r = t.GetComponent<Renderer>();
        if (r == null) return false;
        var b = r.bounds;
        float d = Mathf.Max(b.size.y, b.size.z);
        if (d < 0.4f || d > 1.5f) return false;
        if (Mathf.Abs(b.size.y - b.size.z) > 0.15f * d) return false;
        if (b.size.x > d) return false;
        float above = b.center.y - busMinY;
        return above > 0.3f && above < 0.8f;
    }

    private static bool ApplyArticulated(GameObject tgt, BusKneelBody srcKnee, GameObject reference = null)
    {
        // Decide which objects are wheels: by name ("wheels...") if the prefab was hand-marked, else by geometry.
        bool named = tgt.GetComponentsInChildren<Transform>(true).Any(t => t.name.ToLowerInvariant().StartsWith("wheel"));
        float busMinY = float.MaxValue;
        foreach (var r in tgt.GetComponentsInChildren<Renderer>(true)) busMinY = Mathf.Min(busMinY, r.bounds.min.y);
        var detected = new Dictionary<Transform, List<Transform>>();
        if (!named)
        {
            if (reference != null)
            {
                // Wheel identification by NAME, using the hand-marked reference bus (xhe60): its wheels were renamed,
                // so the "Cylinder.N" names that exist in this prefab but NOT in the reference are the wheels' original
                // names -- except cylinders that show up in BOTH sections, which are the non-wheel ones ("not wheel").
                var refNames = new HashSet<string>(reference.GetComponentsInChildren<Transform>(true).Select(t => t.name));
                var cand = new Dictionary<Transform, List<Transform>>();
                foreach (Transform sec in tgt.transform)
                {
                    if (sec.childCount < 10) continue;
                    cand[sec] = sec.GetComponentsInChildren<Transform>(true)
                        .Where(t => t != sec && t.parent == sec && t.name.StartsWith("Cylinder") && !refNames.Contains(t.name)).ToList();
                }
                // "Cylinder.291 (1)" counts as "Cylinder.291" (a duplicate made by hand).
                string Base(string nm) => System.Text.RegularExpressions.Regex.Replace(nm, @" \(\d+\)$", "");
                int Num(string nm) { var m = System.Text.RegularExpressions.Regex.Match(nm, @"\.(\d+)"); return m.Success ? int.Parse(m.Groups[1].Value) : 0; }
                HashSet<string> inAll = null;
                foreach (var kv in cand)
                {
                    var names = new HashSet<string>(kv.Value.Select(t => Base(t.name)));
                    if (inAll == null) inAll = names; else inAll.IntersectWith(names);
                }
                inAll = inAll ?? new HashSet<string>();
                foreach (var kv in cand) detected[kv.Key] = kv.Value.Where(t => !inAll.Contains(Base(t.name))).ToList();
                // Front section with 11 candidates = 8 wheels + the 3 lowest-numbered non-wheels (e.g. 291-293).
                foreach (var key in detected.Keys.ToList())
                    if (key.name.ToLowerInvariant() == "front" && detected[key].Count == 11)
                        detected[key] = detected[key].OrderBy(t => Num(t.name)).Skip(3).ToList();
            }
            else
            foreach (Transform sec in tgt.transform)
            {
                if (sec.childCount < 10) continue;
                detected[sec] = sec.Cast<Transform>().Where(c => LooksLikeWheel(c, busMinY)).ToList();
            }
            // Guard: a 60 ft artic has 8 wheel objects on the front section and 4 on the rear. If detection
            // doesn't find exactly that, do NOT touch the prefab -- wheels would drop with the body.
            var counts = detected.ToDictionary(kv => kv.Key.name.ToLowerInvariant(), kv => kv.Value.Count);
            counts.TryGetValue("front", out int fc); counts.TryGetValue("rear", out int rc);
            if (fc != 8 || rc != 4)
            {
                Debug.LogError($"[KneelSetup] {tgt.name}: wheel detection found front={fc}, rear={rc} (expected 8 / 4) -- NOT modified. Detected: " +
                               string.Join("; ", detected.Select(kv => kv.Key.name + ": " + string.Join(",", kv.Value.Select(w => w.name)))) +
                               ". Rename the wheels to start with 'wheels' and run again.");
                return false;
            }
            Debug.Log($"[KneelSetup] {tgt.name}: wheels detected by geometry (front 8, rear 4): " +
                      string.Join("; ", detected.Select(kv => kv.Key.name + ": " + string.Join(",", kv.Value.Select(w => w.name)))));
        }
        if (named)
        {
            // Hand-marked wheels: sanity-check the counts per section (8 front / 4 rear on a 60 ft artic).
            foreach (Transform sec in tgt.transform)
            {
                if (sec.childCount < 10) continue;
                int wc = sec.Cast<Transform>().Count(c => c.name.ToLowerInvariant().StartsWith("wheel"));
                Debug.Log($"[KneelSetup] {tgt.name}: '{sec.name}' has {wc} marked wheel object(s).");
            }
        }
        Transform driver = null;
        var bodies = new List<Transform>();
        foreach (Transform sec in tgt.transform)
        {
            if (sec.childCount < 10) continue; // not a mesh section
            if (sec.GetComponent<NPCBusController>() != null) driver = sec;
            var body = sec.Find("body");
            if (body == null)
            {
                body = new GameObject("body").transform;
                body.SetParent(sec, false);
                int moved = 0;
                foreach (var c in sec.Cast<Transform>().ToList())
                {
                    if (c == body) continue;
                    string n = c.name.ToLowerInvariant();
                    if (n.StartsWith("wheel") || n == "pivot") continue;
                    if (!named && detected.TryGetValue(sec, out var wl) && wl.Contains(c)) continue;
                    c.SetParent(body, true); moved++;
                }
                Debug.Log($"[KneelSetup] {sec.name}: moved {moved} objects into 'body' (wheels/pivot left out).");
            }
            bodies.Add(body);
        }
        if (driver == null) { Debug.LogError("[KneelSetup] No section with NPCBusController found -- kneel not added."); return false; }

        var driverBody = driver.Find("body");
        var knee = driver.GetComponent<BusKneelBody>();
        if (knee == null) { knee = driver.gameObject.AddComponent<BusKneelBody>(); EditorUtility.CopySerialized(srcKnee, knee); }
        knee.bodyGroup = driverBody;
        knee.extraBodyGroups = bodies.Where(b => b != driverBody).ToArray();
        Debug.Log($"[KneelSetup] BusKneelBody on '{driver.name}': bodyGroup = its body, extraBodyGroups = {knee.extraBodyGroups.Length}.");
        return true;
    }

    private void Apply()
    {
        string sp = AssetDatabase.GetAssetPath(_source), tp = AssetDatabase.GetAssetPath(_target);
        string backup = tp.Replace(".prefab", " (backup pre-kneel).prefab");
        if (_makeBackup && !AssetDatabase.CopyAsset(tp, AssetDatabase.GenerateUniqueAssetPath(backup)))
        {
            Debug.LogError("[KneelSetup] Couldn't make a backup of the target -- aborting.");
            return;
        }

        var src = PrefabUtility.LoadPrefabContents(sp);
        var tgt = PrefabUtility.LoadPrefabContents(tp);
        try
        {
            var srcKnee = src.GetComponent<BusKneelBody>();
            if (srcKnee == null) { Debug.LogError("[KneelSetup] Source has no BusKneelBody on its root."); return; }

            if (_articulated) { if (ApplyArticulated(tgt, srcKnee, null)) PrefabUtility.SaveAsPrefabAsset(tgt, tp); return; }

            var srcModel = ModelNode(src.transform);
            var tgtModel = ModelNode(tgt.transform);
            if (srcModel == null || tgtModel == null) { Debug.LogError("[KneelSetup] Couldn't find the model node."); return; }
            // If the target was already regrouped by hand (has a "body" group) and/or already has
            // BusKneelBody, keep that work and only add what's missing (e.g. the exterior lights).
            bool alreadyGrouped = tgtModel.Find("body") != null;
            int moved = 0, left = 0;
            var groupNames = new List<string>();
            Transform bodyT = tgtModel.Find("body");

            if (!alreadyGrouped)
            {
                // name -> group (only source children that actually GROUP things, i.e. have children)
                var groupOf = new Dictionary<string, string>();
                foreach (Transform g in srcModel)
                {
                    if (g.childCount == 0) continue;
                    groupNames.Add(g.name);
                    foreach (var t in g.GetComponentsInChildren<Transform>(true))
                        if (t != g && !groupOf.ContainsKey(t.name)) groupOf[t.name] = g.name;
                }

                var groups = new Dictionary<string, Transform>();
                foreach (var gn in groupNames)
                {
                    var go = new GameObject(gn);
                    go.transform.SetParent(tgtModel, false);
                    groups[gn] = go.transform;
                }

                foreach (var c in tgtModel.Cast<Transform>().ToList())
                {
                    if (groups.ContainsValue(c)) continue;
                    if (groupOf.TryGetValue(c.name, out var gn)) { c.SetParent(groups[gn], true); moved++; }
                    else left++;
                }
                groups.TryGetValue("body", out bodyT);
            }

            var knee = tgt.GetComponent<BusKneelBody>();
            if (knee == null)
            {
                knee = tgt.AddComponent<BusKneelBody>();
                EditorUtility.CopySerialized(srcKnee, knee);
                knee.bodyGroup = bodyT;
            }

            CopyExteriorLights(src, tgt);

            PrefabUtility.SaveAsPrefabAsset(tgt, tp);
            Debug.Log($"[KneelSetup] {_target.name}: {(alreadyGrouped ? "already grouped, kept as is" : $"moved {moved} objects into {string.Join("/", groupNames)}, {left} left in place")}. Backup: {backup}");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(src);
            PrefabUtility.UnloadPrefabContents(tgt);
        }
    }
}
#endif
