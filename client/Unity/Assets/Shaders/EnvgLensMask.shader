// A magnified scope's lens as the eye sees it (the game's own lens renderer, which shows the scope's picture), drawn
// over the frame's own depth: where the scope's picture is, the thermal picture is left out (the scope's reticle area
// only; its housing keeps the thermal picture).
Shader "Justachae/FPANO/LensMask"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            float4 frag() : SV_Target { return 1; }
            ENDCG
        }
    }
}
