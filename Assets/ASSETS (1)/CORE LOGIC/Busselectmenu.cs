using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS SELECT MENU  vA2.1  —  (BS.BSM.HF v10)
//
//  FIX vs v5: both possession paths now go through PlayerHandoff's fixed
//  PLAYER_BUS_ID identity instead of assuming AdoptInServiceSlot would fix
//  things up. Fleet possession still calls AdoptInServiceSlot (adopts the
//  NPC's in-progress slot). Custom possession now calls
//  ResetForFreshPossession so playerBus/FleetNumber are always in a known
//  state, even though there's no NPC slot to adopt.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusSelectMenu : MonoBehaviour
{
    [Header("Fleet Roster")]
    [Tooltip("Legacy single-dataset field. If additionalFleetRosters is empty, " +
             "this asset is used exactly as before -- fully backward compatible. " +
             "If additionalFleetRosters has entries, this field gets REPLACED at " +
             "Awake() with a runtime-merged instance combining this asset (if set) " +
             "plus every entry in additionalFleetRosters, in list order. Every " +
             "existing fleetRoster.series[...] access elsewhere in this file keeps " +
             "working unmodified against the merged result.")]
    public FleetRosterData fleetRoster;

    [Tooltip("Extra fleet roster datasets to merge in alongside (or instead of) " +
             "fleetRoster above -- e.g. a separate SCT/Gillig roster asset kept " +
             "apart from the main CBT one. Leave empty for single-dataset mode.")]
    public List<FleetRosterData> additionalFleetRosters = new();

    public Transform       spawnPoint;

    [Header("Custom Bus Pool")]
    public List<GameObject> busPool = new List<GameObject>();

    [Header("Preview Camera")]
    public Camera previewCamera;
    public int    previewResW = 320;
    public int    previewResH = 200;

    [Header("Camera")]
    public Camera          mainCamera;
    public MonoBehaviour   cameraFollowScript;

    [Header("Audio")]
    public float honkDurationSec = 1.2f;

    [Header("Panel")]
    [Range(500, 1000)] public int panelW = 760;
    [Range(400,  700)] public int panelH = 560;
    [Header("UI")]
    public BusDashboardHUD dashboardHUD;

    // [REBUILT] Every list here now mirrors FleetRosterData.GetTxFor()'s
    // switch exactly -- this dictionary had drifted badly out of sync with
    // that file (offering removed tx like d8646art/zh50ep/h50ep_gen5/bare
    // zfave130/voith35/nxt/baegen3, and missing the eGen Flex additions).
    // See FleetRosterData.LegalTransmissionsFor() for the same list kept in
    // sync on the editor side.
    private static readonly Dictionary<BusAudioEngine.EngineType, string[]> _compatTX =
        new()
        {
            // L9N: CNG. B400R default, B500R, ZF, "d8646" (renamed from
            // "voith"), B3400xFE. No hybrids, no D8645 (that's ISL/ISLG/
            // ISL9/X10 territory only).
            // [ADD] b400r_g5/b500r_g5 -- real Allison Gen 4/Gen 5 split
            // (Allison's own "5th Generation Controls" terminology, 2017+).
            // Same physical transmission family as b400r/b500r, so offered
            // everywhere those are.
            // [REORDER] DIWA (d8646) moved up next to the Allison/B3400xFE
            // block instead of trailing after ZF -- it's a primary mechanical
            // option here, not an afterthought.
            { BusAudioEngine.EngineType.L9N,  new[] { "b400r", "b500r", "b400r_g5", "b500r_g5", "b3400xfe", "d8646", "zf", "zfel2", "zfel2_hd" } },
            // L9: 280/330/360 tier. B400R default, B500R, Allison H50 family
            // (h50ep / egenflex50 -- NOT h40, L9 never gets the H40 family),
            // HDS300, "d8646", B3400xFE, ZF (gen1 + EcoLife 2). No BAE
            // HDS200 (B67/B72/ISB67 only), no D8645 (that's ISL9's, not L9's).
            // [REORDER] Same DIWA-near-the-top fix as L9N above -- d8646 now
            // sits right after the Allison/B3400xFE block instead of at the
            // very end.
            { BusAudioEngine.EngineType.L9,   new[] { "b400r", "b500r", "b400r_g5", "b500r_g5", "b3400xfe", "d8646", "zf", "zfel2", "zfel2_hd", "h50ep", "egenflex50", "hds300" } },
            // B67: 6.7L hybrid, locked 280hp. BAE HDS200 default, Allison H40
            // family (h40ep / egenflex40 -- both, real two-way choice). No
            // HDS300 (that's L9-adjacent), no baegen3 (not part of the real
            // fleet's tx system).
            { BusAudioEngine.EngineType.B67,  new[] { "bae", "h40ep", "egenflex40" } },
            // B72: locked 280hp, eGen Flex ONLY -- new enough it skipped the
            // H-series entirely. BAE HDS200 still the default combustion-
            // adjacent APU voice; egenflex40 is the only Allison H40 variant
            // offered.
            { BusAudioEngine.EngineType.B72,  new[] { "bae", "egenflex40" } },
            // Battery-Electric: Siemens ELFA 3 / Accelera NextGen, each with
            // a direct-drive (std body) and centre-axle (artic) variant. Bare
            // "zfave130" removed entirely -- a portal axle with no drive
            // system attached never made sense as a standalone pick.
            { BusAudioEngine.EngineType.XE40, new[] { "elfa3", "accelera", "elfa3_centeraxle", "accelera_centeraxle" } },
            { BusAudioEngine.EngineType.XE60, new[] { "elfa3", "accelera", "elfa3_centeraxle", "accelera_centeraxle" } },
            // Hydrogen Fuel Cell-Electric: same shape, Siemens ELFA 2 /
            // Accelera NextGen FC. Bare "fcave130" removed for the same
            // reason as zfave130 above.
            { BusAudioEngine.EngineType.XHE40, new[] { "elfa2", "accelera_fc", "elfa2_centeraxle", "accelera_fc_centeraxle" } },
            { BusAudioEngine.EngineType.XHE60, new[] { "elfa2", "accelera_fc", "elfa2_centeraxle", "accelera_fc_centeraxle" } },
            // X10: diesel-only, no hybrid pairings at all, no D8645. B400R
            // default, B500R, B3400xFE. ZF/Voith status still UNCONFIRMED
            // against the real NFI spec sheet -- left off pending that.
            { BusAudioEngine.EngineType.X10,  new[] { "b400r", "b500r", "b400r_g5", "b500r_g5", "b3400xfe" } },
            // ISL9: 280/330 tier. B400R default, B500R, ZF, Allison H40
            // (h40ep only, no eGen Flex -- ISL9 never got it) / H50 (h50ep
            // only, same reason), BAE HDS200, HDS300, D8645 (kept, unlike
            // L9 which uses d8646 instead). No B3400xFE, no Voith.
            // [FIX] b400r_g5/b500r_g5 removed -- Allison Gen5 never belonged
            // on ISL9 (non-hybrid-diesel-lineup transmission). EP40/EP50 also
            // deliberately NOT offered here -- ISL-only (see ISL entry below);
            // BusAudioEngine redirects ep40/ep50 straight to h40ep/h50ep on
            // ISL9, so offering them here would just be a confusing dupe.
            { BusAudioEngine.EngineType.ISL9, new[] { "b400r", "b500r", "zf", "zfel2", "zfel2_hd", "h40ep", "h50ep", "bae", "hds300", "d8645" } },
            // ISL: locked 280hp, no hybrid pairing at all (h40ep/h50ep/zh50ep
            // all removed -- ISL never got an articulated tune to justify a
            // hybrid drive pairing). Diesel-only: B400R default, B500R, ZF
            // (gen1 EcoLife only -- EcoLife 2 not offered here), D8645.
            // [FIX] b400r_g5/b500r_g5 removed -- same non-hybrid-diesel-
            // lineup reasoning as ISL9/ISLG.
            // [ADD] ep40/ep50 -- real pre-2010 Allison EP-40 unit, ISL-only:
            // confirmed as the XDE40's original launch pairing (Cummins ISL9
            // + Allison EP-40) before the ~2010 rename to H40EP/H50EP.
            { BusAudioEngine.EngineType.ISL,  new[] { "b400r", "b500r", "zf", "d8645", "ep40", "ep50" } },
            // ISB67: hybrid-only -- H40EP or BAE HDS200 default. No D8645, no
            // Allison B-series (those never paired with this engine per real
            // Xcelsior spec).
            { BusAudioEngine.EngineType.ISB67,new[] { "bae", "h40ep" } },
            // ISLG: B400R default, B500R, D8645. No hybrids, no ZF/Voith/
            // B3400xFE (the real Westport-era ISL G never offered those).
            // [FIX] b400r_g5/b500r_g5 removed -- same reasoning as ISL/ISL9.
            { BusAudioEngine.EngineType.ISLG, new[] { "b400r", "b500r", "d8645" } },
        };
//public enum EngineType { L9N, L9, B67, X10, XE40, ISL9, XE60, ISL, ISB67, ISLG, XHE40, XHE60, B72 }
    private static readonly Dictionary<string, string> _txLabel = new()
    {
        { "d8646",    "D864.6 (Voith)" }, // renamed from "voith" per the naming pass
        { "d8645",    "Voith DIWA.5" },
        { "b400r",    "Allison B400R" },
        { "b500r",    "Allison B500R" },
        { "b400r_g5", "Allison B400R Gen 5" },
        { "b500r_g5", "Allison B500R Gen 5" },
        { "b3400xfe", "Allison B3400xFE" },
        { "zf",       "ZF EcoLife (Gen 1, 6AP1200B)" },
        { "zfel2",    "ZF EcoLife 2 (6AP1420)" },
        { "zfel2_hd", "ZF EcoLife 2 HD (6AP1620/1720)" },
        { "h40ep",    "Allison H40EP" },
        { "h50ep",    "Allison H50EP" },
        // [ADD] Real pre-2010 Allison EP-40/EP-50 -- renamed to H40EP/H50EP
        // circa 2010. ISL-only (Cummins ISL9 + EP-40 was the XDE40's real
        // original launch pairing).
        { "ep40",     "Allison EP40" },
        { "ep50",     "Allison EP50" },
        { "egenflex40", "Allison eGen Flex H40" },
        { "egenflex50", "Allison eGen Flex H50" },
        { "hds300",   "BAE HDS 300" },
        { "bae",      "BAE HDS 200" }, // renamed -- bare "BAE HybriDrive" no longer shown, always the specific HDS200 name now
        { "electric", "Electric (legacy)" },
        { "fcelfa",   "Fuel Cell-Electric (legacy)" },
        { "elfa3",              "Accelera ELFA 3" },
        { "accelera",           "Accelera NextGen (Direct-Drive)" },
        { "elfa3_centeraxle",     "Accelera ELFA 3 – Rear + In-Wheel Centre" },
        { "accelera_centeraxle",  "Accelera NextGen – Rear + In-Wheel Centre" },
        { "elfa2",              "Siemens ELFA 2" },
        { "accelera_fc",            "Accelera NextGen FC (Direct-Drive)" },
        { "elfa2_centeraxle",       "Siemens ELFA 2 – Rear + In-Wheel Centre" },
        { "accelera_fc_centeraxle", "Accelera NextGen FC – Rear + In-Wheel Centre" },
        // Legacy tx strings kept labeled so any old save/PlayerPrefs data
        // still displays sensibly even though none of these are offered as
        // picks going forward (migrated on load — see LoadPrefs).
        { "voith",    "Voith DIWA.6 (legacy -- now \"D864.6\")" },
        { "voith35",  "35ft864.6 (legacy)" },
        { "d8646art", "D864.6art \"Tasty Voith\" (legacy -- artic now handled by plain D864.6)" },
        { "zh50ep",   "Allison H50EP (Prototype, legacy)" },
        { "h50ep_gen5", "Allison H50EP 5th Gen (legacy)" },
        { "baegen3",  "BAE HybriDrive Gen3 (legacy -- not part of the real fleet's tx system)" },
        { "nxt",      "Voith DIWA 867.8 NXT (legacy)" },
        { "zfave130", "ZF AVE 130 (legacy — migrates to Accelera ELFA 3 centre-axle)" },
        { "fcave130", "Fuel Cell ZF AVE 130 (legacy — migrates to Accelera NextGen FC centre-axle)" },
        { "elfa3_zfave130",         "Accelera ELFA 3 (legacy)" },
        { "accelera_zfave130",      "Accelera NextGen (legacy)" },
        { "elfa2_zfave130",         "Siemens ELFA 2 (legacy)" },
        { "accelera_fc_zfave130",   "Accelera NextGen FC (legacy)" },
        { "allison",  "Allison B400R" },
        { "siemenscng", "Siemens ELFA3 x L9N Series Hybrid (removed)"},
    };

    private static readonly Dictionary<BusAudioEngine.EngineType, string> _engLabel = new()
    {
        { BusAudioEngine.EngineType.L9N,  "Cummins Westport L9N CNG  (XN40)" },
        { BusAudioEngine.EngineType.L9,   "Cummins L9 Diesel  (XD40/XDE40)" },
        { BusAudioEngine.EngineType.B67,  "Cummins B6.7 Hybrid  (XDE40)" },
        { BusAudioEngine.EngineType.B72,  "Cummins B7.2 Hybrid  (2027+)" },
        { BusAudioEngine.EngineType.XE40, "Battery-Electric  (XE40/XE60)" },
        { BusAudioEngine.EngineType.XE60, "Electric Portal Axle  (XE60)" },
        { BusAudioEngine.EngineType.XHE40, "Hydrogen Fuel Cell-Electric  (XHE40/XHE60)" },
        { BusAudioEngine.EngineType.XHE60, "Hydrogen Fuel Cell Portal Axle  (XHE60)" },
        { BusAudioEngine.EngineType.X10,  "Cummins X10 Diesel" },
        { BusAudioEngine.EngineType.ISL9, "Cummins ISL9 Diesel" },
        { BusAudioEngine.EngineType.ISL,  "Cummins ISL Diesel" },
        { BusAudioEngine.EngineType.ISB67,"Cummins ISB6.7 Hybrid"},
        { BusAudioEngine.EngineType.ISLG, "Cummins Westport ISLG  (XN40)" }
    };

    // ═════════════════════════════════════════════════════════════════════
    //  POWERTRAIN CATEGORY — the real New Flyer Xcelsior trim family this
    //  fleet is grounded in: Clean Diesel / CNG / Hybrid / Battery-Electric
    //  / Hydrogen-Electric. Four of these are ENGINE-defined (what fuel the
    //  engine burns); Hybrid is TRANSMISSION-defined instead — a B6.7/B7.2
    //  only reads as "Hybrid" because of which drivetrain is bolted to it
    //  (eGen Flex / BAE Gen3 / HDS200/300 / H40EP/H50EP), same real-world
    //  logic that lets those exact hybrid transmissions also show up behind
    //  an L9/ISL9 diesel core elsewhere in this fleet without that engine
    //  itself being a dedicated "hybrid engine". GetPowertrainCategory
    //  below is what actually applies that rule -- it checks the SELECTED
    //  TX first and only falls back to the engine's own base fuel category
    //  if the tx isn't one of the known hybrid drivetrains.
    // ═════════════════════════════════════════════════════════════════════
    private enum PowertrainCategory { CleanDiesel, CNG, Hybrid, BatteryElectric, HydrogenElectric }

    private static readonly Dictionary<PowertrainCategory, Color> _categoryColor = new()
    {
        { PowertrainCategory.CleanDiesel,     new Color(0x34/255f, 0x41/255f, 0x48/255f) }, // Charcoal Blue #344148
        { PowertrainCategory.CNG,             new Color(0x04/255f, 0x4C/255f, 0x99/255f) }, // Steel Azure   #044C99
        { PowertrainCategory.Hybrid,          new Color(0x25/255f, 0xB3/255f, 0xBB/255f) }, // Tropical Teal #25B3BB
        { PowertrainCategory.BatteryElectric, new Color(0x64/255f, 0xE6/255f, 0xA3/255f) }, // Tropical Mint #64E6A3
        { PowertrainCategory.HydrogenElectric,new Color(0xFF/255f, 0xD0/255f, 0x44/255f) }, // Golden Pollen #FFD044
    };

    // Base fuel category per engine -- B67/B72/ISB67 sit in Hybrid here since
    // every tx these three actually offer (see _compatTX) IS a hybrid
    // drivetrain -- there's no plain-diesel pick for any of them in this
    // fleet anymore (ISB67 lost its D8645/B400R/B500R diesel-only options
    // entirely, hybrid-only now), so their base category and their effective
    // category are the same thing.
    private static readonly Dictionary<BusAudioEngine.EngineType, PowertrainCategory> _engineCategory = new()
    {
        { BusAudioEngine.EngineType.L9,    PowertrainCategory.CleanDiesel },
        { BusAudioEngine.EngineType.X10,   PowertrainCategory.CleanDiesel },
        { BusAudioEngine.EngineType.ISL9,  PowertrainCategory.CleanDiesel },
        { BusAudioEngine.EngineType.ISL,   PowertrainCategory.CleanDiesel },
        { BusAudioEngine.EngineType.ISB67, PowertrainCategory.Hybrid },
        { BusAudioEngine.EngineType.L9N,   PowertrainCategory.CNG },
        { BusAudioEngine.EngineType.ISLG,  PowertrainCategory.CNG },
        { BusAudioEngine.EngineType.B67,   PowertrainCategory.Hybrid },
        { BusAudioEngine.EngineType.B72,   PowertrainCategory.Hybrid },
        { BusAudioEngine.EngineType.XE40,  PowertrainCategory.BatteryElectric },
        { BusAudioEngine.EngineType.XE60,  PowertrainCategory.BatteryElectric },
        { BusAudioEngine.EngineType.XHE40, PowertrainCategory.HydrogenElectric },
        { BusAudioEngine.EngineType.XHE60, PowertrainCategory.HydrogenElectric },
    };

    // Any tx string that's a real hybrid drivetrain, regardless of which
    // engine it's paired with -- an L9 or ISL9 running one of these reads
    // as Hybrid-category even though L9/ISL9's own base category is Clean
    // Diesel. [FIX] "egenflex" (placeholder singular) never matched the real
    // dispatched strings "egenflex40"/"egenflex50" -- an L9 running eGen
    // Flex H50 was falling through to plain CleanDiesel coloring.
    // [FIX] Missing "baegen3" (present in FleetRosterData's copy of this same
    // set, never mirrored here -- a B72 running Gen3 showed the wrong color).
    // [ADD] "ep40"/"ep50" -- real pre-2010 Allison hybrid unit, same category
    // treatment as h40ep/h50ep.
    private static readonly HashSet<string> _hybridTxSet = new()
    {
        "bae", "hds300", "baegen3", "h40ep", "h50ep", "ep40", "ep50", "egenflex40", "egenflex50"
    };

    private PowertrainCategory GetPowertrainCategory(BusAudioEngine.EngineType eng, string tx) =>
        !string.IsNullOrEmpty(tx) && _hybridTxSet.Contains(tx)
            ? PowertrainCategory.Hybrid
            : (_engineCategory.TryGetValue(eng, out var cat) ? cat : PowertrainCategory.CleanDiesel);

    // ═════════════════════════════════════════════════════════════════════
    //  ENGINE DISPLAY ORDER — [FIX] the Custom-tab drivetrain picker used
    //  to iterate Enum.GetValues(EngineType) directly, i.e. whatever order
    //  the enum happens to be DECLARED in. That order is essentially
    //  arbitrary (L9N, L9, B67, B72, X10, XE40, ISL9, XE60, ISL, ISB67,
    //  ISLG, XHE40, XHE60 -- CNG, Diesel, Hybrid, Hybrid, Diesel, Electric,
    //  Diesel, Electric, Diesel, Diesel, CNG, Hydrogen, Hydrogen, no
    //  grouping at all) and was never meant as a display order.
    //
    //  IMPORTANT: the enum's own declared order is NOT changed here on
    //  purpose. _selEngine is persisted as a raw int via
    //  PlayerPrefs.SetInt("BSM_Engine", (int)_selEngine) -- reordering the
    //  enum's underlying values would silently reinterpret every existing
    //  save as a DIFFERENT engine the next time it loads. This array only
    //  controls what order the picker DRAWS engines in; the enum's actual
    //  values, and therefore every saved selection, are untouched.
    // ═════════════════════════════════════════════════════════════════════
    private static readonly BusAudioEngine.EngineType[] _engineDisplayOrder =
    {
        // Clean Diesel
        BusAudioEngine.EngineType.L9,
        BusAudioEngine.EngineType.X10,
        BusAudioEngine.EngineType.ISL9,
        BusAudioEngine.EngineType.ISL,
        BusAudioEngine.EngineType.ISB67,
        // CNG
        BusAudioEngine.EngineType.L9N,
        BusAudioEngine.EngineType.ISLG,
        // Hybrid
        BusAudioEngine.EngineType.B67,
        BusAudioEngine.EngineType.B72,
        // Battery-Electric
        BusAudioEngine.EngineType.XE40,
        BusAudioEngine.EngineType.XE60,
        // Hydrogen-Electric
        BusAudioEngine.EngineType.XHE40,
        BusAudioEngine.EngineType.XHE60,
    };

    private Vector2 _customScroll;
    private const float CHIP_MIN_W  = 172f;
    private const float CHIP_H      = 44f;
    private const float CHIP_GAP    = 6f;

    // Cols/rows for a wrapping chip grid — shared by the precompute pass
    // (content height, before drawing) and the draw pass, so they never
    // disagree about layout the way the old single-row division did.
    private static int ChipCols(float w) => Mathf.Max(1, Mathf.FloorToInt((w + CHIP_GAP) / (CHIP_MIN_W + CHIP_GAP)));
    private static int ChipRows(int count, float w) => Mathf.CeilToInt(count / (float)ChipCols(w));

    private enum Tab { Fleet, Custom }


    private Tab _tab = Tab.Fleet;

    private int     _selSeriesIdx = 0;
    private int     _selFleetNum  = -1;
    private Vector2 _seriesScroll, _fleetScroll;

    private BusAudioEngine.EngineType _selEngine = BusAudioEngine.EngineType.L9N;
    private string                    _selTX     = "b400r";
    private int                       _selBusIdx = 0;
    private bool _selOldBus = false;
    // [ADD] Manual Articulated toggle for the Custom tab — previously this
    // was auto-detected from the prefab (IsCustomBusArticulated), which
    // gated d8646art and the artic-only electric/hydrogen tx options. Now
    // there's a real chip for it so it can be set explicitly instead of
    // relying on prefab detection.
    private bool _selArticulated = false;

    // Real, built-in DIWA voice options for the Custom tab — only relevant
    // when engine is L9/L9N/ISL9 and TX is voith/d8645. NOT an add-on: these
    // ARE part of the DIWA voice, same as the base whine.
    private bool _selDiwaOpt1_1 = false; // delayed whine, ~0.5s behind rpm
    private bool _selDiwaOpt1_2 = false; // deep whine overlay, G1 only
    private bool _selDiwaOpt1_3 = false; // smoother / quieter overall
    private bool _selDiwaOpt1_4 = false; // second D864.6 character: suppressed whine until late G1, hiss window, extended G1, audible piston firing
    private bool _selDiwaOpt1_5 = false; // "the shaker": no whine at all, deeper+louder core, rapid-fire click vibration-sim at idle
    private bool _selDiwaOpt1_6 = false; // "fourth voice": whine hidden like opt1_4 but quieter, extended two-tone G1, groan-only retarder
    private bool _selDiwaOpt1_7 = false; // "fifth voice": as opt1_6 but whine never hidden -- Wandler wind-up on move-off instead
    private bool _selDiwaOpt1_8 = false; // sixth voice: Fleet-tab-only until now -- see GetDiwaVoiceFor's own doc for what this represents

    private bool IsDiwaCompatible(BusAudioEngine.EngineType eng, string tx) =>
        eng == BusAudioEngine.EngineType.L9 || eng == BusAudioEngine.EngineType.L9N || eng == BusAudioEngine.EngineType.ISL9;

    private bool  _menuOpen    = false;
    public  bool  IsMenuOpen => _menuOpen;

    /// <summary>[MOBILE] Same open/close pair Alpha0 already drives — lets
    /// a touch "BUS" button toggle the menu the same way.</summary>
    public void ToggleMenu()
    {
        if (_menuOpen) CloseMenu();
        else           OpenMenu();
    }
    private bool  _firstOpen   = true;
    private bool  _stylesReady = false;
    private float _panelX, _panelY;

    private GUIStyle _lblTitle, _lblSub, _lblDim, _lblCyan, _lblGreen,
                     _lblAmber, _lblBody, _lblSmall;
    private GUIStyle _btnPrimary, _btnSecond, _btnActive, _btnTab, _btnTabActive;

    private RenderTexture _previewRT;
    private GameObject    _previewInstance;

    private AudioSource _honkSrc;
    private bool   _honking   = false;
    private float  _honkTimer = 0f;
    private double _honkPh1, _honkPh2, _honkPh3;
    private double _honkSR    = 48000.0;

    public static BusSelectMenu Instance { get; private set; }
    private BusSimulationController _activeBus;
    public  BusSimulationController ActiveBus => _activeBus;
    private GameObject              _spawnedFleetBus;

    private NPCBusController        _possessedNPC  = null;
    private GameObject              _possessedRoot = null;
    private string                  _savedTag      = "Untagged";

    private Transform _phOriginalParent = null;

    // ═════════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═════════════════════════════════════════════════════════════════════════
    private void Awake()
    {
        Instance = this;
        if (mainCamera == null) mainCamera = Camera.main;
        EnforceSingleAudioListener();
        SetupHonkAudio();
        SetupPreviewRT();
        LoadPrefs();

        // [ADD] Multi-dataset support -- only kicks in if additionalFleetRosters
        // has anything in it, so a project with a single fleetRoster asset (and
        // an empty additionalFleetRosters list) behaves exactly as before.
        if (additionalFleetRosters != null && additionalFleetRosters.Count > 0)
        {
            var sources = new List<FleetRosterData>();
            if (fleetRoster != null) sources.Add(fleetRoster);
            sources.AddRange(additionalFleetRosters);
            fleetRoster = FleetRosterData.MergeInto(null, sources);
        }

        if (fleetRoster == null || fleetRoster.series == null || fleetRoster.series.Count == 0)
            _tab = Tab.Custom;
    }

    private void Start()
    {
        var ph = PlayerHandoff.Instance;
        if (ph != null) _phOriginalParent = ph.transform.parent;

        bool hasSaved = PlayerPrefs.HasKey("BSM_Tab");
        if (!hasSaved) OpenMenu();
        else           ApplySelectionToSystems();
    }

    private void Update()
    {
        // [FIX Bug 43] don't allow opening this behind the main menu
        if (!MainMenu.BlocksInput && Input.GetKeyDown(KeyBindings.Current.busSelectMenu))
        {
            if (_menuOpen) CloseMenu();
            else           OpenMenu();
        }

        if (!_menuOpen && Input.GetKeyDown(KeyBindings.Current.honk) && _activeBus != null)
            TriggerHonk();

        if (_honking) { _honkTimer -= Time.deltaTime; if (_honkTimer <= 0f) _honking = false; }

        if (_menuOpen && previewCamera != null && _previewRT != null)
            UpdatePreviewCamera();
    }
/// <summary>Resyncs every NPC bus to its current position. Called by DebugResetController (Ctrl+F7).</summary>
    public void TriggerNPCResync()
    {
        // Find every instance of NPCBusController currently active in the scene
NPCBusController[] controllers = UnityEngine.Object.FindObjectsByType<NPCBusController>(FindObjectsSortMode.None);
        // Loop through each one and call the public method
        foreach (NPCBusController controller in controllers)
        {
            controller.ResyncToCurrentPosition();
        }

        Debug.Log($"Resynced {controllers.Length} NPC Bus Controllers.");
    }
    // ═════════════════════════════════════════════════════════════════════════
    //  OPEN / CLOSE
    // ═════════════════════════════════════════════════════════════════════════
    private void OpenMenu()
    {
        _menuOpen = true;
        _panelX   = (Screen.width  - panelW) * 0.5f;
        _panelY   = (Screen.height - panelH) * 0.5f;
        LoadPrefs();
        RefreshPreview();
    }

    private void CloseMenu()
    {
        _menuOpen = false;
        DestroyPreviewInstance();
        HideAllCustomBuses();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  OnGUI
    // ═════════════════════════════════════════════════════════════════════════
    private void OnGUI()
    {
        if (!_menuOpen) return;
        EnsureStyles();

        GUI.color = new Color(0, 0, 0, 0.75f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        var pr = new Rect(_panelX, _panelY, panelW, panelH);
        MDT_UITheme.DrawPanel(pr);
        GUI.BeginGroup(pr);

        MDT_UITheme.DrawHeader(new Rect(0, 0, panelW, 40f));
        GUI.Label(new Rect(14, 0, panelW - 80, 40), "SELECT YOUR BUS", _lblTitle);
        if (GUI.Button(new Rect(panelW - 46, 8, 34, 24), "✕", _btnSecond))
            CloseMenu();

        float tabY = 42f, tabH = 28f;
        if (fleetRoster != null && fleetRoster.series != null && fleetRoster.series.Count > 0)
        {
            float tabW = (panelW - 16f) * 0.5f;
            DrawTab(new Rect(8,            tabY, tabW, tabH), "FLEET",  Tab.Fleet);
            DrawTab(new Rect(8 + tabW + 4, tabY, tabW, tabH), "CUSTOM", Tab.Custom);
        }
        else
        {
            GUI.Label(new Rect(8, tabY, panelW - 16, tabH), "CUSTOM BUS", _lblSub);
        }

        float bodyY = tabY + tabH + 6f;
        float bodyH = panelH - bodyY - 50f;
        float leftW = panelW * 0.52f - 8f;
        float rightW = panelW * 0.48f - 12f;

        GUI.BeginGroup(new Rect(8, bodyY, leftW, bodyH));
        if (_tab == Tab.Fleet) DrawFleetSelectors(leftW, bodyH);
        else                   DrawCustomSelectors(leftW, bodyH);
        GUI.EndGroup();

        GUI.BeginGroup(new Rect(16 + leftW, bodyY, rightW, bodyH));
        DrawPreviewPanel(rightW, bodyH);
        GUI.EndGroup();

        DrawFooter(new Rect(8, panelH - 46f, panelW - 16f, 38f));
        GUI.EndGroup();
    }

    private void DrawTab(Rect r, string label, Tab t)
    {
        bool active = _tab == t;
        MDT_UITheme.DrawRect(r, active ? MDT_UITheme.BGDirSel : MDT_UITheme.BGButton);
        if (GUI.Button(r, label, active ? _btnTabActive : _btnTab)) { _tab = t; RefreshPreview(); }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  FLEET SELECTORS
    // ═════════════════════════════════════════════════════════════════════════
    /// <summary>ZH50EP's dedicated 1001-1003 series is deliberately excluded
    /// from normal fleet browsing — it's a fixed, mysterious vehicle nobody
    /// casually picks, not a normal fleet option. (PossessFleetNumber still
    /// works if called directly by fleet number, e.g. from a dev console —
    /// this only hides it from the browsable list.)</summary>
    private bool IsHiddenFromFleetBrowser(FleetSeriesDefinition def) =>
        def != null && def.GetTxFor(0) == "zh50ep";

    private void DrawFleetSelectors(float w, float h)
    {
        if (fleetRoster == null || fleetRoster.series == null || fleetRoster.series.Count == 0)
        { GUI.Label(new Rect(0,0,w,20), "No fleet roster assigned.", _lblDim); return; }

        var series = fleetRoster.series;
        float y = 0f;

        GUI.Label(new Rect(0, y, w, 18), "SERIES", _lblSub);  y += 20f;

        var visibleIdx = new List<int>();
        for (int i = 0; i < series.Count; i++)
            if (!IsHiddenFromFleetBrowser(series[i])) visibleIdx.Add(i);

        float seriesH = Mathf.Min(visibleIdx.Count * 30f, h * 0.42f);
        _seriesScroll = GUI.BeginScrollView(new Rect(0, y, w, seriesH), _seriesScroll,
                                            new Rect(0, 0, w - 16f, visibleIdx.Count * 30f), false, true);
        for (int row = 0; row < visibleIdx.Count; row++)
        {
            int  i   = visibleIdx[row];
            var  s   = series[i];
            bool sel = _selSeriesIdx == i;
            var  r   = new Rect(0, row * 30f, w - 16f, 28f);
            MDT_UITheme.DrawRect(r, sel ? MDT_UITheme.BGDirSel : MDT_UITheme.BGPill);
            if (GUI.Button(r, "", GUIStyle.none)) { _selSeriesIdx = i; _selFleetNum = -1; RefreshPreview(); }
            GUI.Label(new Rect(r.x + 6, r.y, r.width - 6, r.height),
                $"{s.seriesName}  ·  {s.busType}  ·  {s.busCount} buses  ·  {s.modelYear}",
                sel ? _lblCyan : _lblBody);
        }
        GUI.EndScrollView();
        y += seriesH + 8f;

        if (_selSeriesIdx < 0 || _selSeriesIdx >= series.Count || IsHiddenFromFleetBrowser(series[_selSeriesIdx])) return;
        var def = series[_selSeriesIdx];

        string txStr = _txLabel.TryGetValue(def.GetTxFor(0), out var tl) ? tl : def.GetTxFor(0);
        GUI.Label(new Rect(0, y, w, 16), $"Drivetrain: {def.engineType}  ·  TX: {txStr}", _lblDim);
        y += 18f;
        if (def.isArticulated) { GUI.Label(new Rect(0, y, w, 16), "Articulated", _lblAmber); y += 18f; }
        y += 4f;

        GUI.Label(new Rect(0, y, w, 18), "FLEET NUMBER", _lblSub);  y += 20f;

        int count = def.busCount;
        int cols  = Mathf.Min(count, 6);
        float btnW  = (w - cols * 3f) / cols;
        float fleetH = Mathf.CeilToInt((float)count / cols) * 28f;
        float viewH  = Mathf.Min(fleetH, h - y - 22f);

        _fleetScroll = GUI.BeginScrollView(new Rect(0, y, w, viewH), _fleetScroll,
                                           new Rect(0, 0, w - 16f, fleetH), false, true);
        for (int i = 0; i < count; i++)
        {
            int  fn      = def.startFleetNumber + i;
            bool sel     = _selFleetNum == fn;
            bool inScene = IsFleetNumberLive(fn);
            int  col     = i % cols;
            int  row     = i / cols;
            var  r       = new Rect(col * (btnW + 3f), row * 28f, btnW, 26f);

            Color bg = sel      ? MDT_UITheme.BGDirSel
                     : inScene  ? MDT_UITheme.BGPill
                                : new Color(0.09f, 0.09f, 0.11f, 1f);
            MDT_UITheme.DrawRect(r, bg);

            if (GUI.Button(r, fn.ToString(), sel ? _lblCyan : (inScene ? _lblBody : _lblDim)))
            { _selFleetNum = fn; RefreshPreview(); }
        }
        GUI.EndScrollView();

        y += viewH + 4f;
        GUI.Label(new Rect(0, y, w, 14), "Dimmed = not yet spawned in scene.", _lblDim);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CHIP GRID  —  shared modern-chip primitive used by prefab/engine/tx
    //  pickers below. Fixes the old bug where N options were squeezed into one
    //  row (compatTX.Length up to 9 → unreadable slivers); now wraps to a grid
    //  sized off a minimum chip width, same math used to precompute scroll
    //  content height as to actually draw it.
    // ═════════════════════════════════════════════════════════════════════════
    private bool DrawChip(Rect r, string title, string subtitle, bool active, Color? accent = null)
    {
        MDT_UITheme.DrawRect(r, active ? MDT_UITheme.BGDirSel : MDT_UITheme.BGPill);
        // [NEW] Category accent bar -- when provided (engine/tx pickers
        // pass their PowertrainCategory color), this replaces the plain
        // cyan "active" stripe with the real fuel/hybrid category color, so
        // Clean Diesel/CNG/Hybrid/Battery-Electric/Hydrogen-Electric are
        // visually distinguishable at a glance, not just by their text
        // label. Every other chip in the menu (bus prefab picker, DIWA
        // voice options) doesn't pass accent, so it keeps the original
        // plain-cyan-when-active look untouched.
        if (accent.HasValue)
            MDT_UITheme.DrawRect(new Rect(r.x, r.y, 4f, r.height), accent.Value);
        else if (active)
            MDT_UITheme.DrawRect(new Rect(r.x, r.y, 3f, r.height), MDT_UITheme.TextCyan);

        bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);

        float pad = 9f;
        GUI.Label(new Rect(r.x + pad, r.y + 3, r.width - pad * 2, 18),
                  title, active ? _lblCyan : _lblBody);
        if (!string.IsNullOrEmpty(subtitle))
            GUI.Label(new Rect(r.x + pad, r.y + 20, r.width - pad * 2, r.height - 22),
                      subtitle, _lblSmall);
        return clicked;
    }

    // Draws a wrapping grid of chips for `count` items and returns the new y.
    private float DrawChipGrid(float y, float w, int count,
                                Func<int, string> title, Func<int, string> subtitle,
                                Func<int, bool> isActive, Action<int> onSelect,
                                Func<int, Color?> accentColor = null)
    {
        int cols = ChipCols(w);
        int rows = ChipRows(count, w);
        float chipW = (w - CHIP_GAP * (cols - 1)) / cols;

        for (int i = 0; i < count; i++)
        {
            int col = i % cols, row = i / cols;
            var r = new Rect(col * (chipW + CHIP_GAP), y + row * (CHIP_H + CHIP_GAP), chipW, CHIP_H);
            if (DrawChip(r, title(i), subtitle(i), isActive(i), accentColor?.Invoke(i))) onSelect(i);
        }
        return y + rows * (CHIP_H + CHIP_GAP);
    }

    // Section header — small caps label with a hairline rule, replacing the
    // old bare _lblSub line for a slightly more structured "card" feel.
    private float DrawSectionHeader(float y, float w, string label)
    {
        GUI.Label(new Rect(2, y, w - 4, 16), label, _lblSub);
        y += 16f;
        MDT_UITheme.DrawRect(new Rect(0, y, w, 1f), MDT_UITheme.BGButton);
        return y + 8f;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CUSTOM SELECTORS  (rebuilt — wrapping chip grids in a scrollview)
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawCustomSelectors(float w, float h)
    {
        float innerW = w - 18f;   // reserve scrollbar gutter

        // [UPDATE] XE60/XHE60 are folded into the XE40/XHE40 rows below —
        // they're driven by which TX the person picks (std vs ZF AVE 130
        // artic flavor), not by a separate row in this list.
        // [FIX] Was Enum.GetValues(EngineType) -- the enum's raw declaration
        // order (arbitrary, no fuel-category grouping). Now draws from
        // _engineDisplayOrder, a curated Clean Diesel -> CNG -> Hybrid ->
        // Battery-Electric -> Hydrogen-Electric ordering. The enum's actual
        // underlying values are untouched (see _engineDisplayOrder's header
        // comment) -- this only changes what order the picker DRAWS in.
        var engines = Array.FindAll(_engineDisplayOrder, e => e != BusAudioEngine.EngineType.XE60 && e != BusAudioEngine.EngineType.XHE60);
        var compatTX = GetCompatTXForCustom(_selEngine, _selBusIdx);

        // ── Side-by-side split: DRIVETRAIN list on the left, TRANSMISSION
        // panel attached directly beside it on the right — picking a
        // drivetrain shows its TX options immediately without scrolling
        // down past everything else first. ──────────────────────────────
        const float colGap = 8f;
        float driveColW = innerW * 0.46f - colGap * 0.5f;
        float txColW    = innerW - driveColW - colGap;

        // ── Precompute content height using the SAME grid math the draw pass
        // uses below, so the scrollview's content rect is always accurate. ──
        float cy = 0f;
        if (busPool.Count > 1) cy += 16f + 8f + ChipRows(busPool.Count, innerW) * (CHIP_H + CHIP_GAP) + 10f;
        float driveSectionH = 16f + 8f + ChipRows(engines.Length, driveColW) * (CHIP_H + CHIP_GAP);
        float txSectionH    = 16f + 8f + ChipRows(compatTX.Length, txColW) * (CHIP_H + CHIP_GAP);
        cy += Mathf.Max(driveSectionH, txSectionH) + 10f;
        cy += CHIP_H + CHIP_GAP + 4f;   // articulated toggle
        cy += CHIP_H + CHIP_GAP + 4f;   // old-bus toggle
        if (IsDiwaCompatible(_selEngine, _selTX))
            cy += 16f + 8f + ChipRows(6, innerW) * (CHIP_H + CHIP_GAP) + 10f; // DIWA voice options -- 6 slots (opt1_8 added)
        cy += 44f;                       // spec caption

        _customScroll = GUI.BeginScrollView(new Rect(0, 0, w, h), _customScroll, new Rect(0, 0, innerW, cy), false, true);

        float y = 0f;

        if (busPool.Count > 1)
        {
            y = DrawSectionHeader(y, innerW, "BUS PREFAB");
            y = DrawChipGrid(y, innerW, busPool.Count,
                i => { var n = busPool[i] != null ? busPool[i].name : $"Bus {i}"; return n.Length > 16 ? n.Substring(0, 15) + "…" : n; },
                i => null,
                i => _selBusIdx == i,
                i => {
                    _selBusIdx = i;
                    RefreshPreview();
                    SyncArticulatedFromPrefab();
                    var compat = GetCompatTXForCustom(_selEngine, _selBusIdx);
                    if (Array.IndexOf(compat, _selTX) < 0) _selTX = compat.Length > 0 ? compat[0] : "b400r";
                });
            y += 10f;
        }

        float sideBySideY = y;

        // ── DRIVETRAIN (left column) ──────────────────────────────────────
        GUI.BeginGroup(new Rect(0, sideBySideY, driveColW, driveSectionH));
        {
            float dy = DrawSectionHeader(0, driveColW, "DRIVETRAIN");
            DrawChipGrid(dy, driveColW, engines.Length,
                i => {
                    string full = _engLabel.TryGetValue(engines[i], out var en) ? en : engines[i].ToString();
                    int paren = full.IndexOf('(');
                    return (paren > 0 ? full.Substring(0, paren) : full).Trim();
                },
                i => {
                    string full = _engLabel.TryGetValue(engines[i], out var en) ? en : "";
                    int paren = full.IndexOf('(');
                    return paren > 0 ? full.Substring(paren) : null;
                },
                i => IsDrivetrainRowActive(engines[i]),
                i => {
                    var picked = engines[i];
                    if (picked == BusAudioEngine.EngineType.XE40)
                    {
                        if (!IsDrivetrainRowActive(picked)) { _selEngine = BusAudioEngine.EngineType.XE40; _selTX = "elfa3"; }
                    }
                    else if (picked == BusAudioEngine.EngineType.XHE40)
                    {
                        if (!IsDrivetrainRowActive(picked)) { _selEngine = BusAudioEngine.EngineType.XHE40; _selTX = "elfa2"; }
                    }
                    else
                    {
                        _selEngine = picked;
                        var compat = GetCompatTXForCustom(_selEngine, _selBusIdx);
                        if (Array.IndexOf(compat, _selTX) < 0) _selTX = compat.Length > 0 ? compat[0] : "b400r";
                    }
                },
                // [NEW] Category accent -- each drivetrain chip shows its
                // own base fuel category color (engine-defined, not tx-
                // defined, since this is the ENGINE picker), so Clean
                // Diesel/CNG/Hybrid/Battery-Electric/Hydrogen-Electric are
                // visually grouped even before reading the label.
                i => _categoryColor.TryGetValue(GetPowertrainCategory(engines[i], null), out var c) ? c : (Color?)null);
        }
        GUI.EndGroup();

        // ── TRANSMISSION (right side panel, attached — no scrolling needed) ─
        GUI.BeginGroup(new Rect(driveColW + colGap, sideBySideY, txColW, txSectionH));
        {
            float ty = DrawSectionHeader(0, txColW, "TRANSMISSION");
            DrawChipGrid(ty, txColW, compatTX.Length,
                i => _txLabel.TryGetValue(compatTX[i], out var td) ? td : compatTX[i],
                i => null,
                i => _selTX == compatTX[i],
                i => {
                    _selTX = compatTX[i];
                    // Picking an artic-flavored TX (genuine ZF portal axle,
                    // or Accelera/ELFA3's centre-in-wheel layout) on a
                    // grouped drivetrain (BATTERY-ELECTRIC / HYDROGEN-
                    // ELECTRIC) flips which real EngineType is applied.
                    bool artic = _selTX.EndsWith("_centeraxle");
                    if (_selEngine == BusAudioEngine.EngineType.XE40 || _selEngine == BusAudioEngine.EngineType.XE60)
                        _selEngine = artic ? BusAudioEngine.EngineType.XE60 : BusAudioEngine.EngineType.XE40;
                    else if (_selEngine == BusAudioEngine.EngineType.XHE40 || _selEngine == BusAudioEngine.EngineType.XHE60)
                        _selEngine = artic ? BusAudioEngine.EngineType.XHE60 : BusAudioEngine.EngineType.XHE40;
                },
                // [NEW] Category accent per TX chip -- this is where the
                // "Hybrid (the tx)" distinction actually shows: a hybrid
                // drivetrain (bae/hds300/baegen3/h40ep/h50ep/...) lights up
                // Tropical Teal here even on an engine whose OWN base
                // category is Clean Diesel, since GetPowertrainCategory
                // checks the tx first.
                i => _categoryColor.TryGetValue(GetPowertrainCategory(_selEngine, compatTX[i]), out var c) ? c : (Color?)null);
        }
        GUI.EndGroup();

        y = sideBySideY + Mathf.Max(driveSectionH, txSectionH) + 10f;

        // [ADD] Manual ARTICULATED toggle — gates d8646art and every artic-
        // only electric/hydrogen tx option (zfave130/centeraxle variants).
        // Starts synced to the selected prefab (SyncArticulatedFromPrefab)
        // but can be flipped independently from here.
        bool articActive = _selArticulated;
        var articRect = new Rect(0, y, innerW, CHIP_H);
        if (DrawChip(articRect, articActive ? "Articulated: ON" : "Articulated: Off",
                     "Unlocks D864.6art, ZF AVE 130, and rear+centre-in-wheel drivetrains", articActive))
        {
            _selArticulated = !_selArticulated;
            var compat = GetCompatTXForCustom(_selEngine, _selBusIdx);
            if (Array.IndexOf(compat, _selTX) < 0) _selTX = compat.Length > 0 ? compat[0] : "b400r";
        }
        y += CHIP_H + CHIP_GAP + 4f;

        bool oldBusActive = _selOldBus;
        var oldBusRect = new Rect(0, y, innerW, CHIP_H);
        if (DrawChip(oldBusRect, oldBusActive ? "Old Bus: ON" : "Old Bus: Off",
                     "Worn character — creaks, rattles, delayed shifts", oldBusActive))
            _selOldBus = !_selOldBus;
        y += CHIP_H + CHIP_GAP + 4f;

        if (IsDiwaCompatible(_selEngine, _selTX))
        {
            y = DrawSectionHeader(y, innerW, "ENGINE VOICE OPTIONS (built-in — any transmission)");
            // [FIX] Was 7 slots (opt1-opt7 in order). Per instruction: opt3
            // and opt6 are retired entirely -- opt3 fully redirects to opt5
            // (opt5 stays as its own unchanged slot, opt3's slot is just
            // gone, not duplicated), and opt6 fully redirects to opt7 (same
            // deal -- opt7 remains, opt6's slot is deleted, not merged).
            // 5 real remaining slots now: opt1, opt2, opt4, opt5, opt7 --
            // all still wired to their ORIGINAL underlying flags, just no
            // longer including the two retired ones in the UI at all.
            // [FIX] Was 5 slots (opt1/opt2/opt4/opt5/opt7) -- Custom tab had
            // no way to set opt1_8 at all even though Fleet-tab buses could
            // carry it via FleetSeriesDefinition.GetDiwaVoiceFor. 6th slot
            // added here, wired straight to _selDiwaOpt1_8 like the others.
            y = DrawChipGrid(y, innerW, 6,
                i => i switch
                {
                    0 => "Delayed Whine",
                    1 => "Deep Overlay",
                    2 => "2nd Harmonic",
                    3 => "3rd Harmonic",
                    4 => "4th Harmonic",
                    _ => "5th Harmonic",
                },
                i => i switch
                {
                    0 => "✓ second whine voice ~0.5s behind rpm",
                    1 => "✓ low overlay, G1 only, ramps hard w/ rpm",
                    2 => "✓ suppressed whine til late G1, hiss window, extended G1, audible piston fire",
                    3 => "✓ no whine at all, deeper+louder core, rapid-fire click vibration-sim at idle",
                    4 => "✓ whine never hidden -- hear the Wandler directly, winds up on move-off, groan-only retarder",
                    _ => "✓ sixth DIWA voice",
                },
                i => i switch
                {
                    0 => _selDiwaOpt1_1,
                    1 => _selDiwaOpt1_2,
                    2 => _selDiwaOpt1_4,
                    3 => _selDiwaOpt1_5,
                    4 => _selDiwaOpt1_7,
                    _ => _selDiwaOpt1_8,
                },
                i =>
                {
                    if (i == 0) _selDiwaOpt1_1 = !_selDiwaOpt1_1;
                    else if (i == 1) _selDiwaOpt1_2 = !_selDiwaOpt1_2;
                    else if (i == 2) _selDiwaOpt1_4 = !_selDiwaOpt1_4;
                    else if (i == 3) _selDiwaOpt1_5 = !_selDiwaOpt1_5;
                    else if (i == 4) _selDiwaOpt1_7 = !_selDiwaOpt1_7;
                    else _selDiwaOpt1_8 = !_selDiwaOpt1_8;
                });
            y += 10f;
        }

        string txFull = _txLabel.TryGetValue(_selTX, out var ts) ? ts : _selTX;
        GUI.Label(new Rect(0, y, innerW, 40),
            $"<color=#00d4ff>{_selEngine}</color>  ·  {txFull}", _lblBody);

        GUI.EndScrollView();
    }

    // A "DRIVETRAIN" row for XE40 reads as active for BOTH XE40 and XE60
    // (it represents the whole BATTERY-ELECTRIC group) — same for XHE40
    // standing in for HYDROGEN-ELECTRIC. Every other row is a plain 1:1
    // EngineType match, unchanged from before.
    private bool IsDrivetrainRowActive(BusAudioEngine.EngineType row)
    {
        if (row == BusAudioEngine.EngineType.XE40)
            return _selEngine == BusAudioEngine.EngineType.XE40 || _selEngine == BusAudioEngine.EngineType.XE60;
        if (row == BusAudioEngine.EngineType.XHE40)
            return _selEngine == BusAudioEngine.EngineType.XHE40 || _selEngine == BusAudioEngine.EngineType.XHE60;
        return _selEngine == row;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PREVIEW PANEL
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawPreviewPanel(float w, float h)
    {
        float previewH = h * 0.58f;
        MDT_UITheme.DrawRect(new Rect(0, 0, w, previewH), new Color(0.04f, 0.04f, 0.05f, 1f));
        if (_previewRT != null)
            GUI.DrawTexture(new Rect(2, 2, w - 4, previewH - 4), _previewRT, ScaleMode.ScaleToFit, false);
        else
            GUI.Label(new Rect(0, previewH * 0.4f, w, 24), "[No Preview Camera]", _lblDim);

        float infoY = previewH + 8f;

        if (_tab == Tab.Fleet && fleetRoster != null
            && _selSeriesIdx >= 0 && _selSeriesIdx < fleetRoster.series.Count)
        {
            var def = fleetRoster.series[_selSeriesIdx];
            GUI.Label(new Rect(0, infoY, w, 20),
                _selFleetNum >= 0 ? $"Fleet #{_selFleetNum}" : "Pick a fleet number", _lblCyan);
            infoY += 22f;
            GUI.Label(new Rect(4, infoY, w-4, 18), $"{def.seriesName}  ·  {def.busType}  ·  {def.modelYear}", _lblDim); infoY += 20f;
            string txStr = _txLabel.TryGetValue(def.GetTxFor(0), out var tl) ? tl : def.GetTxFor(0);
            GUI.Label(new Rect(4, infoY, w-4, 18), $"{def.engineType}  ·  {txStr}", _lblDim); infoY += 20f;
            if (def.isArticulated) { GUI.Label(new Rect(4, infoY, w-4, 18), "Articulated", _lblAmber); infoY += 20f; }
            if (def.homeDepot != null) { GUI.Label(new Rect(4, infoY, w-4, 18), $"Depot: {def.homeDepot.depotName}", _lblDim); infoY += 20f; }

            if (_selFleetNum >= 0)
            {
                bool inScene = IsFleetNumberLive(_selFleetNum);
                Color sc = inScene ? MDT_UITheme.TextGreen : MDT_UITheme.TextAmber;
                GUI.Label(new Rect(4, infoY, w-4, 18),
                    inScene ? "● In scene — ready to possess" : "○ Not yet spawned",
                    MDT_UITheme.MakeLabel(9, FontStyle.Normal, TextAnchor.MiddleLeft, sc));
            }
        }
        else
        {
            foreach (var line in BuildCustomSpecLines())
            { GUI.Label(new Rect(4, infoY, w-4, 18), line, _lblDim); infoY += 19f; }
        }

        float honkY = h - 36f;
        MDT_UITheme.DrawRect(new Rect(0, honkY, w, 30f),
            _honking ? new Color(0.28f, 0.55f, 0.28f, 1f) : new Color(0.14f, 0.17f, 0.19f, 1f));
        if (GUI.Button(new Rect(0, honkY, w, 30f), _honking ? "HONK!" : "Honk", _btnSecond))
            TriggerHonk();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  FOOTER
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawFooter(Rect r)
    {
        MDT_UITheme.DrawRect(r, MDT_UITheme.BGHeader);
        float btnW = (r.width - 8f) * 0.5f;

        bool canCancel = !_firstOpen && _activeBus != null;
        GUI.enabled = canCancel;
        MDT_UITheme.DrawRect(new Rect(r.x, r.y+4, btnW, r.height-8), MDT_UITheme.BGButton);
        if (GUI.Button(new Rect(r.x, r.y+4, btnW, r.height-8), "CANCEL", _btnSecond))
        {
            ReleaseCurrentBus();
            CloseMenu();
        }
        GUI.enabled = true;

        bool canConfirm = _tab == Tab.Custom
            || (_selFleetNum >= 0 && IsFleetNumberLive(_selFleetNum));
        GUI.enabled = canConfirm;
        MDT_UITheme.DrawRect(new Rect(r.x + btnW + 8, r.y+4, btnW, r.height-8),
            canConfirm ? new Color(0.05f, 0.28f, 0.15f, 1f) : MDT_UITheme.BGMid);
        if (GUI.Button(new Rect(r.x + btnW + 8, r.y+4, btnW, r.height-8), "CONFIRM & DRIVE", _btnPrimary))
        {
            SavePrefs();
            ApplySelectionToSystems();
            _firstOpen = false;
            CloseMenu();
        }
        GUI.enabled = true;
    }

    private readonly HashSet<int> _liveFleetNumbersCache = new HashSet<int>();
    private float _liveFleetCacheAge = -999f;
    private const float LIVE_FLEET_CACHE_TTL = 1f;

    private void RefreshLiveFleetCache()
    {
        _liveFleetNumbersCache.Clear();
        foreach (var kv in BusRegistry.ActiveBuses)
            if (kv.Value != null) _liveFleetNumbersCache.Add(kv.Value.fleetNumber);

        if (_liveFleetNumbersCache.Count == 0)
        {
            foreach (var npc in FindObjectsByType<NPCBusController>(FindObjectsInactive.Include))
                if (npc != null) _liveFleetNumbersCache.Add(npc.fleetNumber);
        }
        _liveFleetCacheAge = Time.unscaledTime;
    }

    private bool IsFleetNumberLive(int fleetNumber)
    {
        if (Time.unscaledTime - _liveFleetCacheAge > LIVE_FLEET_CACHE_TTL)
            RefreshLiveFleetCache();
        return _liveFleetNumbersCache.Contains(fleetNumber);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  APPLY TO SYSTEMS
    // ═════════════════════════════════════════════════════════════════════════
    private void ApplySelectionToSystems()
    {
        if (_tab == Tab.Fleet) ApplyFleetPossession();
        else                   ApplyCustomSelection();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  RELEASE — single source of truth
    // ═════════════════════════════════════════════════════════════════════════
    /// <summary>[ADD] Public entry point for anything OUTSIDE this class that
    /// needs to hand a possessed bus back to its NPC -- specifically
    /// PlayerHandoff's EndShift/ReturnToOffDuty. Those used to only
    /// teleport the bus's transform home and never touched any of the
    /// component-level state ApplyFleetPossession set up (NPCBusController.
    /// enabled, BusAIBrain, colliders, tag, CityManager refs) since that
    /// state is private to THIS class -- the bus would end up teleported
    /// home but permanently inert (NPCBusController present but disabled
    /// forever) unless the player happened to also manually reopen this
    /// menu and hit release. This just exposes the existing, already-
    /// correct release logic instead of duplicating it a second time.</summary>
    public void ReleasePossessedBusToNPC() => ReleaseCurrentBus();

    private void ReleaseCurrentBus()
    {        // [FIX] Custom-tab buses have NO NPCBusController at all -- they're
        // raw spawned prefabs, not real fleet NPCs -- so none of the
        // possession-based audio gating a real fleet bus gets (via
        // NPCBusController.IsPlayerDrivingThisBus) applies to them. Nothing
        // here ever cleaned this up before: switching away from a custom
        // bus left it fully active in the scene, AudioSource included,
        // forever.
        //
        // [FIX] Immediately calling Destroy() right after disabling the
        // AudioSource is NOT safe on its own -- Destroy() is deferred to
        // end-of-frame, but FMOD's real-time audio thread can still be
        // mid-callback inside this exact object's OnAudioFilterRead at that
        // moment (a genuine, confirmed crash: SIGBUS / Data Abort inside
        // FMOD::DSPFilter::read -> AudioCustomFilter::readCallback while the
        // main thread simultaneously destroys the GameObject). Disabling
        // the AudioSource stops FMOD from scheduling any FUTURE callback on
        // it, but doesn't retroactively cancel one already in flight.
        // Deferring the actual Destroy() by one full frame (via the
        // coroutine below) gives that in-flight callback time to finish
        // naturally before the object disappears out from under it.
        // [FIX] Tracked so the GC/asset-unload sweep at the bottom of this
        // method only runs when this release actually destroyed something.
        // A normal Fleet-tab release just disables/re-enables components on
        // buses that already exist -- there's nothing orphaned to reclaim,
        // so paying a full Resources.UnloadUnusedAssets()+GC.Collect() sweep
        // on every single switch (the common case while testing) was a real,
        // repeated hitch for zero benefit. Only a Custom-tab bus getting
        // destroyed here actually leaves something behind worth collecting.
        bool destroyedGameObject = _spawnedFleetBus != null;

        if (_spawnedFleetBus != null)
        {
            foreach (var src in _spawnedFleetBus.GetComponentsInChildren<AudioSource>(true))
                src.enabled = false;
            StartCoroutine(DestroyNextFrame(_spawnedFleetBus));
            _spawnedFleetBus = null;
        }

        if (_activeBus != null)
        {
            _activeBus.stopPlayerPaxSystem = false;
            _activeBus.accelKey            = false;
            _activeBus.brakeKey            = false;
            if (_activeBus.audioEngine != null)
                _activeBus.audioEngine.kickdownKey = false;

            _activeBus.running      = false;
            _activeBus.parkingBrake = true;
            _activeBus.accel        = 0f;
            _activeBus.bkPd         = 0f;
            _activeBus.spd          = 0f;

            if (_activeBus.audioEngine != null)
                _activeBus.audioEngine.running = false;

            _activeBus.enabled = false;
        }

        var ph = PlayerHandoff.Instance;
        if (ph != null)
        {
            ph.playerBus = null;
            if (_possessedRoot != null && ph.transform.IsChildOf(_possessedRoot.transform))
                ph.transform.SetParent(_phOriginalParent, false);
        }

        // Declared here (not inside the block below) so it's still visible
        // in the controller-swap block further down, which needs to know
        // whether this release actually sent the bus home.
        DepotParkingSpot spot = null;

        if (_possessedRoot != null && _possessedNPC != null)
        {
            var releasedBoxColliders = _possessedRoot.GetComponentsInChildren<BoxCollider>(true);
            foreach (var bc in releasedBoxColliders)
                bc.enabled = true;

            var npcBrain = _possessedNPC.GetComponent<BusAIBrain>();
            if (npcBrain != null) npcBrain.enabled = true;

            var releasedRb = _possessedNPC.GetComponent<Rigidbody>();
            if (releasedRb != null)
            {
                releasedRb.isKinematic   = true;
                releasedRb.interpolation = RigidbodyInterpolation.Interpolate;
            }
            _possessedRoot.tag = _savedTag;

            // [ADD] Explicit disable, not just relying on NPCBusController's
            // own IsPlayerDrivingThisBus gate (which already silences the
            // OUTPUT correctly, but doesn't touch .enabled itself). Fleet
            // buses keep the component (unlike a destroyed custom bus)
            // since they need to keep functioning as normal NPCs -- this
            // just makes the release explicit rather than implicit.
            var releasedAudio = _possessedNPC.GetComponent<AudioSource>();
            if (releasedAudio != null) releasedAudio.enabled = false;

            // [ADD] Teleport the just-released bus straight back to an open
            // spot at its home depot -- same convention buses already use
            // spawning in the first place. Deliberately a straight
            // teleport, NOT routed through the existing dead-run-to-depot
            // AI (that system's real, but currently slow).
            //
            // [FIX Bug 2 follow-up] Used to call homeDepot.GetFreeSpot() and
            // write spot.occupied/occupiedByBusID directly -- a THIRD writer
            // to DepotData.parkingSpots alongside BusDepot's old table and
            // DepotManager's, keyed by busID (disposable) instead of
            // fleetNumber (persistent) like DepotManager's own convention.
            // Routes through DepotManager (sole owner as of the Bug 2 fix)
            // now. Combined with releasing this bus's own spot on possession
            // above, GetFreeSpot()/ClaimSpotFor should actually find a spot
            // now instead of the depot looking permanently full.
            if (_possessedNPC.homeDepot != null)
            {
                if (DepotManager.Instance != null)
                {
                    DepotManager.Instance.ClaimSpotFor(_possessedNPC.homeDepot, _possessedNPC.fleetNumber, out spot);
                }
                else
                {
                    // Fallback if DepotManager isn't in the scene at all.
                    spot = _possessedNPC.homeDepot.GetFreeSpot();
                    if (spot != null)
                    {
                        spot.occupied              = true;
                        spot.occupiedByFleetNumber = _possessedNPC.fleetNumber;
                    }
                }
            }

            // [FIX] This used to call ResyncToCurrentPosition() UNCONDITIONALLY,
            // before even attempting the teleport above -- resyncing the
            // NPC's route-segment/stop tracking to wherever the player
            // happened to leave it, and then immediately yanking the bus
            // away from that exact position to its depot spot. The resync
            // was instantly stale, and worse: ResumeAfterPossession only
            // resolves 4 specific coroutine-owned states (AtStop/AtTerminal/
            // TerminalIngress/TerminalEgress) -- a bus released while simply
            // InService (mid-drive, nowhere near a stop) fell through both
            // fixes untouched, got re-enabled with State still InService and
            // segment data that no longer matched its new depot position,
            // and tried to "drive" from there. That's the bus-controller-
            // swap-looks-broken symptom.
            //
            // Correct behavior: unpossessing sends the bus home and it
            // becomes properly idle there -- full stop, not a resumed drive.
            // Only fall back to resync-and-resume (the old behavior) in the
            // genuinely rare case there's nowhere to send it (no homeDepot,
            // or the depot has no free spot) -- there, staying put and
            // resuming from wherever it physically is is still the right
            // call.
            if (spot != null)
            {
                _possessedRoot.transform.SetPositionAndRotation(spot.position, Quaternion.Euler(spot.rotationEuler));
                if (releasedRb != null) releasedRb.linearVelocity = Vector3.zero;

                // Also release any TerminalIdleZone bay this bus might still
                // be holding (same leak ApplyFleetPossession's own fix
                // covers on the possess side -- covering it here too in case
                // one was somehow (re)claimed while possessed).
                _possessedNPC.ReleaseHeldIdleZoneBayForPossession();
                BusScheduler.Instance?.ReleaseSlotWithoutComplete(_possessedNPC.busID);
                _possessedNPC.SetIdle(true);
            }
            else
            {
                // No free spot: bus stays exactly where it was released --
                // better than teleporting into an occupied space. Should be
                // rare/never now that possession itself frees this bus's own
                // spot (see ApplyFleetPossession).
                _possessedNPC.ResyncToCurrentPosition();
            }
        }

        if (_possessedNPC != null)
        {
            // [ADD] Pairs with the FreeAgentBusIDs.Add in ApplyFleetPossession --
            // this bus is a normal fleet spare again the instant it's handed
            // back to AI, so the scheduler needs to be able to touch it.
            BusScheduler.FreeAgentBusIDs.Remove(_possessedNPC.busID);

            _possessedNPC.enabled = true;
            BusRegistry.ActiveBuses[_possessedNPC.busID] = _possessedNPC;

            if (spot != null)
            {
                // Teleported home and forced Idle above -- this is the one
                // place a bus actually gets added back to BusManager's idle
                // pool (see that method's own comment), which is what makes
                // it redispatchable again instead of orphaned.
                BusManager.Instance?.NotifyBusParkedAtDepot(_possessedNPC.busID);
            }
            else
            {
                // [FIX] Disabling this component above (well, right before
                // this block re-enables it) killed any StopDwell()/
                // TerminalDwell() coroutine it had running the moment it was
                // possessed. Re-enabling alone doesn't restart it —
                // AtStop/AtTerminal are only ever advanced by that
                // coroutine, so without this the bus just freezes there
                // forever. See ResumeAfterPossession's own comment. Only
                // relevant here on the no-spot fallback path -- the
                // teleport-home path above already forced a clean Idle
                // state itself, nothing left to resume.
                _possessedNPC.ResumeAfterPossession();
            }
        }

        var cm = CityManager.Instance;
        if (cm != null)
        {
            cm.busTransform  = null;
            cm.busController = null;
        }

        _possessedNPC  = null;
        _possessedRoot = null;
        _activeBus     = null;

        DriverConsole.Instance?.RefreshStopHUD();
        StopHUD.Instance?.Refresh();
        EnforceSingleAudioListenerNow();

        // [ADD — ticket 18] This is the one real chokepoint for "player
        // transferred to a different bus OR lost their bus entirely" --
        // every release path funnels through here (explicit menu release,
        // EndShift/ReturnToOffDuty via PlayerHandoff.ReleasePossessedBusToNPC,
        // and a relief-swap transfer, since adopting a NEW bus always
        // releases the old one first). It's exactly the kind of safe
        // "downtime" moment ticket 18 called out -- no real-time gameplay
        // is happening mid-menu-transaction, so a GC pause here is far
        // cheaper to eat than one that lands mid-drive. GC.Collect() only
        // reclaims managed heap garbage (the ticket's own caveat: it does
        // NOT touch native/GPU memory), so it's paired with
        // Resources.UnloadUnusedAssets() to also drop any texture/audio/
        // material assets that lost their last reference in the release
        // work just above (e.g. the released custom bus's destroyed
        // GameObject). Debug.ClearDeveloperConsole() is deliberately NOT
        // included here -- per the ticket 18 correction, it only wipes the
        // visible console text, it doesn't free any RAM, so including it
        // would just be theater.
        //
        // [FIX] Gated on destroyedGameObject -- this sweep is only worth its
        // cost when something was actually destroyed this release (a Custom-
        // tab bus). Every ordinary Fleet-tab switch was paying this same
        // full GC pause for nothing to reclaim, which is a real, repeated
        // hitch during normal bus-cycling/testing.
        if (destroyedGameObject)
        {
            Resources.UnloadUnusedAssets();
            System.GC.Collect();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  FLEET — POSSESS
    // ═════════════════════════════════════════════════════════════════════════
    private void ApplyFleetPossession(bool adoptExistingSlot = true)
    {
        if (fleetRoster == null
            || _selSeriesIdx < 0 || _selSeriesIdx >= fleetRoster.series.Count
            || _selFleetNum < 0)
            return;

        var def = fleetRoster.series[_selSeriesIdx];

        NPCBusController targetNPC = FindNPCByFleetNumber(_selFleetNum);
        if (targetNPC == null)
        {
            DriverConsole.Instance?.Print($"Fleet #{_selFleetNum} isn't spawned yet — pick another bus.");
            return;
        }

        if (_possessedNPC == targetNPC) return;

        // [ADD — root cause of the fleet #2709 / Route 140 bug] Nothing in
        // this method ever checked whether the depot board's pick is still
        // scheduler-committed. isIdle (in BusManager/MainMenu's board
        // filter) is derived purely from NPCBusController.State -- a bus
        // that's physically parked but still carrying a live slot chain
        // (e.g. auto-adopted for a route it hasn't started dead-running to
        // yet) reads as "idle" and gets offered as a free depot pick, and
        // this method would happily hand it to the player, silently
        // dragging that stale chain along (adoptExistingSlot: true) or
        // abandoning it live in the scheduler forever (adoptExistingSlot:
        // false -- see that branch's own comment: "no slot/shift-state side
        // effects"). Either way the player ends up entangled with a route
        // they never asked for. Check BusScheduler directly -- the actual
        // source of truth for "is this bus committed to something" -- and
        // release any live, non-completed slot on it before we touch
        // anything else. ReleaseSlotWithoutComplete (already used elsewhere
        // in this file, e.g. ReleaseCurrentBus) is the correct call here:
        // it tears down the chain without marking it falsely completed.
        if (BusScheduler.Instance != null
            && BusScheduler.Instance.TryGetAssignedSlot(targetNPC.busID, out var staleSlot)
            && staleSlot != null && staleSlot.state != SlotState.Completed)
        {
            Debug.LogWarning($"[BusSelectMenu] Fleet #{_selFleetNum} (Bus#{targetNPC.busID}) was offered as " +
                              $"idle/available but still holds a live scheduler slot for Route " +
                              $"{staleSlot.routeNumber} -- releasing it before possession instead of " +
                              $"silently dragging it along or abandoning it live in the scheduler.");
            BusScheduler.Instance.ReleaseSlotWithoutComplete(targetNPC.busID);
        }

        // [FIX] This never called ReleaseCurrentBus() at all -- unlike
        // ApplyCustomSelection, which does. Switching from a Custom-tab
        // bus to a Fleet bus (e.g. via the shift board) left the old
        // custom bus object fully active in the scene forever, AudioSource
        // included, since a custom bus has no NPCBusController to fall
        // back on for possession-based audio gating the way a real fleet
        // NPC does. Placed AFTER the same-bus early-return above -- calling
        // it unconditionally at the top of this method would have
        // incorrectly torn down an already-correctly-possessed bus when
        // re-selecting the same one in the picker.
        ReleaseCurrentBus();

        // [FIX] Was called twice in a row here (copy-paste). Harmless as
        // written -- _possessedNPC is already null after the first call, so
        // every guarded block inside the second call is a no-op -- but
        // clearly unintentional, removed.

        // [FIX — root cause of "bus never returns to depot on unpossess"]
        // Nothing anywhere released THIS bus's own home parking spot when
        // it got possessed. It stays flagged occupied in DepotData the
        // entire time the player drives it away from that spot. Later,
        // ReleaseCurrentBus() (below) asks homeDepot.GetFreeSpot() for a
        // FRESH free spot to put it back in -- but in a normally-populated
        // depot (every spot filled at spawn, which is the point of
        // DepotManager.BuildAndAssignFleet) there are never any free spots
        // at all, because this bus's own was never released. GetFreeSpot()
        // came back null essentially every time, silently hitting the
        // "no free spot, bus stays exactly where it was released" fallback
        // in ReleaseCurrentBus() -- which is why ending a shift / releasing
        // the bus looked like it did nothing. Release through DepotManager
        // (sole owner as of the Bug 2 fix), keyed by fleetNumber.
        if (DepotManager.Instance != null && targetNPC.homeDepot != null)
            DepotManager.Instance.ReleaseSpotFor(targetNPC.fleetNumber);

        // [FIX] Separate leak from the depot-spot one above -- an in-service
        // bus caught sitting in a TerminalIdleZone bay between legs (State
        // AtTerminal/TerminalIngress/WaitingAtDepot) still holds that bay
        // claim. Disabling NPCBusController below kills whatever coroutine
        // would have eventually released it, orphaning the bay permanently
        // (marked occupied forever, nothing left to ever clear it) unless
        // released explicitly here first. See the method's own comment.
        targetNPC.ReleaseHeldIdleZoneBayForPossession();

        _possessedNPC  = targetNPC;
        _possessedRoot = targetNPC.transform.root.gameObject;
        _savedTag      = _possessedRoot.tag;

        // [ADD — root cause of the "teleported mid-drive" bug] Nothing here
        // ever told the scheduler this physical busID is off-limits while
        // possessed. FreeAgentBusIDs already exists for exactly this --
        // CanAssign and BusManager's idle-bus search both refuse to touch a
        // busID in this set -- it's just never been added to for a Fleet-tab
        // possession, only for CHIP-type free agents. Without it, a SECOND,
        // separate rotation this same busID picks up later (either a
        // pre-existing one MoveChainToPlayer deliberately leaves behind, or
        // a fresh one CompleteSlot/TopUpChain hands out while this bus reads
        // as available) sits there fully live in the scheduler. The
        // player's own slot (PLAYER_BUS_ID) is totally unaffected by any of
        // this -- it's the disabled NPCBusController's OWN busID being
        // dispatched for ITS OWN scheduled departure that hard-repositions
        // the shared rig out from under the player once that time comes.
        BusScheduler.FreeAgentBusIDs.Add(targetNPC.busID);

        targetNPC.enabled = false;
        var npcRb = targetNPC.GetComponent<Rigidbody>();
        if (npcRb != null) { npcRb.linearVelocity = Vector3.zero; npcRb.isKinematic = true; npcRb.interpolation = RigidbodyInterpolation.None; }
        BusRegistry.ActiveBuses.Remove(targetNPC.busID);

        var bus = _possessedRoot.GetComponentInChildren<BusSimulationController>();
        if (bus == null)
        {
            targetNPC.enabled = true;
            BusRegistry.ActiveBuses[targetNPC.busID] = targetNPC;
            _possessedNPC = null; _possessedRoot = null;
            return;
        }
        bus.enabled = true;
        var npcBrain = targetNPC.GetComponent<BusAIBrain>();
        if (npcBrain != null) npcBrain.enabled = false;

        foreach (var mr in _possessedRoot.GetComponentsInChildren<MeshRenderer>(true)) mr.enabled = true;
        foreach (var smr in _possessedRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.enabled = true;

        // [REVERTED] I previously changed this to skip BoxColliders, on the
        // theory that they're the bus's persistent "detection hull" (based
        // on ReleaseCurrentBus() re-enabling them specifically on release).
        // That theory doesn't hold up: ApplyCustomSelection (below in this
        // file) ALSO deliberately disables BoxColliders on possession, via
        // an entirely separate code path, and NPCBusController.cs documents
        // real self-collision problems with articulated hitches fighting
        // the custom movement code. Without BusSimulationController.cs (not
        // provided) to confirm what these BoxColliders physically represent
        // on the rig, changing this was a guess I shouldn't have shipped.
        // Reverted to original behavior pending that file.
        foreach (var col in _possessedRoot.GetComponentsInChildren<Collider>(true)) col.enabled = false;

        var busAudioSrc = bus.GetComponent<AudioSource>();
        if (busAudioSrc != null) { busAudioSrc.enabled = true; if (!busAudioSrc.isPlaying) busAudioSrc.Play(); }

        string txForBus = def.GetTxFor(_selFleetNum - def.startFleetNumber);
        var diwaVoice   = def.GetDiwaVoiceFor(_selFleetNum);
        bus.engineType          = def.engineType;
        bus.tx                  = txForBus;
        bus.diwaOpt1_1          = diwaVoice.opt1_1;
        bus.diwaOpt1_2          = diwaVoice.opt1_2;
        bus.diwaOpt1_3          = diwaVoice.opt1_3;
        bus.diwaOpt1_4          = diwaVoice.opt1_4;
        bus.diwaOpt1_5          = diwaVoice.opt1_5;
        bus.diwaOpt1_6          = diwaVoice.opt1_6;
        bus.diwaOpt1_7          = diwaVoice.opt1_7;
        bus.diwaOpt1_8          = diwaVoice.opt1_8;
        bus.ApplyEngineConstants();
        bus.stopPlayerPaxSystem = true;
        bus.running             = true;
        bus.parkingBrake        = false;
        bus.accel               = 0f;
        bus.bkPd                = 0f;
        bus.spd                 = 0f;
        bus.currentDirection    = BusSimulationController.GearDirection.Drive;

        if (bus.audioEngine != null)
        {
            bus.audioEngine.engineType = (BusAudioEngine.EngineType)(int)def.engineType;
            bus.audioEngine.tx         = txForBus;
            bus.audioEngine.diwaOpt1_1 = diwaVoice.opt1_1;
            bus.audioEngine.diwaOpt1_2 = diwaVoice.opt1_2;
            bus.audioEngine.diwaOpt1_3 = diwaVoice.opt1_3;
            bus.audioEngine.diwaOpt1_4 = diwaVoice.opt1_4;
            bus.audioEngine.diwaOpt1_5 = diwaVoice.opt1_5;
            bus.audioEngine.diwaOpt1_6 = diwaVoice.opt1_6;
            bus.audioEngine.diwaOpt1_7 = diwaVoice.opt1_7;
            bus.audioEngine.diwaOpt1_8 = diwaVoice.opt1_8;
            bus.audioEngine.ApplyEngineConstants();
            bus.audioEngine.running    = true;
        }

        _activeBus = bus;
        _possessedRoot.tag = "Player";

        // [FIX] Computed here (was further down, after the adopt-slot call) so the
        // completion callback below can close over it. On a network client,
        // AdoptInServiceSlot's real scheduler transfer is an RPC round-trip -- this
        // value has to be captured now rather than read after the call returns.
        string txFull = _txLabel.TryGetValue(txForBus, out var tl) ? tl : txForBus;

        var ph = PlayerHandoff.Instance;
        if (ph != null)
        {
            ph.transform.SetParent(_possessedRoot.transform, false);
            ph.transform.localPosition = Vector3.zero;

            if (adoptExistingSlot)
            {
                // Sets playerBus + FleetNumber and transfers the NPC's active
                // scheduler slot to PLAYER_BUS_ID — single identity end to end.
                // [FIX] On a network client this is async (an RPC round-trip to the
                // host), unlike single-player/host where it resolves synchronously
                // inside this same call. Checking ph.IsOnDuty right after the call
                // returns (the old code, below) was correct for single-player/host
                // but would read STALE state on a client -- the round-trip hasn't
                // come back yet, so it would always look like adoption failed and
                // print the wrong "Join a route to begin" message even when the
                // adopt was about to succeed. The completion callback fires at the
                // right time either way: synchronously here for single-player/host,
                // after the round-trip for a client.
                ph.AdoptInServiceSlot(targetNPC.busID, bus, targetNPC, success =>
                {
                    // AdoptInServiceSlot (or its networked counterpart) already
                    // prints its own "Possessed Fleet #..." line and puts the
                    // player on duty when the scheduler transfer succeeds -- a
                    // second "ready" line here would just be redundant noise.
                    // Only the failure case (adoption fell back to Free Drive
                    // via ResetForFreshPossession) needs the "join a route"
                    // prompt, since PlayerHandoff never printed one for that.
                    if (!success)
                    {
                        DriverConsole.Instance?.Print(
                            $"Bus ready — Fleet #{_selFleetNum} / {def.engineType} / {txFull}. Join a route to begin.");
                    }
                });
            }
            else
            {
                // Caller (ShiftRunner.ClaimRouteAndBus, via the board claim
                // flow) owns the slot transfer + join itself right after this
                // returns, via JoinTransferredSlot/JoinRouteWithSlot -- the
                // call that actually sets DeadrunToStart and prints the
                // "dead-run to terminal" prompt. AdoptInServiceSlot would beat
                // it to InService if the targeted NPC still has any slot
                // assigned to its busID, silently skipping the deadrun and
                // blocking the real join call behind the "already on duty"
                // guard. Just bind the bus body here -- no slot/shift-state
                // side effects.
                ph.SetPlayerBus(bus);
                ph.SetFleetNumber(_selFleetNum);
            }
        }

        var cm = CityManager.Instance;
        if (cm != null)
        {
            cm.busTransform  = _possessedRoot.transform;
            cm.busController = bus;
        }

        var followTarget = bus.frontPivot != null ? bus.frontPivot : _possessedRoot.transform;
        SetupCameraFollow(followTarget);
        EnforceSingleAudioListenerNow();

        DriverConsole.Instance?.RefreshStopHUD();
        StopHUD.Instance?.Refresh();
        if (MDT_LiveMap.Instance != null)
        {
            var dirty = typeof(MDT_LiveMap).GetField("_dataDirty",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            dirty?.SetValue(MDT_LiveMap.Instance, true);
        }

        // Board-claim flow (adoptExistingSlot == false): the caller is about to
        // call JoinTransferredSlot/JoinRouteWithSlot right after this returns,
        // which prints its own "Route confirmed — dead-run to Terminal" line.
        // Printing a generic "ready" line here too is just redundant noise
        // ahead of that. The adoptExistingSlot == true case's messaging is
        // handled by the AdoptInServiceSlot completion callback above instead
        // of here, since that call may now be asynchronous (see the callback's
        // own comment).
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CUSTOM — APPLY
    // ═════════════════════════════════════════════════════════════════════════
    /// <summary>[ADD] Waits one full frame before destroying -- gives FMOD's
    /// real-time audio render thread a chance to finish any in-flight
    /// OnAudioFilterRead callback on this object naturally (its AudioSource
    /// is already disabled by the caller, so no NEW callback gets scheduled
    /// in the meantime) before the GameObject actually disappears. See
    /// ReleaseCurrentBus's own comment for the exact crash this prevents.</summary>
    private IEnumerator DestroyNextFrame(GameObject go)
    {
        yield return null;
        if (go != null) Destroy(go);
    }

    private void ApplyCustomSelection()
    {
        ReleaseCurrentBus();

        if (_spawnedFleetBus != null) { Destroy(_spawnedFleetBus); _spawnedFleetBus = null; }

        if (busPool.Count == 0) return;
        for (int i = 0; i < busPool.Count; i++)
            if (busPool[i] != null) busPool[i].SetActive(i == _selBusIdx);

        var chosenRoot = _selBusIdx < busPool.Count ? busPool[_selBusIdx] : null;
        if (chosenRoot == null) return;

        var bus = chosenRoot.GetComponentInChildren<BusSimulationController>();
        if (bus == null) return;

        _possessedRoot = chosenRoot;
        _savedTag      = _possessedRoot.tag;

        bus.enabled = true;
        foreach (var mr in _possessedRoot.GetComponentsInChildren<MeshRenderer>(true)) mr.enabled = true;
        foreach (var smr in _possessedRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.enabled = true;

        var possessedBoxColliders = _possessedRoot.GetComponentsInChildren<BoxCollider>(true);
        foreach (var bc in possessedBoxColliders) bc.enabled = false;

        var busAudioSrc = bus.GetComponent<AudioSource>();
        if (busAudioSrc != null) { busAudioSrc.enabled = true; if (!busAudioSrc.isPlaying) busAudioSrc.Play(); }

        var engineType = (BusSimulationController.EngineType)(int)_selEngine;
        bool diwaCompat = IsDiwaCompatible(_selEngine, _selTX);
        bus.engineType          = engineType;
        bus.tx                  = _selTX;
        bus.diwaOpt1_1          = diwaCompat && _selDiwaOpt1_1;
        bus.diwaOpt1_2          = diwaCompat && _selDiwaOpt1_2;
        bus.diwaOpt1_3          = diwaCompat && _selDiwaOpt1_3;
        bus.diwaOpt1_4          = diwaCompat && _selDiwaOpt1_4;
        bus.diwaOpt1_5          = diwaCompat && _selDiwaOpt1_5;
        bus.diwaOpt1_6          = diwaCompat && _selDiwaOpt1_6;
        bus.diwaOpt1_7          = diwaCompat && _selDiwaOpt1_7;
        bus.diwaOpt1_8          = diwaCompat && _selDiwaOpt1_8;
        bus.ApplyEngineConstants();
        bus.stopPlayerPaxSystem = true;
        bus.running             = true;
        bus.parkingBrake        = false;
        bus.accel               = 0f;
        bus.bkPd                = 0f;
        bus.spd                 = 0f;
        _activeBus = bus;

        if (bus.audioEngine != null)
        {
            bus.audioEngine.engineType = (BusAudioEngine.EngineType)(int)engineType;
            bus.audioEngine.tx         = _selTX;
            bus.audioEngine.diwaOpt1_1 = diwaCompat && _selDiwaOpt1_1;
            bus.audioEngine.diwaOpt1_2 = diwaCompat && _selDiwaOpt1_2;
            bus.audioEngine.diwaOpt1_3 = diwaCompat && _selDiwaOpt1_3;
            bus.audioEngine.diwaOpt1_4 = diwaCompat && _selDiwaOpt1_4;
            bus.audioEngine.diwaOpt1_5 = diwaCompat && _selDiwaOpt1_5;
            bus.audioEngine.diwaOpt1_6 = diwaCompat && _selDiwaOpt1_6;
            bus.audioEngine.diwaOpt1_7 = diwaCompat && _selDiwaOpt1_7;
            bus.audioEngine.diwaOpt1_8 = diwaCompat && _selDiwaOpt1_8;
            bus.audioEngine.ApplyEngineConstants();
            bus.audioEngine.running    = true;
            bus.audioEngine.oldBus     = _selOldBus;
        }

        chosenRoot.tag = "Player";

        var ph = PlayerHandoff.Instance;
        if (ph != null)
        {
            if (ph.transform.parent != chosenRoot.transform)
                ph.transform.SetParent(chosenRoot.transform, false);
            // No NPC slot to adopt — just clears/resets shift state and sets
            // playerBus/FleetNumber. Player identity in the scheduler is
            // untouched until they `join` a route.
            ph.ResetForFreshPossession(bus, displayFleetNumber: -1);
        }

        var cm = CityManager.Instance;
        if (cm != null)
        {
            cm.busTransform  = chosenRoot.transform;
            cm.busController = bus;
        }

        SetupCameraFollow(chosenRoot.transform);
        EnforceSingleAudioListenerNow();

        DriverConsole.Instance?.RefreshStopHUD();
        StopHUD.Instance?.Refresh();

        string txFull = _txLabel.TryGetValue(_selTX, out var tl) ? tl : _selTX;
        DriverConsole.Instance?.Print($"Bus ready — {engineType} / {txFull}. Join a route to begin.");

        if (bus.audioEngine != null)
        {
            var audioBridge = bus.gameObject.GetComponent<AudioKickdownBridge>();
            if (audioBridge == null) audioBridge = bus.gameObject.AddComponent<AudioKickdownBridge>();
            audioBridge.audioEngine = bus.audioEngine;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC ENTRY POINT — used by DispatchConsole
    // ═════════════════════════════════════════════════════════════════════════
    public void PossessFleetNumber(int fleetNumber, bool adoptExistingSlot = true)
    {
        if (fleetRoster == null || fleetRoster.series == null) return;

        for (int i = 0; i < fleetRoster.series.Count; i++)
        {
            var s = fleetRoster.series[i];
            if (fleetNumber >= s.startFleetNumber && fleetNumber < s.startFleetNumber + s.busCount)
            {
                _selSeriesIdx = i;
                _selFleetNum  = fleetNumber;
                _tab          = Tab.Fleet;
                ApplyFleetPossession(adoptExistingSlot);
                return;
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  SCENE NPC LOOKUP
    // ═════════════════════════════════════════════════════════════════════════
    private NPCBusController FindNPCByFleetNumber(int fleetNumber)
    {
        foreach (var kv in BusRegistry.ActiveBuses)
            if (kv.Value != null && kv.Value.fleetNumber == fleetNumber)
                return kv.Value;

        foreach (var npc in FindObjectsByType<NPCBusController>(FindObjectsInactive.Include))
            if (npc.fleetNumber == fleetNumber)
                return npc;

        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PREVIEW
    // ═════════════════════════════════════════════════════════════════════════
    private void RefreshPreview()
    {
        DestroyPreviewInstance();

        if (_tab == Tab.Fleet && fleetRoster != null
            && _selSeriesIdx >= 0 && _selSeriesIdx < fleetRoster.series.Count)
        {
            var def = fleetRoster.series[_selSeriesIdx];
            if (def.prefab != null)
            {
                _previewInstance = Instantiate(def.prefab, new Vector3(99999f, 0f, 99999f), Quaternion.identity);
                _previewInstance.name = "__PreviewBus__";

                var prevNPC = _previewInstance.GetComponentInChildren<NPCBusController>();
                if (prevNPC != null) prevNPC.enabled = false;

                var prevBSC = _previewInstance.GetComponentInChildren<BusSimulationController>();
                if (prevBSC != null) prevBSC.running = false;

                SetLayerRecursive(_previewInstance, GetOrCreatePreviewLayer());
            }
        }
        else if (_tab == Tab.Custom)
        {
            for (int i = 0; i < busPool.Count; i++)
                if (busPool[i] != null)
                    SetLayerRecursive(busPool[i], i == _selBusIdx ? GetOrCreatePreviewLayer() : 31);
        }
    }

    private void DestroyPreviewInstance()
    {
        if (_previewInstance != null) { Destroy(_previewInstance); _previewInstance = null; }
        foreach (var b in busPool) if (b != null) SetLayerRecursive(b, 0);
    }

    private int GetOrCreatePreviewLayer() => 8;

    private void SetupPreviewRT()
    {
        if (previewCamera == null) return;
        _previewRT = new RenderTexture(previewResW, previewResH, 16);
        _previewRT.Create();
        previewCamera.targetTexture = _previewRT;
        previewCamera.cullingMask   = 1 << GetOrCreatePreviewLayer();
    }

    private void UpdatePreviewCamera()
    {
        GameObject subject = _previewInstance;
        if (subject == null && _tab == Tab.Custom && busPool.Count > _selBusIdx)
            subject = busPool[_selBusIdx];
        if (subject == null) return;

        var b = GetBounds(subject);
        float angle = Time.time * 12f;
        Vector3 dir = new Vector3(Mathf.Sin(angle * Mathf.Deg2Rad), 0.35f, Mathf.Cos(angle * Mathf.Deg2Rad)).normalized;
        previewCamera.transform.position = b.center + dir * b.extents.magnitude * 2.6f;
        previewCamera.transform.LookAt(b.center + Vector3.up * 0.5f);
    }

    private void HideAllCustomBuses()
    {
        foreach (var b in busPool) if (b != null) SetLayerRecursive(b, 0);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CAMERA FOLLOW
    // ═════════════════════════════════════════════════════════════════════════
    public void SetupCameraFollow(Transform busRoot)
    {
        if (mainCamera == null) return;

        var cf25 = cameraFollowScript as CameraFollow25D ?? mainCamera.GetComponent<CameraFollow25D>();
        if (cf25 != null)
        {
            cf25.targetBus  = busRoot;
            cf25.orbitYaw   = busRoot.eulerAngles.y;
            cf25.orbitPitch = 20f;

            typeof(CameraFollow25D)
                .GetField("mode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(cf25, 0);
            typeof(CameraFollow25D)
                .GetField("_prevBusYaw", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.SetValue(cf25, busRoot.eulerAngles.y);
            return;
        }

        if (cameraFollowScript != null)
        {
            var type = cameraFollowScript.GetType();
            foreach (var fname in new[] { "targetBus", "target", "followTarget" })
            {
                var f = type.GetField(fname);
                if (f != null && f.FieldType == typeof(Transform)) { f.SetValue(cameraFollowScript, busRoot); return; }
                var p = type.GetProperty(fname);
                if (p != null && p.CanWrite && p.PropertyType == typeof(Transform)) { p.SetValue(cameraFollowScript, busRoot); return; }
            }
        }

        mainCamera.transform.SetParent(busRoot, false);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  HONK
    // ═════════════════════════════════════════════════════════════════════════
    private void SetupHonkAudio()
    {
        if (_honkSrc != null) return;
        var go = new GameObject("BusHonk"); go.transform.SetParent(transform);
        _honkSrc = go.AddComponent<AudioSource>();
        _honkSrc.spatialBlend = 0f; _honkSrc.loop = true;
        _honkSrc.clip = AudioClip.Create("HonkSynth", 44100, 1, 44100, false);
        _honkSrc.volume = 0.85f;
        _honkSR = AudioSettings.outputSampleRate;
        go.AddComponent<HonkDSP>().menu = this;
    }

    private void EnforceSingleAudioListenerNow()
    {
        var listeners = FindObjectsByType<AudioListener>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var al in listeners)
        {
            bool keep = mainCamera != null && al.gameObject == mainCamera.gameObject;
            if (keep) { al.enabled = true; continue; }
            Destroy(al);
        }
        if (mainCamera != null && mainCamera.GetComponent<AudioListener>() == null)
            mainCamera.gameObject.AddComponent<AudioListener>();
    }

    public void TriggerHonk()
    {
        _honking = true; _honkTimer = honkDurationSec;
        if (_honkSrc != null && !_honkSrc.isPlaying) _honkSrc.Play();
    }

    public void FillHonkBuffer(float[] data, int channels)
    {
        double invSR = 1.0 / _honkSR;
        float  env   = _honking ? 1f : 0f;
        for (int i = 0; i < data.Length; i += channels)
        {
            double mix = Math.Tanh(
                (Math.Sin(2.0 * Math.PI * _honkPh1) * 0.6
               + Math.Sin(2.0 * Math.PI * _honkPh2) * 0.5
               + Math.Sin(2.0 * Math.PI * _honkPh3) * 0.35) * 1.4);
            float smp = (float)(mix * 0.4f * env);
            for (int c = 0; c < channels; c++) data[i + c] = smp;
            _honkPh1 = (_honkPh1 + 220.0 * invSR) % 1.0;
            _honkPh2 = (_honkPh2 + 277.0 * invSR) % 1.0;
            _honkPh3 = (_honkPh3 + 370.0 * invSR) % 1.0;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PREFS
    // ═════════════════════════════════════════════════════════════════════════
    private void SavePrefs()
    {
        PlayerPrefs.SetInt   ("BSM_Tab",      (int)_tab);
        PlayerPrefs.SetInt   ("BSM_Series",   _selSeriesIdx);
        PlayerPrefs.SetInt   ("BSM_FleetNum", _selFleetNum);
        PlayerPrefs.SetInt   ("BSM_Engine",   (int)_selEngine);
        PlayerPrefs.SetString("BSM_TX",       _selTX);
        PlayerPrefs.SetInt   ("BSM_BusIdx",   _selBusIdx);
        PlayerPrefs.SetInt   ("BSM_OldBus",   _selOldBus ? 1 : 0);
        PlayerPrefs.SetInt   ("BSM_Diwa1_1",  _selDiwaOpt1_1 ? 1 : 0);
        PlayerPrefs.SetInt   ("BSM_Diwa1_2",  _selDiwaOpt1_2 ? 1 : 0);
        PlayerPrefs.SetInt   ("BSM_Diwa1_3",  _selDiwaOpt1_3 ? 1 : 0);
        PlayerPrefs.SetInt   ("BSM_Diwa1_4",  _selDiwaOpt1_4 ? 1 : 0);
        PlayerPrefs.SetInt   ("BSM_Diwa1_5",  _selDiwaOpt1_5 ? 1 : 0);
        PlayerPrefs.SetInt   ("BSM_Diwa1_6",  _selDiwaOpt1_6 ? 1 : 0);
        PlayerPrefs.SetInt   ("BSM_Diwa1_7",  _selDiwaOpt1_7 ? 1 : 0);
        PlayerPrefs.SetInt   ("BSM_Diwa1_8",  _selDiwaOpt1_8 ? 1 : 0);
        PlayerPrefs.Save();
    }

    private void LoadPrefs()
    {
        _tab          = (Tab)PlayerPrefs.GetInt("BSM_Tab", 0);
        _selSeriesIdx = PlayerPrefs.GetInt("BSM_Series", 0);
        _selFleetNum  = PlayerPrefs.GetInt("BSM_FleetNum", -1);
        _selEngine    = (BusAudioEngine.EngineType)PlayerPrefs.GetInt("BSM_Engine", 0);
        _selTX        = PlayerPrefs.GetString("BSM_TX", "b400r");
        _selOldBus    = PlayerPrefs.GetInt("BSM_OldBus", 0) == 1;
        _selDiwaOpt1_1 = PlayerPrefs.GetInt("BSM_Diwa1_1", 0) == 1;
        _selDiwaOpt1_2 = PlayerPrefs.GetInt("BSM_Diwa1_2", 0) == 1;
        _selDiwaOpt1_3 = PlayerPrefs.GetInt("BSM_Diwa1_3", 0) == 1;
        _selDiwaOpt1_4 = PlayerPrefs.GetInt("BSM_Diwa1_4", 0) == 1;
        _selDiwaOpt1_5 = PlayerPrefs.GetInt("BSM_Diwa1_5", 0) == 1;
        _selDiwaOpt1_6 = PlayerPrefs.GetInt("BSM_Diwa1_6", 0) == 1;
        _selDiwaOpt1_7 = PlayerPrefs.GetInt("BSM_Diwa1_7", 0) == 1;
        _selDiwaOpt1_8 = PlayerPrefs.GetInt("BSM_Diwa1_8", 0) == 1;

        if (_selTX == "allison") _selTX = "b400r";
        // [MIGRATE] Old saves may still have pre-rework tx strings — map
        // them onto their real successors.
        if (_selTX == "electric") _selTX = "elfa3";
        else if (_selTX == "fcelfa") _selTX = "elfa2";
        // [MIGRATE] Briefly-wrong names from an earlier pass that
        // incorrectly grafted "zfave130" onto the Accelera/ELFA3 name —
        // corrected to the real centre-in-wheel naming.
        else if (_selTX == "elfa3_zfave130") _selTX = "elfa3_centeraxle";
        else if (_selTX == "accelera_zfave130") _selTX = "accelera_centeraxle";
        else if (_selTX == "elfa2_zfave130") _selTX = "elfa2_centeraxle";
        else if (_selTX == "accelera_fc_zfave130") _selTX = "accelera_fc_centeraxle";
        // [MIGRATE] Bare "zfave130"/"fcave130" (axle with no drive system
        // attached) never made sense as a standalone pick and is no longer
        // offered — old saves land on the Accelera centre-axle variant.
        else if (_selTX == "zfave130") _selTX = "accelera_centeraxle";
        else if (_selTX == "fcave130") _selTX = "accelera_fc_centeraxle";

        // [MIGRATE] "voith"/"voith35"/"d8646art" all unified into plain
        // "d8646" -- articulated response is now handled by the isArticulated
        // flag directly inside DoVoithDSP instead of a separate tx string.
        if (_selTX == "voith" || _selTX == "voith35" || _selTX == "d8646art") _selTX = "d8646";
        // [MIGRATE] "zh50ep"/"h50ep_gen5" folded back into plain "h50ep" --
        // both were removed as separate identities per the family/variant
        // rework (mechanically the same H50EP drive unit).
        else if (_selTX == "zh50ep" || _selTX == "h50ep_gen5") _selTX = "h50ep";
        // [MIGRATE] "baegen3" was never part of the real fleet's tx system --
        // falls back to plain BAE HDS200.
        else if (_selTX == "baegen3") _selTX = "bae";
        // [MIGRATE] "nxt" (Voith DIWA NXT) was dropped from the real compat
        // lists -- falls back to d8646 (same manufacturer lineage).
        // [RESTORE] zfel2/zfel2_hd migration removed -- both are back in
        // the real compat lists (see _compatTX), so old saves referencing
        // them now resolve normally instead of getting rewritten to gen-1.
        else if (_selTX == "nxt") _selTX = "d8646";

        // [MIGRATE] The Crystal Bay "siemenscng" series-hybrid experiment has
        // been removed entirely (no more genset/motor DSP backing it). Old
        // saves referencing it fall back to plain D864.6 rather than hitting
        // the compat-list reset below with a dangling tx string.
        else if (_selTX == "siemenscng") _selTX = "d8646";

        _selBusIdx = PlayerPrefs.GetInt("BSM_BusIdx", 0);

        if (fleetRoster?.series != null)
            _selSeriesIdx = Mathf.Clamp(_selSeriesIdx, 0, Mathf.Max(0, fleetRoster.series.Count - 1));
        _selBusIdx = Mathf.Clamp(_selBusIdx, 0, Mathf.Max(0, busPool.Count - 1));
        SyncArticulatedFromPrefab();

        var compat = GetCompatTXForCustom(_selEngine, _selBusIdx);
        if (Array.IndexOf(compat, _selTX) < 0) _selTX = compat.Length > 0 ? compat[0] : "b400r";
        if (fleetRoster == null) _tab = Tab.Custom;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═════════════════════════════════════════════════════════════════════════
    private void EnforceSingleAudioListener()
    {
        var listeners = FindObjectsByType<AudioListener>();
        if (listeners.Length <= 1) return;
        foreach (var al in listeners)
        {
            bool keep = mainCamera != null && al.gameObject == mainCamera.gameObject;
            if (!keep) Destroy(al);
        }
    }

    private string[] GetCompatTX(BusAudioEngine.EngineType eng)
        => _compatTX.TryGetValue(eng, out var arr) ? arr : new[] { "b400r" };

    // D8646Art ("Tasty Voith") is the 2200 XN60 CNG artic-only voice — same
    // gate as FleetSeriesDefinition.GetTxFor.
    private bool IsCustomBusArticulated(int idx)
    {
        if (idx < 0 || idx >= busPool.Count || busPool[idx] == null) return false;
        var bc = busPool[idx].GetComponentInChildren<BusSimulationController>();
        if (bc == null) return false;
        return bc.articulationMode != ArticulationMode.None || bc.trailerPivot != null;
    }

    // [ADD] Auto-fills the ARTICULATED chip from the prefab the first time
    // a given bus is selected, so it starts in a sensible state — but from
    // then on _selArticulated (the chip) is what actually gates artic-only
    // tx options, not prefab detection. Lets you e.g. preview an artic-only
    // drivetrain on a standard-body prefab if you want to.
    private void SyncArticulatedFromPrefab() => _selArticulated = IsCustomBusArticulated(_selBusIdx);

    private string[] GetCompatTXForCustom(BusAudioEngine.EngineType eng, int busIdx)
    {
        var arr = GetCompatTX(eng);
        if (_selArticulated) return arr;
        // Non-articulated buses can't run the artic-only electric/hydrogen
        // centre-axle drivetrain layouts (Accelera/ELFA's rear + centre-in-
        // wheel layout). D8646/Voith no longer has a separate artic-only tx
        // string at all -- it now responds to the articulated flag directly
        // inside DoVoithDSP, so there's nothing to filter out for it here.
        return Array.FindAll(arr, t => t != "elfa3_centeraxle" && t != "accelera_centeraxle"
                                     && t != "elfa2_centeraxle" && t != "accelera_fc_centeraxle");
    }

    private string[] BuildCustomSpecLines()
    {
        var lines = new List<string>();
        switch (_selEngine)
        {
            case BusAudioEngine.EngineType.L9N:  lines.Add("8.9L CNG (2017+)  |  IDLE 650  |  GOV 2000"); break;
            case BusAudioEngine.EngineType.L9:   lines.Add("8.9L Diesel (2017+)  |  IDLE 600  |  GOV 2100"); break;
            case BusAudioEngine.EngineType.B67:  lines.Add("6.7L Hybrid (Series)  |  Near-constant RPM"); break;
            case BusAudioEngine.EngineType.B72:  lines.Add("7.2L Hybrid (2027+, HELM platform)  |  IDLE 680  |  GOV 2200  |  Factory stop-start"); break;
            case BusAudioEngine.EngineType.XE40: lines.Add("Battery Electric  |  Regen braking"); break;
            case BusAudioEngine.EngineType.XE60: lines.Add("Battery Electric  |  Dual-motor portal axle  |  Regen braking"); break;
            case BusAudioEngine.EngineType.XHE40: lines.Add("Hydrogen Fuel Cell-Electric  |  Regen braking"); break;
            case BusAudioEngine.EngineType.XHE60: lines.Add("Hydrogen Fuel Cell-Electric  |  Dual-motor portal axle  |  Regen braking"); break;
            case BusAudioEngine.EngineType.X10:  lines.Add("10L Diesel  |  Parallel-hybrid capable"); break;
            case BusAudioEngine.EngineType.ISL9: lines.Add("8.9L Diesel (2010-16, EPA13)  |  IDLE 550  |  GOV 2100"); break;
            case BusAudioEngine.EngineType.ISL:  lines.Add("8.9L Diesel (2007-09)  |  IDLE 550  |  GOV 2100"); break;
            case BusAudioEngine.EngineType.ISB67:lines.Add("6.7L Diesel (2007-16)  |  IDLE 700  |  GOV 2400"); break;
            case BusAudioEngine.EngineType.ISLG: lines.Add("8.9L Westport CNG (2007-16)  |  IDLE 650  |  GOV 2000"); break;
        }
        switch (_selTX)
        {
            case "d8646":    lines.Add("D864.6 (Voith)  |  4-range auto  |  responds to Articulated toggle directly"); break;
            case "d8645":    lines.Add("Voith DIWA.5  |  4-range auto (older)"); break;
            case "b400r":    lines.Add("Allison B400R  |  6-speed auto  |  lockup G3+"); break;
            case "b500r":    lines.Add("Allison B500R  |  6-speed heavy-duty  |  artic"); break;
            case "b400r_g5": lines.Add("Allison B400R Gen 5  |  6-speed auto  |  smoother/quicker/quieter, newer controls"); break;
            case "b500r_g5": lines.Add("Allison B500R Gen 5  |  6-speed heavy-duty  |  smoother/quicker/quieter, newer controls"); break;
            case "b3400xfe": lines.Add("Allison B3400xFE  |  6-speed  |  fuel-economy lockup schedule"); break;
            case "zf":       lines.Add("ZF EcoLife 6AP1200B (Gen 1)  |  6-speed auto  |  lockup G3+"); break;
            case "h40ep":    lines.Add("Allison H40EP  |  Two-mode parallel hybrid"); break;
            case "h50ep":    lines.Add("Allison H50EP  |  Artic two-mode parallel hybrid"); break;
            case "egenflex40": lines.Add("Allison eGen Flex H40  |  Integrated motor-in-gearbox  |  Disconnect clutch (true engine-off)  |  Lithium-titanate ESS"); break;
            case "egenflex50": lines.Add("Allison eGen Flex H50  |  Integrated motor-in-gearbox  |  Disconnect clutch (true engine-off)  |  Lithium-titanate ESS  |  Artic"); break;
            case "bae":      lines.Add("BAE HDS 200  |  Series hybrid  |  Fixed reduction gear"); break;
            case "hds300":   lines.Add("BAE HDS 300  |  Series hybrid  |  Bigger motor  |  Artic"); break;
            case "electric": lines.Add("Direct drive  |  No gearbox"); break;
        }
        return lines.ToArray();
    }

    private static Bounds GetBounds(GameObject root)
    {
        var rr = root.GetComponentsInChildren<Renderer>();
        if (rr.Length == 0) return new Bounds(root.transform.position, Vector3.one * 3f);
        var b = rr[0].bounds; foreach (var r in rr) b.Encapsulate(r.bounds); return b;
    }

    private static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STYLES
    // ═════════════════════════════════════════════════════════════════════════
    private void EnsureStyles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _lblTitle    = MDT_UITheme.MakeLabel(15, FontStyle.Bold,   TextAnchor.MiddleLeft,   MDT_UITheme.TextCyan);
        _lblSub      = MDT_UITheme.MakeLabel(10, FontStyle.Bold,   TextAnchor.UpperLeft,    MDT_UITheme.TextAmber);
        _lblDim      = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.UpperLeft,    MDT_UITheme.TextDim);
        _lblCyan     = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.MiddleLeft,   MDT_UITheme.TextCyan);
        _lblGreen    = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.MiddleCenter, MDT_UITheme.TextGreen);
        _lblAmber    = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.MiddleLeft,   MDT_UITheme.TextAmber);
        _lblBody     = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.MiddleLeft,   MDT_UITheme.TextPrimary);
        _lblSmall    = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleLeft,   MDT_UITheme.TextDim);
        _btnPrimary  = MDT_UITheme.MakeButton(new Color(0.05f, 0.30f, 0.14f, 1f), MDT_UITheme.TextGreen,  11, FontStyle.Bold);
        _btnSecond   = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 10);
        _btnActive   = MDT_UITheme.MakeButton(MDT_UITheme.BGDirSel, MDT_UITheme.TextCyan,   10, FontStyle.Bold);
        _btnTab      = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 11);
        _btnTabActive= MDT_UITheme.MakeButton(MDT_UITheme.BGDirSel, MDT_UITheme.TextCyan,   11, FontStyle.Bold);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  HONK DSP
// ═══════════════════════════════════════════════════════════════════════════════
[RequireComponent(typeof(AudioSource))]
public class HonkDSP : MonoBehaviour
{
    [HideInInspector] public BusSelectMenu menu;
    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (menu == null) { Array.Clear(data, 0, data.Length); return; }
        menu.FillHonkBuffer(data, channels);
    }
}