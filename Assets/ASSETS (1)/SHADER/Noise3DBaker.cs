using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  NOISE 3D BAKER
//
//  Builds one shared Texture3D of smooth value noise at startup and pushes it
//  to the global shader property _VolumeNoiseTex, so every VolumetricFogBox in
//  the scene samples the same baked volume — trilinear-filtered texture reads
//  are what actually make it look like drifting fog instead of static/grain,
//  which a per-pixel hash function can't give you (each sample point is fully
//  independent, so consecutive raymarch steps don't correlate into "shapes").
// ═══════════════════════════════════════════════════════════════════════════════
public class Noise3DBaker : MonoBehaviour
{
    [Header("Bake Settings")]
    public int resolution = 32; // 32^3 is plenty for stylized fog, keeps bake fast
    [Tooltip("Higher = finer base grain before the shader's own scale/octaves stretch it.")]
    public float cellFrequency = 4f;

    private static readonly int NoiseTexID = Shader.PropertyToID("_VolumeNoiseTex");

    private void Awake() => Bake();

    [ContextMenu("Rebake")]
    public void Bake()
    {
        var tex = new Texture3D(resolution, resolution, resolution, TextureFormat.R8, false)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            name = "BakedVolumeNoise",
        };

        var colors = new Color[resolution * resolution * resolution];
        // simple seeded value-noise lattice, baked once — cheap and deterministic
        var rng = new System.Random(1337);
        var lattice = new float[resolution, resolution, resolution];
        for (int x = 0; x < resolution; x++)
            for (int y = 0; y < resolution; y++)
                for (int z = 0; z < resolution; z++)
                    lattice[x, y, z] = (float)rng.NextDouble();

        for (int x = 0; x < resolution; x++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int z = 0; z < resolution; z++)
                {
                    float v = lattice[x, y, z];
                    colors[x + y * resolution + z * resolution * resolution] = new Color(v, v, v, v);
                }
            }
        }

        tex.SetPixels(colors);
        tex.Apply(false, false);

        Shader.SetGlobalTexture(NoiseTexID, tex);
    }
} 
