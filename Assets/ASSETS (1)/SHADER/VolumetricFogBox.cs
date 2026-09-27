using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  VOLUMETRIC FOG BOX
//
//  Thin wrapper: builds a unit cube mesh (the shader's raymarch assumes -0.5..
//  0.5 local space) and applies a VolumetricFogBox material, then just scales
//  the transform to the desired world-space box size. This is the "volumetric
//  box" test case — once this reads right in-scene, the same material/mesh
//  setup can be placed per-district or tied to weather/time-of-day systems.
// ═══════════════════════════════════════════════════════════════════════════════
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class VolumetricFogBox : MonoBehaviour
{
    [Header("Box Size (world units)")]
    public Vector3 size = new Vector3(50f, 20f, 50f);

    [Header("Material")]
    public Material fogMaterial; // assign a material using Custom/VolumetricFogBox

    private static Mesh _sharedUnitCube;

    private void OnEnable()
    {
        var mf = GetComponent<MeshFilter>();
        var mr = GetComponent<MeshRenderer>();

        mf.sharedMesh = GetUnitCube();
        if (fogMaterial != null) mr.sharedMaterial = fogMaterial;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;

        ApplySize();
    }

    private void OnValidate() => ApplySize();

    private void ApplySize()
    {
        transform.localScale = size;
    }

    private static Mesh GetUnitCube()
    {
        if (_sharedUnitCube != null) return _sharedUnitCube;

        var m = new Mesh { name = "VolumetricFogUnitCube" };
        Vector3[] v =
        {
            new(-0.5f,-0.5f,-0.5f), new(0.5f,-0.5f,-0.5f), new(0.5f,0.5f,-0.5f), new(-0.5f,0.5f,-0.5f),
            new(-0.5f,-0.5f,0.5f),  new(0.5f,-0.5f,0.5f),  new(0.5f,0.5f,0.5f),  new(-0.5f,0.5f,0.5f),
        };
        int[] tris =
        {
            0,2,1, 0,3,2,       // back
            5,7,4, 5,6,7,       // front
            4,3,0, 4,7,3,       // left
            1,6,5, 1,2,6,       // right
            3,6,2, 3,7,6,       // top
            4,1,5, 4,0,1,       // bottom
        };
        m.SetVertices(v);
        m.SetTriangles(tris, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        _sharedUnitCube = m;
        return m;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.4f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
    }
#endif
}
