using UnityEngine;
using UnityEngine.Rendering;

// Offline check (not shipped): the chain on a still synthetic scene whose copied picture carries fresh noise every frame
// (as the game's jittered, dithered frame does). Each step's frame-to-frame change is measured as the in-game probe
// measures it; the heat's history must settle the heat (its change far below the raw heat's).
public static class EnvgTaaTest
{
    const int W = 256, H = 192;

    static RenderTexture New(RenderTextureFormat f)
    {
        var rt = new RenderTexture(W, H, 0, f) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
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

    static float[] ReadR(RenderTexture rt)
    {
        var was = RenderTexture.active;
        RenderTexture.active = rt;
        var t = new Texture2D(W, H, TextureFormat.RGBAFloat, false, true);
        t.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        RenderTexture.active = was;
        Color[] c = t.GetPixels();
        var r = new float[c.Length];
        for (int i = 0; i < c.Length; i++) r[i] = c[i].r;
        return r;
    }

    static bool PicNoise = true, AlbNoise = true;
    static int Frames = 300;
    static bool Step = true, NormNoise = false, Move = false;
    public static void Run()
    {
        foreach (var c in new[] { (false, true, false), (false, true, true) })
        {
            Step = c.Item1; NormNoise = c.Item2; Move = c.Item3; PicNoise = AlbNoise = true;
            RunOnce();
        }
    }
    static void RunOnce()
    {
        var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/EnvgOutline.shader");
        var m = new Material(shader);
        var rnd = new System.Random(1);
        // the scene: a wall at 4 m with a box at 2 m in the middle, lit with a few bands (edges to draw)
        var depth = new Color[W * H];
        var normals = new Color[W * H];
        var albedo = new Color[W * H];
        var spec = new Color[W * H];
        var basePic = new Color[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                bool box = x > 90 && x < 170 && y > 60 && y < 130;
                float d = box ? 2f : 4f;
                depth[i] = new Color(d, 0f, 0f, 1f);
                normals[i] = new Color(0.5f, 0.5f, 1f, 1f);
                albedo[i] = new Color(0.5f, 0.5f, 0.5f, 1f);
                spec[i] = new Color(0.04f, 0.04f, 0.04f, 0.3f);
                float lit = 0.2f + (box ? 0.3f : 0f) + ((x / 32) % 2 == 0 ? 0.05f : 0f);
                basePic[i] = new Color(lit, lit, lit, 1f);
            }
        var t = new EnvgChainInput
        {
            Normals = Upload(normals, RenderTextureFormat.ARGB32), Albedo = Upload(albedo, RenderTextureFormat.ARGB32), Specular = Upload(spec, RenderTextureFormat.ARGB32),
            Depth = Upload(depth, RenderTextureFormat.RGFloat), Body = New(RenderTextureFormat.RHalf),
            Heat2x = New(RenderTextureFormat.ARGBHalf), Seams = New(RenderTextureFormat.ARGBHalf), GeoSource = New(RenderTextureFormat.ARGBFloat), GeoAcross = New(RenderTextureFormat.ARGBFloat),
            GeoNear = New(RenderTextureFormat.ARGBFloat), GeoFar = New(RenderTextureFormat.ARGBFloat), NormalsSoft = New(RenderTextureFormat.ARGBFloat), HeatBase = New(RenderTextureFormat.ARGBHalf),
            GrassMask = New(RenderTextureFormat.RHalf), Foliage = New(RenderTextureFormat.ARGBHalf), HistoryB = New(RenderTextureFormat.ARGBHalf), Blurred = New(RenderTextureFormat.ARGBHalf),
            Surround = New(RenderTextureFormat.ARGBHalf), Across = New(RenderTextureFormat.ARGBHalf), Edges = New(RenderTextureFormat.RHalf), Presence = New(RenderTextureFormat.ARGBHalf),
            Density = New(RenderTextureFormat.ARGBHalf), Glow = New(RenderTextureFormat.ARGBHalf), LocalSquare = New(RenderTextureFormat.ARGBFloat), LocalMoments = New(RenderTextureFormat.ARGBFloat),
            Ridge = New(RenderTextureFormat.ARGBHalf), LitSoft = New(RenderTextureFormat.ARGBHalf), AlbedoSoft = New(RenderTextureFormat.ARGBHalf),
            Motion = New(RenderTextureFormat.RGHalf), EdgeHistA = New(RenderTextureFormat.RHalf), EdgeHistB = New(RenderTextureFormat.RHalf),
            Width = W, Height = H, Oversample = 1, FromMain = true, MotionReady = true, Temporal = 0.8f, HeatTemporal = 0.8f,
            Tan = new Vector2(0.7f, 0.5f), CamToWorld = Matrix4x4.identity, DeltaTime = 1f / 60f,
            Amount = 1f, Softness = 0.4f, Brightness = 1f, GlowAmount = 0.6f, ResidualHeat = 4f, BodyHeat = 1f, GroundPatches = 2f, Air = 15f,
            NativeMethod = true, NativeGrass = true, GrassGain = 2f,
            LightHeating = 15f, Daylight = 0f, SkyCooling = 4f, Smoothing = 1f, HeatEdges = 1f, GeometryEdges = 0f, LocalContrast = 0.35f, ShapeNeedsHeat = 1f, MinSpread = 3f,
        };
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
        string[] names = { "picture", "heat", "heat followed", "edge response", "lines" };
        float[][] last = new float[5][];
        var change = new double[5];
        var level = new double[5];
        int counted = 0;
        for (int f = 0; f < Frames; f++)
        {
            int shift = Move ? Mathf.Clamp(f - 100, 0, 30) * 2 : 0;
            if (Move && f >= 100 && f <= 130)
            {
                var dep = new Color[W * H];
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                        dep[y * W + x] = new Color(x > 90 + shift && x < 170 + shift && y > 60 && y < 130 ? 2f : 4f, 0f, 0f, 1f);
                if (t.Depth != null) { t.Depth.Release(); Object.DestroyImmediate(t.Depth); }
                t.Depth = Upload(dep, RenderTextureFormat.RGFloat);
            }
            var pic = new Color[W * H];
            for (int i = 0; i < pic.Length; i++)
            {
                float n = PicNoise ? (float)(rnd.NextDouble() - 0.5) * 0.02f : 0f;   // about 2% of the level, fresh every frame
                pic[i] = new Color(basePic[i].r + n, basePic[i].g + n, basePic[i].b + n, 1f);
            }
            if (t.Picture != null) { t.Picture.Release(); Object.DestroyImmediate(t.Picture); }
            t.Picture = Upload(pic, RenderTextureFormat.ARGBHalf);
            // the G-buffer's colour too: the textures sampled at another sub-pixel each frame
            var alb = new Color[W * H];
            for (int i = 0; i < alb.Length; i++)
            {
                float n = AlbNoise ? (float)(rnd.NextDouble() - 0.5) * 0.025f : 0f;
                int bx = i % W, by = i / W;
                bool inBox = Move && bx > 90 + shift && bx < 170 + shift && by > 60 && by < 130;
                float a = albedo[i].r + (bx / 16 % 2 == 0 ? 0.1f : 0f) + n + (Step && f >= 200 && bx < 40 ? 0.2f : 0f) + (inBox ? 0.3f : 0f);
                alb[i] = new Color(a, a, a, 1f);
            }
            if (t.Albedo != null) { t.Albedo.Release(); Object.DestroyImmediate(t.Albedo); }
            t.Albedo = Upload(alb, RenderTextureFormat.ARGB32);
            if (NormNoise)
            {
                var nor = new Color[W * H];
                for (int i = 0; i < nor.Length; i++)
                {
                    float nx = (float)(rnd.NextDouble() - 0.5) * 0.03f, ny = (float)(rnd.NextDouble() - 0.5) * 0.03f;
                    nor[i] = new Color(0.5f + nx, 0.5f + ny, 1f, 1f);
                }
                if (t.Normals != null) { t.Normals.Release(); Object.DestroyImmediate(t.Normals); }
                t.Normals = Upload(nor, RenderTextureFormat.ARGB32);
            }
            var cb = new CommandBuffer();
            EnvgChain.Fill(cb, m, t);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
            float[][] now = { ReadR(t.Picture), ReadR(t.Heat2x!), ReadR(t.HistoryB!), ReadR(t.Edges!), ReadR(t.Ridge!) };
            if (Move && f == 131)
            {
                // where the box was and is no more: the heat followed should be the wall's again (no ghost)
                double ghost = 0; int nG = 0;
                for (int y = 70; y < 120; y++)
                    for (int x = 95; x < 145; x++)
                    {
                        ghost += now[2][y * W + x] - now[2][y * W + 20 + (x % 16)];
                        nG++;
                    }
                Debug.Log("ENVG_TAA_GHOST box moved off: heat followed there minus the wall " + (ghost / nG).ToString("F5") + " (the box was 0.30 albedo = " + (8f * 0.3f / 100f).ToString("F4") + " warmer)");
            }
            if (f >= 198 && f <= 260 && (f % 4 == 0 || f < 204))
                Debug.Log("ENVG_TAA_STEP frame " + f + ": heat " + now[1][96 * W + 20].ToString("F4") + " followed " + now[2][96 * W + 20].ToString("F4"));
            if (f >= Frames - 30)
            {
                for (int s = 0; s < 5; s++)
                    for (int i = 0; i < now[s].Length; i++)
                    {
                        change[s] += Mathf.Abs(now[s][i] - last[s][i]);
                        level[s] += 0.5f * (Mathf.Abs(now[s][i]) + Mathf.Abs(last[s][i]));
                    }
                counted++;
            }
            last = now;
        }
        var said = new System.Text.StringBuilder("ENVG_TAA_TEST (step " + Step + ", moving box " + Move + ") frame-to-frame change over " + counted + " frames:");
        for (int s = 0; s < 5; s++)
            said.Append(" " + names[s] + " " + (change[s] / System.Math.Max(level[s], 1e-9) * 100.0).ToString("F2") + "%");
        Debug.Log(said.ToString());
    }
}
