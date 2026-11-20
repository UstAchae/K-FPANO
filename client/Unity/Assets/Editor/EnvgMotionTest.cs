using UnityEngine;
using UnityEngine.Rendering;

// Offline check (not shipped): the chain while the view turns. A wall with stripes and a warm box, drawn each frame at a
// sub-pixel offset (as the game's jittered frame), turned sideways by a set number of the chain's pixels a frame; the
// camera's own matrices say the same turn. How many line pixels there are (and how they flicker) still and turning:
// turning must not thin the lines.
public static class EnvgMotionTest
{
    const int W = 256, H = 192;
    const float TanX = 0.7f, TanY = 0.5f;

    static RenderTexture New(RenderTextureFormat f)
    {
        var rt = new RenderTexture(W, H, 0, f) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        rt.Create();
        return rt;
    }

    static void Fill(ref RenderTexture? rt, Color[] px, RenderTextureFormat f)
    {
        var tex = new Texture2D(W, H, TextureFormat.RGBAFloat, false, true);
        tex.SetPixels(px);
        tex.Apply();
        if (rt == null) rt = New(f);
        Graphics.Blit(tex, rt);
        Object.DestroyImmediate(tex);
    }

    static float[] ReadR(RenderTexture rt)
    {
        var was = RenderTexture.active;
        RenderTexture.active = rt;
        var t = new Texture2D(W, H, TextureFormat.RGBAFloat, false, true);
        t.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        RenderTexture.active = was;
        Color[] c = t.GetPixels();
        Object.DestroyImmediate(t);
        var r = new float[c.Length];
        for (int i = 0; i < c.Length; i++) r[i] = c[i].r;
        return r;
    }

    // the scene at a world column u (pixels at yaw 0) and row y
    static bool Box(float u, int y) { float w = Mathf.Repeat(u, 256f); return w > 90f && w < 170f && y > 60 && y < 130; }
    static float Albedo(float u, int y) => 0.5f + (Mathf.FloorToInt(u / 16f) % 2 == 0 ? 0.1f : 0f) + (Box(u, y) ? 0.3f : 0f);

    public static void Run()
    {
        foreach (var c in new[] { (0.8f, 0.8f) })
        {
            HeatT = c.Item1; EdgeT = c.Item2;
            foreach (float sign in new[] { -1f })
                { RunOnce(0.5f, sign); RunOnce(2f, sign); }
        }
    }

    static float HeatT = 0.8f, EdgeT = 0.8f;
    static void RunOnce(float speed, float sign)
    {
        var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/EnvgOutline.shader");
        var m = new Material(shader);
        var rnd = new System.Random(3);
        var t = new EnvgChainInput
        {
            Body = New(RenderTextureFormat.RHalf),
            Heat2x = New(RenderTextureFormat.ARGBHalf), Seams = New(RenderTextureFormat.ARGBHalf), GeoSource = New(RenderTextureFormat.ARGBFloat), GeoAcross = New(RenderTextureFormat.ARGBFloat),
            GeoNear = New(RenderTextureFormat.ARGBFloat), GeoFar = New(RenderTextureFormat.ARGBFloat), NormalsSoft = New(RenderTextureFormat.ARGBFloat), HeatBase = New(RenderTextureFormat.ARGBHalf),
            GrassMask = New(RenderTextureFormat.RHalf), Foliage = New(RenderTextureFormat.ARGBHalf), HistoryB = New(RenderTextureFormat.ARGBHalf), Blurred = New(RenderTextureFormat.ARGBHalf),
            Surround = New(RenderTextureFormat.ARGBHalf), Across = New(RenderTextureFormat.ARGBHalf), Edges = New(RenderTextureFormat.RHalf), Presence = New(RenderTextureFormat.ARGBHalf),
            Density = New(RenderTextureFormat.ARGBHalf), Glow = New(RenderTextureFormat.ARGBHalf), LocalSquare = New(RenderTextureFormat.ARGBFloat), LocalMoments = New(RenderTextureFormat.ARGBFloat),
            Ridge = New(RenderTextureFormat.ARGBHalf), LitSoft = New(RenderTextureFormat.ARGBHalf), AlbedoSoft = New(RenderTextureFormat.ARGBHalf),
            Motion = New(RenderTextureFormat.RGHalf), EdgeHistA = New(RenderTextureFormat.RHalf), EdgeHistB = New(RenderTextureFormat.RHalf),
            Width = W, Height = H, Oversample = 1, FromMain = true, MotionReady = true, Temporal = EdgeT, HeatTemporal = HeatT,
            Tan = new Vector2(TanX, TanY), CamToWorld = Matrix4x4.identity, DeltaTime = 1f / 60f,
            Amount = 1f, Softness = 0.4f, Brightness = 1f, GlowAmount = 0.6f, ResidualHeat = 4f, BodyHeat = 1f, GroundPatches = 2f, Air = 15f,
            NativeMethod = true, NativeGrass = true, GrassGain = 2f,
            LightHeating = 15f, Daylight = 0f, SkyCooling = 4f, Smoothing = 1f, HeatEdges = 1f, GeometryEdges = 0f, LocalContrast = 0.35f, ShapeNeedsHeat = 1f, MinSpread = 3f,
        };
        Shader.SetGlobalVector("_EnvgCrop", Vector4.zero);
        Shader.SetGlobalVector("_EnvgCropPrev", Vector4.zero);
        Shader.SetGlobalVector("_EnvgRpOn", new Vector4(1f, 0f, 0f, 0f));
        Shader.SetGlobalVector("_EnvgRpTan", new Vector4(TanX, TanY, TanX, TanY));
        Shader.SetGlobalTexture("_EnvgRpMotion", t.Motion);
        Shader.SetGlobalTexture("_EnvgRpBody", t.Body);
        EnvgChain.Release();
        EnvgChain.SetLook(m, t);
        EnvgChain.ResetHistory();

        var depth = new Color[W * H];
        var normals = new Color[W * H];
        var albedo = new Color[W * H];
        var spec = new Color[W * H];
        var pic = new Color[W * H];
        RenderTexture? dRt = null, nRt = null, aRt = null, sRt = null, pRt = null;
        for (int i = 0; i < W * H; i++)
        {
            normals[i] = new Color(0.5f, 0.5f, 1f, 1f);
            spec[i] = new Color(0.04f, 0.04f, 0.04f, 0.3f);
        }
        Fill(ref nRt, normals, RenderTextureFormat.ARGB32);
        Fill(ref sRt, spec, RenderTextureFormat.ARGB32);
        t.Normals = nRt; t.Specular = sRt;

        float offset = 0f, yawWas = 0f;
        double stillLines = 0, turnLines = 0, stillFlicker = 0, turnFlicker = 0;
        int stillN = 0, turnN = 0;
        float[]? lastLines = null;
        const int Still = 120, Turn = 120;
        for (int f = 0; f < Still + Turn; f++)
        {
            bool turning = f >= Still;
            if (turning) offset += speed;
            // a turn of the view by `offset` pixels at the centre
            float yaw = sign * Mathf.Atan(offset / W * 2f * TanX) * Mathf.Rad2Deg;
            Matrix4x4 camToWorld = Matrix4x4.Rotate(Quaternion.Euler(0f, yaw, 0f));
            Matrix4x4 prevWorldToCam = Matrix4x4.Rotate(Quaternion.Euler(0f, yawWas, 0f)).inverse;
            yawWas = yaw;
            Shader.SetGlobalMatrix("_EnvgRpCamToWorld", camToWorld);
            Shader.SetGlobalMatrix("_EnvgRpPrevWorldToCam", prevWorldToCam);
            t.CamToWorld = camToWorld;
            // the frame drawn at a sub-pixel offset (jitter), each pixel the mean of 4 samples across it
            float jx = (float)rnd.NextDouble() - 0.5f;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    float a = 0f, d = 0f;
                    for (int s = 0; s < 4; s++)
                    {
                        float u = x + (s + 0.5f) / 4f + jx + offset;
                        a += Albedo(u, y);
                        d += Box(u, y) ? 2f : 4f;
                    }
                    a /= 4f;
                    d /= 4f;
                    depth[i] = new Color(d, 0f, 0f, 1f);
                    albedo[i] = new Color(a, a, a, 1f);
                    pic[i] = new Color(0.2f + 0.3f * a, 0.2f + 0.3f * a, 0.2f + 0.3f * a, 1f);
                }
            Fill(ref dRt, depth, RenderTextureFormat.RGFloat);
            Fill(ref aRt, albedo, RenderTextureFormat.ARGB32);
            Fill(ref pRt, pic, RenderTextureFormat.ARGBHalf);
            t.Depth = dRt; t.Albedo = aRt; t.Picture = pRt;
            t.Frame = f;
            var cb = new CommandBuffer();
            EnvgChain.Fill(cb, m, t);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
            float[] lines = ReadR(t.Ridge!);
            int count = 0;
            for (int y = 20; y < H - 20; y++)
                for (int x = 20; x < W - 20; x++)
                    if (lines[y * W + x] > 0.3f) count++;
            double flick = 0;
            if (lastLines != null && !turning)
                for (int i = 0; i < lines.Length; i++) flick += Mathf.Abs(lines[i] - lastLines[i]);
            lastLines = lines;
            if (!turning && f >= Still - 30) { stillLines += count; stillFlicker += flick; stillN++; }
            if (turning && f >= Still + 30) { turnLines += count; turnN++; }
        }
        Debug.Log("ENVG_MOTION_TEST heat history " + HeatT + ", edge history " + EdgeT + ", speed " + speed + " px/frame, sign " + sign + ": line pixels still " + (stillLines / stillN).ToString("F0")
            + ", turning " + (turnLines / turnN).ToString("F0") + " (" + (turnLines / turnN / System.Math.Max(stillLines / stillN, 1) * 100.0).ToString("F0") + "% of still); still flicker " + (stillFlicker / stillN).ToString("F1"));
    }
}
