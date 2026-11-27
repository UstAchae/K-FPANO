// F-PANO style thermal outline overlay.
// Pass 0: edges of the heat picture (5x5 demean filter, warm side), at the thermal sensor's resolution.
// Pass 1: the edges drawn over the finished screen in amber, added like light from the goggle's OLED overlay.
Shader "Justachae/FPANO/Outline"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float4 _MainTex_TexelSize;
    float4 _EnvgParams;   // x threshold, y softness, z brightness, w glow
    float4 _EnvgCore;     // core colour
    float4 _EnvgHalo;     // halo colour

    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    v2f vert(appdata_img v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = v.texcoord;
        return o;
    }

    // for the game's own full-screen meshes (SSAAImpl): vertices already in clip space, uv flipped as the game needs
    v2f vertScreen(appdata_img v)
    {
        v2f o;
        o.pos = float4(v.vertex.xy, 0, 1);
        o.uv = v.texcoord;
        return o;
    }

    // The thermal picture may cover only part of the screen (in the eyepieces: the thermal rectangle and a margin):
    // its textures span the crop, xy its corner and zw its size in screen uv (all 0: the whole screen). Last frame's
    // crop for following things through time.
    float4 _EnvgCrop, _EnvgCropPrev;
    float4 cropOf(float4 c) { return c.z > 0.0 ? c : float4(0, 0, 1, 1); }
    float2 toScreen(float2 cuv) { float4 c = cropOf(_EnvgCrop); return c.xy + cuv * c.zw; }
    float2 toChain(float2 suv) { float4 c = cropOf(_EnvgCrop); return (suv - c.xy) / c.zw; }
    float2 toChainPrev(float2 suv) { float4 c = cropOf(_EnvgCropPrev); return (suv - c.xy) / c.zw; }
    // where the copies read their source: the main camera's frame through the crop (all 0), or the lens camera's own
    // picture, which is the crop already (0, 0, 1, 1)
    float4 _EnvgSrcCrop;
    float2 toSource(float2 cuv) { return _EnvgSrcCrop.z > 0.0 ? _EnvgSrcCrop.xy + cuv * _EnvgSrcCrop.zw : toScreen(cuv); }

    float4 fragCopy(v2f i) : SV_Target
    {
        return float4(tex2D(_MainTex, i.uv).rgb, 1);
    }

    float heat(float2 uv)
    {
        float3 c = tex2D(_MainTex, uv).rgb;
        return dot(saturate(c), float3(0.3333, 0.3334, 0.3333));
    }

    sampler2D _EnvgNormals;   // the thermal camera's GBuffer2: world normal * 0.5 + 0.5, black where nothing was drawn
    float4 _EnvgParams2;      // x: how much colder a surface open to the sky reads

    // The scene's heat as the long-wave sensor would see it: the game's thermal picture, and on top the part the game
    // leaves out, that a surface open to the sky radiates to it and reflects its cold, so it reads colder the more of
    // the sky it faces (floors and tops cooler than walls, walls than undersides). Sky-view factor (1 + n.y) / 2.
    float4 fragHeat(v2f i) : SV_Target
    {
        float h = heat(i.uv);
        float4 g = tex2D(_EnvgNormals, i.uv);
        if (dot(g.rgb, g.rgb) > 1e-4)
        {
            float3 n = normalize(g.rgb * 2.0 - 1.0);
            h -= _EnvgParams2.x * (1.0 + n.y) * 0.5;
        }
        h = saturate(h);
        return float4(h, h, h, 1);
    }

    // The edge filter of the fusion goggle patent US 7,307,793 (Insight Technology, later L-3): a 5x5 zero-sum
    // "demean" kernel on the thermal picture, the pixel +8, its 8 neighbours +1, the 16 around them -1; then a
    // threshold and clamp that, white hot, keep only the warm side of each edge. Sampled one sensor pixel apart.
    // Divided by 8 so a full white-to-black step on the warm side reads about 1.
    sampler2D _EnvgAgc;   // 1 x 1: the heat picture's mean and mean square, followed slowly

    float4 fragEdges(v2f i) : SV_Target
    {
        float2 t = _MainTex_TexelSize.xy;
        float centre = heat(i.uv) * 8.0;
        float inner = 0, outer = 0;
        [unroll] for (int y = -2; y <= 2; y++)
        {
            [unroll] for (int x = -2; x <= 2; x++)
            {
                if (x == 0 && y == 0)
                    continue;
                float v = heat(i.uv + t * float2(x, y));
                if (abs(x) <= 1 && abs(y) <= 1)
                    inner += v;
                else
                    outer += v;
            }
        }
        // the filter's response, signed (the threshold comes later, on the screen: fragComposite), in units of the
        // scene's own contrast: the thermal core's automatic gain control spreads whatever range of temperatures the
        // scene holds over its output, so the threshold is relative to the scene's spread of heat (its standard
        // deviation, followed over half a second), not to an absolute temperature step
        float r = (centre + inner - outer) / 8.0;
        float2 m = tex2D(_EnvgAgc, float2(0.5, 0.5)).rg;
        float spread = sqrt(max(m.y - m.x * m.x, 0.0));
        r /= max(spread, 0.01);
        return float4(r, r, r, 1);
    }

    // The patent's threshold and clamp, done per screen pixel on the response sampled smoothly between detector
    // pixels. The line as the overlay display draws it: as wide as the strongest response within one detector pixel around
    // (the display's lines are a few pixels wide and glow), so the beads a slanted edge makes on the detector grid
    // join into one line; its brightness follows the response's strength gradually, so a weak edge dims rather than
    // blinking on and off.
    float4 _EnvgScreenTexel;   // xy: one screen pixel in uv; z: the line's half width in screen pixels

    // the line: each nearby centre-line pixel is a short segment through its sub-pixel crest, across its normal; the
    // pen covers what lies within the half width of those segments (anti-aliased over a pixel). Lines follow their
    // real course between the pixels instead of the pixel grid's steps.
    float edgeAt(float2 uv)
    {
        float2 size = _MainTex_TexelSize.zw;
        float2 texel = _MainTex_TexelSize.xy;
        float2 px = _EnvgScreenTexel.xy;
        float r = max(_EnvgScreenTexel.z, 0.5);
        float texelPx = texel.x / px.x;          // one centre-line pixel in screen pixels
        float2 base = floor(uv * size - 0.5);
        float best = 0;
        [unroll] for (int y = -1; y <= 2; y++)
        {
            [unroll] for (int x = -1; x <= 2; x++)
            {
                float2 c = (base + float2(x, y) + 0.5) * texel;
                float4 R = tex2Dlod(_MainTex, float4(c, 0, 0));
                if (R.r <= 0.0)
                    continue;
                float2 n = normalize(R.zw + 1e-6);
                float2 p = c + n * R.y * texel;                 // the crest
                float2 d = (uv - p) / px;                       // to it, in screen pixels
                float across = abs(dot(d, n));
                float along = abs(dot(d, float2(-n.y, n.x)));
                float cover = saturate(r + 0.5 - across) * saturate(texelPx * 1.1 + 0.5 - along);
                best = max(best, R.r * cover);
            }
        }
        return best;
    }

    float4 _EnvgBlur;     // xy: one step, in uv

    // 9-tap gaussian along one axis (sigma about 2 steps): the sensor's own softness, so texture and noise
    // in the heat picture don't each become a line
    float4 fragBlur(v2f i) : SV_Target
    {
        const float w[5] = { 0.2270, 0.1945, 0.1216, 0.0540, 0.0162 };
        float4 s = tex2D(_MainTex, i.uv) * w[0];
        [unroll] for (int n = 1; n < 5; n++)
        {
            s += tex2D(_MainTex, i.uv + _EnvgBlur.xy * n) * w[n];
            s += tex2D(_MainTex, i.uv - _EnvgBlur.xy * n) * w[n];
        }
        return s;
    }

    sampler2D _EnvgDensity;   // how much of the area around is lines (the line presence blurred wide)
    sampler2D _EnvgGlow;      // the line presence blurred a little
    float4 _EnvgDeclutter;    // x: strength, y: the line density above which lines are dimmed

    float4 _EnvgI2Dim;        // x: how far the tubes' gain is turned down (0 full gain, 1 dark)
    sampler2D _EnvgScene;     // the tube's picture the game's SSAA draws, read through the same full-screen mesh (so at i.uv)
    float4 _EnvgWashout;      // x: strength (0 off)
    float4 _EnvgParallax;     // x: the thermal lens above the eye's tube (m), y: uv per unit of vertical slope (1 / (2 tan(half
                              // vertical field))), z: the lens to the right of it (m, negative left), w: the same per horizontal slope
    float4 _EnvgField;        // xy: the thermal picture's half size (uv), z: its soft edge (uv of height), w: 1 on

    sampler2D _EnvgDepth;     // linear depth (m) of the scene, as the thermal picture

    // Where a point of the thermal picture was last frame, for following it in time. The still world (walls, the
    // ground: everything but people) moves on the screen only as the camera moves, and that is known exactly: this
    // point's depth, the camera now and the camera last frame, as they were when the scene was copied (after anything
    // that moves the camera before it renders). The game's own motion vectors don't follow a camera moved that way, and
    // the history fetched by them landed beside each line (lines dimmed while turning, old lines left behind). People
    // move on their own: for them the game's per-object vectors. Returns the thermal picture's uv of last frame.
    float4x4 _EnvgRpCamToWorld, _EnvgRpPrevWorldToCam;
    float4 _EnvgRpTan;   // xy: tan of half the field of view now (x, y), zw: last frame's
    float4 _EnvgRpOn;    // x: 1 when the camera's own following is set (the preview: the game's vectors only)
    sampler2D _EnvgRpMotion, _EnvgRpBody;
    // (predicted: this point's distance from last frame's camera, for the still world; 0 for people, who move)
    float2 previousUvAt(float2 cuv, out float predicted)
    {
        predicted = 0.0;
        float2 su = toScreen(cuv);
        float2 game = su - tex2Dlod(_EnvgRpMotion, float4(cuv, 0, 0)).xy;
        if (_EnvgRpOn.x < 0.5 || tex2Dlod(_EnvgRpBody, float4(cuv, 0, 0)).r > 0.25)
            return toChainPrev(game);
        float d = tex2Dlod(_EnvgDepth, float4(cuv, 0, 0)).r;
        float3 view = float3((su.x * 2.0 - 1.0) * _EnvgRpTan.x, (su.y * 2.0 - 1.0) * _EnvgRpTan.y, -1.0) * d;
        float3 world = mul(_EnvgRpCamToWorld, float4(view, 1.0)).xyz;
        float3 before = mul(_EnvgRpPrevWorldToCam, float4(world, 1.0)).xyz;
        if (before.z > -1e-4)
            return float2(-1.0, -1.0);   // behind last frame's camera: no history
        predicted = -before.z;
        float2 then = float2(before.x / (-before.z) / _EnvgRpTan.z, before.y / (-before.z) / _EnvgRpTan.w) * 0.5 + 0.5;
        return toChainPrev(then);
    }
    float2 previousUv(float2 cuv)
    {
        float predicted;
        return previousUvAt(cuv, predicted);
    }

    // The heat's history (pass 36) and how many frames it holds (pass 38). Where last frame saw this very surface (the
    // still world followed exactly by the camera, last frame's depth around that place holding this point's distance),
    // the history is this point's own and the frames are averaged as they come (each new one 1 / (n + 1)), up to
    // HeatFrames: a still view settles to the mean of many frames, as the game's TAA settles its picture.
    sampler2D _EnvgHeatHistory, _EnvgHeatCount, _EnvgDepthPrev;
    float4 _EnvgHistSize;          // xy: one chain pixel (uv), zw: the chain's size (the history, its count and last depth)
    float4 _EnvgHeatTemporal;      // x: the history's share held to this frame's neighbourhood; y: 1 when last depth is there
    #define HeatFrames 32.0
    #define SpreadHeld 6.0
    // nearer than this, a thing is taken to go with the camera (the gun, the hands): not the still world
    #define NearHeld 1.2
    bool nearHeld(float2 cuv)
    {
        float d = tex2Dlod(_EnvgDepth, float4(cuv, 0, 0)).r;
        return d > 0.0 && d < NearHeld;
    }
    // what was at a point last frame was the gun or the hands: a place the gun has just left (its history is the gun's)
    bool nearThen(float2 previous)
    {
        float d = tex2Dlod(_EnvgDepthPrev, float4(previous, 0, 0)).r;
        return _EnvgHeatTemporal.y > 0.0 && d > 0.0 && d < NearHeld;
    }
    bool sameSurfaceThen(float2 previous, float predicted)
    {
        if (predicted <= 0.0 || _EnvgHeatTemporal.y <= 0.0)
            return false;
        float dlo = 1e6, dhi = 0.0;
        [unroll] for (int y = -1; y <= 1; y++)
        {
            [unroll] for (int x = -1; x <= 1; x++)
            {
                float dp = tex2Dlod(_EnvgDepthPrev, float4(previous + float2(x, y) * _EnvgHistSize.xy, 0, 0)).r;
                dp = dp <= 0.0 ? 1e5 : dp;
                dlo = min(dlo, dp);
                dhi = max(dhi, dp);
            }
        }
        return predicted >= dlo * 0.97 && predicted <= dhi * 1.03;
    }
    // last frame's heat fetched sharply (Catmull-Rom, five taps): a plain bilinear fetch every frame, the view moving
    // by a fraction of a pixel, would soften the picture more with each frame it holds
    float4 sharpAt(sampler2D tex, float2 uv)
    {
        float2 size = _EnvgHistSize.zw;
        float2 p = uv * size;
        float2 p1 = floor(p - 0.5) + 0.5;
        float2 f = p - p1;
        float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
        float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
        float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
        float2 w3 = f * f * (-0.5 + 0.5 * f);
        float2 w12 = w1 + w2;
        float2 p12 = (p1 + w2 / w12) / size;
        float2 p0 = (p1 - 1.0) / size;
        float2 p3 = (p1 + 2.0) / size;
        float4 r = tex2Dlod(tex, float4(p12.x, p0.y, 0, 0)) * (w12.x * w0.y)
                 + tex2Dlod(tex, float4(p0.x, p12.y, 0, 0)) * (w0.x * w12.y)
                 + tex2Dlod(tex, float4(p12.x, p12.y, 0, 0)) * (w12.x * w12.y)
                 + tex2Dlod(tex, float4(p3.x, p12.y, 0, 0)) * (w3.x * w12.y)
                 + tex2Dlod(tex, float4(p12.x, p3.y, 0, 0)) * (w12.x * w3.y);
        return r / (w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y);
    }
    float4 heatHistoryAt(float2 uv) { return sharpAt(_EnvgHeatHistory, uv); }

    // A real change (a body's heat, a door opened on a warm room) is the history far outside this frame's own spread
    // around the point (its mean and spread over the 5 x 5, as TAA's variance test); then the frames start anew from this
    // one. Within it, the history holds and the frames keep being averaged. (_MainTex: this frame's heat.)
    float4 spreadAround(float2 uv)
    {
        float2 t = _MainTex_TexelSize.xy;
        float4 m1 = 0, m2 = 0;
        [unroll] for (int y = -2; y <= 2; y++)
        {
            [unroll] for (int x = -2; x <= 2; x++)
            {
                float4 v = tex2Dlod(_MainTex, float4(uv + float2(x, y) * t, 0, 0));
                m1 += v;
                m2 += v * v;
            }
        }
        m1 /= 25.0;
        return sqrt(max(m2 / 25.0 - m1 * m1, 0.0));
    }
    bool historyHolds(float2 uv, float2 previous)
    {
        float2 t = _MainTex_TexelSize.xy;
        float m1 = 0, m2 = 0;
        [unroll] for (int y = -2; y <= 2; y++)
        {
            [unroll] for (int x = -2; x <= 2; x++)
            {
                float v = tex2Dlod(_MainTex, float4(uv + float2(x, y) * t, 0, 0)).r;
                m1 += v;
                m2 += v * v;
            }
        }
        m1 /= 25.0;
        float sd = sqrt(max(m2 / 25.0 - m1 * m1, 0.0));
        return abs(heatHistoryAt(previous).r - m1) <= SpreadHeld * sd + 1e-5;
    }

    // the thermal lens sits above the tubes, its axis aligned with theirs at infinity: something near is seen by it a
    // little lower than by the tubes, by the lens's height over the distance. What shows at a point of the eyepiece is
    // what the thermal lens sees there: the scene that far above it.
    float2 thermalUv(float2 uv)
    {
        // (uv on the screen; the result in the thermal textures' own uv)
        if (_EnvgParallax.x <= 0.0 && _EnvgParallax.z == 0.0)
            return toChain(uv);
        // seen from the lens, a thing at distance d sits off by the lens's offset over d against the tube's view
        float2 slope = float2(_EnvgParallax.z * _EnvgParallax.w, _EnvgParallax.x * _EnvgParallax.y);
        float d = tex2Dlod(_EnvgDepth, float4(toChain(uv), 0, 0)).r;
        float2 s = slope / max(d, 0.25);
        d = tex2Dlod(_EnvgDepth, float4(toChain(uv + s), 0, 0)).r;
        return toChain(uv + slope / max(d, 0.25));
    }

    // the thermal picture is a rectangle (4:3) within the tubes' round field: outside it, the tubes' picture alone
    // when another mod's eyepieces show the picture (EyepieceFusion): xy the tubes' axis on the screen (uv), z how far
    // the device is down (the picture fades in as it settles), w 1; otherwise all 0, the field at the screen's centre
    float4 _EnvgFieldAt;
    float thermalField(float2 uv)
    {
        float fade = _EnvgFieldAt.w > 0.5 ? _EnvgFieldAt.z : 1.0;
        if (_EnvgField.w <= 0.0)
            return fade;
        float2 centre = _EnvgFieldAt.w > 0.5 ? _EnvgFieldAt.xy : float2(0.5, 0.5);
        float2 d = abs(uv - centre) - _EnvgField.xy;
        float soft = max(_EnvgField.z, 1e-4);
        return fade * saturate(1.0 - max(d.x * (_EnvgField.y / max(_EnvgField.x, 1e-4)), d.y) / soft);
    }

    // The thermal lens sits about 13 cm in front of the eye: nothing nearer can be in its view (the goggle's own tubes
    // and eyecups, which the main camera may see for a frame). Those pixels, and a few around them (their edge),
    // show no thermal picture at all. tuv: the thermal textures' uv.
    // a magnified scope's lens (its picture, the reticle's area): no thermal picture there; screen uv
    sampler2D _EnvgLensMask;
    float4 _EnvgLensMask_TexelSize;
    float4 _EnvgLensMaskOn;   // x: 1 when drawn this frame, yz: this frame's sub-pixel offset (uv)
    float scopeLens(float2 uv)
    {
        if (_EnvgLensMaskOn.x < 0.5)
            return 0.0;
        // (w 1: drawn by the lens camera, in the thermal picture's own uv)
        float2 at = _EnvgLensMaskOn.w > 0.5 ? toChain(uv) : uv;
        at += _EnvgLensMaskOn.yz;
        if (_EnvgLensMaskOn.w < 0.5)
            return saturate(tex2D(_EnvgLensMask, at).r);
        // the sights' hole a mask pixel wider all round: its edge moves by up to one with the lens camera's sub-pixel
        // offset and is softened when read, which left a seam of thermal picture around it (user: "扣掉的区域周围漏缝")
        float2 t = _EnvgLensMask_TexelSize.xy;
        float m = tex2D(_EnvgLensMask, at).r;
        m = max(m, max(tex2D(_EnvgLensMask, at + float2(t.x, 0)).r, tex2D(_EnvgLensMask, at - float2(t.x, 0)).r));
        m = max(m, max(tex2D(_EnvgLensMask, at + float2(0, t.y)).r, tex2D(_EnvgLensMask, at - float2(0, t.y)).r));
        m = max(m, max(tex2D(_EnvgLensMask, at + t).r, tex2D(_EnvgLensMask, at - t).r));
        m = max(m, max(tex2D(_EnvgLensMask, at + float2(t.x, -t.y)).r, tex2D(_EnvgLensMask, at - float2(t.x, -t.y)).r));
        return saturate(m);
    }

    float beyondLens(float2 tuv)
    {
        const float lensReach = 0.04;   // the goggle's own eyecups at the eye (m); a scope's housing, farther, keeps it
        float2 t = _EnvgScreenTexel.xy * 3.0;
        float d = tex2Dlod(_EnvgDepth, float4(tuv, 0, 0)).r;
        d = min(d, tex2Dlod(_EnvgDepth, float4(tuv + float2(t.x, 0), 0, 0)).r);
        d = min(d, tex2Dlod(_EnvgDepth, float4(tuv - float2(t.x, 0), 0, 0)).r);
        d = min(d, tex2Dlod(_EnvgDepth, float4(tuv + float2(0, t.y), 0, 0)).r);
        d = min(d, tex2Dlod(_EnvgDepth, float4(tuv - float2(0, t.y), 0, 0)).r);
        // (no surface at all reads 0: that is the sky, far, not near)
        return d <= 0.0 ? 1.0 : smoothstep(lensReach, lensReach + 0.03, d);
    }


    // the overlay's light adds to the tube's: where the tube is already near white, the copper hardly changes what the
    // eye sees, and the lines sink into the bright background; on a dark background they stand out in full
    float washout(float2 uv)
    {
        if (_EnvgWashout.x <= 0.0)
            return 1.0;
        // as bright as the eye sees it (the frame here is still linear: a spot that looks bright is about half in linear)
        // (the SSAA's picture is the final, display-encoded frame: its values are already as bright as they look)
        float l = saturate(dot(tex2D(_EnvgScene, uv).rgb, float3(0.2126, 0.7152, 0.0722)) * (1.0 - _EnvgI2Dim.x));
        // only where the tube is close to white does the copper sink in (a lit wall, a torch's spot); a pale sky or a
        // lit middle grey still shows the lines
        float keep = 1.0 - smoothstep(0.3, 0.9, l);
        return lerp(1.0, keep, _EnvgWashout.x);
    }

    sampler2D _EnvgHeatView;  // the long-wave picture (C/100)
    float4 _EnvgThermo;       // x: the air (C)
    float4 _EnvgLook;         // x: base brightness, y: extra brightness of hot things, z: degrees above the air for all of it

    // how bright a line is by the heat of what it outlines (the warmest within a couple of sensor pixels): things at
    // or below the air at the base brightness, hot ones (a body, an engine) up to base + extra
    float heatBrightness(float2 uv)
    {
        float2 t = _EnvgScreenTexel.xy * 3.0;
        float h = tex2D(_EnvgHeatView, uv).r;
        h = max(h, tex2D(_EnvgHeatView, uv + float2(t.x, 0)).r);
        h = max(h, tex2D(_EnvgHeatView, uv - float2(t.x, 0)).r);
        h = max(h, tex2D(_EnvgHeatView, uv + float2(0, t.y)).r);
        h = max(h, tex2D(_EnvgHeatView, uv - float2(0, t.y)).r);
        float above = h * 100.0 - _EnvgThermo.x;
        return _EnvgLook.x + _EnvgLook.y * saturate((above - 2.0) / max(_EnvgLook.z, 0.1));
    }

    float4 fragComposite(v2f i) : SV_Target
    {
        float field = thermalField(i.uv);
        if (field <= 0.0)
            return float4(0, 0, 0, 1);
        float2 tuv = thermalUv(i.uv);
        field *= beyondLens(tuv) * (1.0 - scopeLens(i.uv));
        if (field <= 0.0)
            return float4(0, 0, 0, 1);
        float core = edgeAt(tuv);
        // the glow around each line, as the overlay's bright lines bloom in the eyepiece: the lines blurred (a few
        // pixels), not a ring of shifted copies (which drew a crosshatch into every line)
        float halo = saturate(tex2D(_EnvgGlow, tuv).r * 5.0);
        // declutter: where lines crowd (foliage, every leaf an edge) each line is dimmed in proportion, so a tree is
        // a textured mass rather than a solid glare; a line on its own keeps its full strength
        float density = tex2D(_EnvgDensity, tuv).r;
        float keep = lerp(1.0, saturate(_EnvgDeclutter.y / max(density, 1e-3)), _EnvgDeclutter.x);
        float3 col = (_EnvgCore.rgb * core + _EnvgHalo.rgb * halo * _EnvgParams.w) * keep * _EnvgParams.z * heatBrightness(tuv);
        // the overlay display saturates softly: brighter lines grow whiter at the core but keep their copper colour
        // instead of clipping to yellow
        col = 1.0 - exp(-col);
        return float4(col * washout(i.uv) * field, 1);
    }

    // ---- the F-PANO's symbology (passes 42, 43): stroke glyphs of seven segments, 0.5 wide and 1 high from their lower
    // left corner (bits a 1 top, b 2 top right, c 4 bottom right, d 8 bottom, e 16 bottom left, f 32 top left, g 64 middle)
    static const float Segments[10] = { 63, 6, 91, 79, 102, 109, 125, 7, 127, 111 };
    float segDist(float2 p, float2 a, float2 b)
    {
        float2 pa = p - a, ba = b - a;
        return length(pa - ba * saturate(dot(pa, ba) / dot(ba, ba)));
    }
    float segLit(float m, float bit) { return fmod(floor(m / bit), 2.0); }
    // the distance to the nearest lit stroke
    float glyphDist(float2 p, float m)
    {
        float d = 1e3;
        float2 tl = float2(0, 1), tr = float2(0.5, 1), ml = float2(0, 0.5), mr = float2(0.5, 0.5), bl = float2(0, 0), br = float2(0.5, 0);
        if (segLit(m, 1) > 0.5) d = min(d, segDist(p, tl, tr));
        if (segLit(m, 2) > 0.5) d = min(d, segDist(p, tr, mr));
        if (segLit(m, 4) > 0.5) d = min(d, segDist(p, mr, br));
        if (segLit(m, 8) > 0.5) d = min(d, segDist(p, bl, br));
        if (segLit(m, 16) > 0.5) d = min(d, segDist(p, ml, bl));
        if (segLit(m, 32) > 0.5) d = min(d, segDist(p, tl, ml));
        if (segLit(m, 64) > 0.5) d = min(d, segDist(p, ml, mr));
        return d;
    }
    // lit as the ENVG-B's symbology: a core, and a glow falling off over glowPx outside it, gone by 4 x that
    float2 hudLight(float sd, float glowPx)
    {
        float core = saturate(sd + 0.5);
        float beyond = saturate(-sd / (4.0 * glowPx));
        return float2(core, exp(-max(-sd, 0.0) / glowPx) * (1.0 - beyond) * (1.0 - beyond) * (1.0 - core) * 0.55);
    }
    float boxSd(float2 p, float2 c, float2 h)
    {
        float2 q = abs(p - c) - h;
        return -(length(max(q, 0.0)) + min(max(q.x, q.y), 0.0));
    }
    float4 _FpanoHud;    // xy: the ring's centre (screen uv), z: its radius (uv of height), w: width / height
    float4 _FpanoHudB;   // x: the heading shown (whole degrees), z: brightness (0 off), w: the picture's height in pixels
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragEdges
            ENDCG
        }

        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragComposite
            ENDCG
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBlur
            ENDCG
        }

        // 3: the edges added to the screen through the game's full-screen mesh
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vertScreen
            #pragma fragment fragComposite
            ENDCG
        }

        // 4: the thermal picture itself over the screen, the same way
        Pass
        {
            CGPROGRAM
            #pragma vertex vertScreen
            #pragma fragment fragCopy
            ENDCG
        }

        // 5: the scene's heat: the game's thermal picture with the surfaces' sky-facing cooling
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragHeat
            ENDCG
        }

        // 6: each detector pixel integrates the picture over its own area: the mean of the N x N rendered pixels it covers
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragArea
            float4 _EnvgArea;   // x: N
            float4 fragArea(v2f i) : SV_Target
            {
                int n = (int)_EnvgArea.x;
                float2 t = _MainTex_TexelSize.xy;
                float s = 0;
                [loop] for (int y = 0; y < n; y++)
                {
                    [loop] for (int x = 0; x < n; x++)
                        s += tex2D(_MainTex, i.uv + (float2(x, y) + 0.5 - n * 0.5) * t).r;
                }
                s /= (float)(n * n);
                return float4(s, s, s, 1);
            }
            ENDCG
        }

        // 7: the scene's mean brightness as the tube's photocathode sees it (red and near infrared weighted), averaged
        // over each target pixel's footprint in N x N taps: reduces the camera's frame toward one pixel for the tube's
        // brightness control
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragSignal
            float4 _EnvgReduce;   // x: N, yz: target pixel size in uv, w: 1 to weight colour as the tube does
            float4 fragSignal(v2f i) : SV_Target
            {
                int n = (int)_EnvgReduce.x;
                float s = 0;
                [loop] for (int y = 0; y < n; y++)
                {
                    [loop] for (int x = 0; x < n; x++)
                    {
                        float3 c = max(tex2D(_MainTex, i.uv + ((float2(x, y) + 0.5) / n - 0.5) * _EnvgReduce.yz).rgb, 0);
                        // first step: the log of the signal (a geometric mean); later steps average logs already
                        s += _EnvgReduce.w > 0.5 ? log(dot(c, float3(0.45, 0.45, 0.10)) + 1e-4) : c.r;
                    }
                }
                s /= (float)(n * n);
                return float4(s, s, s, 1);
            }
            ENDCG
        }

        // 8: the microbolometer's response time: each detector pixel follows the scene with a first-order lag
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragLag
            sampler2D _EnvgHistory;
            float4 _EnvgLag;   // x: share of the new frame this step
            float4 fragLag(v2f i) : SV_Target
            {
                float now = tex2D(_MainTex, i.uv).r;
                float before = tex2D(_EnvgHistory, i.uv).r;
                float h = lerp(before, now, _EnvgLag.x);
                return float4(h, h, h, 1);
            }
            ENDCG
        }

        // 9: the heat picture's mean and mean square over each target pixel's footprint (N x N taps): reduces the
        // thermal picture toward one pixel for the automatic gain control
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragMoments
            float4 _EnvgReduce;   // x: N, yz: target pixel size in uv, w: 1 on the first step (from the heat itself)
            float4 fragMoments(v2f i) : SV_Target
            {
                int n = (int)_EnvgReduce.x;
                float3 s3 = 0;
                [loop] for (int y = 0; y < n; y++)
                {
                    [loop] for (int x = 0; x < n; x++)
                    {
                        float2 uv = i.uv + ((float2(x, y) + 0.5) / n - 0.5) * _EnvgReduce.yz;
                        float4 c = tex2D(_MainTex, uv);
                        if (_EnvgReduce.w > 2.5)
                        {
                            // the long-wave picture's first step: surfaces only (the sky stays out of the statistics)
                            float4 g = tex2D(_EnvgNormals, uv);
                            float w = dot(g.rgb, g.rgb) > 1e-4 ? 1.0 : 0.0;
                            s3 += float3(c.r, c.r * c.r, 1.0) * w;
                        }
                        else if (_EnvgReduce.w > 1.5)
                            s3 += c.rgb;
                        else
                            s3.xy += _EnvgReduce.w > 0.5 ? float2(c.r, c.r * c.r) : c.rg;
                    }
                }
                s3 /= (float)(n * n);
                // the last step of the surfaces-only path: weighted mean and mean square
                if (_EnvgReduce.w > 1.5 && _EnvgReduce.w < 2.5)
                    return float4(s3.xy / max(s3.z, 1e-6), 0, 1);
                return float4(s3, 1);
            }
            ENDCG
        }

        // 10: the same first-order lag as pass 8, for two channels
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragLag2
            sampler2D _EnvgHistory;
            float4 _EnvgLag;
            float4 fragLag2(v2f i) : SV_Target
            {
                float2 now = tex2D(_MainTex, i.uv).rg;
                float2 before = tex2D(_EnvgHistory, i.uv).rg;
                return float4(lerp(before, now, _EnvgLag.x), 0, 1);
            }
            ENDCG
        }

        // 11: the edge filter as a smooth centre-surround (difference of gaussians: the lens-blurred heat minus the
        // same at twice the radius), the smooth form of the patent's demean kernel (which names gaussian and laplacian
        // kernels as alternatives). Its response hardly depends on where an edge falls within a pixel, so a slanted
        // edge gives an even line instead of beads. Warm side positive; in units of the scene's spread (the AGC).
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDog
            sampler2D _EnvgSurround;
            sampler2D _EnvgGeometry;   // the supersampled picture: g holds the geometric edges
            float4 _EnvgMix;           // x: heat edges' strength, y: geometric edges' strength, z: local contrast, w: shapes need a temperature difference
            sampler2D _EnvgLocal;      // the heat's mean and mean square around each point
            float4 _EnvgGainLimit;     // x: the smallest spread of heat the gain control stretches to full scale (C/100):
                                       // a real core's gain has a ceiling, so a flat night scene stays flat
            sampler2D _EnvgAlbedoSoft; // the colour averaged over a few pixels (foliage is green)
            sampler2D _EnvgNormalsSoft; // normals and inverse depth averaged over several pixels
            float4 _EnvgFoliageStep;    // xy: the relative step in averaged distance that outlines a mass of foliage
            float4 fragDog(v2f i) : SV_Target
            {
                float r = (tex2D(_MainTex, i.uv).r - tex2D(_EnvgSurround, i.uv).r) * 3.0;
                // inside foliage the leaves' small differences are texture, not outlines: weaker there (a crown's own
                // edge against the sky or the ground is a step in depth and stays a full line through the shapes)
                float3 soft = tex2D(_EnvgAlbedoSoft, i.uv).rgb;
                float leafy = saturate((soft.g - max(soft.r * 0.95, soft.b * 1.2)) / max(soft.g, 0.02) * 6.0);
                r *= 1.0 - 0.6 * leafy;
                float2 m = tex2D(_EnvgAgc, float2(0.5, 0.5)).rg;
                float spread = sqrt(max(m.y - m.x * m.x, 0.0));
                // local detail enhancement, as thermal cores do it: the response in units of the spread of heat
                // around this point (about twelve pixels), so the ground's small differences show next to the trees'
                // large ones; never below 30 percent of the whole picture's spread, so flat areas' noise isn't blown up
                float2 lm = tex2D(_EnvgLocal, i.uv).rg;
                float local = sqrt(max(lm.y - lm.x * lm.x, 0.0));
                float scale = lerp(spread, max(local, spread * 0.5), _EnvgMix.z);
                r /= max(scale, _EnvgGainLimit.x);
                // one set of lines: the stronger of the heat edge and the geometric edge (a full geometric edge reads
                // 1.5, past the threshold and its softness at the defaults)
                float geo = tex2D(_EnvgGeometry, i.uv).g * 1.5;
                // a shape is seen in the long-wave band only where its two sides differ in temperature: leaf against
                // leaf (the same temperature) draws nothing, a crown against the cold sky or a warm body against
                // anything draws its full outline. The difference across: the range of the heat within about two pixels,
                // in the same units as the heat edges.
                float2 t = _MainTex_TexelSize.xy;
                float lo = 1e6, hi = -1e6;
                [unroll] for (int k = 0; k < 8; k++)
                {
                    float a = k * 0.7853982;
                    float h = tex2D(_EnvgGeometry, i.uv + float2(cos(a), sin(a)) * t * 2.0).r;
                    lo = min(lo, h);
                    hi = max(hi, h);
                }
                float across = (hi - lo) / max(scale, _EnvgGainLimit.x);
                // inside a crown (b: foliage) small differences are texture, not outlines: only a big one (the crown
                // against the sky, a trunk, a body in the branches) makes a line there
                float foliage = tex2D(_EnvgGeometry, i.uv).b;
                r *= lerp(1.0, 0.3, foliage);
                // ...or a real step in depth: a tree, a tuft of grass standing well in front of what is behind it is
                // a thing of its own (its outline shows from the different distance and air between), unlike one leaf
                // card a little in front of the next
                // (the crown as a mass: its distance averaged over a few pixels, so the gaps between its leaves don't
                // count; a tree before the forest behind it still stands well in front)
                float iMin = 1e6, iMax = 0;
                [unroll] for (int kd = 0; kd < 8; kd++)
                {
                    float ad = kd * 0.7853982;
                    float id = tex2D(_EnvgNormalsSoft, i.uv + float2(cos(ad), sin(ad)) * t * 3.0).a;
                    iMin = min(iMin, id);
                    iMax = max(iMax, id);
                }
                float bigStep = smoothstep(_EnvgFoliageStep.x, _EnvgFoliageStep.y, (iMax - iMin) / max(iMax, 1e-6));
                float gate = smoothstep(lerp(0.25, 1.5, foliage), lerp(1.0, 3.0, foliage), across);
                geo *= lerp(1.0, max(gate, bigStep * foliage), _EnvgMix.w);
                float e = max(r * _EnvgMix.x, geo * _EnvgMix.y);
                return float4(e, e, e, 1);
            }
            ENDCG
        }

        // 12: the thermal camera's linear eye depth, copied while it renders (for reprojecting its history)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDepth
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float4 _EnvgJitter;   // xy: where this frame's sub-pixel offset put the picture (uv)
            float4 _EnvgFootprint; // xy: one sensor pixel (uv): its area is averaged
            float4 fragDepth(v2f i) : SV_Target
            {
                // the mean over the sensor pixel's 4 x 4 screen samples, and how rough the depth is within it: what is
                // left after the best plane through the samples (blades of grass, needles: many little surfaces at
                // different distances even where each is smaller than a sensor pixel; a wall or the ground leave none)
                float sd = 0, sx = 0, sy = 0;
                float s[16];
                [unroll] for (int y = 0; y < 4; y++)
                    [unroll] for (int x = 0; x < 4; x++)
                    {
                        float v = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, toSource(i.uv) + _EnvgJitter.xy + (float2(x, y) - 1.5) * 0.25 * _EnvgFootprint.xy));
                        s[y * 4 + x] = v;
                        sd += v;
                        sx += v * (x - 1.5);
                        sy += v * (y - 1.5);
                    }
                float d = sd / 16.0;
                float bx = sx / 20.0, by = sy / 20.0;   // sum of (x-1.5)^2 over the grid = 20
                float r = 0;
                [unroll] for (int k = 0; k < 16; k++)
                {
                    float e = s[k] - (d + bx * ((k % 4) - 1.5) + by * ((k / 4) - 1.5));
                    r += e * e;
                }
                float rough = sqrt(r / 16.0) / max(d, 0.05);
                return float4(d, rough, 0, 1);
            }
            ENDCG
        }

        // 13: temporal supersampling. Each frame the thermal camera renders with a different sub-pixel offset; this
        // frame's heat is blended into the history, found where it was last frame (reprojected through its depth), so
        // over a few frames each pixel is the mean over its own area, as the detector integrates: a feature thinner
        // than a pixel adds its share instead of rasterising into dashes. While the view moves the history is held to
        // the range of this frame's neighbourhood, so nothing trails behind.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragResolve
            sampler2D _EnvgHistory;
            float4x4 _EnvgCamToWorld;
            float4x4 _EnvgPrevWorldToCam;
            float4 _EnvgTan;   // xy: tan of half the field of view now (x, y); zw: last frame
            float4 _EnvgTss;   // x: share of this frame, y: 1 to start afresh
            float4 _EnvgHistory_TexelSize;

            // the history read at a point between its pixels with a Catmull-Rom filter (9 taps): unlike a bilinear
            // read it hardly blurs, so reading it again every frame while the view moves keeps edges sharp
            float2 historyAt(float2 uv)
            {
                float2 size = _EnvgHistory_TexelSize.zw, texel = _EnvgHistory_TexelSize.xy;
                float2 p = uv * size;
                float2 c = floor(p - 0.5) + 0.5;
                float2 f = p - c;
                float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
                float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
                float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
                float2 w3 = f * f * (-0.5 + 0.5 * f);
                float2 w12 = w1 + w2;
                float2 o12 = w2 / w12;
                float2 t0 = (c - 1.0) * texel, t3 = (c + 2.0) * texel, t12 = (c + o12) * texel;
                float2 sum = 0;
                sum += tex2D(_EnvgHistory, float2(t0.x, t0.y)).rg * w0.x * w0.y;
                sum += tex2D(_EnvgHistory, float2(t12.x, t0.y)).rg * w12.x * w0.y;
                sum += tex2D(_EnvgHistory, float2(t3.x, t0.y)).rg * w3.x * w0.y;
                sum += tex2D(_EnvgHistory, float2(t0.x, t12.y)).rg * w0.x * w12.y;
                sum += tex2D(_EnvgHistory, float2(t12.x, t12.y)).rg * w12.x * w12.y;
                sum += tex2D(_EnvgHistory, float2(t3.x, t12.y)).rg * w3.x * w12.y;
                sum += tex2D(_EnvgHistory, float2(t0.x, t3.y)).rg * w0.x * w3.y;
                sum += tex2D(_EnvgHistory, float2(t12.x, t3.y)).rg * w12.x * w3.y;
                sum += tex2D(_EnvgHistory, float2(t3.x, t3.y)).rg * w3.x * w3.y;
                return sum;
            }

            float4 fragResolve(v2f i) : SV_Target
            {
                // r: heat, g: geometric edge; both supersampled the same way
                float2 now = tex2D(_MainTex, i.uv).rg;
                if (_EnvgTss.y > 0.5)
                    return float4(now, 0, 1);
                float2 t = _MainTex_TexelSize.xy;
                float2 lo = now, hi = now;
                [unroll] for (int y = -1; y <= 1; y++)
                {
                    [unroll] for (int x = -1; x <= 1; x++)
                    {
                        float2 v = tex2D(_MainTex, i.uv + float2(x, y) * t).rg;
                        lo = min(lo, v);
                        hi = max(hi, v);
                    }
                }
                // geometric edges (g): the history may keep anything from nothing up to the strongest edge within two
                // pixels, so a thin line dropped by this frame's pixel grid is carried on by the history instead of
                // being clamped away (static geometry reprojects exactly, so this doesn't trail)
                // and the heat (r) within the range of the same two pixels around: a thin warm or cold line dropped by
                // this frame's grid is carried on the same way
                lo.y = 0;
                [unroll] for (int y2 = -2; y2 <= 2; y2++)
                {
                    [unroll] for (int x2 = -2; x2 <= 2; x2++)
                    {
                        float2 v2 = tex2D(_MainTex, i.uv + float2(x2, y2) * t).rg;
                        lo.x = min(lo.x, v2.x);
                        hi = max(hi, v2);
                    }
                }
                float d = tex2D(_EnvgDepth, i.uv).r;
                // the player's own arms, gun and body move with the view, not with the world: the history found by
                // reprojecting the world would be a ghost of them, so near the camera this frame stands alone
                if (d < 1.2)
                    return float4(now, 0, 1);
                float3 view = float3((i.uv.x * 2.0 - 1.0) * _EnvgTan.x, (i.uv.y * 2.0 - 1.0) * _EnvgTan.y, -1.0) * d;
                float3 world = mul(_EnvgCamToWorld, float4(view, 1.0)).xyz;
                float3 before = mul(_EnvgPrevWorldToCam, float4(world, 1.0)).xyz;
                if (before.z >= -1e-4)
                    return float4(now, 0, 1);
                float2 uv = float2(before.x / (-before.z) / _EnvgTan.z, before.y / (-before.z) / _EnvgTan.w) * 0.5 + 0.5;
                if (any(uv < 0.0) || any(uv > 1.0))
                    return float4(now, 0, 1);
                float2 history = historyAt(uv);
                // held to this frame's neighbourhood only as far as the view has moved (a still view keeps all of it)
                float moved = length((uv - i.uv) / t);
                history = lerp(history, clamp(history, lo, hi), saturate(moved * 2.0));
                // this frame's share grows with the motion, to a quarter: the history still smooths the pixel grid
                // away while the view moves (a moving view needs that as much as a still one), and the clamp above
                // keeps it from trailing
                float share = lerp(_EnvgTss.x, 0.25, saturate((moved - 0.25) / 2.5));
                return float4(lerp(history, now, share), 0, 1);
            }
            ENDCG
        }

        // 14: our own long-wave picture, from the thermal camera's deferred buffers of the real scene. Each surface's
        // temperature: the air's, plus the sunlight and lamplight it absorbs (the light arriving is the lit colour over
        // what the surface reflects; it keeps the share it doesn't reflect), less what it loses to a cold sky the more of
        // the sky it faces. What the sensor sees: emissivity times that, plus the rest reflected from around it (metal
        // and gloss reflect the sky above them, so they read cold). Then the air between: farther things fade toward the
        // air's temperature. Out comes the apparent temperature over 100 (degrees C).
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragTemperature
            sampler2D _EnvgAlbedo;     // GBuffer0: diffuse colour (a: occlusion)
            sampler2D _EnvgAlbedoSoft; // the same, blurred over a few pixels
            sampler2D _EnvgLitSoft;    // the lit picture, blurred over a few pixels
            sampler2D _EnvgSpecular;   // GBuffer1: specular colour (a: smoothness)
            float4x4 _EnvgCamToWorld;
            float4 _EnvgTan;           // xy: tan of half the field of view
            // _EnvgThermo (shared): x air (C), y heating per unit of absorbed light (C), z sky cooling (C), w path (m)
            float4 _EnvgThermo2;       // x: a material's residual heat (C), dark against light; y: body heat (0 off); z: ground patches (C)
            sampler2D _EnvgBody;       // where people are (their share of each sensor pixel)
            sampler2D _EnvgNormalsSoft; // normals (rgb) averaged over several pixels
            sampler2D _EnvgFoliage;     // the share of rough (leafy) surfaces around
            float4 _EnvgGrassGain;      // x: the grass's texture of heat in the outline mode with the game's method; y: bare ground's warmth (C)
            sampler2D _EnvgFoliageWide; // the share of foliage over a wide area (about 25 pixels)
            sampler2D _EnvgGrassMask;   // the grass's share of each sensor pixel (r)
            float hash2(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }
            float valueNoise(float2 p)
            {
                float2 c = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(hash2(c), hash2(c + float2(1, 0)), f.x), lerp(hash2(c + float2(0, 1)), hash2(c + float2(1, 1)), f.x), f.y);
            }
            sampler2D _EnvgGeoFar;     // normals (rgb) blurred over about two pixels
            float4 fragTemperature(v2f i) : SV_Target
            {
                const float3 lum = float3(0.2126, 0.7152, 0.0722);
                float air = _EnvgThermo.x;
                float sky = air - 25.0;                 // a clear night or day sky in the long-wave band
                float4 g2 = tex2D(_EnvgNormals, i.uv);
                if (dot(g2.rgb, g2.rgb) < 1e-4)
                    return float4(sky / 100.0, sky / 100.0, sky / 100.0, 0);   // no surface (alpha 0): sky, or a crack
                // the surface's orientation over a couple of pixels (the geometric source blurred), not each texel's
                // normal map: a texture's bumps don't make its own pattern of heat; the terrain's real slopes do
                float3 nf = tex2D(_EnvgGeoFar, i.uv).rgb;
                float3 n = dot(nf, nf) > 1e-4 ? normalize(nf) : normalize(g2.rgb * 2.0 - 1.0);
                float d = tex2D(_EnvgDepth, i.uv).r;
                float2 su = toScreen(i.uv);
                float3 ray = float3((su.x * 2.0 - 1.0) * _EnvgTan.x, (su.y * 2.0 - 1.0) * _EnvgTan.y, -1.0);
                float3 dir = normalize(mul((float3x3)_EnvgCamToWorld, ray));

                float3 albedoRgb = tex2D(_EnvgAlbedo, i.uv).rgb;
                float albedo = dot(albedoRgb, lum);
                // the game's own thermal method, for comparison: a surface's "heat" is its texture's colour (the
                // G-buffer's diffuse colour, weighted 0.2 against the hot things), the people marked hot on their own
                // (their body temperature); no sun, no night, no sky facing. Fading with distance as the rest.
                if (_EnvgThermo2.w > 0.5)
                {
                    float hotBody = saturate(tex2D(_EnvgBody, i.uv).r);
                    float native = air + 8.0 * albedo;
                    // ...and, from our own model, the vegetation: grass and leaves, thin and transpiring, sit a couple
                    // of degrees under what they grow on, each blade by how much of the sky it faces (the game's own
                    // method has no grass at all: its blades are the colour of the ground)
                    float folN = smoothstep(0.15, 0.35, tex2D(_EnvgFoliage, i.uv).r);
                    float3 nBlade = normalize(g2.rgb * 2.0 - 1.0);
                    // (for the outline mode only: the fused pictures stay the game's own method as it is) the grass's
                    // texture of heat from our own model, around its mean (no offset of the whole patch, so no loop
                    // runs round it): the ground's warmer and cooler patches under it and how much of the sky each part
                    // of it faces
                    if (_EnvgThermo2.w > 1.5)
                    {
                        float3 wpN = mul(_EnvgCamToWorld, float4(ray * d, 1.0)).xyz;
                        float patchesN = valueNoise(wpN.xz / 2.2) * 0.45 + valueNoise(wpN.xz / 0.8 + 17.0) * 0.35 + valueNoise(wpN.xz / 0.3 + 41.0) * 0.2;
                        // the grass itself, as the game drew it this frame (its share of each sensor pixel): thin and
                        // transpiring, it sits under the ground it grows from, by a degree or two and in patches; the
                        // ground, a road, a path keep the game's own method. The lines run along the tufts and the
                        // edges of the grass.
                        float cover = tex2D(_EnvgGrassMask, i.uv).r;
                        native -= cover * _EnvgGrassGain.x * (0.6 + 0.8 * patchesN);
                    }
                    native = lerp(native, 34.0, hotBody);
                    native = lerp(air, native, exp(-d / max(_EnvgThermo.w, 1.0)));
                    return float4(native / 100.0, 0, native / 100.0, 1);
                }
                return float4(air / 100.0, 0, air / 100.0, 1);   // (never: the game's method is the only one)
            }
            ENDCG
        }

        // 15: the long-wave picture shown white hot, spread by the gain control (mean at middle grey, +-2 spreads)
        Pass
        {
            CGPROGRAM
            #pragma vertex vertScreen
            #pragma fragment fragShowHeat
            float4 fragShowHeat(v2f i) : SV_Target
            {
                float2 m = tex2D(_EnvgAgc, float2(0.5, 0.5)).rg;
                float spread = max(sqrt(max(m.y - m.x * m.x, 0.0)), 0.001);
                float v = saturate((tex2D(_MainTex, i.uv).r - m.x) / (4.0 * spread) + 0.5);
                return float4(v, v, v, 1);
            }
            ENDCG
        }

        // 16: seams. Where two meshes of a level don't quite meet, a pixel here and there sees no surface at all; read as
        // sky it is far colder than anything around it, and a seam turned into a dotted cold line. A pixel without a
        // surface that is mostly surrounded by surfaces (within two pixels) is such a gap: it takes their mean. Open sky
        // (no surfaces around) stays sky.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragSeams

            // Geometric edges, from the camera's depth and normals smoothed at two scales (the source: normal in rgb,
            // inverse depth in a; blurred by one pixel in _EnvgGeoNear, by two in _EnvgGeoFar). Where one thing stands in
            // front of another, the inverse depth (linear across any flat surface) bends: the near blur minus the far
            // blur, over the inverse depth, is positive on the nearer side only, so an edge is one line. Where a
            // surface folds, the normals averaged over one and over two pixels point apart. Both are smooth measures, so
            // the strength follows how much of a pixel an edge covers instead of switching per pixel, and a slanted or
            // thin edge stays a continuous line. Neither needs a difference of temperature.
            sampler2D _EnvgGeoNear;
            sampler2D _EnvgGeoFar;
            float geometry(float2 uv)
            {
                float4 a = tex2D(_EnvgGeoNear, uv);
                float4 b = tex2D(_EnvgGeoFar, uv);
                float bend = (a.a - b.a) / max(a.a, 1e-6);
                // a real step in depth (a trunk before the crown behind, a crown against the sky, a body against the
                // ground), not the small gaps between the leaves of one crown
                float silhouette = smoothstep(0.02, 0.07, bend);
                float fold = 0;
                if (dot(a.rgb, a.rgb) > 1e-4 && dot(b.rgb, b.rgb) > 1e-4)
                    fold = smoothstep(0.05, 0.15, 1.0 - dot(normalize(a.rgb), normalize(b.rgb)));   // a real fold, not a bump
                return max(silhouette, fold);
            }

            float4 fragSeams(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                float edge = geometry(i.uv);
                if (c.a > 0.5)
                    return float4(c.r, edge, c.g, 1);
                float2 t = _MainTex_TexelSize.xy;
                float sum = 0, count = 0;
                [unroll] for (int y = -2; y <= 2; y++)
                {
                    [unroll] for (int x = -2; x <= 2; x++)
                    {
                        float4 v = tex2D(_MainTex, i.uv + float2(x, y) * t);
                        if (v.a > 0.5)
                        {
                            sum += v.r;
                            count += 1;
                        }
                    }
                }
                float h = count >= 12 ? sum / count : c.r;
                return float4(h, edge, 0, 1);   // (b: foliage, as the temperature pass found it)
            }
            ENDCG
        }

        // 17: the geometric edges alone, white on black
        Pass
        {
            CGPROGRAM
            #pragma vertex vertScreen
            #pragma fragment fragShowGeometry
            float4 fragShowGeometry(v2f i) : SV_Target
            {
                float g = tex2D(_MainTex, i.uv).g;
                return float4(g, g, g, 1);
            }
            ENDCG
        }

        // 18: the geometric source: the surface normal (-1..1, zero where no surface) and the inverse depth
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragGeoSource
            float4 fragGeoSource(v2f i) : SV_Target
            {
                float4 g = tex2D(_EnvgNormals, i.uv);
                if (dot(g.rgb, g.rgb) > 1e-4)
                    return float4(normalize(g.rgb * 2.0 - 1.0), 1.0 / max(tex2D(_EnvgDepth, i.uv).r, 1e-3));
                // no surface here: a seam between two meshes that meet (mostly surfaces around it, within two pixels)
                // takes their mean, so the seam isn't an edge; open sky stays far and empty
                float2 t = _MainTex_TexelSize.xy;
                float4 sum = 0;
                float count = 0;
                [unroll] for (int y = -2; y <= 2; y++)
                {
                    [unroll] for (int x = -2; x <= 2; x++)
                    {
                        float2 uv = i.uv + float2(x, y) * t;
                        float4 gn = tex2D(_EnvgNormals, uv);
                        if (dot(gn.rgb, gn.rgb) > 1e-4)
                        {
                            sum += float4(normalize(gn.rgb * 2.0 - 1.0), 1.0 / max(tex2D(_EnvgDepth, uv).r, 1e-3));
                            count += 1;
                        }
                    }
                }
                if (count >= 12)
                    return float4(normalize(sum.rgb), sum.a / count);
                return float4(0, 0, 0, 1.0 / max(tex2D(_EnvgDepth, i.uv).r, 1e-3));
            }
            ENDCG
        }

        // 19: where the lines are (through the threshold), for measuring how crowded they are
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragPresence
            float4 fragPresence(v2f i) : SV_Target
            {
                float th = _EnvgParams.x;
                float e = smoothstep(th, th + max(_EnvgParams.y, 1e-4), tex2D(_MainTex, i.uv).r);
                return float4(e, e, e, 1);
            }
            ENDCG
        }

        // 20: the heat and its square, to be blurred into the local mean and mean square
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragSquare
            float4 fragSquare(v2f i) : SV_Target
            {
                float h = tex2D(_MainTex, i.uv).r;
                return float4(h, h * h, 0, 1);
            }
            ENDCG
        }

        // 21: fusion, as the device's own pictures show it: within the thermal rectangle the long-wave picture on the
        // overlay display, nearly opaque (the tubes' picture shows through a little), in its sepia, from dark brown to
        // cream; white hot (warm light) or black hot (cold light). The gain control spreads the scene's temperatures
        // over the display (a soft curve around the mean, as a histogram does), and a local detail boost keeps the
        // small differences (a face, the gear, the grass) visible next to the large ones.
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vertScreen
            #pragma fragment fragFusion
            float4 _EnvgFusion;          // x: 1 white hot, -1 black hot; y: opacity
            float4 _EnvgHeatView_TexelSize;
            sampler2D _EnvgLocal;        // the heat's mean and mean square around each point
            float4 _EnvgGainLimit;       // x: the smallest spread the gain stretches (C/100)

            // the sensor's picture shown smoothly on the display (a small gaussian over its pixels, not the blocks of a
            // magnified texture)
            float heatAt(float2 uv)
            {
                float2 t = _EnvgHeatView_TexelSize.xy;
                float h = tex2D(_EnvgHeatView, uv).r * 0.25;
                h += (tex2D(_EnvgHeatView, uv + float2(t.x, 0)).r + tex2D(_EnvgHeatView, uv - float2(t.x, 0)).r
                    + tex2D(_EnvgHeatView, uv + float2(0, t.y)).r + tex2D(_EnvgHeatView, uv - float2(0, t.y)).r) * 0.125;
                h += (tex2D(_EnvgHeatView, uv + t).r + tex2D(_EnvgHeatView, uv - t).r
                    + tex2D(_EnvgHeatView, uv + float2(t.x, -t.y)).r + tex2D(_EnvgHeatView, uv - float2(t.x, -t.y)).r) * 0.0625;
                return h;
            }

            sampler2D _EnvgHeLut;        // 256 x 1: the plateau-equalized display level of each temperature
            float4 _EnvgHeRange;         // x: the lowest temperature of the range (C/100), y: 1 / its span

            float4 fragFusion(v2f i) : SV_Target
            {
                float field = thermalField(i.uv);
                if (field <= 0.0)
                    return float4(0, 0, 0, 0);
                float2 tuv = thermalUv(i.uv);
                field *= beyondLens(tuv) * (1.0 - scopeLens(i.uv));
                // the picture in two layers, as a thermal core's detail enhancement makes it: the base (the heat averaged
                // over one surface only, so no halo crosses an edge), equalized over the display; the detail (the heat
                // less its base: a face, the gear, the bark), added on top
                float h = heatAt(tuv);
                float base = tex2D(_EnvgHeatView, tuv).a;
                float x = saturate((base - _EnvgHeRange.x) * _EnvgHeRange.y);
                float v = tex2D(_EnvgHeLut, float2(x * (255.0 / 256.0) + 0.5 / 256.0, 0.5)).r;
                v = saturate(v + clamp((h - base) * _EnvgHeRange.y * 2.5, -0.2, 0.2));
                if (_EnvgFusion.x < 0.0)
                    v = 1.0 - v;
                // the sepia of the device's display, dark brown to cream (display values, as the frame holds them)
                float3 dark = float3(0.13, 0.09, 0.05), mid = float3(0.55, 0.43, 0.30), light = float3(0.96, 0.89, 0.75);
                float3 c = v < 0.5 ? lerp(dark, mid, v * 2.0) : lerp(mid, light, v * 2.0 - 1.0);
                return float4(c * _EnvgParams.z, _EnvgFusion.y * field);
            }
            ENDCG
        }

        // 22: normals, with the forward-drawn surfaces' own (from the full depth's slopes, facing the camera)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragNormalsFwd
            sampler2D _EnvgFullDepth;  // the thermal camera's own depth buffer (raw), with what was drawn forward
            float4 _EnvgZBuffer;       // _ZBufferParams for the thermal camera
            float fullLinear(float2 uv)
            {
                float raw = tex2D(_EnvgFullDepth, uv).r;
            #if defined(UNITY_REVERSED_Z)
                if (raw <= 1e-7) return 1e6;
            #else
                if (raw >= 1.0 - 1e-7) return 1e6;
            #endif
                return 1.0 / (_EnvgZBuffer.z * raw + _EnvgZBuffer.w);
            }
            // drawn forward here: a surface in the full depth clearly in front of the deferred one, or where that has none
            float forwardAt(float2 uv)
            {
                float full = fullLinear(uv);
                if (full > 1e5)
                    return 0;
                float4 g = tex2D(_EnvgNormals, uv);
                if (dot(g.rgb, g.rgb) < 1e-4)
                    return 1;
                float d = tex2D(_EnvgDepth, uv).r;
                return full < d * 0.97 - 0.03 ? 1 : 0;
            }
            float4x4 _EnvgCamToWorld;
            float4 _EnvgTan;
            float3 viewPos(float2 uv)
            {
                return float3((uv.x * 2.0 - 1.0) * _EnvgTan.x, (uv.y * 2.0 - 1.0) * _EnvgTan.y, -1.0) * fullLinear(uv);
            }
            float4 fragNormalsFwd(v2f i) : SV_Target
            {
                float4 g = tex2D(_EnvgNormals, i.uv);
                if (forwardAt(i.uv) < 0.5)
                    return g;
                float2 t = _MainTex_TexelSize.xy;
                float3 p = viewPos(i.uv);
                // the slope on the side nearer in depth (the other side may be the ground behind a blade)
                float3 r = viewPos(i.uv + float2(t.x, 0)) - p, l = p - viewPos(i.uv - float2(t.x, 0));
                float3 u = viewPos(i.uv + float2(0, t.y)) - p, d = p - viewPos(i.uv - float2(0, t.y));
                float3 dx = abs(r.z) < abs(l.z) ? r : l;
                float3 dy = abs(u.z) < abs(d.z) ? u : d;
                float3 n = cross(dx, dy);
                if (dot(n, n) < 1e-12 || abs(dx.z) > 0.5 || abs(dy.z) > 0.5)
                    n = -normalize(p);   // a blade a pixel wide: facing the camera
                n = normalize(n);
                if (dot(n, p) > 0)
                    n = -n;
                float3 w = normalize(mul((float3x3)_EnvgCamToWorld, n));
                return float4(w * 0.5 + 0.5, 1);
            }
            ENDCG
        }

        // 23: albedo, with the forward-drawn surfaces marked as vegetation (pure green, as the instanced grass)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragAlbedoFwd
            sampler2D _EnvgFullDepth;  // the thermal camera's own depth buffer (raw), with what was drawn forward
            float4 _EnvgZBuffer;       // _ZBufferParams for the thermal camera
            float fullLinear(float2 uv)
            {
                float raw = tex2D(_EnvgFullDepth, uv).r;
            #if defined(UNITY_REVERSED_Z)
                if (raw <= 1e-7) return 1e6;
            #else
                if (raw >= 1.0 - 1e-7) return 1e6;
            #endif
                return 1.0 / (_EnvgZBuffer.z * raw + _EnvgZBuffer.w);
            }
            // drawn forward here: a surface in the full depth clearly in front of the deferred one, or where that has none
            float forwardAt(float2 uv)
            {
                float full = fullLinear(uv);
                if (full > 1e5)
                    return 0;
                float4 g = tex2D(_EnvgNormals, uv);
                if (dot(g.rgb, g.rgb) < 1e-4)
                    return 1;
                float d = tex2D(_EnvgDepth, uv).r;
                return full < d * 0.97 - 0.03 ? 1 : 0;
            }
            float4 fragAlbedoFwd(v2f i) : SV_Target
            {
                return forwardAt(i.uv) > 0.5 ? float4(0, 0.25, 0, 1) : tex2D(_MainTex, i.uv);
            }
            ENDCG
        }

        // 24: linear depth, with the forward-drawn surfaces in front
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDepthFwd
            sampler2D _EnvgFullDepth;  // the thermal camera's own depth buffer (raw), with what was drawn forward
            float4 _EnvgZBuffer;       // _ZBufferParams for the thermal camera
            float fullLinear(float2 uv)
            {
                float raw = tex2D(_EnvgFullDepth, uv).r;
            #if defined(UNITY_REVERSED_Z)
                if (raw <= 1e-7) return 1e6;
            #else
                if (raw >= 1.0 - 1e-7) return 1e6;
            #endif
                return 1.0 / (_EnvgZBuffer.z * raw + _EnvgZBuffer.w);
            }
            // drawn forward here: a surface in the full depth clearly in front of the deferred one, or where that has none
            float forwardAt(float2 uv)
            {
                float full = fullLinear(uv);
                if (full > 1e5)
                    return 0;
                float4 g = tex2D(_EnvgNormals, uv);
                if (dot(g.rgb, g.rgb) < 1e-4)
                    return 1;
                float d = tex2D(_EnvgDepth, uv).r;
                return full < d * 0.97 - 0.03 ? 1 : 0;
            }
            float4 fragDepthFwd(v2f i) : SV_Target
            {
                float d = tex2D(_EnvgDepth, i.uv).r;
                if (forwardAt(i.uv) > 0.5)
                    d = fullLinear(i.uv);
                return float4(d, d, d, 1);
            }
            ENDCG
        }

        // 25: a copy, read back from the frame's sub-pixel offset
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragCopyJitter
            float4 _EnvgJitter;
            float4 _EnvgFootprint;   // xy: one sensor pixel (uv): the detector integrates over its area
            // The thermal lens's own blur before the detector, as a real one has it: a gaussian of 0.7 of a sensor pixel
            // (sigma), taken over a sensor pixel and a half around each one. The mean over the pixel's own area alone (a
            // box) let detail finer than a pixel through, and as the head swayed a fraction of a pixel each frame (with no
            // history any more) thin lines broke into dashes running to and fro and far things shimmered (the user's,
            // 2026-11-27); what the optics do not pass cannot alias.
            float4 fragCopyJitter(v2f i) : SV_Target
            {
                float4 sum = 0;
                float wsum = 0;
                [unroll] for (int y = 0; y < 7; y++)
                    [unroll] for (int x = 0; x < 7; x++)
                    {
                        // (sigma 0.7 of a sensor pixel, taps out to 1.5: at 0.5 thin lines still broke into dashes running
                        // to and fro in the fused picture, whose detail boost brings out what is left, the user's video)
                        float2 o = (float2(x, y) - 3.0) * 0.5;   // sensor pixels, -1.5 to 1.5
                        float w = exp(-dot(o, o) / (2.0 * 0.7 * 0.7));
                        sum += w * tex2D(_MainTex, toSource(i.uv) + _EnvgJitter.xy + o * _EnvgFootprint.xy);
                        wsum += w;
                    }
                return sum / wsum;
            }
            ENDCG
        }

        // 26: the centre lines. Each edge's response is a band a few pixels wide; only the pixels on its ridge (at least
        // as strong as both neighbours across it, along the response's gradient) are kept, through the threshold: the
        // lines become one pixel wide and the composite draws them with an even pen.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragRidge
            float4 fragRidge(v2f i) : SV_Target
            {
                float2 t = _MainTex_TexelSize.xy;
                float e = tex2D(_MainTex, i.uv).r;
                float th = _EnvgParams.x;
                float soft = max(_EnvgParams.y, 1e-4);
                // weaker than a third of the threshold: nothing to draw here
                if (e < th * 0.35)
                    return 0;
                float ex1 = tex2D(_MainTex, i.uv + float2(t.x, 0)).r, ex0 = tex2D(_MainTex, i.uv - float2(t.x, 0)).r;
                float ey1 = tex2D(_MainTex, i.uv + float2(0, t.y)).r, ey0 = tex2D(_MainTex, i.uv - float2(0, t.y)).r;
                float2 g = float2(ex1 - ex0, ey1 - ey0);
                // across the ridge: the direction the response falls off fastest from the neighbours' second differences
                float cxx = ex1 + ex0 - 2.0 * e, cyy = ey1 + ey0 - 2.0 * e;
                float cxy = (tex2D(_MainTex, i.uv + t).r + tex2D(_MainTex, i.uv - t).r
                           - tex2D(_MainTex, i.uv + float2(t.x, -t.y)).r - tex2D(_MainTex, i.uv + float2(-t.x, t.y)).r) * 0.25;
                // the eigenvector of the most negative curvature of the response
                float tr = cxx + cyy, det = cxx * cyy - cxy * cxy;
                float l = tr * 0.5 - sqrt(max(tr * tr * 0.25 - det, 0.0));
                float2 n = abs(cxy) > 1e-6 ? float2(l - cyy, cxy) : (cxx < cyy ? float2(1, 0) : float2(0, 1));
                n = normalize(n);
                float a = tex2D(_MainTex, i.uv + n * t).r;
                float b = tex2D(_MainTex, i.uv - n * t).r;
                if (e < a || e < b)
                    return 0;
                // a speck (a ridge pixel with fewer than two strong neighbours: no line runs through it) isn't drawn
                int strong = 0;
                [unroll] for (int y = -1; y <= 1; y++)
                {
                    [unroll] for (int x = -1; x <= 1; x++)
                    {
                        if (x != 0 || y != 0)
                            strong += tex2D(_MainTex, i.uv + float2(x, y) * t).r >= th * 0.7 ? 1 : 0;
                    }
                }
                if (strong < 2)
                    return 0;
                // hysteresis: a weaker stretch is kept where it continues a line (a neighbour at full strength), so a line
                // near the threshold doesn't come and go in dashes from frame to frame
                float neighbourMax = 0;
                [unroll] for (int yy = -1; yy <= 1; yy++)
                    [unroll] for (int xx = -1; xx <= 1; xx++)
                        neighbourMax = max(neighbourMax, tex2D(_MainTex, i.uv + float2(xx, yy) * t).r);
                float low = neighbourMax >= th + soft * 0.5 ? th * 0.55 : th;
                float v = smoothstep(low, low + soft, e);
                // the crest between the pixels: a parabola through the three values across the line
                float offset = clamp((a - b) / max(2.0 * (2.0 * e - a - b), 1e-5), -0.5, 0.5);
                return float4(v, offset, n.x, n.y);
            }
            ENDCG
        }

        // 27: the edge strength followed in time, as the game's TAA follows its picture: last frame's value fetched from
        // where the game's motion vectors say this point was, held to this frame's 3 x 3 neighbourhood (widened a
        // little, so a line dipping for a frame is carried over the dip), blended in
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragTemporal
            sampler2D _EnvgMotion;        // the game's motion vectors (uv now - uv then)
            sampler2D _EnvgEdgeHistory;   // last frame's followed edge strength
            float4 _EnvgTemporal;         // x: the share of the history
            float4 fragTemporal(v2f i) : SV_Target
            {
                float2 t = _MainTex_TexelSize.xy;
                float c = tex2D(_MainTex, i.uv).r;
                float lo = c, hi = c;
                [unroll] for (int y = -1; y <= 1; y++)
                {
                    [unroll] for (int x = -1; x <= 1; x++)
                    {
                        float v = tex2D(_MainTex, i.uv + float2(x, y) * t).r;
                        lo = min(lo, v);
                        hi = max(hi, v);
                    }
                }
                float2 previous = previousUv(i.uv);
                // (the gun and the hands: this frame's own lines, as their heat)
                if (_EnvgTemporal.x <= 0.0 || any(previous < 0.0) || any(previous > 1.0) || nearHeld(i.uv) || nearThen(previous))
                    return float4(c, c, c, 1);
                // (fetched sharply, as the heat's history: a bilinear fetch each frame, the view moving, flattened the
                // lines' crests and fewer lines came through while turning)
                float h = sharpAt(_EnvgEdgeHistory, previous).r;
                // held strictly within this frame's neighbourhood, as TAA holds its history: with any slack the
                // history crept up frame after frame inside foliage (every pixel near a strong one) and filled crowns
                h = clamp(h, lo, hi);
                // the history's share by how far the picture moved since last frame (the chain's pixels): whole while the
                // view is still (the lines held steady), less as it moves and none from a pixel and a half a frame on.
                // Each frame's history is fetched between pixels while the view moves, and however sharp the fetch, a
                // line carried over many frames that way spreads and drops under the threshold: lines thinned while
                // turning. Moving, the picture is the frame's own; the heat under it is already settled (pass 36).
                float moved = length((previous - i.uv) * _EnvgHistSize.zw);
                float v = lerp(c, h, _EnvgTemporal.x * saturate(1.0 - moved / 1.5));
                return float4(v, v, v, 1);
            }
            ENDCG
        }

        // 28: where the surfaces face every way within a pixel or two (needles, leaves): 1 there, 0 on a wall, a rock,
        // the ground under its bumps; to be blurred into the share of such surfaces around
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragRough
            sampler2D _EnvgGeoNear;
            sampler2D _EnvgGeoFar;
            float4 fragRough(v2f i) : SV_Target
            {
                float3 nsum = 0;
                float ncount = 0;
                float2 tx = _MainTex_TexelSize.xy;
                [unroll] for (int fy = -1; fy <= 1; fy++)
                {
                    [unroll] for (int fx = -1; fx <= 1; fx++)
                    {
                        float4 gn = tex2D(_EnvgNormals, i.uv + float2(fx, fy) * tx * 1.5);
                        if (dot(gn.rgb, gn.rgb) > 1e-4)
                        {
                            nsum += normalize(gn.rgb * 2.0 - 1.0);
                            ncount += 1;
                        }
                    }
                }
                float roughness = ncount > 3 ? 1.0 - length(nsum) / ncount : 0.0;
                // and leaf cards (flat, so their normals agree): a crown of them is full of small steps in depth, one
                // card in front of the next; a solid thing has them only along its outline
                float4 gNear = tex2D(_EnvgGeoNear, i.uv);
                float4 gFar = tex2D(_EnvgGeoFar, i.uv);
                float steps = smoothstep(0.003, 0.012, abs(gNear.a - gFar.a) / max(gNear.a, 1e-6));
                float v = max(smoothstep(0.12, 0.3, roughness), steps) * step(3.5, ncount);
                // grass too fine for the sensor's pixels: the depth within each pixel is rough
                v = max(v, smoothstep(0.004, 0.012, tex2D(_EnvgDepth, i.uv).g) * step(3.5, ncount));
                // g: the same for the ground's own cover only (grass faces up with the ground; a tree's crown and trunk
                // don't), so a tree doesn't spread a patch of "grass" round its foot
                float upN = ncount > 0.5 ? saturate(normalize(nsum + float3(0, 1e-4, 0)).y * 1.5 - 0.3) : 0.0;
                // (and with the ground's normal: the game draws grass with the terrain's normal, while a crown's needles
                // and leaf cards face every way)
                float smoothN = 1.0 - smoothstep(0.05, 0.15, roughness);
                return float4(v, v * upN * smoothN, 0, 1);
            }
            ENDCG
        }

        // 29: the separable blur of pass 2, but over one surface only: taps at a clearly different distance (another
        // object in front or behind) don't count, so light, colour and orientation averaged over a wall or a crown
        // don't spill onto what stands before it
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBlurDepth
            float4 fragBlurDepth(v2f i) : SV_Target
            {
                const float w[5] = { 0.2270, 0.1945, 0.1216, 0.0540, 0.0162 };
                float d0 = tex2D(_EnvgDepth, i.uv).r;
                float tol = 0.04 * d0 + 0.05;
                float4 s = tex2D(_MainTex, i.uv) * w[0];
                float total = w[0];
                [unroll] for (int n = 1; n < 5; n++)
                {
                    [unroll] for (int side = -1; side <= 1; side += 2)
                    {
                        float2 uv = i.uv + _EnvgBlur.xy * n * side;
                        float d = tex2D(_EnvgDepth, uv).r;
                        float k = w[n] * saturate(1.0 - abs(d - d0) / tol);
                        s += tex2D(_MainTex, uv) * k;
                        total += k;
                    }
                }
                return s / total;
            }
            ENDCG
        }

        // 30: the heat's base layer (averaged over one surface) written into the heat picture's alpha
        Pass
        {
            ColorMask A
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBase
            float4 fragBase(v2f i) : SV_Target
            {
                return float4(0, 0, 0, tex2D(_MainTex, i.uv).r);
            }
            ENDCG
        }

        // 31: the green channel as red (the ground's own cover, for the wide foliage)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragG
            float4 fragG(v2f i) : SV_Target
            {
                float g = tex2D(_MainTex, i.uv).g;
                return float4(g, g, g, 1);
            }
            ENDCG
        }

        // 32: the grass mask at the sensor's pixels: the share of its 4 x 4 screen samples the grass was drawn on (the
        // redraw leaves its own output there; the rest keeps the clear value, -100)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragGrassCover
            float4 _EnvgJitter;
            float4 _EnvgFootprint;
            float4 fragGrassCover(v2f i) : SV_Target
            {
                float c = 0;
                [unroll] for (int y = 0; y < 4; y++)
                    [unroll] for (int x = 0; x < 4; x++)
                        c += tex2D(_MainTex, toSource(i.uv) + _EnvgJitter.xy + (float2(x, y) - 1.5) * 0.25 * _EnvgFootprint.xy).r > -50.0 ? 1.0 : 0.0;
                c /= 16.0;
                return float4(c, c, c, 1);
            }
            ENDCG
        }

        // 33: a wide gaussian along one axis in one pass, the same as pass 2 run several times over (its sigma the
        // root of their squared sum): every texel within three sigma, two at a time through the texture's bilinear
        // filter (taps a pixel apart, so no comb). One read and one write of the picture instead of one per narrow pass.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragWide
            #pragma target 3.0
            float4 _EnvgWide;    // x: sigma (texels), y: radius (texels), zw: one texel along the axis (uv)
            float4 fragWide(v2f i) : SV_Target
            {
                float k = -0.5 / max(_EnvgWide.x * _EnvgWide.x, 1e-4);
                float4 s = tex2D(_MainTex, i.uv);
                float total = 1.0;
                int r = (int)_EnvgWide.y;
                [loop] for (int n = 1; n <= r; n += 2)
                {
                    float wa = exp(k * n * n), wb = exp(k * (n + 1) * (n + 1));
                    float w = wa + wb;
                    float2 o = _EnvgWide.zw * ((n * wa + (n + 1) * wb) / w);
                    s += (tex2D(_MainTex, i.uv + o) + tex2D(_MainTex, i.uv - o)) * w;
                    total += 2.0 * w;
                }
                return s / total;
            }
            ENDCG
        }

        // 34: the same over one surface only (as pass 29): taps at a clearly different distance don't count
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragWideDepth
            #pragma target 3.0
            float4 _EnvgWide;
            float4 fragWideDepth(v2f i) : SV_Target
            {
                float k = -0.5 / max(_EnvgWide.x * _EnvgWide.x, 1e-4);
                float d0 = tex2D(_EnvgDepth, i.uv).r;
                float tol = 0.04 * d0 + 0.05;
                float4 s = tex2D(_MainTex, i.uv);
                float total = 1.0;
                int r = (int)_EnvgWide.y;
                // two texels at a time through the bilinear filter, as pass 33 (their depth read the same way: on one
                // surface it is theirs, across an edge the pair counts in part)
                [loop] for (int n = 1; n <= r; n += 2)
                {
                    float wa = exp(k * n * n), wb = exp(k * (n + 1) * (n + 1));
                    float w = wa + wb;
                    float2 o = _EnvgWide.zw * ((n * wa + (n + 1) * wb) / w);
                    float a = w * saturate(1.0 - abs(tex2D(_EnvgDepth, i.uv + o).r - d0) / tol);
                    float b = w * saturate(1.0 - abs(tex2D(_EnvgDepth, i.uv - o).r - d0) / tol);
                    s += tex2D(_MainTex, i.uv + o) * a + tex2D(_MainTex, i.uv - o) * b;
                    total += a + b;
                }
                return s / total;
            }
            ENDCG
        }

        // (39 to 41, after 38 below: the wide blurs worked out at half the size)

        // 35: the symbology (EnvgHud): light added to the eyepiece's picture, as the overlay display adds it. Glyphs
        // from a TextMeshPro distance atlas (kind 0) and solid bars (kind 1, their distance worked out here); a
        // near-white core and a cyan glow falling off over a few pixels outside it.
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vertHud
            #pragma fragment fragHud
            sampler2D _EnvgHudFont;
            float4 _EnvgHudSdf;    // x: atlas texels of distance per unit of alpha
            float4 _EnvgHudCore;   // rgb: the core's colour, w: the glow's width (pixels)
            float4 _EnvgHudGlow;   // rgb: the glow's colour, a: its strength
            struct hudIn { float4 vertex : POSITION; float2 uv : TEXCOORD0; float2 aux : TEXCOORD1; float4 tint : COLOR; };
            struct hudV { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 aux : TEXCOORD1; float kind : TEXCOORD2; float4 tint : TEXCOORD3; };
            hudV vertHud(hudIn v)
            {
                hudV o;
                o.pos = float4(v.vertex.xy, 0, 1);
                o.uv = v.uv;
                o.aux = v.aux;
                o.kind = v.vertex.z;
                o.tint = v.tint;
                return o;
            }
            float4 fragHud(hudV i) : SV_Target
            {
                float sd;     // signed distance to the shape's edge, in screen pixels, positive inside
                float reach;  // how far out the quad reaches (the glow must be gone there, or the quad shows as a tile)
                if (i.kind < 0.5)
                {
                    sd = (tex2D(_EnvgHudFont, i.uv).a - 0.5) * _EnvgHudSdf.x * i.aux.x;
                    reach = 0.5 * _EnvgHudSdf.x * i.aux.x;   // the atlas's distance runs out at its padding
                }
                else
                {
                    float2 q = abs(i.uv) - i.aux;
                    sd = -(length(max(q, 0.0)) + min(max(q.x, q.y), 0.0));
                    reach = i.kind - 1.0;
                }
                float core = saturate(sd + 0.5);
                float beyond = saturate(-sd / max(reach, 0.5));
                float fall = exp(-max(-sd, 0.0) / max(_EnvgHudCore.w, 0.5)) * (1.0 - beyond) * (1.0 - beyond);
                float glow = fall * (1.0 - core) * _EnvgHudGlow.a;
                // the symbology's own cyan, or a shape's own colour (alpha 1: the battery's green cells)
                float strength = max(max(_EnvgHudCore.r, _EnvgHudCore.g), _EnvgHudCore.b);
                float3 coreCol = i.tint.a > 0.5 ? i.tint.rgb * strength : _EnvgHudCore.rgb;
                float3 glowCol = i.tint.a > 0.5 ? i.tint.rgb * 0.6 * strength : _EnvgHudGlow.rgb;
                return float4(coreCol * core + glowCol * glow, 1);
            }
            ENDCG
        }

        // 36: the heat picture followed in time, as the game's TAA follows its own picture. The scene is copied from a
        // camera jittered by a sub-pixel each frame: the copy is shifted back, but which pixels an edge covers still
        // changes from frame to frame (the game smooths that over frames for its picture, not for its buffers), so
        // every edge of the heat flickered. Last frame's heat fetched from where the motion vectors say this point
        // was, held within this frame's 3 x 3 neighbourhood (nothing new can be dragged along), blended in.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragHeatTemporal
            sampler2D _EnvgMotion;
            float4 fragHeatTemporal(v2f i) : SV_Target
            {
                float2 t = _MainTex_TexelSize.xy;
                float4 c = tex2D(_MainTex, i.uv);
                float4 lo = c, hi = c;
                [unroll] for (int y = -1; y <= 1; y++)
                {
                    [unroll] for (int x = -1; x <= 1; x++)
                    {
                        float4 v = tex2D(_MainTex, i.uv + float2(x, y) * t);
                        lo = min(lo, v);
                        hi = max(hi, v);
                    }
                }
                float predicted;
                float2 previous = previousUvAt(i.uv, predicted);
                if (_EnvgHeatTemporal.x <= 0.0 || any(previous < 0.0) || any(previous > 1.0))
                    return c;
                // As the game's TAA settles its picture: the copy of a jittered frame, and the textures sampled at
                // another sub-pixel, change a little every frame though nothing moved; only many frames together are
                // steady. On the same surface the frames are averaged (see sameSurfaceThen); elsewhere (people,
                // something that came into view) last frame's heat is held to this frame's neighbourhood, as before.
                // the gun and the hands: this frame's own heat. They sway on their own against the camera, and no
                // history of them (followed as the still world, or held at the same place) stays with them: it trailed
                // a ghost of the gun behind it while it swung (the user's video, F-PANO 0.2.52)
                // (and where the gun was last frame: what it uncovers starts anew, no trail of it left behind)
                if (nearHeld(i.uv) || nearThen(previous))
                    return c;
                if (sameSurfaceThen(previous, predicted))
                {
                    if (!historyHolds(i.uv, previous))
                        return c;
                    float n = tex2Dlod(_EnvgHeatCount, float4(previous, 0, 0)).r;
                    n = n >= 0.0 ? min(n, HeatFrames) : 0.0;   // (nothing valid there: none)
                    // (fewer frames held while the picture moves: each is fetched between pixels, and many such
                    // fetches soften it; still, all of them)
                    float moved = length((previous - i.uv) * _EnvgHistSize.zw);
                    // (a few frames only once it moves at all, as the game's TAA keeps about a fifth of each new frame:
                    // the picture moves a fraction of a pixel every frame with K-3PS-C's eyepieces (the head sways,
                    // 0.1-0.4 px a frame), and at up to 32 frames, each fetched between pixels, the history smeared
                    // into trails, the user's 2026-11-27; many frames only while it holds still)
                    // (8 while it moves: the jitter runs over 8 places, and with 4 thin far things, covered by only some
                    // of them, still came and went: the user's, 0.73.28 near fine, far not)
                    // (and fewer as it moves faster: 8 while it only sways (under half a pixel a frame), down to 3 at two
                    // pixels a frame and more, walking or turning, where 8 trailed again: the user's, 0.73.29)
                    float frames = lerp(8.0, 3.0, saturate((moved - 0.5) / 1.5));
                    n = min(n, lerp(HeatFrames, frames, saturate(moved / 0.1)));
                    float4 hh = heatHistoryAt(previous);
                    if (any(hh != hh))
                        return c;
                    // held to this frame's range around the point, widened by its spread: the frames' mean of a
                    // still surface stays inside (and keeps being averaged); what moved off (an edge's old place, a
                    // ghost) is far outside and is cut back to what is here now
                    float4 sd = spreadAround(i.uv);
                    // (the range widened by the spread only while it holds still: moving, old frames on a textured
                    // wall slipped through the widened range and trailed)
                    float4 widen = sd * (1.0 - saturate(moved / 0.1));
                    return lerp(c, clamp(hh, lo - widen, hi + widen), n / (n + 1.0));
                }
                float4 hc = tex2D(_EnvgHeatHistory, previous);
                if (any(hc != hc))
                    return c;
                return lerp(c, clamp(hc, lo, hi), _EnvgHeatTemporal.x);
            }
            ENDCG
        }
        // 37: the tube's picture at the set gain (the eyepieces' copy of it)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragI2Gain
            float4 fragI2Gain(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                return float4(c.rgb * (1.0 - _EnvgI2Dim.x), c.a);
            }
            ENDCG
        }


        // 38: how many frames the heat's history now holds (pass 36): one more where last frame saw the same surface
        // (up to HeatFrames), one where it was held to the neighbourhood, none where there was no history
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragHeatCount
            float4 fragHeatCount(v2f i) : SV_Target
            {
                float predicted;
                float2 previous = previousUvAt(i.uv, predicted);
                if (_EnvgHeatTemporal.x <= 0.0 || any(previous < 0.0) || any(previous > 1.0))
                    return 0;
                if (nearHeld(i.uv))
                    return 1;
                if (sameSurfaceThen(previous, predicted))
                    return historyHolds(i.uv, previous) ? min(tex2Dlod(_EnvgHeatCount, float4(previous, 0, 0)).r + 1.0, HeatFrames) : 1.0;
                return 1;
            }
            ENDCG
        }

        // 39: a half-size copy for the wide same-surface blurs: one texel of each two by two, as it is (an average would
        // mix two surfaces where an edge crosses the four); the depth halved the same way
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragPick
            float4 fragPick(v2f i) : SV_Target
            {
                return tex2Dlod(_MainTex, float4(i.uv - 0.5 * _MainTex_TexelSize.xy, 0, 0));
            }
            ENDCG
        }

        // 40: pass 34 at half the size, along x: the half-size copy, over one surface by the half-size depth. The wide
        // blurs are smooth over many pixels: worked out on a quarter of the pixels, with half the taps
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragWideHalfAcross
            #pragma target 3.0
            float4 _EnvgWide;
            sampler2D _EnvgDepthHalf;
            float4 fragWideHalfAcross(v2f i) : SV_Target
            {
                float k = -0.5 / max(_EnvgWide.x * _EnvgWide.x, 1e-4);
                float d0 = tex2Dlod(_EnvgDepthHalf, float4(i.uv, 0, 0)).r;
                float tol = 0.04 * d0 + 0.05;
                float4 s = tex2Dlod(_MainTex, float4(i.uv, 0, 0));
                float total = 1.0;
                int r = (int)_EnvgWide.y;
                [loop] for (int n = 1; n <= r; n += 2)
                {
                    float wa = exp(k * n * n), wb = exp(k * (n + 1) * (n + 1));
                    float w = wa + wb;
                    float2 o = _EnvgWide.zw * ((n * wa + (n + 1) * wb) / w);
                    float a = w * saturate(1.0 - abs(tex2Dlod(_EnvgDepthHalf, float4(i.uv + o, 0, 0)).r - d0) / tol);
                    float b = w * saturate(1.0 - abs(tex2Dlod(_EnvgDepthHalf, float4(i.uv - o, 0, 0)).r - d0) / tol);
                    s += tex2Dlod(_MainTex, float4(i.uv + o, 0, 0)) * a + tex2Dlod(_MainTex, float4(i.uv - o, 0, 0)) * b;
                    total += a + b;
                }
                return s / total;
            }
            ENDCG
        }

        // 41: then along y into the full size: each pixel its own value and its own depth (so the surfaces stay apart
        // at full size), the taps from the half-size pass 40, over the same surface by the half-size depth
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragWideHalfDown
            #pragma target 3.0
            float4 _EnvgWide;
            sampler2D _EnvgDepthHalf;
            sampler2D _EnvgWideOwn;
            float4 fragWideHalfDown(v2f i) : SV_Target
            {
                float k = -0.5 / max(_EnvgWide.x * _EnvgWide.x, 1e-4);
                float d0 = tex2Dlod(_EnvgDepth, float4(i.uv, 0, 0)).r;
                float tol = 0.04 * d0 + 0.05;
                float4 s = tex2Dlod(_EnvgWideOwn, float4(i.uv, 0, 0));
                float total = 1.0;
                int r = (int)_EnvgWide.y;
                [loop] for (int n = 1; n <= r; n += 2)
                {
                    float wa = exp(k * n * n), wb = exp(k * (n + 1) * (n + 1));
                    float w = wa + wb;
                    float2 o = _EnvgWide.zw * ((n * wa + (n + 1) * wb) / w);
                    float a = w * saturate(1.0 - abs(tex2Dlod(_EnvgDepthHalf, float4(i.uv + o, 0, 0)).r - d0) / tol);
                    float b = w * saturate(1.0 - abs(tex2Dlod(_EnvgDepthHalf, float4(i.uv - o, 0, 0)).r - d0) / tol);
                    s += tex2Dlod(_MainTex, float4(i.uv + o, 0, 0)) * a + tex2Dlod(_MainTex, float4(i.uv - o, 0, 0)) * b;
                    total += a + b;
                }
                return s / total;
            }
            ENDCG
        }

        // 42: the heading HUD in the fused picture in the eyepieces, added light lit as the ENVG-B's symbology: a ring
        // around the heading in degrees (stroke digits)
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vertScreen
            #pragma fragment fragHud
            #pragma target 3.0
            float digitDist(float2 p, int n) { return glyphDist(p, Segments[clamp(n, 0, 9)]); }
            float4 fragHud(v2f i) : SV_Target
            {
                // in the ring's own units (radius 1, y up)
                float2 q = (i.uv - _FpanoHud.xy) * float2(_FpanoHud.w, 1.0) / max(_FpanoHud.z, 1e-5);
                if (_FpanoHudB.z <= 0.0 || abs(q.x) > 1.4 || abs(q.y) > 1.4)
                    return float4(0, 0, 0, 1);
                float px = 1.0 / max(_FpanoHud.z * _FpanoHudB.w, 1.0);
                float r = length(q);
                // signed distances to each shape's edge, in pixels, positive inside
                float ringSd = (0.055 - abs(r - 1.0)) / px;
                // the digits and the degree sign, centred
                int heading = (int)(_FpanoHudB.x + 0.5);
                int count = heading >= 100 ? 3 : heading >= 10 ? 2 : 1;
                float h = 0.44, gap = 0.3 * h, w = 0.5 * h;
                float total = count * (w + gap) + 0.26 * h;
                float x0 = -0.5 * total;
                float textSd = -1e3;
                int div = count == 3 ? 100 : count == 2 ? 10 : 1;
                [unroll] for (int k = 0; k < 3; k++)
                {
                    if (k < count)
                    {
                        int digit = (heading / div) % 10;
                        float2 p = (q - float2(x0 + k * (w + gap), -0.5 * h)) / h;
                        textSd = max(textSd, (0.045 - digitDist(p, digit) * h) / px);
                        div = max(div / 10, 1);
                    }
                }
                float2 deg = float2(x0 + count * (w + gap) + 0.12 * h, 0.5 * h - 0.12 * h);
                textSd = max(textSd, (0.035 - abs(length(q - deg) - 0.09 * h)) / px);
                // lit as the ENVG-B's symbology: a near-white core and a cyan glow falling off over a few pixels (its
                // width a fifth of the text's em), gone before the shape's reach
                float glowPx = max(0.22 * h / 0.7 / px, 0.5);
                // the digits cyan-white, the ring in the overlay's own amber, each glowing in its colour
                float2 t = hudLight(textSd, glowPx), g = hudLight(ringSd, glowPx);
                float3 col = float3(0.80, 1.00, 1.00) * t.x + float3(0.10, 0.75, 1.00) * t.y + _EnvgCore.rgb * g.x + _EnvgHalo.rgb * g.y;
                return float4(col * _FpanoHudB.z, 1);
            }
            ENDCG
        }

        // 43: the I2 gain bar, as the ENVG-B's: a yellow outline filled from the bottom at the tubes' gain, "I2 GAIN"
        // under it, at the left of the thermal picture, while the gain changes and a moment after
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vertScreen
            #pragma fragment fragGainBar
            #pragma target 3.0
            float4 _FpanoGain;   // x: the thermal picture's left edge (uv), y: its centre (uv), z: its half height (uv),
                                 // w: the gain (below 0: not shown)
            static const float Label[7] = { 6, 91, 0, 61, 119, 6, 55 };   // I 2 (space) G A I N
            float4 fragGainBar(v2f i) : SV_Target
            {
                if (_FpanoGain.w < 0.0 || _FpanoHudB.z <= 0.0)
                    return float4(0, 0, 0, 1);
                float H = _FpanoHudB.w;
                float2 p = float2(i.uv.x * _FpanoHud.w, i.uv.y) * H;   // pixels
                float halfH = _FpanoGain.z * H;
                float ts = max(10.0, 0.104 * halfH), lw = max(1.0, 0.07 * ts);
                float gx = _FpanoGain.x * _FpanoHud.w * H + 0.9 * ts;
                float cy = _FpanoGain.y * H;
                float gTop = cy + 0.55 * halfH, gBottom = cy - 0.45 * halfH;
                float gw = 0.32 * ts, gMid = 0.5 * (gTop + gBottom), gHalf = 0.5 * (gTop - gBottom);
                if (p.x < gx - gw - 2.0 * ts || p.x > gx + 5.0 * ts || p.y < gBottom - 2.5 * ts || p.y > gTop + 2.0 * ts)
                    return float4(0, 0, 0, 1);
                float sd = boxSd(p, float2(gx, gTop), float2(gw, 0.5 * lw));
                sd = max(sd, boxSd(p, float2(gx, gBottom), float2(gw, 0.5 * lw)));
                sd = max(sd, boxSd(p, float2(gx - gw, gMid), float2(0.5 * lw, gHalf)));
                sd = max(sd, boxSd(p, float2(gx + gw, gMid), float2(0.5 * lw, gHalf)));
                float inner = gHalf - 2.0 * lw, filled = 2.0 * inner * saturate(_FpanoGain.w);
                if (filled > 0.5)
                    sd = max(sd, boxSd(p, float2(gx, gBottom + 2.0 * lw + 0.5 * filled), float2(gw - 2.0 * lw, 0.5 * filled)));
                // the label, glyphs half the em high
                float gh = 0.5 * ts, adv = 0.8 * gh;
                float2 at = float2(gx - gw, gBottom - 1.2 * ts);
                [unroll] for (int k = 0; k < 7; k++)
                {
                    if (Label[k] > 0.5)
                        sd = max(sd, 0.09 * gh - glyphDist((p - at - float2(k * adv, 0.0)) / gh, Label[k]) * gh);
                }
                float2 l = hudLight(sd, max(0.22 * ts, 0.5));
                float3 yellow = float3(1.0, 214.0 / 255.0, 110.0 / 255.0);
                return float4((yellow * l.x + yellow * 0.6 * l.y) * _FpanoHudB.z, 1);
            }
            ENDCG
        }
    }
}
