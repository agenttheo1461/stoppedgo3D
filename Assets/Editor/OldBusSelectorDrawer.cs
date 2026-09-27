#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(OldBusVariantSelector))]
public class OldBusVariantSelectorDrawer : PropertyDrawer
{
    private static readonly Dictionary<TransmissionType, string[]> VariantNames = new()
    {
        { TransmissionType.B400R, new[] { "None", "Deep Moan (tired engine)", "Hollow Engine" } },
        { TransmissionType.B500R, new[] { "None", "Deep Moan (tired engine)", "Hollow Engine" } },
        { TransmissionType.D8645, new[] { "None", "Deep Moan (tired engine)", "Hollow Engine", "Tired Wheeze" } },
        { TransmissionType.H50EP, new[] { "None", "Deep Moan (tired motor)", "Hollow Motor", "Tired / Worn Motor" } },
        { TransmissionType.H40EP, new[] { "None", "Worn Whir" } },
    };

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        var variantProp = property.FindPropertyRelative("variant");

        // Sibling "transmission" field lives one level up on FleetSeriesDefinition
        string path = property.propertyPath;
        string parentPath = path.Substring(0, path.LastIndexOf('.'));
        var txProp = property.serializedObject.FindProperty(parentPath + ".transmission");

        EditorGUI.BeginProperty(position, label, property);

        if (txProp == null)
        {
            EditorGUI.PropertyField(position, variantProp, label);
            EditorGUI.EndProperty();
            return;
        }

        var txValue = (TransmissionType)txProp.enumValueIndex;

        if (!VariantNames.TryGetValue(txValue, out var names))
        {
            EditorGUI.LabelField(position, label.text, "(no old-bus presets for this TX)");
            variantProp.intValue = 0;
            EditorGUI.EndProperty();
            return;
        }

        int clamped = Mathf.Clamp(variantProp.intValue, 0, names.Length - 1);
        int selected = EditorGUI.Popup(position, "Old Bus Sound", clamped, names);
        variantProp.intValue = selected;

        EditorGUI.EndProperty();
    }
}
#endif