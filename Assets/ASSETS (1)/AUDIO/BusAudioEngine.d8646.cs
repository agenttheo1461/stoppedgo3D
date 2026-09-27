using System;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusAudioEngine.Voith6 v2  —  VOITH DIWA.6 (D864.6) REBUILD, WHINE-CENTRIC
//
//  SUPERSEDES BusAudioEngine_Voith6_D8646.cs (v1) ENTIRELY. v1 wrongly removed
//  the DIWA whine — the 07-13 exterior reference proves it out:
//
//    MEASURED, 07-13 reference video:
//    • DRIVE WHINE: fundamental sweeps ~340 → 780 Hz across the launch climb
//      (800 → ~2100 rpm), i.e. an ENGINE-ORDER mesh at ~23× rpm/60 in G1 and
//      ~21.5× in G2+ — the original tooth calibration was correct. It ramps up
//      within every gear and drops at each upshift (1-2, 2-3, 3-4 sawtooth),
//      sitting above the engine stack so it reads as "its own motor".
//    • RETARDER WHINE: during the 46–59 s deceleration the whine family is far
//      louder (harmonics visible to ~3.4 kHz) and the fundamental descends
//      smoothly ~780 → 330 Hz tracking ROAD SPEED — under braking the turbine
//      is re-coupled to the output (converter-as-retarder), so the tracking
//      law switches from engine-order to output-order. This is the screech.
//    • In the interior 07-12 recording the same whine is present but buried —
//      so levels here sit clearly audible, below exterior-reference prominence.
//
//  KEPT FROM v1 (unchanged concepts, same v6_ fields):
//    [B] stopped-idle shake (40 Hz firing + 2.4/9.8 Hz AM)   [C] end-of-G1 buzz
//    [D] soft TB/PB shift sigh + disc seat                   [G] move-off grab
//    [A] converter churn (slightly reduced — whine now carries G1)
//    [H] shaker-bus trait      [I] gear pump tick
//  REMOVED vs v1: [E] output hum (the whine replaces it).
//  NEW: [W1] drive whine core, [W2] retarder whine + whoosh.
//
//  FILE LAYOUT: PART 1 fields partial (drop-in). PART 2 full DoVoithDSP body —
//  paste OVER the existing method in BusAudioEngine.cs. Signature identical.
//  DoD8645DSP and all v5_/d5_ state remain untouched.
// ═══════════════════════════════════════════════════════════════════════════════

public partial class BusAudioEngine
{
    // ── [W1] drive whine core ─────────────────────────────────────────────────
    private float  v6_whHzSmooth   = 0f;
    private float  v6_whVolSmooth  = 0f;
    private double ph_v6_wh1, ph_v6_wh2, ph_v6_wh3;

    // ── [W2] retarder whine ───────────────────────────────────────────────────
    private float  v6_rwhHzSmooth  = 0f;
    private float  v6_rwhVolSmooth = 0f;
    private double ph_v6_rwh1, ph_v6_rwh2, ph_v6_rwh3;

    // ── [W3] H50EP whine transplant — G1 + opt1_4 only ────────────────────────
    // [ADD] Ported concept (not verbatim code -- the Allison H4x DSP's local
    // state (engHz/wheelRev/isGen5/isH50/mg2Max) is hybrid-motor-specific and
    // doesn't exist in a Voith/D864.6 torque-converter context) from
    // DoH4xDSP's "3b. Cruise whine" MG2 layer: a second, independently-
    // tracked whine voice layered ON TOP of the native DIWA drive whine
    // during gear 1 under opt1_4, so the two read as genuinely two motors
    // rather than one whine turned up. Own smoothed-Hz state + own delay
    // timer so it can fade in a beat after the native whine instead of both
    // slamming in together at G1 entry.
    private float  v6_h50tHzSmooth  = 0f;
    private float  v6_h50tVolSmooth = 0f;
    private float  v6_h50tDelayTimer = 0f;
    private double ph_v6_h50t1, ph_v6_h50t2, ph_v6_h50t3;

    // ── [A] converter churn ───────────────────────────────────────────────────
    private float  v6_churnEnv     = 0f;
    private double v6_churnLP      = 0.0;
    private double ph_v6_churnMod;

    // ── [B] stopped-idle shake ────────────────────────────────────────────────
    private float  v6_idleShakeEnv = 0f;
    private double ph_v6_fire, ph_v6_fireHalf, ph_v6_am1, ph_v6_am2;

    // ── [C] end-of-G1 buzz ────────────────────────────────────────────────────
    private float  v6_g1BuzzVol    = 0f;
    private double ph_v6_buzz;

    // ── [D] shift sigh + disc seat ────────────────────────────────────────────

    private float  v6_sighVol      = 0f;
    private float  v6_seatVol      = 0f;
    private double ph_v6_seat;

    // ── [F] retarder body whoosh ──────────────────────────────────────────────
    private float  v6_retVol       = 0f;
    private double v6_retNoiseLP   = 0.0;

    // ── [G] move-off grab ─────────────────────────────────────────────────────
    private bool   v6_wasStopped   = true;
    private float  v6_moveOffTimer = 0f;
    private double ph_v6_grab;

    // ── [H] shaker-bus trait ──────────────────────────────────────────────────
    private bool   v6_shakerInit   = false;
    private bool   v6_isShaker     = false;
    private double ph_v6_shakeBody;
    private float  v6_creakVol     = 0f;
    private double ph_v6_creak1, ph_v6_creak2;

    // ── [I] gear pump ─────────────────────────────────────────────────────────
    private double ph_v6_pump;

    // ── opt1_4 — hiss window + audible piston firing (second character) ───────
    private double ph_v6_opt4Hiss;
    private double ph_v6_opt4Fire;

    // ── opt1_5 — "the shaker" (third character): no whine, deeper+louder
    // core, rapid-fire click vibration-sim at idle ─────────────────────────────
    private double ph_v6_shakeCore1, ph_v6_shakeCore2;
    private double ph_v6_shakeClick;
    private float  v6_shakeClickEnv;

    // ═══════════════════════════════════════════════════════════════════════════
    //  [NEW] STARTUP ELECTRIC WINDUP — a smooth, HIGH-pitched electric "ahh"
    //  spin-up heard once, right as the engine finishes cranking and comes up
    //  Running. Modeled after a real BAE HybriDrive-style ISG windup (per
    //  spec: "sounds kinda like BAE's irl windup"), tried here on Voith
    //  first — once it reads right, the same shape gets reused for the BAE
    //  tx (see BusAudioEngine.TxRework.cs / DoBAEDSP for that hookup).
    //
    //  Deliberately NOT built like DoMechanicalWhine's gear-3 whine (the
    //  "wave" reference point) — that one beats two close-detuned tones
    //  against each other plus a groan wobble specifically to sound rough
    //  and straining. This is the opposite intent: a soft harmonic stack
    //  (fundamental + fifth, same clean voicing the H4x launch chime uses)
    //  and SmoothStep-eased amplitude/pitch curves throughout, so it climbs
    //  as one continuous "something is actually spinning up" sweep instead
    //  of a bumpy/pulsing texture. Higher register than the DIWA gear-mesh
    //  whine (260 -> 1480Hz) so it reads as a distinct electrical system,
    //  not the transmission itself.
    // ═══════════════════════════════════════════════════════════════════════════
    private bool   v6_wasEngineRunningV6   = false;
    private float  v6_startupWindupEnv     = 0f;
    private float  v6_startupWindupTimer   = 0f;
    private double ph_v6_startupWindup1, ph_v6_startupWindup2;

    // [NEW] gear-1 presence gate for the ported H4x slip/warble voice (see
    // DoVoithDSP's call to DoH50EPSlipVoice) -- smoothed so switching gears
    // doesn't click.
    private float  v6_slipGearPresence = 0f;

    // [NEW] opt1_4 gear-1 continuous rpm ramp state -- see CalcD8645RPM.
    private float v6_opt4G1RampSm = 0f;
    public  float V6_OPT4_RAMP_SPAN_KPH = 26f;  // Voith runs longer than H50EP's ~0-20kph, so stretched
    public  float V6_OPT4_RAMP_HZ_SPAN  = 980f; // rpm climbed across that span, on top of IDLE

    public float V6_WINDUP_HZ_START = 260f;
    public float V6_WINDUP_HZ_PEAK  = 1480f;
    public float V6_WINDUP_RISE_SEC = 1.6f;
    public float V6_WINDUP_HOLD_SEC = 0.35f;
    public float V6_WINDUP_FALL_SEC = 0.55f;

    // Call once per sample from DoVoithDSP (top of the function, ahead of
    // everything else, so it fires from the very first sample the engine is
    // Running). engineState is already a class field, so no extra parameter
    // is needed beyond engMul/invSR:
    //     DoVoithStartupWindup(ref txSample, engineState == EngineRunState.Running, engMul, invSR);
    private void DoVoithStartupWindup(ref double txSample, bool isEngineRunning, float engMul, double invSR)
    {
        if (isEngineRunning && !v6_wasEngineRunningV6)
        {
            v6_startupWindupEnv   = 1f;
            v6_startupWindupTimer = 0f;
            ph_v6_startupWindup1  = 0.0;
            ph_v6_startupWindup2  = 0.0;
        }
        v6_wasEngineRunningV6 = isEngineRunning;

        if (v6_startupWindupEnv < 0.001f) return;

        float totalSec = V6_WINDUP_RISE_SEC + V6_WINDUP_HOLD_SEC + V6_WINDUP_FALL_SEC;
        v6_startupWindupTimer += (float)invSR;
        float t = v6_startupWindupTimer;

        // Smooth (SmoothStep, not linear) pitch climb -- reads as an actual
        // wind-up ease-in/ease-out rather than a straight ramp or a stepped
        // stair-climb.
        float riseT = Mathf.Clamp01(t / V6_WINDUP_RISE_SEC);
        float eased = riseT * riseT * (3f - 2f * riseT);
        float hz = Mathf.Lerp(V6_WINDUP_HZ_START, V6_WINDUP_HZ_PEAK, eased);

        // Amplitude: quick smooth fade-in, holds through the rise + a short
        // settle at the peak, smooth fade back out into the idle mix -- no
        // on/off steps and no beat/tremolo modulation anywhere in this
        // envelope, which is the "less bumpy" half of the fix.
        float ampEnv;
        if (t < 0.15f)
        {
            ampEnv = Mathf.Clamp01(t / 0.15f);
        }
        else if (t < V6_WINDUP_RISE_SEC + V6_WINDUP_HOLD_SEC)
        {
            ampEnv = 1f;
        }
        else
        {
            float fallT = Mathf.Clamp01((t - V6_WINDUP_RISE_SEC - V6_WINDUP_HOLD_SEC) / V6_WINDUP_FALL_SEC);
            ampEnv = 1f - (fallT * fallT * (3f - 2f * fallT));
        }

        // Soft harmonic stack -- fundamental + a fifth above, blended, not a
        // close-detuned beating pair -- clean electrical spin-up voicing
        // instead of a straining mechanical one.
        double h1 = Math.Sin(2.0 * Math.PI * ph_v6_startupWindup1);
        double h2 = Math.Sin(2.0 * Math.PI * ph_v6_startupWindup2);
        double tone = h1 * 0.72 + h2 * 0.28;

        txSample += tone * ampEnv * 0.075 * engMul;

        ph_v6_startupWindup1 = (ph_v6_startupWindup1 + hz       * invSR) % 1.0;
        ph_v6_startupWindup2 = (ph_v6_startupWindup2 + hz * 1.5 * invSR) % 1.0; // a fifth above -- harmonic, not a beat

        if (t >= totalSec) v6_startupWindupEnv = 0f;
    }

    // [REMOVED, PER REQUEST] The H50EP whine transplant (DoH50EPWhineLayer)
    // is gone -- wasn't working, pulled entirely rather than retuned again.
    // v6_h50t* fields above are now unused (harmless to leave declared) --
    // the gear-1 voice on Voith is DoH50EPSlipVoice now (called from
    // DoVoithDSP, gear-1-gated, see BusAudioEngine.cs and
    // BusAudioEngine.H4xLaunchChime.cs).
}