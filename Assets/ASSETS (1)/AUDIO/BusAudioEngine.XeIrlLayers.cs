using System;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  XE60 / XHE60 "IRL" layers — added on top of DoZFAVE130DSP (every XE60/XHE60
//  drivetrain now runs through it; the centre-axle builds swap the ZF centre
//  bank for the in-wheel hub motor). Values are the ones tuned by ear in the
//  comparison widget:
//    · Launch shudder — 3 s of driveline shuffle once the bus passes ~8 km/h
//      from a stop: ~7 Hz uneven AM on motor/mesh + 31/63 Hz structural rumble.
//    · High speed (70→90 km/h) — motor +55% louder and THINNER (fundamental
//      and 2nd partial drop away; no treble boost).
//    · Twin-motor pound — the two rear induction motors drift ~1.4 Hz apart;
//      each beat lands as a 30 Hz + 15 Hz thump, carries a burst of the launch
//      shudder, and swells in the deep compressor.
//    · Motor top pitch 420 Hz at 105 km/h (was 610).
//    · Start-delay fix — under throttle the motor never sits below an audible
//      ~70 Hz floor, so it is heard the moment torque is applied instead of
//      fading in 2-3 s later once road speed lifts it out of the sub-bass.
// ═══════════════════════════════════════════════════════════════════════════════
public partial class BusAudioEngine
{
    public bool  xeIrlLayers           = true;
    public bool  xeLaunchShudder       = true;
    public bool  xeHighSpeedCharacter  = true;
    public bool  xeTwinMotorPound      = true;
    public bool  xePoundShudder        = true;
    public bool  xePoundCompressor     = true;
    [Tooltip("Deep compressor that swells in on each high-speed pound. True = rooftop A/C scroll compressor, false = BTMS battery-cooling compressor.")]
    public bool  xePoundCompressorIsAC = true;
    public float xeMotorTopHz          = 420f;
    public float xeLaunchFloorHz       = 70f;

    // Per-sample values shared between XeIrlBegin and the ZF voice.
    private float  xe_ps = 1f, xe_hs, xe_wob, xe_beatHz, xe_pound, xe_pulse, xe_sh;
    private float  xe_gain = 1f, xe_wF = 1f, xe_w2 = 1f;
    private double xe_shAM = 1.0, xe_fl = 1.0;

    private bool   xe_shudArmed = true;
    private float  xe_shudT = -1f, xe_shPSm, xe_shJ = 7f, xe_cmpVol, xe_cmpSag;
    private double ph_xe_beat, ph_xe_sh, ph_xe_sh13, ph_xe_sr, ph_xe_sr2, ph_xe_th, ph_xe_th2, ph_xe_ov, ph_xe_k1, ph_xe_k2;

    private float  zfh_hzSmooth, zfh_volSmooth;
    private double ph_zfh_1, ph_zfh_2, ph_zfh_cog;

    private float XeLaunchFloorHz(float ldS, float ratio) =>
        running ? xeLaunchFloorHz * ratio * Mathf.Clamp01(ldS * 2f) : 0f;

    private void XeIrlBegin(double invSR)
    {
        float dt  = (float)invSR;
        float srK = (float)(32000.0 * invSR); // widget coefficients were tuned at 32 kHz
        bool on = xeIrlLayers;
        xe_ps = on ? Mathf.Clamp(xeMotorTopHz, 200f, 700f) / 610f : 1f;

        if (spd < 1f) xe_shudArmed = true;
        if (xe_shudArmed && spd >= 8f && accel > 0.05f) { xe_shudArmed = false; xe_shudT = 0f; }
        float shLaunch = 0f;
        if (xe_shudT >= 0f)
        {
            xe_shudT += dt;
            float u = xe_shudT;
            shLaunch = u < 0.45f ? u / 0.45f : (u > 1.8f ? Mathf.Clamp01((3f - u) / 1.2f) : 1f);
            if (u >= 3f) xe_shudT = -1f;
        }
        if (!(on && xeLaunchShudder)) shLaunch = 0f;

        float x = Mathf.Clamp01((spd - 70f) / 20f);
        float hsS = x * x * (3f - 2f * x);
        xe_hs  = on && xeHighSpeedCharacter ? hsS : 0f;
        xe_wob = on && xeTwinMotorPound ? hsS : 0f;
        xe_beatHz = 1.4f * xe_wob;
        xe_pulse  = 0.5f + 0.5f * (float)Math.Cos(2.0 * Math.PI * ph_xe_beat);
        xe_pound  = xe_wob > 0f ? Mathf.Pow(xe_pulse, 5f) : 0f;

        float shPTgt = (on && xeTwinMotorPound && xePoundShudder) ? xe_wob * xe_pulse * xe_pulse * 0.85f : 0f;
        xe_shPSm += (shPTgt - xe_shPSm) * (shPTgt > xe_shPSm ? 0.003f : 0.0007f) * srK;
        xe_sh = Mathf.Max(shLaunch, xe_shPSm);

        xe_shJ  = Mathf.Clamp(xe_shJ + (float)(NextNoiseSample() * 30.0 * invSR), 5.5f, 9f);
        xe_shAM = 1.0 + xe_sh * 0.38 * Math.Sin(2.0 * Math.PI * ph_xe_sh);
        xe_fl   = 1.0 + xe_sh * 0.008 * Math.Sin(2.0 * Math.PI * ph_xe_sh13);

        xe_gain = 1f + 0.55f * xe_hs;
        xe_wF   = 1f - 0.45f * xe_hs;
        xe_w2   = 1f - 0.25f * xe_hs;
    }

    private void XeIrlEnd(ref double engineSample, float ldS, float engVolPersonality, double invSR)
    {
        float srK = (float)(32000.0 * invSR);
        float vol = npcVolumeScale * engVolPersonality;
        double s = 0.0;

        if (xe_sh > 0f)
            s += Math.Tanh((Math.Sin(2.0 * Math.PI * ph_xe_sr) * 0.8 + Math.Sin(2.0 * Math.PI * ph_xe_sr2) * 0.45 + noise_lo * 0.9) * 1.8)
               * xe_sh * 0.055 * (0.5 + 0.5 * Math.Sin(2.0 * Math.PI * ph_xe_sh)) * vol;

        if (xe_wob > 0f)
            s += (Math.Sin(2.0 * Math.PI * ph_xe_th) + Math.Sin(2.0 * Math.PI * ph_xe_th2) * 0.6) * xe_pound * xe_wob * 0.07 * vol;

        float cTgt = (xeIrlLayers && xeTwinMotorPound && xePoundCompressor && xe_wob > 0f) ? xe_wob * Mathf.Pow(xe_pulse, 1.5f) : 0f;
        xe_cmpVol += (cTgt - xe_cmpVol) * (cTgt > xe_cmpVol ? 0.0008f : 0.00028f) * srK;
        xe_cmpSag += (xe_pound - xe_cmpSag) * 0.0006f * srK;
        double cHz;
        if (xePoundCompressorIsAC)
        {
            float acF = acLevel > 0 ? Mathf.Clamp(acLevel / 100f, 0.2f, 1f) : 0.75f;
            cHz = (25.0 + 65.0 * acF) * (1.0 - 0.06 * xe_cmpSag);
            if (xe_cmpVol > 0.0005f)
                s += (Math.Sin(2.0 * Math.PI * ph_xe_k1) + Math.Sin(2.0 * Math.PI * ph_xe_k2) * 0.30 + noise_hi * 0.22)
                   * xe_cmpVol * (0.009 + acF * acF * 0.018) * 1.6 * vol;
        }
        else
        {
            cHz = (46.0 + ldS * 0.55 * 22.0) * (1.0 - 0.06 * xe_cmpSag);
            if (xe_cmpVol > 0.0005f)
                s += (Math.Sin(2.0 * Math.PI * ph_xe_k1) * 0.62 + noise_lp * 0.38) * xe_cmpVol * 0.026 * 1.6 * vol;
        }

        engineSample += s;

        ph_xe_k1   = (ph_xe_k1   + cHz          * invSR) % 1.0;
        ph_xe_k2   = (ph_xe_k2   + cHz * 2.0    * invSR) % 1.0;
        ph_xe_beat = (ph_xe_beat + xe_beatHz    * invSR) % 1.0;
        ph_xe_sh   = (ph_xe_sh   + xe_shJ       * invSR) % 1.0;
        ph_xe_sh13 = (ph_xe_sh13 + xe_shJ * 1.3 * invSR) % 1.0;
        ph_xe_sr   = (ph_xe_sr   + 31.0         * invSR) % 1.0;
        ph_xe_sr2  = (ph_xe_sr2  + 63.5         * invSR) % 1.0;
        ph_xe_th   = (ph_xe_th   + 30.0         * invSR) % 1.0;
        ph_xe_th2  = (ph_xe_th2  + 15.0         * invSR) % 1.0;
    }
}
