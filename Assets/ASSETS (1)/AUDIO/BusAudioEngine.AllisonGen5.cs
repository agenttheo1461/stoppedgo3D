using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ALLISON GEN 5 (b400r_g5 / b500r_g5) — fully separated from Gen 4.
//  Behavior-identical copies of the Gen 4 rpm/shift logic and tuning these
//  two used to call into (CalcAllisonRPM/DoAllisonGear, CalcB500RRPM/
//  DoB500RGear, B4R_* tunables, AL_* tables, downshift blip, lock blend,
//  retarder spit), each with its OWN state, so Gen 4 and Gen 5 can now be
//  tuned independently. Gen 4-only sound triggers (b400_/b500_ thuds, the
//  legacy double-tap) were dropped from the copies -- Gen 5's DSP never
//  played them.
// ═══════════════════════════════════════════════════════════════════════════════
public partial class BusAudioEngine
{
    // ════════════════════════════ B400R GEN 5 ════════════════════════════
    public static readonly float[] AL_R_B4G5 = { 0f, 3.49f, 1.86f, 1.41f, 1.00f, 0.75f, 0.65f };

    public float B4G5_TCC_LOCK_KPH_G1   = 16f;
    public float B4G5_TCC_HYST_KPH      = 4f;
    public float B4G5_TCC_APPLY_TIME    = 0.55f;
    public float B4G5_TCC_SHIFT_RELEASE = 0.45f;
    public bool  B4G5_TCC_LOCK_IN_G1    = true;

    public float B4G5_AW_BASE_HZ        = 780f;
    public float B4G5_AW_RPM_SCALE      = 0.62f;
    public float B4G5_AW_SWELL_ATTACK   = 0.55f;
    public float B4G5_AW_SWELL_RELEASE  = 0.85f;
    public float B4G5_AW_AIR_GEAR_START = 5f;
    public float B4G5_AW_AIR_GEAR_FULL  = 6.3f;
    public float B4G5_AW_VOL            = 0.030f;
    public float B4G5_AW_AIR_NOISE_MIX  = 0.85f;

    private float b4g5_tccBlend, b4g5_tccReleaseTmr;
    private int   b4g5_tccPrevGear;
    private float b4g5_kdCreep;
    private float b4g5_smoothedRPM, b4g5_rpmVelocity;
    private bool  b4g5_blipActive; private float b4g5_blipTimer; private int b4g5_prevGearForBlip;

    private void G5B4_UpdateTCC(float dt)
    {
        if (gear != b4g5_tccPrevGear)
        {
            bool touchesGear1 = gear <= 1 || b4g5_tccPrevGear <= 1;
            if (touchesGear1) b4g5_tccReleaseTmr = B4G5_TCC_SHIFT_RELEASE;
            b4g5_tccPrevGear = gear;
        }
        if (b4g5_tccReleaseTmr > 0f) b4g5_tccReleaseTmr -= dt;

        bool wantLock;
        if (gear <= 0)      wantLock = false;
        else if (gear == 1) wantLock = B4G5_TCC_LOCK_IN_G1 && spd >= B4G5_TCC_LOCK_KPH_G1;
        else                wantLock = true;

        if (gear == 1 && b4g5_tccBlend > 0.5f && spd >= B4G5_TCC_LOCK_KPH_G1 - B4G5_TCC_HYST_KPH)
            wantLock = B4G5_TCC_LOCK_IN_G1;

        if (b4g5_tccReleaseTmr > 0f) wantLock = false;

        float rate = dt / Mathf.Max(0.01f, B4G5_TCC_APPLY_TIME);
        b4g5_tccBlend = Mathf.MoveTowards(b4g5_tccBlend, wantLock ? 1f : 0f, rate);
    }

    private void G5B4_UpdateKickdownCreep(float dt)
    {
        b4g5_kdCreep = kickdownKey
            ? Mathf.Min(1f, b4g5_kdCreep + dt / 9f)
            : Mathf.Max(0f, b4g5_kdCreep - dt / 5f);
    }

    private float G5B4_DownSpeed(int g)
    {
        if (g < 1 || g >= AL_R_B4G5.Length) return 0f;
        float perfHighTarget = 1100f;
        float ecoHighTarget  = 850f;
        float highTarget = (economyMode && !kickdownKey) ? ecoHighTarget : perfHighTarget;
        float r = Mathf.Lerp(700f, highTarget, Mathf.Clamp01(accel));
        return (r / AL_R_B4G5[g] / FINAL) * TCIRC / 60f * 3.6f;
    }

    private float G5B4_ShiftRPM(int fromGear)
    {
        int  toGear = fromGear + 1;
        bool isl9Character = engineType == EngineType.ISL9 || (kickdownKey && !economyMode);

        float shRPM_low = 1100f;
        if (isl9Character) shRPM_low *= 1.15f;

        float perfHigh = (toGear == 2 || toGear == 3) ? 1650f : 1700f;
        float ecoHigh  = 1350f;
        if (isl9Character) { perfHigh *= 1.15f; ecoHigh *= 1.15f; }

        float shRPM_wot = (economyMode && !kickdownKey) ? ecoHigh : perfHigh;
        float shRPM = Mathf.Lerp(shRPM_low, shRPM_wot, Mathf.Clamp01(accel));
        shRPM += b4g5_kdCreep * 70f;
        return Mathf.Min(shRPM, GOV - 30f);
    }

    private float G5B4_UpshiftSpeed(int fromGear)
    {
        bool  isl9Character = engineType == EngineType.ISL9 || (kickdownKey && !economyMode);
        float raw = (G5B4_ShiftRPM(fromGear) / AL_R_B4G5[fromGear] / FINAL) * TCIRC / 60f * 3.6f;
        if (fromGear == 1)
            raw = Mathf.Max(raw, G5B4_DownSpeed(2) + (isl9Character ? 3f : 6f));
        return raw;
    }

    private float G5B4_CalcRPM()
    {
        G5B4_UpdateTCC(Time.deltaTime);
        G5B4_UpdateKickdownCreep(Time.deltaTime);

        if (gear == 0) return IDLE;

        bool isl9Character = engineType == EngineType.ISL9 || (kickdownKey && !economyMode);
        float outRPM = (spd / 3.6f) / TCIRC * 60f;
        float inRPM  = outRPM * FINAL * AL_R_B4G5[gear];
        float targetRPM;

        if (gear == 1)
        {
            float slip      = economyMode ? 500f : (isl9Character ? 480f : 280f);
            float openRPM   = inRPM + (accel * slip + 40f);
            targetRPM = Mathf.Lerp(openRPM, inRPM, b4g5_tccBlend);
            targetRPM = Mathf.Min(targetRPM, G5B4_ShiftRPM(1));

            if (isl9Character && !economyMode)
            {
                float g1Progress = Mathf.Clamp01(spd / Mathf.Max(1f, G5B4_UpshiftSpeed(1)));
                float lateG1Boost = Mathf.Clamp01((g1Progress - 0.65f) / 0.35f);
                lateG1Boost *= lateG1Boost;
                targetRPM *= (1f + lateG1Boost * 0.65f);
                targetRPM = Mathf.Min(targetRPM, GOV);
            }
        }
        else if (gear < 6) targetRPM = inRPM;
        else               targetRPM = Mathf.Min(inRPM, G5B4_ShiftRPM(6));

        float rpmDampTime = isl9Character && !economyMode ? 0.08f : 0.12f;
        b4g5_smoothedRPM = Mathf.SmoothDamp(b4g5_smoothedRPM, targetRPM, ref b4g5_rpmVelocity, rpmDampTime);
        return Mathf.Max(IDLE, G5_DownshiftBlip(b4g5_smoothedRPM, targetRPM,
                               ref b4g5_blipActive, ref b4g5_blipTimer, ref b4g5_prevGearForBlip));
    }

    private void G5B4_DoGear()
    {
        if (gear == 0)
        {
            gear = 1; shiftCD = 0.5f; alShiftTransient = 1f; alShiftTransientDur = 0.22f;
            if (spd >= G5B4_UpshiftSpeed(1))
            {
                gear = 2; shiftCD = 0.55f; alShiftTransient = 1f; alShiftTransientDur = 0.22f;
                b4g5_rpmVelocity = 0f;
            }
            return;
        }
        if (shiftCD > 0f) return;

        if (gear < 6 && spd >= G5B4_UpshiftSpeed(gear))
        {
            gear++;
            shiftCD = 0.55f; alShiftTransient = 1f; alShiftTransientDur = 0.22f;
            b4g5_rpmVelocity = 0f;
            return;
        }
        if (gear > 1 && spd < G5B4_DownSpeed(gear))
        {
            gear--; shiftCD = 0.35f; alShiftTransient = 1f; alShiftTransientDur = 0.22f;
            b4g5_rpmVelocity = 0f;
        }
    }

    // ════════════════════════════ B500R GEN 5 ════════════════════════════
    public static readonly float[] AL_R_B5G5    = { 0f, 4.70f, 2.21f, 1.53f, 1.00f, 0.76f, 0.67f };
    public static readonly bool[]  AL_LOCK_B5G5 = { false, false, false, true, true, true, true };
    public const float B5G5_G1_UP = 20f; public const float B5G5_G2_DN = 16f;
    public const float B5G5_G2_UP = 36f; public const float B5G5_G3_DN = 33f;
    public const float B5G5_G3_UP = 52f; public const float B5G5_G4_DN = 49f;
    public const float B5G5_G4_UP = 66f; public const float B5G5_G5_DN = 62f;
    public const float B5G5_G5_UP = 80f; public const float B5G5_G6_DN = 74f;
    public float B5G5_TCC_APPLY_TIME    = 0.55f;
    public float B5G5_TCC_SHIFT_RELEASE = 0.45f;

    private float b5g5_tccBlend, b5g5_tccReleaseTmr;
    private bool  b5g5_lockStatePrev;
    private float b5g5_smoothedRPM, b5g5_rpmVelocity;
    private bool  b5g5_blipActive; private float b5g5_blipTimer; private int b5g5_prevGearForBlip;

    private void G5B5_UpdateLockBlend(bool wantLockedNow, float dt)
    {
        if (wantLockedNow != b5g5_lockStatePrev)
        {
            b5g5_tccReleaseTmr = B5G5_TCC_SHIFT_RELEASE;
            b5g5_lockStatePrev = wantLockedNow;
        }
        bool lockedNow = wantLockedNow;
        if (b5g5_tccReleaseTmr > 0f) { b5g5_tccReleaseTmr -= dt; lockedNow = false; }
        b5g5_tccBlend = Mathf.MoveTowards(b5g5_tccBlend, lockedNow ? 1f : 0f, dt / Mathf.Max(0.01f, B5G5_TCC_APPLY_TIME));
    }

    private float G5B5_LateGearRampBoost(int g, float up1, float boostAmount)
    {
        if (g < 1 || g > 5) return 0f;
        float[] upThresh = { 0f, up1, B5G5_G2_UP, B5G5_G3_UP, B5G5_G4_UP, B5G5_G5_UP };
        float gProgress = Mathf.Clamp01(spd / Mathf.Max(1f, upThresh[g]));
        float lateBoost = Mathf.Clamp01((gProgress - 0.65f) / 0.35f);
        return lateBoost * lateBoost * boostAmount;
    }

    private float G5B5_CalcRPM()
    {
        if (gear == 0) return IDLE;
        bool isl9Character = engineType == EngineType.ISL9 || (kickdownKey && !economyMode);
        int g = Mathf.Min(gear, AL_R_B5G5.Length - 1);
        float outShaft = (spd / 3.6f) / TCIRC * 60f;
        float inRPM    = outShaft * FINAL * AL_R_B5G5[g];
        G5B5_UpdateLockBlend(AL_LOCK_B5G5[g], Time.deltaTime);
        float openFrac = 1f - b5g5_tccBlend;
        float slipBase = (gear == 1 ? accel * 420f + 100f : accel * 130f + 35f) * openFrac;
        float slip = isl9Character ? slipBase * 1.55f : slipBase;
        float r = inRPM + slip;
        if (isl9Character) r = Mathf.Min(r, gear == 1 ? 2480f : 2050f);
        else               r = Mathf.Min(r, gear == 1 ? 1950f : 1850f);

        r *= 1f + G5B5_LateGearRampBoost(gear, isl9Character ? (B5G5_G2_DN + 3f) : B5G5_G1_UP,
                                         isl9Character ? 0.65f : 0.42f);
        float targetRPM = Mathf.Clamp(r, IDLE, GOV);

        float rpmDampTime = isl9Character && !economyMode ? 0.08f : 0.12f;
        b5g5_smoothedRPM = Mathf.SmoothDamp(b5g5_smoothedRPM, targetRPM, ref b5g5_rpmVelocity, rpmDampTime);
        return Mathf.Max(IDLE, G5_DownshiftBlip(b5g5_smoothedRPM, targetRPM,
                               ref b5g5_blipActive, ref b5g5_blipTimer, ref b5g5_prevGearForBlip));
    }

    private void G5B5_DoGear()
    {
        float ecoUpTighten = (economyMode && !kickdownKey) ? Mathf.Lerp(1.0f, 0.85f, Mathf.Clamp01(accel)) : 1.0f;
        float isl9G1Speed = B5G5_G2_DN + 3f;
        if (gear == 0) { gear = 1; shiftCD = 0.5f; alShiftTransient = 1f; alShiftTransientDur = 0.22f; return; }
        if (shiftCD > 0f) return;
        bool shifted = false;
        if      (gear == 1 && spd >= (engineType == EngineType.ISL9 ? isl9G1Speed : B5G5_G1_UP) * ecoUpTighten) { gear = 2; shifted = true; }
        else if (gear == 2 && spd >= B5G5_G2_UP * ecoUpTighten) { gear = 3; shifted = true; }
        else if (gear == 3 && spd >= B5G5_G3_UP * ecoUpTighten) { gear = 4; shifted = true; }
        else if (gear == 4 && spd >= B5G5_G4_UP * ecoUpTighten) { gear = 5; shifted = true; }
        else if (gear == 5 && spd >= B5G5_G5_UP * ecoUpTighten) { gear = 6; shifted = true; }
        if (shifted)
        {
            shiftCD = 0.65f; alShiftTransient = 1f; alShiftTransientDur = 0.26f;
            b5g5_rpmVelocity = 0f;
            return;
        }
        bool dn = false;
        float ecoResist = (economyMode && !kickdownKey) ? Mathf.Lerp(1.0f, 0.85f, Mathf.Clamp01(accel)) : 1.0f;
        if      (gear == 6 && spd < B5G5_G6_DN * ecoResist) { gear = 5; dn = true; }
        else if (gear == 5 && spd < B5G5_G5_DN * ecoResist) { gear = 4; dn = true; }
        else if (gear == 4 && spd < B5G5_G4_DN * ecoResist) { gear = 3; dn = true; }
        else if (gear == 3 && spd < B5G5_G3_DN * ecoResist) { gear = 2; dn = true; }
        else if (gear == 2 && spd < B5G5_G2_DN * ecoResist) { gear = 1; dn = true; }
        if (dn) { shiftCD = 0.45f; alShiftTransient = 1f; alShiftTransientDur = 0.22f; }
    }

    // ═════════════════════════ shared-shape helpers, Gen 5 copies ═════════════
    private float G5_DownshiftBlip(float r, float ceiling, ref bool active, ref float timer, ref int prevGear)
    {
        if (gear < prevGear && gear > 0 && prevGear > 0) { active = true; timer = 0f; }
        prevGear = gear;
        if (!active) return r;

        timer += Time.deltaTime;
        float blipAmt;
        if (timer < 0.18f)     blipAmt = Mathf.SmoothStep(0f, 1f, timer / 0.18f);
        else if (timer < 1.9f) blipAmt = Mathf.SmoothStep(1f, 0f, (timer - 0.18f) / 1.72f);
        else { active = false; blipAmt = 0f; }

        float blipPeak = Mathf.Min(ceiling, r + 260f);
        return Mathf.Lerp(r, blipPeak, blipAmt);
    }

    private void G5_RetarderSpit(ref double txSample, ref bool zoneWasActive,
                                 ref float tickVol, ref float spitVol,
                                 ref double phTick, ref double phSpit,
                                 float engMul, double invSR)
    {
        bool zoneNow = running && gear > 0 && accel < 0.04f && spd > 10f;
        if (zoneNow && !zoneWasActive) { tickVol = 1f; phTick = 0.0; }
        if (zoneWasActive && !zoneNow) { spitVol = 1f; phSpit = 0.0; }
        zoneWasActive = zoneNow;

        if (tickVol > 0.001f)
        {
            double tk = System.Math.Sin(2.0 * System.Math.PI * phTick);
            txSample += tk * tickVol * 0.045 * engMul + noise_hp_prev * tickVol * 0.02 * engMul;
            phTick = (phTick + 260.0 * invSR) % 1.0;
            tickVol *= 0.994f;
            if (tickVol < 0.004f) tickVol = 0f;
        }
        if (spitVol > 0.001f)
        {
            double hissCarrier = System.Math.Sin(2.0 * System.Math.PI * phSpit);
            double shaped = noise_hi * 0.78 + noise_lp * 0.22;
            txSample += shaped * spitVol * 0.16 * engMul + hissCarrier * spitVol * 0.025 * engMul;
            phSpit = (phSpit + 1150.0 * invSR) % 1.0;
            spitVol *= 0.9988f;
            if (spitVol < 0.004f) spitVol = 0f;
        }
    }
}
