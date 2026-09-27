using System.Collections.Generic;
using UnityEngine;
// Accelera (Cummins) NextGen — permanent-magnet direct-drive traction
// motor, single ratio (no gearbox). Distinct from the default ELFA 3
// eAxle drivetrain ("elfa3"/"elfa3_zfave130" tx, which has its own
// 3-speed internal ratio) — this enum value routes to the "accelera"/
// "accelera_zfave130" tx strings instead. See FleetSeriesDefinition.GetTxFor.
// ═════════════════════════════════════════════════════════════════════
//  [FIX] Was declared in a mostly-arbitrary order (Allison, Voith, BAE,
//  HDS300, B400R, B500R, ZF, H40EP, H50EP, Electric, B3400xFE, D8645,
//  ZH50EP, D8646Art, Accelera, H50EPGen5 -- no grouping, hybrid/combustion/
//  electric all interleaved). Regrouped by drivetrain family below for
//  readability in the Inspector dropdown.
//
//  IMPORTANT: every original member keeps its ORIGINAL underlying int
//  value via explicit assignment -- FleetSeriesDefinition.transmission is
//  serialized as a raw int in every existing .asset file, so silently
//  reordering the enum's default 0,1,2... values would reinterpret every
//  saved series as a DIFFERENT transmission the next time Unity loads it.
//  Only the DECLARED (display) order changes here; every old value is
//  pinned to what it always was.
//
//  [ADD] "add in all options for electrics and accelera" -- the old
//  binary Accelera-vs-default split (see GetTxFor below, pre-fix) could
//  only ever pick between ONE default (elfa3/zfave130/elfa2/fcave130
//  depending on engine) and ONE Accelera variant. It had no way to
//  explicitly choose zfave130 on a standard XE40, or a center-axle
//  layout on either body length -- despite BusSelectMenu's own compatTX
//  list already offering all 5 real options (elfa3, accelera, zfave130,
//  elfa3_centeraxle, accelera_centeraxle -- and the elfa2/_fc equivalents
//  for hydrogen) on EVERY electric engine, std or artic alike. New
//  members appended at the END (16-18) so old serialized values are
//  untouched; "Accelera" (14) keeps its exact old default behavior for
//  any series still using it.
// ═════════════════════════════════════════════════════════════════════
public enum TransmissionType
{
    // ── Combustion — hydraulic/mechanical automatics ────────────────
    Allison    = 0,
    B400R      = 4,
    B500R      = 5,
    ZF         = 6,
    // [RESTORE] Re-added after being pulled from selectable content --
    // BusAudioEngine's DSP for EcoLife 2 was always intact, only the
    // enum + menu/roster paths were removed. Real ZF EcoLife 2, offered
    // wherever gen-1 ZF is (L9N, ISL9) per the audio file's own notes.
    ZFEL2      = 21,
    ZFEL2_HD   = 22,
    Voith      = 1,
    B3400xFE   = 10,
    D8645      = 11,
    D8646Art   = 13,
    // [ADD] Real Allison Gen 4/Gen 5 split ("5th Generation Controls",
    // 2017+) -- same naming convention as H50EPGen5 above.
    B400RGen5  = 19,
    B500RGen5  = 20,
    // [ADD] Real pre-2010 generation of H40EP/H50EP, before Allison's 2010
    // rename. ISL-only: Cummins ISL9 + Allison EP-40 was the real launch
    // pairing on New Flyer's Xcelsior XDE40 per Wikipedia's own article.
    EP40       = 23,
    EP50       = 24,

    // ── Combustion-hybrid series (diesel-electric) ───────────────────
    H40EP      = 7,
    H50EP      = 8,
    H50EPGen5  = 15,
    ZH50EP     = 12,
    BAE        = 2,
    HDS300     = 3,

    // ── Battery / hydrogen electric ──────────────────────────────────
    Electric           = 9,  // legacy value -- see GetTxFor's "electric" -> "elfa3" migration
    Accelera           = 14, // legacy default: direct-drive on std bodies, center-axle on artic
    ZFAVE130           = 16, // [ADD] explicit ZF portal-axle pick, any body length
    ElfaCenterAxle     = 17, // [ADD] explicit ELFA/ELFA2 rear + in-wheel center-axle pick
    AcceleraCenterAxle = 18, // [ADD] explicit Accelera NextGen rear + in-wheel center-axle pick
}
// ═══════════════════════════════════════════════════════════════════════════════
//  FLEET SERIES DEFINITION  —  a numbered block of buses sharing a type/engine
// ═══════════════════════════════════════════════════════════════════════════════
public enum BusTxPreference { AllVoith, AllAllison, AllBAE, MixedAllisonVoith }
public enum AdPackage
{
    None,
    King,
    FullWrap
}
[System.Serializable]
public class DepotAllocation
{
    public DepotData depot;

    [Min(1)]
    public int busCount = 1;
}
    [System.Serializable]
public class OldBusVariantSelector
{
    [Tooltip("0 = None. Valid range/meaning depends on this series' Transmission — see the custom inspector dropdown.")]
    public int variant = 0;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  ENGINE VOICE OPTIONS  —  real, built-in per-engine character layers.
//  Applies to L9 / L9N / ISL9 REGARDLESS of transmission — only the base
//  oscillating idle whine (quiet in G1, near-silent G2-4) is Voith-specific;
//  these three are engine-level and fire the same on an Allison, ZF, BAE, or
//  Voith DIWA gearbox.
//  opt1_1 = delayed whine that mimics rpm ~0.5s late
//  opt1_2 = deep whine overlay, G1 only, ramps harder with rpm than the base whine
//  opt1_3 = smoother/quieter overall engine character
// ═══════════════════════════════════════════════════════════════════════════════
public enum DiwaMixMode { Fixed, RandomMix }

[System.Serializable]
public class DiwaVoiceOptions
{
    [Header("Which layers this series can use")]
    public bool opt1_1_delayedWhine  = false;
    public bool opt1_2_deepOverlay   = false;
    public bool opt1_3_smoothQuiet   = false;
    public bool opt1_4_secondCharacter = false; // suppressed whine til late G1, hiss window, extended G1, audible piston firing
    public bool opt1_5_shaker = false; // "the shaker": no whine at all, deeper+louder core, rapid-fire click vibration-sim at idle
    public bool opt1_6_fourthVoice = false; // whine hidden like opt1_4 but quieter, extended two-tone G1, groan-only retarder
    public bool opt1_7_fifthVoice = false; // as opt1_6 but whine never hidden -- hear the Wandler directly, winds up on move-off
    public bool opt1_8_strain = false;     // harder, load-driven whine that surges irregularly (eeEEEHHHHEHEEE). D864.6 only.

    [Header("Assignment")]
    [Tooltip("Fixed = every bus in the series gets exactly the ticked layers above.\n" +
             "RandomMix = each bus independently rolls each ticked layer against its own % chance below.")]
    public DiwaMixMode mixMode = DiwaMixMode.Fixed;

    [Range(0f, 1f)] public float opt1_1_chance = 0.33f;
    [Range(0f, 1f)] public float opt1_2_chance = 0.33f;
    [Range(0f, 1f)] public float opt1_3_chance = 0.33f;
    [Range(0f, 1f)] public float opt1_4_chance = 0.33f;
    [Range(0f, 1f)] public float opt1_5_chance = 0.33f;
    [Range(0f, 1f)] public float opt1_6_chance = 0.33f;
    [Range(0f, 1f)] public float opt1_7_chance = 0.33f;
    [Range(0f, 1f)] public float opt1_8_chance = 0.33f;

    [System.Serializable]
    public struct Resolved
    {
        public bool opt1_1;
        public bool opt1_2;
        public bool opt1_3;
        public bool opt1_4;
        public bool opt1_5;
        public bool opt1_6;
        public bool opt1_7;
        public bool opt1_8;
    }

    public Resolved Resolve(int fleetNumber)
    {
        var r = new Resolved();

        if (mixMode == DiwaMixMode.Fixed)
        {
            r.opt1_1 = opt1_1_delayedWhine;
            r.opt1_2 = opt1_2_deepOverlay;
            r.opt1_3 = opt1_3_smoothQuiet;
            r.opt1_4 = opt1_4_secondCharacter;
            r.opt1_5 = opt1_5_shaker;
            r.opt1_6 = opt1_6_fourthVoice;
            r.opt1_7 = opt1_7_fifthVoice;
            r.opt1_8 = opt1_8_strain;
            return r;
        }

        // Deterministic per-bus roll so the same fleet number always gets the
        // same voice within a session, instead of re-rolling every rebuild.
        int seed = unchecked((int)((uint)fleetNumber * 2654435761u));
        var rng = new System.Random(seed);

        r.opt1_1 = opt1_1_delayedWhine && rng.NextDouble() < opt1_1_chance;
        r.opt1_2 = opt1_2_deepOverlay  && rng.NextDouble() < opt1_2_chance;
        r.opt1_3 = opt1_3_smoothQuiet  && rng.NextDouble() < opt1_3_chance;
        r.opt1_4 = opt1_4_secondCharacter && rng.NextDouble() < opt1_4_chance;
        r.opt1_5 = opt1_5_shaker && rng.NextDouble() < opt1_5_chance;
        r.opt1_6 = opt1_6_fourthVoice && rng.NextDouble() < opt1_6_chance;
        r.opt1_7 = opt1_7_fifthVoice && rng.NextDouble() < opt1_7_chance;
        r.opt1_8 = opt1_8_strain && rng.NextDouble() < opt1_8_chance;
        return r;
    }
}
[System.Serializable]
public class FleetSeriesDefinition
{
    [Header("Identity")]
    public string seriesName = "2000 Series";
    public string busType    = "XD40";

    // [ADD] Non-serialized -- only ever set at runtime by FleetRosterData.MergeInto()
    // when this definition gets pulled into a merged multi-dataset roster. Stays
    // blank when the source asset is used standalone (single-dataset mode).
    [System.NonSerialized] public string sourceAgencyTag = "";

    [Header("Fleet Numbers")]
    public int startFleetNumber = 2001;
    public int busCount         = 15;

    [Header("Condition")]
    public BusCondition defaultCondition = BusCondition.Standard;
    public bool randomizeCondition = false;
    [Tooltip("If true, each bus in this series gets a randomized condition.")]



    [Header("Engine / TX")]
    public BusSimulationController.EngineType engineType = BusSimulationController.EngineType.L9;
    public TransmissionType transmission = TransmissionType.B400R;
    // [ADD] Needed for BusVehicleSystem/BusBreakdownSystem's drivetrain-aware
    // eligibility -- previously fuel type only ever got set manually per
    // PREFAB in the Inspector, with zero connection to which fleet SERIES a
    // spawned bus actually belongs to.
    [Tooltip("Drivetrain fuel type for BusVehicleSystem -- Electric covers both battery-electric AND hydrogen fuel-cell (both electric drivetrains for breakdown-eligibility purposes).")]
    public BusVehicleSystem.FuelSystemType fuelType = BusVehicleSystem.FuelSystemType.Diesel;
    [Tooltip("Hybrid-diesel-electric (XDE): still Diesel fuelType, just check this box for the better MPG default.")]
    public bool isHybridAssist = false;

    [Header("Model Year (informational / for lore & UI)")]
    public int modelYear = 2020;

    [Header("Body")]
    public bool isArticulated = false;
    [Tooltip("35ft mini bus. Independent of busType text — set this directly instead of " +
             "relying on busType being exactly 'XD35'/'XN35'.")]
    public bool is35Ft = false;

    [Header("Passenger Capacity")]
    [Tooltip("Seats. 0 = default for this body length (35ft 32, 40ft 40, 60ft artic 61 -- New Flyer Xcelsior published figures; fuel type doesn't change them).")]
    public int seatedCapacityOverride = 0;
    [Tooltip("Standing room. 0 = default for this body length (35ft 33, 40ft 43, 60ft artic 62). These are crush-load figures, so lower them if you want a more comfortable ceiling.")]
    public int standingCapacityOverride = 0;

    public int SeatedCapacity   => seatedCapacityOverride   > 0 ? seatedCapacityOverride   : BusCapacityDefaults.Seated(isArticulated, IsMini35());
    public int StandingCapacity => standingCapacityOverride > 0 ? standingCapacityOverride : BusCapacityDefaults.Standing(isArticulated, IsMini35());
    /// <summary>Seated + standing: the most riders the bus is meant to carry.</summary>
    public int PassengerCapacity => SeatedCapacity + StandingCapacity;

    [Header("Prefab")]
    public GameObject prefab;

[Header("Depot Allocation")]
[Tooltip("Leave empty to use Default Depot.")]
public List<DepotAllocation> depotAllocations = new();

[Tooltip("Used if no allocations are specified.")]
public DepotData homeDepot;
    [Header("Customization")]
public bool enableWheelCovers = true;
public bool enableBikeRack = true;
public bool enableWheelchairRamp = true;
// in FleetSeriesDefinition
public bool isOldBus = false;
[Header("Old Bus Sound (only applies if a matching preset exists for the Transmission above)")]
public OldBusVariantSelector oldBusVariant = new OldBusVariantSelector();
public int GetOldBusVariant() => oldBusVariant != null ? oldBusVariant.variant : 0;

[Header("Engine Voice Options (L9, L9N, ISL9 — any transmission)")]
[Tooltip("Built-in per-engine character layers, resolved per-bus at roster build time. " +
         "These are NOT gated by transmission — they fire the same on an Allison, ZF, BAE, " +
         "or Voith DIWA gearbox. Only the base oscillating idle whine (quiet in G1, " +
         "near-silent G2-4) is Voith-specific; these three are engine-level. " +
         "Tick the ones this series should be able to get, then choose Fixed (every " +
         "bus in the series gets exactly the ticked set) or Random Mix (each bus " +
         "independently rolls each ticked option against its own percentage).")]
public DiwaVoiceOptions diwaVoice = new DiwaVoiceOptions();

/// <summary>
/// Resolves which engine voice sub-options a specific bus in this series gets.
/// Deterministic per fleet number (seeded), so re-building the roster in the
/// same session always gives the same bus the same voice.
/// </summary>
public DiwaVoiceOptions.Resolved GetDiwaVoiceFor(int fleetNumber)
{
    return diwaVoice.Resolve(fleetNumber);
}
//public enum EngineType { L9N, L9, B67, X10, XE40, ISL9, XE60, ISL, ISB67, ISLG }

/// <summary>35ft mini gate. Checks the explicit is35Ft checkbox first; falls back to the
/// old busType text match ("XD35"/"XN35") so series set up before is35Ft existed still
/// work without you having to go re-tick every one of them.</summary>
public bool IsMini35() => is35Ft || busType == "XD35" || busType == "XN35";

/// <summary>[ADD] Battery/hydrogen-electric is the one family where body
/// length (std vs artic) is a SEPARATE EngineType value (XE40 vs XE60,
/// XHE40 vs XHE60) rather than being handled purely by isArticulated the
/// way combustion's D8646Art gate is. That's an easy trap: tick
/// isArticulated but leave the Engine Type dropdown on XE40, and you get a
/// standard-body drivetrain assigned to an articulated bus with no warning
/// — GetTxFor only offers std tx options, and ApplyEngineConstants would
/// reject an artic tx anyway since it validates against engineType. This
/// auto-upgrades the effective type so isArticulated alone is authoritative
/// for the electric family too — use this instead of raw `engineType`
/// anywhere it's applied to a spawned bus or fed into GetTxFor.</summary>
public BusSimulationController.EngineType EffectiveEngineType()
{
    if (isArticulated && engineType == BusSimulationController.EngineType.XE40) return BusSimulationController.EngineType.XE60;
    if (isArticulated && engineType == BusSimulationController.EngineType.XHE40) return BusSimulationController.EngineType.XHE60;
    // NOTE: the reverse mismatch (engineType = XE60/XHE60 but isArticulated
    // left unticked) is NOT auto-corrected here — isArticulated also drives
    // FleetMetadata/route eligibility (articulatedPolicy) elsewhere, so
    // silently flipping it here could change route eligibility behind your
    // back. That combination just falls through unchanged; double check it
    // if you see a route rejecting a bus it should allow.
    return engineType;
}

// [ADD] Resolves whether the *engine/DSP* should run in "articulated" strain
// character. Some txType tags are ONLY ever bolted to a 60-foot rig in the real
// fleet chart (B500R, H50EP/any gen, ZH50EP, HDS300, and the artic-only
// accelera_centeraxle / elfa3_centeraxle / fcave130 / d8646art variants) --
// if the resolved tx string matches one of those, we know it's articulated
// regardless of what the isArticulated toggle says, no ambiguity possible.
//
// Voith and ZF are NOT in that list on purpose -- both are shared across
// 35/40/60ft (a 40ft XN40 and a 60ft XN60 can both run plain "voith"), so for
// those we fall back to the explicit isArticulated flag (FleetRosterData
// def, or BusSelectMenu's _selArticulated chip on the Custom tab -- both
// ultimately set the same bool this reads).
public static bool ResolveIsArticulatedEngine(string resolvedTx, bool isArticulatedFlag)
{
    switch (resolvedTx)
    {
        case "b500r":
        case "b500r_g5":
        case "h50ep":
        case "h50ep_gen5":
        case "zh50ep":
        case "hds300":
        case "accelera_centeraxle":
        case "elfa3_centeraxle":
        case "fcave130":
        case "d8646art":
        case "zfel2_hd":
            return true; // tx alone confirms it -- can't be a 40-footer
        default:
            return isArticulatedFlag; // voith/zf/b400r/etc are ambiguous -- trust the flag
    }
}

public string GetTxFor(int indexInSeries)
{
    switch (EffectiveEngineType())
    {
        case BusSimulationController.EngineType.B67:
        case BusSimulationController.EngineType.B72:
            // 6.7L family -- BAE HDS200 always an option alongside H40EP.
            // HDS300 never applies here (that's L9/60ft-adjacent). No D8645
            // or Allison B-series either -- diesel-only variants of these
            // don't exist in this fleet, only hybrid.
            switch (transmission)
            {
                case TransmissionType.H40EP: return "h40ep";
                default:                     return "bae"; // BAE HDS200
            }

        case BusSimulationController.EngineType.XE40:
        case BusSimulationController.EngineType.XE60:
            switch (transmission)
            {
                case TransmissionType.Accelera:
                    return isArticulated ? "accelera_centeraxle" : "accelera";
                case TransmissionType.AcceleraCenterAxle: return "accelera_centeraxle";
                case TransmissionType.ElfaCenterAxle:       return "elfa3_centeraxle";
                default:                                    return "elfa3"; // ELFA 3, direct pick or fallback
            }

        case BusSimulationController.EngineType.XHE40:
        case BusSimulationController.EngineType.XHE60:
            switch (transmission)
            {
                case TransmissionType.Accelera:
                    return isArticulated ? "accelera_fc_centeraxle" : "accelera_fc";
                case TransmissionType.AcceleraCenterAxle: return "accelera_fc_centeraxle";
                case TransmissionType.ElfaCenterAxle:       return "elfa2_centeraxle";
                default:                                    return "elfa2"; // ELFA 2 (fuel-cell), direct pick or fallback
            }

        case BusSimulationController.EngineType.L9N:
            // CNG -- B400R/B500R/ZF/D8646(renamed Voith)/B3400xFE. No hybrids,
            // no D8645 (that's ISL/ISLG/ISL9/X10 territory).
            switch (transmission)
            {
                case TransmissionType.B500R:     return "b500r";
                case TransmissionType.B400RGen5: return "b400r_g5";
                case TransmissionType.B500RGen5: return "b500r_g5";
                case TransmissionType.ZF:        return "zf";
                case TransmissionType.ZFEL2:     return "zfel2";
                case TransmissionType.ZFEL2_HD:  return "zfel2_hd";
                case TransmissionType.Voith:     return "d8646"; // renamed from "voith"
                case TransmissionType.B3400xFE:  return "b3400xfe";
                default:                         return "b400r";
            }

        case BusSimulationController.EngineType.ISLG:
            // [FIX] Allison Gen5 (B400RGen5/B500RGen5) never belonged here --
            // same non-hybrid-diesel-lineup reasoning as ISL/ISL9 below. Falls
            // to the b400r default now instead of returning a Gen5 string.
            switch (transmission)
            {
                case TransmissionType.B500R:     return "b500r";
                case TransmissionType.D8645:     return "d8645";
                default:                         return "b400r";
            }

        case BusSimulationController.EngineType.ISB67:
            // Hybrid-only -- H40EP or BAE HDS200. No D8645, no Allison
            // B-series, no Voith.
            switch (transmission)
            {
                case TransmissionType.H40EP: return "h40ep";
                case TransmissionType.BAE:   return "bae";
                default:                     return "bae";
            }

        case BusSimulationController.EngineType.ISL:
            // Diesel-only, no H40EP/H50EP/ZH50EP hybrid pairing. [ADD] EP40/
            // EP50 ARE valid here though -- real pre-2010 Allison EP-40 unit,
            // confirmed as the XDE40's original launch pairing with ISL9.
            // [FIX] Allison Gen5 (B400RGen5/B500RGen5) never belonged here --
            // those are non-hybrid-diesel-lineup transmissions, not an ISL
            // pairing. Falls to the b400r default now.
            switch (transmission)
            {
                case TransmissionType.B500R:     return "b500r";
                case TransmissionType.D8645:     return "d8645";
                case TransmissionType.ZF:        return "zf";
                case TransmissionType.EP40:      return "ep40";
                case TransmissionType.EP50:      return "ep50";
                default:                         return "b400r";
            }

        case BusSimulationController.EngineType.X10:
            // No hybrid pairings at all, no D8645, ZF/Voith status still
            // UNCONFIRMED against NFI spec -- diesel-only for now.
            switch (transmission)
            {
                case TransmissionType.B500R:     return "b500r";
                case TransmissionType.B400RGen5: return "b400r_g5";
                case TransmissionType.B500RGen5: return "b500r_g5";
                case TransmissionType.B3400xFE:  return "b3400xfe";
                default:                         return "b400r";
            }

        case BusSimulationController.EngineType.ISL9:
            // 280/330 tier -- Allison H40(H40EP only)/H50(H50EP only), BAE
            // HDS200, HDS300, plus D8645 (kept, unlike L9 which uses D8646).
            // [FIX] Allison Gen5 (B400RGen5/B500RGen5) never belonged here
            // either -- same reasoning as ISL/ISLG. EP40/EP50 stay ISL-only
            // (BusAudioEngine redirects them to h40ep/h50ep on ISL9 anyway),
            // so they're deliberately not offered as a distinct pick here.
            switch (transmission)
            {
                case TransmissionType.B500R:     return "b500r";
                case TransmissionType.ZF:        return "zf";
                case TransmissionType.ZFEL2:     return "zfel2";
                case TransmissionType.ZFEL2_HD:  return "zfel2_hd";
                case TransmissionType.H40EP:     return "h40ep";
                case TransmissionType.H50EP:     return "h50ep";
                case TransmissionType.BAE:       return "bae";
                case TransmissionType.HDS300:    return "hds300";
                case TransmissionType.D8645:     return "d8645";
                default:                         return "b400r";
            }

        default: // L9
            // 280/330/360 tier -- Allison H50(H50EP/eGen Flex H50 -- NOT H40,
            // L9 never gets the H40 family), HDS300, D8646 (renamed Voith),
            // B3400xFE. No BAE HDS200 (that's B67/B72/ISB67 only), no ZF
            // (dropped per the final confirmed table -- ZF stays ISL9-only).
            switch (transmission)
            {
                case TransmissionType.B500R:     return "b500r";
                case TransmissionType.B400RGen5: return "b400r_g5";
                case TransmissionType.B500RGen5: return "b500r_g5";
                case TransmissionType.H50EP:     return "h50ep";
                case TransmissionType.HDS300:    return "hds300";
                case TransmissionType.D8645:     return "d8645";
                case TransmissionType.Voith:     return "d8646"; // renamed from "voith"
                case TransmissionType.B3400xFE:  return "b3400xfe";
                case TransmissionType.ZF:        return "zf";
                case TransmissionType.ZFEL2:     return "zfel2";
                case TransmissionType.ZFEL2_HD:  return "zfel2_hd";
                default:                         return "b400r";
            }
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  RATED POWER TIER — how much HP this series' engine is tuned to. Real
//  fleet reality: 280 is the baseline "main engine" tune; articulated
//  60-footers get a hotter tune of the SAME engine. Not every engine has
//  the same tier ladder -- see LegalTiersFor().
// ═══════════════════════════════════════════════════════════════════════
public enum RatedPowerTier { HP280 = 280, HP320 = 320, HP330 = 330, HP360 = 360 }

// Which tiers are legal per engine:
//  - B67/B72/ISB67 (6.7L class) -- locked to 280, no stronger variant exists.
//  - ISL (2007-09, pre-ISL9) -- locked to 280, never got a hotter tune.
//  - L9N/ISLG -- only 280 or 320 (no 330/360 tune exists for these two).
//  - ISL9 -- 280 or 330 (330 is L9-shared, but ISL9 tops out there, no 360).
//  - L9/X10 -- full 280/330/360 ladder.
public static RatedPowerTier[] LegalTiersFor(BusSimulationController.EngineType engineType)
{
    if (engineType == BusSimulationController.EngineType.B67
     || engineType == BusSimulationController.EngineType.B72
     || engineType == BusSimulationController.EngineType.ISB67
     || engineType == BusSimulationController.EngineType.ISL)
        return new[] { RatedPowerTier.HP280 };

    if (engineType == BusSimulationController.EngineType.L9N
     || engineType == BusSimulationController.EngineType.ISLG)
        return new[] { RatedPowerTier.HP280, RatedPowerTier.HP320 };

    if (engineType == BusSimulationController.EngineType.ISL9)
        return new[] { RatedPowerTier.HP280, RatedPowerTier.HP330 };

    if (engineType == BusSimulationController.EngineType.L9
     || engineType == BusSimulationController.EngineType.X10)
        return new[] { RatedPowerTier.HP280, RatedPowerTier.HP330, RatedPowerTier.HP360 };

    return new[] { RatedPowerTier.HP280 }; // electrics/hydrogen -- no HP concept, locked
}

public static RatedPowerTier ResolveRatedPowerTier(
    BusSimulationController.EngineType engineType, bool isArticulatedEngine)
{
    if (!isArticulatedEngine) return RatedPowerTier.HP280;
    var legal = LegalTiersFor(engineType);
    // Default to the second entry (first non-280 option) when one exists.
    return legal.Length > 1 ? legal[1] : legal[0];
}

// Clamps a chosen/customized tier back to something legal for this engine +
// articulation state -- catches an illegal combo (e.g. 360 on L9N, or ANY
// tier above 280 while non-articulated) and falls back instead of breaking.
public static RatedPowerTier ClampToLegalTier(
    BusSimulationController.EngineType engineType, bool isArticulatedEngine, RatedPowerTier requested)
{
    if (!isArticulatedEngine) return RatedPowerTier.HP280;
    var legal = LegalTiersFor(engineType);
    foreach (var t in legal) if (t == requested) return requested;
    return legal[legal.Length - 1];
}

// ═══════════════════════════════════════════════════════════════════════
//  ALLISON H40/H50 FAMILY -- family dropdown, then a variant dropdown only
//  when the family has more than one option for this engine.
//  [UPDATE, supersedes the old note below] EP40/EP50 are NOT shelved --
//  confirmed real: the New Flyer Xcelsior XDE40 originally launched with
//  Cummins ISL9 + Allison EP-40 before Allison's ~2010 rename to H40EP/
//  H50EP (per Wikipedia's Xcelsior article). Implemented as its own
//  TransmissionType (EP40/EP50, see enum above) and offered ONLY on ISL --
//  BusAudioEngine redirects ep40/ep50 to h40ep/h50ep on every other engine
//  (including ISL9), so this dropdown never offers it anywhere but ISL.
//   - ISL9: plain H40EP or H50EP only, no eGen Flex.
//   - B67: BOTH H40EP and eGen Flex H40 -- real two-way choice.
//   - B72: eGen Flex ONLY -- new enough it skipped the H-series entirely.
//   - L9: BOTH H50EP and eGen Flex H50.
// ═══════════════════════════════════════════════════════════════════════
public enum TxFamily { AllisonH40, AllisonH50 }
public enum TxVariant { H40EP, EGenFlexH40, H50EP, EGenFlexH50 }

public static TxVariant[] VariantsFor(TxFamily family, BusSimulationController.EngineType engineType)
{
    if (family == TxFamily.AllisonH40)
    {
        if (engineType == BusSimulationController.EngineType.B72)
            return new[] { TxVariant.EGenFlexH40 };
        if (engineType == BusSimulationController.EngineType.B67)
            return new[] { TxVariant.H40EP, TxVariant.EGenFlexH40 };
        return new[] { TxVariant.H40EP }; // ISB67, ISL9, etc -- legacy only
    }
    if (family == TxFamily.AllisonH50)
    {
        if (engineType == BusSimulationController.EngineType.L9)
            return new[] { TxVariant.H50EP, TxVariant.EGenFlexH50 };
        return new[] { TxVariant.H50EP }; // ISL9, etc -- legacy only
    }
    return System.Array.Empty<TxVariant>();
}

// ═══════════════════════════════════════════════════════════════════════
//  CENTER AXLE TYPE -- simple rule: HP >= 320 -> 132-series axle (portal,
//  low-floor clearance). Below that (280, or electrics which report HP280
//  via LegalTiersFor) -> standard MAN 40ft axle.
// ═══════════════════════════════════════════════════════════════════════
public enum CenterAxleType { MAN40ft, ZF_AVN132_Passive, ZF_AVE130_Driven }

public static CenterAxleType ResolveCenterAxleType(
    BusSimulationController.EngineType engineType, RatedPowerTier tier)
{
    if ((int)tier < 320) return CenterAxleType.MAN40ft;
    bool isElectric = engineType == BusSimulationController.EngineType.XE60
                    || engineType == BusSimulationController.EngineType.XHE60;
    return isElectric ? CenterAxleType.ZF_AVE130_Driven : CenterAxleType.ZF_AVN132_Passive;
}

// ═══════════════════════════════════════════════════════════════════════
//  [ADD] Mirrors GetTxFor()'s switch exactly, one level up -- which
//  TransmissionType enum VALUES are even legal to pick for a given engine.
//  Used by FleetRosterDataEditor so the Inspector's transmission dropdown
//  only shows real options per engine (same job Busselectmenu's compatTX
//  arrays do for the in-game picker), instead of the raw full enum popup
//  letting you set e.g. HDS300 on an X10 that GetTxFor() would just
//  silently fall back away from anyway.
// ═══════════════════════════════════════════════════════════════════════
public static TransmissionType[] LegalTransmissionsFor(BusSimulationController.EngineType engineType)
{
    switch (engineType)
    {
        case BusSimulationController.EngineType.B67:
        case BusSimulationController.EngineType.B72:
            return new[] { TransmissionType.BAE, TransmissionType.H40EP };

        case BusSimulationController.EngineType.XE40:
        case BusSimulationController.EngineType.XE60:
        case BusSimulationController.EngineType.XHE40:
        case BusSimulationController.EngineType.XHE60:
            return new[] { TransmissionType.Accelera, TransmissionType.AcceleraCenterAxle, TransmissionType.ElfaCenterAxle };

        case BusSimulationController.EngineType.L9N:
            return new[] { TransmissionType.B400R, TransmissionType.B500R, TransmissionType.B400RGen5, TransmissionType.B500RGen5, TransmissionType.ZF, TransmissionType.ZFEL2, TransmissionType.ZFEL2_HD, TransmissionType.Voith, TransmissionType.B3400xFE };

        // [FIX] Allison Gen5 (B400RGen5/B500RGen5) removed from ISLG/ISL/ISL9
        // -- non-hybrid-diesel-lineup transmissions, never a real pairing for
        // these three engines.
        case BusSimulationController.EngineType.ISLG:
            return new[] { TransmissionType.B400R, TransmissionType.B500R, TransmissionType.D8645 };

        case BusSimulationController.EngineType.ISB67:
            return new[] { TransmissionType.BAE, TransmissionType.H40EP };

        // [ADD] EP40/EP50 -- real pre-2010 Allison EP-40 unit, ISL-only (see
        // the Allison H40/H50 family header comment above).
        case BusSimulationController.EngineType.ISL:
            return new[] { TransmissionType.B400R, TransmissionType.B500R, TransmissionType.ZF, TransmissionType.D8645, TransmissionType.EP40, TransmissionType.EP50 };

        case BusSimulationController.EngineType.X10:
            return new[] { TransmissionType.B400R, TransmissionType.B500R, TransmissionType.B400RGen5, TransmissionType.B500RGen5, TransmissionType.B3400xFE };

        case BusSimulationController.EngineType.ISL9:
            return new[] { TransmissionType.B400R, TransmissionType.B500R, TransmissionType.ZF, TransmissionType.ZFEL2, TransmissionType.ZFEL2_HD,
                           TransmissionType.H40EP, TransmissionType.H50EP, TransmissionType.BAE,
                           TransmissionType.HDS300, TransmissionType.D8645 };

        default: // L9
            return new[] { TransmissionType.B400R, TransmissionType.B500R, TransmissionType.B400RGen5, TransmissionType.B500RGen5, TransmissionType.H50EP,
                           TransmissionType.HDS300, TransmissionType.Voith, TransmissionType.B3400xFE, TransmissionType.D8645, TransmissionType.ZF, TransmissionType.ZFEL2, TransmissionType.ZFEL2_HD };
    }
}
}

// ═══════════════════════════════════════════════════════════════════════════════
//  POWERTRAIN CATEGORY COLORS — [ADD] mirrors BusSelectMenu's _categoryColor/
//  _engineCategory/_hybridTxSet/GetPowertrainCategory exactly (same hex codes,
//  same "check tx for a real hybrid drivetrain first, fall back to the
//  engine's own base fuel category" rule) so fleet-side logging reads with
//  the same color language the in-game bus picker already uses.
// ═══════════════════════════════════════════════════════════════════════════════
public enum PowertrainCategory { CleanDiesel, CNG, Hybrid, BatteryElectric, HydrogenElectric }

public static class PowertrainCategoryColors
{
    public static readonly Dictionary<PowertrainCategory, Color> CategoryColor = new()
    {
        { PowertrainCategory.CleanDiesel,      new Color(0x34/255f, 0x41/255f, 0x48/255f) }, // Charcoal Blue #344148
        { PowertrainCategory.CNG,              new Color(0x04/255f, 0x4C/255f, 0x99/255f) }, // Steel Azure   #044C99
        { PowertrainCategory.Hybrid,           new Color(0x25/255f, 0xB3/255f, 0xBB/255f) }, // Tropical Teal #25B3BB
        { PowertrainCategory.BatteryElectric,  new Color(0x64/255f, 0xE6/255f, 0xA3/255f) }, // Tropical Mint #64E6A3
        { PowertrainCategory.HydrogenElectric, new Color(0xFF/255f, 0xD0/255f, 0x44/255f) }, // Golden Pollen #FFD044
    };

    // Hex string form, for Unity rich-text <color=#RRGGBB> tags in Debug.Log.
    public static readonly Dictionary<PowertrainCategory, string> CategoryHex = new()
    {
        { PowertrainCategory.CleanDiesel,      "344148" },
        { PowertrainCategory.CNG,              "044C99" },
        { PowertrainCategory.Hybrid,            "25B3BB" },
        { PowertrainCategory.BatteryElectric,  "64E6A3" },
        { PowertrainCategory.HydrogenElectric, "FFD044" },
    };

    private static readonly Dictionary<BusSimulationController.EngineType, PowertrainCategory> _engineCategory = new()
    {
        { BusSimulationController.EngineType.L9,    PowertrainCategory.CleanDiesel },
        { BusSimulationController.EngineType.X10,   PowertrainCategory.CleanDiesel },
        { BusSimulationController.EngineType.ISL9,  PowertrainCategory.CleanDiesel },
        { BusSimulationController.EngineType.ISL,   PowertrainCategory.CleanDiesel },
        { BusSimulationController.EngineType.ISB67, PowertrainCategory.Hybrid },
        { BusSimulationController.EngineType.L9N,   PowertrainCategory.CNG },
        { BusSimulationController.EngineType.ISLG,  PowertrainCategory.CNG },
        { BusSimulationController.EngineType.B67,   PowertrainCategory.Hybrid },
        { BusSimulationController.EngineType.B72,   PowertrainCategory.Hybrid },
        { BusSimulationController.EngineType.XE40,  PowertrainCategory.BatteryElectric },
        { BusSimulationController.EngineType.XE60,  PowertrainCategory.BatteryElectric },
        { BusSimulationController.EngineType.XHE40, PowertrainCategory.HydrogenElectric },
        { BusSimulationController.EngineType.XHE60, PowertrainCategory.HydrogenElectric },
    };

    // Same real hybrid-tx set as BusSelectMenu -- an L9/ISL9 running one of
    // these reads as Hybrid-category even though its base category is Clean
    // Diesel.
    // [ADD] "ep40"/"ep50" -- real pre-2010 Allison hybrid unit, same category
    // treatment as h40ep/h50ep.
    private static readonly HashSet<string> _hybridTxSet = new()
    {
        "bae", "hds300", "baegen3", "h40ep", "h50ep", "h50ep_gen5", "zh50ep", "ep40", "ep50", "egenflex40", "egenflex50"
    };

    public static PowertrainCategory GetCategory(BusSimulationController.EngineType eng, string tx) =>
        !string.IsNullOrEmpty(tx) && _hybridTxSet.Contains(tx)
            ? PowertrainCategory.Hybrid
            : (_engineCategory.TryGetValue(eng, out var cat) ? cat : PowertrainCategory.CleanDiesel);

    public static string Hex(BusSimulationController.EngineType eng, string tx) =>
        CategoryHex.TryGetValue(GetCategory(eng, tx), out var hex) ? hex : "FFFFFF";
}

// ═══════════════════════════════════════════════════════════════════════════════
//  FLEET ROSTER DATA  —  ScriptableObject asset listing all series in the fleet
// ═══════════════════════════════════════════════════════════════════════════════
[CreateAssetMenu(menuName = "Transit/Fleet Roster", fileName = "FleetRoster")]
public class FleetRosterData : ScriptableObject
{
    [Header("Agency")]
    [Tooltip("e.g. 'CBT' or 'SCT'. Used only when merging multiple datasets " +
             "(see MergeInto()) so a series can still be traced back to its source " +
             "asset after merging -- has no effect when this asset is used standalone.")]
    public string agencyTag = "";

    [Header("Series")]
    public List<FleetSeriesDefinition> series = new();

    // [ADD] Supports multiple fleet roster datasets (e.g. a separate CBT asset
    // and SCT/Gillig asset) without touching any of the ~15 call sites in
    // Busselectmenu.cs that index into `fleetRoster.series[...]`. Instead of
    // rewiring those, BusSelectMenu builds ONE runtime-merged FleetRosterData
    // instance from however many source assets are assigned, and every existing
    // access pattern keeps working unmodified against that merged instance.
    public static FleetRosterData MergeInto(FleetRosterData target, IList<FleetRosterData> sources)
    {
        if (target == null) target = ScriptableObject.CreateInstance<FleetRosterData>();
        target.series.Clear();
        if (sources == null) return target;
        foreach (var src in sources)
        {
            if (src == null || src.series == null) continue;
            foreach (var def in src.series)
            {
                if (def == null) continue;
                // Stamp the source agency onto each definition as it's merged in,
                // so downstream code (spawner/scheduler/audio) can still tell a
                // Gillig/SCT series apart from a CBT one after merging.
                def.sourceAgencyTag = src.agencyTag;
                target.series.Add(def);
            }
        }
        return target;
    }

private void LogSeriesSummary()
{
    Debug.Log("========== FLEET SERIES SUMMARY ==========");

    foreach (var def in series)
    {
        if (def == null) continue;

        int totalCount = (def.depotAllocations != null && def.depotAllocations.Count > 0)
            ? SumAllocationCounts(def.depotAllocations)
            : def.busCount;

        int endFleetNumber = def.startFleetNumber + Mathf.Max(totalCount, 1) - 1;
        string numberRange = totalCount <= 1
            ? $"{def.startFleetNumber}"
            : $"{def.startFleetNumber}-{endFleetNumber}";

        string txString = def.GetTxFor(0);
        string body      = def.isArticulated ? "Articulated" : "Standard";
        // [ADD] Color-code the series line by powertrain category -- same
        // hex codes BusSelectMenu uses for its chip accents, so console
        // output and in-game picker read the same visual language.
        string hex = PowertrainCategoryColors.Hex(def.engineType, txString);

        Debug.Log($"<color=#{hex}>{def.seriesName}</color>  |  #{numberRange} ({totalCount} buses)  |  {def.busType} ({body})  |  " +
                  $"Engine: {def.engineType}  |  TX: {txString}  |  MY {def.modelYear}");
    }

    Debug.Log("============================================");
}

private static int SumAllocationCounts(List<DepotAllocation> allocations)
{
    int sum = 0;
    foreach (var a in allocations)
        if (a != null && a.busCount > 0) sum += a.busCount;
    return sum;
}

private void LogDepotAssignments(List<BusSlot> slots)
{
    var depots = new Dictionary<string, List<int>>();

    foreach (var slot in slots)
    {
        var meta = FleetMetadata.Get(slot.fleetNumber);
        if (meta == null)
            continue;

        string depotName = meta.homeDepot != null
            ? meta.homeDepot.depotName
            : "(No Depot)";

        if (!depots.TryGetValue(depotName, out var list))
        {
            list = new List<int>();
            depots[depotName] = list;
        }

        list.Add(slot.fleetNumber);
    }

    Debug.Log("========== FLEET DEPOT ASSIGNMENTS ==========");

    foreach (var kvp in depots)
    {
        kvp.Value.Sort();

        var ranges = new List<string>();

        int start = kvp.Value[0];
        int end = start;

        for (int i = 1; i < kvp.Value.Count; i++)
        {
            if (kvp.Value[i] == end + 1)
            {
                end = kvp.Value[i];
            }
            else
            {
                ranges.Add(start == end ? $"{start}" : $"{start}-{end}");
                start = end = kvp.Value[i];
            }
        }

        ranges.Add(start == end ? $"{start}" : $"{start}-{end}");

        Debug.Log($"{kvp.Key}: {string.Join(", ", ranges)}");
    }

    Debug.Log("============================================");
}

    /// <summary>[ADD] One-time diagnostic for the board RenderTexture
    /// investigation -- logs two things side by side so a mismatch is
    /// immediately visible instead of guessed at from a Memory Profiler
    /// count alone:
    ///
    ///   1. PREFAB-DECLARED: for each series, which board types its own
    ///      prefab actually carries (the "should exist" answer, works
    ///      whether or not you're in Play Mode).
    ///   2. LIVE INSTANCE COUNT: for each series, how many of its
    ///      currently-spawned buses actually carry each board type RIGHT
    ///      NOW (the "does exist" answer -- only available in Play Mode,
    ///      since it reads BusRegistry.ActiveBuses).
    ///
    /// Any board type whose live count doesn't match (series bus count ×
    /// prefab says yes/no) is flagged as a MISMATCH. BusDriverLCDBoard is
    /// called out specially -- no series prefab should ever declare it (see
    /// its own header comment: player-only, dynamically attached on
    /// possession), so ANY live instance of it at all is inherently
    /// suspicious and gets logged individually with the bus/fleet number
    /// carrying it, so you can go find that exact GameObject in the
    /// Hierarchy rather than just knowing a number is high somewhere.</summary>
    [ContextMenu("Census: Board Types (Prefab-Declared vs Live Instances)")]
    private void CensusBoardTypes()
    {
        Debug.Log("========== BOARD CENSUS ==========");

        // ── 1. Prefab-declared, per series ──────────────────────────────
        var declaredLCD       = new Dictionary<string, bool>();
        var declaredDest      = new Dictionary<string, bool>();
        var declaredRouteNum  = new Dictionary<string, bool>();
        var declaredScroll    = new Dictionary<string, bool>();
        var declaredDriverLCD = new Dictionary<string, bool>();

        foreach (var def in series)
        {
            if (def == null) continue;
            string key = def.seriesName;

            if (def.prefab == null)
            {
                Debug.LogWarning($"[BoardCensus] Series '{key}' has no prefab -- skipped.");
                continue;
            }

            bool hasLCD       = def.prefab.GetComponentInChildren<BusInteriorLCDBoard>(true)       != null;
            bool hasDest      = def.prefab.GetComponentInChildren<BusInteriorDestinationSign>(true) != null;
            bool hasRouteNum  = def.prefab.GetComponentInChildren<BusInteriorRouteNumberSign>(true) != null;
            bool hasScroll    = def.prefab.GetComponentInChildren<BusInteriorScrollBoard>(true)     != null;
            bool hasDriverLCD = def.prefab.GetComponentInChildren<BusDriverLCDBoard>(true)          != null;

            declaredLCD[key]       = hasLCD;
            declaredDest[key]      = hasDest;
            declaredRouteNum[key]  = hasRouteNum;
            declaredScroll[key]    = hasScroll;
            declaredDriverLCD[key] = hasDriverLCD;

            Debug.Log($"[Prefab] {key} (#{def.startFleetNumber}-{def.startFleetNumber + def.busCount - 1}, {def.busCount} buses): "
                     + $"LCD={hasLCD}  Dest={hasDest}  RouteNum={hasRouteNum}  Scroll={hasScroll}  DriverLCD={hasDriverLCD}"
                     + (hasDriverLCD ? "  ⚠ DriverLCDBoard should NEVER be on a prefab -- it's meant to be added dynamically to the possessed bus only." : ""));
        }

        // ── 2. Live instance counts, per series, per bus ────────────────
        if (!Application.isPlaying)
        {
            Debug.Log("[BoardCensus] Not in Play Mode -- skipping live-instance half (needs BusRegistry.ActiveBuses). Run this again while playing to get the actual leak numbers.");
            Debug.Log("===================================");
            return;
        }

        var liveLCD       = new Dictionary<string, int>();
        var liveDest      = new Dictionary<string, int>();
        var liveRouteNum  = new Dictionary<string, int>();
        var liveScroll    = new Dictionary<string, int>();
        var liveDriverLCD = new Dictionary<string, int>();
        var liveBusCount  = new Dictionary<string, int>();
        var driverLCDOffenders = new List<string>();

        foreach (var kv in BusRegistry.ActiveBuses)
        {
            var npc = kv.Value;
            if (npc == null) continue;

            // Find which series this fleet number belongs to.
            string seriesKey = null;
            foreach (var def in series)
            {
                if (def == null) continue;
                if (npc.fleetNumber >= def.startFleetNumber && npc.fleetNumber < def.startFleetNumber + def.busCount)
                {
                    seriesKey = def.seriesName;
                    break;
                }
            }
            seriesKey ??= "(unmatched fleet number)";

            liveBusCount.TryGetValue(seriesKey, out int bc); liveBusCount[seriesKey] = bc + 1;

            int lcdN       = npc.GetComponentsInChildren<BusInteriorLCDBoard>(true).Length;
            int destN      = npc.GetComponentsInChildren<BusInteriorDestinationSign>(true).Length;
            int routeNumN  = npc.GetComponentsInChildren<BusInteriorRouteNumberSign>(true).Length;
            int scrollN    = npc.GetComponentsInChildren<BusInteriorScrollBoard>(true).Length;
            int driverLCDN = npc.GetComponentsInChildren<BusDriverLCDBoard>(true).Length;

            liveLCD.TryGetValue(seriesKey, out int a);       liveLCD[seriesKey]       = a + lcdN;
            liveDest.TryGetValue(seriesKey, out int b);      liveDest[seriesKey]      = b + destN;
            liveRouteNum.TryGetValue(seriesKey, out int c);  liveRouteNum[seriesKey]  = c + routeNumN;
            liveScroll.TryGetValue(seriesKey, out int d);    liveScroll[seriesKey]    = d + scrollN;
            liveDriverLCD.TryGetValue(seriesKey, out int e); liveDriverLCD[seriesKey] = e + driverLCDN;

            // BusDriverLCDBoard should exist on AT MOST ONE bus in the whole
            // fleet at any given moment (whichever one is currently
            // possessed) -- log every single instance found, individually,
            // so you can go straight to that GameObject instead of just
            // knowing a total count is too high.
            if (driverLCDN > 0)
                driverLCDOffenders.Add($"Bus#{npc.busID} (fleet #{npc.fleetNumber}, {seriesKey}) carries {driverLCDN} BusDriverLCDBoard instance(s)");
        }

        Debug.Log("---- LIVE INSTANCE COUNTS (per series) ----");
        foreach (var def in series)
        {
            if (def == null) continue;
            string key = def.seriesName;
            liveBusCount.TryGetValue(key, out int spawned);
            liveLCD.TryGetValue(key, out int lcdCount);
            liveDest.TryGetValue(key, out int destCount);
            liveRouteNum.TryGetValue(key, out int routeNumCount);
            liveScroll.TryGetValue(key, out int scrollCount);
            liveDriverLCD.TryGetValue(key, out int driverLCDCount);

            declaredLCD.TryGetValue(key, out bool expLCD);
            declaredDest.TryGetValue(key, out bool expDest);
            declaredRouteNum.TryGetValue(key, out bool expRouteNum);
            declaredScroll.TryGetValue(key, out bool expScroll);

            int expectedLCD      = expLCD      ? spawned : 0;
            int expectedDest     = expDest     ? spawned : 0;
            int expectedRouteNum = expRouteNum ? spawned : 0;
            int expectedScroll   = expScroll   ? spawned : 0;

            string mismatch = "";
            if (lcdCount != expectedLCD)           mismatch += $" ⚠ LCD expected {expectedLCD}, got {lcdCount}.";
            if (destCount != expectedDest)         mismatch += $" ⚠ Dest expected {expectedDest}, got {destCount}.";
            if (routeNumCount != expectedRouteNum) mismatch += $" ⚠ RouteNum expected {expectedRouteNum}, got {routeNumCount}.";
            if (scrollCount != expectedScroll)     mismatch += $" ⚠ Scroll expected {expectedScroll}, got {scrollCount}.";
            if (driverLCDCount > 0)                mismatch += $" ⚠ DriverLCD should be 0 unless one of these is currently possessed, got {driverLCDCount}.";

            Debug.Log($"[Live] {key}: {spawned} spawned  |  LCD={lcdCount}  Dest={destCount}  RouteNum={routeNumCount}  Scroll={scrollCount}  DriverLCD={driverLCDCount}"
                     + (string.IsNullOrEmpty(mismatch) ? "" : $"   MISMATCH:{mismatch}"));
        }

        if (driverLCDOffenders.Count > 0)
        {
            Debug.LogWarning($"---- {driverLCDOffenders.Count} BusDriverLCDBoard instance(s) found across the live fleet (expected at most 1, on the possessed bus) ----");
            foreach (var line in driverLCDOffenders)
                Debug.LogWarning("  " + line);
        }

        Debug.Log("===================================");
    }
#if UNITY_EDITOR
    /// <summary>Formats an absolute minute-of-day as HH:MM. Local copy —
    /// BusScheduler has its own, but this check needs to run without a
    /// BusScheduler in the scene at all.</summary>
    private static string MinToHHMM(float minuteOfDay)
    {
        int m = Mathf.RoundToInt(minuteOfDay) % 1440;
        if (m < 0) m += 1440;
        return $"{m / 60:00}:{m % 60:00}";
    }

    /// <summary>
    /// PREDICTIVE check — doesn't look at any runtime schedule or "today's"
    /// slots at all. Asks a structural question instead: given the buses
    /// this roster actually produces (series/fleet numbers/policy flags)
    /// and every route's own eligibility rules + schedule windows, is there
    /// ANY combination of overlapping routes whose combined concurrent-bus
    /// requirement exceeds the pool of buses eligible to serve both? If so,
    /// double-booking isn't a bug that MIGHT happen at runtime — it's
    /// mathematically guaranteed to happen on some day, regardless of how
    /// good BusScheduler's conflict-avoidance logic is, because there simply
    /// aren't enough eligible buses to go around during that window.
    ///
    /// This is a NECESSARY-condition check (combined demand vs. shared pool
    /// size), not a full bipartite-matching proof — it can under-report in
    /// exotic 3+-route interlocking cases, but any conflict it DOES report
    /// is real: no scheduling algorithm can avoid it, only a bigger fleet,
    /// a tighter eligibility policy, or a schedule change can.
    /// </summary>
    [ContextMenu("Check For Structural Double-Booking Risk (Series + Schedule)")]
    private void CheckStructuralDoubleBookingRisk()
    {
        // FleetMetadata (home depot, articulated, 35ft, etc.) is what
        // route.IsBusAllowed() reads. BuildSlots() is what populates it, so
        // this runs it fresh here rather than requiring the game to be in
        // Play Mode first — this check is meant to be run from the editor,
        // on the fleet as authored, not on live game state.
        BuildSlots();

        string[] guids = UnityEditor.AssetDatabase.FindAssets("t:BusRouteData");
        var routes = new List<BusRouteData>();
        foreach (var guid in guids)
        {
            var r = UnityEditor.AssetDatabase.LoadAssetAtPath<BusRouteData>(UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
            if (r != null) routes.Add(r);
        }

        // Every fleet number THIS roster actually produces — buses that
        // exist, not theoretical series ranges.
        var allFleetNumbers = new List<int>();
        foreach (var def in series)
        {
            if (def == null) continue;
            for (int i = 0; i < def.busCount; i++) allFleetNumbers.Add(def.startFleetNumber + i);
        }

        // Per-window demand: one entry per route (or per schedule window,
        // for a route with time-of-day windows), each carrying its own
        // required-concurrent count and its own eligible-bus set.
        var demand = new List<(BusRouteData route, float start, float end, int required, HashSet<int> eligible)>();

        foreach (var route in routes)
        {
            if (route == null) continue;

            var eligible = new HashSet<int>();
            foreach (int fn in allFleetNumbers)
                if (route.IsBusAllowed(fn)) eligible.Add(fn);

            if (route.UsesTimeOfDayWindows)
            {
                foreach (var w in route.scheduleWindows)
                {
                    float headway = Mathf.Min(w.headwayFromAMinutes, w.headwayFromZMinutes);
                    int required = Mathf.CeilToInt(route.CycleTimeMinutes / Mathf.Max(0.1f, headway));
                    demand.Add((route, w.windowStartMinutes, w.windowEndMinutes, required, eligible));
                }
            }
            else
            {
                demand.Add((route, route.operatingStartMinutes, route.operatingEndMinutes, route.RequiredBusCount, eligible));
            }
        }

        bool anyIssue = false;
        var lines = new List<string> { "╔══════ STRUCTURAL DOUBLE-BOOKING RISK CHECK ══════╗" };
        lines.Add($"  │ {routes.Count} route(s) checked against {allFleetNumbers.Count} bus(es) in this roster.");

        // (1) A route that can't be fully staffed even completely on its
        // own — not a double-booking per se, but the same root cause: not
        // enough eligible buses for what the schedule demands.
        foreach (var d in demand)
        {
            if (d.eligible.Count < d.required)
            {
                anyIssue = true;
                lines.Add($"  │ ✗ Route {d.route.routeNumber} ({MinToHHMM(d.start)}-{MinToHHMM(d.end)}): " +
                          $"needs {d.required} concurrent bus(es) but only {d.eligible.Count} eligible bus(es) exist in the whole roster.");
            }
        }

        // (2) Pairwise overlap: two different routes whose service windows
        // overlap in time, checked against the SHARED (intersected) eligible
        // pool — only buses eligible for BOTH are actually contended for.
        // If their combined ask exceeds that shared pool, at least one of
        // them WILL come up short during the overlap on some day — no
        // amount of scheduler cleverness avoids it, only more/different
        // buses or a schedule change does.
        for (int i = 0; i < demand.Count; i++)
        {
            for (int j = i + 1; j < demand.Count; j++)
            {
                var a = demand[i]; var b = demand[j];
                if (a.route == b.route) continue; // same route's own windows never contend with each other
                if (a.start >= b.end || b.start >= a.end) continue; // no time overlap

                var shared = new HashSet<int>(a.eligible);
                shared.IntersectWith(b.eligible);
                if (shared.Count == 0) continue; // no bus is eligible for both — genuinely impossible for them to contend

                int combinedNeed = a.required + b.required;
                if (combinedNeed > shared.Count)
                {
                    anyIssue = true;
                    lines.Add($"  │ ⚠ Route {a.route.routeNumber} & Route {b.route.routeNumber} overlap " +
                              $"{MinToHHMM(Mathf.Max(a.start, b.start))}-{MinToHHMM(Mathf.Min(a.end, b.end))}: " +
                              $"need {a.required}+{b.required}={combinedNeed} concurrent bus(es) but only {shared.Count} bus(es) " +
                              $"are eligible for both routes — structural double-booking risk.");
                }
            }
        }

        if (!anyIssue) lines.Add("  │ ✓ No structural conflicts — this fleet CAN theoretically cover every route's schedule without double-booking.");
        lines.Add("  └──────────────────────────────────────────────────────");
        Debug.LogWarning(string.Join("\n", lines));
    }
#endif

    public List<BusSlot> BuildSlots()
    {
        var slots = new List<BusSlot>();

        foreach (var def in series)
        {
            if (def.prefab == null)
            {
                Debug.LogWarning($"[FleetRoster] Series '{def.seriesName}' has no prefab — skipped.");
                continue;
            }
int fleetNum = def.startFleetNumber;

if (def.depotAllocations != null && def.depotAllocations.Count > 0)
{
    foreach (var allocation in def.depotAllocations)
    {
        if (allocation == null || allocation.busCount <= 0)
            continue;

        for (int i = 0; i < allocation.busCount; i++)
        {
            var slot = new BusSlot
            {
                fleetNumber = fleetNum,
                busLabel = $"{def.seriesName} #{fleetNum}",
                prefab = def.prefab,
                allowedEngines = new List<EngineConfig>
                {
new EngineConfig
{
    engineType = def.EffectiveEngineType(), // [FIX] use the isArticulated-corrected type, not the raw dropdown value
    txType = def.GetTxFor(fleetNum - def.startFleetNumber),
    oldBusVariant = def.GetOldBusVariant(),
    diwaOpt1_1 = def.GetDiwaVoiceFor(fleetNum).opt1_1,
    diwaOpt1_2 = def.GetDiwaVoiceFor(fleetNum).opt1_2,
    diwaOpt1_3 = def.GetDiwaVoiceFor(fleetNum).opt1_3,
    diwaOpt1_4 = def.GetDiwaVoiceFor(fleetNum).opt1_4,
    diwaOpt1_5 = def.GetDiwaVoiceFor(fleetNum).opt1_5,
    diwaOpt1_6 = def.GetDiwaVoiceFor(fleetNum).opt1_6,
    diwaOpt1_7 = def.GetDiwaVoiceFor(fleetNum).opt1_7,
    diwaOpt1_8 = def.GetDiwaVoiceFor(fleetNum).opt1_8
}
                }
            };

            slots.Add(slot);

            FleetMetadata.Register(fleetNum, new FleetMetadata.Entry
            {
                busType = def.busType,
                isArticulated = def.isArticulated,
                is35Ft = def.IsMini35(),
                passengerCapacity = def.PassengerCapacity,
                homeDepot = allocation.depot,
                modelYear = def.modelYear,
                seriesName = def.seriesName,
                condition = def.randomizeCondition
                    ? (BusCondition)Random.Range(0, 4)
                    : def.defaultCondition,
                fuelType = def.fuelType,
                isHybridAssist = def.isHybridAssist,
                hasWheelCovers = def.enableWheelCovers,
                hasBikeRack = def.enableBikeRack,
                hasWheelchairRamp = def.enableWheelchairRamp
            });

            fleetNum++;
        }
    }
}
else
{
    for (int i = 0; i < def.busCount; i++)
    {
        int currentFleet = def.startFleetNumber + i;

        var slot = new BusSlot
        {
            fleetNumber = currentFleet,
            busLabel = $"{def.seriesName} #{currentFleet}",
            prefab = def.prefab,
            allowedEngines = new List<EngineConfig>
            {
new EngineConfig
{
    engineType = def.EffectiveEngineType(), // [FIX] use the isArticulated-corrected type, not the raw dropdown value
    txType = def.GetTxFor(fleetNum - def.startFleetNumber),
    oldBusVariant = def.GetOldBusVariant(),
    diwaOpt1_1 = def.GetDiwaVoiceFor(currentFleet).opt1_1,
    diwaOpt1_2 = def.GetDiwaVoiceFor(currentFleet).opt1_2,
    diwaOpt1_3 = def.GetDiwaVoiceFor(currentFleet).opt1_3,
    diwaOpt1_4 = def.GetDiwaVoiceFor(currentFleet).opt1_4,
    diwaOpt1_5 = def.GetDiwaVoiceFor(currentFleet).opt1_5,
    diwaOpt1_6 = def.GetDiwaVoiceFor(currentFleet).opt1_6,
    diwaOpt1_7 = def.GetDiwaVoiceFor(currentFleet).opt1_7,
    diwaOpt1_8 = def.GetDiwaVoiceFor(currentFleet).opt1_8
}
            }
        };

        slots.Add(slot);

        FleetMetadata.Register(currentFleet, new FleetMetadata.Entry
        {
            busType = def.busType,
            isArticulated = def.isArticulated,
            is35Ft = def.IsMini35(),
            passengerCapacity = def.PassengerCapacity,
            homeDepot = def.homeDepot,
            modelYear = def.modelYear,
            seriesName = def.seriesName,
            condition = def.randomizeCondition
                ? (BusCondition)Random.Range(0, 4)
                : def.defaultCondition,
            fuelType = def.fuelType,
            isHybridAssist = def.isHybridAssist,
            hasWheelCovers = def.enableWheelCovers,
            hasBikeRack = def.enableBikeRack,
            hasWheelchairRamp = def.enableWheelchairRamp
        });
    }
}
        }
LogDepotAssignments(slots);
LogSeriesSummary();
        return slots;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  FLEET METADATA
// ═══════════════════════════════════════════════════════════════════════════════
/// <summary>Default seating / standing capacity by body length (New Flyer Xcelsior published figures:
/// 35ft 32+33, 40ft 40+43, 60ft 61+62). Fuel type (XD diesel vs XN CNG) doesn't change these.</summary>
public static class BusCapacityDefaults
{
    public const int Standard40Total = 83;

    public static int Seated(bool articulated, bool mini35)   => articulated ? 61 : mini35 ? 32 : 40;
    public static int Standing(bool articulated, bool mini35) => articulated ? 62 : mini35 ? 33 : 43;
}

public static class FleetMetadata
{
    public class Entry
    {
        /// <summary>Seated + standing capacity for this bus's series (see FleetSeriesDefinition.PassengerCapacity).</summary>
        public int       passengerCapacity;
        public string    busType;
        public bool      isArticulated;
        public bool      is35Ft;
        public DepotData homeDepot;
        public int       modelYear;
        public string    seriesName;
        public BusCondition condition;
        // [ADD] Was completely missing -- BusCondition already existed and
        // was series-driven, but only ever touched cosmetic old-bus AUDIO
        // flags (ApplyConditionPreset). It never actually reached
        // BusVehicleSystem's real numeric PartWear values, which is what
        // BusBreakdownSystem's condition-weighted roll actually reads. So a
        // 17-year-old 1000 Series bus and a fresh 2300 Series bus started
        // with IDENTICAL breakdown odds despite one being flagged
        // "Neglected" and the other "Pristine" in the roster data the whole
        // time. Fixed via ConditionToNumeric below.
        public BusVehicleSystem.FuelSystemType fuelType;
        public bool isHybridAssist;
        [Header("Advertising")]
        public AdPackage adPackage;
        public AdData ad;
        public bool hasWheelCovers;
    public bool hasBikeRack;
    public bool hasWheelchairRamp;
    }

    /// <summary>Maps the existing qualitative BusCondition tiers onto the
    /// numeric 0-100 scale BusVehicleSystem's PartWear actually uses. This is
    /// the missing link -- BusCondition existed, PartWear existed, nothing
    /// ever connected them.</summary>
    public static float ConditionToNumeric(BusCondition c) => c switch
    {
        BusCondition.Pristine   => 100f,
        BusCondition.Standard   => 85f,
        BusCondition.Worn       => 55f,
        BusCondition.Neglected  => 25f,
        _ => 100f
    };

    private static readonly Dictionary<int, Entry> _byFleetNumber = new();

    /// <summary>Passenger capacity for a fleet number; a standard 40ft figure if the bus isn't registered
    /// (custom / unknown buses) so displays never divide by zero.</summary>
    public static int CapacityFor(int fleetNumber)
    {
        var e = Get(fleetNumber);
        return e != null && e.passengerCapacity > 0 ? e.passengerCapacity : BusCapacityDefaults.Standard40Total;
    }

    public static void Register(int fleetNumber, Entry entry) => _byFleetNumber[fleetNumber] = entry;
    public static Entry Get(int fleetNumber) =>
        _byFleetNumber.TryGetValue(fleetNumber, out var e) ? e : null;
    public static void Clear() => _byFleetNumber.Clear();
}