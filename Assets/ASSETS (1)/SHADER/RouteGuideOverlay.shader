// Minimal unlit, vertex-colored overlay shader for RouteGuideArrows.
//
// Why this exists instead of "Sprites/Default": the guide ribbon/chevrons/
// stop-circles need to stay visible even when something transparent (e.g.
// WINDOW_1/WINDOW_2 via ToonRampLit, which hardcodes ZWrite On in its pass
// regardless of its own exposed "_ZWrite (auto)" toggle) has already written
// nearer depth at that pixel. Sprites/Default ZTests normally (LEqual) and
// exposes no way to override that from a material, so the guide mesh -- drawn
// after the window in queue order but physically farther from the camera --
// was failing the depth test and getting culled. ZTest Always here means this
// draws on top of everything regardless of what's already in the depth
// buffer, which is exactly what an X-ray-style route guide overlay wants.
//
// ZWrite stays Off (matches Sprites/Default) so the guide itself never blocks
// anything drawn after it.
Shader "Hidden/RouteGuideOverlay"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+100" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                fixed4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }
}
