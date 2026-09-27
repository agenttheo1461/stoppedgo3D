// VolumetricFogBox.shader  v2  -- "walk-in fog"
//
// Put this material on any cube (built-in Cube mesh, -0.5..0.5). The cube is a fog VOLUME:
//   * Invisible from OUTSIDE. Nothing is drawn unless the camera is inside the cube, so you never see a fog box.
//   * Once the camera is inside, the whole screen is fogged: every ray is marched from the camera to whatever
//     it hits first (ground, buildings, or the far end of the cube), clamped to the scene depth, so fog covers
//     nearby geometry as well as the sky.
//   * Real fog falloff (Beer-Lambert: alpha = 1 - exp(-density x distance)), so the fog gets denser with distance
//     and can actually be dense. _Density is extinction: 1.0 = roughly 20 m to fade to ~95% fog.
//   * Fades in smoothly as the camera walks in from a face (_EnterFade) and thins toward the cube boundary
//     (_EdgeSoftness) so there is no visible wall of fog when you look out of the volume.
//
// Renders the cube's BACK faces with ZTest Always: with the camera inside the cube those faces cover the whole
// screen, and the shader does its own depth handling (it can't rely on the depth test, because the back faces sit
// behind nearer scene geometry and would be rejected -- which is why the old version showed no fog inside).
// Needs the URP asset's "Depth Texture" enabled (PC_RPAsset has it on; Mobile_RPAsset does not).

Shader "Custom/VolumetricFogBox"
{
    Properties
    {
        [Header(Fog)]
        _FogColor      ("Fog Color", Color)            = (0.75, 0.82, 0.95, 1)
        _Density       ("Density (extinction)", Range(0, 5)) = 1.5
        _MaxOpacity    ("Max Opacity", Range(0, 1))    = 0.98
        _StepCount     ("Step Count", Range(8, 64))    = 32

        [Header(Volume Edges)]
        _EdgeSoftness  ("Boundary Falloff (metres)", Range(0.01, 200)) = 40
        _EnterFade     ("Enter Fade (metres)", Range(0.01, 200))       = 30

        [Header(Noise)]
        _NoiseScale    ("Noise Scale", Range(0.05, 8)) = 3.0
        _NoiseStrength ("Noise Strength", Range(0,1))  = 0.5
        _ScrollSpeed   ("Scroll Speed", Vector)        = (0.02, 0.01, 0.0, 0)
        _Octaves       ("Octaves", Range(1, 4))        = 3
        _OctaveGain    ("Octave Gain", Range(0.1, 0.9)) = 0.5
        _OctaveLacunarity ("Octave Lacunarity", Range(1.2, 3)) = 2.0

        [HideInInspector] _DepthFadeDistance ("(unused, kept so old materials still load)", Float) = 3
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Cull Front
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "VolumetricFogBox"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Attributes { float4 positionOS : POSITION; };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
                float4 screenPos  : TEXCOORD1;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _FogColor;
                float  _Density;
                float  _MaxOpacity;
                float  _StepCount;
                float  _EdgeSoftness;
                float  _EnterFade;
                float  _NoiseScale;
                float  _NoiseStrength;
                float4 _ScrollSpeed;
                float  _Octaves;
                float  _OctaveGain;
                float  _OctaveLacunarity;
                float  _DepthFadeDistance;
            CBUFFER_END

            // Extinction per metre for _Density = 1. 0.05 -> about 60 m of fog to reach ~95% at density 1.
            static const float EXTINCTION_PER_DENSITY = 0.05;

            float MinWorldScale()
            {
                float3 s = float3(length(unity_ObjectToWorld._m00_m10_m20),
                                  length(unity_ObjectToWorld._m01_m11_m21),
                                  length(unity_ObjectToWorld._m02_m12_m22));
                return min(s.x, min(s.y, s.z));
            }

            bool CameraInsideBox(out float3 camOS)
            {
                camOS = TransformWorldToObject(_WorldSpaceCameraPos);
                float3 a = abs(camOS);
                return max(a.x, max(a.y, a.z)) < 0.5;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionOS = IN.positionOS.xyz;
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 camOS;
                // Camera OUTSIDE the volume: push every vertex outside the clip volume so nothing is drawn at all.
                OUT.positionCS = CameraInsideBox(camOS) ? TransformWorldToHClip(positionWS) : float4(2, 2, 2, 1);
                OUT.screenPos  = ComputeScreenPos(OUT.positionCS);
                return OUT;
            }

            // Smooth 3D value noise (procedural: no baked 3D texture needed).
            float hash13(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.zyx + 31.32);
                return frac((p.x + p.y) * p.z);
            }
            float vnoise(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                float n000 = hash13(i);
                float n100 = hash13(i + float3(1,0,0));
                float n010 = hash13(i + float3(0,1,0));
                float n110 = hash13(i + float3(1,1,0));
                float n001 = hash13(i + float3(0,0,1));
                float n101 = hash13(i + float3(1,0,1));
                float n011 = hash13(i + float3(0,1,1));
                float n111 = hash13(i + float3(1,1,1));
                return lerp(lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                            lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y), f.z);
            }
            // Layered noise, normalised to 0..1 (mean ~0.5)
            float Fbm(float3 p)
            {
                float total = 0.0, ampSum = 0.0, amp = 1.0, freq = 1.0;
                int octaves = clamp((int)_Octaves, 1, 4);
                for (int o = 0; o < octaves; o++)
                {
                    total  += vnoise(p * freq) * amp;
                    ampSum += amp;
                    freq   *= _OctaveLacunarity;
                    amp    *= _OctaveGain;
                }
                return total / max(ampSum, 1e-4);
            }

            // ray vs unit cube (-0.5..0.5) in object space; camera is inside so tNear <= 0
            float RayExitUnitCube(float3 ro, float3 rd)
            {
                float3 invRd = 1.0 / rd;
                float3 t0 = (float3(-0.5, -0.5, -0.5) - ro) * invRd;
                float3 t1 = (float3( 0.5,  0.5,  0.5) - ro) * invRd;
                float3 tmax = max(t0, t1);
                return min(min(tmax.x, tmax.y), tmax.z);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 camOS;
                if (!CameraInsideBox(camOS)) discard;

                float3 rayOS = normalize(IN.positionOS - camOS);
                float tFar = RayExitUnitCube(camOS, rayOS);          // where this ray leaves the cube (object units)
                if (tFar <= 0.0) discard;

                float3 worldScale = float3(length(unity_ObjectToWorld._m00_m10_m20),
                                           length(unity_ObjectToWorld._m01_m11_m21),
                                           length(unity_ObjectToWorld._m02_m12_m22));
                float metresPerUnit = max(length(rayOS * worldScale), 1e-4);   // world metres per object unit along this ray

                // Clamp the march to the first solid surface along the ray (ground, buildings...); sky = far plane.
                float2 uv = IN.screenPos.xy / IN.screenPos.w;
                float rawDepth = SampleSceneDepth(uv);
                float3 scenePosWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                float tScene = distance(scenePosWS, _WorldSpaceCameraPos) / metresPerUnit;
                float tEnd = min(tFar, tScene);
                if (tEnd <= 0.0) discard;

                int steps = clamp((int)_StepCount, 4, 64);
                float dt = tEnd / steps;
                float dtMetres = dt * metresPerUnit;
                float minScale = MinWorldScale();
                float jitter = frac(52.9829189 * frac(dot(IN.positionCS.xy, float2(0.06711056, 0.00583715))));   // hides banding
                float3 scroll = _ScrollSpeed.xyz * _Time.y;

                float optical = 0.0;
                for (int i = 0; i < steps; i++)
                {
                    float t = (i + jitter) * dt;
                    float3 pos = camOS + rayOS * t;
                    // thin out toward the cube boundary so there is no wall of fog at the edge
                    float faceDist = (0.5 - max(abs(pos.x), max(abs(pos.y), abs(pos.z)))) * minScale;   // metres to the nearest face
                    float edge = smoothstep(0.0, 1.0, faceDist / max(_EdgeSoftness, 0.01));
                    float n = Fbm(pos * _NoiseScale + scroll);
                    float shape = max(0.0, lerp(1.0, n * 2.0, _NoiseStrength));   // averages 1
                    optical += _Density * EXTINCTION_PER_DENSITY * shape * edge * dtMetres;
                }

                // fade in as the camera walks in from a face (no pop when crossing the boundary)
                float camFaceDist = (0.5 - max(abs(camOS.x), max(abs(camOS.y), abs(camOS.z)))) * minScale;
                float enterFade = smoothstep(0.0, 1.0, camFaceDist / max(_EnterFade, 0.01));

                float alpha = (1.0 - exp(-optical)) * enterFade * _MaxOpacity;
                return half4(_FogColor.rgb, alpha * _FogColor.a);
            }
            ENDHLSL
        }
    }
}
