#nullable enable
using UnityEngine;
using UnityEngine.Rendering;

// The long-wave picture and its outlines, from one frame's scene data (lit picture, deferred buffers, depth, the body
// mask), as a command buffer of the outline shader's passes. Nothing here knows the game: the mod fills it from the
// main camera each frame, and the offline preview in the Unity project (Assets/Editor/EnvgPreview.cs) fills it from a
// dumped frame. The same file is compiled into both, so what the preview shows is what the game shows.
internal sealed class EnvgChainInput
{
    // the scene, at the processing size
    public RenderTexture? Picture, Normals, Depth, Albedo, Specular, Body;
    // for the own-camera path only (forward-drawn things merged into the deferred buffers)
    public RenderTexture? FullDepth, NormalsFwd, AlbedoFwd, DepthFwd;
    // working textures, at the processing size
    public RenderTexture? HeatBase, GrassMask;
    public RenderTexture? Heat2x, Seams, GeoSource, GeoAcross, GeoNear, GeoFar, NormalsSoft, Foliage, HistoryB, Blurred, Surround, Across, Edges, Presence,
        Density, Glow, LocalSquare, LocalMoments, Ridge, LitSoft, AlbedoSoft;
    // temporal smoothing (the game's motion vectors and the edge strength followed in time)
    public RenderTexture? Motion, EdgeHistA, EdgeHistB;
    public bool FromMain = true, MotionReady;
    public int Width, Height, Oversample = 1;
    public Matrix4x4 CamToWorld = Matrix4x4.identity;
    public Vector2 Tan = Vector2.one;
    public Vector4 ZBuffer;
    public float DeltaTime = 1f / 60f;

    // settings (as the config sets them)
    public float Amount = 0.5f, Softness = 0.4f, Brightness = 1f, GlowAmount = 0.6f;
    public float ResidualHeat = 4f, BodyHeat = 1f, GroundPatches = 1.5f, Air = 15f, LightHeating = 15f, Daylight, SkyCooling = 4f;
    public bool NativeMethod, NativeGrass;
    public float GrassGain = 2f, RoadWarm = 2f;
    public float MinSpread = 3f;   // °C
    public float FoliageStepLo = 0.12f, FoliageStepHi = 0.3f;
    public float LineBase = 0.4f, LineHot = 1.2f, LineHotAbove = 12f;
    public float Smoothing = 1f, HeatEdges = 1f, GeometryEdges = 1f, LocalContrast = 1f, ShapeNeedsHeat = 1f, Temporal = 0.8f, Declutter = 1f;
    public float HeatTemporal = 0.8f;   // the heat picture's own history share (pass 36)
    public int Frame = -1;              // the frame's number (a gap starts the history anew); -1 unknown
}

internal static class EnvgChain
{
    private static readonly int ParamsId = Shader.PropertyToID("_EnvgParams");
    private static readonly int CoreId = Shader.PropertyToID("_EnvgCore");
    private static readonly int HaloId = Shader.PropertyToID("_EnvgHalo");
    private static readonly int LookId = Shader.PropertyToID("_EnvgLook");
    private static readonly int BlurId = Shader.PropertyToID("_EnvgBlur");
    private static readonly int NormalsId = Shader.PropertyToID("_EnvgNormals");
    private static readonly int AlbedoId = Shader.PropertyToID("_EnvgAlbedo");
    private static readonly int SpecularId = Shader.PropertyToID("_EnvgSpecular");
    private static readonly int DepthId = Shader.PropertyToID("_EnvgDepth");
    private static readonly int BodyId = Shader.PropertyToID("_EnvgBody");
    private static readonly int CamToWorldId = Shader.PropertyToID("_EnvgCamToWorld");
    private static readonly int TanId = Shader.PropertyToID("_EnvgTan");
    private static readonly int FullDepthId = Shader.PropertyToID("_EnvgFullDepth");
    private static readonly int ZBufferId = Shader.PropertyToID("_EnvgZBuffer");
    private static readonly int ThermoId = Shader.PropertyToID("_EnvgThermo");
    private static readonly int GrassGainId = Shader.PropertyToID("_EnvgGrassGain");
    private static readonly int GrassMaskId = Shader.PropertyToID("_EnvgGrassMask");
    private static readonly int Thermo2Id = Shader.PropertyToID("_EnvgThermo2");
    private static readonly int GeoNearId = Shader.PropertyToID("_EnvgGeoNear");
    private static readonly int GeoFarId = Shader.PropertyToID("_EnvgGeoFar");
    private static readonly int NormalsSoftId = Shader.PropertyToID("_EnvgNormalsSoft");
    private static readonly int FoliageId = Shader.PropertyToID("_EnvgFoliage");
    private static readonly int FoliageWideId = Shader.PropertyToID("_EnvgFoliageWide");
    private static RenderTexture? _foliageWide, _foliageWideAcross;
    private static readonly int FoliageStepId = Shader.PropertyToID("_EnvgFoliageStep");
    private static readonly int LitSoftId = Shader.PropertyToID("_EnvgLitSoft");
    private static readonly int AlbedoSoftId = Shader.PropertyToID("_EnvgAlbedoSoft");
    private static readonly int ReduceId = Shader.PropertyToID("_EnvgReduce");
    private static readonly int HistoryId = Shader.PropertyToID("_EnvgHistory");
    private static readonly int LagId = Shader.PropertyToID("_EnvgLag");
    private static readonly int AgcId = Shader.PropertyToID("_EnvgAgc");
    private static readonly int SurroundId = Shader.PropertyToID("_EnvgSurround");
    private static readonly int LocalId = Shader.PropertyToID("_EnvgLocal");
    private static readonly int GeometryId = Shader.PropertyToID("_EnvgGeometry");
    private static readonly int MixId = Shader.PropertyToID("_EnvgMix");
    private static readonly int GainLimitId = Shader.PropertyToID("_EnvgGainLimit");
    private static readonly int MotionId = Shader.PropertyToID("_EnvgMotion");
    private static readonly int EdgeHistoryId = Shader.PropertyToID("_EnvgEdgeHistory");
    private static readonly int TemporalId = Shader.PropertyToID("_EnvgTemporal");
    private static readonly int GlowId = Shader.PropertyToID("_EnvgGlow");
    private static readonly int DensityId = Shader.PropertyToID("_EnvgDensity");
    private static readonly int DeclutterId = Shader.PropertyToID("_EnvgDeclutter");

    private static RenderTexture? _agcCoarse, _agcMean, _agcA, _agcB;
    private static bool _edgeFlip, _edgeHistoryValid;

    // the thermal core's gain control (mean and mean square of the picture, followed over a second)
    internal static RenderTexture? Agc => _agcB;

    internal static void ResetHistory()
    {
        _edgeHistoryValid = false;
        _heatHistoryValid = false;
        _depthPrevValid = false;
    }

    // the heat picture's own history (pass 36), ping-ponged
    private static RenderTexture? _heatHistA, _heatHistB;
    private static bool _heatFlip, _heatHistoryValid;
    // last frame's depth, for telling where last frame saw the same surface (the heat's history kept whole there)
    private static RenderTexture? _depthPrev;
    private static bool _depthPrevValid;
    private static readonly int DepthPrevId = Shader.PropertyToID("_EnvgDepthPrev");
    // how many frames the heat's history holds, per pixel (pass 38), ping-pong with the history
    private static RenderTexture? _countA, _countB;
    private static int _lastFrame = -1;
    private static readonly int HeatCountId = Shader.PropertyToID("_EnvgHeatCount"), HistSizeId = Shader.PropertyToID("_EnvgHistSize");
    private static readonly int HeatHistoryId = Shader.PropertyToID("_EnvgHeatHistory");
    private static readonly int HeatTemporalId = Shader.PropertyToID("_EnvgHeatTemporal");

    // ---- plateau histogram equalization of the fused display (the base layer's temperatures) ----
    internal const int HeWidth = 160, HeHeight = 90, HeBins = 256;
    private static RenderTexture? _heSmall, _heHalf, _heQuarter;
    internal static RenderTexture? HeSmall;
    private static Texture2D? _heLut;
    private static readonly float[] _heLevels = new float[HeBins], _heTarget = new float[HeBins];
    private static readonly int[] _heCount = new int[HeBins], _heFine = new int[1024];
    private static float _heLo = 0.1f, _heHi = 0.2f;
    private static bool _heValid;
    private static readonly int HeLutId = Shader.PropertyToID("_EnvgHeLut");
    private static readonly int HeRangeId = Shader.PropertyToID("_EnvgHeRange");

    // From the small picture's temperatures: the range (1st to 99th percentile, at least minSpan wide), a histogram over
    // it with each bin held to a plateau (a large even area, the sky or a wall, can't take the whole display), its
    // cumulative sum as the display level, mixed a quarter with a straight ramp; followed over half a second.
    internal static void BuildLut(Unity.Collections.NativeArray<float> values, float minSpanC, float dt, bool instant)
    {
        int n = values.Length;
        if (n == 0)
            return;
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            float v = values[i];
            if (float.IsNaN(v) || float.IsInfinity(v))
                continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }
        if (!(max > min))
            max = min + 0.01f;
        System.Array.Clear(_heFine, 0, _heFine.Length);
        float fineScale = (_heFine.Length - 1) / (max - min);
        for (int i = 0; i < n; i++)
        {
            float v = values[i];
            if (float.IsNaN(v) || float.IsInfinity(v))
                continue;
            _heFine[Mathf.Clamp((int)((v - min) * fineScale), 0, _heFine.Length - 1)]++;
        }
        int lowCount = n / 100, highCount = n - n / 100, acc = 0;
        float lo = min, hi = max;
        bool loSet = false;
        for (int b = 0; b < _heFine.Length; b++)
        {
            acc += _heFine[b];
            if (!loSet && acc >= lowCount) { lo = min + b / fineScale; loSet = true; }
            if (acc >= highCount) { hi = min + (b + 1) / fineScale; break; }
        }
        float minSpan = Mathf.Max(minSpanC, 0.1f) / 100f;
        if (hi - lo < minSpan)
        {
            float mid = 0.5f * (hi + lo);
            lo = mid - 0.5f * minSpan;
            hi = mid + 0.5f * minSpan;
        }
        System.Array.Clear(_heCount, 0, HeBins);
        float scale = HeBins / (hi - lo);
        int inRange = 0;
        for (int i = 0; i < n; i++)
        {
            float v = values[i];
            if (float.IsNaN(v) || float.IsInfinity(v))
                continue;
            _heCount[Mathf.Clamp((int)((v - lo) * scale), 0, HeBins - 1)]++;
            inRange++;
        }
        int plateau = Mathf.Max(1, 2 * inRange / HeBins);
        float total = 0f;
        for (int b = 0; b < HeBins; b++)
            total += Mathf.Min(_heCount[b], plateau);
        float run = 0f;
        for (int b = 0; b < HeBins; b++)
        {
            float c = Mathf.Min(_heCount[b], plateau);
            float level = (run + 0.5f * c) / Mathf.Max(total, 1f);
            run += c;
            _heTarget[b] = 0.75f * level + 0.25f * (b + 0.5f) / HeBins;
        }
        // followed over two seconds, as a real core's gain moves; the range only once it is off by more than 5 percent
        // of its span (a still scene's statistics wander a little from frame to frame and must not move the display)
        float k = instant || !_heValid ? 1f : 1f - Mathf.Exp(-Mathf.Max(dt, 0f) / 2f);
        float dead = 0.05f * Mathf.Max(_heHi - _heLo, 1e-5f);
        if (instant || !_heValid || Mathf.Abs(lo - _heLo) > dead || Mathf.Abs(hi - _heHi) > dead)
        {
            _heLo += (lo - _heLo) * k;
            _heHi += (hi - _heHi) * k;
        }
        for (int b = 0; b < HeBins; b++)
            _heLevels[b] += (_heTarget[b] - _heLevels[b]) * k;
        _heValid = true;
        if (_heLut == null)
            _heLut = new Texture2D(HeBins, 1, TextureFormat.RFloat, false, true) { name = "Justachae F-PANO equalization", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        _heLut.SetPixelData(_heLevels, 0);
        _heLut.Apply(false, false);
        Shader.SetGlobalTexture(HeLutId, _heLut);
        Shader.SetGlobalVector(HeRangeId, new Vector4(_heLo, 1f / Mathf.Max(_heHi - _heLo, 1e-5f), 0f, 0f));
    }

    internal static void Release()
    {
        Free(ref _heSmall);
        Free(ref _heatHistA);
        Free(ref _heatHistB);
        Free(ref _heHalf);
        Free(ref _heQuarter);
        Free(ref _foliageWide);
        Free(ref _foliageWideAcross);
        Free(ref _depthHalf);
        foreach (RenderTexture[] pair in _halves.Values)
            for (int i = 0; i < pair.Length; i++)
            {
                RenderTexture? each = pair[i];
                Free(ref each);
            }
        _halves.Clear();
        HeSmall = null;
        _heValid = false;
        Free(ref _agcCoarse);
        Free(ref _agcMean);
        Free(ref _agcA);
        Free(ref _agcB);
        _edgeHistoryValid = false;
    }

    // the lines' look: threshold, softness, brightness, glow, and the copper of the Army's F-PANO footage (a near-white
    // core and an orange-red glow)
    internal static void SetLook(Material material, EnvgChainInput t)
    {
        material.SetVector(ParamsId, new Vector4(0.9f - 0.8f * t.Amount, t.Softness, t.Brightness, t.GlowAmount));
        material.SetColor(CoreId, new Color(1f, 0.86f, 0.66f));
        material.SetColor(HaloId, new Color(1f, 0.42f, 0.12f));
        material.SetVector(LookId, new Vector4(t.LineBase, t.LineHot, t.LineHotAbove, 0f));
    }

    // (a measuring build only: GPU samplers around the chain's groups, set by FpanoCost; null otherwise)
    internal static UnityEngine.Profiling.CustomSampler?[]? Samplers;
    internal static void Mark(CommandBuffer cb, int group, bool begin)
    {
        var s = Samplers;
        if (s == null || group >= s.Length || s[group] == null)
            return;
        if (begin) cb.BeginSample(s[group]); else cb.EndSample(s[group]);
    }

    internal static void Fill(CommandBuffer cb, Material m, EnvgChainInput t)
    {
        Mark(cb, 1, true);
        int fineWidth = t.Width * t.Oversample, fineHeight = t.Height * t.Oversample;
        // our own long-wave picture from the deferred buffers: the lit picture, diffuse and specular colour, normals, depth
        cb.SetGlobalTexture(NormalsId, t.Normals);
        cb.SetGlobalTexture(AlbedoId, t.Albedo);
        cb.SetGlobalTexture(SpecularId, t.Specular);
        cb.SetGlobalTexture(DepthId, t.Depth);
        if (t.Body != null)
            cb.SetGlobalTexture(BodyId, t.Body);
        cb.SetGlobalMatrix(CamToWorldId, t.CamToWorld);
        cb.SetGlobalVector(TanId, new Vector4(t.Tan.x, t.Tan.y, t.Tan.x, t.Tan.y));
        // (own camera) what was drawn forward joins the deferred buffers: its depth, a normal from it, the vegetation mark
        if (!t.FromMain && t.FullDepth != null && t.NormalsFwd != null && t.AlbedoFwd != null && t.DepthFwd != null)
        {
            cb.SetGlobalTexture(FullDepthId, t.FullDepth);
            cb.SetGlobalVector(ZBufferId, t.ZBuffer);
            Step(cb, t.Normals, t.NormalsFwd, m, 22);
            Step(cb, t.Albedo, t.AlbedoFwd, m, 23);
            Step(cb, t.Depth, t.DepthFwd, m, 24);
            cb.SetGlobalTexture(NormalsId, t.NormalsFwd);
            cb.SetGlobalTexture(AlbedoId, t.AlbedoFwd);
            cb.SetGlobalTexture(DepthId, t.DepthFwd);
        }
        // the depth at half the size, for the wide blurs over one surface (Blur)
        RenderTexture? depthNow = !t.FromMain && t.DepthFwd != null ? t.DepthFwd : t.Depth;
        if (depthNow != null && fineWidth >= 32 && fineHeight >= 32)
        {
            Ensure(ref _depthHalf, fineWidth / 2, fineHeight / 2, RenderTextureFormat.RGFloat, "Justachae thermal depth, half size");
            Step(cb, depthNow, _depthHalf, m, 39);
            cb.SetGlobalTexture(DepthHalfId, _depthHalf);
        }
        if (t.GrassMask != null)
            cb.SetGlobalTexture(GrassMaskId, t.GrassMask);
        cb.SetGlobalVector(GrassGainId, new Vector4(t.GrassGain, t.RoadWarm, 0f, 0f));
        cb.SetGlobalVector(Thermo2Id, new Vector4(t.ResidualHeat, t.FromMain ? t.BodyHeat : 0f, t.GroundPatches, t.NativeMethod ? (t.NativeGrass ? 2f : 1f) : 0f));
        // only the sun heats surfaces enough to show (lamps and torches don't): the light's heating is scaled by daylight
        cb.SetGlobalVector(ThermoId, new Vector4(t.Air, t.LightHeating * t.Daylight, t.SkyCooling, 800f));
        // the geometric source (normals, inverse depth) smoothed by one and by two pixels, for smooth geometric edges
        // and for the temperature (surface orientation without the textures' bumps); in full floats
        Step(cb, t.Picture, t.GeoSource, m, 18);
        Blur(cb, m, t.GeoSource!, t.GeoNear!, 0.5f, fineWidth, fineHeight, t.GeoAcross!);
        Blur(cb, m, t.GeoNear!, t.GeoFar!, 0.8660254f, fineWidth, fineHeight, t.GeoAcross!);
        cb.SetGlobalTexture(GeoNearId, t.GeoNear);
        cb.SetGlobalTexture(GeoFarId, t.GeoFar);
        // and over several pixels, for how much of the sky a surface sees (a crown as one mass, not its leaf cards)
        if (t.NormalsSoft != null)
        {
            Blur(cb, m, t.GeoFar!, t.NormalsSoft, 4f * t.Oversample, fineWidth, fineHeight, t.GeoAcross!, true);
            cb.SetGlobalTexture(NormalsSoftId, t.NormalsSoft);
        }
        else
            cb.SetGlobalTexture(NormalsSoftId, t.GeoFar);
        // the light absorbed is averaged over a few pixels (heat spreads sideways in a wall): no pattern of paint shows
        Blur(cb, m, t.Picture!, t.LitSoft!, 3f, fineWidth, fineHeight, t.Across!, true);
        cb.SetGlobalTexture(LitSoftId, t.LitSoft);
        Blur(cb, m, t.Albedo!, t.AlbedoSoft!, 5f * t.Oversample, fineWidth, fineHeight, t.Across!, true);
        cb.SetGlobalTexture(AlbedoSoftId, t.AlbedoSoft);
        // foliage: where rough surfaces crowd (a crown, a bush), over about five pixels
        if (t.Foliage != null)
        {
            Step(cb, t.Picture, t.Foliage, m, 28);
            Blur(cb, m, t.Foliage, t.Foliage, 4f * t.Oversample, fineWidth, fineHeight, t.Across!);
            cb.SetGlobalTexture(FoliageId, t.Foliage);
            // and over a wide area, at a quarter of the size (a road against the gaps between tufts)
            int qw = Mathf.Max(fineWidth / 4, 8), qh = Mathf.Max(fineHeight / 4, 8);
            Ensure(ref _foliageWide, qw, qh, RenderTextureFormat.RHalf, "Justachae F-PANO foliage, wide");
            Ensure(ref _foliageWideAcross, qw, qh, RenderTextureFormat.RHalf, "Justachae F-PANO foliage, wide across");
            // (the ground's cover only: a tree's crown must not make the ground round its foot count as grass)
            Step(cb, t.Foliage, _foliageWide, m, 31);
            Blur(cb, m, _foliageWide!, _foliageWide!, 3f, qw, qh, _foliageWideAcross!);
            cb.SetGlobalTexture(FoliageWideId, _foliageWide);
        }
        Mark(cb, 1, false);
        Mark(cb, 2, true);
        Step(cb, t.Picture, t.Heat2x, m, 14);
        // seams between meshes that see no surface are filled from the surfaces around; the geometric edges into g
        Step(cb, t.Heat2x, t.Seams, m, 16);
        Step(cb, t.Seams, t.HistoryB);
        // followed in time (the jittered copy's edges settle, as the game's TAA settles its picture)
        // (a gap in the frames, the thermal picture off for a while, leaves the history stale: it starts anew)
        if (t.Frame >= 0 && _lastFrame >= 0 && t.Frame != _lastFrame + 1)
            ResetHistory();
        _lastFrame = t.Frame;
        Mark(cb, 2, false);
        Mark(cb, 3, true);
        if (t.FromMain && t.MotionReady && t.HeatTemporal > 0f && t.Motion != null && t.HistoryB != null)
        {
            // (made anew at another size, the history and its count hold nothing yet: the history starts anew)
            RenderTexture? wasA = _heatHistA, wasB = _heatHistB, wasCa = _countA, wasCb = _countB;
            Ensure(ref _heatHistA, t.HistoryB.width, t.HistoryB.height, RenderTextureFormat.ARGBHalf, "Justachae F-PANO heat, followed A");
            Ensure(ref _heatHistB, t.HistoryB.width, t.HistoryB.height, RenderTextureFormat.ARGBHalf, "Justachae F-PANO heat, followed B");
            Ensure(ref _countA, t.HistoryB.width, t.HistoryB.height, RenderTextureFormat.RHalf, "Justachae F-PANO heat, frames held A");
            Ensure(ref _countB, t.HistoryB.width, t.HistoryB.height, RenderTextureFormat.RHalf, "Justachae F-PANO heat, frames held B");
            if (wasA != _heatHistA || wasB != _heatHistB || wasCa != _countA || wasCb != _countB)
                _heatHistoryValid = false;
            RenderTexture previous = _heatFlip ? _heatHistA! : _heatHistB!;
            RenderTexture next = _heatFlip ? _heatHistB! : _heatHistA!;
            _heatFlip = !_heatFlip;
            cb.SetGlobalTexture(MotionId, t.Motion);
            cb.SetGlobalTexture(HeatHistoryId, previous);
            RenderTexture countThen = _heatFlip ? _countB! : _countA!, countNow = _heatFlip ? _countA! : _countB!;
            cb.SetGlobalTexture(HeatCountId, countThen);
            cb.SetGlobalVector(HistSizeId, new Vector4(1f / t.HistoryB.width, 1f / t.HistoryB.height, t.HistoryB.width, t.HistoryB.height));
            bool depthPrevOk = _depthPrevValid && _depthPrev != null && t.Depth != null && _depthPrev.width == t.Depth.width && _depthPrev.height == t.Depth.height;
            if (depthPrevOk)
                cb.SetGlobalTexture(DepthPrevId, _depthPrev);
            cb.SetGlobalVector(HeatTemporalId, new Vector4(_heatHistoryValid ? t.HeatTemporal : 0f, _heatHistoryValid && depthPrevOk ? 1f : 0f, 0f, 0f));
            Step(cb, t.HistoryB, next, m, 36);
            Step(cb, t.HistoryB, countNow, m, 38);
            Step(cb, next, t.HistoryB);
            _heatHistoryValid = true;
        }
        else
            _heatHistoryValid = false;
        Mark(cb, 3, false);
        Mark(cb, 4, true);
        // the base layer for the fused display: the heat averaged over one surface (about three pixels), into alpha
        if (t.HeatBase != null)
        {
            Blur(cb, m, t.HistoryB, t.HeatBase, 3f * t.Oversample, fineWidth, fineHeight, t.Across!, true);
            Step(cb, t.HeatBase, t.HistoryB, m, 30);
            // and a small copy of the base for the histogram (read back to the CPU)
            HeSmall = Ensure(ref _heSmall, HeWidth, HeHeight, RenderTextureFormat.RFloat, "Justachae F-PANO histogram source");
            // averaged down in halving steps (a plain copy picked every few pixels, and as the view swayed the picked
            // ones changed: the percentiles and the whole display breathed)
            RenderTexture heHalf = Ensure(ref _heHalf, Mathf.Max(HeWidth * 2, 2), Mathf.Max(HeHeight * 2, 2), RenderTextureFormat.RFloat, "Justachae F-PANO histogram source, half");
            RenderTexture heQuarter = Ensure(ref _heQuarter, Mathf.Max(HeWidth * 4, 4), Mathf.Max(HeHeight * 4, 4), RenderTextureFormat.RFloat, "Justachae F-PANO histogram source, quarter");
            Step(cb, t.HeatBase, heQuarter);
            Step(cb, heQuarter, heHalf);
            Step(cb, heHalf, _heSmall);
        }
        // the gain control: the picture's mean and spread over the surfaces only (the cold sky would set the scale and
        // flatten everything on the ground), followed over a second
        RenderTexture agcCoarse = Ensure(ref _agcCoarse, 32, 18, RenderTextureFormat.ARGBFloat, "Justachae F-PANO AGC 32");
        RenderTexture agcMean = Ensure(ref _agcMean, 1, 1, RenderTextureFormat.RGFloat, "Justachae F-PANO AGC");
        bool agcFresh = _agcA == null;
        Ensure(ref _agcA, 1, 1, RenderTextureFormat.RGFloat, "Justachae F-PANO AGC A");
        Ensure(ref _agcB, 1, 1, RenderTextureFormat.RGFloat, "Justachae F-PANO AGC B");
        (_agcA, _agcB) = (_agcB, _agcA);
        cb.SetGlobalVector(ReduceId, new Vector4(4f, 1f / 32f, 1f / 18f, 3f));
        Step(cb, t.HistoryB, agcCoarse, m, 9);
        cb.SetGlobalVector(ReduceId, new Vector4(8f, 1f, 1f, 2f));
        Step(cb, agcCoarse, agcMean, m, 9);
        cb.SetGlobalTexture(HistoryId, _agcA);
        cb.SetGlobalVector(LagId, new Vector4(agcFresh ? 1f : 1f - Mathf.Exp(-Mathf.Max(t.DeltaTime, 0f) / 1f), 0f, 0f, 0f));
        Step(cb, agcMean, _agcB, m, 10);
        cb.SetGlobalTexture(AgcId, _agcB);
        Mark(cb, 4, false);
        Mark(cb, 5, true);
        // the lens's blur (radius in detector pixels, at least half of one) is the filter's centre; the surround is the
        // same at twice the radius
        float centre = Mathf.Max(t.Smoothing, 0.5f) * t.Oversample;
        Blur(cb, m, t.HistoryB!, t.Blurred!, centre * 0.5f, fineWidth, fineHeight, t.Across!);
        Blur(cb, m, t.Blurred!, t.Surround!, centre * 1.7320508f * 0.5f, fineWidth, fineHeight, t.Across!);
        cb.SetGlobalTexture(SurroundId, t.Surround);
        // the heat's local mean and mean square (about twelve processing pixels) for local contrast
        Step(cb, t.HistoryB, t.LocalSquare, m, 20);
        Blur(cb, m, t.LocalSquare!, t.LocalMoments!, 6f * t.Oversample, fineWidth, fineHeight, t.Across!);
        cb.SetGlobalTexture(LocalId, t.LocalMoments);
        // heat edges and geometric edges as one set of lines
        cb.SetGlobalTexture(GeometryId, t.HistoryB);
        cb.SetGlobalVector(GainLimitId, new Vector4(Mathf.Max(t.MinSpread, 0.05f) / 100f, 0f, 0f, 0f));
        cb.SetGlobalVector(FoliageStepId, new Vector4(t.FoliageStepLo, t.FoliageStepHi, 0f, 0f));
        cb.SetGlobalVector(MixId, new Vector4(t.HeatEdges, t.GeometryEdges, t.LocalContrast, t.ShapeNeedsHeat));
        Step(cb, t.Blurred, t.Edges, m, 11);
        // like the game's TAA: last frame's edge strength, brought to where things are now by the game's motion vectors
        // and held within this frame's neighbourhood, blended in before the threshold
        Mark(cb, 5, false);
        Mark(cb, 6, true);
        RenderTexture followed = t.Edges!;
        if (t.FromMain && t.MotionReady && t.Temporal > 0f && t.Motion != null && t.EdgeHistA != null && t.EdgeHistB != null)
        {
            RenderTexture previous = _edgeFlip ? t.EdgeHistA : t.EdgeHistB;
            RenderTexture next = _edgeFlip ? t.EdgeHistB : t.EdgeHistA;
            _edgeFlip = !_edgeFlip;
            cb.SetGlobalTexture(MotionId, t.Motion);
            cb.SetGlobalTexture(EdgeHistoryId, previous);
            cb.SetGlobalVector(TemporalId, new Vector4(_edgeHistoryValid ? t.Temporal : 0f, 0f, 0f, 0f));
            Step(cb, t.Edges, next, m, 27);
            _edgeHistoryValid = true;
            followed = next;
        }
        else
            _edgeHistoryValid = false;
        Mark(cb, 6, false);
        Mark(cb, 7, true);
        // each edge's centre line (one pixel wide, along the response's ridge), through the threshold
        Step(cb, followed, t.Ridge, m, 26);
        // how crowded the lines are around each point, and their glow
        Step(cb, t.Ridge, t.Presence);
        Blur(cb, m, t.Presence!, t.Density!, 3f * t.Oversample, fineWidth, fineHeight, t.Across!);
        Blur(cb, m, t.Presence!, t.Glow!, 2.5f * t.Oversample, fineWidth, fineHeight, t.Across!);
        cb.SetGlobalTexture(GlowId, t.Glow);
        cb.SetGlobalTexture(DensityId, t.Density);
        cb.SetGlobalVector(DeclutterId, new Vector4(t.Declutter, 0.07f, 0f, 0f));
        // this frame's depth kept for the next (the heat's history checked against it)
        if (t.Depth != null)
        {
            Ensure(ref _depthPrev, t.Depth.width, t.Depth.height, RenderTextureFormat.RGFloat, "Justachae F-PANO depth, last frame");
            // (written below this frame: valid from the next on)
            Step(cb, t.Depth, _depthPrev);
            _depthPrevValid = true;
        }
        Mark(cb, 7, false);
    }


    // Offline timing only (EnvgPerfTest): when Steps is set, each blit of the chain is noted with what it reads, writes
    // and the blur settings it runs with. Null in the game: nothing noted.
    internal struct StepNote { public Texture From; public RenderTexture To; public int Pass; public Vector4 Wide, Blur; }
    internal static System.Collections.Generic.List<StepNote>? Steps;
    private static Vector4 _wideNow, _blurNow;
    private static void Step(CommandBuffer cb, Texture? from, RenderTexture? to, Material? m = null, int pass = -1)
    {
        if (m != null) cb.Blit(from, to, m, pass); else cb.Blit(from, to);
        if (Steps != null && from != null && to != null)
            Steps.Add(new StepNote { From = from, To = to, Pass = m != null ? pass : -1, Wide = _wideNow, Blur = _blurNow });
    }

    // A gaussian of the given step (sigma about two steps). A wide one is made of several narrow ones (their variances
    // add): taps more than a pixel and a half apart left copies of every edge (a comb) instead of a smooth blur.
    private static readonly int WideId = Shader.PropertyToID("_EnvgWide");
    // A wide one (a step of 2.5 pixels or more: smooth over a dozen pixels and more) is worked out at half the size:
    // across on a half-size copy, then down into the full size, each pixel keeping its own value and, over one surface,
    // its own depth (pass 39 to 41). A quarter of the pixels and half the taps across, half the taps down: the chain's
    // largest cost (measured offline at the game's size: the wide blurs were three quarters of it).
    private static readonly int DepthHalfId = Shader.PropertyToID("_EnvgDepthHalf");
    private static readonly int WideOwnId = Shader.PropertyToID("_EnvgWideOwn");
    private static RenderTexture? _depthHalf;
    // the half-size work textures, a pair for each size the wide blurs run at (the sensor's, the wide foliage's): kept,
    // never made anew between the sizes
    private static readonly System.Collections.Generic.Dictionary<int, RenderTexture[]> _halves = new System.Collections.Generic.Dictionary<int, RenderTexture[]>();
    internal static bool HalfWide = true;   // (false only offline, to compare with the full-size blurs)
    internal static void Blur(CommandBuffer cb, Material m, RenderTexture from, RenderTexture to, float step, int width, int height, RenderTexture across, bool sameSurface = false)
    {
        int passes = Mathf.Clamp(Mathf.CeilToInt(step * step / 2.25f), 1, 8);
        int hw = width / 2, hh = height / 2;
        if (HalfWide && step >= 2.5f && hw >= 16 && hh >= 16 && (!sameSurface || _depthHalf != null && _depthHalf.width == hw && _depthHalf.height == hh))
        {
            int key = hw * 65536 + hh;
            if (!_halves.TryGetValue(key, out RenderTexture[] pair) || pair[0] == null || pair[1] == null)
            {
                RenderTexture? a = null, b = null;
                pair = new[] { Ensure(ref a, hw, hh, RenderTextureFormat.ARGBFloat, "Justachae thermal wide blur, half size"),
                               Ensure(ref b, hw, hh, RenderTextureFormat.ARGBFloat, "Justachae thermal wide blur, half size across") };
                _halves[key] = pair;
            }
            RenderTexture _halfSrc = pair[0], _halfMid = pair[1];
            float half = step * 0.5f;
            // (over one surface: one texel of each four, as it is; else their average)
            if (sameSurface) Step(cb, from, _halfSrc, m, 39); else Step(cb, from, _halfSrc);
            cb.SetGlobalVector(WideId, _wideNow = new Vector4(1.69f * half, Mathf.Ceil(3f * 1.69f * half), 1f / hw, 0f));
            Step(cb, _halfSrc, _halfMid, m, sameSurface ? 40 : 33);
            cb.SetGlobalVector(WideId, _wideNow = new Vector4(1.69f * half, Mathf.Ceil(3f * 1.69f * half), 0f, 1f / hh));
            if (sameSurface)
            {
                cb.SetGlobalTexture(WideOwnId, from);
                Step(cb, _halfMid, to, m, 41);
            }
            else
                Step(cb, _halfMid, to, m, 33);
            return;
        }
        if (passes > 1)
        {
            // as wide as those passes together (sigma 1.69 steps of each, added in squares: 1.69 x the step), in one
            cb.SetGlobalVector(WideId, _wideNow = new Vector4(1.69f * step, Mathf.Ceil(3f * 1.69f * step), 1f / width, 0f));
            Step(cb, from, across, m, sameSurface ? 34 : 33);
            cb.SetGlobalVector(WideId, _wideNow = new Vector4(1.69f * step, Mathf.Ceil(3f * 1.69f * step), 0f, 1f / height));
            Step(cb, across, to, m, sameSurface ? 34 : 33);
            return;
        }
        float each = step / Mathf.Sqrt(passes);
        int pass = sameSurface ? 29 : 2;
        RenderTexture source = from;
        for (int k = 0; k < passes; k++)
        {
            cb.SetGlobalVector(BlurId, _blurNow = new Vector4(each / width, 0f, 0f, 0f));
            Step(cb, source, across, m, pass);
            cb.SetGlobalVector(BlurId, _blurNow = new Vector4(0f, each / height, 0f, 0f));
            Step(cb, across, to, m, pass);
            source = to;
        }
    }

    private static RenderTexture Ensure(ref RenderTexture? texture, int width, int height, RenderTextureFormat format, string name)
    {
        if (texture == null || texture.width != width || texture.height != height || texture.format != format)
        {
            Free(ref texture);
            texture = new RenderTexture(width, height, 0, format) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            texture.Create();
        }
        return texture;
    }

    private static void Free(ref RenderTexture? texture)
    {
        if (texture != null)
        {
            texture.Release();
            if (Application.isPlaying) Object.Destroy(texture); else Object.DestroyImmediate(texture);
            texture = null;
        }
    }
}
