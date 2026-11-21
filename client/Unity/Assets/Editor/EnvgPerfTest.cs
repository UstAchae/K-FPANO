using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

// Offline timing (not shipped): the thermal chain at the size it runs at in the game (the user's crop at 32 px/deg,
// 966 x 768), on this machine's GPU. The whole chain, then each of its blits alone (its own inputs and blur settings),
// each run many times and waited for. Logged largest first.
public static class EnvgPerfTest
{
    const int W = 966, H = 768, Repeat = 40;

    static RenderTexture New(RenderTextureFormat f)
    {
        var rt = new RenderTexture(W, H, 0, f) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        rt.Create();
        return rt;
    }

    static RenderTexture NewHalf(RenderTextureFormat f)
    {
        var rt = new RenderTexture(W / 2, H / 2, 0, f) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        rt.Create();
        return rt;
    }

    static RenderTexture Upload(Color[] px, RenderTextureFormat f)
    {
        var tex = new Texture2D(W, H, TextureFormat.RGBAFloat, false, true);
        tex.SetPixels(px);
        tex.Apply();
        var rt = New(f);
        Graphics.Blit(tex, rt);
        Object.DestroyImmediate(tex);
        return rt;
    }

    static void Wait(RenderTexture rt) => UnityEngine.Rendering.AsyncGPUReadback.Request(rt, 0, 0, 1, 0, 1, 0, 1).WaitForCompletion();

    static float[] Read(RenderTexture rt, int channel)
    {
        var was = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        RenderTexture.active = was;
        Color[] c = tex.GetPixels();
        Object.DestroyImmediate(tex);
        var r = new float[c.Length];
        for (int i = 0; i < c.Length; i++) r[i] = c[i][channel];
        return r;
    }

    // the chain with the full-size wide blurs and with the half-size ones: their time and how far their results differ
    public static void Compare()
    {
        var names = new[] { "NormalsSoft.a", "AlbedoSoft.g", "LitSoft.g", "HeatBase.a", "LocalMoments.r", "Density.r", "Glow.r", "HistoryB.r", "Ridge.r (lines)" };
        float[][]? full = null;
        foreach (bool half in new[] { false, true })
        {
            EnvgChain.HalfWide = half;
            Run();
            var got = new[] { Read(_t!.NormalsSoft!, 3), Read(_t.AlbedoSoft!, 1), Read(_t.LitSoft!, 1), Read(_t.HeatBase!, 3), Read(_t.LocalMoments!, 0), Read(_t.Density!, 0), Read(_t.Glow!, 0), Read(_t.HistoryB!, 0), Read(_t.Ridge!, 0) };
            if (full == null) { full = got; continue; }
            var line = new System.Text.StringBuilder("ENVG_PERF_COMPARE half-size wide blurs against full-size:");
            for (int k = 0; k < names.Length; k++)
            {
                double sum = 0, level = 0, worst = 0;
                for (int i = 0; i < got[k].Length; i++)
                {
                    double d = System.Math.Abs(got[k][i] - full[k][i]);
                    sum += d; level += System.Math.Abs(full[k][i]); worst = System.Math.Max(worst, d);
                }
                line.Append("\n  " + names[k] + ": mean difference " + (sum / System.Math.Max(level, 1e-9) * 100.0).ToString("F2") + "% of its level, worst " + worst.ToString("F4"));
            }
            int linesFull = 0, linesHalf = 0, both = 0;
            for (int i = 0; i < got[8].Length; i++)
            {
                bool a = full[8][i] > 0.02f, b = got[8][i] > 0.02f;
                if (a) linesFull++; if (b) linesHalf++; if (a && b) both++;
            }
            line.Append("\n  line pixels: full " + linesFull + ", half " + linesHalf + ", the same " + both);
            UnityEngine.Debug.Log(line.ToString());
        }
        EnvgChain.HalfWide = true;
    }

    static EnvgChainInput? _t;

    public static void Run()
    {
        var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/EnvgOutline.shader");
        var m = new Material(shader);
        var rnd = new System.Random(1);
        var depth = new Color[W * H]; var normals = new Color[W * H]; var albedo = new Color[W * H]; var spec = new Color[W * H]; var pic = new Color[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                bool box = (x / 120 + y / 90) % 3 == 0;
                float n = (float)rnd.NextDouble();
                depth[i] = new Color(box ? 2f + n * 0.01f : 4f + x * 0.01f, 0f, 0f, 1f);
                normals[i] = new Color(0.5f + 0.1f * n, 0.5f, 1f, 1f);
                albedo[i] = new Color(0.3f + 0.4f * n, 0.4f, 0.4f, 1f);
                spec[i] = new Color(0.04f, 0.04f, 0.04f, 0.3f);
                float lit = 0.2f + (box ? 0.3f : 0f) + 0.05f * n;
                pic[i] = new Color(lit, lit, lit, 1f);
            }
        var t = new EnvgChainInput
        {
            Normals = Upload(normals, RenderTextureFormat.ARGB32), Albedo = Upload(albedo, RenderTextureFormat.ARGB32), Specular = Upload(spec, RenderTextureFormat.ARGB32),
            Depth = Upload(depth, RenderTextureFormat.RGFloat), Picture = Upload(pic, RenderTextureFormat.ARGBHalf), Body = New(RenderTextureFormat.RHalf),
            Heat2x = New(RenderTextureFormat.ARGBHalf), Seams = New(RenderTextureFormat.ARGBHalf), GeoSource = New(RenderTextureFormat.ARGBFloat), GeoAcross = New(RenderTextureFormat.ARGBFloat),
            GeoNear = New(RenderTextureFormat.ARGBFloat), GeoFar = New(RenderTextureFormat.ARGBFloat), NormalsSoft = New(RenderTextureFormat.ARGBFloat), HeatBase = New(RenderTextureFormat.ARGBHalf),
            GrassMask = New(RenderTextureFormat.RHalf), Foliage = New(RenderTextureFormat.ARGBHalf), HistoryB = New(RenderTextureFormat.ARGBHalf), Blurred = New(RenderTextureFormat.ARGBHalf),
            Surround = New(RenderTextureFormat.ARGBHalf), Across = New(RenderTextureFormat.ARGBHalf), Edges = New(RenderTextureFormat.RHalf), Presence = New(RenderTextureFormat.ARGBHalf),
            Density = NewHalf(RenderTextureFormat.ARGBHalf), Glow = NewHalf(RenderTextureFormat.ARGBHalf), LocalSquare = New(RenderTextureFormat.ARGBFloat), LocalMoments = NewHalf(RenderTextureFormat.ARGBFloat),
            Ridge = New(RenderTextureFormat.ARGBHalf), LitSoft = New(RenderTextureFormat.ARGBHalf), AlbedoSoft = New(RenderTextureFormat.ARGBHalf),
            Motion = New(RenderTextureFormat.RGHalf), EdgeHistA = New(RenderTextureFormat.RHalf), EdgeHistB = New(RenderTextureFormat.RHalf),
            Width = W, Height = H, Oversample = 1, FromMain = true, MotionReady = true, Temporal = 0.8f, HeatTemporal = 0.8f,
            Tan = new Vector2(0.7f, 0.5f), CamToWorld = Matrix4x4.identity, DeltaTime = 1f / 60f,
            Amount = 1f, Softness = 0.4f, Brightness = 1f, GlowAmount = 0.6f, ResidualHeat = 4f, BodyHeat = 1f, GroundPatches = 2f, Air = 15f,
            NativeMethod = true, NativeGrass = true, GrassGain = 2f,
            LightHeating = 15f, Daylight = 0f, SkyCooling = 4f, Smoothing = 1f, HeatEdges = 1f, GeometryEdges = 0f, LocalContrast = 0.35f, ShapeNeedsHeat = 1f, MinSpread = 3f,
        };
        _t = t;
        Shader.SetGlobalVector("_EnvgCrop", Vector4.zero);
        Shader.SetGlobalVector("_EnvgCropPrev", Vector4.zero);
        Shader.SetGlobalVector("_EnvgRpOn", new Vector4(1f, 0f, 0f, 0f));
        Shader.SetGlobalMatrix("_EnvgRpCamToWorld", Matrix4x4.identity);
        Shader.SetGlobalMatrix("_EnvgRpPrevWorldToCam", Matrix4x4.identity);
        Shader.SetGlobalVector("_EnvgRpTan", new Vector4(0.7f, 0.5f, 0.7f, 0.5f));
        Shader.SetGlobalTexture("_EnvgRpMotion", t.Motion);
        Shader.SetGlobalTexture("_EnvgRpBody", t.Body);
        EnvgChain.Release();
        EnvgChain.SetLook(m, t);
        EnvgChain.ResetHistory();

        // the whole chain, warmed up, then timed
        double whole = 0;
        var sw = new Stopwatch();
        for (int f = 0; f < 10 + Repeat; f++)
        {
            t.Frame = f;
            var cb = new CommandBuffer();
            EnvgChain.Fill(cb, m, t);
            Wait(t.Ridge!);
            sw.Restart();
            Graphics.ExecuteCommandBuffer(cb);
            Wait(t.Ridge!);
            sw.Stop();
            if (f >= 10) whole += sw.Elapsed.TotalMilliseconds;
            cb.Release();
        }
        whole /= Repeat;

        // each blit alone, with what it ran with
        EnvgChain.Steps = new System.Collections.Generic.List<EnvgChain.StepNote>();
        var noted = new CommandBuffer();
        t.Frame = 10 + Repeat;
        EnvgChain.Fill(noted, m, t);
        Graphics.ExecuteCommandBuffer(noted);
        Wait(t.Ridge!);
        noted.Release();
        var steps = EnvgChain.Steps;
        EnvgChain.Steps = null;
        var ms = new double[steps.Count];
        for (int s = 0; s < steps.Count; s++)
        {
            EnvgChain.StepNote n = steps[s];
            Shader.SetGlobalVector("_EnvgWide", n.Wide);
            Shader.SetGlobalVector("_EnvgBlur", n.Blur);
            Wait(n.To);
            sw.Restart();
            for (int r = 0; r < Repeat; r++)
            {
                if (n.Pass >= 0) Graphics.Blit(n.From, n.To, m, n.Pass); else Graphics.Blit(n.From, n.To);
            }
            Wait(n.To);
            sw.Stop();
            ms[s] = sw.Elapsed.TotalMilliseconds / Repeat;
        }
        double sum = 0;
        foreach (double v in ms) sum += v;
        var order = new int[steps.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        System.Array.Sort(order, (a, b) => ms[b].CompareTo(ms[a]));
        var line = new System.Text.StringBuilder("ENVG_PERF " + W + "x" + H + (EnvgChain.HalfWide ? " half-size wide blurs" : " full-size wide blurs") + ": whole chain " + whole.ToString("F2") + " ms; " + steps.Count + " blits alone add to " + sum.ToString("F2") + " ms; largest:");
        foreach (int i in order)
        {
            EnvgChain.StepNote n = steps[i];
            line.Append("\n  " + ms[i].ToString("F3") + " ms  #" + i + " pass " + n.Pass + "  " + n.From.name + " (" + n.From.width + "x" + n.From.height + ") -> " + n.To.name + " (" + n.To.format + ")"
                + (n.Pass == 33 || n.Pass == 34 ? "  radius " + n.Wide.y : n.Pass == 2 || n.Pass == 29 ? "  step " + (n.Blur.x > 0 ? n.Blur.x * W : n.Blur.y * H).ToString("F2") : ""));
        }
        UnityEngine.Debug.Log(line.ToString());
        var settle = new CommandBuffer();
        t.Frame = 11 + Repeat;
        EnvgChain.Fill(settle, m, t);
        Graphics.ExecuteCommandBuffer(settle);
        Wait(t.Ridge!);
        settle.Release();
    }
}
