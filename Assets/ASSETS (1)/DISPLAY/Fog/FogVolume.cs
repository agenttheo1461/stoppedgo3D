using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
[AddComponentMenu("Rendering/Fog Volume")]
public class FogVolume : MonoBehaviour
{
    [Header("Shape (local space box, uses transform scale)")]
    public Vector3 size = Vector3.one;

    [Header("Fog")]
    [ColorUsage(true, false)] public Color fogColor = new Color(0.6f, 0.65f, 0.7f, 1f);
    [Range(0f, 5f)] public float density = 1f;

    // NOTE: noise-driven density variation is a later pass. For now density is flat
    // across the whole volume, so we can do a closed-form ray-box optical depth
    // instead of raymarching per pixel.
 
    public static readonly List<FogVolume> Active = new List<FogVolume>();

    void OnEnable()
    {
        if (!Active.Contains(this))
            Active.Add(this);
    }

    void OnDisable()
    {
        Active.Remove(this);
    }

    // World-space AABB min/max. Assumes uniform/simple transforms (no arbitrary rotation
    // baked into the box test — shader does an axis-aligned test in world space).
    // If you need rotated volumes later, transform the ray into local space instead.
    public void GetWorldBounds(out Vector3 worldMin, out Vector3 worldMax)
    {
        Vector3 center = transform.position;
        Vector3 halfExtents = Vector3.Scale(size, transform.lossyScale) * 0.5f;
        worldMin = center - halfExtents;
        worldMax = center + halfExtents;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(fogColor.r, fogColor.g, fogColor.b, 0.35f);
        Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        Gizmos.DrawCube(Vector3.zero, Vector3.Scale(size, transform.lossyScale));
        Gizmos.color = fogColor;
        Gizmos.DrawWireCube(Vector3.zero, Vector3.Scale(size, transform.lossyScale));
    }
}
