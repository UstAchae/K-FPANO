// A light source as the eye or a night-vision tube sees it: a camera-facing glow at a lens, drawn with the scene (so the
// game's night vision and K-3PS-C's eyepieces see it as they see everything else). A hot core, a halo, and (for the IR
// strobe through a tube) the long star spikes of the user's videos. Its world size grows with the square root of the
// distance (a bloom shrinks slower than the object); pulled a little toward the camera (no more than its own size) so
// the lamp's own glass does not hide it. A lamp shines forward: _Dir.w > 0 fades it as the lens turns away (the
// object's +z is the lens's outward axis).
Shader "Justachae/FPANO/StrobeGlow"
{
    Properties
    {
        _Color ("Colour x intensity", Color) = (1, 1, 1, 1)
        _Size ("Radius at 1 m", Float) = 0.1
        _Shape ("core, halo, spikes, spike width", Vector) = (0.12, 0.45, 0.0, 0.012)
        _Dir ("x: forward only (0 all round, else the power of the falloff)", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Color;
            float _Size;
            float4 _Shape;
            float4 _Dir;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float facing : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                float3 centre = mul(unity_ObjectToWorld, float4(0, 0, 0, 1)).xyz;
                float3 axis = normalize(mul((float3x3)unity_ObjectToWorld, float3(0, 0, 1)));
                float3 toCam = _WorldSpaceCameraPos - centre;
                float dist = max(length(toCam), 0.05);
                float3 fwd = toCam / dist;
                float3 right = normalize(UNITY_MATRIX_V[0].xyz);
                float3 up = normalize(UNITY_MATRIX_V[1].xyz);
                float size = _Size * sqrt(max(dist, 0.3));
                float3 world = centre + fwd * min(min(0.04, _Size), dist * 0.5) + (right * v.vertex.x + up * v.vertex.y) * size;
                o.pos = mul(UNITY_MATRIX_VP, float4(world, 1));
                o.uv = v.vertex.xy;
                o.facing = _Dir.x > 0 ? pow(saturate(dot(axis, fwd)), _Dir.x) : 1.0;
                return o;
            }

            float spike(float2 uv, float angle, float len, float width)
            {
                float2 d = float2(cos(angle), sin(angle));
                float along = abs(dot(uv, d));
                float across = abs(dot(uv, float2(-d.y, d.x)));
                float fall = saturate(1.0 - along / len);
                return exp(-across * across / (width * width)) * fall * fall;
            }

            float4 frag(v2f i) : SV_Target
            {
                float r = length(i.uv);
                float core = exp(-r * r / (_Shape.x * _Shape.x));
                float halo = exp(-r * r / (_Shape.y * _Shape.y)) * 0.35;
                float rays = 0.0;
                if (_Shape.z > 0.0)
                {
                    float w = _Shape.w * (1.0 + 4.0 * r);
                    rays = spike(i.uv, 0.52, 1.0, w) + spike(i.uv, 2.09, 0.85, w) + 0.6 * spike(i.uv, 1.31, 0.6, w) + 0.6 * spike(i.uv, 2.88, 0.55, w);
                    rays *= _Shape.z;
                }
                float v = (core + halo + rays) * saturate(1.2 - r) * i.facing;
                return float4(_Color.rgb * v, 0);
            }
            ENDCG
        }
    }
}
