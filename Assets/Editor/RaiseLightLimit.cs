using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public class RaiseLightLimit
{
    [MenuItem("Tools/URP/Check Light Limit")]
static void CheckLimit()
{
    var guids = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset");
    foreach (var guid in guids)
    {
        var path = AssetDatabase.GUIDToAssetPath(guid);
        var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
        SerializedObject so = new SerializedObject(asset);
        var prop = so.FindProperty("m_AdditionalLightsPerObjectLimit");
        Debug.Log($"{asset.name} current value: {prop.intValue}");
    }
}
    [MenuItem("Tools/URP/Force Additional Light Limit To 16")]
    static void ForceLimit()
    {
        var guids = AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset");
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);

            SerializedObject so = new SerializedObject(asset);
            SerializedProperty prop = so.FindProperty("m_AdditionalLightsPerObjectLimit");
            if (prop != null)
            {
                prop.intValue = 16;
                so.ApplyModifiedProperties();
                so.Update();
                Debug.Log($"Set {asset.name} m_AdditionalLightsPerObjectLimit to {prop.intValue}");
            }
        }
        AssetDatabase.SaveAssets();
    }
}