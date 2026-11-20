// Where people are in the frame: their body and gear drawn again over the frame's own depth (equal depth passes), as
// a mask for body heat in the long-wave picture.
Shader "Justachae/FPANO/BodyMask"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -1, -1
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 vert(float4 vertex : POSITION) : SV_POSITION
            {
                return UnityObjectToClipPos(vertex);
            }

            // how far this body has cooled toward the air (0 alive; a corpse cools after death): the mask holds its
            // warmth, 1 at a living body's surface temperature
            float _EnvgBodyCool;
            float4 frag() : SV_Target
            {
                return 1.0 - saturate(_EnvgBodyCool);
            }
            ENDCG
        }
    }
}
