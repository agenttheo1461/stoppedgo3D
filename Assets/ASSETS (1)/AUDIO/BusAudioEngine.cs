using System;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusAudioEngine  —  shared drivetrain + DSP audio
//
//  New in this revision
//  ─────────────────────
//  [ZF]    ZF EcoLife 6AP1200B — 6-speed auto, lockup G3+.
//          Cleaner planetary mesh than Allison (56 teeth), 4-oscillator sine
//          bank, very soft shift thud, subtle TC swoosh.  Valid for L9N + L9.
//
//  [b400r] Canonical TX string for Allison B400R.  "allison" still accepted
//          as a legacy alias in all routing checks.
//
//  [L9N]   Reworked to sound distinctly different from L9 Diesel:
//          · Smoother: lower 3rd harmonic, far less clatter noise.
//          · Deeper: boosted sub-oscillator, louder deep undertones.
//          · Idle wobble: 2.2 Hz LFO amplitude modulation that fades as
//            RPM rises — CNG engines have a characteristic loping idle.
//
//  [Creak] L9N + Allison B400R only.  10% chance per bus instance.
//          Triggered in gear 1 when accelerating hard through 11–20 km/h
//          (approaching the G1→G2 upshift).  A high, metallic sweep
//          "eeeeEEEEErrrrRRRR" built from a sawtooth + harmonic + hi-noise
//          mix.  Disappears instantly on G2 engagement.
//
//  [Engine Voice Options]  diwaOpt1_1 / diwaOpt1_2 / diwaOpt1_3 — real, built-in
//          engine character layers (L9/L9N/ISL9, ANY transmission — not gated
//          by tx), set per-bus from FleetRosterData.DiwaVoiceOptions via
//          EngineConfig. Only the base DIWA idle whine (Voith DIWA.6/.5
//          only) oscillates mid-high pitch down then fades, back up roughly
//          every 1s; quiet in G1, near-silent G2-4 — that part IS tx-specific.
//          opt1_1 = a second, delayed whine voice ~0.5s behind rpm.
//          opt1_2 = deep whine overlay, G1 only, ramps harder with rpm.
//          opt1_3 = smoother, quieter overall engine character.
//
//  [B3400xFE]  NEW. Allison-family TX, distinct from B400R/B500R. Modeled as
//          a fuel-economy-oriented variant: tighter lockup schedule (locks
//          earlier than B400R), noticeably quieter/cleaner planetary mesh
//          (lower mesh amplitude, faster whine settle), and a soft electric
//          "xFE" accessory whir layered under the mesh whine suggesting an
//          electrified oil pump / aux system. Own gear ratio table.
//
//  [D864.5]  NEW. Older-generation Voith DIWA.5 sibling to "voith" (D864.6).
//          Same 4-speed DIWA architecture but looser/older electronic
//          control: more torque-converter slip noise, lower-pitched whine,
//          heavier/less crisp shift thuds. Routed as tx == "d8645".
//
//  [HDS300]  NEW. BAE Systems HDS 300 — bigger-motor sibling to HDS 200
//          (current "bae" tx). Deeper/louder traction whine, higher torque
//          transient on regen and acceleration. Routed as tx == "hds300".
//
//  [B6.7 v2]  HUGE rework of the BAE HDS200 diesel APU character (was just
//          a flat "tired diesel" loop riding on the shared L9/ISL9 oscillator
//          bank). New in this pass:
//            · Phase-locked injector clatter (same pulse-sync technique
//              L9N/X10/ISL9 already use) instead of flat unmodulated grit.
//            · Dynamic turbo spool with its own smoothed Hz/volume ramp,
//              plus a wastegate flutter/chatter burst that fires on a hard
//              throttle lift while the turbo is still spun up.
//            · Audible startup sequence: starter-motor cranking churn,
//              followed by a rev-flare overshoot as the APU catches and
//              settles back to baseline idle.
//            · Shutdown "spin-down": descending-pitch rumble as the APU
//              winds down when the auto-stop system kills it.
//            · Deep load moan (heavy-load low-end lugging undertone) and
//              subtle intake/breathing noise under load.
//            · Slow ~0.9 Hz governor-hunt amplitude wobble at/near idle —
//              distinct, slower character than L9N's 2.2 Hz CNG idle wobble.
//            · Load-driven cooling fan that spools in only after sustained
//              heavy load (thermal proxy), fading back out once load eases.
//
//  [L9N v2]  Second makeover pass on the CNG engine character:
//            · Audible startup sequence (lighter/quicker than B6.7's —
//              CNG catches faster, less dramatic flare).
//            · Afterfire pop/crackle on hard deceleration from load — the
//              characteristic CNG exhaust "bark" on lift-off.
//            · Turbo wastegate flutter burst on a hard throttle lift.
//            · Subtle CNG fuel-pressure-regulator hiss under heavy demand.
//            · Deep load/lugging moan and intake breathing noise layers.
//
//  [Coast]  NEW — Coast-down character. For ~2 seconds after ANY gear
//          change, if the driver lets off the throttle (accel < 0.02),
//          a soft overrun texture (light noise wash + slow low moan,
//          decaying over the 2s window) fades in/out under the engine.
//          Engine-agnostic, applies to every powertrain.
//
//  [Fuzz]  NEW — A very small constant amount of high-frequency grit is
//          mixed into the final output of every engine type, so nothing
//          renders as a perfectly clean tone.
//
//  [L9N retune]  L9N's gas-noise / breath layers were dialed back — the
//          old mix leaned too hard on broadband noise ("air rushing")
//          instead of tone; noise components are now roughly half the
//          previous gain so the tonal oscillators carry more of the
//          character.
//
//  [Voith inverter fix]  The "inverter bank" DSP under the Voith DIWA TX
//          was a leftover BAE/XE40-style hack that force-ramped pitch off
//          gear-1 progress instead of anything physically meaningful for
//          a mechanical Voith gearbox. Replaced with a sane RPM-tracked
//          accessory/alternator whine.
//
//  [ISL9 + Allison gear-1 whine]  ISL9 paired with B400R now gets an
//          extra whine layer that blooms as gear 1 approaches the 1→2
//          upshift, on top of the existing Allison whine.
//
//  [ISL9 + B400R/B500R wobble]  NEW self-contained "old hydraulic valve
//          body" wobble character: in gear 1, a longer ooooo-UUUU-AHHH
//          wobble swell (3400xFE-ish whine mixed with an EP50-style
//          vibrato wobble) builds and holds; each subsequent gear repeats
//          a shorter, quieter version of the same swell. Kickdown speeds
//          up the gear-1 buildup and lets the wobble carry over into
//          gear 2 while still revving.
// ═══════════════════════════════════════════════════════════════════════════════
public partial class BusAudioEngine
{
public enum EngineType { L9N, L9, B67, X10, XE40, ISL9, XE60, ISL, ISB67, ISLG, XHE40, XHE60, B72 }

    // ── Shared constants ──────────────────────────────────────────────────────
    public static readonly float   TCIRC   = 2f * (float)Math.PI * 0.48f;
    public static readonly float[] AL_R    = {0f, 3.49f, 1.86f, 1.41f, 1.00f, 0.75f, 0.65f};
    public static readonly bool[]  AL_LOCK = { false, true, true, true, true, true, true };
    public static readonly float[] DIWA_R = {0f, 3.38f, 1.970f, 1.000f, 0.730f};
    public const float MAX_SPD = 105f; // ~65 mph
    public const float FINAL   = 5.13f;

    // ── Allison B500R arrays ──────────────────────────────────────────────────
    // Index 0 placeholder, followed by 1st through 6th gear mechanical ratios
    public static readonly float[] AL_R_B500 = { 0f, 4.70f, 2.21f, 1.53f, 1.00f, 0.76f, 0.67f };
    public static readonly bool[]  AL_LOCK_B500 = { false, false, false, true, true, true, true };
    public const float B500R_G1_UP = 20f; public const float B500R_G2_DN = 16f;
    public const float B500R_G2_UP = 36f; public const float B500R_G3_DN = 33f;
    public const float B500R_G3_UP = 52f; public const float B500R_G4_DN = 49f;
    public const float B500R_G4_UP = 66f; public const float B500R_G5_DN = 62f;
    public const float B500R_G5_UP = 80f; public const float B500R_G6_DN = 74f;

    private double ph_l9_engWhine;

    // ── ZF EcoLife 6AP1200B arrays ────────────────────────────────────────────
    public static readonly float[] ZF_R    = { 0f, 3.47f, 2.00f, 1.43f, 1.00f, 0.73f, 0.60f };
    public static readonly bool[]  ZF_LOCK = { false, false, false, true, true, true, true };
    public const float ZF_G1_UP = 18f; public const float ZF_G2_DN = 14f;
    public const float ZF_G2_UP = 34f; public const float ZF_G3_DN = 29f;
    public const float ZF_G3_UP = 52f; public const float ZF_G4_DN = 47f;
    public const float ZF_G4_UP = 70f; public const float ZF_G5_DN = 64f;
    public const float ZF_G5_UP = 83f; public const float ZF_G6_DN = 77f;

    // ── Allison B3400xFE arrays ───────────────────────────────────────────────
    // Fuel-economy variant: slightly taller overall gearing, earlier lockup
    // (locks from G2 instead of G3) for reduced TC slip / better economy.
    public static readonly float[] AL_R_B3400 = { 0f, 3.10f, 1.81f, 1.39f, 1.00f, 0.78f, 0.64f };
    public static readonly bool[]  AL_LOCK_B3400 = { false, false, true, true, true, true, true };
    // Gen4 B400R locks the same shape (only gear 1 ever slips) but keeps its
    // own bespoke speed-gated partial lock inside gear 1 (B4xUpdateTCC) --
    // this table is for Gen5, which uses the shared blend helper directly.
    public static readonly bool[]  AL_LOCK_B4   = { false, false, true, true, true, true, true };
    public const float B3400_G1_UP = 19f; public const float B3400_G2_DN = 15f;
    public const float B3400_G2_UP = 33f; public const float B3400_G3_DN = 30f;
    public const float B3400_G3_UP = 50f; public const float B3400_G4_DN = 46f;
    public const float B3400_G4_UP = 64f; public const float B3400_G5_DN = 60f;
    public const float B3400_G5_UP = 78f; public const float B3400_G6_DN = 72f;

    // ── Voith DIWA shift thresholds ───────────────────────────────────────────

    public const float VOITH_GEAR2_UPSHIFT_SPD  = 36f;
    public const float VOITH_GEAR3_UPSHIFT_SPD  = 54f;
    public const float VOITH_GEAR4_UPSHIFT_SPD  = 80f;
    public const float VOITH_GEAR2_DOWNSHIFT_SPD = 12f;
    public const float VOITH_GEAR3_DOWNSHIFT_SPD = 35f;
    public const float VOITH_GEAR4_DOWNSHIFT_SPD = 58f;

    // ── Voith DIWA 867.8 NXT — own 6-speed thresholds (real spec: 6- or
    // 7-speed family, second overdrive the ECU prefers to hold). Deliberately
    // NOT reusing the VOITH_GEAR* constants above — those are tuned for the
    // classic 4-speed D864.6/D8645 family and this is a genuinely different
    // gearbox with two more ratios above them.
    public const float NXT_GEAR1_UPSHIFT_SPD   = 22f;
    public const float NXT_GEAR2_UPSHIFT_SPD   = 34f;
    public const float NXT_GEAR3_UPSHIFT_SPD   = 50f;
    public const float NXT_GEAR4_UPSHIFT_SPD   = 68f;
    public const float NXT_GEAR5_UPSHIFT_SPD   = 88f; // into 2nd overdrive (gear 6) — the ECU tries to hold this
    public const float NXT_GEAR2_DOWNSHIFT_SPD = 26f;
    public const float NXT_GEAR3_DOWNSHIFT_SPD = 40f;
    public const float NXT_GEAR4_DOWNSHIFT_SPD = 56f;
    public const float NXT_GEAR5_DOWNSHIFT_SPD = 72f;
    public const float NXT_GEAR6_DOWNSHIFT_SPD = 78f; // wide gap below 88 -- "held overdrive" resists dropping out

    // ── Config ────────────────────────────────────────────────────────────────
    private double audioClock = 0.0;
    public EngineType engineType  = EngineType.L9N;
    public string     tx          = "b400r";
    public bool       economyMode = false;
    public bool       hillMode    = false;
    [Tooltip("Hill Mode load multiplier fed into the combustion/firing DSP (ld/rn). This is what makes each individual firing pulse read as strained/laboring under grade, on top of the gear-hold extension and RPM climb -- 1.0 = no extra strain, 1.4 = ~40% harder-worked firing character at the same throttle input.")]
    public float      hillEngineStrain = 1.4f;

    [Tooltip("Set externally at spawn/handoff via FleetSeriesDefinition.ResolveIsArticulatedEngine(). " +
             "Confirmed true whenever the resolved tx is B500R/H50EP(any gen)/ZH50EP/HDS300/etc " +
             "(60ft-only in the real fleet chart); otherwise falls back to the isArticulated flag for " +
             "ambiguous tx like Voith/ZF/B400R.")]
    public bool       isArticulatedEngine = false;

    // [FIX] NPCBusController assigns this in two places (OnAudioFilterRead's
    // sim-state sync) but it was previously never actually declared here --
    // a genuine compile-breaking gap. Selector-position truth, synced from
    // BusController's currentDirection == GearDirection.Neutral. Previously
    // the EVT/hybrid transmissions (H40EP/H50EP/eGen Flex/BAE/HDS300/
    // BAEGen3/electric) used "gear == 0" to mean BOTH "actually shifted to
    // Neutral" AND their own internal "stopped, foot off the gas" resting
    // state -- so any of those buses sitting at a red light IN DRIVE would
    // report gear 0 exactly like a bus the driver had shifted to Neutral,
    // wrongly qualifying for Fast Idle (which real buses only allow with
    // the selector actually in N) while mechanical/torque-converter buses
    // (Allison/Voith/ZF), which hold gear 1 at a dead stop in Drive, never
    // had this problem. This flag gives the audio engine the real selector
    // state so it can tell the two apart instead of overloading "gear".
    public bool       isNeutral = false;

    [Tooltip("Set externally from BusController.isGillig (see the isGilligBus component check on " +
             "spawn/handoff). Gillig BRT has a genuinely more hollow/resonant body than the Xcelsior " +
             "-- engine and transmission whine both read louder/more present through the chassis -- " +
             "and a distinct post-shift stall/rattle character the Xcelsior doesn't have. Drives the " +
             "gilligHollowMul body-resonance boost and the post-shift rattle burst below.")]
    public bool       isGillig = false;

    // [ADD] Gillig post-shift stall/rattle -- a short, rattly mis-fire-adjacent
    // burst right after a gear change completes, distinct from the clean
    // shift-thud every Xcelsior transmission already has. Reuses the existing
    // alShiftTransient/alShiftTransientDur timing (already fires on every
    // shift regardless of tx) as the trigger, rather than adding a second
    // parallel shift-detection system.
    private float  gilligRattleVol = 0f;
    private float  gilligPrevShiftDur = 0f;
    private double ph_gilligRattle1, ph_gilligRattle2;

    [Tooltip("Set externally via FleetSeriesDefinition.ClampToLegalTier(engineType, isArticulatedEngine, override). " +
             "280 = standard tune. 320 = ISLG/L9N artic default. 330/360 = ISL9/L9/X10 artic tiers. Drives " +
             "PeakTorqueNm()/EvaluateTorqueNm() below, and the RPM-shaped low-load torque bias applied inside " +
             "DoCombustionEngine (BusAudioEngine_AudioLayers.cs) -- NOT a flat multiplier anymore.")]
    public FleetSeriesDefinition.RatedPowerTier ratedTier = FleetSeriesDefinition.RatedPowerTier.HP280;

    [Tooltip("Set externally via FleetSeriesDefinition.ResolveCenterAxleType(engineType, ratedTier). " +
             "MAN40ft = no extra layer. ZF_AVN132_Passive/ZF_AVE130_Driven = portal-axle whine layer " +
             "added in DoVoithDSP (see centerAxlePortalWhineHz below) -- a portal design rides on a " +
             "different gear-reduction-at-the-hub setup than a MAN beam axle, audible as a light " +
             "constant whine tied to wheel speed rather than engine RPM.")]
    public FleetSeriesDefinition.CenterAxleType centerAxleType = FleetSeriesDefinition.CenterAxleType.MAN40ft;
    private double ph_centerAxlePortalWhine;

    // [ADD] Articulation joint creak/groan -- real, confirmed phenomenon on
    // X60 artics: the turntable bearing under torsional load and the rubber
    // accordion bellows fabric both creak, most noticeably while actively
    // turning (stick-slip friction working) and to a lesser degree while
    // just holding a big hinge angle under load (steady cornering). Gated on
    // centerAxleType being an actual AVN132/AVE130 portal-axle artic (per
    // ResolveCenterAxleType), same real-hardware hook point requested for
    // this layer. Driven by hingeAngleDeg, synced in from whichever
    // controller (BusController/NPCBusController) is authoritative this
    // frame -- see DoArticulationCreakDSP.
    public float hingeAngleDeg = 0f;
    private float  ca_prevHinge         = 0f;
    private float  ca_creakVolSmooth    = 0f;
    private float  ca_creakPitchJitter  = 90f;
    private double ph_ca_creak1, ph_ca_creak2;
    private float  ca_popTimer          = 0f;
    private float  ca_popEnvVol         = 0f;

    // [ADD] Real diesel torque curve, evaluated per-tier. Peak Nm figures are rough
    // conversions from the ISL9/L9 family's published lb-ft ratings at each HP tier
    // (280hp ~= 900 lb-ft, 320 ~= 1050, 330 ~= 1100, 360 ~= 1250). Shape is a typical
    // diesel curve: torque climbs fast off idle, holds flat through the mid-range,
    // then tapers toward governor.
    private static float PeakTorqueNm(FleetSeriesDefinition.RatedPowerTier tier)
    {
        switch (tier)
        {
            case FleetSeriesDefinition.RatedPowerTier.HP320: return 1420f;
            case FleetSeriesDefinition.RatedPowerTier.HP330: return 1490f;
            case FleetSeriesDefinition.RatedPowerTier.HP360: return 1695f;
            default:                                          return 1220f; // HP280 baseline
        }
    }

    /// <summary>Torque (Nm) at the current RPM for this engine's rated tier.</summary>
    public float EvaluateTorqueNm(float rpmNow)
    {
        float peak = PeakTorqueNm(ratedTier);
        float rnLocal = Mathf.Clamp01((rpmNow - IDLE) / Mathf.Max(1f, GOV - IDLE));
        float shape;
        if (rnLocal < 0.35f)      shape = Mathf.Lerp(0.60f, 1.0f, rnLocal / 0.35f);
        else if (rnLocal < 0.6f)  shape = 1.0f;
        else                       shape = Mathf.Lerp(1.0f, 0.55f, (rnLocal - 0.6f) / 0.4f);
        return peak * shape;
    }

    [Range(0f, 1f)] public float npcVolumeScale = 0.55f;
    [Range(0f, 1f)] public float spatialBlend   = 1f;
    public float maxAudioDistance = 120f;
    public bool  acHighEngLow     = false;
    public int   acLevel          = 75;

    // ── Power / ignition state ─────────────────────────────────────────────────
    // BAT button: master electrical power. Doors, dash, interior lights, and
    // A/C all require this — same as flipping the battery disconnect switch on
    // a real bus. Independent of whether the engine itself is running.
    public bool batteryOn   = true;
    // ACFailure breakdowns should be able to kill just the A/C comfort layer
    // without taking out the battery/doors/engine — kept separate from
    // batteryOn for that reason. Effective A/C requires BOTH to be true.
    public bool acComfortOn = true;

    public enum EngineRunState { Off, Cranking, ReadyToStart, Running }
    public EngineRunState engineState = EngineRunState.Off;
    private const float CRANK_DURATION = 8f;
    private float  _crankTimer;
    private double ph_crank1, ph_crank2, ph_crankPuff;
    private float  _crankPuffTimer, _crankPuffNextAt;

    // ═════════════════════════════════════════════════════════════════════════
    //  BREAKDOWN AUDIO — set once per frame by NPCBusController/PlayerHandoff's
    //  ApplyBreakdownAudioOverrides (mirrors the existing batteryOn/acComfortOn/
    //  engineState pattern above). All additive on top of whatever the normal
    //  engine/tx DSP is already producing -- these never replace it, they just
    //  layer a new sound on top, so a bus with a cosmetic sub-breakdown still
    //  sounds like itself, just with an extra quirk riding along.
    // ═════════════════════════════════════════════════════════════════════════
    public bool bd_flatTirePop;       // one-shot -- set true for exactly one frame on trigger
    public bool bd_batteryGroan;      // BatteryTrip -- ghostly low groan, ZH50EP-style character
    public bool bd_deepRpmGroan;      // cosmetic -- hella deep, loud groan tracking RPM
    public bool bd_stuckGear;         // cosmetic -- persistent fixed-pitch whine, ignores actual gear/speed
    public bool bd_roughIdleLope;     // cosmetic -- uneven, lopey idle
    public bool bd_turboWhistleLeak;  // cosmetic -- continuous high-pitch boost-leak whistle

    private bool   _bdPopConsumed;         // guards the one-shot so it only actually fires once per rising edge
    private double _bdPopEnvelope;         // current amplitude of the pop transient, decays each frame
    private double ph_bdGroan1, ph_bdGroan2;
    private double ph_bdDeepRpm1, ph_bdDeepRpm2;
    private double ph_bdStuckGear;
    private double ph_bdRoughLope1, ph_bdRoughLope2;
    private double ph_bdTurboWhistle;

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP HELPER — Breakdown Audio. Called once per sample from the true
    //  final combine point in ProcessAudio, additive on top of everything
    //  else -- never replaces the normal engine/tx tone, just layers a
    //  breakdown-specific sound on top of it.
    // ═════════════════════════════════════════════════════════════════════════
    private double UpdateBreakdownAudio(double invSR, float engVolPersonality)
    {
        double sample = 0.0;

        // ── FlatTire — literal POP. One-shot transient: a sharp noise burst
        // plus a low thump, decaying fast. Fires once on the rising edge of
        // bd_flatTirePop (caller sets this true for exactly one frame).
        if (bd_flatTirePop && !_bdPopConsumed)
        {
            _bdPopConsumed = true;
            _bdPopEnvelope = 1.0;
        }
        if (!bd_flatTirePop) _bdPopConsumed = false; // re-arm once caller clears the flag
        if (_bdPopEnvelope > 0.0005)
        {
            double popNoise = NextNoiseSample() * _bdPopEnvelope * 0.9;
            double popThump = Math.Sin(2.0 * Math.PI * ph_bdGroan1) * _bdPopEnvelope * 0.6;
            ph_bdGroan1 = (ph_bdGroan1 + 70.0 * invSR) % 1.0; // low thump under the crack
            sample += (popNoise + popThump) * npcVolumeScale;
            _bdPopEnvelope *= 0.90; // fast decay -- reads as a crack/bang, not a ring-out
        }

        // ── BatteryTrip — ghostly, low groan. Reuses the same "detuned pair
        // beating against each other" idea as the ZH50EP zombie character
        // (see DoH4xDSP's isZombie handling) rather than anything new --
        // dead HV system, mournful low drone.
        if (bd_batteryGroan)
        {
            double g1 = Math.Sin(2.0 * Math.PI * ph_bdGroan1) * 0.14;
            double g2 = Math.Sin(2.0 * Math.PI * ph_bdGroan2) * 0.10;
            sample += (g1 + g2) * npcVolumeScale * engVolPersonality;
            ph_bdGroan1 = (ph_bdGroan1 + 42.0        * invSR) % 1.0;
            ph_bdGroan2 = (ph_bdGroan2 + 42.0 * 1.03  * invSR) % 1.0; // slightly detuned -> slow beat, mournful not musical
        }

        // ── DeepRPMGroan (cosmetic) — hella deep, loud, tracks RPM directly.
        // Bus drives completely normally; this is purely a "something's
        // wrong under there" sound layered on top.
        if (bd_deepRpmGroan)
        {
            double groanHz = 18.0 + (rpm / 60.0) * 0.35; // very low fundamental, rises gently with RPM
            double g1 = Math.Sin(2.0 * Math.PI * ph_bdDeepRpm1) * 0.30;
            double g2 = Math.Sin(2.0 * Math.PI * ph_bdDeepRpm2) * 0.16;
            sample += (g1 + g2) * npcVolumeScale * engVolPersonality;
            ph_bdDeepRpm1 = (ph_bdDeepRpm1 + groanHz        * invSR) % 1.0;
            ph_bdDeepRpm2 = (ph_bdDeepRpm2 + groanHz * 2.01 * invSR) % 1.0;
        }

        // ── StuckGear (cosmetic) — fixed-pitch whine that ignores actual
        // gear/speed entirely -- the whole point is it's NOT tracking
        // anything correctly anymore, transmission's just hanging there.
        if (bd_stuckGear)
        {
            double whine = Math.Sin(2.0 * Math.PI * ph_bdStuckGear) * 0.09;
            sample += whine * npcVolumeScale * engVolPersonality;
            ph_bdStuckGear = (ph_bdStuckGear + 340.0 * invSR) % 1.0; // fixed pitch, never changes
        }

        // ── RoughIdleLope (cosmetic) — uneven, lopey idle. Two slow LFOs at
        // a non-integer ratio so the amplitude wobble never settles into a
        // clean, regular pulse -- reads as genuinely uneven, not just slow.
        if (bd_roughIdleLope && spd < 2f)
        {
            double lfo1 = Math.Sin(2.0 * Math.PI * ph_bdRoughLope1);
            double lfo2 = Math.Sin(2.0 * Math.PI * ph_bdRoughLope2);
            double lopeAmt = 0.5 + 0.5 * (lfo1 * 0.6 + lfo2 * 0.4);
            double idleRumble = NextNoiseSample() * lopeAmt * 0.05 * npcVolumeScale;
            sample += idleRumble;
            ph_bdRoughLope1 = (ph_bdRoughLope1 + 2.3  * invSR) % 1.0;
            ph_bdRoughLope2 = (ph_bdRoughLope2 + 3.7  * invSR) % 1.0; // non-integer ratio vs lfo1 -> irregular beat
        }

        // ── TurboWhistleLeak (cosmetic) — continuous high-pitch whistle,
        // doesn't track load/RPM/anything -- just a steady boost leak.
        if (bd_turboWhistleLeak)
        {
            double whistle = Math.Sin(2.0 * Math.PI * ph_bdTurboWhistle) * 0.045;
            sample += whistle * npcVolumeScale;
            ph_bdTurboWhistle = (ph_bdTurboWhistle + 2600.0 * invSR) % 1.0;
        }

        return sample;
    }

    /// Hybrids/electrics don't need a starter to spin an engine over — the
    /// traction motor (or genset) is just switched on, no ~8s crank.
    public bool IsHybridOrElectric() =>
        tx == "bae" || tx == "hds300" || tx == "h40ep" || tx == "h50ep" || tx == "zh50ep" || tx == "h50ep_gen5"
        // [FIX] eGen Flex was missing here entirely -- it's a parallel hybrid
        // same as h40ep/h50ep (same family, same instant-run capability, real
        // hardware even has an easier engine-off/on cycle than the older
        // H-series thanks to the disconnect clutch), but without this it fell
        // through to false and forced the full crank/startup sequence instead
        // of the hybrid/electric skip-startup shortcut every other Allison
        // hybrid tx gets.
        || tx == "egenflex40" || tx == "egenflex50"
        || IsElectric();

    /// ENG button entry point. One state transition per call:
    ///   Off           → Cranking (or straight to Running for hybrids/electric)
    ///   Cranking      → ignored (don't re-trigger a spinning starter)
    ///   ReadyToStart  → Running  ("turn the engine on" once it's caught)
    ///   Running       → Off      (shut down)
    public void RequestEngineToggle()
    {
        if (!batteryOn) return; // dead battery — starter won't even click

        switch (engineState)
        {
            case EngineRunState.Off:
                if (IsHybridOrElectric())
                {
                    engineState = EngineRunState.Running;
                }
                else
                {
                    engineState     = EngineRunState.Cranking;
                    _crankTimer     = 0f;
                    _crankPuffTimer = 0f;
                    _crankPuffNextAt = 0f;
                }
                break;

            case EngineRunState.Cranking:
                break; // already cranking, ignore

            case EngineRunState.ReadyToStart:
                engineState = EngineRunState.Running;
                break;

            case EngineRunState.Running:
                engineState = EngineRunState.Off;
                break;
        }
    }

    // ── Live state ────────────────────────────────────────────────────────────
    public float rpm = 0f, spd = 0f, accel = 0f, bkPd = 0f, shiftCD = 0f;
    public int   gear = 0;
    public bool  running = false;
    public bool  doorsOpen = false;
    public bool  parkingBrake = false;
    // [ADD] Mirrors NPCBusController/BusController's own DoorsFullyClosed --
    // true only once every door leaf reports fully closed (animation done),
    // not just "not told to be open." Used to retrigger the pneumatic
    // release puff off the door-closed moment instead of the raw parking-
    // brake toggle. Defaults true so a bus that spawns with doors already
    // shut (or has no door rig at all) doesn't read as permanently "still
    // closing."
    public bool  doorsFullyClosed = true;

    // [NEW] ABS active flag — referenced by the Voith ANS conditions (real
    // manual lists "ABS not active" as a hard requirement) and available for
    // any other system that needs it. Set externally by the vehicle
    // controller; defaults false so ANS behaves normally on buses/rigs that
    // never drive it.
    public bool  absActive = false;

    // ── Allison 3-stage retarder (real spec, see the rebuild in ProcessAudio) ──
    // retStage: 0 = off, 1 = accelerator released, 2 = light brake treadle,
    // 3 = full. retIntensitySmooth is the ramped 0..1 version for DSP use.
    private int   retStage           = 0;
    private float retIntensitySmooth = 0f;
    private float retAbsLockout      = 0f; // real ~6s resume delay after an ABS event
    // Real driver-facing control: "The retarder can be disabled using the
    // Retarder switch located inside the destination sign compartment."
    // Defaults enabled, matching normal service configuration.
    public bool  retarderEnabled = true;

    // ── ANS: Automatic Neutral at Standstill (Voith DIWA.6/.5 only) ────────────
    // See CalcVoithRPM for the full mechanism/real-manual grounding.
    private bool  voith_ansActive      = false;
    private float voith_ansTimer       = 0f;
    private float voith_ansEngagePulse = 0f; // re-engagement transient when ANS drops out

    // [NEW] Fast Idle Speed switch — real feature confirmed via a New Flyer
    // XN40 operator's manual: a physical toggle that raises idle RPM to
    // maintain engine temp during extended idling, and speeds up warm-up
    // after a cold start. Real gating, straight from the manual: "The FAST
    // position on the Idle Speed switch only operates if the engine is
    // running, the shift selector is in the neutral [N] position and the
    // parking brake is applied." This is just the driver's request/toggle
    // -- whether it actually takes effect is computed each tick against
    // those exact conditions (see fastIdleActive), not assumed true the
    // instant the switch is flipped.
    public bool  fastIdleRequested = false;

    // ── New universal sound features: edge-detection state ────────────────────
    private bool  _wasDoorsOpen = false;
    private float _doorHissThunkEnv = 0f;
    private double ph_doorHiss, ph_doorThunk;

    // [REPURPOSED, per direct request] Was the door-close-only trigger's
    // unused predecessor field. Now tracks parking-brake state so a RELEASE
    // (true -> false) can also fire the pneumatic puff -- but only once the
    // doors are actually fully closed, same as the door-close trigger below.
    // If the brake releases while the doors are still open/closing, the puff
    // is held pending and fires the moment doorsFullyClosed catches up,
    // instead of firing early against an open-door animation.
    private bool  _wasParkingBrakeOn = false;
    private bool  _pendingBrakeReleasePuff = false;
    // [ADD] Drives the pneumatic release puff's trigger now -- see the
    // PARKING-BRAKE RELEASE block in AudioLayers.cs.
    private bool  _wasDoorsFullyClosed = true;
    // [UNUSED after pneumatic rebuild — left declared, harmless]
    private float _airPuffEnv = 0f;
    private double ph_airPuff;
    private float  _airRelayEnv  = 0f;
    private double ph_airRelayBuzz;
    private double ph_airRelayClick;

    // [REBUILT AGAIN — real pneumatic "psstEEEHH"] Stage-1 sustain envelope,
    // see the AIR-BRAKE RELEASE block in BusAudioEngine.AudioLayers.cs.
    private float  _airPuffTail = 0f;
    private double ph_airPuffTail;
    // Stage-0 "PSST" crack envelope.
    private float  _airCrackEnv  = 0f;
    // [UNUSED — swapped for the sharp-hiss filter below] Resonator band-pass
    // read as ringy/warbly ("stuttering") rather than real air. Left
    // declared, harmless.
    private Resonator airCrackRes;
    private Resonator airHissRes;
    // Sharp air-hiss filter state -- same technique as the opt1_4 Voith
    // gear-1 hiss (v6_hissLP/v6_hissPrev): high-pass + differentiated-edge
    // noise reads as genuinely sharp rushing air, where plain low-passed
    // "noise_hi" reads as a soft wind blow. Own state so it doesn't fight
    // with the Voith hiss's continuity.
    private double airHissLP   = 0.0;
    private double airHissPrev = 0.0;

    private double ph_injectorTick;
    private float  _injectorTickPhaseAccum = 0f;

    private float  _turboWhistleEnv = 0f;
    private double ph_turboWhistle;

    private float  _wheelWhineHzSmooth = 0f;
    private double ph_wheelWhine1, ph_wheelWhine2;

    // ── Cooling fan clutch (universal, all L/B-series engines) ─────────────────
    private float  fanClutchHeat       = 0f;
    private int    fanClutchLastBand   = -1;

    // ── Engine-driven air compressor (Wabco twin, real Bendix D2 thresholds) ────
    private float  airPressurePsi        = 124f; // starts mid-band
    private bool   airCompressorCharging = false;
    private double ph_airComp;
    private float  fanClutchClunkEnv   = 0f;
    private float  fanClutchVolSmooth  = 0f;
    private double ph_fanClutch;

    // ── DPF active regeneration (diesel only — L9/ISL9/ISL) ─────────────────────
    private bool   dpfRegenActive  = false;
    private float  dpfRegenTimer   = 0f;
    private float  dpfNextRegenAt  = 0f;

    // ── Engine constants ──────────────────────────────────────────────────────
    public float IDLE, GOV;
    public int   CYLINDERS = 6;
    private float FHz(float r) => (r / 60f) * (CYLINDERS / 2f);

    // ── DSP sample rate ───────────────────────────────────────────────────────
    private double SR = 48000.0;

    // ── Shared engine oscillator phases ───────────────────────────────────────
    private double ph_e1, ph_e2, ph_e3, ph_esub, ph_eex, ph_etb;
    private double noise_lp, noise_hp_prev;
    private double noise_lo = 0, noise_hi = 0;

    // ── Voith DIWA whine (D864.6) ─────────────────────────────────────────────
    private double ph_vw1 = 0.0, ph_vw2 = 0.5, ph_vw3 = 0.0;
    private float  voith_whineHzSmooth = 0f;
    private float  voith_shiftThud     = 0f;
    private int    voith_lastGearAudio = 0;
    // Post-upshift whine "catch and hold" — real DIWA recording (G2->G3) showed the
    // whine locking onto a sustained tone for ~2.4s after the shift before decaying,
    // rather than gliding straight back down with the RPM. This timer reproduces that.
    private float  voith_shiftHoldTimer = 0f;
    private const float VOITH_SHIFT_HOLD_DUR = 2.4f;
    private double ph_voith_thud1      = 0;
    private double ph_voith_thud2      = 0;
    private double ph_vMw, ph_vMw2, ph_vRet, ph_vRet2;
    private float  voithRetHz = 78f;
    private float  voith_retSecSmooth = 0f; // [NEW] slow-attack smoothing for the secondary retarder tone, softens saw->groan transition
    private float  voith_g3GroanVol = 0f;   // [NEW] chassis groan layer, gated on gear==3 dwell time
    private float  voith_g3DwellTimer = 0f; // [NEW] time spent continuously in gear 3
    private double ph_v_g3Groan1 = 0.0, ph_v_g3Groan2 = 0.0; // [NEW] gear-3 body groan oscillator phases

    // ── Universal neutral fast-idle (1745-1755 RPM hunt, all engines) ─────────
    private float ph_neutralFastIdle = 0f;
    // [REBUILT per instruction/research] Was a perfectly smooth, perfectly
    // repeating single-frequency sine -- real idle hunt at a stop is two
    // irregular things layered together: the diesel governor/ECM hunting
    // for a stable idle target, and the torque converter modulating line
    // pressure while stalled against a closed throttle. Neither is a clean
    // wave -- it wanders unevenly and occasionally kicks a bit harder, the
    // "random little brRRRRRRRR" the report described. Random-walked
    // target + an occasional bigger converter "bump" on top, both driven
    // by NextNoiseSample() so no two idle stretches sound identical.
    private float idleHuntRpm      = 1750f;
    private float idleHuntBumpTimer = 0f;
    private float idleHuntBumpEnv   = 0f;

    // ── D864.6art — "Tasty Voith" (2200 XN60 CNG artic) ────────────────────────
    // FULL standalone variant of the DIWA.6 voice — does NOT share v6_ state,
    // so an art unit and a regular D864.6 in the same scene never fight over
    // smoothing/holds. Built on the same measured reference as the v2 rebuild
    // (drive whine ≈ engine-order 23×rpm/60 in G1 / 21.5× in G2+, sweeping
    // ~340→780 Hz over the launch climb; retarder whine output-order,
    // descending ~780→330 Hz with road speed, harmonics visible to ~3.4 kHz)
    // but calibrated HOTTER for the artic: more mass, more load, more voice.
    private float  d6a_whHzSmooth = 0f, d6a_whVolSmooth = 0f;
    private float  d6a_whHoldTimer = 0f, d6a_whHoldHz = 0f;
    private int    d6a_lastGear = -1;
    private double ph_d6a_wh1, ph_d6a_wh2, ph_d6a_wh3, ph_d6a_wh5;
    private float  d6a_screechSmooth = 0f;          // near-governor converter scream env
    private float  d6a_screamPitchMul = 1f;         // gear-1 windup/hold pitch multiplier ("eyyayyy" -> "EEEE")
    private double ph_d6a_scream1, ph_d6a_scream2;
    private float  d6a_rwhHzSmooth = 0f, d6a_rwhVolSmooth = 0f; // retarder whine (output-order)
    private double ph_d6a_rwh1, ph_d6a_rwh2, ph_d6a_rwh3;
    private float  d6a_churnEnv = 0f;
    private double d6a_churnLP  = 0.0;
    private double ph_d6a_churnMod;
    private float  d6a_surgeVol = 0f, d6a_surgePitch = 1f;      // launch REV surge
    private float  d6a_prevAccel = 0f;
    private float  d6a_turboHzSmooth = 0f, d6a_turboVolSmooth = 0f;
    private double ph_d6a_turbo1, ph_d6a_turbo2;
    private float  d6a_wgFlutterVol = 0f;                        // wastegate flutter burst
    private float  d6a_prevSpool = 0f;
    private double ph_d6a_wg;
    private float  d6a_deepVolSmooth = 0f;                       // high-RPM deep growl
    private double ph_d6a_deep1, ph_d6a_deep2;
    private float  d6a_raspHzSmooth = 0f;                        // XE40-style motor rasp (kept)
    private double ph_d6a_rasp;
    private float  d6a_lashVol = 0f;                             // artic rear-section driveline lash
    private double ph_d6a_lash;
    private int    d6a_lastGearAudio = 0;
    private float  d6a_sighVol = 0f, d6a_seatVol = 0f, d6a_thudVol = 0f;
    private double ph_d6a_seat, ph_d6a_thud1, ph_d6a_thud2;
    private bool   d6a_wasStopped = true;
    private float  d6a_moveOffTimer = 0f;
    private double ph_d6a_grab;
    // Post-shift "stall flutter" — plane-stall / lawnmower-sputter chatter
    private float  d6a_stallFlutterTimer = 0f;
    private double ph_d6a_stallFlutter, ph_d6a_stallGate;

    // ── Voith DIWA.5 (D864.5) — older-generation sibling phases ───────────────
    private double ph_v5_w1 = 0.0, ph_v5_w2 = 0.5, ph_v5_w3 = 0.0;
    private float  v5_whineHzSmooth = 0f;
    private float  v5_shiftThud     = 0f;
    private int    v5_lastGearAudio = 0;
    private double ph_v5_thud1 = 0, ph_v5_thud2 = 0;
    // [NEW] Ported straight from D864.6's v6_ drive-whine transition model
    // (same DIWA mesh physics, per direct instruction) -- hard vertical
    // cutoff on G1->G2, ~220ms audible step-relock on every other shift,
    // plus the same stretched gear-1 build/hold envelope. Own state, own
    // gear tracker (v5_lastGearWhine) so it doesn't collide with the
    // existing shift-thud tracker (v5_lastGearAudio) above.
    private int    v5_lastGearWhine = -1;
    private float  v5_whHoldTimer   = 0f;
    private float  v5_whHoldHz      = 0f;
    private float  v5_whRelockWin   = 0f;
    private float  v5_g1EnvTimer    = 0f;
    private bool   v5_wasInGear1    = false;
    // [NEW] "Deepen at high rpm" character, D864.5-only -- per instruction,
    // D864.5's whine should get audibly deeper (not just louder) as rpm
    // climbs, unlike D864.6 which stays clean/bright throughout. Implemented
    // as a growing suboctave blend + fundamental droop at the top of the rev
    // range, see the deepen block right below the mesh pitch calc.
    private float  v5_deepenAmt     = 0f;
    private double ph_v5_deepenSub;
    // [NEW] Full-voice port from D864.6's DoVoithDSP, per direct instruction
    // ("grab everything"). Own d5_-prefixed state throughout so nothing
    // collides with D864.6's v6_ fields. Dirtied vs. the 864.6 originals
    // per the same established pattern as the whine/pump-floor/grab above:
    // cruder, hoarser, louder, slower-decaying, no smart-circuit tapering.
    // Deliberately NOT ported: the electric-motor background drone (section
    // 10 in DoVoithDSP) -- doesn't make sense on a diesel ISL9/L9/ISLG
    // transmission; and opt1_4/opt1_5/opt1_6 (sections 14-16) -- those are
    // D864.6-specific configurable character variants tied to that
    // generation's own option flags, not generic DIWA content to duplicate.
    private float  d5_articDeepenNA; // unused placeholder -- articDeepen applied inline instead, no state needed
    private float  d5_ringaTimer = 0f, d5_ringaVol = 0f;
    private double ph_d5_ringa1, ph_d5_ringa2, ph_d5_ringa3;
    private float  d5_churnEnv = 0f;
    private double d5_churnLP = 0.0;
    private double ph_d5_churnMod;
    // [REMOVED per instruction] Stopped-idle body shake and RPM-load engine
    // vibration were ported over from D864.6 initially, then explicitly
    // removed -- D864.5 doesn't get either vibration layer.
    private bool   d5_shakerRolled = false, d5_isShaker = false;
    private double ph_d5_shakeBody;
    private float  d5_creakVol = 0f;
    private double ph_d5_creak1, ph_d5_creak2;
    private float  d5_g3DwellTimer = 0f, d5_g3GroanVol = 0f;
    private double ph_d5_g3Groan1, ph_d5_g3Groan2;
    private float  d5_wailSmooth = 0f;
    private double ph_d5_wail1, ph_d5_wail2;
    private int    d5_lastGearForPumpDown = -1;
    private float  d5_pumpSpinDown = 0f;
    private double ph_d5_pumpSpin;
    // [NEW] Full v6-style bright-chord whine oscillator, ported over
    // (screech partial only -- w1/w2/w3 reuse the existing ph_v5_w1-3
    // phases, suboctave reuses ph_v5_deepenSub).
    private float  d5_screechSmooth = 0f;
    private double ph_d5_wh4;
    private double ph_v5_Mw, ph_v5_Mw2, ph_v5_Ret, ph_v5_Ret2;
    // [REMOVED] v5_RetHz -- dead field from the old retarder that never
    // actually sounded (retAct was unused). Superseded by d5_retHzSmooth
    // in BusAudioEngine_txrework.cs, which the rebuilt retarder actually
    // reads from and smooths toward road speed under braking.

    // ═════════════════════════════════════════════════════════════════════════
    //  VOITH DIWA 867.8 NXT — fresh build, own fields throughout (nxt_*),
    //  nothing shared with voith/d8646/d8645. Real spec basis (Voith's own
    //  materials, cross-checked against a real EPA2024 Gillig build sheet:
    //  8.9L Cummins L9 280hp + DIWA 867.8 NXT + ArvinMeritor axles):
    //    · World-first FULLY SEPARATED torque converter + retarder — every
    //      other Voith unit in this fleet (D864.6, D8645) uses the classic
    //      architecture where the retarder lives inside the converter.
    //      NXT's converter is optimized purely for start-up; the retarder is
    //      a genuinely separate hydrodynamic unit (real spec: up to 1,800 Nm,
    //      brakes almost to standstill) — two independent fluid systems, so
    //      two independent voices below (nxt_conv* vs nxt_ret*).
    //    · 6-speed with a held 2nd overdrive — the ECU reaches gear 6 at
    //      cruise and tries to STAY there rather than hunting, unlike the
    //      Allison/classic-DIWA stepped-shift feel elsewhere in this fleet.
    //    · 48V mild-hybrid CRU (Central Recuperation Unit) — 25kW continuous/
    //      35kW peak, frequency converter built INTO the transmission
    //      housing, inverter mounted on top specifically to avoid line
    //      losses. This is a tiny fraction of H40EP's full two-mode motor
    //      scale — modeled as a small, thin, subtle accessory whine, not a
    //      second engine. Four real functions, four real states below:
    //      stop-start, coast (engine decouples toward idle off-throttle),
    //      boost (light-moderate accel assist), and brake regen (light brake
    //      only — the separate retarder above takes over past retAct's
    //      existing bkPd>0.30 threshold).
    //    · Real quote: "lower noise emissions due to reduced average engine
    //      speed" — CalcNXTRPM below targets a genuinely lower RPM curve
    //      than the classic DIWA family gets for the same speed/load.
    //    · economyMode IS the in-game Eco button, already wired everywhere
    //      else in this file (RPM ceilings, kickdown gating) — here it does
    //      double duty as the mild-hybrid ON/OFF switch: Eco on = the 48V
    //      system actively runs stop-start/coast/boost/regen (real hybrid-
    //      vehicle UX, same idea as a Prius-style ECO button); Eco off = the
    //      hybrid layer goes fully dormant and this drives like a straight
    //      diesel automatic, engine idling through stops same as everything
    //      else in the fleet without stop-start.
    // ═════════════════════════════════════════════════════════════════════════

    // ── Launch-only converter voice — confined to gear 1, fades hard by
    // gear 3 (NXT's converter no longer does double duty as a retarder, so
    // there's no reason for it to linger the way the classic DIWA whine does).
    private float  nxt_convVolSmooth = 0f;
    private double ph_nxt_conv1, ph_nxt_conv2;

    // ── Separate hydrodynamic retarder — own envelope, gated purely by brake
    // input (retAct, i.e. bkPd > 0.30 at real speed), independent of the
    // converter entirely.
    private float  nxt_retVolSmooth = 0f;
    private double ph_nxt_ret1, ph_nxt_ret2;

    // ── Planetary mesh whine — present but genuinely quiet in gears 5/6
    // (the whole point of the held overdrive is a quiet cruise).
    private float  nxt_meshVolSmooth = 0f;
    private double ph_nxt_mesh1, ph_nxt_mesh2;

    // ── Shift thud — rarer at cruise than Allison/classic DIWA by design
    // (held-overdrive behavior means fewer shift EVENTS once at speed, not
    // a quieter thud when one does happen).
    private float  nxt_shiftThud = 0f;
    private double ph_nxt_thud1, ph_nxt_thud2;
    private int    nxt_lastGearAudio = -1;

    // ── 48V mild-hybrid CRU — four real states, gated by economyMode (Eco button) ──
    private float  nxt_boostVolSmooth = 0f;
    private double ph_nxt_boost;
    private float  nxt_regenVolSmooth = 0f;
    private double ph_nxt_regen;
    private float  nxt_stopTimer      = 0f;
    private bool   nxt_engineOff      = false;
    private float  nxt_restartPulse   = 0f;
    private float  nxt_coastRpmBlend  = 0f; // 0 = normal gear-ratio RPM, 1 = fully decoupled toward idle

    private float  v5_tcSlipSmooth = 0f;

    // ── DIWA "Differential Wandler" core whine ─────────────────────────────────
    // The DIWA name literally means Differential-Wandler (differential converter).
    // The differential gearset sits AHEAD of the torque converter in the power
    // path and uses low-helix-angle gears that stay in mesh continuously —
    // independent of which forward gear is selected and, on pre-idle-declutch
    // units, even independent of whether the bus is moving at all. That's why
    // older DIWA buses are reported to whine audibly even sitting still at idle:
    // it's not the converter or the ratio-dependent mesh whine, it's this gearset.
    // Modeled here as its own always-on layer, driven directly by input/engine
    // RPM rather than by selected-gear ratio or load, unlike v5_whineHzSmooth above.
    private float  dw_coreHzSmooth = 0f;
    private double ph_dw_core1, ph_dw_core2, ph_dw_core3;
    private const float DW_HELIX_TEETH = 27f;   // low helix-angle diff input gear, plausible tooth count

    // idle "di-wa-di-wa" warble that morphs smoothly into the sustained climb tone
    private double ph_dw_idleLfo;
    private float  dw_idlenessSmooth = 0f;
    // brightness/volume swell as RPM climbs toward the next upshift ("aaa->eee->EEE")
    private float  dw_climbBright   = 0f;
    // pre-1->2-shift deep rev flare with its own engine-firing-rate texture
    private float  dw_preShiftRevAmt      = 0f;
    private double ph_dw_rev1, ph_dw_rev2, ph_dw_revFire;
    private int    dw_lastGearForRevFlare = 0;
    // idle-only rhythmic chassis vibration ("nanana") — cuts instantly on accel, no fade
    private float  dw_idleVibAmt = 0f;
    private double ph_dw_idleVib1, ph_dw_idleVib2;

    // ── Allison B400R phases ──────────────────────────────────────────────────
    private double ph_alGw, ph_alGw2, ph_alPd;
    private double ph_l9_aw1, ph_l9_aw2, ph_l9_awo;
    private double ph_l9_tc, ph_l9_tc2;
    private double ph_l9_thud1 = 0, ph_l9_thud2 = 0;

    // ── Allison B500R phases ──────────────────────────────────────────────────
    private double ph_b5_wh1, ph_b5_wh2, ph_b5_wh3;
    private double ph_b5_tc1, ph_b5_tc2;
    private double ph_b5_pump;
    private double ph_b5_thud1, ph_b5_thud2;
    private float  b500_whineHzSmooth = 0f;
    private float  b500_tcNoiseSmooth = 0f;
    private float  b500_shiftThud     = 0f;
    private float  b500_lockupThud    = 0f;
    // [ADD] B500R Gen 5's OWN dedicated DSP state -- genuinely separate
    // from B500R Gen 4's b500_*/b5w_* fields, not a reused/shared voice.
    // See DoB500RGen5DSP for the full design rationale.
    private float  b5g5_shiftThud      = 0f;
    private float  b5g5_lockupThud     = 0f;
    private float  b5g5_tcNoiseSmooth  = 0f;
    private float  b5g5_runTimeSec     = 0f;
    private float  b5g5_pumpVolSmooth  = 0f;
    private float  b5g5_accHzSmooth    = 0f;
    private float  b5g5_accWhineSmooth = 0f;
    private float  b5g5_howlAccelDecay = 1f;
    private bool   b5g5_retardZoneActive = false;
    private float  b5g5_tickVol, b5g5_spitVol;
    private double ph_b5g5_pump1, ph_b5g5_pump2, ph_b5g5_accWh1, ph_b5g5_accWhSub, ph_b5g5_accWhHowl;
    private double ph_b5g5_tc1, ph_b5g5_tc2, ph_b5g5_pumpOrder;
    private double ph_b5g5_out, ph_b5g5_howlTrem, ph_b5g5_thud1, ph_b5g5_thud2, ph_b5g5_wh2;
    private double ph_b5g5_spitTick, ph_b5g5_spitHiss;
    private int    b500_lastGear      = -1;

    // ── ALLISON FAMILY REWORK — separate-identity fields ────────────────────
    // Previously B400R borrowed B500R's pump/mesh/howl/thud wholesale (shared
    // b5w_*/b500_shiftThud/b500_lockupThud/ph_b5_out fields, identical
    // tuning) and B3400xFE's howl also rode the same shared ph_b5_out field.
    // Real spec basis for giving each its own voice (Allison World
    // Transmission spec sheets): B400R is rated up to 295 ghp (220kW) for
    // rigid 40ft buses; B500R up to 450 ghp (336kW) for artic 60ft duty —
    // roughly 50% more torque-converter capacity and fluid volume moving
    // through a physically bigger pump. B3400xFE is the FuelSense Max
    // "xFE" line: optimized ratios + earlier lockup for up to 7% better
    // fuel economy, meaning genuinely less converter slip overall.
    private float  b400w_pumpVolSmooth = 0f;
    private double ph_b400w_pump1, ph_b400w_pump2;
    private float  b400w_runTimeSec    = 0f;
    private float  b400w_accHzSmooth    = 0f;
    private float  b400w_accWhineSmooth = 0f;
    private double ph_b400w_accWh1, ph_b400w_accWhSub, ph_b400w_accWhThird;
    private double ph_b400_out;
    private double ph_b400_howlTrem;
    private float  b400HowlAccelDecay  = 1.0f;
    private bool   b400_bigHowlRolled  = false;
    private bool   b400_bigHowl        = false;
    private float  b400_shiftThud      = 0f;
    private float  b400_lockupThud     = 0f;
    // [ADD -- real per-gear shift "shape"] Real Allison shift bark isn't the
    // same size every gear -- G1 is the big, long "BRRRAAAHHH" (wide-open
    // converter, most fluid to spin up), then it fades hard and fast: by G6
    // it's barely audible at all, just the pitch stepping down. Shared
    // across all three real Allison-family variants (B400R/B500R/B3400xFE)
    // since it's the same physical shape -- only the ABSOLUTE timing (decay
    // rate) differs per variant below. Index 0 unused (gear 0 = no shift).
    private static readonly float[] ALLISON_GEAR_TAPER = { 0f, 1.00f, 0.62f, 0.40f, 0.26f, 0.16f, 0.09f };
    private static float AllisonGearTaper(int g) => ALLISON_GEAR_TAPER[Mathf.Clamp(g, 1, ALLISON_GEAR_TAPER.Length - 1)];
    // [FIX -- click bug] Smoothed shadow envelopes for the lockup-thud
    // layers whose sine partial rides a phase that's shared with another,
    // continuously-running tone (so the phase itself can't be zeroed on
    // trigger without breaking that other tone) -- see each usage site for
    // the fast-attack smoothing that replaces the instant 0->1 step there.
    private float  b400_lockupThudSm = 0f, b4g5_lockupThudSm = 0f, b34_lockupThudSm = 0f;
    // [NEW] Ported from D864.5's ORIGINAL whine model (pre-deepen-rework),
    // per direct instruction -- ISL9/L9 + B400R only. Own state so it
    // doesn't touch the DIWA v5_ fields or B400R's existing AW whine.
    private float  b400_dwWhineHzSmooth = 0f;
    private double ph_b400_dw1, ph_b400_dw2, ph_b400_dw3;
    // [ADD] B400R Gen 5's OWN dedicated DSP state -- fully self-contained,
    // no dependency on Gen 4's b4r_tccBlend/B4xUpdateTCC state machine.
    // See DoB400RGen5DSP for the full design rationale.
    private float  b4g5_shiftThud = 0f, b4g5_lockupThud = 0f;
    private float  b4g5_tcNoiseSmooth = 0f;
    private float  b4g5_awBrightSm = 0f, b4g5_awAirBlend = 0f;
    private bool   b4g5_retardZoneActive = false;
    private float  b4g5_tickVol, b4g5_spitVol;
    private double ph_b4g5_aw1, ph_b4g5_aw2, ph_b4g5_aw3, ph_b4g5_tc1, ph_b4g5_tc2;
    private double ph_b4g5_out, ph_b4g5_howlTrem, ph_b4g5_thud1, ph_b4g5_thud2;
    private double ph_b4g5_spitTick, ph_b4g5_spitHiss;
    private float  b4g5_howlAccelDecay = 1f;
    private double ph_b400_thud1, ph_b400_thud2;
    // [FIX -- continuity] Gen 5 no longer runs its own separate RPM/gear
    // logic (that's what was inventing the "weird revs" -- extra phantom
    // slip added in gears 2-5 that Gen 4 never has, plus its own unrelated
    // late-gear ramp). Gen 5 now calls the EXACT SAME real B400R/B500R
    // logic (CalcAllisonRPM/DoAllisonGear, CalcB500RRPM/DoB500RGear) and
    // only smooths the result through this extra low-pass "mask" before
    // it's used for pitch tracking, so the newer electronic controls just
    // read as smoother/quieter acoustically without any separate revving
    // behavior underneath.
    private float  b4g5_maskRPM = 0f;
    private int    b4g5_prevGearForThud = 0;
    private float  b5g5_maskRPM = 0f;
    private int    b5g5_prevGearForThud = 0;

    private double ph_b500_out;
    private double ph_b500_howlTrem;
    private float  b500HowlAccelDecay  = 1.0f;

    private double ph_b34_out;
    private double ph_b34_howlTrem;
    private float  b34HowlAccelDecay   = 1.0f;
    private float  b34_lockupThud      = 0f;

    // ── Allison B3400xFE phases ───────────────────────────────────────────────
    private double ph_b34_wh1, ph_b34_wh2, ph_b34_wh3;
    private float  b34_gearGainSmooth = 0f;
    private double ph_b34_tc1, ph_b34_tc2;
    private double ph_b34_thud1, ph_b34_thud2;
    private double ph_b34_xfe1, ph_b34_xfe2;
    private float  b34_whineHzSmooth = 0f;
    private float  b34_tcNoiseSmooth = 0f;
    private float  b34_shiftThud     = 0f;
private float h50RevCycle = 0f;
private float h50RevBurst = 0f;
private float h50WhineLevel = 0f;
private float h50MotorLevel = 0f;
private float h50EngineRevZone = 0f;
    // ── ZF EcoLife phases ─────────────────────────────────────────────────────
    private double ph_zf_wh1, ph_zf_wh2, ph_zf_wh3, ph_zf_wh4;
    private double ph_zf_tc1, ph_zf_tc2;
    private double ph_zf_thud1, ph_zf_thud2;
    private float  zf_whineHzSmooth = 0f;
    private float  zf_tcSmooth      = 0f;
    private float  zf_shiftThud     = 0f;
    // ── ZF remake: primary (input-side) retarder ─────────────────────────────
    private double ph_zf_ret1, ph_zf_ret2;
    private float  zf_retVolSmooth = 0f;

    // ═════════════════════════════════════════════════════════════════════
    //  ZF ECOLIFE 2 (tx=="zfel2" std / "zfel2_hd" heavy-duty-artic) — real,
    //  confirmed-distinct generation from EcoLife gen 1 above. Own field set
    //  (zfel2_/ph_zfel2_ prefix) so a bus running either generation never
    //  shares/collides state with the other. See DoZFEL2DSP's header for
    //  the full real-spec grounding (ZF's own EcoLife 2 launch materials).
    // ═════════════════════════════════════════════════════════════════════
    private double ph_zfel2_wh1, ph_zfel2_wh2, ph_zfel2_wh3, ph_zfel2_wh4;
    private double ph_zfel2_tc1, ph_zfel2_tc2;
    private double ph_zfel2_thud1, ph_zfel2_thud2;
    private double ph_zfel2_clunk1, ph_zfel2_clunk2;
    private double ph_zfel2_ret1, ph_zfel2_ret2;
    private double ph_zfel2_restart;
    private float  zfel2_whineHzSmooth  = 0f;
    private float  zfel2_tcSmooth       = 0f;
    private float  zfel2_shiftThud      = 0f;
    private float  zfel2_lockupClunkVol = 0f;
    private int    zfel2_lastGearAudio  = -1;
    private float  zfel2_retVolSmooth   = 0f;
    // Universal stop-start -- unconditional (real spec: "stop-start
    // capability for all model variants"), unlike NXT's Eco-button-gated
    // version. Same safe pattern as NXT's: silences rpm/DSP output rather
    // than touching the global engineState machine.
    private bool   zfel2_engineOff      = false;
    private float  zfel2_stopTimer      = 0f;
    private float  zfel2_restartPulse   = 0f;

    // ── Engine compression brake ("Jake brake") -- OFF by default. Real-
    // world note: most transit buses do NOT run true compression-release
    // engine brakes in city service -- they're loud enough that many
    // municipalities have "Engine Brake Prohibited" ordinances specifically
    // targeting them, and transit agencies typically rely on the hydraulic/
    // hydrodynamic retarder + service brakes instead. This is here as an
    // opt-in toggle (e.g. for a highway-route coach or a specific bus you
    // want it on) rather than a universal default.
    public bool engineCompressionBrake = false;
    private double ph_jakeBrake1, ph_jakeBrake2;
    private float  jakeBrakeVolSmooth = 0f;
    // ── Engine Voice Options (real, built-in — set from FleetRosterData.DiwaVoiceOptions
    //    via FleetSeriesDefinition.diwaVoice, resolved per-bus and carried through
    //    EngineConfig). Valid for L9/L9N/ISL9, ANY transmission — applied once in the
    //    main dispatch loop (not inside DoVoithDSP/DoD8645DSP), unlike the base
    //    oscillating DIWA idle whine which stays Voith-specific.
    public bool diwaOpt1_1; // delayed whine — mimics rpm, ~0.5s behind
    public bool diwaOpt1_2; // deep whine overlay — G1 only, ramps harder w/ rpm
    public bool diwaOpt1_3; // smoother + quieter overall character
    // opt1_4 — "second character" D864.6 variant, per video analysis: whine
    // suppressed until late G1 then barely present after the shift, a hiss
    // window from ~75-97% through G1, extended G1 length, and an audible
    // individual-piston-firing texture while revving hard in G1. Implemented
    // directly inside DoVoithDSP (not as a separate DoDiwaOpt1_4... function
    // like opt1_1/1_2) since it needs the real g1Progress DoVoithDSP already
    // computes internally, not the crude ld-based approximation passed to
    // the opt1_1/1_2 dispatch call.
    public bool diwaOpt1_4;
    public bool diwaOpt1_5; // "the shaker" -- no whine at all, deeper+louder core, rapid-fire click vibration-sim at idle
    // [ADD] These two never actually existed here -- BusSelectMenu already
    // assigns bus.audioEngine.diwaOpt1_6/1_7 (has for a while, per its own
    // "fourth voice"/"fifth voice" comments), but nothing on this class
    // ever declared them, which is a genuine compile-breaking bug: those
    // member references don't exist. Field declarations only for now --
    // real DSP behavior matching BusSelectMenu's own descriptions ("fourth
    // voice": whine hidden like opt1_4 but quieter, extended two-tone G1,
    // groan-only retarder; "fifth voice": as opt1_6 but whine never hidden,
    // Wandler wind-up on move-off instead) still needs to be built at each
    // relevant DoVoithDSP gating site (mirroring how diwaOpt1_4 is checked
    // in multiple places) -- flagging rather than guessing that blind.
    public bool diwaOpt1_6;
    public bool diwaOpt1_7;
    public bool diwaOpt1_8; // "strained" -- harder, load-driven whine that surges irregularly (eeEEEHHHHEHEEE) instead of a smooth swell. D864.6 only.
    private float v6_strainPhA, v6_strainPhB, v6_strainPhC;
    private double v6_strainBuzzPh, v6_strainFlutPh;

    private double ph_diwa_delay1, ph_diwa_delay2;
    private float  diwa_delayHzSmooth = 0f;
    private const int DIWA_DELAY_BUFFER_LEN = 24000; // ~0.5s at 48kHz, resized in ApplyEngineConstants if needed
    private float[] diwa_rpmHistory;
    private int     diwa_rpmHistoryHead = 0;
    private int     diwa_rpmHistoryLen  = 0;

    private double ph_diwa_deep1, ph_diwa_deep2;
    private float  diwa_deepVolSmooth = 0f;

    // ── L9N character extras ──────────────────────────────────────────────────
    // Creak — one-time 10% roll per instance
    private bool   l9n_creakInit = false;
    private bool   l9n_hasCreak  = false;
    private float  l9n_creakVol  = 0f;
    private double ph_l9n_creak1 = 0, ph_l9n_creak2 = 0;

    // L9N extra oscillators (deep undertone / CNG character)
    private double ph_l9n_dw1 = 0, ph_l9n_dw2 = 0;

    // ── L9N v2 makeover — startup sequence, afterfire pop, wastegate flutter,
    //    regulator hiss, load moan, breathing. All self-contained, no external
    //    dependencies beyond the existing noise/phase machinery. ─────────────
    private bool   l9n_prevRunning    = false;
    private bool   l9n_startSeqActive = false;
    private float  l9n_startSeqTimer  = 0f;
    private double ph_l9n_crank1, ph_l9n_crank2;
    private float  l9n_popVol         = 0f;
    private float  l9n_prevLdForPop   = 0f;
    private double ph_l9n_pop;
    private float  l9n_turboVolSmooth = 0f;
    private float  l9n_prevLdForWg    = 0f;
    private float  l9n_wastegateVol   = 0f;
    private double ph_l9n_wastegate;
    private double ph_l9n_hiss;
    private double ph_l9n_moan;

    // ── L9 diesel extra phases ────────────────────────────────────────────────
    private double ph_l9_e1b;

    // ── BAE HybriDrive (HDS 200, tx == "bae") ─────────────────────────────────
    private double ph_bae_mot1, ph_bae_mot2, ph_bae_mot3;
    private float  bae_rpm           = 0f;
    private float  bae_rpmBase       = 1700f;
    private float  bae_rpmAccelPeak  = 2000f;
    private float  bae_engVolMul     = 1f;
    private float  bae_startDelayTimer = 0f;
    private bool   bae_engineStarting  = false;
    private bool   bae_engineRunning   = false;
    private bool   bae_wasMoving       = false;
    private float  bae_restartDelay    = 0f;
    private float  bae_motorHzSmooth   = 0f;
    private bool   bae_engineOff       = false;
    private float  bae_engineOffChance = 0.30f;
    private bool   bae_wasStopped      = false;
    private double ph_bae_regen = 0.0;

    private double ph_bae_inv = 0.0;

    // ── B6.7 v2 makeover (BAE HDS200 diesel APU) — turbo dynamics, wastegate
    //    flutter, crank/flare startup, spin-down shutdown, load moan, intake
    //    breathing, idle governor hunt, sustained-load cooling fan. ─────────
    private float  bae_turboVolSmooth  = 0f;
    private float  bae_prevLdForTurbo  = 0f;
    private float  bae_wastegateVol    = 0f;
    private double ph_bae_turbo        = 0.0;
    private double ph_bae_wastegate    = 0.0;
    private bool   bae_prevEngineStarting = false;
    private bool   bae_crankFlareActive   = false;
    private float  bae_crankFlareTimer    = 0f;
    private double ph_bae_crank1          = 0.0;
    private double ph_bae_crank2          = 0.0;
    private bool   bae_wasRunningPrev  = false;
    private float  bae_shutdownVol     = 0f;
    private float  bae_shutdownPitch   = 1f;
    private double ph_bae_shutdown     = 0.0;
    private double ph_bae_moan         = 0.0;
    private float  bae_loadAvgSmooth   = 0f;
    private float  bae_fanVolSmooth    = 0f;
    private double ph_bae_fan1         = 0.0;
    private double ph_bae_fan2         = 0.0;

    // ── BAE HDS200 v2 — genset thermostat rebuild ─────────────────────────────
    // Replaces the old continuous accel/decel-chased RPM target with a real
    // series-hybrid control pattern: the diesel genset runs independent of
    // the traction motor, snapping between a small number of fixed
    // efficiency-island setpoints selected by a smoothed, multi-signal demand
    // estimate (hybrid-thermostat strategy — see design notes at call site).
    // [FIX] These used to be flat absolute RPMs, identical no matter which
    // engine (B67, L9N/XNE40, etc) the genset actually is -- every BAE bus
    // hit the exact same three plateaus at the exact same demand thresholds
    // regardless of host engineType, which is why different BAE buses read
    // as "the same engine revving the same way." Now expressed as fractions
    // of the HOST engine's own IDLE..GOV band (already correctly
    // differentiated per engineType in ApplyEngineConstants), so a B67
    // genset and an L9N genset land on genuinely different absolute RPMs.
    public float BAE_RPM_IDLE_FRAC   = 0.14f;  // idle-charge point
    public float BAE_RPM_CRUISE_FRAC = 0.42f;  // cruise-charge point
    public float BAE_RPM_MAX_FRAC    = 0.78f;  // max-charge point

    private float BaeTierRpm(float frac) => Mathf.Lerp(IDLE, GOV, frac);

    // Demand blend weights. W_LOAD reads `ld` (== accel today; will carry
    // real grade strain once hillMode/hillEngineStrain is implemented — see
    // [XNE40 TODO] at the demand calc). W_SPEED carries the sustained-cruise
    // baseline draw until that lands, so keep it meaningful, not token.
    public float BAE_DEMAND_W_LOAD   = 0.62f;
    public float BAE_DEMAND_W_SPEED  = 0.28f;
    public float BAE_DEMAND_W_AC     = 0.10f;
    public float BAE_DEMAND_W_BRAKE  = 0.35f;   // subtracted — regen relieves genset demand

    // Asymmetric smoothing: react fast to a demand rise (don't let the
    // battery sag under new load), decay slow on a drop (don't give up an
    // efficient operating point over a half-second lift of the pedal).
    public float BAE_DEMAND_ATTACK  = 0.06f;
    public float BAE_DEMAND_RELEASE = 0.008f;

    // Hysteresis thresholds, index 0 = tier0<->1 boundary, index 1 = tier1<->2.
    // Down-thresholds sit below their matching up-threshold so a value
    // sitting on the line doesn't hunt.
    public float[] BAE_TIER_UP   = new float[] { 0.32f, 0.68f };
    public float[] BAE_TIER_DOWN = new float[] { 0.20f, 0.52f };

    // Debounce: demand must hold past a threshold this long before the tier
    // actually changes, AND a tier just changed must wait this long before
    // it's allowed to change again — together these are what actually kill
    // hunting in stop-and-go traffic (hysteresis alone isn't enough).
    public float BAE_TIER_SUSTAIN_TIME = 0.8f;
    public float BAE_TIER_DWELL_TIME   = 2.5f;

    private float bae_demandRaw      = 0f;
    private float bae_demandSmooth   = 0f;
    private int   bae_gensetTier     = 0;     // 0 = idle-charge, 1 = cruise-charge, 2 = max-charge
    private int   bae_tierPending    = 0;     // tier the sustain timer is counting toward
    private float bae_tierSustainTimer = 0f;
    private float bae_tierDwellTimer   = 0f;

    // Tier-change transient — short relay-clunk when the genset steps up or
    // down a tier. Routine control action, kept subtle, not a fault sound.
    private float  bae_tierClunkVol = 0f;
    private double ph_bae_tierClunk1, ph_bae_tierClunk2;

    // ── BAE HDS200 v2 — idle-stop dwell timer (replaces bae_engineOffChance
    //    coin-flip). Genset shuts down only after sitting stopped for a real
    //    dwell period, not randomly on every stop. Threshold is jittered per
    //    stop event (not the outcome) for natural variation. ────────────────
    public float BAE_IDLE_STOP_BASE   = 5.0f;  // [unused post-fix, left declared]
    public float BAE_IDLE_STOP_JITTER = 1.5f;  // [unused post-fix, left declared]
    private float bae_idleStopTimer     = 0f;  // [unused post-fix, left declared]
    private float bae_idleStopThreshold = 5f;  // [unused post-fix, left declared]
    // [FIX] Real BAE Start/Stop Drive spec (per BAE/Metro Magazine
    // documentation): cuts on deceleration through 8mph, restarts on
    // acceleration through 10mph. Shared kph thresholds for HDS200/HDS300.
    public  float BAE_STARTSTOP_OFF_KPH = 12.875f; // 8 mph
    public  float BAE_STARTSTOP_ON_KPH  = 16.09f;  // 10 mph
    private bool  bae_startStopOff = false;
    private bool  h3v_startStopOff = false;

    // ── BAE HDS200 — load-stress accumulator + new components ────────────
    // Builds over SECONDS of sustained accelerator (real torque/thermal
    // buildup character), not per-sample -- see [FIX] note at its use site
    // for why the old per-sample coefficients never produced this.
    public  float  BAE_STRESS_ATTACK_TAU  = 5.0f; // seconds to ~63% under sustained pull
    public  float  BAE_STRESS_RELEASE_TAU = 2.2f; // relaxes faster than it built
    private float  bae_stress = 0f;
    private double ph_bae_windup, ph_bae_windup2;   // torque-windup whine (Voith-scream analog)
    private float  bae_regenSmooth = 0f;            // regen whine envelope (ph_bae_regen already declared above)
    private double ph_bae_aps;                      // APS (accessory power system) hum
    private bool  bae_wasOuterRunning   = false;   // tracks engineState==Running edge, see startup fix below

    // ── BAE HDS200 v2 — single-motor slip/warble (H40EP-derived, collapsed
    //    from dual MG1/MG2 planetary split down to ONE traction motor, since
    //    HDS200 has no planetary gearset — genset→battery/inverter→one motor
    //    →fixed reduction). Signed torque/slip drives warble rate + depth,
    //    notching to zero at the motoring↔generating crossover. ────────────
    public float BAE_SLIP_MAX       = 2.6f;   // Hz at full torque
    public float BAE_FLIP_KPH       = 16f;    // speed where torque sign crosses under coast
    public float BAE_DEPTH          = 0.38f;  // AM depth from warble
    public float BAE_NOTCH_HZ       = 0.70f;  // slip magnitude below which depth notches to 0
    public float BAE_MOD_FADE_KPH   = 30f;    // warble is a low/mid-speed phenomenon
    public float BAE_TQ_TAU         = 0.20f;  // torque command slew, seconds
    public float BAE_PITCH_DIP      = 0.035f;

    public float BAE_REV_GEAR       = 12.0f;  // once-per-rev thump: shaft ratio off wheel rev
    public float BAE_REV_DEPTH      = 0.12f;
    public float BAE_REV_FADE_KPH   = 26f;

    public float BAE_WANDER_HZ      = 2.0f;
    public float BAE_WANDER_GAIN    = 55f;
    public float BAE_WANDER_DEPTH   = 0.16f;
    public float BAE_WANDER_FADE_KPH = 18f;

    private float  bae_torque      = 0f;   // signed: + motoring, − generating
    private float  bae_slipHz      = 0f;
    private float  bae_depth       = 0f;
    private float  bae_mod         = 1f;
    private float  bae_modRev      = 1f;
    private float  bae_modWander   = 1f;
    private float  bae_pitch       = 1f;
    private float  bae_wander1     = 0f, bae_wander2 = 0f;
    private double ph_bae_warble, ph_bae_rev;

    // ── BAE HDS200 v3 — GENSET REBUILD (speed-locked RPM, UFO motor voice) ─
    // Two physically distinct things were being conflated before: the
    // TRACTION MOTOR (electric, always live the instant the bus moves,
    // continuous, speed-coupled -- unchanged, see continuousMotorVol
    // elsewhere in DoBAEDSP) and the GENSET (the diesel-generator "engine"
    // that charges the battery). The genset is what gets a real RPM here.
    // [FIX] RPM used to chase `accel`/pedal position directly (bae3_rpm),
    // which reads as a mechanical engine revving with your foot -- wrong
    // for a decoupled series-hybrid genset. Now purely speed-locked: 0
    // from 0-10kph (genset not engaged), then an exponential approach to
    // a ~1700rpm ceiling past 10kph. Still called "RPM" -- it's a real
    // synthetic value other layers key off, just sourced from road speed
    // instead of the pedal.
    public  float BAE_GENSET_ENGAGE_KPH = 10f;
    public  float BAE_GENSET_MAX_RPM    = 1700f;
    public  float BAE_GENSET_RISE_TAU   = 9f;    // kph span of the exponential approach past engage speed
    public  float BAE_GENSET_RPM_RISE_RATE = 900f; // rpm/sec slew toward target
    public  float BAE_GENSET_RPM_FALL_RATE = 500f;
    private float  bae_gensetRpm        = 0f;

    // ── UFO motor voice -- replaces the old DIWA-adjacent whine/hiMotor
    //    layer entirely. Detuned near-unison oscillator trio (same trick
    //    as the old bae3_hiMotorHzSmooth "weird beating" layer, retuned
    //    and promoted from background texture to the lead voice): an
    //    ISL9-turbo-ish register but electric, alien, futuristic. Driven
    //    off bae_gensetRpm, not accel. ─────────────────────────────────
    public  float BAE_UFO_HZ_BASE  = 900f;
    public  float BAE_UFO_HZ_SPAN  = 950f;
    private float  bae_ufoHzSmooth = 0f;
    private double ph_bae_ufo1, ph_bae_ufo2, ph_bae_ufo3;

    // ── Genset combustion-ish core -- driven by bae_gensetRpm instead of
    //    the old bae3_rpm accel-chase. ───────────────────────────────────
    private double ph_bae_core1, ph_bae_core2;

    // ── Ported from DoVoithDSP -- generic mechanical/vibration character
    //    that isn't tied to the Voith transmission itself, re-staged off
    //    spd/genset-state instead of gear. Fixed 633Hz aux tone is reused
    //    directly via DoVoithFixedToneDSP (mutually exclusive tx, safe to
    //    share those v6_ fields). ─────────────────────────────────────
    private bool   bae_isShaker = false, bae_shakerInit = false;
    private float  bae_moveOffTimer = 0f;
    private double ph_bae_grab;
    private float  bae_idleShakeEnv = 0f;
    private double ph_bae_idleVib, ph_bae_am1;
    private float  bae_rpmVibHz = 0f, bae_rpmVibVol = 0f;
    private double ph_bae_rpmVib;
    private float  bae_creakVol = 0f;
    private double ph_bae_creak1, ph_bae_creak2, ph_bae_shakeBody;
    private double ph_bae_pump;

    // ═════════════════════════════════════════════════════════════════════
    //  BAE HYBRID GEN3 (tx == "baegen3") — real, confirmed next-generation
    //  successor to HDS200/300 above. New Flyer's own Aug 2024 launch
    //  materials: "BAE Systems' Gen3 modular power control system (MPCS)
    //  and traction motor... smaller and lighter than the previous
    //  generation product... the BAE traction motor is DETACHED FROM THE
    //  ENGINE, allowing enhanced accessibility... reduced noise and
    //  vibrations." Metro/SFMTA coverage confirms silicon carbide power
    //  electronics for "advanced materials... to maximize electrical
    //  efficiency." Same fundamental series-hybrid architecture as HDS200/
    //  300 (genset -> generator -> single traction motor through fixed
    //  reduction, RPM decoupled from road speed) -- this is a newer/
    //  quieter/tighter generation of that same real hardware category, not
    //  a different propulsion concept. Own field set (bg3_ prefix), same
    //  "separate identity" convention this file already uses for Allison's
    //  B400R/B500R/B3400xFE split -- HDS200 and Gen3 are mutually exclusive
    //  tx choices on any one bus, but each keeps its own tuning identity.
    // ═════════════════════════════════════════════════════════════════════
    private double ph_bg3_mot1, ph_bg3_mot2, ph_bg3_mot3;
    private double ph_bg3_redux1, ph_bg3_redux2;
    private double ph_bg3_isg1, ph_bg3_isg2;
    private double ph_bg3_inv;
    private double ph_bg3_regen;
    private double ph_bg3_warble, ph_bg3_rev;
    private double ph_bg3_tierClunk1, ph_bg3_tierClunk2;

    private float  bg3_motorHzSmooth  = 0f;
    private float  bg3_reduxHzSmooth  = 0f;
    private float  bg3_isgHzSmooth    = 0f;
    private float  bg3_gensetLd       = 0f;
    private float  bg3_engVolMul      = 1f;
    // Startup is deliberately WITHOUT HDS200's crank-churn/rev-flare drama
    // -- "fewer components... more reliability" reads as a smoother,
    // quicker, more integrated genset spin-up, not a scripted mechanical
    // event. Just a fast fade-in, gated by the outer engineState the same
    // safe way HDS200's fresh-start edge trigger already is.
    private bool   bg3_engineRunning     = false;
    private bool   bg3_wasOuterRunning   = false;
    private float  bg3_startFadeT        = 0f;
    // Idle-stop dwell -- same real behavior as HDS200's (genset shuts down
    // after a real stationary dwell, not a coin-flip), jittered per-stop.
    private bool   bg3_wasStopped        = false;
    private float  bg3_idleStopTimer     = 0f;
    private float  bg3_idleStopThreshold = 5f;
    public  float  BG3_IDLE_STOP_BASE    = 4.2f;  // shorter dwell than HDS200's 5.0s -- quicker, more confident auto-stop decision on newer control hardware
    public  float  BG3_IDLE_STOP_JITTER  = 1.2f;
    // Genset thermostat tiers -- reuses the SAME real BaeTierRpm()/
    // BAE_RPM_*_FRAC/BAE_TIER_* constants HDS200 uses (host-engine-relative,
    // already correctly generalized), own tier/demand smoothing state.
    private float  bg3_demandRaw       = 0f;
    private float  bg3_demandSmooth    = 0f;
    private int    bg3_gensetTier      = 0;
    private int    bg3_tierPending     = 0;
    private float  bg3_tierSustainTimer = 0f;
    private float  bg3_tierDwellTimer   = 0f;
    private float  bg3_tierClunkVol     = 0f;
    // Signed torque/slip motor model -- same real physics as HDS200's
    // (motor generation doesn't change how induction/PM traction motors
    // slip), own smoothing state so the two never cross-talk.
    private float  bg3_torque = 0f, bg3_slipHz = 0f, bg3_depth = 0f;
    private float  bg3_mod = 1f, bg3_modRev = 1f, bg3_modWander = 1f, bg3_pitch = 1f;
    private float  bg3_wander1 = 0f, bg3_wander2 = 0f;

    // ── BAE HDS 300 (tx == "hds300") — bigger-motor sibling state ─────────────
    private double ph_h3_mot1, ph_h3_mot2, ph_h3_mot3;
    private float  h3_rpm             = 0f;
    private float  h3_rpmBase         = 1650f;
    private float  h3_rpmAccelPeak    = 2050f;
    private float  h3_demandSmooth    = 0f;
    private float  h3_engVolMul       = 1f;
    private float  h3_startDelayTimer = 0f;
    private bool   h3_engineStarting  = false;
    private bool   h3_engineRunning   = false;
    private bool   h3_wasMoving       = false;
    private float  h3_restartDelay    = 0f;
    private float  h3_motorHzSmooth   = 0f;
    private bool   h3_engineOff       = false;
    private float  h3_engineOffChance = 0.30f;
    private bool   h3_wasStopped      = false;
    private bool   h3_wasOuterRunning = false;
    private double ph_h3_regen = 0.0;
    private double ph_h3_inv = 0.0;

    // ── ELFA H40EP / H50EP ───────────────────────────────────────────────────
    private double ph_h4x_mg2a, ph_h4x_mg2b, ph_h4x_mg2c;
    private double ph_h4x_mg1a, ph_h4x_mg1b;
    private double ph_h4x_inv1, ph_h4x_inv2;
    private double ph_h4x_regen1, ph_h4x_regen2;
    private double ph_h4x_crank;
    private float  h4x_mg2HzSmooth   = 0f;
    private float  h4x_mg1HzSmooth   = 0f;
    private float  h4x_invHzSmooth   = 0f;
    private float  h4x_regenVolSmooth = 0f;
    private float  h4x_engVolMul      = 1f;
    private bool   h4x_wasMode1       = true;
    private float  h4x_modeXientVol   = 0f;

    // H40/H50 shared state (public so owner can read/write each tick)
    public  int   h40Mode      = 1;
    public  float h40ModeTimer = 0f, h40DipTimer = 0f, h40DipAmount = 0f;
    public  int   h40PrevMode  = 1;
    public  bool  h40StopStart = false;
    public  float h40SSTimer   = 0f;
    public  float alShiftTransient = 0f, alShiftTransientDur = 0f;
    public  float h40_crankTransient = 0f;
    public  float h40_crankTimer    = 0f;
    public  bool  h40_wasStopStart  = false;

    // ── XE40 Battery-Electric ─────────────────────────────────────────────────
    private double ph_xe_mot1, ph_xe_mot2, ph_xe_mot3;
    private double ph_xe_inv1, ph_xe_inv2;
    private double ph_xe_hum;
    // [RENAME/CLARIFY] ph_xe_comp1/2 + xe_comp* below are the ELECTRIC AIR
    // COMPRESSOR — a genuinely separate machine from the traction motor,
    // motor-driven (not gear-driven off an engine like the diesel Wabco/
    // Bendix unit), servicing doors/brakes/air suspension. Real electric-bus
    // compressors DO cycle on a duty-cycle timer like this rather than
    // tracking RPM (there's no engine RPM to track), so the periodic
    // idle/run timing here is correct for what it represents -- the naming
    // was just ambiguous ("Electric whirr") next to the motor code above it.
    private double ph_xe_comp1, ph_xe_comp2;
    private float  xe_motorHzSmooth   = 0f;
    private float  xe_invHzSmooth     = 0f;
    private bool   xe_compRunning     = false;
    private float  xe_compTimer       = 0f;
    private float  xe_compRunDuration = 0f;
    private float  xe_compIdleDuration= 0f;
    private float  xe_compVolSmooth   = 0f;
    private float  xe_regenVolSmooth  = 0f;
    // [FIX] Regen used to reuse ph_xe_mot1/2 (already-wrapped 0..1 phase
    // accumulators) multiplied by 0.85 INSIDE the sine call -- that doesn't
    // give a clean tone at 0.85x frequency, it truncates every motor cycle's
    // sweep at 306 degrees and snaps back to 0, a real phase-reset
    // discontinuity every single cycle. That's a genuine sawtooth-edge
    // artifact, not a design choice -- own properly-accumulated phases here.
    private double ph_xe_regenTone1, ph_xe_regenTone2;
    private bool   xe_contactorFired  = false;

    // [ADD] TRACTION MOTOR SETTLE WHIRR — separate from the compressor above
    // AND separate from the continuous speed-tracking motor tone. Fires once
    // as a short one-shot the moment the bus actually comes to a stop (not
    // cyclic, not tied to a duty-cycle timer): the traction motor's own
    // audible wind-down as it drops out of active torque control and settles
    // into the standstill cogging-hold hum. This is the "whirrrrr" that
    // should read as coming from the motor itself at the stop, distinct from
    // the compressor's independent periodic run cycle.
    private bool   xe_wasMovingForSettle = false;
    private float  xe_settleWhirrEnv     = 0f;
    private float  xe_settleWhirrHz      = 0f;
    private double ph_xe_settle1, ph_xe_settle2;
    private float  xe_contactorTimer  = 0f;
    private float  xe_contactorPulse  = 0f;

    // ── ZF AVE 130 electric portal axle (XE60) ────────────────────────────────
    // Real product context: ZF's AVE 130 is a low-floor electric portal axle
    // carrying TWO integrated traction motors — one per wheel — each driving
    // through its own two-stage planetary reduction inside the axle housing.
    // That's structurally different from XE40's single central direct-drive
    // motor in two audible ways: (1) two independent-but-correlated motor
    // oscillator banks instead of one, never perfectly in unison, and (2) an
    // actual gear-mesh whine riding on top of the motor tone, since there's
    // real reduction gearing in the path — XE40 has none of that at all.
    private double ph_zfa_motL1, ph_zfa_motL2, ph_zfa_motL3;
    private double ph_zfa_motR1, ph_zfa_motR2;
    private double ph_zfa_mesh1, ph_zfa_mesh2;
    private double ph_zfa_inv1, ph_zfa_inv2;
    private double ph_zfa_hum;
    private double ph_zfa_comp1, ph_zfa_comp2;
    // [FIX] Same regen phase bug as XE40 -- see ph_xe_regenTone1/2's comment.
    private double ph_zfa_regenTone1, ph_zfa_regenTone2;
    private float  zfa_motorHzSmooth    = 0f;
    private float  zfa_invHzSmooth      = 0f;
    private bool   zfa_compRunning      = false;
    private float  zfa_compTimer        = 0f;
    private float  zfa_compRunDuration  = 0f;
    private float  zfa_compIdleDuration = 0f;
    private float  zfa_compVolSmooth    = 0f;
    private float  zfa_regenVolSmooth   = 0f;
    private bool   zfa_contactorFired   = false;
    private float  zfa_contactorTimer   = 0f;
    private float  zfa_contactorPulse   = 0f;

    // ── Hydrogen Fuel Cell Balance-of-Plant (XHE40 / XHE60) ───────────────────
    // Ballard FCmove-HD-style stack: cathode air compressor (continuous,
    // load-tracked tonal whine — NOT the same thing as the cyclic pneumatic
    // door/brake air compressor above, which fuel cell buses still also have)
    // plus a stack cooling fan that ramps in on sustained load and fades back
    // out slowly, same thermal-proxy shape as the old-bus diesel fan but never
    // fully silent since the fuel cell needs some airflow even near idle.
    // Shared across both sizes — traction motor differs (XE40 vs XE60 layer),
    // aux hardware doesn't.
    private double ph_fch_comp1, ph_fch_comp2;
    private double ph_fch_fan1;
    private float  fch_compHzSmooth  = 367f; // ~22,000 RPM idle floor, matches DoFuelCellAuxDSP
    private float  fch_compVolSmooth = 0f;
    private float  fch_fanLoadSmooth = 0f;
    private float  fch_fanVolSmooth  = 0f;
    private double ph_fch_flutter;   // slow LFO — centrifugal-compressor surge/turbulence wobble

    // ── [ADD — Accelera/Siemens ELFA3: rear direct-drive + centre in-wheel
    //    motor, artic only] ─────────────────────────────────────────────────
    // CORRECTED per real spec (Accelera/Siemens documentation): this is NOT
    // a geared multi-speed drivetrain — it's direct-drive on the rear axle
    // (identical architecture to the existing single-motor XE40 tone) PLUS
    // a genuinely separate in-wheel motor on the centre axle for artics.
    // In-wheel (hub) motors have no reduction gearing and are mounted
    // directly in the unsprung wheel assembly — lower effective RPM for the
    // same road speed, more structurally-coupled "thrum" than a remote-
    // mount motor's cleaner whine, and their own cogging character since
    // they're driving the wheel 1:1 with no gear multiplication smoothing
    // torque ripple out. Modeled as an ADDITIVE second motor layer on top
    // of the existing rear-motor DSP, not a replacement for it.
    private float  ceax_hzSmooth   = 0f;
    private float  ceax_volSmooth  = 0f;
    private double ph_ceax_1, ph_ceax_2, ph_ceax_cog;

    // ── [ADD — hydrogen refresh: membrane humidifier + H2 injector solenoid] ──
    // Real, previously-unmodeled hardware: PEM fuel cell systems run a
    // membrane humidifier (recirculating water vapor to keep the stack
    // membrane hydrated — a small continuous fan/pump hum, separate from
    // the cathode air compressor above) and a solenoid-driven hydrogen
    // injector that clicks/ticks open on a duty cycle tied to stack demand
    // (distinct from the anode PURGE valve already modeled — purge is a
    // periodic dump vent, the injector is a much faster, quieter, more
    // frequent metering click).
    private double ph_fch_humid1, ph_fch_humid2;
    private float  fch_humidVolSmooth = 0f;
    private float  fch_injTimer       = 0f;
    private float  fch_injVol         = 0f;

    // ── AC (HVAC) compressor — electric scroll, Thermo King TE-series-style ───
    // Real spec: variable-speed electric scroll compressors on transit buses
    // run a 25-90Hz drive frequency (vs. 50-70Hz fixed-speed on older units),
    // scaled with cooling demand. Scroll compressors are hermetic/low-vibration
    // by design, so this reads as a soft low hum on the drive fundamental +
    // 2nd harmonic, NOT a whine — that character is reserved for the fuel
    // cell cathode compressor above, which is a completely different machine
    // (centrifugal, high-speed, no liquid handling) even on XHE buses that
    // have both running at once.
    private double ph_ac_comp1, ph_ac_comp2;
    private float  ac_compHzSmooth  = 40f;
    private float  ac_compVolSmooth = 0f;

    // ── AC noise ──────────────────────────────────────────────────────────────
    private double acNoise_lp;

    // ═════════════════════════════════════════════════════════════════════════
    //  ELECTRIC / FUEL-CELL / UNIVERSAL AC REBUILD — NEW STATE FIELDS
    //  (See DoXE40DSP, DoZFAVE130DSP, DoFuelCellAuxDSP, DoXHE40DSP, DoXHE60DSP,
    //  DoUniversalACDSP for the research notes behind each addition.)
    // ═════════════════════════════════════════════════════════════════════════

    // ── XE40 additions — cogging tremor, regen chirp, 4th motor partial,
    //    battery/inverter thermal loop (pump + fan) ───────────────────────────
    private float  xe_cogVolSmooth     = 0f;
    private double ph_xe_cog;
    private double ph_xe_mot4;
    private bool   xe_wasRegenActive   = false;
    private float  xe_regenChirpEnv    = 0f;
    private double ph_xe_regenChirp;
    private float  xe_thermalLoadSmooth  = 0f;
    private float  xe_thermalFanVolSmooth = 0f;
    private double ph_xe_thermalFan;
    private double ph_xe_pump;
    private float  xe_humVolSmooth = 0f;
    private double ph_xe_slot;
    private float  xe_compHzSmooth = 75f;
    private float  xe_ldSmooth = 0f;
    // [ADD] Electric air compressor -- slow "wobble" AM (real duty-cycle
    // compressors have a slight speed/load waver over their run, plus the
    // motor-cooling fan riding on the same housing isn't perfectly balanced)
    // and its own slowly-drifting rate so it doesn't read as a metronomic LFO.
    private double ph_xe_compWobble;
    private float  xe_compWobbleHz = 1.6f;

    // ── XE60 additions — mirrors XE40's additions.
    //    [CORRECTED against the real Miami-Dade XE60 manual] This block
    //    previously said a center-axle bank had been REMOVED as a research
    //    error, on the belief that the XE60 is a single ZF AVE130 portal
    //    axle. The actual manual lists a Siemens ELFA3 traction motor AND
    //    a separately-cooled Center Axle (its own EMP heat exchanger, own
    //    2 Fil-11 fans, own Ametek pump and reservoir) -- i.e. two real
    //    drive units. The center axle bank is restored below, correctly
    //    grounded this time. See DoZFAVE130DSP for the full citation. ─────
    private float  zfa_cogVolSmooth    = 0f;
    private double ph_zfa_cog;
    private bool   zfa_wasRegenActive  = false;
    private float  zfa_regenChirpEnv   = 0f;
    private double ph_zfa_regenChirp;
    private float  zfa_thermalLoadSmooth   = 0f;
    private float  zfa_thermalFanVolSmooth = 0f;
    private double ph_zfa_thermalFan;
    private double ph_zfa_pump;
    private float  zfa_humVolSmooth = 0f;
    private double ph_zfa_slot;
    private float  zfa_compHzSmooth = 70f;
    private float  zfa_ldSmooth = 0f;

    // ── XE60 CENTER AXLE drive unit (restored — see the correction note
    //    above and DoZFAVE130DSP's citation). Second real driven axle with
    //    its own motor bank, planetary mesh and dedicated cooling loop. ───
    private float  zfc_motorHzSmooth       = 0f;
    private double ph_zfc_mot1, ph_zfc_mot2, ph_zfc_mesh;
    private float  zfc_thermalLoadSmooth   = 0f;
    private float  zfc_thermalFanVolSmooth = 0f;
    private double ph_zfc_fan, ph_zfc_pump;

    // ── Battery Thermal Management System (Modine rooftop unit — own
    //    refrigerant compressor/heater/pump, separate from cabin HVAC). ───
    private float  zfa_btmsLoadSmooth = 0f;
    private bool   zfa_btmsRunning    = false;
    private float  zfa_btmsVolSmooth  = 0f;
    private double ph_zfa_btms;

    // Real driver-facing control: "The regenerative braking system can be
    // disabled by using the Regen Brake Disable switch located in the
    // destination sign compartment." Parallel to retarderEnabled.
    public bool  regenBrakeEnabled = true;

    // ── Hydrogen Fuel Cell Balance-of-Plant additions — anode purge valve +
    //    active stack coolant pump (both previously unmodeled) ─────────────
    private float  fch_purgeTimer   = 0f;
    private float  fch_purgeNextAt  = 0f;
    private float  fch_purgeEnvVol  = 0f;
    private double ph_fch_pump1, ph_fch_pump2;

    // ── Universal A/C rebuild additions — blower/condenser-fan/compressor
    //    split, soft-start ramp, slow thermostatic "hunt" cycle ─────────────
    private bool   ac_wasOn          = false;
    // [NEW] AC cycling mode -- toggle: AC runs for ~30s, then cuts off
    // abruptly (with some falloff, not instant) for ~30s, then comes back
    // on through the normal smooth ~7s spin-up. Distinct from the existing
    // slow thermostatic hunt LFO (ac_satRateHz, a subtle 95-165s wobble in
    // pitch/volume while running) -- this is a deliberate, driver-visible
    // duty-cycle mode, not a background texture.
    public bool    acCyclingMode       = false;
    private float  acCyclePhaseTimer   = 0f;
    private bool   acCyclePhaseOn      = true;

    // ── Smooth AC shutdown tail (engine/battery off) ────────────────────────────
    private bool   _wasPoweredForACTail = false;
    private float  _acShutdownTailTimer = 0f;
    private const float AC_SHUTDOWN_TAIL_SEC = 3f; // long enough for the ~2.7s smooth release above to actually finish
    private float  ac_startupTimer   = 0f;
    private float  ac_startupPulse   = 0f;
    private float  ac_spinUpT        = 0f;
    private float  ac_hissVolSmooth  = 0f;
    private float  ac_fanVolSmooth   = 0f;
    private double ph_ac_fan1, ph_ac_fan2;
    private double ph_ac_satLFO;
    private float  ac_satRateHz      = 0f;   // rolled once per instance so a fleet doesn't hunt in lockstep

    // ── Kickdown ──────────────────────────────────────────────────────────────
    public bool  kickdownKey = false;

    // ── Converter Stall Hold ("power-braking" a torque converter against a
    // held speed — same idea as a mechanic's stall-speed test, done live) ──
    // Hold Ctrl + throttle: RPM drops off its normal target and hangs at a
    // strained, elevated drone while road speed locks in place. Only usable
    // once the box has proven it can shift (first G1->G2 upshift ever), OR,
    // for gearless/direct-drive powertrains that never shift, once rolling
    // at 7+ km/h. Released by letting off Ctrl, letting off the throttle,
    // or braking.
    public  bool  stallHoldKey       = false;
    private bool  hasUpshiftedEver   = false;
    private bool  stallHoldActive    = false;
    private float stallHoldHeldSpeed = 0f;
    private float stallHoldEngageT   = 0f; // time since engagement, drives the drone swell
    private float stallSoundVolSmooth = 0f;
    private double ph_stallDrone1, ph_stallDrone2, ph_stallSwell;

    // ── [ADD] Fake stall-recovery RPM shape — opt1_4 gear 1, first half only.
    // Not tied to stallHoldKey/stallHoldActive at all -- this fires on its
    // own every G1 entry under opt1_4, landing rpm on the same IDLE+250
    // floor the real stall-hold uses and climbing out at the same 350 rpm/s
    // recovery rate, so gear 1 reads like it just came off a stall-hold
    // release even though no hold ever happened. See the block right after
    // the real Converter Stall Hold section for the full mechanism.
    private bool v6_fakeStallActive = false;
    private float v6_fakeStallTimer = 0f;

    private float pumpEnergy       = 0f;
    private float prevAccelForPump = 0f;
    private float kickdownPumpTimer= 0f;

    // ── Gear hold timer ───────────────────────────────────────────────────────
    public  float gearHoldTimer = 0f;
    private int   lastGear = 0;

    // ── Noise RNG ─────────────────────────────────────────────────────────────
    private uint _noiseSeed;

    // ── Coast character (NEW) ───────────────────────────────────────────────
    // For ~2s after ANY gear change, if the driver lets off the throttle,
    // a soft overrun/coast texture fades in then decays away with the window.
    private int   coast_lastGear     = 0;
    private float coast_windowTimer  = 0f; // counts down from COAST_WINDOW after a shift
    private float coast_volSmooth    = 0f;
    private double ph_coast_moan     = 0.0;
    private const float COAST_WINDOW = 2.0f;

    // ── Old Bus character ─────────────────────────────────────────────────────
    public bool oldBus = false;
    public bool  ob_deepMoan, ob_worn_whine, ob_revHang, ob_delayedShifts;
    public bool  ob_airRush, ob_roar, ob_rattle, ob_exhaustChuff, ob_beltSqueal, ob_doorWheeze;
    [Range(0f,1f)] public float ob_deepMoanAmt, ob_whineAmt, ob_revHangAmt, ob_shiftDelay;
    [Range(0f,1f)] public float ob_airRushAmt, ob_roarAmt, ob_rattleAmt, ob_exhaustChuffAmt;
    [Range(0f,1f)] public float ob_beltSquealAmt, ob_doorWheezeAmt;
    private bool   ob_initialized   = false;
    private double ph_ob_moan, ph_ob_moan2, ph_ob_whine, ph_ob_whine2;
    private double ph_ob_airRush, ph_ob_roar, ph_ob_roar2, ph_ob_rattle;
    private double ph_ob_belt, ph_ob_belt2, ph_ob_chuff;
    private float  ob_revHangRPM = 0f;
    private float  ob_chuffTimer = 0f, ob_chuffNextAt = 0f;
    private float  ob_beltTimer  = 0f;

    // ═════════════════════════════════════════════════════════════════════════
    //  CONSTRUCTOR
    // ═════════════════════════════════════════════════════════════════════════
    public BusAudioEngine(uint noiseSeed = 0x12345678u) { _noiseSeed = noiseSeed; }

    private double NextNoiseSample()
    {
        _noiseSeed ^= _noiseSeed << 13;
        _noiseSeed ^= _noiseSeed >> 17;
        _noiseSeed ^= _noiseSeed << 5;
        return (_noiseSeed / (double)uint.MaxValue) * 2.0 - 1.0;
    }
private bool   al_whineArmed      = false;  // window is open, waiting for first throttle
private float  al_whineArmTimer   = 0f;     // counts up while armed, window = 7s
private bool   al_whineFiring     = false;  // whine is actively sounding
private float  al_whineFireTimer  = 0f;
private float  al_whineVolSmooth  = 0f;
private double ph_al_whine1, ph_al_whine2, ph_al_whine3;
private int    al_whineLastGear   = -1;
 
/*
    Continuous Allison whine — replaces the fire/timer-window version.
    Always present once in gear and under any load, volume/pitch track
    rpm+load smoothly, no arm/fire state machine to glitch or drop out.
*/
private double ph_al_contWh1, ph_al_contWh2, ph_al_contWh3;
private float  al_contWhHzSmooth = 0f;

// [REMOVED] DoAllisonG1G2Whine + DoAllisonDeepWhine — v3 Allison rewrite.
// Both ran unconditionally for ALL THREE variants (b400r/b500r/b3400xfe)
// and grew LOUDER with higher gear. That's backwards: real Allison
// gear-mesh whine gets QUIETER once the torque converter locks up (no
// slip = no whine), which is exactly what DoB400RWhineDSP/DoB500RDSP/
// DoB3400DSP's own dedicated whine already correctly does. These two
// functions duplicated that whine with the opposite trend stacked on top
// — the direct cause of B3400xFE sounding "weird" (its own correct
// fade-with-gear whine fighting this backwards-growing layer every frame).
// DoAllisonDoubleTap (the actual "lockup thing at end of gear 1" sound) is
// preserved — it's now called directly from DoB400RWhineDSP/DoB3400DSP
// instead of being a side-effect buried inside these two deleted functions.

private double ph_al_deepWh1, ph_al_deepWh2, ph_al_deepWhSub;
private float  al_deepWhHzSmooth = 0f;
private float  al_deepWhFadeSmooth = 0f;

private double ph_g1g2_sweet;
    // ═════════════════════════════════════════════════════════════════════════
    //  SETUP
    // ═════════════════════════════════════════════════════════════════════════
    public void Configure(AudioSource audioSource)
    {
        audioSource.spatialBlend = spatialBlend;
        audioSource.rolloffMode  = AudioRolloffMode.Linear;
        audioSource.maxDistance  = maxAudioDistance;
        audioSource.minDistance  = 5f;
        audioSource.loop         = true;
        audioSource.clip         = AudioClip.Create("BusSynth", 44100, 1, 44100, false);
        audioSource.Play();
        SR = AudioSettings.outputSampleRate;

        // ── Sample-based audio layers (NEW) — wire up any Inspector-assigned
        // AudioLayerConfig fields here. Add one line per layer per tx/engine;
        // no other plumbing required. See BusAudioEngine.AudioLayers.cs.
        //   Example:
        //     RegisterAudioLayer("zf", "zfPlanetaryMesh", zfMeshLayer);
        //     RegisterAudioLayer("b400r", "b400rClunk", b400rClunkLayer);
        RegisterConfiguredAudioLayers();
    }

    /// <summary>
    /// Override or extend this (or just edit it directly) to register your
    /// AudioLayerConfig fields against the tx string(s) they should play
    /// under. Kept separate from Configure() so it's easy to find.
    /// </summary>
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 
    /// 

private void RegisterConfiguredAudioLayers()
{


        // Add more layers here the same way, e.g.:
        //   public AudioLayerConfig zfMeshLayer;
        //   zfMeshLayer.resourcePath = "Audio/zf_planetary_mesh";
        //   RegisterAudioLayer("zf", "zfMesh", zfMeshLayer);
    }

    // Voith converter whine — clip auto-loaded via resourcePath above, no
    // Inspector drag-and-drop required.

void Update()
{
    
    // Replace "Kickdown" with your specific Input Manager name or KeyCode
    kickdownKey = Input.GetKey(KeyBindings.Current.kickdown) || Input.GetAxis("Kickdown") > 0.5f;
    // NOTE: stallHoldKey is NOT set here — this class isn't a MonoBehaviour
    // (it's instantiated via `new BusAudioEngine(...)`), so this Update()
    // never actually runs. Same as kickdownKey, stallHoldKey is driven
    // externally by BusController each frame — see BusController.cs.
}
public void ApplyEngineConstants()
{
    // [FIX] GetTxFor() (FleetRosterData.cs) now returns "d8646" for what used
    // to be "voith" on L9/L9N -- but every DSP dispatch/fallback check in this
    // file and BusAudioEngine_AudioLayers.cs still tests the literal string
    // "voith" (DoVoithDSP's dispatch, every "if tx==bae -> voith" fallback,
    // the retarder/D8645 checks, etc -- a dozen+ sites). Renaming all of
    // those individually risks missing one and leaving a silent bus behind
    // again. Normalizing HERE, once, at the single point tx gets processed,
    // means "d8646" behaves IDENTICALLY to "voith" everywhere downstream
    // without touching any of those existing checks.
    // [FIX] Was unconditionally aliasing d8646 -> voith, meaning base
    // D8646 (no opts selected) shared 100% of the same code path as every
    // generic "voith"-tagged bus -- including the whine machinery, which
    // was baked directly into that shared function with no way to opt out.
    // That's the reported "864.6 sounds messy" problem: the whine wasn't a
    // togglable 864.6-specific feature, it WAS current-day Voith,
    // unconditionally, for every bus tagged either name.
    //
    // Rebuilt so base D8646 dispatches to DoVoithDSP directly, same as
    // before -- but DoVoithDSP's own body was rebuilt: shift thud and
    // move-off grab replaced with D8645's versions (grab boosted stronger
    // still), plus D8645's pump floor/TC slip/tail whine/G1 buzz added
    // alongside 8646's existing drive whine/fixed-tone/retarder, which all
    // stayed untouched. See DoVoithDSP's own section comments for the
    // full breakdown of what changed vs stayed.
    // diwaOpt1_4/diwaOpt1_5 still alias to "voith" as before, per explicit
    // instruction to keep those working exactly as they always have, for
    // compatibility -- only the OPT-LESS base case changes.
    if (tx == "d8646" && (diwaOpt1_4 || diwaOpt1_5)) tx = "voith";

    switch (engineType)
    {
        case EngineType.L9:
            IDLE = 600f; GOV = 2100f; CYLINDERS = 6;
            // BAE HDS 200 (series hybrid) needs its own dedicated APU chassis
            // and isn't valid bolted to a straight L9 diesel. H40EP/H50EP
            // (parallel-hybrid retrofits) and HDS300 ARE valid here per the
            // bus-select compatibility list — only "bae" itself gets blocked.
            // [ADD] EP40/EP50 -- real pre-2010 generation of H40EP/H50EP,
            // gated to ISL only (see CalcEP40RPM's header). An L9-engined
            // bus never ran the old EP-era drive unit in reality, so it
            // redirects to the modern H40EP/H50EP name it actually pairs
            // with instead of falling all the way back to Voith.
            if (tx == "bae") tx = "voith";
            else if (tx == "ep40") tx = "h40ep";
            else if (tx == "ep50") tx = "h50ep";
            break;
        case EngineType.X10:
            IDLE = 550f; GOV = 2000f; CYLINDERS = 6;
            // [FIX] Real fleet chart lists X10 as Clean-Diesel-only across
            // 35/40/60ft (B400R/B3400xFE/B500R/ZF EcoLife2/Voith NXT) --
            // it's never paired with ANY hybrid drivetrain. Previously only
            // "bae"/"hds300" were blocked, which incorrectly let h40ep/h50ep
            // (parallel-hybrid retrofits) through. All hybrid tx now redirect.
            if (tx == "bae" || tx == "hds300" || tx == "h40ep" || tx == "h50ep" || tx == "h50ep_gen5"
                || tx == "egenflex" || tx == "baegen3" || tx == "ep40" || tx == "ep50") tx = "voith";
            break;
        case EngineType.ISL9:
            IDLE = 550f; GOV = 2100f; CYLINDERS = 6;
            // [FIX] ZF (both gen1 "zf" and EcoLife 2 "zfel2"/"zfel2_hd") IS
            // offered on ISL9 per the real compat list in BusSelectMenu --
            // this used to contradict that and silently force it to Voith.
            // Only BAE (HDS200) is actually invalid here.
            // [ADD] EP40/EP50 -- same ISL-only gating as the L9 case above.
            // [FIX] Allison Gen5 (b400r_g5/b500r_g5) were never valid here --
            // ISL9 pairs with Voith/ZF/H4x-family/BAE, not the Gen5 Allison
            // siblings (those belong to the non-hybrid diesel lineup). Falling
            // through unredirected let them slip in by accident.
            if (tx == "bae") tx = "voith";
            else if (tx == "ep40") tx = "h40ep";
            else if (tx == "ep50") tx = "h50ep";
            else if (tx == "b400r_g5" || tx == "b500r_g5") tx = "voith";
            break;

        case EngineType.XE40:
            // [FIX] This used to unconditionally stomp tx to "electric"
            // regardless of what was actually selected — which meant an
            // Accelera pick from BusSelectMenu/FleetRosterData never
            // survived past this call. Now it validates against the real
            // BATTERY-ELECTRIC option set and only falls back to the
            // default (ELFA 3) if tx isn't one of them.
            IDLE = 0f; GOV = 1f; CYLINDERS = 0;
            if (tx != "elfa3" && tx != "accelera") tx = "elfa3";
            break;
        case EngineType.XE60:
            // [FIX] The artic option set here used to wrongly graft the
            // Accelera/ELFA3 name onto the ZF AVE 130 tx string — those are
            // two unrelated real drivetrains (see DoElfa3CenterAxleDSP's
            // header comment). Now XE60 validates against all three genuine
            // options: the original ZF portal axle, or the Accelera/ELFA3
            // rear-direct-drive + centre-in-wheel layout (either motor).
            IDLE = 0f; GOV = 1f; CYLINDERS = 0;
            if (tx != "zfave130" && tx != "elfa3_centeraxle" && tx != "accelera_centeraxle") tx = "zfave130";
            break;
        case EngineType.XHE40:
            // Same fix, HYDROGEN-ELECTRIC option set (40ft).
            IDLE = 0f; GOV = 1f; CYLINDERS = 0;
            if (tx != "elfa2" && tx != "accelera_fc") tx = "elfa2";
            break;
        case EngineType.XHE60:
            // Same corrected 3-option set as XE60 above, hydrogen-electric
            // flavor (legacy "elfa2_zfave130"/"accelera_fc_zfave130"/
            // "fcave130" from older saves still route correctly in
            // DoXHE60DSP, but aren't offered going forward).
            IDLE = 0f; GOV = 1f; CYLINDERS = 0;
            if (tx != "fcave130" && tx != "elfa2_centeraxle" && tx != "accelera_fc_centeraxle") tx = "fcave130";
            break;
            case EngineType.ISL:   IDLE = 550f; GOV = 2100f; CYLINDERS = 6;
                // [FIX] Regular ZF EcoLife (gen 1) IS offered on ISL per the
                // real compat list -- only "bae" and EcoLife 2 (zfel2/
                // zfel2_hd, never offered on this engine) still redirect.
                // [FIX] Allison Gen5 (b400r_g5/b500r_g5) also don't belong on
                // ISL -- same reasoning as ISL9/ISLG, these are non-hybrid
                // diesel-lineup transmissions, not ISL pairings.
                if (tx=="bae"||tx=="zfel2"||tx=="zfel2_hd"||tx=="b400r_g5"||tx=="b500r_g5") tx="voith"; break;
case EngineType.ISB67:
    // [FIX] ISB67 was redesignated hybrid-only (H40EP/BAE HDS200, see
    // GetTxFor()) once its diesel-only D8645/B400R/B500R options got
    // removed -- but this line was never updated to match, and was still
    // actively blocking "bae" and forcing it to Voith. That's the ISB6.7/
    // BAE HDS200 redirect bug: this engine has no Voith pairing at all
    // anymore, "bae" is one of its only two legal options.
    IDLE = 700f; GOV = 2400f; CYLINDERS = 6;
    // [ADD] EP40/EP50 -- 6.7L ISB67 never ran the ISL-paired EP-era unit
    // in reality; redirect to the modern H40EP name this engine already
    // legitimately offers.
    if (tx == "ep40") tx = "h40ep"; else if (tx == "ep50") tx = "h40ep";
    break;
case EngineType.ISLG:  IDLE = 650f; GOV = 2000f; CYLINDERS = 6;
    // [FIX] Allison Gen5 (b400r_g5/b500r_g5) don't belong on ISLG either --
    // same non-hybrid-diesel-lineup reasoning as the ISL/ISL9 fixes above.
    if (tx=="bae"||tx=="hds300"||tx=="h40ep"||tx=="h50ep"||tx=="h50ep_gen5"||tx=="ep40"||tx=="ep50"||tx=="b400r_g5"||tx=="b500r_g5") tx="voith";
break;
// change existing B67 case so it can run standalone:
case EngineType.B67:   IDLE = 650f; GOV = 2000f; CYLINDERS = 6; /* only default to APU if nothing else set: */ if (tx==null||tx=="") tx="bae"; else if (tx=="ep40"||tx=="ep50") tx="bae";
break;
// [NEW] Cummins B7.2 -- real, confirmed direct successor to B6.7 (Cummins'
// own HELM-platform materials): 7.2L (up from 6.7L), 240-340hp/650-1000
// lb-ft, factory stop-start, higher peak cylinder pressure than B6.7 for
// more complete energy extraction per real Cummins engineering notes.
// Positioned by Cummins specifically as the B6.7 Hybrid's successor for
// transit -- this codebase's fleet spec only offers B7.2 under the Hybrid
// trim (paired with eGen Flex or BAE Hybrid Gen3), matching real-world
// positioning, so it defaults to eGen Flex the same minimal way B67
// defaults to "bae" rather than hard-blocking anything.
case EngineType.B72:   IDLE = 680f; GOV = 2200f; CYLINDERS = 6; if (tx==null||tx=="") tx="baegen3"; else if (tx=="ep40"||tx=="ep50") tx="baegen3"; // eGen Flex lands in a future pass; baegen3 exists now
break;
        default: // L9N
            IDLE = 650f; GOV = 2000f; CYLINDERS = 6;
            // CNG: most hybrid-only TX strings (HDS300/H40EP/H50EP) are not
            // applicable; B3400xFE and D8645 ARE valid (mechanical/4-speed
            // siblings work fine behind a CNG engine). "siemenscng" removed
            // along with the original Crystal Bay series-hybrid experiment.
            //
            // [XNE40] "bae" (HDS200) IS valid on L9N — this is the real-world
            // combo behind Crystal Bay Transit's in-house #2001 series-hybrid
            // pilot (L9N genset + BAE-pattern traction system, see fleet lore:
            // 2000 Series "XNE40" conversion). Left as a general compatibility
            // option rather than gated to one bus instance, since the shop-
            // built precedent means any L9N chassis could plausibly get the
            // same in-house conversion down the line — narratively tied to
            // #2001, but not code-restricted to it.
            // [NEW] eGen Flex and BAE Hybrid Gen3 are real B6.7/B7.2-only
            // pairings per Cummins/Allison/BAE's own materials -- neither
            // is offered behind a CNG engine in the real fleet chart this
            // is grounded in, so they redirect the same way HDS300/H40EP/
            // H50EP already do above.
            // [ADD] EP40/EP50 -- gas-engine bus never ran this diesel-era
            // drive unit either, same redirect-to-Voith treatment as the
            // rest of the hybrid list here.
            if (tx == "hds300" || tx == "h40ep" || tx == "h50ep" || tx == "h50ep_gen5" || tx == "siemenscng"
                || tx == "egenflex" || tx == "baegen3" || tx == "ep40" || tx == "ep50") tx = "voith";
            break;
    }

}
private int ob_shiftDelayLastGear = -999;
    // ═════════════════════════════════════════════════════════════════════════
    //  SIMULATION TICK
    // ═════════════════════════════════════════════════════════════════════════
    public void Tick(float dt)
    {
        if (!running) return;

        accel = Mathf.Clamp01(accel); bkPd = Mathf.Clamp01(bkPd);
        if (accel > 0.05f && bkPd  > 0f) bkPd  = 0f;
        if (bkPd  > 0.05f && accel > 0f) accel = 0f;
        shiftCD = Mathf.Max(0f, shiftCD - dt);
// [FIX] Was speed/accel-based ("gear = 0 whenever stopped with foot off
// the gas"), so a bus sitting at a red light IN DRIVE reported gear 0
// exactly like true Neutral -- wrongly qualifying for Fast Idle and the
// "neutral revving" RPM branches below. These EVTs don't have discrete
// gears to shift, so gear here is really just "engaged (1) or not (0)";
// it should track the selector (isNeutral), not motion, matching how
// Allison/Voith hold gear 1 at a dead stop in Drive.
if (IsElectric()) { gear = isNeutral ? 0 : 1; }
// [FIX] eGen Flex was falling all the way through this chain to the final
// "else DoVoithRange(dt)" catch-all -- meaning it was silently running
// Voith's actual multi-gear progression logic underneath the whole time,
// completely separate from (and undoing) the "no discrete gears" design
// intent. Treated the same single-gear way IsElectric() is above, since
// it's a continuously-variable parallel-hybrid drive unit, not a
// multi-speed gearbox that shifts through discrete ratios.
else if (tx == "egenflex40" || tx == "egenflex50") { gear = isNeutral ? 0 : 1; } // [FIX] see IsElectric() comment above -- same bug, same fix
else if (IsAllison())  DoAllisonGear();
else if (tx == "b500r") DoB500RGear();
else if (tx == "b500r_g5") DoB500RGen5Gear();
else if (tx == "b400r_g5") DoB400RGen5Gear();
else if (tx == "b3400xfe") DoB3400Gear();
else if (tx == "zf" || tx == "zfel2" || tx == "zfel2_hd")   DoZFGear();
else if (tx == "bae")  DoBAERange();
else if (tx == "hds300") DoHDS300Range();
else if (tx == "baegen3") DoBAEGen3Range();
else if (tx == "h40ep") DoH40EPGear(dt);
else if (tx == "h50ep" || tx == "zh50ep" || tx == "h50ep_gen5") DoH50EPGear(dt);
// [ADD] EP40/EP50 -- real pre-2010 generation of the exact same H40EP/
// H50EP drive unit (Allison renamed EP40->H40EP, EP50->H50EP circa 2010,
// same physical hardware, same power/torque -- see CalcEP40RPM/CalcEP50RPM
// header for the full real-research basis). Gear logic genuinely doesn't
// differ between generations (single-speed EVT either way), so these
// reuse H40EP/H50EP's own gear functions rather than duplicating them.
else if (tx == "ep40") DoH40EPGear(dt);
else if (tx == "ep50") DoH50EPGear(dt);
else if (tx == "d8645") DoD8645Range();
else if (tx == "d8646" || tx == "voith35" || tx == "d8646art" || diwaOpt1_4 || diwaOpt1_5) DoD8645Range(); // [FIX] Now covers opt1_4/opt1_5 too, not just the opt-less base case -- those alias tx to "voith" before dispatch, so checking the flags directly (rather than tx) targets exactly the d8646-origin buses without touching unrelated buses that also alias to "voith" (BAE/ZF/egenflex fallbacks etc, which never have these DIWA-specific opt flags set). voith35/d8646art added -- same gear logic as the rest of the d8646 family now.
else if (tx == "nxt")   DoNXTGear();
else                    DoVoithRange(dt);

// Old-bus worn linkage: tired valve body / linkage hesitates before
// committing to a shift. Stretches whatever cooldown the TX just set,
// so it's transmission-agnostic and never fights the TX's own logic.
if (oldBus && ob_delayedShifts && ob_shiftDelay > 0f && gear != ob_shiftDelayLastGear)
{
    shiftCD *= (1f + ob_shiftDelay);
    ob_shiftDelayLastGear = gear;
}
        if (alShiftTransientDur > 0f)
        {
            // [ADD] Gillig post-shift stall/rattle -- rising-edge detector
            // (this frame's duration higher than last frame's) catches the
            // exact moment ANY shift just fired, regardless of which tx's
            // specific transient duration (0.16s-0.26s) is in play, rather
            // than a fixed threshold that would miss the shorter ones.
            if (isGillig && alShiftTransientDur > gilligPrevShiftDur) gilligRattleVol = 0.11f;
            gilligPrevShiftDur = alShiftTransientDur;
            alShiftTransientDur -= dt;
            alShiftTransient = alShiftTransientDur > 0f ? alShiftTransientDur / 0.22f : 0f;
        }
        else gilligPrevShiftDur = 0f;
        gilligRattleVol *= 0.90f; // fast decay -- short rattle burst, not a sustained effect

        // ── Coast window: any gear change (in either direction) opens a
        // ~2s window. Inside that window, if the driver lets off the gas,
        // coast_windowTimer counts down and drives a soft overrun texture
        // in ProcessAudio. Engine-agnostic — works for every powertrain. ──
        if (gear != coast_lastGear)
        {
            coast_lastGear    = gear;
            coast_windowTimer = COAST_WINDOW;
        }
        else if (coast_windowTimer > 0f)
        {
            coast_windowTimer = Mathf.Max(0f, coast_windowTimer - dt);
        }

if (h50RevActive)
{
    h50RevTime += dt;

    if (h50RevTime > 8.0f)
        h50RevActive = false;
}
        if (IsElectric())
        {
            rpm = IDLE;
        }
        else
        {
float rpmTgt = IDLE;
// [FIX -- fundamental bug, same family as the RPM-dispatch gap above]
// eGen Flex has no torque converter either (disconnect-clutch parallel-
// hybrid, same as H40EP/H50EP -- it's their real successor system) --
// nothing to build/hunt line pressure with. Without this it would fall
// into the torque-converter neutral fast-idle hunt below whenever the
// selector sat in N, even though CalcH40EPRPM/CalcH50EPRPM (now actually
// wired up for it) already model its real idle-off/idle-low behavior.
bool isH4xEVT = (tx == "h40ep" || tx == "h50ep" || tx == "zh50ep" || tx == "h50ep_gen5" || tx == "egenflex40" || tx == "egenflex50" || tx == "ep40" || tx == "ep50");
// [FIX] "RPM very high at 0kph, slowly decreasing" on BAE — root cause was
// HERE, not in DoBAEDSP. BAE/HDS300/Gen3 were never excluded from this
// torque-converter "neutral fast-idle hunting" branch below, the same way
// h40ep/h50ep already are (isH4xEVT). A series-hybrid genset has no
// torque converter to hunt line pressure with — it should go idle-off at
// a stop, not oscillate around ~1750rpm forever. That 1750 target (and the
// slew toward it at 700-900rpm/s) is exactly what was audible as "high RPM
// at 0kph, slowly decreasing" — this whole time it had nothing to do with
// the new bae3_rpm stop-start logic, which only controls DoBAEDSP's own
// overlay; the actual host engine core runs off THIS rpm variable.
bool isBaeHybrid = (tx == "bae" || tx == "hds300" || tx == "baegen3");
// [FIX] These two branches used "gear == 0" as a stand-in for "selector
// actually in Neutral". That happened to work for the torque-converter
// transmissions (Allison/Voith/ZF/etc) ONLY by accident, and only once
// their own Do*Gear() has run at least once -- freshly shifted into Drive
// from a stop, gear also reads 0 before the driver ever presses the gas
// (DoAllisonGear()/DoB500RGear()/etc only got invoked below once
// spd/accel were already nonzero), so those buses would sit in this
// "neutral" revving/fast-idle-hunt branch in Drive at a red light too.
// Gated on isNeutral now so this genuinely only fires with the selector
// in N.
// [REVERTED] Was: tcParkedHunt, an attempt to route "stopped in Drive with
// parking brake set" into the same neutral idle-hunt/rev-bump branch as a
// real selector-in-N, to fix a "RPM paused at parking brake" complaint.
// That made the full random-walk hunt (1742-1762 RPM) plus the periodic
// "brrrrrr" rev-bumps (+260rpm) fire every single time the parking brake
// is set on almost every torque-converter transmission -- not what was
// wanted. Killed outright: parked-in-Drive-with-brake now falls straight
// through to the normal Calc*RPM() branch below and settles to a flat
// idle, same as it did before this was added. Only a genuine selector-in-N
// still gets the idle-hunt behavior.
bool tcParkedHunt = false;

if (isNeutral && accel > 0.03f && !isH4xEVT && !isBaeHybrid)
{
    // Neutral: no load on the engine, revs climb fast and freely
    rpmTgt = Mathf.Lerp(IDLE, 1670f, Mathf.Clamp01(accel * 1.4f));
}
else if ((isNeutral || tcParkedHunt) && !isH4xEVT && !isBaeHybrid)
{
    // Neutral, foot off the gas: fast-idle hunting rather than resting flat
    // at IDLE — oscillates gently within 1745-1755 RPM. This is a
    // torque-converter-automatic behavior (building line pressure), so it
    // does NOT apply to H40EP/H50EP/ZH50EP — those are parallel-hybrid EVT
    // retrofits on a plain ISL/ISB diesel genset that idles low normally,
    // same as it would with no hybrid gear attached at all. They fall
    // through to CalcH40EPRPM/CalcH50EPRPM below instead, which already
    // return the real low IDLE at a stop — this exclusion is what actually
    // lets that code run instead of being pre-empted here. [FIX] BAE/HDS300/
    // Gen3 excluded the same way now — no torque converter, no line
    // pressure to hunt. They fall through to CalcBAERPM/CalcHDS300RPM/
    // CalcBAEGen3RPM below, rebuilt to actually return 0 (engine off) when
    // not engaged instead of a floor around 950-1500rpm that never dropped.
    // [REBUILT per instruction/research] Random-walk the idle target instead
    // of tracing a clean sine -- governor hunting isn't periodic. Small,
    // slow, noise-driven drift within a real band, gently pulled back
    // toward 1750 so it never wanders off entirely.
    idleHuntRpm += (float)(NextNoiseSample() * 6.5 * dt);
    idleHuntRpm += (1750f - idleHuntRpm) * dt * 0.6f;
    idleHuntRpm = Mathf.Clamp(idleHuntRpm, 1742f, 1762f);
    // [REDONE per instruction] Not a subtle wobble -- a genuinely audible
    // "brrrrrr...BRRRRrrrrrrr...BRRRR" rev bump, randomly spaced, same
    // deal on CNG/gas engines too (this whole branch already isn't gated
    // to diesel -- only H4x/BAE-hybrid EVTs are excluded above -- so CNG's
    // siemenscng tx already runs through here same as everything else).
    // Real cause: the torque converter's charge pump re-pressurizing/
    // catching up while stalled in gear -- genuinely revs the engine up a
    // few hundred RPM for a moment, not a barely-there ripple.
    idleHuntBumpTimer -= dt;
    if (idleHuntBumpTimer <= 0f && (NextNoiseSample() * 0.5 + 0.5) < dt * 0.10)
    {
        idleHuntBumpEnv   = 1f;
        idleHuntBumpTimer = 2.0f + (float)(NextNoiseSample() * 0.5 + 0.5) * 3.5f; // next bump 2-5.5s out
    }
    // Quick-ish rise into the bump, slower settle back down out of it --
    // reads as a real rev-and-fall, not a click.
    float bumpRate = idleHuntBumpEnv > 0.9f ? dt * 3.0f : dt * 1.3f;
    idleHuntBumpEnv = Mathf.Max(0f, idleHuntBumpEnv - bumpRate);
    rpmTgt = idleHuntRpm + idleHuntBumpEnv * 260f;
}
else if (!isNeutral || spd >= 0.0000001f || accel >= 0.03f || isH4xEVT || tx == "nxt" || tx == "zfel2" || tx == "zfel2_hd")
{
                // [FIX] "!isNeutral ||" added to this gate -- without it, a
                // mechanical/torque-converter bus freshly shifted into
                // Drive from a stop never got its Do*Gear() called until
                // the driver pressed the gas at least once (gear was still
                // 0 and neither of the two branches above fired for it),
                // so it sat revving/fast-idle-hunting like Neutral even
                // though the selector already read Drive. Now the real
                // gear routine runs immediately once out of Neutral,
                // bringing gear up to 1 (creep) right away, matching how
                // an Allison actually behaves the instant you pull it into
                // D.
                // [NOTE] tx=="nxt" added to this gate's condition above --
                // every other TX here only needs RPM math while actually
                // moving or under throttle, but NXT's stop-start timer (in
                // CalcNXTRPM) has to keep ticking even at a dead stop with
                // the foot off the gas, or the engine-off state would never
                // trigger. Harmless when Eco/economyMode is off -- the
                // function just returns a normal idle-based RPM in that case.
                if      (IsAllison())    rpmTgt = CalcAllisonRPM();
                else if (tx == "b500r") rpmTgt = CalcB500RRPM();
                else if (tx == "b500r_g5") rpmTgt = CalcB500RGen5RPM();
                else if (tx == "b400r_g5") rpmTgt = CalcB400RGen5RPM();
                else if (tx == "b3400xfe") rpmTgt = CalcB3400RPM();
                else if (tx == "zf")    rpmTgt = CalcZFRPM();
                else if (tx == "zfel2") rpmTgt = CalcZFEL2RPM(false);
                else if (tx == "zfel2_hd") rpmTgt = CalcZFEL2RPM(true);
                else if (tx == "bae")   rpmTgt = CalcBAERPM();
                else if (tx == "hds300") rpmTgt = CalcHDS300RPM();
                else if (tx == "baegen3") rpmTgt = CalcBAEGen3RPM();
                else if (tx == "h40ep") rpmTgt = CalcH40EPRPM(dt);
                else if (tx == "h50ep" || tx == "zh50ep" || tx == "h50ep_gen5") rpmTgt = CalcH50EPRPM(dt);
                else if (tx == "ep40") rpmTgt = CalcEP40RPM(dt);
                else if (tx == "ep50") rpmTgt = CalcEP50RPM(dt);
                else if (tx == "d8645") rpmTgt = CalcD8645RPM();
                else if (tx == "d8646" || tx == "voith35" || tx == "d8646art" || diwaOpt1_4 || diwaOpt1_5) rpmTgt = CalcD8645RPM(); // [FIX] Now covers opt1_4/opt1_5 too, not just the opt-less base case -- those alias tx to "voith" before dispatch, so checking the flags directly (rather than tx) targets exactly the d8646-origin buses without touching unrelated buses that also alias to "voith" (BAE/ZF/egenflex fallbacks etc, which never have these DIWA-specific opt flags set). voith35/d8646art added -- same RPM curve as the rest of the d8646 family now.
                else if (tx == "nxt")   rpmTgt = CalcNXTRPM(dt);
                // [FIX -- fundamental bug] eGen Flex was never wired to its
                // own RPM math at all -- its gear dispatch (isNeutral?0:1)
                // and its DSP voice were both correctly hooked up, but RPM
                // silently fell through to this final CalcVoithRPM() -- a
                // discrete 6-gear torque-converter curve indexed by a gear
                // value eGen Flex never uses past 1. Routed to its own
                // CalcEGenFlexRPM now (see that function -- deliberately
                // punchier than H40EP/H50EP's curve, per instruction to
                // keep the aggressive rev-gain feel the old fallback gave
                // it by accident, now built as a real curve of its own).
                else if (tx == "egenflex40" || tx == "egenflex50") rpmTgt = CalcEGenFlexRPM(dt);
                else                    rpmTgt = CalcVoithRPM();
}

// [NEW] Fast Idle Speed switch -- real gating straight from a New Flyer
// XN40 manual: only takes effect with the engine actually running, the
// shift selector in neutral, AND the parking brake applied. Raises the
// idle target rather than the driver's raw request alone doing anything,
// so flipping the switch while still in gear (for example) correctly has
// no effect, matching the real system's own behavior.
// [FIX] Was "gear == 0", which the EVT/hybrid transmissions also report
// while simply stopped in Drive (see isNeutral field comment above) --
// that let Fast Idle fire on those buses at any red light instead of only
// with the selector actually in N. Gated on isNeutral now, matching the
// real XN40 manual: selector in N + parking brake + engine running.
bool fastIdleActive = fastIdleRequested && isNeutral && parkingBrake && engineState == EngineRunState.Running;
if (fastIdleActive) rpmTgt = Mathf.Max(rpmTgt, IDLE * 1.55f);

float rpmRate = rpmTgt > rpm ? (gear == 0 ? 900f : 350f) : (gear == 0 ? 700f : 280f);
if (gear == 1 && accel > 0.10f && !economyMode)
{
    /* Fast enough to actually trace the pulse/dip/pulse shape instead of
       smoothing it away. */
    rpmRate = rpmTgt > rpm ? 2600f : 2200f;
}
// [FIX] The normal downward rate (280f) is genuinely slow -- a typical
// ~500 RPM post-upshift drop takes ~1.8s to fully settle at that rate.
// With short shift cooldowns (especially Gen5's), another shift can fire
// before RPM has even gotten halfway down, so it visually/audibly never
// "fully" drops -- it's perpetually chasing a target that keeps moving
// again before it arrives. alShiftTransient is freshly set to 1f on
// EVERY shift (up or down, every tx) right when it happens -- catching
// that moment specifically, and only when the new target is LOWER
// (rpmTgt < rpm, i.e. an upshift's natural RPM drop, not a downshift's
// natural rise), snaps rpm most of the way there immediately instead of
// crawling down at the slow rate. Universal fix -- helps every
// transmission, not just Gen5.
// [FIX] The blanket 4200 rpm/s snap below was written to stop Gen5's
// short shift-cooldown chains from perpetually chasing a moving target,
// but it fired on EVERY tx's every upshift regardless of gear -- so 4/5/6
// (direct-drive/overdrive, smaller ratio steps, meant to feel like the
// bus just eased off half a beat) were snapping the RPM drop essentially
// instantly instead of letting it sag down over a few tenths of a
// second. Splitting the rate by landing gear: low gears keep the fast
// resolve (they shift in quick succession under load and need to actually
// finish dropping between shifts), high gears get a much gentler ease so
// the drop reads as "eased off a bit," not a hard cut.
if (alShiftTransient >= 0.99f && rpmTgt < rpm)
    rpmRate = Mathf.Max(rpmRate, gear >= 4 ? 850f : 4200f);
rpm += Mathf.Sign(rpmTgt - rpm) * Mathf.Min(Mathf.Abs(rpmTgt - rpm), rpmRate * dt);
// [FIX] THIS was the actual bottleneck making Voith kickdown "still get
// stuck, still rpm limit" -- CalcVoithRPM's own gear1Ceiling (raised to
// 4550f under kickdown) never mattered, because this shared, generic
// clamp runs AFTER every Calc*RPM() function regardless of what it
// returned, and was hard-capped at GOV+30 for every engine/tx. Voith
// kickdown (voith_kdRevActive) now gets a genuinely large overspeed
// allowance here instead -- "gain RPM... more aggressive" means actually
// letting it climb well past the normal governor line during the hold,
// not just reaching right up to it.
float govCeiling = voith_kdRevActive ? GOV + 500f : GOV + 30f;
rpm  = Mathf.Clamp(rpm, IDLE * 0.8f, govCeiling);

// Old-bus rev hang: worn throttle return spring / sticky linkage holds
// RPM up after a hard lift-off instead of snapping back to idle target.
if (oldBus && ob_revHang)
{
    bool liftOff = accel < 0.04f && rpm > IDLE + 200f;
    if (liftOff)
    {
        ob_revHangRPM = Mathf.Max(ob_revHangRPM, rpm);
        float hangTarget = Mathf.Lerp(rpmTgt, ob_revHangRPM, ob_revHangAmt);
        rpm = Mathf.MoveTowards(rpm, hangTarget, 60f * dt); // slow bleed-down
        ob_revHangRPM = Mathf.MoveTowards(ob_revHangRPM, IDLE, 90f * dt);
    }
    else ob_revHangRPM = rpm;
}

// ── [ADD] Fake stall-recovery RPM shape — opt1_4 gear 1, first half only ──
// Real-world observation: DIWA gear 1 winds up sounding like it just came
// OFF a stall-hold recovery even though no stall-hold ever actually
// happened. Modeled as if the driver had genuinely been holding stall for
// ~5s right before taking off (long enough for the real stall-hold's
// 900 rpm/s drag to have already fully bottomed at the floor -- "held for
// 5s" and "sitting at IDLE+250" are the same state, no need to simulate
// the 5s itself).
//
// Both the hold AND the climb happen entirely within the first half --
// the climb must be DONE by the time g1Progress crosses 0.5, not still
// running through it, so the second half is always fully back to normal
// gear-1 RPM with no override touching it. The climb itself still uses
// the real, unscaled 350 rpm/s recovery rate (same as an actual
// stall-hold release) rather than some compressed rate forced to fit the
// window -- under heavy throttle the true gap can take longer than the
// first half has room for, so the 0.5 crossing snaps straight to rpmTgt
// as a safety net. That snap is what enforces "before, not during" when
// the honest rate can't finish in time; it never touches the rate itself.
// Placed here (still inside the IsElectric()-else block) specifically
// because rpmTgt only exists in this scope -- it goes out of scope at
// the closing brace right after this.
// [FIXED, PER REQUEST — "too many rpm checks between 0-5kph when
// decelling... increases then decreases"] This used to arm on ANY frame
// with gear==1 && g1Progress<0.5, with no check on whether you were
// actually launching. So coasting/braking back down through that same
// low-speed range while still sitting in gear 1 -- which is most of a
// deceleration to a stop -- ALSO got pinned to IDLE+250 and then forced
// to "climb" at 350rpm/s toward rpmTgt, exactly like a fresh launch. That
// produced a fake rise right in the middle of slowing down, followed by
// a fall again once rpmTgt (which is itself falling as speed drops)
// caught up -- the up-then-down bump. Now it only arms on a genuine
// stopped/near-stopped + throttle-applied transition, so decelerating
// through this range no longer triggers it at all -- rpm just follows
// rpmTgt normally.
bool genuineLaunch = spd < 1.5f && accel > 0.05f;
if (diwaOpt1_4 && gear == 1 && (v6_fakeStallActive || genuineLaunch))
{
    // Same progress metric opt1_4's stretched gear 1 uses elsewhere --
    // local copy since this runs in a different method than where
    // g1Progress normally gets computed for the whine/DSP side.
    float g1Progress = Mathf.Clamp01(spd / Mathf.Max(1f, VOITH_GEAR1_UPSHIFT_SPD * V6_OPT4_G1_EXTEND));

    if (!v6_fakeStallActive) { v6_fakeStallActive = true; v6_fakeStallTimer = 0f; }
    v6_fakeStallTimer += dt;

    const float FAKE_STALL_HOLD_SEC = 0.30f; // brief token hold, then real-rate recovery starts

    if (g1Progress < 0.5f)
    {
        if (v6_fakeStallTimer < FAKE_STALL_HOLD_SEC)
        {
            // Still "in the hold" -- pinned flat, where 5s of real
            // stall-hold would have already settled. No climb yet.
            rpm = IDLE + 250f;
        }
        else
        {
            // Release moment -- genuine 350 rpm/s recovery rate, same as
            // a real stall-hold release, not compressed to fit anything.
            rpm = Mathf.MoveTowards(rpm, rpmTgt, 350f * dt);
        }
    }
    else
    {
        // Hard cutover at the halfway line -- guarantees the climb is
        // fully resolved before the second half starts, even if throttle
        // made the gap too big for the real rate to close naturally by now.
        rpm = rpmTgt;
        v6_fakeStallActive = false;
    }
}
else
{
    v6_fakeStallActive = false;   // resets so the next genuine launch re-triggers fresh
}
        }

// ── Converter Stall Hold ────────────────────────────────────────────────
// Ctrl + throttle: locks road speed and drags RPM down into a strained
// drone, like power-braking against a torque converter that won't let the
// bus actually accelerate. Gated so it can't be used from a dead stop:
// needs either a proven G1->G2 upshift at some point this trip, or (for
// gearless/direct-drive electrics that never shift) at least 7 km/h.
if (gear >= 2) hasUpshiftedEver = true;
bool stallEligible = hasUpshiftedEver || spd >= 7f;
bool wantStallHold = stallHoldKey && accel > 0.5f && stallEligible && spd > 0.05f;
if (wantStallHold && !stallHoldActive)
{
    stallHoldActive    = true;
    stallHoldHeldSpeed = spd;
    stallHoldEngageT   = 0f;
}
if (stallHoldActive)
{
    stallHoldEngageT += dt;
    // Drag RPM down off whatever it was tracking toward a strained hold
    // point, fast enough to read as the engine "giving up" against a load.
    float strainTarget = Mathf.Max(IDLE + 250f, rpm * 0.80f);
    rpm = Mathf.MoveTowards(rpm, strainTarget, 900f * dt);

    if (!stallHoldKey || accel < 0.5f || bkPd > 0.01f)
    {
        stallHoldActive  = false;
        stallHoldEngageT = 0f;
    }
}

        if (accel > 0.01f && gear > 0)
        {
            float gF = 1.0f;
            if (IsElectric())
            {
                gF = spd < 30f ? 1.25f : Mathf.Lerp(1.25f, 0.85f, Mathf.Clamp01((spd - 30f) / 30f));
            }
            else
            {
                float[] tqMod = { 0f, 1.3f, 1.0f, 0.8f, 0.65f, 0.5f, 0.35f };
                if      (IsAllison() && gear < tqMod.Length) gF = tqMod[gear];
                else if (tx == "zf"  && gear < tqMod.Length) gF = tqMod[gear];
                else if (tx == "bae")   gF = spd < 13f ? 3.0f : spd < 20f ? 0.94f : 1.573f;
                else if (tx == "hds300") gF = spd < 13f ? 3.2f : spd < 20f ? 1.00f : 1.65f;
                else if (tx == "baegen3") gF = spd < 13f ? 2.9f : spd < 20f ? 0.90f : 1.50f;
                else if (tx == "h40ep") gF = h40Mode == 1 ? (1.4f - spd / 80f) : 1.0f;
                else if (tx == "h50ep" || tx == "zh50ep" || tx == "h50ep_gen5") gF = h40Mode == 1 ? (1.50f - spd / 80f) : 1.0f;
                // [ADD] EP40/EP50 -- same real output as H40EP/H50EP (the
                // 2010 rename didn't change power/torque), so same curve.
                else if (tx == "ep40") gF = h40Mode == 1 ? (1.4f - spd / 80f) : 1.0f;
                else if (tx == "ep50") gF = h40Mode == 1 ? (1.50f - spd / 80f) : 1.0f;
                else if (tx == "b500r") { float[] tqB5 = { 0f, 1.40f, 1.05f, 0.85f, 0.65f, 0.50f, 0.38f }; if (gear < tqB5.Length) gF = tqB5[gear]; }
                else if (tx == "b3400xfe") { float[] tqB34 = { 0f, 1.32f, 1.00f, 0.82f, 0.64f, 0.50f, 0.37f }; if (gear < tqB34.Length) gF = tqB34[gear]; }
                else if (tx == "nxt") { float[] tqNxt = { 0f, 1.35f, 1.05f, 0.85f, 0.68f, 0.54f, 0.42f }; if (gear < tqNxt.Length) gF = tqNxt[gear]; }
            }
            float hillResist = hillMode ? 0.72f : 1.0f;
            spd = Mathf.Min(spd + accel * 1.2f * gF * 2.8f * hillResist * dt, MAX_SPD);
        }

        // Stall Hold overrides whatever the throttle integration just did —
        // speed stays pinned at the value it was engaged at.
        if (stallHoldActive) spd = stallHoldHeldSpeed;

        if ((tx == "h40ep" || tx == "h50ep" || tx == "zh50ep" || tx == "h50ep_gen5") && h40StopStart) { spd = 0f; accel = 0f; bkPd = 0f; }
if (tx == "h50ep" || tx == "zh50ep" || tx == "h50ep_gen5")
{
    // Simplified rotation tracker without throttle/decel overrev triggers
    h50RevCycle += dt * (0.06f + accel * 0.20f);
    if (h50RevCycle > 1f) h50RevCycle -= 1f;

    bool revEngaged = accel > 0.12f && gear > 0 && spd > 2.5f;
    if (revEngaged) h50RevActive = true;
    else if (accel < 0.06f) h50RevActive = false;

    h50RevTime += dt * (revEngaged ? 1.0f : -1.8f);
    h50RevTime = Mathf.Clamp(h50RevTime, 0f, 6.0f);
    
    // REMOVED: All the conflicting h50EngineRevZone, h50WhineLevel, and 
    // h50MotorLevel Lerp overrides that were fighting your gear state machine!
}
else
{
    h50WhineLevel = Mathf.Lerp(h50WhineLevel, 0f, dt * 0.2f);
    h50MotorLevel = Mathf.Lerp(h50MotorLevel, 0f, dt * 1.0f);
    h50EngineRevZone = Mathf.Lerp(h50EngineRevZone, 0f, dt * 2.0f);
    h50RevBurst = 0f;
    h50RevCycle = 0f;
    h50RevActive = false;
}
        if (!IsElectric())
        {
            if (kickdownKey)
            {
                float accelDelta = accel - prevAccelForPump;
                if (Mathf.Abs(accelDelta) > 0.15f)
                { pumpEnergy = Mathf.Min(1f, pumpEnergy + Mathf.Abs(accelDelta) * 1.5f); kickdownPumpTimer = 0f; }
                kickdownPumpTimer += dt;
                pumpEnergy = Mathf.Max(0f, pumpEnergy - dt * 0.6f);
            }
            else { pumpEnergy = Mathf.Max(0f, pumpEnergy - dt * 2f); kickdownPumpTimer = 0f; }
            prevAccelForPump = accel;
        }

        float hillDrag = hillMode ? 0.18f : 0f;
        spd = Mathf.Max(0f, spd - (0.22f + hillDrag + 0.006f * spd) * dt);
        if (bkPd > 0.01f && spd > 0f)
        {
            float brakeRate = IsElectric() ? (spd > 5f ? 26f : 14f) : 20f;
            spd = Mathf.Max(0f, spd - brakeRate * bkPd * dt);
        }
    }
private double ph_al_growl1, ph_al_growl2, ph_al_growlSub;
private double ph_al_crackNoise;
private float  al_crackVol = 0f;
private double ph_al_contWhBright;
 private void GetAllisonGearWindow(out float progress)
{
    if (gear < 1) { progress = 0f; return; }
    float hi = GetAlUp(gear);
    progress = Mathf.Clamp01(spd / Mathf.Max(1f, hi));
}
private void GetAllisonGearShape(out float onsetBark, out bool hardCrackEnd,
                                  out float driveBase, out float hzBase, out float hzTop)
{
    switch (gear)
    {
        case 1: onsetBark = 0f;   hardCrackEnd = true;  driveBase = 1.3f; hzBase = 30f; hzTop = 58f; break;
        case 2: onsetBark = 1f;   hardCrackEnd = true;  driveBase = 1.5f; hzBase = 34f; hzTop = 54f; break;
        case 3: onsetBark = 0.5f; hardCrackEnd = false; driveBase = 1.2f; hzBase = 32f; hzTop = 46f; break;
        case 4: onsetBark = 0.5f; hardCrackEnd = false; driveBase = 1.35f;hzBase = 34f; hzTop = 48f; break;
        case 5: onsetBark = 0.6f; hardCrackEnd = false; driveBase = 1.6f; hzBase = 42f; hzTop = 60f; break;
        default:onsetBark = 0.6f; hardCrackEnd = false; driveBase = 1.7f; hzBase = 46f; hzTop = 66f; break;
    }
}







// ── L9N — REBUILT FROM SCRATCH, RPM/SPEED-ONLY ─────────────────────────────
    // Nothing below depends on tx, gear, engineType branching elsewhere, or
    // any prior L9N synthesis. Single input dependency: rpm (already computed
    // upstream each Tick via CalcVoithRPM/CalcAllisonRPM/etc — that's fine,
    // this layer only reads the resulting rpm float, it doesn't care how it
    // got there).
    private double ph_l9n_fund, ph_l9n_sub, ph_l9n_third, ph_l9n_fifth, ph_l9n_exhaust;
    private float  l9n_rpmSmooth = 0f;
    private const float L9N_IDLE_RPM       = 650f;
    private const float L9N_GOV_RPM        = 2000f;
    private const int   L9N_FIRING_ORDER   = 6;
    private const float L9N_WOBBLE_HZ      = 2.2f;
    private const float L9N_WOBBLE_DEPTH   = 0.07f;
private float eBaseMul(float engMul, float rn) => engMul * (0.5f + rn * 0.5f);

// [NEW — shared base] Generic TCC lock/unlock blend for every Allison
// variant except Gen4 B400R (which keeps its own bespoke speed-gated
// partial lock inside gear 1 via B4xUpdateTCC/b4r_tccBlend directly).
// Reuses those SAME fields (b4r_tccBlend/b4r_tccReleaseTmr) rather than
// separate per-tx state, because a given bus instance only ever runs ONE
// Allison variant at a time -- there's no risk of two tx's state
// colliding. This is the piece B500R/B3400xFE/both Gen5s were missing:
// they toggled slip on/off as a hard instant switch keyed off AL_LOCK's
// bool for the current gear, so the moment of actually locking or
// unlocking mid-gear (not just the gear-change moment) had zero
// transition -- felt like a "clunk," not a real clutch engaging over a
// few tenths of a second. Now every variant gets the same smooth
// MoveTowards blend and a brief forced-open flare across the one
// lock-state boundary each family actually has (real B500R/B3400/Gen5
// units DO have exactly one such boundary each -- see each AL_LOCK_*
// table -- this isn't inventing TCC behavior the real hardware lacks).
private bool  al_lockStatePrev = false;
private void UpdateAllisonLockBlend(bool wantLockedNow, float applyTime, float releaseTime, float dt)
{
    if (wantLockedNow != al_lockStatePrev)
    {
        b4r_tccReleaseTmr = releaseTime;
        al_lockStatePrev = wantLockedNow;
    }
    bool lockedNow = wantLockedNow;
    if (b4r_tccReleaseTmr > 0f) { b4r_tccReleaseTmr -= dt; lockedNow = false; }
    float rate = dt / Mathf.Max(0.01f, applyTime);
    b4r_tccBlend = Mathf.MoveTowards(b4r_tccBlend, lockedNow ? 1f : 0f, rate);
}



 private void DoAllisonGearGrowl(ref double engineSample, float ld, float engMul, double invSR)
{
    if (gear < 1) { al_crackVol *= 0.9f; return; }
 
    GetAllisonGearWindow(out float progress);
    GetAllisonGearShape(out float onsetBark, out bool hardCrackEnd, out float driveBase, out float hzBase, out float hzTop);
 
    /* Segments: 0.00-0.12 onset (bark if this gear has one, else quiet
       open), 0.12-0.75 "wooo->eeee" build, 0.75-1.00 either a hard crack
       (G1/G2) or an open "ahhh" trail-off (G3+). */
    float onsetT = Mathf.Clamp01(progress / 0.12f);
    float buildT = Mathf.Clamp01((progress - 0.12f) / 0.63f);
    float endT   = Mathf.Clamp01((progress - 0.75f) / 0.25f);
 
    float vol, hz, drive;
    if (progress < 0.12f)
    {
        vol   = onsetBark * Mathf.SmoothStep(0.6f, 0f, onsetT); // bark hits hard then eases into the build
        hz    = Mathf.Lerp(hzTop * 0.9f, hzBase, onsetT);
        drive = driveBase * 1.8f;
    }
    else if (progress < 0.75f)
    {
        vol   = Mathf.SmoothStep(0.30f, 0.90f, buildT);
        hz    = Mathf.Lerp(hzBase, hzTop, buildT);
        drive = driveBase * Mathf.Lerp(1.0f, 1.6f, buildT);
    }
    else if (hardCrackEnd)
    {
        vol   = Mathf.SmoothStep(0.90f, 1.25f, endT);
        hz    = Mathf.Lerp(hzTop, hzTop * 1.15f, endT);
        drive = driveBase * (2.4f + endT * 1.0f);
    }
    else
    {
        /* open trail-off — "ahhhhh", volume settles rather than spiking */
        vol   = Mathf.SmoothStep(0.90f, 0.62f, endT);
        hz    = Mathf.Lerp(hzTop, hzTop * 0.92f, endT);
        drive = driveBase * 1.4f;
    }
 
    double s1 = Math.Sin(2.0 * Math.PI * ph_al_growl1);
    double s2 = Math.Sin(2.0 * Math.PI * ph_al_growl2);
    double ssub = Math.Sin(2.0 * Math.PI * ph_al_growlSub);
    double growl = Math.Tanh((s1 * 1.0 + s2 * 0.5 - ssub * 0.22) * drive);
 
    float finalVol = vol * (0.30f + ld * 0.55f) * engMul;
    engineSample += growl * finalVol;
 
    ph_al_growl1   = (ph_al_growl1   + hz         * invSR) % 1.0;
    ph_al_growl2   = (ph_al_growl2   + hz * 1.498 * invSR) % 1.0;
    ph_al_growlSub = (ph_al_growlSub + hz * 0.5   * invSR) % 1.0;
 
    /* Hard crack — a short noise-burst transient right at the shift, only
       for gears that end that way (G1/G2). Triggered off shiftCD like the
       existing thud transients elsewhere in the file. */
    if (hardCrackEnd && gear != al_prevGearForTap) al_crackVol = 1.0f;
    if (al_crackVol > 0.002f)
    {
        float crackEnv = al_crackVol;
        engineSample += noise_hi * crackEnv * 0.14f * engMul + noise_lp * crackEnv * 0.08f * engMul;
        al_crackVol *= 0.90f;
    }
}
    public bool RegenActive =>
        (engineType == EngineType.B67 || engineType == EngineType.XE40 || engineType == EngineType.XE60
         || engineType == EngineType.XHE40 || engineType == EngineType.XHE60) && bkPd > 0.08f && spd > 0.5f;

    // ── Convenience: matches all Allison B400R tx strings ────────────────────
    // ── Convenience: matches all electric-drivetrain buses (XE40 direct-drive,
    // XE60's ZF AVE 130 portal axle, and the two hydrogen fuel cell variants —
    // XHE40/XHE60 use the same traction hardware as XE40/XE60, just with a
    // fuel cell charging the battery instead of plug/pantograph charging, so
    // they're electric here too) — anywhere vehicle-dynamics code just needs
    // to know "no gearbox, no combustion", not which one.
    private bool IsElectric() => tx == "elfa3" || tx == "accelera" || tx == "zfave130"
                               || tx == "elfa3_centeraxle" || tx == "accelera_centeraxle"
                               || tx == "elfa2" || tx == "accelera_fc" || tx == "fcave130"
                               || tx == "elfa2_centeraxle" || tx == "accelera_fc_centeraxle"
                               // legacy strings kept recognized in case any save data / prefab still has an old name
                               || tx == "electric" || tx == "fcelfa"
                               || tx == "elfa3_zfave130" || tx == "accelera_zfave130"
                               || tx == "elfa2_zfave130" || tx == "accelera_fc_zfave130";

    // [siemensCNG] Added here rather than a hand-copy, per direction ("use
    // EVERYTHING from Allison") -- IsAllison() is the actual switch that
    // gear dispatch (DoAllisonGear), rpm dispatch (CalcAllisonRPM), torque
    // scaling, and downstream whine/growl audio all key off throughout this
    // file, so this reuses the real mechanism rather than re-deriving it.
    private bool IsAllison() => tx == "allison" || tx == "b400r";

    // ═════════════════════════════════════════════════════════════════════════
    //  GEAR HELPERS — Allison B400R
    // ═════════════════════════════════════════════════════════════════════════
// ═════════════════════════════════════════════════════════════════════════
    //  GEAR HELPERS — Allison B400R
    // ═════════════════════════════════════════════════════════════════════════

    private float GetAlUp(int g)
    {
        if (g < 1 || g >= AL_R.Length) return 0f;

        // [REBUILT per correction] Real Load-Based Shift Scheduling: the
        // shift RPM target scales CONTINUOUSLY with throttle load for
        // BOTH modes, converging to the same low, smooth, early-shifting
        // baseline at light throttle -- performance mode itself already
        // has a low RPM ceiling and early/smooth shifts at low throttle,
        // it's not that economy is simply "ignored" there. Only as load
        // increases does the target climb, and economy's ceiling climbs
        // less than performance's -- which is WHY there's no difference at
        // low throttle (both compute nearly the same low value), not
        // because the modes are hardcoded identical at low load.
        if (g == 1)
        {
            float downBackSpeed = GetAlDown(2);
            return engineType == EngineType.ISL9 ? downBackSpeed + 3f : downBackSpeed + 6f;
        }

        float perfHighTarget = (g == 2 || g == 3) ? 1650f : 1700f;
        float ecoHighTarget  = 1350f;
        // [KICKDOWN OVERRIDE] Kickdown forces full hard performance,
        // same as the old B400R behavior -- it's a driver override of Eco,
        // not just "high accel," so it bypasses the eco target entirely
        // rather than merely pushing accel toward 1 through the lerp.
        float highTarget = (economyMode && !kickdownKey) ? ecoHighTarget : perfHighTarget;
        float shRPM = Mathf.Lerp(1100f, highTarget, Mathf.Clamp01(accel));
        return (shRPM / AL_R[g] / FINAL) * TCIRC / 60f * 3.6f;
    }

    private float GetAlDown(int g)
    {
        if (g < 1 || g >= AL_R.Length) return 0f;

        // [REBUILT per correction] Same continuous load-scaling as
        // GetAlUp -- both modes converge to a common low, smooth downshift
        // RPM at light throttle. As load increases, performance's target
        // climbs higher (downshifts sooner/at a higher RPM to keep power
        // available), economy's climbs less (tolerates lower RPM, resists
        // downshifting, letting it sag toward peak-torque) -- but at
        // accel=0 they're the same value, which is the real reason there's
        // no difference at low throttle.
        float perfHighTarget = 1100f;
        float ecoHighTarget  = 850f;
        // [KICKDOWN OVERRIDE] Same as GetAlUp -- kickdown bypasses eco entirely.
        float highTarget = (economyMode && !kickdownKey) ? ecoHighTarget : perfHighTarget;
        float r = Mathf.Lerp(700f, highTarget, Mathf.Clamp01(accel));

        return (r / AL_R[g] / FINAL) * TCIRC / 60f * 3.6f;
    }
// ── L9 hoarse rasp — narrow resonant "sore throat" texture, not broadband
// grit. Two close-detuned formant-style tones (≈900Hz/1050Hz) amplitude-
// modulated by noise and gated to the firing pulse, instead of flat hiss.
double ph_l9_formant1 = 0, ph_l9_formant2 = 0; // (declare as private double fields outside the loop, see note below)
private float al_g12BumpPhase = 0f; private bool al_g1CrackArmed = false;
private bool  al_dtapFired  = false; // one-shot latch, resets when conditions leave the trigger zone
private bool  al_dtapActive = false;
private float al_dtapTimer  = 0f;
 
// CRITICAL: Add this variable at the top of your script with your other class variables!
private float al_dtapStartRPM = 0f;
private float al_dtapPeakRPM  = 0f;
private float al_dtapEndRPM   = 0f;
private float al_smoothedRPM = 0f;
private float al_rpmVelocity = 0f;
// [ADD] Shared smoothing state for the ZF family (zf/zfel2/zfel2_hd) --
// same one-tx-per-bus safety as al_smoothedRPM being reused across the
// whole Allison family below.
private float zf_smoothedRPM = 0f;
private float zf_rpmVelocity = 0f;

// ═══════════════════════════════════════════════════════════════════════════
//  B400R REWORK — replaces the old CalcAllisonRPM / DoAllisonGear /
//  DoB400RWhineDSP. See notes at each section for what changed and why.
//
//  SOURCED (Allison B 400/B 400R product spec, bus applications):
//   • Ratios 3.49 / 1.86 / 1.41 / 1.00 / 0.75 / 0.65, coverage 5.03:1 — AL_R
//     already matches this, unchanged.
//   • Lockup clutch with torsional damper, integral/standard, available ALL
//     ranges — including gear 1. The old AL_LOCK bool array couldn't express
//     that gear 1 is open at launch and locks partway through, so it's
//     replaced with a blend (b4r_tccBlend, 0=open→1=locked).
//   • 4000-family housing: three planetary gear sets. AL_R[4] == 1.00 is
//     direct drive (gearset locked, rotates as a unit); 5/6 are overdrive.
//     That's the real boundary for the two-whine split below, not a
//     1-2/3-5 split.
//
//  1. TCC LOCKUP IS A BLEND, NOT A FLAG. b4r_tccBlend ramps 0→1 once gear 1
//     passes B4R_TCC_LOCK_KPH_G1, releases briefly across every shift, and
//     reapplies. RPM lerps openRPM↔lockedRPM on it; the TC slip whoosh in
//     the whine DSP is gated on (1-blend), which makes it audible for the
//     first time — it was dead code before (AL_LOCK[1] was permanently true).
//     Traced: gear 1 pins at ceiling (1880) from ~15 kph to the 31.3 kph
//     upshift — 16 kph of flat RPM — and the lockup event lands inside that
//     stretch, pulling RPM from ~1900 down to ~1580. Because the TCC also
//     releases and reapplies across the 1→2 shift, RPM now flares toward
//     ceiling and settles back on its own — the same shape the old scripted
//     al_dtap flare/lock/settle block was hand-drawing, now falling out of
//     the converter model instead.
//
//  2. THE WHINE DOES THE DOUBLE-TAP, NOT THE RPM. No more scripted RPM
//     flare. The tap is an amplitude+pitch envelope on the LOW WHINE, fired
//     on the G1→G2 edge — two bumps with a gap, tunable via B4R_TAP*.
//
//  3. TWO WHINES ON THE REAL GEAR REGIMES. Gears 1-3 (reduction) run the
//     LOW whine — the measured 2014 ISL9/B400R values stay untouched (280Hz
//     base, exp 0.32 RPM sensitivity, inharmonic 1.22x/1.48x partials.)
//     Gear 4 (direct, ratio 1.00) dips both whines — nothing's meshing under
//     load. Gears 5-6 (overdrive) bring in the HIGH whine, shaped (not
//     measured) and tracking output shaft speed so it keeps climbing through
//     the top gears instead of resetting each shift.
//
//  4. KICKDOWN ONLY SHORTENS GEAR 1. isl9Character no longer includes
//     kickdownKey — that used to also raise converter slip and the RPM
//     ceiling. Kickdown's only effect now is kdUpExt on the G1→G2 speed.
//     [BUG FIX] GetAlUp(2)*0.62 = 19.4 kph, below GetAlDown(2) = 20.9 kph —
//     the bus would upshift under kickdown then immediately re-satisfy
//     gear 2's own downshift condition and hunt 1↔2 forever. Clamped so the
//     kickdown upshift point can never fall below the downshift-back point.
// ═══════════════════════════════════════════════════════════════════════════

public float B4R_TCC_LOCK_KPH_G1   = 16f;
public float B4R_TCC_HYST_KPH      = 4f;
public float B4R_TCC_APPLY_TIME    = 0.55f;
public float B4R_TCC_SHIFT_RELEASE = 0.45f;
public bool  B4R_TCC_LOCK_IN_G1    = true;

public float B4R_TAP1_DUR   = 0.075f;
public float B4R_TAP_GAP    = 0.060f;
public float B4R_TAP2_DUR   = 0.115f;
public float B4R_TAP1_AMP   = 1.00f;
public float B4R_TAP2_AMP   = 0.74f;
public float B4R_TAP_DEPTH  = 0.85f;
public float B4R_TAP_PITCH  = 0.055f;

public float B4R_LOW_BASE_HZ  = 280f;
public float B4R_LOW_REF_RPM  = 1700f;
public float B4R_LOW_RPM_EXP  = 0.32f;
public float B4R_LOW_P2_RATIO = 1.22f;
public float B4R_LOW_P3_RATIO = 1.48f;
// [MORE HARMONIC] Was 0.34 — heavy broadband noise mixed with the tonal
// partials made this read as gritty/gear-grind rather than an actual
// pitched whine. Cut hard so the sine partials dominate; this is a WHINE,
// not a hiss.
public float B4R_LOW_NOISE    = 0.09f;

public float B4R_LOW_G1 = 1.10f, B4R_LOW_G2 = 1.00f, B4R_LOW_G3 = 0.88f;
public float B4R_LOW_G4 = 0.22f, B4R_LOW_G5 = 0.30f, B4R_LOW_G6 = 0.26f;

// ── Allison whine (NEW) — present across ALL gears, not gated by regime.
// Mid-high pitched, RPM-tracked, swells from a rounder "woooo" toward a
// brighter "AHHHH" the harder it revs (see B4xUpdateAllisonWhine), then
// past gear 5 the swell itself fades out and the tone settles into a
// steadier, airier "constant air through a pipe" character — less pitched
// modulation, more filtered-noise whoosh.
public float B4R_AW_BASE_HZ     = 780f;
public float B4R_AW_RPM_SCALE   = 0.62f;   // Hz added per rpm above idle
public float B4R_AW_SWELL_ATTACK = 0.55f;  // seconds — how slowly "wooo" opens into "AHHH"
public float B4R_AW_SWELL_RELEASE = 0.85f; // seconds — how slowly it falls back
public float B4R_AW_AIR_GEAR_START = 5f;   // gear where the airy-whoosh morph begins
public float B4R_AW_AIR_GEAR_FULL  = 6.3f; // gear-equivalent where morph is complete
public float B4R_AW_VOL             = 0.030f;
public float B4R_AW_AIR_NOISE_MIX   = 0.85f; // how much of the tone becomes filtered noise by full air blend

public float B4R_G2_LOCK_DROP  = 0f;   // TCC blend produces the drop now; kept as an extra fudge, off by default
public float B4R_CEIL_LOW      = 1880f;
public float B4R_CEIL_HIGH     = 1850f;
public float B4R_ISL9_MUL_LOW  = 1.26f;
public float B4R_ISL9_MUL_HIGH = 1.22f;
public bool  B4R_KEEP_LEGACY_TAP_THUD = true;

private int    b4r_prevGear       = 0;
private float  b4r_tapTimer       = 0f;
private bool   b4r_tapActive      = false;
private float  b4r_tapEnvSm       = 0f;
private float  b4r_awBrightSm     = 0f;   // slow-lagged "wooo -> AHHH" brightness envelope
private float  b4r_awAirBlend     = 0f;   // 0 = tonal howl, 1 = airy whoosh (gear-driven)
private double ph_b4r_aw1, ph_b4r_aw2, ph_b4r_awAir;
private float  b4r_tccBlend       = 0f;
private bool   b4r_tccWasApplied  = false;
private int    b4r_tccPrevGear    = 0;
private float  b4r_tccReleaseTmr  = 0f;

private void B4xUpdateTCC(float dt)
{
    // [FIX] Was releasing on EVERY gear change (gear != b4r_tccPrevGear),
    // which meant 2→3, 3→4, 4→5, 5→6, and every downshift among them ALSO
    // dropped the TCC open for 0.45s then took another 0.55s to relock —
    // a full second of uncapped openRPM after every single shift, with
    // shiftCD (0.55s) short enough that the next shift lands before the
    // previous one ever finishes relocking. RPM never settled: perpetual
    // flare, no decrease, sounded like repeated double-taps on every gear.
    // Once locked, the TCC should only release for transitions that
    // actually TOUCH gear 1 — establishing or losing lockup — which is the
    // only real unlock event. Locked-to-locked shifts (2↔3, 3↔4, etc.) keep
    // the converter engaged throughout, matching "available all ranges."
    if (gear != b4r_tccPrevGear)
    {
        bool touchesGear1 = gear <= 1 || b4r_tccPrevGear <= 1;
        if (touchesGear1) b4r_tccReleaseTmr = B4R_TCC_SHIFT_RELEASE;
        b4r_tccPrevGear = gear;
    }
    if (b4r_tccReleaseTmr > 0f) b4r_tccReleaseTmr -= dt;

    bool wantLock;
    if (gear <= 0)      wantLock = false;
    else if (gear == 1) wantLock = B4R_TCC_LOCK_IN_G1 && spd >= B4R_TCC_LOCK_KPH_G1;
    else                wantLock = true;

    if (gear == 1 && b4r_tccBlend > 0.5f && spd >= B4R_TCC_LOCK_KPH_G1 - B4R_TCC_HYST_KPH)
        wantLock = B4R_TCC_LOCK_IN_G1;

    if (b4r_tccReleaseTmr > 0f) wantLock = false;

    float rate = dt / Mathf.Max(0.01f, B4R_TCC_APPLY_TIME);
    b4r_tccBlend = Mathf.MoveTowards(b4r_tccBlend, wantLock ? 1f : 0f, rate);

    bool appliedNow = b4r_tccBlend > 0.5f;
    if (appliedNow && !b4r_tccWasApplied) b400_lockupThud = 1.0f;
    b4r_tccWasApplied = appliedNow;
}

private float B4xTapEnvelope(float t)
{
    if (t < B4R_TAP1_DUR)
        return Mathf.Sin(Mathf.Clamp01(t / Mathf.Max(0.001f, B4R_TAP1_DUR)) * Mathf.PI) * B4R_TAP1_AMP;

    float gapEnd = B4R_TAP1_DUR + B4R_TAP_GAP;
    if (t < gapEnd) return 0f;

    float d = t - gapEnd;
    if (d < B4R_TAP2_DUR)
        return Mathf.Sin(Mathf.Clamp01(d / Mathf.Max(0.001f, B4R_TAP2_DUR)) * Mathf.PI) * B4R_TAP2_AMP;

    return 0f;
}

private float B4xTapTotalDur => B4R_TAP1_DUR + B4R_TAP_GAP + B4R_TAP2_DUR;
private void B4xFireWhineTap() { b4r_tapActive = true; b4r_tapTimer = 0f; }

private void B4xOnG1toG2()
{
    if (B4R_KEEP_LEGACY_TAP_THUD && !b400_bigHowl && !al_dblTapActive)
    {
        al_dblTapActive = true;
        al_dblTapTimer  = 0f;
    }
    // [FIX -- crash/click bug] Envelope was jumping 0->1 instantly while
    // the thud's own oscillator phase kept running from wherever it
    // happened to be -- a genuine sample-to-sample discontinuity ("zipper
    // noise"), audible as a sharp percussive "tick" on every shift, and
    // with enough buses shifting in the same block it was hammering the
    // audio thread hard enough to choke performance. Zeroing the phase at
    // the same instant the envelope opens means the waveform itself starts
    // from 0 (sin(0)=0), so the envelope step lands on silence instead of
    // wherever the wave happened to be -- no discontinuity, no click.
    // [ADD -- real per-gear shape] B4xOnG1toG2 always fires ON the 1->2
    // transition (gear is already 2 here), so this is the ONE bark that
    // stays basically full-strength -- G1's real converter-unlock bark is
    // huge, and this taper table's G2 entry (0.62) still reads as a solid
    // event, just already off the true G1 peak.
    b400_shiftThud = 1.0f * AllisonGearTaper(gear);
    ph_b400_thud1 = 0.0; ph_b400_thud2 = 0.0;
    B4xFireWhineTap();
}

// ═══════════════════════════════════════════════════════════════════════════
//  [FIX — REAL ROOT CAUSE, not the TCC-release bug from before] GetAlUp(g)
//  converts a target RPM into a speed threshold using AL_R[g] where g is the
//  gear being shifted INTO. But the shift-out condition is checked as
//  `spd >= GetAlUp(gear + 1)` — meaning the speed threshold was computed off
//  the NEXT gear's ratio, not the CURRENT gear's. That's backwards for a
//  shift schedule: you leave a gear when THAT gear's own RPM hits the
//  trigger (standard shift-schedule design — see e.g. any TCU shift-table
//  reference: threshold is defined in the FROM gear, and the RPM you land
//  at afterward falls out of the ratio step, it isn't a separate target).
//
//  Traced numerically against your real AL_R/FINAL/TCIRC: with the old
//  formula, gear 1's ACTUAL rpm at its own computed 31.3 kph shift point was
//  3096 — nowhere near the intended 1650. Same for every gear (2177, 2397,
//  2267, 1962 rpm respectively). All of those blow past the old B4R_CEIL_LOW/
//  HIGH constants (1880/1850), so RPM would rocket up, slam into the ceiling,
//  and sit dead flat for most of the gear waiting for that inflated shift
//  speed to arrive. That's "no scaling, flat, then drop." And because the
//  target then stepped down hard while SmoothDamp's velocity was still
//  carrying upward from the flat hold, it overshot below target and bounced
//  back — that's the wobble / double-tap on every gear.
//
//  B4xUpshiftSpeed fixes the ratio (AL_R[fromGear], not AL_R[toGear]).
//  Checked every gear pair numerically: only 1→2 comes out unsafe against
//  its own downshift-back threshold (16.7 kph up vs 20.9 kph down-back), so
//  only that one gets the hysteresis clamp; 2→3 through 5→6 are naturally
//  safe once the ratio is fixed and need nothing extra.
//
//  Consequence: gear 2 and up no longer need a separate ceiling at all.
//  Once the trigger speed is correct, the gear's own RPM (inRPM) never
//  exceeds the shift point by construction — so RPM is now simply
//  proportional to road speed within a gear (exactly "scales with progress
//  of gear"), drops by the ratio step at each shift, and climbs again. Gear
//  1 keeps the open/locked TCC blend from before (that's a real converter
//  behavior, not a bug); gear 6 gets a soft ceiling since there's no next
//  gear to derive one from.
// ═══════════════════════════════════════════════════════════════════════════
// ═══════════════════════════════════════════════════════════════════════════
//  [FIX ROUND 2 — throttle-dependent shift schedule] The ratio-indexing fix
//  from before was real and stays, but B4xUpshiftSpeed was still pinning
//  every shift to a FLAT 1650/1700 rpm regardless of throttle. That's not a
//  bug in the ratio math — it's simply the wrong target. Once a gear is
//  locked (1:1, no converter slip), RPM at a given road speed is fixed by
//  the ratio; throttle can't make it flare independently. What throttle
//  actually changes on a real Allison is WHEN the next shift fires, not the
//  RPM ceiling within the current gear:
//    - Confirmed from operator reports: Economy Mode won't upshift/downshift
//      until RPM is down near peak torque; Performance/Power mode shifts
//      noticeably later, holding higher RPM before each upshift — "quite a
//      bit higher in power mode than economy."
//    - Real-world cruise/governed numbers land around 1800-2200 rpm
//      depending on engine, with governed speed for L9/ISL9 (the two
//      engines actually paired with B400R in this codebase — see GOV field,
//      both 2100) sitting right in that band.
//  A flat 1650/1700 shift point for every gear regardless of throttle is
//  the ECONOMY-mode number applied everywhere, all the time — hence "feels
//  like I'm in economy mode" even outside economyMode. Fixed by lerping the
//  shift-trigger RPM between a light-throttle base (unchanged, ~1650/1700 —
//  matches real light-throttle/cruise behavior) and a WOT ceiling near GOV,
//  using `accel` (already a clamped 0-1 throttle proxy) as the blend. This
//  makes RPM climb naturally further under hard acceleration BEFORE each
//  shift, simply because the shift itself is delayed — no artificial flare
//  needed, the physically-locked inRPM formula stays untouched.
// ═══════════════════════════════════════════════════════════════════════════
// ═══════════════════════════════════════════════════════════════════════════
//  Single source of truth for the throttle-dependent shift RPM target.
//  Both B4xUpshiftSpeed (converts to a speed threshold) and CalcAllisonRPM
//  (uses it directly as gear 1's / top gear's ceiling) call this SAME
//  function, so the ceiling and the shift trigger can never drift apart
//  again the way they did before — that mismatch was the entire root cause
//  of the flat-hold bug.
// ═══════════════════════════════════════════════════════════════════════════
// ═══════════════════════════════════════════════════════════════════════════
//  [REVISION] Kickdown was an instant step (kdUpExt = 0.62 snapped the gear-1
//  shift speed shorter the moment the key went down). Per direct request,
//  replaced with the SAME creep model H40EP/H50EP already use in
//  EvtContinuousRPM: held kickdown climbs to a higher RPM ceiling gradually
//  over ~9s, decays back over ~5s on release, never a step. Same timing
//  constants as EvtContinuousRPM's epv2_kdCreep, for consistency across the
//  whole engine family.
//
//  Because B4xShiftRPM feeds BOTH the shift trigger and gear 1's RPM
//  ceiling, boosting it via creep does two things at once, matching "adds
//  rpm gradually, higher limit": gear 1's ceiling audibly climbs the longer
//  kickdown is held, and every gear's upshift point recedes a little further
//  since the RPM it must reach is now higher — so gears run a bit longer and
//  rev a bit harder under sustained kickdown, without ever snapping.
//  Capped at GOV-30 regardless of creep, same safety-margin logic as the
//  normal WOT ceiling (GOV-100), just closer to redline.
// ═══════════════════════════════════════════════════════════════════════════
private float b4r_kdCreep = 0f;   // 0→1 slow creep while kickdown held — mirrors epv2_kdCreep

private void B4xUpdateKickdownCreep(float dt)
{
    b4r_kdCreep = kickdownKey
        ? Mathf.Min(1f, b4r_kdCreep + dt / 9f)     // ~9s to reach full creep
        : Mathf.Max(0f, b4r_kdCreep - dt / 5f);    // ~5s decay on release
}

private float B4xShiftRPM(int fromGear)
{
    int  toGear = fromGear + 1;
    // [GENERALIZED per instruction] ISL9's hard-rev character is now
    // also what ANY engine gets under performance-mode kickdown (WOT),
    // not an ISL9-only permanent trait anymore -- ISL9 still keeps it
    // unconditionally (that's its own real hardware character), everyone
    // else gets it only while actually mashing the pedal in performance
    // mode.
    bool isl9Character = engineType == EngineType.ISL9 || (kickdownKey && !economyMode);

    // [FIX] The convergence structure here (Lerp at throttleFrac=0
    // returning the shared low-throttle value) was already correct, but
    // the shared value itself was wrong -- 1650/1700 is a HIGH shift RPM,
    // not the low, early, smooth-shift baseline real Load-Based Shift
    // Scheduling actually uses at light throttle. That meant the bus was
    // shifting at nearly max RPM even at idle/light throttle, every time.
    float shRPM_low = 1100f; // shared low-throttle baseline, BOTH modes converge here
    if (isl9Character) shRPM_low *= 1.15f;

    float perfHigh = (toGear == 2 || toGear == 3) ? 1650f : 1700f;
    float ecoHigh  = 1350f; // economy climbs less than performance as throttle deepens
    if (isl9Character) { perfHigh *= 1.15f; ecoHigh *= 1.15f; }

    // [KICKDOWN OVERRIDE] Kickdown bypasses eco entirely, same as GetAlUp/GetAlDown.
    float shRPM_wot = (economyMode && !kickdownKey) ? ecoHigh : perfHigh;

    float throttleFrac = Mathf.Clamp01(accel);
    float shRPM = Mathf.Lerp(shRPM_low, shRPM_wot, throttleFrac);

    // Kickdown creep adds on top, gradually, capped just under redline.
    float kdCreepSpan = 70f;
    shRPM += b4r_kdCreep * kdCreepSpan;
    shRPM = Mathf.Min(shRPM, GOV - 30f);

    return shRPM;
}

private float B4xUpshiftSpeed(int fromGear)
{
    // [GENERALIZED per instruction] Same as CalcAllisonRPM/B4xShiftRPM --
    // ISL9 permanent, everyone else gets it under performance kickdown.
    bool  isl9Character = engineType == EngineType.ISL9 || (kickdownKey && !economyMode);
    float shRPM = B4xShiftRPM(fromGear);
    float raw   = (shRPM / AL_R[fromGear] / FINAL) * TCIRC / 60f * 3.6f;

    if (fromGear == 1)
        raw = Mathf.Max(raw, GetAlDown(2) + (isl9Character ? 3f : 6f));

    return raw;
}

private float CalcAllisonRPM()
{
    B4xUpdateTCC(Time.deltaTime);
    B4xUpdateKickdownCreep(Time.deltaTime);

    if (gear == 0) return IDLE;

    // [GENERALIZED per instruction] ISL9's hard-rev character is now
    // also what ANY engine gets under performance-mode kickdown (WOT),
    // not an ISL9-only permanent trait anymore -- ISL9 still keeps it
    // unconditionally (that's its own real hardware character), everyone
    // else gets it only while actually mashing the pedal in performance
    // mode.
    bool isl9Character = engineType == EngineType.ISL9 || (kickdownKey && !economyMode);

    float outRPM = (spd / 3.6f) / TCIRC * 60f;
    float inRPM  = outRPM * FINAL * AL_R[gear];

    al_g1CrackArmed = false;
    float targetRPM;

    if (gear == 1)
    {
        // Only gear 1 has a real open converter — see B4xUpdateTCC. RPM
        // blends between open (revving with throttle) and locked (pure
        // road-speed tracking) as the TCC applies partway through the gear.
        // Ceiling now comes from the SAME throttle-dependent B4xShiftRPM(1)
        // the shift trigger uses, so it climbs toward governor under WOT
        // instead of being pinned at the old flat light-throttle number.
        float slip      = economyMode ? 500f : (isl9Character ? 480f : 280f);
        float openRPM   = inRPM + (accel * slip + 40f);
        float lockedRPM = inRPM;
        targetRPM = Mathf.Lerp(openRPM, lockedRPM, b4r_tccBlend);
        targetRPM = Mathf.Min(targetRPM, B4xShiftRPM(1));

        // [NEW] ISL9 late-gear-1 rev spike — same mechanic as the B500R
        // side (CalcB500RRPM). Instead of flatlining at B4xShiftRPM(1) for
        // the back half of gear 1, RPM keeps climbing straight through —
        // no cut near the end — then genuinely revs hard right up toward
        // true governor before the shift, matching how ISL9 normally revs.
        if (isl9Character && !economyMode)
        {
            float g1Up = B4xUpshiftSpeed(1);
            float g1Progress = Mathf.Clamp01(spd / Mathf.Max(1f, g1Up));
            float lateG1Boost = Mathf.Clamp01((g1Progress - 0.65f) / 0.35f);
            lateG1Boost *= lateG1Boost;
            targetRPM *= (1f + lateG1Boost * 0.65f);
            targetRPM = Mathf.Min(targetRPM, GOV);
        }
    }
    else if (gear < 6)
    {
        // Locked from gear 2 up — RPM is just road speed through the current
        // gear's ratio. No separate ceiling: since the shift trigger and the
        // ceiling share the same B4xShiftRPM source, inRPM never exceeds it
        // by construction — the shift fires first.
        targetRPM = inRPM;
    }
    else
    {
        // Top gear — no next-gear trigger to derive a ceiling from, so use
        // B4xShiftRPM(6) directly (still throttle-dependent, still climbs
        // toward governor under load) purely as a safety cap near MAX_SPD.
        targetRPM = Mathf.Min(inRPM, B4xShiftRPM(6));
    }

    float rpmDampTime = isl9Character && !economyMode ? 0.08f : 0.12f;
    al_smoothedRPM = Mathf.SmoothDamp(al_smoothedRPM, targetRPM, ref al_rpmVelocity, rpmDampTime);

    return Mathf.Max(IDLE, ApplyAllisonDownshiftBlip(al_smoothedRPM, targetRPM));
}

private void DoAllisonGear()
{
    if (gear == 0)
    {
        gear = 1; shiftCD = 0.5f; alShiftTransient = 1f; alShiftTransientDur = 0.22f;
        // [ADD -- real per-gear shape] This is the launch into G1 -- real
        // Allison's biggest, longest bark, and previously had NO thud
        // trigger at all here (only the subsequent 1->2/2->3/etc shifts
        // fired one). Full taper (1.00, the loudest entry in the table).
        b400_shiftThud = 1.0f * AllisonGearTaper(1);
        ph_b400_thud1 = 0.0; ph_b400_thud2 = 0.0;
        if (spd >= B4xUpshiftSpeed(1))
        {
            gear = 2; shiftCD = 0.55f; alShiftTransient = 1f; alShiftTransientDur = 0.22f;
            al_g12BumpPhase = 0f;
            al_rpmVelocity = 0f;   // [FIX] kill residual SmoothDamp velocity across the
                                   // instantaneous RPM step, or it overshoots and wobbles.
            B4xOnG1toG2();
        }
        al_prevGearForTap = gear;
        return;
    }

    if (shiftCD > 0f) return;

    if (gear < 6 && spd >= B4xUpshiftSpeed(gear))
    {
        bool wasG1 = gear == 1;
        gear++;
        shiftCD = 0.55f; alShiftTransient = 1f; alShiftTransientDur = 0.22f;
        al_rpmVelocity = 0f;   // [FIX] see above — every shift steps RPM instantly now
        if (wasG1) B4xOnG1toG2();
        else       { b400_shiftThud = 1.0f * AllisonGearTaper(gear); ph_b400_thud1 = 0.0; ph_b400_thud2 = 0.0; } // [FIX] see B4xOnG1toG2 comment -- same click/discontinuity fix; [ADD] tapered by real per-gear shape
        al_prevGearForTap = gear;
        return;
    }

    if (gear > 1 && spd < GetAlDown(gear))
    {
        gear--; shiftCD = 0.35f; alShiftTransient = 1f; alShiftTransientDur = 0.22f;
        al_rpmVelocity = 0f;   // [FIX] same reset on downshift
    }
    al_prevGearForTap = gear;
}

private void DoB400RWhineDSP(ref double txSample, float rn, float ld,
                              float outRPM, float engMul,
                              bool retAct, double noiseClt, double invSR)
{
    /*
    b400w_runTimeSec += (float)invSR;
    float coldFactor = Mathf.Clamp01(1f - b400w_runTimeSec / 170f);

    float tapEnv = 0f;
    if (b4r_tapActive)
    {
        b4r_tapTimer += (float)invSR;
        tapEnv = B4xTapEnvelope(b4r_tapTimer);
        if (b4r_tapTimer > B4xTapTotalDur) { b4r_tapActive = false; b4r_tapTimer = 0f; }
    }
    b4r_tapEnvSm += (tapEnv - b4r_tapEnvSm) * 0.10f;

    if (b4r_prevGear == 1 && gear == 2 && !b4r_tapActive && b4r_tapEnvSm < 0.02f)
        B4xFireWhineTap();
    b4r_prevGear = gear;

    // ── Charging pump ──────────────────────────────────────────────────────
    float pumpHz = 88f + rpm * 0.135f;
    float speedAtten = 1f - Mathf.Clamp01((spd - 8f) / 65f);
    float pumpTarget = (0.020f + coldFactor * 0.018f) * Mathf.Lerp(0.32f, 1f, speedAtten);
    b400w_pumpVolSmooth += (pumpTarget - b400w_pumpVolSmooth) * (pumpTarget > b400w_pumpVolSmooth ? 0.004f : 0.002f);

    if (b400w_pumpVolSmooth > 0.003f)
    {
        double p1 = Math.Sin(2.0 * Math.PI * ph_b400w_pump1);
        double p2 = Math.Sin(2.0 * Math.PI * ph_b400w_pump2);
        txSample += (p1 * 1.0 + p2 * 0.44) * b400w_pumpVolSmooth * engMul
                  + noise_lp * b400w_pumpVolSmooth * 0.22 * engMul;
    }
    ph_b400w_pump1 = (ph_b400w_pump1 + (double)pumpHz         * invSR) % 1.0;
    ph_b400w_pump2 = (ph_b400w_pump2 + (double)pumpHz * 1.012 * invSR) % 1.0;

    // ── Whine 1: low/mech whine, gears 1-3 reduction, carries the tap ───────
    float rpmRatio = Mathf.Max(0.15f, rpm / Mathf.Max(1f, B4R_LOW_REF_RPM));
    float lowHzTgt = B4R_LOW_BASE_HZ * Mathf.Pow(rpmRatio, B4R_LOW_RPM_EXP);
    lowHzTgt      *= (1f + b4r_tapEnvSm * B4R_TAP_PITCH);
    b400w_accHzSmooth += (lowHzTgt - b400w_accHzSmooth) * (lowHzTgt > b400w_accHzSmooth ? 0.008f : 0.004f);
    if (b400w_accHzSmooth < 2f) b400w_accHzSmooth = 0f;

    float lowGain = gear <= 1 ? B4R_LOW_G1 : gear == 2 ? B4R_LOW_G2 : gear == 3 ? B4R_LOW_G3
                  : gear == 4 ? B4R_LOW_G4 : gear == 5 ? B4R_LOW_G5 : B4R_LOW_G6;
    float lowTgt = Mathf.Clamp01(ld * 1.25f) * lowGain;
    b400w_accWhineSmooth += (lowTgt - b400w_accWhineSmooth) * (lowTgt > b400w_accWhineSmooth ? 0.006f : 0.0032f);

    if (b400w_accWhineSmooth > 0.004f && b400w_accHzSmooth > 5f)
    {
        double a1 = Math.Sin(2.0 * Math.PI * ph_b400w_accWh1);
        double a2 = Math.Sin(2.0 * Math.PI * ph_b400w_accWhSub);
        double a3 = Math.Sin(2.0 * Math.PI * ph_b400w_accWhThird);
        double sing = a1 * 1.0 + a2 * 0.42 + a3 * 0.24;

        float tapLift = 1f + b4r_tapEnvSm * B4R_TAP_DEPTH;
        float lowVol  = (0.016f + rn * 0.024f) * b400w_accWhineSmooth * engMul * tapLift;

        // [MORE HARMONIC] Sine mix raised 0.48->0.72 to match B4R_LOW_NOISE
        // cut above — this is a whine, the tonal partials should dominate.
        txSample += sing * lowVol * 0.72 + noise_lp * lowVol * B4R_LOW_NOISE;
    }
    ph_b400w_accWh1     = (ph_b400w_accWh1     + b400w_accHzSmooth                    * invSR) % 1.0;
    ph_b400w_accWhSub   = (ph_b400w_accWhSub   + b400w_accHzSmooth * B4R_LOW_P2_RATIO * invSR) % 1.0;
    ph_b400w_accWhThird = (ph_b400w_accWhThird + b400w_accHzSmooth * B4R_LOW_P3_RATIO * invSR) % 1.0;
*/
    // ── Whine 2: ALLISON WHINE — present across ALL gears, mid-high pitched,
    //    tracks RPM. Two-partial "vowel" pair whose relative brightness
    //    swells slowly with revs (the "wooo -> AHHH" character) rather than
    //    tracking RPM instantly — that lag IS the swell. Past gear 5 the
    //    swell fades and the tone morphs into a steadier airy whoosh
    //    (filtered noise) instead, like air blowing through a pipe.
    //
    //    [GATE] ISL9/L9 + B400R get D864.5's ORIGINAL whine instead (ported
    //    verbatim, pre-deepen-rework, per direct instruction) -- every other
    //    engine keeps the AW swell/whoosh voice above unchanged.
    // ═════════════════════════════════════════════════════════════════════
    bool useDiwaWhineForB400 = (engineType == EngineType.ISL9 || engineType == EngineType.L9);
    if (useDiwaWhineForB400)
    {
        // ── Ported verbatim from D864.5's pre-rework whine (DoD8645DSP) ────
        float currentTeeth = gear <= 1 ? 23.0f : 21.5f;
        const float OCTAVE_RATIO = 2.0002f;
        const float FOURTH_RATIO = 1.3333f;
        const float PITCH_TAU_UP   = 0.016f;
        const float PITCH_TAU_DOWN = 0.012f;

        float meshTarget = (rpm / 60f) * currentTeeth;
        float loadStrain = ld * 0.030f * Mathf.Clamp01(1f - rn);
        float meshMod = meshTarget * (1f - loadStrain);

        float pumpWobble = 0f, pumpGrit = 0f;
        if (kickdownKey && pumpEnergy > 0.01f)
        {
            float wobbleHz = 3.0f + pumpEnergy * 5f;
            pumpWobble = Mathf.Sin((float)audioClock * Mathf.PI * 2f * wobbleHz) * pumpEnergy * 0.06f;
            pumpGrit   = pumpEnergy;
        }
        meshMod *= (1f + pumpWobble);
        float ptau = meshMod > b400_dwWhineHzSmooth ? PITCH_TAU_UP : PITCH_TAU_DOWN;
        if (kickdownKey && pumpEnergy > 0.01f) ptau *= (1f + pumpEnergy * 2.4f);

        b400_dwWhineHzSmooth += (meshMod - b400_dwWhineHzSmooth) * ptau;
        if (b400_dwWhineHzSmooth < 2f) b400_dwWhineHzSmooth = 0f;

        double fW1 = b400_dwWhineHzSmooth;
        double fW2 = b400_dwWhineHzSmooth * OCTAVE_RATIO;
        double fW3 = b400_dwWhineHzSmooth * FOURTH_RATIO;

        float gearMod;
        if (gear <= 1) gearMod = 0.2f + ld * 1.00f;
        else           gearMod = 0.1f;

        float whineVol = (0.072f + rn * 0.046f + ld * 0.034f) * gearMod * engMul * (1f + pumpGrit * 0.34f);

        if (b400_dwWhineHzSmooth > 5f)
        {
            double w1 = Math.Sin(2.0 * Math.PI * ph_b400_dw1);
            double w2 = -Math.Sin(2.0 * Math.PI * ph_b400_dw2);
            double w3 = Math.Sin(2.0 * Math.PI * ph_b400_dw3);
            double whineChord = (w1 * 1.0) + (w3 * 0.42) - (w2 * 0.20);
            txSample += whineChord * whineVol + noise_lp * (whineVol * 0.12);

            if (pumpGrit > 0.01f)
            {
                double gritDrive = Math.Tanh(w1 * (2.2 + pumpGrit * 3.0));
                txSample += gritDrive * whineVol * pumpGrit * 0.42;
            }
        }
        ph_b400_dw1 = (ph_b400_dw1 + fW1 * invSR) % 1.0;
        ph_b400_dw2 = (ph_b400_dw2 + fW2 * invSR) % 1.0;
        ph_b400_dw3 = (ph_b400_dw3 + fW3 * invSR) % 1.0;
    }
    else
    {
        float awHz = B4R_AW_BASE_HZ + Mathf.Max(0f, rpm - IDLE) * (B4R_AW_RPM_SCALE * 0.5f);

        // Brightness target: how hard the engine is pulling, 0..1.
        float brightTgt = Mathf.Clamp01((rpm - IDLE) / Mathf.Max(1f, (GOV - IDLE)));
        float rate = brightTgt > b4r_awBrightSm
            ? (float)invSR / Mathf.Max(0.05f, B4R_AW_SWELL_ATTACK)
            : (float)invSR / Mathf.Max(0.05f, B4R_AW_SWELL_RELEASE);
        b4r_awBrightSm = Mathf.MoveTowards(b4r_awBrightSm, brightTgt, rate);

        // Air blend: 0 through gear 4, ramps across gear 5-6.
        float airTgt = Mathf.Clamp01((gear - B4R_AW_AIR_GEAR_START) / Mathf.Max(0.01f, B4R_AW_AIR_GEAR_FULL - B4R_AW_AIR_GEAR_START));
        b4r_awAirBlend += (airTgt - b4r_awAirBlend) * 0.01f;

        // As it goes airy, the swell itself relaxes — a steady whoosh
        // doesn't "wooo-AHHH," it just sits there. Also softens the pitch
        // climb so the airy tail reads as more constant, less RPM-chasing.
        float swellDepth = 1f - b4r_awAirBlend * 0.75f;
        float awHzFinal  = awHz * (1f - b4r_awAirBlend * 0.20f);

        float vowelRatio = Mathf.Lerp(1.28f, 1.11f, b4r_awBrightSm * swellDepth); // closes toward unison as it brightens = "opens up"
        float awVol = B4R_AW_VOL * Mathf.Clamp01(0.25f + ld * 0.9f);

        double t1 = Math.Sin(2.0 * Math.PI * ph_b4r_aw1);
        double t2 = Math.Sin(2.0 * Math.PI * ph_b4r_aw2);
        double tone = t1 * 1.0 + t2 * (0.35 + 0.5 * b4r_awBrightSm * swellDepth);

        double airNoise = noise_hi * 0.6 + noise_lp * 0.4;
        double airMix = tone * (1.0 - b4r_awAirBlend * B4R_AW_AIR_NOISE_MIX)
                       + airNoise * (b4r_awAirBlend * B4R_AW_AIR_NOISE_MIX);

        txSample += airMix * awVol * engMul;

        ph_b4r_aw1   = (ph_b4r_aw1   + awHzFinal               * invSR) % 1.0;
        ph_b4r_aw2   = (ph_b4r_aw2   + awHzFinal * vowelRatio  * invSR) % 1.0;
    }

    // ── TC slip whoosh — gated on converter blend, now reachable ────────────
    float openFrac = 1f - b4r_tccBlend;
    float slipLoad = openFrac * (gear <= 1 ? ld : ld * 0.45f);
    b400_tcNoiseSmooth += (slipLoad - b400_tcNoiseSmooth) * 0.005f;
    if (b400_tcNoiseSmooth > 0.003f && spd > 0.5f)
    {
        float tcVol = (0.048f + b400_tcNoiseSmooth * 0.095f) * engMul;
        txSample += noiseClt * tcVol;
        double tcOutHz = outRPM * 0.065 + 10.0;
        txSample += Math.Sin(2.0 * Math.PI * ph_b400_tc1) * (tcVol * 0.40);
        txSample += Math.Sin(2.0 * Math.PI * ph_b400_tc2) * (tcVol * 0.20);
        ph_b400_tc1 = (ph_b400_tc1 + tcOutHz       * invSR) % 1.0;
        ph_b400_tc2 = (ph_b400_tc2 + tcOutHz * 2.0 * invSR) % 1.0;
    }
    else
    {
        b400_tcNoiseSmooth = Mathf.Max(0f, b400_tcNoiseSmooth - (float)invSR * 3f);
        double tcIdleHz = 10.0 + outRPM * 0.065;
        ph_b400_tc1 = (ph_b400_tc1 + tcIdleHz       * invSR) % 1.0;
        ph_b400_tc2 = (ph_b400_tc2 + tcIdleHz * 2.0 * invSR) % 1.0;
    }

    // ── Howl (unchanged) ─────────────────────────────────────────────────────
    if (!b400_bigHowlRolled) { b400_bigHowlRolled = true; b400_bigHowl = (NextNoiseSample() * 0.5 + 0.5) < 0.30; }

    if (gear >= 7 && spd > 99f)
    {
        double b400OutHz    = b400_bigHowl ? (128.0 + outRPM * 0.48) : (190.0 + outRPM * 0.62);
        double b400HowlTrem = b400_bigHowl
            ? (0.65 + 0.35 * Math.Sin(2.0 * Math.PI * ph_b400_howlTrem))
            : (0.72 + 0.28 * Math.Sin(2.0 * Math.PI * ph_b400_howlTrem));
        bool highAccel = ld > 0.8f;
        float b400HowlDecayTarget = highAccel ? 0.5f : 1.0f;
        float b400DecayRate = b400_bigHowl ? (highAccel ? 0.12f : 0.35f) : (highAccel ? 0.18f : 0.5f);
        b400HowlAccelDecay += (b400HowlDecayTarget - b400HowlAccelDecay) * (float)invSR * b400DecayRate;
        float b400OutVol = b400_bigHowl
            ? (0.070f + (spd / MAX_SPD) * 0.074f + ld * 0.024f) * engMul * b400HowlAccelDecay
            : (0.043f + (spd / MAX_SPD) * 0.046f + ld * 0.016f) * engMul * b400HowlAccelDecay;
        double b400HowlTone = b400_bigHowl
            ? Math.Sin(2.0 * Math.PI * ph_b400_out) * 1.0 + Math.Sin(2.0 * Math.PI * ph_b400_out * 0.5) * 0.72
            : Math.Sin(2.0 * Math.PI * ph_b400_out) * 1.0 + Math.Sin(2.0 * Math.PI * ph_b400_out * 0.5) * 0.42;
        txSample += b400HowlTone * b400OutVol * b400HowlTrem;
        ph_b400_out      = (ph_b400_out      + b400OutHz * invSR) % 1.0;
        ph_b400_howlTrem = (ph_b400_howlTrem + (b400_bigHowl ? 1.7 : 2.9) * invSR) % 1.0;
    }
    else
    {
        ph_b400_out      = (ph_b400_out      + (b400_bigHowl ? (128.0 + outRPM * 0.48) : (190.0 + outRPM * 0.62)) * invSR) % 1.0;
        ph_b400_howlTrem = (ph_b400_howlTrem + (b400_bigHowl ? 1.7 : 2.9) * invSR) % 1.0;
    }

    // ── Shift / lockup thud. Lockup thud now fired by B4xUpdateTCC on the ──
    //    actual converter apply edge, not the gear change.
    if (b400_shiftThud > 0f)
    {
        txSample += (Math.Sin(2.0 * Math.PI * ph_b400_thud1) * 0.26
                   + Math.Sin(2.0 * Math.PI * ph_b400_thud2) * 0.14) * b400_shiftThud * engMul;
        b400_shiftThud *= 0.9960f;
        if (b400_shiftThud < 0.005f) b400_shiftThud = 0f;
    }
    if (b400_lockupThud > 0f)
    {
        txSample += noiseClt * b400_lockupThud * 0.18 * engMul;
        // [FIX -- click bug] ph_b400w_accWh1 is a shared whine phase (keeps
        // running elsewhere), so it can't be zeroed the way the dedicated
        // shift-thud phases were -- instead give the envelope itself a fast
        // (~4ms) attack instead of an instant 0->1 step, so it never lands
        // on a jump mid-waveform.
        b400_lockupThudSm += (b400_lockupThud - b400_lockupThudSm) * Mathf.Min(1f, (float)invSR / 0.004f);
        txSample += Math.Sin(2.0 * Math.PI * ph_b400w_accWh1) * b400_lockupThudSm * 0.13 * engMul;
        b400_lockupThud *= 0.9950f;
        if (b400_lockupThud < 0.004f) b400_lockupThud = 0f;
    }
    ph_b400_thud1 = (ph_b400_thud1 + 44.0 * invSR) % 1.0;
    ph_b400_thud2 = (ph_b400_thud2 + 74.0 * invSR) % 1.0;

    if (B4R_KEEP_LEGACY_TAP_THUD && !b400_bigHowl)
        DoAllisonDoubleTap(ref txSample, engMul, invSR);

    DoAllisonRetarderSpit(ref txSample, ref b400_retardZoneActive,
                          ref b400_tickVol, ref b400_spitVol,
                          ref ph_b400_spitTick, ref ph_b400_spitHiss, engMul, invSR);
}

// ═════════════════════════════════════════════════════════════════════════
//  B400R GEN 5 — genuinely dedicated DSP, fully self-contained (no
//  dependency on Gen 4's b4r_tccBlend/B4xUpdateTCC external state machine).
//  Same real-research design language as DoB500RGen5DSP: cleaner tone,
//  faster response, quieter overall, 5-level stepped VAC throttle response
//  instead of a continuous curve.
// ═════════════════════════════════════════════════════════════════════════
private void DoB400RGen5DSP(ref double txSample, float rn, float ld,
                             float outRPM, float engMul,
                             bool retAct, double noiseClt, double invSR)
{
    // ── Allison whine — same vowel-pair swell concept as Gen 4, but
    // faster attack/release (quicker TCM) and a genuinely stepped
    // brightness response (5 discrete VAC levels) instead of a continuous
    // ramp -- the real, audible "two additional acceleration levels" trait.
    {
        float awHz = B4R_AW_BASE_HZ + Mathf.Max(0f, rpm - IDLE) * (B4R_AW_RPM_SCALE * 0.5f);

        float brightRaw = Mathf.Clamp01((rpm - IDLE) / Mathf.Max(1f, (GOV - IDLE)));
        // [NEW] 5-level VAC stepping.
        float brightTgt = Mathf.Floor(brightRaw * 5f) / 5f;
        float rate = brightTgt > b4g5_awBrightSm
            ? (float)invSR / Mathf.Max(0.05f, B4R_AW_SWELL_ATTACK * 0.65f)   // faster than Gen 4
            : (float)invSR / Mathf.Max(0.05f, B4R_AW_SWELL_RELEASE * 0.65f);
        b4g5_awBrightSm = Mathf.MoveTowards(b4g5_awBrightSm, brightTgt, rate);

        // Self-contained air blend, gear-driven only (no external TCC
        // dependency) -- same shape as Gen4's gear-based ramp.
        float airTgt = Mathf.Clamp01((gear - B4R_AW_AIR_GEAR_START) / Mathf.Max(0.01f, B4R_AW_AIR_GEAR_FULL - B4R_AW_AIR_GEAR_START));
        b4g5_awAirBlend += (airTgt - b4g5_awAirBlend) * 0.014f; // faster than Gen4's 0.01f

        float swellDepth = 1f - b4g5_awAirBlend * 0.75f;
        float awHzFinal  = awHz * (1f - b4g5_awAirBlend * 0.20f);

        float vowelRatio = Mathf.Lerp(1.20f, 1.08f, b4g5_awBrightSm * swellDepth); // tighter/cleaner interval than Gen4's 1.28/1.11
        // [EDIT per instruction] Louder + more howl-like -- added a
        // sub-octave partial (t3, half-frequency) for the sustained,
        // resonant low-end depth that reads as a "howl" rather than a
        // thin whine, and raised overall volume above even Gen4's (was
        // 0.72x QUIETER than Gen4; now louder than Gen4 outright, per
        // instruction). Still smooth -- the extra partial is a pure sine,
        // no added grit/noise, and the vowelRatio/swell shaping above is
        // untouched, so it stays clean, just fuller and more present.
        float awVol = B4R_AW_VOL * 1.15f * Mathf.Clamp01(0.25f + ld * 0.9f);

        double t1 = Math.Sin(2.0 * Math.PI * ph_b4g5_aw1);
        double t2 = Math.Sin(2.0 * Math.PI * ph_b4g5_aw2);
        double t3 = Math.Sin(2.0 * Math.PI * ph_b4g5_aw3); // sub-octave -- the "howl" body
        double tone = t1 * 1.0 + t2 * (0.28 + 0.40 * b4g5_awBrightSm * swellDepth) + t3 * 0.38;

        double airNoise = noise_hi * 0.6 + noise_lp * 0.4;
        double airMix = tone * (1.0 - b4g5_awAirBlend * B4R_AW_AIR_NOISE_MIX)
                       + airNoise * (b4g5_awAirBlend * B4R_AW_AIR_NOISE_MIX);

        txSample += airMix * awVol * engMul;

        ph_b4g5_aw1 = (ph_b4g5_aw1 + awHzFinal              * invSR) % 1.0;
        ph_b4g5_aw2 = (ph_b4g5_aw2 + awHzFinal * vowelRatio * invSR) % 1.0;
        ph_b4g5_aw3 = (ph_b4g5_aw3 + awHzFinal * 0.5        * invSR) % 1.0;
    }

    // ── TC slip whoosh — tighter lockup than Gen 4, quieter, self-contained
    // (no b4r_tccBlend dependency -- uses gear/ld directly).
    float slipLoad = gear <= 1 ? ld * 0.75f : ld * 0.30f;
    b4g5_tcNoiseSmooth += (slipLoad - b4g5_tcNoiseSmooth) * 0.007f; // faster than Gen4's 0.005
    if (b4g5_tcNoiseSmooth > 0.003f && spd > 0.5f)
    {
        float tcVol = (0.036f + b4g5_tcNoiseSmooth * 0.075f) * engMul; // was 0.048/0.095
        txSample += noiseClt * tcVol;
        double tcOutHz = outRPM * 0.065 + 10.0;
        txSample += Math.Sin(2.0 * Math.PI * ph_b4g5_tc1) * (tcVol * 0.40);
        ph_b4g5_tc1 = (ph_b4g5_tc1 + tcOutHz       * invSR) % 1.0;
        ph_b4g5_tc2 = (ph_b4g5_tc2 + tcOutHz * 2.0 * invSR) % 1.0;
    }
    else
    {
        b4g5_tcNoiseSmooth = Mathf.Max(0f, b4g5_tcNoiseSmooth - (float)invSR * 4f);
        double tcIdleHz = 10.0 + outRPM * 0.065;
        ph_b4g5_tc1 = (ph_b4g5_tc1 + tcIdleHz       * invSR) % 1.0;
        ph_b4g5_tc2 = (ph_b4g5_tc2 + tcIdleHz * 2.0 * invSR) % 1.0;
    }

    // ── Howl — single clean variant (no random "big howl" roll -- Gen 5's
    // more consistent, refined character doesn't need per-bus randomness
    // here), quieter/tighter decay.
    if (gear >= 7 && spd > 99f)
    {
        double outHz    = 128.0 + outRPM * 0.48;
        double howlTrem = 0.65 + 0.35 * Math.Sin(2.0 * Math.PI * ph_b4g5_howlTrem);
        bool highAccel = ld > 0.8f;
        float decayTarget = highAccel ? 0.5f : 1.0f;
        b4g5_howlAccelDecay += (decayTarget - b4g5_howlAccelDecay) * (float)invSR * (highAccel ? 0.16f : 0.45f); // faster than Gen4
        float outVol = (0.052f + (spd / MAX_SPD) * 0.055f + ld * 0.018f) * engMul * b4g5_howlAccelDecay; // quieter than Gen4
        double howlTone = Math.Sin(2.0 * Math.PI * ph_b4g5_out) * 1.0 + Math.Sin(2.0 * Math.PI * ph_b4g5_out * 0.5) * 0.55;
        txSample += howlTone * outVol * howlTrem;
        ph_b4g5_out      = (ph_b4g5_out      + outHz * invSR) % 1.0;
        ph_b4g5_howlTrem = (ph_b4g5_howlTrem + 1.7    * invSR) % 1.0;
    }
    else
    {
        ph_b4g5_out      = (ph_b4g5_out      + (128.0 + outRPM * 0.48) * invSR) % 1.0;
        ph_b4g5_howlTrem = (ph_b4g5_howlTrem + 1.7             * invSR) % 1.0;
    }

    // ── Shift / lockup thud — snappier decay (quicker/more precise TCM),
    // softer peak (smoother) than Gen 4.
    if (b4g5_shiftThud > 0f)
    {
        txSample += (Math.Sin(2.0 * Math.PI * ph_b4g5_thud1) * 0.19 + Math.Sin(2.0 * Math.PI * ph_b4g5_thud2) * 0.10) * b4g5_shiftThud * engMul; // was 0.26/0.14
        b4g5_shiftThud *= 0.9935f; // faster decay than Gen4's 0.9960f
        if (b4g5_shiftThud < 0.005f) b4g5_shiftThud = 0f;
    }
    if (b4g5_lockupThud > 0f)
    {
        txSample += noiseClt * b4g5_lockupThud * 0.13 * engMul; // was 0.18
        // [FIX -- click bug] ph_b4g5_aw1 is a shared whine phase, same fix
        // as B400R's b400_lockupThud -- fast attack instead of instant step.
        b4g5_lockupThudSm += (b4g5_lockupThud - b4g5_lockupThudSm) * Mathf.Min(1f, (float)invSR / 0.004f);
        txSample += Math.Sin(2.0 * Math.PI * ph_b4g5_aw1) * b4g5_lockupThudSm * 0.09 * engMul; // was 0.13
        b4g5_lockupThud *= 0.9925f; // was 0.9950f
        if (b4g5_lockupThud < 0.004f) b4g5_lockupThud = 0f;
    }
    ph_b4g5_thud1 = (ph_b4g5_thud1 + 44.0 * invSR) % 1.0;
    ph_b4g5_thud2 = (ph_b4g5_thud2 + 74.0 * invSR) % 1.0;

    // No legacy tap-thud on Gen 5 -- that's specifically a Gen4-era
    // artifact the real 5th Gen controls refresh moved past.

    DoAllisonRetarderSpit(ref txSample, ref b4g5_retardZoneActive,
                          ref b4g5_tickVol, ref b4g5_spitVol,
                          ref ph_b4g5_spitTick, ref ph_b4g5_spitHiss, engMul, invSR);
}

private bool  al_downshiftBlipActive = false;
private float al_downshiftBlipTimer = 0f;
private int   al_prevGearForBlip = 0;
 
 private float ApplyAllisonDownshiftBlip(float r, float ceiling)
{
    if (gear < al_prevGearForBlip && gear > 0 && al_prevGearForBlip > 0)
    {
        al_downshiftBlipActive = true;
        al_downshiftBlipTimer  = 0f;
    }
    al_prevGearForBlip = gear;
 
    if (!al_downshiftBlipActive) return r;
 
    al_downshiftBlipTimer += Time.deltaTime;
 
    /* Quick rise (0.00-0.18s) up to a blip peak above the current target,
       then one smooth, unbroken fall back to the real target (0.18-1.9s).
       [FIXED] This used to have a 3.0Hz->0.7Hz sine "wobble" riding on top
       of the fall, deliberately built to produce multiple swells on the
       way down ("woooWOOOOOOOoooowooooWOOOOOOOoooowoooo"). That's what
       was actually causing the chattery "wwwwwwwwwwwrrrrrrrrrwwrrrrrrrrrr"
       -- an oscillation starting at 3 cycles/sec is fast enough to read as
       a warble/motorboat, not a swell. A real torque-converter downshift
       flare is ONE rise and ONE fall, no interior ripples -- SmoothStep
       both halves and nothing else. */
    float blipAmt;
    if (al_downshiftBlipTimer < 0.18f)
    {
        blipAmt = Mathf.SmoothStep(0f, 1f, al_downshiftBlipTimer / 0.18f);
    }
    else if (al_downshiftBlipTimer < 1.9f)
    {
        float fallT = (al_downshiftBlipTimer - 0.18f) / 1.72f;
        blipAmt = Mathf.SmoothStep(1f, 0f, fallT);
    }
    else
    {
        al_downshiftBlipActive = false;
        blipAmt = 0f;
    }
 
    float blipPeak = Mathf.Min(ceiling, r + 260f); // "whooi" rise above wherever it currently sits
    return Mathf.Lerp(r, blipPeak, blipAmt);
}
// ── L9: launch rev-hunt — small RPM blip/settle right as the engine
// first takes load from a dead stop, before lugging up to speed. ──────────
private bool  l9_prevLaunching   = false;
private float l9_launchHuntTimer = 0f;
private bool  l9_launchHuntActive = false;
    // ═════════════════════════════════════════════════════════════════════════
    //  GEAR HELPERS — Allison B500R
    // ═════════════════════════════════════════════════════════════════════════
    private float CalcB500RRPM()
    {
        if (gear == 0) return IDLE;
        // [GENERALIZED per instruction] Same as B400R Gen4: ISL9 keeps its
        // permanent hard-rev character, every other engine now gets the
        // same treatment under performance-mode kickdown (WOT) instead of
        // only L9/L9N getting it — matches what ISL9 already does for
        // B400R Gen4, extended to B500R.
        bool isl9Character = engineType == EngineType.ISL9 || (kickdownKey && !economyMode);
        int g = Mathf.Min(gear, AL_R_B500.Length - 1);
        float outShaft = (spd / 3.6f) / TCIRC * 60f;
        float inRPM    = outShaft * FINAL * AL_R_B500[g];
        // [REDONE — B400R-base unification] Was a hard instant on/off
        // switch keyed off AL_LOCK_B500[g] every frame -- meant the moment
        // the converter actually locks or releases mid-gear (not just at
        // a gear change) had zero transition. Now uses the same blend
        // helper B400R's gear 1 uses, gated on B500R's own lock table so
        // the ONE real lock boundary this family has (start of gear 4)
        // gets a proper flare-then-settle instead of a clunk.
        UpdateAllisonLockBlend(AL_LOCK_B500[g], B4R_TCC_APPLY_TIME, B4R_TCC_SHIFT_RELEASE, Time.deltaTime);
        float openFrac = 1f - b4r_tccBlend;
        float slipBase = (gear == 1 ? accel * 420f + 100f : accel * 130f + 35f) * openFrac;
        // [PUSHED FURTHER] 1.45 -> 1.55 -- revs harder still.
        float slip = isl9Character ? slipBase * 1.55f : slipBase;
        float r = inRPM + slip;
        // [REBUILT per real research] The flat economy-mode RPM ceiling is
        // gone -- real Allison economy mode doesn't cap RPM at all, it
        // resists downshifting under heavy throttle specifically (see
        // DoB500RGear's downshift thresholds below for where that logic
        // actually lives now). RPM ceiling itself is the SAME regardless
        // of economyMode.
        if (isl9Character) r = Mathf.Min(r, gear == 1 ? 2480f : 2050f);
        else                r = Mathf.Min(r, gear == 1 ? 1950f : 1850f);

        // [NEW] Late-gear-1 rev spike — same mechanic as CalcAllisonRPM's
        // B400R side: a lot more RPM specifically near the END of gear 1,
        // ramping in hard over the last 35% of gear 1's speed range instead
        // of a flat elevated ceiling the whole time. Feeds into the
        // function's existing final Mathf.Clamp(r, IDLE, GOV) below, so a
        // strong late-G1 spike can genuinely pin the governor right before
        // the shift rather than always sitting under it.
        r *= 1f + LateGearRampBoost(gear, isl9Character ? (B500R_G2_DN + 3f) : B500R_G1_UP,
                                     B500R_G2_UP, B500R_G3_UP, B500R_G4_UP, B500R_G5_UP,
                                     1.0f, isl9Character ? 0.65f : 0.42f);
        float targetRPM = Mathf.Clamp(r, IDLE, GOV);

        // [REDONE — Allison-smooth] This used to return the raw computed
        // target every frame with zero filtering -- every gear-ratio step,
        // slip fluctuation, and lock-blend tick landed on the output
        // instantly, which is exactly why this read as "jumpy" next to
        // CalcAllisonRPM (which has always run through SmoothDamp). Same
        // damp-time shape as Allison: tighter under hard-rev kickdown/ISL9
        // character, looser otherwise. al_rpmVelocity/al_smoothedRPM are
        // reset to 0 on every real gear-step in DoB500RGear so a shift
        // still reads as an instant step, not a smeared slide -- this only
        // removes the WITHIN-gear jitter.
        float rpmDampTime = isl9Character && !economyMode ? 0.08f : 0.12f;
        al_smoothedRPM = Mathf.SmoothDamp(al_smoothedRPM, targetRPM, ref al_rpmVelocity, rpmDampTime);
        return Mathf.Max(IDLE, ApplyAllisonDownshiftBlip(al_smoothedRPM, targetRPM));
    }
// ── Voith deep-engine swell layer (gear-1-end build + retarder reuse) ──
    private float  voith_deepG1Timer     = 0f;
    private bool   voith_deepG1WasActive = false;
    private float  voith_deepRetTimer    = 0f;
    private float  voith_deepRetVol      = 0f;
    private bool   voith_deepRetWasActive= false;
    private float  voith_deepHzSmooth    = 0f;
    private double ph_v_deep1 = 0.0, ph_v_deep2 = 0.0;
    private const float VOITH_DEEP_HZ_BASE = 78f; // lowered per the "1 sec in" pitch you found
    private void DoB500RGear()
    {
        // [FIX per instruction] kdExt (the old gear-1 kickdown speed
        // shrink) removed entirely -- kickdown no longer touches the gear
        // ladder anywhere in this function; it only boosts RPM gain via
        // CalcB500RRPM's isl9Character slip multiplier now.
        // [REDONE] Real Allison Eco/FuelSense behavior only shows up at
        // WOT: it upshifts EARLIER (lower RPM/speed) under heavy throttle,
        // and separately resists downshifting under heavy throttle -- at
        // light/no throttle eco and performance are identical. Previously
        // only the downshift half of this existed (ecoResist below); the
        // upshift side was missing entirely, so eco mode never actually
        // shifted differently going up, only coming back down -- the
        // asymmetry that made it read as "weird." Same continuous
        // throttle-scaled convergence shape as ecoResist, mirrored.
        // [KICKDOWN OVERRIDE] Kickdown forces full hard performance, same
        // as B400R's old behavior -- bypasses eco entirely rather than just
        // pushing the accel term toward 1.
        float ecoUpTighten = (economyMode && !kickdownKey) ? Mathf.Lerp(1.0f, 0.85f, Mathf.Clamp01(accel)) : 1.0f;
        // ISL9 character: gear 1 short, same spec as the B400R side
        // (GetAlUp). ISL9-only -- L9/L9N do NOT get gear 1 shortened, even
        // under kickdown (they still get the rev/ceiling boost and hollow
        // roar elsewhere, just not this).
        // [FIX — real hysteresis bug, same as GetAlUp(1)'s fix] 0.48
        // multiplied against B500R_G1_UP (20f) gives 9.6 km/h, but
        // B500R_G2_DN (gear 2's downshift-back-to-1 threshold) is 16 km/h
        // -- 9.6 < 16 meant a dead zone where gear 2 still satisfied its
        // own downshift-back condition right after upshifting, causing the
        // same "keeps switching gear 1/gear 2" hunting bug B400R had.
        // Computed relative to B500R_G2_DN with a safe margin now, same
        // fix shape as GetAlUp(1).
        float isl9G1Speed = B500R_G2_DN + 3f;
        // [ADD -- real per-gear shape] Launch into G1 previously had no
        // thud trigger at all, same gap as B400R had -- real B500R's G1
        // bark is the biggest/longest of any variant (most fluid volume to
        // spin up). Full taper.
        if (gear == 0) { gear = 1; shiftCD = 0.5f; alShiftTransient = 1f; alShiftTransientDur = 0.22f; b500_shiftThud = 1.0f * AllisonGearTaper(1); ph_b5_thud1 = 0.0; ph_b5_thud2 = 0.0; return; }
        if (shiftCD > 0f) return;
        bool shifted = false;
        // [FIX per instruction] kdExt (the 0.78x gear-1 threshold shrink)
        // removed from the actual shift condition below -- kickdown now
        // only ever changes RPM gain (via CalcB500RRPM's isl9Character
        // slip boost), never speed/gear points, matching the fix just
        // applied to gears 2-6 above.
        if      (gear == 1 && spd >= (engineType == EngineType.ISL9 ? isl9G1Speed : B500R_G1_UP) * ecoUpTighten) { gear = 2; shifted = true; }
        // [FIX per instruction] Kickdown used to stretch gears 2-6's
        // upshift points out by 1.28x (letting the bus rev further before
        // shifting) -- that changes WHERE it shifts, not how hard it revs
        // getting there. Kickdown now only feeds the RPM-gain side
        // (isl9Character's slip multiplier in CalcB500RRPM); the gear
        // ladder itself is identical with or without kickdown held, same
        // as B400R Gen4/Gen5 already do.
        else if (gear == 2 && spd >= B500R_G2_UP * ecoUpTighten) { gear = 3; shifted = true; }
        else if (gear == 3 && spd >= B500R_G3_UP * ecoUpTighten) { gear = 4; shifted = true; }
        else if (gear == 4 && spd >= B500R_G4_UP * ecoUpTighten) { gear = 5; shifted = true; }
        else if (gear == 5 && spd >= B500R_G5_UP * ecoUpTighten) { gear = 6; shifted = true; }
        if (shifted) {
            shiftCD = 0.65f; alShiftTransient = 1f; alShiftTransientDur = 0.26f;
            b500_shiftThud = 1.0f * AllisonGearTaper(gear); ph_b5_thud1 = 0.0; ph_b5_thud2 = 0.0; // [FIX -- click bug] zero the thud's own phase on every envelope-open, same fix as B400R -- kills the shift "tick" discontinuity; [ADD] tapered by real per-gear shape
            b500_lockupThud = (gear >= 3 && !AL_LOCK_B500[gear - 1]) ? 1.0f : 0f;
            if (b500_lockupThud > 0f) ph_b5_wh2 = 0.0; // [FIX -- click bug] same phase-zero fix -- ph_b5_wh2 is only ever read by the lockup thud, safe to reset
            al_rpmVelocity = 0f; return;
        }
        bool dn = false;
        // Continuous load-scaling (Load-Based Shift Scheduling), converging
        // to the SAME downshift point at light throttle for both modes,
        // only diverging as load increases.
        // [KICKDOWN OVERRIDE] Same bypass as ecoUpTighten above.
        float ecoResist = (economyMode && !kickdownKey) ? Mathf.Lerp(1.0f, 0.85f, Mathf.Clamp01(accel)) : 1.0f;
        if      (gear == 6 && spd < B500R_G6_DN * ecoResist) { gear = 5; dn = true; }
        else if (gear == 5 && spd < B500R_G5_DN * ecoResist) { gear = 4; dn = true; }
        else if (gear == 4 && spd < B500R_G4_DN * ecoResist) { gear = 3; dn = true; }
        else if (gear == 3 && spd < B500R_G3_DN * ecoResist) { gear = 2; dn = true; }
        else if (gear == 2 && spd < B500R_G2_DN * ecoResist) { gear = 1; dn = true; }
        if (dn) { shiftCD = 0.45f; alShiftTransient = 1f; alShiftTransientDur = 0.22f; b500_shiftThud = 0.70f * AllisonGearTaper(gear); ph_b5_thud1 = 0.0; ph_b5_thud2 = 0.0; } // [FIX -- click bug] same phase-zero fix; [ADD] tapered by real per-gear shape
    }

    // [ADD] Shared late-gear RPM ramp helper -- referenced by
    // CalcB500RGen5RPM below (this file snapshot didn't have it defined at
    // all, confirmed via search -- re-added here).
    private float LateGearRampBoost(int g, float up1, float up2, float up3, float up4, float up5, float kdMul, float boostAmount)
    {
        if (g < 1 || g > 5) return 0f;
        float[] upThresh = { 0f, up1, up2, up3, up4, up5 };
        float gUp = upThresh[g] * kdMul;
        float gProgress = Mathf.Clamp01(spd / Mathf.Max(1f, gUp));
        float lateBoost = Mathf.Clamp01((gProgress - 0.65f) / 0.35f);
        lateBoost *= lateBoost;
        return lateBoost * boostAmount;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  GEAR HELPERS — Allison B500R GEN 5 (2017+ "5th Generation Controls")
    //  Real, documented generational split -- Allison's own valve-body docs
    //  cover Gen 4/Gen 5 B400R/B500R separately, tied to a real "5th
    //  Generation Controls" refresh (2021 GM/Allison doc references it by
    //  name). Newer electronic control system: smoother, quicker, quieter
    //  than Gen 4 above -- normal driving sits close to what Gen 4's OWN
    //  economy mode sounds like, with Gen 5's own eco going lower again on
    //  top of that. tx = "b500r_g5".
    // ═════════════════════════════════════════════════════════════════════════
    private float CalcB500RGen5RPM()
    {
        // [REBUILT per instruction] Same fix shape as B400R Gen 5 above --
        // Gen 5 no longer computes its own RPM. Straight passthrough of the
        // real B500R math (CalcB500RRPM) plus the same smoothing "mask" on
        // top, so it reads smoother/quieter without any separate revving
        // logic underneath.
        float raw = CalcB500RRPM();
        float maskTau = 0.10f;
        b5g5_maskRPM = Mathf.Lerp(b5g5_maskRPM, raw, 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, maskTau)));
        return b5g5_maskRPM;
    }

    private void DoB500RGen5Gear()
    {
        // [REBUILT per instruction] No more independent Gen 5 shift ladder
        // -- this is now literally DoB500RGear() (real B500R shift points),
        // with Gen 5's own (quieter, snappier) thud character still fired
        // on any gear change so DoB500RGen5DSP's b5g5_shiftThud/
        // b5g5_lockupThud layers keep working.
        int gearBefore = gear;
        bool wasLockedBefore = gearBefore >= 1 && AL_LOCK_B500[Mathf.Min(gearBefore, AL_LOCK_B500.Length - 1)];
        DoB500RGear();
        if (gear != gearBefore)
        {
            b5g5_shiftThud = gear > gearBefore ? 1.0f : 0.70f;
            ph_b5g5_thud1 = 0.0; ph_b5g5_thud2 = 0.0; // [FIX -- click bug] zero the thud's own phase on every envelope-open
            bool nowLocked = AL_LOCK_B500[Mathf.Min(gear, AL_LOCK_B500.Length - 1)];
            b5g5_lockupThud = (gear >= 3 && nowLocked && !wasLockedBefore) ? 1.0f : 0f;
            if (b5g5_lockupThud > 0f) ph_b5g5_wh2 = 0.0; // [FIX -- click bug] ph_b5g5_wh2 is only ever read by the lockup thud, safe to reset
            b5g5_prevGearForThud = gear;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  GEAR HELPERS — Allison B400R GEN 5 (2017+ "5th Generation Controls")
    //  Same real generational split as B500R Gen 5 above. B400R Gen 4's own
    //  TCC-blend architecture (B4xUpdateTCC etc.) is deliberately NOT
    //  duplicated here -- Gen 5's whole character is simpler, more refined
    //  electronic control, so a leaner structure is the more faithful way
    //  to build it, not less. tx = "b400r_g5".
    // ═════════════════════════════════════════════════════════════════════════
    private float CalcB400RGen5RPM()
    {
        // [REBUILT per instruction] Gen 5 no longer computes its OWN RPM at
        // all -- that's exactly what was inventing the "sounds like Voith
        // NXT with weird revs" bug (phantom slip added in gears 2-5 that
        // real B400R never has, plus a late-gear ramp of its own). This is
        // now a straight passthrough of the real B400R math, with only an
        // extra smoothing "mask" applied on top so the newer electronic
        // controls read as smoother/quieter acoustically -- the underlying
        // revving behavior is now IDENTICAL to Gen 4.
        float raw = CalcAllisonRPM();
        float maskTau = 0.10f;
        b4g5_maskRPM = Mathf.Lerp(b4g5_maskRPM, raw, 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, maskTau)));
        return b4g5_maskRPM;
    }

    private void DoB400RGen5Gear()
    {
        // [REBUILT per instruction] No more independent Gen 5 shift ladder
        // -- this is now literally DoAllisonGear() (real B400R shift
        // points), with Gen 5's own (quieter, snappier) thud character
        // still fired on any gear change so DoB400RGen5DSP's b4g5_shiftThud
        // layer keeps working.
        int gearBefore = gear;
        DoAllisonGear();
        if (gear != gearBefore)
        {
            b4g5_shiftThud = gear > gearBefore ? 0.55f : 0.40f;
            ph_b4g5_thud1 = 0.0; ph_b4g5_thud2 = 0.0; // [FIX -- click bug] zero the thud's own phase on every envelope-open
            b4g5_prevGearForThud = gear;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  GEAR HELPERS — Allison B3400xFE (fuel-economy variant, earlier lockup)
    // ═════════════════════════════════════════════════════════════════════════
    private float CalcB3400RPM()
    {
        if (gear == 0) return IDLE;
        // [ADD per instruction] B3400xFE never had a kickdown RPM-gain
        // character at all -- kickdown only used to stretch the gear
        // ladder (DoB3400Gear's old kdExt), which is being removed there
        // so gear points stay identical. This gives kickdown somewhere to
        // actually go: more slip/rev under hard throttle, same shape as
        // every other Allison variant's isl9Character boost, just milder
        // (xFE's whole mandate is tighter lockup/less slip than B400R).
        bool kdCharacter = kickdownKey && !economyMode;
        int g = Mathf.Min(gear, AL_R_B3400.Length - 1);
        float outShaft = (spd / 3.6f) / TCIRC * 60f;
        float inRPM    = outShaft * FINAL * AL_R_B3400[g];
        // [REDONE — B400R-base unification, same helper as the two B500Rs
        // and Gen5 B400R above] Locks from G2 — far less slip overall than
        // B400R/B500R, true to its fuel-economy mandate — but the ONE real
        // lock boundary it does have (end of gear 1) now gets the same
        // smooth flare-then-settle instead of an instant switch.
        UpdateAllisonLockBlend(AL_LOCK_B3400[g], B4R_TCC_APPLY_TIME, B4R_TCC_SHIFT_RELEASE, Time.deltaTime);
        float openFrac = 1f - b4r_tccBlend;
        float slipBase = (gear == 1 ? accel * 360f + 80f : accel * 110f + 25f) * openFrac;
        float slip = kdCharacter ? slipBase * 1.35f : slipBase;
        float r = inRPM + slip;
        // [REDONE] Matches the B500R fix: real Allison Eco/FuelSense doesn't
        // cap RPM at all -- it only moves the shift SCHEDULE at heavy
        // throttle (see DoB3400Gear below). The old flat economyMode RPM
        // ceiling here was the same wrong model B500R/B400R Gen4 already
        // had removed, just never ported over to B3400xFE -- it clamped
        // RPM growth independent of the shift points, which is exactly
        // the kind of mismatch that reads as "weird" (RPM flatlining
        // against a wall mid-gear instead of tracking speed/load, with a
        // visible seam right at the eco/perf toggle). Ceiling is now the
        // same regardless of economyMode; economyMode only feeds into
        // DoB3400Gear's shift thresholds.
        float ceiling = gear == 1 ? 1820f : 1700f;
        if (kdCharacter) ceiling *= 1.12f; // kickdown raises the ceiling too, not just the slip term
        r = Mathf.Min(r, ceiling);
        float targetRPM = Mathf.Clamp(r, IDLE, GOV);
        // [REDONE — Allison-smooth] Same jump fix as every other Allison
        // variant above -- this used to return the raw target unfiltered.
        float rpmDampTime = kdCharacter ? 0.08f : 0.12f;
        al_smoothedRPM = Mathf.SmoothDamp(al_smoothedRPM, targetRPM, ref al_rpmVelocity, rpmDampTime);
        return Mathf.Max(IDLE, al_smoothedRPM);
    }

    private void DoB3400Gear()
    {
        // [ADD -- real per-gear shape] Launch into G1 previously had no
        // thud trigger, same gap B400R/B500R had -- real B3400xFE's G1
        // bark is real but noticeably SHORTER/lighter than either sibling
        // (smaller/lighter 44-tooth internals, less to spin up), which is
        // handled below by this variant's own faster decay rate rather
        // than by shrinking the taper itself (the taper shape is shared).
        if (gear == 0) { gear = 1; shiftCD = 0.5f; alShiftTransient = 1f; alShiftTransientDur = 0.20f; b34_shiftThud = 0.65f * AllisonGearTaper(1); ph_b34_thud1 = 0.0; ph_b34_thud2 = 0.0; return; }
        if (shiftCD > 0f) return;
        // [FIX per instruction] kdExt (1.24x shift-point stretch) removed --
        // kickdown now only changes RPM gain via CalcB3400RPM's kdCharacter
        // boost, never the gear ladder itself.
        float ecoUpTighten = (economyMode && !kickdownKey) ? Mathf.Lerp(1.0f, 0.85f, Mathf.Clamp01(accel)) : 1.0f;
        int prevGearForLockup = gear;
        bool shifted = false;
        if      (gear == 1 && spd >= B3400_G1_UP * ecoUpTighten) { gear = 2; shifted = true; }
        else if (gear == 2 && spd >= B3400_G2_UP * ecoUpTighten) { gear = 3; shifted = true; }
        else if (gear == 3 && spd >= B3400_G3_UP * ecoUpTighten) { gear = 4; shifted = true; }
        else if (gear == 4 && spd >= B3400_G4_UP * ecoUpTighten) { gear = 5; shifted = true; }
        else if (gear == 5 && spd >= B3400_G5_UP * ecoUpTighten) { gear = 6; shifted = true; }
        if (shifted) { shiftCD = 0.60f; alShiftTransient = 1f; alShiftTransientDur = 0.20f; b34_shiftThud = 0.65f * AllisonGearTaper(gear); ph_b34_thud1 = 0.0; ph_b34_thud2 = 0.0; al_rpmVelocity = 0f; // [FIX -- click bug] zero the thud's own phase, same fix as B400R/B500R; [ADD] tapered by real per-gear shape
            // B3400xFE locks starting gear 2 (AL_LOCK_B3400), same schedule
            // as B400R -- so its "end of gear 1" lockup moment happens at
            // this exact transition too. It never had this sound before;
            // this gives it the same thump + burst B400R/B500R both have,
            // at the gear that's actually correct for ITS lock schedule.
            if (prevGearForLockup == 1 && gear == 2 && !al_dblTapActive)
            {
                al_dblTapActive = true;
                al_dblTapTimer  = 0f;
                b34_lockupThud  = 1.0f;
            }
            return; }
        bool dn = false;
        // [KICKDOWN OVERRIDE] Same bypass as ecoUpTighten above.
        float ecoResist = (economyMode && !kickdownKey) ? Mathf.Lerp(1.0f, 0.85f, Mathf.Clamp01(accel)) : 1.0f;
        if      (gear == 6 && spd < B3400_G6_DN * ecoResist) { gear = 5; dn = true; }
        else if (gear == 5 && spd < B3400_G5_DN * ecoResist) { gear = 4; dn = true; }
        else if (gear == 4 && spd < B3400_G4_DN * ecoResist) { gear = 3; dn = true; }
        else if (gear == 3 && spd < B3400_G3_DN * ecoResist) { gear = 2; dn = true; }
        else if (gear == 2 && spd < B3400_G2_DN * ecoResist) { gear = 1; dn = true; }
        if (dn) { shiftCD = 0.42f; alShiftTransient = 1f; alShiftTransientDur = 0.18f; b34_shiftThud = 0.45f * AllisonGearTaper(gear); ph_b34_thud1 = 0.0; ph_b34_thud2 = 0.0; } // [FIX -- click bug] same phase-zero fix; [ADD] tapered by real per-gear shape
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  GEAR HELPERS — ZF EcoLife 6AP1200B
    // ═════════════════════════════════════════════════════════════════════════
    private float CalcZFRPM()
    {
        if (gear == 0) return IDLE;
        int g = Mathf.Min(gear, ZF_R.Length - 1);
        float outShaft = (spd / 3.6f) / TCIRC * 60f;
        float inRPM    = outShaft * FINAL * ZF_R[g];
        // TC slip — ZF lockup from G3; G1 heavy slip, G2 moderate
        // [ADD per instruction] ZF never had a kickdown RPM-gain character
        // at all -- kickdown used to only stretch the shift ladder
        // (DoZFGear's old kdExt), which is being removed there so gear
        // points stay identical. Same shape as the Allison family's
        // isl9Character boost: more slip and a higher ceiling under WOT
        // kickdown, nothing to do with where the shifts actually land.
        bool kdCharacter = kickdownKey && !economyMode;
        float slipBase = !ZF_LOCK[g] ? (gear == 1 ? accel * 360f + 70f : accel * 90f + 20f) : 0f;
        float slip = kdCharacter ? slipBase * 1.4f : slipBase;
        float r = inRPM + slip;
        float ceiling;
        if (economyMode) ceiling = gear == 1 ? 1320f : 1440f;
        else             ceiling = gear == 1 ? 1880f : 1780f;
        if (kdCharacter) ceiling *= 1.15f;
        r = Mathf.Min(r, ceiling);
        float targetRPM = Mathf.Clamp(r, IDLE, GOV);
        // [REDONE — smooth] Same jump fix as the Allison family: this used
        // to return the raw computed target with zero filtering every
        // frame, which is exactly why ZF read as jumpy/un-smooth compared
        // to Allison's SmoothDamp-backed output.
        float zfDampTime = kdCharacter ? 0.08f : 0.12f;
        zf_smoothedRPM = Mathf.SmoothDamp(zf_smoothedRPM, targetRPM, ref zf_rpmVelocity, zfDampTime);
        return Mathf.Max(IDLE, zf_smoothedRPM);
    }

    private void DoZFGear()
    {
        if (gear == 0) { gear = 1; shiftCD = 0.40f; alShiftTransient = 1f; alShiftTransientDur = 0.18f; return; }
        if (shiftCD > 0f) return;
        // [FIX per instruction] kdExt (1.22x shift-point stretch) removed --
        // kickdown now only changes RPM gain via CalcZFRPM/CalcZFEL2RPM's
        // kdCharacter boost, never the gear ladder itself.
        bool shifted = false;
        if      (gear == 1 && spd >= ZF_G1_UP) { gear = 2; shifted = true; }
        else if (gear == 2 && spd >= ZF_G2_UP) { gear = 3; shifted = true; }
        else if (gear == 3 && spd >= ZF_G3_UP) { gear = 4; shifted = true; }
        else if (gear == 4 && spd >= ZF_G4_UP) { gear = 5; shifted = true; }
        else if (gear == 5 && spd >= ZF_G5_UP) { gear = 6; shifted = true; }
        if (shifted) { shiftCD = 0.48f; alShiftTransient = 1f; alShiftTransientDur = 0.16f; zf_shiftThud = 1.0f; ph_zf_thud1 = 0.0; ph_zf_thud2 = 0.0; zf_rpmVelocity = 0f; return; } // [FIX -- click bug] zero the thud's own phase on every envelope-open
        bool dn = false;
        if      (gear == 6 && spd < ZF_G6_DN) { gear = 5; dn = true; }
        else if (gear == 5 && spd < ZF_G5_DN) { gear = 4; dn = true; }
        else if (gear == 4 && spd < ZF_G4_DN) { gear = 3; dn = true; }
        else if (gear == 3 && spd < ZF_G3_DN) { gear = 2; dn = true; }
        else if (gear == 2 && spd < ZF_G2_DN) { gear = 1; dn = true; }
        if (dn) { shiftCD = 0.38f; alShiftTransient = 1f; alShiftTransientDur = 0.16f; zf_shiftThud = 0.55f; ph_zf_thud1 = 0.0; ph_zf_thud2 = 0.0; } // [FIX -- click bug] same phase-zero fix
    }

    // ═════════════════════════════════════════════════════════════════════
    //  ZF ECOLIFE 2 — RPM CALC. Real-world grounded (ZF's own EcoLife 2
    //  launch materials, full citations in DoZFEL2DSP's header):
    //    · Universal stop-start -- "for all model variants", unconditional,
    //      unlike NXT's Eco-button-gated version. Same safe silence-the-rpm
    //      pattern NXT already uses rather than touching engineState.
    //    · "Shifting at lower engine speeds to improve fuel efficiency" --
    //      ZF kept "the basic principle of a six-stage planetary gearset
    //      with torque converter and primary retarder," so DoZFGear() above
    //      is reused UNCHANGED for zfel2/zfel2_hd (same real road-speed
    //      shift schedule) -- only the RPM ceiling per gear is genuinely
    //      lower here, which is exactly what "shifts at lower engine
    //      speeds" means for the SAME shift points.
    //    · heavyDuty selects the 6AP1620/1720 torque-rated class (60ft
    //      artic) vs 6AP1420 (35/40ft) -- same real ZF part-number
    //      convention as this file's existing B400R/B500R split by size.
    // ═════════════════════════════════════════════════════════════════════
    private float CalcZFEL2RPM(bool heavyDuty)
    {
        bool stoppedHere = spd < 0.3f && accel < 0.02f;
        if (stoppedHere)
        {
            zfel2_stopTimer += Time.deltaTime;
            if (zfel2_stopTimer > 2.2f) zfel2_engineOff = true; // real stop-start settles in within a couple seconds of a stop
        }
        else
        {
            if (zfel2_engineOff && accel > 0.05f) zfel2_restartPulse = 1f; // soft integrated-starter re-crank, not a traditional grind
            zfel2_engineOff = false;
            zfel2_stopTimer = 0f;
        }

        if (gear == 0) return IDLE;
        if (zfel2_engineOff) { zf_rpmVelocity = 0f; zf_smoothedRPM = 0f; return 0f; } // genuine silence -- same pattern CalcNXTRPM already uses for its own stop-start, plus reset the smoothing state so restart doesn't slide up from a stale silent value

        bool kdCharacter = kickdownKey && !economyMode;
        int g = Mathf.Min(gear, ZF_R.Length - 1);
        float outShaft = (spd / 3.6f) / TCIRC * 60f;
        float inRPM    = outShaft * FINAL * ZF_R[g];
        // Slip is tighter than gen 1's -- the new torsional damper is
        // specifically about reducing converter/driveline vibration, which
        // in practice means tighter slip control too. Heavy-duty class
        // carries a touch more residual slip (bigger converter, more
        // torque capacity to absorb).
        float slipMul = heavyDuty ? 0.92f : 0.85f;
        float slipBase = !ZF_LOCK[g] ? (gear == 1 ? accel * 360f + 70f : accel * 90f + 20f) * slipMul : 0f;
        float slip = kdCharacter ? slipBase * 1.4f : slipBase;
        float r = inRPM + slip;
        // [LOWER SHIFT-SPEED RPM] Real spec: genuinely lower RPM ceiling per
        // gear than gen 1's 1880f/1780f (economy 1320f/1440f), same road-
        // speed shift points (DoZFGear unchanged).
        float ceiling;
        if (economyMode) ceiling = gear == 1 ? 1220f : 1320f;
        else             ceiling = gear == 1 ? 1680f : 1580f;
        if (kdCharacter) ceiling *= 1.15f;
        r = Mathf.Min(r, ceiling);
        float targetRPM = Mathf.Clamp(r, IDLE, GOV);
        // [REDONE — smooth] Same jump fix as CalcZFRPM.
        float zfDampTime = kdCharacter ? 0.08f : 0.12f;
        zf_smoothedRPM = Mathf.SmoothDamp(zf_smoothedRPM, targetRPM, ref zf_rpmVelocity, zfDampTime);
        return Mathf.Max(IDLE, zf_smoothedRPM);
    }

private float CalcVoithRPM()
{
    if (gear == 0) return IDLE;

    // ═════════════════════════════════════════════════════════════════════════
    //  ANS — AUTOMATIC NEUTRAL AT STANDSTILL  (Voith-specific, DIWA.6/.5 only)
    //  Grounded in the real Voith DIWA.6 Technical Manual (150.00845811en).
    //  Mechanism: at a stop, the input clutch EK is DISENGAGED and the
    //  transmission locks itself mechanically via the turbine brake (TB) and
    //  reverse gear brake (RB). This is genuinely NOT the same thing as
    //  Allison's engine stop-start or the NXT's mild-hybrid stop-start
    //  elsewhere in this file -- the engine keeps running here; it's the
    //  TRANSMISSION that decouples from it. Real quote: ANS "significantly
    //  reduces the dragging effect of the transmission which is otherwise
    //  still effective when the vehicle is stationary. As a result, the
    //  engine runs smoother."
    //
    //  Activation conditions below are the manual's own list, verbatim in
    //  substance: accelerator pedal in idle position, driving speed below
    //  1 km/h, forward gear engaged, engine speed below 1000 rpm, ABS not
    //  active. (The manual also lists output-speed-sensor and solenoid-valve
    //  health checks -- not modeled here since this sim has no failure
    //  states for those components.)
    //
    //  Audibly: engine load drops off the instant ANS engages, so revs
    //  settle to a lighter, smoother, slightly LOWER idle than a normal
    //  in-gear stop, and the converter drag disappears. DoVoithDSP reads
    //  voith_ansActive for the drag/whine side of this.
    // ═════════════════════════════════════════════════════════════════════════
    bool ansConditions = accel < 0.02f
                      && spd < 1f
                      && gear > 0
                      && rpm < 1000f
                      && !absActive;

    if (ansConditions)
    {
        voith_ansTimer += Time.deltaTime;
        // Small settle delay so ANS doesn't flicker in/out while the bus is
        // still rocking to a halt right at the 1 km/h line.
        if (voith_ansTimer > 0.5f) voith_ansActive = true;
    }
    else
    {
        voith_ansTimer = 0f;
        // Re-engagement is immediate the moment any condition breaks (real
        // ANS drops out as soon as the driver touches the accelerator).
        if (voith_ansActive) { voith_ansActive = false; voith_ansEngagePulse = 1f; }
    }

    if (voith_ansActive)
    {
        // Input clutch disengaged -> engine is no longer dragging the
        // converter, so it settles slightly below normal idle and much
        // smoother. This is the whole audible point of the feature.
        return IDLE * 0.94f;
    }

    // ── Kickdown, rebuilt — this was dead (kdRpmBoost was a literal
    // no-op, `kickdownKey ? 1f : 1.0f`, and voith_kdRevActive/
    // voith_kdRevTimer were declared but never once read anywhere in the
    // file — leftover fields from a kickdown system that got stripped
    // out at some point). "More integrated" than D8645's version means
    // ONE continuous hold state feeding every gear's RPM uniformly,
    // instead of a scripted separate event: holds ~2s after the key
    // actually releases (voith_kdRevTimer/voith_kdRevActive -- the
    // orphaned fields, now doing their intended job), rather than
    // snapping back the instant the driver lets off.
    if (kickdownKey) { voith_kdRevActive = true; voith_kdRevTimer = 0f; }
    else if (voith_kdRevActive)
    {
        voith_kdRevTimer += Time.deltaTime;
        if (voith_kdRevTimer > 2f) voith_kdRevActive = false;
    }
    float kdRpmBoost = voith_kdRevActive ? 1.18f : 1.0f;

    if (gear == 1)
    {
        // --- LONGER GEAR 1 ---
        // Stretched by 15% so the RPM climb matches the longer shift point

        // [FIX] spdRamp denominator (*0.6) hit 1.0 way before the real shift point
        // (*1.15, same as DoVoithRange), so turbineClimb flatlined mid-gear --
        // audible "stuck" plateau from ~13kph all the way to the ~25kph shift.
        // Denominator now matches the actual shift speed so the climb is linear
        // across the full gear, not capped early.
        float g1ShiftSpd   = VOITH_GEAR1_UPSHIFT_SPD;
        float spdRamp      = Mathf.Clamp01(spd / g1ShiftSpd);
        float throttleBase = IDLE + accel * 200f;

        // Extra RPM in gear 1, on request -- a flat additive bump on top of
        // the existing throttle-based climb, scaling a little with throttle
        // so it's not just a static idle-up regardless of accel.
        float g1ExtraRPM = 180f + accel * 120f;
        throttleBase += g1ExtraRPM;
        
        // Older TC stage: climbs a bit more eagerly (looser converter)
        // Stretched turbine climb to 950f (up from 900f) to smoothly scale into the higher ceiling
        // [FIX] Clamp01(0.28 + accel*19) started the climb multiplier too low and
        // ramped too gently, so revs bloomed sluggishly off idle even under real
        // throttle. Raised floor + steeper slope so the climb responds fast.
        float turbineClimb = spdRamp * 700f * (hillMode ? 1.3f : 1.0f);
        
        // DIWA motor kicker -- last 15% before the upshift, layer on an extra
        // rev/jump so it doesn't just coast flat into the shift.
        turbineClimb += 320f * Mathf.Clamp01(0.9f + accel);
        
        float baseRPM = (throttleBase + turbineClimb) * kdRpmBoost;
        
        if (bkPd > 0.08f) baseRPM = Mathf.Max(IDLE, baseRPM - bkPd * 900f);
        
        // --- HIGHER RPM CEILING ---
        // [FIX] Flat 2300f clamped before the linear ramp actually reached the
        // shift point (natural max ~= IDLE + 1850 at full throttle), so it
        // plateaued from ~22kph to the shift instead of climbing all the way in.
        // Ceiling now floats off IDLE with headroom so the clamp only ever acts
        // as a safety cap, not a mid-gear flatline.
        // [KICKDOWN] Ceiling itself lifts too, not just the climb -- "lets it
        // rev higher," not just "revs harder against the same cap."
        float gear1Ceiling = voith_kdRevActive ? 4550f : 4060f;

        // Hill Mode "stall" flare -- like a ZF/Allison torque converter
        // hitting its stall speed: in the last 30% of the gear, RPM stops
        // climbing linearly with speed and instead flares/holds up near the
        // ceiling, as if the engine can't push road speed any further in
        // this gear without the upshift. Combined with the extended
        // upshift threshold in DoVoithRange, this is the "laboring, about
        // to give up on this gear" moment before it finally grabs the next
        // one. Only active in gears 1-3 (gear 4/OD doesn't stall this way).
        if (hillMode)
        {
            float stallZone = Mathf.Clamp01((spdRamp - 0.7f) / 0.3f);
            baseRPM = Mathf.Lerp(baseRPM, gear1Ceiling * 0.95f, stallZone * 0.55f);
        }

        return Mathf.Clamp(baseRPM, IDLE, gear1Ceiling);
    }
    else
    {
        float[] v5Ratios = { 0f, 0f, 1.430f, 1.000f, 0.700f }; // 4th = 0.70 OD, per spec
        float ratio = gear < v5Ratios.Length ? v5Ratios[gear] : 1.0f;
        float outShaftRPM = (spd / 3.6f) / TCIRC * 60f;
        
        // [FIX] rpm here was pure ratio math against road speed, so it never
        // actually "reached" for the top of the gear before the upshift fired --
        // no climb-to-peak, so the whine (which tracks rpm) never peaked either.
        // Add a converter-slip bump that grows as spd approaches this gear's
        // upshift point, stronger under load, so rpm visibly climbs into the shift.
        // [FIX] upSpd didn't include DoVoithRange's shift-point multipliers
        // (1.10 for G2, 1.05 for G3, plus kdExtend), so gearProgress hit 1.0
        // well before the real shift -- same early-plateau bug as gear1 had.

        float upSpd = gear == 2 ? VOITH_GEAR2_UPSHIFT_SPD
                    : gear == 3 ? VOITH_GEAR3_UPSHIFT_SPD
                    : VOITH_GEAR4_UPSHIFT_SPD;
        float gearProgress = Mathf.Clamp01(spd / Mathf.Max(1f, upSpd));
        // [FIX] Pow(gearProgress, 2.2) stayed near-flat for most of the gear then
        // spiked right at the end -- read as "stuck until it's almost time to
        // shift". Base climb is now linear across the whole gear, with a smaller
        // end-of-gear kicker (last 18%) doing the "revs some more" jump instead
        // of carrying the whole climb.
        float climbBump = 1.0f;
        
        // A bit more residual slip carried into mechanical gears (older unit)
        // [KICKDOWN] kdRpmBoost applied here too -- "rest gears will also
        // have increased rev," not just gear 1.
        float baseRPM = (outShaftRPM * FINAL * ratio + climbBump + (gear == 2 ? accel * 130f + 60f : accel * 70f)) * kdRpmBoost;
        if (bkPd > 0.08f) baseRPM = Mathf.Max(IDLE, baseRPM - bkPd * 280f);

        // Hill Mode "stall" flare, gears 2-3 only (gear 4/OD stays clean --
        // an overdrive gear doesn't get flogged the way the lower ones do).
        // Same converter-stall-speed idea as gear 1 above: last 30% of the
        // gear, RPM stops tracking road speed linearly and flares toward
        // governor instead.
        if (hillMode && gear <= 3)
        {
            float stallZone = Mathf.Clamp01((gearProgress - 0.7f) / 0.3f);
            baseRPM = Mathf.Lerp(baseRPM, GOV, stallZone * 0.55f);
        }

        return Mathf.Clamp(baseRPM, IDLE, GOV);
    }
}

private void DoVoithRange(float dt)
{
    // Keeping the exact 864.5 range/shift logic structure
    if (gear == 0) { gear = 1; gearHoldTimer = 0f; return; }
    if (shiftCD > 0f) return;
    
    bool isLaunching = spd < 2.0f && gear == 1;
    if (!isLaunching && gearHoldTimer < 0.26f) return; // 864.5 loose hold

    // Hill Mode holds every gear longer before upshifting -- same lugging
    // pattern kickdown already gets via kdExtend, but authored on the load
    // itself (grade), not driver input. 1.35x means 3rd gear rides all the
    // way to ~73 km/h instead of 54 before grabbing 4th, etc -- the bus
    // visibly struggles in a lower gear rather than shifting up early and
    // hiding the strain.
    float hillExtend = hillMode ? 1.35f : 1.0f;

    // opt1_4's "gear 1 is also extended" — only gear 1 stretches, matching
    // the video analysis (whine/hiss/piston-fire behavior was all specific
    // to an unusually long G1, not a general lugging pattern across every
    // gear the way hillMode's extend is).
    //
    // [ADD] diwaOpt1_6 ("RPM itself follows the extended one of opt1_4")
    // shares this exact same timing -- NOT opt1_4's hiss/firing-click DSP
    // (that stays exclusive to opt1_4), just the gear-1-holds-longer shape,
    // since the eerie idle whine section below (16) is a completely
    // separate, self-contained voice from opt1_4's.
    // [FIX] Was shared between opt1_4 and opt1_6 (both 1.5x) -- opt1_4
    // reduced to 1.10x per instruction, opt1_6 kept at its original 1.5x
    // (it was built specifically against that value, "RPM follows the
    // extended one of opt_4" in its own header comment).
    // [FIXED, PER REQUEST — "physically extend g1"] This was the actual
    // gear-1->2 upshift trigger for opt1_4, and it was only stretched 1.10x
    // -- meanwhile every audio-side timing (the whine envelope, the stall-
    // recovery shape, the RPM ramp) already assumes V6_OPT4_G1_EXTEND
    // (1.35x). Physical gear 1 was upshifting well before the audio thought
    // it should, so it never actually got the longer gear it sounded like
    // it had. Now shares the same constant instead of its own separate
    // (shorter) number, so the real shift point and the audio timing agree.
    float opt1_4G1Extend = diwaOpt1_4 ? V6_OPT4_G1_EXTEND : (diwaOpt1_6 ? 1.5f : 1.0f);
    // [FIXED, PER REQUEST — "g2 gotta be extended to fit too, same for g3"]
    // Was pinned at a flat 1.25x regardless of G1's own extend, so once G1
    // got stretched to match the audio (1.35x, V6_OPT4_G1_EXTEND), 2nd/3rd
    // fell out of proportion with it again -- a longer G1 feeding into
    // unchanged-length G2/G3 read as lopsided. Now tracks G1's own extend
    // directly (times a small extra stretch, since 2nd/3rd cover more
    // ground per gear than 1st does) so tuning V6_OPT4_G1_EXTEND keeps the
    // whole opt1_4 gearset in proportion instead of just gear 1.
    float opt1_4G234Extend = diwaOpt1_4 ? V6_OPT4_G1_EXTEND * 1.05f : 1.0f;

    // [KICKDOWN] "Holds gear 1 longer" -- rebuilt kickdown (see
    // CalcVoithRPM's voith_kdRevActive) extends gear 1's hold, same idea
    // as D8645's kdExtend but driven off the shared hold state instead of
    // raw kickdownKey, so it stays held through the ~2s tail too, not just
    // while the key is physically down.
    float kdG1Extend = voith_kdRevActive ? 1.30f : 1.0f;

    // --- LONGER GEAR 1 ---
    // Multiplied by 1.15f to stretch out the speed threshold before grabbing 2nd
    if      (gear == 1 && spd > (VOITH_GEAR1_UPSHIFT_SPD * hillExtend * opt1_4G1Extend * kdG1Extend)) ShiftGearD8645(+1);
    else if (gear == 2 && spd > (VOITH_GEAR2_UPSHIFT_SPD * hillExtend * opt1_4G234Extend)) ShiftGearD8645(+1);
    else if (gear == 3 && spd > (VOITH_GEAR3_UPSHIFT_SPD * hillExtend * opt1_4G234Extend)) ShiftGearD8645(+1);
    
    if      (gear == 4 && spd < VOITH_GEAR4_DOWNSHIFT_SPD) ShiftGearD8645(-1);
    else if (gear == 3 && spd < VOITH_GEAR3_DOWNSHIFT_SPD) ShiftGearD8645(-1);
    else if (gear == 2 && spd < VOITH_GEAR2_DOWNSHIFT_SPD) ShiftGearD8645(-1);
}
    private void ShiftGear(int delta)
    {
        lastGear = gear; gear += delta;
        gear = Mathf.Clamp(gear, 1, 4);
        shiftCD = 0.9f; gearHoldTimer = 0f;
        if (lastGear == 1 && gear == 2) shiftCD = 1f;
    }

private bool  voith_kdRevActive = false;
private float voith_kdRevTimer  = 0f;

private const float BASE_UPSHIFT_SPD   = 60f;
private const float BASE_DOWNSHIFT_SPD = 18f;
private float GetNorm(int g)
{
    // Fix 1: Protect against boundaries and division by zero
    if (g <= 0 || g >= DIWA_R.Length || DIWA_R[g] == 0f) return 1f;

    // Fix 2: Reference DIWA_R instead of AL_R, and invert the math 
    // so higher ratios yield lower target speeds.
    return DIWA_R[3] / DIWA_R[g]; 
}

    private void ShiftGearD8645(int delta)
    {
        lastGear = gear; gear += delta;
        gear = Mathf.Clamp(gear, 1, 4);
        shiftCD = 1.05f; gearHoldTimer = 0f; // looser/slower than D864.6's 0.9f
        if (lastGear == 1 && gear == 2) shiftCD = 1.15f;
    }
// L9 pipe resonance
private double ph_l9_pipe = 0.0;
private double ph_l9_pipe2 = 0.0;

// L9 coast resonance ("UUUUGGHHH")
private double ph_l9_coast = 0.0;

// Slow modulation for pipe resonance
private double ph_l9_wobble = 0.0;
    // [EXPERIMENTAL, opt1_4 only] Takeoff RPM dip -- from a dead stop, real
    // DIWA torque converters briefly bog the engine down hard as the
    // converter first grabs load, before the engine muscles through it and
    // revs climb normally. Rise -> dip -> rise-to-catch-up, gated to
    // diwaOpt1_4 specifically per instruction -- CalcVoithRPM (the plain,
    // no-opt base voice) and the d8646/opt1_5 paths through this same
    // function are untouched.
    private bool  d5_opt4LaunchDipActive = false;
    private float d5_opt4LaunchDipTimer  = 0f;
    private float d5_opt4DipMulSmooth    = 0f; // [NEW] smoothed dip amount, so it eases in/out instead of snapping straight to the curve value -- this is the "bake it in" part
    private const float D5_OPT4_DIP_DUR      = 0.7f;  // dip-and-recover window, AFTER the delay below
    private const float D5_OPT4_DIP_DELAY    = 0.35f; // normal climb plays out untouched this long first
    private const float D5_OPT4_DIP_FLOOR_RPM = 1000f; // [CHANGED] absolute floor at the bottom of the dip, not a proportional multiplier -- genuinely bottoms out near-stall

    private float CalcD8645RPM()
    {
        if (gear == 0) return IDLE;

        // ═════════════════════════════════════════════════════════════════
        //  ANS — AUTOMATIC NEUTRAL AT STANDSTILL. Confirmed present since
        //  the DIWA 2 generation (1985-1999): "Automatic neutral selection
        //  when stopping." D864.5 is a later generation than that, so it
        //  genuinely has this feature too -- CalcD8645RPM never had it
        //  wired in at all before this pass. Same real mechanism as the
        //  base D864.6 voice (input clutch EK disengages at a stop, the
        //  transmission stops dragging the engine, so revs settle slightly
        //  below normal idle and smoother) -- own d5_ans* state, since this
        //  is a genuinely separate transmission from D864.6/NXT in this sim.
        // ═════════════════════════════════════════════════════════════════
        bool ansConditions = accel < 0.02f && spd < 1f && gear > 0 && rpm < 1000f && !absActive;
        if (ansConditions)
        {
            d5_ansTimer += Time.deltaTime;
            if (d5_ansTimer > 0.5f) d5_ansActive = true;
        }
        else
        {
            d5_ansTimer = 0f;
            if (d5_ansActive) { d5_ansActive = false; d5_ansEngagePulse = 1f; }
        }
        if (d5_ansActive) return IDLE * 0.94f;

        // [FIX] Was an instant on/off jump (1.0 <-> 1.16 the moment
        // kickdownKey flips) -- exactly what reads as "jumpy" kickdown.
        // d5_kdCreep was declared for this smoothing but never actually
        // wired in anywhere; ramping it toward the kickdown target instead
        // of snapping produces the smooth up/down transition genuine D8645
        // buses are known for.
        d5_kdCreep += ((kickdownKey ? 1f : 0f) - d5_kdCreep) * (kickdownKey ? 0.08f : 0.05f);
        float kdRpmBoost = Mathf.Lerp(1.0f, 1.16f, d5_kdCreep);
if (gear == 1)
{
    bool is8646 = tx == "d8646" || diwaOpt1_4 || diwaOpt1_5;

    float spdRamp      = Mathf.Clamp01(spd / VOITH_GEAR1_UPSHIFT_SPD);
    float throttleBase = IDLE + accel * 640f;

    // 8646 / Opt 1.4 / Opt 1.5:
    // Slight converter stall/lazy takeoff at very low speed.
    float turbineClimb = spdRamp
        * 900f
        * Mathf.Clamp01(0.5f + accel * 24.0f)
        * (hillMode ? 2.6f : 1.0f);

    if (is8646)
    {
        // Suppress turbine RPM contribution right off the line,
        // then progressively let the converter "grab".
        float launchGrab = Mathf.SmoothStep(0.35f, 1.0f, Mathf.Clamp01(spd / 5.0f));
        turbineClimb *= launchGrab;
    }

    float baseRPM = (throttleBase + turbineClimb) * kdRpmBoost;

    // [NEW, PER REQUEST] opt1_4-only continuous rpm ramp, same shape family
    // as CalcH50EPRPM's cruise curve (a steady climb across speed plus a
    // throttle contribution, slewed toward the target rather than jumping)
    // instead of the turbineClimb/launchGrab shape above. Voith's real gear
    // 1 covers meaningfully more ground than H50EP's EVT curve, so the same
    // idea is stretched across a longer span (~0-26kph vs H50EP's ~0-20)
    // and slewed slower to match.
    if (diwaOpt1_4)
    {
        float rampProg   = Mathf.Clamp01(spd / V6_OPT4_RAMP_SPAN_KPH);
        float rampTarget = IDLE + rampProg * V6_OPT4_RAMP_HZ_SPAN + accel * 150f;
        if (v6_opt4G1RampSm <= 0f) v6_opt4G1RampSm = rampTarget; // first-tick seed, same pattern EvtContinuousRPM uses
        float rampRate = rampTarget > v6_opt4G1RampSm ? 190f : 130f; // slower than H50EP's 230/150 -- longer gear, lazier ramp
        v6_opt4G1RampSm = Mathf.MoveTowards(v6_opt4G1RampSm, rampTarget, rampRate * Time.deltaTime);
        baseRPM = v6_opt4G1RampSm * kdRpmBoost;
    }
    else
    {
        v6_opt4G1RampSm = 0f; // reseed next time opt1_4 gear 1 is entered
    }

    if (bkPd > 0.08f)
        baseRPM = Mathf.Max(IDLE, baseRPM - bkPd * 400f);

    // [REMOVED, PER REQUEST] The opt1_4 launch dip (dip-and-recover sine
    // bite down toward D5_OPT4_DIP_FLOOR_RPM early in the launch) is gone --
    // it read as a weird little rpm dip, not a real engine behavior. Fields
    // (d5_opt4LaunchDipActive/Timer, d5_opt4DipMulSmooth, D5_OPT4_DIP_*) are
    // left declared but now unused.
    float d5DipFloorOverride = IDLE;

    float d8645Gear1Ceiling = kickdownKey ? GOV : 2100f;

    if (hillMode)
    {
        float stallZone = Mathf.Clamp01((spdRamp - 0.7f) / 0.3f);
        baseRPM = Mathf.Lerp(
            baseRPM,
            d8645Gear1Ceiling * 0.95f,
            stallZone * 0.55f
        );
    }

    // [EXPERIMENTAL, opt1_4 only] d5DipFloorOverride drops below IDLE only
    // while the dip curve is actually biting; otherwise it's just IDLE, so
    // this clamp is a no-op for every other case.
    return Mathf.Clamp(baseRPM, d5DipFloorOverride, d8645Gear1Ceiling);
}
else
{
    float[] v5Ratios = { 0f, 0f, 1.430f, 1.000f, 0.700f };
    float ratio = gear < v5Ratios.Length ? v5Ratios[gear] : 1.0f;

    float outShaftRPM = (spd / 3.6f) / TCIRC * 60f;

    float baseRPM =
        (outShaftRPM * FINAL * ratio +
        (gear == 2 ? accel * 130f + 60f : accel * 70f))
        * kdRpmBoost;

    if (bkPd > 0.08f)
        baseRPM = Mathf.Max(IDLE, baseRPM - bkPd * 280f);

    if (hillMode && gear <= 3)
    {
        float d8645UpSpd =
            gear == 2 ? VOITH_GEAR2_UPSHIFT_SPD : VOITH_GEAR3_UPSHIFT_SPD;

        float gearProgress =
            Mathf.Clamp01(spd / Mathf.Max(1f, d8645UpSpd));

        float stallZone =
            Mathf.Clamp01((gearProgress - 0.7f) / 0.3f);

        baseRPM = Mathf.Lerp(
            baseRPM,
            GOV,
            stallZone * 0.55f
        );
    }

    return Mathf.Clamp(baseRPM, IDLE, GOV);
}
    }

    private void DoD8645Range()
    {
        if (gear == 0) { gear = 1; gearHoldTimer = 0f; return; }
        if (shiftCD > 0f) return;
        bool isLaunching = spd < 2.0f && gear == 1;
        if (!isLaunching && gearHoldTimer < 0.26f) return; // looser hold than D864.6 (0.2f)
        // Kickdown and Hill Mode both extend gear holds and STACK -- a
        // hill-mode bus under kickdown lugs even harder/longer than either
        // alone, which is correct: driver flooring it on a grade should be
        // the most strained state in the sim.
        float kdExtend   = kickdownKey ? 1.32f : 1.0f;
        float hillExtend = hillMode    ? 1.35f : 1.0f;
        // opt1_8 "strained" (d8646 only): holds every gear ~22% longer so the engine is worked much harder, rpm climbs toward the governor before each shift.
        float strainExtend = (diwaOpt1_8 && tx == "d8646") ? 1.22f : 1.0f;
        float extend     = kdExtend * hillExtend * strainExtend;
        if      (gear == 1 && spd > VOITH_GEAR1_UPSHIFT_SPD * extend) ShiftGearD8645(+1);
        else if (gear == 2 && spd > VOITH_GEAR2_UPSHIFT_SPD * extend) ShiftGearD8645(+1);
        else if (gear == 3 && spd > VOITH_GEAR3_UPSHIFT_SPD * extend) ShiftGearD8645(+1);
        if      (gear == 4 && spd < VOITH_GEAR4_DOWNSHIFT_SPD) ShiftGearD8645(-1);
        else if (gear == 3 && spd < VOITH_GEAR3_DOWNSHIFT_SPD) ShiftGearD8645(-1);
        else if (gear == 2 && spd < VOITH_GEAR2_DOWNSHIFT_SPD) ShiftGearD8645(-1);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  VOITH DIWA 867.8 NXT — gear logic + RPM curve
    // ═════════════════════════════════════════════════════════════════════════
    private void ShiftGearNXT(int delta)
    {
        lastGear = gear; gear += delta;
        gear = Mathf.Clamp(gear, 1, 6);
        shiftCD = 0.85f; gearHoldTimer = 0f; // tighter/quicker than classic DIWA -- newer valve body
        if (lastGear == 1 && gear == 2) shiftCD = 1.0f; // launch gear still gets a beat longer, same convention as D8645/D864.6
    }

    private void DoNXTGear()
    {
        if (gear == 0) { gear = 1; gearHoldTimer = 0f; return; }
        if (shiftCD > 0f) return;
        bool isLaunching = spd < 2.0f && gear == 1;
        if (!isLaunching && gearHoldTimer < 0.20f) return;

        float kdExtend   = kickdownKey ? 1.28f : 1.0f;
        float hillExtend = hillMode    ? 1.30f : 1.0f;
        float extend     = kdExtend * hillExtend;

        // ── Held 2nd overdrive: gear 6's downshift threshold gets an EXTRA
        // widening on top of kickdown/hill (0.72x instead of matching the
        // upshift extend factor) — real behavior per Voith's own
        // description: "once the second overdrive is applied, we try to
        // keep it applied." This is what makes NXT feel structurally
        // different from the Allison/classic-DIWA stepped-shift pattern
        // elsewhere in the fleet: fewer shift EVENTS once at cruise, not
        // just a quieter thud when one happens.
        float g6HoldExtend = 0.72f * extend;

        if      (gear == 1 && spd > NXT_GEAR1_UPSHIFT_SPD * extend) ShiftGearNXT(+1);
        else if (gear == 2 && spd > NXT_GEAR2_UPSHIFT_SPD * extend) ShiftGearNXT(+1);
        else if (gear == 3 && spd > NXT_GEAR3_UPSHIFT_SPD * extend) ShiftGearNXT(+1);
        else if (gear == 4 && spd > NXT_GEAR4_UPSHIFT_SPD * extend) ShiftGearNXT(+1);
        else if (gear == 5 && spd > NXT_GEAR5_UPSHIFT_SPD * extend) ShiftGearNXT(+1);

        if      (gear == 6 && spd < NXT_GEAR6_DOWNSHIFT_SPD * g6HoldExtend) ShiftGearNXT(-1);
        else if (gear == 5 && spd < NXT_GEAR5_DOWNSHIFT_SPD) ShiftGearNXT(-1);
        else if (gear == 4 && spd < NXT_GEAR4_DOWNSHIFT_SPD) ShiftGearNXT(-1);
        else if (gear == 3 && spd < NXT_GEAR3_DOWNSHIFT_SPD) ShiftGearNXT(-1);
        else if (gear == 2 && spd < NXT_GEAR2_DOWNSHIFT_SPD) ShiftGearNXT(-1);
    }

    private float CalcNXTRPM(float dt)
    {
        // ── Stop-start: real spec supports holding the engine off for up to
        // 60s without affecting the driveline's readiness. Only active when
        // the Eco button (economyMode) is on -- the mild-hybrid system's
        // own ON/OFF switch. With Eco off, this behaves like a plain diesel
        // automatic and idles through stops same as every other TX that
        // doesn't have stop-start.
        bool stoppedHere = spd < 0.3f && accel < 0.02f;
        if (economyMode && stoppedHere)
        {
            nxt_stopTimer += dt;
            if (nxt_stopTimer > 2.5f) nxt_engineOff = true; // real-world stop-start engages within a couple seconds of a settled stop
        }
        else
        {
            if (nxt_engineOff && accel > 0.05f)
            {
                // Soft 48V-assisted re-crank -- quicker and gentler than a
                // traditional starter-motor grind, since the electric side
                // does the spin-up work. DoNXTDSP reads this envelope to
                // add the actual crank sound; here it just kicks it off.
                nxt_restartPulse = 1f;
            }
            nxt_engineOff = false;
            nxt_stopTimer = 0f;
        }

        if (gear == 0) return IDLE;
        if (nxt_engineOff) return 0f; // genuine silence, not idle -- the real distinguishing trait vs every other TX in the fleet

        float kdRpmBoost = kickdownKey ? 1.12f : 1.0f; // tighter than D8645's 1.16 -- newer unit, less need to lean on kickdown

        float baseRPM;
        if (gear == 1)
        {
            // Same Differential-Wandler launch principle as D8645/D864.6,
            // but tuned lower per the real "reduced average engine speed"
            // claim -- flatter throttle-to-RPM slope and a lower turbine
            // climb than either classic unit.
            float spdRamp      = Mathf.Clamp01(spd / NXT_GEAR1_UPSHIFT_SPD);
            float throttleBase = IDLE + accel * 520f; // vs D8645's 640f
            float turbineClimb = spdRamp * 700f * Mathf.Clamp01(0.5f + accel * 24.0f) * (hillMode ? 2.4f : 1.0f); // vs D8645's 900f
            baseRPM = (throttleBase + turbineClimb) * kdRpmBoost;
            if (bkPd > 0.08f) baseRPM = Mathf.Max(IDLE, baseRPM - bkPd * 400f);
            float nxtGear1Ceiling = kickdownKey ? GOV : 1950f; // vs D8645's 2100f -- quieter launch
            baseRPM = Mathf.Clamp(baseRPM, IDLE, nxtGear1Ceiling);
        }
        else
        {
            // 6 mechanical ratios (index 0/1 unused -- gear 1 is the
            // converter-assisted launch handled above). Gears 5/6 are the
            // "second overdrive" pair -- genuinely lower ratios than
            // anything the classic 4-speed family has.
            float[] nxtRatios = { 0f, 0f, 1.55f, 1.15f, 0.85f, 0.68f, 0.54f };
            float ratio = gear < nxtRatios.Length ? nxtRatios[gear] : 0.50f;
            float outShaftRPM = (spd / 3.6f) / TCIRC * 60f;
            float loadAdd = gear == 2 ? accel * 110f + 50f : accel * 55f; // lower load-add than D8645 -- newer valve body, less strain reads through
            baseRPM = (outShaftRPM * FINAL * ratio + loadAdd) * kdRpmBoost;
            if (bkPd > 0.08f) baseRPM = Mathf.Max(IDLE, baseRPM - bkPd * 260f);

            if (hillMode && gear <= 3)
            {
                float upSpd = gear == 2 ? NXT_GEAR2_UPSHIFT_SPD : NXT_GEAR3_UPSHIFT_SPD;
                float gearProgress = Mathf.Clamp01(spd / Mathf.Max(1f, upSpd));
                float stallZone     = Mathf.Clamp01((gearProgress - 0.7f) / 0.3f);
                baseRPM = Mathf.Lerp(baseRPM, GOV, stallZone * 0.5f);
            }

            baseRPM = Mathf.Clamp(baseRPM, IDLE, GOV);
        }

        // ── Coast: real 48V CRU function -- off-throttle, no brake, the
        // engine decouples from the driveline and drops toward idle while
        // road speed holds, instead of engine braking dragging RPM along
        // with road speed. Only when Eco/hybrid is on; with it off this
        // behaves like a normal automatic (engine RPM tracks the gear ratio
        // the whole time, same as every other TX).
        bool coasting = economyMode && accel < 0.02f && bkPd < 0.02f && spd > 5f && gear > 0;
        float coastTarget = coasting ? 1f : 0f;
        nxt_coastRpmBlend += (coastTarget - nxt_coastRpmBlend) * (coastTarget > nxt_coastRpmBlend ? 0.02f : 0.03f);
        if (nxt_coastRpmBlend > 0.001f)
            baseRPM = Mathf.Lerp(baseRPM, IDLE, nxt_coastRpmBlend);

        return baseRPM;
    }


    // ═════════════════════════════════════════════════════════════════════════
    //  GEAR HELPERS — BAE / H40 / H50
    // ═════════════════════════════════════════════════════════════════════════
    // [FIX] Rebuilt to actually match DoBAEDSP's own stop-start engage point
    // (spd >= 10kph AND accel pressed) instead of a flat 950+ floor that
    // never went below ~950rpm regardless of speed. Previously this fed the
    // REAL host engine core (via the shared `rpm` global, independent of
    // DoBAEDSP's own overlay) a nonzero rev target the instant you touched
    // the pedal even at a dead stop — the host engine would start revving
    // before the genset was even supposed to be engaged. Now it returns
    // plain IDLE whenever the genset isn't engaged, same threshold DoBAEDSP
    // uses, so the two systems agree instead of running two different
    // stop-start models against each other.
    private float CalcBAERPM()
    {
        bool engaged = spd >= 10f && accel > 0.03f;
        if (!engaged) return IDLE;
        float loadTarget = 950f + accel * 380f + (spd > 5f ? 20f : 0f);
        if (bkPd > 0.12f && spd > 3f) loadTarget = Mathf.Min(loadTarget, 850f);
        return loadTarget;
    }
    // [FIX] Was speed/accel-based -- gear dropped to 0 (== Neutral, to the
    // fast-idle/RPM logic) just from sitting stopped in Drive. Series
    // hybrids have no discrete gears either; track the actual selector
    // (isNeutral) instead, same fix as eGen Flex/electric above.
    private void DoBAERange() { gear = isNeutral ? 0 : 1; }

    // ── BAE HDS 300 — bigger APU, same series-hybrid range-extender logic ─────
    // [FIX] Same engage-threshold rebuild as CalcBAERPM above.
    private float CalcHDS300RPM()
    {
        bool engaged = spd >= 10f && accel > 0.03f;
        if (!engaged) return IDLE;
        float loadTarget = 1500f + accel * 430f + (spd > 5f ? 24f : 0f);
        if (bkPd > 0.12f && spd > 3f) loadTarget = Mathf.Min(loadTarget, 880f);
        return loadTarget;
    }
    private void DoHDS300Range() { gear = isNeutral ? 0 : 1; } // [FIX] see DoBAERange() comment -- same bug, same fix

    // ── BAE Hybrid Gen3 — same series-hybrid range-extender logic ─────────────
    private float CalcBAEGen3RPM()
    {
        float loadTarget = 1000f + accel * 400f + (spd > 5f ? 20f : 0f);
        if (bkPd > 0.12f && spd > 3f) loadTarget = Mathf.Min(loadTarget, 860f);
        return loadTarget;
    }
    private void DoBAEGen3Range() { gear = isNeutral ? 0 : 1; } // [FIX] see DoBAERange() comment -- same bug, same fix

    // ── CONTINUOUS EVT model — shared by H40/H50 RPM calcs ────────────────────
    // v3 correction: no discrete modes, no rev cycles. The EVT holds the
    // engine on a single continuous curve that rises gently with road speed
    // (and a little with pedal). KICKDOWN is the event: engine climbs to a
    // held continuous rev, which itself creeps slowly upward the longer
    // kickdown stays engaged. Both directions are slewed — engaging ramps
    // you up gently, releasing settles you gently back onto the correct
    // cruise RPM. Never a step.
    private float epv2_rpmSm    = 0f;   // internally slewed RPM (the gentle part)
    private float epv2_kdCreep  = 0f;   // 0→1 slow creep while kickdown held

    private float EvtContinuousRPM(float dt, float cruiseBase, float kdBase, float kdCreepSpan, float rpmCap)
    {
        bool kd = kickdownKey;

        // Kickdown creep: rises over ~9s of held kickdown, drains a bit
        // faster on release so a re-stab doesn't start from the ceiling.
        epv2_kdCreep = kd
            ? Mathf.Min(1f, epv2_kdCreep + dt / 9f)
            : Mathf.Max(0f, epv2_kdCreep - dt / 5f);

        float target = kd ? kdBase + epv2_kdCreep * kdCreepSpan
                          : cruiseBase;
        target = Mathf.Min(target, rpmCap);

        // Gentle slew both ways — up a touch faster than down, but neither
        // is a snap. (~230 RPM/s up, ~150 RPM/s down.)
        if (epv2_rpmSm <= 0f) epv2_rpmSm = target;   // first-tick seed
        float rate = target > epv2_rpmSm ? 230f : 150f;
        epv2_rpmSm = Mathf.MoveTowards(epv2_rpmSm, target, rate * dt);
        return epv2_rpmSm;
    }

    private float CalcH40EPRPM(float dt)
    {
        if (gear == 0) return IDLE;
        // [FIXED — neutral/drive RPM swap] The flat "+1020f" floor never
        // relaxed at a dead stop: Drive sat at ~1020rpm even fully stationary
        // with no throttle, which is ABOVE Neutral's fast-idle target
        // (IDLE*1.55, ~930-1085rpm depending on engine) -- so Drive-at-rest
        // read as the "revved" state and Neutral's real fast idle read as
        // the calmer one, backwards from what riders expect (Neutral+park
        // brake = elevated fast idle; Drive at a stop = plain idle). restGate
        // pulls the floor down to genuine IDLE right at a dead stop with no
        // pedal, and lets go fast as either speed or throttle appears, so
        // normal driving is completely unaffected.
        float restGate = Mathf.Clamp01(1f - spd / 4f) * Mathf.Clamp01(1f - accel / 0.04f);
        float cruise = Mathf.Lerp(1020f, IDLE, restGate) + (spd / MAX_SPD) * 560f + accel * 160f;
        // Hill Mode: H40EP is single-speed (no gears to extend/stall like
        // the DIWA boxes get), so the only way to convey "working harder
        // against grade" here is RPM itself sitting higher for the same
        // speed/throttle -- same effect a real EVT hybrid gets from its
        // control unit holding a higher operating point under load.
        float hillBoost = hillMode ? (spd / MAX_SPD) * 220f + accel * 130f : 0f;
        // [FIXED — the "cut off when slowing down" bug] coastRegen/brakeRegen
        // were hard booleans: the instant spd crossed 6kph while coasting
        // (or bkPd crossed 0.05 while braking), regenBoost snapped on/off by
        // up to ~140rpm in a single frame -- a real, audible discontinuity
        // in rpmTgt that read as the engine RPM "resetting" mid-deceleration.
        // Replaced with continuous gates: both fade in/out smoothly across a
        // couple kph / a small bkPd range instead of switching at an exact
        // threshold, and the two blend by whichever is stronger instead of
        // a hard either/or branch, so there's no seam between them either.
        float coastGate = Mathf.Clamp01((spd - 5f) / 2f) * Mathf.Clamp01(1f - accel / 0.02f);
        float brakeGate = Mathf.Clamp01((spd - 0f) / 2f) * Mathf.Clamp01((bkPd - 0.02f) / 0.06f);
        float coastAmt  = 140f * Mathf.Clamp01(spd / 20f) * coastGate;
        float brakeAmt  = (240f + bkPd * 300f) * Mathf.Clamp01(spd / 15f) * brakeGate;
        float regenBoost = Mathf.Max(coastAmt, brakeAmt);
        float rpmOut = EvtContinuousRPM(dt, cruise + hillBoost + regenBoost, 1720f, 160f, 1900f);
        float flutter = Mathf.Sin(Time.time / 0.7f) * 6f + Mathf.Sin(Time.time / 0.29f) * 3f;
        return Mathf.Clamp(rpmOut + flutter, IDLE, 1900f);
    }
    // [ADD -- per instruction] eGen Flex's own RPM curve. Same restGate/
    // regen-blend shape as H40EP/H50EP (they share the real single-speed
    // parallel-hybrid architecture -- see the DoH4xDSP header comment), but
    // deliberately punchier: taller speed-linked ceiling and a MUCH harder
    // per-throttle pull (accel*420 vs H40EP's accel*160), plus a genuinely
    // faster slew rate under throttle. This is the "badass" aggressive
    // rev-gain feel the old CalcVoithRPM fallback happened to give it by
    // accident -- rebuilt here as eGen Flex's real own curve instead of
    // borrowed multi-gear-automatic math. Real basis: disconnect clutch +
    // LTO pack support a harder, more immediate torque response than
    // H40EP/H50EP's NiMH/Li-ion setup, so it should genuinely pull harder,
    // not just borrow a different transmission's math to sound that way.
    private float CalcEGenFlexRPM(float dt)
    {
        if (gear == 0) return IDLE;
        float restGate = Mathf.Clamp01(1f - spd / 4f) * Mathf.Clamp01(1f - accel / 0.04f);
        float cruise = Mathf.Lerp(1080f, IDLE, restGate) + (spd / MAX_SPD) * 640f + accel * 420f;
        float hillBoost = hillMode ? (spd / MAX_SPD) * 260f + accel * 170f : 0f;

        float coastGate = Mathf.Clamp01((spd - 5f) / 2f) * Mathf.Clamp01(1f - accel / 0.02f);
        float brakeGate = Mathf.Clamp01((spd - 0f) / 2f) * Mathf.Clamp01((bkPd - 0.02f) / 0.06f);
        float coastAmt  = 140f * Mathf.Clamp01(spd / 20f) * coastGate;
        float brakeAmt  = (240f + bkPd * 300f) * Mathf.Clamp01(spd / 15f) * brakeGate;
        float regenBoost = Mathf.Max(coastAmt, brakeAmt);

        float target = Mathf.Min(cruise + hillBoost + regenBoost, 2050f); // taller ceiling than H40EP's 1900
        if (egf_rpmSmooth <= 0f) egf_rpmSmooth = target; // first-tick seed
        // Genuinely fast, hard pull under throttle (vs H40EP's gentle
        // 230rpm/s) -- settles back down smoother, not twitchy at cruise.
        float rate = target > egf_rpmSmooth ? 1450f : 420f;
        egf_rpmSmooth = Mathf.MoveTowards(egf_rpmSmooth, target, rate * dt);

        float flutter = Mathf.Sin(Time.time / 0.7f) * 6f + Mathf.Sin(Time.time / 0.29f) * 3f;
        return Mathf.Clamp(egf_rpmSmooth + flutter, IDLE, 2050f);
    }

private bool  h4x_revTriggered = false; // Only allows one rev until throttle is released
private bool  h4x_highSpeedLocked = false; // Permanent rev flag
private float h4x_delayTimer = 0f;    // The 2-second wait after press
private bool  h4x_isRevving  = false; // Are we currently in the rev duration?
private bool  h4x_hasFired   = false; // Lock: only one rev per pedal-down
    private void DoH40EPGear(float dt)
    {
        // [FIX] Was speed/accel-based -- an eGen Flex/H40EP bus sitting at
        // a stop IN DRIVE reported gear 0, indistinguishable from actually
        // being shifted to Neutral, which wrongly triggered Fast Idle and
        // the neutral-revving RPM branches while identical Allison/Voith
        // buses stopped in Drive correctly held gear 1 and stayed quiet.
        // Tracks the real selector state now.
        gear = isNeutral ? 0 : 1;
        // [REMOVED] Two-mode swap logic — modeled as fully continuous now, no
        // range change anywhere in the speed band. Mode pinned for mirrors.
        h40Mode = 1; h40ModeTimer = 0f; h40DipTimer = 0f; h40DipAmount = 0f;
        // [REMOVED] Auto stop-start — these units idle at stops in real
        // service (agencies disable engine shutdown), so the ISL/ISB just
        // sits at idle in the dwell. Fields kept for external mirrors.
        h40StopStart = false;
        h40SSTimer   = 0f;
        h40_crankTransient = 0f;
    }
private float CalcH50EPRPM(float dt)
{
    if (gear == 0) return IDLE;

    // Same continuous EVT model as H40EP, slightly taller curve for the
    // 400hp/ISL-330 unit. The old scripted rev-stage / kickdown-step
    // machinery (h4x_isRevving, h4x_kdStep, h50EngineRevZone, ...) is
    // retired — kickdown is now the continuous held rev with slow creep,
    // handled inside EvtContinuousRPM.
    // [FIXED — same neutral/drive RPM swap as CalcH40EPRPM] see that
    // function's comment; identical restGate fix, same reasoning.
    float restGate = Mathf.Clamp01(1f - spd / 4f) * Mathf.Clamp01(1f - accel / 0.04f);
    float cruise = Mathf.Lerp(1090f, IDLE, restGate) + (spd / MAX_SPD) * 590f + accel * 170f;
    // Hill Mode: same reasoning as H40EP -- no gears here to extend/stall,
    // so grade shows up as RPM itself running higher for the same
    // speed/throttle instead.
    float hillBoost = hillMode ? (spd / MAX_SPD) * 230f + accel * 140f : 0f;
    // [ADD] Regen RPM hold — real parallel-hybrid behavior: the MG-B
    // generator loads the engine through the planetary set during coast/
    // braking, and Allison's control strategy holds RPM elevated (rather
    // than letting it fall to idle) so full generation capacity and torque
    // response stay immediately available. This was previously completely
    // missing — the regen audio layer in DoH4xDSP ran totally disconnected
    // from the actual RPM feeding the tach/engine tone.
    // [FIXED — same "cut off when slowing down" bug as CalcH40EPRPM] see
    // that function's comment; identical continuous-gate fix.
    float coastGate = Mathf.Clamp01((spd - 5f) / 2f) * Mathf.Clamp01(1f - accel / 0.02f);
    float brakeGate = Mathf.Clamp01((spd - 0f) / 2f) * Mathf.Clamp01((bkPd - 0.02f) / 0.06f);
    float coastAmt  = 150f * Mathf.Clamp01(spd / 20f) * coastGate;
    float brakeAmt  = (260f + bkPd * 320f) * Mathf.Clamp01(spd / 15f) * brakeGate;
    float regenBoost = Mathf.Max(coastAmt, brakeAmt);
    if (tx == "h50ep_gen5") regenBoost *= 1.25f; // deeper regen capture -> more generator load -> higher RPM hold
    return Mathf.Clamp(EvtContinuousRPM(dt, cruise + hillBoost + regenBoost, 1780f, 170f, 1950f), IDLE, 1950f);
}

// ═══════════════════════════════════════════════════════════════════════════
//  EP40 / EP50 — the ORIGINAL (2003-2010) generation of the exact same
//  Allison drive unit sold today as H40EP/H50EP. Confirmed via CPTDB's
//  Allison EP System page: "EP40/EP50 and H40 EP/H50 EP refer to the SAME
//  PRODUCTS, just renamed" -- H 40 EP was "Originally known as EP40
//  System", H 50 EP "EP50 System". Real specs are UNCHANGED across the
//  rename (280hp/910lb-ft and 330hp/1050lb-ft respectively) -- what
//  Allison's own 2010 refresh actually changed was "enhanced electronic
//  controls and improved component reliability," not the mechanical specs.
//  So these reuse H40EP/H50EP's real cruise/regen numbers verbatim
//  (CalcH40EPRPM/CalcH50EPRPM), and layer a slow RPM hunt/wander on top
//  that the later, better-controlled generation doesn't have -- modeling
//  the actual real difference (control-electronics refinement) instead of
//  inventing a fake power/torque gap that never existed.
//  Gated to EngineType.ISL only (see the engine-compat switch above) --
//  confirmed via New Flyer Xcelsior's own Wikipedia page: the XDE40
//  launched "with power from the Cummins ISL 280 ... or the Allison
//  EP-40 hybrid drive," an ISL-specific pairing from the EP-era.
// ═══════════════════════════════════════════════════════════════════════════
private float ep4LegacyHuntRpm;   // slow wander added on top of the real H40/H50 curve
private float ep4LegacyHuntTimer;
private float CalcEP40RPM(float dt)
{
    if (gear == 0) return IDLE;
    float baseRpm = CalcH40EPRPM(dt); // real specs unchanged -- same curve as the modern name
    return baseRpm + LegacyEPHuntOffset(dt);
}
private float CalcEP50RPM(float dt)
{
    if (gear == 0) return IDLE;
    float baseRpm = CalcH50EPRPM(dt);
    return baseRpm + LegacyEPHuntOffset(dt);
}
// Slow, low-amplitude RPM wander -- real basis: the pre-2010 generation's
// control electronics were less refined (Allison's own language: the 2010
// refresh brought "enhanced electronic controls and improved component
// reliability"), so the operating point doesn't sit as rock-steady as the
// modern H40EP/H50EP curve does. Deliberately slow (multi-second period,
// not audio-rate) and small (+/-18rpm) -- a wander, not a malfunction.
private float LegacyEPHuntOffset(float dt)
{
    ep4LegacyHuntTimer += dt;
    float wander = Mathf.Sin(ep4LegacyHuntTimer * 0.9f) * 12f + Mathf.Sin(ep4LegacyHuntTimer * 2.3f) * 6f;
    ep4LegacyHuntRpm += (wander - ep4LegacyHuntRpm) * dt * 1.5f;
    return ep4LegacyHuntRpm;
}

private double ph_x10_core;
double ph_x10_core2;
double ph_x10_core3;
double ph_x10_torque;
double ph_x10_exhaust;
double ph_x10_turbo;
double ph_x10_moan;
double ph_x10_whine;
double ph_x10_whine2;
double ph_x10_txmoan;
private float h4x_currentRevMaxTime = 4.0f;
private bool  h4x_isPermanentRev = false;
private int   h4x_kdStep = 0;       // 0=Idle, 1=3s, 2=5s, 3=7s
private float h4x_kdGapTimer = 0f;  // Tracks the pauses between roars
private bool  h4x_isKickdown = false;
private void DoH50EPGear(float dt)
{
    gear = isNeutral ? 0 : 1; // [FIX] see DoH40EPGear() comment -- same bug, same fix
    
    h40Mode = 1; 
    h40ModeTimer = 0f;
    h40DipTimer = 0f;
    
    // [REMOVED] Auto stop-start — engine idles at stops in real service,
    // it never shuts itself off. Fields kept for external mirrors.
    h40StopStart = false;
    h40SSTimer   = 0f;
    h40_crankTransient = 0f;
// ==========================================
    // FIXED: 2-SEC DELAY -> 4-8 SEC REV STATE MACHINE
    // ==========================================
    if (accel > 0.05f)
    {
        accelTimer += dt;
        h50RevCycle = (h50RevCycle + dt * 0.75f) % 1.0f;
        h50WhineLevel += (0.45f - h50WhineLevel) * 0.15f;
        h50MotorLevel += (0.55f - h50MotorLevel) * 0.15f;
        
        float targetAccelRev = accel * 0.85f; 
        h50EngineRevZone += (targetAccelRev - h50EngineRevZone) * 0.15f;

        if (spd >= 75f)
        {
            h4x_isPermanentRev = true;
            h50RevTime = 0.001f;
        }
        else if (kickdownKey) // KICKDOWN ACTIVE
        {
            h4x_isKickdown = true;
            h4x_isRevving = false;
            if (h4x_kdStep == 0) { h4x_kdStep = 1; h50RevTime = 0.001f; }
        }
        else
        {
            h4x_isPermanentRev = false;
            h4x_isKickdown = false;
            // 1. 2-Second Delay Phase
            if (!h4x_isRevving && !h4x_hasFired)
            {
                h4x_delayTimer += dt;
                if (h4x_delayTimer >= 35.0f)
                {
                    // 2. Transition to Rev Phase
                    h4x_isRevving = true;
                    h4x_hasFired = true;
                    h50RevTime = 0.001f;
                    // Calculate duration: 4s min, 8s max
                    h4x_currentRevMaxTime = Mathf.Clamp(3.0f + (spd / MAX_SPD) * 4.0f, 4.0f, 6.0f);
                }
            }
        }
    }
    else if (bkPd > 0.08f)
    {
        accelTimer = 0f;
        h4x_isPermanentRev = false;
        h4x_delayTimer = 0f;
        h4x_isRevving = false;
        
        float motorFade = Mathf.Clamp01(spd / 45f);
        h50MotorLevel += (motorFade * 0.6f - h50MotorLevel) * 0.15f;
        h50WhineLevel += (motorFade * 0.4f - h50WhineLevel) * 0.15f;
        
        if (spd > 3f) h50EngineRevZone += (1.3f - h50EngineRevZone) * 0.20f; 
        else          h50EngineRevZone += (0.0f - h50EngineRevZone) * 0.25f;
    }
    else
    {
        accelTimer = 0f;
        h4x_isPermanentRev = false;
        h4x_delayTimer = 0f;
        h4x_isRevving = false;
        h4x_isKickdown = false;
        h4x_hasFired = false; // Reset the trigger for next press
        
        h50EngineRevZone += (0.0f - h50EngineRevZone) * 0.25f;
        h50MotorLevel    += (0.0f - h50MotorLevel) * 0.20f;
        h50WhineLevel    += (0.0f - h50WhineLevel) * 0.20f;
    }
    if (h4x_isKickdown)
    {
        h50RevTime += dt;
        float stepDur = (h4x_kdStep == 1) ? 3.0f : (h4x_kdStep == 2) ? 5.0f : 7.0f;
        
        if (h50RevTime >= stepDur)
        {
            h4x_kdStep++;
            h50RevTime = -((h4x_kdStep == 2) ? 2.0f : 3.0f); // 2s pause, then 3s pause
        }
        if (h4x_kdStep > 3) h4x_kdStep = 0;
    }
    else if (h4x_isRevving && !h4x_isPermanentRev)
    {
        h50RevTime += dt;
        if (h50RevTime >= h4x_currentRevMaxTime) { h4x_isRevving = false; h50RevTime = 0f; }
    }
    // ==========================================
    // TIMER ADVANCEMENT
    // ==========================================
    if (h4x_isRevving && !h4x_isPermanentRev)
    {
        h50RevTime += dt;
        if (h50RevTime >= h4x_currentRevMaxTime) 
        {
            h4x_isRevving = false;
            h50RevTime = 0f;
        }
    }
}
bool l9_wasStopped;
bool l9_wasUnder3;
private double ph_l9n_pump, ph_l9n_pumpTone;
    // ═════════════════════════════════════════════════════════════════════════
    //  OLD BUS CHARACTER
    // ═════════════════════════════════════════════════════════════════════════
    private void InitOldBusCharacter()
    {
        if (ob_initialized) return; ob_initialized = true;
        if (!oldBus) return;
        float r() => (float)(NextNoiseSample() * 0.5 + 0.5);
        ob_deepMoanAmt     = r() < 0.55f ? Mathf.Lerp(0.3f, 1f, r()) : 0f;
        ob_whineAmt        = r() < 0.45f ? Mathf.Lerp(0.25f, 1f, r()) : 0f;
        ob_shiftDelay      = r() < 0.50f ? Mathf.Lerp(0.15f, 0.6f, r()) : 0f;
        ob_airRushAmt      = Mathf.Lerp(0.5f, 1f, r());
        ob_roarAmt         = Mathf.Lerp(0.4f, 1f, r());
        ob_revHangAmt      = r() < 0.40f ? Mathf.Lerp(0.3f, 1f, r()) : 0f;
        ob_rattleAmt       = r() < 0.60f ? Mathf.Lerp(0.2f, 1f, r()) : 0f;
        ob_exhaustChuffAmt = r() < 0.35f ? Mathf.Lerp(0.3f, 1f, r()) : 0f;
        ob_beltSquealAmt   = r() < 0.30f ? Mathf.Lerp(0.4f, 1f, r()) : 0f;
        ob_doorWheezeAmt   = r() < 0.45f ? Mathf.Lerp(0.3f, 1f, r()) : 0f;
        ob_chuffNextAt = 8f + r() * 20f;
        ob_revHang       = r() < 0.40f;          // already had ob_revHangAmt below it
ob_delayedShifts = r() < 0.35f;
ob_shiftDelay    = ob_delayedShifts ? Mathf.Lerp(0.15f, 0.6f, r()) : 0f;
    }
    // Timers & Volume Envelopes
float l9_groogleTimer;
float l9_brakePuffVol;
float l9_coastMoanVol;
private float l9_coastGroanVol;
private bool l9_wasAccel;
// declare near ph_l9_engWhine:
private double ph_l9_metalRub1, ph_l9_metalRub2;
private double ph_l9n_bump1, ph_l9n_bump2;

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP HELPER — ISL9 (and the older ISL it was renamed from in 2010) +
    //  Allison B400R/B500R "old hydraulic valve body" wobble character.
    //
    //  Real-world basis: the ISL/ISL9/L9 are the same underlying engine
    //  architecture (ISL9 is just a 2010 SCR/DPF-updated rename of ISL, later
    //  renamed again to L9 in 2017/2016) — the block didn't change. What
    //  differs generation to generation is the Allison B400R/B500R TCM
    //  lockup calibration and, on real ISL/ISL9-era units, another decade-plus
    //  of accumulated valve-body/TC-clutch wear versus a newer L9-spec unit.
    //  Both effects show up as extra torque-converter slip/roar specifically
    //  in gear 1 (before lockup), and both wash out once you're in the
    //  locked-up top gears — which is why this layer decays to near-nothing
    //  by gear 5/6, converging with the cleaner newer-calibration L9 sound.
    //
    //  [UPDATED] Real-world note: present-day ISL9/ISL units don't carry a
    //  tonal Allison whine anymore — that layer has been removed. What's left
    //  is a plain mechanical roar (saturated low fundamental + broadband
    //  noise, no clean pitched partials), pushed louder, with gear 1 shortened
    //  to match how quickly these units actually chew through it in real life.
    //
    //  Pattern requested: ooooooooooooUUUUUUUAHHHHHHHHooooooooooooooooUOUOUOuoUOUOU
    //  AHHhhhooooooooooouuuuaououhuh — repeated each gear, shorter & quieter
    //  every gear after 1, basically silent by gear 5/6. Kickdown speeds the
    //  gear-1 buildup (handled in the gear-shift helpers above) and lets the
    //  roar carry into gear 2 while still revving (the carryover decay below).
    // ═════════════════════════════════════════════════════════════════════════
    private float  isl9wob_carryGear   = 0;
    private float  isl9wob_carryVol    = 0f;
    private double ph_isl9wob_wh1, ph_isl9wob_wh2, ph_isl9wob_wh3;
    private double ph_isl9wob_lfo;

    private void DoISL9AllisonWobble(ref double txSample, ref double engineSample,
                                      float engMul, double invSR)
    {
        bool isB500 = tx == "b500r";
        bool isOldGenCalibration = engineType == EngineType.ISL9 || engineType == EngineType.ISL;
        bool eligible = isOldGenCalibration && (IsAllison() || isB500);
        if (!eligible)
        {
            // Let any carried-over wobble decay cleanly even if combo changes mid-flight
            isl9wob_carryVol *= 0.96f;
            ph_isl9wob_wh1 = (ph_isl9wob_wh1 + 300.0 * invSR) % 1.0;
            return;
        }

        // Per-gear envelope length: gear 1 holds the "ooo-UUU-AHH" swell, but
        // kept SHORT — real ISL9/ISL units chew through gear 1 fast, they
        // don't linger in it. Each subsequent gear is a shorter, quieter
        // repeat of the same shape.
        float gearDecay   = Mathf.Pow(0.62f, Mathf.Max(0, gear - 1));
        float kdSpeedMul  = kickdownKey ? 1.8f : 1.0f;
        float swellLenSec = (gear <= 1 ? 0.55f : Mathf.Max(0.18f, 0.45f * gearDecay)) / kdSpeedMul;
        // Position within the current gear's swell, derived from the existing
        // shift-cooldown/transient machinery rather than a brand-new timer.
        float sinceShift = Mathf.Clamp01(1f - (shiftCD / 0.65f));
        float prog = Mathf.Clamp01(sinceShift * (0.65f / Mathf.Max(0.1f, swellLenSec)));

        // ooooo (build) -> UUUU (peak drag) -> AHHH (release/rev) -> ooo (settle)
        float env;
        if (prog < 0.30f)      env = Mathf.SmoothStep(0.25f, 0.75f, prog / 0.30f);
        else if (prog < 0.60f) env = Mathf.SmoothStep(0.75f, 1.00f, (prog - 0.30f) / 0.30f);
        else if (prog < 0.85f) env = Mathf.SmoothStep(1.00f, 0.55f, (prog - 0.60f) / 0.25f);
        else                   env = Mathf.SmoothStep(0.55f, 0.20f, (prog - 0.85f) / 0.15f);

        float gearVol = (gear >= 8) ? env * gearDecay * 0.25f : env * gearDecay; // "quiet by gear 5/6"
        // ISL is the older of the two calibrations/units (ISL9 was the 2010
        // SCR/DPF rename of it), so give it a touch more wear-roar than ISL9.
        float genWearMul = (engineType == EngineType.ISL) ? 1.15f : 1.0f;
        // [ROAR PASS] Real-world present-day ISL9/ISL units don't carry the
        // tonal Allison whine anymore — pushed the whole layer louder to
        // compensate now that it's roar, not whine, doing the work.
        float targetVol = (gear > 0 && accel > 0.04f) ? gearVol * 0.34f * genWearMul * engMul : 0f;
        // Kickdown lets gear-1 roar carry into gear 2 while still revving.
        if (kickdownKey && gear == 2 && isl9wob_carryGear == 1) targetVol = Mathf.Max(targetVol, isl9wob_carryVol);
        isl9wob_carryGear = gear;

        float tau = targetVol > isl9wob_carryVol ? 0.05f : 0.012f;
        isl9wob_carryVol += (targetVol - isl9wob_carryVol) * tau;

        if (isl9wob_carryVol > 0.0006f)
        {
            // Slow EP50-ish vibrato wobble still shapes the roar's intensity.
            float wobbleLfoHz = 3.2f + (kickdownKey ? 2.0f : 0f);
            double wobbleLfo  = Math.Sin(2.0 * Math.PI * ph_isl9wob_lfo);
            float  wobbleDepth = 0.18f + 0.10f * (float)wobbleLfo;

            // [ROAR, no whine] Low, growl-register fundamental driven hard into
            // tanh saturation + broadband noise — a gruff mechanical roar
            // rather than a clean pitched mesh whine (that whine tone has
            // been dropped entirely per the real-world note).
            double roarHz = (Mathf.Max(rpm, IDLE) / 60.0) * 9.0 * (1.0 + wobbleDepth * 0.05);
            double r1 = Math.Sin(2.0 * Math.PI * ph_isl9wob_wh1);
            double r2 = Math.Sin(2.0 * Math.PI * ph_isl9wob_wh2) * 0.55;
            double r3 = Math.Sin(2.0 * Math.PI * ph_isl9wob_wh3) * 0.30;
            double roarFund = r1 + r2 + r3;
            double roarBody = Math.Tanh(roarFund * (1.8 + wobbleDepth)) ;

            double wobbled = roarBody * (1.0 + wobbleDepth) + noise_lp * 0.45 * (1.0 + wobbleDepth * 0.4);
            txSample     += wobbled * isl9wob_carryVol;
            // A little of the vibrato bleeds into the engine block itself —
            // this is what reads as the bus "revving and vibrating" through it.
            engineSample += wobbleLfo * isl9wob_carryVol * 0.35;

            ph_isl9wob_wh1 = (ph_isl9wob_wh1 + roarHz        * invSR) % 1.0;
            ph_isl9wob_wh2 = (ph_isl9wob_wh2 + roarHz * 1.97 * invSR) % 1.0;
            ph_isl9wob_wh3 = (ph_isl9wob_wh3 + roarHz * 3.01 * invSR) % 1.0;
            ph_isl9wob_lfo = (ph_isl9wob_lfo + wobbleLfoHz     * invSR) % 1.0;
        }
        else
        {
            ph_isl9wob_wh1 = (ph_isl9wob_wh1 + 300.0 * invSR) % 1.0;
            ph_isl9wob_lfo = (ph_isl9wob_lfo + 3.2    * invSR) % 1.0;
        }
    }
    // ═════════════════════════════════════════════════════════════════════════
//  DSP HELPER — Allison B400R (shared by L9N and L9)
//  Replaces the old flat 2-osc "hz*4.5" whine with the same gear-aware,
//  detuned 3-osc bank + TC-slip-noise treatment the B500R/B3400xFE/ZF
//  siblings already get. 42 teeth — between B3400xFE (44, lighter/cleaner)
//  and B500R (48, cleaner casting) — B400R is the "baseline" heavy-duty
//  unit so it sits a little grittier/louder than both.
// ═════════════════════════════════════════════════════════════════════════

// ═════════════════════════════════════════════════════════════════════════
//  DSP HELPER — Allison B400R (shared by L9N and L9)
//
//  IRL basis (per CTA/Chicago Transit forum reports on actual B400R-
//  equipped New Flyers, plus Allison mechanic/owner threads): the whine
//  people hear on a B400R is the transmission's hydraulic charging pump,
//  NOT gear-tooth mesh. Two things fall out of that:
//    1. It should sound like a low, dense, mechanical whir/moan — not a
//       bright detuned chord like Voith/ZF's straight-cut gear whine.
//    2. It's load/pressure driven, not idle-driven — the CTA report is
//       specifically "when the driver accelerates hard," and it's absent
//       on the ZF-equipped sister buses entirely. So: nothing at idle or
//       light-throttle low gears, and it builds into a low, heavy presence
//       specifically under sustained load — climbing through gears 4/5/6,
//       while cruising loaded, and under the retarder/braking (pump
//       pressure spikes there too) — exactly what you asked for.
//
//  DSP: one deep fundamental + a sub-octave partner, engine-RPM-tracked
//  (it's a pump, driven off the input shaft) but pitched low and gated
//  almost entirely off until gear 4+, cruise load, or braking.
// ═════════════════════════════════════════════════════════════════════════




























private double ph_b400_wh1, ph_b400_wh2, ph_b400_lfo;
private double ph_b400_tc1, ph_b400_tc2;
private float  b400_whineHzSmooth = 0f;
private float  b400_whineVolSmooth = 0f;
private float  b400_tcNoiseSmooth = 0f;
private void DoB500RDSP(ref double txSample, float rn, float ld, float hz,
                         float outRPM, float engMul, double noiseHp, double noiseClt, double invSR)
{
    b5w_runTimeSec += (float)invSR;
    float coldFactor = Mathf.Clamp01(1f - b5w_runTimeSec / 230f); // genuinely warms slower — bigger fluid volume (450 ghp/336kW vs B400R's 295 ghp/220kW)

    // ── Pump whine — B500R's pump/converter handles ~50% more torque
    //    capacity than B400R's (336kW vs 220kW, per Allison World
    //    Transmission spec sheets), and it's a bigger physical pump moving
    //    more fluid volume. Real result: lower pitch floor, louder, duller
    //    (less "whiny," more "whirr") — genuinely distinct from B400R's
    //    higher/brighter pump now, not the same curve with a different name.
    float pumpHz = 62f + rpm * 0.095f;
    float speedAtten = 1f - Mathf.Clamp01((spd - 8f) / 65f);
    // [TONED DOWN] ~25% quieter, smoothing slowed slightly for a softer blend.
    float pumpTarget = (0.027f + coldFactor * 0.024f) * Mathf.Lerp(0.32f, 1f, speedAtten);
    b5w_pumpVolSmooth += (pumpTarget - b5w_pumpVolSmooth) * (pumpTarget > b5w_pumpVolSmooth ? 0.0035f : 0.0018f);

    if (b5w_pumpVolSmooth > 0.003f)
    {
        double p1 = Math.Sin(2.0 * Math.PI * ph_b5w_pump1);
        double p2 = Math.Sin(2.0 * Math.PI * ph_b5w_pump2);
        double pumpTone = p1 * 1.0 + p2 * 0.34;
        txSample += pumpTone * b5w_pumpVolSmooth * engMul + noise_lp * b5w_pumpVolSmooth * 0.30 * engMul;
    }
    ph_b5w_pump1 = (ph_b5w_pump1 + (double)pumpHz          * invSR) % 1.0;
    ph_b5w_pump2 = (ph_b5w_pump2 + (double)pumpHz * 1.008   * invSR) % 1.0;

    // ── Under-load gear whine — real 48-tooth casting (vs B400R's 38),
    //    "cleaner casting" per the file's original design intent: more pure
    //    tonal content, less noise mixed in, than B400R's grittier mesh. ────
    // [RETUNED — same reference-recording finding as B400R] Compressed
    // pitch-vs-RPM sensitivity and inharmonic partials, same reasoning as
    // the B400R fix directly above DoB500RDSP's sibling function — see that
    // comment for the full spectral-analysis basis. B500R's cleaner-casting
    // character is kept: still fewer/quieter noise-mixed partials than
    // B400R's grittier version.
    const float B5_WHINE_BASE_HZ = 280f;
    const float B5_WHINE_REF_RPM = 1700f;
    const float B5_WHINE_RPM_EXP = 0.32f;
    float b5RpmRatio = Mathf.Max(0.15f, rpm / B5_WHINE_REF_RPM);
    float accHzTarget = B5_WHINE_BASE_HZ * Mathf.Pow(b5RpmRatio, B5_WHINE_RPM_EXP);
    b5w_accHzSmooth += (accHzTarget - b5w_accHzSmooth) * (accHzTarget > b5w_accHzSmooth ? 0.006f : 0.003f);
    if (b5w_accHzSmooth < 2f) b5w_accHzSmooth = 0f;

    float accGearGain = gear <= 1 ? 1.00f : gear == 2 ? 0.78f : gear == 3 ? 0.58f : gear == 4 ? 0.38f : gear == 5 ? 0.24f : 0.15f;
    // [FIX] Was Clamp01(ld*1.15)*gain -- zero throttle meant literally zero
    // whine, gated on ACCELERATOR position with no floor at all. Real gear
    // mesh/pump whine tracks transmitted TORQUE (see the gear-mesh-whine
    // research above), which never truly bottoms out to nothing while the
    // transmission is turning -- coasting still has driveline load from
    // engine braking/rolling resistance. Most audible exactly at gear
    // 1-2 since that's where accGearGain (and so the amount being lost)
    // is highest -- matches the "everything goes quiet right on a low-gear
    // downshift" symptom, since downshifts into 1/2 usually happen right
    // as the driver lifts off.
    float accTgt = Mathf.Lerp(0.15f, 1.0f, Mathf.Clamp01(ld * 1.15f)) * accGearGain;
    // [BETTER BLENDED] Slowed for smoother gear-to-gear transitions.
    b5w_accWhineSmooth += (accTgt - b5w_accWhineSmooth) * (accTgt > b5w_accWhineSmooth ? 0.005f : 0.0028f);

    if (b5w_accWhineSmooth > 0.004f && b5w_accHzSmooth > 5f)
    {
        double a1 = Math.Sin(2.0 * Math.PI * ph_b5w_accWh1);
        double aSub = Math.Sin(2.0 * Math.PI * ph_b5w_accWhSub);
        double aThird = Math.Sin(2.0 * Math.PI * ph_b5w_accWhThird);
        double sing = a1 * 1.0 + aSub * 0.36 + aThird * 0.20; // cleaner casting -> lighter partials than B400R's 0.42/0.24
        // [TONED DOWN] ~25% quieter.
        float accVol = (0.015f + rn * 0.022f) * b5w_accWhineSmooth * engMul;
        txSample += sing * accVol * 0.68 + noise_lp * accVol * 0.12;
    }
    ph_b5w_accWh1     = (ph_b5w_accWh1     + b5w_accHzSmooth        * invSR) % 1.0;
    // [RETUNED] Same 1.22x/1.48x inharmonic ratios as B400R -- the reference
    // recording didn't show a distinct B400R-vs-B500R difference in the
    // whine's relative peak spacing, just in overall texture/noise mix.
    ph_b5w_accWhSub   = (ph_b5w_accWhSub   + b5w_accHzSmooth * 1.22 * invSR) % 1.0;
    ph_b5w_accWhThird = (ph_b5w_accWhThird + b5w_accHzSmooth * 1.48 * invSR) % 1.0;

    // ── TC slip whoosh (kept, same shape as before) ─────────────────────────
    // [SYNCED] Now driven by the same b4r_tccBlend the RPM calc uses, so
    // the audible converter whoosh fades in step with the actual lock
    // transition instead of snapping off a frame independent of it.
    float b5OpenFrac = gear > 0 ? (1f - b4r_tccBlend) : 0f;
    float b5SlipLoad = b5OpenFrac * (gear == 1 ? ld : ld * 0.45f);
    b500_tcNoiseSmooth += (b5SlipLoad - b500_tcNoiseSmooth) * 0.005f;
    if (b500_tcNoiseSmooth > 0.003f && spd > 0.5f)
    {
        float tcVol = (0.048f + b500_tcNoiseSmooth * 0.095f) * engMul;
        txSample += noiseClt * tcVol;
        double tcOutHz = outRPM * 0.065 + 10.0;
        txSample += Math.Sin(2.0 * Math.PI * ph_b5_tc1) * (tcVol * 0.40);
        txSample += Math.Sin(2.0 * Math.PI * ph_b5_tc2) * (tcVol * 0.20);
        ph_b5_tc1 = (ph_b5_tc1 + tcOutHz       * invSR) % 1.0;
        ph_b5_tc2 = (ph_b5_tc2 + tcOutHz * 2.0 * invSR) % 1.0;
    }
    else
    {
        b500_tcNoiseSmooth = Mathf.Max(0f, b500_tcNoiseSmooth - (float)invSR * 3f);
        double tcIdleHz = 10.0 + outRPM * 0.065;
        ph_b5_tc1 = (ph_b5_tc1 + tcIdleHz       * invSR) % 1.0;
        ph_b5_tc2 = (ph_b5_tc2 + tcIdleHz * 2.0 * invSR) % 1.0;
    }

    // ── Pump order tick (kept, cosmetic low-level pump-order tone) ──────────
    double b5PumpHz  = (rpm / 60.0) * 0.45;
    float  b5PumpVol = (0.022f + rn * 0.011f) * engMul;
    txSample += Math.Sin(2.0 * Math.PI * ph_b5_pump) * b5PumpVol;
    ph_b5_pump = (ph_b5_pump + b5PumpHz * invSR) % 1.0;

    // ── Howl — B500R's own dedicated fields now (was sharing ph_b5_out with
    //    B400R and B3400xFE). This is the "cruise ship horn" end of the real
    //    Allison forum description: bigger 60ft artic body resonates more,
    //    so it sits lower, louder, with a longer/heavier tremolo and slower
    //    settle than either sibling. ─────────────────────────────────────────
    if (gear >= 7 && spd > 99f)
    {
        double b500OutHz    = 128.0 + outRPM * 0.48;
        double b500HowlTrem = 0.65 + 0.35 * Math.Sin(2.0 * Math.PI * ph_b500_howlTrem);
        bool highAccel = ld > 0.8f;
        float b500HowlDecayTarget = highAccel ? 0.5f : 1.0f;
        b500HowlAccelDecay += (b500HowlDecayTarget - b500HowlAccelDecay) * (float)invSR * (highAccel ? 0.12f : 0.35f);
        float  b500OutVol   = (0.070f + (spd / MAX_SPD) * 0.074f + ld * 0.024f) * engMul * b500HowlAccelDecay;
        double b500HowlTone = Math.Sin(2.0 * Math.PI * ph_b500_out) * 1.0 + Math.Sin(2.0 * Math.PI * ph_b500_out * 0.5) * 0.72;
        txSample += b500HowlTone * b500OutVol * b500HowlTrem;
        ph_b500_out     = (ph_b500_out     + b500OutHz * invSR) % 1.0;
        ph_b500_howlTrem = (ph_b500_howlTrem + 1.7      * invSR) % 1.0;
    }
        else
    {
            ph_b500_out      = (ph_b500_out      + (128.0 + outRPM * 0.48) * invSR) % 1.0;
            ph_b500_howlTrem = (ph_b500_howlTrem + 1.7           * invSR) % 1.0;
    }

    // ── Shift thud / lockup thud — B500R's own fields, kept heavier than
    //    B400R (bigger clutch packs, artic-duty unit) with a longer decay tail.
    if (b500_shiftThud > 0f)
    {
        txSample += (Math.Sin(2.0 * Math.PI * ph_b5_thud1) * 0.34 + Math.Sin(2.0 * Math.PI * ph_b5_thud2) * 0.19) * b500_shiftThud * engMul;
        b500_shiftThud *= 0.9975f;
        if (b500_shiftThud < 0.005f) b500_shiftThud = 0f;
    }
    if (b500_lockupThud > 0f)
    {
        txSample += noiseHp * b500_lockupThud * 0.22 * engMul;
        txSample += Math.Sin(2.0 * Math.PI * ph_b5_wh2) * b500_lockupThud * 0.16 * engMul;
        b500_lockupThud *= 0.9948f;
        if (b500_lockupThud < 0.004f) b500_lockupThud = 0f;
    }
    ph_b5_thud1 = (ph_b5_thud1 + 32.0 * invSR) % 1.0; // lower-pitched thud than B400R's 44Hz — bigger casting
    ph_b5_thud2 = (ph_b5_thud2 + 58.0 * invSR) % 1.0;
    ph_b5_wh2   = (ph_b5_wh2   + b5w_accHzSmooth * 0.985 * invSR) % 1.0; // kept alive for lockup-thud partial above

    // ── NEW: retarder spit / tick — B500R does this too, per the same forum
    //    thread ("Allison B500R Transmission features the same thing!!"),
    //    just fed through slightly larger noise/hiss weighting since it's a
    //    bigger air volume being released on an artic unit. ────────────────
    DoAllisonRetarderSpit(ref txSample, ref b5w_retardZoneActive,
                          ref b5w_tickVol, ref b5w_spitVol,
                          ref ph_b5w_spitTick, ref ph_b5w_spitHiss, engMul, invSR);
}

// ═════════════════════════════════════════════════════════════════════════
//  B500R GEN 5 (2017+ "5th Generation Controls") — genuinely dedicated DSP,
//  NOT a reused DoB500RDSP call with a volume multiplier. Real research
//  basis (Allison's own 5th Gen documentation):
//    · Faster/more precise TCM hardware+software -- quicker response,
//      snappier thud decay, faster smoothing rates throughout.
//    · Two ADDITIONAL Vehicle Acceleration Control levels -- Gen 4's
//      continuous ld-based whine response is replaced with a genuinely
//      STEPPED/TIERED response (5 discrete levels instead of one smooth
//      curve), reflecting a more digitally-graduated, deliberate throttle
//      character rather than a simple analog ramp.
//    · Cleaner tone throughout -- fewer/lighter inharmonic partials than
//      Gen 4's whine, reflecting a more refined unit.
//    · Quieter overall -- every layer's base volume reduced vs Gen 4, not
//      applied as one global multiplier after the fact.
// ═════════════════════════════════════════════════════════════════════════
private void DoB500RGen5DSP(ref double txSample, float rn, float ld, float hz,
                             float outRPM, float engMul, double noiseHp, double noiseClt, double invSR)
{
    b5g5_runTimeSec += (float)invSR;
    float coldFactor = Mathf.Clamp01(1f - b5g5_runTimeSec / 180f); // warms faster than Gen 4's 230s -- newer thermal management

    // ── Pump whine — same physical scale as Gen 4 (same real transmission
    // family) but cleaner: single dominant partial instead of two, quieter.
    float pumpHz = 62f + rpm * 0.095f;
    float speedAtten = 1f - Mathf.Clamp01((spd - 8f) / 65f);
    float pumpTarget = (0.019f + coldFactor * 0.015f) * Mathf.Lerp(0.32f, 1f, speedAtten);
    b5g5_pumpVolSmooth += (pumpTarget - b5g5_pumpVolSmooth) * (pumpTarget > b5g5_pumpVolSmooth ? 0.0055f : 0.0030f); // faster than Gen 4's 0.0035/0.0018

    if (b5g5_pumpVolSmooth > 0.003f)
    {
        double p1 = Math.Sin(2.0 * Math.PI * ph_b5g5_pump1);
        double p2 = Math.Sin(2.0 * Math.PI * ph_b5g5_pump2);
        double pumpTone = p1 * 1.0 + p2 * 0.18; // was 0.34 -- cleaner, less beating
        txSample += pumpTone * b5g5_pumpVolSmooth * engMul + noise_lp * b5g5_pumpVolSmooth * 0.18 * engMul; // was 0.30
    }
    ph_b5g5_pump1 = (ph_b5g5_pump1 + (double)pumpHz         * invSR) % 1.0;
    ph_b5g5_pump2 = (ph_b5g5_pump2 + (double)pumpHz * 1.008 * invSR) % 1.0;

    // ── Under-load gear whine — STEPPED throttle response (5 discrete VAC
    // levels) instead of Gen 4's continuous ld curve. This is the real,
    // audible reflection of "two additional acceleration levels" -- the
    // whine now visits distinct plateaus as throttle deepens rather than
    // ramping smoothly, a genuinely different character, not just quieter.
    const float B5G5_WHINE_BASE_HZ = 280f;
    const float B5G5_WHINE_REF_RPM = 1700f;
    const float B5G5_WHINE_RPM_EXP = 0.32f;
    float b5g5RpmRatio = Mathf.Max(0.15f, rpm / B5G5_WHINE_REF_RPM);
    float accHzTarget = B5G5_WHINE_BASE_HZ * Mathf.Pow(b5g5RpmRatio, B5G5_WHINE_RPM_EXP);
    b5g5_accHzSmooth += (accHzTarget - b5g5_accHzSmooth) * (accHzTarget > b5g5_accHzSmooth ? 0.010f : 0.006f); // faster than Gen 4's 0.006/0.003
    if (b5g5_accHzSmooth < 2f) b5g5_accHzSmooth = 0f;

    float accGearGain = gear <= 1 ? 1.00f : gear == 2 ? 0.78f : gear == 3 ? 0.58f : gear == 4 ? 0.38f : gear == 5 ? 0.24f : 0.15f;
    // [NEW] 5-level VAC stepping -- quantizes ld into discrete plateaus
    // instead of reading it continuously.
    float ldStepped = Mathf.Floor(Mathf.Clamp01(ld) * 5f) / 5f;
    // [FIX] Same floor as Gen4's accTgt above -- ldStepped's bottom rung is
    // a literal 0 (zero throttle), which zeroed the whine completely same
    // as the unfloored Gen4 bug did.
    float accTgt = Mathf.Lerp(0.15f, 1.15f, ldStepped) * accGearGain;
    accTgt = Mathf.Clamp01(accTgt);
    b5g5_accWhineSmooth += (accTgt - b5g5_accWhineSmooth) * (accTgt > b5g5_accWhineSmooth ? 0.009f : 0.005f); // faster than Gen4's 0.005/0.0028

    if (b5g5_accWhineSmooth > 0.004f && b5g5_accHzSmooth > 5f)
    {
        double a1 = Math.Sin(2.0 * Math.PI * ph_b5g5_accWh1);
        double aSub = Math.Sin(2.0 * Math.PI * ph_b5g5_accWhSub);
        // [EDIT per instruction] Louder + more howl-like -- added a
        // sub-octave partial (aHowl, half-frequency) for sustained,
        // resonant low-end body, same "howl" treatment as B400R Gen 5's
        // whine above. Still smooth: pure sine, no extra grit.
        double aHowl = Math.Sin(2.0 * Math.PI * ph_b5g5_accWhHowl);
        double sing = a1 * 1.0 + aSub * 0.22 + aHowl * 0.40;
        // Louder overall (was 0.011/0.016, quieter than Gen4 -- now above
        // Gen4's own 0.015/0.022, per instruction).
        float accVol = (0.019f + rn * 0.026f) * b5g5_accWhineSmooth * engMul;
        txSample += sing * accVol * 0.72 + noise_lp * accVol * 0.06; // slightly less noise mix for smoothness
    }
    ph_b5g5_accWh1    = (ph_b5g5_accWh1    + b5g5_accHzSmooth        * invSR) % 1.0;
    ph_b5g5_accWhSub  = (ph_b5g5_accWhSub  + b5g5_accHzSmooth * 1.22 * invSR) % 1.0;
    ph_b5g5_accWhHowl = (ph_b5g5_accWhHowl + b5g5_accHzSmooth * 0.5  * invSR) % 1.0;

    // ── TC slip whoosh — tighter lockup than Gen 4 (newer torsional
    // damper), so less time spent slipping overall, quieter when it does.
    bool  b5g5Slipping = gear > 0 && !AL_LOCK_B500[Mathf.Min(gear, AL_LOCK_B500.Length - 1)];
    float b5g5SlipLoad = b5g5Slipping ? (gear == 1 ? ld * 0.85f : ld * 0.35f) : 0f;
    b5g5_tcNoiseSmooth += (b5g5SlipLoad - b5g5_tcNoiseSmooth) * 0.007f; // faster than Gen4's 0.005
    if (b5g5_tcNoiseSmooth > 0.003f && spd > 0.5f)
    {
        float tcVol = (0.036f + b5g5_tcNoiseSmooth * 0.075f) * engMul; // was 0.048/0.095
        txSample += noiseClt * tcVol;
        double tcOutHz = outRPM * 0.065 + 10.0;
        txSample += Math.Sin(2.0 * Math.PI * ph_b5g5_tc1) * (tcVol * 0.40);
        ph_b5g5_tc1 = (ph_b5g5_tc1 + tcOutHz       * invSR) % 1.0;
        ph_b5g5_tc2 = (ph_b5g5_tc2 + tcOutHz * 2.0 * invSR) % 1.0;
    }
    else
    {
        b5g5_tcNoiseSmooth = Mathf.Max(0f, b5g5_tcNoiseSmooth - (float)invSR * 4f); // decays faster than Gen4's 3f
        double tcIdleHz = 10.0 + outRPM * 0.065;
        ph_b5g5_tc1 = (ph_b5g5_tc1 + tcIdleHz       * invSR) % 1.0;
        ph_b5g5_tc2 = (ph_b5g5_tc2 + tcIdleHz * 2.0 * invSR) % 1.0;
    }

    // ── Pump order tick — kept, quieter.
    double b5g5PumpHz  = (rpm / 60.0) * 0.45;
    float  b5g5PumpVol = (0.016f + rn * 0.008f) * engMul; // was 0.022/0.011
    txSample += Math.Sin(2.0 * Math.PI * ph_b5g5_pumpOrder) * b5g5PumpVol;
    ph_b5g5_pumpOrder = (ph_b5g5_pumpOrder + b5g5PumpHz * invSR) % 1.0;

    // ── Howl — same top-gear resonance concept, quieter/tighter decay.
    if (gear >= 7 && spd > 99f)
    {
        double b5g5OutHz    = 128.0 + outRPM * 0.48;
        double b5g5HowlTrem = 0.65 + 0.35 * Math.Sin(2.0 * Math.PI * ph_b5g5_howlTrem);
        bool highAccel = ld > 0.8f;
        float b5g5HowlDecayTarget = highAccel ? 0.5f : 1.0f;
        b5g5_howlAccelDecay += (b5g5HowlDecayTarget - b5g5_howlAccelDecay) * (float)invSR * (highAccel ? 0.16f : 0.45f); // faster than Gen4's 0.12/0.35
        float b5g5OutVol   = (0.052f + (spd / MAX_SPD) * 0.055f + ld * 0.018f) * engMul * b5g5_howlAccelDecay; // was 0.070/0.074/0.024
        double b5g5HowlTone = Math.Sin(2.0 * Math.PI * ph_b5g5_out) * 1.0 + Math.Sin(2.0 * Math.PI * ph_b5g5_out * 0.5) * 0.55; // was 0.72
        txSample += b5g5HowlTone * b5g5OutVol * b5g5HowlTrem;
        ph_b5g5_out      = (ph_b5g5_out      + b5g5OutHz * invSR) % 1.0;
        ph_b5g5_howlTrem = (ph_b5g5_howlTrem + 1.7        * invSR) % 1.0;
    }
    else
    {
        ph_b5g5_out      = (ph_b5g5_out      + (128.0 + outRPM * 0.48) * invSR) % 1.0;
        ph_b5g5_howlTrem = (ph_b5g5_howlTrem + 1.7             * invSR) % 1.0;
    }

    // ── Shift thud / lockup thud — snappier decay than Gen 4 (quicker,
    // more precise TCM per the real spec), softer peak (smoother).
    if (b5g5_shiftThud > 0f)
    {
        txSample += (Math.Sin(2.0 * Math.PI * ph_b5g5_thud1) * 0.24 + Math.Sin(2.0 * Math.PI * ph_b5g5_thud2) * 0.13) * b5g5_shiftThud * engMul; // was 0.34/0.19
        b5g5_shiftThud *= 0.9955f; // decays faster than Gen4's 0.9975f
        if (b5g5_shiftThud < 0.005f) b5g5_shiftThud = 0f;
    }
    if (b5g5_lockupThud > 0f)
    {
        txSample += noiseHp * b5g5_lockupThud * 0.15 * engMul; // was 0.22
        txSample += Math.Sin(2.0 * Math.PI * ph_b5g5_wh2) * b5g5_lockupThud * 0.11 * engMul; // was 0.16
        b5g5_lockupThud *= 0.9930f; // was 0.9948f
        if (b5g5_lockupThud < 0.004f) b5g5_lockupThud = 0f;
    }
    ph_b5g5_thud1 = (ph_b5g5_thud1 + 32.0 * invSR) % 1.0;
    ph_b5g5_thud2 = (ph_b5g5_thud2 + 58.0 * invSR) % 1.0;
    ph_b5g5_wh2   = (ph_b5g5_wh2   + b5g5_accHzSmooth * 0.985 * invSR) % 1.0;

    // ── Retarder spit/tick — shared real mechanism (both generations
    // physically retard the same way), state kept fully separate though.
    DoAllisonRetarderSpit(ref txSample, ref b5g5_retardZoneActive,
                          ref b5g5_tickVol, ref b5g5_spitVol,
                          ref ph_b5g5_spitTick, ref ph_b5g5_spitHiss, engMul, invSR);
}

private bool   al_dblTapActive = false;
private float  al_dblTapTimer  = 0f;
private int    al_prevGearForTap = 0;
private double ph_al_dbltap;
private double ph_l9_coastMoan1, ph_l9_coastMoan2; 
private double ph_isl9_turboHunt;
// ═════════════════════════════════════════════════════════════════════════
//  DSP HELPER — Deep L9 diesel voice, SMOOTHED (shared primary layer for
//  L9N, pitched down under its own legacy CNG synth as a secondary).
//  Stripped vs. the true L9 branch: no groogle bubble, no stopping puff,
//  no clatter/grit noise, no hollow-square edge, no hoarse noise-mod on
//  the fundamental. Fewer, cleaner harmonics — a round low diesel tone,
//  not the raspy/worn L9 character.
// ═════════════════════════════════════════════════════════════════════════
private double ph_l9v_e1, ph_l9v_e2, ph_l9v_esub, ph_l9v_eex, ph_l9v_etb;

private void DoDeepL9VoiceDSP(ref double voiceSample, float hz, float rn, float ld,
                               float engMul, double invSR)
{
    // [TUNED — less deep, louder, per direction] Sub-oscillator (sEsub,
    // half-frequency -- the actual "depth") was carrying the heaviest
    // weight of anything in the stack (0.90, higher than the fundamental
    // itself at 0.68). That's what was reading as too deep. Weight cut
    // roughly in half and the fundamental/2nd raised to compensate, plus
    // the overall floor+swing raised for straightforward loudness.
    float eBase = engMul * (0.65f + rn * 0.55f) * (1.0f + rn * 0.55f);

    // Smooth core: just fundamental + a light 3rd, no noise-modulated
    // "hoarseMod", no hollow-square, no 5th harmonic edge.
    double pipeFundamental = Math.Sin(2.0 * Math.PI * ph_l9v_e1);
    double pipeHarmonic3   = Math.Sin(2.0 * Math.PI * (ph_l9v_e1 * 3.0)) * 0.18;
    double coreTone = Math.Tanh((pipeFundamental * 1.1) * 0.9); // gentle drive, not 1.5 — softer roll-off

    double sE1   = coreTone * 0.75 + pipeHarmonic3;
    double sE2   = Math.Sin(2.0 * Math.PI * ph_l9v_e2) * 0.6;       // pure sine, no 2.25x detune
    double sEsub = Math.Sin(2.0 * Math.PI * ph_l9v_esub);
    double sEex  = Math.Sin(2.0 * Math.PI * ph_l9v_eex) * 0.55;     // sine only, no sawtooth edge
    double sEtb  = Math.Sin(2.0 * Math.PI * ph_l9v_etb);

    // Soft, tonal turbo presence only — no grit/clatter noise layered on it.
    float turboVolL9 = rn > 0.05f ? (rn - 0.05f) * 0.09f * (0.3f + ld * 0.5f) * engMul : 0f;
    double turboWhistle = sEtb * turboVolL9;

    voiceSample =
        sE1   * eBase * 0.82   // was 0.68 -- carries more of the tone now
      + sE2   * eBase * 0.10   // was 0.06
      + sEsub * eBase * 0.42   // was 0.90 -- this was "the deep", cut roughly in half
      + sEex  * eBase * 0.30
      + turboWhistle;

    voiceSample = Math.Tanh(voiceSample * 0.85); // gentler final saturation, rounder tone

    // Coast groan kept — it's a smooth low moan, not grit/hiss, fits the brief.
    float coastTarget = (accel <= 0.01f && spd > 10f) ? 1.0f : 0f;
    l9_coastGroanVol += (coastTarget - l9_coastGroanVol) * (coastTarget > l9_coastGroanVol ? 0.012f : 0.0035f);
    if (l9_coastGroanVol > 0.001f)
    {
        double groanHz = 55.0 + (rn * 20.0);
        double moan1 = Math.Sin(2.0 * Math.PI * ph_l9_coastMoan1);
        double moan2 = Math.Sin(2.0 * Math.PI * ph_l9_coastMoan2);
        double groanTone = Math.Tanh((moan1 * 0.8 + moan2 * 0.5) * 1.5); // slightly gentler drive too
        voiceSample += groanTone * l9_coastGroanVol * engMul * 0.40f;
        ph_l9_coastMoan1 = (ph_l9_coastMoan1 + groanHz * invSR) % 1.0;
        ph_l9_coastMoan2 = (ph_l9_coastMoan2 + (groanHz * 1.03) * invSR) % 1.0;
    }

    ph_l9v_e1   = (ph_l9v_e1   + hz       * invSR) % 1.0;
    ph_l9v_e2   = (ph_l9v_e2   + hz * 2.0 * invSR) % 1.0;
    ph_l9v_esub = (ph_l9v_esub + hz * 0.5 * invSR) % 1.0;
    ph_l9v_eex  = (ph_l9v_eex  + hz * 1.5 * invSR) % 1.0;
    ph_l9v_etb  = (ph_l9v_etb  + (600.0 + rn * 2500.0) * invSR) % 1.0;
}

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP — Starter cranking + spaced air-system puffs (ignition sequence)
    //  Runs instead of the normal drivetrain tree while engineState==Cranking.
    //  Pitch/churn rate is flavored by IDLE/CYLINDERS so different engine
    //  families (L9N vs X10 vs ISL9 etc.) crank slightly differently without
    //  needing a fully bespoke synth per engine.
    // ═════════════════════════════════════════════════════════════════════════
    private double RenderCrankSample(double invSR)
    {
        // Starter-motor churn — high pitched, warbling.
        double churnHz    = 5.0 + (CYLINDERS / 6.0) * 1.5;   // ~5–7 Hz churn rate
        double crankPitch = 320.0 + (IDLE / 10.0);           // ~370–450 Hz depending on idle target
        double churnEnv   = 0.55 + 0.45 * Math.Sin(2.0 * Math.PI * ph_crank1);
        double crank = Math.Sin(2.0 * Math.PI * ph_crank2) * churnEnv * 0.16
                     + Math.Sin(2.0 * Math.PI * (ph_crank2 * 1.5 % 1.0)) * churnEnv * 0.07;
        ph_crank1 = (ph_crank1 + churnHz    * invSR) % 1.0;
        ph_crank2 = (ph_crank2 + crankPitch * invSR) % 1.0;

        // Spaced air-system puffs — "PFFT ... (1s) ... PFFT ... (1s) ..." —
        // a pneumatic pressure-release burst layered under the starter whine.
        double puff = RenderAirPuffLayer(invSR, 0.9f, 0.2f);

        return (crank + puff) * npcVolumeScale;
    }

    /// <summary>The spaced pneumatic-puff layer factored out of
    /// RenderCrankSample so the same "PFFT" burst can also be used AFTER
    /// cranking -- see the extended air-puff tail in
    /// BusAudioEngine.StartupSequence.cs, which calls this with a widening
    /// minInterval as the tail progresses (pressure building, puffs spacing
    /// out, then stopping). minInterval/jitterRange work exactly like the
    /// literal 0.9f/0.2f this replaced: next puff fires minInterval to
    /// minInterval+jitterRange seconds after the previous one.</summary>
    private double RenderAirPuffLayer(double invSR, float minInterval, float jitterRange)
    {
        _crankPuffTimer += (float)invSR;
        if (_crankPuffNextAt <= 0f)
            _crankPuffNextAt = minInterval + (float)(NextNoiseSample() * 0.5 + 0.5) * jitterRange;
        if (_crankPuffTimer >= _crankPuffNextAt)
        {
            _crankPuffTimer  = 0f;
            _crankPuffNextAt = minInterval + (float)(NextNoiseSample() * 0.5 + 0.5) * jitterRange;
            ph_crankPuff     = 0.0;
        }
        double puff = 0.0;
        if (ph_crankPuff < 0.18)
        {
            double puffEnv = 1.0 - ph_crankPuff / 0.18;
            puff = noise_hi * puffEnv * puffEnv * 0.22 + noise_lp * puffEnv * 0.10;
            ph_crankPuff += invSR;
        }
        return puff;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  AUDIO FILTER — ProcessAudio
    // ═════════════════════════════════════════════════════════════════════════
    public void ProcessAudio(float[] data, int channels, float dist)
    {
        ProcessAudioCore(data, channels, dist);
        MixBlinkerTick(data, channels); // turn-signal tick rides on top of whatever the engine just rendered
    }

    private void ProcessAudioCore(float[] data, int channels, float dist)
    {
        // [NEW] Smooth AC shutdown tail -- "when I turn off bus AC comes
        // off too (smoothly) not just abruptly". Root cause: this used to
        // hard-clear the ENTIRE buffer to silence the instant running/
        // batteryOn went false, which cut AC (and everything else) off
        // dead instantly -- DoUniversalACDSP's own internal smoothing never
        // got a chance to run at all once this early-return fired every
        // single callback. Now, for a couple seconds after the engine/
        // battery actually goes off, AC's own DSP keeps running (through
        // its normal release curve) while everything else stays silent, so
        // shutting off the ignition behaves the same as manually turning
        // AC off with the engine still running, instead of differently.
        bool poweredNow = batteryOn && running;
        if (!poweredNow)
        {
            if (_wasPoweredForACTail) _acShutdownTailTimer = AC_SHUTDOWN_TAIL_SEC;
            _wasPoweredForACTail = false;
            // Same reset as the isEngineRunning==false branch further down --
            // covers the case where power cuts (battery off / breakdown)
            // without ever passing back through that branch first.
            v6_wasEngineRunningV6 = false;
            hyb_wasEngineRunning  = false;
            elec_wasEngineRunning = false;
            _wasRunningForCatch = false;
            _alternatorTimer    = -1f;
            _extendedPuffTimer  = -1f;

            if (_acShutdownTailTimer > 0f)
            {
                double invSRTail = 1.0 / SR;
                _acShutdownTailTimer -= (float)(data.Length / (double)channels / SR);
                float acVolPersonalityTail = acHighEngLow ? 1.45f : 1.0f;
                for (int i = 0; i < data.Length; i += channels)
                {
                    double acSampleTail = DoUniversalACDSP(NextNoiseSample(), false, 0f, acVolPersonalityTail, invSRTail, false);
                    float sTail = (float)(acSampleTail * npcVolumeScale);
                    for (int c = 0; c < channels; c++) data[i + c] = sTail;
                }
                return;
            }

            Array.Clear(data, 0, data.Length);
            return;
        }
        _wasPoweredForACTail = true;

        if (dist >= maxAudioDistance) { Array.Clear(data, 0, data.Length); return; }
        if (oldBus) InitOldBusCharacter();

        bool isEngineRunning = engineState == EngineRunState.Running;
        bool isCranking      = engineState == EngineRunState.Cranking;

        if (isCranking)
        {
            _crankTimer += (float)(data.Length / (double)channels / SR);
            if (_crankTimer >= CRANK_DURATION)
                engineState = EngineRunState.ReadyToStart;
        }

        float attenuation = 1.0f - (dist / maxAudioDistance);
        float hz  = FHz(rpm);
        // Hill Mode ONLY: the engine gets audibly DEEPER the harder it revs,
        // instead of the normal higher-rpm-=higher-pitch relationship. This
        // is a deliberate stylization, not a real diesel behavior -- real
        // engines don't do this -- but it's what sells "laboring/straining
        // under load" the way a a real recording's perceived pitch can seem
        // to drop when an engine is under heavy load even as RPM climbs
        // (more low-end harmonic content, less bright top end). rn (0-1
        // normalized rpm) drives it, so it's silent at idle and deepest
        // right as RPM peaks against the stall flare above.
        if (hillMode)
        {
            float rnForPitch = (rpm - IDLE) / Mathf.Max(1f, GOV - IDLE);
            hz *= Mathf.Lerp(1.0f, 0.80f, Mathf.Clamp01(rnForPitch));
        }
        float rn  = (rpm - IDLE) / Mathf.Max(1f, GOV - IDLE);
        // Hill Mode: 'ld' feeds every combustion/growl/whine DSP function
        // downstream (DoCombustionEngine, DoAllisonGearGrowl, DoB500RDSP,
        // etc) as the engine's load/throttle character -- boosting it here,
        // once, at the source, is what makes each individual firing pulse
        // sound laboring under grade rather than just "louder RPM". rn gets
        // a smaller boost too so clatter/roughness ramps in step with the
        // load rather than only responding to actual RPM rise.
        //
        // [FIX] The old flat isArticulatedEngine -> articulatedLoadMul bump
        // here has been REMOVED. That was a constant ratio regardless of
        // RPM (both tiers share one curve shape, so it never actually read
        // as "lugs harder specifically down low" like a real hotter tune
        // should). The real tier-based torque bias now lives inside
        // DoCombustionEngine itself (BusAudioEngine_AudioLayers.cs), weighted
        // by rn so it's front-loaded into the low-RPM range and tapers off
        // by mid-range -- ld here stays pure throttle/hill-mode input.
        float ld  = Mathf.Clamp01(accel * (hillMode ? hillEngineStrain : 1.0f));
        if (hillMode) rn = Mathf.Clamp01(rn * 1.15f);
        // ═════════════════════════════════════════════════════════════════
        //  ALLISON RETARDER — REBUILT as the real 3-stage system.
        //  Grounded in the New Flyer XD40 operator's manual's "Retarder
        //  Operation" section, which describes something quite different
        //  from the single on/off gate this used to be
        //  (`bkPd > 0.30f && spd > 15f`):
        //
        //   · "The retarder operates in three stages and is ONLY EFFECTIVE
        //     AT SPEEDS ABOVE 5 MPH" -- ~8 km/h, not the 15 km/h floor
        //     that was coded.
        //   · "RELEASING THE ACCELERATOR engages the FIRST STAGE" -- stage
        //     1 needs no brake pedal at all, which the old gate made
        //     impossible (it required bkPd > 0.30 before anything happened).
        //   · "Lightly pressing on the brake treadle (the first 5° to 10°
        //     of movement) engages the SECOND STAGE."
        //   · "Further brake application engages the THIRD STAGE leading to
        //     full retarder operation."
        //   · "Releasing the brake treadle will disengage the retarder."
        //   · ABS: "The retarder will automatically be turned off if the
        //     ABS system is in active operation... When the ABS event
        //     deactivates, retarder operation will resume in approximately
        //     6 SECONDS." -- a real, specific resume delay, not instant.
        //
        //  retStage (0-3) is the graduated output; retAct stays as the
        //  boolean every existing DSP call site already consumes, so this
        //  rebuild doesn't require touching all of them.
        //  Percent split note: the CNG XN40 manual quotes 33/66/100% and
        //  the diesel XD40 spec sheet quotes 25/66/100% for the same
        //  3-stage system -- a real per-spec variance, not modeled
        //  separately here since both describe the same staging shape.
        // ═════════════════════════════════════════════════════════════════
        {
            bool retSpeedOK = spd > 8f; // real 5 mph floor

            // [SCOPE NOTE] ProcessAudio is an audio-buffer callback, not a
            // frame update -- there's no `dt` here. Buffer duration is
            // computed from the real sample count instead, which is also
            // more accurate than a frame delta for audio-rate timing.
            float retDt = (float)(data.Length / (double)channels / SR);

            if (absActive)
            {
                retAbsLockout = 6f; // real ~6s resume delay after an ABS event ends
            }
            else if (retAbsLockout > 0f)
            {
                retAbsLockout -= retDt;
                if (retAbsLockout < 0f) retAbsLockout = 0f;
            }

            int stageTarget = 0;
            if (retSpeedOK && retAbsLockout <= 0f && retarderEnabled)
            {
                if      (bkPd > 0.25f)  stageTarget = 3; // further brake application -> full
                else if (bkPd > 0.02f)  stageTarget = 2; // first 5-10 degrees of treadle
                else if (accel < 0.02f) stageTarget = 1; // accelerator released, no brake needed
            }
            retStage = stageTarget;
            // Smoothed 0..1 intensity so the DSP layers can ramp between
            // stages instead of stepping (real stages engage progressively
            // as fluid fills the housing, not instantaneously).
            float retIntensityTarget = retStage / 3f;
            retIntensitySmooth += (retIntensityTarget - retIntensitySmooth)
                                * retDt * (retIntensityTarget > retIntensitySmooth ? 4.5f : 3.0f);
        }
        bool  retAct = retStage > 0;
        float outRPM = gear > 0 ? (spd / 3.6f) / TCIRC * 60f : 0f;

        bool  acEffectiveOn     = batteryOn && acComfortOn;

        // [NEW] AC cycling mode -- 30s-on/30s-off duty cycle when enabled,
        // overriding acEffectiveOn during the off phase. Timer advanced
        // once per ProcessAudio callback using this buffer's real duration
        // -- 30s-scale timing doesn't need per-sample precision, so this
        // avoids restructuring the per-sample loop below just for a timer.
        // Attack (turning back on) always uses DoUniversalACDSP's normal
        // ~7s smooth spin-up regardless of cycling mode -- only the RELEASE
        // speed differs (fastRelease below), matching "off abruptly (with
        // some falloff) ... when it comes back it comes back smoothly".
        if (acCyclingMode && batteryOn && acComfortOn)
        {
            float bufferDurationSec = (float)(data.Length / (double)channels / SR);
            acCyclePhaseTimer += bufferDurationSec;
            if (acCyclePhaseTimer >= 30f)
            {
                acCyclePhaseTimer = 0f;
                acCyclePhaseOn = !acCyclePhaseOn;
            }
            if (!acCyclePhaseOn) acEffectiveOn = false;
        }
        else
        {
            acCyclePhaseTimer = 0f;
            acCyclePhaseOn    = true; // re-enabling the mode later always starts in the "on" phase
        }
        bool acFastRelease = acCyclingMode && !acCyclePhaseOn;

        float acLevelEffective  = acEffectiveOn ? acLevel : 0f;
        float engVolPersonality = acHighEngLow ? 0.70f : 1.0f;
        // [Engine Voice opt1_3] smoother/quieter overall character — L9/L9N/ISL9
        // only, not gated by transmission (see dispatch below).
        bool diwaSmoothQuietActive = diwaOpt1_3 &&
            (engineType == EngineType.L9 || engineType == EngineType.L9N || engineType == EngineType.ISL9);
        if (diwaSmoothQuietActive) engVolPersonality *= 0.50f;
        float acVolPersonality  = acHighEngLow ? 1.45f : 1.0f;
        float acFrac = acLevelEffective / 100f;

        double invSR = 1.0 / SR;

        for (int i = 0; i < data.Length; i += channels)
        {
            audioClock += invSR;

            double rawNoise = NextNoiseSample();
            noise_lp      = noise_lp * 0.85 + rawNoise * 0.15;
            double noiseHp = rawNoise - noise_hp_prev;
            noise_hp_prev = rawNoise;
            noise_lo = noise_lo * 0.92 + rawNoise * 0.08;
            noise_hi = noise_hi * 0.73 + rawNoise * 0.27;
            double noiseClt = noise_lp - noise_lo;
            double noiseInd = noise_hi  - noise_lp;

            // Universal rooftop A/C — rebuilt into blower / condenser-fan /
            // scroll-compressor as separate machines with soft-start and a
            // slow thermostatic hunt cycle (see DoUniversalACDSP).
            double acSample = DoUniversalACDSP(rawNoise, acEffectiveOn, acFrac, acVolPersonality, invSR, acFastRelease);

            // Cranking renders its own isolated layer and skips the entire
            // drivetrain tree below — no engine/transmission tone while the
            // starter is just spinning things over.
            if (isCranking)
            {
                double crankOut = RenderCrankSample(invSR);
                float crankOutput = (float)(crankOut + acSample) * 0.525f * attenuation;
                for (int c = 0; c < channels; c++) data[i + c] = crankOutput;
                continue;
            }
            if (!isEngineRunning)
            {
                // Off / ReadyToStart — caught or not, engine itself makes no
                // sound until the player explicitly confirms Running. Also
                // where every startup-cue edge detector resets, so the NEXT
                // Cranking/Off -> Running transition re-triggers cleanly
                // (see BusAudioEngine.StartupSequence.cs).
                //
                // [FIX] v6_wasEngineRunningV6 (Voith's own windup, added
                // first -- BusAudioEngine.d8646.cs) had no reset anywhere at
                // all: DoVoithDSP only ever runs from inside this same
                // isEngineRunning branch, so it never saw isEngineRunning
                // go false to flip its own flag back -- meaning the windup
                // could only ever fire once per bus instance, never again on
                // a second engine restart. Same shape would've hit
                // hyb_wasEngineRunning/elec_wasEngineRunning below without
                // this reset, so fixing all three here together.
                v6_wasEngineRunningV6 = false;
                hyb_wasEngineRunning  = false;
                elec_wasEngineRunning = false;
                _wasRunningForCatch = false;
                float idleOutput = (float)acSample * 0.525f * attenuation;
                for (int c = 0; c < channels; c++) data[i + c] = idleOutput;
                continue;
            }

            // Rough-catch idle wobble + universal alternator load-in cue --
            // fires once on the Cranking/Off -> Running edge. See
            // BusAudioEngine.StartupSequence.cs for the full reasoning.
            double catchSample = UpdateStartupCatchAndAlternator(invSR, engVolPersonality, out float catchWobble);
            double alternatorSample = UpdateAlternatorCue(invSR);
            // Extended air-puff tail -- only for plain combustion (hybrids/
            // electric never crank, so UpdateStartupCatchAndAlternator never
            // arms this timer for them; see that method's own gating).
            double extendedPuffSample = 0.0;
            if (_extendedPuffTimer >= 0f)
            {
                _extendedPuffTimer += (float)invSR;
                if (_extendedPuffTimer <= EXTENDED_PUFF_DURATION)
                {
                    float puffProgress = _extendedPuffTimer / EXTENDED_PUFF_DURATION;
                    float minInterval  = Mathf.Lerp(1.0f, 2.6f, puffProgress);
                    extendedPuffSample = RenderAirPuffLayer(invSR, minInterval, 0.35f) * 0.8 * npcVolumeScale;
                }
                else
                {
                    _extendedPuffTimer = -1f;
                }
            }

            double engineSample = 0.0;
            double txSample     = 0.0;

            // ══════════════════════════════════════════════════════════════
            //  L9N — Cummins L9N CNG
            //  SOUND CHARACTER: smoother, deeper, idle wobble, less clatter.
            // ══════════════════════════════════════════════════════════════
// ── CNG (L9N) with Configurable Smoothness & Volume Scaling ──────────
// ══════════════════════════════════════════════════════════════
            //  HDS 200 / HDS 300 / Gen3 — BAE HybriDrive series hybrid
            //  [FIX] BAE/HDS300/baegen3 are a TRANSMISSION (tx), not their own
            //  engine — same relationship h40ep/h50ep (parallel-hybrid
            //  retrofits) already have to their host diesel. This used to
            //  redirect here BEFORE the engineType dispatch below, completely
            //  replacing the real engine voice with a thinner, self-contained
            //  genset core instead of layering over it — which is why B67
            //  hosts paired with "bae" went effectively silent (DoBAEEngineDSP's
            //  own core is much thinner than DoCombustionEngine's full voice)
            //  and why the shift/RPM side-channel (DoBAERange/CalcBAERPM etc,
            //  driven separately below) could fall through to Voith's real
            //  multi-gear logic on any combo this redirect didn't happen to
            //  intercept. Now B67/B72 always run DoCombustionEngine like any
            //  other engine, and DoBAEEngineDSP/DoHDS300EngineDSP/
            //  DoBAEGen3EngineDSP are dispatched from INSIDE DoCombustionEngine's
            //  own transmission-dispatch chain (AudioLayers.cs) as pure
            //  additive overlays, exactly like h40ep/h50ep are.
            // ══════════════════════════════════════════════════════════════
// ═════════════════════════════════════════════════════════════════════════
//  L9N REWORK — per spec: L9N is L9's voice (raspy core, not the smoothed-
//  down version it had), pitched deeper, with a stronger whine over it.
//  Previously L9N used a separate smoothed synthesis path that dropped the
//  hoarse/grit character L9 has. Now it reuses L9's raspy core verbatim,
//  transposed down (0.94x), keeping L9N's own idle wobble / startup / pop
//  / hiss / moan layers untouched since those are CNG-specific and correct.
// ═════════════════════════════════════════════════════════════════════════
if (engineType == EngineType.L9N || engineType == EngineType.ISLG
      || engineType == EngineType.L9  || engineType == EngineType.ISL9 || engineType == EngineType.ISL
      || engineType == EngineType.ISB67
      || engineType == EngineType.B67
      || engineType == EngineType.B72
      || engineType == EngineType.X10)
{
    // [FIX -- X10 routing] X10 used to run a completely standalone legacy
    // branch here (own hand-rolled core, own duplicate Allison whine, its
    // own copies of the voith/nxt/b500r/b3400xfe/zf tx dispatches) that
    // never touched DoCombustionEngine at all -- meaning every universal
    // layer every OTHER diesel gets (idle chug->hum settle, turbo flutter,
    // wheel/hub whine, door hiss+thunk, parking-brake release air puff,
    // jake brake -- including the real X10 HPD tuning already sitting in
    // DoCombustionEngine's jake-brake block, unreachable until now --
    // cooling fan, engine-driven air compressor chuff, DPF regen) was
    // silently missing on X10, which is why it read as flat/"normal" next
    // to L9/ISL9. DoCombustionEngine already had a dedicated, better-built
    // X10 core (DoX10RealCore, resonant box/pipe cavity model matching the
    // L9/ISL9 architecture) wired into its own dispatch (see ResolveCombustion
    // core selection below) that was ALSO dead code for the same reason --
    // X10 never called this function to reach it. Routing X10 through here
    // like every other diesel fixes both at once: picks up every universal
    // layer, and finally uses the real core instead of the old duplicate
    // one. The old branch's tx sub-dispatch (voith/voith35/d8646art/d8646/
    // d8645/nxt/b500r/b3400xfe/zf/zfel2/zfel2_hd) is already fully covered
    // by DoCombustionEngine's own tx chain (AudioLayers.cs) -- nothing lost.
    // ph_x10_* phase fields above are now unused (old branch removed) but
    // left declared in case they're needed again.
    DoCombustionEngine(ref engineSample, ref txSample, hz, rn, ld, outRPM, retAct,
        engVolPersonality, acFrac, noiseHp, noiseClt, noiseInd, invSR);
}
            // ══════════════════════════════════════════════════════════════
            //  B67 — BAE HybriDrive series hybrid (HDS 200)
            // ══════════════════════════════════════════════════════════════


            // ══════════════════════════════════════════════════════════════
            //  XE40 — Battery-Electric
            // ══════════════════════════════════════════════════════════════
            else if (engineType == EngineType.XE40)
            {
                DoXE40DSP(ref engineSample, ref txSample, ld, engVolPersonality, noiseHp, invSR);
            }

            // (siemensCNG moved: now dispatched early by tx, alongside
            //  bae/hds300, same reason those two are early -- see that
            //  block near the top of this dispatch chain. This spot was
            //  DEAD CODE: engineType==L9N above already caught every
            //  siemensCNG bus first since the donor's EngineType is L9N.)

            // ══════════════════════════════════════════════════════════════
            //  XE60 — either genuine ZF AVE 130 portal axle ("zfave130",
            //  unchanged, own real hardware) OR Accelera/ELFA3's rear
            //  direct-drive + centre in-wheel motor layout — two entirely
            //  different real drivetrains that both happen to live on the
            //  XE60 body/EngineType, picked by tx.
            // ══════════════════════════════════════════════════════════════
            else if (engineType == EngineType.XE60)
            {
                if (tx == "elfa3_centeraxle" || tx == "accelera_centeraxle")
                    DoElfa3CenterAxleDSP(ref engineSample, ref txSample, ld, engVolPersonality, noiseHp, invSR);
                else
                    DoZFAVE130DSP(ref engineSample, ref txSample, ld, engVolPersonality, noiseHp, invSR);
            }

            // ══════════════════════════════════════════════════════════════
            //  XHE40 — Hydrogen Fuel Cell-Electric (40ft)
            // ══════════════════════════════════════════════════════════════
            else if (engineType == EngineType.XHE40)
            {
                DoXHE40DSP(ref engineSample, ref txSample, ld, engVolPersonality, noiseHp, invSR);
            }

            // ══════════════════════════════════════════════════════════════
            //  XHE60 — Hydrogen Fuel Cell-Electric (60ft artic)
            // ══════════════════════════════════════════════════════════════
            else if (engineType == EngineType.XHE60)
            {
                DoXHE60DSP(ref engineSample, ref txSample, ld, engVolPersonality, noiseHp, invSR);
            }

            // ── Old Bus character layer ───────────────────────────────────────
            double oldBusSample = 0.0;
            if (oldBus) DoOldBusCharacter(ref oldBusSample, rn, ld, hz, outRPM, engVolPersonality, noiseHp, invSR);
double variantSample = 0.0;
if (oldBusVariant > 0) DoOldBusVariantCharacter(ref variantSample, rn, ld, hz, outRPM, engVolPersonality, invSR);
            // ── Coast layer (NEW) — soft overrun texture for ~2s after any
            // gear change, only while the driver is off the throttle. ──────
            double coastSample = 0.0;
            {
                bool coastEligible = coast_windowTimer > 0f && accel < 0.02f && running && !IsElectric();
                float coastTarget = coastEligible
                    ? Mathf.Clamp01(coast_windowTimer / COAST_WINDOW) * 0.05f * npcVolumeScale * engVolPersonality
                    : 0f;
                coast_volSmooth += (coastTarget - coast_volSmooth) * (coastTarget > coast_volSmooth ? 0.01f : 0.004f);
                if (coast_volSmooth > 0.0005f)
                {
                    double coastHzLocal = 38.0 + rn * 14.0;
                    coastSample = noise_lp * coast_volSmooth * 0.6
                                + Math.Sin(2.0 * Math.PI * ph_coast_moan) * coast_volSmooth * 0.5;
                    ph_coast_moan = (ph_coast_moan + coastHzLocal * invSR) % 1.0;
                }
            }

            // ── Converter Stall Hold layer (NEW) — universal, every engine.
            // Two parts: a rising "catch" swell right on engagement (the
            // "uuuueeerrrrrrr" as the converter takes the strain), settling
            // into a sustained strained drone ("ERRRRRRRR...") for as long
            // as the hold is active. Fades out fast on release. ─────────────
            double stallHoldSample = 0.0;
            {
                float swellEnv = Mathf.Clamp01(stallHoldEngageT / 0.35f); // ~0.35s catch
                float sustainGate = stallHoldActive ? 1f : 0f;
                stallSoundVolSmooth += (sustainGate - stallSoundVolSmooth) * (sustainGate > stallSoundVolSmooth ? 0.05f : 0.10f);

                if (stallSoundVolSmooth > 0.0008f)
                {
                    // Swell: pitch rises quickly from a low moan up toward the
                    // strained drone pitch over the first ~0.35s.
                    double swellHz = Mathf.Lerp(70f, 130f, swellEnv);
                    double droneHz = 118.0 + rn * 40.0; // tracks current strained RPM a bit

                    double freqNow = stallHoldActive ? Mathf.Lerp((float)swellHz, (float)droneHz, swellEnv) : swellHz;
                    float vol = (0.10f + ld * 0.06f) * stallSoundVolSmooth * npcVolumeScale * engVolPersonality;

                    double core = Math.Tanh(Math.Sin(2.0 * Math.PI * ph_stallDrone1) * 1.9) * vol
                                + Math.Sin(2.0 * Math.PI * ph_stallDrone2) * vol * 0.45;
                    // Slow amplitude wobble so the sustained drone doesn't sit
                    // dead flat — reads more like a strained "errrrrrr" moan.
                    double wobble = 0.85 + 0.15 * Math.Sin(2.0 * Math.PI * ph_stallSwell);
                    stallHoldSample = core * wobble + noiseHp * vol * 0.10;

                    ph_stallDrone1 = (ph_stallDrone1 + freqNow        * invSR) % 1.0;
                    ph_stallDrone2 = (ph_stallDrone2 + freqNow * 1.503 * invSR) % 1.0;
                    ph_stallSwell  = (ph_stallSwell  + 5.0             * invSR) % 1.0;
                }
            }

            // ── Fuzz layer (NEW) — a small constant amount of high-frequency
            // grit mixed into every engine so nothing renders as a perfectly
            // clean tone. Kept intentionally subtle. ─────────────────────────
            double fuzzSample = noise_hi * 0.0045 * (0.55 + rn * 0.45) * npcVolumeScale;

            // [ADD] Articulation joint creak/groan -- X60 artics only
            // (ResolveCenterAxleType gates this to AVN132_Passive/
            // AVE130_Driven; MAN40ft = no layer at all, same as the
            // portal-axle whine hook it rides alongside).
            double creakSample = 0.0;
            DoArticulationCreakDSP(ref creakSample, invSR);


            // ── L9/ISL9 "inverter" overlay — a genuine FILTER, not another
            // additive layer. Applied here specifically because this is the
            // true final combine point for the ENGINE's own voice.
            // cb_invFilterMod is 1.0 (no-op) on every engine except L9/ISL9,
            // set each call inside DoISL9Core/DoL9Core.
            //
            // [FIX] This used to multiply (engineSample + txSample) together
            // -- meaning the HE300VG turbo actuator's electronic character
            // (a genuinely ENGINE-side trait, specific to ISL9/L9's modern
            // VGT hardware) was bleeding into whatever TRANSMISSION voice
            // happened to be attached, regardless of what that transmission
            // actually is. Most obviously wrong on DIWA.5 (d8645) -- its own
            // header explicitly frames it as the OLDER, "looser/older
            // electronic control" Voith unit, so having a modern engine-side
            // electronic filter color its whine directly contradicted its
            // own established character -- but it was equally wrong for
            // every other transmission (Allison, ZF, B500R...) paired with
            // ISL9/L9, since a turbo actuator has no physical relationship
            // to a gearbox's own gear-mesh/converter voice at all. Now only
            // engineSample is filtered; txSample passes through untouched.
            double coreSample = ((engineSample * cb_invFilterMod) + txSample) * catchWobble;
            double breakdownSample = UpdateBreakdownAudio(invSR, engVolPersonality);

float output = (float)(coreSample + acSample + oldBusSample + coastSample + fuzzSample + creakSample + variantSample + stallHoldSample + breakdownSample + catchSample + alternatorSample + extendedPuffSample) * 0.525f * attenuation;
            for (int c = 0; c < channels; c++) data[i + c] = output;
        }
    }
// ── Voith DIWA whine (D864.6) — additional fields for the converter-slip
    //    rework. Add these next to the existing ph_vw1/ph_vw2/ph_vw3 block.
    private double ph_v_conv1 = 0.0;   // primary converter whine (slip-tracked)
    private double ph_v_conv2 = 0.5;   // phase-inverted partner — the "inverted" beat
    private double ph_v_conv3 = 0.0;   // upper harmonic, gear-1 dominant
    private float  voith_convHzSmooth  = 0f;
    private double ph_v_acc1 = 0.0, ph_v_acc2 = 0.33; // accessory/alternator whine bank
    private float  voith_accHzSmooth   = 0f;
    private double ph_vw4;
    private double ph_v_shake1, ph_v_shake2, ph_v_shake3;
    private float voith_motoTimer;
bool voith_motoActive;
 public const float VOITH_GEAR1_UPSHIFT_SPD = 22f;
private double ph_v_clA, ph_v_clB, ph_v_clC, ph_v_clD, ph_v_clE;
private float  v_clDriftA = 320f, v_clDriftB = 360f, v_clDriftC = 430f;
private double ph_v_clDriftLfo1, ph_v_clDriftLfo2;
private double ph_v_inv1, ph_v_inv2, ph_v_inv3, ph_v_invSub;
private double ph_v_g1eng1, ph_v_g1eng2, ph_v_g1engSub;
private double ph_v_lowMot1, ph_v_lowMot2, ph_v_lowWarbleLfo;
private double ph_v_gate; // "EaEaEaE" pulse gate phase, position-driven
 
private void GetVoithGearWindow(out float lo, out float hi, out float progress)
{
    switch (gear)
    {
        case 1:  lo = 0f;                       hi = VOITH_GEAR1_UPSHIFT_SPD; break;
        case 2:  lo = VOITH_GEAR2_DOWNSHIFT_SPD; hi = VOITH_GEAR2_UPSHIFT_SPD; break;
        case 3:  lo = VOITH_GEAR3_DOWNSHIFT_SPD; hi = VOITH_GEAR3_UPSHIFT_SPD; break;
        case 4:  lo = VOITH_GEAR4_DOWNSHIFT_SPD; hi = VOITH_GEAR4_UPSHIFT_SPD; break;
        default: lo = 0f; hi = 1f; break;
    }
    progress = gear >= 1 ? Mathf.Clamp01((spd - lo) / Mathf.Max(1f, hi - lo)) : 0f;
}
private double ph_v_mesh1;
private double ph_v_mesh2;
private double ph_v_mesh3;

private double ph_v_contWh1, ph_v_contWh2, ph_v_contWhSub;
private float  v_contWhHzSmooth = 0f;

private double ph_v_dir1;
private double ph_v_dir2;
private double ph_v_dir3;
private double ph_v_bear;

private float directMeshHz = 420f;

private double ph_v_pump1;
private double ph_v_pump2;
private double ph_v_pumpLFO;


private float al_dblTapVol = 0f;
private double ph_al_dbltap1, ph_al_dbltap2;

private void DoAllisonDoubleTap(ref double txSample, float engMul, double invSR)
{
    // Reconfigured to act as the Voith "Hydraulic Line Thud" when the direct brake snaps shut
    if (al_dblTapActive)
    {
        al_dblTapTimer += (float)invSR;
        al_dblTapVol = al_dblTapTimer < 0.08f
            ? Mathf.SmoothStep(0f, 1f, al_dblTapTimer / 0.08f)
            : Mathf.SmoothStep(1f, 0f, Mathf.Clamp01((al_dblTapTimer - 0.08f) / 0.20f));
            
        if (al_dblTapTimer > 0.28f) { al_dblTapActive = false; al_dblTapTimer = 0f; }
    }
    else al_dblTapVol *= 0.85f;

    if (al_dblTapVol > 0.003f)
    {
        double t1 = Math.Sin(2.0 * Math.PI * ph_al_dbltap1);
        double t2 = Math.Sin(2.0 * Math.PI * ph_al_dbltap2);
        // Heavy saturated low-pass characteristic thud for DIWA.6 brake pack engagement
        double thud = Math.Tanh((t1 * 1.5 + t2 * 0.4) * 3.0);
        txSample += thud * al_dblTapVol * 0.55f * engMul;
    }
    
    // Voith hydraulic accumulator resonance frequencies (approx. 58Hz body thump)
    ph_al_dbltap1 = (ph_al_dbltap1 + 58.0 * invSR) % 1.0;
    ph_al_dbltap2 = (ph_al_dbltap2 + 96.0 * invSR) % 1.0;
}

 
/* private void DoAllisonDoubleTap(ref double txSample, float engMul, double invSR)
{
    if (al_dblTapActive)
    {
        al_dblTapTimer += (float)invSR;
        al_dblTapVol = al_dblTapTimer < 0.10f
            ? Mathf.SmoothStep(0f, 1f, al_dblTapTimer / 0.10f)
            : Mathf.SmoothStep(1f, 0f, Mathf.Clamp01((al_dblTapTimer - 0.10f) / 0.22f));
        if (al_dblTapTimer > 0.34f) { al_dblTapActive = false; al_dblTapTimer = 0f; }
    }
    else al_dblTapVol *= 0.9f;
 
    if (al_dblTapVol > 0.003f)
    {
        double t1 = Math.Sin(2.0 * Math.PI * ph_al_dbltap1);
        double t2 = Math.Sin(2.0 * Math.PI * ph_al_dbltap2);
        double bark = Math.Tanh((t1 * 1.2 + t2 * 0.6) * 2.4);
        /* louder + a hair lower than the base shift thud so it reads as
           its own distinct "tap" instead of blending into it
        txSample += bark * al_dblTapVol * 0.42f * engMul;
    }
    ph_al_dbltap1 = (ph_al_dbltap1 + 70.0  * invSR) % 1.0;
    ph_al_dbltap2 = (ph_al_dbltap2 + 118.0 * invSR) % 1.0;
}
*/
/*
    DoVoithDSP — now takes engineSample too, since the G1/mech sequences
    write to both the tx channel and the engine channel. Update both call
    sites (L9N branch and L9/ISL9/X10 branch) to pass `ref engineSample`
    through, same pattern DoH4xDSP already uses.
*/


private double ph_l9n_chopGate;
// ── Voith whine — rebuilt from D864.5 parameters ──

private double ph_vwhine_hiss;
// [FIX] Pitch-hold-through-shift tracking for the W1 drive whine.
private int    v6_lastGear    = -1;
private float  v6_whHoldTimer = 0f;
private float  v6_whHoldHz    = 0f;
// [NEW] Measured on XN40 D864.6 ref (09-06 clip): G1->G2 is a true vertical
// cutoff (no glide at all), G2->G3 is a fast ~150-250ms step-and-relock, NOT
// instant. v6_whRelockWin times that relock window after the flat hold ends.
private float  v6_whRelockWin = 0f;
// Screech layer -- fades in as rpm climbs toward redline
private float  v6_screechSmooth = 0f;
private double ph_v6_wh4;
private double ph_v6_kdGrowl; // [NEW] kickdown-deepen growl layer, see DoVoithDSP
private double ph_v6_ansClunk; // [NEW] ANS re-engagement clunk, see DoVoithDSP
private float  voith_pumpSpinDown     = 0f; // [NEW] 1→2 pump impeller drag-down, see DoVoithDSP
private int    v6_lastGearForPumpDown = -1;
private double ph_v6_pumpSpin;
private float  v6_g1EnvTimer  = 0f; // [NEW] time since entering gear 1, drives the build/hold envelope, see DoVoithDSP
private bool   v6_wasInGear1  = false;
private float  v6_firePulseMod = 1.0f; // [NEW] real per-firing-event modulation tied to the engine's own hz
private double ph_v6_firePulse;

// ── Second converter voice — the FIXED tone (measured) ────────────────────
//  Measured off the real XN60/L9N/D864.6 recording: a tone sits at 633.0 Hz
//  mean (std 24.9 Hz) across the ENTIRE 56.9s clip, while the mesh ridge
//  swings 1344 -> 2190 Hz. correlation(fixed tone, mesh ridge) = -0.068 —
//  i.e. none. Ratio mesh/fixed: mean 2.307, std 0.370, so it is NOT
//  harmonically locked to the mesh either. Prominence 25-38 dB above local
//  floor, on par with the mesh whine itself.
//
//  It ignores engine rpm, road speed, gear AND load. That rules out gear
//  mesh and converter slip both — it is something running at its own
//  constant rate. On an XN60 CNG artic the candidates are the electrically
//  driven transmission oil cooling circuit or a constant-speed charge/lube
//  pump. Modelled as its own standalone voice (see DoVoithFixedToneDSP)
//  rather than another partial on the mesh chord, because it has to survive
//  gear changes and ANS untouched — stacking it on the mesh would make it
//  drop out exactly where the recording shows it holding steady.
private double ph_v6_fix1, ph_v6_fix2, ph_v6_fixSub;
private double ph_v6_fixWob;
private float  v6_fixVolSmooth = 0f;

// ── Gear-1 envelope timing ─────────────────────────────────────────────────
//  Originally corrected to a measured 2.84s window (2.35s build + 0.50s
//  hold). The XN40 D864.6 reference (09-06 clip) shows a visibly longer,
//  slower launch -- the whine climb runs at least 5.7s (8.5s->14.2s) before
//  the upshift, and we don't have a clean start timestamp so the real gear-1
//  duration on that bus is likely even longer than that. Forced longer here
//  per direct request rather than recomputed from an exact measured window --
//  treat this as "stretched to match the XN40's real feel," not a precise
//  re-derivation like the original 2.84s figure was.
private const float V6_G1_BUILD_SEC   = 4.40f;   // was 2.35f (orig measured: 2.84s total window)
private const float V6_G1_HOLD_SEC    = 0.85f;   // was 0.50f
//  Measured gear-1 mesh rise (older L9N/D864.6 ref): 1344.3 -> 1528.1 Hz = 1.1367.
//  Nudged slightly wider after the XN40 clip's launch showed a noticeably
//  bigger relative climb (fundamental roughly 500->900Hz, ~1.8x, on that
//  bus/gearing) -- only a modest bump off one data point, not a full rebase.
private const float V6_G1_PITCH_SPAN  = 1.19f;   // was 1.1367f

// ── opt1_4 ("second voice") tuning ────────────────────────────────────────
//  Must match opt1_4G1Extend in DoVoithRange. Gear 1 is held 1.10x longer
//  [REDUCED per instruction, was 1.5x]; the gear-1 curve is stretched
//  across that shorter window rather than being allowed to keep climbing.
private const float V6_OPT4_G1_EXTEND   = 1.35f; // [CHANGED] was 1.10f — genuinely slower G1 under opt1_4, not just a 10% stretch, to make room for the transplanted H50EP whine layer without the two fighting for the same short window
//  Converter whine fade-in window, as a fraction of gear-1 progress. Was
//  effectively 0.85 -> 1.00 (only the last 15%, so it arrived late and
//  abruptly); now it starts early and swells across roughly the first half.
private const float V6_OPT4_WHINE_IN_LO = 0.04f;
private const float V6_OPT4_WHINE_IN_HI = 0.55f;
//  [ADD] Overall whine loudness multiplier for opt1_4 specifically, per
//  instruction ("make the DIWA whine a lot QUIETER when it comes on").
//  Applied on top of the existing fade shape, not replacing it.
private const float V6_OPT4_WHINE_QUIETER = 0.45f;
//  [ADD] Retarder groan state -- see section 11 in DoVoithDSP for usage.
private float  v6_opt4RetGroanVol = 0f;
private double ph_v6_opt4RetGroan1, ph_v6_opt4RetGroan2;
//  How far the whine is pulled down by the end of the EXTENDED gear 1.
//  1.0 = no droop (old behaviour, runs shrill). 1/1.5 = 0.667 fully cancels
//  the extension but overshoots and ends below the gear's own start pitch.
//  0.88 keeps the stretched gear 1 clearly lower without going flat.
private const float V6_OPT4_G1_PITCH_DROP = 0.88f;

// ── opt1_4 sharp air-hiss filter state ────────────────────────────────────
//  noise_hi is low-passed despite its name, which is why the old hiss read
//  as a wind blow. These carry a local high-pass + differentiator instead.
private double v6_hissLP   = 0.0;
private double v6_hissPrev = 0.0;

// Stock DIWA torque-converter wail — distinct single-cry voice, own tuning
private float  v6_wailSmooth = 0f;
private double ph_v6_wail1, ph_v6_wail2;

// [ADD] opt1_6 eerie idle whine state -- own fields, own phases, not
// shared with opt1_4's hiss (deliberately separate, see the section's own
// header comment for why this is a genuine anomaly voice, not a variant
// of opt1_4's real air-hiss).
private double ph_v6_opt6Whine1, ph_v6_opt6Whine2;
private float  v6_opt6ChopHz      = 0f;   // current stepped/held pitch offset -- only updates in discrete jumps, not smoothly
private float  v6_opt6ChopTimer   = 0f;
private double ph_v6_opt6LFO;             // slow oscillation, ~0.4Hz, modulates amplitude while idling
// ── XE40 Traction Background Drone Layer ──
private float v6_ringaTimer;
private float v6_ringaVol;

private double ph_v6_ringa1;
private double ph_v6_ringa2;
private double ph_v6_ringa3;
private float v5_xeDroneSmooth = 0f;

// ═══════════════════════════════════════════════════════════════════════════
//  D8645-derived additions for DoVoithDSP — fresh d6_ prefix, deliberately
//  NOT reusing v6_ (owned by 8646's own untouched drive whine/fixed-tone/
//  retarder) or v5_/d5_ (D8645's own function). These back the "8b" section
//  inside DoVoithDSP (pump floor, TC slip, tail whine, G1 buzz) plus the
//  replaced shift-thud and move-off-grab sections. A few fields below
//  (whine/retarder/move-off ones) are leftover from an earlier standalone-
//  function draft that got folded into DoVoithDSP directly instead --
//  harmless unused declarations, not wired to anything currently.
// ═══════════════════════════════════════════════════════════════════════════
private double ph_d6_w1 = 0.0, ph_d6_w2 = 0.5, ph_d6_w3 = 0.0;
private float  d6_whineHzSmooth = 0f;
private float  d6_shiftThud     = 0f;
private int    d6_lastGearAudio = 0;
private double ph_d6_thud1 = 0, ph_d6_thud2 = 0;
private double ph_d6_Mw, ph_d6_Mw2, ph_d6_Ret, ph_d6_Ret2;
private float  d6_retHzSmooth = 0f;
private float  d6_tcSlipSmooth = 0f;
private float  d6_pumpFloorVol = 0f;
private double ph_d6_pumpFloor1, ph_d6_pumpFloor2;
private bool   d6_wasStoppedVo = true;
private float  d6_moveOffTimer = 0f;
private double ph_d6_grab;
private float  d6_g1VibVol = 0f;
private double ph_d6_buzz;
// -- DORMANT (commented out per instruction, kept for possible future
// re-enabling) -- stopped-idle shake + shaker-chassis state, ported from
// the old shared v6_ implementation with a fresh d6_ prefix. See the
// DORMANT block inside DoD8646DSP itself for the actual (inert) logic.
// private float  d6_idleShakeEnv = 0f;
// private double ph_d6_am1, ph_d6_fireHalf, ph_d6_idleVib;
// private float  d6_idleVibHz;
// private bool   d6_shakerInit = false, d6_isShaker = false;
// private double ph_d6_shakeBody, ph_d6_creak1, ph_d6_creak2;
// private float  d6_creakVol = 0f;

private double ph_retPulse;
private double ph_vRetSqueak;
private double ph_body1;
private double ph_body2;
private double ph_body3;
private double ph_wobble;
private double ph_v6_idleVib;
private double ph_v6_rpmVib;
// ── DIWA opt1_1 — delayed whine ────────────────────────────────────────────
// A second whine voice that tracks rpm the same way the base whine does, but
// ~0.5s behind, at reduced volume. Built with a small ring buffer of recent
// rpm samples instead of a fixed-length filter, so the delay is exact
// regardless of DSP callback size.
private void DoDiwaOpt1_1DelayedWhine(ref double txSample, float rn, float rpm, int gear, float engMul, double invSR)
{
    if (!diwaOpt1_1) return;

    const float DELAY_SEC = 0.5f;
    int bufLen = Mathf.Max(64, Mathf.CeilToInt(DELAY_SEC / (float)invSR) + 1);

    if (diwa_rpmHistory == null || diwa_rpmHistory.Length != bufLen)
    {
        diwa_rpmHistory = new float[bufLen];
        diwa_rpmHistoryHead = 0;
        diwa_rpmHistoryLen = 0;
    }

    diwa_rpmHistory[diwa_rpmHistoryHead] = rpm;
    diwa_rpmHistoryHead = (diwa_rpmHistoryHead + 1) % bufLen;
    diwa_rpmHistoryLen = Mathf.Min(diwa_rpmHistoryLen + 1, bufLen);

    // Read the sample from ~0.5s ago (or the oldest we have, at startup).
    int delaySamples = Mathf.Min(bufLen - 1, diwa_rpmHistoryLen - 1);
    int readIdx = ((diwa_rpmHistoryHead - 1 - delaySamples) % bufLen + bufLen) % bufLen;
    float delayedRpm = diwa_rpmHistory[readIdx];

    float teeth = gear <= 1 ? 23.0f : 21.5f;
    float delayedHzTgt = (Mathf.Max(delayedRpm, IDLE) / 60f) * teeth;
    diwa_delayHzSmooth += (delayedHzTgt - diwa_delayHzSmooth) * 0.045f;

    if (diwa_delayHzSmooth > 40f)
    {
        double s1 = Math.Sin(2.0 * Math.PI * ph_diwa_delay1);
        double s2 = Math.Sin(2.0 * Math.PI * ph_diwa_delay2 * 1.5);
        // Prominent second whine voice — an audible echo of the base whine,
        // not just a texture layer buried in the mix.
        float vol = (0.055f + rn * 0.095f) * (gear <= 1 ? 1.0f : 0.45f);
        txSample += (s1 * 0.8 + s2 * 0.3) * vol * engMul;
    }

    ph_diwa_delay1 = (ph_diwa_delay1 + diwa_delayHzSmooth * invSR) % 1.0;
    ph_diwa_delay2 = (ph_diwa_delay2 + diwa_delayHzSmooth * invSR) % 1.0;
}

// ── DIWA opt1_2 — deep whine overlay ───────────────────────────────────────
// Low-register whine layer active only in G1, ramping harder with rpm than
// the base whine does (base whine caps its G1 gain around ~0.55-1.0x; this
// overlay climbs the full 0-1 range across the G1 rpm band).
private void DoDiwaOpt1_2DeepOverlay(ref double txSample, float rn, float rpm, int gear, float g1Progress, float engMul, double invSR)
{
    if (!diwaOpt1_2) return;

    float target = 0f;
    if (gear == 1 && rpm > IDLE * 0.9f)
        target = Mathf.Clamp01(rn * 1.35f) * (0.35f + 0.65f * g1Progress);

    diwa_deepVolSmooth += (target - diwa_deepVolSmooth) * (target > diwa_deepVolSmooth ? 0.030f : 0.020f);

    if (diwa_deepVolSmooth > 0.001f)
    {
        float deepHz = 130f + rpm * 0.085f; // roughly an octave-plus under the base whine
        ph_diwa_deep1 = (ph_diwa_deep1 + deepHz * invSR) % 1.0;
        ph_diwa_deep2 = (ph_diwa_deep2 + deepHz * 1.5 * invSR) % 1.0;

        double d1 = Math.Sin(2.0 * Math.PI * ph_diwa_deep1);
        double d2 = Math.Sin(2.0 * Math.PI * ph_diwa_deep2);
        // Prominent low overlay, not a subtle texture — meant to be clearly
        // audible as a distinct deep growl riding under G1 launch.
        txSample += Math.Tanh((d1 * 1.0 + d2 * 0.5) * 1.6) * diwa_deepVolSmooth * 0.16f * engMul;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ═══════════════════════════════════════════════════════════════════════════════
//  DoVoith35DSP — "35ft864.6" — the 35ft mini's (XD35/XN35) own dedicated
//  Voith voice. Currently an EXACT, byte-for-byte copy of DoVoithDSP below,
//  under its own name and its own tx branch (tx=="voith35") — same
//  relationship d8646art has to the base DIWA voice: no boolean gate,
//  selected purely by which transmission string a bus is configured with.
//
//  Nothing has been changed from the base voice yet — this is the starting
//  point for hand-editing the mini's actual character in from here.
// ═══════════════════════════════════════════════════════════════════════════════
private void DoVoith35DSP(ref double engineSample, ref double txSample, float rn, float ld, float hz,
                         float outRPM, float engMul, bool retAct, double invSR)
{
    // ═════════════════════════════════════════════════════════════════════════
    //  1. LOCAL VARIABLE DECLARATIONS & SHAKER INITIALIZATION
    // ═════════════════════════════════════════════════════════════════════════
    double ph_v6_idleVib = 0;
    float v6_idleVibHz = 0;
    float v6_rpmVibHz = 0;
    float v6_rpmVibVol = 0;

    if (!v6_shakerInit) 
    { 
        v6_shakerInit = true; 
        v6_isShaker = (NextNoiseSample() * 0.5 + 0.5) < 0.18; 
    }

    // g1Top scales with actual kickdown without altering global configuration thresholds
    float g1Top      = VOITH_GEAR1_UPSHIFT_SPD * 1.15f * (kickdownKey ? 1.32f : 1f);
    float g1Progress = gear == 1 ? Mathf.Clamp01(spd / Mathf.Max(1f, g1Top)) : 0f;
    float hydShare   = gear == 1 ? (1f - g1Progress) * (1f - g1Progress) : 0f;

    // ═════════════════════════════════════════════════════════════════════════
    //  2. DRIVE WHINE (DIWA GEAR MESH VOICE)
    // ═════════════════════════════════════════════════════════════════════════
    {
        float teeth = gear <= 1 ? 30.0f : 28.5f;
        float whTgt = (Mathf.Max(rpm, IDLE) / 60f) * teeth;

        // ── Upshift whine transition -- see field comment on v6_whRelockWin.
        bool isG1toG2 = (v6_lastGear == 1 && gear == 2);
        if (v6_lastGear != -1 && v6_lastGear != gear)
        {
            if (isG1toG2)
            {
                v6_whHoldTimer = 0f;
                v6_whHzSmooth  = whTgt;     // hard cutoff, no glide
            }
            else
            {
                v6_whHoldTimer = 0.20f;
                v6_whHoldHz    = v6_whHzSmooth;
                v6_whRelockWin = 0.22f;     // audible step-and-relock, not instant
            }
        }
        v6_lastGear = gear;

        if (v6_whHoldTimer > 0f)
        {
            v6_whHoldTimer -= (float)invSR;
            v6_whHzSmooth   = v6_whHoldHz; 
        }
        else if (v6_whRelockWin > 0f)
        {
            v6_whRelockWin -= (float)invSR;
            float relockPtau = (float)invSR / 0.20f;
            v6_whHzSmooth += (whTgt - v6_whHzSmooth) * relockPtau;
        }
        else
        {
            float ptau = whTgt > v6_whHzSmooth ? 0.040f : 0.010f;
            v6_whHzSmooth += (whTgt - v6_whHzSmooth) * ptau;
        }

        // [MODIFIED] Whine steps down drastically outside of 1st gear launch phase
        float gearMul = 1.0f;
        if (gear == 2 || gear == 3) gearMul = 0.38f; 
        else if (gear >= 4)         gearMul = 0.18f;

        float whTarget = 0f;
        if (gear >= 1 && rpm > IDLE * 0.9f)
        {
            whTarget = (0.020f + rn * 0.055f + ld * 0.030f) * gearMul;
            if (gear == 1) whTarget *= (0.55f + 0.45f * g1Progress);
            if (spd < 0.5f && accel < 0.03f) whTarget *= 0.25f;     
        }
        v6_whVolSmooth += (whTarget - v6_whVolSmooth) * 0.006f;


        float screechThresh = GOV * 0.86f;
        float screechRaw    = Mathf.Clamp01((Mathf.Max(rpm, IDLE) - screechThresh) / Mathf.Max(1f, GOV - screechThresh));
        v6_screechSmooth += (screechRaw - v6_screechSmooth) * (screechRaw > v6_screechSmooth ? 0.0035f : 0.008f);

        if (v6_whVolSmooth > 0.0008f && v6_whHzSmooth > 40f)
        {
            float rnPitch = Mathf.Clamp01((rpm - IDLE) / (GOV - IDLE));

            double p1 = Mathf.Lerp(1.75f, 2.45f, rnPitch);
            double p2 = Mathf.Lerp(1.90f, 2.75f, rnPitch);
            double p3 = Mathf.Lerp(2.10f, 3.10f, rnPitch);

            double w1 = Math.Sin(p1 * Math.PI * ph_v6_wh1);
            double w2 = Math.Sin(p2 * Math.PI * ph_v6_wh2);
            double w3 = Math.Sin(p3 * Math.PI * ph_v6_wh3);

            double chord = w1 * 1.00 + w2 * 0.48 + w3 * 0.24;

            if (v6_screechSmooth > 0.001f)
            {
                double w4 = Math.Sin(2.0 * Math.PI * ph_v6_wh4); 
                chord += w4 * 0.34 * v6_screechSmooth;
                chord += noise_hi * 0.22 * v6_screechSmooth;
            }

            txSample += (chord + noise_hi * 0.06) * v6_whVolSmooth * engMul;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  3. DIWA "BRRRRRRINGA" LAUNCH OVERTONE
    // ═════════════════════════════════════════════════════════════════════════
    if (gear == 1 && g1Progress > 0.88f && v6_ringaTimer <= 0f)
    {
        v6_ringaTimer = 3.0f;
        v6_ringaVol = 1.0f;
    }

    if (v6_ringaTimer > 0f)
    {
        v6_ringaTimer -= (float)invSR;
        float t = 1f - Mathf.Clamp01(v6_ringaTimer / 3f);
        float growlHz = Mathf.Lerp(58f, 215f, t);

        ph_v6_ringa1 = (ph_v6_ringa1 + growlHz * invSR) % 1.0;
        ph_v6_ringa2 = (ph_v6_ringa2 + growlHz * 1.45 * invSR) % 1.0;
        ph_v6_ringa3 = (ph_v6_ringa3 + growlHz * 2.90 * invSR) % 1.0;

        double low   = Math.Sin(2.0 * Math.PI * ph_v6_ringa1);
        double mid   = Math.Sin(2.0 * Math.PI * ph_v6_ringa2);
        double high  = Math.Sin(2.0 * Math.PI * ph_v6_ringa3);
        float rasp   = Mathf.Lerp(0.08f, 0.42f, t);

        double sound = low * 1.00 + mid * 0.38 + high * 0.12 + noise_lp * rasp + noise_hi * rasp * 0.10;
        sound = Math.Tanh(sound * (1.4 + t));
        float env = Mathf.Sin(t * Mathf.PI);

        txSample += sound * env * 0.085 * engMul;

        if (v6_ringaTimer <= 0f) v6_ringaVol = 0f;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  4. CONVERTER CHURN (FLUID HYDRAULIC BED)
    // ═════════════════════════════════════════════════════════════════════════
    {
        float churnTarget = 0f;
        if (gear == 1)
        {
            churnTarget = hydShare * (0.25f + ld * 0.75f);
            if (spd < 2f) churnTarget = Mathf.Max(churnTarget, 0.22f + ld * 0.30f);
        }
        v6_churnEnv += (churnTarget - v6_churnEnv) * (churnTarget > v6_churnEnv ? 0.010f : 0.004f);

        if (v6_churnEnv > 0.004f)
        {
            v6_churnLP += (noise_lp - v6_churnLP) * 0.22;
            double swirl = 0.80 + 0.20 * Math.Sin(2.0 * Math.PI * ph_v6_churnMod);
            txSample += v6_churnLP * swirl * v6_churnEnv * 0.11 * engMul;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  5. STOPPED-IDLE BODY SHAKE
    // ═════════════════════════════════════════════════════════════════════════
    {
        bool trueStop = gear <= 1 && spd < 2.2f && accel < 0.08f;
        float stopAmount = Mathf.Clamp01(1f - spd / 2.2f);
        float shakeTarget = trueStop ? stopAmount : 0f;

        v6_idleShakeEnv += (shakeTarget - v6_idleShakeEnv) * (shakeTarget > v6_idleShakeEnv ? 0.006f : 0.015f);

        if (v6_idleShakeEnv > 0.01f)
        {
            double sway = Math.Sin(2.0 * Math.PI * ph_v6_am1);
            double vibration = Math.Sin(2.0 * Math.PI * ph_v6_idleVib);
            double fire3 = Math.Sin(2.0 * Math.PI * ph_v6_fireHalf);

            double shakeSound = sway * 0.8 + vibration * 0.65 + fire3 * 0.35 + noise_lp * 0.35;
            shakeSound = Math.Tanh(shakeSound * 2.8);

            txSample += shakeSound * v6_idleShakeEnv * 0.13 * engMul;
        }

        v6_idleVibHz = Mathf.Lerp(18f, 55f, Mathf.Clamp01(1f - spd / 2f));
        ph_v6_idleVib = (ph_v6_idleVib + v6_idleVibHz * invSR) % 1.0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  6. RPM LOAD ENGINE VIBRATION
    // ═════════════════════════════════════════════════════════════════════════
    {
        float rpmProgress = Mathf.Clamp01((rpm - IDLE) / (GOV - IDLE));
        float rpmVibTargetHz = Mathf.Lerp(4f, 75f, Mathf.Pow(rpmProgress, 3f));

        v6_rpmVibHz += (rpmVibTargetHz - v6_rpmVibHz) * 0.02f;

        float targetVol = (gear == 1) ? Mathf.Pow(rpmProgress, 4f) * (0.18f + ld * 0.25f) : 0f;
        v6_rpmVibVol += (targetVol - v6_rpmVibVol) * 0.02f;

        if (v6_rpmVibVol > 0.001f)
        {
            double vib = Math.Sin(2.0 * Math.PI * ph_v6_rpmVib);
            double grit = noise_lp * rpmProgress * 0.7;
            double brrr = vib * 0.8 + grit;

            brrr = Math.Tanh(brrr * (1.0 + rpmProgress * 4));
            txSample += brrr * v6_rpmVibVol * engMul;
        }

        ph_v6_rpmVib = (ph_v6_rpmVib + v6_rpmVibHz * invSR) % 1.0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  7. MECHANICAL TRANSIT / PNEUMATIC SHIFT THUDS & SIGHS
    // ═════════════════════════════════════════════════════════════════════════
    if (gear != voith_lastGearAudio)
    {
        bool upFrom1 = (voith_lastGearAudio == 1 && gear == 2);
        voith_lastGearAudio = gear;
        v6_sighVol = upFrom1 ? 0.9f : 0.55f;
        v6_seatVol = upFrom1 ? 0.7f : 0.45f;
        if (upFrom1)
        {
            v6_churnEnv  = Mathf.Min(v6_churnEnv, 0.05f);
            v6_g1BuzzVol = 0f;
        }
        voith_shiftThud = 1.0f; ph_voith_thud1 = 0.0; ph_voith_thud2 = 0.0; // [FIX -- click bug] zero the thud's own phase on every envelope-open, kills the shift "tick"
    }

    if (v6_sighVol > 0.004f)
    {
        txSample += (noise_lp * 0.6 + noise_hi * 0.4) * v6_sighVol * 0.055 * engMul;
        v6_sighVol *= 0.9982f;
    }
    if (v6_seatVol > 0.004f)
    {
        txSample += Math.Sin(2.0 * Math.PI * ph_v6_seat) * v6_seatVol * 0.055 * engMul;
        v6_seatVol *= 0.9970f;
    }
    if (voith_shiftThud > 0f)
    {
        txSample += Math.Sin(2.0 * Math.PI * ph_voith_thud1) * voith_shiftThud * 0.22 * engMul;
        txSample += Math.Sin(2.0 * Math.PI * ph_voith_thud2) * voith_shiftThud * 0.12 * engMul;
        voith_shiftThud *= 0.9975f;
        if (voith_shiftThud < 0.004f) voith_shiftThud = 0f;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  8. MOVE-OFF TRANSITION PULL GRAB
    // ═════════════════════════════════════════════════════════════════════════
    {
        bool stoppedNow = spd < 0.4f;
        if (v6_wasStopped && !stoppedNow && accel > 0.05f) v6_moveOffTimer = 0.50f;
        v6_wasStopped = stoppedNow;

        if (v6_moveOffTimer > 0f)
        {
            v6_moveOffTimer -= (float)invSR;
            float t = Mathf.Clamp01(v6_moveOffTimer / 0.50f);
            float grabHz = 46f + (1f - t) * 18f;
            float judder = 0.5f + 0.5f * Mathf.Sin((float)audioClock * Mathf.PI * 2f * 10.5f);
            double grab  = (Math.Sin(2.0 * Math.PI * ph_v6_grab) * 0.8 + noise_lp * 0.4)
                           * t * (0.08f + judder * 0.05f) * (0.5f + ld * 0.5f) * engMul;
            txSample += grab;
            ph_v6_grab = (ph_v6_grab + grabHz * invSR) % 1.0;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  9. SHAKER CHASSIS MECHANICS (CREAKS & RATTLING MODULATION)
    // ═════════════════════════════════════════════════════════════════════════
    if (v6_isShaker)
    {
        float shakeLoad = Mathf.Clamp01(accel * 1.2f) * Mathf.Clamp01(1f - spd / 45f) + (spd < 1f ? 0.22f : 0f);
        if (shakeLoad > 0.02f)
        {
            double bodyv   = Math.Sin(2.0 * Math.PI * (ph_v6_shakeBody * 0.78));
            double tremorv = 0.55 + 0.45 * Math.Sin(2.0 * Math.PI * (ph_v6_shakeBody * 4.9));
            txSample += bodyv * tremorv * shakeLoad * 0.040 * engMul;
        }
        if (v6_creakVol < 0.01f && shakeLoad > 0.15f && (NextNoiseSample() * 0.5 + 0.5) > 0.99955)
        {
            v6_creakVol = 0.6f + (float)(NextNoiseSample() * 0.5 + 0.5) * 0.4f;
        }
        if (v6_creakVol > 0.003f)
        {
            double c1 = Math.Sin(2.0 * Math.PI * ph_v6_creak1);
            double c2 = Math.Sin(2.0 * Math.PI * ph_v6_creak2);
            txSample += Math.Tanh((c1 + c2 * 0.5) * 1.6) * v6_creakVol * 0.045 * engMul;
            double crHz = 320.0 + (1.0 - v6_creakVol) * 260.0;
            ph_v6_creak1 = (ph_v6_creak1 + crHz        * invSR) % 1.0;
            ph_v6_creak2 = (ph_v6_creak2 + crHz * 1.48 * invSR) % 1.0;
            v6_creakVol *= 0.9975f;
            if (v6_creakVol < 0.004f) v6_creakVol = 0f;
        }
    }

    // Faint engine oil gear pump tick
    if (spd < 3f)
    {
        float pumpVol = 0.004f * Mathf.Clamp01(1f - spd / 3f) * engMul;
        txSample += Math.Sin(2.0 * Math.PI * ph_v6_pump) * pumpVol;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  10. ELECTRIC BACKGROUND MOTOR DRONE (PITCH SCALED RESIDUAL BACKGROUND)
    // ═════════════════════════════════════════════════════════════════════════
    float xe_motorHzSmooth = (spd / MAX_SPD) * 720f; 
    if (xe_motorHzSmooth < 0.3f) xe_motorHzSmooth = 0f;

    float droneGearTarget = (gear == 1) ? 1.2f : 0.3f;
    v5_xeDroneSmooth += (droneGearTarget - v5_xeDroneSmooth) * (droneGearTarget > v5_xeDroneSmooth ? 0.15f : 0.05f);

    float motSpdFrac = Mathf.Clamp01(xe_motorHzSmooth / 720f);
    float continuousMotorVol = motSpdFrac > 0.001f
        ? (0.04f + motSpdFrac * 0.08f + ld * 0.04f) * engMul * (1f + ld * 0.35f) * v5_xeDroneSmooth
        : 0f;

    if (continuousMotorVol > 0.0005f)
    {
        txSample += Math.Sin(2.0 * Math.PI * ph_xe_mot1 * 0.85) * continuousMotorVol
                  + Math.Sin(2.0 * Math.PI * ph_xe_mot2 * 0.85 * 1.998 / 2.0) * continuousMotorVol * 0.4;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  11. RETARDER CORE ACOUSTICS & ROTATIONAL SHAFT WHINE
    // ═════════════════════════════════════════════════════════════════════════
    {
        float hissTarget = gear >= 1 ? Mathf.Clamp01(ld * 1.2f) : 0f;
        float hissLin = hissTarget * Mathf.Pow(10f, -85f / 20f) * engMul * 12f;
        if (hissLin > 0.00005f)
        {
            double hissTone = Math.Sin(2.0 * Math.PI * ph_vwhine_hiss);
            txSample += (hissTone * 0.3 + noise_hi * 0.7) * hissLin;
            ph_vwhine_hiss = (ph_vwhine_hiss + 6800.0 * invSR) % 1.0;
        }
    }

    if (retAct)
    {
        if (!voith_deepRetWasActive)
        {
            voith_deepRetTimer = 0f;
            voith_deepRetVol = 0f;
        }
        voith_deepRetTimer += (float)invSR;
        voith_deepRetVol += (1f - voith_deepRetVol) * 0.0025f;
    }
    else
    {
        voith_deepRetVol *= 0.996f;
        if (voith_deepRetVol < 0.002f)
        {
            voith_deepRetVol = 0f;
            voith_deepRetTimer = 0f;
        }
    }
    voith_deepRetWasActive = retAct;

    float age = Mathf.Clamp01(voith_deepRetTimer / 3.5f);
    
    // [MODIFIED] Drop fundamental frequency targets down for a deeper pitch sweep
    float baseHz = Mathf.Lerp(110f, 55f, age);
    float targetHz = baseHz + spd * 0.82f; 
    voithRetHz += (targetHz - voithRetHz) * ((retAct ? 0.05f : 0.12f) * 60f * (float)invSR);

    // [MODIFIED] Global attenuation scaling factor (* 0.58f) to keep it less loud
    float retVol = (0.02f + age * 0.22f) * (0.12f + bkPd * 0.26f) * engMul * 0.58f;
    double pulse = 0.70 + 0.30 * Math.Sin(2.0 * Math.PI * ph_retPulse);

    if (retVol > 0.0001f)
    {
        // [MODIFIED] Attenuated sine tones, augmented low noise bed, and pushed Tanh saturation to 3.8x for a hoarse, ragged fluid texture
        double low = Math.Sin(2.0 * Math.PI * ph_vRet);
        double second = Math.Sin(2.0 * Math.PI * ph_vRet2);
        double roar = low * 0.60 + second * 0.08 + noise_lp * (0.75 + age * 0.45) + noise_hi * (0.08 + age * 0.12);

        roar = Math.Tanh(roar * 3.8);
        txSample += roar * pulse * retVol;
    }

    float squeakEnv = Mathf.Clamp01((voith_deepRetTimer - 1.0f) / 2.0f);
    if (squeakEnv > 0.001f)
    {
        float squeakHz = 1350f + spd * 5f + bkPd * 120f;
        ph_vRetSqueak = (ph_vRetSqueak + squeakHz * invSR) % 1.0;
        double squeak = Math.Sin(2.0 * Math.PI * ph_vRetSqueak) + noise_hi * 0.06;
        txSample += squeak * (float)Math.Pow(squeakEnv, 2.5) * 0.015 * engMul;
    }

    float shake = Mathf.Clamp01(ld * 0.9f) * Mathf.Clamp01(rpm / 1200f);
    float bodyHz = Mathf.Lerp(8f, 16f, rn);
    float vibHz  = Mathf.Lerp(28f, 65f, rn);

    ph_body1 = (ph_body1 + bodyHz * invSR) % 1.0;
    ph_body2 = (ph_body2 + vibHz * invSR) % 1.0;
    ph_body3 = (ph_body3 + FHz(rpm) * 0.5 * invSR) % 1.0;

    float launchShake = gear == 1 ? Mathf.Pow(1f - g1Progress, 0.6f) : 0f;
    shake *= 1f + launchShake * 1.8f;

    if (shake > 0.001f)
    {
        double body = Math.Sin(2.0 * Math.PI * ph_body1);
        double vib  = Math.Sin(2.0 * Math.PI * ph_body2);
        double fire = Math.Sin(2.0 * Math.PI * ph_body3);
        double rumble = body * 1.00 + vib * 0.45 + fire * 0.22 + noise_lp * 0.30;
        txSample += Math.Tanh(rumble * 2.0) * shake * 0.09 * engMul;
    }

    // [MODIFIED] Muted the secondary tail whine in higher gear intervals
    float voiMwV = (spd > 1.5f) ? (0.012f + (spd / MAX_SPD) * 0.070f + ld * 0.012f) * engMul : 0f;
    if (gear == 2 || gear == 3) voiMwV *= 0.30f;
    else if (gear >= 4)         voiMwV *= 0.15f;

    double fVMw = outRPM * 0.58;
    double fVMw2 = fVMw * 2.0;
    if (voiMwV > 0f)
    {
        txSample += Math.Sin(2.0 * Math.PI * ph_vMw)  * voiMwV;
        txSample += Math.Sin(2.0 * Math.PI * ph_vMw2) * voiMwV * 0.35;
    }

    float retTHz = 205f + spd * 1.8f;
    // [MODIFIED] Reduced the peak volume of the secondary brake component to line up with the primary volume reduction
    float voiRetV = retAct ? (0.07f + bkPd * 0.06f) * engMul : 0f;
    if (voiRetV > 0f)
    {
        txSample += (2.0 * (ph_vRet - Math.Floor(ph_vRet + 0.5))) * voiRetV * 0.80;
        txSample += Math.Sin(2.0 * Math.PI * ph_vRet2) * voiRetV * 0.55;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  12. TORQUE CONVERTER WAIL (STOCK VOITH SIGNATURE CRY)
    // ═════════════════════════════════════════════════════════════════════════
    // A single rising cry, distinct from the D864.6art's two-stage windup/
    // hold scream — no staging here, just a smooth swell that only shows up
    // deep in G1 under real load and peaks right as the box is about to
    // kick to 2nd. Detuned pair riding above the drive-whine fundamental.
    {
        float wailGate   = (gear == 1) ? Mathf.Clamp01(Mathf.InverseLerp(0.55f, 0.95f, g1Progress)) : 0f;
        float wailTarget = wailGate * wailGate * (0.10f + ld * 0.18f);
        v6_wailSmooth += (wailTarget - v6_wailSmooth) * (wailTarget > v6_wailSmooth ? 0.010f : 0.020f);

        if (v6_wailSmooth > 0.001f)
        {
            double w1  = Math.Sin(2.0 * Math.PI * ph_v6_wail1);
            double w2  = Math.Sin(2.0 * Math.PI * ph_v6_wail2);
            double cry = w1 * 0.85 + w2 * 0.40 + noise_hi * 0.20;
            txSample += Math.Tanh(cry * 1.6) * v6_wailSmooth * 0.16 * engMul;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  13. GLOBAL PHASE ACCUMULATION
    // ═════════════════════════════════════════════════════════════════════════
    float fireHz2 = FHz(Mathf.Max(rpm, IDLE));
    ph_v6_wh1       = (ph_v6_wh1       + (double)v6_whHzSmooth          * invSR) % 1.0;
    ph_v6_wh2       = (ph_v6_wh2       + (double)v6_whHzSmooth  * 2.003 * invSR) % 1.0;
    ph_v6_wh3       = (ph_v6_wh3       + (double)v6_whHzSmooth  * 3.01  * invSR) % 1.0;
    ph_v6_wh4       = (ph_v6_wh4       + (double)v6_whHzSmooth  * 4.98  * invSR) % 1.0;
    ph_v6_rwh1      = (ph_v6_rwh1      + (double)v6_rwhHzSmooth         * invSR) % 1.0;
    ph_v6_rwh2      = (ph_v6_rwh2      + (double)v6_rwhHzSmooth * 2.01  * invSR) % 1.0;
    ph_v6_rwh3      = (ph_v6_rwh3      + (double)v6_rwhHzSmooth * 3.4   * invSR) % 1.0;
    ph_v6_fire      = (ph_v6_fire      + (double)fireHz2                * invSR) % 1.0;
    ph_v6_fireHalf  = (ph_v6_fireHalf  + (double)fireHz2 * 0.5          * invSR) % 1.0;
    ph_v6_am1       = (ph_v6_am1       + 2.4                            * invSR) % 1.0;
    ph_v6_am2       = (ph_v6_am2       + 9.8                            * invSR) % 1.0;
    ph_v6_churnMod  = (ph_v6_churnMod  + 1.7                            * invSR) % 1.0;
    ph_v6_buzz      = (ph_v6_buzz      + 33.0                           * invSR) % 1.0;
    ph_v6_seat      = (ph_v6_seat      + 55.0                           * invSR) % 1.0;
    ph_v6_shakeBody = (ph_v6_shakeBody + 34.0                           * invSR) % 1.0;
    ph_v6_pump      = (ph_v6_pump      + (double)(rpm / 60f) * 9.0      * invSR) % 1.0;
        
    ph_xe_mot1      = (ph_xe_mot1      + (double)xe_motorHzSmooth       * invSR) % 1.0;
    ph_xe_mot2      = (ph_xe_mot2      + (double)xe_motorHzSmooth * 2.0 * invSR) % 1.0;
    ph_vRet         = (ph_vRet         + (double)voithRetHz             * invSR) % 1.0;
    ph_vRet2        = (ph_vRet2        + (double)voithRetHz * 1.92      * invSR) % 1.0;
    ph_retPulse     = (ph_retPulse     + 2.8                            * invSR) % 1.0;
    ph_vMw          = (ph_vMw          + fVMw                           * invSR) % 1.0;
    ph_vMw2         = (ph_vMw2         + fVMw2                          * invSR) % 1.0;
    ph_v6_wail1     = (ph_v6_wail1     + (double)v6_whHzSmooth * 1.35   * invSR) % 1.0;
    ph_v6_wail2     = (ph_v6_wail2     + (double)v6_whHzSmooth * 1.70   * invSR) % 1.0;
}
// ── Second converter voice — the fixed tone ───────────────────────────────
//  Standalone by design. Called from DoVoithDSP OUTSIDE the !diwaOpt1_5
//  whine block, so the shaker variant ("no whine AT ALL") still keeps this
//  tone — the recording shows it present continuously regardless of what
//  the mesh is doing, so it is not part of the whine and must not be gated
//  with it. Not gated on gear and not gated on ANS either, for the same
//  reason: the clip has it running unbroken through every shift and through
//  the whole decel, which is exactly what makes it read as a separate unit
//  rather than a converter byproduct.
// [SUPERSEDED — see DoWindup40GrowlDSP below] First pass at the 40ft wind-up
// used a stepped whine (measured steps: 211/234/258/281/305/328/352/375/
// 398/422 Hz, ~0.2-0.4s dwell each). Replaced per instruction with the
// "brrrring"-mechanism growl below instead.

// Reworked per instruction to use the same mechanism as section 3's DIWA
// "BRRRRRRINGA" launch overtone (detuned 3-harmonic stack -> tanh saturation
// -> noise grit) instead of a stepped whine -- that's what gives the motor-
// cycle-like buzz/growl character, not a clean sine. Same shape, just a
// slower ramp (6s instead of ringa's 3s) so it reads as the wind-up building
// rather than the short post-shift flare ringa does.
private float  windup40_ringaLikeTimer = 0f;
private double ph_windup40_g1, ph_windup40_g2, ph_windup40_g3;
private double ph_windup40_hi, ph_windup40_hiSB; // [NEW] H50EP-style carrier + sideband
private const float WINDUP40_GROWL_LO_HZ   = 58f;   // same floor as ringa
private const float WINDUP40_GROWL_HI_HZ   = 215f;  // same ceiling as ringa
private const float WINDUP40_GROWL_DUR_SEC = 9.0f;  // [LONGER] was 6.0f, up from ringa's 3.0f -- starts earlier and lasts longer, per instruction
private const float WINDUP40_HI_BASE_HZ    = 1400f; // [NEW] carrier floor, well below H50EP's 3600-4700Hz -- this is a blended texture, not a literal inverter whine
private const float WINDUP40_HI_TOP_HZ     = 3100f; // [NEW] carrier ceiling at full wind-up
private const float WINDUP40_HI_SB_OFFSET  = 5.5f;  // [NEW] fixed Hz offset for the sideband -- same trick as ph_epv2_invSB/ph_ep_gen5Blend: a small fixed offset against the carrier is what makes it beat/shimmer instead of sitting flat

private void DoWindup40GrowlDSP(ref double txSample, float rn, float ld, float engMul,
                                 bool isArticulated, float chassisLenFt, int gear, double invSR)
{
    // Gate: 40ft, non-articulated, gear 1 only -- same reasoning as before.
    if (isArticulated || chassisLenFt > 45f || gear != 1)
    {
        windup40_ringaLikeTimer = 0f;
        return;
    }

    // Starts building as soon as gear 1 begins (unlike ringa, which only
    // fires once near the top of g1) -- this IS the wind-up, not a launch
    // flourish tacked onto the end of it. [EARLIER] Threshold dropped from
    // 0.03 to 0.01 so it catches gear 1 almost immediately instead of
    // waiting for rn to build up first.
    if (windup40_ringaLikeTimer <= 0f && rn > 0.01f)
        windup40_ringaLikeTimer = WINDUP40_GROWL_DUR_SEC;

    if (windup40_ringaLikeTimer <= 0f) return;

    windup40_ringaLikeTimer -= (float)invSR;
    float t = 1f - Mathf.Clamp01(windup40_ringaLikeTimer / WINDUP40_GROWL_DUR_SEC);
    float growlHz = Mathf.Lerp(WINDUP40_GROWL_LO_HZ, WINDUP40_GROWL_HI_HZ, t);

    ph_windup40_g1 = (ph_windup40_g1 + growlHz        * invSR) % 1.0;
    ph_windup40_g2 = (ph_windup40_g2 + growlHz * 1.45 * invSR) % 1.0;
    ph_windup40_g3 = (ph_windup40_g3 + growlHz * 2.90 * invSR) % 1.0;

    double low  = Math.Sin(2.0 * Math.PI * ph_windup40_g1);
    double mid  = Math.Sin(2.0 * Math.PI * ph_windup40_g2);
    double high = Math.Sin(2.0 * Math.PI * ph_windup40_g3);
    float rasp  = Mathf.Lerp(0.08f, 0.42f, t);

    double sound = low * 1.00 + mid * 0.38 + high * 0.12 + noise_lp * rasp + noise_hi * rasp * 0.10;

    // [NEW] H50EP-style carrier + sideband, folded INTO the same sound
    // before saturation rather than added after -- that's what makes it
    // read as "blended and tuned" into the growl instead of a separate
    // whine bolted on top. Carrier pitch rises with t (same ramp driving
    // the growl), sideband sits a small fixed 5.5Hz above it for shimmer,
    // exactly like ep_invHzSmooth/ph_epv2_invSB do on the real H50EP.
    float hiHz = Mathf.Lerp(WINDUP40_HI_BASE_HZ, WINDUP40_HI_TOP_HZ, t);
    double hiCarrier  = Math.Sin(2.0 * Math.PI * ph_windup40_hi);
    double hiSideband = Math.Sin(2.0 * Math.PI * ph_windup40_hiSB);
    float hiVol = Mathf.Lerp(0.10f, 0.30f, t); // quiet at first, blends in more as it winds up
    sound += hiCarrier * hiVol + hiSideband * hiVol * 0.45;

    ph_windup40_hi   = (ph_windup40_hi   + hiHz                            * invSR) % 1.0;
    ph_windup40_hiSB = (ph_windup40_hiSB + (hiHz + WINDUP40_HI_SB_OFFSET)  * invSR) % 1.0;

    sound = Math.Tanh(sound * (1.4 + t));

    float vol = Mathf.Clamp01(rn * 1.4f) * 0.085f * engMul;

    // [NEW] Louder at the start, leveling off after -- a quick attack spike
    // that decays back down to the steady-state level within the first
    // ~0.8s of the ramp (t goes 0->1 over the full 9s, so 0.8s is t≈0.09),
    // rather than the volume just building steadily off rn the whole way.
    float startBoost = Mathf.Lerp(1.8f, 1.0f, Mathf.Clamp01(t / 0.09f));
    vol *= startBoost;

    txSample += sound * vol;

    if (windup40_ringaLikeTimer <= 0f) windup40_ringaLikeTimer = 0f;
}

private void DoVoithFixedToneDSP(ref double txSample, float ld, float engMul, double invSR)
{
    // Measured centre frequency across the full clip.
    const double FIX_HZ = 633.0;

    // Measured std was 24.9 Hz (~3.9% of 633). That is not tracking drift --
    // it wanders slowly and aimlessly, the way a free-running auxiliary does
    // as its load and oil temperature move around. Two slow LFOs at
    // non-harmonic rates so the wander never falls into an obvious cycle.
    double wob = Math.Sin(2.0 * Math.PI * ph_v6_fixWob) * 0.024
               + Math.Sin(2.0 * Math.PI * ph_v6_fixWob * 2.7 + 1.1) * 0.011;
    double fixHz = FIX_HZ * (1.0 + wob);
    ph_v6_fixWob = (ph_v6_fixWob + 0.13 * invSR) % 1.0;

    // Volume: essentially constant. Only a light load term, since a cooling/
    // charge circuit does work a little harder under load, and a small idle
    // taper so a parked bus is not buzzing at full level.
    float fixTarget = 0.030f + ld * 0.012f;
    if (rpm < IDLE * 1.05f && spd < 0.5f) fixTarget *= 0.62f;
    v6_fixVolSmooth += (fixTarget - v6_fixVolSmooth) * 0.004f;

    if (v6_fixVolSmooth > 0.0006f)
    {
        // Three partials. The measured peak is narrow and strong (25-38 dB
        // prominence) with weaker energy at the octave, so: a dominant
        // fundamental, a soft octave for body, and a quiet sub an octave
        // down to stop it sounding like a thin test tone sitting on top of
        // the mix. Slight detune on the octave gives a gentle beat rather
        // than a sterile stack.
        double a1 = Math.Sin(2.0 * Math.PI * ph_v6_fix1);
        double a2 = Math.Sin(2.0 * Math.PI * ph_v6_fix2);
        double a3 = Math.Sin(2.0 * Math.PI * ph_v6_fixSub);

        double tone = a1 * 1.00 + a2 * 0.26 + a3 * 0.14;

        txSample += tone * v6_fixVolSmooth * engMul;

        ph_v6_fix1   = (ph_v6_fix1   + fixHz         * invSR) % 1.0;
        ph_v6_fix2   = (ph_v6_fix2   + fixHz * 2.004 * invSR) % 1.0;
        ph_v6_fixSub = (ph_v6_fixSub + fixHz * 0.5   * invSR) % 1.0;
    }
}

private void DoVoithDSP(ref double engineSample, ref double txSample, float rn, float ld, float hz,
                         float outRPM, float engMul, bool retAct, double invSR)
{
    // ═════════════════════════════════════════════════════════════════════════
    //  [FIX — real continuity bug] D864.5 used to run its own separate,
    //  wholly independent DSP (DoD8645DSP) instead of D864.6's. Per spec:
    //  864.5 is supposed to have the SAME DSP/audio as 864.6 -- New Flyer's
    //  smart electronics (the diwaOpt1_4+ option layers) only ever shipped
    //  paired with 864.6 hardware, so an 864.5-equipped bus should sound
    //  IDENTICAL to a base, no-opt 864.6 (i.e. the same as an XD60), just
    //  with an older/sharper RPM+vibration character on top (handled by the
    //  caller passing 864.5-tuned rn/outRPM/hz already, via CalcD8645RPM/
    //  DoD8645Range -- those stay separate on purpose, since THAT part is
    //  meant to stay distinct). This function is now the single shared
    //  voice for both -- DoD8645DSP is orphaned (left in place, unused,
    //  rather than deleted, in case anything else still references it).
    //
    //  The guard below is defensive, not decorative: rather than trust that
    //  no fleet config ever accidentally sets a diwaOpt1_x flag on an
    //  864.5-equipped bus, it forcibly zeroes all four opt flags for the
    //  duration of this call whenever tx is genuinely "d8645", then
    //  restores them before returning -- so an 864.5 CANNOT run any opt
    //  layer through this function no matter what upstream state says.
    // ═════════════════════════════════════════════════════════════════════════
    bool legacy854 = tx == "d8645";
    bool savedOpt4 = diwaOpt1_4, savedOpt5 = diwaOpt1_5, savedOpt6 = diwaOpt1_6, savedOpt7 = diwaOpt1_7, savedOpt8 = diwaOpt1_8;
    if (legacy854) { diwaOpt1_4 = diwaOpt1_5 = diwaOpt1_6 = diwaOpt1_7 = diwaOpt1_8 = false; }

    // ═════════════════════════════════════════════════════════════════════════
    //  1. LOCAL VARIABLE DECLARATIONS & SHAKER INITIALIZATION
    // ═════════════════════════════════════════════════════════════════════════
    double ph_v6_idleVib = 0;
    float v6_idleVibHz = 0;
    float v6_rpmVibHz = 0;
    float v6_rpmVibVol = 0;

    if (!v6_shakerInit)
    {
        v6_shakerInit = true;
        v6_isShaker = (NextNoiseSample() * 0.5 + 0.5) < 0.18;
    }

    // [NEW] Startup electric windup -- fires once on the Cranking->Running
    // edge, ahead of everything else in this function so it's never gated
    // behind gear/opt1_4/shaker state. See DoVoithStartupWindup in
    // BusAudioEngine.d8646.cs.
    DoVoithStartupWindup(ref txSample, engineState == EngineRunState.Running, engMul, invSR);

    // [RETUNED, PER REQUEST] Keep the H4x MG-A/MG-B slip/warble voice
    // (DoH50EPSlipVoice) on Voith, but gear-1-only and driven by GEAR 1's
    // own rpm instead of road-speed wheelRev -- H4xUpdateSlipModel's
    // once-per-rev thump (the only place its wheelRev argument is actually
    // used) now tracks the real engine rev rate the gear-1 whine itself
    // already tracks (rpm), not the output shaft, so the slip voice's pitch
    // relationship is tied to G1 specifically rather than generic road
    // speed. Barely present in any other gear -- v6_slipGearPresence is a
    // smoothed 1.0 (gear 1) / ~0.06 (everywhere else) multiplier so gear
    // changes don't click.
    {
        float presenceTarget = gear == 1 ? 1.0f : 0.06f;
        v6_slipGearPresence += (presenceTarget - v6_slipGearPresence) * 0.05f;
        float g1Rev = Mathf.Max(rpm, IDLE) / 60f; // engine rev/s -- the "rpm already in existence", tuned to G1 via presence gating rather than a separate curve
        DoH50EPSlipVoice(ref txSample, g1Rev, engMul * v6_slipGearPresence, invSR, 0.33f); // [TUNED] ported copy sits further back than the native voice
    }

    // g1Top scales with actual kickdown without altering global configuration thresholds
    float g1Top      = VOITH_GEAR1_UPSHIFT_SPD * 1.15f * (kickdownKey ? 1.32f : 1f);
    float g1Progress = gear == 1 ? Mathf.Clamp01(spd / Mathf.Max(1f, g1Top)) : 0f;
    float hydShare   = gear == 1 ? (1f - g1Progress) * (1f - g1Progress) : 0f;

    // [ADD] Articulated response -- this is what used to be D8646Art's whole
    // separate identity, folded into plain Voith/D8646 itself now that the
    // dedicated artic-only tx string is gone. A 60ft artic's Voith runs a
    // deeper, more resonant character under load (more mass hanging off the
    // same converter, longer driveline to the rear axle) -- NOT a different
    // gear-mesh whine, just a heavier body/retarder voice underneath it.
    float articDeepen = isArticulatedEngine ? Mathf.Lerp(1.0f, 1.22f, Mathf.Clamp01(ld)) : 1.0f;
    engMul *= articDeepen; // propagates the deeper artic character through every gain stage in this function, not just the fixed tone below

    // ═════════════════════════════════════════════════════════════════════════
    //  1b. SECOND CONVERTER VOICE — FIXED TONE (measured, ~633Hz)
    // ═════════════════════════════════════════════════════════════════════════
    // Deliberately called HERE, above the !diwaOpt1_5 gate, so the shaker
    // variant keeps this tone even though it has no mesh whine at all. In
    // the real recording this tone runs unbroken through every gear, every
    // shift and the whole decel, so it is not part of the whine voice.
    DoVoithFixedToneDSP(ref txSample, ld, engMul, invSR);

    // ═════════════════════════════════════════════════════════════════════════
    //  1c. OPT1_4 — RETARDER GROAN + HISS WINDOW + AUDIBLE PISTON FIRING
    //  [MOVED per instruction] — both opt1_4 layers below used to run late
    //  in the function (old sections 11/14, after the drive whine). Moved
    //  up here so they establish before DoDiwaWhine/section 2 runs, same
    //  as everything else opt1_4 changes about the base voice.
    // ═════════════════════════════════════════════════════════════════════════

    // ── Retarder groan -- per instruction: NOT tied to retAct (braking) at
    // all, a separate low groaning texture keyed purely to RPM/gear
    // position. Fades in near the end of any gear as RPM climbs toward the
    // upshift point, and is unconditionally present through ALL of gear 2
    // specifically (not just fading in there like every other gear).
    // [FIX -- continuity] Universal for ANY opt now (opt1_4 through
    // opt1_7), not just opt1_4 -- per instruction, only the true no-opt
    // original should be without retarder groan.
    if (diwaOpt1_4 || diwaOpt1_5 || diwaOpt1_6 || diwaOpt1_7)
    {
        float groanTarget;
        if (gear == 2)
            groanTarget = 0.7f;
        else if (gear >= 1)
            groanTarget = Mathf.Clamp01((rn - 0.70f) / 0.30f) * 0.7f;
        else
            groanTarget = 0f;

        v6_opt4RetGroanVol += (groanTarget - v6_opt4RetGroanVol) * (groanTarget > v6_opt4RetGroanVol ? 0.012f : 0.006f);
        if (v6_opt4RetGroanVol > 0.001f)
        {
            double rg1 = Math.Sin(2.0 * Math.PI * ph_v6_opt4RetGroan1);
            double rg2 = Math.Sin(2.0 * Math.PI * ph_v6_opt4RetGroan2);
            double groan = Math.Tanh((rg1 * 0.7 + rg2 * 0.4 + noise_lp * 0.4) * 1.8);
            txSample += groan * v6_opt4RetGroanVol * 0.09 * engMul;
        }
        ph_v6_opt4RetGroan1 = (ph_v6_opt4RetGroan1 + 55.0       * invSR) % 1.0;
        ph_v6_opt4RetGroan2 = (ph_v6_opt4RetGroan2 + 55.0 * 1.5 * invSR) % 1.0;
    }

    // ── Hiss window (75%-97% of gear 1) + audible individual piston firing.
    // [RENAME] fireHz2 -> fireHz2Pre1c here only: the section-13 fireHz2
    // local still gets declared later in this function for the stopped-idle
    // shake, and C# won't allow the same name declared twice in one method
    // body even in a nested block, so this copy is intentionally distinct.
    if (diwaOpt1_4 && gear == 1)
    {
        float hissIn  = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.75f, 0.80f, g1Progress));
        float hissOut = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.94f, 0.97f, g1Progress));
        float hissEnv = hissIn * hissOut;

        if (hissEnv > 0.001f)
        {
            double rawH   = NextNoiseSample();
            v6_hissLP     = v6_hissLP * 0.55 + rawH * 0.45;
            double hp1    = rawH - v6_hissLP;          // high-passed
            double hp2    = hp1 - v6_hissPrev;         // differentiated edge
            v6_hissPrev   = hp1;
            double sharpN = hp1 * 0.55 + hp2 * 0.45;

            double hissTone = Math.Sin(2.0 * Math.PI * ph_v6_opt4Hiss);
            double hissMix  = hissTone * 0.22 + sharpN * 1.35;

            txSample += hissMix * hissEnv * (0.05f + ld * 0.04f) * engMul;
            ph_v6_opt4Hiss = (ph_v6_opt4Hiss + 6400.0 * invSR) % 1.0;
        }

        float revHard = Mathf.Clamp01((rn - 0.35f) / 0.5f) * Mathf.Clamp01(ld * 1.3f);
        float fireHz2Pre1c = FHz(Mathf.Max(rpm, IDLE));
        if (revHard > 0.01f && fireHz2Pre1c > 1f)
        {
            double frac = ph_v6_opt4Fire;
            double decay = Math.Exp(-frac * 9.0);
            double click = decay * (Math.Sin(2.0 * Math.PI * frac * 3.0) * 0.6 + (NextNoiseSample() * 0.5 + 0.5) * 0.4 * 2.0 - 1.0);
            engineSample += click * revHard * 0.09 * engMul;
            ph_v6_opt4Fire = (ph_v6_opt4Fire + fireHz2Pre1c * invSR) % 1.0;
        }
    }

    // ── [NEW] 40ft wind-up growl -- see DoWindup40GrowlDSP above (replaces
    // the earlier stepped-whine version per instruction: same brrrring
    // mechanism as section 3, just a slower 6s ramp instead of steps).
    // TODO: no chassisLenFt/is40ft field exists in this file yet -- wire one
    // up via FleetSeriesDefinition (same place isArticulatedEngine gets set)
    // and pass it here instead of the "40f" placeholder.
    DoWindup40GrowlDSP(ref txSample, rn, ld, engMul, isArticulatedEngine, 40f, gear, invSR);

    // ═════════════════════════════════════════════════════════════════════════
    //  2. DRIVE WHINE (DIWA GEAR MESH VOICE)
    // ═════════════════════════════════════════════════════════════════════════
    // opt1_5 ("the shaker") hard-bypasses this whole section -- per spec
    // "no whine AT ALL for this one", not suppressed like opt1_4.
    if (!diwaOpt1_5)
    {
        float teeth = gear <= 1 ? 23.0f : 21.5f; // [LOWERED] this IS the base pitch driver (whTgt = rpm/60*teeth) -- the envelope pitch-swing is only a small modifier on top of this, so reverting teeth back to 23.0/21.5 pushed the whole register back up regardless of the envelope tweak
        // [REVERTED] The tooth-order pitch-drop trick wasn't the right
        // idea -- whTgt tracks rpm naturally again, rising with revs like
        // every other gear here. "Deepen" now means genuine ADDED low-end
        // content under the whine during the kickdown hold (see the growl
        // layer down by the whine's txSample write, tagged [KICKDOWN
        // DEEPEN]), not an artificial downward pitch-shift of the whine's
        // own fundamental.
        float whTgt = (Mathf.Max(rpm, IDLE) / 60f) * teeth;

        // ═════════════════════════════════════════════════════════════════
        //  [NEW — real DIWA gear-1 converter physics] Straight from the
        //  Voith DIWA.6 Technical Manual's 1st-gear power-flow section:
        //  at launch the pump impeller is driven "with multiple engine
        //  speed" through the input differential, and then -- critically --
        //  "as the vehicle speed increases, i.e. with increasing speed of
        //  output shaft (i), the speed of the sun gear (o) in the input
        //  differential (B), DRIVING THE PUMP IMPELLER (P), DECREASES."
        //
        //  [UPDATE] The manual describes the sun-gear/impeller speed
        //  FALLING as output shaft speed rises through gear 1 -- that was
        //  the original basis for this section. But checked directly
        //  against a real L9N+Voith recording (frame-by-frame within the
        //  actual gear-1 window, not just the manual's description), the
        //  whine's peak frequency was heard/measured RISING through the
        //  early launch instead. Reversed below to match what's actually
        //  audible on a real bus rather than the manual's literal
        //  mechanism description -- see the reasoning at the Lerp call.
        //
        //  Only gear 1 is affected: in 2nd the pump brake physically stops
        //  the impeller (see the gear-2 suppression below), and 3rd/4th are
        //  purely mechanical (lock-up clutch / overdrive clutch), so there
        //  is no converter pitch to speak of in any of them.
        // ═════════════════════════════════════════════════════════════════
        float g1EnvVol = 1.0f; // [NEW] scoped here so the gear<=1 block below can set it, and whTarget further down can read it
        if (gear <= 1)
        {
            // [REBUILT #2 — measured against real XN60/L9N/D864.6 recording]
            // Previous version drove BOTH volume and pitch off a timer.
            // Measured gear 1 is 2.84s, not the 5.75s the old envelope ran,
            // and the whine rises right up to the shift (peak at 97% of the
            // window) rather than plateauing. Volume stays time-driven (that
            // shape was tuned by ear and is kept); PITCH now comes off road
            // speed, which is the shaft the mesh whine actually tracks.
            // See VoithD8646_AnalysisNotes.md for the full measurement.
            if (!v6_wasInGear1) { v6_g1EnvTimer = 0f; }
            v6_wasInGear1 = true;
            v6_g1EnvTimer += (float)invSR;

            // ── opt1_4 EXTENDED GEAR 1 — stretch, don't climb ─────────────
            //  opt1_4 holds gear 1 for 1.5x longer (opt1_4G1Extend in
            //  DoVoithRange). Previously that extra time was just more rpm
            //  climb, so the whine ran up past where a normal gear 1 ever
            //  reaches and went shrill.
            //
            //  Instead the SAME gear-1 curve now lasts the extended time:
            //  progress is measured against the extended upshift point so
            //  the pitch rise is stretched across the longer window rather
            //  than finishing early and then overshooting, and a matching
            //  compensation cancels the extra rpm climb so the ceiling
            //  lands where a normal gear 1 would have topped out.
            float g1Ext       = diwaOpt1_4 ? V6_OPT4_G1_EXTEND : 1.0f;
            float g1PitchProg = Mathf.Clamp01(spd / Mathf.Max(1f, g1Top * g1Ext));

            // ── VOLUME envelope — shape unchanged, durations corrected ────
            // near-silent -> builds bold -> short held-higher stretch, now
            // retimed from 5.00s+0.75s to 2.35s+0.50s so the whole shape
            // completes inside the real 2.84s gear-1 window instead of being
            // guillotined mid-build (the old "EEEE" hold was never actually
            // being reached in normal driving). Stretched by g1Ext under
            // opt1_4 so the shape still lands on the shift.
            float buildT = Mathf.Clamp01(v6_g1EnvTimer / (V6_G1_BUILD_SEC * g1Ext));
            float holdT  = Mathf.Clamp01((v6_g1EnvTimer - V6_G1_BUILD_SEC * g1Ext) / (V6_G1_HOLD_SEC * g1Ext));

            float envVol = Mathf.Lerp(0.06f, 1.0f, buildT * buildT);
            envVol = Mathf.Lerp(envVol, 1.15f, holdT);
            g1EnvVol = envVol;

            // ── PITCH — road-speed driven (the real correction) ───────────
            // whTgt above is (rpm/60)*teeth, i.e. ENGINE speed. In a real
            // DIWA gear 1 the engine sits roughly flat near its converter
            // stall point while the converter absorbs the difference, and
            // it's the OUTPUT/turbine side that speeds up with road speed --
            // so the mesh whine climbs while engine rpm barely moves. That
            // wrong-shaft tracking is the whole reason a fudge multiplier
            // was needed here at all.
            //
            // Measured span is 1344.3 -> 1528.1 Hz (1.1367); the old
            // envPitch could only produce ~1.5%. Linear, not eased:
            // measured slope is +74.8 Hz/s on a ~1344 Hz base over 2.84s.
            float envPitch = Mathf.Lerp(1.0f, V6_G1_PITCH_SPAN, g1PitchProg);
            whTgt *= envPitch;

            // Cancel the extra rpm the extended gear accumulates, so a
            // stretched gear 1 ends lower than it otherwise would --
            // longer, not shriller. A full 1/g1Ext cancel overshot and
            // pulled the whine BELOW its own starting pitch, so this is a
            // partial droop instead, exposed as a constant to dial by ear.
            // No-op when g1Ext is 1.
            if (g1Ext > 1.0f)
                whTgt *= Mathf.Lerp(1.0f, V6_OPT4_G1_PITCH_DROP, g1PitchProg);

            // [REMOVED, PER REQUEST] The H50EP whine transplant
            // (DoH50EPWhineLayer) didn't work -- pulled entirely. The slip
            // voice (DoH50EPSlipVoice, called near the top of this function)
            // is what's replacing it, now gear-1-gated and G1-rpm-driven.

            // Individual firing pulses -- unchanged. One smooth rounded bump
            // per engine firing event (max(0,sin)^3 gives ONE bump per cycle,
            // not two), riding on the whine's own amplitude. At low hz the
            // bumps are far enough apart to hear individually; as hz climbs
            // they blend into one smooth buzz.
            double fireBump = Math.Pow(Math.Max(0.0, Math.Sin(2.0 * Math.PI * ph_v6_firePulse)), 3.0);
            v6_firePulseMod = 1.0f + (float)fireBump * 0.55f;
            ph_v6_firePulse = (ph_v6_firePulse + hz * invSR) % 1.0;
        }
        else
        {
            v6_wasInGear1 = false;
            v6_firePulseMod = 1.0f;
        }

        // [NEW] 2nd gear -- the pump brake PB physically locks the pump
        // impeller and the turbine is disconnected from the power flow
        // entirely ("thus stopping the pump impeller and deactivating the
        // hydrodynamic power transmission"). The converter genuinely stops
        // contributing, rather than just getting quieter, so this is a hard
        // suppression rather than a taper.
        if (gear == 2) whTgt *= 0.12f;

        // ── Upshift whine transition — measured on XN40 D864.6 ref (09-06
        //    clip): G1->G2 @14.2s is a TRUE vertical cutoff (no glide, no
        //    hold at all); G2->G3 @~17s is a fast STEP-AND-RELOCK, ~150-250ms
        //    -- the fundamental audibly climbs to its new value rather than
        //    jumping. One clean data point per transition type, so only
        //    these two are differentiated; anything else keeps the old
        //    hold+snap behavior. See v6_whRelockWin's field comment.
        bool isG1toG2 = (v6_lastGear == 1 && gear == 2);
        if (v6_lastGear != -1 && v6_lastGear != gear)
        {
            if (isG1toG2)
            {
                v6_whHoldTimer = 0f;
                v6_whHzSmooth  = whTgt;     // hard cutoff, matches the measured drop exactly
            }
            else
            {
                v6_whHoldTimer = 0.20f;     // brief flat hold, then...
                v6_whHoldHz    = v6_whHzSmooth;
                v6_whRelockWin = 0.22f;     // ...a ~220ms audible relock, not an instant snap
            }
        }
        v6_lastGear = gear;

        if (v6_whHoldTimer > 0f)
        {
            v6_whHoldTimer -= (float)invSR;
            v6_whHzSmooth   = v6_whHoldHz; 
        }
        else if (v6_whRelockWin > 0f)
        {
            v6_whRelockWin -= (float)invSR;
            float relockPtau = (float)invSR / 0.20f;   // ~200ms time constant
            v6_whHzSmooth += (whTgt - v6_whHzSmooth) * relockPtau;
        }
        else
        {
            float ptau = whTgt > v6_whHzSmooth ? 0.040f : 0.010f;
            v6_whHzSmooth += (whTgt - v6_whHzSmooth) * ptau;
        }

        // [MODIFIED] Whine steps down drastically outside of 1st gear launch phase
        float gearMul = 1.0f;
        if (gear == 2 || gear == 3) gearMul = 0.38f; 
        else if (gear >= 4)         gearMul = 0.18f;

        float whTarget = 0f;
        if (gear >= 1 && rpm > IDLE * 0.9f)
        {
            whTarget = (0.017f + rn * 0.047f + ld * 0.026f) * gearMul; // [TRIMMED] whine volume pulled down ~15% overall
            // [FIX] Replaced the old g1Progress-based shaping here with
            // g1EnvVol (the timed envelope built specifically to replace
            // it -- see the [REBUILT] comment above). g1EnvVol is 1.0 for
            // any gear other than 1, so this is a no-op outside gear 1.
            if (gear == 1) whTarget *= g1EnvVol;
            if (spd < 0.5f && accel < 0.03f) whTarget *= 1.75f;

            // [NEW — ANS] With the input clutch (EK) disengaged, the engine
            // is no longer driving the converter at all, so the converter
            // whine genuinely stops rather than just quieting down. This is
            // the audible signature of the whole feature: at an ANS stop the
            // drivetrain goes notably quieter than a normal in-gear stop.
            if (voith_ansActive) whTarget *= 0.05f;

            // opt1_4 -- per video analysis, whine reads as "less present
            // until the end of gear 1" then "barely any whine" after the
            // shift. Not everyone sounds the same -- this is the second
            // character variant, layered on top of the base G1 progress
            // shaping above rather than replacing it: suppressed hard
            // until ~85% through G1 (smoothstep ramp-in over the last
            // 15%), and gear 2+ gets almost nothing (0.08x on top of the
            // already-reduced gearMul), since the reference audio has the
            // whine essentially vanish once gear 2 starts.
            if (diwaOpt1_4)
            {
                if (gear == 1)
                {
                    // [REWORKED] Was suppressed until 85% through gear 1 and
                    // then ramped in over the last 15% -- so the converter
                    // whine only appeared right before the shift, arriving
                    // late and abruptly.
                    //
                    // Now it starts near the beginning of gear 1 and fades
                    // in across roughly the first half. Two things make the
                    // fade land properly rather than switching on: the
                    // window is far wider than the old 0.85-1.00 (so the
                    // ramp has real time to breathe), and it's squared on
                    // top of the smoothstep, which keeps the first part of
                    // the fade very quiet and puts the audible swell in the
                    // middle instead of at the instant it unmutes.
                    float fade = Mathf.SmoothStep(0f, 1f,
                                    Mathf.InverseLerp(V6_OPT4_WHINE_IN_LO,
                                                      V6_OPT4_WHINE_IN_HI,
                                                      g1Progress));
                    // [FIX] Overall quieter per instruction -- same fade
                    // shape, lower ceiling.
                    whTarget *= fade * fade * V6_OPT4_WHINE_QUIETER;
                }
                else
                {
                    // [FIX] Same quieter multiplier applied here too, so
                    // "barely any" stays proportionally just as faint
                    // relative to the now-quieter gear-1 whine.
                    whTarget *= 0.08f * V6_OPT4_WHINE_QUIETER;
                }
            }
        }
        // ── opt1_8 "STRAINED" ─────────────────────────────────────────────────────
        // Harder, more laboured whine: louder overall and, instead of the smooth swell, it surges
        // irregularly (eeeeEEEEHHHHEHEEEE). Three slow, unrelated LFOs plus a faster rough one give an
        // uneven surge that never repeats audibly; the surge depth grows with load so a bus working
        // hard sounds like it is straining and a coasting one settles. Volume only -- pitch tracking
        // is untouched, so it still follows the gear/rpm the same way as every other d8646.
        if (diwaOpt1_8 && whTarget > 0f)
        {
            const float TWO_PI = 6.2831853f;
            float dt = (float)invSR * TWO_PI;
            v6_strainPhA += dt * 1.7f;  if (v6_strainPhA > TWO_PI) v6_strainPhA -= TWO_PI;
            v6_strainPhB += dt * 4.3f;  if (v6_strainPhB > TWO_PI) v6_strainPhB -= TWO_PI;
            v6_strainPhC += dt * 11.0f; if (v6_strainPhC > TWO_PI) v6_strainPhC -= TWO_PI;
            float surge = 0.50f * Mathf.Sin(v6_strainPhA) + 0.30f * Mathf.Sin(v6_strainPhB) + 0.20f * Mathf.Sin(v6_strainPhC);
            float loadK = 0.55f + 0.45f * Mathf.Clamp01(ld);
            whTarget *= 1.30f + 0.35f * surge * loadK;
        }

        v6_whVolSmooth += (whTarget - v6_whVolSmooth) * 0.006f;



        float screechThresh = GOV * 0.86f;
        float screechRaw    = Mathf.Clamp01((Mathf.Max(rpm, IDLE) - screechThresh) / Mathf.Max(1f, GOV - screechThresh));
        v6_screechSmooth += (screechRaw - v6_screechSmooth) * (screechRaw > v6_screechSmooth ? 0.0035f : 0.008f);

        if (v6_whVolSmooth > 0.0008f && v6_whHzSmooth > 40f)
        {
            float rnPitch = Mathf.Clamp01((rpm - IDLE) / (GOV - IDLE));

            // [REVERTED] The widened-spacing "fix" here was a mistake --
            // isolated on its own the tight original spacing looked like a
            // beating bug, but heard the way it's actually meant to be
            // heard (the full combined engine+transmission mix, not the
            // raw chord alone) that tightness was part of what made gear 3
            // sound right. Back to the original values.
            double p1 = Mathf.Lerp(1.75f, 2.45f, rnPitch);
            double p2 = Mathf.Lerp(1.90f, 2.75f, rnPitch);
            double p3 = Mathf.Lerp(2.10f, 3.10f, rnPitch);

            double w1 = Math.Sin(p1 * Math.PI * ph_v6_wh1);
            double w2 = Math.Sin(p2 * Math.PI * ph_v6_wh2);
            double w3 = Math.Sin(p3 * Math.PI * ph_v6_wh3);

            double chord = w1 * 1.00 + w2 * 0.48 + w3 * 0.24;

            if (v6_screechSmooth > 0.001f)
            {
                double w4 = Math.Sin(2.0 * Math.PI * ph_v6_wh4); 
                chord += w4 * 0.34 * v6_screechSmooth;
                chord += noise_hi * 0.22 * v6_screechSmooth;
            }

            // [REPLACED] The generic tremolo chop is gone -- superseded by
            // v6_firePulseMod (set in the gear-1 envelope section above),
            // which is real individual-firing-pulse modulation tied to the
            // engine's own hz, not a made-up LFO rate. gear>1/other tx
            // states leave v6_firePulseMod at 1.0 (no-op).
            // [NEW] "brighter at higher rpms" -- noise_hi content scales up
            // with rn on top of its flat 0.06 base, so the whine gains real
            // high-frequency edge as revs climb instead of staying a fixed
            // brightness throughout.
            // [MORE "INVERTER"] Real inverter/PWM tones are clean and
            // electronic, not breathy -- dialed the noise component back
            // down (was leaning noisier from the last brightness pass) and
            // let the pure sine chord carry the brightness instead, via a
            // sharper secondary partial that gets more prominent with rn.
            float brightBoost = 0.025f + rn * 0.03f;
            double cleanEdge = w2 * (0.10f + rn * 0.22f); // extra clean tonal bite at higher revs, not noise
            txSample += (chord + cleanEdge + noise_hi * brightBoost) * v6_whVolSmooth * engMul * v6_firePulseMod;

            // [KICKDOWN DEEPEN] This is what "deepen" should have meant the
            // first time: genuine added low-frequency growl content layered
            // UNDER the naturally-rising whine during the kickdown hold, not
            // an artificial downward pitch-shift of the whine's own
            // fundamental (reverted above). Low sub-harmonic order (well
            // under the whine's own teeth-order pitch) driven through a
            // hard tanh so it reads as a strained, gravelly undertone rather
            // than just another clean tone stacked on top.
            if (voith_kdRevActive)
            {
                double growlHz  = Mathf.Max(rpm, IDLE) / 60.0 * 5.5;
                double growlRaw = Math.Sin(2.0 * Math.PI * ph_v6_kdGrowl);
                double growl    = Math.Tanh(growlRaw * 1.8) * 0.30 * v6_whVolSmooth * engMul;
                txSample += growl;
                ph_v6_kdGrowl = (ph_v6_kdGrowl + growlHz * invSR) % 1.0;
            }

            // ═════════════════════════════════════════════════════════════
            //  [ADD] H50EP WHINE TRANSPLANT — gear 1 + opt1_4 only.
            //  Ported concept from DoH4xDSP's MG2 "cruise whine" (a
            //  continuously-tracked, Hz-smoothed additive sine layer) --
            //  NOT a literal copy, since that layer's own math depends on
            //  Allison hybrid-motor state (engHz/wheelRev/isGen5/isH50)
            //  that has no equivalent on a Voith torque-converter box. This
            //  reuses the same TECHNIQUE (smoothed-Hz-tracked 2-partial
            //  whine, volume built from load+revs) with D8646's own
            //  available inputs (rpm, rn, ld) instead.
            //
            //  Delayed ~0.35s after G1 entry (v6_h50tDelayTimer) rather
            //  than starting instantly alongside the native drive whine --
            //  the two are meant to read as separate motors arriving at
            //  slightly different moments, not one sound doubled in volume
            //  from the first instant of the gear. The G1 extension above
            //  (V6_OPT4_G1_EXTEND) is what gives this layer enough room to
            //  actually build and be heard before the upshift cuts it.
            // ═════════════════════════════════════════════════════════════
            if (gear == 1)
            {
                if (v6_h50tDelayTimer < 0.35f) v6_h50tDelayTimer += (float)invSR;

                if (v6_h50tDelayTimer >= 0.35f)
                {
                    // Own tooth-order, deliberately different from the
                    // native whine's 23.0 so the two carriers don't beat
                    // against each other or read as one fattened tone.
                    float h50tTeeth = 17.5f;
                    float h50tTgt   = (Mathf.Max(rpm, IDLE) / 60f) * h50tTeeth;
                    v6_h50tHzSmooth += (h50tTgt - v6_h50tHzSmooth) * (h50tTgt > v6_h50tHzSmooth ? 0.012f : 0.006f);

                    // Fades in over its own short window after the delay
                    // (same "build" shape idea as g1EnvVol above, own
                    // timer so it doesn't fight the native whine's own
                    // envelope), then tracks load/revs like a real second
                    // motor under torque, not a canned swell.
                    float h50tFadeIn = Mathf.Clamp01((v6_h50tDelayTimer - 0.35f) / 0.45f);
                    float h50tTgtVol = (0.014f + rn * 0.020f + ld * 0.012f) * h50tFadeIn * h50tFadeIn;
                    v6_h50tVolSmooth += (h50tTgtVol - v6_h50tVolSmooth) * 0.008f;

                    if (v6_h50tVolSmooth > 0.0006f && v6_h50tHzSmooth > 30f)
                    {
                        double t1 = Math.Sin(2.0 * Math.PI * ph_v6_h50t1);
                        double t2 = Math.Sin(2.0 * Math.PI * ph_v6_h50t2) * 0.42;
                        double t3 = Math.Sin(2.0 * Math.PI * ph_v6_h50t3) * 0.18;
                        txSample += (t1 + t2 + t3) * v6_h50tVolSmooth * engMul;
                    }
                }

                ph_v6_h50t1 = (ph_v6_h50t1 + (double)v6_h50tHzSmooth         * invSR) % 1.0;
                ph_v6_h50t2 = (ph_v6_h50t2 + (double)v6_h50tHzSmooth * 1.503 * invSR) % 1.0;
                ph_v6_h50t3 = (ph_v6_h50t3 + (double)v6_h50tHzSmooth * 2.51  * invSR) % 1.0;
            }
            else
            {
                v6_h50tDelayTimer = 0f;   // resets so the next G1 entry gets the same fresh delay
                v6_h50tVolSmooth  = 0f;
            }

            // ═════════════════════════════════════════════════════════════
            //  [ADD] RETARDER GROAN — gear 1, RPM-tracked (per instruction --
            //  the design-doc comment above the [W2] field block describes
            //  real DIWA retarder pitch tracking ROAD SPEED under braking;
            //  this specific G1 groan is deliberately tracking ENGINE RPM
            //  instead, a different, lower-register texture under the
            //  whine layers rather than the braking-retarder effect).
            //  Wires up v6_rwhHzSmooth/v6_rwhVolSmooth/ph_v6_rwh1-3 --
            //  declared since the v1→v2 rebuild but never actually
            //  assigned or mixed into txSample anywhere until now.
            //  Distorted (tanh) rather than clean, same technique as the
            //  kickdown growl just above, so it reads as a groan/strain
            //  texture rather than another clean tone stacked in.
            // ═════════════════════════════════════════════════════════════
            if (gear == 1)
            {
                float rwhTgt = Mathf.Max(rpm, IDLE) / 60f * 4.2f;
                v6_rwhHzSmooth += (rwhTgt - v6_rwhHzSmooth) * 0.01f;

                float rwhTgtVol = 0.010f + rn * 0.014f + ld * 0.010f;
                v6_rwhVolSmooth += (rwhTgtVol - v6_rwhVolSmooth) * 0.006f;

                if (v6_rwhVolSmooth > 0.0005f && v6_rwhHzSmooth > 15f)
                {
                    double r1 = Math.Sin(2.0 * Math.PI * ph_v6_rwh1);
                    double r2 = Math.Sin(2.0 * Math.PI * ph_v6_rwh2);
                    double groan = Math.Tanh((r1 + r2 * 0.5) * 1.6) * v6_rwhVolSmooth * engMul;
                    txSample += groan;
                }

                ph_v6_rwh1 = (ph_v6_rwh1 + (double)v6_rwhHzSmooth        * invSR) % 1.0;
                ph_v6_rwh2 = (ph_v6_rwh2 + (double)v6_rwhHzSmooth * 1.48 * invSR) % 1.0;
                ph_v6_rwh3 = (ph_v6_rwh3 + (double)v6_rwhHzSmooth * 2.02 * invSR) % 1.0;
            }
            else
            {
                v6_rwhVolSmooth = 0f;
            }
        }
    }

    // [NEW — ANS re-engagement clunk] When ANS drops out (driver touches the
    // accelerator), the input clutch EK re-engages and the turbine/reverse
    // gear brakes release -- a real, audible mechanical event, distinct from
    // a normal gear shift thud. Fires once per ANS exit via the pulse set in
    // CalcVoithRPM. Deliberately a soft, low clunk rather than a sharp bang:
    // ANS release is a clutch taking up load, not a hard shift.
    // [NEW — 1→2 pump impeller spin-down] The manual describes a specific,
    // distinct event at this one shift: when the pump brake PB is applied,
    // "for improvement of the shifting quality, the pump impeller is
    // ADDITIONALLY DECELERATED BY THE CONVERTER PRESSURE which is still
    // present." So the impeller doesn't just stop -- it gets actively
    // dragged down by residual fluid pressure, a real audible spin-down
    // unique to the 1→2 shift (no other shift in this gearbox stops a
    // spinning impeller). Triggered on the actual 1→2 transition only.
    if (v6_lastGearForPumpDown == 1 && gear == 2) voith_pumpSpinDown = 1f;
    v6_lastGearForPumpDown = gear;

    if (voith_pumpSpinDown > 0.001f)
    {
        // Falling pitch as the impeller is dragged to a stop -- starts near
        // the converter's own working range and decays away.
        double spinHz = 40.0 + voith_pumpSpinDown * 210.0;
        double spin = Math.Sin(2.0 * Math.PI * ph_v6_pumpSpin) * voith_pumpSpinDown * 0.13 * engMul
                    + noise_lp * voith_pumpSpinDown * 0.09 * engMul;
        txSample += spin;
        ph_v6_pumpSpin = (ph_v6_pumpSpin + spinHz * invSR) % 1.0;
        voith_pumpSpinDown -= (float)(invSR / 0.55); // ~0.55s drag-down
        if (voith_pumpSpinDown < 0f) voith_pumpSpinDown = 0f;
    }

    if (voith_ansEngagePulse > 0.001f)
    {
        double clunk = Math.Sin(2.0 * Math.PI * ph_v6_ansClunk) * voith_ansEngagePulse * 0.16 * engMul
                     + noise_lp * voith_ansEngagePulse * 0.10 * engMul;
        txSample += clunk;
        ph_v6_ansClunk = (ph_v6_ansClunk + 52.0 * invSR) % 1.0;
        voith_ansEngagePulse -= (float)(invSR / 0.28); // ~0.28s soft clunk
        if (voith_ansEngagePulse < 0f) voith_ansEngagePulse = 0f;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  3. DIWA "BRRRRRRINGA" LAUNCH OVERTONE
    // ═════════════════════════════════════════════════════════════════════════
    if (gear == 1 && g1Progress > 0.88f && v6_ringaTimer <= 0f)
    {
        v6_ringaTimer = 3.0f;
        v6_ringaVol = 1.0f;
    }

    if (v6_ringaTimer > 0f)
    {
        v6_ringaTimer -= (float)invSR;
        float t = 1f - Mathf.Clamp01(v6_ringaTimer / 3f);
        float growlHz = Mathf.Lerp(58f, 215f, t);

        ph_v6_ringa1 = (ph_v6_ringa1 + growlHz * invSR) % 1.0;
        ph_v6_ringa2 = (ph_v6_ringa2 + growlHz * 1.45 * invSR) % 1.0;
        ph_v6_ringa3 = (ph_v6_ringa3 + growlHz * 2.90 * invSR) % 1.0;

        double low   = Math.Sin(2.0 * Math.PI * ph_v6_ringa1);
        double mid   = Math.Sin(2.0 * Math.PI * ph_v6_ringa2);
        double high  = Math.Sin(2.0 * Math.PI * ph_v6_ringa3);
        float rasp   = Mathf.Lerp(0.08f, 0.42f, t);

        double sound = low * 1.00 + mid * 0.38 + high * 0.12 + noise_lp * rasp + noise_hi * rasp * 0.10;
        sound = Math.Tanh(sound * (1.4 + t));
        float env = Mathf.Sin(t * Mathf.PI);

        txSample += sound * env * 0.085 * engMul;

        if (v6_ringaTimer <= 0f) v6_ringaVol = 0f;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  4. CONVERTER CHURN (FLUID HYDRAULIC BED)
    // ═════════════════════════════════════════════════════════════════════════
    {
        float churnTarget = 0f;
        if (gear == 1)
        {
            churnTarget = hydShare * (0.25f + ld * 0.75f);
            if (spd < 2f) churnTarget = Mathf.Max(churnTarget, 0.22f + ld * 0.30f);
        }
        v6_churnEnv += (churnTarget - v6_churnEnv) * (churnTarget > v6_churnEnv ? 0.010f : 0.004f);

        if (v6_churnEnv > 0.004f)
        {
            v6_churnLP += (noise_lp - v6_churnLP) * 0.22;
            double swirl = 0.80 + 0.20 * Math.Sin(2.0 * Math.PI * ph_v6_churnMod);
            txSample += v6_churnLP * swirl * v6_churnEnv * 0.11 * engMul;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  5. STOPPED-IDLE BODY SHAKE
    // ═════════════════════════════════════════════════════════════════════════
    //  [GATED per instruction] "Comment out the shaking" for base 8646 --
    //  can't literally comment this out since opt1_4/opt1_5 also run this
    //  same shared function and must keep it exactly as before. Gated on
    //  !diwaOpt1_4 && !diwaOpt1_5 instead: since only d8646/opt-aliases
    //  ever reach DoVoithDSP at all, this condition is true ONLY for the
    //  opt-less base case, leaving opt1_4/opt1_5 fully untouched either way.
    if (!diwaOpt1_4 && !diwaOpt1_5)
    {
        bool trueStop = gear <= 1 && spd < 2.2f && accel < 0.08f;
        float stopAmount = Mathf.Clamp01(1f - spd / 2.2f);
        float shakeTarget = trueStop ? stopAmount : 0f;

        v6_idleShakeEnv += (shakeTarget - v6_idleShakeEnv) * (shakeTarget > v6_idleShakeEnv ? 0.006f : 0.015f);

        if (v6_idleShakeEnv > 0.01f)
        {
            double sway = Math.Sin(2.0 * Math.PI * ph_v6_am1);
            double vibration = Math.Sin(2.0 * Math.PI * ph_v6_idleVib);
            double fire3 = Math.Sin(2.0 * Math.PI * ph_v6_fireHalf);

            double shakeSound = sway * 0.8 + vibration * 0.65 + fire3 * 0.35 + noise_lp * 0.35;
            shakeSound = Math.Tanh(shakeSound * 2.8);

            txSample += shakeSound * v6_idleShakeEnv * 0.13 * engMul;
        }

        v6_idleVibHz = Mathf.Lerp(18f, 55f, Mathf.Clamp01(1f - spd / 2f));
        ph_v6_idleVib = (ph_v6_idleVib + v6_idleVibHz * invSR) % 1.0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  6. RPM LOAD ENGINE VIBRATION
    // ═════════════════════════════════════════════════════════════════════════
    {
        float rpmProgress = Mathf.Clamp01((rpm - IDLE) / (GOV - IDLE));
        float rpmVibTargetHz = Mathf.Lerp(4f, 75f, Mathf.Pow(rpmProgress, 3f));

        v6_rpmVibHz += (rpmVibTargetHz - v6_rpmVibHz) * 0.02f;

        float targetVol = (gear == 1) ? Mathf.Pow(rpmProgress, 4f) * (0.18f + ld * 0.25f) : 0f;
        v6_rpmVibVol += (targetVol - v6_rpmVibVol) * 0.02f;

        if (v6_rpmVibVol > 0.001f)
        {
            double vib = Math.Sin(2.0 * Math.PI * ph_v6_rpmVib);
            double grit = noise_lp * rpmProgress * 0.7;
            double brrr = vib * 0.8 + grit;

            brrr = Math.Tanh(brrr * (1.0 + rpmProgress * 4));
            txSample += brrr * v6_rpmVibVol * engMul;
        }

        ph_v6_rpmVib = (ph_v6_rpmVib + v6_rpmVibHz * invSR) % 1.0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  7. MECHANICAL TRANSIT / PNEUMATIC SHIFT THUDS & SIGHS
    //  [REPLACED per instruction] Was 3 separate elements (sigh/seat/thud).
    //  Now D8645's single heavier, slower-decay two-tone thud -- cruder
    //  valve body, bigger fill lag, matching what 8645 always had. The
    //  upshift-from-1 reset side-effects (churn/G1-buzz clamp) are KEPT
    //  since other unmodified sections below still depend on them firing
    //  at the exact moment of a real gear change.
    // ═════════════════════════════════════════════════════════════════════════
    if (gear != voith_lastGearAudio)
    {
        bool upFrom1 = (voith_lastGearAudio == 1 && gear == 2);
        voith_lastGearAudio = gear;
        if (upFrom1)
        {
            v6_churnEnv  = Mathf.Min(v6_churnEnv, 0.05f);
            v6_g1BuzzVol = 0f;
        }
        d6_shiftThud = 1.0f; ph_d6_thud1 = 0.0; ph_d6_thud2 = 0.0; // [FIX -- click bug] zero the thud's own phase on every envelope-open, kills the shift "tick"
    }

    if (d6_shiftThud > 0f)
    {
        txSample += Math.Sin(2.0 * Math.PI * ph_d6_thud1) * d6_shiftThud * 0.28 * engMul;
        txSample += Math.Sin(2.0 * Math.PI * ph_d6_thud2) * d6_shiftThud * 0.16 * engMul;
        d6_shiftThud *= 0.9965f;
        if (d6_shiftThud < 0.004f) d6_shiftThud = 0f;
    }
    ph_d6_thud1 = (ph_d6_thud1 + 44.0  * invSR) % 1.0;
    ph_d6_thud2 = (ph_d6_thud2 + 100.0 * invSR) % 1.0;

    // ═════════════════════════════════════════════════════════════════════════
    //  8. MOVE-OFF TRANSITION PULL GRAB
    //  [REPLACED per instruction] D8645's version as the base (longer
    //  0.80s timer, lower/heavier 38-54Hz grab range vs the old 46-64Hz),
    //  then made STRONGER again on top of that for 8646 specifically --
    //  amplitude pushed noticeably past even D8645's own already-heavier
    //  values, per explicit instruction.
    // ═════════════════════════════════════════════════════════════════════════
    {
        bool stoppedNow = spd < 0.4f;
        if (v6_wasStopped && !stoppedNow && accel > 0.05f) v6_moveOffTimer = 0.80f;
        v6_wasStopped = stoppedNow;

        if (v6_moveOffTimer > 0f)
        {
            v6_moveOffTimer -= (float)invSR;
            float t = Mathf.Clamp01(v6_moveOffTimer / 0.80f);
            float grabHz = 38f + (1f - t) * 16f;
            float judder = 0.5f + 0.5f * Mathf.Sin((float)audioClock * Mathf.PI * 2f * 8f);
            // [STRONGER] 0.22 base + 0.15 judder swing, vs D8645's own
            // 0.14 + 0.09 -- roughly 55-65% more amplitude on top of an
            // already-heavier unit.
            double grab  = (Math.Sin(2.0 * Math.PI * ph_v6_grab) * 0.85 + noise_lp * 0.5)
                           * t * (0.22f + judder * 0.15f) * engMul;
            txSample += grab;
            ph_v6_grab = (ph_v6_grab + grabHz * invSR) % 1.0;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  8b. D8645 ADDITIONS — pump floor, ANS clunk, TC slip, tail whine,
    //  G1 buzz. Per instruction: everything from D8645 not already present
    //  in 8646's own function above (drive whine, fixed tone, retarder all
    //  stay 8646's own -- these five are genuinely new, layered alongside).
    // ═════════════════════════════════════════════════════════════════════════

    // ── ANS engage clunk — same transient D8645 has on input-clutch
    // re-engagement, not something 8646's own ANS handling (the whine's
    // "*= 0.05f" quieting, section 2) produces on its own.
    if (d5_ansEngagePulse > 0.001f)
    {
        double clunk = Math.Sin(2.0 * Math.PI * ph_d5_grab) * d5_ansEngagePulse * 0.14 * engMul
                     + noise_lp * d5_ansEngagePulse * 0.09 * engMul;
        txSample += clunk;
        d5_ansEngagePulse -= (float)(invSR / 0.30);
        if (d5_ansEngagePulse < 0f) d5_ansEngagePulse = 0f;
    }

    // ── [DIFFERENTIATOR] Load-adaptive converter/pump noise floor. D8645's
    // own version is a flat always-on floor whenever in gear (no smart
    // pressure circuit) -- 8646's tapers down at light throttle/idle and
    // builds under real load instead, the "smart circuit" 864.6 is
    // supposed to have per D8645's own header comment.
    {
        float ansMulLocal = voith_ansActive ? 0.05f : 1.0f;
        float floorTarget = (gear > 0 ? Mathf.Lerp(0.18f, 0.65f, Mathf.Clamp01(ld * 1.4f)) : 0f) * ansMulLocal;
        d6_pumpFloorVol += (floorTarget - d6_pumpFloorVol) * (floorTarget > d6_pumpFloorVol ? 0.012f : 0.008f);
        if (d6_pumpFloorVol > 0.001f)
        {
            double pf1 = Math.Sin(2.0 * Math.PI * ph_d6_pumpFloor1);
            double pf2 = Math.Sin(2.0 * Math.PI * ph_d6_pumpFloor2);
            double floorTone = (pf1 * 0.35 + pf2 * 0.20) + noise_lp * 0.55;
            txSample += floorTone * d6_pumpFloorVol * 0.052 * engMul;
            ph_d6_pumpFloor1 = (ph_d6_pumpFloor1 + 46.0       * invSR) % 1.0;
            ph_d6_pumpFloor2 = (ph_d6_pumpFloor2 + 46.0 * 1.5 * invSR) % 1.0;
        }
    }

    // ── TC slip noise — D8645's, unchanged. More prominent than a smart-
    // circuit unit would have, but genuinely new content 8646 didn't have.
    {
        bool sustainedSlip = gear > 0;
        float slipTarget = (sustainedSlip ? (gear <= 1 ? ld * 0.85f + 0.10f : ld * 0.30f + 0.04f) : 0f)
                          * (voith_ansActive ? 0.05f : 1.0f);
        d6_tcSlipSmooth += (slipTarget - d6_tcSlipSmooth) * 0.004f;
        if (d6_tcSlipSmooth > 0.005f)
        {
            float tcVol = (0.034f + d6_tcSlipSmooth * 0.060f) * engMul;
            txSample += noise_lp * tcVol;
        }
    }

    // ── Tail whine at speed — D8645's, unchanged. A separate accessory-
    // style whine tracking output speed, distinct from the main gear-mesh
    // drive whine above.
    {
        float d6MwV = (spd > 1.5f)
            ? (0.010f + (spd / MAX_SPD) * 0.056f + ld * 0.010f) * engMul * (voith_ansActive ? 0.05f : 1.0f) : 0f;
        double fD6Mw = outRPM * 0.58, fD6Mw2 = fD6Mw * 1.97;
        if (d6MwV > 0f)
        {
            txSample += Math.Sin(2.0 * Math.PI * ph_d6_Mw)  * d6MwV * 0.75
                      + Math.Sin(2.0 * Math.PI * ph_d6_Mw2) * d6MwV * 0.25
                      + noise_lp * d6MwV * 0.30;
        }
        ph_d6_Mw  = (ph_d6_Mw  + fD6Mw  * invSR) % 1.0;
        ph_d6_Mw2 = (ph_d6_Mw2 + fD6Mw2 * invSR) % 1.0;
    }

    // ── End-of-gear-1 vibration/buzz — D8645's, unchanged. Distinct from
    // section 6's RPM-driven vibration above: this one triggers by
    // proximity to the gear-1 upshift point regardless of RPM, not by
    // engine revs.
    {
        float g1VibTarget = 0f;
        if (gear == 1)
        {
            float frac = Mathf.Clamp01((spd - (VOITH_GEAR2_UPSHIFT_SPD - 10f)) / 10f);
            g1VibTarget = frac * (0.5f + accel * 0.7f);
        }
        d6_g1VibVol += (g1VibTarget - d6_g1VibVol) * (g1VibTarget > d6_g1VibVol ? 0.018f : 0.009f);
        if (d6_g1VibVol > 0.002f)
        {
            double buzz = Math.Sin(2.0 * Math.PI * ph_d6_buzz) * d6_g1VibVol * 0.065 * engMul;
            buzz *= 0.55 + 0.45 * Math.Sin(2.0 * Math.PI * ph_d6_buzz * 6.6);
            txSample += buzz;
        }
        ph_d6_buzz = (ph_d6_buzz + 29.0 * invSR) % 1.0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  9. SHAKER CHASSIS MECHANICS (CREAKS & RATTLING MODULATION)
    // ═════════════════════════════════════════════════════════════════════════
    // [GATED per instruction] Combined with the same opt1_4/opt1_5
    // exclusion as section 5 above -- see that section's comment.
    if (v6_isShaker && !diwaOpt1_4 && !diwaOpt1_5)
    {
        float shakeLoad = Mathf.Clamp01(accel * 1.2f) * Mathf.Clamp01(1f - spd / 45f) + (spd < 1f ? 0.22f : 0f);
        if (shakeLoad > 0.02f)
        {
            double bodyv   = Math.Sin(2.0 * Math.PI * (ph_v6_shakeBody * 0.78));
            double tremorv = 0.55 + 0.45 * Math.Sin(2.0 * Math.PI * (ph_v6_shakeBody * 4.9));
            txSample += bodyv * tremorv * shakeLoad * 0.040 * engMul;
        }
        if (v6_creakVol < 0.01f && shakeLoad > 0.15f && (NextNoiseSample() * 0.5 + 0.5) > 0.99955)
        {
            v6_creakVol = 0.6f + (float)(NextNoiseSample() * 0.5 + 0.5) * 0.4f;
        }
        if (v6_creakVol > 0.003f)
        {
            double c1 = Math.Sin(2.0 * Math.PI * ph_v6_creak1);
            double c2 = Math.Sin(2.0 * Math.PI * ph_v6_creak2);
            txSample += Math.Tanh((c1 + c2 * 0.5) * 1.6) * v6_creakVol * 0.045 * engMul;
            double crHz = 320.0 + (1.0 - v6_creakVol) * 260.0;
            ph_v6_creak1 = (ph_v6_creak1 + crHz        * invSR) % 1.0;
            ph_v6_creak2 = (ph_v6_creak2 + crHz * 1.48 * invSR) % 1.0;
            v6_creakVol *= 0.9975f;
            if (v6_creakVol < 0.004f) v6_creakVol = 0f;
        }
    }

    // Faint engine oil gear pump tick
    if (spd < 3f)
    {
        float pumpVol = 0.004f * Mathf.Clamp01(1f - spd / 3f) * engMul;
        txSample += Math.Sin(2.0 * Math.PI * ph_v6_pump) * pumpVol;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  10. ELECTRIC BACKGROUND MOTOR DRONE (PITCH SCALED RESIDUAL BACKGROUND)
    // ═════════════════════════════════════════════════════════════════════════
    float xe_motorHzSmooth = (spd / MAX_SPD) * 720f; 
    if (xe_motorHzSmooth < 0.3f) xe_motorHzSmooth = 0f;

    float droneGearTarget = (gear == 1) ? 1.2f : 0.3f;
    v5_xeDroneSmooth += (droneGearTarget - v5_xeDroneSmooth) * (droneGearTarget > v5_xeDroneSmooth ? 0.15f : 0.05f);

    float motSpdFrac = Mathf.Clamp01(xe_motorHzSmooth / 720f);
    float continuousMotorVol = motSpdFrac > 0.001f
        ? (0.04f + motSpdFrac * 0.08f + ld * 0.04f) * engMul * (1f + ld * 0.35f) * v5_xeDroneSmooth
        : 0f;

    if (continuousMotorVol > 0.0005f)
    {
        txSample += Math.Sin(2.0 * Math.PI * ph_xe_mot1 * 0.85) * continuousMotorVol
                  + Math.Sin(2.0 * Math.PI * ph_xe_mot2 * 0.85 * 1.998 / 2.0) * continuousMotorVol * 0.4;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  11. RETARDER CORE ACOUSTICS & ROTATIONAL SHAFT WHINE
    // ═════════════════════════════════════════════════════════════════════════
    {
        float hissTarget = gear >= 1 ? Mathf.Clamp01(ld * 1.2f) : 0f;
        float hissLin = hissTarget * Mathf.Pow(10f, -85f / 20f) * engMul * 12f;
        if (hissLin > 0.00005f)
        {
            double hissTone = Math.Sin(2.0 * Math.PI * ph_vwhine_hiss);
            txSample += (hissTone * 0.3 + noise_hi * 0.7) * hissLin;
            ph_vwhine_hiss = (ph_vwhine_hiss + 6800.0 * invSR) % 1.0;
        }
    }

    // [MOVED to section 1c, ahead of the drive whine] — retarder groan

    if (retAct)
    {
        if (!voith_deepRetWasActive)
        {
            voith_deepRetTimer = 0f;
            voith_deepRetVol = 0f;
        }
        voith_deepRetTimer += (float)invSR;
        voith_deepRetVol += (1f - voith_deepRetVol) * 0.0025f;
    }
    else
    {
        voith_deepRetVol *= 0.996f;
        if (voith_deepRetVol < 0.002f)
        {
            voith_deepRetVol = 0f;
            voith_deepRetTimer = 0f;
        }
    }
    voith_deepRetWasActive = retAct;

    float age = Mathf.Clamp01(voith_deepRetTimer / 3.5f);
    
    // [MODIFIED] Drop fundamental frequency targets down for a deeper pitch sweep
    float baseHz = Mathf.Lerp(110f, 55f, age);
    float targetHz = baseHz + spd * 0.82f; 
    voithRetHz += (targetHz - voithRetHz) * ((retAct ? 0.05f : 0.12f) * 60f * (float)invSR);

    // [FIX -- per spec] "No sharp retarder... make [the groan] universal for
    // everything that has an opt, only the original doesn't have it." This
    // "roar" (tanh-3.8x hoarse/ragged texture -- the actual "sharp"
    // retarder) used to play unconditionally on every Voith regardless of
    // opts, fighting/duplicating the smoother opt retarder groan (section
    // 1c above) on any opt-equipped bus. Now it's exclusive to the TRUE
    // original (no diwaOpt1_4/5/6/7 at all) -- an opt-equipped bus gets
    // only the smooth groan, never this.
    bool isOriginalNoOpt = !diwaOpt1_4 && !diwaOpt1_5 && !diwaOpt1_6 && !diwaOpt1_7;

    // [MODIFIED] Global attenuation scaling factor (* 0.58f) to keep it less loud
    float retVol = (0.02f + age * 0.22f) * (0.12f + bkPd * 0.26f) * engMul * 0.58f;
    double pulse = 0.70 + 0.30 * Math.Sin(2.0 * Math.PI * ph_retPulse);

    if (isOriginalNoOpt && retVol > 0.0001f)
    {
        // [MODIFIED] Attenuated sine tones, augmented low noise bed, and pushed Tanh saturation to 3.8x for a hoarse, ragged fluid texture
        double low = Math.Sin(2.0 * Math.PI * ph_vRet);
        double second = Math.Sin(2.0 * Math.PI * ph_vRet2);
        double roar = low * 0.60 + second * 0.08 + noise_lp * (0.75 + age * 0.45) + noise_hi * (0.08 + age * 0.12);

        roar = Math.Tanh(roar * 3.8);
        txSample += roar * pulse * retVol;
    }

    // [TUNED -- "tuned squeal like IRL"] Exclusive to the original now too
    // (same reasoning as the roar above), and given a bit more presence
    // since it's carrying the original's whole retarder character on its
    // own rather than layering under the roar.
    float squeakEnv = isOriginalNoOpt ? Mathf.Clamp01((voith_deepRetTimer - 1.0f) / 2.0f) : 0f;
    if (squeakEnv > 0.001f)
    {
        float squeakHz = 1350f + spd * 5f + bkPd * 120f;
        ph_vRetSqueak = (ph_vRetSqueak + squeakHz * invSR) % 1.0;
        double squeak = Math.Sin(2.0 * Math.PI * ph_vRetSqueak) + noise_hi * 0.06;
        txSample += squeak * (float)Math.Pow(squeakEnv, 2.5) * 0.022 * engMul;
    }

    float shake = Mathf.Clamp01(ld * 0.9f) * Mathf.Clamp01(rpm / 1200f);
    // [ADD] Legacy 864.5 character -- per spec: same DSP as 864.6, but
    // "older and sharper... more rev and stuff" on the RPM/vibration side
    // specifically. Real basis: 864.5 is the earlier, non-electronically-
    // refined valve body -- a harder mechanical shift/hold feel than 864.6's
    // smoothed-over character, so its shake reads a bit more present and a
    // bit higher-pitched rather than a genuinely different sound layer.
    if (legacy854) shake *= 1.28f;
    float bodyHz = Mathf.Lerp(8f, 16f, rn)  * (legacy854 ? 1.12f : 1f);
    float vibHz  = Mathf.Lerp(28f, 65f, rn) * (legacy854 ? 1.15f : 1f);

    ph_body1 = (ph_body1 + bodyHz * invSR) % 1.0;
    ph_body2 = (ph_body2 + vibHz * invSR) % 1.0;
    ph_body3 = (ph_body3 + FHz(rpm) * 0.5 * invSR) % 1.0;

    float launchShake = gear == 1 ? Mathf.Pow(1f - g1Progress, 0.6f) : 0f;
    shake *= 1f + launchShake * 1.8f;

    if (shake > 0.001f)
    {
        double body = Math.Sin(2.0 * Math.PI * ph_body1);
        double vib  = Math.Sin(2.0 * Math.PI * ph_body2);
        double fire = Math.Sin(2.0 * Math.PI * ph_body3);
        double rumble = body * 1.00 + vib * 0.45 + fire * 0.22 + noise_lp * 0.30;
        txSample += Math.Tanh(rumble * (legacy854 ? 2.3 : 2.0)) * shake * 0.09 * engMul;
    }

    // [MODIFIED] Muted the secondary tail whine in higher gear intervals
    float voiMwV = (spd > 1.5f) ? (0.012f + (spd / MAX_SPD) * 0.070f + ld * 0.012f) * engMul : 0f;
    if (gear == 2 || gear == 3) voiMwV *= 0.30f;
    else if (gear >= 4)         voiMwV *= 0.15f;

    double fVMw = outRPM * 0.58;
    double fVMw2 = fVMw * 2.0;
    if (voiMwV > 0f)
    {
        txSample += Math.Sin(2.0 * Math.PI * ph_vMw)  * voiMwV;
        txSample += Math.Sin(2.0 * Math.PI * ph_vMw2) * voiMwV * 0.35;
    }

    float retTHz = 205f + spd * 1.8f;
    // [MODIFIED] Reduced the peak volume of the secondary brake component to line up with the primary volume reduction
    // [SOFTENED -- saw tone -> groan] This used to be a raw sawtooth
    // (2*(frac-0.5)) driving the secondary retarder voice, which reads as
    // a hard, buzzy edge rather than a hydrodynamic groan. Swapped the
    // sawtooth for a two-partial sine stack (fundamental + a sub-octave
    // partner) instead of generating the harsh harmonic content in the
    // first place. Also added slow-attack smoothing (voith_retSecSmooth)
    // so the layer swells in over roughly a third of a second instead of
    // snapping on with retAct, which was the other half of the "sharp"
    // character.
    float voiRetTarget = retAct ? (0.07f + bkPd * 0.06f) * engMul : 0f;
    voith_retSecSmooth += (voiRetTarget - voith_retSecSmooth) * (retAct ? 0.012f : 0.03f);
    float voiRetV = voith_retSecSmooth;
    if (voiRetV > 0.0005f)
    {
        double groanLow = Math.Sin(2.0 * Math.PI * ph_vRet);
        double groanSub = Math.Sin(2.0 * Math.PI * ph_vRet2 * 0.5); // sub-octave partner, gives the groan body without added brightness
        txSample += groanLow * voiRetV * 0.75;
        txSample += groanSub * voiRetV * 0.40;
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    //  11b. GEAR-3 CHASSIS BODY GROAN
    // ═══════════════════════════════════════════════════════════════════════════════
    // [NEW] Low, tone-and-noise chassis groan gated specifically on
    // dwelling in gear 3 (not general G1-4 shake) -- distinct from the
    // shift-thud/creak layers elsewhere in this file. Builds the longer
    // the bus stays in gear 3 under load and decays quickly on leaving it.
    if (gear == 3)
    {
        voith_g3DwellTimer += (float)invSR;
    }
    else
    {
        voith_g3DwellTimer = 0f;
    }
    float g3Target = (gear == 3) ? Mathf.Clamp01(voith_g3DwellTimer / 2.0f) * (0.05f + ld * 0.09f) * engMul : 0f;
    voith_g3GroanVol += (g3Target - voith_g3GroanVol) * (gear == 3 ? 0.006f : 0.02f);

    if (voith_g3GroanVol > 0.0006f)
    {
        float g3Hz = 42f + rn * 10f; // low, engine-load-tracking groan pitch
        ph_v_g3Groan1 = (ph_v_g3Groan1 + g3Hz * invSR) % 1.0;
        ph_v_g3Groan2 = (ph_v_g3Groan2 + g3Hz * 1.015 * invSR) % 1.0; // slight detune against groan1, beats slowly for a "creaking under load" character

        double g3a = Math.Sin(2.0 * Math.PI * ph_v_g3Groan1);
        double g3b = Math.Sin(2.0 * Math.PI * ph_v_g3Groan2);
        double g3mix = g3a * 0.6 + g3b * 0.6 + noise_lp * 0.25;
        txSample += Math.Tanh(g3mix * 1.4) * voith_g3GroanVol;
    }


    // ═════════════════════════════════════════════════════════════════════════
    //  12. TORQUE CONVERTER WAIL (STOCK VOITH SIGNATURE CRY)
    // ═════════════════════════════════════════════════════════════════════════
    // A single rising cry, distinct from the D864.6art's two-stage windup/
    // hold scream — no staging here, just a smooth swell that only shows up
    // deep in G1 under real load and peaks right as the box is about to
    // kick to 2nd. Detuned pair riding above the drive-whine fundamental.
    {
        float wailGate   = (gear == 1) ? Mathf.Clamp01(Mathf.InverseLerp(0.55f, 0.95f, g1Progress)) : 0f;
        float wailTarget = wailGate * wailGate * (0.10f + ld * 0.18f);
        v6_wailSmooth += (wailTarget - v6_wailSmooth) * (wailTarget > v6_wailSmooth ? 0.010f : 0.020f);

        if (v6_wailSmooth > 0.001f)
        {
            double w1  = Math.Sin(2.0 * Math.PI * ph_v6_wail1);
            double w2  = Math.Sin(2.0 * Math.PI * ph_v6_wail2);
            double cry = w1 * 0.85 + w2 * 0.40 + noise_hi * 0.20;
            txSample += Math.Tanh(cry * 1.6) * v6_wailSmooth * 0.16 * engMul;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  13. GLOBAL PHASE ACCUMULATION
    // ═════════════════════════════════════════════════════════════════════════
    float fireHz2 = FHz(Mathf.Max(rpm, IDLE));
    ph_v6_wh1       = (ph_v6_wh1       + (double)v6_whHzSmooth          * invSR) % 1.0;
    ph_v6_wh2       = (ph_v6_wh2       + (double)v6_whHzSmooth  * 2.003 * invSR) % 1.0;
    ph_v6_wh3       = (ph_v6_wh3       + (double)v6_whHzSmooth  * 3.01  * invSR) % 1.0;
    ph_v6_wh4       = (ph_v6_wh4       + (double)v6_whHzSmooth  * 4.98  * invSR) % 1.0;
    ph_v6_rwh1      = (ph_v6_rwh1      + (double)v6_rwhHzSmooth         * invSR) % 1.0;
    ph_v6_rwh2      = (ph_v6_rwh2      + (double)v6_rwhHzSmooth * 2.01  * invSR) % 1.0;
    ph_v6_rwh3      = (ph_v6_rwh3      + (double)v6_rwhHzSmooth * 3.4   * invSR) % 1.0;
    ph_v6_fire      = (ph_v6_fire      + (double)fireHz2                * invSR) % 1.0;
    ph_v6_fireHalf  = (ph_v6_fireHalf  + (double)fireHz2 * 0.5          * invSR) % 1.0;
    ph_v6_am1       = (ph_v6_am1       + 2.4                            * invSR) % 1.0;
    ph_v6_am2       = (ph_v6_am2       + 9.8                            * invSR) % 1.0;
    ph_v6_churnMod  = (ph_v6_churnMod  + 1.7                            * invSR) % 1.0;
    ph_v6_buzz      = (ph_v6_buzz      + 33.0                           * invSR) % 1.0;
    ph_v6_seat      = (ph_v6_seat      + 55.0                           * invSR) % 1.0;
    ph_v6_shakeBody = (ph_v6_shakeBody + 34.0                           * invSR) % 1.0;
    ph_v6_pump      = (ph_v6_pump      + (double)(rpm / 60f) * 9.0      * invSR) % 1.0;
        
    ph_xe_mot1      = (ph_xe_mot1      + (double)xe_motorHzSmooth       * invSR) % 1.0;
    ph_xe_mot2      = (ph_xe_mot2      + (double)xe_motorHzSmooth * 2.0 * invSR) % 1.0;
    ph_vRet         = (ph_vRet         + (double)voithRetHz             * invSR) % 1.0;
    ph_vRet2        = (ph_vRet2        + (double)voithRetHz * 1.92      * invSR) % 1.0;
    ph_retPulse     = (ph_retPulse     + 2.8                            * invSR) % 1.0;
    ph_vMw          = (ph_vMw          + fVMw                           * invSR) % 1.0;
    ph_vMw2         = (ph_vMw2         + fVMw2                          * invSR) % 1.0;
    ph_v6_wail1     = (ph_v6_wail1     + (double)v6_whHzSmooth * 1.35   * invSR) % 1.0;
    ph_v6_wail2     = (ph_v6_wail2     + (double)v6_whHzSmooth * 1.70   * invSR) % 1.0;

    // [MOVED to section 1c, ahead of the drive whine] — hiss window +
    // audible individual piston firing

    // ═════════════════════════════════════════════════════════════════════════
    //  15. OPT1_5 — "THE SHAKER"
    //  No whine at all (see the hard bypass on section 2 above). Instead:
    //  a deeper, louder core voice, and -- since we can't actually shake the
    //  floor -- a simulated vibration made of rapid-fire click impulses,
    //  dense enough to blur into a felt rattle/buzz rather than read as
    //  individual hits (unlike opt1_4's piston-fire layer, which is
    //  deliberately sparse enough to be heard as distinct events). Active
    //  specifically at/near idle, per spec ("you can feel through the floor
    //  when idle").
    // ═════════════════════════════════════════════════════════════════════════
    if (diwaOpt1_5)
    {
        // ── Deeper + louder core ─────────────────────────────────────────────
        // Half the normal firing frequency (an octave down) for "deeper",
        // and a noticeably higher gain ceiling than the base engine core for
        // "louder" -- both applied as an ADDITIONAL layer on top of the
        // normal engineSample rather than replacing it, so it reads as the
        // same engine with more low-end weight and volume, not a different
        // instrument.
        float coreHz = Mathf.Max(rpm, IDLE) / 60f * 0.5f;
        double core1 = Math.Sin(2.0 * Math.PI * ph_v6_shakeCore1);
        double core2 = Math.Sin(2.0 * Math.PI * ph_v6_shakeCore2 * 1.503);
        float coreVol = (0.10f + rn * 0.14f + ld * 0.05f) * 1.6f; // 1.6x -- "louder"
        engineSample += (core1 * 1.0 + core2 * 0.5) * coreVol * engMul;
        ph_v6_shakeCore1 = (ph_v6_shakeCore1 + coreHz         * invSR) % 1.0;
        ph_v6_shakeCore2 = (ph_v6_shakeCore2 + coreHz * 0.997 * invSR) % 1.0;

        // ── Simulated floor vibration -- dense rapid-fire click train ───────
        // Strongest at/near idle (spd near 0), loud per spec ("actually
        // make noise and loud"). Rate is fast enough (18-30 Hz) that
        // individual clicks blur together into a felt buzz rather than
        // reading as separate hits, unlike the opt1_4 piston-fire layer.
        float idleWeight = 1f - Mathf.Clamp01(spd / 8f); // strongest below ~8 km/h, gone by ~8
        if (idleWeight > 0.01f)
        {
            float clickHz = 22f + rpm * 0.01f; // slight rpm dependence, mostly constant
            double clickFrac = ph_v6_shakeClick;
            double clickDecay = Math.Exp(-clickFrac * 14.0);
            double clickNoise = NextNoiseSample();
            double click = clickDecay * clickNoise;
            txSample += click * idleWeight * 0.16 * engMul;
            ph_v6_shakeClick = (ph_v6_shakeClick + clickHz * invSR) % 1.0;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  16. OPT1_6 — EERIE ELECTRIC-SOUNDING WHINE (idle anomaly)
    //
    //  Per Voith's own DIWA.5 technical manual, the real hydrodynamic whine
    //  comes from broadband turbulent fluid noise in the pump/turbine
    //  torus -- not a pure tone. A hydrodynamic unit sounding almost
    //  electric-motor-like is therefore a genuine ANOMALY, not a documented
    //  behavior: the read here is unusually tight impeller/turbine
    //  clearances producing a resonant, near-pure-tone whistle instead of
    //  the normal broadband wash -- a manufacturing quirk, same lore
    //  category as the zombie H50EP prototype elsewhere in this fleet, not
    //  a real DIWA characteristic.
    //
    //  Deliberately NOT built on opt1_4's hiss machinery (v6_hissLP/
    //  v6_hissPrev, hissEnv) -- own oscillators entirely, since "without
    //  the hush of air" means this voice never touches that block at all,
    //  not that the hiss is muted after the fact.
    //
    //  Only active near-stationary (idle/0 kph) -- a moving bus doesn't
    //  trigger this at all, per spec. RPM-following uses the SAME extended
    //  gear-1 timing opt1_4 gets (see opt1_4G1Extend's diwaOpt1_6 branch in
    //  DoVoithRange), so the whine's pitch tracks a bus that's holding
    //  gear 1 unusually long, same shape as opt1_4's own extended G1 --
    //  just a completely different voice riding on top of that timing.
    // ═════════════════════════════════════════════════════════════════════════
    if (diwaOpt1_6 && spd < 1.0f)
    {
        // ── Choppy/stepped pitch climb -- NOT a smooth glide. Holds one
        // pitch offset for a short random-ish duration, then jumps to the
        // next, so the whine's rise reads as juddery/uneven rather than a
        // clean sweep -- an electric motor with a failing speed controller,
        // not a smoothly spinning-up turbine.
        v6_opt6ChopTimer -= (float)invSR;
        if (v6_opt6ChopTimer <= 0f)
        {
            v6_opt6ChopTimer = 0.09f + (float)(NextNoiseSample() * 0.5 + 0.5) * 0.16f; // holds 90-250ms per step
            v6_opt6ChopHz = (float)(NextNoiseSample() * 0.5 + 0.5) * 180f; // each jump lands somewhere in a 0-180Hz offset range
        }

        // ── Loudness ramps with rn (gear-1 progress), capped at 0.85 --
        // past that point it doesn't get any louder, matching "gains
        // loudness as the gear progresses specifically 0.85."
        float loudnessRn = Mathf.Clamp01(rn / 0.85f);

        // ── Slow oscillation while idling -- ~0.4Hz amplitude LFO, on top
        // of (not instead of) the rn-based loudness ramp above.
        double lfo = Math.Sin(2.0 * Math.PI * ph_v6_opt6LFO) * 0.5 + 0.5;
        ph_v6_opt6LFO = (ph_v6_opt6LFO + 0.4 * invSR) % 1.0;

        // ── The whine itself -- base pitch in the range a real electric
        // traction motor whine sits (much higher and purer than a
        // hydrodynamic torque converter's broadband wail), plus the choppy
        // stepped offset above, plus a quiet detuned second partial so it's
        // not a completely flat single sine.
        float opt6BaseHz = 640f + v6_opt6ChopHz;
        double w1 = Math.Sin(2.0 * Math.PI * ph_v6_opt6Whine1);
        double w2 = Math.Sin(2.0 * Math.PI * ph_v6_opt6Whine2 * 1.997); // near-2nd-harmonic, slight detune keeps it from sounding perfectly clean
        double whineMix = w1 * 0.8 + w2 * 0.2;

        float vol = (0.05f + loudnessRn * 0.11f) * (0.6f + (float)lfo * 0.4f);
        txSample += whineMix * vol * engMul;

        ph_v6_opt6Whine1 = (ph_v6_opt6Whine1 + opt6BaseHz         * invSR) % 1.0;
        ph_v6_opt6Whine2 = (ph_v6_opt6Whine2 + opt6BaseHz * 0.997 * invSR) % 1.0;
    }

    // [FIX — restore] Undo the opt-flag zeroing from the top of this
    // function now that every opt-gated block above has run (or not run,
    // for an 864.5 call) for this sample.
    // ── opt1_8 "STRAINED": growl + high-rev BRRRRR ────────────────────────────
    // Two stages, both a rough pulse train locked to the engine's real firing rate (rpm/60 x cylinders/2),
    // so it tracks revs like an ISL growl rather than sounding like a separate tone:
    //   GROWL  -- comes in the moment revs leave idle territory (~1000 rpm), full by ~1600, about half strength.
    //   BRRRR  -- from ~62% of the rev range to ~92% it takes over at full strength: the HDS300 / EP50 buzz.
    // A slow ~9 Hz flutter on the gate makes it growl instead of hum. BusController adds the body tremor.
    // Off for everything except opt1_8 D864.6 buses (opt1_8 is cleared above for D864.5).
    if (diwaOpt1_8)
    {
        float rpmNow = Mathf.Max(rpm, IDLE);
        float growl  = Mathf.Clamp01((rpmNow - 1000f) / 600f);
        float top    = Mathf.Clamp01((rn - 0.62f) / 0.30f);
        float amt    = Mathf.Max(growl * 0.55f, top);
        if (amt > 0.001f)
        {
            float bzHz = rpmNow / 60f * Mathf.Max(2f, CYLINDERS * 0.5f) + ld * 14f;
            v6_strainBuzzPh += bzHz * invSR;
            if (v6_strainBuzzPh > 1.0) v6_strainBuzzPh -= 1.0;
            v6_strainFlutPh += 9.0 * invSR;
            if (v6_strainFlutPh > 1.0) v6_strainFlutPh -= 1.0;
            double a = 2.0 * Math.PI * v6_strainBuzzPh;
            double strainPulse = Math.Sin(a) + 0.55 * Math.Sin(2.0 * a) + 0.30 * Math.Sin(3.0 * a);
            double flutter = 0.78 + 0.22 * Math.Sin(2.0 * Math.PI * v6_strainFlutPh);
            txSample += strainPulse * flutter * (amt * amt) * (0.030 + 0.030 * ld) * engMul;
        }
    }

    if (legacy854) { diwaOpt1_4 = savedOpt4; diwaOpt1_5 = savedOpt5; diwaOpt1_6 = savedOpt6; diwaOpt1_7 = savedOpt7; diwaOpt1_8 = savedOpt8; }
}
// ═════════════════════════════════════════════════════════════════════════
//  VOITH DIWA 867.8 NXT — full standalone voice. Fresh build, own fields
//  throughout (nxt_*) — does NOT run DoVoithDSP/DoD8645DSP underneath.
//  [CLARIFICATION, per direct request] NXT (867.8) is NOT another D864.x
//  variant -- it's Voith's NEWER transmission generation, the successor
//  line to D864.5/D864.6 (same DIWA differential-converter family/
//  mechanical principle, later generation of hardware/model number).
//  That's WHY it deliberately stays a fully separate standalone DSP voice
//  instead of sharing DoVoithDSP the way D8645/D8646/D8646art all do with
//  each other -- it's meant to genuinely sound like a newer unit, not a
//  reskinned D864.x.
//  See the nxt_* field block's header comment for the full research basis.
// ═════════════════════════════════════════════════════════════════════════
private void DoNXTDSP(ref double txSample, float rn, float ld, float hz,
                       float outRPM, float engMul, bool retAct, double invSR)
{
    // ── Launch-only converter voice — confined to gear 1, hard-fades by
    // gear 3. NXT's converter is optimized purely for start-up (no longer
    // doing double duty as a retarder), so unlike the classic DIWA whine
    // elsewhere in this fleet, there's no mechanical reason for this to
    // linger into cruise.
    float convGearGain = gear <= 1 ? 1.0f : gear == 2 ? 0.35f : 0f;
    float convTarget    = convGearGain * (0.05f + ld * 0.05f) * engMul;
    nxt_convVolSmooth += (convTarget - nxt_convVolSmooth) * (convTarget > nxt_convVolSmooth ? 0.03f : 0.02f);
    if (nxt_convVolSmooth > 0.002f)
    {
        double convHz = 130.0 + outRPM * 0.42;
        txSample += Math.Sin(2.0 * Math.PI * ph_nxt_conv1) * nxt_convVolSmooth
                  + Math.Sin(2.0 * Math.PI * ph_nxt_conv2) * nxt_convVolSmooth * 0.4
                  + noise_lp * nxt_convVolSmooth * 0.25;
        ph_nxt_conv1 = (ph_nxt_conv1 + convHz       * invSR) % 1.0;
        ph_nxt_conv2 = (ph_nxt_conv2 + convHz * 1.5 * invSR) % 1.0;
    }

    // ── Separate hydrodynamic retarder — real spec: up to 1,800 Nm, brakes
    // almost to standstill, a genuinely independent fluid circuit from the
    // converter above. Gated purely by retAct (bkPd > 0.30 at real speed) --
    // the actual "world-first separated converter and retarder" feature
    // made audible as two distinct voices instead of one shared mechanism.
    float retTarget = retAct ? (0.06f + (spd / MAX_SPD) * 0.08f) * engMul : 0f;
    nxt_retVolSmooth += (retTarget - nxt_retVolSmooth) * (retAct ? 0.05f : 0.03f);
    if (nxt_retVolSmooth > 0.002f)
    {
        double retHz = 60.0 + (spd / MAX_SPD) * 70.0; // deep, road-speed-tracked drone -- reads as its own dedicated unit, not a converter byproduct
        txSample += Math.Sin(2.0 * Math.PI * ph_nxt_ret1) * nxt_retVolSmooth
                  + Math.Sin(2.0 * Math.PI * ph_nxt_ret2) * nxt_retVolSmooth * 0.5
                  + noise_lp * nxt_retVolSmooth * 0.35;
        ph_nxt_ret1 = (ph_nxt_ret1 + retHz       * invSR) % 1.0;
        ph_nxt_ret2 = (ph_nxt_ret2 + retHz * 0.5 * invSR) % 1.0;
    }

    // ── Planetary mesh whine — quiet by design in gears 5/6. The whole
    // point of a held 2nd overdrive is a genuinely quiet cruise, so unlike
    // Allison/classic-DIWA mesh layers elsewhere, this deliberately tapers
    // off rather than holding steady at speed.
    float meshGearGain = gear <= 2 ? 1.0f : gear <= 4 ? 0.55f : 0.22f; // gears 5/6 noticeably quieter
    float meshTarget    = meshGearGain * (0.018f + ld * 0.02f) * engMul;
    nxt_meshVolSmooth += (meshTarget - nxt_meshVolSmooth) * (meshTarget > nxt_meshVolSmooth ? 0.01f : 0.006f);
    if (nxt_meshVolSmooth > 0.001f)
    {
        double meshHz = (rpm / 60.0) * 27.0; // real tooth-count-style order, own value not shared with any classic Voith mesh
        txSample += Math.Sin(2.0 * Math.PI * ph_nxt_mesh1) * nxt_meshVolSmooth
                  + Math.Sin(2.0 * Math.PI * ph_nxt_mesh2) * nxt_meshVolSmooth * 0.4;
        ph_nxt_mesh1 = (ph_nxt_mesh1 + meshHz       * invSR) % 1.0;
        ph_nxt_mesh2 = (ph_nxt_mesh2 + meshHz * 2.0 * invSR) % 1.0;
    }

    // ── Shift thud — rarer at cruise by design (held-overdrive means fewer
    // shift EVENTS once at speed, not a quieter thud when one happens).
    if (gear != nxt_lastGearAudio) { nxt_shiftThud = 1.0f; ph_nxt_thud1 = 0.0; ph_nxt_thud2 = 0.0; nxt_lastGearAudio = gear; } // [FIX -- click bug] zero the thud's own phase on every envelope-open, kills the shift "tick"
    if (nxt_shiftThud > 0f)
    {
        txSample += Math.Sin(2.0 * Math.PI * ph_nxt_thud1) * nxt_shiftThud * 0.22 * engMul;
        txSample += Math.Sin(2.0 * Math.PI * ph_nxt_thud2) * nxt_shiftThud * 0.11 * engMul;
        nxt_shiftThud *= 0.9975f; // quicker decay than classic DIWA -- newer, tighter valve body
        if (nxt_shiftThud < 0.005f) nxt_shiftThud = 0f;
    }
    ph_nxt_thud1 = (ph_nxt_thud1 + 48.0 * invSR) % 1.0;
    ph_nxt_thud2 = (ph_nxt_thud2 + 88.0 * invSR) % 1.0;

    // ── 48V mild-hybrid CRU — four real states, all gated by economyMode
    // (the in-game Eco button doing double duty as this system's ON/OFF
    // switch). Small, thin, high-register texture throughout -- real spec
    // is 25kW continuous/35kW peak, a tiny fraction of a full traction
    // motor, so this should read as a subtle accessory whine layered on
    // top of the diesel, never as a second engine.
    if (economyMode)
    {
        // Boost: light-moderate acceleration assist. Caps out fast (real
        // 35kW peak is small), so it's only really audible in the
        // light-to-moderate throttle band -- under hard acceleration the
        // diesel is doing essentially all the work again and this fades out.
        float boostBand   = Mathf.Clamp01(1f - Mathf.Abs(ld - 0.35f) / 0.35f); // peaks around ld≈0.35, fades at both ends
        float boostTarget = gear > 0 && spd > 1f ? boostBand * 0.035f * engMul : 0f;
        nxt_boostVolSmooth += (boostTarget - nxt_boostVolSmooth) * (boostTarget > nxt_boostVolSmooth ? 0.02f : 0.015f);
        if (nxt_boostVolSmooth > 0.001f)
        {
            double boostHz = 900.0 + ld * 260.0; // small, high, inverter-ish -- distinct register from the diesel/converter/retarder below it
            txSample += Math.Sin(2.0 * Math.PI * ph_nxt_boost) * nxt_boostVolSmooth
                      + noise_hi * nxt_boostVolSmooth * 0.3;
            ph_nxt_boost = (ph_nxt_boost + boostHz * invSR) % 1.0;
        }

        // Regen: LIGHT brake only -- the electric side takes the load
        // first, before handing off to the separate hydrodynamic retarder
        // once retAct's own threshold (bkPd > 0.30) is crossed. Real
        // layered behavior: quiet electric whir under light braking, then
        // the retarder's deeper drone takes over once you push harder.
        float regenTarget = (!retAct && bkPd > 0.04f && spd > 1f) ? Mathf.Clamp01(bkPd / 0.30f) * 0.03f * engMul : 0f;
        nxt_regenVolSmooth += (regenTarget - nxt_regenVolSmooth) * (regenTarget > nxt_regenVolSmooth ? 0.025f : 0.02f);
        if (nxt_regenVolSmooth > 0.001f)
        {
            double regenHz = 700.0 + (spd / MAX_SPD) * 300.0;
            txSample += Math.Sin(2.0 * Math.PI * ph_nxt_regen) * nxt_regenVolSmooth
                      + noise_hi * nxt_regenVolSmooth * 0.25;
            ph_nxt_regen = (ph_nxt_regen + regenHz * invSR) % 1.0;
        }

        // Soft re-crank -- CalcNXTRPM sets nxt_restartPulse=1 the instant
        // the driver goes to move again after an engine-off stop. Quick,
        // gentle envelope (48V-assisted spin-up, not a mechanical starter
        // grind), distinct from every other TX's engine-start behavior in
        // this fleet since none of them have real stop-start at all.
        if (nxt_restartPulse > 0f)
        {
            double crankHz = 90.0 + (1.0 - nxt_restartPulse) * 340.0; // quick upward whirr
            txSample += Math.Sin(2.0 * Math.PI * ph_nxt_boost * 0.7) * nxt_restartPulse * 0.05 * engMul
                      + noise_hi * nxt_restartPulse * 0.04 * engMul;
            nxt_restartPulse -= (float)(invSR / 0.4); // ~0.4s soft crank, quick
            if (nxt_restartPulse < 0f) nxt_restartPulse = 0f;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  VOITH DIWA.5 (D864.5) — COMPLETE REDO, real-world grounded
//
//  Confirmed via Voith's own DIWA.6 launch materials: DIWA.6's headline
//  improvement over DIWA.5 is a "new 'smart' electro-hydraulic circuit which
//  can control the main operating pressure of the transmission and reduce
//  its load on the engine when full pressure is not required." D864.5
//  predates that circuit entirely -- it runs its converter/pump hydraulics
//  at a roughly CONSTANT baseline operating pressure regardless of
//  throttle. That is the real, mechanically grounded reason D864.5 should
//  never carry any of D864.6's more refined, load-adaptive, electronic-
//  sounding character (including the accessory whine the D864.6 pass added
//  under [Voith inverter fix]) -- D864.5 instead gets an always-on
//  converter/pump noise floor that does NOT taper at light throttle the
//  way D864.6's legitimately does, because D864.5 has no circuit capable
//  of doing that tapering in the first place.
//
//  Also fixed: `retAct` was a parameter on this function that was NEVER
//  READ anywhere in the old body -- ph_v5_Ret/ph_v5_Ret2 spun uselessly off
//  a fixed v5_RetHz constant every frame with nothing ever sampling them.
//  D864.5 had literally no audible retarder at all despite genuinely having
//  one (same converter-re-coupled-as-retarder mechanism every DIWA
//  generation uses). Real retarder pitch now tracks road/output speed under
//  braking, same physical reasoning as the D864.6 rebuild.
//
//  Also added: real Automatic Neutral at Standstill (confirmed present
//  since the DIWA 2 generation, 1985+) -- see CalcD8645RPM. This function
//  now folds ANS into every layer via ansMul, same pattern D864.6 uses.
//
//  Kept, already correctly reasoned in the prior pass: 4-speed w/ 0.70
//  overdrive 4th gear (real spec for D864.5/D884.5), heavier/looser shift
//  thud and move-off grab than D864.6, coarser end-of-gear-1 buzz, more
//  torque-converter slip noise (the direct plain-language consequence of
//  lacking smart pressure control -- slip isn't being actively minimized).
// ═══════════════════════════════════════════════════════════════════════════════
private void DoD8645DSP(ref double txSample, float rn, float ld, float hz,
                         float outRPM, float engMul, bool retAct, double invSR)
{
    // ── ANS pass-through — input clutch disengaged means the converter
    // isn't being driven at all, so every layer below reads as genuinely
    // quieter than a normal in-gear stop. Folded in once as ansMul rather
    // than gating each layer separately.
    float ansMul = d5_ansActive ? 0.10f : 1.0f;
    if (d5_ansEngagePulse > 0.001f)
    {
        double clunk = Math.Sin(2.0 * Math.PI * ph_d5_grab) * d5_ansEngagePulse * 0.14 * engMul
                     + noise_lp * d5_ansEngagePulse * 0.09 * engMul;
        txSample += clunk;
        d5_ansEngagePulse -= (float)(invSR / 0.30);
        if (d5_ansEngagePulse < 0f) d5_ansEngagePulse = 0f;
    }

    // ── Articulated response — same body/retarder deepening D864.6 gets
    // for a 60ft artic, ported directly (engine-agnostic multiplier, no new
    // state needed).
    float articDeepen = isArticulatedEngine ? Mathf.Lerp(1.0f, 1.22f, Mathf.Clamp01(ld)) : 1.0f;
    engMul *= articDeepen;

    // ── Second converter voice — fixed ~633Hz tone, shared/generation-
    // agnostic function (mutually exclusive tx per bus, safe to call from
    // both D864.6 and D864.5 as-is per its own comment).
    DoVoithFixedToneDSP(ref txSample, ld, engMul, invSR);

    // ── [NEW] Always-on converter/pump noise floor. D864.5 has no
    // load-adaptive pressure circuit (see header) -- this does NOT taper
    // down at light throttle/idle the way D864.6's whine does, only
    // dropping under real ANS (above) or out of gear entirely. This is the
    // concrete, audible difference from D864.6's more refined voice, and
    // the direct fix for "shouldn't have that [electronic/smart] sound".
    {
        float floorTarget = (gear > 0 ? 0.65f : 0f) * ansMul;
        d5_pumpFloorVol += (floorTarget - d5_pumpFloorVol) * (floorTarget > d5_pumpFloorVol ? 0.010f : 0.006f);
        if (d5_pumpFloorVol > 0.001f)
        {
            double pf1 = Math.Sin(2.0 * Math.PI * ph_d5_pumpFloor1);
            double pf2 = Math.Sin(2.0 * Math.PI * ph_d5_pumpFloor2);
            // Deliberately dull/mechanical -- mostly filtered noise with a
            // low fundamental riding under it, nothing that reads as clean
            // or electronic.
            double floorTone = (pf1 * 0.35 + pf2 * 0.20) + noise_lp * 0.55;
            txSample += floorTone * d5_pumpFloorVol * 0.052 * engMul;
            ph_d5_pumpFloor1 = (ph_d5_pumpFloor1 + 46.0       * invSR) % 1.0;
            ph_d5_pumpFloor2 = (ph_d5_pumpFloor2 + 46.0 * 1.5 * invSR) % 1.0;
        }
    }

    // ── Drive whine core — [REBUILT] same Differential-Wandler mesh physics
    // as D864.6's v6_ whine model, ported straight over per instruction
    // (they sound the same). Same teeth ratio, same hard-cutoff-on-G1->G2 /
    // step-relock-on-other-shifts transition, same stretched gear-1 build.
    // D864.5-specific difference: the whine gets audibly DEEPER (not just
    // louder) as rpm climbs -- a suboctave blends in and the fundamental
    // droops slightly at the top of the rev range, unlike D864.6 which
    // stays clean/bright throughout. See v5_deepenAmt below.
    float currentTeeth = gear <= 1 ? 23.0f : 21.5f;

    const float PITCH_TAU_UP   = 0.016f;
    const float PITCH_TAU_DOWN = 0.012f;

    float meshTarget = (rpm / 60f) * currentTeeth;
    float loadStrain = ld * 0.030f * Mathf.Clamp01(1f - rn);
    float meshMod = meshTarget * (1f - loadStrain);

    float pumpWobble = 0f, pumpGrit = 0f;
    if (kickdownKey && pumpEnergy > 0.01f)
    {
        float wobbleHz = 3.0f + pumpEnergy * 5f;
        pumpWobble = Mathf.Sin((float)audioClock * Mathf.PI * 2f * wobbleHz) * pumpEnergy * 0.06f;
        pumpGrit   = pumpEnergy;
    }
    meshMod *= (1f + pumpWobble);

    // ── Gear-1 stretched build, ported from V6_G1_BUILD_SEC/HOLD_SEC/
    // PITCH_SPAN (D864.6's own constants, reused directly -- "sound the
    // same" per instruction, so no separate d5-specific timing constants).
    if (gear <= 1)
    {
        if (!v5_wasInGear1) v5_g1EnvTimer = 0f;
        v5_wasInGear1 = true;
        v5_g1EnvTimer += (float)invSR;

        float g1Top2      = VOITH_GEAR1_UPSHIFT_SPD * 1.15f * (kickdownKey ? 1.32f : 1f);
        float g1PitchProg = Mathf.Clamp01(spd / Mathf.Max(1f, g1Top2));
        float envPitch    = Mathf.Lerp(1.0f, V6_G1_PITCH_SPAN, g1PitchProg);
        meshMod *= envPitch;
    }
    else
    {
        v5_wasInGear1 = false;
    }

    // ── Upshift whine transition — ported from D864.6's v6_ model. Hard
    // vertical cutoff on G1->G2, ~220ms audible step-relock on every other
    // shift, instead of the old single continuous ptau chase.
    bool isG1toG2v5 = (v5_lastGearWhine == 1 && gear == 2);
    if (v5_lastGearWhine != -1 && v5_lastGearWhine != gear)
    {
        if (isG1toG2v5)
        {
            v5_whHoldTimer = 0f;
            v5_whineHzSmooth = meshMod;
        }
        else
        {
            v5_whHoldTimer = 0.20f;
            v5_whHoldHz    = v5_whineHzSmooth;
            v5_whRelockWin = 0.22f;
        }
    }
    v5_lastGearWhine = gear;

    if (v5_whHoldTimer > 0f)
    {
        v5_whHoldTimer -= (float)invSR;
        v5_whineHzSmooth = v5_whHoldHz;
    }
    else if (v5_whRelockWin > 0f)
    {
        v5_whRelockWin -= (float)invSR;
        float relockPtau = (float)invSR / 0.20f;
        v5_whineHzSmooth += (meshMod - v5_whineHzSmooth) * relockPtau;
    }
    else
    {
        float ptau = meshMod > v5_whineHzSmooth ? PITCH_TAU_UP : PITCH_TAU_DOWN;
        if (kickdownKey && pumpEnergy > 0.01f) ptau *= (1f + pumpEnergy * 2.4f);
        v5_whineHzSmooth += (meshMod - v5_whineHzSmooth) * ptau;
    }
    if (v5_whineHzSmooth < 2f) v5_whineHzSmooth = 0f;

    // ── [NEW] Deepen at high rpm — D864.5-only character, per instruction
    // this is the WHINE ITSELF getting deeper, not a separate vibration
    // layer (see removal notes further down: the old buzz/shake/vibration
    // layers are gone). v5_deepenAmt grows 0->1 from idle to governor and
    // is applied directly inside the oscillator below.
    float rpmFrac = Mathf.Clamp01((rpm - IDLE) / Mathf.Max(1f, GOV - IDLE));
    v5_deepenAmt += (rpmFrac * rpmFrac - v5_deepenAmt) * 0.01f;

    double fW0 = v5_whineHzSmooth * 0.5;          // suboctave, blended into the chord as deepenAmt rises
    double fW1 = v5_whineHzSmooth;
    double fW2 = v5_whineHzSmooth * 2.003;        // near-2nd-harmonic, same beat-rate trick as D864.6's
    double fW3 = v5_whineHzSmooth * 3.01;

    // ── [NEW] Full whTarget-style volume envelope, ported from D864.6:
    // same gear-based stepping (0.38x in G2/G3, 0.18x in G4+), same timed
    // gear-1 build/hold (reusing v5_g1EnvTimer + D864.6's own constants),
    // same "louder when basically stopped with no throttle" boost.
    float gearMulVol = 1.0f;
    if (gear == 2 || gear == 3) gearMulVol = 0.38f;
    else if (gear >= 4)         gearMulVol = 0.18f;

    float d5G1EnvVol = 1.0f;
    if (gear == 1)
    {
        float buildT = Mathf.Clamp01(v5_g1EnvTimer / V6_G1_BUILD_SEC);
        float holdT  = Mathf.Clamp01((v5_g1EnvTimer - V6_G1_BUILD_SEC) / V6_G1_HOLD_SEC);
        float envVol = Mathf.Lerp(0.06f, 1.0f, buildT * buildT);
        d5G1EnvVol = Mathf.Lerp(envVol, 1.15f, holdT);
    }

    float whineVol = 0f;
    if (gear >= 1 && rpm > IDLE * 0.9f)
    {
        // [NOT TRIMMED] D864.6's own version pulls this ~15% quieter --
        // D864.5 keeps the fuller level, consistent with it already
        // reading louder/cruder everywhere else in this function.
        whineVol = (0.020f + rn * 0.047f + ld * 0.026f) * gearMulVol;
        if (gear == 1) whineVol *= d5G1EnvVol;
        if (spd < 0.5f && accel < 0.03f) whineVol *= 1.75f;
        whineVol *= ansMul * (1f + pumpGrit * 0.34f) * engMul;
    }

    if (v5_whineHzSmooth > 40f)
    {
        float rnPitch = Mathf.Clamp01((rpm - IDLE) / (GOV - IDLE));

        double p1 = Mathf.Lerp(1.75f, 2.45f, rnPitch);
        double p2 = Mathf.Lerp(1.90f, 2.75f, rnPitch);
        double p3 = Mathf.Lerp(2.10f, 3.10f, rnPitch);

        double w1 = Math.Sin(p1 * Math.PI * ph_v5_w1);
        double w2 = Math.Sin(p2 * Math.PI * ph_v5_w2);
        double w3 = Math.Sin(p3 * Math.PI * ph_v5_w3);

        double chord = w1 * 1.00 + w2 * 0.48 + w3 * 0.24;

        // ── Screech layer, same mechanism as D864.6's -- fades in as rpm
        // climbs toward redline.
        float screechThresh = GOV * 0.86f;
        float screechRaw = Mathf.Clamp01((Mathf.Max(rpm, IDLE) - screechThresh) / Mathf.Max(1f, GOV - screechThresh));
        d5_screechSmooth += (screechRaw - d5_screechSmooth) * (screechRaw > d5_screechSmooth ? 0.0035f : 0.008f);
        if (d5_screechSmooth > 0.001f)
        {
            double w4 = Math.Sin(2.0 * Math.PI * ph_d5_wh4);
            chord += w4 * 0.34 * d5_screechSmooth;
            chord += noise_hi * 0.22 * d5_screechSmooth;
        }

        // ── [DEEPEN] The actual requested effect: the whine ITSELF gets
        // deeper as rpm climbs -- a suboctave blends in and the bright
        // chord above fades proportionally, so the tone genuinely shifts
        // down and coarsens rather than gaining an extra layer under an
        // unchanged bright tone.
        double w0 = Math.Sin(2.0 * Math.PI * ph_v5_deepenSub);
        chord = chord * (1.0 - v5_deepenAmt * 0.55) + w0 * 1.35 * v5_deepenAmt;

        // Hoarser mix than D864.6 throughout -- more noise, less clean edge.
        float brightBoost = 0.020f + rn * 0.020f * (1f - v5_deepenAmt);
        txSample += (chord + noise_hi * brightBoost) * whineVol + noise_lp * (whineVol * 0.14);

        if (pumpGrit > 0.01f)
        {
            double gritDrive = Math.Tanh(w1 * (2.2 + pumpGrit * 3.0));
            txSample += gritDrive * whineVol * pumpGrit * 0.42;
        }
    }

    // ── TC slip noise — more prominent than D864.6's. Real, direct
    // consequence of lacking smart pressure control: slip isn't being
    // actively minimized the way D864.6's circuit does it.
    bool sustainedSlip = gear > 0;
    float slipTarget = (sustainedSlip ? (gear <= 1 ? ld * 0.85f + 0.10f : ld * 0.30f + 0.04f) : 0f) * ansMul;
    v5_tcSlipSmooth += (slipTarget - v5_tcSlipSmooth) * 0.004f;
    if (v5_tcSlipSmooth > 0.005f)
    {
        float tcVol = (0.034f + v5_tcSlipSmooth * 0.060f) * engMul;
        txSample += noise_lp * tcVol;
    }

    // ── Shift thud — heavier, lower, slower decay than D864.6 (cruder
    // valve body, bigger fill lag). Also fires the pump-impeller spin-down
    // below on the specific 1->2 transition.
    if (gear != v5_lastGearAudio)
    {
        if (v5_lastGearAudio == 1 && gear == 2) d5_pumpSpinDown = 1f;
        v5_shiftThud = 1.0f; ph_v5_thud1 = 0.0; ph_v5_thud2 = 0.0; // [FIX -- click bug] zero the thud's own phase on every envelope-open, kills the shift "tick"
        v5_lastGearAudio = gear;
    }
    if (v5_shiftThud > 0f)
    {
        txSample += Math.Sin(2.0 * Math.PI * ph_v5_thud1) * v5_shiftThud * 0.28 * engMul;
        txSample += Math.Sin(2.0 * Math.PI * ph_v5_thud2) * v5_shiftThud * 0.16 * engMul;
        v5_shiftThud *= 0.9965f;
        if (v5_shiftThud < 0.004f) v5_shiftThud = 0f;
    }

    // ── [NEW] Pump-impeller spin-down on 1->2, ported from D864.6's
    // voith_pumpSpinDown. Same "additionally decelerated by residual
    // converter pressure" mechanism the manual describes for every DIWA
    // generation. Dirtied: lower starting pitch, ~40% longer drag-down
    // (cruder valve body takes longer to bleed pressure), louder noise mix.
    if (d5_pumpSpinDown > 0.001f)
    {
        double spinHz = 34.0 + d5_pumpSpinDown * 180.0;
        double spin = Math.Sin(2.0 * Math.PI * ph_d5_pumpSpin) * d5_pumpSpinDown * 0.15 * engMul
                    + noise_lp * d5_pumpSpinDown * 0.13 * engMul;
        txSample += spin;
        ph_d5_pumpSpin = (ph_d5_pumpSpin + spinHz * invSR) % 1.0;
        d5_pumpSpinDown -= (float)(invSR / 0.78); // ~0.78s drag-down, vs D864.6's 0.55s
        if (d5_pumpSpinDown < 0f) d5_pumpSpinDown = 0f;
    }

    // ── Tail whine at speed — [REBUILT] previously a clean output-tracked
    // sine pair at a clean 2.0x octave, which read too close to D864.6's
    // "sane RPM-tracked accessory whine" fix -- exactly the refined,
    // electronic-adjacent character D864.5 shouldn't carry. Roughened:
    // noise mixed in, detuned off the clean octave so the two partials beat
    // against each other instead of sitting in a pure unison relationship.
    float v5MwV = (spd > 1.5f)
        ? (0.010f + (spd / MAX_SPD) * 0.056f + ld * 0.010f) * engMul * ansMul : 0f;
    double fV5Mw = outRPM * 0.58, fV5Mw2 = fV5Mw * 1.97; // [CHANGED] was a clean 2.0 octave -- detuned into a beat
    if (v5MwV > 0f)
    {
        txSample += Math.Sin(2.0 * Math.PI * ph_v5_Mw)  * v5MwV * 0.75
                  + Math.Sin(2.0 * Math.PI * ph_v5_Mw2) * v5MwV * 0.25
                  + noise_lp * v5MwV * 0.30; // [NEW] noise-mixed -- reads as bearing/gear noise, not a clean whine
    }

    // ── Retarder — [FIX] previously dead: retAct was passed in and never
    // read, so this transmission had no audible retarder at all despite
    // genuinely having one. Same converter-re-coupled-as-retarder mechanism
    // as every DIWA generation -- pitch now tracks road/output speed under
    // braking, rougher/hoarser drive than D864.6's retarder (cruder
    // pressure regulation on the older unit).
    {
        float retHzTarget = 60f + spd * 1.55f;
        d5_retHzSmooth += (retHzTarget - d5_retHzSmooth) * (retAct ? 0.05f : 0.12f) * 60f * (float)invSR;

        float retVol = retAct ? (0.075f + bkPd * 0.065f) * engMul * ansMul : 0f;
        if (retVol > 0f)
        {
            double r1 = Math.Sin(2.0 * Math.PI * ph_v5_Ret);
            double r2 = Math.Sin(2.0 * Math.PI * ph_v5_Ret2);
            double roar = Math.Tanh((r1 * 0.65 + r2 * 0.20 + noise_lp * 0.85) * 2.6);
            txSample += roar * retVol;
        }
    }

    // ── Move-off grab lurch + end-of-gear-1 vibration — unchanged from the
    // prior pass, already correctly heavier/looser than D864.6.
    bool stoppedNowD5 = spd < 0.4f;
    if (d5_wasStoppedVo && !stoppedNowD5 && accel > 0.05f) d5_moveOffTimer = 0.80f;   // longer than D864.6's 0.55s
    d5_wasStoppedVo = stoppedNowD5;
    if (d5_moveOffTimer > 0f)
    {
        d5_moveOffTimer -= (float)invSR;
        float t = Mathf.Clamp01(d5_moveOffTimer / 0.80f);
        float grabHz = 38f + (1f - t) * 16f;   // lower/heavier than D864.6's 46-66Hz
        float judder = 0.5f + 0.5f * Mathf.Sin((float)audioClock * Mathf.PI * 2f * 8f); // slower judder, looser unit
        double grab = (Math.Sin(2.0 * Math.PI * ph_d5_grab) * 0.85 + noise_lp * 0.5)
                      * t * (0.14f + judder * 0.09f) * engMul;   // bigger than D864.6's 0.10+0.06
        txSample += grab;
        ph_d5_grab = (ph_d5_grab + grabHz * invSR) % 1.0;
    }

    // [REMOVED per instruction] The end-of-gear-1 "buzz" vibration layer
    // used to sit here (d5_g1VibVol / ph_d5_buzz) -- removed. The deepen
    // effect now lives entirely inside the whine oscillator above, not as
    // a separate choppy vibration layer.

    // ── [NEW] DIWA "BRRRRRRINGA" launch overtone, ported from D864.6.
    // Dirtied: fires a touch earlier (85% vs 88% through gear 1, an older
    // looser unit hunting sooner), runs slightly longer (3.3s vs 3.0s),
    // and leans harder into the raspy noise mix throughout.
    if (gear == 1 && v5_g1EnvTimer > 0f)
    {
        float g1Top3      = VOITH_GEAR1_UPSHIFT_SPD * 1.15f * (kickdownKey ? 1.32f : 1f);
        float g1ProgRinga = Mathf.Clamp01(spd / Mathf.Max(1f, g1Top3));
        if (g1ProgRinga > 0.85f && d5_ringaTimer <= 0f)
        {
            d5_ringaTimer = 3.3f;
            d5_ringaVol = 1.0f;
        }
    }
    if (d5_ringaTimer > 0f)
    {
        d5_ringaTimer -= (float)invSR;
        float t = 1f - Mathf.Clamp01(d5_ringaTimer / 3.3f);
        float growlHz = Mathf.Lerp(52f, 195f, t); // lower register than D864.6's 58-215

        ph_d5_ringa1 = (ph_d5_ringa1 + growlHz * invSR) % 1.0;
        ph_d5_ringa2 = (ph_d5_ringa2 + growlHz * 1.45 * invSR) % 1.0;
        ph_d5_ringa3 = (ph_d5_ringa3 + growlHz * 2.90 * invSR) % 1.0;

        double low  = Math.Sin(2.0 * Math.PI * ph_d5_ringa1);
        double mid  = Math.Sin(2.0 * Math.PI * ph_d5_ringa2);
        double high = Math.Sin(2.0 * Math.PI * ph_d5_ringa3);
        float rasp  = Mathf.Lerp(0.14f, 0.55f, t); // noisier throughout than D864.6's 0.08-0.42

        double sound = low * 1.00 + mid * 0.38 + high * 0.12 + noise_lp * rasp + noise_hi * rasp * 0.12;
        sound = Math.Tanh(sound * (1.5 + t));
        float env = Mathf.Sin(t * Mathf.PI);

        txSample += sound * env * 0.09 * engMul;

        if (d5_ringaTimer <= 0f) d5_ringaVol = 0f;
    }

    // ── [NEW] Converter churn (fluid hydraulic bed), ported from D864.6.
    // Same hydShare-driven swirl, dirtied with a rawer noise blend (no
    // smart pressure circuit smoothing the fluid path) and a slightly
    // louder floor at a dead stop in gear 1.
    {
        float g1TopChurn = VOITH_GEAR1_UPSHIFT_SPD * 1.15f * (kickdownKey ? 1.32f : 1f);
        float g1ProgChurn = gear == 1 ? Mathf.Clamp01(spd / Mathf.Max(1f, g1TopChurn)) : 0f;
        float hydShare = gear == 1 ? (1f - g1ProgChurn) * (1f - g1ProgChurn) : 0f;

        float churnTarget = 0f;
        if (gear == 1)
        {
            churnTarget = hydShare * (0.28f + ld * 0.80f);
            if (spd < 2f) churnTarget = Mathf.Max(churnTarget, 0.26f + ld * 0.34f);
        }
        d5_churnEnv += (churnTarget - d5_churnEnv) * (churnTarget > d5_churnEnv ? 0.010f : 0.004f);

        if (d5_churnEnv > 0.004f)
        {
            d5_churnLP += (noise_lp - d5_churnLP) * 0.26; // faster/rawer than D864.6's 0.22
            double swirl = 0.78 + 0.22 * Math.Sin(2.0 * Math.PI * ph_d5_churnMod);
            txSample += d5_churnLP * swirl * d5_churnEnv * 0.125 * engMul;
        }
        ph_d5_churnMod = (ph_d5_churnMod + 1.7 * invSR) % 1.0;
    }

    // [REMOVED per instruction] Stopped-idle body shake and RPM-load engine
    // vibration were ported over from D864.6 in an earlier pass, then
    // explicitly removed -- D864.5 doesn't get either vibration layer.

    // ── [NEW] Shaker chassis mechanics (creaks), ported from D864.6.
    // Older/looser buses shake more often -- 25% chance vs D864.6's 18%.
    if (!d5_shakerRolled) { d5_shakerRolled = true; d5_isShaker = (NextNoiseSample() * 0.5 + 0.5) < 0.25f; }
    if (d5_isShaker)
    {
        float shakeLoad = Mathf.Clamp01(accel * 1.2f) * Mathf.Clamp01(1f - spd / 45f) + (spd < 1f ? 0.24f : 0f);
        if (shakeLoad > 0.02f)
        {
            double bodyv   = Math.Sin(2.0 * Math.PI * (ph_d5_shakeBody * 0.78));
            double tremorv = 0.55 + 0.45 * Math.Sin(2.0 * Math.PI * (ph_d5_shakeBody * 4.9));
            txSample += bodyv * tremorv * shakeLoad * 0.048 * engMul; // a bit louder than D864.6's 0.040
        }
        if (d5_creakVol < 0.01f && shakeLoad > 0.15f && (NextNoiseSample() * 0.5 + 0.5) > 0.9993f) // creaks a bit more often
        {
            d5_creakVol = 0.6f + (float)(NextNoiseSample() * 0.5 + 0.5) * 0.4f;
        }
        if (d5_creakVol > 0.003f)
        {
            double c1 = Math.Sin(2.0 * Math.PI * ph_d5_creak1);
            double c2 = Math.Sin(2.0 * Math.PI * ph_d5_creak2);
            txSample += Math.Tanh((c1 + c2 * 0.5) * 1.8) * d5_creakVol * 0.05 * engMul; // harder tanh, louder
            double crHz = 290.0 + (1.0 - d5_creakVol) * 240.0; // lower/coarser than D864.6's 320+260
            ph_d5_creak1 = (ph_d5_creak1 + crHz        * invSR) % 1.0;
            ph_d5_creak2 = (ph_d5_creak2 + crHz * 1.48 * invSR) % 1.0;
            d5_creakVol *= 0.9970f; // decays a touch slower
            if (d5_creakVol < 0.004f) d5_creakVol = 0f;
        }
    }
    ph_d5_shakeBody = (ph_d5_shakeBody + 32.0 * invSR) % 1.0; // slightly lower than D864.6's 34Hz

    // ── [NEW] Gear-3 chassis body groan, ported from D864.6. A bit louder/
    // rougher, per the same crude-mounts pattern as the rest of D864.5.
    if (gear == 3) d5_g3DwellTimer += (float)invSR;
    else           d5_g3DwellTimer = 0f;
    float g3TargetD5 = (gear == 3) ? Mathf.Clamp01(d5_g3DwellTimer / 2.0f) * (0.06f + ld * 0.11f) * engMul : 0f;
    d5_g3GroanVol += (g3TargetD5 - d5_g3GroanVol) * (gear == 3 ? 0.006f : 0.02f);
    if (d5_g3GroanVol > 0.0006f)
    {
        float g3Hz = 39f + rn * 10f; // slightly lower than D864.6's 42
        ph_d5_g3Groan1 = (ph_d5_g3Groan1 + g3Hz         * invSR) % 1.0;
        ph_d5_g3Groan2 = (ph_d5_g3Groan2 + g3Hz * 1.021 * invSR) % 1.0; // wider detune, rougher beat
        double g3a = Math.Sin(2.0 * Math.PI * ph_d5_g3Groan1);
        double g3b = Math.Sin(2.0 * Math.PI * ph_d5_g3Groan2);
        double g3mix = g3a * 0.6 + g3b * 0.6 + noise_lp * 0.32;
        txSample += Math.Tanh(g3mix * 1.55) * d5_g3GroanVol;
    }

    // ── [NEW] Torque converter wail, ported from D864.6. Same G1-under-
    // load swell just before the 1->2 shift, hoarser/noisier than D864.6's
    // cleaner cry.
    {
        float g1TopWail = VOITH_GEAR1_UPSHIFT_SPD * 1.15f * (kickdownKey ? 1.32f : 1f);
        float g1ProgWail = gear == 1 ? Mathf.Clamp01(spd / Mathf.Max(1f, g1TopWail)) : 0f;
        float wailGate   = (gear == 1) ? Mathf.Clamp01(Mathf.InverseLerp(0.55f, 0.95f, g1ProgWail)) : 0f;
        float wailTarget = wailGate * wailGate * (0.11f + ld * 0.20f);
        d5_wailSmooth += (wailTarget - d5_wailSmooth) * (wailTarget > d5_wailSmooth ? 0.010f : 0.020f);

        if (d5_wailSmooth > 0.001f)
        {
            double w1  = Math.Sin(2.0 * Math.PI * ph_d5_wail1);
            double w2  = Math.Sin(2.0 * Math.PI * ph_d5_wail2);
            double cry = w1 * 0.80 + w2 * 0.42 + noise_hi * 0.28; // noisier than D864.6's 0.20
            txSample += Math.Tanh(cry * 1.7) * d5_wailSmooth * 0.17 * engMul;
        }
        ph_d5_wail1 = (ph_d5_wail1 + (double)v5_whineHzSmooth * 1.35 * invSR) % 1.0;
        ph_d5_wail2 = (ph_d5_wail2 + (double)v5_whineHzSmooth * 1.70 * invSR) % 1.0;
    }

    ph_v5_w1   = (ph_v5_w1   + fW1                    * invSR) % 1.0;
    ph_v5_w2   = (ph_v5_w2   + fW2                    * invSR) % 1.0;
    ph_v5_w3   = (ph_v5_w3   + fW3                    * invSR) % 1.0;
    ph_d5_wh4  = (ph_d5_wh4  + (double)v5_whineHzSmooth * 4.98 * invSR) % 1.0;
    ph_v5_deepenSub = (ph_v5_deepenSub + fW0          * invSR) % 1.0;
    ph_v5_Mw   = (ph_v5_Mw   + fV5Mw                  * invSR) % 1.0;
    ph_v5_Mw2  = (ph_v5_Mw2  + fV5Mw2                 * invSR) % 1.0;
    ph_v5_Ret  = (ph_v5_Ret  + (double)d5_retHzSmooth       * invSR) % 1.0;
    ph_v5_Ret2 = (ph_v5_Ret2 + (double)d5_retHzSmooth * 1.92 * invSR) % 1.0;
    ph_v5_thud1= (ph_v5_thud1+ 44.0                   * invSR) % 1.0;
    ph_v5_thud2= (ph_v5_thud2+ 100.0                  * invSR) % 1.0;
}

// ── Allison B400R — pump whine + retarder spit (research-based rework) ────
private double ph_b400_pump1, ph_b400_pump2;
private float  b400_pumpVolSmooth = 0f;
private float  b400_runTimeSec    = 0f;   // for cold->warm pump-whine fade
private double ph_b400_accWhSub;

[Tooltip("0 = off. Curated old-bus sound preset selected from the Fleet Roster (NOT the random ob_* toggles above — this is a separate, deliberate layer). Meaning of 1/2/3 depends on tx: see DoOldBusVariantCharacter.")]
public int oldBusVariant = 0;
private double ph_obv_moan1, ph_obv_moan2;
private double ph_obv_hollow1, ph_obv_hollow2, ph_obv_hollowMod;
private double ph_obv_wheeze;
private double ph_obv_tiredMotor1, ph_obv_tiredMotor2;
private double ph_obv_wornWhir1, ph_obv_wornWhir2;
// ═════════════════════════════════════════════════════════════════════════
//  OLD BUS VARIANT CHARACTER — curated, TX-specific worn-out presets,
//  picked per-series from the Fleet Roster dropdown (deliberate, not the
//  random ob_* toggles elsewhere in this file). oldBusVariant: 0 = off.
//  Meaning of 1/2/3 depends on tx:
//    b400r / b500r   : 1 = Deep Moan (tired engine), 2 = Hollow Engine
//    d8645 (Voith.5) : 1 = Deep Moan, 2 = Hollow Engine, 3 = Tired Wheeze
//    h50ep           : 1 = Deep Moan (tired motor), 2 = Hollow Motor,
//                      3 = Tired / Worn Motor
//    h40ep           : 1 = Worn Whir (its only preset)
// ═════════════════════════════════════════════════════════════════════════
private void DoOldBusVariantCharacter(ref double ob, float rn, float ld, float hz,
                                       float outRPM, float engVolPersonality, double invSR)
{
    if (oldBusVariant <= 0) return;
    // ZH50EP (the zombie 1001-1003 XDE60 prototype) has its own permanent,
    // always-on ghost character (see DoZombieGhostOverlay) — it's never
    // "selected" like a normal old-bus variant, it just always sounds like
    // that. Skip the generic curated system entirely for it so the two
    // don't fight/double up on the same bus.
    if (tx == "zh50ep") return;
    float vol = npcVolumeScale * engVolPersonality;

    bool isB400  = IsAllison();
    bool isB500  = tx == "b500r";
    bool isD8645 = tx == "d8645";
    bool isH50   = tx == "h50ep";
    bool isH40   = tx == "h40ep";

    // ── Deep Moan (tired engine) — low, ragged, breathy undertone that
    //    swells with load. b400r/b500r/d8645/h50ep variant 1. ────────────
    if (oldBusVariant == 1 && (isB400 || isB500 || isD8645 || isH50))
    {
        double m1 = Math.Sin(2.0 * Math.PI * ph_obv_moan1);
        double m2 = Math.Sin(2.0 * Math.PI * ph_obv_moan2);
        double moan = Math.Tanh((m1 * 1.0 + m2 * 0.55) * 1.6);
        float moanHz = isB500 ? 24f : isD8645 ? 30f : isH50 ? 34f : 27f; // deeper on bigger units
        float moanVol = (0.05f + rn * 0.05f + ld * 0.05f) * vol;
        ob += moan * moanVol + noise_lp * moanVol * 0.35;
        ph_obv_moan1 = (ph_obv_moan1 + moanHz         * invSR) % 1.0;
        ph_obv_moan2 = (ph_obv_moan2 + moanHz * 1.503 * invSR) % 1.0;
    }

    // ── Hollow Engine — resonant, boxy undertone (two close, deliberately
    //    non-harmonic partials beating against each other) like sound
    //    bouncing around loose interior panels. b400r/b500r/d8645/h50ep
    //    variant 2. ──────────────────────────────────────────────────────
    if (oldBusVariant == 2 && (isB400 || isB500 || isD8645 || isH50))
    {
        double h1 = Math.Sin(2.0 * Math.PI * ph_obv_hollow1);
        double h2 = Math.Sin(2.0 * Math.PI * ph_obv_hollow2);
        double hollowMod = 0.6 + 0.4 * Math.Sin(2.0 * Math.PI * ph_obv_hollowMod);
        double hollow = (h1 * 1.0 + h2 * 0.5) * hollowMod;
        float hollowBaseHz = 120f + outRPM * 0.35f;
        float hollowVol = (0.035f + ld * 0.045f) * vol;
        ob += hollow * hollowVol + noise_lp * hollowVol * 0.2;
        ph_obv_hollow1   = (ph_obv_hollow1   + hollowBaseHz        * invSR) % 1.0;
        ph_obv_hollow2   = (ph_obv_hollow2   + hollowBaseHz * 1.19 * invSR) % 1.0; // non-harmonic ratio = the "boxy" beat
        ph_obv_hollowMod = (ph_obv_hollowMod + 3.4                 * invSR) % 1.0;
    }

    // ── Tired Wheeze — d8645 (Voith.5) variant 3 only: a leaky, breathy
    //    high-passed hiss gated to the firing rate, like a worn valve-
    //    body seal losing pressure. ─────────────────────────────────────
    if (oldBusVariant == 3 && isD8645)
    {
        double wheezeGate = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * ph_obv_wheeze);
        float wheezeVol = (0.03f + ld * 0.03f) * vol * (float)Math.Max(0.0, wheezeGate);
        ob += noise_hi * wheezeVol * 0.8 + noise_hp_prev * wheezeVol * 0.4;
        ph_obv_wheeze = (ph_obv_wheeze + (hz * 0.5) * invSR) % 1.0;
    }

    // ── Tired / Worn Motor — h50ep variant 3 only: a grinding, uneven
    //    motor tone (slight detune + amplitude flutter) suggesting worn
    //    motor bearings on the electric-hybrid drive. ────────────────────
    if (oldBusVariant == 3 && isH50)
    {
        double t1 = Math.Sin(2.0 * Math.PI * ph_obv_tiredMotor1);
        double t2 = Math.Sin(2.0 * Math.PI * ph_obv_tiredMotor2);
        float flutter = 0.75f + 0.25f * Mathf.Sin((float)(audioClock * Math.PI * 2.0 * 5.5));
        float tiredVol = (0.03f + rn * 0.03f) * vol * flutter;
        ob += (t1 * 1.0 + t2 * 0.4) * tiredVol;
        float motorHz = 60f + outRPM * 1.4f;
        ph_obv_tiredMotor1 = (ph_obv_tiredMotor1 + motorHz         * invSR) % 1.0;
        ph_obv_tiredMotor2 = (ph_obv_tiredMotor2 + motorHz * 1.021 * invSR) % 1.0; // detune = "worn"
    }

    // ── Worn Whir — h40ep's only preset: a soft, uneven whirring drone
    //    from the parallel-hybrid motor/gearset, present continuously at
    //    low volume rather than load-gated. ──────────────────────────────
    if (oldBusVariant == 1 && isH40)
    {
        double w1 = Math.Sin(2.0 * Math.PI * ph_obv_wornWhir1);
        double w2 = Math.Sin(2.0 * Math.PI * ph_obv_wornWhir2);
        float whirVol = (0.03f + rn * 0.02f) * vol;
        ob += (w1 * 1.0 + w2 * 0.5) * whirVol;
        float whirHz = 85f + outRPM * 0.6f;
        ph_obv_wornWhir1 = (ph_obv_wornWhir1 + whirHz        * invSR) % 1.0;
        ph_obv_wornWhir2 = (ph_obv_wornWhir2 + whirHz * 1.03 * invSR) % 1.0;
    }
}
private bool   b400_retardZoneActive = false;
private float  b400_tickVol = 0f, b400_spitVol = 0f;
private double ph_b400_spitTick, ph_b400_spitHiss;

// ── Allison B500R — same treatment, heavier/deeper (bigger unit, artic duty) ─
private double ph_b5w_pump1, ph_b5w_pump2;
private float  b5w_pumpVolSmooth = 0f;
private float  b5w_runTimeSec    = 0f;
private double ph_b5w_accWh1, ph_b5w_accWhSub, ph_b5w_accWhThird;
private float  b5w_accHzSmooth    = 0f;
private float  b5w_accWhineSmooth = 0f;
private bool   b5w_retardZoneActive = false;
private float  b5w_tickVol = 0f, b5w_spitVol = 0f;
private double ph_b5w_spitTick, ph_b5w_spitHiss;
// ═════════════════════════════════════════════════════════════════════════
//  Allison retarder "spit" — real, documented behavior (CTA/Chicago Transit
//  Forum, 2009 — B-400R thread, confirmed present on B500R too):
//
//  While coasting or light-braking above ~10mph, the retarder is engaged
//  (you hear a quiet "tick" as it applies). It disengages — releasing a
//  distinct pneumatic "pssshht" spit — one of two ways:
//    (a) driver gets back on the throttle, or
//    (b) road speed drops under ~10mph while still coasting/braking,
//        heading toward a stop.
//  This is NOT the deep retarder growl elsewhere in the file (that's the
//  hydraulic braking effort itself, gated on bkPd/retAct) — this is the
//  separate valve-body air release, gated on the coast/retard *zone*.
// ═════════════════════════════════════════════════════════════════════════
private void DoAllisonRetarderSpit(ref double txSample, ref bool zoneWasActive,
                                    ref float tickVol, ref float spitVol,
                                    ref double phTick, ref double phSpit,
                                    float engMul, double invSR)
{
    bool zoneNow = running && gear > 0 && accel < 0.04f && spd > 10f;

    if (zoneNow && !zoneWasActive) { tickVol = 1f; phTick = 0.0; }   // retarder applies
    if (!zoneWasActive == false && zoneWasActive && !zoneNow) { spitVol = 1f; phSpit = 0.0; } // disengage
    zoneWasActive = zoneNow;

    if (tickVol > 0.001f)
    {
        // Dry, small, mechanical — a valve solenoid seating, not the spit itself.
        double tk = Math.Sin(2.0 * Math.PI * phTick);
        txSample += tk * tickVol * 0.045 * engMul + noise_hp_prev * tickVol * 0.02 * engMul;
        phTick = (phTick + 260.0 * invSR) % 1.0;
        tickVol *= 0.994f;
        if (tickVol < 0.004f) tickVol = 0f;
    }
    if (spitVol > 0.001f)
    {
        // The actual "pssshht" — shaped broadband air noise, brief hiss carrier
        // riding on top so it reads as pressurized air, not just white noise.
        double hissCarrier = Math.Sin(2.0 * Math.PI * phSpit);
        double shaped = noise_hi * 0.78 + noise_lp * 0.22;
        txSample += shaped * spitVol * 0.16 * engMul + hissCarrier * spitVol * 0.025 * engMul;
        phSpit = (phSpit + 1150.0 * invSR) % 1.0;
        spitVol *= 0.9988f;
        if (spitVol < 0.004f) spitVol = 0f;
    }
}

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP HELPER — Allison B3400xFE (shared by L9N, L9, ISL9, X10)
    //
    //  Fuel-economy character vs B400R/B500R:
    //   · Earlier lockup (G2+) → far less TC slip noise overall.
    //   · Quieter, faster-settling planetary mesh whine — smaller/lighter
    //     internals than the heavy-duty B400R/B500R castings.
    //   · A soft, continuous "xFE" electrified-accessory whir (oil pump /
    //     aux drive) layered under the mesh, distinct from any other TX.
    //   · Lighter shift thuds — efficiency-tuned shift firmness is softer.
    // ═════════════════════════════════════════════════════════════════════════
    private void DoB3400DSP(ref double txSample, float rn, float ld, float hz,
                             float outRPM, float engMul, double noiseHp, double noiseClt, double invSR)
    {
        const int   B34_TEETH = 44; // smaller/lighter internals than B500R's 48
        const float B34_DET_A = 1.0008f;
        const float B34_DET_B = 1.5f;

        float b34MeshTarget = (rpm / 60f) * B34_TEETH;
        float b34Tau = b34MeshTarget > b34_whineHzSmooth ? 0.0085f : 0.0045f; // settles faster — lighter gearset
        b34_whineHzSmooth += (b34MeshTarget - b34_whineHzSmooth) * b34Tau;
        if (b34_whineHzSmooth < 2f) b34_whineHzSmooth = 0f;

        float b34GearGain;
        if      (gear <= 1) b34GearGain = 1.35f + ld * 0.55f; // quieter overall than B400R/B500R
        else if (gear == 2) b34GearGain = 0.55f;              // already locked from G2 — quiet
        else if (gear == 3) b34GearGain = 0.40f;
        else if (gear == 4) b34GearGain = 0.30f;
        else                b34GearGain = 0.22f;

        // [BETTER BLENDED] b34GearGain used to apply the instant it changed
        // (no smoothing at all) -- now eased so each shift's gain change is
        // a glide rather than a step, same fix as B400R/B500R.
        b34_gearGainSmooth += (b34GearGain - b34_gearGainSmooth) * 0.006f;
        float b34WhineVol = (0.014f + rn * 0.020f + ld * 0.009f) * b34_gearGainSmooth * engMul;
        if (b34_whineHzSmooth > 5f)
        {
            double b34W1 =  Math.Sin(2.0 * Math.PI * ph_b34_wh1);
            double b34W2 = -Math.Sin(2.0 * Math.PI * ph_b34_wh2);
            double b34W3 =  Math.Sin(2.0 * Math.PI * ph_b34_wh3);
            txSample += b34W1 * b34WhineVol + b34W2 * (b34WhineVol * 0.55) + b34W3 * (b34WhineVol * 0.24)
                     + noise_lp * (b34WhineVol * 0.06);
        }

        // TC slip — only audible pre-lockup (gear 1), since G2+ already
        // locked. [SYNCED] Uses b4r_tccBlend now too, same as B500R above.
        float b34OpenFrac = gear == 1 ? (1f - b4r_tccBlend) : 0f;
        float b34SlipLoad = b34OpenFrac * ld * 0.70f;
        b34_tcNoiseSmooth += (b34SlipLoad - b34_tcNoiseSmooth) * 0.006f;
        if (b34_tcNoiseSmooth > 0.003f && spd > 0.5f)
        {
            float tcVol = (0.030f + b34_tcNoiseSmooth * 0.060f) * engMul;
            txSample += noiseClt * tcVol;
            double tcOutHz = outRPM * 0.065 + 10.0;
            txSample += Math.Sin(2.0 * Math.PI * ph_b34_tc1) * (tcVol * 0.35);
            txSample += Math.Sin(2.0 * Math.PI * ph_b34_tc2) * (tcVol * 0.16);
            ph_b34_tc1 = (ph_b34_tc1 + tcOutHz       * invSR) % 1.0;
            ph_b34_tc2 = (ph_b34_tc2 + tcOutHz * 2.0 * invSR) % 1.0;
        }
        else
        {
            b34_tcNoiseSmooth = Mathf.Max(0f, b34_tcNoiseSmooth - (float)invSR * 3.5f);
            double tcIdleHz = 10.0 + outRPM * 0.065;
            ph_b34_tc1 = (ph_b34_tc1 + tcIdleHz       * invSR) % 1.0;
            ph_b34_tc2 = (ph_b34_tc2 + tcIdleHz * 2.0 * invSR) % 1.0;
        }

        // xFE accessory whir — soft, continuous, present even off-throttle
        // (electrified oil pump keeps spinning independent of TC state)
        double xfeHz = 220.0 + (rpm * 0.04);
        float  xfeVol = (0.006f + rn * 0.006f) * engMul;
        txSample += Math.Sin(2.0 * Math.PI * ph_b34_xfe1) * xfeVol;
        txSample += Math.Sin(2.0 * Math.PI * ph_b34_xfe2) * xfeVol * 0.40;
        ph_b34_xfe1 = (ph_b34_xfe1 + xfeHz       * invSR) % 1.0;
        ph_b34_xfe2 = (ph_b34_xfe2 + xfeHz * 1.5 * invSR) % 1.0;

        // ── Howl — B3400xFE's own dedicated fields now (was sharing
        //    ph_b5_out with B400R/B500R). Real basis: FuelSense Max locks
        //    up earlier (from gear 2, per CPTDB/Allison xFE documentation)
        //    with optimized ratios specifically to minimize converter slip
        //    — so the converter works less hard overall, and this reads as
        //    a brief, thin whistle rather than either sibling's sustained
        //    howl: higher pitch, quieter, faster tremolo, settles out fast.
        if (gear >= 7 && spd > 99f)
        {
            double b34OutHz    = 240.0 + outRPM * 0.70;
            double b34HowlTrem = 0.80 + 0.20 * Math.Sin(2.0 * Math.PI * ph_b34_howlTrem);
            bool highAccel = ld > 0.8f;
            float b34HowlDecayTarget = highAccel ? 0.5f : 1.0f;
            b34HowlAccelDecay += (b34HowlDecayTarget - b34HowlAccelDecay) * (float)invSR * (highAccel ? 0.22f : 0.6f);
            float  b34OutVol   = (0.030f + (spd / MAX_SPD) * 0.032f + ld * 0.012f) * engMul * b34HowlAccelDecay;
            double b34HowlTone = Math.Sin(2.0 * Math.PI * ph_b34_out) * 1.0 + Math.Sin(2.0 * Math.PI * ph_b34_out * 0.5) * 0.28;
            txSample += b34HowlTone * b34OutVol * b34HowlTrem;
            ph_b34_out     = (ph_b34_out     + b34OutHz * invSR) % 1.0;
            ph_b34_howlTrem = (ph_b34_howlTrem + 3.6      * invSR) % 1.0;
        }
            else
        {
                ph_b34_out      = (ph_b34_out      + (240.0 + outRPM * 0.70) * invSR) % 1.0;
                ph_b34_howlTrem = (ph_b34_howlTrem + 3.6           * invSR) % 1.0;
        }

        // Shift thud — lighter/softer than B400R/B500R (efficiency-tuned firmness)
        if (b34_shiftThud > 0f)
        {
            txSample += (Math.Sin(2.0 * Math.PI * ph_b34_thud1) * 0.18 + Math.Sin(2.0 * Math.PI * ph_b34_thud2) * 0.09) * b34_shiftThud * engMul;
            // [FIX -- real timing-scale] Was 0.9975f -- the SAME slow decay
            // as B500R's (the biggest, slowest-settling unit of the three).
            // B3400xFE is the opposite: fuel-economy variant, lighter/
            // smaller internals, earlier lockup -- its bark should be the
            // SHORTEST of the three, not tied with the longest. B400R's
            // 0.9960f is the baseline; this now decays faster than that.
            b34_shiftThud *= 0.9925f;
            if (b34_shiftThud < 0.005f) b34_shiftThud = 0f;
        }
        ph_b34_thud1 = (ph_b34_thud1 + 42.0 * invSR) % 1.0;
        ph_b34_thud2 = (ph_b34_thud2 + 72.0 * invSR) % 1.0;

        // ── End-of-gear-1 lockup burst — B3400xFE's own dedicated field
        //    (b34_lockupThud, was sharing b500_shiftThud/b500_lockupThud
        //    with the other two variants). Fires once, at the actual
        //    gear1→2 lock transition (triggered in DoB3400Gear).
        if (b34_lockupThud > 0f)
        {
            txSample += noiseHp * b34_lockupThud * 0.14f * engMul;
            // [FIX -- click bug] ph_b34_wh1 is a shared whine phase, same
            // fix as B400R's b400_lockupThud -- fast attack instead of an
            // instant envelope step landing mid-waveform.
            b34_lockupThudSm += (b34_lockupThud - b34_lockupThudSm) * Mathf.Min(1f, (float)invSR / 0.004f);
            txSample += Math.Sin(2.0 * Math.PI * ph_b34_wh1) * b34_lockupThudSm * 0.11f * engMul;
            b34_lockupThud *= 0.9950f;
            if (b34_lockupThud < 0.004f) b34_lockupThud = 0f;
        }

        // Same reasoning as DoB400RWhineDSP -- called directly now that the
        // function which used to call it as a side effect is gone.
        DoAllisonDoubleTap(ref txSample, engMul, invSR);

        ph_b34_wh1 = (ph_b34_wh1 + b34_whineHzSmooth               * invSR) % 1.0;
        ph_b34_wh2 = (ph_b34_wh2 + b34_whineHzSmooth * B34_DET_A   * invSR) % 1.0;
        ph_b34_wh3 = (ph_b34_wh3 + b34_whineHzSmooth * B34_DET_B   * invSR) % 1.0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP HELPER — ZF EcoLife 6AP1200B (shared by L9N and L9)
    //  Very clean 4-osc planetary mesh, soft TC swoosh, barely-there thud.
    // ═════════════════════════════════════════════════════════════════════════

    private void DoZFDSP(ref double txSample, float rn, float ld,
                      float outRPM, float engMul, double noiseClt, bool retAct, double invSR)
{
    const int   ZF_TEETH  = 56;   // higher tooth count → higher-pitched, cleaner whine
    const float ZF_DET_A  = 1.0007f;
    const float ZF_DET_B  = 1.997f;   // near-2nd harmonic
    const float ZF_DET_C  = 2.997f;   // near-3rd harmonic
    const float ZF_TAU_UP = 0.007f;
    const float ZF_TAU_DN = 0.003f;
 
    float zfMesh = (rpm / 60f) * ZF_TEETH;
    float zfTau  = zfMesh > zf_whineHzSmooth ? ZF_TAU_UP : ZF_TAU_DN;
    zf_whineHzSmooth += (zfMesh - zf_whineHzSmooth) * zfTau;
    if (zf_whineHzSmooth < 2f) zf_whineHzSmooth = 0f;
 
    // Gear-dependent volume: very quiet in OD — clean European feel. OD noise
    // floor thinned further (gearGain trimmed on 4/5, whine's noise_lp term
    // cut in half below) so top gear reads as genuinely silent, not just quiet.
    float zfGearGain;
    if      (gear <= 1) zfGearGain = 1.55f + ld * 0.65f;
    else if (gear == 2) zfGearGain = 1.05f + ld * 0.20f;
    else if (gear == 3) zfGearGain = 0.68f;    // lockup — clean
    else if (gear == 4) zfGearGain = 0.36f;    // was 0.48 — thinner
    else                zfGearGain = 0.22f;    // was 0.35 — near silent OD
 
    float zfWhineVol = (0.015f + rn * 0.022f + ld * 0.011f) * zfGearGain * engMul;
 
    if (zf_whineHzSmooth > 5f)
    {
        // Pure sine bank — ZF sounds cleaner than Allison
        txSample += Math.Sin(2.0 * Math.PI * ph_zf_wh1) * zfWhineVol;
        txSample += Math.Sin(2.0 * Math.PI * ph_zf_wh2) * zfWhineVol * 0.60;
        txSample += Math.Sin(2.0 * Math.PI * ph_zf_wh3) * zfWhineVol * 0.22;
        txSample += Math.Sin(2.0 * Math.PI * ph_zf_wh4) * zfWhineVol * 0.08;
        // Noise floor halved in gears 4+ (OD) — was a flat 0.05 everywhere.
        float noiseFloorMul = gear >= 4 ? 0.02f : 0.05f;
        txSample += noise_lp * zfWhineVol * noiseFloorMul;
    }
 
    // TC swoosh — gears 1-2 only; ZF TC is smaller than Allison
    bool  zfSlipping = gear > 0 && !ZF_LOCK[Mathf.Min(gear, ZF_LOCK.Length - 1)];
    float zfSlipLoad = zfSlipping ? ld * (gear == 1 ? 0.85f : 0.38f) : 0f;
    zf_tcSmooth += (zfSlipLoad - zf_tcSmooth) * 0.004f;
    if (zf_tcSmooth > 0.002f && spd > 0.5f)
    {
        float tcVol  = (0.022f + zf_tcSmooth * 0.042f) * engMul;
        txSample += noiseClt * tcVol;
        double zfTcHz = outRPM * 0.058 + 9.0;
        txSample += Math.Sin(2.0 * Math.PI * ph_zf_tc1) * tcVol * 0.38;
        txSample += Math.Sin(2.0 * Math.PI * ph_zf_tc2) * tcVol * 0.18;
        ph_zf_tc1 = (ph_zf_tc1 + zfTcHz       * invSR) % 1.0;
        ph_zf_tc2 = (ph_zf_tc2 + zfTcHz * 2.0 * invSR) % 1.0;
    }
    else
    {
        zf_tcSmooth = Mathf.Max(0f, zf_tcSmooth - (float)invSR * 3f);
        double zfTcIdleHz = 9.0 + outRPM * 0.058;
        ph_zf_tc1 = (ph_zf_tc1 + zfTcIdleHz       * invSR) % 1.0;
        ph_zf_tc2 = (ph_zf_tc2 + zfTcIdleHz * 2.0 * invSR) % 1.0;
    }
 
    // Shift transient — light, soft, very European
    if (zf_shiftThud > 0f)
    {
        txSample += Math.Sin(2.0 * Math.PI * ph_zf_thud1) * zf_shiftThud * 0.11 * engMul;
        txSample += Math.Sin(2.0 * Math.PI * ph_zf_thud2) * zf_shiftThud * 0.05 * engMul;
        zf_shiftThud *= 0.9988f;
        if (zf_shiftThud < 0.004f) zf_shiftThud = 0f;
    }
 
    // ── NEW: lockup clunk on the G2→G3 engagement specifically. ZF_LOCK goes
    // false→true right at gear 3 (see ZF_LOCK array), so that's the one shift
    // where the converter clutch actually engages for the first time — a
    // distinct mechanical "clunk" as the lockup clutch bites, on top of the
    // soft shift-thud every gear change already gets. ───────────────────────
    if (gear != zf_lastGearForClunk)
    {
        if (gear == 3 && zf_lastGearForClunk == 2) zf_lockupClunkVol = 1f;
        zf_lastGearForClunk = gear;
    }
    if (zf_lockupClunkVol > 0.001f)
    {
        double clunkHz = 44.0;
        double c1 = Math.Sin(2.0 * Math.PI * ph_zf_clunk1);
        double c2 = Math.Sin(2.0 * Math.PI * ph_zf_clunk2);
        txSample += Math.Tanh((c1 * 0.8 + c2 * 0.4) * 2.0) * zf_lockupClunkVol * 0.09 * engMul;
        ph_zf_clunk1 = (ph_zf_clunk1 + clunkHz        * invSR) % 1.0;
        ph_zf_clunk2 = (ph_zf_clunk2 + clunkHz * 2.3   * invSR) % 1.0;
        zf_lockupClunkVol *= 0.965f;   // quick, tight, mechanical — decays fast
        if (zf_lockupClunkVol < 0.004f) zf_lockupClunkVol = 0f;
    }

    // ── ZF REMAKE: primary (input-side) retarder ────────────────────────────
    // EcoLife's retarder sits between the torque converter and the planetary
    // gearset -- on the transmission INPUT, not the output like Voith/
    // Allison. Real consequence: brake torque at the wheels gets multiplied
    // by whatever gear ratio is currently active, same as drive torque does.
    // Modeled directly: retarder effort scales with the current gear's own
    // ratio (ZF_R[gear]) rather than being flat/gear-independent, so braking
    // in gear 1 is genuinely stronger than the same retarder command in top
    // gear -- not just "on/off", the actual felt effort differs by gear.
    // Tone stays cleaner/more restrained than Voith's rougher retarder churn,
    // matching ZF's whole reputation as the quiet, refined option.
    {
        int gClamped = Mathf.Clamp(gear, 1, ZF_R.Length - 1);
        float gearRatioMul = ZF_R[gClamped] / ZF_R[1]; // normalized so gear 1 = 1.0x, higher gears < 1.0x

        float zfRetTarget = retAct ? Mathf.Clamp01(bkPd) * gearRatioMul : 0f;
        zf_retVolSmooth += (zfRetTarget - zf_retVolSmooth) * (retAct ? 0.045f : 0.09f);

        if (zf_retVolSmooth > 0.002f)
        {
            // Output-shaft-order tone (retarder is a hydrodynamic unit on the
            // input side, but what you HEAR outside tracks road/output speed,
            // same reasoning Voith's retarder whine already uses).
            double zfRetHz = 70.0 + outRPM * 0.60;
            double r1 = Math.Sin(2.0 * Math.PI * ph_zf_ret1);
            double r2 = Math.Sin(2.0 * Math.PI * ph_zf_ret2);
            double retTone = r1 * 1.0 + r2 * 0.35;
            // Clean, controlled -- noise content kept low relative to tone,
            // unlike Voith's rougher/hoarser retarder texture.
            txSample += (retTone * 0.75 + noiseClt * 0.25) * zf_retVolSmooth * 0.16 * engMul;
            ph_zf_ret1 = (ph_zf_ret1 + zfRetHz        * invSR) % 1.0;
            ph_zf_ret2 = (ph_zf_ret2 + zfRetHz * 1.98 * invSR) % 1.0;
        }
    }

    ph_zf_thud1 = (ph_zf_thud1 + 60.0 * invSR) % 1.0;
    ph_zf_thud2 = (ph_zf_thud2 + 110.0 * invSR) % 1.0;
 
    ph_zf_wh1 = (ph_zf_wh1 + zf_whineHzSmooth               * invSR) % 1.0;
    ph_zf_wh2 = (ph_zf_wh2 + zf_whineHzSmooth * ZF_DET_A    * invSR) % 1.0;
    ph_zf_wh3 = (ph_zf_wh3 + zf_whineHzSmooth * ZF_DET_B    * invSR) % 1.0;
    ph_zf_wh4 = (ph_zf_wh4 + zf_whineHzSmooth * ZF_DET_C    * invSR) % 1.0;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  ZF ECOLIFE 2 (tx=="zfel2" 6AP1420 std / "zfel2_hd" 6AP1620-1720 heavy-
//  duty-artic) — real, confirmed generation distinct from EcoLife gen 1
//  ("zf"/DoZFDSP above). Grounded directly in ZF's own EcoLife 2 launch
//  materials and product pages:
//
//    · "Numerous technical modifications optimize the new version of the
//      powershift transmission... A torque converter with a new torsional
//      damper enables fast and smooth shifting... the torque converter
//      also transmits FEWER ENGINE VIBRATIONS to both transmission and
//      complete drivetrain. This increases levels of comfort while
//      minimizing [noise]." → genuinely quieter/smoother TC swoosh and
//      whine noise floor than gen 1's, not just a retune for its own sake.
//    · "Stop-start capability for ALL model variants" → unconditional, not
//      an Eco-button option like Voith NXT's mild-hybrid stop-start. Real
//      spec: "Unlimited start/stop capability over the entire transmission
//      service life." Modeled the same safe way NXT's stop-start already
//      is in this file (silencing rpm/DSP output, never touching the
//      global engineState machine) — see CalcZFEL2RPM.
//    · "Provides shifting at lower engine speeds to improve fuel
//      efficiency" → same 6-speed planetary gearset/road-speed shift
//      points as gen 1 (DoZFGear reused unchanged), lower RPM ceiling.
//    · "Reduces weight up to 20 kg... new torsional damper with larger
//      torsion angle" → lighter mechanical presence overall, reflected
//      here as reduced gains across whine/TC/thud/retarder vs gen 1.
//    · Real torque-rated size split (6AP1420 vs 6AP1620/1720) — same shape
//      as this file's existing B400R/B500R split, via a heavyDuty bool
//      rather than full duplication since the real difference is torque
//      capacity/converter mass, not a different gear architecture.
// ═══════════════════════════════════════════════════════════════════════════════
private void DoZFEL2DSP(ref double txSample, float rn, float ld,
                         float outRPM, float engMul, double noiseClt, bool retAct, double invSR,
                         bool heavyDuty)
{
    const int   ZFEL2_TEETH  = 58;      // slightly higher than gen1's 56 -- newer casting tolerance
    const float ZFEL2_DET_A  = 1.0005f; // tighter detune than gen1's 1.0007 -- new torsional damper, cleaner mesh
    const float ZFEL2_DET_B  = 1.998f;
    const float ZFEL2_DET_C  = 2.998f;
    const float ZFEL2_TAU_UP = 0.006f;
    const float ZFEL2_TAU_DN = 0.0025f;

    float zfMesh = (rpm / 60f) * ZFEL2_TEETH;
    float zfTau  = zfMesh > zfel2_whineHzSmooth ? ZFEL2_TAU_UP : ZFEL2_TAU_DN;
    zfel2_whineHzSmooth += (zfMesh - zfel2_whineHzSmooth) * zfTau;
    if (zfel2_whineHzSmooth < 2f) zfel2_whineHzSmooth = 0f;

    // [QUIETER THROUGHOUT] Lower base gains than gen1 across every gear --
    // the new torsional damper transmitting fewer vibrations to the
    // driveline is a whole-transmission trait, not one specific layer.
    float zfGearGain;
    if      (gear <= 1) zfGearGain = 1.30f + ld * 0.55f;
    else if (gear == 2) zfGearGain = 0.88f + ld * 0.16f;
    else if (gear == 3) zfGearGain = 0.55f;
    else if (gear == 4) zfGearGain = 0.28f;
    else                zfGearGain = 0.16f;   // near-silent OD, even quieter than gen1's

    float sizeMul = heavyDuty ? 1.18f : 1.0f; // 6AP1620/1720 torque class reads a touch more present than 6AP1420
    float zfWhineVol = (0.012f + rn * 0.018f + ld * 0.009f) * zfGearGain * engMul * sizeMul;

    if (zfel2_whineHzSmooth > 5f)
    {
        txSample += Math.Sin(2.0 * Math.PI * ph_zfel2_wh1) * zfWhineVol;
        txSample += Math.Sin(2.0 * Math.PI * ph_zfel2_wh2) * zfWhineVol * 0.55;
        txSample += Math.Sin(2.0 * Math.PI * ph_zfel2_wh3) * zfWhineVol * 0.18;
        txSample += Math.Sin(2.0 * Math.PI * ph_zfel2_wh4) * zfWhineVol * 0.05;
        float noiseFloorMul = gear >= 4 ? 0.012f : 0.03f; // quieter noise floor than gen1's -- damper reduces transmitted vibration
        txSample += noise_lp * zfWhineVol * noiseFloorMul;
    }

    // TC swoosh — [QUIETER/SMOOTHER] real: new torsional damper transmits
    // fewer vibrations. Lower gain, faster settle than gen1's.
    bool  zfSlipping = gear > 0 && !ZF_LOCK[Mathf.Min(gear, ZF_LOCK.Length - 1)];
    float zfSlipLoad = zfSlipping ? ld * (gear == 1 ? 0.70f : 0.30f) : 0f; // was 0.85/0.38 on gen1
    zfel2_tcSmooth += (zfSlipLoad - zfel2_tcSmooth) * 0.005f;
    if (zfel2_tcSmooth > 0.002f && spd > 0.5f)
    {
        float tcVol  = (0.015f + zfel2_tcSmooth * 0.030f) * engMul * sizeMul; // was 0.022+0.042 on gen1
        txSample += noiseClt * tcVol;
        double zfTcHz = outRPM * 0.058 + 9.0;
        txSample += Math.Sin(2.0 * Math.PI * ph_zfel2_tc1) * tcVol * 0.35;
        txSample += Math.Sin(2.0 * Math.PI * ph_zfel2_tc2) * tcVol * 0.15;
        ph_zfel2_tc1 = (ph_zfel2_tc1 + zfTcHz       * invSR) % 1.0;
        ph_zfel2_tc2 = (ph_zfel2_tc2 + zfTcHz * 2.0 * invSR) % 1.0;
    }
    else
    {
        zfel2_tcSmooth = Mathf.Max(0f, zfel2_tcSmooth - (float)invSR * 3.5f);
        double zfTcIdleHz = 9.0 + outRPM * 0.058;
        ph_zfel2_tc1 = (ph_zfel2_tc1 + zfTcIdleHz       * invSR) % 1.0;
        ph_zfel2_tc2 = (ph_zfel2_tc2 + zfTcIdleHz * 2.0 * invSR) % 1.0;
    }

    // Shift thud + lockup clunk — self-contained gear-change detection (not
    // relying on DoZFGear()'s zf_shiftThud side effect, since that function
    // is reused unchanged from gen 1 and writes to the GEN 1 field). Same
    // real G2->G3 lockup-clutch event gen 1 has, quieter/tighter (new
    // damper) here.
    if (gear != zfel2_lastGearAudio)
    {
        if (gear == 3 && zfel2_lastGearAudio == 2) zfel2_lockupClunkVol = 1f;
        zfel2_shiftThud = (gear > zfel2_lastGearAudio) ? 1.0f : 0.55f;
        zfel2_lastGearAudio = gear;
    }
    if (zfel2_shiftThud > 0f)
    {
        txSample += Math.Sin(2.0 * Math.PI * ph_zfel2_thud1) * zfel2_shiftThud * 0.08 * engMul;
        txSample += Math.Sin(2.0 * Math.PI * ph_zfel2_thud2) * zfel2_shiftThud * 0.035 * engMul;
        zfel2_shiftThud *= 0.9990f;
        if (zfel2_shiftThud < 0.004f) zfel2_shiftThud = 0f;
    }
    if (zfel2_lockupClunkVol > 0.001f)
    {
        double clunkHz = 46.0;
        double c1 = Math.Sin(2.0 * Math.PI * ph_zfel2_clunk1);
        double c2 = Math.Sin(2.0 * Math.PI * ph_zfel2_clunk2);
        txSample += Math.Tanh((c1 * 0.7 + c2 * 0.3) * 1.7) * zfel2_lockupClunkVol * 0.06 * engMul;
        ph_zfel2_clunk1 = (ph_zfel2_clunk1 + clunkHz       * invSR) % 1.0;
        ph_zfel2_clunk2 = (ph_zfel2_clunk2 + clunkHz * 2.3 * invSR) % 1.0;
        zfel2_lockupClunkVol *= 0.97f;
        if (zfel2_lockupClunkVol < 0.004f) zfel2_lockupClunkVol = 0f;
    }

    // Retarder — same real input-side mechanism as gen 1 (effort scales
    // with the current gear's own ratio), cleaner/quieter drive.
    {
        int gClamped = Mathf.Clamp(gear, 1, ZF_R.Length - 1);
        float gearRatioMul = ZF_R[gClamped] / ZF_R[1];

        float zfRetTarget = retAct ? Mathf.Clamp01(bkPd) * gearRatioMul : 0f;
        zfel2_retVolSmooth += (zfRetTarget - zfel2_retVolSmooth) * (retAct ? 0.05f : 0.10f);

        if (zfel2_retVolSmooth > 0.002f)
        {
            double zfRetHz = 70.0 + outRPM * 0.60;
            double r1 = Math.Sin(2.0 * Math.PI * ph_zfel2_ret1);
            double r2 = Math.Sin(2.0 * Math.PI * ph_zfel2_ret2);
            double retTone = r1 * 1.0 + r2 * 0.30;
            txSample += (retTone * 0.80 + noiseClt * 0.20) * zfel2_retVolSmooth * 0.13 * engMul * sizeMul; // was 0.16 on gen1 -- cleaner
            ph_zfel2_ret1 = (ph_zfel2_ret1 + zfRetHz        * invSR) % 1.0;
            ph_zfel2_ret2 = (ph_zfel2_ret2 + zfRetHz * 1.98 * invSR) % 1.0;
        }
    }

    // ── [NEW] Universal stop-start restart — soft integrated-starter
    // re-crank, quick, since this is a modern factory stop-start system
    // rather than a traditional standalone starter-motor grind.
    if (zfel2_restartPulse > 0f)
    {
        double crankHz = 90.0 + (1.0 - zfel2_restartPulse) * 300.0;
        txSample += Math.Sin(2.0 * Math.PI * ph_zfel2_restart) * zfel2_restartPulse * 0.045 * engMul
                  + noise_hi * zfel2_restartPulse * 0.03 * engMul;
        ph_zfel2_restart = (ph_zfel2_restart + crankHz * invSR) % 1.0;
        zfel2_restartPulse -= (float)(invSR / 0.35);
        if (zfel2_restartPulse < 0f) zfel2_restartPulse = 0f;
    }

    ph_zfel2_thud1 = (ph_zfel2_thud1 + 60.0  * invSR) % 1.0;
    ph_zfel2_thud2 = (ph_zfel2_thud2 + 110.0 * invSR) % 1.0;

    ph_zfel2_wh1 = (ph_zfel2_wh1 + zfel2_whineHzSmooth               * invSR) % 1.0;
    ph_zfel2_wh2 = (ph_zfel2_wh2 + zfel2_whineHzSmooth * ZFEL2_DET_A * invSR) % 1.0;
    ph_zfel2_wh3 = (ph_zfel2_wh3 + zfel2_whineHzSmooth * ZFEL2_DET_B * invSR) % 1.0;
    ph_zfel2_wh4 = (ph_zfel2_wh4 + zfel2_whineHzSmooth * ZFEL2_DET_C * invSR) % 1.0;
}
    private float h50RevTime;
private bool h50RevActive;
private double ph_h50_res1;
private double ph_h50_res2;
private double ph_h50_mot1;
private double ph_h50_mot2;
private double ph_h50_mot3;
private double ph_h50_mot4;
private double ph_h50_howl;
private double ph_h50_mot5;
// Put these at the class level alongside your other smoothers
private float accelTimer = 0f;
private float h4x_revSmooth = 0f;
private bool  h4x_revWasLow = true;
private float h4x_revConsecutiveCount = 1.0f; // Starts at 1.0f to avoid division issues

// ─────────────────────────────────────────────────────────────────────────────
//  ZH50EP  —  the zombie 1001-1003 XDE60 (2008 New Flyer Xcelsior prototype).
//  Mechanically an ordinary H50EP (see DoH50EPGear/CalcH50EPRPM/DoH4xDSP —
//  all extended above to also match tx=="zh50ep"), but layered with a
//  permanent, always-on ghost character that a normal H50EP never has:
//
//    1. Phantom shift flare — H50EP is a CVT/EVT with no real gears, but
//       this one revs up and drops back down on a slow, steady cycle under
//       load anyway, like it's shifting through gears that don't exist.
//       B500R-style growl texture (tanh-driven close-detuned pair).
//
//    2. ~10 angelic harmonic layers that bloom in as road speed nears
//       40kph cruise and fade back out either side of it — inharmonic
//       spacing (not clean overtones) so it reads as unsettling/"outside
//       the bus" rather than musical.
//
//    3. The bridge-joint "dum dum . dum dum -- dum dum . dum dum" thump
//       pattern — plays whenever road speed is high, REGARDLESS of whether
//       the bus is anywhere near an actual bridge. The whole point is that
//       it shouldn't be happening.
//
//  This is called ADDITIVELY alongside the normal DoH4xDSP call for this
//  bus — it only adds texture, never replaces the base H50EP drivetrain.
// ─────────────────────────────────────────────────────────────────────────────
private double ph_zh_shiftRev1, ph_zh_shiftRev2;
private float  zh_shiftCyclePhase = 0f;
private float  zh_shiftFlareVol   = 0f;

private double[] ph_zh_angel          = new double[18];
private float[]  zh_angelRateScale    = new float[18];
private float[]  zh_angelVolSmooth    = new float[18];
private float[]  zh_angelStagger      = new float[18]; // per-layer bloom-in threshold offset
private bool     zh_angelInit         = false;

private double ph_zh_bridgeThump;
private float  zh_bridgeBeatTimer  = 0f;
private int    zh_bridgeBeatIndex  = 0;
private float  zh_bridgeThumpVol   = 0f;

/// <summary>Pattern durations in seconds, alternating thump/gap starting
/// with a thump: dum(0.16) . dum(0.16) -- dum(0.16) . dum(0.16) --repeat--.
/// Tune these directly if the exact rhythm doesn't quite match what you're
/// picturing — the shape (short gap after 1st pair, long gap after 2nd) is
/// the important part, exact seconds are easy to retune by ear.</summary>
private static readonly float[] ZH_BRIDGE_PATTERN =
{
    0.16f, 0.20f,   // dum, short gap
    0.16f, 0.55f,   // dum, long gap
    0.16f, 0.20f,   // dum, short gap
    0.16f, 0.90f,   // dum, long gap (end of full phrase — loops)
};

private void DoZombieGhostOverlay(ref double txSample, ref double engineSample, float engMul, double invSR)
{
    // ── 1. Phantom shift flare — cycles roughly every 3-4.5s under any real
    //    load. Rises, holds briefly, drops — same shape as a real torque-
    //    converter shift bump, just with nothing actually shifting. ───────
    float cycleSpeed = 0.22f + accel * 0.35f;
    zh_shiftCyclePhase += (float)invSR * cycleSpeed;
    if (zh_shiftCyclePhase > 1f) zh_shiftCyclePhase -= 1f;

    float flareEnv;
    if (zh_shiftCyclePhase < 0.35f)      flareEnv = Mathf.SmoothStep(0f, 1f, zh_shiftCyclePhase / 0.35f);
    else if (zh_shiftCyclePhase < 0.55f) flareEnv = 1f;
    else                                 flareEnv = Mathf.SmoothStep(1f, 0f, (zh_shiftCyclePhase - 0.55f) / 0.45f);

    float flareVolTarget = flareEnv * Mathf.Clamp01(0.25f + accel * 0.9f) * (running ? 1f : 0f);
    zh_shiftFlareVol += (flareVolTarget - zh_shiftFlareVol) * 0.02f;

    if (zh_shiftFlareVol > 0.003f)
    {
        double s1 = Math.Sin(2.0 * Math.PI * ph_zh_shiftRev1);
        double s2 = Math.Sin(2.0 * Math.PI * ph_zh_shiftRev2);
        double growl = Math.Tanh((s1 * 1.0 + s2 * 0.6) * 2.2);
        float growlHz = 34f + rpm * 0.05f + flareEnv * 40f; // pitch climbs into the "shift"
        engineSample += growl * zh_shiftFlareVol * 0.10 * engMul;
        ph_zh_shiftRev1 = (ph_zh_shiftRev1 + growlHz         * invSR) % 1.0;
        ph_zh_shiftRev2 = (ph_zh_shiftRev2 + growlHz * 1.503 * invSR) % 1.0;
    }

    // ── 2. ~18 angelic harmonics — bloom in near 40kph cruise, inharmonic
    //    spacing so they read as ghostly rather than musical. Each layer now
    //    has its own stagger threshold and drift rate, so instead of all ten
    //    (now eighteen) fading in together as one swelling chord, they creep
    //    in one at a time, offset from each other — that asynchrony is what
    //    reads as unsettling instead of just "a pad sound got louder." ────
    if (!zh_angelInit)
    {
        zh_angelInit = true;
        for (int a = 0; a < 18; a++)
        {
            ph_zh_angel[a]       = (a * 0.137) % 1.0;
            zh_angelRateScale[a] = 0.6f + a * 0.09f;               // each layer drifts at a slightly different rate
            zh_angelStagger[a]   = (a * 0.6180339887f) % 1f * 0.7f; // golden-ratio spread, 0-0.7 — no two layers bloom in sync
            zh_angelVolSmooth[a] = 0f;
        }
    }

    float cruiseProximity = Mathf.Clamp01(1f - Mathf.Abs(spd - 40f) / 16f); // peaks at 40kph, gone by ~24/56
    if (cruiseProximity > 0.001f || true) // always ticks so smoothed layers can fade back to 0 cleanly
    {
        double angelSum = 0.0;
        bool anyAudible = false;
        for (int a = 0; a < 18; a++)
        {
            // This layer only starts responding once cruiseProximity clears
            // its own staggered threshold — low-index layers bloom in first,
            // higher layers trail in behind them, each at a different rate.
            float layerTarget = Mathf.Clamp01((cruiseProximity - zh_angelStagger[a]) / (1f - zh_angelStagger[a])) ;
            zh_angelVolSmooth[a] += (layerTarget - zh_angelVolSmooth[a]) * (layerTarget > zh_angelVolSmooth[a] ? 0.006f : 0.010f);

            if (zh_angelVolSmooth[a] > 0.0005f)
            {
                anyAudible = true;
                double lfo     = 0.5 + 0.5 * Math.Sin(audioClock * zh_angelRateScale[a] * Math.PI * 2.0 * 0.15);
                double partial = Math.Sin(2.0 * Math.PI * ph_zh_angel[a]);
                angelSum += partial * lfo * zh_angelVolSmooth[a] * (0.5 + 0.5 / (a + 1)); // higher layers a bit quieter
            }
            double hz = 610.0 + a * 83.7; // deliberately NOT clean harmonic ratios, wider spread across 18 layers
            ph_zh_angel[a] = (ph_zh_angel[a] + hz * invSR) % 1.0;
        }
        if (anyAudible)
        {
            float angelVol = 0.052f * engMul;
            txSample += angelSum * angelVol;
        }
    }

    // ── 3. Bridge-joint phantom thump — fires at high speed no matter
    //    where the bus actually is. ───────────────────────────────────────
    bool bridgePhantomActive = spd > 70f;
    if (bridgePhantomActive)
    {
        zh_bridgeBeatTimer += (float)invSR;
        int idx = zh_bridgeBeatIndex % ZH_BRIDGE_PATTERN.Length;
        if (zh_bridgeBeatTimer >= ZH_BRIDGE_PATTERN[idx])
        {
            zh_bridgeBeatTimer -= ZH_BRIDGE_PATTERN[idx];
            zh_bridgeBeatIndex++;
            bool isThumpSlot = (idx % 2 == 0); // even slots are the "dum", odd are gaps
            if (isThumpSlot) { zh_bridgeThumpVol = 1f; ph_zh_bridgeThump = 0.0; }
        }
    }
    else
    {
        zh_bridgeBeatTimer = 0f;
        zh_bridgeBeatIndex = 0;
    }

    if (zh_bridgeThumpVol > 0.002f)
    {
        double thumpHz   = 46.0;
        double thump     = Math.Sin(2.0 * Math.PI * ph_zh_bridgeThump);
        double thumpBody = Math.Tanh(thump * 2.4);
        txSample += thumpBody * zh_bridgeThumpVol * 0.16 * engMul + noise_lp * zh_bridgeThumpVol * 0.05 * engMul;
        ph_zh_bridgeThump = (ph_zh_bridgeThump + thumpHz * invSR) % 1.0;
        zh_bridgeThumpVol *= 0.992f;
        if (zh_bridgeThumpVol < 0.004f) zh_bridgeThumpVol = 0f;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  ALLISON H 40/50 EP  —  v2 FULL COMPONENT REBUILD (Voith-rework treatment)
//
//  Real hardware modeled (Allison spec sheet + operator's manual + two-mode
//  EVT patent):
//   · Drive Unit = compound-split EVT: 2 motor-generators (A + B) acting as
//     variable clutches on the planetary sets. ONE physical range — no gear
//     steps — engine RPM fully decoupled from road speed. That decoupling is
//     the "mountainous revs": engine swells up/crests/falls/climbs again
//     while road speed rises smoothly (see CalcH40EPRPM mountain machine).
//   · MG-A ("sweeper"): its shaft speed is a LINEAR COMBINATION of engine
//     and output speed, so as ratio changes it physically sweeps DOWN
//     THROUGH ZERO and back up — the iconic saucer sweep, with a real
//     silence notch at the zero crossing. Modeled exactly that way.
//   · MG-B (traction): dominant "vacuum cleaner" whine, tracks output speed.
//   · Mode 1 = under-drive launch range / Mode 2 = direct-drive cruise range,
//     swapped by clutches → audible transient + mesh ratio change.
//   · DPIM2 inverter: 430–900VDC 160kW 3-phase — PWM carrier + sidebands.
//   · Regen: per the manual, active whenever throttle is released (not just
//     on brake), increasing with brake pressure.
//   · H40EP ≈ 350hp (ISB/ISL 280) / H50EP ≈ 400hp (ISL 330) — H50 gets the
//     deeper, louder motor + slightly lower PWM carrier.
// ─────────────────────────────────────────────────────────────────────────────
    // ── Always-on-while-moving state (replaces bae_idleStopTimer/Threshold) ─
    private bool   bae_spoolActive   = false;
    private float  bae_spoolTimer    = 0f;
    private double ph_bae_spool1, ph_bae_spool2;

    // ── Choppy / motorcycle-style rev character ─────────────────────────────
    // AM "chop" on the combustion core. Rate tied to genset load (bae_gensetLd,
    // already computed each frame for the core call), not raw rpm, so it reads
    // as revving-in-bursts rather than a smooth drone, and settles once the
    // tier plateau is reached.
    public float BAE_CHOP_RATE_BASE = 6.5f;   // Hz at idle-charge plateau
    public float BAE_CHOP_RATE_MAX  = 14.0f;  // Hz at max-charge plateau
    public float BAE_CHOP_DEPTH     = 0.35f;  // AM depth, 0-1
    private double ph_bae_chop;
    private float  bae_chopDepthSmooth = 0f;
    // v2 state — epv2_ prefix so nothing collides with the ep_* fields
    // declared in the other partial-class file.
    private bool   epv2_charInit      = false;
    private bool   epv2_isWhistler    = false;  // ~20% of units: loud DPIM carrier
    private float  epv2_whineTint     = 1f;     // per-instance mesh detune character
    private float  epv2_whHoldTimer   = 0f, epv2_whHoldHz  = 0f;
    private float  epv2_meshHoldTimer = 0f, epv2_meshHoldHz = 0f;
    private int    epv2_lastMode      = -1;
    private float  epv2_mg1SignedSm   = 0f;     // SIGNED sweeper speed (Hz, can cross 0)
    private float  epv2_mg2VolSm      = 0f;
    private float  epv2_meshVolSm     = 0f;
    private float  epv2_regenVolSm    = 0f;
    private float  epv2_bloomSm       = 0f;     // rev-surge whine bloom envelope
    private float  epv2_thudVol       = 0f;
    private double ph_epv2_mg2c, ph_epv2_invSB;
    private double ph_ep_gen5Blend; // [ADD] h50ep_gen5's smoother 3rd inverter partial
    // [ADD] Gen5 HGM self-check startup chime — one-time, on power-up only.
    private bool   epGen5_chimeActive = false;
    private float  epGen5_chimeTimer  = 0f;
    private double ph_gen5_chime1, ph_gen5_chime2;
    // [ADD] Gen5/L9 DEF dosing tick — the more durable DEF metering unit on
    // L9's compact combined DPF+SCR aftertreatment, scales with load/NOx
    // output. Genuinely new sound source, not present on the 1400s' ISL9.
    private float  epGen5_defTimer  = 0f;
    private float  epGen5_defVol    = 0f;
    // [ADD] L9's single combined flow-through DPF+SCR unit resonates at a
    // different chamber size/pitch than ISL9's older separate two-box
    // aftertreatment — a steady low resonance tied to load, distinct from
    // anything the 1400 Series' ISL9 produces.
    private double ph_gen5_aftrRes;
    private float  epGen5_iap2VolSm = 0f;
    private double ph_gen5_iap2;
    private double ph_epv2_bloom1, ph_epv2_bloom2;
    private double ph_epv2_thud1, ph_epv2_thud2;
    private double ph_epv2_over;
    // Launch voice ("oohwwwaaa" + "ouh ouh" syllable pulses)
    private float  epv2_lvVolSm = 0f, epv2_lvHzSm = 90f;
    private double epv2_lvPulsePhase = 0.0;
    private double ph_epv2_lv1, ph_epv2_lv2, ph_epv2_lv3;

    // ═══════════════════════════════════════════════════════════════════════
    // [ADD] eGen Flex state — egf_ prefix, own fields entirely separate from
    // epv2_/H4x above. This is a genuinely different Allison architecture
    // (integrated motor-in-gearbox drive unit, disconnect clutch for true
    // engine-off operation, lithium-titanate ESS, WEG-cooled inverter instead
    // of oil-cooled) -- NOT a reskin of H40EP/H50EP, so it gets its own
    // synthesis path rather than extra branches inside DoH4xDSP.
    // ═══════════════════════════════════════════════════════════════════════
    private bool   egf_charInit     = false;
    private float  egf_whineTint    = 1f;
    private float  egf_engineOffSm = 1f;   // 1 = engine running, 0 = fully engine-off (EV-only)
    private float  egf_gearVolSm    = 0f;
    private float  egf_invVolSm     = 0f;
    private float  egf_clutchThumpVol = 0f;
    private int    egf_lastGearStep  = -1;
    private double ph_egf_inv1, ph_egf_inv2;      // WEG-cooled inverter carrier -- cleaner, fewer harmonics than DPIM2
    private double ph_egf_gearWhine1, ph_egf_gearWhine2; // integrated multispeed gearbox mesh
    private double ph_egf_clutchThump;
    // [ADD] eGen Flex's own RPM state -- see CalcEGenFlexRPM. Punchier/
    // faster-slewing than H40EP/H50EP's restGate+cruise curve, per
    // instruction to keep the aggressive "badass" rev-gain feel it used
    // to get (by accident) from the old CalcVoithRPM fallback, now built
    // as a real curve of its own instead of borrowed math.
    private float  egf_rpmSmooth = 0f;
    // [ADD] eGen Flex's own load-stress state, separate from H4x's --
    // drives the windup/gearWhine intensity and the harder torque pull.
    private float  egf_stress = 0f;
    private float  egf_regenSmooth = 0f;
    private double ph_egf_regen;
    private double egf_gearWhineLP = 0.0;

private void DoH4xDSP(ref double txSample, ref double engineSample,
                       float rn, float ld, float engMul, float engVolPersonality,
                       double noiseHp, double noiseHi, double invSR)
{
    // [ADD] Startup windup -- see DoHybridStartupWindup in
    // BusAudioEngine.StartupSequence.cs (generic version of Voith's
    // DoVoithStartupWindup, reused here per that file's own plan). Ahead of
    // everything else so it's never gated behind gear/mode state.
    DoHybridStartupWindup(ref txSample, engineState == EngineRunState.Running, 250f, 1420f, engMul, invSR);

    // ═════════════════════════════════════════════════════════════════════════

    //  1. SETUP + PER-INSTANCE CHARACTER
    // ═════════════════════════════════════════════════════════════════════════
    bool  isH50    = tx == "h50ep" || tx == "zh50ep" || tx == "h50ep_gen5" || tx == "egenflex50";
    bool  isZombie = tx == "zh50ep";
    // [ADD] h50ep_gen5 — separate tx string, own real generation of the same
    // physical H50 EP drive unit (dual 100kW motor/generators unchanged per
    // Allison's own spec sheet). What's actually different is the control
    // stack (5th Gen TCM/VCM + dedicated HGM vs 4th Gen's 2-module setup),
    // the DPIM2 inverter's revised IGBTs/firmware, and the ESS chemistry
    // (Next-Gen Li-ion vs NiMH/early Li-ion) — all producing genuinely
    // different sonic behavior on the SAME mechanical hardware as the 1400
    // Series' plain "h50ep", not a relabel of it.
    bool  isGen5   = tx == "h50ep_gen5";
    // [ADD] eGen Flex -- Allison's real, CARB-certified (2022) successor to
    // the H 40/50 EP system, confirmed paired with Cummins B6.7 and L9 (same
    // engines this fleet already pairs h40ep/h50ep with). Same base parallel-
    // hybrid drive-unit voice as H4x below -- treated as another generation
    // of the SAME family, not a separate function/architecture, since it
    // genuinely is one. What's real and different: a disconnect clutch
    // enabling true engine-off EV propulsion (H4x never fully silences the
    // diesel), a WEG-cooled inverter (cleaner/lower-noise-floor carrier vs
    // DPIM2's oil-cooled unit), and LTO battery chemistry (faster-responding
    // energy delivery vs NiMH/Li-ion).
    // [NOTE -- now effectively dead here, harmless] eGen Flex has been
    // moved OFF this shared function entirely -- see DoEGenFlexDSP. It
    // used to reuse this whole voice with a few isEGenFlex-conditional
    // bonus layers bolted on (the true-engine-off/clutch-thump block right
    // below, the WEG-inverter noiseHp scale-down, the mg2Max/invBase
    // ternary arms), per direct instruction that reusing H40/H50EP's voice
    // wholesale for a real, distinct Allison architecture isn't
    // convincing on its own. tx is never "egenflex40"/"egenflex50" by the
    // time this function runs anymore, so isEGenFlex is always false here
    // now -- left in place rather than ripped out wholesale to avoid
    // destabilizing the surrounding H50/Gen5 logic under time pressure.
    bool  isEGenFlex = tx == "egenflex40" || tx == "egenflex50";
    // [FIX] Previously invBase/mg2Max were flat isH50-only constants —
    // meaning h50ep_gen5 inherited the exact same base motor/inverter pitch
    // as plain h50ep, and every Gen5 difference had to be an ADDED layer on
    // top of an identical foundation. That's backwards. Per Allison's own
    // 2012 DPIM2 spec sheet, the inverter's real operating window is
    // 430-900VDC — a wide range. The 1400 Series' NiMH pack ran ~588V
    // (per Allison's original EP40/50 spec); a modern Next-Gen Li-ion pack
    // is realistically configured meaningfully higher within that same
    // 430-900V DPIM2 window (Li-ion packs commonly run higher nominal
    // voltage than NiMH for a given capacity). Higher DC bus voltage
    // through the same inverter architecture means a higher achievable
    // switching/carrier frequency and a higher motor speed ceiling for the
    // same torque — so Gen5 gets a genuinely different BASE pitch, not an
    // identical one with extra decoration. eGen Flex's LTO pack runs
    // meaningfully higher again than even Gen5's Li-ion (LTO's flatter
    // discharge curve supports a higher sustained bus voltage), so it gets
    // the highest base pitch of the whole family.
    float mg2Max  = isH50 ? (isEGenFlex ? 600f : isGen5 ? 545f : 480f) : (isEGenFlex ? 615f : 560f);
    float invBase = isH50 ? (isEGenFlex ? 4650f : isGen5 ? 4150f : 3600f) : (isEGenFlex ? 4700f : 4200f);

    // Ghostly quieter — the zombie 1001-1003 doesn't run at normal H50EP
    // presence, it's a half-heard thing under everything else. engMul feeds
    // every gain stage below (motor whine, DPIM, launch chime, growl...),
    // so scaling it once here quiets the whole base engine uniformly rather
    // than hunting down each individual volume line.
    if (isZombie) engMul *= 0.52f;

    // [ADD] WEG-cooled inverter -- real spec, confirmed via Allison's own
    // product literature (water-ethylene-glycol cooling vs DPIM2's oil-
    // cooled unit). Liquid cooling holds a steadier switching frequency
    // instead of thermal-throttling under sustained load, which reads as a
    // cleaner, lower-noise-floor tone -- less noiseHp bleed than H4x/Gen5.
    if (isEGenFlex) noiseHp *= 0.62;

    if (!epv2_charInit)
    {
        epv2_charInit   = true;
        epv2_isWhistler = (NextNoiseSample() * 0.5 + 0.5) < 0.20;          // loud-DPIM unit
        epv2_whineTint  = 0.994f + (float)(NextNoiseSample() * 0.5 + 0.5) * 0.012f;
        // [ADD] Gen5's dedicated HGM (Hybrid Gateway Module) — real hardware
        // that doesn't exist on the 2-module Gen4 stack — runs a diagnostic
        // self-check on power-up. Fire the chime state machine once here.
        if (isGen5) { epGen5_chimeActive = true; epGen5_chimeTimer = 0f; }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  1B2. eGEN FLEX -- TRUE ENGINE-OFF via the disconnect clutch. This is
    //  the one genuinely new hardware capability H4x/Gen5 don't have at all
    //  (both keep the diesel idling continuously) -- below a light-load/
    //  low-speed threshold, the diesel core fades toward silent (motor-only
    //  EV cruise) instead. engineSample gets scaled directly.
    // ═════════════════════════════════════════════════════════════════════
    if (isEGenFlex)
    {
        // [FIX] Previous threshold (ld<0.14 && rn<0.30) almost never actually
        // triggered during normal driving -- steady light cruise rarely dips
        // both conditions at once, so the diesel core was audible nearly all
        // the time instead of reflecting eGen Flex's real "up to 50% of route
        // in engine-off mode" capability. Broadened to trigger on genuinely
        // light, steady-state driving (not just near-idle) -- any light-
        // throttle cruise or light-load rolling, not only near-stationary.
        float engineOffTarget = (ld < 0.28f && rn < 0.55f) ? 0.06f : 1f;
        float offRate = engineOffTarget < egf_engineOffSm ? 0.997f : 0.985f;
        egf_engineOffSm = Mathf.Lerp(engineOffTarget, egf_engineOffSm, offRate);
        engineSample *= egf_engineOffSm;

        // Disconnect-clutch engagement thump -- a real, single audible event
        // not present on H4x/Gen5 at all -- exactly at the moment the clutch
        // grabs to bring the diesel back online under sudden load.
        bool clutchJustEngaging = engineOffTarget > 0.5f && egf_engineOffSm < 0.35f;
        if (clutchJustEngaging) egf_clutchThumpVol = 0.09f;
        egf_clutchThumpVol *= 0.90f;
        if (egf_clutchThumpVol > 0.001)
        {
            ph_egf_clutchThump += 55.0 * invSR;
            txSample += Math.Sin(2.0 * Math.PI * ph_egf_clutchThump) * egf_clutchThumpVol * npcVolumeScale;
        }
    }

    // [REMOVED] stop-start engine fade — engine never auto-shuts-off, so the
    // engine bus always plays at full level here.

    float engHz    = Mathf.Max(rpm, IDLE) / 60f;         // engine rev/s
    float wheelRev = (spd / 3.6f) / TCIRC;               // output rev/s

    // [FIXED] H4xUpdateSlipModel (H4xLaunchChime.cs) was fully written but
    // never called from anywhere, and nothing ever turned its modulators
    // into sound -- DoH50EPSlipVoice below is the missing carrier/wire-up.
    DoH50EPSlipVoice(ref txSample, wheelRev, engMul, invSR, 0.5f); // [TUNED] native H40/H50EP voice, ~half previous volume

    // ═════════════════════════════════════════════════════════════════════════
    //  1B. GEN5-ONLY: HGM self-check chime, DEF dosing tick, L9 aftertreatment
    //      resonance. None of this exists on plain h50ep/zh50ep at all —
    //      these are new sound sources tied specifically to the 5th Gen
    //      control stack and its L9 pairing, not volume/timing tweaks on
    //      existing layers.
    // ═════════════════════════════════════════════════════════════════════════
    if (isGen5)
    {
        // Self-check chime — two short ascending digital tones, once, on
        // power-up, representing the dedicated HGM's diagnostic self-test
        // that simply doesn't exist as hardware on the Gen4 stack.
        if (epGen5_chimeActive)
        {
            epGen5_chimeTimer += (float)invSR;
            float t1 = 0.00f, t1Len = 0.14f;
            float t2 = 0.22f, t2Len = 0.14f;
            double chimeAmp = 0.0;
            if (epGen5_chimeTimer >= t1 && epGen5_chimeTimer < t1 + t1Len)
                chimeAmp = Math.Sin(Math.PI * (epGen5_chimeTimer - t1) / t1Len) * 0.055;
            else if (epGen5_chimeTimer >= t2 && epGen5_chimeTimer < t2 + t2Len)
                chimeAmp = Math.Sin(Math.PI * (epGen5_chimeTimer - t2) / t2Len) * 0.065;
            if (chimeAmp > 0.0001)
            {
                double chimeHz1 = epGen5_chimeTimer < t2 ? 880.0 : 1175.0; // ascending: A5 -> D6
                txSample += Math.Sin(2.0 * Math.PI * ph_gen5_chime1) * chimeAmp * npcVolumeScale
                          + Math.Sin(2.0 * Math.PI * ph_gen5_chime2) * chimeAmp * 0.4 * npcVolumeScale;
                ph_gen5_chime1 = (ph_gen5_chime1 + chimeHz1       * invSR) % 1.0;
                ph_gen5_chime2 = (ph_gen5_chime2 + chimeHz1 * 2.0 * invSR) % 1.0;
            }
            if (epGen5_chimeTimer > t2 + t2Len + 0.05f) epGen5_chimeActive = false;
        }

        // DEF dosing tick — L9's more durable metering unit injects DEF into
        // the exhaust stream on a duty cycle that tightens under load (more
        // NOx to treat = more frequent dosing events). Quiet, fast, and
        // rhythmically distinct from the anode-purge/injector-style ticks
        // used elsewhere in the file — this one's a short double-tap.
        float defInterval = Mathf.Lerp(2.4f, 0.6f, Mathf.Clamp01(ld));
        epGen5_defTimer += (float)invSR;
        if (epGen5_defTimer >= defInterval) { epGen5_defTimer = 0f; epGen5_defVol = 1f; }
        epGen5_defVol = epGen5_defVol > 0.001f ? epGen5_defVol * 0.72f : 0f;
        if (epGen5_defVol > 0.002f)
            engineSample += noise_hi * epGen5_defVol * 0.020 * npcVolumeScale * engVolPersonality;

        // L9 compact combined DPF+SCR resonance — single flow-through unit
        // vs ISL9's older separate two-box setup, so it rings at its own
        // chamber-size-determined pitch, steady and load-tracked rather
        // than tied to engine RPM the way the core exhaust note is.
        float aftrTgt = (0.010f + ld * 0.014f) * npcVolumeScale * engVolPersonality;
        engineSample += Math.Sin(2.0 * Math.PI * ph_gen5_aftrRes) * aftrTgt
                      + Math.Sin(2.0 * Math.PI * ph_gen5_aftrRes * 1.5) * aftrTgt * 0.3;
        ph_gen5_aftrRes = (ph_gen5_aftrRes + 71.0 * invSR) % 1.0; // fixed chamber-resonance pitch, not RPM-tracked

        // IAP2 (Increased Accessory Power 2) — real Allison hardware per
        // their own service bulletin (SIL 4-EP-13): introduced Q4 2014,
        // adds a Vanner Exportable Power Inverter (VEPI) on top of the main
        // DPIM2 traction inverter, up to 30kW/300A for accessory loads
        // (HVAC, lighting) drawn off the same high-voltage bus. The 1400
        // Series (2013) predates this by over a year — it would only have
        // the older IAP1 setup, no VEPI at all. Modeled as a steady,
        // separate low-level inverter hum, distinct pitch from the main
        // DPIM2 carrier since it's genuinely different hardware, present
        // continuously rather than load-tied since accessory draw runs
        // independent of traction demand.
        float iap2Tgt = (0.006f + 0.004f * (float)(Math.Sin(audioClock * Math.PI * 2.0 * 0.15) * 0.5 + 0.5))
                       * npcVolumeScale * engVolPersonality;
        epGen5_iap2VolSm += (iap2Tgt - epGen5_iap2VolSm) * 0.02f;
        txSample += Math.Sin(2.0 * Math.PI * ph_gen5_iap2)       * epGen5_iap2VolSm
                  + Math.Sin(2.0 * Math.PI * ph_gen5_iap2 * 1.5) * epGen5_iap2VolSm * 0.3;
        ph_gen5_iap2 = (ph_gen5_iap2 + 4400.0 * invSR) % 1.0; // VEPI's own carrier, unrelated to DPIM2's
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  2. LAUNCH CHIME STATE MACHINE (kept — user-configurable epRevStages)
    // ═════════════════════════════════════════════════════════════════════════
    bool isStoppedH4x = spd < 0.5f;
    bool launching    = !isStoppedH4x && accel > 0.05f;
    if (isStoppedH4x) { ep_launchStageIdx = -1; ep_launchPhase = 0; ep_stageTimer = 0f; }
    else if (ep_launchStageIdx == -1 && launching && epRevStages != null && epRevStages.Count > 0)
    { ep_launchStageIdx = 0; ep_launchPhase = 0; ep_stageTimer = 0f; }

    if (epRevStages != null && ep_launchStageIdx >= 0 && ep_launchStageIdx < epRevStages.Count)
    {
        ep_stageTimer += (float)invSR;
        var stage = epRevStages[ep_launchStageIdx];
        if (ep_launchPhase == 0)
        {
            float tau = Mathf.Max(0.05f, stage.duration * 0.30f);
            ep_surgeSmooth = stage.peak * Mathf.Exp(-ep_stageTimer / tau);
            if (ep_stageTimer >= stage.duration)
            {
                ep_stageTimer = 0f;
                if (stage.gapAfter > 0.0001f) ep_launchPhase = 1;
                else { ep_launchStageIdx++; ep_surgeSmooth = 0f; }
            }
        }
        else
        {
            ep_surgeSmooth = 0f;
            if (ep_stageTimer >= stage.gapAfter) { ep_launchStageIdx++; ep_launchPhase = 0; ep_stageTimer = 0f; }
        }
    }
    else ep_surgeSmooth = 0f;

    if (ep_surgeSmooth > 0.002f)
    {
        float chimeHz = 46f + ep_surgeSmooth * 70f;
        double c1 = Math.Sin(2.0 * Math.PI * ph_ep_howl1);
        double c2 = Math.Sin(2.0 * Math.PI * ph_ep_howl2);
        double c3 = Math.Sin(2.0 * Math.PI * ph_ep_howl3);
        double shimmer = 1.0 + Math.Sin(audioClock * Math.PI * 2.0 * 4.3) * 0.03;
        engineSample += (c1 * 0.9 + c2 * 0.5 + c3 * 0.3) * shimmer
                      * ep_surgeSmooth * (0.075f + ld * 0.03f) * engMul;
        ph_ep_howl1 = (ph_ep_howl1 + chimeHz       * invSR) % 1.0;
        ph_ep_howl2 = (ph_ep_howl2 + chimeHz * 1.5 * invSR) % 1.0;
        ph_ep_howl3 = (ph_ep_howl3 + chimeHz * 2.0 * invSR) % 1.0;
    }
    else
    {
        ph_ep_howl1 = (ph_ep_howl1 + 46.0 * invSR) % 1.0;
        ph_ep_howl2 = (ph_ep_howl2 + 69.0 * invSR) % 1.0;
        ph_ep_howl3 = (ph_ep_howl3 + 92.0 * invSR) % 1.0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  3. MG-B TRACTION MOTOR — TWO regimes:
    //
    //  3a. LAUNCH VOICE (0–20 kph) — the part that was missing entirely.
    //      "oohwwwaaaaaa": a swept vowel-like motor sweep — fundamental climbs
    //      ~90→400 Hz with speed while a resonant partner partial climbs
    //      slightly FASTER, morphing the vowel from "ooh" (partials close,
    //      dark) to "aaa" (spread, open) as it pulls.
    //      "ouh ouh ouh ouh": a ~3.2 Hz syllabic pulse on that sweep at
    //      walking speed — amplitude dips AND a small pitch dip each pulse
    //      (pure tremolo doesn't read as syllables; the pitch dip does).
    //      Pulses fade out by ~18 kph as the sweep smooths into the whine.
    //
    //  3b. CRUISE WHINE — the established 3-partial vacuum-cleaner voice.
    // ═════════════════════════════════════════════════════════════════════════
    // [REMOVED] two-mode swap detection + pitch holds — fully continuous now.

    // ── 3a. Launch voice ──────────────────────────────────────────────────────
    {
        float launchFrac = Mathf.Clamp01(spd / 20f);                 // 0 at rest → 1 by 20kph
        bool  moving     = spd > 0.15f || accel > 0.05f;
        float lvGate     = moving ? (1f - launchFrac) : 0f;           // strongest 0-10kph, gone by 20
        float lvTgt      = lvGate * (0.020f + accel * 0.026f) * (isH50 ? 1.12f : 1f);
        epv2_lvVolSm += (lvTgt - epv2_lvVolSm) * 0.006f;

        if (epv2_lvVolSm > 0.0006f)
        {
            // Syllabic "ouh ouh" pulse: strongest at walking pace, gone by ~18kph.
            epv2_lvPulsePhase = (epv2_lvPulsePhase + 3.2 * invSR) % 1.0;
            float pulseDepth  = Mathf.Clamp01(1f - spd / 18f) * 0.55f;
            float pulseRaw    = 0.5f + 0.5f * (float)Math.Sin(2.0 * Math.PI * epv2_lvPulsePhase);
            float pulse       = 1f - pulseDepth + pulseDepth * pulseRaw * pulseRaw;   // squared → dippy syllables
            float pitchDip    = 1f - pulseDepth * 0.04f * (1f - pulseRaw);            // small pitch sag per syllable

            // Vowel morph: fundamental 90→400Hz; resonant partner runs from
            // 1.25x (dark "ooh") up to 2.4x (open "aaa") across the sweep.
            float lvHz  = (90f + launchFrac * 310f) * pitchDip;
            float vowel = 1.25f + launchFrac * 1.15f;
            epv2_lvHzSm += (lvHz - epv2_lvHzSm) * 0.02f;

            float v = epv2_lvVolSm * pulse * npcVolumeScale * engVolPersonality;
            txSample += Math.Sin(2.0 * Math.PI * ph_epv2_lv1) * v
                      + Math.Sin(2.0 * Math.PI * ph_epv2_lv2) * v * 0.55
                      + Math.Sin(2.0 * Math.PI * ph_epv2_lv3) * v * 0.20;
            ph_epv2_lv1 = (ph_epv2_lv1 + epv2_lvHzSm          * invSR) % 1.0;
            ph_epv2_lv2 = (ph_epv2_lv2 + epv2_lvHzSm * vowel  * invSR) % 1.0;
            ph_epv2_lv3 = (ph_epv2_lv3 + epv2_lvHzSm * 3.02   * invSR) % 1.0;
        }
    }

    // ── 3b. Cruise whine ──────────────────────────────────────────────────────
    float mg2Tgt = (spd / MAX_SPD) * mg2Max;
    ep_mg2HzSmooth += (mg2Tgt - ep_mg2HzSmooth) * (isGen5
        ? (mg2Tgt > ep_mg2HzSmooth ? 0.009f : 0.005f)   // Gen5: dedicated HGM processing → tighter torque-blend response
        : (mg2Tgt > ep_mg2HzSmooth ? 0.006f : 0.003f)); // Gen4 baseline, unchanged
    if (ep_mg2HzSmooth < 0.3f) ep_mg2HzSmooth = 0f;

    float mg2Frac = Mathf.Clamp01(ep_mg2HzSmooth / mg2Max);
    // Volume floor raised at the low end so the motor is audible from the
    // first meters instead of only appearing at speed (mg2Frac² alone killed
    // everything below ~15kph).
    float mg2VolTgt = mg2Frac > 0.001f
        ? (0.010f + mg2Frac * 0.014f + mg2Frac * mg2Frac * 0.028f + ld * 0.016f) * (isH50 ? 1.15f : 1f)
        : 0f;
    epv2_mg2VolSm += (mg2VolTgt - epv2_mg2VolSm) * 0.008f;
    if (ep_mg2HzSmooth > 0.3f && epv2_mg2VolSm > 0.0005f)
    {
        float v = epv2_mg2VolSm * npcVolumeScale * engVolPersonality;
        txSample += Math.Sin(2.0 * Math.PI * ph_ep_mg2a) * v
                  + Math.Sin(2.0 * Math.PI * ph_ep_mg2b) * v * 0.38
                  + Math.Sin(2.0 * Math.PI * ph_epv2_mg2c) * v * 0.16;   // 3rd partial: the "air" on top
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  4. MG-A SWEEPER — SIGNED speed, sweeps DOWN THROUGH ZERO and back up.
    //     Real physics: MG-A speed ≈ k1·engine − k2·output in the split set,
    //     so at one specific ratio it stops dead — volume notches out at the
    //     crossing, then the whine returns climbing the other side.
    // ═════════════════════════════════════════════════════════════════════════
    {
        float mg1Signed = engHz * 6.4f - wheelRev * 7.1f;
        epv2_mg1SignedSm += (mg1Signed - epv2_mg1SignedSm) * 0.010f;

        float mg1AbsHz = Mathf.Abs(epv2_mg1SignedSm);
        ep_mg1HzSmooth += (mg1AbsHz - ep_mg1HzSmooth) * 0.02f;

        // Silence notch at the zero crossing — |speed| gates the volume.
        float zeroNotch = Mathf.Clamp01(mg1AbsHz / 55f);
        float mg1Vol = (0.011f + ep_surgeSmooth * 0.012f + ld * 0.006f)
                     * zeroNotch * npcVolumeScale * engVolPersonality;
        if (ep_mg1HzSmooth > 2f && mg1Vol > 0.0004f)
            txSample += Math.Sin(2.0 * Math.PI * ph_ep_mg1a) * mg1Vol
                      + Math.Sin(2.0 * Math.PI * ph_ep_mg1b) * mg1Vol * 0.30;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  5. PLANETARY MESH WHINE MACHINE — fixed tooth count (continuous ratio,
    //     under-drive path meshes more teeth than direct drive), engine-RPM
    //     tracked, blooming toward the rev-surge crest exactly like the
    //     Allison B400R gear-1 whine blooms toward its 1→2 upshift.
    // ═════════════════════════════════════════════════════════════════════════
    {
        float teeth   = 44f * epv2_whineTint;
        float meshTgt = engHz * teeth;
        ep_meshHzSmooth += (meshTgt - ep_meshHzSmooth) * (meshTgt > ep_meshHzSmooth ? 0.006f : 0.003f);
        if (ep_meshHzSmooth < 2f) ep_meshHzSmooth = 0f;

        // Bloom envelope: rides rn — as the mountain rev crests near the top,
        // the mesh whine swells with it, then recedes on the fall.
        float bloomTgt = Mathf.Clamp01((rn - 0.45f) / 0.5f);
        epv2_bloomSm += (bloomTgt - epv2_bloomSm) * (bloomTgt > epv2_bloomSm ? 0.004f : 0.008f);

        float meshVolTgt = (0.011f + ld * 0.011f + epv2_bloomSm * 0.026f);
        epv2_meshVolSm += (meshVolTgt - epv2_meshVolSm) * 0.006f;

        if (ep_meshHzSmooth > 0.1f && epv2_meshVolSm > 0.0005f)
        {
            double m1 =  Math.Sin(2.0 * Math.PI * ph_ep_mesh1);
            double m2 = -Math.Sin(2.0 * Math.PI * ph_ep_mesh2);   // inverted partner pair
            double m3 =  Math.Sin(2.0 * Math.PI * ph_ep_mesh3);
            txSample += (m1 + m2 * 0.5 + m3 * 0.2) * epv2_meshVolSm * engMul;
        }

        // Bloom overlay — a separate close-detuned pair a fifth above the
        // mesh so the surge crest gets that Allison-G1 "eeeEEEE" sheen.
        if (epv2_bloomSm > 0.02f && ep_meshHzSmooth > 40f)
        {
            float bv = epv2_bloomSm * 0.018f * engMul;
            txSample += Math.Sin(2.0 * Math.PI * ph_epv2_bloom1) * bv
                      + Math.Sin(2.0 * Math.PI * ph_epv2_bloom2) * bv * 0.6;
            ph_epv2_bloom1 = (ph_epv2_bloom1 + ep_meshHzSmooth * 1.5   * invSR) % 1.0;
            ph_epv2_bloom2 = (ph_epv2_bloom2 + ep_meshHzSmooth * 1.512 * invSR) % 1.0;
        }
        ph_ep_mesh1 = (ph_ep_mesh1 + ep_meshHzSmooth          * invSR) % 1.0;
        ph_ep_mesh2 = (ph_ep_mesh2 + ep_meshHzSmooth * 1.0008 * invSR) % 1.0;
        ph_ep_mesh3 = (ph_ep_mesh3 + ep_meshHzSmooth * 1.5    * invSR) % 1.0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  6. DPIM2 PWM CARRIER — 3-phase inverter switching whine + sidebands.
    //     Sidebands ride the MG-B electrical frequency (carrier ± modulation),
    //     which is what makes a real inverter "sing" with the motor rather
    //     than sit at a flat tone. Whistler units carry it louder.
    // ═════════════════════════════════════════════════════════════════════════
    {
        float invTgt = invBase + (spd / MAX_SPD) * 900f;
        ep_invHzSmooth += (invTgt - ep_invHzSmooth) * (isGen5 ? 0.028f : 0.02f); // Gen5: faster inverter response too
        float actWork = Mathf.Max(ld, epv2_regenVolSm * 6f);   // inverter works hard BOTH directions
        // [ADD] Gen5's revised DPIM2 IGBTs/firmware manage heat/voltage
        // spikes "significantly better" per Allison's own spec sheet — in
        // switching-inverter terms that's a cleaner PWM waveform, which
        // reads as LESS harsh sideband/whistler content, not just quieter.
        // Whistler-unit odds are halved, and the whistler multiplier itself
        // is softened, since a cleaner-switching inverter is less prone to
        // that harsh resonance in the first place.
        bool  gen5Whistler = epv2_isWhistler && (NextNoiseSample() * 0.5 + 0.5) < 0.5; // roughly halves the odds
        float whistlerMul  = isGen5 ? (gen5Whistler ? 1.7f : 1f) : (epv2_isWhistler ? 2.6f : 1f);
        float invVol  = (0.0012f + actWork * 0.0020f) * whistlerMul * npcVolumeScale;
        txSample += Math.Sin(2.0 * Math.PI * ph_ep_inv)    * invVol
                  + Math.Sin(2.0 * Math.PI * ph_epv2_invSB) * invVol * (isGen5 ? 0.32f : 0.45f);
        // [FIX] Previous version locked this partial's frequency to
        // (invHz+mg2Hz)/2 — harmonically related to the carrier, so it
        // never actually beat against anything and was inaudible. Real
        // mechanism for "smoother blended harmonics" from a revised
        // multi-IGBT inverter: interleaved/phase-shifted PWM carriers
        // across the two half-bridge legs (a genuine modern EMI-reduction
        // technique) — the two legs run at a small deliberate Hz offset
        // from each other instead of one locked carrier. That produces
        // real audible beating/shimmer against the main carrier tone,
        // which is what actually reads as "blended" rather than flat.
        if (isGen5)
            txSample += Math.Sin(2.0 * Math.PI * ph_ep_gen5Blend) * invVol * 0.6;
        ph_ep_inv     = (ph_ep_inv     +  ep_invHzSmooth                        * invSR) % 1.0;
        ph_epv2_invSB = (ph_epv2_invSB + (ep_invHzSmooth + ep_mg2HzSmooth * 2f) * invSR) % 1.0;
        if (isGen5)
            ph_ep_gen5Blend = (ph_ep_gen5Blend + (ep_invHzSmooth + 4.3f) * invSR) % 1.0; // small fixed offset -> real beat
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  7. REGEN — per the operator's manual: active whenever throttle is
    //     released, increasing with brake. Descending MG-B whine + soft
    //     overrun undertone, NOT just a brake-gated blip.
    // ═════════════════════════════════════════════════════════════════════════
    {
        bool coastRegen  = accel < 0.01f && spd > 6f;
        bool brakeRegen  = bkPd  > 0.05f && spd > 1f;
        // [ADD] Gen5: revised DPIM2 + faster BMS cell balancing captures
        // regen more efficiently — a deeper target and a slower release
        // (longer sustain into the stop) rather than just a louder blip.
        float regenTgt = brakeRegen ? (0.030f + bkPd * 0.035f) * Mathf.Clamp01(spd / 12f) * (isGen5 ? 1.35f : 1f)
                       : coastRegen ? 0.016f * Mathf.Clamp01(spd / 20f) * (isGen5 ? 1.35f : 1f)
                       : 0f;
        epv2_regenVolSm += (regenTgt - epv2_regenVolSm) * (regenTgt > epv2_regenVolSm
            ? 0.010f
            : (isGen5 ? 0.0022f : 0.006f)); // Gen5 releases much slower — clearly longer sustained tail
        if (epv2_regenVolSm > 0.0006f)
        {
            float rv = epv2_regenVolSm * npcVolumeScale * engVolPersonality;
            double regenHz = ep_mg2HzSmooth * 1.5;
            ph_ep_regen  = (ph_ep_regen  + regenHz        * invSR) % 1.0;
            ph_epv2_over = (ph_epv2_over + (34.0 + spd * 0.5) * invSR) % 1.0;
            txSample += Math.Sin(2.0 * Math.PI * ph_ep_regen)       * rv
                      + Math.Sin(2.0 * Math.PI * ph_ep_regen * 2.0) * rv * 0.30
                      + Math.Sin(2.0 * Math.PI * ph_epv2_over)      * rv * 0.35;  // low overrun moan
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  8. [REMOVED] mode-swap clutch transient — no modes in the continuous
    //     model; the ratio just slides. (Thud/hiss machinery deleted.)
    //  9. [REMOVED] stop-start restart crank — engine idles at stops in real
    //     service and never auto-restarts, so there's nothing to crank.
    // ═════════════════════════════════════════════════════════════════════════

    ph_ep_mg2a   = (ph_ep_mg2a   + ep_mg2HzSmooth        * invSR) % 1.0;
    ph_ep_mg2b   = (ph_ep_mg2b   + ep_mg2HzSmooth * 2.0  * invSR) % 1.0;
    ph_epv2_mg2c = (ph_epv2_mg2c + ep_mg2HzSmooth * 3.01 * invSR) % 1.0;
    ph_ep_mg1a   = (ph_ep_mg1a   + ep_mg1HzSmooth        * invSR) % 1.0;
    ph_ep_mg1b   = (ph_ep_mg1b   + ep_mg1HzSmooth * 1.5  * invSR) % 1.0;
}

// ═══════════════════════════════════════════════════════════════════════════
//  EGEN FLEX — genuinely its own voice now, not a reskinned DoH4xDSP call.
//  Real differences from H40EP/H50EP this is built around:
//   · Integrated motor-in-gearbox drive unit -- ONE blended mesh/whine
//     voice instead of H4x's separate MG1/MG2 pair.
//   · Disconnect clutch -- true engine-off EV propulsion, with a real
//     clutch-engagement thump when the diesel core comes back online.
//   · WEG-cooled (water-ethylene-glycol) inverter -- steadier switching
//     frequency under load than DPIM2's oil-cooled unit, so its carrier
//     stays cleaner/lower-noise-floor rather than getting grittier as it
//     heats up.
//   · LTO battery -- faster-responding energy delivery, reflected in a
//     harder/punchier load-stress windup than H4x's.
// ═══════════════════════════════════════════════════════════════════════════
private void DoEGenFlexDSP(ref double txSample, ref double engineSample,
                            float rn, float ld, float engMul, float engVolPersonality,
                            double noiseHp, double noiseHi, double invSR)
{
    // [ADD] Startup windup -- see DoHybridStartupWindup in
    // BusAudioEngine.StartupSequence.cs. Punchier/higher pitch than H4x's,
    // matching eGen Flex's own higher-voltage LTO-pack character elsewhere
    // in this function.
    DoHybridStartupWindup(ref txSample, engineState == EngineRunState.Running, 280f, 1600f, engMul, invSR);

    // WEG-cooled inverter -- cleaner, steadier carrier, less noise bleed
    // than DPIM2's oil-cooled unit.
    noiseHp *= 0.55;

    // ── TRUE ENGINE-OFF via the disconnect clutch -- the one hardware
    //    capability H4x/Gen5 flatly don't have (both idle the diesel
    //    continuously). Below a light-load/steady cruise threshold, the
    //    diesel core fades toward silent (motor-only EV cruise). ────────
    float engineOffTarget = (ld < 0.28f && rn < 0.55f) ? 0.06f : 1f;
    float offRate = engineOffTarget < egf_engineOffSm ? 0.997f : 0.985f;
    egf_engineOffSm = Mathf.Lerp(engineOffTarget, egf_engineOffSm, offRate);
    engineSample *= egf_engineOffSm;

    bool clutchJustEngaging = engineOffTarget > 0.5f && egf_engineOffSm < 0.35f;
    if (clutchJustEngaging) egf_clutchThumpVol = 0.11f;
    egf_clutchThumpVol *= 0.90f;
    if (egf_clutchThumpVol > 0.001f)
    {
        ph_egf_clutchThump += 52.0 * invSR;
        txSample += Math.Sin(2.0 * Math.PI * ph_egf_clutchThump) * egf_clutchThumpVol * npcVolumeScale;
    }

    float motSpd = (spd / 3.6f) / TCIRC; // output rev/s -- integrated unit, no separate MG1 input-side speed

    // ── LOAD STRESS -- harder/punchier than H4x's, per the LTO pack's
    //    faster-responding delivery. Drives the gearbox-mesh whine's
    //    intensity and saturation below. ─────────────────────────────────
    float egfStressTarget = Mathf.Clamp01(accel);
    egf_stress += (egfStressTarget - egf_stress) * (float)invSR / (egfStressTarget > egf_stress ? 0.9f : 2.2f); // faster attack than BAE's family

    // ── INTEGRATED GEARBOX MESH -- ONE blended voice (not a separate
    //    MG1/MG2 pair) since the motor lives inside the gearbox casing on
    //    real eGen Flex hardware, tracking output speed directly. ────────
    float gearWhineHzTgt = 46f + motSpd * 11.5f;
    egf_gearVolSm += ((0.055f + Mathf.Min(motSpd / 60f, 1f) * 0.09f + ld * 0.05f + egf_stress * 0.05f) - egf_gearVolSm)
        * (float)invSR / 0.22f;
    if (egf_gearVolSm > 0.0006f)
    {
        double gw1 = Math.Sin(2.0 * Math.PI * ph_egf_gearWhine1);
        double gw2 = Math.Sin(2.0 * Math.PI * ph_egf_gearWhine2) * 0.34;
        double gwRaw = gw1 + gw2;
        gwRaw = Math.Tanh(gwRaw * (1.0 + egf_stress * 1.5)) * 0.9; // saturates in under a hard pull -- punchy, not clean forever
        egf_gearWhineLP += (gwRaw - egf_gearWhineLP) * 0.30;
        double gearVolThisSample = egf_gearVolSm * engMul * engVolPersonality * (isNeutral ? 0.0 : 1.0);
        txSample += egf_gearWhineLP * gearVolThisSample
                  + noiseHi * gearVolThisSample * 0.06 * egf_stress;
    }
    ph_egf_gearWhine1 = (ph_egf_gearWhine1 + gearWhineHzTgt        * invSR) % 1.0;
    ph_egf_gearWhine2 = (ph_egf_gearWhine2 + gearWhineHzTgt * 2.49 * invSR) % 1.0;

    // ── WEG-COOLED INVERTER CARRIER -- clean, mostly-tonal high-frequency
    //    switching whine, thin harmonic content (liquid cooling holding a
    //    steady frequency instead of the carrier drifting/roughening as it
    //    heats up the way an oil-cooled unit's does). ─────────────────────
    float invHzTgt = 3800f + egf_stress * 900f + Mathf.Min(motSpd / 60f, 1f) * 500f;
    egf_invVolSm += ((EngineOffGate(egf_engineOffSm) * (0.010f + egf_stress * egf_stress * 0.028f)) - egf_invVolSm) * 0.02f;
    if (egf_invVolSm > 0.0003f)
    {
        double iv1 = Math.Sin(2.0 * Math.PI * ph_egf_inv1);
        double iv2 = Math.Sin(2.0 * Math.PI * ph_egf_inv2 * 1.997) * 0.22; // just one thin partial -- cleaner than DPIM2's grittier mix
        txSample += (iv1 + iv2) * egf_invVolSm * engMul * engVolPersonality;
    }
    ph_egf_inv1 = (ph_egf_inv1 + invHzTgt        * invSR) % 1.0;
    ph_egf_inv2 = (ph_egf_inv2 + invHzTgt * 1.997 * invSR) % 1.0;

    // ── REGEN WHINE -- descending voice on lift-off/braking while still
    //    rolling, same real mechanism as BAE's/H4x's regen layers. ───────
    {
        bool  egfCoastRegen  = accel < 0.01f && spd > 6f;
        float egfRegenTarget = egfCoastRegen ? Mathf.Clamp01(0.3f + bkPd * 0.7f) : 0f;
        egf_regenSmooth += (egfRegenTarget - egf_regenSmooth) * (float)invSR
            / (egfRegenTarget > egf_regenSmooth ? 0.14f : 0.55f); // faster attack than H4x's -- LTO pack accepts regen current quicker
        if (egf_regenSmooth > 0.001f)
        {
            double regenHz = 420.0 + Mathf.Min(motSpd / 60f, 1f) * 480.0;
            double regen = Math.Sin(2.0 * Math.PI * ph_egf_regen);
            txSample += regen * egf_regenSmooth * (0.05f + Mathf.Min(motSpd / 60f, 1f) * 0.05f) * engMul * engVolPersonality;
            ph_egf_regen = (ph_egf_regen + regenHz * invSR) % 1.0;
        }
    }
}
// Small local helper -- the inverter carrier should still fade with the
// disconnect clutch's engine-off state (no traction-inverter switching to
// speak of while coasting on the battery alone at near-zero load), same
// gate the engine core itself uses, without re-deriving it twice.
private static float EngineOffGate(float engineOffSm) => Mathf.Clamp01(engineOffSm);

// ═══════════════════════════════════════════════════════════════════════════
//  EP40 / EP50 — genuinely its own DSP voice, not a reskinned DoH4xDSP call
//  (per direct instruction -- same reasoning eGen Flex got its own function
//  for: reusing another unit's whole voice with flags bolted on doesn't
//  read as a real, distinct generation on its own). Same physical hardware
//  as H40EP/H50EP (dual MG1/MG2, DPIM-family inverter -- see CalcEP40RPM's
//  header for the real EP40/H40EP naming research), but built to sound like
//  the pre-2010 generation specifically:
//   · MORE HOLLOW motor tone -- routed through a struck box resonator (the
//     same Resonator/Strike() mechanism the diesel cores use for their
//     hollow-body character), which none of the H4x-family motors use at
//     all. Reads like the unit resonates inside a bigger, less-damped
//     housing rather than H40EP/H50EP's tighter, more contained tone.
//   · Coarser, rawer inverter carrier -- more broadband noise blended into
//     the switching whine (real basis: less mature power electronics/
//     firmware before the 2010 "enhanced electronic controls" refresh).
//   · Occasional relay/contactor clunk -- an audible mechanical relay
//     click on power-electronics transitions that a modern, more reliable
//     unit's solid-state switching doesn't produce.
// ═══════════════════════════════════════════════════════════════════════════
private double ep4x_ph_mg1a, ep4x_ph_mg1b;
private double ep4x_ph_mg2a, ep4x_ph_mg2b;
private float  ep4x_mg1HzSmooth, ep4x_mg2HzSmooth;
private Resonator ep4x_resHollow; // motor cavity -- the "more hollow" character
private double ep4x_ph_inv1, ep4x_ph_inv2;
private float  ep4x_invVolSmooth;
private float  ep4x_relayClunkVol;
private float  ep4x_relayCooldown;
private bool   ep4x_relayWasLow = true;
private double ep4x_ph_regen;
private float  ep4x_regenSmooth;

private void DoEP4xDSP(ref double txSample, ref double engineSample,
                        float rn, float ld, float engMul, float engVolPersonality,
                        double noiseHp, double noiseHi, double invSR)
{
    bool isEP50 = tx == "ep50";

    // Same real motor ceilings as H40EP/H50EP (unchanged spec across the
    // rename) -- not a different physical unit, just a rougher voice on it.
    float mg2Max = isEP50 ? 480f : 560f;

    // ── DUAL MG1/MG2 WHINE -- rougher partial mix than H4x's cleaner tone,
    //    and routed through a hollow box resonator for the "more hollow
    //    motor" character neither H40EP nor H50EP has. ────────────────────
    float mg1HzTgt = 40f + rn * 260f;
    float mg2HzTgt = 60f + Mathf.Min(mg2Max, 60f + rn * mg2Max);
    ep4x_mg1HzSmooth += (mg1HzTgt - ep4x_mg1HzSmooth) * (float)invSR / 0.35f;
    ep4x_mg2HzSmooth += (mg2HzTgt - ep4x_mg2HzSmooth) * (float)invSR / 0.30f;

    float motorVol = (0.045f + ld * 0.075f + rn * 0.035f) * engMul * engVolPersonality * (isEP50 ? 1.10f : 1.0f);

    double mg1 = Math.Sin(2.0 * Math.PI * ep4x_ph_mg1a) * 1.0
               + Math.Sin(2.0 * Math.PI * ep4x_ph_mg1b) * 0.42;
    double mg2 = Math.Sin(2.0 * Math.PI * ep4x_ph_mg2a) * 1.0
               + Math.Sin(2.0 * Math.PI * ep4x_ph_mg2b) * 0.36;
    // Rougher mix than H4x's tanh-clean blend -- a bit of straight noise
    // folded in, pre-refresh power electronics reading grittier.
    double motorRaw = (mg1 * 0.55 + mg2 * 0.60) + noiseHi * 0.10;

    // The hollow-cavity pass -- this IS the "more hollow motor" request:
    // strike the box resonator with the motor's own combined tone instead
    // of adding it as a flat sine, so it genuinely rings inside a cavity.
    double fBox = 2.0 * Math.Sin(Math.PI * 210.0 / SR); // tuned lower/boxier than the diesel cores' engine-bay cavities -- this is a motor housing, not an engine bay
    double hollow = ep4x_resHollow.Strike(motorRaw, fBox, 1.0 / 3.4);

    txSample += (motorRaw * 0.35 + hollow * 0.85) * motorVol;

    ep4x_ph_mg1a = (ep4x_ph_mg1a + ep4x_mg1HzSmooth        * invSR) % 1.0;
    ep4x_ph_mg1b = (ep4x_ph_mg1b + ep4x_mg1HzSmooth * 1.5  * invSR) % 1.0;
    ep4x_ph_mg2a = (ep4x_ph_mg2a + ep4x_mg2HzSmooth        * invSR) % 1.0;
    ep4x_ph_mg2b = (ep4x_ph_mg2b + ep4x_mg2HzSmooth * 2.0  * invSR) % 1.0;

    // ── COARSE INVERTER CARRIER -- rawer/noisier than DPIM2's cleaner
    //    switching whine, real basis: pre-2010 power electronics generation. ──
    float invHzTgt = 2600f + ld * 700f + (isEP50 ? 350f : 0f); // lower base than H4x's 3600/4200 -- earlier-generation switching frequency ceiling
    float invTarget = (0.018f + ld * ld * 0.030f) * engMul * engVolPersonality;
    ep4x_invVolSmooth += (invTarget - ep4x_invVolSmooth) * 0.02f;
    if (ep4x_invVolSmooth > 0.0003f)
    {
        double iv1 = Math.Sin(2.0 * Math.PI * ep4x_ph_inv1) * 0.5;
        double iv2 = noiseHp * 0.6; // noisier/rawer than eGen Flex's near-pure tonal carrier
        txSample += (iv1 + iv2) * ep4x_invVolSmooth;
    }
    ep4x_ph_inv1 = (ep4x_ph_inv1 + invHzTgt * invSR) % 1.0;

    // ── RELAY/CONTACTOR CLUNK -- audible mechanical relay click on power
    //    transitions, real basis: less mature switching hardware before
    //    the 2010 reliability refresh. Fires on load crossing a threshold. ──
    bool loadIsHigh = ld > 0.30f;
    ep4x_relayCooldown -= (float)invSR;
    if (loadIsHigh != !ep4x_relayWasLow && ep4x_relayCooldown <= 0f)
    {
        ep4x_relayClunkVol = 0.05f;
        ep4x_relayCooldown = 0.4f; // debounce -- one clunk per real transition, not per sample
    }
    ep4x_relayWasLow = !loadIsHigh;
    if (ep4x_relayClunkVol > 0.001f)
    {
        txSample += noiseHp * ep4x_relayClunkVol * engMul;
        ep4x_relayClunkVol *= 0.85f;
    }

    // ── REGEN WHINE -- same real mechanism as H4x's, slightly duller/less
    //    filtered to match the rest of this unit's rawer character. ────────
    bool  ep4CoastRegen  = accel < 0.01f && spd > 6f;
    float ep4RegenTarget = ep4CoastRegen ? Mathf.Clamp01(0.3f + bkPd * 0.7f) : 0f;
    ep4x_regenSmooth += (ep4RegenTarget - ep4x_regenSmooth) * (float)invSR / (ep4RegenTarget > ep4x_regenSmooth ? 0.18f : 0.60f);
    if (ep4x_regenSmooth > 0.001f)
    {
        double regenHz = 360.0 + rn * 420.0;
        double regen = Math.Sin(2.0 * Math.PI * ep4x_ph_regen) * 0.7 + noiseHi * 0.3;
        txSample += regen * ep4x_regenSmooth * (0.045f + rn * 0.04f) * engMul * engVolPersonality;
        ep4x_ph_regen = (ep4x_ph_regen + regenHz * invSR) % 1.0;
    }
}

    // ═══════════════════════════════════════════════════════════════════════
    //  BAE HYBRIDRIVE  (tx == "bae")  —  FULL FRESH REBUILD
    //
    //  Everything from the old DoBAEEngineDSP/DoBAEGen3EngineDSP/
    //  DoHDS300EngineDSP is gone. Those were also never actually called from
    //  anywhere in this file — dead code, dispatched from nothing — which is
    //  the real reason nothing about them ever reliably came through. BAE is
    //  now wired exactly like every other transmission: one entry in
    //  DoCombustionEngine's tx dispatch chain, one method, called with the
    //  same signature shape DoH4xDSP uses.
    //
    //  BEHAVIOR (per spec):
    //   · 0-10 kph: pure stop-start. The genset is NOT running at all in this
    //     band — only the traction motor is audible, and it's the real XE
    //     electric-background-drone layer (stolen verbatim from the XE40
    //     "ELECTRIC BACKGROUND MOTOR DRONE" block), tracking road speed.
    //   · 10 kph+: the genset wakes up. Both its RPM and its own volume
    //     multiplier climb together, naturally (slew-limited, not a snap),
    //     from 0.0 up to 1.3x, chasing a "wanted RPM" set by how hard the
    //     pedal is pressed (ld). Once it reaches that wanted RPM it just
    //     SITS there — forever, as long as accel stays held, because the
    //     target itself isn't moving, not because of any special "stuck"
    //     state. Let go of accel and it cuts off (fast decay, distinct from
    //     the slow climb — an actual cutoff, not a taper). Press accel again
    //     and it climbs fresh from wherever it left off (near-zero by then)
    //     and continues normally — there's no persistent plateau field left
    //     to get stuck holding a stale value across a stop the way the old
    //     version did.
    //   · The motor is NEVER stop-start — it's live at every speed above a
    //     hair off zero, and its own volume/pitch keeps climbing and
    //     "stressing" with road speed and load the whole time, on top of
    //     whatever the genset is doing.
    // ═══════════════════════════════════════════════════════════════════════
    private float  bae3_motorHzSmooth = 0f;   // XE-drone motor pitch, tracks spd only
    private float  bae3_droneGainSmooth = 0f; // XE-drone's own gear/load gain smoothing
    private double ph_bae3_mot1, ph_bae3_mot2;
    private double bae3_droneLP = 0.0; // one-pole lowpass state -- rounds the drone off, less sharp

    // High "futuristic" electric motor whine -- see call site for reasoning.
    private float  bae3_hiMotorHzSmooth = 0f;
    private double ph_bae3_hiMot1, ph_bae3_hiMot2, ph_bae3_hiMot3;

    // "Plane engine" second motor layer -- see call site for reasoning.
    private float  bae3_hissEnvSmooth = 0f;   // DIWA opt1_4-style hiss layer envelope
    private double bae3_hissLP, bae3_hissPrev;
    private double ph_bae3_hiss;
    private float  bae3_invWhineHzSmooth = 0f; // "inverted" whine -- falls as spd rises
    private double ph_bae3_invWhine;
    private float  bae3_invWhineAttackT = 0f; // time since this engagement started -- drives the start-high/dip/recover shape

    private float  bae3_wantedRpm  = 0f; // smoothed target -- how hard the pedal is pressed, translated to RPM
    private float  bae3_rpm        = 0f; // the actual climbing/holding/cutting-off RPM
    public  float  BAE3_RPM_RISE_RATE = 900f;  // rpm/s climbing -- natural, not instant
    public  float  BAE3_RPM_CUT_RATE  = 2600f; // rpm/s on release -- an actual cutoff, not a taper
    public  float  BAE3_IDLE_RPM      = 650f;
    public  float  BAE3_MAX_RPM       = 2650f; // [FIX] raised 2100 -> 2650, more real headroom to climb into under full throttle
    public  float  BAE3_ENGAGE_KPH    = 10f;   // stop-start threshold -- below this, genset is silent, period

    // [NEW] "Energy needed" -- a slow-moving reservoir that adds to or takes
    // away from the current RPM threshold on top of the base accel->RPM
    // curve. Hard accel builds it up (genset needs to work harder, so the
    // ceiling it's climbing toward creeps higher the longer/harder you push),
    // coasting bleeds it back down (less energy demanded, threshold relaxes),
    // and kickdown adds a hard extra chunk on top, same spirit as the real
    // kickdown-creep systems elsewhere in this file (epv2_kdCreep etc).
    private float  bae3_energyNeeded = 0f; // 0..1 reservoir
    public  float  BAE3_ENERGY_BUILD_RATE = 0.35f;  // per second, scaled by accel strength
    public  float  BAE3_ENERGY_DECAY_RATE = 0.55f;  // per second while coasting (accel released)
    public  float  BAE3_ENERGY_KICKDOWN_ADD = 0.45f; // extra flat add while kickdown held
    public  float  BAE3_ENERGY_RPM_SPAN     = 550f;  // how many rpm the full 0..1 reservoir is worth

    // [NEW] "Panting" -- stolen from Voith 864.6's real RPM-load engine
    // vibration (section 6 in DoVoithCharacter): a throb whose rate rises
    // with RPM progress, giving that breathy motorcycle-rev-under-load feel.
    // Only active while accelerating AND actually moving -- never at 0kph.
    private float  bae3_pantHz  = 0f;
    private float  bae3_pantVol = 0f;
    private double ph_bae3_pant;

    private double ph_bae3_isg1, ph_bae3_isg2;
    private float  bae3_engineVolMul = 0f; // the 0.0 -> 1.3 climb, in lockstep with bae3_rpm

    // [ADD] H50EP whine transplant state -- see DoBAEDSP for the full
    // design note. Tracks the traction motor (bae3_motorHzSmooth), NOT the
    // genset, so it works before/without the genset running.
    private float  bae_h50tDelayTimer = 0f;
    private float  bae_h50tHzSmooth   = 0f;
    private float  bae_h50tVolSmooth  = 0f;
    private double ph_bae_h50t1, ph_bae_h50t2, ph_bae_h50t3;

    private void DoBAEDSP(ref double txSample, ref double engineSample,
                           float rn, float ld, float engMul, float engVolPersonality,
                           double noiseHp, double noiseHi, double invSR)
    {
        // [ADD] Startup windup -- see DoHybridStartupWindup in
        // BusAudioEngine.StartupSequence.cs. Same tuned range
        // DoVoithStartupWindup already validated -- that one was explicitly
        // modeled AFTER a real BAE HybriDrive ISG windup and tried on Voith
        // first per its own header note, so BAE (the real thing it was
        // modeling) gets the identical shape.
        DoHybridStartupWindup(ref txSample, engineState == EngineRunState.Running, 260f, 1480f, engMul, invSR);

        // [FIX] Louder again -- 2.1 -> 2.8.
        const float BAE3_MASTER_VOL = 2.8f;

        // ═════════════════════════════════════════════════════════════════
        //  GENSET — demand/tier driven, not speed-locked.
        //  A real HybriDrive APU is commanded off electrical demand, not
        //  off vehicle speed directly -- the old version made the diesel a
        //  mirror of spd, which isn't how a series hybrid works. Demand
        //  (load + speed as the available proxies for traction draw --
        //  BAE_DEMAND_W_AC/W_BRAKE stay ready for when those signals get
        //  threaded into this function's params) drives a 3-tier setpoint
        //  (idle-charge / cruise-charge / max-charge) with sustain+dwell
        //  hysteresis so it doesn't hunt on every small throttle wobble,
        //  then RPM slews toward that tier's target.
        // ═════════════════════════════════════════════════════════════════
        bae_demandRaw = Mathf.Clamp01(
            (ld * BAE_DEMAND_W_LOAD + Mathf.Clamp01(spd / MAX_SPD) * BAE_DEMAND_W_SPEED)
            / Mathf.Max(0.01f, BAE_DEMAND_W_LOAD + BAE_DEMAND_W_SPEED));
        bae_demandSmooth += (bae_demandRaw - bae_demandSmooth)
            * (bae_demandRaw > bae_demandSmooth ? BAE_DEMAND_ATTACK : BAE_DEMAND_RELEASE);

        int wantTier = bae_demandSmooth >= BAE_TIER_UP[1] ? 2
                     : bae_demandSmooth >= BAE_TIER_UP[0] ? 1
                     : bae_demandSmooth < BAE_TIER_DOWN[0] ? 0
                     : bae_gensetTier; // inside the down-band: hold current tier
        if (bae_gensetTier == 2 && bae_demandSmooth < BAE_TIER_DOWN[1]) wantTier = 1;

        if (wantTier != bae_gensetTier)
        {
            if (wantTier == bae_tierPending) bae_tierSustainTimer += (float)invSR;
            else { bae_tierPending = wantTier; bae_tierSustainTimer = 0f; }

            if (bae_tierDwellTimer <= 0f && bae_tierSustainTimer >= BAE_TIER_SUSTAIN_TIME)
            {
                bae_gensetTier = wantTier;
                bae_tierDwellTimer = BAE_TIER_DWELL_TIME;
                bae_tierClunkVol = 1f; // small mechanical settle transient on tier change
            }
        }
        else
        {
            bae_tierPending = bae_gensetTier;
            bae_tierSustainTimer = 0f;
        }
        if (bae_tierDwellTimer > 0f) bae_tierDwellTimer -= (float)invSR;

        // ── Idle-stop: full shutdown (not just idle-charge) after a
        //    sustained stop with no electrical demand -- BAE's real
        //    auto-stop behavior. ──────────────────────────────────────
        // [FIX] Was a guessed "stopped + no load" dwell timer. Real BAE
        // Start/Stop Drive spec: engine cuts on DECELERATION through 8mph,
        // restarts on ACCELERATION through 10mph -- a speed hysteresis
        // band, not a stationary dwell. (8mph = 12.87kph, 10mph = 16.09kph)
        if (!bae_startStopOff && spd < BAE_STARTSTOP_OFF_KPH && accel <= 0.02f) bae_startStopOff = true;
        else if (bae_startStopOff && spd > BAE_STARTSTOP_ON_KPH) bae_startStopOff = false;
        bool baeIdleStopped = bae_startStopOff;

        float bae_tierFrac = bae_gensetTier == 2 ? BAE_RPM_MAX_FRAC
                            : bae_gensetTier == 1 ? BAE_RPM_CRUISE_FRAC
                            : BAE_RPM_IDLE_FRAC;
        float bae_gensetRpmTarget = baeIdleStopped ? 0f : bae_tierFrac * BAE_GENSET_MAX_RPM;
        float bae_gensetRate = bae_gensetRpmTarget > bae_gensetRpm ? BAE_GENSET_RPM_RISE_RATE : BAE_GENSET_RPM_FALL_RATE;
        bae_gensetRpm = Mathf.MoveTowards(bae_gensetRpm, bae_gensetRpmTarget, bae_gensetRate * (float)invSR);
        if (bae_gensetRpm < 5f) bae_gensetRpm = 0f;
        float gensetProgress = Mathf.Clamp01(bae_gensetRpm / BAE_GENSET_MAX_RPM);
        bool  gensetRunning  = bae_gensetRpm > 5f;

        // NOTE: the diesel APU's actual combustion voice now comes from
        // DoCombustionEngine on the host engineType (B67/etc) -- this
        // function no longer synthesizes a second, redundant combustion
        // core into engineSample. Everything below is the electrical side
        // only: generator load character, traction motor, inverter whine.

        // Small mechanical settle "clunk" on a tier change.
        if (bae_tierClunkVol > 0.01f)
        {
            double k1 = Math.Sin(2.0 * Math.PI * ph_bae_tierClunk1);
            double k2 = Math.Sin(2.0 * Math.PI * ph_bae_tierClunk2);
            txSample += Math.Tanh((k1 + k2 * 0.5) * 1.5) * bae_tierClunkVol * 0.035 * engMul;
            ph_bae_tierClunk1 = (ph_bae_tierClunk1 + 40.0 * invSR) % 1.0;
            ph_bae_tierClunk2 = (ph_bae_tierClunk2 + 64.0 * invSR) % 1.0;
            bae_tierClunkVol *= 0.985f;
        }

        // ═════════════════════════════════════════════════════════════════
        //  GENERATOR + INVERTER VOICE — replaces the old three-sine "UFO"
        //  trio. Generator fundamental tracks gensetRpm (electrical
        //  frequency off the genset shaft); inverter switching whine rides
        //  above it and brightens with electrical demand rather than just
        //  RPM, so a lightly-loaded genset at higher RPM reads leaner than
        //  a hard-loaded one at the same RPM.
        // ═════════════════════════════════════════════════════════════════
        float generatorHz = gensetRunning ? (60f + bae_gensetRpm * 0.20f) : 0f;
        float genVol = gensetRunning
            ? (0.045f + gensetProgress * 0.05f + bae_demandSmooth * 0.05f) * engMul * engVolPersonality
            : 0f;
        if (genVol > 0.0003f)
        {
            double g1 = Math.Sin(2.0 * Math.PI * ph_bae_ufo1);
            double g2 = Math.Sin(2.0 * Math.PI * ph_bae_ufo2 * 2.0);
            txSample += (g1 * 0.7 + g2 * 0.3) * genVol
                      + noiseHi * genVol * 0.08 * bae_demandSmooth; // faint electrical hash under load
        }
        ph_bae_ufo1 = (ph_bae_ufo1 + generatorHz * invSR) % 1.0;
        ph_bae_ufo2 = (ph_bae_ufo2 + generatorHz * invSR) % 1.0;

        float bae_invHzTarget = gensetRunning ? (900f + bae_demandSmooth * 950f) : 0f;
        bae_ufoHzSmooth += (bae_invHzTarget - bae_ufoHzSmooth) * (bae_invHzTarget > bae_ufoHzSmooth ? 0.03f : 0.015f);
        float invVol = gensetRunning
            ? (0.012f + bae_demandSmooth * bae_demandSmooth * 0.05f) * engMul * engVolPersonality
            : 0f;
        if (invVol > 0.0003f)
        {
            double iv = Math.Sin(2.0 * Math.PI * ph_bae_inv) * 0.7
                      + Math.Sin(2.0 * Math.PI * ph_bae_ufo3 * 2.998) * 0.3;
            txSample += iv * invVol;
        }
        ph_bae_inv  = (ph_bae_inv  + bae_ufoHzSmooth        * invSR) % 1.0;
        ph_bae_ufo3 = (ph_bae_ufo3 + bae_ufoHzSmooth * 1.503 * invSR) % 1.0;

        // ── Traction motor — Series-E permanent-magnet motor, no reduction
        //    gearbox (real BAE spec), so pitch tracks wheel speed closely.
        //    [FIX] The old Hz-smoothing coefficients (0.0035/0.006) were
        //    applied PER SAMPLE, unscaled by invSR -- at audio sample rate
        //    that converges in ~6ms, i.e. effectively instantaneous, which
        //    is why the "slower torque build" comment above them never
        //    actually produced an audible windup no matter how those
        //    numbers got tuned. Real motors ARE electrically fast, so pitch
        //    tracking stays quick here -- the slow, Voith-DIWA-style windup
        //    character now lives in the separate STRESS/WINDUP layers below,
        //    which use a proper invSR-scaled seconds-based time constant.
        // ─────────────────────────────────────────────────────────────────
        float bae3MotTarget = spd <= 10f
            ? (spd / 10f) * 340f
            : 340f + (spd - 10f) * 2.4f;
        if (bae3MotTarget < 0.3f) bae3MotTarget = 0f;
        bae3_motorHzSmooth += (bae3MotTarget - bae3_motorHzSmooth) * (float)invSR
            / (bae3MotTarget > bae3_motorHzSmooth ? 0.20f : 0.35f); // ~0.2s/0.35s -- fast, but a real audible slew now

        float droneGainTarget = 1.0f;
        bae3_droneGainSmooth += (droneGainTarget - bae3_droneGainSmooth) * (float)invSR / 1.5f;

        float motSpdFrac = Mathf.Clamp01(bae3_motorHzSmooth / 720f);

        // ═════════════════════════════════════════════════════════════════
        //  LOAD STRESS — the missing "hold the pedal down long enough and
        //  it keeps building" character. Multi-second attack under
        //  sustained accelerator, faster release. Feeds harmonic content,
        //  saturation, the windup whine, and the cooling fan below.
        // ═════════════════════════════════════════════════════════════════
        float bae_stressTarget = Mathf.Clamp01(accel);
        bae_stress += (bae_stressTarget - bae_stress) * (float)invSR
            / (bae_stressTarget > bae_stress ? BAE_STRESS_ATTACK_TAU : BAE_STRESS_RELEASE_TAU);

        float continuousMotorVol = motSpdFrac > 0.001f
            ? (0.05f + motSpdFrac * motSpdFrac * 0.12f + ld * 0.06f + bae_stress * 0.05f) * engMul * (1f + ld * 0.40f) * bae3_droneGainSmooth * BAE3_MASTER_VOL * 0.2f
            : 0f;

        if (continuousMotorVol > 0.0005f)
        {
            // 3rd harmonic and extra grit only bite in as stress builds --
            // a clean tone at low/steady load, a straining one under a long
            // hard pull, instead of one fixed timbre at every condition.
            double droneRaw = Math.Sin(2.0 * Math.PI * ph_bae3_mot1 * 0.85) * 1.0
                             + Math.Sin(2.0 * Math.PI * ph_bae3_mot2 * 0.85 * 1.998 / 2.0) * 0.22
                             + Math.Sin(2.0 * Math.PI * ph_bae3_mot1 * 0.85 * 3.0) * 0.14 * bae_stress;
            droneRaw = Math.Tanh(droneRaw * (1.0 + bae_stress * 1.4)) * 0.9;
            bae3_droneLP += (droneRaw - bae3_droneLP) * 0.35;
            txSample += bae3_droneLP * continuousMotorVol
                      + noiseHi * continuousMotorVol * 0.10 * bae_stress; // inverter/current strain hash, load-only
        }
        ph_bae3_mot1 = (ph_bae3_mot1 + bae3_motorHzSmooth       * invSR) % 1.0;
        ph_bae3_mot2 = (ph_bae3_mot2 + bae3_motorHzSmooth * 2.0 * invSR) % 1.0;

        // ── TORQUE WINDUP WHINE — the Voith-DIWA-scream analog: a second
        //    voice that only blooms in as bae_stress climbs, not on
        //    throttle tip-in. Climbs in pitch and volume the longer the
        //    pedal stays down; this is the "sluggish windup" character. ──
        {
            float windupHz = 700f + bae_stress * 900f + motSpdFrac * 200f;
            double windup = Math.Sin(2.0 * Math.PI * ph_bae_windup) * 0.7
                          + Math.Sin(2.0 * Math.PI * ph_bae_windup2 * 1.503) * 0.3;
            float windupVol = bae_stress * bae_stress * (0.05f + ld * 0.03f) * engMul * engVolPersonality;
            if (windupVol > 0.0003f) txSample += windup * windupVol;
            ph_bae_windup  = (ph_bae_windup  + windupHz        * invSR) % 1.0;
            ph_bae_windup2 = (ph_bae_windup2 + windupHz * 1.503 * invSR) % 1.0;
        }

        // ═════════════════════════════════════════════════════════════════
        //  [ADD] H50EP WHINE TRANSPLANT — same technique already ported
        //  into Voith opt1_4 (a second, continuously Hz-tracked, smoothed
        //  2-partial whine that fades in after a short delay so it reads
        //  as a distinct second voice arriving slightly after the primary
        //  motor sound, not doubled up with it instantly). Ported here
        //  with BAE's own inputs (motor speed/load) instead of Voith's
        //  (rpm/rn), same as the earlier transplant note explains.
        //
        //  [KEY DIFFERENCE per instruction] Voith's version tracks ENGINE
        //  rpm, which only exists once the engine/genset is running --
        //  this one deliberately tracks bae3_motorHzSmooth (the PM
        //  traction motor's own speed) instead, and is NOT gated on
        //  gensetRunning/bae_gensetTier at all. A series-hybrid's traction
        //  motor runs off the HV battery and pulls away from a stop before
        //  the genset ever spins up (and stays silent through it entirely
        //  during an idle-stop coast) -- so this whine has to work whether
        //  or not the genset has kicked in yet, same as the real motor.
        // ═════════════════════════════════════════════════════════════════
        {
            if (motSpdFrac > 0.001f) { if (bae_h50tDelayTimer < 0.35f) bae_h50tDelayTimer += (float)invSR; }
            else                       bae_h50tDelayTimer = 0f;   // resets so the next launch gets the same fresh delay

            if (bae_h50tDelayTimer >= 0.35f)
            {
                // Own order, deliberately different from the primary
                // drone's fundamental so the two carriers don't beat
                // against each other or read as one fattened tone.
                float h50tTgt = Mathf.Max(bae3_motorHzSmooth, 30f) * 0.62f;
                bae_h50tHzSmooth += (h50tTgt - bae_h50tHzSmooth) * (h50tTgt > bae_h50tHzSmooth ? 0.012f : 0.006f);

                float h50tFadeIn = Mathf.Clamp01((bae_h50tDelayTimer - 0.35f) / 0.45f);
                float h50tTgtVol = (0.012f + motSpdFrac * 0.018f + ld * 0.012f) * h50tFadeIn * h50tFadeIn;
                bae_h50tVolSmooth += (h50tTgtVol - bae_h50tVolSmooth) * 0.008f;

                if (bae_h50tVolSmooth > 0.0006f && bae_h50tHzSmooth > 20f)
                {
                    double t1 = Math.Sin(2.0 * Math.PI * ph_bae_h50t1);
                    double t2 = Math.Sin(2.0 * Math.PI * ph_bae_h50t2) * 0.42;
                    double t3 = Math.Sin(2.0 * Math.PI * ph_bae_h50t3) * 0.18;
                    txSample += (t1 + t2 + t3) * bae_h50tVolSmooth * engMul * engVolPersonality;
                }
            }
            else
            {
                bae_h50tVolSmooth = 0f;
            }
            ph_bae_h50t1 = (ph_bae_h50t1 + (double)bae_h50tHzSmooth         * invSR) % 1.0;
            ph_bae_h50t2 = (ph_bae_h50t2 + (double)bae_h50tHzSmooth * 1.503 * invSR) % 1.0;
            ph_bae_h50t3 = (ph_bae_h50t3 + (double)bae_h50tHzSmooth * 2.51  * invSR) % 1.0;
        }

        // ── REGEN WHINE — motor flips to generating on lift-off/braking
        //    while still rolling. Same coast-regen condition already used
        //    elsewhere in this file (accel<0.01 && spd>6), strengthened by
        //    actual brake pedal position. Distinct descending voice, not
        //    just the steady drone fading out. ────────────────────────────
        {
            bool  baeCoastRegen  = accel < 0.01f && spd > 6f;
            float baeRegenTarget = baeCoastRegen ? Mathf.Clamp01(0.3f + bkPd * 0.7f) : 0f;
            bae_regenSmooth += (baeRegenTarget - bae_regenSmooth) * (float)invSR
                / (baeRegenTarget > bae_regenSmooth ? 0.15f : 0.6f);
            if (bae_regenSmooth > 0.001f)
            {
                float regenHz = 500f + motSpdFrac * 500f; // descends naturally as spd falls during the braking event
                double regen = Math.Sin(2.0 * Math.PI * ph_bae_regen);
                txSample += regen * bae_regenSmooth * (0.05f + motSpdFrac * 0.05f) * engMul * engVolPersonality;
                ph_bae_regen = (ph_bae_regen + regenHz * invSR) % 1.0;
            }
        }

        // ── APS (Accessory Power System) hum — BAE's APS replaces a
        //    conventional alternator, stepping HV battery power down to
        //    run 28V accessories; it hums continuously whenever the bus is
        //    powered, independent of genset run/stop state entirely. ──────
        {
            const double apsHz = 118.0;
            float apsVol = 0.006f * engMul;
            txSample += Math.Sin(2.0 * Math.PI * ph_bae_aps) * apsVol
                      + Math.Sin(2.0 * Math.PI * ph_bae_aps * 2.0) * apsVol * 0.3;
            ph_bae_aps = (ph_bae_aps + apsHz * invSR) % 1.0;
        }

        // ── Inverter/traction-motor cooling fan — spools in only after
        //    sustained stress (thermal proxy) and fades back out once load
        //    eases; reuses bae_fanVolSmooth/ph_bae_fan1/2, fields that were
        //    declared for exactly this in an earlier pass but never wired
        //    up. ──────────────────────────────────────────────────────────
        {
            bae_fanVolSmooth += (bae_stress - bae_fanVolSmooth) * (float)invSR / (bae_stress > bae_fanVolSmooth ? 4.5f : 6f);
            if (bae_fanVolSmooth > 0.01f)
            {
                double fanHz = 70.0 + bae_fanVolSmooth * 40.0;
                double fan = Math.Sin(2.0 * Math.PI * ph_bae_fan1) * 0.6 + Math.Sin(2.0 * Math.PI * ph_bae_fan2 * 1.97) * 0.4;
                txSample += fan * bae_fanVolSmooth * 0.03 * engMul + noiseHi * bae_fanVolSmooth * 0.015 * engMul;
                ph_bae_fan1 = (ph_bae_fan1 + fanHz        * invSR) % 1.0;
                ph_bae_fan2 = (ph_bae_fan2 + fanHz * 1.97 * invSR) % 1.0;
            }
        }

        // ── Deep load moan — low-end drivetrain/motor-mount groan tracking
        //    a slower load average; reuses bae_loadAvgSmooth/ph_bae_moan,
        //    also previously declared and never wired up. ─────────────────
        {
            bae_loadAvgSmooth += (ld - bae_loadAvgSmooth) * (float)invSR / (ld > bae_loadAvgSmooth ? 1.5f : 3f);
            if (bae_loadAvgSmooth > 0.05f)
            {
                double moanHz = 30.0 + bae_loadAvgSmooth * 14.0;
                txSample += Math.Sin(2.0 * Math.PI * ph_bae_moan) * bae_loadAvgSmooth * 0.05 * engMul * engVolPersonality;
                ph_bae_moan = (ph_bae_moan + moanHz * invSR) % 1.0;
            }
        }


        // ── [REMOVED] DIWA opt1_4-style hiss layer and the companion
        //    inverted whine were pulled out entirely per request -- neither
        //    is called/added to txSample anymore. Fields are left declared
        //    (harmless, unused) in case this comes back later.

        // ── [REMOVED — v3] The old "futuristic" hiMotorVol whine and the
        //    old accel-chased genset RPM/core/panting blocks (bae3_rpm,
        //    bae3_wantedRpm, bae3_energyNeeded, the ISG whine, the panting
        //    layer) are gone. That's the exact bug this rebuild fixes --
        //    genset RPM chasing accel/pedal position 1:1 like a mechanical
        //    engine instead of being decoupled and speed-locked. Replaced
        //    by the GENSET RPM + UFO MOTOR VOICE block near the top of this
        //    function (bae_gensetRpm, driven purely by spd; ph_bae_ufo*).
        //    bae3_rpm/bae3_wantedRpm/etc fields are left declared (harmless,
        //    unused) rather than ripped out of the class wholesale.

        // ═════════════════════════════════════════════════════════════════
        //  PORTED FROM DoVoithDSP — generic mechanical/vibration character
        //  that isn't tied to the Voith transmission itself. Re-staged off
        //  spd/gensetRunning instead of gear (BAE has no gears): the
        //  "gear==1 launch" window becomes "genset not yet engaged"
        //  (spd < BAE_GENSET_ENGAGE_KPH), and rpm-progress terms use
        //  gensetProgress in place of Voith's (rpm-IDLE)/(GOV-IDLE). Grit/
        //  texture noise uses noise_lp (class field, low-passed/rumbly),
        //  same as Voith's originals -- NOT noiseHp (high-passed/hissy,
        //  wrong character for shake, only used for the genset core above).
        //  Dropped as genuinely transmission-specific with no BAE
        //  equivalent: the 633Hz fixed aux tone (always-on, doesn't belong
        //  on a genset that's silent below 10kph), DIWA whine (replaced
        //  above), converter churn, mechanical shift thuds, ANS clutch
        //  pulses, pump spin-down, the "brrringa" launch overtone, and the
        //  torque converter wail -- none of these correspond to anything a
        //  gearless series-hybrid genset does.
        // ═════════════════════════════════════════════════════════════════
        if (!bae_shakerInit)
        {
            bae_shakerInit = true;
            bae_isShaker = (NextNoiseSample() * 0.5 + 0.5) < 0.18;
        }

        // ── Stopped-idle body shake — was Voith's "trueStop = gear<=1 &&
        //    spd<2.2 && accel<0.08"; BAE has no gear<=1, so gated purely on
        //    being genuinely stopped/idling instead. ─────────────────────
        {
            bool trueStop = spd < 2.2f && accel < 0.08f;
            float stopAmount = Mathf.Clamp01(1f - spd / 2.2f);
            float shakeTarget = trueStop ? stopAmount : 0f;

            bae_idleShakeEnv += (shakeTarget - bae_idleShakeEnv) * (shakeTarget > bae_idleShakeEnv ? 0.006f : 0.015f);

            if (bae_idleShakeEnv > 0.01f)
            {
                double sway = Math.Sin(2.0 * Math.PI * ph_bae_am1);
                double vibration = Math.Sin(2.0 * Math.PI * ph_bae_idleVib);
                double shakeSound = sway * 0.8 + vibration * 0.65 + noise_lp * 0.35;
                shakeSound = Math.Tanh(shakeSound * 2.8);
                txSample += shakeSound * bae_idleShakeEnv * 0.13 * engMul;
            }

            float idleVibHz = Mathf.Lerp(18f, 55f, Mathf.Clamp01(1f - spd / 2f));
            ph_bae_idleVib = (ph_bae_idleVib + idleVibHz * invSR) % 1.0;
            ph_bae_am1     = (ph_bae_am1     + 2.4        * invSR) % 1.0;
        }

        // ── Genset RPM-load vibration — was Voith's "RPM LOAD ENGINE
        //    VIBRATION" (section 6), rpmProgress swapped in for
        //    (rpm-IDLE)/(GOV-IDLE), gated on the genset actually running
        //    instead of gear==1. ───────────────────────────────────────
        {
            float rpmVibTargetHz = Mathf.Lerp(4f, 75f, Mathf.Pow(gensetProgress, 3f));
            bae_rpmVibHz += (rpmVibTargetHz - bae_rpmVibHz) * 0.02f;

            float targetVol = gensetRunning ? Mathf.Pow(gensetProgress, 4f) * (0.18f + ld * 0.25f) : 0f;
            bae_rpmVibVol += (targetVol - bae_rpmVibVol) * 0.02f;

            if (bae_rpmVibVol > 0.001f)
            {
                double vib = Math.Sin(2.0 * Math.PI * ph_bae_rpmVib);
                double grit = noise_lp * gensetProgress * 0.7;
                double brrr = vib * 0.8 + grit;
                brrr = Math.Tanh(brrr * (1.0 + gensetProgress * 4));
                txSample += brrr * bae_rpmVibVol * engMul;
            }
            ph_bae_rpmVib = (ph_bae_rpmVib + bae_rpmVibHz * invSR) % 1.0;
        }

        // ── Move-off transition pull grab — identical to Voith's section 8,
        //    no gear dependency in the original, ported verbatim. ────────
        {
            bool stoppedNow = spd < 0.4f;
            if (bae_wasStopped && !stoppedNow && accel > 0.05f) bae_moveOffTimer = 0.50f;
            bae_wasStopped = stoppedNow;

            if (bae_moveOffTimer > 0f)
            {
                bae_moveOffTimer -= (float)invSR;
                float t = Mathf.Clamp01(bae_moveOffTimer / 0.50f);
                float grabHz = 46f + (1f - t) * 18f;
                float judder = 0.5f + 0.5f * Mathf.Sin((float)audioClock * Mathf.PI * 2f * 10.5f);
                double grab = (Math.Sin(2.0 * Math.PI * ph_bae_grab) * 0.8 + noise_lp * 0.4)
                               * t * (0.08f + judder * 0.05f) * (0.5f + ld * 0.5f) * engMul;
                txSample += grab;
                ph_bae_grab = (ph_bae_grab + grabHz * invSR) % 1.0;
            }
        }

        // ── Shaker chassis mechanics — identical to Voith's section 9,
        //    per-instance random rattle, unrelated to engine type, ported
        //    verbatim. ─────────────────────────────────────────────────
        if (bae_isShaker)
        {
            float shakeLoad = Mathf.Clamp01(accel * 1.2f) * Mathf.Clamp01(1f - spd / 45f) + (spd < 1f ? 0.22f : 0f);
            if (shakeLoad > 0.02f)
            {
                double bodyv   = Math.Sin(2.0 * Math.PI * (ph_bae_shakeBody * 0.78));
                double tremorv = 0.55 + 0.45 * Math.Sin(2.0 * Math.PI * (ph_bae_shakeBody * 4.9));
                txSample += bodyv * tremorv * shakeLoad * 0.040 * engMul;
            }
            if (bae_creakVol < 0.01f && shakeLoad > 0.15f && (NextNoiseSample() * 0.5 + 0.5) > 0.99955)
            {
                bae_creakVol = 0.6f + (float)(NextNoiseSample() * 0.5 + 0.5) * 0.4f;
            }
            if (bae_creakVol > 0.003f)
            {
                double c1 = Math.Sin(2.0 * Math.PI * ph_bae_creak1);
                double c2 = Math.Sin(2.0 * Math.PI * ph_bae_creak2);
                txSample += Math.Tanh((c1 + c2 * 0.5) * 1.6) * bae_creakVol * 0.045 * engMul;
                double crHz = 320.0 + (1.0 - bae_creakVol) * 260.0;
                ph_bae_creak1 = (ph_bae_creak1 + crHz        * invSR) % 1.0;
                ph_bae_creak2 = (ph_bae_creak2 + crHz * 1.48 * invSR) % 1.0;
                bae_creakVol *= 0.9975f;
                if (bae_creakVol < 0.004f) bae_creakVol = 0f;
            }
            ph_bae_shakeBody = (ph_bae_shakeBody + 34.0 * invSR) % 1.0;
        }

        // ── Faint oil/coolant pump tick at low speed — ported verbatim. ──
        if (spd < 3f)
        {
            float pumpVol = 0.004f * Mathf.Clamp01(1f - spd / 3f) * engMul;
            txSample += Math.Sin(2.0 * Math.PI * ph_bae_pump) * pumpVol;
            // [FIX] Was pitched off bae_gensetRpm, which is genuinely 0 at
            // idle (unlike Voith's real engine, which always idles at a
            // nonzero rpm) -- flooring that to 1 gave a ~0.15Hz phase
            // increment, i.e. a near-DC sine wobbling in txSample with a
            // ~6.7s period. That's the "worse at 0kph" throb. Auxiliary
            // pump tick doesn't need to track genset rpm at all -- fixed
            // rate instead, same register the old rpm-driven version sat
            // in during normal operation.
            ph_bae_pump = (ph_bae_pump + 95.0 * invSR) % 1.0;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BAE HYBRIDRIVE HDS 300  (tx == "hds300")  —  FRESH, same architecture
    //  as DoBAEDSP above, own h3b_ state so the two never collide (a single
    //  bus instance is only ever "bae" or "hds300", but keeping them
    //  independent avoids any cross-talk if that ever changes). Bigger-motor
    //  artic sibling: deeper/louder traction, higher RPM ceiling, louder
    //  overall than HDS200 — real distinguishing trait carried over from
    //  every previous HDS300 pass.
    // ═══════════════════════════════════════════════════════════════════════
    private float  h3b_motorHzSmooth = 0f;
    private float  h3b_droneGainSmooth = 0f;
    private double ph_h3b_mot1, ph_h3b_mot2;
    private double h3b_droneLP = 0.0;
    private float  h3b_hiMotorHzSmooth = 0f;
    private double ph_h3b_hiMot1, ph_h3b_hiMot2, ph_h3b_hiMot3;
    private float  h3b_hissEnvSmooth = 0f;
    private double h3b_hissLP, h3b_hissPrev;
    private double ph_h3b_hiss;
    private float  h3b_invWhineHzSmooth = 0f;
    private float  h3b_invWhineAttackT = 0f;
    private double ph_h3b_invWhine;
    private float  h3b_wantedRpm  = 0f;
    private float  h3b_rpm        = 0f;
    public  float  HDS300_RPM_RISE_RATE = 950f;
    public  float  HDS300_RPM_CUT_RATE  = 2700f;
    public  float  HDS300_IDLE_RPM      = 700f;
    public  float  HDS300_MAX_RPM       = 2850f; // taller than HDS200's 2650
    public  float  HDS300_ENGAGE_KPH    = 10f;
    private float  h3b_energyNeeded = 0f;
    public  float  HDS300_ENERGY_BUILD_RATE = 0.35f;
    public  float  HDS300_ENERGY_DECAY_RATE = 0.55f;
    public  float  HDS300_ENERGY_KICKDOWN_ADD = 0.45f;
    public  float  HDS300_ENERGY_RPM_SPAN     = 600f; // taller than HDS200's, matches HDS300_MAX_RPM headroom
    private float  h3b_pantHz  = 0f;
    private float  h3b_pantVol = 0f;
    private double ph_h3b_pant;
    private double ph_h3b_isg1, ph_h3b_isg2;
    private float  h3b_engineVolMul = 0f;

    // ── HDS300 v2 — GENSET REBUILD (speed-locked RPM, UFO motor voice) ──
    // Same fix as HDS200's DoBAEDSP: genset RPM decoupled from accel,
    // locked to spd instead. Taller ceiling than HDS200 (bigger unit).
    public  float HDS300_GENSET_ENGAGE_KPH   = 10f;
    public  float HDS300_GENSET_MAX_RPM      = 1900f;
    public  float HDS300_GENSET_RISE_TAU     = 8f;
    public  float HDS300_GENSET_RPM_RISE_RATE = 950f;
    public  float HDS300_GENSET_RPM_FALL_RATE = 550f;
    private float  h3v_gensetRpm = 0f;
    public  float HDS300_UFO_HZ_BASE = 950f;
    public  float HDS300_UFO_HZ_SPAN = 1000f;
    private float  h3v_ufoHzSmooth = 0f;
    private double ph_h3v_ufo1, ph_h3v_ufo2, ph_h3v_ufo3;
    private double ph_h3v_core1, ph_h3v_core2;
    private bool   h3v_isShaker = false, h3v_shakerInit = false;
    private bool   h3v_wasStopped = false;
    private float  h3v_moveOffTimer = 0f;
    private double ph_h3v_grab;
    private float  h3v_idleShakeEnv = 0f;
    private double ph_h3v_idleVib, ph_h3v_am1;
    private float  h3v_rpmVibHz = 0f, h3v_rpmVibVol = 0f;
    private double ph_h3v_rpmVib;
    private float  h3v_creakVol = 0f;
    private double ph_h3v_creak1, ph_h3v_creak2, ph_h3v_shakeBody;
    private double ph_h3v_pump;

    // ── HDS300 demand/tier genset state — same architecture as BAE's
    //    bae_demand*/bae_gensetTier*, but its own instance state and its
    //    own (slower, deeper) response constants: bigger unit, slower
    //    governor response, holds load longer before tiering down. ────────
    public  float HDS300_DEMAND_ATTACK  = 0.045f; // slower than BAE's 0.06 -- lazier response
    public  float HDS300_DEMAND_RELEASE = 0.006f; // slower to release load too
    public  float[] HDS300_TIER_UP   = new float[] { 0.30f, 0.64f };
    public  float[] HDS300_TIER_DOWN = new float[] { 0.16f, 0.46f };
    public  float HDS300_TIER_SUSTAIN_TIME = 1.0f;  // holds tiers slightly longer than HDS200
    public  float HDS300_TIER_DWELL_TIME   = 3.0f;
    public  float HDS300_RPM_IDLE_FRAC   = 0.13f;
    public  float HDS300_RPM_CRUISE_FRAC = 0.40f;
    public  float HDS300_RPM_MAX_FRAC    = 0.80f;
    private float h3v_demandRaw, h3v_demandSmooth;
    private int   h3v_gensetTier, h3v_tierPending;
    private float h3v_tierSustainTimer, h3v_tierDwellTimer, h3v_tierClunkVol;
    private double ph_h3v_tierClunk1, ph_h3v_tierClunk2;
    private float h3v_idleStopTimer, h3v_idleStopThreshold;
    private double ph_h3v_inv;

    // ── HDS300 — same load-stress/windup/regen additions as HDS200, tuned
    //    deeper/slower (bigger unit, lazier response) and reusing the
    //    h3b_hiMotorHzSmooth/hissEnvSmooth/invWhineHzSmooth/pantHz/pantVol/
    //    ph_h3b_isg1/isg2 fields that were declared for exactly this kind
    //    of layer in an earlier pass but never actually wired up. ─────────
    public  float  HDS300_STRESS_ATTACK_TAU  = 6.5f; // slower to build than HDS200's 5.0
    public  float  HDS300_STRESS_RELEASE_TAU = 3.0f;
    private float  h3b_stress = 0f;
    private float  h3b_regenSmooth = 0f;
    private double ph_h3b_aps;

    // [ADD] H50EP whine transplant state for HDS300 -- own instance,
    // deeper/slower than HDS200's bae_h50t* fields. See DoHDS300DSP.
    private float  h3b_h50tDelayTimer = 0f;
    private float  h3b_h50tHzSmooth   = 0f;
    private float  h3b_h50tVolSmooth  = 0f;
    private double ph_h3b_h50t1, ph_h3b_h50t2, ph_h3b_h50t3;

    private void DoHDS300DSP(ref double txSample, ref double engineSample,
                              float rn, float ld, float engMul, float engVolPersonality,
                              double noiseHp, double noiseHi, double invSR)
    {
        // [ADD] Startup windup -- see DoHybridStartupWindup in
        // BusAudioEngine.StartupSequence.cs. Deeper/slower than HDS200's,
        // same relationship the rest of this function already has to
        // DoBAEDSP (bigger motor, same architecture).
        DoHybridStartupWindup(ref txSample, engineState == EngineRunState.Running, 230f, 1320f, engMul, invSR);

        const float HDS300_MASTER_VOL = 3.3f; // [FIX] louder again, 2.5 -> 3.3, still ahead of HDS200's 2.8

        // [FIX] Same fix as HDS200: old 0.0035/0.006 coefficients were
        // applied PER SAMPLE (unscaled by invSR), converging in ~6ms --
        // effectively instant despite the "slower torque build" comment.
        // Motor pitch itself stays quick (real PM motors respond fast);
        // the actual slow windup character now lives in h3b_stress below.
        float h3bMotTarget = spd <= 10f
            ? (spd / 10f) * 380f
            : 380f + (spd - 10f) * 2.7f;
        if (h3bMotTarget < 0.3f) h3bMotTarget = 0f;
        h3b_motorHzSmooth += (h3bMotTarget - h3b_motorHzSmooth) * (float)invSR
            / (h3bMotTarget > h3b_motorHzSmooth ? 0.28f : 0.45f); // a touch lazier than HDS200 -- bigger unit

        float h3bDroneGainTarget = 1.0f;
        h3b_droneGainSmooth += (h3bDroneGainTarget - h3b_droneGainSmooth) * (float)invSR / 1.8f;

        float h3bMotSpdFrac = Mathf.Clamp01(h3b_motorHzSmooth / 800f);

        // ── LOAD STRESS — slower to build and slower to release than
        //    HDS200's, per spec ("heavier load sound, slower governor
        //    response" for the bigger artic unit). ──────────────────────
        float h3b_stressTarget = Mathf.Clamp01(accel);
        h3b_stress += (h3b_stressTarget - h3b_stress) * (float)invSR
            / (h3b_stressTarget > h3b_stress ? HDS300_STRESS_ATTACK_TAU : HDS300_STRESS_RELEASE_TAU);

        float h3bContinuousMotorVol = h3bMotSpdFrac > 0.001f
            ? (0.062f + h3bMotSpdFrac * h3bMotSpdFrac * 0.145f + ld * 0.072f + h3b_stress * 0.06f) * engMul * (1f + ld * 0.44f) * h3b_droneGainSmooth * HDS300_MASTER_VOL * 0.2f
            : 0f;
        if (h3bContinuousMotorVol > 0.0005f)
        {
            double h3bDroneRaw = Math.Sin(2.0 * Math.PI * ph_h3b_mot1 * 0.85) * 1.0
                                + Math.Sin(2.0 * Math.PI * ph_h3b_mot2 * 0.85 * 1.998 / 2.0) * 0.22
                                + Math.Sin(2.0 * Math.PI * ph_h3b_mot1 * 0.85 * 3.0) * 0.16 * h3b_stress; // deeper/heavier 3rd than HDS200
            h3bDroneRaw = Math.Tanh(h3bDroneRaw * (1.0 + h3b_stress * 1.7)) * 0.9; // saturates harder -- "heavier load sound"
            h3b_droneLP += (h3bDroneRaw - h3b_droneLP) * 0.32;
            txSample += h3b_droneLP * h3bContinuousMotorVol
                      + noiseHi * h3bContinuousMotorVol * 0.11 * h3b_stress;
        }
        ph_h3b_mot1 = (ph_h3b_mot1 + h3b_motorHzSmooth       * invSR) % 1.0;
        ph_h3b_mot2 = (ph_h3b_mot2 + h3b_motorHzSmooth * 2.0 * invSR) % 1.0;

        // ── TORQUE WINDUP WHINE — deeper register, slower bloom than
        //    HDS200's, reusing h3b_hiMotorHzSmooth/ph_h3b_hiMot1-3
        //    (declared in an earlier pass, never wired up until now). ────
        {
            float h3bWindupHzTarget = 620f + h3b_stress * 780f + h3bMotSpdFrac * 180f;
            h3b_hiMotorHzSmooth += (h3bWindupHzTarget - h3b_hiMotorHzSmooth) * (float)invSR / 1.2f;
            double h3bWindup = Math.Sin(2.0 * Math.PI * ph_h3b_hiMot1) * 0.65
                              + Math.Sin(2.0 * Math.PI * ph_h3b_hiMot2 * 1.503) * 0.35;
            float h3bWindupVol = h3b_stress * h3b_stress * (0.06f + ld * 0.035f) * engMul * engVolPersonality;
            if (h3bWindupVol > 0.0003f) txSample += h3bWindup * h3bWindupVol;
            ph_h3b_hiMot1 = (ph_h3b_hiMot1 + h3b_hiMotorHzSmooth        * invSR) % 1.0;
            ph_h3b_hiMot2 = (ph_h3b_hiMot2 + h3b_hiMotorHzSmooth * 1.503 * invSR) % 1.0;
        }

        // ═════════════════════════════════════════════════════════════════
        //  [ADD] H50EP WHINE TRANSPLANT — same layer just ported into
        //  HDS200's DoBAEDSP, own h3b_h50t state so the two never collide.
        //  Deeper register and a slower fade-in than HDS200's, matching
        //  this file's own established "bigger unit, lazier" pattern.
        //  Tracks h3b_motorHzSmooth (the traction motor), NOT the genset --
        //  same reasoning as HDS200's: the motor pulls away before the
        //  genset ever spools up, so this has to work with or without it
        //  running.
        // ═════════════════════════════════════════════════════════════════
        {
            if (h3bMotSpdFrac > 0.001f) { if (h3b_h50tDelayTimer < 0.45f) h3b_h50tDelayTimer += (float)invSR; } // slower than HDS200's 0.35s
            else                          h3b_h50tDelayTimer = 0f;

            if (h3b_h50tDelayTimer >= 0.45f)
            {
                float h3bH50tTgt = Mathf.Max(h3b_motorHzSmooth, 25f) * 0.52f; // lower ratio than HDS200's 0.62 -- deeper register
                h3b_h50tHzSmooth += (h3bH50tTgt - h3b_h50tHzSmooth) * (h3bH50tTgt > h3b_h50tHzSmooth ? 0.010f : 0.005f);

                float h3bH50tFadeIn = Mathf.Clamp01((h3b_h50tDelayTimer - 0.45f) / 0.60f); // slower bloom than HDS200's 0.45s
                float h3bH50tTgtVol = (0.016f + h3bMotSpdFrac * 0.022f + ld * 0.014f) * h3bH50tFadeIn * h3bH50tFadeIn; // a bit louder than HDS200's
                h3b_h50tVolSmooth += (h3bH50tTgtVol - h3b_h50tVolSmooth) * 0.007f;

                if (h3b_h50tVolSmooth > 0.0006f && h3b_h50tHzSmooth > 18f)
                {
                    double t1 = Math.Sin(2.0 * Math.PI * ph_h3b_h50t1);
                    double t2 = Math.Sin(2.0 * Math.PI * ph_h3b_h50t2) * 0.42;
                    double t3 = Math.Sin(2.0 * Math.PI * ph_h3b_h50t3) * 0.20; // slightly stronger 3rd than HDS200 -- "heavier" per spec
                    txSample += (t1 + t2 + t3) * h3b_h50tVolSmooth * engMul * engVolPersonality;
                }
            }
            else
            {
                h3b_h50tVolSmooth = 0f;
            }
            ph_h3b_h50t1 = (ph_h3b_h50t1 + (double)h3b_h50tHzSmooth         * invSR) % 1.0;
            ph_h3b_h50t2 = (ph_h3b_h50t2 + (double)h3b_h50tHzSmooth * 1.503 * invSR) % 1.0;
            ph_h3b_h50t3 = (ph_h3b_h50t3 + (double)h3b_h50tHzSmooth * 2.51  * invSR) % 1.0;
        }

        // ── REGEN WHINE — reuses ph_h3b_invWhine/h3b_invWhineHzSmooth
        //    (declared, never wired). Louder/deeper than HDS200's. ───────
        {
            bool  h3bCoastRegen  = accel < 0.01f && spd > 6f;
            float h3bRegenTarget = h3bCoastRegen ? Mathf.Clamp01(0.3f + bkPd * 0.7f) : 0f;
            h3b_regenSmooth += (h3bRegenTarget - h3b_regenSmooth) * (float)invSR
                / (h3bRegenTarget > h3b_regenSmooth ? 0.18f : 0.7f);
            if (h3b_regenSmooth > 0.001f)
            {
                h3b_invWhineHzSmooth = 440f + h3bMotSpdFrac * 460f;
                double h3bRegen = Math.Sin(2.0 * Math.PI * ph_h3b_invWhine);
                txSample += h3bRegen * h3b_regenSmooth * (0.06f + h3bMotSpdFrac * 0.06f) * engMul * engVolPersonality;
                ph_h3b_invWhine = (ph_h3b_invWhine + h3b_invWhineHzSmooth * invSR) % 1.0;
            }
        }

        // ── APS hum — same rationale as HDS200's, own phase. ─────────────
        {
            const double h3bApsHz = 118.0;
            float h3bApsVol = 0.007f * engMul;
            txSample += Math.Sin(2.0 * Math.PI * ph_h3b_aps) * h3bApsVol
                      + Math.Sin(2.0 * Math.PI * ph_h3b_aps * 2.0) * h3bApsVol * 0.3;
            ph_h3b_aps = (ph_h3b_aps + h3bApsHz * invSR) % 1.0;
        }

        // ── Cooling fan — reuses h3b_hissEnvSmooth/ph_h3b_hiss (declared,
        //    never wired). Bigger unit: slower spool, slightly louder. ───
        {
            h3b_hissEnvSmooth += (h3b_stress - h3b_hissEnvSmooth) * (float)invSR / (h3b_stress > h3b_hissEnvSmooth ? 5.5f : 7f);
            if (h3b_hissEnvSmooth > 0.01f)
            {
                double h3bFanHz = 62.0 + h3b_hissEnvSmooth * 36.0;
                double h3bFan = Math.Sin(2.0 * Math.PI * ph_h3b_hiss) * 0.6 + Math.Sin(2.0 * Math.PI * ph_h3b_hiss * 1.97) * 0.4;
                txSample += h3bFan * h3b_hissEnvSmooth * 0.035 * engMul + noiseHi * h3b_hissEnvSmooth * 0.018 * engMul;
                ph_h3b_hiss = (ph_h3b_hiss + h3bFanHz * invSR) % 1.0;
            }
        }

        // ── Deep load moan — reuses h3b_pantHz/pantVol/ph_h3b_pant
        //    (declared, never wired). ───────────────────────────────────
        {
            h3b_pantVol += (ld - h3b_pantVol) * (float)invSR / (ld > h3b_pantVol ? 1.8f : 3.5f);
            if (h3b_pantVol > 0.05f)
            {
                h3b_pantHz = 26f + h3b_pantVol * 15f;
                txSample += Math.Sin(2.0 * Math.PI * ph_h3b_pant) * h3b_pantVol * 0.06 * engMul * engVolPersonality;
                ph_h3b_pant = (ph_h3b_pant + h3b_pantHz * invSR) % 1.0;
            }
        }

        // ── [REMOVED] DIWA opt1_4-style hiss layer and companion inverted
        //    whine pulled out per request -- fields left declared, unused.

        // ── [REMOVED — v2] Old accel-chased whine (h3bHiTarget/h3b_wantedRpm/
        //    h3b_rpm/h3b_energyNeeded/ISG whine/panting) is gone -- same bug
        //    as HDS200's original DoBAEDSP: genset RPM tracking the pedal
        //    1:1 instead of being decoupled and speed-locked. Replaced by
        //    the GENSET RPM + UFO MOTOR VOICE block below.

        // ═════════════════════════════════════════════════════════════════
        //  GENSET — demand/tier driven, same fix as HDS200's DoBAEDSP
        //  above, own h3v_ state. Deliberately slower/deeper than HDS200:
        //  lazier attack/release on demand and longer tier sustain/dwell,
        //  so the bigger unit reads as slower to spool and slower to back
        //  off -- "heavier load sound, slower governor response" per spec.
        // ═════════════════════════════════════════════════════════════════
        h3v_demandRaw = Mathf.Clamp01(
            (ld * BAE_DEMAND_W_LOAD + Mathf.Clamp01(spd / MAX_SPD) * BAE_DEMAND_W_SPEED)
            / Mathf.Max(0.01f, BAE_DEMAND_W_LOAD + BAE_DEMAND_W_SPEED));
        h3v_demandSmooth += (h3v_demandRaw - h3v_demandSmooth)
            * (h3v_demandRaw > h3v_demandSmooth ? HDS300_DEMAND_ATTACK : HDS300_DEMAND_RELEASE);

        int h3vWantTier = h3v_demandSmooth >= HDS300_TIER_UP[1] ? 2
                        : h3v_demandSmooth >= HDS300_TIER_UP[0] ? 1
                        : h3v_demandSmooth < HDS300_TIER_DOWN[0] ? 0
                        : h3v_gensetTier;
        if (h3v_gensetTier == 2 && h3v_demandSmooth < HDS300_TIER_DOWN[1]) h3vWantTier = 1;

        if (h3vWantTier != h3v_gensetTier)
        {
            if (h3vWantTier == h3v_tierPending) h3v_tierSustainTimer += (float)invSR;
            else { h3v_tierPending = h3vWantTier; h3v_tierSustainTimer = 0f; }

            if (h3v_tierDwellTimer <= 0f && h3v_tierSustainTimer >= HDS300_TIER_SUSTAIN_TIME)
            {
                h3v_gensetTier = h3vWantTier;
                h3v_tierDwellTimer = HDS300_TIER_DWELL_TIME;
                h3v_tierClunkVol = 1f;
            }
        }
        else
        {
            h3v_tierPending = h3v_gensetTier;
            h3v_tierSustainTimer = 0f;
        }
        if (h3v_tierDwellTimer > 0f) h3v_tierDwellTimer -= (float)invSR;

        // [FIX] Same Start/Stop Drive fix as HDS200 above -- real 8mph
        // decel-cut / 10mph accel-restart hysteresis, own latch for HDS300.
        if (!h3v_startStopOff && spd < BAE_STARTSTOP_OFF_KPH && accel <= 0.02f) h3v_startStopOff = true;
        else if (h3v_startStopOff && spd > BAE_STARTSTOP_ON_KPH) h3v_startStopOff = false;
        bool h3vIdleStopped = h3v_startStopOff;

        float h3vTierFrac = h3v_gensetTier == 2 ? HDS300_RPM_MAX_FRAC
                           : h3v_gensetTier == 1 ? HDS300_RPM_CRUISE_FRAC
                           : HDS300_RPM_IDLE_FRAC;
        float h3vGensetRpmTarget = h3vIdleStopped ? 0f : h3vTierFrac * HDS300_GENSET_MAX_RPM;
        float h3vGensetRate = h3vGensetRpmTarget > h3v_gensetRpm ? HDS300_GENSET_RPM_RISE_RATE : HDS300_GENSET_RPM_FALL_RATE;
        h3v_gensetRpm = Mathf.MoveTowards(h3v_gensetRpm, h3vGensetRpmTarget, h3vGensetRate * (float)invSR);
        if (h3v_gensetRpm < 5f) h3v_gensetRpm = 0f;
        float h3vGensetProgress = Mathf.Clamp01(h3v_gensetRpm / HDS300_GENSET_MAX_RPM);
        bool  h3vGensetRunning  = h3v_gensetRpm > 5f;

        // NOTE: real diesel voice comes from the host engineType via
        // DoCombustionEngine -- no redundant combustion core synthesized
        // here anymore (same fix as HDS200).

        if (h3v_tierClunkVol > 0.01f)
        {
            double k1 = Math.Sin(2.0 * Math.PI * ph_h3v_tierClunk1);
            double k2 = Math.Sin(2.0 * Math.PI * ph_h3v_tierClunk2);
            txSample += Math.Tanh((k1 + k2 * 0.5) * 1.5) * h3v_tierClunkVol * 0.04 * engMul;
            ph_h3v_tierClunk1 = (ph_h3v_tierClunk1 + 34.0 * invSR) % 1.0;
            ph_h3v_tierClunk2 = (ph_h3v_tierClunk2 + 54.0 * invSR) % 1.0;
            h3v_tierClunkVol *= 0.985f;
        }

        // ═════════════════════════════════════════════════════════════════
        //  GENERATOR + INVERTER VOICE — taller/louder register than
        //  HDS200's, matching HDS300's bigger-motor character; same
        //  architecture as DoBAEDSP's replacement for the old UFO trio.
        // ═════════════════════════════════════════════════════════════════
        float h3vGeneratorHz = h3vGensetRunning ? (60f + h3v_gensetRpm * 0.20f) : 0f;
        float h3vGenVol = h3vGensetRunning
            ? (0.055f + h3vGensetProgress * 0.06f + h3v_demandSmooth * 0.06f) * engMul * engVolPersonality
            : 0f;
        if (h3vGenVol > 0.0003f)
        {
            double g1 = Math.Sin(2.0 * Math.PI * ph_h3v_ufo1);
            double g2 = Math.Sin(2.0 * Math.PI * ph_h3v_ufo2 * 2.0);
            txSample += (g1 * 0.7 + g2 * 0.3) * h3vGenVol
                      + noiseHi * h3vGenVol * 0.08 * h3v_demandSmooth;
        }
        ph_h3v_ufo1 = (ph_h3v_ufo1 + h3vGeneratorHz * invSR) % 1.0;
        ph_h3v_ufo2 = (ph_h3v_ufo2 + h3vGeneratorHz * invSR) % 1.0;

        float h3vInvHzTarget = h3vGensetRunning ? (950f + h3v_demandSmooth * 1000f) : 0f;
        h3v_ufoHzSmooth += (h3vInvHzTarget - h3v_ufoHzSmooth) * (h3vInvHzTarget > h3v_ufoHzSmooth ? 0.025f : 0.012f);
        float h3vInvVol = h3vGensetRunning
            ? (0.014f + h3v_demandSmooth * h3v_demandSmooth * 0.055f) * engMul * engVolPersonality
            : 0f;
        if (h3vInvVol > 0.0003f)
        {
            double iv = Math.Sin(2.0 * Math.PI * ph_h3v_inv) * 0.7
                      + Math.Sin(2.0 * Math.PI * ph_h3v_ufo3 * 2.998) * 0.3;
            txSample += iv * h3vInvVol;
        }
        ph_h3v_inv  = (ph_h3v_inv  + h3v_ufoHzSmooth        * invSR) % 1.0;
        ph_h3v_ufo3 = (ph_h3v_ufo3 + h3v_ufoHzSmooth * 1.503 * invSR) % 1.0;

        // ═════════════════════════════════════════════════════════════════
        //  PORTED FROM DoVoithDSP (same as HDS200) — re-staged off spd/
        //  h3vGensetRunning instead of gear. Grit uses noise_lp (class
        //  field), never the hissy noiseHp. Pump tick is a fixed rate --
        //  NOT tied to genset rpm, which is genuinely 0 at idle.
        // ═════════════════════════════════════════════════════════════════
        if (!h3v_shakerInit) { h3v_shakerInit = true; h3v_isShaker = (NextNoiseSample() * 0.5 + 0.5) < 0.18; }

        {
            bool trueStop = spd < 2.2f && accel < 0.08f;
            float stopAmount = Mathf.Clamp01(1f - spd / 2.2f);
            float shakeTarget = trueStop ? stopAmount : 0f;
            h3v_idleShakeEnv += (shakeTarget - h3v_idleShakeEnv) * (shakeTarget > h3v_idleShakeEnv ? 0.006f : 0.015f);
            if (h3v_idleShakeEnv > 0.01f)
            {
                double sway = Math.Sin(2.0 * Math.PI * ph_h3v_am1);
                double vibration = Math.Sin(2.0 * Math.PI * ph_h3v_idleVib);
                double shakeSound = sway * 0.8 + vibration * 0.65 + noise_lp * 0.35;
                shakeSound = Math.Tanh(shakeSound * 2.8);
                txSample += shakeSound * h3v_idleShakeEnv * 0.14 * engMul;
            }
            float idleVibHz = Mathf.Lerp(18f, 55f, Mathf.Clamp01(1f - spd / 2f));
            ph_h3v_idleVib = (ph_h3v_idleVib + idleVibHz * invSR) % 1.0;
            ph_h3v_am1     = (ph_h3v_am1     + 2.4        * invSR) % 1.0;
        }

        {
            float rpmVibTargetHz = Mathf.Lerp(4f, 75f, Mathf.Pow(h3vGensetProgress, 3f));
            h3v_rpmVibHz += (rpmVibTargetHz - h3v_rpmVibHz) * 0.02f;
            float targetVol = h3vGensetRunning ? Mathf.Pow(h3vGensetProgress, 4f) * (0.19f + ld * 0.26f) : 0f;
            h3v_rpmVibVol += (targetVol - h3v_rpmVibVol) * 0.02f;
            if (h3v_rpmVibVol > 0.001f)
            {
                double vib = Math.Sin(2.0 * Math.PI * ph_h3v_rpmVib);
                double grit = noise_lp * h3vGensetProgress * 0.7;
                double brrr = vib * 0.8 + grit;
                brrr = Math.Tanh(brrr * (1.0 + h3vGensetProgress * 4));
                txSample += brrr * h3v_rpmVibVol * engMul;
            }
            ph_h3v_rpmVib = (ph_h3v_rpmVib + h3v_rpmVibHz * invSR) % 1.0;
        }

        {
            bool stoppedNow = spd < 0.4f;
            if (h3v_wasStopped && !stoppedNow && accel > 0.05f) h3v_moveOffTimer = 0.50f;
            h3v_wasStopped = stoppedNow;
            if (h3v_moveOffTimer > 0f)
            {
                h3v_moveOffTimer -= (float)invSR;
                float t = Mathf.Clamp01(h3v_moveOffTimer / 0.50f);
                float grabHz = 46f + (1f - t) * 18f;
                float judder = 0.5f + 0.5f * Mathf.Sin((float)audioClock * Mathf.PI * 2f * 10.5f);
                double grab = (Math.Sin(2.0 * Math.PI * ph_h3v_grab) * 0.8 + noise_lp * 0.4)
                               * t * (0.09f + judder * 0.05f) * (0.5f + ld * 0.5f) * engMul;
                txSample += grab;
                ph_h3v_grab = (ph_h3v_grab + grabHz * invSR) % 1.0;
            }
        }

        if (h3v_isShaker)
        {
            float shakeLoad = Mathf.Clamp01(accel * 1.2f) * Mathf.Clamp01(1f - spd / 45f) + (spd < 1f ? 0.22f : 0f);
            if (shakeLoad > 0.02f)
            {
                double bodyv   = Math.Sin(2.0 * Math.PI * (ph_h3v_shakeBody * 0.78));
                double tremorv = 0.55 + 0.45 * Math.Sin(2.0 * Math.PI * (ph_h3v_shakeBody * 4.9));
                txSample += bodyv * tremorv * shakeLoad * 0.045 * engMul;
            }
            if (h3v_creakVol < 0.01f && shakeLoad > 0.15f && (NextNoiseSample() * 0.5 + 0.5) > 0.99955)
                h3v_creakVol = 0.6f + (float)(NextNoiseSample() * 0.5 + 0.5) * 0.4f;
            if (h3v_creakVol > 0.003f)
            {
                double c1 = Math.Sin(2.0 * Math.PI * ph_h3v_creak1);
                double c2 = Math.Sin(2.0 * Math.PI * ph_h3v_creak2);
                txSample += Math.Tanh((c1 + c2 * 0.5) * 1.6) * h3v_creakVol * 0.05 * engMul;
                double crHz = 320.0 + (1.0 - h3v_creakVol) * 260.0;
                ph_h3v_creak1 = (ph_h3v_creak1 + crHz        * invSR) % 1.0;
                ph_h3v_creak2 = (ph_h3v_creak2 + crHz * 1.48 * invSR) % 1.0;
                h3v_creakVol *= 0.9975f;
                if (h3v_creakVol < 0.004f) h3v_creakVol = 0f;
            }
            ph_h3v_shakeBody = (ph_h3v_shakeBody + 34.0 * invSR) % 1.0;
        }

        if (spd < 3f)
        {
            float pumpVol = 0.0045f * Mathf.Clamp01(1f - spd / 3f) * engMul;
            txSample += Math.Sin(2.0 * Math.PI * ph_h3v_pump) * pumpVol;
            ph_h3v_pump = (ph_h3v_pump + 95.0 * invSR) % 1.0; // fixed rate -- not genset-rpm-driven (see HDS200 fix)
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  BAE HYBRIDRIVE GEN3  (tx == "baegen3")  —  FRESH, same architecture,
    //  own g3b_ state. Real, confirmed next-gen successor per New Flyer's own
    //  Aug 2024 launch material: "smaller and lighter... fewer components...
    //  more efficiency," SiC power electronics, "reduced noise and
    //  vibrations." Modeled as a genuinely QUIETER, TIGHTER unit than
    //  HDS200/300 (not just a retune) — lower master volume, higher-pitched/
    //  thinner PWM-adjacent whine (SiC switches faster), and a quicker RPM
    //  response (lighter rotating assembly spins up/down faster).
    // ═══════════════════════════════════════════════════════════════════════
    private float  g3b_motorHzSmooth = 0f;
    private float  g3b_droneGainSmooth = 0f;
    private double ph_g3b_mot1, ph_g3b_mot2;
    private double g3b_droneLP = 0.0;
    private float  g3b_hiMotorHzSmooth = 0f;
    private double ph_g3b_hiMot1, ph_g3b_hiMot2, ph_g3b_hiMot3;
    private float  g3b_hissEnvSmooth = 0f;
    private double g3b_hissLP, g3b_hissPrev;
    private double ph_g3b_hiss;
    private float  g3b_invWhineHzSmooth = 0f;
    private float  g3b_invWhineAttackT = 0f;
    private double ph_g3b_invWhine;
    private float  g3b_wantedRpm  = 0f;
    private float  g3b_rpm        = 0f;
    public  float  BAEGEN3_RPM_RISE_RATE = 1150f; // faster than HDS200/300 -- lighter assembly
    public  float  BAEGEN3_RPM_CUT_RATE  = 3200f;
    public  float  BAEGEN3_IDLE_RPM      = 600f;
    public  float  BAEGEN3_MAX_RPM       = 2500f;
    public  float  BAEGEN3_ENGAGE_KPH    = 10f;
    private float  g3b_energyNeeded = 0f;
    public  float  BAEGEN3_ENERGY_BUILD_RATE = 0.40f; // lighter unit, builds/decays a touch quicker
    public  float  BAEGEN3_ENERGY_DECAY_RATE = 0.60f;
    public  float  BAEGEN3_ENERGY_KICKDOWN_ADD = 0.40f;
    public  float  BAEGEN3_ENERGY_RPM_SPAN     = 500f;
    private float  g3b_pantHz  = 0f;
    private float  g3b_pantVol = 0f;
    private double ph_g3b_pant;
    private double ph_g3b_isg1, ph_g3b_isg2;
    private float  g3b_engineVolMul = 0f;

    // ── Gen3 v2 — GENSET REBUILD (speed-locked RPM, UFO motor voice) ────
    // Same fix as HDS200/HDS300. Gen3 is the "quieter, tighter" unit --
    // lower genset ceiling, faster/lighter response, thinner higher-
    // pitched UFO register (SiC-tinged).
    public  float BAEGEN3_GENSET_ENGAGE_KPH   = 10f;
    public  float BAEGEN3_GENSET_MAX_RPM      = 1600f;
    public  float BAEGEN3_GENSET_RISE_TAU     = 7f;
    public  float BAEGEN3_GENSET_RPM_RISE_RATE = 1150f;
    public  float BAEGEN3_GENSET_RPM_FALL_RATE = 650f;
    private float  g3v_gensetRpm = 0f;
    public  float BAEGEN3_UFO_HZ_BASE = 1000f;
    public  float BAEGEN3_UFO_HZ_SPAN = 850f;
    private float  g3v_ufoHzSmooth = 0f;
    private double ph_g3v_ufo1, ph_g3v_ufo2, ph_g3v_ufo3;
    private double ph_g3v_core1, ph_g3v_core2;
    private bool   g3v_isShaker = false, g3v_shakerInit = false;
    private bool   g3v_wasStopped = false;
    private float  g3v_moveOffTimer = 0f;
    private double ph_g3v_grab;
    private float  g3v_idleShakeEnv = 0f;
    private double ph_g3v_idleVib, ph_g3v_am1;
    private float  g3v_rpmVibHz = 0f, g3v_rpmVibVol = 0f;
    private double ph_g3v_rpmVib;
    private float  g3v_creakVol = 0f;
    private double ph_g3v_creak1, ph_g3v_creak2, ph_g3v_shakeBody;
    private double ph_g3v_pump;

    private void DoBAEGen3DSP(ref double txSample, ref double engineSample,
                               float rn, float ld, float engMul, float engVolPersonality,
                               double noiseHp, double noiseHi, double invSR)
    {
        const float BAEGEN3_MASTER_VOL = 1.9f; // [FIX] louder again, 1.5 -> 1.9 -- stays the quietest of the three by design

        // [FIX] Same two-slope Hz curve as HDS200/300 -- fast 0-10kph, then
        // slow near-linear crawl (H50EP-style) past it.
        float g3bMotTarget = spd <= 10f
            ? (spd / 10f) * 320f
            : 320f + (spd - 10f) * 2.1f;
        if (g3bMotTarget < 0.3f) g3bMotTarget = 0f;
        g3b_motorHzSmooth += (g3bMotTarget - g3b_motorHzSmooth) * (g3bMotTarget > g3b_motorHzSmooth ? 0.0045f : 0.008f); // [FIX] slower torque build -- Voith-style lazy startup (still a touch quicker than HDS200/300, lighter unit)

        float g3bDroneGainTarget = 1.0f;
        g3b_droneGainSmooth += (g3bDroneGainTarget - g3b_droneGainSmooth) * (g3bDroneGainTarget > g3b_droneGainSmooth ? 0.18f : 0.06f);

        float g3bMotSpdFrac = Mathf.Clamp01(g3b_motorHzSmooth / 760f);
        float g3bContinuousMotorVol = g3bMotSpdFrac > 0.001f
            ? (0.040f + g3bMotSpdFrac * g3bMotSpdFrac * 0.095f + ld * 0.048f) * engMul * (1f + ld * 0.35f) * g3b_droneGainSmooth * BAEGEN3_MASTER_VOL * 0.2f // [FIX] motor dropped to 0.2x, permanently, every speed
            : 0f;
        if (g3bContinuousMotorVol > 0.0005f)
        {
            // [FIX] Same softening as HDS200/300.
            double g3bDroneRaw = Math.Sin(2.0 * Math.PI * ph_g3b_mot1 * 0.85) * g3bContinuousMotorVol
                                + Math.Sin(2.0 * Math.PI * ph_g3b_mot2 * 0.85 * 1.998 / 2.0) * g3bContinuousMotorVol * 0.22;
            g3b_droneLP += (g3bDroneRaw - g3b_droneLP) * 0.35;
            txSample += g3b_droneLP;
        }
        ph_g3b_mot1 = (ph_g3b_mot1 + g3b_motorHzSmooth       * invSR) % 1.0;
        ph_g3b_mot2 = (ph_g3b_mot2 + g3b_motorHzSmooth * 2.0 * invSR) % 1.0;

        // ── [REMOVED] DIWA opt1_4-style hiss layer and companion inverted
        //    whine pulled out per request -- fields left declared, unused.

        // ── [REMOVED — v2] Old accel-chased whine (g3bHiTarget/g3b_wantedRpm/
        //    g3b_rpm/g3b_energyNeeded/ISG whine/panting) is gone -- same bug
        //    as HDS200/HDS300. Replaced by the GENSET RPM + UFO MOTOR VOICE
        //    block below.

        // ═════════════════════════════════════════════════════════════════
        //  GENSET RPM — speed-locked, NOT accel-chased. Lower ceiling,
        //  faster response than HDS200/300 -- lighter, tighter unit.
        // ═════════════════════════════════════════════════════════════════
        float g3vGensetRpmTarget = spd < BAEGEN3_GENSET_ENGAGE_KPH
            ? 0f
            : BAEGEN3_GENSET_MAX_RPM * (1f - Mathf.Exp(-(spd - BAEGEN3_GENSET_ENGAGE_KPH) / BAEGEN3_GENSET_RISE_TAU));
        float g3vGensetRate = g3vGensetRpmTarget > g3v_gensetRpm ? BAEGEN3_GENSET_RPM_RISE_RATE : BAEGEN3_GENSET_RPM_FALL_RATE;
        g3v_gensetRpm = Mathf.MoveTowards(g3v_gensetRpm, g3vGensetRpmTarget, g3vGensetRate * (float)invSR);
        if (g3v_gensetRpm < 5f) g3v_gensetRpm = 0f;
        float g3vGensetProgress = Mathf.Clamp01(g3v_gensetRpm / BAEGEN3_GENSET_MAX_RPM);
        bool  g3vGensetRunning  = g3v_gensetRpm > 5f;

        if (g3vGensetRunning)
        {
            float g3vCoreEngMul = engMul * engVolPersonality * BAEGEN3_MASTER_VOL * 0.5f;
            double g3vCoreHz = g3v_gensetRpm / 60.0 * 3.0;
            double g3vCore = Math.Sin(2.0 * Math.PI * ph_g3v_core1) * 0.7
                            + Math.Sin(2.0 * Math.PI * ph_g3v_core2 * 2.0) * 0.3;
            g3vCore = Math.Tanh(g3vCore * 1.2) * 0.85; // softer drive -- tighter, more refined unit
            engineSample += g3vCore * (0.075f + g3vGensetProgress * 0.075f) * g3vCoreEngMul
                          + noiseHp * g3vGensetProgress * 0.010f * g3vCoreEngMul;
            ph_g3v_core1 = (ph_g3v_core1 + g3vCoreHz       * invSR) % 1.0;
            ph_g3v_core2 = (ph_g3v_core2 + g3vCoreHz * 0.5 * invSR) % 1.0;
        }

        // ═════════════════════════════════════════════════════════════════
        //  UFO MOTOR VOICE — replaces the old whine entirely. Higher-pitched,
        //  thinner register than HDS200/300's (SiC-tinged, matches the
        //  "reduced noise and vibrations" quieter-unit character).
        // ═════════════════════════════════════════════════════════════════
        float g3vUfoHzTarget = g3vGensetRunning ? (BAEGEN3_UFO_HZ_BASE + g3vGensetProgress * BAEGEN3_UFO_HZ_SPAN) : 0f;
        g3v_ufoHzSmooth += (g3vUfoHzTarget - g3v_ufoHzSmooth) * (g3vUfoHzTarget > g3v_ufoHzSmooth ? 0.024f : 0.012f); // quicker response -- lighter unit
        float g3vUfoVol = g3vGensetRunning
            ? (0.04f + g3vGensetProgress * g3vGensetProgress * 0.12f + ld * 0.045f) * engMul * engVolPersonality
            : 0f;
        if (g3vUfoVol > 0.0005f)
        {
            double u1 = Math.Sin(2.0 * Math.PI * ph_g3v_ufo1);
            double u2 = Math.Sin(2.0 * Math.PI * ph_g3v_ufo2);
            double u3 = Math.Sin(2.0 * Math.PI * ph_g3v_ufo3);
            txSample += (u1 * 0.55 + u2 * 0.55 + u3 * 0.20) * g3vUfoVol;
        }
        ph_g3v_ufo1 = (ph_g3v_ufo1 + g3v_ufoHzSmooth          * invSR) % 1.0;
        ph_g3v_ufo2 = (ph_g3v_ufo2 + g3v_ufoHzSmooth * 1.0037 * invSR) % 1.0;
        ph_g3v_ufo3 = (ph_g3v_ufo3 + g3v_ufoHzSmooth * 2.998  * invSR) % 1.0;

        // ═════════════════════════════════════════════════════════════════
        //  PORTED FROM DoVoithDSP (same as HDS200/300) — re-staged off spd/
        //  g3vGensetRunning instead of gear, thinner/quieter levels to
        //  match Gen3's "reduced noise and vibrations" character. Grit uses
        //  noise_lp, never the hissy noiseHp. Pump tick is a fixed rate.
        // ═════════════════════════════════════════════════════════════════
        if (!g3v_shakerInit) { g3v_shakerInit = true; g3v_isShaker = (NextNoiseSample() * 0.5 + 0.5) < 0.18; }

        {
            bool trueStop = spd < 2.2f && accel < 0.08f;
            float stopAmount = Mathf.Clamp01(1f - spd / 2.2f);
            float shakeTarget = trueStop ? stopAmount : 0f;
            g3v_idleShakeEnv += (shakeTarget - g3v_idleShakeEnv) * (shakeTarget > g3v_idleShakeEnv ? 0.006f : 0.015f);
            if (g3v_idleShakeEnv > 0.01f)
            {
                double sway = Math.Sin(2.0 * Math.PI * ph_g3v_am1);
                double vibration = Math.Sin(2.0 * Math.PI * ph_g3v_idleVib);
                double shakeSound = sway * 0.8 + vibration * 0.65 + noise_lp * 0.35;
                shakeSound = Math.Tanh(shakeSound * 2.8);
                txSample += shakeSound * g3v_idleShakeEnv * 0.10 * engMul; // thinner than HDS200/300 -- quieter unit
            }
            float idleVibHz = Mathf.Lerp(18f, 55f, Mathf.Clamp01(1f - spd / 2f));
            ph_g3v_idleVib = (ph_g3v_idleVib + idleVibHz * invSR) % 1.0;
            ph_g3v_am1     = (ph_g3v_am1     + 2.4        * invSR) % 1.0;
        }

        {
            float rpmVibTargetHz = Mathf.Lerp(4f, 75f, Mathf.Pow(g3vGensetProgress, 3f));
            g3v_rpmVibHz += (rpmVibTargetHz - g3v_rpmVibHz) * 0.02f;
            float targetVol = g3vGensetRunning ? Mathf.Pow(g3vGensetProgress, 4f) * (0.11f + ld * 0.16f) : 0f;
            g3v_rpmVibVol += (targetVol - g3v_rpmVibVol) * 0.02f;
            if (g3v_rpmVibVol > 0.001f)
            {
                double vib = Math.Sin(2.0 * Math.PI * ph_g3v_rpmVib);
                double grit = noise_lp * g3vGensetProgress * 0.7;
                double brrr = vib * 0.8 + grit;
                brrr = Math.Tanh(brrr * (1.0 + g3vGensetProgress * 4));
                txSample += brrr * g3v_rpmVibVol * engMul;
            }
            ph_g3v_rpmVib = (ph_g3v_rpmVib + g3v_rpmVibHz * invSR) % 1.0;
        }

        {
            bool stoppedNow = spd < 0.4f;
            if (g3v_wasStopped && !stoppedNow && accel > 0.05f) g3v_moveOffTimer = 0.50f;
            g3v_wasStopped = stoppedNow;
            if (g3v_moveOffTimer > 0f)
            {
                g3v_moveOffTimer -= (float)invSR;
                float t = Mathf.Clamp01(g3v_moveOffTimer / 0.50f);
                float grabHz = 46f + (1f - t) * 18f;
                float judder = 0.5f + 0.5f * Mathf.Sin((float)audioClock * Mathf.PI * 2f * 10.5f);
                double grab = (Math.Sin(2.0 * Math.PI * ph_g3v_grab) * 0.8 + noise_lp * 0.4)
                               * t * (0.06f + judder * 0.04f) * (0.5f + ld * 0.5f) * engMul; // gentler -- lighter unit
                txSample += grab;
                ph_g3v_grab = (ph_g3v_grab + grabHz * invSR) % 1.0;
            }
        }

        if (g3v_isShaker)
        {
            float shakeLoad = Mathf.Clamp01(accel * 1.2f) * Mathf.Clamp01(1f - spd / 45f) + (spd < 1f ? 0.22f : 0f);
            if (shakeLoad > 0.02f)
            {
                double bodyv   = Math.Sin(2.0 * Math.PI * (ph_g3v_shakeBody * 0.78));
                double tremorv = 0.55 + 0.45 * Math.Sin(2.0 * Math.PI * (ph_g3v_shakeBody * 4.9));
                txSample += bodyv * tremorv * shakeLoad * 0.030 * engMul; // quieter -- "reduced noise and vibrations"
            }
            if (g3v_creakVol < 0.01f && shakeLoad > 0.15f && (NextNoiseSample() * 0.5 + 0.5) > 0.99955)
                g3v_creakVol = 0.6f + (float)(NextNoiseSample() * 0.5 + 0.5) * 0.4f;
            if (g3v_creakVol > 0.003f)
            {
                double c1 = Math.Sin(2.0 * Math.PI * ph_g3v_creak1);
                double c2 = Math.Sin(2.0 * Math.PI * ph_g3v_creak2);
                txSample += Math.Tanh((c1 + c2 * 0.5) * 1.6) * g3v_creakVol * 0.035 * engMul;
                double crHz = 320.0 + (1.0 - g3v_creakVol) * 260.0;
                ph_g3v_creak1 = (ph_g3v_creak1 + crHz        * invSR) % 1.0;
                ph_g3v_creak2 = (ph_g3v_creak2 + crHz * 1.48 * invSR) % 1.0;
                g3v_creakVol *= 0.9975f;
                if (g3v_creakVol < 0.004f) g3v_creakVol = 0f;
            }
            ph_g3v_shakeBody = (ph_g3v_shakeBody + 34.0 * invSR) % 1.0;
        }

        if (spd < 3f)
        {
            float pumpVol = 0.0035f * Mathf.Clamp01(1f - spd / 3f) * engMul;
            txSample += Math.Sin(2.0 * Math.PI * ph_g3v_pump) * pumpVol;
            ph_g3v_pump = (ph_g3v_pump + 95.0 * invSR) % 1.0; // fixed rate -- not genset-rpm-driven
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP HELPER — XE40 Battery-Electric
    // ═════════════════════════════════════════════════════════════════════════
    private void DoXE40DSP(ref double engineSample, ref double txSample,
                            float ld, float engVolPersonality, double noiseHp, double invSR,
                            bool isELFA2 = false)
    {
        // [FIX — abrupt harmonics on acceleration] `ld` used to feed straight
        // into motVol/invVol, which the tanh rasp is then applied to -- a
        // sudden pedal press meant an instant jump in how hard the rasp was
        // driven, which changes HARMONIC CONTENT, not just volume. That's
        // what read as harmonics abruptly appearing/disappearing. Smoothed
        // once here, used everywhere `ld` previously fed the tone directly.
        xe_ldSmooth += (ld - xe_ldSmooth) * (ld > xe_ldSmooth ? 0.004f : 0.003f);
        float ldS = xe_ldSmooth;

        bool isStopped = spd < 0.3f;
        if (!xe_contactorFired && running) { xe_contactorFired = true; xe_contactorTimer = 0f; xe_contactorPulse = 1f; }
        if (xe_contactorPulse > 0f) { xe_contactorTimer += (float)invSR; xe_contactorPulse = Mathf.Clamp01(1f - xe_contactorTimer / 0.05f); }

        // [FIX — takeoff blending] humVol used to snap instantly between its
        // stopped/moving values the same frame spd crossed 0.3 -- right when
        // the motor tone is just starting to fade in from silence. That
        // created an audible seam exactly at takeoff. Now smoothed like
        // everything else, so the hold-hum fades OUT while the motor tone
        // fades IN, overlapping instead of hard-cutting.
        float humTarget = (isStopped ? 0.045f : 0.022f) * npcVolumeScale * engVolPersonality;
        xe_humVolSmooth += (humTarget - xe_humVolSmooth) * 0.01f;

        // Cogging tremor — PM synchronous motors audibly "cog" while holding
        // torque near zero speed (stopped on a grade, creeping at a light);
        // a slow ~2.2Hz AM on the hold hum instead of a dead tone.
        float cogTarget = (isStopped && running) ? 1f : 0f;
        xe_cogVolSmooth += (cogTarget - xe_cogVolSmooth) * (cogTarget > xe_cogVolSmooth ? 0.01f : 0.02f);
        double cogAM = 1.0 + (xe_cogVolSmooth > 0.01f ? Math.Sin(2.0 * Math.PI * ph_xe_cog) * 0.12 * xe_cogVolSmooth : 0.0);
        ph_xe_cog = (ph_xe_cog + 2.2 * invSR) % 1.0;

        // Traction motor settle whirr — one-shot at the moving→stopped edge.
        // Starts near wherever the motor's own tone was at the moment of
        // stopping (so it reads as a continuation, not a new sound) and
        // whirrs its pitch down to a low floor over ~1s while it decays.
        bool wasMovingNow = !isStopped;
        if (xe_wasMovingForSettle && isStopped)
        {
            xe_settleWhirrEnv = 1f;
            xe_settleWhirrHz  = Mathf.Max(140f, xe_motorHzSmooth);
        }
        xe_wasMovingForSettle = wasMovingNow;

        double settleSample = 0.0;
        if (xe_settleWhirrEnv > 0.003f)
        {
            xe_settleWhirrHz = Mathf.Max(45f, xe_settleWhirrHz - xe_settleWhirrHz * 0.6f * (float)invSR * 60f);
            float settleVol = xe_settleWhirrEnv * xe_settleWhirrEnv * 0.05f * npcVolumeScale * engVolPersonality;
            settleSample = Math.Sin(2.0 * Math.PI * ph_xe_settle1) * settleVol
                         + Math.Sin(2.0 * Math.PI * ph_xe_settle2) * settleVol * 0.4;
            ph_xe_settle1 = (ph_xe_settle1 + xe_settleWhirrHz       * invSR) % 1.0;
            ph_xe_settle2 = (ph_xe_settle2 + xe_settleWhirrHz * 2.0 * invSR) % 1.0;
            xe_settleWhirrEnv *= 0.985f; // ~1s decay
            if (xe_settleWhirrEnv < 0.01f) xe_settleWhirrEnv = 0f;
        }

        // [FIX — takeoff blending] motor-Hz attack slowed slightly (0.012→0.009)
        // so the initial pitch rise off a stop has a touch more glide instead
        // of snapping up almost as fast as pedal input changes.
        float motTarget = (spd / MAX_SPD) * 640f;
        xe_motorHzSmooth += (motTarget - xe_motorHzSmooth) * (motTarget > xe_motorHzSmooth ? 0.009f : 0.006f);
        if (xe_motorHzSmooth < 0.3f) xe_motorHzSmooth = 0f;
        float motSpdFrac = Mathf.Clamp01(xe_motorHzSmooth / 640f);
        float motVol = motSpdFrac > 0.001f
            ? (0.05f + motSpdFrac * motSpdFrac * 0.11f + ldS * 0.03f) * npcVolumeScale * engVolPersonality * (1f + ldS * 0.45f) : 0f;

        // Slot-harmonic shimmer — RESEARCH: real PMSM traction motors show
        // strong, non-simple-integer "slot harmonic" orders tied to stator
        // slot/pole count rather than clean low integers (confirmed EV field
        // data cites 48th-order whine from stator slot harmonics; the PMSM
        // NVH literature analyzes 6-pole/36-slot designs the same way). This
        // is a thin, high, fast-tracking partial well above the fund/2nd/3rd/
        // 4th bank -- the "quality" shimmer real EV whine has that a simple
        // low-order sine stack doesn't.
        double motSample = 0;
        if (xe_motorHzSmooth > 0.3)
        {
            motSample = Math.Sin(2.0 * Math.PI * ph_xe_mot1) * motVol
                      + Math.Sin(2.0 * Math.PI * ph_xe_mot2) * motVol * 0.42
                      + Math.Sin(2.0 * Math.PI * ph_xe_mot3) * motVol * 0.18
                      + Math.Sin(2.0 * Math.PI * ph_xe_mot4) * motVol * 0.09 * motSpdFrac
                      + Math.Sin(2.0 * Math.PI * ph_xe_slot) * motVol * 0.05 * motSpdFrac;
            motSample *= cogAM;

            // [RESTORED] Motor rasp — the original XE40 whine (before the
            // ELFA-generation split) ran its fund+2nd+3rd bank through a
            // tanh drive for a gritty, "electric-adjacent" texture; that got
            // dropped when the clean/ELFA3-only path was introduced. Brought
            // back here as XE's baseline signature (light drive), with
            // isELFA2 (XHE) still riding a heavier drive on top of it so the
            // two remain distinct -- XE is lightly gritty, XHE is grittier.
            double raspDrive = isELFA2 ? 1.18 : 1.07;
            double raspMix   = isELFA2 ? 0.92 : 0.97;
            motSample = Math.Tanh(motSample * raspDrive) * raspMix
                      + Math.Sin(2.0 * Math.PI * ph_xe_mot3) * motVol * (isELFA2 ? 0.05 : 0.03);
        }

        xe_invHzSmooth += (4200f + (spd / MAX_SPD) * 1800f - xe_invHzSmooth) * 0.04f;
        float invHzCeil = isELFA2 ? 3600f : 4200f;
        float invHzUsed = Mathf.Min(xe_invHzSmooth, invHzCeil);
        float invVol = motSpdFrac > 0.001f
            ? (0.006f + ldS * 0.012f + motSpdFrac * motSpdFrac * 0.008f) * npcVolumeScale * engVolPersonality * (isELFA2 ? 1.15f : 1f) : 0f;

        // Regen chirp — brief relay/torque-ramp blip on the rising edge of
        // meaningful regen brake pressure (one-pedal-style engagement).
        bool regenAct = bkPd > 0.08f && spd > 0.5f;
        if (regenAct && !xe_wasRegenActive) { xe_regenChirpEnv = 1f; ph_xe_regenChirp = 0.0; }
        xe_wasRegenActive = regenAct;
        xe_regenChirpEnv = xe_regenChirpEnv > 0.001f ? xe_regenChirpEnv * 0.937f : 0f;
        double chirpHz = 900.0 - (1.0 - xe_regenChirpEnv) * 500.0;
        double regenChirp = xe_regenChirpEnv > 0.002f
            ? Math.Sin(2.0 * Math.PI * ph_xe_regenChirp) * xe_regenChirpEnv * 0.02 * npcVolumeScale * engVolPersonality : 0.0;
        ph_xe_regenChirp = (ph_xe_regenChirp + chirpHz * invSR) % 1.0;

        float regenTgt = regenAct ? (0.045f + bkPd * 0.06f) * npcVolumeScale * engVolPersonality : 0f;
        xe_regenVolSmooth += (regenTgt - xe_regenVolSmooth) * (regenAct ? 0.05f : 0.08f);

        // ── Electric whirr — REBUILT TIMING per spec: ~8 second total run,
        // wind-up (pitch+volume rise) for the first ~15% (~1.2s), then HOLD
        // constant (flat pitch, flat volume) through the bulk of the run,
        // then a short fade at the very end. Previous version kept the
        // pitch sweeping upward through 60% of the run instead of settling,
        // so it never actually finished winding up before starting to fade.
        if (!xe_compRunning)
        {
            xe_compTimer += (float)invSR;
            if (xe_compIdleDuration <= 0f) xe_compIdleDuration = 25f + (float)(NextNoiseSample() * 0.5 + 0.5) * 25f;
            if (xe_compTimer >= xe_compIdleDuration) { xe_compRunning = true; xe_compTimer = 0f; xe_compRunDuration = 7f + (float)(NextNoiseSample() * 0.5 + 0.5) * 2f; }
        }
        else
        {
            xe_compTimer += (float)invSR;
            if (xe_compTimer >= xe_compRunDuration) { xe_compRunning = false; xe_compTimer = 0f; xe_compIdleDuration = 0f; }
        }
        const float whirrAttackFrac  = 0.15f; // wind-up: first ~15% of the ~8s run
        const float whirrReleaseFrac = 0.88f; // fade starts at 88%, final ~12% is release
        float whirrProgress = xe_compRunning ? Mathf.Clamp01(xe_compTimer / xe_compRunDuration) : 0f;
        float attackEase = Mathf.Clamp01(whirrProgress / whirrAttackFrac);
        attackEase = attackEase * attackEase * (3f - 2f * attackEase); // smoothstep wind-up
        // Once whirrProgress passes whirrAttackFrac, attackEase stays pinned
        // at 1.0 -- that's the "hold constant" plateau -- until the release
        // envelope below starts pulling volume back down at whirrReleaseFrac.
        float whirrHzTarget  = xe_compRunning ? Mathf.Lerp(75f, 240f, attackEase) : 75f;
        xe_compHzSmooth += (whirrHzTarget - xe_compHzSmooth) * 0.01f;

        float envShape = xe_compRunning
            ? (whirrProgress < whirrReleaseFrac
                ? attackEase
                : attackEase * (1f - Mathf.Clamp01((whirrProgress - whirrReleaseFrac) / (1f - whirrReleaseFrac))))
            : 0f;
        float compTgt = envShape * 0.050f * npcVolumeScale * engVolPersonality;
        xe_compVolSmooth += (compTgt - xe_compVolSmooth) * (compTgt > xe_compVolSmooth ? 0.02f : 0.012f);

        // [ADD — "fan/wobble" character] Real-world note: this is a
        // real, genuinely audible-but-not-loud electric bus phenomenon --
        // the motor-driven air compressor's own cooling fan/housing isn't
        // silent, and its speed drifts slightly against load, so a run
        // doesn't hold a razor-flat pitch/volume the way a plain tone-stack
        // did before. Rate itself wanders a little (not a fixed LFO) so two
        // separate compressor cycles don't sound identical.
        xe_compWobbleHz += (float)(NextNoiseSample() * 0.15 * invSR);
        xe_compWobbleHz = Mathf.Clamp(xe_compWobbleHz, 1.1f, 2.3f);
        double compWobbleAM = 1.0 + Math.Sin(2.0 * Math.PI * ph_xe_compWobble) * 0.16;
        ph_xe_compWobble = (ph_xe_compWobble + xe_compWobbleHz * invSR) % 1.0;

        // Battery/inverter thermal loop — coolant pump (always-on, subtle) +
        // radiator fan (load-ramped). Real BEV transit buses run liquid-
        // cooled packs and inverters; this was entirely unmodeled before.
        float thermalLoadTarget = Mathf.Clamp01(ldS * 0.6f + motSpdFrac * 0.25f);
        xe_thermalLoadSmooth += (thermalLoadTarget - xe_thermalLoadSmooth) * (thermalLoadTarget > xe_thermalLoadSmooth ? 0.003f : 0.001f);
        float thermalFanVol = (0.006f + xe_thermalLoadSmooth * 0.014f) * npcVolumeScale * engVolPersonality;
        xe_thermalFanVolSmooth += (thermalFanVol - xe_thermalFanVolSmooth) * 0.008f;
        double thermalFan = noise_lp * xe_thermalFanVolSmooth * 0.5
                           + Math.Sin(2.0 * Math.PI * ph_xe_thermalFan) * xe_thermalFanVolSmooth * 0.4;
        ph_xe_thermalFan = (ph_xe_thermalFan + (70.0 + xe_thermalLoadSmooth * 40.0) * invSR) % 1.0;
        double pumpTone = Math.Sin(2.0 * Math.PI * ph_xe_pump) * 0.006 * npcVolumeScale * engVolPersonality;
        ph_xe_pump = (ph_xe_pump + 46.0 * invSR) % 1.0;

        engineSample = Math.Sin(2.0 * Math.PI * ph_xe_hum) * xe_humVolSmooth * cogAM + motSample
                     + (xe_contactorPulse > 0f ? noiseHp * xe_contactorPulse * 0.25 * npcVolumeScale : 0)
                     + pumpTone + settleSample;

        txSample = Math.Sin(2.0 * Math.PI * ph_xe_inv1) * invVol
                 + Math.Sin(2.0 * Math.PI * ph_xe_inv2) * invVol * 0.35
                 + noise_hi * invVol * 0.55
                 + regenChirp
                 + (xe_regenVolSmooth > 0.0005f
                    ? Math.Sin(2.0 * Math.PI * ph_xe_regenTone1) * xe_regenVolSmooth
                    + Math.Sin(2.0 * Math.PI * ph_xe_regenTone2) * xe_regenVolSmooth * 0.4 : 0)
                 + (xe_compVolSmooth > 0.0005f
                    ? (Math.Sin(2.0 * Math.PI * ph_xe_comp1) * xe_compVolSmooth
                      + Math.Sin(2.0 * Math.PI * ph_xe_comp2) * xe_compVolSmooth * 0.45
                      // Broadband fan-body noise (low-passed -- housing/air
                      // rush, not a hissy tone) -- this is most of what
                      // makes it read as "fan" rather than a bare whine.
                      + noise_lp * xe_compVolSmooth * 0.55
                      + noise_hi * xe_compVolSmooth * 0.05) * compWobbleAM
                    : 0)
                 + thermalFan;

        ph_xe_hum   = (ph_xe_hum   + 60.0                 * invSR) % 1.0;
        ph_xe_mot1  = (ph_xe_mot1  + xe_motorHzSmooth      * invSR) % 1.0;
        ph_xe_mot2  = (ph_xe_mot2  + xe_motorHzSmooth * 2.0    * invSR) % 1.0;
        // Regen tone -- own properly-accumulated phases, same target
        // frequency ratio (0.85x / 0.85x*~1x) the old (broken) approach was
        // aiming for, just without the discontinuity.
        ph_xe_regenTone1 = (ph_xe_regenTone1 + xe_motorHzSmooth * 0.85               * invSR) % 1.0;
        ph_xe_regenTone2 = (ph_xe_regenTone2 + xe_motorHzSmooth * 0.85 * 1.998 / 2.0 * invSR) % 1.0;
        ph_xe_mot3  = (ph_xe_mot3  + xe_motorHzSmooth * 2.997  * invSR) % 1.0;
        ph_xe_mot4  = (ph_xe_mot4  + xe_motorHzSmooth * 4.01   * invSR) % 1.0;
        ph_xe_slot  = (ph_xe_slot  + xe_motorHzSmooth * 5.98   * invSR) % 1.0;
        ph_xe_inv1  = (ph_xe_inv1  + invHzUsed             * invSR) % 1.0;
        ph_xe_inv2  = (ph_xe_inv2  + invHzUsed * 1.5       * invSR) % 1.0;
        ph_xe_comp1 = (ph_xe_comp1 + xe_compHzSmooth       * invSR) % 1.0;
        ph_xe_comp2 = (ph_xe_comp2 + xe_compHzSmooth * 1.5 * invSR) % 1.0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP HELPER — ZF AVE 130 electric portal axle (XE60)
    //
    //  [CORRECTED — RESEARCH] A prior pass added a second "center axle" motor
    //  bank on the claim that New Flyer's XE60 powers both center and rear
    //  axles. Re-checked against ZF's own AVE 130 documentation and multiple
    //  independent sources (ZF press materials, CPTDB, Sustainable Bus,
    //  Fleet Maintenance, ZF's Australian dealer writeup): the AVE 130 is a
    //  SINGLE portal axle with TWO wheel-hub motors (one per wheel, ~120kW
    //  each, no differential needed) — and ZF explicitly states articulated
    //  buses can run on just that one driven axle ("puller principle,"
    //  mechanically pulled from the second axle, not a second powered
    //  electric axle). The center-axle bank was a real bug, not a real
    //  system — removed. XE60 is correctly TWO oscillator banks (left/right
    //  wheel motor), matching XE40's single-bank structure doubled, not three.
    //
    //  [FIX] The original version detuned the right-motor bank ~0.5-0.6% AND
    //  ran it nearly as loud as the left bank (0.95x/0.38x gain). Two sines
    //  that close in frequency don't read as "two independent motors" —
    //  they beat, and at 0.6% detune against a ~600Hz fundamental that's a
    //  ~3.6Hz beat, i.e. an audible fast tremolo ("WOWOWOW"). That's the bug.
    //  Real portal-axle motors ARE two independent inverters, but the
    //  difference between them is small enough that you hear "one bigger,
    //  richer tone," not a warble — same way XE40 reads as one clean sine
    //  swell, not a wobble. Fixed by (a) dropping detune to ~0.02-0.03%
    //  (beat period now several seconds, well below "notice a wobble"
    //  territory) and (b) making the right bank genuinely a quiet secondary
    //  layer (0.22x/0.10x) instead of near-equal volume to the left.
    //
    //  Same overall shape as DoXE40DSP (hum + motor tone + inverter whine +
    //  regen layer + cyclic compressor + contactor pop on start), but:
    //    · A dedicated planetary-mesh whine layer that XE40 has no equivalent
    //      of at all, since XE40 is direct-drive and this genuinely has
    //      reduction gearing in the axle.
    //    · Slightly louder/heavier throughout — a 60ft artic with two motors
    //      reads as bigger than XE40's single 40ft motor, not just louder.
    // ═════════════════════════════════════════════════════════════════════════
    // ═════════════════════════════════════════════════════════════════════════
    //  DSP HELPER — ARTICULATION JOINT CREAK/GROAN (X60 AVN132/AVE130 only)
    //
    //  Real, physical phenomenon riders on actual X60 artics notice: the
    //  turntable/hinge bearing creaks under torsional load, and the rubber
    //  accordion bellows fabric creaks/pops as it compresses on tighter
    //  turns. Two distinct real mechanisms modeled here:
    //
    //    1. BEARING GROAN — classic stick-slip friction. Real stick-slip
    //       doesn't hold a clean pitch; it jitters/steps as the surfaces
    //       repeatedly grip then release. Loudest while the hinge angle is
    //       actively CHANGING (working against the bearing), with a smaller
    //       residual while just holding a large angle under load (steady
    //       cornering, not straight-line running).
    //    2. BELLOWS FABRIC POP — discrete, higher-pitched textured pops as
    //       the accordion folds compress, more frequent the faster the
    //       joint is articulating.
    //
    //  Gated on centerAxleType being a real portal-axle artic config
    //  (AVN132_Passive/AVE130_Driven via FleetSeriesDefinition.
    //  ResolveCenterAxleType) — MAN40ft (non-artic, or artics on a plain
    //  beam axle) gets nothing, same gate the portal-axle whine hook uses.
    //  Driven by hingeAngleDeg, which both controllers now sync in every
    //  tick from their own real hinge-angle solver (BusController/
    //  NPCBusController's HingeAngleDegrees).
    // ═════════════════════════════════════════════════════════════════════════
    private void DoArticulationCreakDSP(ref double txSample, double invSR)
    {
        if (centerAxleType == FleetSeriesDefinition.CenterAxleType.MAN40ft) return;

        float dt = Mathf.Max(0.0001f, (float)invSR);
        float hingeRate = (hingeAngleDeg - ca_prevHinge) / dt;
        ca_prevHinge = hingeAngleDeg;

        float absAngle = Mathf.Abs(hingeAngleDeg);
        float absRate  = Mathf.Abs(hingeRate);

        // Bearing groan target: mostly rate-driven (actively turning), plus
        // a smaller angle-held term (parked mid-turn, or a long steady bend).
        float creakTarget = Mathf.Clamp01(absRate / 6f) * 0.6f + Mathf.Clamp01(absAngle / 40f) * 0.25f;
        ca_creakVolSmooth += (creakTarget - ca_creakVolSmooth) * (creakTarget > ca_creakVolSmooth ? 0.03f : 0.01f);

        if (ca_creakVolSmooth > 0.001f)
        {
            // Stick-slip pitch jitter -- random-walked, not swept, so it
            // reads as friction catching/releasing rather than a whine.
            ca_creakPitchJitter += (float)(NextNoiseSample() * 40.0 * invSR);
            ca_creakPitchJitter = Mathf.Clamp(ca_creakPitchJitter, 55f, 140f);
            double tone = Math.Sin(2.0 * Math.PI * ph_ca_creak1) * 0.7
                        + Math.Sin(2.0 * Math.PI * ph_ca_creak2) * 0.35;
            double creak = Math.Tanh(tone * 1.6 + noise_lp * 0.3) * ca_creakVolSmooth * 0.09 * npcVolumeScale;
            txSample += creak;
            ph_ca_creak1 = (ph_ca_creak1 + ca_creakPitchJitter       * invSR) % 1.0;
            ph_ca_creak2 = (ph_ca_creak2 + ca_creakPitchJitter * 1.5 * invSR) % 1.0;
        }

        // Bellows pop -- random interval, shortens (more frequent) the
        // harder the joint is actively working.
        ca_popTimer += (float)invSR;
        float popInterval = Mathf.Lerp(4.0f, 0.35f, Mathf.Clamp01(absRate / 10f));
        if (ca_popTimer >= popInterval)
        {
            ca_popTimer = 0f;
            ca_popEnvVol = Mathf.Clamp01(0.3f + absRate * 0.05f);
        }
        if (ca_popEnvVol > 0.002f)
        {
            txSample += noise_hi * ca_popEnvVol * 0.05 * npcVolumeScale;
            ca_popEnvVol *= 0.90f;
        }
    }

    private void DoZFAVE130DSP(ref double engineSample, ref double txSample,
                                float ld, float engVolPersonality, double noiseHp, double invSR,
                                bool isELFA2 = false)
    {
        // [FIX — abrupt harmonics on acceleration] `ld` (accelerator/load)
        // arrives unsmoothed and used to feed straight into motVol/invVol/
        // rasp drive -- a sudden pedal press meant an instant jump in both
        // volume AND how hard the tanh rasp was driven (more drive = audibly
        // different harmonic content, not just louder), which is what read
        // as harmonics "abruptly coming on/off." Smoothed here once, used
        // everywhere `ld` previously was for these parts of the chain.
        zfa_ldSmooth += (ld - zfa_ldSmooth) * (ld > zfa_ldSmooth ? 0.004f : 0.003f);
        float ldS = zfa_ldSmooth;

        // [REAL SPEC — Miami-Dade XE60 manual] "When the brake pedal is
        // depressed, the regenerative braking is blended with the vehicle
        // service brakes" -- so regen is brake-pedal-driven (not a
        // coast-down effect), which the existing bkPd gate already had
        // right. Two real gates were MISSING though:
        //   · "Under certain operating conditions, the ABS system will
        //     override the regenerative braking system." Same override the
        //     Allison retarder has, and for the same reason -- an ABS event
        //     must not have driveline braking fighting it.
        //   · "The regenerative braking system can be disabled by using the
        //     Regen Brake Disable switch located in the destination sign
        //     compartment." A real driver-facing control, directly parallel
        //     to the Allison Retarder switch.
        // [MOVED UP — real compile error fix] This used to sit right before
        // the electric whirr section, well AFTER the center axle/BTMS block
        // further down in this function that also needs regenAct -- a
        // "used before declared" error, since C# local variables must be
        // declared before any use, textually, even earlier in the same
        // method. Relocated here (right after ldS is established) since
        // this block has no dependency on anything computed in between.
        bool regenAct = bkPd > 0.08f && spd > 0.5f && !absActive && regenBrakeEnabled;
        if (regenAct && !zfa_wasRegenActive) { zfa_regenChirpEnv = 1f; ph_zfa_regenChirp = 0.0; }
        zfa_wasRegenActive = regenAct;
        zfa_regenChirpEnv = zfa_regenChirpEnv > 0.001f ? zfa_regenChirpEnv * 0.934f : 0f;
        double chirpHz = 850.0 - (1.0 - zfa_regenChirpEnv) * 470.0;
        double regenChirp = zfa_regenChirpEnv > 0.002f
            ? Math.Sin(2.0 * Math.PI * ph_zfa_regenChirp) * zfa_regenChirpEnv * 0.022 * npcVolumeScale * engVolPersonality : 0.0;
        ph_zfa_regenChirp = (ph_zfa_regenChirp + chirpHz * invSR) % 1.0;

        float regenTgt = regenAct ? (0.05f + bkPd * 0.065f) * npcVolumeScale * engVolPersonality : 0f;
        zfa_regenVolSmooth += (regenTgt - zfa_regenVolSmooth) * (regenAct ? 0.05f : 0.08f);


        bool isStopped = spd < 0.3f;
        if (!zfa_contactorFired && running) { zfa_contactorFired = true; zfa_contactorTimer = 0f; zfa_contactorPulse = 1f; }
        if (zfa_contactorPulse > 0f) { zfa_contactorTimer += (float)invSR; zfa_contactorPulse = Mathf.Clamp01(1f - zfa_contactorTimer / 0.06f); }

        float humTarget = (isStopped ? 0.052f : 0.026f) * npcVolumeScale * engVolPersonality;
        zfa_humVolSmooth += (humTarget - zfa_humVolSmooth) * 0.01f;

        float cogTarget = (isStopped && running) ? 1f : 0f;
        zfa_cogVolSmooth += (cogTarget - zfa_cogVolSmooth) * (cogTarget > zfa_cogVolSmooth ? 0.01f : 0.02f);
        double cogAM = 1.0 + (zfa_cogVolSmooth > 0.01f ? Math.Sin(2.0 * Math.PI * ph_zfa_cog) * 0.12 * zfa_cogVolSmooth : 0.0);
        ph_zfa_cog = (ph_zfa_cog + 2.15 * invSR) % 1.0;

        // ── REAR axle bank (existing zfa_ fields) — closer, brighter. ──────
        // Portal axle's final reduction means the motor itself spins a touch
        // slower at road speed than XE40's direct motor does. Same smoothing
        // shape as XE40 — this IS the "eeeeEEEEAAAAHHH" glide; it's a single
        // continuously-tracked target, never stepped. [FIX — takeoff
        // blending] attack slowed slightly (0.012→0.009), same reasoning as
        // XE40's.
        float motTarget = (spd / MAX_SPD) * 610f;
        zfa_motorHzSmooth += (motTarget - zfa_motorHzSmooth) * (motTarget > zfa_motorHzSmooth ? 0.009f : 0.006f);
        if (zfa_motorHzSmooth < 0.3f) zfa_motorHzSmooth = 0f;
        float motSpdFrac = Mathf.Clamp01(zfa_motorHzSmooth / 610f);
        float motVol = motSpdFrac > 0.001f
            ? (0.045f + motSpdFrac * motSpdFrac * 0.10f + ldS * 0.03f) * npcVolumeScale * engVolPersonality * (1f + ldS * 0.45f) : 0f;

        double motSample = 0;
        if (zfa_motorHzSmooth > 0.3f)
        {
            // Left bank — the dominant, clean swell (mirrors XE40's mot1/2/3
            // exactly in structure/weighting, so the core tone reads the same).
            motSample = Math.Sin(2.0 * Math.PI * ph_zfa_motL1) * motVol
                      + Math.Sin(2.0 * Math.PI * ph_zfa_motL2) * motVol * 0.42
                      + Math.Sin(2.0 * Math.PI * ph_zfa_motL3) * motVol * 0.18
                      // Right bank — quiet, near-unison "second motor" thickening.
                      // Detune is small enough (~0.02%) that the beat period is
                      // several seconds long: it reads as body/richness, not a wobble.
                      + Math.Sin(2.0 * Math.PI * ph_zfa_motR1) * motVol * 0.22
                      + Math.Sin(2.0 * Math.PI * ph_zfa_motR2) * motVol * 0.10
                      // Slot-harmonic shimmer — same research basis as XE40.
                      + Math.Sin(2.0 * Math.PI * ph_zfa_slot) * motVol * 0.05 * motSpdFrac;
            motSample *= cogAM;

            // [RESTORED] Motor rasp — same reasoning/values as XE40: XE60's
            // baseline now carries the light signature grit back, isELFA2
            // (XHE60) rides a heavier drive on top so the two stay distinct.
            // Drive amount now derives from the SMOOTHED load (ldS isn't
            // used directly here -- the drive constants are fixed -- but
            // motVol itself, which the drive is applied to, now only moves
            // as fast as ldS allows, which is what actually stopped the
            // harmonic content from jumping.
            double raspDrive = isELFA2 ? 1.16 : 1.06;
            double raspMix   = isELFA2 ? 0.93 : 0.97;
            motSample = Math.Tanh(motSample * raspDrive) * raspMix
                      + Math.Sin(2.0 * Math.PI * ph_zfa_motL3) * motVol * (isELFA2 ? 0.045 : 0.028);
        }

        // Planetary reduction mesh whine — real gear teeth, real gear-mesh
        // tone, tracking motor speed at a fixed ratio. This is the layer
        // that makes AVE 130 read as a portal axle rather than a hub motor.
        // Two mesh partials are a fixed 1.503 ratio apart (nowhere near
        // unison), so this layer was never the source of the wobble.
        float meshVol = motSpdFrac > 0.001f
            ? (0.020f + motSpdFrac * 0.045f + ldS * 0.02f) * npcVolumeScale * engVolPersonality : 0f;
        double meshHz = 260.0 + zfa_motorHzSmooth * 2.35;
        double meshSample = meshVol > 0.0005f
            ? Math.Sin(2.0 * Math.PI * ph_zfa_mesh1) * meshVol + Math.Sin(2.0 * Math.PI * ph_zfa_mesh2) * meshVol * 0.5
            : 0.0;

        // ═════════════════════════════════════════════════════════════════
        //  CENTER AXLE DRIVE  —  RESTORED, and this time actually grounded.
        //
        //  [CORRECTION OF MY OWN EARLIER MISTAKE] Earlier this session I
        //  REMOVED a center-axle motor bank from this function, on the
        //  reasoning that the ZF AVE130 is a single portal axle with two
        //  wheel-hub motors and therefore a third bank was a research
        //  error. The real Miami-Dade XE60 operator's manual (SR2837)
        //  shows that removal was wrong. Its spec pages list, separately:
        //    · "Traction Motor: Siemens ELFA3 Permanent Electromagnetic
        //      Motor (PEM) A5E48131981, Model 1DB2016-6NB06, 561 V,
        //      215 HP (160 kW), 752 ft-lbs"
        //    · "Traction Motor/Inverter Cooling System — EMP Radiator with
        //      2 Fil-11 pusher-type fans, Coolant Reservoir, Ametek
        //      Coolant Pump"
        //    · "Center Axle Cooling System — EMP Heat Exchanger with 2
        //      Fil-11 pusher-type fans, Coolant Reservoir, Ametek Coolant
        //      Pump"
        //  A center axle with its own dedicated cooling loop, heat
        //  exchanger, reservoir and pump is a DRIVEN axle -- you do not
        //  plumb a full second liquid-cooling circuit for an idler. This
        //  also matches the XE60 being described as a center- AND
        //  rear-motor four-wheel-drive layout. So the XE60 genuinely has
        //  TWO drive units, and the bank below is real.
        //
        //  Character: the center axle sits further from the listener at
        //  the rear of the bus, so it reads darker and quieter than the
        //  rear bank -- lower motor ratio, softer mesh, no separate rasp
        //  layer of its own. It tracks the same road speed (both axles are
        //  mechanically tied to the ground) but is deliberately detuned a
        //  little so the two banks beat gently against each other rather
        //  than sitting in artificial unison.
        // ═════════════════════════════════════════════════════════════════
        float ctrMotTarget = (spd / MAX_SPD) * 548f; // lower than the rear bank's 610f
        zfc_motorHzSmooth += (ctrMotTarget - zfc_motorHzSmooth) * (ctrMotTarget > zfc_motorHzSmooth ? 0.009f : 0.006f);
        if (zfc_motorHzSmooth < 0.3f) zfc_motorHzSmooth = 0f;
        float ctrSpdFrac = Mathf.Clamp01(zfc_motorHzSmooth / 548f);
        float ctrMotVol = ctrSpdFrac > 0.001f
            ? (0.030f + ctrSpdFrac * ctrSpdFrac * 0.062f + ldS * 0.020f) * npcVolumeScale * engVolPersonality * (1f + ldS * 0.35f)
            : 0f;
        double ctrSample = 0.0;
        if (ctrMotVol > 0.0005f)
        {
            ctrSample = Math.Sin(2.0 * Math.PI * ph_zfc_mot1) * ctrMotVol
                      + Math.Sin(2.0 * Math.PI * ph_zfc_mot2) * ctrMotVol * 0.42;
            // Its own planetary reduction whine -- present but softer than
            // the rear bank's, and at a different ratio so the two mesh
            // layers never sit on top of each other.
            ctrSample += Math.Sin(2.0 * Math.PI * ph_zfc_mesh) * ctrMotVol * 0.30;
            ph_zfc_mot1  = (ph_zfc_mot1  + zfc_motorHzSmooth          * invSR) % 1.0;
            ph_zfc_mot2  = (ph_zfc_mot2  + zfc_motorHzSmooth * 2.0    * invSR) % 1.0;
            ph_zfc_mesh  = (ph_zfc_mesh  + (215.0 + zfc_motorHzSmooth * 2.05) * invSR) % 1.0;
        }
        engineSample += ctrSample;

        // ── Center axle cooling loop — its OWN 2 Fil-11 pusher fans and
        //    Ametek pump, entirely separate from the traction motor/
        //    inverter loop. Runs on its own thermal state, so at times only
        //    one of the two loops is spun up, which is a real and
        //    noticeable asymmetry on a 60ft electric.
        float ctrHeatTarget = Mathf.Clamp01(ldS * 0.8f + ctrSpdFrac * 0.45f);
        zfc_thermalLoadSmooth += (ctrHeatTarget - zfc_thermalLoadSmooth) * (float)invSR * 0.010f;
        float ctrFanTarget = Mathf.Clamp01((zfc_thermalLoadSmooth - 0.18f) / 0.72f);
        zfc_thermalFanVolSmooth += (ctrFanTarget - zfc_thermalFanVolSmooth) * (float)invSR * 0.55f;
        if (zfc_thermalFanVolSmooth > 0.002f)
        {
            double ctrFanHz = 24.0 + zfc_thermalFanVolSmooth * 30.0;
            engineSample += (noise_lp * 0.72 + Math.Sin(2.0 * Math.PI * ph_zfc_fan) * 0.28)
                          * zfc_thermalFanVolSmooth * 0.040 * npcVolumeScale * engVolPersonality;
            ph_zfc_fan = (ph_zfc_fan + ctrFanHz * invSR) % 1.0;
            // Ametek coolant pump for this loop -- continuous tonal hum
            // whenever the loop is active.
            engineSample += Math.Sin(2.0 * Math.PI * ph_zfc_pump)
                          * zfc_thermalFanVolSmooth * 0.011 * npcVolumeScale * engVolPersonality;
            ph_zfc_pump = (ph_zfc_pump + 41.0 * invSR) % 1.0;
        }

        // ── BATTERY THERMAL MANAGEMENT SYSTEM (BTMS) — real, and entirely
        //    unmodeled until now. Manual: "Modine Rooftop unit with
        //    integrated air-to-liquid heat exchanger, condenser, heater,
        //    refrigerant compressor, filter, pump & reservoir." That is a
        //    complete standalone refrigeration circuit dedicated to the
        //    770 kWh ESS pack -- a real second compressor on the roof,
        //    quite separate from the two Thermo King cabin HVAC units.
        //    Cycles on its own long duty cycle driven by pack thermal load
        //    (charge/discharge current, approximated here by load + regen),
        //    so it can be running while the cabin AC is idle and vice
        //    versa.
        float btmsHeatTarget = Mathf.Clamp01(ldS * 0.55f + (regenAct ? 0.45f : 0f) + ctrSpdFrac * 0.2f);
        zfa_btmsLoadSmooth += (btmsHeatTarget - zfa_btmsLoadSmooth) * (float)invSR * 0.006f;
        bool btmsShouldRun = zfa_btmsRunning ? zfa_btmsLoadSmooth > 0.28f : zfa_btmsLoadSmooth > 0.58f;
        zfa_btmsRunning = btmsShouldRun;
        float btmsTarget = zfa_btmsRunning ? 1f : 0f;
        zfa_btmsVolSmooth += (btmsTarget - zfa_btmsVolSmooth) * (float)invSR * (btmsTarget > zfa_btmsVolSmooth ? 0.5f : 0.35f);
        if (zfa_btmsVolSmooth > 0.002f)
        {
            // Rooftop refrigerant compressor -- lower and steadier than the
            // cabin AC scroll compressor, since it serves a fixed thermal
            // load rather than chasing a cabin setpoint.
            double btmsHz = 46.0 + zfa_btmsLoadSmooth * 22.0;
            engineSample += (Math.Sin(2.0 * Math.PI * ph_zfa_btms) * 0.62 + noise_lp * 0.38)
                          * zfa_btmsVolSmooth * 0.026 * npcVolumeScale * engVolPersonality;
            ph_zfa_btms = (ph_zfa_btms + btmsHz * invSR) % 1.0;
        }

        zfa_invHzSmooth += (4000f + (spd / MAX_SPD) * 1900f - zfa_invHzSmooth) * 0.04f;
        float invHzCeil = isELFA2 ? 3400f : 4000f;
        float invHzUsed = Mathf.Min(zfa_invHzSmooth, invHzCeil);
        float invVol = motSpdFrac > 0.001f
            ? (0.007f + ldS * 0.013f + motSpdFrac * motSpdFrac * 0.009f) * npcVolumeScale * engVolPersonality * (isELFA2 ? 1.15f : 1f) : 0f;

        // ── Electric whirr — REBUILT TIMING per spec: ~8 second total run,
        // wind-up (pitch+volume rise) for the first ~15%, then HOLD constant
        // (flat pitch, flat volume, no further movement) through the bulk of
        // the run, then a short fade at the very end rather than winding
        // down or snapping off. Previous version kept sweeping pitch upward
        // most of the run instead of settling, which read as never actually
        // finishing its wind-up.
        if (!zfa_compRunning)
        {
            zfa_compTimer += (float)invSR;
            if (zfa_compIdleDuration <= 0f) zfa_compIdleDuration = 22f + (float)(NextNoiseSample() * 0.5 + 0.5) * 25f;
            if (zfa_compTimer >= zfa_compIdleDuration) { zfa_compRunning = true; zfa_compTimer = 0f; zfa_compRunDuration = 7f + (float)(NextNoiseSample() * 0.5 + 0.5) * 2f; }
        }
        else
        {
            zfa_compTimer += (float)invSR;
            if (zfa_compTimer >= zfa_compRunDuration) { zfa_compRunning = false; zfa_compTimer = 0f; zfa_compIdleDuration = 0f; }
        }
        const float whirrAttackFrac  = 0.15f; // wind-up: first ~15% of the ~8s run (~1.2s)
        const float whirrReleaseFrac = 0.88f; // fade starts at 88%, so release is the final ~12% (~1s)
        float whirrProgress = zfa_compRunning ? Mathf.Clamp01(zfa_compTimer / zfa_compRunDuration) : 0f;
        float attackEase = Mathf.Clamp01(whirrProgress / whirrAttackFrac);
        attackEase = attackEase * attackEase * (3f - 2f * attackEase); // smoothstep wind-up
        // Pitch and volume both wind up during the attack, then HOLD flat
        // (attackEase stays at 1.0 for the whole sustain since whirrProgress
        // only grows past whirrAttackFrac -- no further pitch/volume motion
        // until the release envelope below takes over).
        float whirrHzTarget  = zfa_compRunning ? Mathf.Lerp(70f, 220f, attackEase) : 70f;
        zfa_compHzSmooth += (whirrHzTarget - zfa_compHzSmooth) * 0.01f;

        float envShape = zfa_compRunning
            ? (whirrProgress < whirrReleaseFrac
                ? attackEase
                : attackEase * (1f - Mathf.Clamp01((whirrProgress - whirrReleaseFrac) / (1f - whirrReleaseFrac))))
            : 0f;
        float compTgt = envShape * 0.058f * npcVolumeScale * engVolPersonality;
        zfa_compVolSmooth += (compTgt - zfa_compVolSmooth) * (compTgt > zfa_compVolSmooth ? 0.02f : 0.012f);

        // Thermal loop — bigger than XE40's single-axle version (two motor sets to cool).
        float thermalLoadTarget = Mathf.Clamp01(ldS * 0.62f + motSpdFrac * 0.26f);
        zfa_thermalLoadSmooth += (thermalLoadTarget - zfa_thermalLoadSmooth) * (thermalLoadTarget > zfa_thermalLoadSmooth ? 0.003f : 0.001f);
        float thermalFanVol = (0.007f + zfa_thermalLoadSmooth * 0.017f) * npcVolumeScale * engVolPersonality;
        zfa_thermalFanVolSmooth += (thermalFanVol - zfa_thermalFanVolSmooth) * 0.008f;
        double thermalFan = noise_lp * zfa_thermalFanVolSmooth * 0.5
                           + Math.Sin(2.0 * Math.PI * ph_zfa_thermalFan) * zfa_thermalFanVolSmooth * 0.4;
        ph_zfa_thermalFan = (ph_zfa_thermalFan + (72.0 + zfa_thermalLoadSmooth * 42.0) * invSR) % 1.0;
        double pumpTone = Math.Sin(2.0 * Math.PI * ph_zfa_pump) * 0.007 * npcVolumeScale * engVolPersonality;
        ph_zfa_pump = (ph_zfa_pump + 45.0 * invSR) % 1.0;

        engineSample = Math.Sin(2.0 * Math.PI * ph_zfa_hum) * zfa_humVolSmooth * cogAM + motSample
                     + (zfa_contactorPulse > 0f ? noiseHp * zfa_contactorPulse * 0.28 * npcVolumeScale : 0)
                     + pumpTone;

        txSample = Math.Sin(2.0 * Math.PI * ph_zfa_inv1) * invVol
                 + Math.Sin(2.0 * Math.PI * ph_zfa_inv2) * invVol * 0.35
                 + noise_hi * invVol * 0.55
                 + meshSample
                 + regenChirp
                 + (zfa_regenVolSmooth > 0.0005f
                    ? Math.Sin(2.0 * Math.PI * ph_zfa_regenTone1) * zfa_regenVolSmooth
                    + Math.Sin(2.0 * Math.PI * ph_zfa_regenTone2) * zfa_regenVolSmooth * 0.4 : 0)
                 + (zfa_compVolSmooth > 0.0005f
                    ? Math.Sin(2.0 * Math.PI * ph_zfa_comp1) * zfa_compVolSmooth
                    + Math.Sin(2.0 * Math.PI * ph_zfa_comp2) * zfa_compVolSmooth * 0.45
                    + noise_hi * zfa_compVolSmooth * 0.05 : 0)
                 + thermalFan;

        ph_zfa_hum   = (ph_zfa_hum   + 58.0                 * invSR) % 1.0;
        ph_zfa_motL1 = (ph_zfa_motL1 + zfa_motorHzSmooth           * invSR) % 1.0;
        ph_zfa_motL2 = (ph_zfa_motL2 + zfa_motorHzSmooth * 2.0     * invSR) % 1.0;
        ph_zfa_motL3 = (ph_zfa_motL3 + zfa_motorHzSmooth * 2.997   * invSR) % 1.0;
        ph_zfa_slot  = (ph_zfa_slot  + zfa_motorHzSmooth * 5.98    * invSR) % 1.0;
        // [FIX, kept from prior pass] Right bank detune dropped from
        // ~0.5-0.6% to ~0.02-0.03% — old values produced a several-Hz beat
        // (audible fast wobble); these produce a beat period of several
        // seconds (reads as thickness, not motion).
        ph_zfa_motR1 = (ph_zfa_motR1 + zfa_motorHzSmooth * 1.0002  * invSR) % 1.0;
        ph_zfa_motR2 = (ph_zfa_motR2 + zfa_motorHzSmooth * 2.0003  * invSR) % 1.0;
        ph_zfa_regenTone1 = (ph_zfa_regenTone1 + zfa_motorHzSmooth * 0.85               * invSR) % 1.0;
        ph_zfa_regenTone2 = (ph_zfa_regenTone2 + zfa_motorHzSmooth * 0.85 * 1.998 / 2.0 * invSR) % 1.0;
        ph_zfa_mesh1 = (ph_zfa_mesh1 + meshHz                      * invSR) % 1.0;
        ph_zfa_mesh2 = (ph_zfa_mesh2 + meshHz * 1.503              * invSR) % 1.0;
        ph_zfa_inv1  = (ph_zfa_inv1  + invHzUsed                   * invSR) % 1.0;
        ph_zfa_inv2  = (ph_zfa_inv2  + invHzUsed * 1.5             * invSR) % 1.0;
        ph_zfa_comp1 = (ph_zfa_comp1 + zfa_compHzSmooth            * invSR) % 1.0;
        ph_zfa_comp2 = (ph_zfa_comp2 + zfa_compHzSmooth * 1.5      * invSR) % 1.0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP HELPER — Accelera/Siemens ELFA3 drivetrain: rear axle direct-
    //  drive motor + centre axle IN-WHEEL motor (artic only)
    //
    //  Real spec, confirmed via Accelera/Siemens/Cummins documentation: this
    //  is NOT the ZF AVE 130 portal axle (that's genuine, separate ZF
    //  hardware — see DoZFAVE130DSP, unchanged). This layout is rear-axle
    //  direct-drive (identical architecture to the single-motor XE40/XHE40
    //  tone, so it rides on DoXE40DSP unmodified for that part) PLUS a
    //  second, mechanically distinct in-wheel hub motor on the centre axle.
    //  Additive on top, not a replacement — a real artic on this drivetrain
    //  has BOTH motors running simultaneously.
    // ═════════════════════════════════════════════════════════════════════════
    private void DoElfa3CenterAxleDSP(ref double engineSample, ref double txSample,
                                       float ld, float engVolPersonality, double noiseHp, double invSR,
                                       bool isELFA2 = false)
    {
        // Rear axle — same direct-drive motor/inverter/aux chain as the
        // single-motor XE40/XHE40 tone (isELFA2 still selects the older/
        // grittier Siemens-generation character when this is the hydrogen
        // "elfa2_centeraxle" pick).
        DoXE40DSP(ref engineSample, ref txSample, ld, engVolPersonality, noiseHp, invSR, isELFA2);

        // Centre axle — in-wheel hub motor. No reduction gearing (it drives
        // the wheel 1:1), mounted directly in the unsprung wheel assembly
        // rather than remote-mounted to the chassis, so it reads as lower-
        // pitched and more structurally coupled ("thrum" rather than clean
        // whine) than the rear motor, with its own torque-ripple roughness
        // since there's no gear stage to smooth that out.
        bool isStopped = spd < 0.3f;
        float ceaxHzTarget = (spd / MAX_SPD) * 480f; // lower ceiling than the rear motor's ~640-720Hz — no gear multiplication
        ceax_hzSmooth += (ceaxHzTarget - ceax_hzSmooth) * (ceaxHzTarget > ceax_hzSmooth ? 0.008f : 0.005f); // slower response — heavier unsprung mass
        if (ceax_hzSmooth < 0.3f) ceax_hzSmooth = 0f;
        float ceaxSpdFrac = Mathf.Clamp01(ceax_hzSmooth / 480f);
        float ceaxVolTarget = ceaxSpdFrac > 0.001f
            ? (0.032f + ceaxSpdFrac * ceaxSpdFrac * 0.065f + ld * 0.028f) * npcVolumeScale * engVolPersonality : 0f;
        ceax_volSmooth += (ceaxVolTarget - ceax_volSmooth) * 0.01f;

        double ceaxSample = 0.0;
        if (ceax_hzSmooth > 0.3f)
        {
            // Sub-harmonic-heavy stack (wheel-well structural resonance)
            // rather than the rear motor's higher slot-harmonic shimmer.
            ceaxSample = Math.Sin(2.0 * Math.PI * ph_ceax_1) * ceax_volSmooth
                       + Math.Sin(2.0 * Math.PI * ph_ceax_1 * 0.5) * ceax_volSmooth * 0.38
                       + Math.Sin(2.0 * Math.PI * ph_ceax_2) * ceax_volSmooth * 0.20;
            // Slight cogging tremor at low speed under load — same physical
            // cause as XE40's cogAM, but a hub motor's lower pole count
            // makes it read a bit coarser/slower here.
            double cogAM = 1.0 + (isStopped ? Math.Sin(2.0 * Math.PI * ph_ceax_cog) * 0.10 : 0.0);
            ph_ceax_cog = (ph_ceax_cog + 1.7 * invSR) % 1.0;
            ceaxSample = Math.Tanh(ceaxSample * cogAM * 1.12) * 0.94;
        }
        ph_ceax_1 = (ph_ceax_1 + ceax_hzSmooth         * invSR) % 1.0;
        ph_ceax_2 = (ph_ceax_2 + ceax_hzSmooth * 1.997 * invSR) % 1.0;

        txSample += ceaxSample;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP HELPER — Hydrogen Fuel Cell Balance-of-Plant (XHE40 / XHE60)
    //
    //  Shared by both XHE sizes since the aux hardware (Ballard FCmove-HD
    //  stack, cathode air compressor, stack cooling fans) is the same family
    //  regardless of chassis length — only the traction motor/axle layer
    //  differs, which is why this rides on top of DoXE40DSP/DoZFAVE130DSP
    //  rather than replacing them.
    //
    //    · CATHODE COMPRESSOR: continuous tonal whine, pitch+volume tracking
    //      electrical load — NOT cyclic like the door/brake air compressor;
    //      real FCEV cathode air compressors run continuously whenever the
    //      stack is producing power.
    //
    //      PITCH IS RPM-DERIVED, not a guessed frequency band: published
    //      research on automotive/HD fuel-cell centrifugal air compressors
    //      (SAE/Eaton commentary via Mobility Engineering Tech; Sciencedirect
    //      "Review of recent developments in fuel cell centrifugal air
    //      compressor") puts these machines at 60,000-150,000 RPM, with
    //      80,000-110,000 RPM cited as the practical size/cost/efficiency
    //      sweet spot for heavy-duty units like Ballard's FCmove-HD family.
    //      At idle/low airflow demand the compressor still has to spin (the
    //      stack needs continuous cathode air even at low power) but at a
    //      much lower speed, roughly 20,000-30,000 RPM. Rotational speed in
    //      RPM/60 gives the fundamental tone directly:
    //        20,000 RPM =  333 Hz   (idle floor)
    //        100,000 RPM = 1,667 Hz  (typical cruise/mid-load)
    //        140,000 RPM = 2,333 Hz  (heavy load ceiling)
    //      Cummins' own e-compressor documentation confirms blade-pass tone
    //      (RPM x blade count, which would land far higher — 10kHz+ on a
    //      6-8 blade impeller) is a real, deliberately-engineered-around NVH
    //      problem; enclosure/intercooler damping knocks most of that down
    //      before it reaches a rider, so it's represented here as a quiet,
    //      heavily-attenuated shimmer layer rather than a dominant tone —
    //      the earlier version made that shimmer the ONLY thing playing,
    //      which is why it never dropped out of "high-pitched" register
    //      even at idle.
    //    · STACK COOLING FAN: broadband + blade-pass tone, ramps in over a
    //      few seconds of sustained load and fades back out slowly once load
    //      eases — same thermal-proxy technique as DoOldBusCharacter's
    //      load-driven fan, but never fully off, since the fuel cell needs
    //      some airflow even near idle.
    // ═════════════════════════════════════════════════════════════════════════
    private void DoFuelCellAuxDSP(ref double txSample, float ld, float engVolPersonality, double invSR)
    {
        // Cathode air compressor — RPM-derived fundamental. Idle floor ~22k
        // RPM (continuous low-flow cathode air), climbing toward ~120k RPM
        // under heavy load/speed, inside the real 60k-150k operating window.
        float compTargetRPM = 22000f + ld * ld * 68000f + (spd / MAX_SPD) * 30000f;
        float compTargetHz  = compTargetRPM / 60f; // ~367Hz idle -> ~2000Hz heavy load
        fch_compHzSmooth += (compTargetHz - fch_compHzSmooth) * 0.03f;
        float compTargetVol = (0.010f + ld * ld * 0.024f) * npcVolumeScale * engVolPersonality;
        fch_compVolSmooth += (compTargetVol - fch_compVolSmooth) * (compTargetVol > fch_compVolSmooth ? 0.02f : 0.006f);

        // Slow surge/flutter — centrifugal compressors hunt slightly around
        // their target operating point rather than holding a razor-flat
        // pitch; a ~4-7Hz wobble at ~0.6% depth reads as "alive" instead of
        // synthesized. Rate itself drifts a little with load so it doesn't
        // read as a metronomic LFO.
        double flutterHz = 4.0 + ld * 3.0;
        double flutterAmt = Math.Sin(2.0 * Math.PI * ph_fch_flutter) * 0.006;
        ph_fch_flutter = (ph_fch_flutter + flutterHz * invSR) % 1.0;
        float compHzFluttered = fch_compHzSmooth * (1f + (float)flutterAmt);

        // Turbulence floor — high-passed noise, load-tracked, sitting well
        // under the fundamental. This is what turns "one sine wave" into
        // "air being violently compressed," without needing a literal
        // 10kHz+ blade-pass tone to sell it.
        double turbVol = (0.005 + ld * 0.011) * npcVolumeScale * engVolPersonality * (fch_compVolSmooth > 0.0005f ? 1.0 : 0.0);

        // Blade-pass shimmer — real physics puts this at RPM x blade-count
        // (multi-kHz on a 6-8 blade impeller), but it's the tone real intake
        // enclosures/intercoolers are specifically designed to knock down
        // (Cummins e-compressor NVH work), so it's represented as a quiet,
        // high, heavily-damped partial rather than a second full-volume tone.
        double bladePassVol = fch_compVolSmooth * 0.12;

        // Stack cooling fan — slow thermal-proxy ramp on sustained load, never
        // fully silent (idle floor baked into fch_fanVolSmooth's target curve).
        // [NEW] Target volume nudged up (0.012→0.014 base, 0.028→0.034 load
        // coefficient) — fuel cells reject roughly as much heat as they
        // output as electricity (~50-60% stack efficiency), so the real
        // cooling loop is proportionally bigger than a battery pack's.
        float fanLoadTarget = Mathf.Clamp01(ld * 0.7f + (spd / MAX_SPD) * 0.3f);
        fch_fanLoadSmooth += (fanLoadTarget - fch_fanLoadSmooth) * (fanLoadTarget > fch_fanLoadSmooth ? 0.004f : 0.0015f);
        float fanTargetVol = (0.014f + fch_fanLoadSmooth * 0.034f) * npcVolumeScale * engVolPersonality;
        fch_fanVolSmooth += (fanTargetVol - fch_fanVolSmooth) * 0.01f;
        double fanBladeHz = 90.0 + fch_fanLoadSmooth * 60.0;

        // [NEW] Anode purge — periodic hiss-chuff as accumulated nitrogen/
        // water/residual H2 vents through the purge valve and silencer.
        // Confirmed real behavior via fuel-cell-system patent literature:
        // cadence shortens under higher electrical load (faster nitrogen
        // crossover under harder draw = more frequent purges needed). Soft
        // noise-envelope hiss, not a hard bang — the real silencer exists
        // specifically to keep this from being a loud event.
        fch_purgeTimer += (float)invSR;
        if (fch_purgeNextAt <= 0f) fch_purgeNextAt = 18f + (float)(NextNoiseSample() * 0.5 + 0.5) * 14f;
        float purgeInterval = fch_purgeNextAt / (1f + ld * 0.8f);
        if (fch_purgeTimer >= purgeInterval) { fch_purgeTimer = 0f; fch_purgeNextAt = 0f; fch_purgeEnvVol = 1f; }
        fch_purgeEnvVol = fch_purgeEnvVol > 0.001f ? fch_purgeEnvVol * 0.9975f : 0f;
        double purgeHiss = fch_purgeEnvVol > 0.002f
            ? noise_hi * fch_purgeEnvVol * 0.05 * npcVolumeScale * engVolPersonality : 0.0;

        // [NEW] Stack coolant pump — real, previously-unmodeled hardware.
        // Continuous two-partial tonal hum, pitch nudged up slightly with load.
        float pumpHzTarget = 44f + ld * 6f;
        double fchPumpTone = Math.Sin(2.0 * Math.PI * ph_fch_pump1) * 0.010 * npcVolumeScale * engVolPersonality
                            + Math.Sin(2.0 * Math.PI * ph_fch_pump2) * 0.004 * npcVolumeScale * engVolPersonality;
        ph_fch_pump1 = (ph_fch_pump1 + pumpHzTarget       * invSR) % 1.0;
        ph_fch_pump2 = (ph_fch_pump2 + pumpHzTarget * 1.5 * invSR) % 1.0;

        // [ADD — hydrogen refresh] Membrane humidifier — small continuous
        // recirculation fan/pump keeping the PEM stack membrane hydrated,
        // separate hardware from both the cathode compressor and the
        // coolant pump above. Quiet, steady, load-tracked only a little
        // (humidifier duty is closer to constant than compressor duty).
        float humidTarget = (0.008f + ld * 0.006f) * npcVolumeScale * engVolPersonality;
        fch_humidVolSmooth += (humidTarget - fch_humidVolSmooth) * 0.006f;
        double humidTone = Math.Sin(2.0 * Math.PI * ph_fch_humid1) * fch_humidVolSmooth
                          + Math.Sin(2.0 * Math.PI * ph_fch_humid2) * fch_humidVolSmooth * 0.4;
        ph_fch_humid1 = (ph_fch_humid1 + 96.0        * invSR) % 1.0;
        ph_fch_humid2 = (ph_fch_humid2 + 96.0 * 1.5  * invSR) % 1.0;

        // [ADD — hydrogen refresh] H2 injector solenoid — fast, quiet
        // metering clicks distinct from the anode purge valve (that's a
        // periodic dump-vent hiss; this is frequent, short, ticking duty-
        // cycle clicks, faster under higher electrical load).
        float injInterval = Mathf.Lerp(0.9f, 0.22f, Mathf.Clamp01(ld));
        fch_injTimer += (float)invSR;
        if (fch_injTimer >= injInterval) { fch_injTimer = 0f; fch_injVol = 1f; }
        fch_injVol = fch_injVol > 0.001f ? fch_injVol * 0.80f : 0f;
        double injClick = fch_injVol > 0.002f
            ? noise_hi * fch_injVol * 0.018f * npcVolumeScale * engVolPersonality : 0.0;

        txSample += (fch_compVolSmooth > 0.0005f
                     ? Math.Sin(2.0 * Math.PI * ph_fch_comp1) * fch_compVolSmooth
                     + Math.Sin(2.0 * Math.PI * ph_fch_comp2) * bladePassVol
                     + noise_hi * turbVol : 0)
                  + (fch_fanVolSmooth > 0.0003f
                     ? noise_hp_prev * fch_fanVolSmooth * 0.6
                     + Math.Sin(2.0 * Math.PI * ph_fch_fan1) * fch_fanVolSmooth * 0.35 : 0)
                  + purgeHiss
                  + fchPumpTone
                  + humidTone
                  + injClick;

        ph_fch_comp1 = (ph_fch_comp1 + compHzFluttered         * invSR) % 1.0;
        ph_fch_comp2 = (ph_fch_comp2 + compHzFluttered * 5.0 * invSR) % 1.0; // blade-pass shimmer, ~5-blade impeller
        ph_fch_fan1  = (ph_fch_fan1  + fanBladeHz                * invSR) % 1.0;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP HELPER — XHE40 Hydrogen Fuel Cell-Electric (40ft)
    //
    //  Same Siemens/Accelera single direct-drive motor as XE40 — the fuel
    //  cell only replaces the battery's charge source, it doesn't change the
    //  traction hardware — so this rides on DoXE40DSP() unmodified and layers
    //  the fuel cell balance-of-plant (compressor + cooling fan) on top.
    // ═════════════════════════════════════════════════════════════════════════
    private void DoXHE40DSP(ref double engineSample, ref double txSample,
                             float ld, float engVolPersonality, double noiseHp, double invSR)
    {
        // [FIX] Used to hardcode isELFA2=true for every XHE40, which was
        // correct back when hydrogen only had one drivetrain option. Now
        // that "accelera_fc" (NextGen direct-drive motor, fuel cell just
        // swaps the charge source) exists as a real alternate pick, only
        // the "elfa2" tx gets the older/grittier Siemens-generation
        // character — Accelera-FC gets the same clean direct-drive tone
        // as its battery-electric counterpart.
        bool legacyElfa2 = tx == "elfa2" || tx == "elfa2_zfave130";
        DoXE40DSP(ref engineSample, ref txSample, ld, engVolPersonality, noiseHp, invSR, legacyElfa2);
        DoFuelCellAuxDSP(ref txSample, ld, engVolPersonality, invSR);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP HELPER — XHE60 Hydrogen Fuel Cell-Electric (60ft artic)
    //
    //  Dual-motor ZF AVE 130-style portal axle drive, same as XE60 — rides on
    //  DoZFAVE130DSP() and layers the fuel cell balance-of-plant on top.
    // ═════════════════════════════════════════════════════════════════════════
    private void DoXHE60DSP(ref double engineSample, ref double txSample,
                             float ld, float engVolPersonality, double noiseHp, double invSR)
    {
        // [FIX] tx now determines BOTH the drivetrain layout (genuine ZF
        // portal axle vs the corrected Accelera/ELFA3 rear+centre-in-wheel
        // layout) AND, for the ELFA family, whether it's the older/grittier
        // Siemens-generation ELFA2 character or the clean Accelera one.
        if (tx == "elfa2_centeraxle" || tx == "accelera_fc_centeraxle")
        {
            bool legacyElfa2 = tx == "elfa2_centeraxle";
            DoElfa3CenterAxleDSP(ref engineSample, ref txSample, ld, engVolPersonality, noiseHp, invSR, legacyElfa2);
        }
        else
        {
            // Legacy ZF-portal-axle hydrogen path — kept for old saves still
            // carrying "elfa2_zfave130"/"accelera_fc_zfave130"/"fcave130".
            bool legacyElfa2 = tx != "accelera_fc_zfave130";
            DoZFAVE130DSP(ref engineSample, ref txSample, ld, engVolPersonality, noiseHp, invSR, legacyElfa2);
        }
        DoFuelCellAuxDSP(ref txSample, ld, engVolPersonality, invSR);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP HELPER — Universal Rooftop A/C  (Thermo King TE-series-style
    //  electric scroll unit — confirmed real fit via New Flyer Xcelsior
    //  parts records)  —  REBUILT, BRAND NEW METHOD
    //
    //  Previously: one continuous broadband-hiss layer plus one compressor-
    //  hum layer, both tracking acLevel directly with no separate identity.
    //  A real rooftop unit is three separable machines behind one panel —
    //  evaporator blower, condenser/roof fan, and the variable-speed scroll
    //  compressor — plus a soft-start ramp and normal thermostatic cycling.
    //  Rebuilt to actually be those three things:
    //    · Evaporator blower — the broadband cabin hiss, own attack/release
    //      so it doesn't snap in lockstep with the compressor.
    //    · Condenser/roof fan — broadband whoosh + soft blade-pass tone,
    //      runs continuously whenever the unit is called for cooling
    //      (independent of compressor drive frequency — real rooftop units
    //      keep the fan turning through compressor modulation).
    //    · Scroll compressor — kept the grounded 25-90Hz variable-drive
    //      spec (Thermo King TE-series), now soft-started (~1.2s ease-out
    //      ramp from a low idle pitch) with a contactor pop on engage, and
    //      riding a slow (95-165s, randomized per instance) thermostatic
    //      "hunt" LFO on both pitch and volume so a long AC-on stretch
    //      reads as controlling to a band rather than a flat drone.
    //    · Refrigerant/expansion-valve hiss — a finer, higher-passed
    //      texture than the blower, present only while the compressor is
    //      actually moving refrigerant — distinct from the blower's tone.
    // ═════════════════════════════════════════════════════════════════════════
    private double DoUniversalACDSP(double rawNoise, bool acOn, float acFrac, float acVolPersonality, double invSR, bool fastRelease = false)
    {
        acNoise_lp = acNoise_lp * 0.88 + rawNoise * 0.12;

        if (acOn && !ac_wasOn) { ac_startupTimer = 0f; ac_startupPulse = 1f; ac_spinUpT = 0f; }
        ac_wasOn = acOn;
        if (ac_startupPulse > 0f) { ac_startupTimer += (float)invSR; ac_startupPulse = Mathf.Clamp01(1f - ac_startupTimer / 0.06f); }
        // [SLOWED] 1.2f -> 7f -- "takes TIME to get to that level, around 7
        // secs" -- was reaching full spin-up in ~1.2s before, nowhere close
        // to what was asked for.
        ac_spinUpT = Mathf.Clamp01(ac_spinUpT + (float)invSR / 7f);
        float spinUpEase = acOn ? (1f - (1f - ac_spinUpT) * (1f - ac_spinUpT)) : 0f;

        if (ac_satRateHz <= 0f) ac_satRateHz = 1f / (95f + (float)(NextNoiseSample() * 0.5 + 0.5) * 70f);
        double satLFO = Math.Sin(2.0 * Math.PI * ph_ac_satLFO);
        ph_ac_satLFO = (ph_ac_satLFO + ac_satRateHz * invSR) % 1.0;
        float satHzMul  = 1f + (float)satLFO * 0.05f;
        float satVolMul = 1f + (float)satLFO * 0.10f;

        // Evaporator blower.
        // [SLOWED] Attack coefficient computed directly from invSR now
        // instead of a hardcoded per-sample constant tuned for one specific
        // sample rate -- gives a genuine ~7s rise (3 time constants) to
        // reach full level, matching the compressor spin-up above instead
        // of snapping in over a few milliseconds like before. Release
        // (turning off) has two speeds: the normal smooth wind-down, or a
        // faster "abrupt with some falloff" release specifically for
        // acCyclingMode's off-phase (see fastRelease param) -- still not
        // an instant cut, just quicker than the smooth version.
        float hissTarget = acOn ? acFrac * 0.16f * npcVolumeScale * acVolPersonality : 0f;
        float hissAttackCoef  = (float)(invSR / 2.3); // ~7s to ~95%
        float hissReleaseCoef = fastRelease ? 0.05f : (float)(invSR / 0.9); // fast: abrupt-ish; smooth: ~2.7s
        ac_hissVolSmooth += (hissTarget - ac_hissVolSmooth) * (hissTarget > ac_hissVolSmooth ? hissAttackCoef : hissReleaseCoef);
        double acSample = acNoise_lp * ac_hissVolSmooth;

        // Condenser / roof fan.
        float fanTarget = acOn ? (0.020f + acFrac * 0.010f) * npcVolumeScale * acVolPersonality : 0f;
        float fanAttackCoef  = (float)(invSR / 2.3);
        float fanReleaseCoef = fastRelease ? 0.045f : (float)(invSR / 0.9);
        ac_fanVolSmooth += (fanTarget - ac_fanVolSmooth) * (fanTarget > ac_fanVolSmooth ? fanAttackCoef : fanReleaseCoef);
        if (ac_fanVolSmooth > 0.0004f)
        {
            double fanBladeHz = 108.0 + acFrac * 26.0;
            acSample += noise_lo * ac_fanVolSmooth * 0.55
                      + Math.Sin(2.0 * Math.PI * ph_ac_fan1) * ac_fanVolSmooth * 0.30
                      + Math.Sin(2.0 * Math.PI * ph_ac_fan2) * ac_fanVolSmooth * 0.12;
            ph_ac_fan1 = (ph_ac_fan1 + fanBladeHz       * invSR) % 1.0;
            ph_ac_fan2 = (ph_ac_fan2 + fanBladeHz * 2.0 * invSR) % 1.0;
        }

        // Scroll compressor — soft-started, thermostatic hunt riding on top.
        float acCompTargetHz = (acOn ? Mathf.Lerp(25f, 90f, acFrac) : 32f) * satHzMul;
        float rampedTargetHz = Mathf.Lerp(18f, acCompTargetHz, spinUpEase);
        ac_compHzSmooth += (rampedTargetHz - ac_compHzSmooth) * 0.012f;
        float acCompTargetVol = acOn
            ? (0.009f + acFrac * acFrac * 0.018f) * npcVolumeScale * acVolPersonality * satVolMul * spinUpEase : 0f;
        float compReleaseCoef = fastRelease ? 0.06f : (float)(invSR / 0.8);
        ac_compVolSmooth += (acCompTargetVol - ac_compVolSmooth) * (acCompTargetVol > ac_compVolSmooth ? 0.015f : compReleaseCoef);
        if (ac_compVolSmooth > 0.0003f)
        {
            acSample += Math.Sin(2.0 * Math.PI * ph_ac_comp1) * ac_compVolSmooth
                      + Math.Sin(2.0 * Math.PI * ph_ac_comp2) * ac_compVolSmooth * 0.30;
            // Refrigerant/expansion-valve hiss — finer/higher texture than the blower.
            acSample += noise_hi * ac_compVolSmooth * 0.22;
        }
        ph_ac_comp1 = (ph_ac_comp1 + ac_compHzSmooth       * invSR) % 1.0;
        ph_ac_comp2 = (ph_ac_comp2 + ac_compHzSmooth * 2.0 * invSR) % 1.0;

        if (ac_startupPulse > 0f) acSample += NextNoiseSample() * ac_startupPulse * 0.05 * npcVolumeScale;

        return acSample;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP — D864.6art "TASTY VOITH"  (tx == "d8646art", 2200-series XN60 CNG artic)
    //
    //  Full standalone rebuild of the DIWA.6 voice for the 60-foot artic, from
    //  the same measured exterior reference the v2 D864.6 rebuild used:
    //
    //    · DRIVE WHINE: engine-order mesh, 23×rpm/60 in G1, 21.5× in G2+ —
    //      ~340→780 Hz across the launch climb, sawtoothing down at each
    //      upshift. On the artic this whine carries MORE of the mix (bigger
    //      final drive, more load, the mesh "sings"): hotter gain, brighter
    //      harmonic stack, and a fixed body-resonance formant near 1.9 kHz
    //      that the harmonics sweep through — that's the "squeal" moment when
    //      a partial crosses the resonance and momentarily blooms.
    //    · CONVERTER SCREAM ("the highness"): in G1 near the governor the
    //      converter's fluid coupling shrieks a detuned dyad above the mesh —
    //      envelope opens past ~80% GOV, closes instantly on the 1→2 shift.
    //    · RETARDER WHINE: output-order (tracks ROAD SPEED, not rpm),
    //      descending ~780→330 Hz under braking, harmonics to ~3.4 kHz.
    //      Prominent — this is the DIWA's signature deceleration screech.
    //    · LAUNCH "REVVV" SURGE: hard tip-in in G1/G2 pitch-overshoots the
    //      whole whine bank ~6% with a fast-attack/slow-decay envelope — the
    //      eager rev-flare character before the converter catches up.
    //    · TURBO (NEW): the L9N's turbocharger — spool whistle rising
    //      ~2.4→5.2 kHz with boost (throttle×load proxy), plus a wastegate
    //      flutter burst ("psh-sh-sh") on a hard throttle lift while spooled.
    //    · MOTOR RASP (KEPT from v1 art): XE40-shape sine bank (fund+2nd+3rd)
    //      tracked to revs, tanh-saturated so it reads gritty — the electric-
    //      adjacent texture that makes the art unit unmistakable vs. stock.
    //    · DEEP HIGH-RPM GROWL (KEPT): sub-oscillator past ~70% GOV — the top
    //      of the rev range digs in instead of just getting whinier.
    //    · ARTIC BODY: heavier move-off grab (60 ft of bus behind the
    //      converter), a rear-section driveline-lash "clonk" trailing each
    //      shift thud, converter churn with a longer fill, pneumatic shift
    //      sigh + brake-pack seat borrowed from the base architecture but
    //      weighted for the bigger box.
    //
    //  Shares NO smoothing state with DoVoithDSP — an art unit and a stock
    //  D864.6 in the same scene can never fight over v6_ fields.
    // ═════════════════════════════════════════════════════════════════════════
    private void DoD8646ArtDSP(ref double engineSample, ref double txSample, float rn, float ld, float hz,
                                float outRPM, float engMul, bool retAct, double invSR)
    {
        /*
        float g1Top      = VOITH_GEAR1_UPSHIFT_SPD * 1.15f * (kickdownKey ? 1.32f : 1f);
        float g1Progress = gear == 1 ? Mathf.Clamp01(spd / Mathf.Max(1f, g1Top)) : 0f;
        float hydShare   = gear == 1 ? (1f - g1Progress) * (1f - g1Progress) : 0f;
        float rnClamp    = Mathf.Clamp01(rn);
        float ldClamp    = Mathf.Clamp01(ld);

        // ── 1. DRIVE WHINE CORE (engine-order mesh, artic-hot) ────────────────

    // ═════════════════════════════════════════════════════════════════════════
    //  2. DRIVE WHINE (DIWA GEAR MESH VOICE)
    // ═════════════════════════════════════════════════════════════════════════
    {
        float teeth = gear <= 1 ? 23.0f : 21.5f;
        float whTgt = (Mathf.Max(rpm, IDLE) / 60f) * teeth;

        // ── Upshift whine transition -- see field comment on v6_whRelockWin.
        bool isG1toG2 = (v6_lastGear == 1 && gear == 2);
        if (v6_lastGear != -1 && v6_lastGear != gear)
        {
            if (isG1toG2)
            {
                v6_whHoldTimer = 0f;
                v6_whHzSmooth  = whTgt;
            }
            else
            {
                v6_whHoldTimer = 0.20f;
                v6_whHoldHz    = v6_whHzSmooth;
                v6_whRelockWin = 0.22f;
            }
        }
        v6_lastGear = gear;

        if (v6_whHoldTimer > 0f)
        {
            v6_whHoldTimer -= (float)invSR;
            v6_whHzSmooth   = v6_whHoldHz; 
        }
        else if (v6_whRelockWin > 0f)
        {
            v6_whRelockWin -= (float)invSR;
            float relockPtau = (float)invSR / 0.20f;
            v6_whHzSmooth += (whTgt - v6_whHzSmooth) * relockPtau;
        }
        else
        {
            float ptau = whTgt > v6_whHzSmooth ? 0.040f : 0.010f;
            v6_whHzSmooth += (whTgt - v6_whHzSmooth) * ptau;
        }

        // [MODIFIED] Whine steps down drastically outside of 1st gear launch phase
        float gearMul = 1.0f;
        if (gear == 2 || gear == 3) gearMul = 0.38f; 
        else if (gear >= 4)         gearMul = 0.18f;

        float whTarget = 0f;
        if (gear >= 1 && rpm > IDLE * 0.9f)
        {
            whTarget = (0.020f + rn * 0.055f + ld * 0.030f) * gearMul;
            if (gear == 1) whTarget *= (0.55f + 0.45f * g1Progress);
            if (spd < 0.5f && accel < 0.03f) whTarget *= 0.25f;     
        }
        v6_whVolSmooth += (whTarget - v6_whVolSmooth) * 0.006f;


        float screechThresh = GOV * 0.86f;
        float screechRaw    = Mathf.Clamp01((Mathf.Max(rpm, IDLE) - screechThresh) / Mathf.Max(1f, GOV - screechThresh));
        v6_screechSmooth += (screechRaw - v6_screechSmooth) * (screechRaw > v6_screechSmooth ? 0.0035f : 0.008f);

        if (v6_whVolSmooth > 0.0008f && v6_whHzSmooth > 40f)
        {
            float rnPitch = Mathf.Clamp01((rpm - IDLE) / (GOV - IDLE));

            double p1 = Mathf.Lerp(1.75f, 2.45f, rnPitch);
            double p2 = Mathf.Lerp(1.90f, 2.75f, rnPitch);
            double p3 = Mathf.Lerp(2.10f, 3.10f, rnPitch);

            double w1 = Math.Sin(p1 * Math.PI * ph_v6_wh1);
            double w2 = Math.Sin(p2 * Math.PI * ph_v6_wh2);
            double w3 = Math.Sin(p3 * Math.PI * ph_v6_wh3);

            double chord = w1 * 1.00 + w2 * 0.48 + w3 * 0.24;

            if (v6_screechSmooth > 0.001f)
            {
                double w4 = Math.Sin(2.0 * Math.PI * ph_v6_wh4); 
                chord += w4 * 0.34 * v6_screechSmooth;
                chord += noise_hi * 0.22 * v6_screechSmooth;
            }

            txSample += (chord + noise_hi * 0.06) * v6_whVolSmooth * engMul;
        }
    }
        // ── 3. LAUNCH "REVVV" SURGE — tip-in pitch overshoot ──────────────────
        {
            if (gear >= 1 && gear <= 2 && accel - d6a_prevAccel > 0.06f && rnClamp > 0.25f)
            {
                d6a_surgeVol   = Mathf.Max(d6a_surgeVol, (0.5f + ldClamp * 0.5f));
                d6a_surgePitch = 1.06f;
            }
            d6a_prevAccel = accel;
            d6a_surgeVol   *= 0.9985f;
            d6a_surgePitch += (1f - d6a_surgePitch) * 0.008f;
            if (d6a_surgeVol < 0.002f) d6a_surgeVol = 0f;
            // (surgePitch is applied in the phase-advance block below — the
            //  entire whine bank momentarily runs sharp, then settles.)
        }

        // ── 4. TURBO — spool whistle + wastegate flutter (L9N CNG) ────────────
        {
            float spool = Mathf.Clamp01(rnClamp * 0.62f + ldClamp * 0.38f);
            float turboHzTgt = Mathf.Lerp(2400f, 5200f, spool * spool);
            d6a_turboHzSmooth += (turboHzTgt - d6a_turboHzSmooth) * 0.010f;
            float turboVolTgt = spool > 0.12f ? (spool - 0.12f) * 0.030f : 0f;
            d6a_turboVolSmooth += (turboVolTgt - d6a_turboVolSmooth) * (turboVolTgt > d6a_turboVolSmooth ? 0.008f : 0.020f);

            if (d6a_turboVolSmooth > 0.0004f)
            {
                txSample += (Math.Sin(2.0 * Math.PI * ph_d6a_turbo1) * 0.6
                           + Math.Sin(2.0 * Math.PI * ph_d6a_turbo2) * 0.25
                           + noise_hi * 0.35) * d6a_turboVolSmooth * engMul;
            }

            // Wastegate flutter: hard throttle lift while spooled → short
            // "psh-sh-sh" AM'd noise burst, pitch falling with the spool.
            if (d6a_prevSpool - spool > 0.045f && d6a_prevSpool > 0.45f)
                d6a_wgFlutterVol = Mathf.Max(d6a_wgFlutterVol, 0.06f * engMul);
            d6a_prevSpool = spool;
            if (d6a_wgFlutterVol > 0.0006f)
            {
                double flutter = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * ph_d6a_wg);
                txSample += (noise_hi * 0.8 + noise_lp * 0.2) * flutter * d6a_wgFlutterVol;
                d6a_wgFlutterVol *= 0.9970f;
            }
        }

        // ── 5. XE40-STYLE MOTOR RASP (kept from v1 art) ───────────────────────
        {
            float raspHzTgt = 90f + rnClamp * 560f;
            d6a_raspHzSmooth += (raspHzTgt - d6a_raspHzSmooth) * (raspHzTgt > d6a_raspHzSmooth ? 0.05f : 0.03f);
            float raspVol = (0.032f + rnClamp * rnClamp * 0.058f + ldClamp * 0.030f) * engMul; // ~30% less deep
            double raspFund = Math.Sin(2.0 * Math.PI * ph_d6a_rasp)
                             + Math.Sin(2.0 * Math.PI * ph_d6a_rasp * 2.0) * 0.45
                             + Math.Sin(2.0 * Math.PI * ph_d6a_rasp * 2.997) * 0.22;
            txSample += Math.Tanh(raspFund * (1.15f + ldClamp * 0.85f)) * raspVol   // softer drive → less clip-noise
                      + noise_hi * raspVol * 0.10;
        }

        // ── 6. DEEP HIGH-RPM GROWL (kept) ─────────────────────────────────────
        {
            float deepGate = Mathf.Clamp01(Mathf.InverseLerp(0.70f, 0.97f, rnClamp));
            float deepTgt = deepGate * deepGate * (0.07f + ldClamp * 0.06f); // pulled back ~30%, less depth
            d6a_deepVolSmooth += (deepTgt - d6a_deepVolSmooth) * 0.010f;
            if (d6a_deepVolSmooth > 0.001f)
            {
                txSample += (Math.Tanh(Math.Sin(2.0 * Math.PI * ph_d6a_deep1) * 1.4)
                           + Math.Sin(2.0 * Math.PI * ph_d6a_deep2) * 0.4) * d6a_deepVolSmooth * engMul;
            }
        }

        // ── 7. CONVERTER CHURN — longer artic fill ────────────────────────────
        {
            float churnTarget = 0f;
            if (gear == 1)
            {
                churnTarget = hydShare * (0.30f + ld * 0.80f); // heavier than stock
                if (spd < 2f) churnTarget = Mathf.Max(churnTarget, 0.26f + ld * 0.32f);
            }
            d6a_churnEnv += (churnTarget - d6a_churnEnv) * (churnTarget > d6a_churnEnv ? 0.008f : 0.0035f);
            if (d6a_churnEnv > 0.004f)
            {
                d6a_churnLP += (noise_lp - d6a_churnLP) * 0.22;
                double swirl = 0.80 + 0.20 * Math.Sin(2.0 * Math.PI * ph_d6a_churnMod);
                txSample += d6a_churnLP * swirl * d6a_churnEnv * 0.085 * engMul; // less deep fill
            }
        }

        // ── 8. RETARDER WHINE — output-order, THE deceleration screech ───────
        {
            // Fundamental tracks ROAD SPEED (turbine re-coupled to output under
            // braking): descending ~780 → 330 Hz as the bus slows.
            float rwHzTgt = Mathf.Lerp(330f, 780f, Mathf.Clamp01(spd / 60f));
            d6a_rwhHzSmooth += (rwHzTgt - d6a_rwhHzSmooth) * 0.02f;

            float rwTgt = (retAct && spd > 2f) ? (0.045f + bkPd * 0.075f) : 0f;
            d6a_rwhVolSmooth += (rwTgt - d6a_rwhVolSmooth) * (rwTgt > d6a_rwhVolSmooth ? 0.006f : 0.012f);

            if (d6a_rwhVolSmooth > 0.0006f)
            {
                double r1 = Math.Sin(2.0 * Math.PI * ph_d6a_rwh1);
                double r2 = Math.Sin(2.0 * Math.PI * ph_d6a_rwh2);
                double r3 = Math.Sin(2.0 * Math.PI * ph_d6a_rwh3);
                // Harmonics visible to ~3.4 kHz in the reference — keep the
                // upper partials genuinely present, with a noise shimmer.
                txSample += (r1 * 1.00 + r2 * 0.55 + r3 * 0.30 + noise_hi * 0.18)
                          * d6a_rwhVolSmooth * engMul;
            }
        }

        // ── 9. SHIFT EVENTS — sigh, brake-pack seat, thud, artic lash ─────────
        if (gear != d6a_lastGearAudio)
        {
            bool upFrom1 = (d6a_lastGearAudio == 1 && gear == 2);
            bool wasUpshift = gear > d6a_lastGearAudio;
            d6a_lastGearAudio = gear;
            d6a_sighVol = upFrom1 ? 0.9f : 0.55f;
            d6a_seatVol = upFrom1 ? 0.7f : 0.45f;
            d6a_thudVol = 1.0f;
            // Artic: the trailer section's driveline lash arrives a beat after
            // the thud — seeded here, played below with its own decay.
            d6a_lashVol = upFrom1 ? 0.65f : 0.40f;
            if (upFrom1) d6a_churnEnv = Mathf.Min(d6a_churnEnv, 0.05f);
            // Kick off the post-shift stall flutter on upshifts — the deep
            // "aruhhh-tu-tu-tu-tu" sputter, like a plane engine briefly
            // stalling/lawnmower chatter as the converter re-couples.
            if (wasUpshift) d6a_stallFlutterTimer = 0.55f;
        }
        if (d6a_stallFlutterTimer > 0f)
        {
            d6a_stallFlutterTimer -= (float)invSR;
            float ft = Mathf.Clamp01(d6a_stallFlutterTimer / 0.55f); // 1 → 0 across the event
            float envShape = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ft * 2.2f)) * ft; // quick swell, then dies out

            float flutterHz = 62f + 10f * Mathf.Sin((float)audioClock * 6.5f); // deep, slightly wavering
            ph_d6a_stallFlutter = (ph_d6a_stallFlutter + flutterHz * invSR) % 1.0;

            // fast duty-cycled gate carves the tone into "tu-tu-tu-tu" pulses (~17Hz)
            ph_d6a_stallGate = (ph_d6a_stallGate + 17.0 * invSR) % 1.0;
            double gate = ph_d6a_stallGate < 0.42 ? 1.0 : 0.0;

            double tone = Math.Sin(2.0 * Math.PI * ph_d6a_stallFlutter)
                        + Math.Sin(2.0 * Math.PI * ph_d6a_stallFlutter * 1.98) * 0.5;
            double sputter = Math.Tanh(tone * 2.6) * gate + noise_lp * 0.18 * gate;

            txSample += sputter * envShape * 0.16 * engMul;
        }
        if (d6a_sighVol > 0.004f)
        {
            txSample += (noise_lp * 0.6 + noise_hi * 0.4) * d6a_sighVol * 0.060 * engMul;
            d6a_sighVol *= 0.9982f;
        }
        if (d6a_seatVol > 0.004f)
        {
            txSample += Math.Sin(2.0 * Math.PI * ph_d6a_seat) * d6a_seatVol * 0.055 * engMul;
            d6a_seatVol *= 0.9970f;
        }
        if (d6a_thudVol > 0f)
        {
            txSample += Math.Sin(2.0 * Math.PI * ph_d6a_thud1) * d6a_thudVol * 0.26 * engMul; // heavier than stock
            txSample += Math.Sin(2.0 * Math.PI * ph_d6a_thud2) * d6a_thudVol * 0.14 * engMul;
            d6a_thudVol *= 0.9973f;
            if (d6a_thudVol < 0.004f) d6a_thudVol = 0f;
        }
        if (d6a_lashVol > 0.004f)
        {
            // Lash lags the thud: gate it by (1 - thudVol) so it swells as the
            // thud dies — clonk from the rear section a moment later.
            float lashNow = d6a_lashVol * Mathf.Clamp01(1f - d6a_thudVol * 1.6f);
            if (lashNow > 0.003f)
                txSample += Math.Tanh(Math.Sin(2.0 * Math.PI * ph_d6a_lash) * 2.2) * lashNow * 0.09 * engMul;
            d6a_lashVol *= 0.9968f;
        }

        // ── 10. MOVE-OFF GRAB — 60 feet of bus behind the converter ──────────
        {
            bool stoppedNow = spd < 0.4f;
            if (d6a_wasStopped && !stoppedNow && accel > 0.05f) d6a_moveOffTimer = 0.62f; // longer than stock
            d6a_wasStopped = stoppedNow;
            if (d6a_moveOffTimer > 0f)
            {
                d6a_moveOffTimer -= (float)invSR;
                float t = Mathf.Clamp01(d6a_moveOffTimer / 0.62f);
                float grabHz = 42f + (1f - t) * 18f; // artic grabs lower
                float judder = 0.5f + 0.5f * Mathf.Sin((float)audioClock * Mathf.PI * 2f * 9.5f);
                double grab  = (Math.Sin(2.0 * Math.PI * ph_d6a_grab) * 0.8 + noise_lp * 0.45)
                               * t * (0.10f + judder * 0.06f) * (0.5f + ld * 0.5f) * engMul;
                txSample += grab;
                ph_d6a_grab = (ph_d6a_grab + grabHz * invSR) % 1.0;
            }
        }

        // ── 11. STOCK DIWA DRIVE WHINE OVERLAY (V6 gear-mesh voice, layered
        // beneath the artic's own whine core in section 1 — brings back the
        // original stock mesh tone as a second, quieter voice under the hood.)
        {
            float teeth = gear <= 1 ? 23.0f : 21.5f;
            float whTgt = (Mathf.Max(rpm, IDLE) / 60f) * teeth;

            // ── Upshift whine transition -- see field comment on v6_whRelockWin.
            bool isG1toG2 = (v6_lastGear == 1 && gear == 2);
            if (v6_lastGear != -1 && v6_lastGear != gear)
            {
                if (isG1toG2)
                {
                    v6_whHoldTimer = 0f;
                    v6_whHzSmooth  = whTgt;
                }
                else
                {
                    v6_whHoldTimer = 0.20f;
                    v6_whHoldHz    = v6_whHzSmooth;
                    v6_whRelockWin = 0.22f;
                }
            }
            v6_lastGear = gear;

            if (v6_whHoldTimer > 0f)
            {
                v6_whHoldTimer -= (float)invSR;
                v6_whHzSmooth   = v6_whHoldHz;
            }
            else if (v6_whRelockWin > 0f)
            {
                v6_whRelockWin -= (float)invSR;
                float relockPtau = (float)invSR / 0.20f;
                v6_whHzSmooth += (whTgt - v6_whHzSmooth) * relockPtau;
            }
            else
            {
                float ptau = whTgt > v6_whHzSmooth ? 0.040f : 0.010f;
                v6_whHzSmooth += (whTgt - v6_whHzSmooth) * ptau;
            }

            float gearMul = 1.0f;
            if (gear == 2 || gear == 3) gearMul = 0.38f;
            else if (gear >= 4)         gearMul = 0.18f;

            float whTarget = 0f;
            if (gear >= 1 && rpm > IDLE * 0.9f)
            {
                whTarget = (0.020f + rn * 0.055f + ld * 0.030f) * gearMul;
                if (gear == 1) whTarget *= (0.55f + 0.45f * g1Progress);
                if (spd < 0.5f && accel < 0.03f) whTarget *= 0.25f;
            }
            v6_whVolSmooth += (whTarget - v6_whVolSmooth) * 0.006f;


            float screechThresh = GOV * 0.86f;
            float screechRaw    = Mathf.Clamp01((Mathf.Max(rpm, IDLE) - screechThresh) / Mathf.Max(1f, GOV - screechThresh));
            v6_screechSmooth += (screechRaw - v6_screechSmooth) * (screechRaw > v6_screechSmooth ? 0.0035f : 0.008f);

            if (v6_whVolSmooth > 0.0008f && v6_whHzSmooth > 40f)
            {
                float rnPitch = Mathf.Clamp01((rpm - IDLE) / (GOV - IDLE));
                double p1 = Mathf.Lerp(1.75f, 2.45f, rnPitch);
                double p2 = Mathf.Lerp(1.90f, 2.75f, rnPitch);
                double p3 = Mathf.Lerp(2.10f, 3.10f, rnPitch);

                double w1 = Math.Sin(p1 * Math.PI * ph_v6_wh1);
                double w2 = Math.Sin(p2 * Math.PI * ph_v6_wh2);
                double w3 = Math.Sin(p3 * Math.PI * ph_v6_wh3);

                double chord = w1 * 1.00 + w2 * 0.48 + w3 * 0.24;

                if (v6_screechSmooth > 0.001f)
                {
                    double w4 = Math.Sin(2.0 * Math.PI * ph_v6_wh4);
                    chord += w4 * 0.34 * v6_screechSmooth;
                    chord += noise_hi * 0.22 * v6_screechSmooth;
                }

                // Dialed back to ~45% — sits under the artic's own whine core,
                // doesn't compete with it.
                txSample += (chord + noise_hi * 0.06) * v6_whVolSmooth * 0.45 * engMul;
            }
        }

        // ── 12. ELECTRIC BACKGROUND MOTOR DRONE (RESIDUAL LAYER, kept from
        // the XE background layer — faint pitch-scaled hum under the diesel
        // voice above.) ────────────────────────────────────────────────────
        {
            xe_motorHzSmooth = (spd / MAX_SPD) * 720f;
            if (xe_motorHzSmooth < 0.3f) xe_motorHzSmooth = 0f;

            float droneGearTarget = (gear == 1) ? 1.2f : 0.3f;
            v5_xeDroneSmooth += (droneGearTarget - v5_xeDroneSmooth) * (droneGearTarget > v5_xeDroneSmooth ? 0.15f : 0.05f);

            float motSpdFrac = Mathf.Clamp01(xe_motorHzSmooth / 720f);
            float continuousMotorVol = motSpdFrac > 0.001f
                ? (0.04f + motSpdFrac * 0.08f + ld * 0.04f) * engMul * (1f + ld * 0.35f) * v5_xeDroneSmooth
                : 0f;

            if (continuousMotorVol > 0.0005f)
            {
                txSample += Math.Sin(2.0 * Math.PI * ph_xe_mot1 * 0.85) * continuousMotorVol
                          + Math.Sin(2.0 * Math.PI * ph_xe_mot2 * 0.85 * 1.998 / 2.0) * continuousMotorVol * 0.4;
            }
        }

        // ── 13. PHASE ACCUMULATION ────────────────────────────────────────────
        double whAdvance = (double)d6a_whHzSmooth * d6a_surgePitch; // surge runs the bank sharp
        ph_d6a_wh1     = (ph_d6a_wh1     + whAdvance          * invSR) % 1.0;
        ph_d6a_wh2     = (ph_d6a_wh2     + whAdvance * 2.003  * invSR) % 1.0;
        ph_d6a_wh3     = (ph_d6a_wh3     + whAdvance * 3.01   * invSR) % 1.0;
        ph_d6a_wh5     = (ph_d6a_wh5     + whAdvance * 4.98   * invSR) % 1.0;
        ph_d6a_scream1 = (ph_d6a_scream1 + whAdvance * 1.19 * d6a_screamPitchMul * invSR) % 1.0;
        ph_d6a_scream2 = (ph_d6a_scream2 + whAdvance * 1.51 * d6a_screamPitchMul * invSR) % 1.0;
        ph_d6a_rwh1    = (ph_d6a_rwh1    + (double)d6a_rwhHzSmooth        * invSR) % 1.0;
        ph_d6a_rwh2    = (ph_d6a_rwh2    + (double)d6a_rwhHzSmooth * 2.01 * invSR) % 1.0;
        ph_d6a_rwh3    = (ph_d6a_rwh3    + (double)d6a_rwhHzSmooth * 3.02 * invSR) % 1.0;
        ph_d6a_churnMod= (ph_d6a_churnMod+ 1.7                            * invSR) % 1.0;
        ph_d6a_turbo1  = (ph_d6a_turbo1  + (double)d6a_turboHzSmooth      * invSR) % 1.0;
        ph_d6a_turbo2  = (ph_d6a_turbo2  + (double)d6a_turboHzSmooth * 1.5* invSR) % 1.0;
        ph_d6a_wg      = (ph_d6a_wg      + 26.0                           * invSR) % 1.0;
        ph_d6a_deep1   = (ph_d6a_deep1   + (42.0 + rnClamp * 26.0)        * invSR) % 1.0;
        ph_d6a_deep2   = (ph_d6a_deep2   + (42.0 + rnClamp * 26.0) * 1.503 * invSR) % 1.0;
        ph_d6a_rasp    = (ph_d6a_rasp    + (double)d6a_raspHzSmooth       * invSR) % 1.0;
        ph_d6a_lash    = (ph_d6a_lash    + 64.0                           * invSR) % 1.0;
        ph_d6a_seat    = (ph_d6a_seat    + 96.0                           * invSR) % 1.0;
        ph_d6a_thud1   = (ph_d6a_thud1   + 52.0                           * invSR) % 1.0;
        ph_d6a_thud2   = (ph_d6a_thud2   + 34.0                           * invSR) % 1.0;
        // Stock drive-whine overlay + electric drone overlay phase advance
        ph_v6_wh1      = (ph_v6_wh1      + (double)v6_whHzSmooth          * invSR) % 1.0;
        ph_v6_wh2      = (ph_v6_wh2      + (double)v6_whHzSmooth  * 2.003 * invSR) % 1.0;
        ph_v6_wh3      = (ph_v6_wh3      + (double)v6_whHzSmooth  * 3.01  * invSR) % 1.0;
        ph_v6_wh4      = (ph_v6_wh4      + (double)v6_whHzSmooth  * 4.98  * invSR) % 1.0;
        ph_xe_mot1     = (ph_xe_mot1     + (double)xe_motorHzSmooth       * invSR) % 1.0;
        ph_xe_mot2     = (ph_xe_mot2     + (double)xe_motorHzSmooth * 2.0 * invSR) % 1.0;
        */
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DSP HELPER — Old Bus character
    // ═════════════════════════════════════════════════════════════════════════
    private void DoOldBusCharacter(ref double ob, float rn, float ld, float hz,
                                    float outRPM, float engVolPersonality, double noiseHp, double invSR)
    {
        float rnClamp = Mathf.Clamp01(rn);
        if (ob_deepMoan)
        {
            double mHz = 26.0 + rnClamp * 16.0, mHz2 = mHz * 1.503;
            float mV = (0.09f + rn * 0.08f + ld * 0.05f) * ob_deepMoanAmt * engVolPersonality * npcVolumeScale;
            ob += Math.Sin(2.0 * Math.PI * ph_ob_moan) * mV + Math.Sin(2.0 * Math.PI * ph_ob_moan2) * mV * 0.5;
            ph_ob_moan  = (ph_ob_moan  + mHz  * invSR) % 1.0;
            ph_ob_moan2 = (ph_ob_moan2 + mHz2 * invSR) % 1.0;
        }
        if (ob_worn_whine)
        {
            double wHz = 160.0 + outRPM * 0.6 + rn * 60.0, wHz2 = wHz * 1.012;
            float wV = (0.030f + (spd / MAX_SPD) * 0.035f + rn * 0.02f) * ob_whineAmt * npcVolumeScale;
            ob += Math.Sin(2.0 * Math.PI * ph_ob_whine) * wV + Math.Sin(2.0 * Math.PI * ph_ob_whine2) * wV * 0.6;
            ph_ob_whine  = (ph_ob_whine  + wHz  * invSR) % 1.0;
            ph_ob_whine2 = (ph_ob_whine2 + wHz2 * invSR) % 1.0;
        }
        if (ob_airRush)
        {
            float aV = (0.025f + Mathf.Pow(Mathf.Clamp01(spd / MAX_SPD), 1.4f) * 0.10f) * ob_airRushAmt * npcVolumeScale;
            ob += noise_hi * aV + Math.Sin(2.0 * Math.PI * ph_ob_airRush) * aV * 0.3;
            ph_ob_airRush = (ph_ob_airRush + (16.0 + (spd / MAX_SPD) * 10.0) * invSR) % 1.0;
        }
        if (ob_roar)
        {
            float rV = (0.045f + ld * 0.09f + rn * 0.06f) * ob_roarAmt * engVolPersonality * npcVolumeScale;
            double r1 = Math.Sin(2.0 * Math.PI * ph_ob_roar), r2 = Math.Sin(2.0 * Math.PI * ph_ob_roar2);
            ob += Math.Tanh((r1 + r2 * 0.5) * 1.8) * rV + noise_lp * rV * 0.25;
            ph_ob_roar  = (ph_ob_roar  + hz * 0.5       * invSR) % 1.0;
            ph_ob_roar2 = (ph_ob_roar2 + hz * 0.5 * 1.98 * invSR) % 1.0;
        }
        if (ob_rattle)
        {
            double rattHz = 22.0 + rnClamp * 18.0;
            float  rattV  = (0.022f + rn * 0.025f + ld * 0.018f) * ob_rattleAmt * npcVolumeScale;
            ob += (Math.Abs(ph_ob_rattle % 1.0 - 0.5) * 4.0 - 1.0) * rattV + noiseHp * rattV * 0.4;
            ph_ob_rattle = (ph_ob_rattle + rattHz * invSR) % 1.0;
        }
        if (ob_beltSqueal)
        {
            float sqEnv = 0.55f + 0.45f * Mathf.Sin((float)audioClock * 0.8f);
            double bHz = 1850.0 + Math.Sin(audioClock * 5.0) * 150.0, bHz2 = bHz * 1.503;
            float bV = sqEnv * 0.045f * ob_beltSquealAmt * npcVolumeScale;
            ob += Math.Sin(2.0 * Math.PI * ph_ob_belt) * bV + Math.Sin(2.0 * Math.PI * ph_ob_belt2) * bV * 0.5;
            ph_ob_belt  = (ph_ob_belt  + bHz  * invSR) % 1.0;
            ph_ob_belt2 = (ph_ob_belt2 + bHz2 * invSR) % 1.0;
        }
        if (ob_exhaustChuff)
        {
            ob_chuffTimer += (float)invSR;
            if (ob_chuffNextAt <= 0f) ob_chuffNextAt = 3f + (float)(NextNoiseSample() * 0.5 + 0.5) * 6f;
            if (ob_chuffTimer >= ob_chuffNextAt) { ob_chuffTimer = 0f; ob_chuffNextAt = 3f + (float)(NextNoiseSample() * 0.5 + 0.5) * 6f; ph_ob_chuff = 0.0; }
            if (ph_ob_chuff < 0.12)
            {
                float cEnv = (float)(1.0 - ph_ob_chuff / 0.12);
                float cV = cEnv * cEnv * 0.16f * ob_exhaustChuffAmt * npcVolumeScale;
                ob += Math.Sin(2.0 * Math.PI * ph_ob_chuff * ((36.0 + ld * 14.0) / 36.0)) * cV + noise_lp * cV * 0.5;
                ph_ob_chuff += invSR;
            }
        }
        if (ob_doorWheeze)
        {
            float wV2 = (0.012f + (1f - Mathf.Clamp01(spd / 8f)) * 0.025f) * ob_doorWheezeAmt * npcVolumeScale;
            ob += noise_hp_prev * wV2 * 0.6 + noise_lp * wV2 * 0.4;
        }
        // ── Cold idle stumble: random RPM-correlated misfire flutter, worse on
//    diesels (L9/ISL9/X10) than CNG (L9N already has its own wobble) ──────
if (ob_roar && engineType != EngineType.L9N && rn < 0.08f)
{
    double stumbleHz = 3.0 + (NextNoiseSample() * 0.5 + 0.5) * 2.0;
    float stumbleVol = (float)(0.012 + (NextNoiseSample() * 0.5 + 0.5) * 0.01) * ob_roarAmt * npcVolumeScale;
    ob += noise_lp * stumbleVol * Mathf.Max(0f, (float)Math.Sin(audioClock * stumbleHz));
}

// ── TX-specific worn character ─────────────────────────────────────────
if (ob_worn_whine)
{
    if (IsAllison() || tx == "b500r" || tx == "b3400xfe")
    {
        // Old Allison: looser planetary, audible whenever TC is unlocked
        float clunkVol = (0.020f + ld * 0.030f) * ob_whineAmt * npcVolumeScale;
        ob += noiseHp * clunkVol * (gear <= 2 ? 1f : 0.3f);
    }
    else if (tx == "voith" || tx == "d8645" || tx == "d8646")
    {
        // Old Voith: extra converter slip hiss, worse than stock spec
        // [FIX] "d8646" was missing from this check -- base D8646 used to
        // unconditionally alias to "voith" for the whole tick, so it always
        // matched here. Now that it keeps its own literal tx string for
        // the opt-less case, this check silently stopped applying the
        // worn-bus character to it. Added back explicitly.
        float slipVol = (0.018f + ld * 0.022f) * ob_whineAmt * npcVolumeScale * (gear <= 1 ? 1.4f : 0.5f);
        ob += noise_lp * slipVol;
    }
    else if (tx == "bae" || tx == "hds300")
    {
        // Old BAE: traction motor bearing whine, constant low growl
        double brgHz = 180.0 + spd * 4.0;
        float brgVol = 0.014f * ob_whineAmt * npcVolumeScale;
        ob += Math.Sin(2.0 * Math.PI * (audioClock * brgHz % 1.0)) * brgVol;
    }
}

// ── Hard-shift exhaust bark: old buses occasionally bark on upshift ────
if (ob_exhaustChuff && alShiftTransient > 0.7f && (float)(NextNoiseSample() * 0.5 + 0.5) < 0.15f * (float)invSR * 1000f)
{
    ph_ob_chuff = 0.0; // retrigger the existing chuff envelope early
}
    }
}