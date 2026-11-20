// The F-PANO's image-intensifier channel: white phosphor (P45) tubes. Applied where the game applies its own night
// vision (SSAAPropagator's night-vision material: the HDR frame before tone mapping, with the game's night-vision
// ambient on), so one pass only (the game blits it with every pass).
Shader "Justachae/FPANO/WhitePhosphor"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _EnvgI2;      // x gain, y noise, w grain size in pixels
            float4 _EnvgI2Time;  // x noise frame index (30 Hz), y: 1 once _EnvgAbc holds the scene's mean
            sampler2D _EnvgAbc;  // 1 x 1: the scene's mean signal, followed slowly (the brightness control's lag)

            // the photocathode's response: strong in red and near infrared, weak in blue
            float signal(float3 c)
            {
                return dot(max(c, 0), float3(0.45, 0.45, 0.10));
            }

            float hash(float2 p, float t)
            {
                float3 q = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973) + t * 0.6180339);
                q += dot(q, q.yzx + 33.33);
                return frac((q.x + q.y) * q.z);
            }

            float4 frag(v2f_img i) : SV_Target
            {
                float gain = _EnvgI2.x;
                // a modern tube (64+ lp/mm) is about as sharp as the screen: only a slight softening (half a pixel)
                float2 px = _MainTex_TexelSize.xy * 0.5;
                float lum = signal(tex2D(_MainTex, i.uv).rgb) * 0.6;
                [unroll] for (int s = 0; s < 4; s++)
                {
                    float b = s * 1.5707963 + 0.7853982;
                    lum += signal(tex2D(_MainTex, i.uv + float2(cos(b), sin(b)) * px).rgb) * 0.1;
                }

                // automatic brightness control: the tube's power supply turns the gain down as the screen's overall
                // brightness rises, so a lit scene sits at a moderate level instead of washing out. It reacts with a
                // lag: the scene's mean (red channel) from the game's night-vision input, last frame, followed over half a
                // second (held as it was on a frame where that frame isn't available; never measured this frame).
                float mean = _EnvgI2Time.y > 0.5 ? clamp(tex2D(_EnvgAbc, float2(0.5, 0.5)).r, 1e-4, 10.0) : 0.0;   // red mean, followed slowly
                const float target = 0.18;   // the output level the control holds a bright scene to: a middle grey
                float g = gain / (1.0 + gain * mean / target);

                // each pixel saturates instead of clipping
                float sig = 1.0 - exp(-g * lum);

                // scintillation: photon shot noise, larger relative to the signal the darker it is, new 30 times a second
                float2 cell = floor(i.uv * _MainTex_TexelSize.zw / max(_EnvgI2.w, 1.0));
                float n = hash(cell, _EnvgI2Time.x) + hash(cell + 17.0, _EnvgI2Time.x) - 1.0;
                float noise = n * _EnvgI2.y * 0.12 * sqrt(sig + 0.02);

                // the tube's own faint background glow (equivalent background input) keeps black from being black
                float o = max(sig + noise, 0) + 0.006;
                // P45 white phosphor: one colour from the darks to the brightest (display 0.61 : 0.85 : 1, as the item's
                // 156, 217, 255), here in linear with blue the largest, so even a saturated pixel keeps the colour instead
                // of going white; times 0.62, the item's brightness (1.4 against the GPNVG-18's 2.27)
                const float3 p45 = float3(0.333, 0.694, 1.0) * 0.62;
                return float4(o * p45, 1);
            }
            ENDCG
        }
    }
}
