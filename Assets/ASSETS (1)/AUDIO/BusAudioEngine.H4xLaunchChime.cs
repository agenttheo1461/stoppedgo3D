using System;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusAudioEngine.H4xLaunchChime  —  NEW FIELDS ONLY (drop-in, compiles as-is).
//  Supports the rebuilt DoH4xDSP in BusAudioEngine_H4x_LaunchChime_REPLACEMENT.cs.
//
//  Replaces the old continuous "decoupled rn" surge-howl (a scientifically
//  plausible but IRL-inaccurate model) with a deterministic multi-beat launch
//  sequence per Ted's real-world observation:
//    take off → one big rev, straight up then exponential decay → wait ~4s →
//    a second, longer rev → wait ~7s → a third, longer rev → settles into a
//    smooth/sluggish idle. Tone is soft harmonic (fundamental+fifth+octave),
//    not a growl — matches Allison's own spec of ~79dB @ 10m (near
//    passenger-car quiet) and "smooth and seamless" acceleration.
//
//  [Configurable] The stage list is now a public, Inspector-editable List — no
//  more hardcoded 3-stage array. Add, remove, or reorder entries in the
//  Inspector (or per-series from a fleet config) to place rev boosts wherever
//  you want, with whatever peak/duration/gap you want. Defaults reproduce the
//  original 3-beat sequence exactly, so nothing changes until you touch it.
// ═══════════════════════════════════════════════════════════════════════════════
public partial class BusAudioEngine
{
    [System.Serializable]
    public struct EPRevStage
    {
        [Tooltip("Rev intensity, roughly 0-1.5. Higher = bigger swell.")]
        public float peak;
        [Tooltip("Seconds the rev takes to decay away (exponential — instant onset, sloping decay).")]
        public float duration;
        [Tooltip("Seconds of silence AFTER this rev before the next one starts. Ignored on the last stage.")]
        public float gapAfter;

        public EPRevStage(float peak, float duration, float gapAfter)
        { this.peak = peak; this.duration = duration; this.gapAfter = gapAfter; }
    }

    // NOTE: BusAudioEngine is a plain C# class, not a MonoBehaviour, so a List
    // here is invisible to the Inspector no matter what attributes it carries.
    // The actual editable list lives on BusController.epRevStages and gets
    // handed across in Start()/ApplyEngineConstants(); this is just where the
    // state machine reads from at runtime.
    public List<EPRevStage> epRevStages = new List<EPRevStage>
    {
        new EPRevStage(0.55f, 1.0f, 4.0f),
        new EPRevStage(0.78f, 1.6f, 7.0f),
        new EPRevStage(1.00f, 2.2f, 0.0f),
    };

    // -1 = waiting for next launch. Otherwise an index into epRevStages.
    private int    ep_launchStageIdx = -1;
    private int    ep_launchPhase    = 0;   // 0 = in the rev, 1 = in the gap after it
    private float  ep_stageTimer     = 0f;
    private double ph_ep_howl3;   // third chime partial (octave) — howl1/howl2 already exist

    // ───────────────────────────────────────────────────────────────────────────
    //  TUNABLES — plain public fields so a fleet config or BusController can
    //  overwrite them per-series in Start()/ApplyEngineConstants(), same pattern
    //  as epRevStages. (BusAudioEngine is a plain C# class, so these are not
    //  Inspector-visible on their own.)
    // ───────────────────────────────────────────────────────────────────────────

    // Slip frequency at full torque, Hz. Rated slip on large traction induction
    // machines lands in the 0.1–5 Hz band; warble rate is 2× this.
    public float H4S_SLIP_MAX_B   = 2.8f;   // output motor (the one you hear at launch)
    public float H4S_SLIP_MAX_A   = 2.1f;   // input motor

    // Speed (kph) at which each machine's torque — and therefore its slip —
    // changes sign. MG-B flips inside the 0–15 mph window; MG-A flips later,
    // near the mode-1 mechanical point. These being DIFFERENT is the point.
    public float H4S_B_FLIP_KPH   = 18f;    // ~11 mph
    public float H4S_A_FLIP_KPH   = 38f;    // ~24 mph

    // Amplitude modulation depth from the slip warble, per machine.
    public float H4S_DEPTH_B      = 0.42f;
    public float H4S_DEPTH_A      = 0.26f;

    // Slip magnitude (Hz) below which depth notches toward zero. This is what
    // makes the warble stall at the motoring↔generating crossing instead of
    // just continuing at a slow rate.
    public float H4S_NOTCH_HZ     = 0.75f;

    // Modulation is a launch phenomenon — gone by here.
    public float H4S_MOD_FADE_KPH = 34f;

    // Small pitch sag per warble trough. Pure tremolo doesn't read as syllables;
    // the pitch dip does. (Kept from the original launch-voice design.)
    public float H4S_PITCH_DIP    = 0.045f;

    // Once-per-rev rotor eccentricity thump. Rate RISES with speed (0→~10 Hz
    // across the launch), opposing the falling warble.
    public float H4S_B_GEAR       = 15.0f;  // output shaft → MG-B rotor ratio
    public float H4S_REV_DEPTH    = 0.16f;
    public float H4S_REV_FADE_KPH = 22f;

    // Non-periodic control-loop wander (near-zero stator frequency instability).
    public float H4S_WANDER_HZ       = 2.0f;   // lowpass corner
    public float H4S_WANDER_GAIN     = 60f;    // normalises the 2-pole LP output
    public float H4S_WANDER_DEPTH    = 0.20f;
    public float H4S_WANDER_FADE_KPH = 20f;

    // Torque command slew (seconds). Stops the flip from being instantaneous.
    public float H4S_TQ_TAU       = 0.18f;

    // ───────────────────────────────────────────────────────────────────────────
    //  STATE — h4s_ prefix, nothing here collides with existing fields.
    // ───────────────────────────────────────────────────────────────────────────
    private float  h4s_torqueA = 0f, h4s_torqueB = 0f;   // signed: + motoring, − generating
    private float  h4s_slipA   = 0f, h4s_slipB   = 0f;   // signed Hz
    private float  h4s_depthA  = 0f, h4s_depthB  = 0f;
    private float  h4s_modA    = 1f, h4s_modB    = 1f;   // amplitude multipliers
    private float  h4s_modRev  = 1f, h4s_modWander = 1f;
    private float  h4s_pitchB  = 1f;
    private float  h4s_wander1 = 0f, h4s_wander2 = 0f;
    private double h4s_phWarbleA, h4s_phWarbleB, h4s_phRevB;

    // ═══════════════════════════════════════════════════════════════════════════
    //  MODULATOR BANK — call once per sample before any oscillator work.
    // ═══════════════════════════════════════════════════════════════════════════
    private void H4xUpdateSlipModel(float wheelRev, double invSR)
    {
        float demand  = Mathf.Clamp01(accel / 0.5f);
        float braking = Mathf.Clamp01(bkPd  / 0.5f);
        bool  coast   = accel < 0.01f && bkPd < 0.02f && spd > 4f;

        // ── MG-B: motors hard off the line, crosses to generating at the flip ──
        float flipB   = Mathf.Clamp(1f - spd / Mathf.Max(1f, H4S_B_FLIP_KPH), -1f, 1f);
        float tgtB    = flipB * demand;
        if (braking > 0.01f) tgtB = -braking;          // brake regen → generating
        else if (coast)      tgtB = -0.25f;            // coast regen → light generating

        // ── MG-A: generating at launch (reacting engine torque), reverses later ─
        float flipA   = Mathf.Clamp(-(1f - spd / Mathf.Max(1f, H4S_A_FLIP_KPH)), -1f, 1f);
        float tgtA    = flipA * (0.35f + demand * 0.65f);
        if (braking > 0.01f) tgtA = Mathf.Abs(flipA) * braking * 0.6f;

        float tqSlew = (float)(invSR / Mathf.Max(0.001f, H4S_TQ_TAU));
        if (tqSlew > 1f) tqSlew = 1f;
        h4s_torqueB += (tgtB - h4s_torqueB) * tqSlew;
        h4s_torqueA += (tgtA - h4s_torqueA) * tqSlew;

        // ── Slip ∝ torque. Signed, so it genuinely passes through zero. ────────
        h4s_slipB = H4S_SLIP_MAX_B * h4s_torqueB;
        h4s_slipA = H4S_SLIP_MAX_A * h4s_torqueA;

        float warbleB = 2f * Mathf.Abs(h4s_slipB);
        float warbleA = 2f * Mathf.Abs(h4s_slipA);

        float modGate = Mathf.Clamp01(1f - spd / Mathf.Max(1f, H4S_MOD_FADE_KPH));
        float notch   = Mathf.Max(0.01f, H4S_NOTCH_HZ);
        h4s_depthB = H4S_DEPTH_B * modGate * Mathf.Clamp01(Mathf.Abs(h4s_slipB) / notch);
        h4s_depthA = H4S_DEPTH_A * modGate * Mathf.Clamp01(Mathf.Abs(h4s_slipA) / notch);

        h4s_phWarbleB = (h4s_phWarbleB + warbleB * invSR) % 1.0;
        h4s_phWarbleA = (h4s_phWarbleA + warbleA * invSR) % 1.0;

        // Squared trough → dippy syllables rather than smooth tremolo.
        float wB = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * (float)h4s_phWarbleB);
        float wA = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * (float)h4s_phWarbleA);
        h4s_modB   = 1f - h4s_depthB + h4s_depthB * wB * wB;
        h4s_modA   = 1f - h4s_depthA + h4s_depthA * wA * wA;
        h4s_pitchB = 1f - h4s_depthB * H4S_PITCH_DIP * (1f - wB);

        // ── Once-per-rev: rate RISES while the warble falls. ───────────────────
        float shaftBHz = wheelRev * H4S_B_GEAR;
        h4s_phRevB = (h4s_phRevB + shaftBHz * invSR) % 1.0;
        float revDepth = H4S_REV_DEPTH * Mathf.Clamp01(1f - spd / Mathf.Max(1f, H4S_REV_FADE_KPH));
        float rv = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * (float)h4s_phRevB);
        h4s_modRev = 1f - revDepth + revDepth * rv;

        // ── Control-loop wander: two-pole LP on noise, NOT periodic. ───────────
        float wc = (float)(invSR * 2.0 * Mathf.PI * H4S_WANDER_HZ);
        if (wc > 0.9f) wc = 0.9f;
        h4s_wander1 += ((float)NextNoiseSample() - h4s_wander1) * wc;
        h4s_wander2 += (h4s_wander1 - h4s_wander2) * wc;
        float w = Mathf.Clamp(h4s_wander2 * H4S_WANDER_GAIN, -1f, 1f);
        float wanderGate = Mathf.Clamp01(1f - spd / Mathf.Max(1f, H4S_WANDER_FADE_KPH));
        h4s_modWander = 1f + w * H4S_WANDER_DEPTH * wanderGate;
        if (h4s_modWander < 0.15f) h4s_modWander = 0.15f;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  [FIXED] CARRIER VOICE -- H4xUpdateSlipModel above only ever produced
    //  MODULATORS (h4s_modA/modB/modRev/modWander/pitchB); nothing called it
    //  and nothing multiplied an actual oscillator by them, so none of this
    //  was audible. This is the missing two-motor carrier: MG-B (output
    //  motor -- the loud, close voice at launch, carrying the once-per-rev
    //  thump and pitch-dip syllable shaping) and MG-A (input motor --
    //  quieter, higher, own modulator), summed and passed through the
    //  wander gate. Called from DoH4xDSP right after wheelRev is computed.
    // ═══════════════════════════════════════════════════════════════════════════
    private double ph_h50_mgA, ph_h50_mgB;
    public float H50_MGB_BASE_HZ = 210f;  // output motor carrier
    public float H50_MGA_BASE_HZ = 340f;  // input motor carrier

    // [ADD] volMul lets the two callers tune independently -- native
    // H40/H50EP (its own real voice) vs. the ported Voith gear-1-only copy
    // (same model reused, but should sit noticeably further back since it's
    // a transplant, not the tx's own native sound). Defaults to 1.0 so
    // nothing changes for a caller that doesn't pass one.
    private void DoH50EPSlipVoice(ref double txSample, float wheelRev, float engMul, double invSR, float volMul = 1.0f)
    {
        H4xUpdateSlipModel(wheelRev, invSR);

        double mgBHz = H50_MGB_BASE_HZ * h4s_pitchB;
        double mgB = Math.Sin(2.0 * Math.PI * ph_h50_mgB) * h4s_modB * h4s_modRev;
        ph_h50_mgB = (ph_h50_mgB + mgBHz * invSR) % 1.0;

        double mgA = Math.Sin(2.0 * Math.PI * ph_h50_mgA) * h4s_modA * 0.55;
        ph_h50_mgA = (ph_h50_mgA + H50_MGA_BASE_HZ * invSR) % 1.0;

        double voice = (mgB + mgA) * h4s_modWander;

        // Self-gating: h4s_depthA/depthB (and therefore modA/modB) already
        // fade to silence above their own *_FADE_KPH thresholds, so this can
        // be added unconditionally without a separate speed gate here.
        txSample += voice * 0.10 * volMul * engMul;
    }
}