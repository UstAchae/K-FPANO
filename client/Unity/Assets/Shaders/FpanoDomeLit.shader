// The HEL-STAR's clear dome lit from inside for the flash, as in the user's daylight video: the dome's own geometry
// drawn again over itself, added light only, brightest where it faces the eye (the light comes through the housing
// toward the viewer), no halo around it. Both faces (a mirrored right-hand strobe turns the winding).
Shader "Justachae/FPANO/DomeLit"
{
    Properties
    {
        _Color ("Colour x intensity", Color) = (1, 0.95, 0.8, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+11" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -1, -1
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Color;

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float3 n : TEXCOORD0; float3 v : TEXCOORD1; };

            v2f vert(appdata i)
            {
                v2f o;
                float3 world = mul(unity_ObjectToWorld, i.vertex).xyz;
                o.pos = UnityWorldToClipPos(world);
                o.n = UnityObjectToWorldNormal(i.normal);
                o.v = _WorldSpaceCameraPos - world;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float facing = abs(dot(normalize(i.n), normalize(i.v)));
                return float4(_Color.rgb * (0.35 + 0.65 * facing), 0);
            }
            ENDCG
        }
    }
}
