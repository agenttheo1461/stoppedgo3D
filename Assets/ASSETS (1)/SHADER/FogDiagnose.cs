#if UNITY_EDITOR
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Tools > Fog > Diagnose. Prints (to the Console) everything that decides whether Custom/VolumetricFogBox can show:
/// which pipeline asset is active and whether it has the depth texture on, every enabled camera's position and whether it
/// is INSIDE each fog cube, and whether the shader compiled/is supported. Read-only: changes nothing.
/// Run it in Play Mode with the camera where you expect fog (and again in first person / another camera mode).
/// </summary>
public static class FogDiagnose
{
    [MenuItem("Tools/Fog/Diagnose")]
    private static void Run()
    {
        var sb = new StringBuilder("[FogDiagnose]\n");

        var rp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        sb.AppendLine($"Active pipeline asset: {(rp != null ? rp.name : "none / not URP")}  (quality level: {QualitySettings.names[QualitySettings.GetQualityLevel()]})");
        if (rp != null) sb.AppendLine($"  supportsCameraDepthTexture: {rp.supportsCameraDepthTexture}   supportsCameraOpaqueTexture: {rp.supportsCameraOpaqueTexture}");

        var shader = Shader.Find("Custom/VolumetricFogBox");
        sb.AppendLine(shader == null ? "Shader Custom/VolumetricFogBox: NOT FOUND" : $"Shader Custom/VolumetricFogBox: found, isSupported = {shader.isSupported}");

        var fogRenderers = new System.Collections.Generic.List<MeshRenderer>();
        foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            foreach (var m in r.sharedMaterials)
                if (m != null && m.shader != null && m.shader.name == "Custom/VolumetricFogBox") { fogRenderers.Add(r); break; }
        sb.AppendLine($"Fog cubes in scene: {fogRenderers.Count}");
        foreach (var r in fogRenderers)
        {
            var t = r.transform;
            sb.AppendLine($"  '{r.name}' enabled={r.enabled} active={r.gameObject.activeInHierarchy} pos={t.position} lossyScale={t.lossyScale} layer={LayerMask.LayerToName(r.gameObject.layer)}");
            var m = r.sharedMaterial;
            if (m != null && m.HasProperty("_Density"))
                sb.AppendLine($"    material density={m.GetFloat("_Density")} steps={m.GetFloat("_StepCount")} edge={m.GetFloat("_EdgeSoftness")}");
        }

        foreach (var cam in Camera.allCameras)
        {
            var data = cam.GetUniversalAdditionalCameraData();
            sb.AppendLine($"Camera '{cam.name}' pos={cam.transform.position} enabled={cam.enabled} target={(cam.targetTexture != null ? cam.targetTexture.name : "screen")} " +
                          $"cullingMask={cam.cullingMask} depthOption={(data != null ? data.requiresDepthTexture.ToString() : "n/a")} priority={cam.depth}");
            foreach (var r in fogRenderers)
            {
                var local = r.transform.InverseTransformPoint(cam.transform.position);
                bool inside = Mathf.Abs(local.x) < 0.5f && Mathf.Abs(local.y) < 0.5f && Mathf.Abs(local.z) < 0.5f;
                bool layerVisible = (cam.cullingMask & (1 << r.gameObject.layer)) != 0;
                sb.AppendLine($"    inside '{r.name}': {inside}   (cube-local pos {local:F2}; fog layer visible to this camera: {layerVisible})");
            }
        }
        Debug.Log(sb.ToString());
    }
}
#endif
