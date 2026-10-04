using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  DESIRABLE ZONE
//
//  Hand-placed in the Inspector: a world X/Z center + a radius, marking an area
//  passengers actually want to go (a downtown core, a stadium, a beach strip...).
//  Nothing pathfinds toward these -- they only bias which onboard passenger a
//  boarding roll assigns as their "stops ahead" destination (see
//  PlayerHandoff.RollPassengerDestination), so a stop that lands inside a zone
//  shows up as somebody's destination more often than one that doesn't.
//
//  Every instance self-registers into the static `All` list on enable, so any
//  system can just read DesirableZone.All / DesirableZone.WeightAt(pos) without
//  having to be handed references. Untested in Unity.
// ═══════════════════════════════════════════════════════════════════════════════
public class DesirableZone : MonoBehaviour
{
    [Header("Zone")]
    [Tooltip("World X/Z center of the zone (Y is ignored -- distance check is flat, like RoadEvent.IsInRange).")]
    public Vector2 center = Vector2.zero;

    [Tooltip("Radius in world units. A stop within this distance of the center counts as inside the zone.")]
    public float range = 50f;

    [Tooltip("How much more likely a stop inside this zone is to get picked as a destination, vs. a stop outside any zone (weight 1). 4 = 4x as likely.")]
    public float desirabilityWeight = 4f;

    public static readonly List<DesirableZone> All = new List<DesirableZone>();

    private void OnEnable()  { if (!All.Contains(this)) All.Add(this); }
    private void OnDisable() { All.Remove(this); }

    public bool Contains(Vector3 worldPos)
    {
        float dx = worldPos.x - center.x;
        float dz = worldPos.z - center.y;
        return dx * dx + dz * dz <= range * range;
    }

    /// <summary>Highest desirability weight among every zone containing worldPos, or 1 (neutral) if none do.</summary>
    public static float WeightAt(Vector3 worldPos)
    {
        float best = 1f;
        for (int i = 0; i < All.Count; i++)
        {
            var z = All[i];
            if (z != null && z.Contains(worldPos) && z.desirabilityWeight > best)
                best = z.desirabilityWeight;
        }
        return best;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.2f, 0.9f, 0.3f, 0.20f);
        Vector3 c = new Vector3(center.x, transform.position.y, center.y);
        Gizmos.DrawSphere(c, range);
        Gizmos.color = new Color(0.2f, 0.9f, 0.3f, 0.85f);
        Gizmos.DrawWireSphere(c, range);
#if UNITY_EDITOR
        UnityEditor.Handles.Label(c + Vector3.up * (range * 0.3f + 1f), $"[{name}] desirable ×{desirabilityWeight}");
#endif
    }
}
