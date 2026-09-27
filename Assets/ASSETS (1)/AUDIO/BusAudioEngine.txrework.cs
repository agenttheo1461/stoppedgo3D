using System;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusAudioEngine.TxRework  —  NEW STATE FIELDS ONLY (drop-in, compiles as-is).
//
//  This file only adds fields. The rebuilt method BODIES live in
//  BusAudioEngine_TxRework_REPLACEMENTS.cs and are pasted OVER the existing
//  same-named methods in BusAudioEngine.cs (identical signatures → no rewiring).
// ═══════════════════════════════════════════════════════════════════════════════
public partial class BusAudioEngine
{

    // ── BAE HybriDrive (HDS200 / HDS300) rebuild ──────────────────────────────
    // Series hybrid: genset RPM is decoupled from road speed. Adds the missing
    // integrated starter-generator (ISG) whine and the fixed reduction-gear mesh.
    private float  bae_isgHzSmooth   = 0f;
    private float  bae_reduxHzSmooth = 0f;
    private float  bae_gensetLd      = 0f;
    private double ph_bae_isg1, ph_bae_isg2;
    private double ph_bae_redux1, ph_bae_redux2;

    private float  h3_isgHzSmooth    = 0f;
    private float  h3_reduxHzSmooth  = 0f;
    private float  h3_gensetLd       = 0f;
    private double ph_h3_isg1, ph_h3_isg2;
    private double ph_h3_redux1, ph_h3_redux2;

    // ── Allison H 40/50 EP two-mode rebuild ───────────────────────────────────
    private float  ep_mg1HzSmooth  = 0f;   // reaction MG — the wild sweeper
    private float  ep_mg2HzSmooth  = 0f;   // traction MG — tracks road speed
    private float  ep_meshHzSmooth = 0f;   // planetary mesh whine
    private float  ep_invHzSmooth  = 0f;   // DPIM PWM carrier
    private float  ep_surgeSmooth  = 0f;   // engine rev-surge howl envelope
    private float  ep_prevRpm      = 0f;
    private int    ep_modePrev     = 0;    // 0 = low-mode (input split), 1 = high-mode
    private float  ep_modeXientVol = 0f;   // mode-change transient
    private double ph_ep_mg1a, ph_ep_mg1b;
    private double ph_ep_mg2a, ph_ep_mg2b;
    private double ph_ep_mesh1, ph_ep_mesh2, ph_ep_mesh3;
    private double ph_ep_inv;
    private double ph_ep_regen;
    private double ph_ep_howl1, ph_ep_howl2;

    // ── Voith DIWA character add-on (shake / creak / move-off lurch / G1 buzz) ─
    private bool   voith_shakerInit   = false;
    private bool   voith_isShaker     = false;  // ~18% of Voith buses shake
    private double ph_voith_shake;
    private float  voith_creakVol     = 0f;
    private double ph_voith_creak1, ph_voith_creak2;
    private bool   voith_wasStoppedVo = true;
    private float  voith_moveOffTimer = 0f;     // DIWA fill/grab lurch on pull-away
    private double ph_voith_grab;
    private float  voith_g1VibVol     = 0f;     // vibration near the end of gear 1

    // ── Allison B400R whine rebuild — classic rising accel whine layer ────────
    private double ph_b400_accWh1, ph_b400_accWh2, ph_b400_accWh3;
    private float  b400_accWhineSmooth = 0f;
    private float  b400_accHzSmooth    = 0f;

    // ── ZF EcoLife — G2→G3 lockup clunk ───────────────────────────────────────
    private int    zf_lastGearForClunk = -1;
    private float  zf_lockupClunkVol   = 0f;
    private double ph_zf_clunk1, ph_zf_clunk2;

    // ── Voith DIWA.5 (d8645) — move-off grab lurch + end-of-gear-1 vibration,
    //    heavier/looser parallel to the D864.6 character pass. ────────────────
    private float  d5_moveOffTimer = 0f;
    private double ph_d5_grab;
    private float  d5_g1VibVol     = 0f;
    private double ph_d5_buzz;
    private bool   d5_wasStoppedVo = true;

    // ═════════════════════════════════════════════════════════════════════
    //  D864.5 COMPLETE REDO — real-world grounded (full reasoning in the
    //  header comment above DoD8645DSP). Two confirmed facts drove this:
    //    1. DIWA.6's headline change over DIWA.5 is a "smart" electro-
    //       hydraulic circuit that adaptively REDUCES main operating
    //       pressure when full pressure isn't needed (Voith's own launch
    //       materials). D864.5 predates that entirely -- no load-adaptive
    //       quieting, so it needs an always-on pump/converter noise floor
    //       D864.6 doesn't have.
    //    2. Automatic Neutral at Standstill has existed since the DIWA 2
    //       generation (1985-1999) -- so D864.5 genuinely has it, own
    //       state so it never collides with CalcVoithRPM's ANS machinery.
    // ═════════════════════════════════════════════════════════════════════
    private bool   d5_ansActive      = false;
    private float  d5_ansTimer       = 0f;
    private float  d5_kdCreep        = 0f; // [NEW] 0->1 smoothed kickdown RPM ramp, see kdRpmBoost in DoVoithRange
    private float  d5_ansEngagePulse = 0f;
    private double ph_d5_pumpFloor1, ph_d5_pumpFloor2;
    private float  d5_pumpFloorVol   = 0f;
    // Retarder pitch state -- previously ph_v5_Ret/ph_v5_Ret2 advanced every
    // frame off a fixed v5_RetHz constant with NOTHING ever reading from
    // them (retAct was passed into DoD8645DSP and never once used) -- D864.5
    // had literally no audible retarder at all. Real DIWA retarder pitch
    // tracks road/output speed under braking (converter re-coupled as a
    // retarder), same mechanism the D864.6 rebuild uses -- this is that,
    // with its own smoothed-Hz state.
    private float  d5_retHzSmooth     = 74f;

    // ═════════════════════════════════════════════════════════════════════════
    //  VOITH CHARACTER ADD-ON  (additive — new method, no collision)
    //  Add ONE call inside DoVoithDSP, right before its phase-accumulation block:
    //        DoVoithCharacter(ref txSample, engMul, invSR);
    // ═════════════════════════════════════════════════════════════════════════
    private void DoVoithCharacter(ref double txSample, float engMul, double invSR)
    {
        // One-time per-instance roll: ~18% of Voith buses are "shakers".
        if (!voith_shakerInit) { voith_shakerInit = true; voith_isShaker = (NextNoiseSample() * 0.5 + 0.5) < 0.18; }

        // 1) Move-off lurch — the DIWA converter fills and GRABS as you pull
        //    away from a dead stop: a brief low judder + hesitation ("immediate
        //    stop when you start to move").
        bool stoppedNow = spd < 0.4f;
        if (voith_wasStoppedVo && !stoppedNow && accel > 0.05f) voith_moveOffTimer = 0.55f;
        voith_wasStoppedVo = stoppedNow;
        if (voith_moveOffTimer > 0f)
        {
            voith_moveOffTimer -= (float)invSR;
            float t = Mathf.Clamp01(voith_moveOffTimer / 0.55f);   // 1 → 0
            float grabHz  = 46f + (1f - t) * 20f;
            float judder  = 0.5f + 0.5f * Mathf.Sin((float)audioClock * Mathf.PI * 2f * 11f);
            double grab   = (Math.Sin(2.0 * Math.PI * ph_voith_grab) * 0.8 + noise_lp * 0.4)
                            * t * (0.10f + judder * 0.06f) * engMul;
            txSample += grab;
            ph_voith_grab = (ph_voith_grab + grabHz * invSR) % 1.0;
        }

        // 2) Gear-1-end vibration — approaching the G1→G2 upshift the converter
        //    loads up and the driveline buzzes slightly.
        float g1VibTarget = 0f;
        if (gear == 1)
        {
            float frac = Mathf.Clamp01((spd - (VOITH_GEAR2_UPSHIFT_SPD - 10f)) / 10f); // last ~10 km/h of G1
            g1VibTarget = frac * (0.4f + accel * 0.6f);
        }
        voith_g1VibVol += (g1VibTarget - voith_g1VibVol) * (g1VibTarget > voith_g1VibVol ? 0.02f : 0.01f);
        if (voith_g1VibVol > 0.002f)
        {
            double buzz = Math.Sin(2.0 * Math.PI * ph_voith_shake) * voith_g1VibVol * 0.05 * engMul;
            buzz *= 0.6 + 0.4 * Math.Sin(2.0 * Math.PI * ph_voith_shake * 7.3); // tremor → buzz, not tone
            txSample += buzz;
            ph_voith_shake = (ph_voith_shake + 34.0 * invSR) % 1.0;
        }
        else ph_voith_shake = (ph_voith_shake + 34.0 * invSR) % 1.0;

        // 3) Shaker buses — persistent whole-bus vibration + occasional metallic
        //    creak, strongest at low speed / under torque.
        if (voith_isShaker)
        {
            float shakeLoad = Mathf.Clamp01(accel * 1.2f) * Mathf.Clamp01(1f - spd / 45f) + (spd < 1f ? 0.25f : 0f);
            if (shakeLoad > 0.02f)
            {
                double body   = Math.Sin(2.0 * Math.PI * (ph_voith_shake * 0.78));
                double tremor = 0.55 + 0.45 * Math.Sin(2.0 * Math.PI * (ph_voith_shake * 4.9));
                txSample += body * tremor * shakeLoad * 0.045 * engMul;
            }
            if (voith_creakVol < 0.01f && shakeLoad > 0.15f && (NextNoiseSample() * 0.5 + 0.5) > 0.99955)
                voith_creakVol = 0.6f + (float)(NextNoiseSample() * 0.5 + 0.5) * 0.4f;
            if (voith_creakVol > 0.003f)
            {
                double c1 = Math.Sin(2.0 * Math.PI * ph_voith_creak1);
                double c2 = Math.Sin(2.0 * Math.PI * ph_voith_creak2);
                txSample += Math.Tanh((c1 + c2 * 0.5) * 1.6) * voith_creakVol * 0.05 * engMul;
                double crHz = 320.0 + (1.0 - voith_creakVol) * 260.0; // metallic sweep
                ph_voith_creak1 = (ph_voith_creak1 + crHz        * invSR) % 1.0;
                ph_voith_creak2 = (ph_voith_creak2 + crHz * 1.48 * invSR) % 1.0;
                voith_creakVol *= 0.9975f;
                if (voith_creakVol < 0.004f) voith_creakVol = 0f;
            }
        }
    }
}