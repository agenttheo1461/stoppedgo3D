using System;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  PAX ROLL PLANNER
//
//  Rolls expected boarding AND alighting demand for every stop on a trip
//  when the trip is assigned. Every scheduled stop is always served; this
//  planner only determines how many passengers are expected to board/alight.
//
//  CHANGES THIS PASS:
//   · Alighting is now rolled here instead of as an inline dice-roll inside
//     NPCBusController.ProcessStopArrival — one source of truth for pax
//     numbers instead of two systems each doing their own RNG.
//   · Terminal stops always clear the whole bus (GetAlightingCount forces
//     currentOnboard, no probability involved) — real transit doesn't leave
//     anyone on a bus that's ending its trip.
//   · Demand zones: PaxDemandZoneRegistry holds world-space zones (High or
//     Low), looked up by a stop's actual coordinates. High zones extend a
//     stop's max boarding cap by +10 per overlapping zone (base cap 5, one
//     zone = 15, two = 25...) and skew the roll toward the busier end
//     instead of just padding a negligible tail. Low zones bias the other
//     way — more likely to roll 0, no cap change.
//   · Zones ONLY apply during Rush + Normal bands, which together are
//     exactly the 6am-7pm window (6-9 Rush, 9-16 Normal, 16-19 Rush) — no
//     separate time check needed, the existing band split already lines up.
//   · Night + Reduced (everything outside 6am-7pm) ignore zones completely
//     and use a flat 90% chance of 0 / 10% chance of 1 pax, regardless of
//     what zone a stop sits in.
// ═══════════════════════════════════════════════════════════════════════════════
public static class PaxRollPlanner
{
    public enum TimeBand
    {
        Night,
        Reduced,
        Normal,
        Rush
    }

    // Time bands
    public const float NightStart  = 22f * 60f; // 10 PM
    public const float NightEnd    = 4f * 60f;  // 4 AM

    public const float ReducedMorningStart = 4f * 60f;
    public const float ReducedMorningEnd   = 6f * 60f;

    public const float RushMorningStart = 6f * 60f;
    public const float RushMorningEnd   = 9f * 60f;

    public const float NormalStart = 9f * 60f;
    public const float NormalEnd   = 16f * 60f;

    public const float RushEveningStart = 16f * 60f;
    public const float RushEveningEnd   = 19f * 60f;

    public const float ReducedEveningStart = 19f * 60f;
    public const float ReducedEveningEnd   = 22f * 60f;

    public static TimeBand GetBand(float gameTimeMinutesOfDay)
    {
        float m = ((gameTimeMinutesOfDay % 1440f) + 1440f) % 1440f;

        // 10 PM - 4 AM
        if (m >= NightStart || m < NightEnd)
            return TimeBand.Night;

        // 4 AM - 6 AM
        if (m >= ReducedMorningStart && m < ReducedMorningEnd)
            return TimeBand.Reduced;

        // 6 AM - 9 AM
        if (m >= RushMorningStart && m < RushMorningEnd)
            return TimeBand.Rush;

        // 9 AM - 4 PM
        if (m >= NormalStart && m < NormalEnd)
            return TimeBand.Normal;

        // 4 PM - 7 PM
        if (m >= RushEveningStart && m < RushEveningEnd)
            return TimeBand.Rush;

        // 7 PM - 10 PM
        return TimeBand.Reduced;
    }

    /// <summary>True for Rush and Normal — together exactly the 6am-7pm
    /// window demand zones are scoped to. Night/Reduced never consult zones.</summary>
    public static bool BandAllowsZones(TimeBand band) => band == TimeBand.Rush || band == TimeBand.Normal;

    // Base weights, indices 0-5 (0-5 pax), used as the starting point before
    // any zone adjustment. Rush/Normal only — Night/Reduced use FlatOffPeakWeights.
    private static readonly Dictionary<TimeBand, float[]> BaseWeights = new()
    {
        //             0    1    2    3    4    5
        { TimeBand.Normal,  new[] { 22f, 30f, 24f, 14f, 7f, 3f } },
        { TimeBand.Rush,    new[] { 6f, 14f, 24f, 28f, 18f, 10f } },
    };

    // Flat off-peak roll used for Night AND Reduced, zones ignored entirely:
    // 90% chance of 0 pax, 10% chance of 1 pax.
    private static readonly float[] FlatOffPeakWeights = { 90f, 10f };

    private static int RollFromWeights(System.Random rng, float[] weights)
    {
        float total = 0f;
        foreach (float w in weights) total += w;

        float r = (float)rng.NextDouble() * total;
        float accum = 0f;
        for (int i = 0; i < weights.Length; i++)
        {
            accum += weights[i];
            if (r <= accum) return i;
        }
        return weights.Length - 1;
    }

    /// <summary>Builds the actual weight table used for a stop's boarding
    /// roll, given how many High/Low demand zones cover it. Only ever
    /// called for Rush/Normal — Night/Reduced never reach this.</summary>
    private static float[] BuildZoneAdjustedWeights(TimeBand band, int highZoneCount, int lowZoneCount)
    {
        var weights = new List<float>(BaseWeights[band]);

        // Low zones: bias toward 0, quietly deflate everything else. No cap
        // change — a low-demand zone just means quieter, not impossible.
        for (int i = 0; i < lowZoneCount; i++)
        {
            weights[0] *= 1.5f;
            for (int k = 1; k < weights.Count; k++)
                weights[k] *= 0.7f;
        }

        // High zones: extend the range by 10 pax per zone (base max 5 -> 15
        // for one zone, 25 for two, stacking) and weight the new block
        // higher than the current tail so "busier than normal" actually
        // shows up as a real chance, not a vanishing tail. Also chips the
        // zero-weight down a bit per zone — a real hotspot makes "nobody
        // boards" progressively less likely.
        for (int i = 0; i < highZoneCount; i++)
        {
            if (weights.Count > 0) weights[0] *= 0.85f;
            float blockWeight = weights[weights.Count - 1] * 1.4f;
            for (int k = 0; k < 10; k++)
                weights.Add(blockWeight);
        }

        return weights.ToArray();
    }

    /// <summary>Rolls a full trip's boarding + alighting plan. stopPositions
    /// and isTerminalFlags must be the same length as the stop sequence and
    /// line up index-for-index — needed now so each stop can be checked
    /// against PaxDemandZoneRegistry by its actual world coordinates.</summary>
    public static PaxRollPlan RollTrip(
        IReadOnlyList<Vector3> stopPositions,
        IReadOnlyList<bool> isTerminalFlags,
        float gameTimeMinutesOfDay,
        int? seed = null)
    {
        var rng = seed.HasValue ? new System.Random(seed.Value) : new System.Random();
        var band = GetBand(gameTimeMinutesOfDay);

        int stopCount = stopPositions?.Count ?? 0;
        var boarding  = new int[stopCount];
        var alightingFraction = new float[stopCount];
        var terminal  = new bool[stopCount];

        bool zonesApply = BandAllowsZones(band);

        for (int i = 0; i < stopCount; i++)
        {
            terminal[i] = isTerminalFlags != null && i < isTerminalFlags.Count && isTerminalFlags[i];

            // [FIX] A terminal stop already forces every onboard pax off
            // (GetAlightingCount below) -- but nothing stopped it from ALSO
            // rolling new boarding demand here, which makes no sense for a
            // real transit trip's last stop (the bus is ending service
            // there, not picking anyone up). Skip the roll entirely instead
            // of rolling then discarding, same end result either way but no
            // wasted RNG draw.
            if (terminal[i])
            {
                boarding[i] = 0;
                alightingFraction[i] = 0f; // irrelevant -- GetAlightingCount ignores this for a terminal stop -- but keep it well-defined
                continue;
            }

            float[] weights;
            if (zonesApply)
            {
                PaxDemandZoneRegistry.CountZonesAt(stopPositions[i], out int highCount, out int lowCount);
                weights = BuildZoneAdjustedWeights(band, highCount, lowCount);
            }
            else
            {
                weights = FlatOffPeakWeights;
            }
            boarding[i] = RollFromWeights(rng, weights);

            // Alighting intent — a FRACTION of whoever's onboard when this
            // stop is actually reached, not a flat pre-rolled count. This is
            // what makes GetAlightingCount below both "sticky" (same onboard
            // count in -> same result out, no fresh roll each check, so it
            // doesn't flicker/reset if you drive past without stopping) and
            // "growing" (more people board elsewhere before you service this
            // stop -> the same fraction of a bigger onboard count means more
            // people want off) without needing separate logic for either.
            alightingFraction[i] = rng.NextDouble() < 0.4
                ? (float)(0.15 + rng.NextDouble() * 0.35) // 15%-50% of onboard, when there's demand at all
                : 0f;
        }

        return new PaxRollPlan(boarding, alightingFraction, terminal, band);
    }

    /// <summary>Back-compat overload for any caller that only has a stop
    /// count, not real positions/terminal flags (no zone support, no
    /// terminal-forced alighting — falls back to old flat behavior).</summary>
    [Obsolete("Use the overload with stop positions + terminal flags so zones and terminal alighting actually work.")]
    public static PaxRollPlan RollTrip(int stopCount, float gameTimeMinutesOfDay, int? seed = null)
    {
        var positions = new Vector3[Mathf.Max(0, stopCount)];
        var terminals = new bool[Mathf.Max(0, stopCount)];
        return RollTrip(positions, terminals, gameTimeMinutesOfDay, seed);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  PAX DEMAND ZONES
// ═══════════════════════════════════════════════════════════════════════════════
public enum PaxZoneType { High, Low }

[System.Serializable]
public class PaxDemandZone
{
    public string     name = "Zone";
    public Vector3    center;
    public float      radius = 50f;
    public PaxZoneType type  = PaxZoneType.High;
}

/// <summary>Static registry of active demand zones. A scene-level authoring
/// MonoBehaviour (PaxDemandZoneManager, below) populates this on Awake so
/// zones can be placed/edited in the Inspector; PaxRollPlanner only ever
/// reads from here, it never creates zones itself.</summary>
public static class PaxDemandZoneRegistry
{
    private static readonly List<PaxDemandZone> _zones = new();

    public static void Register(PaxDemandZone zone) { if (zone != null) _zones.Add(zone); }
    public static void Clear() => _zones.Clear();
    public static IReadOnlyList<PaxDemandZone> All => _zones;

    /// <summary>Counts how many High and Low zones cover a world position —
    /// overlapping zones of the same type stack (two High zones covering
    /// the same stop both count), which is what lets stacking zones push a
    /// stop's cap past the single-zone +10.</summary>
    public static void CountZonesAt(Vector3 worldPos, out int highCount, out int lowCount)
    {
        highCount = 0; lowCount = 0;
        foreach (var z in _zones)
        {
            if (z == null) continue;
            if (Vector3.Distance(z.center, worldPos) > z.radius) continue;
            if (z.type == PaxZoneType.High) highCount++;
            else lowCount++;
        }
    }
}

/// <summary>Drop this in the scene and author zones in the Inspector —
/// world-space center + radius + High/Low, matched against bus stop
/// coordinates (BusStopData.GetWorldPosition()) at pax-roll time.</summary>
public class PaxDemandZoneManager : MonoBehaviour
{
    public List<PaxDemandZone> zones = new();

    private void Awake()
    {
        PaxDemandZoneRegistry.Clear();
        foreach (var z in zones) PaxDemandZoneRegistry.Register(z);
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (zones == null) return;
        foreach (var z in zones)
        {
            if (z == null) continue;
            Gizmos.color = z.type == PaxZoneType.High
                ? new Color(1f, 0.35f, 0.2f, 0.25f)
                : new Color(0.2f, 0.5f, 1f, 0.2f);
            Gizmos.DrawSphere(z.center, z.radius);
            Gizmos.color = z.type == PaxZoneType.High ? Color.red : Color.blue;
            Gizmos.DrawWireSphere(z.center, z.radius);
        }
    }
#endif
}

// ═══════════════════════════════════════════════════════════════════════════════
//  PAX ROLL PLAN — per-trip result
// ═══════════════════════════════════════════════════════════════════════════════
public class PaxRollPlan
{
    private readonly int[]   _boarding;
    private readonly float[] _alightingFraction;
    private readonly bool[]  _isTerminal;

    public PaxRollPlanner.TimeBand Band { get; }

    public PaxRollPlan(int[] boarding, float[] alightingFraction, bool[] isTerminal, PaxRollPlanner.TimeBand band)
    {
        _boarding          = boarding          ?? Array.Empty<int>();
        _alightingFraction = alightingFraction ?? Array.Empty<float>();
        _isTerminal        = isTerminal        ?? Array.Empty<bool>();
        Band = band;
    }

    public int StopCount => _boarding.Length;

    public int PaxAt(int stopIndex)
    {
        if (stopIndex < 0 || stopIndex >= _boarding.Length) return 0;
        return _boarding[stopIndex];
    }

    /// <summary>Actual alighting count for this stop, given how many are
    /// really onboard right now. Terminal stops ALWAYS return the full
    /// currentOnboard — real transit doesn't leave anyone on a bus that's
    /// ending its trip, no roll, no chance involved.
    ///
    /// Non-terminal stops apply a pre-rolled FRACTION (not a flat count) to
    /// currentOnboard. This is deliberate: calling this twice with the same
    /// (stopIndex, currentOnboard) always returns the same answer — no
    /// fresh RNG per call — so a driver who drives past without stopping
    /// sees the exact same "stop requested" count next time they check
    /// (never resets/flickers), and if more people board elsewhere before
    /// this stop is actually serviced, currentOnboard goes up and the same
    /// fraction naturally yields MORE people wanting off here. One formula
    /// covers both "stays" and "grows" without separate bookkeeping.
    ///
    /// Rounds to at least 1 (not 0) whenever the fraction is >0 and someone
    /// is onboard — a rolled intent that rounds down to invisible would
    /// silently disable the stop-requested flag even though real demand
    /// exists.</summary>
    public int GetAlightingCount(int stopIndex, int currentOnboard)
    {
        if (currentOnboard <= 0) return 0;

        bool isTerminal = stopIndex >= 0 && stopIndex < _isTerminal.Length && _isTerminal[stopIndex];
        if (isTerminal) return currentOnboard;

        float frac = (stopIndex >= 0 && stopIndex < _alightingFraction.Length) ? _alightingFraction[stopIndex] : 0f;
        if (frac <= 0f) return 0;

        int count = Mathf.RoundToInt(frac * currentOnboard);
        return Mathf.Clamp(count, 1, currentOnboard);
    }
}