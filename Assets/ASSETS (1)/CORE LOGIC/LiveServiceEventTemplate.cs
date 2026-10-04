using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  LIVE SERVICE EVENT TEMPLATE
//
//  A reusable ASSET version of a live event -- author "Route 199 Storm
//  Cleanup" once in the Project window (Create ▸ Headway ▸ Live Service
//  Event Template), and Activate() it as many times as you want, whenever
//  you want, instead of re-typing a console command every time. Each
//  activation is a fresh, independent LiveServiceEvent starting now.
//
//  Runtime lookup-by-name (for the console's "liveevent template <name>")
//  needs the asset under Assets/.../Resources/LiveEventTemplates/ -- that's
//  the one Unity idiom that lets a ScriptableObject be found by name at
//  runtime without a scene reference. Assets elsewhere still work fine if
//  you just drag them into a field or call .Activate() directly (e.g. from
//  a UnityEvent on a button) -- Resources.LoadAll is only needed for the
//  by-name lookup path.
// ═══════════════════════════════════════════════════════════════════════════════
[CreateAssetMenu(fileName = "LiveEvent_", menuName = "Headway/Live Service Event Template")]
public class LiveServiceEventTemplate : ScriptableObject
{
    [Header("Route")]
    public string routeNumber;
    public LiveEventEffect effect = LiveEventEffect.ReducedService;
    public bool outboundAffected = true;
    public bool inboundAffected  = true;

    [Header("Magnitude — ReducedService/AdditionalService only")]
    [Tooltip("ReducedService: fraction of service CUT. AdditionalService: fraction ADDED.")]
    [Range(0f, 1f)] public float fraction = 0.5f;

    [Header("Duration")]
    [Tooltip("Days this runs for once activated. Overridable per-activation (console 'liveevent template <name> <days>' or the daysOverride param).")]
    public float defaultDays = 7f;

    [Header("Label")]
    [Tooltip("Free-text cause/description shown in parentheses on the alert, e.g. \"storm cleanup\".")]
    public string label = "";

    [Header("Detour — Detour effect only")]
    [Tooltip("Typed by hand -- wins over detourWorldPos if non-empty.")]
    public string detourLocationText = "";
    public Vector2 detourWorldPos;
    public bool    hasDetourWorldPos;
    public List<string> skippedStopCodes = new List<string>();

    /// <summary>Creates a real LiveServiceEvent starting right now (DateTime.UtcNow) and lasting
    /// daysOverride days, or defaultDays if daysOverride &lt;= 0. Safe to call repeatedly -- every
    /// call is an independent event with its own id.</summary>
    public LiveServiceEvent Activate(float daysOverride = -1f)
    {
        if (LiveEventManager.Instance == null)
        {
            Debug.LogWarning($"[LiveServiceEventTemplate '{name}'] LiveEventManager isn't ready -- can't activate.");
            return null;
        }
        float days = daysOverride > 0f ? daysOverride : defaultDays;
        return LiveEventManager.Instance.CreateEvent(routeNumber, effect, days, fraction, label,
            detourLocationText, detourWorldPos, hasDetourWorldPos, outboundAffected, inboundAffected,
            skippedStopCodes);
    }
}
