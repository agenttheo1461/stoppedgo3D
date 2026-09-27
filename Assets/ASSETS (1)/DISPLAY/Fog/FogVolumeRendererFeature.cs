using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// Renderer Feature: add this to your Standard Universal Renderer asset.
// Requires: FogVolume.shader assigned in the inspector (Hidden/FogVolumeBlit or wherever you put it).
public class FogVolumeRendererFeature : ScriptableRendererFeature
{
    public const int MAX_VOLUMES = 8;

    [SerializeField] private Shader fogShader;
    [SerializeField] private RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;

    private Material fogMaterial;
    private FogVolumePass fogPass;

    public override void Create()
    {
        if (fogShader == null)
            fogShader = Shader.Find("Hidden/FogVolumeBlit");

        if (fogShader != null)
            fogMaterial = CoreUtils.CreateEngineMaterial(fogShader);

        fogPass = new FogVolumePass(fogMaterial)
        {
            renderPassEvent = renderPassEvent
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (fogMaterial == null || FogVolume.Active.Count == 0)
            return;

        renderer.EnqueuePass(fogPass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(fogMaterial);
    }

    private class FogVolumePass : ScriptableRenderPass
    {
        private readonly Material material;

        private static readonly int VolumeCountId = Shader.PropertyToID("_FogVolumeCount");
        private static readonly int BoxMinId = Shader.PropertyToID("_FogBoxMin");
        private static readonly int BoxMaxId = Shader.PropertyToID("_FogBoxMax");
        private static readonly int ColorId = Shader.PropertyToID("_FogColor");
        private static readonly int DensityId = Shader.PropertyToID("_FogDensity");

        private readonly Vector4[] boxMin = new Vector4[MAX_VOLUMES];
        private readonly Vector4[] boxMax = new Vector4[MAX_VOLUMES];
        private readonly Vector4[] colors = new Vector4[MAX_VOLUMES];
        private readonly float[] densities = new float[MAX_VOLUMES];

        private class PassData
        {
            public Material material;
            public TextureHandle source;
        }

        public FogVolumePass(Material mat)
        {
            material = mat;
            requiresIntermediateTexture = true;
            // [FIX] Without this, URP has no reason to ever populate
            // cameraDepthTexture, so SampleSceneDepth() in the shader can
            // come back invalid/zero — which trips the shader's
            // "rawDepth <= 0.0001 -> return sceneColor unchanged" bail-out
            // and the fog never appears even when this pass does run.
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        private void GatherVolumes()
        {
            int count = Mathf.Min(FogVolume.Active.Count, MAX_VOLUMES);
            for (int i = 0; i < count; i++)
            {
                var v = FogVolume.Active[i];
                v.GetWorldBounds(out Vector3 mn, out Vector3 mx);
                boxMin[i] = mn;
                boxMax[i] = mx;
                colors[i] = v.fogColor;
                densities[i] = v.density;
            }

            material.SetInt(VolumeCountId, count);
            material.SetVectorArray(BoxMinId, boxMin);
            material.SetVectorArray(BoxMaxId, boxMax);
            material.SetVectorArray(ColorId, colors);
            material.SetFloatArray(DensityId, densities);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (material == null)
                return;

            GatherVolumes();

            var resourceData = frameData.Get<UniversalResourceData>();

            // [FIX] This used to bail out entirely with
            // "if (resourceData.isActiveTargetBackBuffer) return;". That
            // made the pass a no-op on the most common setup — a plain
            // camera with no other pass forcing an intermediate target,
            // where the active target IS the back buffer at this render
            // pass event. That's why the fog never showed up at all: the
            // pass silently skipped itself every single frame. We still
            // read whichever texture is actually active (back buffer or
            // intermediate) via activeColorTexture below, and write our
            // blended result into a fresh texture that becomes the new
            // camera color either way.
            var source = resourceData.activeColorTexture;

            var destDesc = renderGraph.GetTextureDesc(source);
            destDesc.name = "_FogVolumeTarget";
            destDesc.clearBuffer = false;
            TextureHandle destination = renderGraph.CreateTexture(destDesc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Fog Volumes", out var passData))
            {
                passData.material = material;
                passData.source = source;

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                builder.SetRenderAttachment(destination, 0, AccessFlags.Write);

                builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                {
                    Blitter.BlitTexture(ctx.cmd, data.source, new Vector4(1, 1, 0, 0), data.material, 0);
                });
            }

            resourceData.cameraColor = destination;
        }
    }
}