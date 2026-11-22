// A sight's glass (a scope's lens, a red dot's or holographic sight's window) as the thermal lens sees it: long-wave
// infrared does not pass glass, so it is a surface like any other, not a window. The game draws the glass after the
// opaque scene (transparent), so it is missing from the deferred buffers the thermal picture is made from; drawn here
// into the thermal copies themselves (lit picture, normals, colour, specular, depth), all at once.
Shader "Justachae/FPANO/GlassSurface"
{
    SubShader
    {
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            struct v2f { float4 pos : SV_POSITION; float3 n : TEXCOORD0; float3 toEye : TEXCOORD1; float depth : TEXCOORD2; };
            v2f vert(float4 vertex : POSITION, float3 normal : NORMAL)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.n = UnityObjectToWorldNormal(normal);
                float3 world = mul(unity_ObjectToWorld, vertex).xyz;
                o.toEye = _WorldSpaceCameraPos - world;
                o.depth = -UnityObjectToViewPos(vertex.xyz).z;
                return o;
            }
            struct targets
            {
                float4 lit : SV_Target0;       // the lit picture: glass absorbs little light
                float4 normals : SV_Target1;   // as the G-buffer's: the world normal, 0.5 + 0.5 n
                float4 albedo : SV_Target2;    // a mid grey
                float4 specular : SV_Target3;
                float4 depth : SV_Target4;     // linear eye depth, and no roughness within a pixel
            };
            targets frag(v2f i)
            {
                float3 n = normalize(i.n);
                if (dot(n, i.toEye) < 0.0)
                    n = -n;
                targets t;
                t.lit = float4(0, 0, 0, 1);
                t.normals = float4(n * 0.5 + 0.5, 1);
                // (about the air's warmth, as ENVG-B 0.65 had it: no colour of its own, no light taken in)
                t.albedo = float4(0, 0, 0, 1);
                t.specular = float4(0, 0, 0, 0);
                t.depth = float4(i.depth, 0, 0, 0);
                return t;
            }
            ENDCG
        }

        // 1: a sight's housing, its depth only: what stands in front of the glass as the lens sees it keeps it hidden
        Pass
        {
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            float4 frag() : SV_Target { return 0; }
            ENDCG
        }

        // 2: the hole in the thermal picture (the user's, 2026-11-14): where the line from the thermal lens through a
        // sight's glass goes on out through its objective end to the scene (more than 20 cm behind the glass), the
        // thermal picture is cut and the tubes' picture alone shows; where it meets the sight's own hood or tube inside
        // (nearer), the thermal picture stays. Over the depth the lens camera saw with no glass (its copy).
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _FpanoGlassBehind;   // the lens camera's linear depth (m), at the mask's own size and place
            struct v2f { float4 pos : SV_POSITION; float4 sp : TEXCOORD0; float depth : TEXCOORD1; };
            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.sp = ComputeScreenPos(o.pos);
                o.depth = -UnityObjectToViewPos(vertex.xyz).z;
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float behind = tex2D(_FpanoGlassBehind, i.sp.xy / i.sp.w).r;
                // (nothing behind it at all reads 0: the sky, far)
                return behind <= 0.0 || behind - i.depth > 0.2 ? 1 : 0;
            }
            ENDCG
        }
    }
}
