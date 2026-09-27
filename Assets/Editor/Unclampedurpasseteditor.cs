using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// Put this in an Editor folder, e.g. Assets/Editor/UnclampedURPAssetEditor.cs
//
// Draws an unclamped int field for m_AdditionalLightsPerObjectLimit above the
// normal URP inspector, so you can type any value without the stock 0-8
// slider clamping it back down on the next redraw.
//
// NOTE: this does NOT replace URP's own inspector -- it just runs first and
// lets you set the real value before the stock editor draws its own (still
// clamped) slider below. You'll see the field twice; use the one at the top
// under "Additional Lights Per Object Limit (Unclamped)" to actually change
// the value. Don't drag the stock slider underneath it, or it'll clamp again.
[CustomEditor(typeof(UniversalRenderPipelineAsset))]
[CanEditMultipleObjects]
public class UnclampedURPAssetEditor : Editor
{
    Editor defaultEditor;

    void OnEnable()
    {
        // Create the real URP editor via reflection so we still get the
        // normal, fully-featured inspector drawn below our override field.
        var urpEditorType = System.Type.GetType(
            "UnityEditor.Rendering.Universal.UniversalRenderPipelineAssetEditor, Unity.RenderPipelines.Universal.Editor");

        if (urpEditorType != null)
            defaultEditor = CreateEditor(targets, urpEditorType);
    }

    void OnDisable()
    {
        if (defaultEditor != null)
        {
            DestroyImmediate(defaultEditor);
        }
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
var prop = serializedObject.FindProperty("m_AdditionalLightsPerObjectLimit");
if (prop != null)
{
    EditorGUILayout.LabelField("Unclamped Override", EditorStyles.boldLabel);

    EditorGUI.BeginChangeCheck();
    int newValue = EditorGUILayout.IntField(
        new GUIContent("Additional Lights Per Object Limit"),
        prop.intValue);
    if (EditorGUI.EndChangeCheck())
    {
        prop.intValue = Mathf.Max(0, newValue); // no upper clamp
    }

    EditorGUILayout.Space();
}
        serializedObject.ApplyModifiedProperties();

        EditorGUILayout.LabelField("Standard URP Inspector", EditorStyles.boldLabel);
        if (defaultEditor != null)
        {
            defaultEditor.OnInspectorGUI();
        }
        else
        {
            EditorGUILayout.HelpBox("Could not find URP's built-in editor via reflection -- falling back to default inspector.", MessageType.Warning);
            DrawDefaultInspector();
        }
    }
}