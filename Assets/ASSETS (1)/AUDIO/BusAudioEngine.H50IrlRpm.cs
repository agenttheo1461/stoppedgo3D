using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  H50EP / ZH50EP — "IRL" power-demand rpm model (from the H50EP comparison
//  widget). Replaces the speed-driven EvtContinuousRPM curve for these two builds:
//    · stopped, no throttle  → idle
//    · coasting / braking    → falls toward ~900 rpm with speed (no regen hold by
//                              default; h50IrlRegenHold = true brings back the old
//                              game-style hold)
//    · under power           → base + (WOT − base) · throttle^0.9 + 0.8 · km/h
//    · rises 160 rpm/s (gentle climb after the launch jump), falls 300 rpm/s
//    · KICKDOWN keeps the game's held rev (1780 creeping to 1950 over ~9 s)
//  Not applied to h50ep_gen5 or the legacy ep50 (they keep the old model).
//  Set h50IrlRpm = false to return to the old curve. The L9/ISL9 launch jump in
//  BusAudioEngine.L9Character.cs sits on top of this as a floor.
// ═══════════════════════════════════════════════════════════════════════════════
public partial class BusAudioEngine
{
    public bool  h50IrlRpm        = true;
    public bool  h50IrlRegenHold  = false;
    public float h50IrlWotRpm     = 1850f;
    public float h50IrlBaseRpm    = 900f;
    public float h50IrlRiseRate   = 160f; // was 420 — gained rpm too fast right after the launch jump
    public float h50IrlFallRate   = 300f;

    private float h50i_rpm;
    private float h50i_kdCreep;

    private bool H50UsesIrlRpm => h50IrlRpm && (tx == "h50ep" || tx == "zh50ep");

    private float CalcH50EPIrlRPM(float dt)
    {
        if (gear == 0) { h50i_rpm = IDLE; h50i_kdCreep = 0f; return IDLE; }
        if (h50i_rpm < 1f) h50i_rpm = IDLE;

        float P = Mathf.Clamp01(accel);
        float tgt;
        if (spd < 0.5f && P < 0.04f)
        {
            tgt = IDLE;
        }
        else if (P < 0.02f && spd >= 2f)
        {
            if (h50IrlRegenHold)
            {
                float coastGate = Mathf.Clamp01((spd - 5f) / 2f);
                float brakeGate = Mathf.Clamp01(spd / 2f) * Mathf.Clamp01((bkPd - 0.02f) / 0.06f);
                float hold = Mathf.Max(150f * Mathf.Clamp01(spd / 20f) * coastGate,
                                       (260f + bkPd * 320f) * Mathf.Clamp01(spd / 15f) * brakeGate);
                tgt = h50IrlBaseRpm + hold;
            }
            else
            {
                tgt = Mathf.Lerp(IDLE + 60f, h50IrlBaseRpm - 60f, Mathf.Clamp01(spd / 40f));
            }
        }
        else
        {
            tgt = h50IrlBaseRpm + (h50IrlWotRpm - h50IrlBaseRpm) * Mathf.Pow(P, 0.9f) + 0.8f * spd;
        }

        if (hillMode) tgt += (spd / MAX_SPD) * 230f + accel * 140f;

        // Kickdown: the game's held rev with its slow creep.
        h50i_kdCreep = kickdownKey ? Mathf.Min(1f, h50i_kdCreep + dt / 9f)
                                   : Mathf.Max(0f, h50i_kdCreep - dt / 5f);
        if (kickdownKey) tgt = Mathf.Max(tgt, 1780f + h50i_kdCreep * 170f);

        tgt = Mathf.Clamp(tgt, IDLE, 1950f);
        float rate = tgt > h50i_rpm ? (kickdownKey ? Mathf.Max(h50IrlRiseRate, 230f) : h50IrlRiseRate) : h50IrlFallRate;
        h50i_rpm = Mathf.MoveTowards(h50i_rpm, tgt, rate * dt);
        return h50i_rpm;
    }

    // While the L9/ISL9 launch jump is playing, the model continues from the jump's rpm so there is
    // no dip when the jump ends, and the climb afterwards stays at the gentle rise rate.
    private void H50IrlFollowJump(float outRpm)
    {
        if (L9JumpActive && outRpm > h50i_rpm) h50i_rpm = outRpm;
    }
}
