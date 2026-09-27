// NightCitySkybox.shader  — v2
// Procedural day/night skybox for URP.  Driven by DayNightController.cs which
// sets three global shader properties each frame:
//
//   _SkyboxTimeOfDay  (float)   0 = midnight · 0.25 = 6 AM · 0.5 = noon · 0.75 = 6 PM
//   _SkyboxSunDir     (float4)  world-space unit vector pointing TOWARD the sun
//                               (set to -sunLight.transform.forward in DayNightController)
//   _SkyboxSunColor   (float4)  current sun light color (matches sunColor gradient)
//
// Without DayNightController the skybox defaults to full night (all three globals
// zero-initialise, which maps to midnight).
//
// Day/night behaviour:
//   Stars      → fade out between 5 AM (0.208) and 8 AM (0.333), back in at dusk.
//   City glow  → fades to 10% during full day, surges back at dusk.
//   Moon       → fades with stars; can be above/below horizon independently.
//   Sun disc   → appears at dawn, peaks at noon, sets at dusk, driven by _SkyboxSunDir.
//   Sky colour → smoothly blends night deep-blue ↔ day clear-blue ↔ dawn/dusk warm orange.
//
// Setup: Window > Rendering > Lighting > Environment > Skybox Material

Shader "Custom/NightCitySkybox"
{
    Properties
    {
        [Header(Night Sky Gradient)]
        _ZenithColor  ("Night Zenith",  Color) = (0.02, 0.04, 0.12, 1)
        _HorizonColor ("Night Horizon", Color) = (0.18, 0.27, 0.46, 1)
        _GradientPower ("Gradient Falloff", Range(0.2, 4)) = 1.2

        [Header(Day Sky Gradient)]
        _DayZenithColor  ("Day Zenith",  Color) = (0.12, 0.36, 0.82, 1)
        _DayHorizonColor ("Day Horizon", Color) = (0.55, 0.76, 0.98, 1)

        [Header(Dawn Dusk Tint)]
        _DawnDuskTint ("Dawn/Dusk Horizon Tint", Color) = (1.0, 0.48, 0.12, 1)
        _DawnDuskSpread ("Dawn/Dusk Spread",     Range(0.0, 1.0)) = 0.45

        [Header(City Light Pollution Glow)]
        _GlowColor     ("Glow Color",     Color)      = (0.95, 0.78, 0.42, 1)
        _GlowHeight    ("Glow Height",    Range(0.01,1)) = 0.18
        _GlowIntensity ("Glow Intensity", Range(0, 4))   = 1.3

        [Header(Stars)]
        _StarsScale     ("Stars Density",  Range(20,800))    = 250
        _StarsIntensity ("Stars Intensity",Range(0,3))       = 0.6
        _StarsThreshold ("Stars Sparseness",Range(0.9,0.999)) = 0.994

        [Header(Moon)]
        _MoonDirection ("Moon Direction", Vector) = (0.35, 0.55, 0.25, 0)
        _MoonColor     ("Moon Color",     Color)  = (0.96, 0.97, 0.92, 1)
        _MoonSize      ("Moon Angular Size",Range(0.0005,0.02)) = 0.0025
        _MoonGlow      ("Moon Halo Intensity",Range(0,2))       = 0.5

        [Header(Sun Disc  direction driven by DayNightController globals)]
        _SunDiscSize      ("Sun Angular Size",    Range(0.001,0.05)) = 0.006
        _SunHaloIntensity ("Sun Halo Intensity",  Range(0,4))        = 1.5
        _SunHaloPower     ("Sun Halo Falloff",    Range(2,32))       = 8.0
    } 

    SubShader
    {
        Tags
        {
            "Queue"          = "Background"
            "RenderType"     = "Background"
            "PreviewType"    = "Skybox"
            "RenderPipeline" = "UniversalPipeline"
        }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // ── Per-material properties ──────────────────────────────────────────
            half4 _ZenithColor, _HorizonColor;
            half4 _DayZenithColor, _DayHorizonColor;
            half4 _DawnDuskTint;
            half  _DawnDuskSpread;
            half  _GradientPower;

            half4 _GlowColor;
            half  _GlowHeight, _GlowIntensity;

            half  _StarsScale, _StarsIntensity, _StarsThreshold;

            half4 _MoonDirection, _MoonColor;
            half  _MoonSize, _MoonGlow;

            half  _SunDiscSize, _SunHaloIntensity, _SunHaloPower;

            // ── Globals set by DayNightController.cs ────────────────────────────
            // Declared outside CBUFFER — Shader.SetGlobalXxx writes here.
            float  _SkyboxTimeOfDay;   // 0=midnight, 0.5=noon
            float4 _SkyboxSunDir;      // world-space unit vector toward the sun
            half4  _SkyboxSunColor;    // current sun light tint

            struct Attributes { float4 positionOS : POSITION; };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 dir        : TEXCOORD0;
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.dir        = normalize(IN.positionOS.xyz);
                return OUT;
            }

            // Simple hash for procedural stars
            float Hash3(float3 p)
            {
                p  = frac(p * float3(0.1031, 0.1030, 0.0973));
                p += dot(p, p.yxz + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                float3 dir = normalize(IN.dir);
                float  tod = _SkyboxTimeOfDay;   // shorthand

                // ── Day / night / transition factors ─────────────────────────────
                //
                // tod timeline (approximate Miami, not adjusted for seasons):
                //   0.00 = midnight
                //   0.21 = 5 AM  — civil twilight begins  (stars start fading)
                //   0.26 = 6:15 AM — sunrise
                //   0.31 = 7:30 AM — full day sky
                //   0.67 = 4 PM   — sky still full day
                //   0.72 = 5:15 PM — golden hour begins
                //   0.77 = 6:30 PM — sunset
                //   0.82 = 8 PM   — stars return
                //
                float morningRise = smoothstep(0.21, 0.31, tod);
                float eveningFall = 1.0 - smoothstep(0.70, 0.82, tod);
                float dayFactor   = morningRise * eveningFall;   // 1 at full day, 0 at night

                // Dawn tint: peaks around sunrise (0.26), width controlled by inspector
                float dawnPeak   = smoothstep(0.17, 0.26, tod) * (1.0 - smoothstep(0.26, 0.36, tod));
                // Dusk tint: peaks around sunset (0.77)
                float duskPeak   = smoothstep(0.67, 0.77, tod) * (1.0 - smoothstep(0.77, 0.86, tod));
                float dawnDusk   = saturate(dawnPeak + duskPeak); // combined transition factor

                // ── Sky gradient ─────────────────────────────────────────────────
                float t      = pow(saturate(dir.y), _GradientPower);
                half3 nightSky = lerp(_HorizonColor.rgb, _ZenithColor.rgb, t);
                half3 daySky   = lerp(_DayHorizonColor.rgb, _DayZenithColor.rgb, t);
                half3 sky      = lerp(nightSky, daySky, dayFactor);

                // Warm dawn/dusk tint — strongest near horizon, fades upward
                float horizonMask = saturate(1.0 - dir.y * (1.0 / max(_DawnDuskSpread, 0.01)));
                sky = lerp(sky, sky * _DawnDuskTint.rgb * 1.5, dawnDusk * horizonMask);

                // ── City light-pollution glow ─────────────────────────────────────
                // Modified: Keep it subtle at night, pop during dusk/dawn transitions, faint in day.
                float nightGlowFactor = 0.35; // Lower this to 0.0 for pure pitch-black night horizons
                float glowDayMute = lerp(nightGlowFactor, 0.05, dayFactor) + (dawnDusk * 0.8);

                float glow = exp(-max(dir.y, 0.0) / max(_GlowHeight, 0.001))
                           * step(0.0, dir.y + 0.02);
                sky += _GlowColor.rgb * glow * _GlowIntensity * glowDayMute;

                // ── Stars ─────────────────────────────────────────────────────────
                // starDayFade: 1 at night, 0 during day, brief twinkle at very deep dusk.
                float starDayFade = 1.0 - dayFactor;
                float3 cell    = floor(dir * _StarsScale);
                float  starN   = Hash3(cell);
                float  starMask = step(_StarsThreshold, starN);
                float  starAlt  = saturate(dir.y * 3.0);        // fade near horizon
                sky += starMask * _StarsIntensity * starAlt * starDayFade;

                // ── Moon disc + halo ──────────────────────────────────────────────
                // Moon fades with stars — invisible during full day.
                float moonFade  = starDayFade;
                float3 moonDir  = normalize(_MoonDirection.xyz);
                float  moonDot  = dot(dir, moonDir);
                float  moonDisc = smoothstep(1.0 - _MoonSize, 1.0 - _MoonSize * 0.3, moonDot);
                float  moonHalo = pow(saturate(moonDot), 80.0) * _MoonGlow;
                sky += _MoonColor.rgb * (moonDisc + moonHalo) * moonFade;

                // ── Sun disc + halo ───────────────────────────────────────────────
                // Direction toward sun comes from _SkyboxSunDir (global set by
                // DayNightController as -sunLight.transform.forward).
                // When the sun is below horizon its direction vector has a negative
                // Y component, so the dot product with any above-horizon skybox ray
                // is small and the disc/halo naturally disappear.
                float3 sunDir = normalize(_SkyboxSunDir.xyz + float3(0,0.0001,0)); // safety
                float  sunDot = dot(dir, sunDir);

                // Halo: broad atmospheric scatter, visible even at dusk.
                float sunHalo = pow(saturate(sunDot), _SunHaloPower)
                              * _SunHaloIntensity
                              * saturate(dayFactor + dawnDusk * 0.7);

                // Disc: hard sun circle, visible when above horizon.
                float sunOnHorizonSide = step(0.0, sunDir.y);  // 1 above, 0 below
                float sunDisc = smoothstep(1.0 - _SunDiscSize,
                                           1.0 - _SunDiscSize * 0.25, sunDot)
                              * sunOnHorizonSide;

                // Tint both halo and disc by the actual light color so the disc
                // goes orange at dusk automatically without extra setup.
                half3 sunTint = lerp(half3(1.3, 1.2, 1.0), _SkyboxSunColor.rgb * 1.5, dawnDusk);
                sky += sunTint * (sunDisc + sunHalo);

                return half4(sky, 1.0);
            }
            ENDHLSL
        }
    }
}