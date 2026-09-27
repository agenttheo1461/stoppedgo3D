using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusAudioEngine.WindNoise — aerodynamic wind rush at speed.
//
//  Universal, engine/transmission-agnostic -- this is airflow over the body,
//  not anything mechanical, so it's rendered in the main per-sample loop
//  (ProcessAudioCore in BusAudioEngine.cs) rather than inside
//  DoCombustionEngine, which only runs for combustion engineTypes. That
//  placement means it's the one speed-based ambient layer that's actually
//  present on every bus, electric drivetrains included, unlike the existing
//  wheel-bearing/hub whine (BusAudioEngine.AudioLayers.cs), which only fires
//  from inside DoCombustionEngine today.
//
//  Broadband, not tonal -- real aerodynamic turbulence is noise, not a pitch
//  -- and scales with the SQUARE of road speed (drag/turbulence intensity
//  roughly follows v^2), so it stays close to inaudible around city speeds
//  and only really becomes present approaching highway speed. A second,
//  lighter high-passed layer only cuts in near top speed for the thin
//  "whistle" edge real buses get at door seals/mirrors well above cruise.
// ═══════════════════════════════════════════════════════════════════════════════
public partial class BusAudioEngine
{
    private double _windLp1 = 0.0, _windLp2 = 0.0; // two-pole lowpass -- the low "rush" body
    private double _windHiLp = 0.0;                // lighter lowpass -- the high-speed whistle edge

    public float WIND_START_KPH = 35f;  // below this, essentially inaudible
    public float WIND_FULL_KPH  = 100f; // volume reaches WIND_MAX_VOL at/above this
    public float WIND_MAX_VOL   = 0.055f;

    /// <summary>Called once per sample from ProcessAudioCore, added straight
    /// into the final output mix alongside acSample/fuzzSample/etc. -- see
    /// this class's own header comment for why it lives here instead of
    /// inside DoCombustionEngine.</summary>
    private double RenderWindNoise(double invSR)
    {
        float speedFrac = Mathf.Clamp01((spd - WIND_START_KPH) / Mathf.Max(1f, WIND_FULL_KPH - WIND_START_KPH));
        if (speedFrac <= 0.0001f) return 0.0;

        float vol = speedFrac * speedFrac * WIND_MAX_VOL * npcVolumeScale;

        double raw = NextNoiseSample();

        // Low body of the rush -- two cascaded one-pole lowpasses, heavier
        // filtered than the engine's own noise_lp/noise_lo, for a smoother,
        // more "air" texture than anything mechanical in this file uses.
        _windLp1 += (raw - _windLp1) * 0.06;
        _windLp2 += (_windLp1 - _windLp2) * 0.03;
        double rush = _windLp2;

        // Thin whistle layer -- lighter filtering (more high content through),
        // only fades in over the top ~40% of the speed range so it reads as
        // a highway-speed trait, not part of the base rush.
        _windHiLp += (raw - _windHiLp) * 0.22;
        double whistle = _windHiLp * Mathf.Clamp01((speedFrac - 0.6f) / 0.4f);

        return (rush + whistle * 0.35) * vol;
    }
}
