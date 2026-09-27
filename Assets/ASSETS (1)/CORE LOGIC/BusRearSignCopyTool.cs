#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Bus > Copy Rear Route Number Sign. Copies the rear BusInteriorRouteNumberSign object
/// (the little "DESTBOARD" under the rear section on 3.4.5.1xd60) onto every other articulated
/// bus prefab in the folder that has a "rear" section and doesn't already have one. The copy keeps
/// its local position/rotation and is parented under the rear section's "body" group if it has one
/// (so it kneels with the body), else directly under "rear". Its data source is re-pointed at the
/// SAME data source the target's own destination sign uses. A backup of each prefab is made first.
/// </summary>
public class BusRearSignCopyTool : EditorWindow
{
    private GameObject _source;
    private const string Folder = "Assets/ASSETS (1)/BUS MODELS";

    [MenuItem("Tools/Bus/Copy Rear Route Number Sign")]
    private static void Open() => GetWindow<BusRearSignCopyTool>("Rear Route Sign");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox($"Source = the articulated prefab that has the rear route number sign (3.4.5.1xd60). Applies to every other prefab in {Folder} that has a 'rear' section and no BusInteriorRouteNumberSign yet.", MessageType.Info);
        _source = (GameObject)EditorGUILayout.ObjectField("Source prefab", _source, typeof(GameObject), false);
        using (new EditorGUI.DisabledScope(_source == null))
            if (GUILayout.Button("Apply to all articulated prefabs")) Apply();
    }

    private static Transform Rear(GameObject root) =>
        root.transform.Cast<Transform>().FirstOrDefault(t => t.name.ToLowerInvariant() == "rear");

    private void Apply()
    {
        string sp = AssetDatabase.GetAssetPath(_source);
        var src = PrefabUtility.LoadPrefabContents(sp);
        int done = 0, skipped = 0;
        try
        {
            var srcSign = src.GetComponentInChildren<BusInteriorRouteNumberSign>(true);
            if (srcSign == null) { Debug.LogError("[RearSign] Source has no BusInteriorRouteNumberSign."); return; }

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { Folder }))
            {
                string tp = AssetDatabase.GUIDToAssetPath(guid);
                if (tp == sp || tp.Contains("(backup")) continue;

                var tgt = PrefabUtility.LoadPrefabContents(tp);
                try
                {
                    var rear = Rear(tgt);
                    if (rear == null || tgt.GetComponentInChildren<BusInteriorRouteNumberSign>(true) != null) { skipped++; continue; }

                    var destSign = tgt.GetComponentInChildren<BusInteriorDestinationSign>(true);
                    if (destSign == null) { Debug.LogWarning($"[RearSign] {tp}: no BusInteriorDestinationSign to borrow a data source from -- skipped."); skipped++; continue; }

                    AssetDatabase.CopyAsset(tp, AssetDatabase.GenerateUniqueAssetPath(tp.Replace(".prefab", " (backup pre-rearsign).prefab")));

                    var parent = rear.Find("body") ?? rear;
                    var copy = Object.Instantiate(srcSign.gameObject);
                    copy.name = srcSign.gameObject.name;
                    copy.transform.SetParent(parent, false);
                    copy.transform.localPosition = srcSign.transform.localPosition;
                    copy.transform.localRotation = srcSign.transform.localRotation;
                    copy.transform.localScale    = srcSign.transform.localScale;

                    var newSign = copy.GetComponent<BusInteriorRouteNumberSign>();
                    var dsProp = new SerializedObject(destSign).FindProperty("dataSourceBehaviour");
                    var so = new SerializedObject(newSign);
                    so.FindProperty("dataSourceBehaviour").objectReferenceValue = dsProp != null ? dsProp.objectReferenceValue : null;
                    so.ApplyModifiedPropertiesWithoutUndo();

                    PrefabUtility.SaveAsPrefabAsset(tgt, tp);
                    Debug.Log($"[RearSign] {tp}: added rear route number sign under '{parent.name}'.");
                    done++;
                }
                finally { PrefabUtility.UnloadPrefabContents(tgt); }
            }
        }
        finally { PrefabUtility.UnloadPrefabContents(src); }
        Debug.Log($"[RearSign] Done: {done} prefab(s) updated, {skipped} skipped (no rear section, or already had one).");
    }
}
#endif
