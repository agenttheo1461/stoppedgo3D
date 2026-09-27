// ═══════════════════════════════════════════════════════════════════════════════
//  Custom/DotMatrixDot
//
//  Unlit shader for the dot-matrix boards. Every dot is just a flat quad in
//  the mesh (see DotMatrixText.cs) -- this shader procedurally masks each
//  quad down to a soft-edged circle using its UVs, so it reads as a round
//  glowing LED instead of a square pixel. No texture asset required.
//
//  Color comes from PER-VERTEX color (set by DotMatrixText per row), not a
//  shader property, so one shared material can drive every board/row/bus in
//  the fleet with different colors at once.
// ═══════════════════════════════════════════════════════════════════════════════
Shader "Custom/DotMatrixDot"
{
    Properties
    {
        _GlowPower ("Glow Falloff", Range(0.5, 4)) = 1.6
        _CoreSize  ("Solid Core Size", Range(0.0, 1.0)) = 0.55
    }
    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" "IgnoreProjector"="True" }
        LOD 100

        ZWrite On
        Cull Off
        AlphaToMask On // let MSAA resolve partial pixel coverage on sub-pixel-sized dots
                        // instead of each pixel hard-snapping in/out between frames

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv     : TEXCOORD0;
                fixed4 color  : COLOR;
            };

            float _GlowPower;
            float _CoreSize;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv     = v.uv;
                o.color  = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // UV space per-dot is 0..1 on both axes; centre = 0.5,0.5
                float2 centered = (i.uv - 0.5) * 2.0;
                float dist = length(centered);

                // Anti-aliased circle edge instead of a hard clip(). A hard
                // clip has no in-between: on a distant/small dot whose whole
                // footprint is under a pixel, each pixel's pass/fail flips
                // between frames as the camera subpixel-shifts, which reads
                // as the dot flickering in and out of existence. fwidth(dist)
                // gives the screen-space rate of change of dist, so we fade
                // alpha to 0 over roughly one pixel at the edge -- combined
                // with AlphaToMask On (see SubShader tags above), MSAA then
                // resolves genuine partial coverage instead of a binary flip.
                float edgeAA = fwidth(dist);
                float edgeAlpha = 1.0 - smoothstep(1.0 - edgeAA, 1.0 + edgeAA, dist);
                clip(edgeAlpha - 0.001); // still discard fully-outside pixels, cheaply

                float core = 1.0 - smoothstep(_CoreSize, 1.0, dist);
                float glow = pow(saturate(1.0 - dist), _GlowPower);
                float boost = saturate(core + glow * 0.6);

                fixed4 col = i.color;
                col.rgb *= lerp(0.85, 1.15, boost); // subtle core/glow shading, opaque either way
                col.a = edgeAlpha; // AlphaToMask reads this for MSAA coverage
                return col;
            }
            ENDCG
        }
    }
}
