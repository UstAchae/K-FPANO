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
        // window glass on the level (ThermalScene): an even colour into the lens camera's diffuse colour
        Pass
        {
            Name "WORLDGLASS"
            ZWrite Off
            ZTest LEqual
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _ThermalWorldGlass;
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex.xyz); }
            float4 frag() : SV_Target { return _ThermalWorldGlass; }
            ENDCG
        }
            // window glass on the level (ThermalScene), into the lens camera's G-buffer: an opaque surface of its own
        Pass
        {
            Name "WORLDGLASSGB"
            ZWrite On
            ZTest LEqual
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _ThermalWorldGlass;
            struct v2f { float4 pos : SV_POSITION; float3 n : TEXCOORD0; float3 wp : TEXCOORD1; };
            v2f vert(float4 vertex : POSITION, float3 normal : NORMAL)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex.xyz);
                o.n = UnityObjectToWorldNormal(normal);
                o.wp = mul(unity_ObjectToWorld, vertex).xyz;
                return o;
            }
            void frag(v2f i, out half4 g0 : SV_Target0, out half4 g1 : SV_Target1, out half4 g2 : SV_Target2, out half4 g3 : SV_Target3)
            {
                float3 n = normalize(i.n);
                if (dot(n, _WorldSpaceCameraPos - i.wp) < 0.0)
                    n = -n;
                g0 = half4(_ThermalWorldGlass.rgb, 1);
                g1 = half4(0.04, 0.04, 0.04, 0.5);
                g2 = half4(n * 0.5 + 0.5, 1);
                g3 = 0;
            }
            ENDCG
        }
    }
}
