using System;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusAudioEngine.StartupSequence — the rest of the real engine startup ask
//  (see DoVoithStartupWindup in BusAudioEngine.d8646.cs for the piece that
//  already shipped). Three more pieces, split by drivetrain:
//
//  PLAIN COMBUSTION (diesel/CNG, no tx-level hybrid system) — goes through a
//  real 8s Cranking state already (see RenderCrankSample/CRANK_DURATION in
//  BusAudioEngine.cs). What was missing was everything AFTER the crank
//  catches: real engines don't snap straight to a glass-smooth idle the
//  instant combustion starts.
//    • Rough-catch idle: a couple seconds of uneven, lumpy firing + a light
//      amplitude wobble settling into the normal steady idle.
//    • Extended air-puff tail: the same pneumatic puff layer cranking
//      already has continues a few seconds into Running, spacing out
//      (pressure building) before stopping for good.
//  Both gated off IsHybridOrElectric() — a hybrid/electric bus never goes
//  through Cranking (RequestEngineToggle skips straight to Running for
//  them), so there's no "catch" for these to attach to.
//
//  ALTERNATOR LOAD-IN — universal to every non-electric drivetrain, plain
//  combustion AND real-engine hybrids alike (a BAE/HDS300/H4x/eGen Flex bus
//  still carries a genuine diesel genset with its own alternator). A brief
//  rising-then-settling electrical whine, timed to the same Cranking/Off ->
//  Running edge the rough-catch idle uses.
//
//  REAL-ENGINE-HYBRID STARTUP WINDUP — generic version of
//  DoVoithStartupWindup so BAE/HDS300/H4x-family/eGen Flex get the same
//  "BAE HybriDrive-style ISG spin-up" cue Voith already validated, per that
//  file's own header note ("once it reads right, the same shape gets reused
//  for the BAE tx"). One shared implementation + one shared state block —
//  safe because exactly one tx's DSP function runs per instance per sample
//  (the tx dispatch in AudioLayers.cs is a single if/else chain), same
//  assumption Voith's own dedicated v6_ fields already rely on.
//
//  PURE ELECTRIC — no engine, no torque converter, nothing to "catch" or
//  wind up. Real EV buses: a brief contactor-relay click (main pack
//  contactors closing) then a short, clean two-note "systems ready" chime,
//  well under a second total. Own function, own dispatch branch (pure
//  electric currently gets NO tx-level voice at all in AudioLayers.cs —
//  this only adds the startup cue, it does not attempt to build the
//  driving voice electric buses are still missing).
// ═══════════════════════════════════════════════════════════════════════════════
public partial class BusAudioEngine
{
    // ── Rough-catch idle + extended air-puff tail (plain combustion only) ──────
    private bool   _wasRunningForCatch  = false;
    private float  _catchEnvIdle        = 0f;
    private float  _extendedPuffTimer   = -1f;
    private double ph_catchLo1, ph_catchWobble;

    // [TUNED per feedback] Was 2.2s -- read as barely there over actual
    // engine noise. At least 5s of audible rough-catch before it settles.
    public float CATCH_IDLE_DURATION    = 5.5f;
    // Stretched to keep pace with the now-longer catch window instead of
    // stopping partway through it.
    public float EXTENDED_PUFF_DURATION = 4.5f;

    // ── RPM flare (main-thread side -- Tick() runs off the game thread, the
    //    rest of this file off the audio thread, so this needs its own
    //    trigger/timer rather than sharing _catchEnvIdle across threads).
    //    Armed directly from RequestEngineToggle's ReadyToStart -> Running
    //    case (see BusAudioEngine.cs) -- the one place that transition is
    //    unambiguous, rather than an edge-detector inferred from Tick()
    //    only ever running while already Running. Real diesels/CNGs don't
    //    come up dead-flat on IDLE either -- a governor overshoot is part
    //    of what "catching" actually looks/sounds like; audio alone (the
    //    lump/wobble above) wasn't backed by the RPM the rest of the DSP
    //    (and the dashboard tach) actually reads.
    private bool  _rpmCatchActive = false;
    private float _rpmCatchTimer  = 0f;

    // ── Universal alternator load-in cue ────────────────────────────────────────
    private float  _alternatorTimer = -1f;
    private double ph_alt1, ph_alt2;

    public float ALTERNATOR_DURATION = 1.4f;
    public float ALTERNATOR_HZ_START = 85f;
    public float ALTERNATOR_HZ_PEAK  = 235f;

    /// <summary>Called once per sample from ProcessAudioCore, right after the
    /// engine is confirmed Running (isEngineRunning true). Advances/renders
    /// the rough-catch idle wobble + envelope and the alternator cue; the
    /// extended air-puff tail is rendered by the caller via
    /// RenderAirPuffLayer (needs the same invSR/timer but lives next to
    /// RenderCrankSample since it shares that function's puff state).
    /// Returns the additive catch-idle sample and, via catchWobble, a
    /// multiplier the caller applies to the combined engine+tx core sample.</summary>
    private double UpdateStartupCatchAndAlternator(double invSR, float engVolPersonality, out float catchWobble)
    {
        bool isHybridOrElectric = IsHybridOrElectric();

        if (!_wasRunningForCatch)
        {
            _alternatorTimer = 0f;
            if (!isHybridOrElectric)
            {
                _catchEnvIdle      = 1f;
                _extendedPuffTimer = 0f;
                ph_catchLo1        = 0.0;
                ph_catchWobble     = 0.0;
            }
        }
        _wasRunningForCatch = true;

        double catchSample = 0.0;
        catchWobble = 1f;
        if (_catchEnvIdle > 0.001f)
        {
            // Uneven, irregular firing pulses right as combustion catches --
            // real diesels/CNGs don't settle glass-smooth the instant they
            // start; a beat or two of lumpiness is normal. Rate wanders
            // around the idle firing rate rather than sitting locked to it.
            double lumpHz = (IDLE / 60.0) * (0.8 + 0.35 * (NextNoiseSample() * 0.5 + 0.5));
            double lump   = Math.Sin(2.0 * Math.PI * ph_catchLo1) * 0.5 + noise_lp * 0.5;
            // [TUNED per feedback] 0.10 -> 0.14 -- inaudible against the rest
            // of the engine mix at the old level, especially now that it's
            // stretched over CATCH_IDLE_DURATION instead of fading fast.
            catchSample   = lump * _catchEnvIdle * 0.14 * npcVolumeScale * engVolPersonality;
            ph_catchLo1   = (ph_catchLo1 + lumpHz * invSR) % 1.0;

            // A light amplitude tremor on top of the core engine+tx sample --
            // the "not smoothed out yet" half of a rough catch, separate from
            // the lump texture above. Caller multiplies its core sample by
            // this every sample; it decays to a flat 1.0 with the same
            // envelope.
            const double wobbleHz = 6.5;
            // [TUNED per feedback] 0.16 -> 0.22 -- same audibility reasoning
            // as the lump volume above.
            catchWobble = 1f + (float)Math.Sin(2.0 * Math.PI * ph_catchWobble) * _catchEnvIdle * 0.22f;
            ph_catchWobble = (ph_catchWobble + wobbleHz * invSR) % 1.0;

            _catchEnvIdle -= (float)invSR / Mathf.Max(0.1f, CATCH_IDLE_DURATION);
            if (_catchEnvIdle < 0f) _catchEnvIdle = 0f;
        }

        return catchSample;
    }

    private double UpdateAlternatorCue(double invSR)
    {
        if (_alternatorTimer < 0f) return 0.0;
        if (IsElectric()) { _alternatorTimer = -1f; return 0.0; } // no alternator on a pure EV

        _alternatorTimer += (float)invSR;
        if (_alternatorTimer > ALTERNATOR_DURATION) { _alternatorTimer = -1f; return 0.0; }

        float t       = _alternatorTimer;
        float riseT   = Mathf.Clamp01(t / Mathf.Max(0.05f, ALTERNATOR_DURATION * 0.35f));
        float eased   = riseT * riseT * (3f - 2f * riseT);
        double altHz  = Mathf.Lerp(ALTERNATOR_HZ_START, ALTERNATOR_HZ_PEAK, eased);

        float fadeInEnd = ALTERNATOR_DURATION * 0.15f;
        float ampEnv = t < fadeInEnd
            ? Mathf.Clamp01(t / Mathf.Max(0.01f, fadeInEnd))
            : 1f - Mathf.Clamp01((t - fadeInEnd) / Mathf.Max(0.01f, ALTERNATOR_DURATION - fadeInEnd));

        double alt = Math.Sin(2.0 * Math.PI * ph_alt1) * 0.7 + Math.Sin(2.0 * Math.PI * ph_alt2) * 0.3;
        double sample = alt * ampEnv * ampEnv * 0.032 * npcVolumeScale;

        ph_alt1 = (ph_alt1 + altHz       * invSR) % 1.0;
        ph_alt2 = (ph_alt2 + altHz * 2.0 * invSR) % 1.0;
        return sample;
    }

    // ── Real-engine-hybrid startup windup (generic DoVoithStartupWindup) ───────
    private bool   hyb_wasEngineRunning = false;
    private float  hyb_windupEnv        = 0f;
    private float  hyb_windupTimer      = 0f;
    private double ph_hyb_windup1, ph_hyb_windup2;

    public float HYB_WINDUP_RISE_SEC = 1.6f;
    public float HYB_WINDUP_HOLD_SEC = 0.35f;
    public float HYB_WINDUP_FALL_SEC = 0.55f;

    /// <summary>Same shape as DoVoithStartupWindup (BusAudioEngine.d8646.cs) --
    /// a soft harmonic-stack electric "ahh" spin-up heard once on the
    /// Off -> Running edge, SmoothStep-eased throughout. hzStart/hzPeak are
    /// the only thing each hybrid family tunes differently. Call once per
    /// sample from the top of that tx's own DSP function, exactly like
    /// DoVoithDSP calls DoVoithStartupWindup.</summary>
    private void DoHybridStartupWindup(ref double txSample, bool isEngineRunning, float hzStart, float hzPeak, float engMul, double invSR)
    {
        if (isEngineRunning && !hyb_wasEngineRunning)
        {
            hyb_windupEnv   = 1f;
            hyb_windupTimer = 0f;
            ph_hyb_windup1  = 0.0;
            ph_hyb_windup2  = 0.0;
        }
        hyb_wasEngineRunning = isEngineRunning;

        if (hyb_windupEnv < 0.001f) return;

        float totalSec = HYB_WINDUP_RISE_SEC + HYB_WINDUP_HOLD_SEC + HYB_WINDUP_FALL_SEC;
        hyb_windupTimer += (float)invSR;
        float t = hyb_windupTimer;

        float riseT = Mathf.Clamp01(t / Mathf.Max(0.01f, HYB_WINDUP_RISE_SEC));
        float eased = riseT * riseT * (3f - 2f * riseT);
        float hz = Mathf.Lerp(hzStart, hzPeak, eased);

        float ampEnv;
        if (t < 0.15f)
        {
            ampEnv = Mathf.Clamp01(t / 0.15f);
        }
        else if (t < HYB_WINDUP_RISE_SEC + HYB_WINDUP_HOLD_SEC)
        {
            ampEnv = 1f;
        }
        else
        {
            float fallT = Mathf.Clamp01((t - HYB_WINDUP_RISE_SEC - HYB_WINDUP_HOLD_SEC) / Mathf.Max(0.01f, HYB_WINDUP_FALL_SEC));
            ampEnv = 1f - (fallT * fallT * (3f - 2f * fallT));
        }

        double h1 = Math.Sin(2.0 * Math.PI * ph_hyb_windup1);
        double h2 = Math.Sin(2.0 * Math.PI * ph_hyb_windup2);
        double tone = h1 * 0.72 + h2 * 0.28;

        txSample += tone * ampEnv * 0.075 * engMul;

        ph_hyb_windup1 = (ph_hyb_windup1 + hz       * invSR) % 1.0;
        ph_hyb_windup2 = (ph_hyb_windup2 + hz * 1.5 * invSR) % 1.0;

        if (t >= totalSec) hyb_windupEnv = 0f;
    }

    // ── Pure electric: contactor click + ready chime ────────────────────────────
    private bool   elec_wasEngineRunning = false;
    private float  elec_seqTimer         = -1f;
    private double ph_elec_chime1, ph_elec_chime2;

    public float ELEC_CLICK_AT_SEC   = 0.05f;
    public float ELEC_CHIME_AT_SEC   = 0.22f;
    public float ELEC_CHIME_DURATION = 0.45f;
    public float ELEC_CHIME_HZ_1     = 660f;  // first note
    public float ELEC_CHIME_HZ_2     = 990f;  // second note, a fifth up -- "ready" lift

    /// <summary>Pure-electric equivalent of DoHybridStartupWindup -- no engine
    /// to wind up, so this is a short one-shot instead of a sustained sweep:
    /// a brief contactor click (main pack contactors closing) then a clean
    /// two-note ascending chime a moment later. Call from the tx dispatch
    /// (IsElectric() only) once per sample.</summary>
    private double DoElectricStartupCue(bool isEngineRunning, double invSR)
    {
        if (isEngineRunning && !elec_wasEngineRunning)
        {
            elec_seqTimer  = 0f;
            ph_elec_chime1 = 0.0;
            ph_elec_chime2 = 0.0;
        }
        elec_wasEngineRunning = isEngineRunning;

        if (elec_seqTimer < 0f) return 0.0;

        elec_seqTimer += (float)invSR;
        double sample = 0.0;

        // Contactor click -- a short, sharp noise transient, not a tone.
        float clickT = elec_seqTimer - ELEC_CLICK_AT_SEC;
        if (clickT >= 0f && clickT < 0.03f)
        {
            double clickEnv = 1.0 - clickT / 0.03;
            sample += noise_hi * clickEnv * clickEnv * 0.28;
        }

        // Ready chime -- clean two-note sine stack, first note then a fifth
        // up a beat later, both with a soft fade in/out (no clicks).
        float chimeT = elec_seqTimer - ELEC_CHIME_AT_SEC;
        if (chimeT >= 0f && chimeT < ELEC_CHIME_DURATION)
        {
            float halfway = ELEC_CHIME_DURATION * 0.5f;
            float note1Env = Mathf.Clamp01(1f - Mathf.Abs(chimeT - halfway * 0.5f) / (halfway * 0.5f));
            float note2Env = Mathf.Clamp01(1f - Mathf.Abs(chimeT - halfway * 1.4f) / (halfway * 0.6f));

            double n1 = Math.Sin(2.0 * Math.PI * ph_elec_chime1) * note1Env;
            double n2 = Math.Sin(2.0 * Math.PI * ph_elec_chime2) * note2Env;
            sample += (n1 + n2) * 0.10;

            ph_elec_chime1 = (ph_elec_chime1 + ELEC_CHIME_HZ_1 * invSR) % 1.0;
            ph_elec_chime2 = (ph_elec_chime2 + ELEC_CHIME_HZ_2 * invSR) % 1.0;
        }

        if (elec_seqTimer > ELEC_CHIME_AT_SEC + ELEC_CHIME_DURATION + 0.1f)
            elec_seqTimer = -1f;

        return sample * npcVolumeScale;
    }
}
