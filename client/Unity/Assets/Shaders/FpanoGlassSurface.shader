// The sights' hole in the thermal picture (the user's, 2026-11-14): where the line from the thermal lens through a sight's
// glass (a scope's lens, a red dot's or holographic sight's window) goes on out through its objective end, the thermal
// picture is cut and the tubes' picture alone shows. The sight's own shape, from its glass; nothing of the game's reticle
// or scope mask.
//
// Drawn against the lens camera's own depth buffer, the glass pushed on along each pixel's ray (the same pixel, deeper)
// to just past its own sight's far end (_FpanoSightFar, view depth; 0: 20 cm on): it passes only where nothing opaque
// lies between the glass and there. In front of the glass (a hand, a helmet) or the sight's own hood or tube inside: no
// hole. The gun's own parts beyond the sight (a front sight, a rail): hole.
Shader "Justachae/FPANO/GlassSurface"
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
            float _FpanoSightFar;
            float4 vert(float4 vertex : POSITION) : SV_POSITION
            {
                float3 v = UnityObjectToViewPos(vertex.xyz);
                float d = max(-v.z, 1e-4);
                float to = _FpanoSightFar > 0.0 ? max(_FpanoSightFar, d + 0.02) : d + 0.2;
                v *= to / d;
                return mul(UNITY_MATRIX_P, float4(v, 1.0));
            }
            float4 frag() : SV_Target { return 1; }
            ENDCG
        }
    }
}
