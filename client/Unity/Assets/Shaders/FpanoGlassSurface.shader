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

        // 1: a sight's own housing, its depth only (faces culled as the scene draws them: a tube's inside is no wall)
        Pass
        {
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            float4 frag() : SV_Target { return 0; }
            ENDCG
        }

        // The hole in the thermal picture (the user's, 2026-11-14): where the line from the thermal lens through a
        // sight's glass goes on out through its objective end, the thermal picture is cut and the tubes' picture alone
        // shows; where it meets the sight's OWN hood or tube inside within 20 cm, the thermal picture stays. The sight's
        // own shape, from its glass; nothing of the game's reticle or scope mask. Two passes into the mask:
        // 3: the glass seen at all, against the lens camera's own depth buffer: 1 where nothing stands in front of it
        //    (a hand, a helmet keep their thermal picture).
        // 2: then, against a depth buffer of the sight's own housing alone, the glass pushed 20 cm on along each pixel's
        //    ray (the same pixel, 20 cm deeper): 0 where the housing lies nearer than that (its hood or tube inside).
        //    The gun's own parts beyond the sight (a front sight, a rail) are not its housing: the hole goes on over them
        //    (user: "瞄具区域里面能看到枪自己的热融合").
        Pass
        {
            ZWrite Off
            ZTest Greater
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 vert(float4 vertex : POSITION) : SV_POSITION
            {
                float3 v = UnityObjectToViewPos(vertex.xyz);
                float d = max(-v.z, 1e-4);
                v *= (d + 0.2) / d;                       // along the ray: the same pixel, 20 cm deeper
                return mul(UNITY_MATRIX_P, float4(v, 1.0));
            }
            float4 frag() : SV_Target { return 0; }
            ENDCG
        }

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
