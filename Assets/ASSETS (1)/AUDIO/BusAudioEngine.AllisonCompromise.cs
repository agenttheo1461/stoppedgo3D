using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ALLISON B400R / B500R (Gen 4) — "compromise" rpm model.
//  RPM GAIN follows a real VAC-limited Allison (Altoona XD40 / ISL9 280 / B400R
//  reference): gentle converter launch, slow climb per gear, lockup in 2nd.
//  RPM DROP after a shift is left to the game's shared Tick down-slew
//  (280 rpm/s), which merges the shift drop and the lockup drop into one slide.
//    · Any throttle: upshift at ~1550 rpm (both modes).
//    · Kickdown: upshift at ~2000 rpm (Performance) / ~1800 (Economy), and a
//      power downshift whenever the next-lower gear is still under its shift
//      point.
//    · Economy locks the converter inside the 1→2 shift; Performance locks
//      shortly after it.
//    · Gear 6 is no longer capped at the shift rpm (the old cap froze rpm while
//      speed kept climbing).
//  The old model is untouched — set allisonCompromiseModel = false to use it.
//  Gen 5 (b400r_g5 / b500r_g5) has its own separate copy and is not affected.
// ═══════════════════════════════════════════════════════════════════════════════
public partial class BusAudioEngine
{
    public bool  allisonCompromiseModel = true;
    public float ALC_NORMAL_SHIFT_RPM  = 1550f;
    public float ALC_KD_PERF_SHIFT_RPM = 2000f;
    public float ALC_KD_ECO_SHIFT_RPM  = 1800f;

    // B500 real ratios (Allison SA2495); B400 uses AL_R, which already matches SA2392.
    private static readonly float[] ALC_R_B500 = { 0f, 3.51f, 1.91f, 1.43f, 1.00f, 0.74f, 0.64f };

    private float alc_lock, alc_lockDelay, alc_smoothed, alc_vel;
    private int   alc_prevGear;
    private bool  alc_wasLocked;

    private bool    AlcIsB500 => tx == "b500r";
    private float[] AlcR      => AlcIsB500 ? ALC_R_B500 : AL_R;
    private static float AlcK => 60f * FINAL / (3.6f * TCIRC); // rpm per km/h per unit ratio

    private float AlcShiftRPM()
    {
        if (kickdownKey) return Mathf.Min(economyMode ? ALC_KD_ECO_SHIFT_RPM : ALC_KD_PERF_SHIFT_RPM, GOV - 30f);
        return Mathf.Min(ALC_NORMAL_SHIFT_RPM, GOV - 60f);
    }

    // Gear 1 runs on the converter, so the engine leads the turbine by ~5% at the shift.
    private float AlcUpSpeed(int fromGear) =>
        AlcShiftRPM() * (fromGear == 1 ? 0.95f : 1f) / (AlcK * AlcR[fromGear]);

    private float AlcDownSpeed(int fromGear)
    {
        float dRPM = Mathf.Lerp(720f, kickdownKey ? 1100f : 900f, Mathf.Clamp01(accel));
        float v = dRPM / (AlcK * AlcR[fromGear]);
        return Mathf.Min(v, AlcUpSpeed(fromGear - 1) * 0.80f); // never re-satisfy the upshift we just made
    }

    private void AlcShiftSound(bool up, bool fromG1)
    {
        if (AlcIsB500)
        {
            b500_shiftThud = (up ? 1.0f : 0.70f) * AllisonGearTaper(gear);
            ph_b5_thud1 = 0.0; ph_b5_thud2 = 0.0;
        }
        else if (up && fromG1)
        {
            B4xOnG1toG2();
        }
        else
        {
            b400_shiftThud = (up ? 1.0f : 0.70f) * AllisonGearTaper(gear);
            ph_b400_thud1 = 0.0; ph_b400_thud2 = 0.0;
        }
    }

    private void AlcDoGear()
    {
        if (gear == 0)
        {
            gear = 1; shiftCD = 0.5f; alShiftTransient = 1f; alShiftTransientDur = 0.22f;
            if (AlcIsB500) { b500_shiftThud = AllisonGearTaper(1); ph_b5_thud1 = 0.0; ph_b5_thud2 = 0.0; }
            else           { b400_shiftThud = AllisonGearTaper(1); ph_b400_thud1 = 0.0; ph_b400_thud2 = 0.0; }
            al_prevGearForTap = gear;
            return;
        }
        if (shiftCD > 0f) return;

        if (gear < 6 && spd >= AlcUpSpeed(gear))
        {
            bool fromG1 = gear == 1;
            gear++;
            shiftCD = AlcIsB500 ? 0.65f : 0.55f; alShiftTransient = 1f; alShiftTransientDur = 0.22f;
            alc_vel = 0f;
            AlcShiftSound(true, fromG1);
            al_prevGearForTap = gear;
            return;
        }

        if (gear > 1)
        {
            bool kickdownDrop = kickdownKey && spd < AlcUpSpeed(gear - 1) * 0.92f;
            if (kickdownDrop || spd < AlcDownSpeed(gear))
            {
                gear--;
                shiftCD = 0.35f; alShiftTransient = 1f; alShiftTransientDur = 0.22f;
                alc_vel = 0f;
                AlcShiftSound(false, false);
            }
        }
        al_prevGearForTap = gear;
    }

    private float AlcCalcRPM()
    {
        float dt = Time.deltaTime;
        l9c_preset = ResolveL9Preset();
        if (gear == 0)
        {
            alc_lock = 0f; b4r_tccBlend = 0f; alc_prevGear = 0;
            l9j_t = -1f;
            return IDLE;
        }

        float[] R = AlcR;
        int g = Mathf.Clamp(gear, 1, 6);
        float t = spd * AlcK * R[g];

        // Converter: VAC-limited launch, slip shrinking toward coupling.
        float a = kickdownKey ? 1f : Mathf.Clamp01(accel);
        float tf = Mathf.Clamp01(t / 2200f);
        float slip = 430f * a * (1f - tf * tf);
        float eConv = Mathf.Max(IDLE + 0.6f * slip, t + slip);

        // Lockup: Economy applies it inside the 1→2 shift, Performance just after.
        if (gear != alc_prevGear)
        {
            if (gear < 2) alc_lockDelay = 0f;
            else if (gear == 2 && alc_prevGear == 1) alc_lockDelay = economyMode ? 0f : shiftCD + 0.30f;
            alc_prevGear = gear;
        }
        if (alc_lockDelay > 0f) alc_lockDelay -= dt;
        float lockOn  = economyMode ? 600f : 1000f;
        float lockOff = economyMode ? 550f : 880f;
        bool wantLock = g >= 2 && alc_lockDelay <= 0f && t > (alc_lock > 0.5f ? lockOff : lockOn);
        alc_lock = Mathf.MoveTowards(alc_lock, wantLock ? 1f : 0f, dt / (wantLock ? 0.5f : 0.25f));
        b4r_tccBlend = alc_lock; // drives the existing converter-whoosh layers in the B400R/B500R DSP

        bool lockedNow = alc_lock > 0.5f;
        if (lockedNow && !alc_wasLocked)
        {
            if (AlcIsB500) { b500_lockupThud = 1f; ph_b5_wh2 = 0.0; }
            else           b400_lockupThud = 1f;
        }
        alc_wasLocked = lockedNow;

        float target = Mathf.Clamp(Mathf.Lerp(eConv, t, alc_lock), IDLE, GOV);
        alc_smoothed = Mathf.SmoothDamp(alc_smoothed < 1f ? IDLE : alc_smoothed, target, ref alc_vel, 0.12f,
                                        Mathf.Infinity, Mathf.Max(1e-4f, dt));
        float rpmOut = Mathf.Max(IDLE, ApplyAllisonDownshiftBlip(alc_smoothed, target));
        return L9LaunchJump(rpmOut, a); // L9/ISL9 launch flare — see BusAudioEngine.L9Character.cs
    }
}
