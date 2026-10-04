using System;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  L9 / ISL9 + Allison B400R/B500R (Gen 4) and H50EP/ZH50EP — launch rpm "jump" and character.
//  Tuned by ear in the comparison widget; two presets, one per engine.
//    · Launch jump — from a stop under throttle the engine flares from idle up to
//      `jumpHeight` within `jumpTime`, holds there while road speed catches up, and
//      the compromise model's normal climb carries on from it. The compromise rpm
//      model is NOT changed — the jump is only a floor under its output while the
//      bus is launching in gear 1.
//    · Growl — rough low-mid noise gated at the firing rate, plus extra engine
//      weight, while the jump is playing.
//    · Hollow — extra ring through two fixed body resonances (210 / 520 Hz).
//    · Roar — continuous low-mid rumble (140 / 300 / 560 Hz), grows with load/rpm.
//    · Airy — faint breath/hiss, grows with load/rpm.
//  All layers are additive on top of DoL9Core / DoISL9Core; the tuned core voice
//  is untouched. Gen 5 Allisons (b400r_g5 / b500r_g5) are not affected.
//  Levels are first-pass — use l9CharLevel to scale all four sound layers at once.
// ═══════════════════════════════════════════════════════════════════════════════
[Serializable]
public class L9CharPreset
{
    public float jumpHeight = 1100f; // rpm reached at the top of the launch jump
    public float jumpTime   = 0.5f;  // seconds to get there from idle
    public float growl      = 0f;
    public float hollow     = 0f;
    public float roar       = 0f;
    public float airy       = 0f;
}

public partial class BusAudioEngine
{
    public bool  l9Character = true;
    [Tooltip("Scales the growl / hollow / roar / airy layers together (not the rpm jump).")]
    public float l9CharLevel = 1f;

    public L9CharPreset l9CharISL9 = new L9CharPreset
        { jumpHeight = 1150f, jumpTime = 0.5f, growl = 0f, hollow = 0.40f, roar = 0f, airy = 0f };
    public L9CharPreset l9CharL9 = new L9CharPreset
        { jumpHeight = 1150f, jumpTime = 0.5f, growl = 0f, hollow = 0.40f, roar = 0f, airy = 0f };

    // Resolved once per frame in AlcCalcRPM; read per sample by the audio thread.
    [Tooltip("Removes every air/hiss element from the L9 and ISL9 engine voices: the VGT inverter-overlay air, L9's breathy turbo noise, and the stop puff.")]
    public bool l9NoAir = true;
    private float L9AirK => (l9NoAir && (engineType == EngineType.L9 || engineType == EngineType.ISL9)) ? 0f : 1f;

    private const float L9C_AIRY_TRIM = 0.25f;
    private L9CharPreset l9c_preset;

    private float l9j_t = -1f;      // seconds since the jump started, -1 = inactive
    private bool  l9j_armed = true;
    private float l9j_rate;         // rpm/s the slew limiter must allow while the jump plays
    private bool  L9JumpActive => l9j_t >= 0f;

    private Resonator l9c_hA, l9c_hB, l9c_rA, l9c_rB, l9c_rC;
    private float l9c_gEnv, l9c_rEnv, l9c_aEnv;

    private L9CharPreset ResolveL9Preset()
    {
        if (!l9Character) return null;
        bool gen4Allison = IsAllison() || tx == "b500r";
        bool hybridEP    = tx == "h50ep" || tx == "zh50ep";
        if (gen4Allison && !allisonCompromiseModel) return null;
        if (!gen4Allison && !hybridEP) return null;
        if (engineType == EngineType.L9)   return l9CharL9;
        if (engineType == EngineType.ISL9) return l9CharISL9;
        return null;
    }

    // Floor under the compromise model's rpm while a launch jump is playing.
    private float L9LaunchJump(float r, float a)
    {
        L9CharPreset P = l9c_preset;
        if (P == null) { l9j_t = -1f; return r; }

        if (spd < 0.5f && a < 0.10f && !kickdownKey) l9j_armed = true;
        if (l9j_t < 0f)
        {
            if (l9j_armed && spd < 2f && a > 0.25f && gear <= 1) { l9j_t = 0f; l9j_armed = false; }
            else return r;
        }

        l9j_t += Time.deltaTime;
        if (spd > 15f || gear > 1 || l9j_t > 8f || (a < 0.10f && !kickdownKey)) { l9j_t = -1f; return r; }

        float jt   = Mathf.Max(0.05f, P.jumpTime);
        float amt  = Mathf.Clamp01(a);
        float rise = Mathf.Max(0f, P.jumpHeight - IDLE);
        float env  = IDLE + rise * Mathf.SmoothStep(0f, 1f, l9j_t / jt) * amt
                          + 60f * Mathf.Max(0f, l9j_t - jt) * amt;
        l9j_rate = rise / jt * 1.3f;
        return Mathf.Max(r, env);
    }

    // Additive character layer; `core` is the finished L9/ISL9 core sample.
    private double L9CharLayer(double core, float hz, float rn, float ld, float engMul)
    {
        L9CharPreset P = l9c_preset;
        if (P == null || l9CharLevel <= 0f) return 0.0;

        float eBase = engMul * (0.42f + rn * 0.40f);
        double fire = FirePulse(ph_cb_fire, 2.0f);
        double sum = 0.0;

        // Growl envelope follows the jump: fast rise over jumpTime, ~1.5 s fade after.
        float gTarget = 0f;
        if (l9j_t >= 0f)
        {
            float jt = Mathf.Max(0.05f, P.jumpTime);
            gTarget = Mathf.SmoothStep(0f, 1f, l9j_t / jt) * Mathf.Exp(-Mathf.Max(0f, l9j_t - jt) / 1.5f);
        }
        l9c_gEnv += (gTarget - l9c_gEnv) * (gTarget > l9c_gEnv ? 0.01f : 0.0004f);

        double load = 0.35 + 0.65 * ld;

        if (P.hollow > 0.001f)
        {
            double strike = fire * (1.0 + 0.6 * noise_lp);
            double a = l9c_hA.Strike(strike, 2.0 * Math.Sin(Math.PI * 210.0 / SR), 1.0 / 3.4);
            double b = l9c_hB.Strike(strike, 2.0 * Math.Sin(Math.PI * 520.0 / SR), 1.0 / 6.9);
            sum += P.hollow * (a + 0.6 * b) * (0.6f + ld * 0.4f + rn * 0.15f) * eBase * 0.85;
        }

        if (P.roar > 0.001f)
        {
            float rTarget = (float)(load * (0.35 + rn));
            l9c_rEnv += (rTarget - l9c_rEnv) * 0.002f;
            double mod = 0.72 + 0.28 * fire;
            double ex = noise_hi * 1.5;
            double r1 = l9c_rA.Strike(ex, 2.0 * Math.Sin(Math.PI * 140.0 / SR), 0.5);
            double r2 = l9c_rB.Strike(ex, 2.0 * Math.Sin(Math.PI * 300.0 / SR), 0.5);
            double r3 = l9c_rC.Strike(ex, 2.0 * Math.Sin(Math.PI * 560.0 / SR), 0.5);
            sum += P.roar * l9c_rEnv * mod * (r1 * 1.3 + r2 * 0.9 + r3 * 0.5) * eBase * 5.0;
        }

        if (P.airy > 0.001f)
        {
            float aTarget = (float)(load * (0.4 + 0.6 * rn));
            l9c_aEnv += (aTarget - l9c_aEnv) * 0.003f;
            double gg = 0.7 + 0.3 * fire;
            // Game calibration: the widget's airy level read as a constant fan/hiss in the game, so it is
            // trimmed hard and built from the low-passed noise (no top-end hiss).
            sum += P.airy * L9C_AIRY_TRIM * l9c_aEnv * (noise_lp * 1.0 + noise_hi * 0.15) * gg * eBase;
        }

        sum *= l9CharLevel;

        if (P.growl > 0.001f && l9c_gEnv > 0.001f)
        {
            double gate = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * ph_cb_fire);
            double rough = noise_lo * gate * 4.5 * eBase;
            sum += P.growl * l9c_gEnv * (rough + core * 0.45);
        }

        return sum;
    }
}
