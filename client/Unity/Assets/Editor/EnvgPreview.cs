#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;

// Offline preview of the F-PANO overlay: reruns the mod's processing chain (EnvgChain.cs, the same file the mod is built
// from) and the same outline shader on a frame dumped in the game (F12 「导出热像缓冲」: raw buffers + frame.json), and
// writes what the eyepiece would show as PNG files.
//
// Unity.exe -batchmode -quit -projectPath Unity -executeMethod EnvgPreview.Run -envgDump <dump dir> -envgOut <out dir>
//           [-envgSet key=value,key=value]   (keys as in frame.json, e.g. amount=0.5,declutter=1)
public static class EnvgPreview
{
    public static void Run()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        string dump = Arg(args, "-envgDump") ?? throw new System.Exception("-envgDump missing");
        string output = Arg(args, "-envgOut") ?? Path.Combine(dump, "preview");
        string set = Arg(args, "-envgSet") ?? "";
        Directory.CreateDirectory(output);

        Dictionary<string, float> frame = ReadJson(Path.Combine(dump, "frame.json"));
        foreach (string pair in set.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=');
            frame[kv[0].Trim()] = float.Parse(kv[1], CultureInfo.InvariantCulture);
        }

        Shader shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/EnvgOutline.shader");
        var m = new Material(shader);

        int width = (int)frame["width"], height = (int)frame["height"], over = (int)frame["oversample"];
        // -envgCrop x,y,w,h (screen uv): the chain worked out for that part of the screen only, as in the eyepieces
        Vector4 crop = new Vector4(0f, 0f, 1f, 1f);
        string? cropArg = Arg(args, "-envgCrop");
        if (cropArg != null)
        {
            string[] c = cropArg.Split(',');
            crop = new Vector4(float.Parse(c[0], CultureInfo.InvariantCulture), float.Parse(c[1], CultureInfo.InvariantCulture), float.Parse(c[2], CultureInfo.InvariantCulture), float.Parse(c[3], CultureInfo.InvariantCulture));
            width = Mathf.RoundToInt(width * crop.z);
            height = Mathf.RoundToInt(height * crop.w);
        }
        Shader.SetGlobalVector("_EnvgCrop", cropArg != null ? crop : Vector4.zero);
        Shader.SetGlobalVector("_EnvgCropPrev", cropArg != null ? crop : Vector4.zero);
        _crop = crop;
        int fw = width * over, fh = height * over;
        var t = new EnvgChainInput
        {
            Picture = Load(dump, "picture", RenderTextureFormat.ARGBHalf),
            Normals = Load(dump, "normals", RenderTextureFormat.ARGB32),
            Albedo = Load(dump, "albedo", RenderTextureFormat.ARGB32),
            Specular = Load(dump, "specular", RenderTextureFormat.ARGB32),
            Depth = Load(dump, "depth", RenderTextureFormat.RGFloat),
            Body = Load(dump, "body", RenderTextureFormat.RHalf),
            Heat2x = New(fw, fh, RenderTextureFormat.ARGBHalf),
            Seams = New(fw, fh, RenderTextureFormat.ARGBHalf),
            GeoSource = New(fw, fh, RenderTextureFormat.ARGBFloat),
            GeoAcross = New(fw, fh, RenderTextureFormat.ARGBFloat),
            GeoNear = New(fw, fh, RenderTextureFormat.ARGBFloat),
            GeoFar = New(fw, fh, RenderTextureFormat.ARGBFloat),
            NormalsSoft = New(fw, fh, RenderTextureFormat.ARGBFloat),
            HeatBase = New(fw, fh, RenderTextureFormat.ARGBHalf),
            GrassMask = File.Exists(Path.Combine(dump, "grassmask.raw")) ? Load(dump, "grassmask", RenderTextureFormat.RHalf) : New(fw, fh, RenderTextureFormat.RHalf),
            Foliage = New(fw, fh, RenderTextureFormat.ARGBHalf),
            HistoryB = New(fw, fh, RenderTextureFormat.ARGBHalf),
            Blurred = New(fw, fh, RenderTextureFormat.ARGBHalf),
            Surround = New(fw, fh, RenderTextureFormat.ARGBHalf),
            Across = New(fw, fh, RenderTextureFormat.ARGBHalf),
            Edges = New(fw, fh, RenderTextureFormat.RHalf),
            Presence = New(fw, fh, RenderTextureFormat.ARGBHalf),
            Density = New(fw, fh, RenderTextureFormat.ARGBHalf),
            Glow = New(fw, fh, RenderTextureFormat.ARGBHalf),
            LocalSquare = New(fw, fh, RenderTextureFormat.ARGBFloat),
            LocalMoments = New(fw, fh, RenderTextureFormat.ARGBFloat),
            Ridge = New(fw, fh, RenderTextureFormat.ARGBHalf),
            LitSoft = New(fw, fh, RenderTextureFormat.ARGBHalf),
            AlbedoSoft = New(fw, fh, RenderTextureFormat.ARGBHalf),
            FromMain = true,
            MotionReady = false,
            Width = width,
            Height = height,
            Oversample = over,
            Tan = new Vector2(frame["tanX"], frame["tanY"]),
        };
        var camToWorld = new Matrix4x4();
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                camToWorld[r, c] = frame["camToWorld" + r + c];
        t.CamToWorld = camToWorld;
        t.Amount = frame["amount"]; t.Softness = frame["softness"]; t.Brightness = frame["brightness"]; t.GlowAmount = frame["glow"];
        t.ResidualHeat = frame["residualHeat"]; t.BodyHeat = frame["bodyHeat"]; t.GroundPatches = frame["groundPatches"]; t.Air = frame["air"];
        t.LightHeating = frame["lightHeating"]; t.Daylight = frame["daylight"]; t.SkyCooling = frame["skyCooling"]; t.Smoothing = frame["smoothing"];
        t.HeatEdges = frame["heatEdges"]; t.GeometryEdges = frame["geometryEdges"]; t.LocalContrast = frame["localContrast"];
        t.ShapeNeedsHeat = frame["shapeNeedsHeat"]; t.Declutter = frame["declutter"];
        if (frame.TryGetValue("minSpread", out float ms)) t.MinSpread = ms;
        if (frame.TryGetValue("lineBase", out float lb)) t.LineBase = lb;
        if (frame.TryGetValue("grassGain", out float gg)) t.GrassGain = gg;
        if (frame.TryGetValue("roadWarm", out float rw)) t.RoadWarm = rw;
        if (frame.TryGetValue("native", out float nat)) { t.NativeMethod = nat > 0.5f; t.NativeGrass = nat > 1.5f; }
        if (frame.TryGetValue("fsLo", out float fsl)) t.FoliageStepLo = fsl;
        if (frame.TryGetValue("fsHi", out float fsh)) t.FoliageStepHi = fsh;
        if (frame.TryGetValue("lineHot", out float lh)) t.LineHot = lh;

        // -envgReproj: the history followed by the camera itself, with last frame's camera the same as this one (a
        // still view): it must land exactly where the game's zero motion lands (a check of the reprojection)
        bool reproj = Arg(args, "-envgReproj") != null;
        Shader.SetGlobalVector("_EnvgRpOn", new Vector4(reproj ? 1f : 0f, 0f, 0f, 0f));
        if (reproj)
        {
            Shader.SetGlobalMatrix("_EnvgRpCamToWorld", camToWorld);
            Shader.SetGlobalMatrix("_EnvgRpPrevWorldToCam", camToWorld.inverse);
            Shader.SetGlobalVector("_EnvgRpTan", new Vector4(t.Tan.x, t.Tan.y, t.Tan.x, t.Tan.y));
        }
        EnvgChain.Release();   // a fresh gain control: settles in one frame
        // the look first, as the mod sets it before the chain runs (the centre lines' threshold is part of it)
        EnvgChain.SetLook(m, t);
        // as in the game: the edge strength followed over frames (a still view: zero motion), -envgFrames of them
        int frames = int.TryParse(Arg(args, "-envgFrames"), out int nf) ? nf : 12;
        t.Motion = New(fw, fh, RenderTextureFormat.RGHalf);
        Shader.SetGlobalTexture("_EnvgRpMotion", t.Motion);
        if (t.Body != null) Shader.SetGlobalTexture("_EnvgRpBody", t.Body);
        t.EdgeHistA = New(fw, fh, RenderTextureFormat.RHalf);
        t.EdgeHistB = New(fw, fh, RenderTextureFormat.RHalf);
        t.MotionReady = frames > 1;
        t.Temporal = frame.TryGetValue("temporal", out float tp) ? tp : 0.8f;
        EnvgChain.ResetHistory();
        for (int f = 0; f < Mathf.Max(frames, 1); f++)
        {
            var cb = new CommandBuffer { name = "F-PANO preview" };
            EnvgChain.Fill(cb, m, t);
            Graphics.ExecuteCommandBuffer(cb);
            cb.Release();
        }

        // the tube's picture under the overlay: dumped, or (when the game drew straight to the screen) made here from the
        // lit picture by the same white phosphor shader, at twice the sensor's size
        RenderTexture tube = File.Exists(Path.Combine(dump, "screen.raw")) ? Load(dump, "screen", RenderTextureFormat.ARGBHalf) : Tube(t.Picture!, frame);
        // at the screen's own size, as the game shows it (the SSAA's picture is upscaled to it)
        int sw = frame.TryGetValue("screenWidth", out float scrW) ? (int)scrW : tube.width;
        int sh = frame.TryGetValue("screenHeight", out float scrH) ? (int)scrH : tube.height;
        RenderTexture screen = New(sw, sh, RenderTextureFormat.ARGBHalf);
        Graphics.Blit(tube, screen);
        float field = frame.TryGetValue("thermalField", out float tf) ? tf : 0f;
        m.SetVector("_EnvgField", new Vector4(0.4f * field * sh / sw, 0.3f * field, 4f / sh, field > 0f ? 1f : 0f));
        float lens = (frame.TryGetValue("lensOffset", out float lo) ? lo : 0f) * 0.01f;
        m.SetVector("_EnvgParallax", new Vector4(lens, 0.5f / Mathf.Max(t.Tan.y, 1e-4f), 0f, 0f));
        Mesh quad = FullScreenQuad();
        m.SetVector("_EnvgScreenTexel", new Vector4(1f / screen.width / crop.z, 1f / screen.height / crop.w, frame.TryGetValue("lineWidth", out float lw) ? lw : 1.5f, 0f));
        m.SetTexture("_EnvgScene", screen);
        m.SetVector("_EnvgWashout", new Vector4(frame.TryGetValue("washout", out float wo) ? wo : 1f, 0f, 0f, 0f));
        m.SetTexture("_EnvgHeatView", t.HistoryB);

        // the histogram from the small picture, read back at once
        if (EnvgChain.HeSmall != null)
        {
            RenderTexture.active = EnvgChain.HeSmall;
            var small = new Texture2D(EnvgChain.HeSmall.width, EnvgChain.HeSmall.height, TextureFormat.RFloat, false, true);
            small.ReadPixels(new Rect(0, 0, small.width, small.height), 0, 0);
            small.Apply();
            RenderTexture.active = null;
            EnvgChain.BuildLut(small.GetPixelData<float>(0), t.MinSpread * 4f, 0f, true);
        }
        bool display = File.Exists(Path.Combine(dump, "screen.raw"));   // the game's own frame: display values already
        _displayGain = 1f;
        string finalPath = Path.Combine(dump, "final.png");
        if (display && File.Exists(finalPath))
        {
            // the bottom-right corner (outside the thermal rectangle and any settings window): shown vs drawn
            var shown = new Texture2D(2, 2);
            shown.LoadImage(File.ReadAllBytes(finalPath));
            RenderTexture.active = tube;
            var drawn = new Texture2D(tube.width, tube.height, TextureFormat.RGBAFloat, false, true);
            drawn.ReadPixels(new Rect(0, 0, tube.width, tube.height), 0, 0);
            drawn.Apply();
            RenderTexture.active = null;
            double a = 0, b = 0;
            for (int y = 10; y < shown.height / 4; y += 3)
                for (int x = shown.width * 3 / 4; x < shown.width - 10; x += 3)
                {
                    Color s = shown.GetPixel(x, y);
                    Color d = drawn.GetPixelBilinear((x + 0.5f) / shown.width, (y + 0.5f) / shown.height);
                    a += s.r + s.g + s.b;
                    b += d.r + d.g + d.b;
                }
            if (b > 1e-3)
                _displayGain = Mathf.Clamp((float)(a / b), 0.2f, 2f);
            Debug.Log("F-PANO preview: the game shows its frame at " + _displayGain.ToString("F2") + " of the SSAA's picture");
        }
        Save(screen, Path.Combine(output, "0_tube.png"), !display);
        Save(Overlay(m, quad, screen, t.Ridge!, 3), Path.Combine(output, "1_outline.png"), !display);
        m.SetVector("_EnvgFusion", new Vector4(1f, 0.85f, 0f, 0f));
        Save(Overlay(m, quad, screen, t.Ridge!, 21), Path.Combine(output, "2_fusion.png"), !display);
        m.SetVector("_EnvgFusion", new Vector4(-1f, 0.85f, 0f, 0f));
        Save(Overlay(m, quad, screen, t.Ridge!, 21), Path.Combine(output, "2_fusion_blackhot.png"), !display);
        RenderTexture black = New(screen.width, screen.height, RenderTextureFormat.ARGBHalf);
        Save(Overlay(m, quad, black, t.HistoryB!, 15), Path.Combine(output, "3_heat.png"), false);
        Save(Overlay(m, quad, black, t.Ridge!, 3), Path.Combine(output, "4_lines_only.png"), true);
        Save(t.HistoryB!, Path.Combine(output, "5_heat_edge_foliage.png"), false);
        Debug.Log("ENVG_PREVIEW_DONE " + output);
    }

    private static RenderTexture Tube(RenderTexture picture, Dictionary<string, float> frame)
    {
        // the brightness control's mean: the photocathode's signal averaged over the frame
        RenderTexture tmp = New(picture.width, picture.height, RenderTextureFormat.ARGBFloat);
        Graphics.Blit(picture, tmp);
        RenderTexture.active = tmp;
        var read = new Texture2D(picture.width, picture.height, TextureFormat.RGBAFloat, false, true);
        read.ReadPixels(new Rect(0, 0, picture.width, picture.height), 0, 0);
        read.Apply();
        RenderTexture.active = null;
        double sum = 0;
        foreach (Color c in read.GetPixels())
            sum += Mathf.Max(c.r, 0f);
        float mean = (float)(sum / (picture.width * picture.height));
        var abc = new Texture2D(1, 1, TextureFormat.RGFloat, false, true);
        abc.SetPixel(0, 0, new Color(mean, 0f, 0f, 0f));
        abc.Apply();
        var tube = new Material(UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/EnvgWhitePhosphor.shader"));
        tube.SetTexture("_EnvgAbc", abc);
        tube.SetVector("_EnvgI2", new Vector4(100f * (frame.TryGetValue("tubeBrightness", out float tb) ? tb : 1f), 0f, 0f, 1.5f));
        tube.SetVector("_EnvgI2Time", new Vector4(0f, 1f, 0f, 0f));
        RenderTexture screen = New(picture.width * 2, picture.height * 2, RenderTextureFormat.ARGBHalf);
        Graphics.Blit(picture, screen, tube);
        Debug.Log("F-PANO preview: tube made from the lit picture (mean " + mean + ")");
        return screen;
    }

    // the overlay drawn over a copy of the frame through a full-screen quad, as the mod draws it after the game's SSAA
    private static RenderTexture Overlay(Material m, Mesh quad, RenderTexture under, RenderTexture main, int pass)
    {
        RenderTexture target = New(under.width, under.height, RenderTextureFormat.ARGBHalf);
        Graphics.Blit(under, target);
        m.SetTexture("_MainTex", main);
        var cb = new CommandBuffer();
        cb.SetRenderTarget(target);
        cb.SetViewport(new Rect(0, 0, under.width, under.height));
        cb.DrawMesh(quad, Matrix4x4.identity, m, 0, pass);
        Graphics.ExecuteCommandBuffer(cb);
        return target;
    }

    private static Mesh FullScreenQuad()
    {
        var mesh = new Mesh();
        mesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0), new Vector3(1, -1, 0) };
        bool flip = SystemInfo.graphicsUVStartsAtTop;
        mesh.uv = flip
            ? new[] { new Vector2(0, 1), new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1) }
            : new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        return mesh;
    }

    private static Vector4 _crop = new Vector4(0f, 0f, 1f, 1f);
    private static RenderTexture Load(string dir, string name, RenderTextureFormat format)
    {
        string path = Path.Combine(dir, name + ".raw");
        using var file = new BinaryReader(File.OpenRead(path));
        int w = file.ReadInt32(), h = file.ReadInt32();
        byte[] data = file.ReadBytes(w * h * 16);
        var tex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
        tex.LoadRawTextureData(data);
        tex.Apply();
        tex.filterMode = FilterMode.Point;
        if (name != "screen" && _crop.z < 1f)
        {
            // the part of the screen the chain is worked out for
            RenderTexture part = New(Mathf.RoundToInt(w * _crop.z), Mathf.RoundToInt(h * _crop.w), format);
            Graphics.Blit(tex, part, new Vector2(_crop.z, _crop.w), new Vector2(_crop.x, _crop.y));
            Object.DestroyImmediate(tex);
            return part;
        }
        RenderTexture rt = New(w, h, format);
        Graphics.Blit(tex, rt);
        Object.DestroyImmediate(tex);
        return rt;
    }

    private static RenderTexture New(int w, int h, RenderTextureFormat format)
    {
        var rt = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        rt.Create();
        return rt;
    }

    // linear values written as sRGB (the game's frame is linear at this point)
    // what the game does to the frame after its SSAA (seen in the screenshot taken with the dump): the shown frame is
    // darker than the SSAA's own picture by this factor; the preview's display images are shown the same way
    private static float _displayGain = 1f;

    private static void Save(RenderTexture rt, string path, bool linear)
    {
        RenderTexture tmp = New(rt.width, rt.height, RenderTextureFormat.ARGBFloat);
        Graphics.Blit(rt, tmp);
        RenderTexture.active = tmp;
        var read = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
        read.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        read.Apply();
        RenderTexture.active = null;
        Color[] px = read.GetPixels();
        for (int i = 0; i < px.Length; i++)
        {
            Color c = px[i];
            if (linear)
                c = new Color(Mathf.Pow(Mathf.Clamp01(c.r), 1f / 2.2f), Mathf.Pow(Mathf.Clamp01(c.g), 1f / 2.2f), Mathf.Pow(Mathf.Clamp01(c.b), 1f / 2.2f), 1f);
            else
                c = new Color(c.r * _displayGain, c.g * _displayGain, c.b * _displayGain, 1f);
            px[i] = c;
        }
        var outTex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
        outTex.SetPixels(px);
        outTex.Apply();
        File.WriteAllBytes(path, outTex.EncodeToPNG());
        Object.DestroyImmediate(read);
        Object.DestroyImmediate(outTex);
    }

    private static Dictionary<string, float> ReadJson(string path)
    {
        var values = new Dictionary<string, float>();
        foreach (Match match in Regex.Matches(File.ReadAllText(path), "\"(\\w+)\":\\s*(-?[0-9.Ee+-]+)"))
            values[match.Groups[1].Value] = float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return values;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = System.Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
