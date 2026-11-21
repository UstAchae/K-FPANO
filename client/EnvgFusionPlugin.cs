using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace EnvgFusion;

// F-PANO style thermal outlines over the whole screen. A camera of our own (the game's scope camera prefab, as a
// thermal scope does it) renders the scene in white-hot thermal at a low sensor resolution from the main camera's
// place and view; a shader smooths it, finds its edges and a second camera of ours adds them in amber onto the
// finished screen, after everything the main camera does (night vision, BRNVG, post-processing, upscaling), as the
// goggle's OLED overlay adds its picture to the tube's.
[BepInPlugin(Guid, Name, Version)]
[BepInDependency("justachae.thirdpersoncamera", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class EnvgFusionPlugin : BaseUnityPlugin
{
    public const string Guid = "justachae.fpanofusion";
    public const string Name = "Justachae's F-PANO Thermal Overlay";
    public const string Version = "0.2.24";
    private const string Section = "F-PANO 热像轮廓";
    private const string Advanced = "F-PANO 热像轮廓 · 高级";

    internal static ManualLogSource? Log;
    internal static ConfigEntry<bool>? On, WhitePhosphor;
    internal static ConfigEntry<float>? GrassAmount, ThermalField, Latency, MinSpread, GroundPatches, BodyHeat, ShapeNeedsHeat, Washout, Temporal, LineWidth, Brightness, Amount, TubeBrightness, Softness, Glow, Smoothing, AirTemperature, LightHeating, SkyCooling, I2Noise, HeatEdges, GeometryEdges, ResidualHeat, Declutter, LocalContrast;
    internal static ConfigEntry<float>? SensorResolution;
    internal static ConfigEntry<int>? RenderScale;
    internal static ConfigEntry<string>? Show, HeatSource;
    internal const string SourceModel = "物理模型", SourceNative = "原版方法";
    internal const string ShowOutline = "轮廓", ShowFusion = "白热融合", ShowBlackHot = "黑热融合", ShowI2 = "纯夜视";
    // the device's four modes, in the order the arrow keys step through them
    internal static readonly string[] Modes = { ShowI2, ShowOutline, ShowFusion, ShowBlackHot };
    internal static ConfigEntry<BepInEx.Configuration.KeyboardShortcut>? NextMode, PreviousMode, GainUp, GainDown, LeftLight, RightLight;
    internal static ConfigEntry<float>? I2Level;
    private const float GainFullTravel = 2.5f;   // seconds for the gain to go from dark to full with an arrow held
    internal static float GainShownUntil;   // the I2 gain bar shows while it changes and for a moment after
    private static float _gainLive = -1f;    // the gain while an arrow is held (saved when it is let go)
    internal static float GainNow => _gainLive >= 0f ? _gainLive : Mathf.Clamp01(I2Level?.Value ?? 1f);

    private void Awake()
    {
        Log = Logger;
        // ---- what to use: a few settings ----
        // the thermal channel works on its own whenever the F-PANO is worn and flipped down; this is for testing only
        On = Config.Bind(Advanced, "调试：不戴 F-PANO 也开启", false,
            new ConfigDescription("测试用：打开后，不戴 F-PANO 也在全屏叠加热像。正常使用不用打开：戴上 F-PANO 并放下就会工作。", null, new ConfigurationManagerAttributes { IsAdvanced = true }));
        Show = Config.Bind(Section, "显示内容", ShowOutline,
            new ConfigDescription("F-PANO 的四种模式（游戏里用 ← → 切换）：纯夜视：只有白磷夜视。轮廓：温差的边缘画成琥珀色的线。白热融合：整幅热像（热的亮）叠在夜视上。黑热融合：整幅热像（冷的亮，热的暗）叠在夜视上。",
                new AcceptableValueList<string>(ShowI2, ShowOutline, ShowFusion, ShowBlackHot), new ConfigurationManagerAttributes { Order = 9 }));
        HeatSource = Config.Bind(Section, "热像来源", SourceNative,
            new ConfigDescription("物理模型：本模组按阳光、夜间散热、天空、植被、人体估算的温度。原版方法：照游戏原版热成像的做法，表面的热度取贴图颜色深浅，人按体温单独标热（用来对比）。三种热像模式都跟着变。",
                new AcceptableValueList<string>(SourceModel, SourceNative), new ConfigurationManagerAttributes { Order = 8 }));
        GrassAmount = Config.Bind(Section, "草的轮廓强度", 2f,
            new ConfigDescription("轮廓模式、热像来源为原版方法时，草比地面冷多少（°C）：草丛和草地边缘（路边、空地）的线由此而来。越大线越多越亮；0 是没有草的线（原版方法本来的样子）。",
                new AcceptableValueRange<float>(0f, 5f), new ConfigurationManagerAttributes { Order = 8 }));
        NextMode = Config.Bind(Section, "下一个模式", new BepInEx.Configuration.KeyboardShortcut(KeyCode.RightArrow),
            new ConfigDescription("切到下一个模式（纯夜视 → 轮廓 → 白热融合 → 黑热融合）。", null, new ConfigurationManagerAttributes { Order = 8 }));
        PreviousMode = Config.Bind(Section, "上一个模式", new BepInEx.Configuration.KeyboardShortcut(KeyCode.LeftArrow),
            new ConfigDescription("切到上一个模式。", null, new ConfigurationManagerAttributes { Order = 8 }));
        GainUp = Config.Bind(Section, "夜视增益 +", new BepInEx.Configuration.KeyboardShortcut(KeyCode.UpArrow),
            new ConfigDescription("调高夜视管增益：点一下微调，按住连续变化（从 0 到满约 2.5 秒）。调节时目镜热像框里靠左边显示黄色的 I2 GAIN 竖条。", null, new ConfigurationManagerAttributes { Order = 8 }));
        GainDown = Config.Bind(Section, "夜视增益 -", new BepInEx.Configuration.KeyboardShortcut(KeyCode.DownArrow),
            new ConfigDescription("调低夜视管增益：点一下微调，按住连续变化；调到 0 夜视画面全黑，只剩热像（轮廓模式下就只有轮廓）。", null, new ConfigurationManagerAttributes { Order = 8 }));
        LeftLight = Config.Bind("RAILINK 照明", "左侧照明切换模式", new BepInEx.Configuration.KeyboardShortcut(KeyCode.LeftArrow, KeyCode.LeftControl),
            new ConfigDescription("切换 RAILINK 左侧（佩戴者的左边）前灯的模式。H 仍然开关全部头灯，Ctrl+H 只切换频闪灯的效果。", null, new ConfigurationManagerAttributes { Order = 2 }));
        RightLight = Config.Bind("RAILINK 照明", "右侧照明切换模式", new BepInEx.Configuration.KeyboardShortcut(KeyCode.RightArrow, KeyCode.LeftControl),
            new ConfigDescription("切换 RAILINK 右侧前灯的模式。", null, new ConfigurationManagerAttributes { Order = 1 }));
        I2Level = Config.Bind(Section, "夜视管增益", 1f,
            new ConfigDescription("夜视管增益，0 到 1（游戏里按住 ↑ ↓ 调）。0 是关掉夜视画面，只看热像。", new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 8 }));
        Brightness = Config.Bind(Section, "轮廓亮度", 1f,
            new ConfigDescription("轮廓线有多亮。", new AcceptableValueRange<float>(0f, 4f), new ConfigurationManagerAttributes { Order = 8 }));
        Amount = Config.Bind(Section, "轮廓多少", 1f,
            new ConfigDescription("画多少线：越大，越弱的边缘也画出来（更多细纹）；越小，只剩明显的轮廓。", new AcceptableValueRange<float>(0f, 1f), new ConfigurationManagerAttributes { Order = 7 }));
        WhitePhosphor = Config.Bind(Section, "白磷夜视", true,
            new ConfigDescription("夜视画面换成白磷管（蓝白色）。戴着游戏里的夜视仪并打开时，用那台夜视仪自己的画面。", null, new ConfigurationManagerAttributes { Order = 6 }));
        TubeBrightness = Config.Bind(Section, "夜视亮度", 1f,
            new ConfigDescription("白磷夜视画面的亮度（管子的最大增益）。太暗调高，太亮调低。", new AcceptableValueRange<float>(0.25f, 4f), new ConfigurationManagerAttributes { Order = 5 }));

        // ---- tuning: kept to the defaults unless there is a reason (shown with Advanced settings ticked) ----
        var advanced = new ConfigurationManagerAttributes { IsAdvanced = true };
        LineWidth = Config.Bind(Advanced, "线宽（像素）", 1.5f,
            new ConfigDescription("轮廓线的半宽，屏幕像素。", new AcceptableValueRange<float>(0.5f, 4f), advanced));
        Softness = Config.Bind(Advanced, "边缘过渡", 0.4f,
            new ConfigDescription("线从阈值到满亮度的过渡宽度（以场景温差为单位）。越大，弱边缘越淡、越渐变。", new AcceptableValueRange<float>(0.01f, 3f), advanced));
        HeatEdges = Config.Bind(Advanced, "热源轮廓强度", 1f,
            new ConfigDescription("由温差得到的轮廓有多强。0 是不画。", new AcceptableValueRange<float>(0f, 3f), advanced));
        GeometryEdges = Config.Bind(Advanced, "几何轮廓强度", 0f,
            new ConfigDescription("由深度和表面朝向得到的外形轮廓有多强。0 是不画。", new AcceptableValueRange<float>(0f, 3f), advanced));
        Glow = Config.Bind(Advanced, "光晕", 0.6f,
            new ConfigDescription("线条周围光晕的强度。", new AcceptableValueRange<float>(0f, 3f), advanced));
        SensorResolution = Config.Bind(Advanced, "热像角分辨率（像素/度）", 32f,
            new ConfigDescription("热像每度视场多少像素（真机约 640 像素 / 40°）。", new AcceptableValueRange<float>(4f, 32f), advanced));
        RenderScale = Config.Bind(Advanced, "热像渲染倍数", 1,
            new ConfigDescription("按传感器分辨率的几倍处理。1（默认）像真传感器，细碎的叶子平均成一团，线少而稳；2 细节多、线碎。", new AcceptableValueRange<int>(1, 2), advanced));
        Smoothing = Config.Bind(Advanced, "镜头模糊（像素）", 1f,
            new ConfigDescription("热像镜头的衍射模糊。越大只剩大的温差轮廓。", new AcceptableValueRange<float>(0f, 6f), advanced));
        AirTemperature = Config.Bind(Advanced, "气温（°C）", 15f,
            new ConfigDescription("热像的基准温度。", new AcceptableValueRange<float>(-30f, 45f), advanced));
        LightHeating = Config.Bind(Advanced, "光照升温（°C）", 15f,
            new ConfigDescription("吸收的光让表面升温多少。", new AcceptableValueRange<float>(0f, 60f), advanced));
        SkyCooling = Config.Bind(Advanced, "天空降温（°C）", 4f,
            new ConfigDescription("朝天的表面比气温低多少。", new AcceptableValueRange<float>(0f, 15f), advanced));
        ResidualHeat = Config.Bind(Advanced, "材质余热（°C）", 4f,
            new ConfigDescription("白天存下的热：深色、厚重的表面（石头、土、树干、水泥）夜里比空气暖。按几像素内平均的颜色算，不会画出贴图花纹。", new AcceptableValueRange<float>(0f, 15f), advanced));
        ThermalField = Config.Bind(Advanced, "热像视场（相对夜视管）", 1f,
            new ConfigDescription("热像画面是夜视管圆形视野里的一个 4:3 矩形：1 是真机的比例：宽为夜视管视野直径的 0.68、高 0.52。矩形外只有夜视画面。0 是铺满全屏。", new AcceptableValueRange<float>(0f, 1.5f), advanced));
        Latency = Config.Bind(Advanced, "热像延迟（毫秒）", 0f,
            new ConfigDescription("非制冷热像探测器加数字处理和显示的延迟：转头时轮廓比夜视画面慢这么多。0 是不延迟。", new AcceptableValueRange<float>(0f, 60f), advanced));
        MinSpread = Config.Bind(Advanced, "热像增益上限（°C）", 3f,
            new ConfigDescription("热像增益的上限：整个画面温差小于这个值时（比如夜里），不会再把细小温差放大成满屏的线。", new AcceptableValueRange<float>(0.3f, 20f), advanced));
        GroundPatches = Config.Bind(Advanced, "地面温度斑块（°C）", 2f,
            new ConfigDescription("地面各处温度不一（干湿土、草和裸土交错）的幅度，几米大小的斑块，固定在地面上不随视角动。远处地面因此有零碎的斑点和短线。", new AcceptableValueRange<float>(0f, 5f), advanced));
        BodyHeat = Config.Bind(Advanced, "人体发热", 1f,
            new ConfigDescription("人（玩家、敌人，连同身上的装备）比环境热，在热像里最醒目。1 是衣物表面约 28°C，0 是不发热。", new AcceptableValueRange<float>(0f, 1.5f), advanced));
        ShapeNeedsHeat = Config.Bind(Advanced, "外形轮廓需温差", 1f,
            new ConfigDescription("像真热像：物体的外形只在两侧有温差的地方出线（树冠对天空、人对环境），叶子对叶子这种温度一样的地方不出线。0 是外形全画。", new AcceptableValueRange<float>(0f, 1f), advanced));
        Washout = Config.Bind(Advanced, "亮处淡出", 1f,
            new ConfigDescription("热像轮廓和夜视画面是两束光相加：被灯照亮、夜视画面接近发白的地方，轮廓融进亮背景里看不出来，暗处的轮廓才醒目（像实物视频里那样）。0 是不淡出。", new AcceptableValueRange<float>(0f, 1f), advanced));
        Temporal = Config.Bind(Advanced, "时间平滑", 0.8f,
            new ConfigDescription("像游戏的 TAA：用游戏自己的运动矢量把上一帧对齐后，和这一帧的热像、边缘强度混合（热像画面和轮廓线都不随游戏每帧的亚像素抖动闪动）。越大越稳，0 是关。", new AcceptableValueRange<float>(0f, 0.95f), advanced));
        Declutter = Config.Bind(Advanced, "去杂乱", 0f,
            new ConfigDescription("线密集的地方（树冠）压暗。0 是不压。", new AcceptableValueRange<float>(0f, 1f), advanced));
        LocalContrast = Config.Bind(Advanced, "局部对比", 0.35f,
            new ConfigDescription("按周围的温差定边缘强弱（小温差在大温差旁也显出来）。0 是按全画面。", new AcceptableValueRange<float>(0f, 1f), advanced));
        I2Noise = Config.Bind(Advanced, "夜视噪点", 0f,
            new ConfigDescription("白磷夜视的散粒噪声。0 是没有。", new AcceptableValueRange<float>(0f, 3f), advanced));
        Camera.onPreRender += EnvgFusion.OnPreRender;
        var harmony = new Harmony(Guid);
        MethodInfo? final = AccessTools.Method(typeof(SSAAImpl), "OnRenderImage");
        if (final != null)
            harmony.Patch(final, postfix: new HarmonyMethod(typeof(EnvgFusion), nameof(EnvgFusion.AfterFinalImage)));
        else
            Log.LogError("SSAAImpl.OnRenderImage not found; the overlay can't be drawn");
        // the grass: GPUInstancer submits it for the main camera only
        MethodInfo? grassDraw = AccessTools.Method(typeof(GPUInstancer.GPUInstancerUtility), "GPUIDrawMeshInstancedIndirect");
        try
        {
            if (grassDraw != null && grassDraw.IsGenericMethodDefinition)
                harmony.Patch(grassDraw.MakeGenericMethod(typeof(GPUInstancer.GPUInstancerRuntimeData)),
                    postfix: new HarmonyMethod(typeof(EnvgFusion), nameof(EnvgFusion.AfterGrassDraw)));
            else
                Log.LogWarning("GPUInstancerUtility.GPUIDrawMeshInstancedIndirect not found; no grass in the thermal picture");
        }
        catch (System.Exception e)
        {
            Log.LogWarning("Patching the grass draw failed; no grass in the thermal picture: " + e.Message);
        }
        // the sights' glass: the red dots and holographic sights the game has enabled, as it announces them
        FpanoRailink.Patch(harmony);
        FpanoSlotIcons.Patch(harmony);
        FpanoLights.Patch(harmony);
        MethodInfo? nvPreCull = AccessTools.Method(typeof(BSG.CameraEffects.NightVision), "OnPreCull");
        if (nvPreCull != null)
            harmony.Patch(nvPreCull, postfix: new HarmonyMethod(typeof(EnvgFusion), nameof(EnvgFusion.AfterNightVisionPreCull)));
        Log.LogInfo(Name + " " + Version + " loaded");
    }

    private void Update()
    {
        bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)
            || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);   // (Ctrl + ← →: the rails' lights)
        FpanoLights.Update();
        FpanoProbe.Update();
        _ = EnvgFusion.EnvgbDown;   // (read first: it notes the worn device this frame)
        // ← →: the device's modes (not while a menu, the inventory or the map has the cursor)
        if (EnvgFusion.Active && Show != null && !Cursor.visible)
        {
            int step = alt ? 0 : NextMode != null && NextMode.Value.IsDown() ? 1 : PreviousMode != null && PreviousMode.Value.IsDown() ? -1 : 0;
            if (step != 0)
            {
                int at = System.Array.IndexOf(Modes, Show.Value);
                at = at < 0 ? 1 : (at + step + Modes.Length) % Modes.Length;
                Show.Value = Modes[at];
                EnvgFusion.ModeChanged();
            }
            // ↑ ↓: the tubes' gain, smoothly: a tap nudges it, held it runs (dark to full in GainFullTravel)
            if (I2Level != null)
            {
                bool up = GainUp != null && GainUp.Value.IsPressed(), down = GainDown != null && GainDown.Value.IsPressed();
                bool tapUp = GainUp != null && GainUp.Value.IsDown(), tapDown = GainDown != null && GainDown.Value.IsDown();
                float change = (tapUp ? 0.03f : 0f) - (tapDown ? 0.03f : 0f) + ((up ? 1f : 0f) - (down ? 1f : 0f)) * Time.unscaledDeltaTime / GainFullTravel;
                if (up || down || tapUp || tapDown)
                {
                    float level = Mathf.Clamp01(GainNow + change);
                    // saved only when it settles (the config file is written on every change)
                    _gainLive = level;
                    GainShownUntil = Time.unscaledTime + 2.5f;
                }
                else if (_gainLive >= 0f)
                {
                    I2Level.Value = _gainLive;
                    _gainLive = -1f;
                }
            }
        }
        // the eyepieces (when they take it) dim both tubes, in every mode, from the saved level on (sent only on a change)
        if (EnvgFusion.Active)
            EyepieceLink.SetGain(EnvgFusion.I2Gain);
        EnvgFusion.Update();
    }

    private void LateUpdate() => EnvgFusion.SetWhitePhosphor();

    // every plugin's assembly is loaded by now: K-3PS-C's eyepieces, if it is there
    private void Start() => EyepieceLink.Bind();
}

internal static class EnvgFusion
{
    private static Shader? _shader;
    private static bool _bundleTried;
    internal static Material? Material;
    internal static RenderTexture? Picture, Normals, Depth, Albedo, Specular, Heat2x, Seams, GeoSource, GeoAcross, GeoNear, GeoFar, HistoryA, HistoryB, Across, Blurred, Surround, Edges, Presence, Density, Glow, LocalSquare, LocalMoments, Ridge, LitSoft, AlbedoSoft, Motion, EdgeHistA, EdgeHistB, BodyMask, NormalsSoft, Foliage, HeatBase, GrassMask;
    private static Matrix4x4 _camToWorld = Matrix4x4.identity;
    private static Matrix4x4 _prevWorldToCam = Matrix4x4.identity;
    private static Vector2 _prevTan = Vector2.one;
    private static bool _hasPrevCamera, _loggedFollow;
    private static readonly int RpCamToWorldId = Shader.PropertyToID("_EnvgRpCamToWorld"), RpPrevWorldToCamId = Shader.PropertyToID("_EnvgRpPrevWorldToCam");
    private static readonly int RpTanId = Shader.PropertyToID("_EnvgRpTan"), RpOnId = Shader.PropertyToID("_EnvgRpOn");
    private static readonly int RpMotionId = Shader.PropertyToID("_EnvgRpMotion"), RpBodyId = Shader.PropertyToID("_EnvgRpBody");
    private static Vector2 _tan = Vector2.one;
    internal static RenderTexture? Heat => HistoryB;
    private static Material? _i2Material;
    private static Shader? _i2Shader;
    private static bool _loggedI2;
    private static int _tubeFrame = -1;   // the frame the brightness was last measured on
    private static bool _loggedGrass;
    private static RenderTexture? _abcCoarse, _abcMean, AbcA, AbcB;
    private static int _abcBuilt;
    private static CommandBuffer? _abcDraw;
    private static readonly AccessTools.FieldRef<SSAAPropagator, RenderTexture[]> PropagatorHdr =
        AccessTools.FieldRefAccess<SSAAPropagator, RenderTexture[]>("_resampledColorTargetHDR");
    private static int Oversample = 1;  // processing pixels per detector pixel, each way (热像渲染倍数)
    private static int _sensorWidth, _sizedFor;   // the sensor's width, kept at the widest view seen (see RenderThermal)
    // the main camera's own deferred buffers of this frame, copied during its render (it has everything you see)
    private static CommandBuffer? _mainCopy;
    private static Camera? _mainCopyCamera;
    private static bool _loggedMain;
    private static readonly int JitterId = Shader.PropertyToID("_EnvgJitter");
    private static readonly int FootprintId = Shader.PropertyToID("_EnvgFootprint");
    private static readonly int CropId = Shader.PropertyToID("_EnvgCrop"), CropPrevId = Shader.PropertyToID("_EnvgCropPrev");

    // The part of the screen the thermal picture is worked out for. Over the screen, all of it. In K-3PS-C's
    // eyepieces only the thermal rectangle is ever shown, so only it (and a margin for the blurs and the lens's
    // parallax): its size fixed by the eyepiece's field, its corner on the sensor's pixel grid, so the history stays
    // put. (xy corner, zw size, screen uv)
    private static Vector4 _crop = new Vector4(0f, 0f, 1f, 1f), _cropPrev = new Vector4(0f, 0f, 1f, 1f);
    private static Vector4 _lastField;
    private static int _lastFieldFrame = -100;
    private static bool _loggedCrop;
    private const float CropMargin = 0.04f;   // uv of the screen's height, each side

    // The crop's size in sensor pixels, kept while the eyepieces show the device: the field they report moves a little
    // with the eye, and a crop a pixel larger or smaller re-made every texture of the chain (and dropped its history).
    // It grows at once when more is needed and shrinks only when 15 percent too large.
    private static int _cropW, _cropH;

    private static Vector4 CropFor(int fullWidth, int fullHeight)
    {
        if (!EyepieceLink.ClaimedRecently || Time.frameCount - _lastFieldFrame > 2 || _lastField.z <= 0f)
        {
            _cropW = _cropH = 0;
            return new Vector4(0f, 0f, 1f, 1f);
        }
        // this frame's eyepiece where the eyepieces have claimed it already (they set it at the camera's pre-cull, before
        // this): last frame's lagged a running wearer's bobbing eyepiece, and part of the rectangle fell outside the crop
        Vector4 f = EyepieceLink.ClaimedNow && EyepieceLink.FieldInMainUv.z > 0f ? EyepieceLink.FieldInMainUv : _lastField;
        float aspect = Mathf.Max(f.w, 1e-3f), share = EnvgFusionPlugin.ThermalField?.Value ?? 1f;
        if (share <= 0f)
            return new Vector4(0f, 0f, 1f, 1f);
        float halfH = EyepieceLink.FusedRect.y * f.z * share + CropMargin, halfW = (EyepieceLink.FusedRect.x * f.z * share + CropMargin) / aspect;
        int needW = Mathf.Min(fullWidth, Mathf.CeilToInt(2f * halfW * fullWidth)), needH = Mathf.Min(fullHeight, Mathf.CeilToInt(2f * halfH * fullHeight));
        // the crop's size only grows (until the eyepieces let go): a new size makes every texture anew and the history
        // starts over, which showed as a frame of raw bright lines each time the view's field changed (sprinting)
        if (needW > _cropW || _cropW > fullWidth)
            _cropW = Mathf.Min(fullWidth, needW + 8);
        if (needH > _cropH || _cropH > fullHeight)
            _cropH = Mathf.Min(fullHeight, needH + 8);
        int w = _cropW, h = _cropH;
        halfW = 0.5f * w / fullWidth;
        halfH = 0.5f * h / fullHeight;
        int x = Mathf.Clamp(Mathf.RoundToInt((f.x - halfW) * fullWidth), 0, fullWidth - w);
        int y = Mathf.Clamp(Mathf.RoundToInt((f.y - halfH) * fullHeight), 0, fullHeight - h);
        return new Vector4((float)x / fullWidth, (float)y / fullHeight, (float)w / fullWidth, (float)h / fullHeight);
    }
    private static readonly int LitSoftId = Shader.PropertyToID("_EnvgLitSoft");
    private static readonly int MotionId = Shader.PropertyToID("_EnvgMotion");
    private static readonly int EdgeHistoryId = Shader.PropertyToID("_EnvgEdgeHistory");
    private static readonly int TemporalId = Shader.PropertyToID("_EnvgTemporal");
    private static bool _motionReady, _loggedMotion;
    private static CommandBuffer? _motionCopy;
    private static readonly int AlbedoSoftId = Shader.PropertyToID("_EnvgAlbedoSoft");
    private static bool _loggedSun;

    // How much of the day's heat is left in the ground, stones, trunks and walls: all of it while the sun is up, then
    // fading through the night (about 1/e every four hours after dusk at 19:00), so by dawn most things sit at the air's
    // temperature and only bodies, engines and the cold sky stand out. From the game's own clock.
    private static float _nightLeft = 1f;
    private static int _nightFrame = -1;
    private static bool _loggedNight;
    private static float NightHeatLeft()
    {
        if (_nightFrame == Time.frameCount)
            return _nightLeft;
        _nightFrame = Time.frameCount;
        float left = 1f;
        try
        {
            if (Daylight() < 0.05f && Comfort.Common.Singleton<EFT.GameWorld>.Instantiated && Comfort.Common.Singleton<EFT.GameWorld>.Instance.GameDateTime != null)
            {
                System.DateTime clock = Comfort.Common.Singleton<EFT.GameWorld>.Instance.GameDateTime.Calculate();
                float hour = clock.Hour + clock.Minute / 60f;
                float sinceDusk = (hour - 19f + 24f) % 24f;
                if (sinceDusk < 12f)
                    left = Mathf.Exp(-sinceDusk / 4f);
                if (!_loggedNight)
                {
                    _loggedNight = true;
                    EnvgFusionPlugin.Log?.LogInfo("F-PANO night: game clock " + clock.ToString("HH:mm") + ", the day's heat left " + left.ToString("F2"));
                }
            }
        }
        catch (System.Exception)
        {
        }
        _nightLeft = left;
        return left;
    }

    // how much the sun is up and shining (0 at night): the scene's sun light, its intensity and its height
    private static float Daylight()
    {
        Light? sun = RenderSettings.sun;
        float day = 0f;
        if (sun != null && sun.isActiveAndEnabled)
            day = Mathf.Clamp01(sun.intensity) * Mathf.Clamp01(-sun.transform.forward.y * 4f);
        if (!_loggedSun)
        {
            _loggedSun = true;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO daylight from " + (sun != null ? sun.name + " intensity " + sun.intensity.ToString("F2") + " height " + (-sun.transform.forward.y).ToString("F2") : "no sun") + ": " + day.ToString("F2"));
        }
        return day;
    }
    // The instanced grass is drawn in two steps: its MotionVectors pass first writes its depth (GPUInstancer puts it in
    // a command buffer before the G-buffer), then its deferred pass draws only where that depth is (equal depth). The
    // thermal camera gets the first step here too, or the second draws almost nothing.
    private static Camera? _main;
    private static CommandBuffer? _overlayDraw;
    private static CommandBuffer? _screenDraw;
    private static bool _loggedOverlay;
    private static bool _loggedRender;

    private static readonly int ParamsId = Shader.PropertyToID("_EnvgParams");
    private static readonly int CoreId = Shader.PropertyToID("_EnvgCore");
    private static readonly int HaloId = Shader.PropertyToID("_EnvgHalo");
    private static readonly int BlurId = Shader.PropertyToID("_EnvgBlur");
    private static readonly int NormalsId = Shader.PropertyToID("_EnvgNormals");
    private static readonly int Params2Id = Shader.PropertyToID("_EnvgParams2");
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int HeatViewId = Shader.PropertyToID("_EnvgHeatView");
    private static readonly int SceneUnderId = Shader.PropertyToID("_EnvgScene");
    private static readonly int WashoutId = Shader.PropertyToID("_EnvgWashout");
    private static bool _loggedScene;
    private static bool _hePending;
    private static float _heLastTime;

    // the fused display's equalization, from the small picture of this frame read back (a frame or two later)
    private static void RequestHistogram()
    {
        string show = EnvgFusionPlugin.Show?.Value ?? "";
        if (_hePending || EnvgChain.HeSmall == null || !SystemInfo.supportsAsyncGPUReadback
            || (show != EnvgFusionPlugin.ShowFusion && show != EnvgFusionPlugin.ShowBlackHot))
            return;
        _hePending = true;
        AsyncGPUReadback.Request(EnvgChain.HeSmall, 0, TextureFormat.RFloat, request =>
        {
            _hePending = false;
            if (request.hasError)
                return;
            float now = Time.unscaledTime;
            EnvgChain.BuildLut(request.GetData<float>(), (EnvgFusionPlugin.MinSpread?.Value ?? 3f) * 4f, now - _heLastTime, false);
            _heLastTime = now;
        });
    }
    private static readonly int FusionId = Shader.PropertyToID("_EnvgFusion");

    // a mode switched: the thermal picture starts anew (no lines or history from before)
    internal static void ModeChanged()
    {
        EnvgChain.ResetHistory();
        _ringFilled = 0;
    }
    private static readonly int ParallaxId = Shader.PropertyToID("_EnvgParallax");
    private static readonly int FieldId = Shader.PropertyToID("_EnvgField");
    private static readonly int GlowTexId = Shader.PropertyToID("_EnvgGlow");
    private static readonly AccessTools.FieldRef<SSAAImpl, Material> SsaaMaterial = AccessTools.FieldRefAccess<SSAAImpl, Material>("RenderTextureMaterialBicubic2");

    // the picture SSAAImpl drew this frame: the texture on its own drawing material
    private static Texture? SsaaPicture(SSAAImpl ssaa)
    {
        Material? material = SsaaMaterial(ssaa);
        return material != null ? material.GetTexture(MainTexId) : null;
    }

    // the thermal picture's rectangle within the tubes' field, and the thermal lens's height over the tubes
    private static void SetFieldAndParallax(int width, int height)
    {
        float field = EnvgFusionPlugin.ThermalField?.Value ?? 1f;
        // the real device's rectangle: 0.68 of the tubes' circle (the screen's height) wide, 0.52 high, times the field: half sizes in uv
        float halfH = 0.26f * field, halfW = 0.34f * field * height / Mathf.Max(width, 1);
        Material!.SetVector(FieldId, new Vector4(halfW, halfH, 4f / Mathf.Max(height, 1), field > 0f ? 1f : 0f));
        // (over the screen: at its centre, faded as the right pod swings up)
        float shown = EnvgPods.RightShown;
        Material.SetVector(FieldAtId, shown < 1f ? new Vector4(0.5f, 0.5f, shown, 1f) : Vector4.zero);
        // (no shift for the lens's place: the thermal picture lies exactly over the tube's; a shift by a fixed offset
        // doubled near things, the gun, where the per-pixel distance was guessed wrong)
        Material.SetVector(ParallaxId, Vector4.zero);
    }

    // The thermal channel's latency: the lines, their glow and the heat shown from a few frames back (as many as the
    // set latency spans at the present frame rate), while the tubes' picture is the present one.
    private const int RingSize = 4;
    private static readonly RenderTexture?[] _ringRidge = new RenderTexture?[RingSize], _ringGlow = new RenderTexture?[RingSize], _ringHeat = new RenderTexture?[RingSize];
    private static int _ringHead, _ringFilled;
    private static void DelayThermal(string show)
    {
        if (Ridge == null || Glow == null || HistoryB == null || Material == null)
            return;
        float latency = (EnvgFusionPlugin.Latency?.Value ?? 40f) * 0.001f;
        if (latency <= 0f)
        {
            _ringFilled = 0;
            Material.SetTexture(GlowTexId, Glow);
            Material.SetTexture(HeatViewId, HistoryB);
            return;
        }
        _ringHead = (_ringHead + 1) % RingSize;
        RenderTexture ridge = Ensure(ref _ringRidge[_ringHead], Ridge.width, Ridge.height, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO lines, delayed");
        RenderTexture glow = Ensure(ref _ringGlow[_ringHead], Glow.width, Glow.height, 0, RenderTextureFormat.RHalf, "Justachae F-PANO glow, delayed");
        RenderTexture heat = Ensure(ref _ringHeat[_ringHead], HistoryB.width, HistoryB.height, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO heat, delayed");
        Graphics.Blit(Ridge, ridge);
        Graphics.Blit(Glow, glow);
        Graphics.Blit(HistoryB, heat);
        _ringFilled = Mathf.Min(_ringFilled + 1, RingSize);
        int back = Mathf.Clamp(Mathf.RoundToInt(latency / Mathf.Max(Time.smoothDeltaTime, 0.001f)), 0, _ringFilled - 1);
        int at = (_ringHead - back + RingSize) % RingSize;
        Material.SetTexture(MainTexId, _ringRidge[at]);
        Material.SetTexture(GlowTexId, _ringGlow[at]);
        Material.SetTexture(HeatViewId, _ringHeat[at]);
    }

    private static readonly int HistoryId = Shader.PropertyToID("_EnvgHistory");
    private static readonly int LagId = Shader.PropertyToID("_EnvgLag");
    private static readonly int I2Id = Shader.PropertyToID("_EnvgI2");
    private static readonly int I2TimeId = Shader.PropertyToID("_EnvgI2Time");
    private static readonly int AreaId = Shader.PropertyToID("_EnvgArea");
    private static readonly int ReduceId = Shader.PropertyToID("_EnvgReduce");
    private static readonly int AbcId = Shader.PropertyToID("_EnvgAbc");
    private static readonly int AgcId = Shader.PropertyToID("_EnvgAgc");
    private static readonly int SurroundId = Shader.PropertyToID("_EnvgSurround");
    private static readonly int DepthId = Shader.PropertyToID("_EnvgDepth");
    private static readonly int AlbedoId = Shader.PropertyToID("_EnvgAlbedo");
    private static readonly int SpecularId = Shader.PropertyToID("_EnvgSpecular");
    private static readonly int ThermoId = Shader.PropertyToID("_EnvgThermo");
    private static readonly int Thermo2Id = Shader.PropertyToID("_EnvgThermo2");
    private static readonly int DensityId = Shader.PropertyToID("_EnvgDensity");
    private static readonly int DeclutterId = Shader.PropertyToID("_EnvgDeclutter");
    private static readonly int GlowId = Shader.PropertyToID("_EnvgGlow");
    private static readonly int LocalId = Shader.PropertyToID("_EnvgLocal");
    private static readonly int ScreenTexelId = Shader.PropertyToID("_EnvgScreenTexel");
    private static readonly int GeometryId = Shader.PropertyToID("_EnvgGeometry");
    private static readonly int MixId = Shader.PropertyToID("_EnvgMix");
    private static readonly int GeoNearId = Shader.PropertyToID("_EnvgGeoNear");
    private static readonly int GeoFarId = Shader.PropertyToID("_EnvgGeoFar");
    private static readonly int CamToWorldId = Shader.PropertyToID("_EnvgCamToWorld");
    private static readonly int TanId = Shader.PropertyToID("_EnvgTan");

    internal static bool Active => EnvgFusionPlugin.On?.Value == true || EnvgbDown;

    // ---- the device: the F-PANO (either colour) on the local player's head, flipped down ----
    private static readonly string[] EnvgbIds = { "6a4f9a400000000000000001", "6a4f9a400000000000000002" };   // the F-PANO, FDE and black
    private static int _envgbFrame = -1;
    private static bool _envgbDown, _lastIsEnvgb, _loggedEnvgb;
    private static object? _lastNvItem;

    // As the game itself tells whether the goggles are down (its own toggle sound reads the same): the night-vision
    // component in the headwear slot, its togglable on. Read once a frame; the item's template looked up only when the
    // item changes.
    // the F-PANO on the local player's head (down or up), and who wears it: for its pods
    internal static EFT.Player? Wearer;
    internal static EFT.InventoryLogic.Item? WornDevice;
    // the worn goggle's rails have no battery: it goes down, but shows nothing (the tubes dark, no thermal)
    internal static bool Unpowered;

    internal static bool EnvgbDown
    {
        get
        {
            if (_envgbFrame == Time.frameCount)
                return _envgbDown;
            _envgbFrame = Time.frameCount;
            _envgbDown = false;
            Wearer = null;
            WornDevice = null;
            Unpowered = false;
            try
            {
                if (!Singleton<EFT.GameWorld>.Instantiated)
                    return false;
                EFT.Player? player = Singleton<EFT.GameWorld>.Instance.MainPlayer;
                EFT.InventoryLogic.NightVisionComponent? nv = player?.NightVisionObserver?.Component;
                if (nv == null)
                    return false;
                if (!ReferenceEquals(nv.Item, _lastNvItem))
                {
                    _lastNvItem = nv.Item;
                    string id = nv.Item.TemplateId.ToString();
                    _lastIsEnvgb = id == EnvgbIds[0] || id == EnvgbIds[1];
                    if (_lastIsEnvgb && !_loggedEnvgb)
                    {
                        _loggedEnvgb = true;
                        EnvgFusionPlugin.Log?.LogInfo("F-PANO on the head (" + id + "): the thermal channel follows its toggle");
                    }
                }
                _envgbDown = _lastIsEnvgb && (nv.Togglable == null || nv.Togglable.On);
                if (_lastIsEnvgb)
                {
                    Wearer = player;
                    WornDevice = nv.Item;
                    Unpowered = FpanoRailink.Unpowered(nv.Item);
                }
            }
            catch (System.Exception)
            {
                _envgbDown = false;
            }
            return _envgbDown;
        }
    }

    internal static void Update()
    {
        Camera? main = Active ? Camera.main : null;
        _main = main;
        if (main == null)
        {
            if (!Active)
                Idle();
            return;
        }
        if (!EnsureMaterial())
            return;
        _idle = false;
        _overlayDraw ??= new CommandBuffer { name = "Justachae F-PANO edges" };
    }

    // The thermal picture through the sensor and the outline processing, ready to run once the main camera's frame is
    // finished. Everything runs at twice the detector's resolution: computed on the detector's own grid, a slanted
    // edge's response rises and falls along it with where it crosses the pixels (5 to 12 percent), and near the
    // threshold that broke the line into beads; at twice the resolution the ripple is 0 to 4 percent.
    private static readonly EnvgChainInput _chain = new EnvgChainInput();

    // the processing chain's input from the mod: this frame's textures, the camera and the settings
    private static EnvgChainInput ChainInput(int width, int height)
    {
        EnvgChainInput t = _chain;
        t.Picture = Picture; t.Normals = Normals; t.Depth = Depth; t.Albedo = Albedo; t.Specular = Specular; t.Body = BodyMask;
        t.Heat2x = Heat2x; t.Seams = Seams; t.GeoSource = GeoSource; t.GeoAcross = GeoAcross; t.GeoNear = GeoNear; t.GeoFar = GeoFar; t.NormalsSoft = NormalsSoft; t.Foliage = Foliage; t.HeatBase = HeatBase; t.GrassMask = GrassMask;
        t.HistoryB = HistoryB; t.Blurred = Blurred; t.Surround = Surround; t.Across = Across; t.Edges = Edges; t.Presence = Presence;
        t.Density = Density; t.Glow = Glow; t.LocalSquare = LocalSquare; t.LocalMoments = LocalMoments; t.Ridge = Ridge;
        t.LitSoft = LitSoft; t.AlbedoSoft = AlbedoSoft; t.Motion = Motion; t.EdgeHistA = EdgeHistA; t.EdgeHistB = EdgeHistB;
        t.FromMain = true; t.MotionReady = _motionReady; t.Frame = Time.frameCount;
        t.Width = width; t.Height = height; t.Oversample = Oversample;
        t.CamToWorld = _camToWorld; t.Tan = _tan; t.DeltaTime = Time.unscaledDeltaTime;
        t.Amount = EnvgFusionPlugin.Amount?.Value ?? 1f;
        t.Softness = EnvgFusionPlugin.Softness?.Value ?? 0.4f;
        t.Brightness = EnvgFusionPlugin.Brightness?.Value ?? 1f;
        t.GlowAmount = EnvgFusionPlugin.Glow?.Value ?? 0.6f;
        float night = NightHeatLeft();
        t.ResidualHeat = (EnvgFusionPlugin.ResidualHeat?.Value ?? 4f) * night;
        t.BodyHeat = EnvgFusionPlugin.BodyHeat?.Value ?? 1f;
        t.GroundPatches = (EnvgFusionPlugin.GroundPatches?.Value ?? 2f) * (0.3f + 0.7f * night);
        t.Air = EnvgFusionPlugin.AirTemperature?.Value ?? 15f;
        t.LightHeating = EnvgFusionPlugin.LightHeating?.Value ?? 15f;
        t.Daylight = Daylight();
        t.SkyCooling = EnvgFusionPlugin.SkyCooling?.Value ?? 4f;
        t.Smoothing = EnvgFusionPlugin.Smoothing?.Value ?? 1f;
        t.HeatEdges = EnvgFusionPlugin.HeatEdges?.Value ?? 1f;
        t.GeometryEdges = EnvgFusionPlugin.GeometryEdges?.Value ?? 0f;
        t.LocalContrast = EnvgFusionPlugin.LocalContrast?.Value ?? 0.35f;
        t.ShapeNeedsHeat = EnvgFusionPlugin.ShapeNeedsHeat?.Value ?? 1f;
        t.Temporal = EnvgFusionPlugin.Temporal?.Value ?? 0.8f;
        t.HeatTemporal = t.Temporal;
        t.Declutter = EnvgFusionPlugin.Declutter?.Value ?? 0f;
        t.MinSpread = EnvgFusionPlugin.MinSpread?.Value ?? 3f;
        t.NativeMethod = EnvgFusionPlugin.HeatSource?.Value == EnvgFusionPlugin.SourceNative;
        t.GrassGain = EnvgFusionPlugin.GrassAmount?.Value ?? 2f;
        // the grass from our model joins the game's method in the outline mode only
        t.NativeGrass = EnvgFusionPlugin.Show?.Value == EnvgFusionPlugin.ShowOutline;
        return t;
    }

    private static void FillOverlay(int width, int height)
    {
        if (_overlayDraw == null || Material == null || Picture == null || Normals == null || Depth == null || Albedo == null || Specular == null || Heat2x == null || Seams == null || GeoSource == null || GeoAcross == null || GeoNear == null || GeoFar == null
            || HistoryB == null || Blurred == null || Surround == null || Across == null || Edges == null || Presence == null || Density == null || Glow == null || LocalSquare == null || LocalMoments == null
            || Ridge == null || LitSoft == null || AlbedoSoft == null)
            return;
        _overlayDraw.Clear();
        _overlayDraw.BeginSample(FpanoProbe.Chain);
        EnvgChain.Fill(_overlayDraw, Material, ChainInput(width, height));
        _overlayDraw.EndSample(FpanoProbe.Chain);
    }

    private static void Blur(CommandBuffer cb, RenderTexture from, RenderTexture to, float step, int width, int height, RenderTexture? across = null)
    {
        across ??= Across;
        cb.SetGlobalVector(BlurId, new Vector4(step / width, 0f, 0f, 0f));
        cb.Blit(from, across, Material, 2);
        cb.SetGlobalVector(BlurId, new Vector4(0f, step / height, 0f, 0f));
        cb.Blit(across, to, Material, 2);
    }

    // The game's SSAAImpl is the main camera's last step: it draws the frame straight onto the screen (sized to the
    // screen, through a mesh flipped for it). Anything after it, another image effect or another camera drawing to the
    // screen, makes Unity hand it an in-between texture instead, and the frame comes out small and upside down. So the
    // edges are added right as it finishes, onto the same output.
    internal static void AfterFinalImage(SSAAImpl __instance, RenderTexture destination)
    {
        if (!Active || _main == null || __instance == null || __instance.gameObject != _main.gameObject
            || _overlayDraw == null || Material == null || Edges == null || Picture == null)
            return;
        // K-3PS-C's eyepieces show the device this frame: the picture went into them (Fuse), nothing over the screen.
        // Without them, over the screen only once the game's night vision is on (the device all the way down), or
        // with the debug switch.
        if (EyepieceLink.ClaimedNow || (EnvgFusionPlugin.On?.Value != true && !GameNightVisionOn(_main)))
            return;
        try
        {
            if (WhitePhosphorOn && _tubeFrame != Time.frameCount && (EnvgbDown || !GameNightVisionOn(_main)))
            {
                _tubeFrame = Time.frameCount;
                MeasureBrightness(_main);
            }
            if (EnvgFusionPlugin.Show?.Value == EnvgFusionPlugin.ShowI2 || EnvgPods.RightShown <= 0f)
                return;
            if (!FpanoProbe.NoChain) Graphics.ExecuteCommandBuffer(_overlayDraw);
            RequestHistogram();
            // Drawn the way SSAAImpl draws the frame: its target, the full output as the viewport (the camera's own is
            // still the smaller render size here) and its full-screen mesh, flipped as it flips it.
            string show = EnvgFusionPlugin.Show?.Value ?? EnvgFusionPlugin.ShowOutline;
            Material.SetTexture(MainTexId, Ridge);
            Mesh? mesh = __instance.FlippedV ? __instance.FullScreenYFlippedMesh : __instance.FullScreenMesh;
            if (mesh == null)
                return;
            int width = destination != null ? destination.width : Screen.width;
            int height = destination != null ? destination.height : Screen.height;
            _screenDraw ??= new CommandBuffer { name = "Justachae F-PANO overlay" };
            _screenDraw.Clear();
            // the tube's picture under the overlay: what SSAAImpl itself just drew, read through the same mesh (it is the
            // texture on its material), so the lines sink into it where it is bright, whether it drew to the screen or not
            float washout = EnvgFusionPlugin.Washout?.Value ?? 1f;
            Texture? scene = SsaaPicture(__instance);
            if (scene != null && washout > 0f)
                Material.SetTexture(SceneUnderId, scene);
            else
                washout = 0f;
            Material.SetVector(WashoutId, new Vector4(washout, 0f, 0f, 0f));
            if (!_loggedScene)
            {
                _loggedScene = true;
                EnvgFusionPlugin.Log?.LogInfo("F-PANO tube picture under the overlay: " + (scene != null ? scene.name + " " + scene.width + "x" + scene.height : "none (no fading in bright places)"));
            }
            SetFieldAndParallax(width, height);
            DelayThermal(show);
            _screenDraw.SetGlobalVector(I2DimId, Vector4.zero);
            _screenDraw.SetRenderTarget(destination);
            _screenDraw.SetViewport(new Rect(0f, 0f, width, height));
            int pass = show == EnvgFusionPlugin.ShowFusion || show == EnvgFusionPlugin.ShowBlackHot ? 21 : 3;
            Material.SetVector(FusionId, new Vector4(show == EnvgFusionPlugin.ShowBlackHot ? -1f : 1f, 0.85f, 0f, 0f));
            Material.SetVector(ScreenTexelId, new Vector4(1f / Mathf.Max(width, 1), 1f / Mathf.Max(height, 1), EnvgFusionPlugin.LineWidth?.Value ?? 1.5f, 0f));
            _screenDraw.DrawMesh(mesh, Matrix4x4.identity, Material, 0, pass);
            Graphics.ExecuteCommandBuffer(_screenDraw);
            // (over the screen the white phosphor itself carries the gain: the picture under the lines is already dimmed)
            if (!_loggedOverlay)
            {
                _loggedOverlay = true;
                EnvgFusionPlugin.Log?.LogInfo("F-PANO overlay drawn after SSAAImpl onto " + (destination != null ? destination.name + " " + destination.width + "x" + destination.height : "the screen"));
            }
        }
        catch (System.Exception e)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO overlay failed: " + e);
        }
    }

    // ---- into K-3PS-C's eyepieces (EyepieceFusion v1) ----
    // Called from their image effect, after the main camera's own rendering and before SSAAImpl: so the chain runs
    // here on a claimed frame (AfterFinalImage then draws nothing). i2 is the tube's picture, dest the same size and
    // format, both in the main camera's screen uv; the thermal picture is laid into dest the way it is laid over the
    // screen otherwise, within the tubes' field where their eyepieces put it.
    private static int _fusedFrame = -1;
    private static Mesh? _fuseQuad;
    private static readonly int FieldAtId = Shader.PropertyToID("_EnvgFieldAt");
    internal static bool FuseInto(RenderTexture i2, RenderTexture dest)
    {
        if (!Active || _main == null || _overlayDraw == null || Material == null || Edges == null || Picture == null || Ridge == null)
            return false;
        string show = EnvgFusionPlugin.Show?.Value ?? EnvgFusionPlugin.ShowOutline;
        // (the right pod swung up: its tube, the thermal module with it, is off)
        if (show == EnvgFusionPlugin.ShowI2 || EnvgPods.RightShown <= 0f)
            return false;
        if (_fusedFrame != Time.frameCount)
        {
            // the chain and the latency ring move on once a frame, however many times the eyepieces ask
            _fusedFrame = Time.frameCount;
            if (!FpanoProbe.NoChain) Graphics.ExecuteCommandBuffer(_overlayDraw);
            RequestHistogram();
            DelayThermal(show);
        }
        if (_fuseQuad == null)
        {
            // the screen's corners straight into clip space (the shader's vertScreen), upside down where a texture's
            // first row is its top
            float y = SystemInfo.graphicsUVStartsAtTop ? -1f : 1f;
            _fuseQuad = new Mesh
            {
                name = "Justachae F-PANO eyepiece quad",
                vertices = new[] { new Vector3(-1f, -y, 0f), new Vector3(-1f, y, 0f), new Vector3(1f, y, 0f), new Vector3(1f, -y, 0f) },
                uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
            };
            _fuseQuad.UploadMeshData(true);
        }
        Material.SetTexture(MainTexId, Ridge);
        float washout = EnvgFusionPlugin.Washout?.Value ?? 1f;
        Material.SetTexture(SceneUnderId, i2);
        Material.SetVector(WashoutId, new Vector4(washout, 0f, 0f, 0f));
        // the thermal rectangle around the tubes' axis, as the real device shows it: 0.68 of the tube's circle wide and
        // 0.52 of it high (measured on the maker's picture through the eyepiece), times the set share
        Vector4 at = EyepieceLink.FieldInMainUv;
        _lastField = at;
        _lastFieldFrame = Time.frameCount;
        float radius = Mathf.Max(at.z, 1e-3f), aspect = Mathf.Max(at.w, 1e-3f);
        float share = EnvgFusionPlugin.ThermalField?.Value ?? 1f;
        Material.SetVector(FieldId, new Vector4(EyepieceLink.FusedRect.x * radius * share / aspect, EyepieceLink.FusedRect.y * radius * share, 4f / Mathf.Max(dest.height, 1), share > 0f ? 1f : 0f));
        // faded in as the device settles, and out as the right pod (with the thermal module) swings up
        float settled = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.85f, 1f, EyepieceLink.Deployed)) * EnvgPods.RightShown;
        Material.SetVector(FieldAtId, new Vector4(at.x, at.y, settled, 1f));
        // (no shift for the lens's place, as on the screen)
        Material.SetVector(ParallaxId, Vector4.zero);
        int pass = show == EnvgFusionPlugin.ShowFusion || show == EnvgFusionPlugin.ShowBlackHot ? 21 : 3;
        Material.SetVector(FusionId, new Vector4(show == EnvgFusionPlugin.ShowBlackHot ? -1f : 1f, 0.85f, 0f, 0f));
        Material.SetVector(ScreenTexelId, new Vector4(1f / Mathf.Max(dest.width, 1) / _crop.z, 1f / Mathf.Max(dest.height, 1) / _crop.w, EnvgFusionPlugin.LineWidth?.Value ?? 1.5f, 0f));
        _fuseDraw ??= new CommandBuffer { name = "Justachae F-PANO into the eyepieces" };
        _fuseDraw.Clear();
        _fuseDraw.BeginSample(FpanoProbe.Fuse);
        // the tube's picture at the set gain (0: the tubes dark, the thermal picture alone)
        // (handed to the eyepieces for both tubes when they take it; else applied here to the right one)
        EyepieceLink.SetGain(I2Gain);
        // (a plain copy whenever nothing is to be dimmed here: the copy through our own pass 37 put green fringes along
        // every bright edge of the tube's picture, in game; found by the user, gone with the plain copy)
        bool dimHere = !EyepieceLink.TakesGain && I2Gain < 0.999f;
        _fuseDraw.SetGlobalVector(I2DimId, new Vector4(dimHere ? 1f - I2Gain : 0f, 0f, 0f, 0f));
        if (dimHere)
            _fuseDraw.Blit(i2, dest, Material, 37);
        else
            _fuseDraw.Blit(i2, dest);
        _fuseDraw.SetRenderTarget(dest);
        _fuseDraw.SetViewport(new Rect(0f, 0f, dest.width, dest.height));
        _fuseDraw.DrawMesh(_fuseQuad, Matrix4x4.identity, Material, 0, pass);
        _fuseDraw.EndSample(FpanoProbe.Fuse);
        Graphics.ExecuteCommandBuffer(_fuseDraw);
        // the screen's own draw keeps the field at the centre
        Material.SetVector(FieldAtId, Vector4.zero);
        if (!_loggedFuse)
        {
            _loggedFuse = true;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO picture laid into the K-3PS-C eyepieces (" + i2.width + "x" + i2.height + " " + i2.format + ", field at "
                + at.x.ToString("F3") + "," + at.y.ToString("F3") + " radius " + at.z.ToString("F3") + ", " + show + ")");
        }
        return true;
    }
    private static CommandBuffer? _fuseDraw;

    private static readonly int I2DimId = Shader.PropertyToID("_EnvgI2Dim");
    // (no battery in the rails: the tubes dark)
    internal static float I2Gain => EnvgbDown && Unpowered ? 0f : EnvgFusionPlugin.GainNow;




    private static bool _loggedFuse;

    internal static bool TubesWanted => Active && EnvgFusionPlugin.WhitePhosphor?.Value == true;
    internal static bool WhitePhosphorOn => TubesWanted && _i2Material != null;

    // The game's night vision on the main camera, if the player has goggles on and switched on: then its picture stays.
    private static bool GameNightVisionOn(Camera main)
    {
        var nv = main.GetComponent<BSG.CameraEffects.NightVision>();
        return nv != null && nv.enabled && nv.On;
    }

    // Our white phosphor material where the game's night vision goes: SSAAPropagator's night-vision material, which it
    // blits over the HDR frame before tone mapping. Set before the frame (the post-processing builds its buffers in the
    // camera's pre-cull) and again right after the game's NightVision clears it in its own pre-cull.
    internal static void SetWhitePhosphor()
    {
        // (another device's own night vision is left alone; the F-PANO's tubes are the white phosphor's)
        if (!WhitePhosphorOn || _main == null || (GameNightVisionOn(_main) && !EnvgbDown) || EyepieceLink.ClaimedRecently)
            return;
        SSAAPropagator? propagator = _main.GetComponent<SSAAPropagator>();
        if (propagator == null)
            return;
        bool abc = _abcBuilt >= 1;   // measured at least once
        _i2Material!.SetTexture(AbcId, AbcB);
        _i2Material!.SetVector(I2Id, new Vector4(100f * (EnvgFusionPlugin.TubeBrightness?.Value ?? 1f) * I2Gain, EnvgFusionPlugin.I2Noise?.Value ?? 1f, 0f, 1.5f));
        _i2Material.SetVector(I2TimeId, new Vector4(Mathf.Floor(Time.time * 30f) % 1024f, abc ? 1f : 0f, 0f, 0f));
        propagator.SetNightVisionMaterial(_i2Material);
        if (!_loggedI2)
        {
            _loggedI2 = true;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO white phosphor set as the night-vision material of " + _main.name);
        }
    }

    // ---- the grass mask: the grass's shape drawn once more, inside the main camera's own render (after its G-buffer,
    // against its depth, from the instances GPUInstancer culled for it this frame), into a buffer of its own ----
    private static CommandBuffer? _grassMaskCb;
    private static Camera? _grassMaskCamera;
    private static int _grassMaskFrame = -1, _grassMaskDraws;
    private static bool _loggedGrassMask;
    // the grass shader's passes to draw its shape with, in order of preference; the first that leaves anything in the
    // mask is kept (checked on the mask itself, read back every half second)
    private static readonly string[] GrassPasses = { "FORWARD", "DEFERRED", "MotionVectors", "DepthPrePass" };
    private static int _grassPass, _grassEmpty, _grassCheckFrame;
    private static bool _grassChecking, _grassPassFound;

    private static void CheckGrassMask()
    {
        if (_grassPassFound || _grassChecking || GrassMask == null || _grassMaskDraws == 0 || !SystemInfo.supportsAsyncGPUReadback
            || Time.frameCount - _grassCheckFrame < 30)
            return;
        _grassCheckFrame = Time.frameCount;
        _grassChecking = true;
        int draws = _grassMaskDraws;
        AsyncGPUReadback.Request(GrassMask, 0, TextureFormat.RHalf, request =>
        {
            _grassChecking = false;
            if (request.hasError)
                return;
            var data = request.GetData<ushort>();
            int covered = 0;
            for (int i = 0; i < data.Length; i += 7)
                if (Mathf.HalfToFloat(data[i]) > 0.01f)
                    covered++;
            if (covered > 0)
            {
                _grassPassFound = true;
                EnvgFusionPlugin.Log?.LogInfo("F-PANO grass mask works with the " + GrassPasses[_grassPass] + " pass (" + covered + " of " + (data.Length / 7) + " sampled pixels are grass, " + draws + " draws)");
                return;
            }
            if (++_grassEmpty >= 2 && _grassPass < GrassPasses.Length - 1)
            {
                EnvgFusionPlugin.Log?.LogInfo("F-PANO grass mask: the " + GrassPasses[_grassPass] + " pass left nothing (" + draws + " draws); trying " + GrassPasses[_grassPass + 1]);
                _grassPass++;
                _grassEmpty = 0;
            }
        });
    }
    private static readonly int GrassScreenId = Shader.PropertyToID("_EnvgGrassScreen");
    internal static bool GrassMaskWanted => EnvgFusionPlugin.Show?.Value == EnvgFusionPlugin.ShowOutline
        && EnvgFusionPlugin.HeatSource?.Value == EnvgFusionPlugin.SourceNative;

    private static void RecordGrassMask(System.Collections.IList list, int layerMask)
    {
        if (_main == null || !GrassMaskWanted)
            return;
        if (_grassMaskCb == null || _grassMaskCamera != _main)
        {
            if (_grassMaskCb != null && _grassMaskCamera != null)
                _grassMaskCamera.RemoveCommandBuffer(CameraEvent.AfterGBuffer, _grassMaskCb);
            _grassMaskCb ??= new CommandBuffer { name = "Justachae F-PANO grass mask" };
            _main.AddCommandBuffer(CameraEvent.AfterGBuffer, _grassMaskCb);
            _grassMaskCamera = _main;
        }
        if (_grassMaskFrame != Time.frameCount)
        {
            _grassMaskFrame = Time.frameCount;
            _grassMaskDraws = 0;
            _grassMaskCb.Clear();
            _grassMaskCb.GetTemporaryRT(GrassScreenId, -1, -1, 0, FilterMode.Point, RenderTextureFormat.RHalf);
            _grassMaskCb.SetRenderTarget(GrassScreenId, BuiltinRenderTextureType.CameraTarget);
            _grassMaskCb.ClearRenderTarget(false, true, new Color(-100f, -100f, -100f, -100f));
        }
        string passName = "";
        foreach (object entry in list)
        {
            if (entry is not GPUInstancer.GPUInstancerRuntimeData data || data.transformationMatrixVisibilityBuffer == null
                || data.bufferSize == 0 || data.instanceCount == 0 || data.instanceLODs == null || data.argsBuffer == null)
                continue;
            foreach (GPUInstancer.GPUInstancerPrototypeLOD lod in data.instanceLODs)
            {
                if (lod?.renderers == null)
                    continue;
                foreach (GPUInstancer.GPUInstancerRenderer renderer in lod.renderers)
                {
                    if (renderer?.materials == null || renderer.mesh == null || (layerMask & (1 << renderer.layer)) == 0)
                        continue;
                    for (int m = 0; m < renderer.materials.Count; m++)
                    {
                        Material material = renderer.materials[m];
                        if (material == null)
                            continue;
                        // its motion-vector pass: the shape only (the grass's own vertex animation, its alpha cut), drawn
                        // where the frame's depth says it is in front
                        passName = GrassPasses[_grassPass];
                        int pass = material.FindPass(passName);
                        if (pass < 0)
                            continue;
                        int sub = Mathf.Min(m, renderer.mesh.subMeshCount - 1);
                        int offset = (renderer.argsBufferOffset + 5 * sub) * GPUInstancer.GPUInstancerConstants.STRIDE_SIZE_INT;
                        _grassMaskCb!.DrawMeshInstancedIndirect(renderer.mesh, sub, material, pass, data.argsBuffer, offset, renderer.mpb);
                        _grassMaskDraws++;
                    }
                }
            }
        }
        if (!_loggedGrassMask && _grassMaskDraws > 0)
        {
            _loggedGrassMask = true;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO grass mask: " + _grassMaskDraws + " grass draws recorded with the " + passName + " pass");
        }
    }

    // at the end of the recording (the main camera's pre-render): the mask down to the sensor's pixels; or, with no
    // grass this frame or no use for it, an empty mask
    private static void FinishGrassMask(Vector2 jitter)
    {
        if (GrassMask == null || Material == null)
            return;
        if (_grassMaskCb == null || _grassMaskFrame != Time.frameCount || !GrassMaskWanted)
        {
            if (_grassMaskCb != null && _grassMaskFrame != Time.frameCount)
                _grassMaskCb.Clear();
            if (_grassMaskCb != null)
            {
                _grassMaskCb.SetRenderTarget(GrassMask);
                _grassMaskCb.ClearRenderTarget(false, true, Color.clear);
            }
            return;
        }
        _grassMaskCb.SetGlobalVector(JitterId, new Vector4(jitter.x, jitter.y, 0f, 0f));
        _grassMaskCb.SetGlobalVector(FootprintId, new Vector4(_crop.z / GrassMask.width, _crop.w / GrassMask.height, 0f, 0f));
        _grassMaskCb.Blit(GrassScreenId, GrassMask, Material, 32);
        _grassMaskCb.ReleaseTemporaryRT(GrassScreenId);
    }

    internal static void AfterGrassDraw(object runtimeDataList, Bounds instancingBounds, GPUInstancer.GPUInstancerCameraData cameraData, int layerMask)
    {
        if (Active && runtimeDataList is System.Collections.IList maskList && cameraData != null && _main != null
            && cameraData.mainCamera == _main)
        {
            try
            {
                RecordGrassMask(maskList, layerMask);
            }
            catch (System.Exception e)
            {
                if (!_loggedGrass)
                {
                    _loggedGrass = true;
                    EnvgFusionPlugin.Log?.LogWarning("F-PANO grass mask failed: " + e);
                }
            }
            return;
        }
    }

    // The tube's brightness control senses the whole picture and settles over a fraction of a second. The game's
    // SSAAPropagator keeps this frame's HDR picture before the night vision in its first resampled target (the night
    // vision material's input); once the frame is done it is reduced to its geometric mean signal and followed with a
    // 0.5 s time constant, which the white phosphor pass uses from the next frame on.
    private static void MeasureBrightness(Camera main)
    {
        if (Material == null || _tubeFrame == -2)
            return;
        SSAAPropagator? propagator = main.GetComponent<SSAAPropagator>();
        RenderTexture[]? targets = propagator != null ? PropagatorHdr(propagator) : null;
        RenderTexture? frame = targets != null && targets.Length > 0 ? targets[0] : null;
        if (frame == null || !frame.IsCreated())
            return;
        // the same reduction and lag passes as the thermal gain control (passes 9 and 10), in a command buffer: the red
        // channel's mean (the photocathode is most sensitive in red and near infrared), followed over half a second
        RenderTexture coarse = Ensure(ref _abcCoarse, 32, 18, 0, RenderTextureFormat.RGFloat, "Justachae F-PANO brightness 32");
        RenderTexture mean = Ensure(ref _abcMean, 1, 1, 0, RenderTextureFormat.RGFloat, "Justachae F-PANO brightness");
        Ensure(ref AbcA, 1, 1, 0, RenderTextureFormat.RGFloat, "Justachae F-PANO brightness A");
        Ensure(ref AbcB, 1, 1, 0, RenderTextureFormat.RGFloat, "Justachae F-PANO brightness B");
        (AbcA, AbcB) = (AbcB, AbcA);
        _abcDraw ??= new CommandBuffer { name = "Justachae F-PANO tube brightness" };
        CommandBuffer cb = _abcDraw;
        cb.Clear();
        cb.SetGlobalVector(ReduceId, new Vector4(4f, 1f / 32f, 1f / 18f, 1f));
        cb.Blit(frame, coarse, Material, 9);
        cb.SetGlobalVector(ReduceId, new Vector4(8f, 1f, 1f, 0f));
        cb.Blit(coarse, mean, Material, 9);
        cb.SetGlobalTexture(HistoryId, AbcA);
        cb.SetGlobalVector(LagId, new Vector4(_abcBuilt == 0 ? 1f : 1f - Mathf.Exp(-Mathf.Max(Time.unscaledDeltaTime, 0f) / 0.5f), 0f, 0f, 0f));
        cb.Blit(mean, AbcB, Material, 10);
        Graphics.ExecuteCommandBuffer(cb);
        _abcBuilt++;
        if (_abcBuilt == 1)
            EnvgFusionPlugin.Log?.LogInfo("F-PANO tube brightness control measuring " + frame.name + " " + frame.width + "x" + frame.height + " (" + frame.format + ")");
    }

    internal static void AfterNightVisionPreCull(BSG.CameraEffects.NightVision __instance)
    {
        if (_main != null && __instance != null && __instance.gameObject == _main.gameObject && (!__instance.On || EnvgbDown))
            SetWhitePhosphor();
    }

    // the shader bundle, loaded once (an asset bundle can be loaded only once), its shaders kept by name
    private static readonly Dictionary<string, Shader> _bundleShaders = new Dictionary<string, Shader>();
    private static bool _bundleLoaded;
    private static string BundlePath => Path.Combine(Path.GetDirectoryName(typeof(EnvgFusion).Assembly.Location) ?? "", "fpanofusion.bundle");
    private static bool LoadBundle()
    {
        if (!_bundleLoaded)
        {
            _bundleLoaded = true;
            AssetBundle? bundle = File.Exists(BundlePath) ? AssetBundle.LoadFromFile(BundlePath) : null;
            if (bundle != null)
                foreach (Shader shader in bundle.LoadAllAssets<Shader>())
                    _bundleShaders[shader.name] = shader;
        }
        return _bundleShaders.Count > 0;
    }
    internal static Shader? BundleShader(string name) => LoadBundle() && _bundleShaders.TryGetValue(name, out Shader shader) ? shader : null;

    private static bool EnsureMaterial()
    {
        if (Material != null)
            return true;
        if (_bundleTried)
            return false;
        _bundleTried = true;
        string path = BundlePath;
        if (LoadBundle())
        {
            foreach (Shader shader in _bundleShaders.Values)
            {
                if (shader.name == "Justachae/FPANO/Outline")
                    _shader = shader;
                else if (shader.name == "Justachae/FPANO/WhitePhosphor")
                    _i2Shader = shader;
                else if (shader.name == "Justachae/FPANO/LensMask" && shader.isSupported)
                    _lensMaskMaterial = new Material(shader) { hideFlags = HideFlags.DontSave };
                else if (shader.name == "Justachae/FPANO/BodyMask" && shader.isSupported)
                    _bodyMaterial = new Material(shader) { hideFlags = HideFlags.DontSave };
            }
        }
        if (_shader == null || !_shader.isSupported)
        {
            EnvgFusionPlugin.Log?.LogError("F-PANO shader missing or unsupported: " + path);
            return false;
        }
        Material = new Material(_shader) { hideFlags = HideFlags.DontSave };
        if (_i2Shader != null && _i2Shader.isSupported)
            _i2Material = new Material(_i2Shader) { hideFlags = HideFlags.DontSave };
        else
            EnvgFusionPlugin.Log?.LogError("F-PANO white phosphor shader missing or unsupported");
        return true;
    }

    // nothing to do while the goggles are up: the command buffers on the camera emptied (the textures kept)
    private static bool _idle;
    private static void Idle()
    {
        if (_idle)
            return;
        _idle = true;
        _mainCopy?.Clear();
        _motionCopy?.Clear();
        _grassMaskCb?.Clear();

    }

    private static void Release()
    {
        if (_mainCopy != null && _mainCopyCamera != null)
            _mainCopyCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, _mainCopy);
        if (_motionCopy != null && _mainCopyCamera != null)
            _mainCopyCamera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, _motionCopy);
        _motionCopy?.Release();
        _motionCopy = null;
        _motionReady = false;
        _mainCopy?.Release();
        _mainCopy = null;
        _mainCopyCamera = null;
        Free(ref Picture);
        for (int r = 0; r < RingSize; r++)
        {
            Free(ref _ringRidge[r]);
            Free(ref _ringGlow[r]);
            Free(ref _ringHeat[r]);
        }
        _ringFilled = 0;
        Free(ref Normals);
        Free(ref Heat2x);
        Free(ref Seams);
        Free(ref GeoSource);
        Free(ref GeoAcross);
        Free(ref GeoNear);
        Free(ref GeoFar);
        Free(ref Depth);
        Free(ref Albedo);
        Free(ref Specular);
        Free(ref HistoryA);
        Free(ref HistoryB);
        _abcBuilt = 0;
        _abcDraw?.Release();
        _abcDraw = null;
        EnvgChain.Release();
        Free(ref _abcCoarse);
        Free(ref _abcMean);
        Free(ref AbcA);
        Free(ref AbcB);
        Free(ref Across);
        Free(ref Blurred);
        Free(ref Surround);
        Free(ref Presence);
        Free(ref Density);
        Free(ref Glow);
        Free(ref LocalSquare);
        Free(ref LocalMoments);
        Free(ref Edges);
        Free(ref Ridge);
        Free(ref LitSoft);
        Free(ref Motion);
        Free(ref NormalsSoft);
        Free(ref Foliage);
        Free(ref HeatBase);
        Free(ref GrassMask);
        if (_grassMaskCb != null && _grassMaskCamera != null)
            _grassMaskCamera.RemoveCommandBuffer(CameraEvent.AfterGBuffer, _grassMaskCb);
        _grassMaskCb?.Release();
        _grassMaskCb = null;
        _grassMaskCamera = null;
        Free(ref BodyMask);
        _bodyRenderers.Clear();
        Free(ref EdgeHistA);
        Free(ref EdgeHistB);
        EnvgChain.ResetHistory();
        Free(ref AlbedoSoft);
        _overlayDraw?.Release();
        _overlayDraw = null;
        _fuseDraw?.Release();
        _fuseDraw = null;
        _screenDraw?.Release();
        _screenDraw = null;
    }

    private static void Free(ref RenderTexture? texture)
    {
        if (texture == null)
            return;
        texture.Release();
        Object.Destroy(texture);
        texture = null;
    }

    private static RenderTexture Ensure(ref RenderTexture? texture, int width, int height, int depth, RenderTextureFormat format, string name)
    {
        if (texture == null || texture.width != width || texture.height != height || texture.format != format)
        {
            Free(ref texture);
            texture = new RenderTexture(width, height, depth, format) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            texture.Create();
        }
        return texture;
    }

    // Right before the main camera renders, when its view for the frame is final (later than its pre-cull, where the
    // game or other mods may still move it), ours renders the thermal picture with that very view. The main camera's
    // target and the active texture are put back after (the game's SSAA has already set its low-resolution target).
    internal static void OnPreRender(Camera camera)
    {
        if (!Active && _mainCopy != null && camera == _mainCopyCamera)
            _mainCopy.Clear();
        if (!Active || camera != _main || _main == null || Material == null)
            return;
        if (EnvgFusionPlugin.Show?.Value == EnvgFusionPlugin.ShowI2 || EnvgPods.RightShown <= 0f)
        {
            _mainCopy?.Clear();
            _motionCopy?.Clear();
            return;
        }
        try
        {
            long probeFrom = FpanoProbe.Now;
            RenderThermal(_main);
            FpanoProbe.AddThermal(probeFrom);
        }
        catch (System.Exception e)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO thermal render failed: " + e);
        }
    }

    private static void RenderThermal(Camera main)
    {

        // the sensor's pixels at the real device's angular resolution over the camera's horizontal field of view
        float aspect = (float)main.pixelWidth / Mathf.Max(main.pixelHeight, 1);
        float across = 2f * Mathf.Atan(Mathf.Tan(main.fieldOfView * 0.5f * Mathf.Deg2Rad) * aspect) * Mathf.Rad2Deg;
        int wanted = Mathf.Clamp(Mathf.RoundToInt((EnvgFusionPlugin.SensorResolution?.Value ?? 16f) * across), 64, 2560);
        // The textures keep the size of the widest view seen: aiming narrows the view over a few frames, and sizing
        // the sensor to each frame's view re-allocated every texture (and dropped the history) each of those frames.
        // A narrower view simply gets more pixels per degree. Re-sized only when the setting or the screen changes.
        int sizedFor = Mathf.RoundToInt((EnvgFusionPlugin.SensorResolution?.Value ?? 16f) * 100f) * 100000
            + (EnvgFusionPlugin.RenderScale?.Value ?? 1) * 10000 + main.pixelWidth * 7 + main.pixelHeight;
        if (sizedFor != _sizedFor)
        {
            _sizedFor = sizedFor;
            _sensorWidth = 0;
            Oversample = Mathf.Clamp(EnvgFusionPlugin.RenderScale?.Value ?? 1, 1, 2);
        }
        _sensorWidth = Mathf.Max(_sensorWidth, wanted);
        int fullWidth = _sensorWidth;
        int fullHeight = Mathf.Max(16, Mathf.RoundToInt(fullWidth * (float)main.pixelHeight / Mathf.Max(main.pixelWidth, 1)));
        _cropPrev = _crop;
        _crop = CropFor(fullWidth, fullHeight);
        Shader.SetGlobalVector(CropId, _crop);
        Shader.SetGlobalVector(CropPrevId, _cropPrev);
        int width = Mathf.Max(16, Mathf.RoundToInt(_crop.z * fullWidth));
        int height = Mathf.Max(16, Mathf.RoundToInt(_crop.w * fullHeight));
        if (!_loggedCrop && _crop.z < 1f)
        {
            _loggedCrop = true;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO: in the eyepieces the thermal picture is worked out for its rectangle only: " + width + "x" + height
                + " of the sensor's " + fullWidth + "x" + fullHeight + " (crop " + _crop.x.ToString("F3") + "," + _crop.y.ToString("F3") + " size " + _crop.z.ToString("F3") + "x" + _crop.w.ToString("F3") + ")");
        }
        int fineWidth = width * Oversample, fineHeight = height * Oversample;
        RenderTexture picture = Ensure(ref Picture, fineWidth, fineHeight, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO thermal");
        RenderTexture normals = Ensure(ref Normals, fineWidth, fineHeight, 0, RenderTextureFormat.ARGB32, "Justachae F-PANO normals");
        RenderTexture depth = Ensure(ref Depth, fineWidth, fineHeight, 0, RenderTextureFormat.RGFloat, "Justachae F-PANO depth");
        RenderTexture albedo = Ensure(ref Albedo, fineWidth, fineHeight, 0, RenderTextureFormat.ARGB32, "Justachae F-PANO albedo");
        RenderTexture specular = Ensure(ref Specular, fineWidth, fineHeight, 0, RenderTextureFormat.ARGB32, "Justachae F-PANO specular");
        Ensure(ref Heat2x, fineWidth, fineHeight, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO heat");
        Ensure(ref Seams, fineWidth, fineHeight, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO heat, seams filled");
        Ensure(ref GeoSource, fineWidth, fineHeight, 0, RenderTextureFormat.ARGBFloat, "Justachae F-PANO geometry");
        Ensure(ref GeoAcross, fineWidth, fineHeight, 0, RenderTextureFormat.ARGBFloat, "Justachae F-PANO geometry across");
        Ensure(ref GeoNear, fineWidth, fineHeight, 0, RenderTextureFormat.ARGBFloat, "Justachae F-PANO geometry near");
        Ensure(ref GeoFar, fineWidth, fineHeight, 0, RenderTextureFormat.ARGBFloat, "Justachae F-PANO geometry far");
        Ensure(ref HistoryA, width * Oversample, height * Oversample, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO history A");
        Ensure(ref HistoryB, width * Oversample, height * Oversample, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO history B");
        Ensure(ref Across, width * Oversample, height * Oversample, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO across");
        Ensure(ref Blurred, width * Oversample, height * Oversample, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO blurred");
        Ensure(ref Surround, width * Oversample, height * Oversample, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO surround");
        Ensure(ref Edges, width * Oversample, height * Oversample, 0, RenderTextureFormat.RHalf, "Justachae F-PANO edges");
        Ensure(ref Ridge, width * Oversample, height * Oversample, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO line centres");
        Ensure(ref LitSoft, fineWidth, fineHeight, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO lit, averaged");
        Ensure(ref Motion, fineWidth, fineHeight, 0, RenderTextureFormat.RGHalf, "Justachae F-PANO motion");
        Ensure(ref NormalsSoft, fineWidth, fineHeight, 0, RenderTextureFormat.ARGBFloat, "Justachae F-PANO normals, averaged");
        Ensure(ref Foliage, fineWidth, fineHeight, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO foliage");
        Ensure(ref HeatBase, fineWidth, fineHeight, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO heat base");
        Ensure(ref GrassMask, fineWidth, fineHeight, 0, RenderTextureFormat.RHalf, "Justachae F-PANO grass mask");
        Ensure(ref BodyMask, fineWidth, fineHeight, 0, RenderTextureFormat.RHalf, "Justachae F-PANO body heat");
        if (EdgeHistA == null || EdgeHistA.width != width * Oversample || EdgeHistA.height != height * Oversample)
            EnvgChain.ResetHistory();
        Ensure(ref EdgeHistA, width * Oversample, height * Oversample, 0, RenderTextureFormat.RHalf, "Justachae F-PANO edges, followed A");
        Ensure(ref EdgeHistB, width * Oversample, height * Oversample, 0, RenderTextureFormat.RHalf, "Justachae F-PANO edges, followed B");
        Ensure(ref AlbedoSoft, fineWidth, fineHeight, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO albedo, averaged");
        Ensure(ref Presence, width * Oversample, height * Oversample, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO line presence");
        Ensure(ref Density, width * Oversample, height * Oversample, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO line density");
        Ensure(ref Glow, width * Oversample, height * Oversample, 0, RenderTextureFormat.ARGBHalf, "Justachae F-PANO line glow");
        Ensure(ref LocalSquare, width * Oversample, height * Oversample, 0, RenderTextureFormat.ARGBFloat, "Justachae F-PANO heat squared");
        Ensure(ref LocalMoments, width * Oversample, height * Oversample, 0, RenderTextureFormat.ARGBFloat, "Justachae F-PANO local moments");

        {
            Matrix4x4 unjittered = main.nonJitteredProjectionMatrix;
            Matrix4x4 drawn = main.projectionMatrix;
            Matrix4x4 worldToCam = main.worldToCameraMatrix;
            _camToWorld = worldToCam.inverse;
            _tan = new Vector2(1f / unjittered.m00, 1f / unjittered.m11);
            // the camera now and last frame, as the scene is copied (after anything that moved it before it renders):
            // the still world's history is followed by them (see the shader's previousUv)
            Shader.SetGlobalMatrix(RpCamToWorldId, _camToWorld);
            Shader.SetGlobalMatrix(RpPrevWorldToCamId, _hasPrevCamera ? _prevWorldToCam : worldToCam);
            Shader.SetGlobalVector(RpTanId, new Vector4(_tan.x, _tan.y, _hasPrevCamera ? _prevTan.x : _tan.x, _hasPrevCamera ? _prevTan.y : _tan.y));
            Shader.SetGlobalVector(RpOnId, new Vector4(1f, 0f, 0f, 0f));
            if (Motion != null) Shader.SetGlobalTexture(RpMotionId, Motion);
            if (BodyMask != null) Shader.SetGlobalTexture(RpBodyId, BodyMask);
            _prevWorldToCam = worldToCam;
            _prevTan = _tan;
            _hasPrevCamera = true;
            if (!_loggedFollow)
            {
                _loggedFollow = true;
                EnvgFusionPlugin.Log?.LogInfo("F-PANO history followed by the camera itself (the depth and where the camera was last frame); the game's motion vectors on people only");
            }
            // the frame is drawn with a sub-pixel offset (temporal anti-aliasing): read it back from where it is, so
            // the copies stay put
            Vector2 jitter = new Vector2(drawn.m02 - unjittered.m02, drawn.m12 - unjittered.m12) * -0.5f;
            CopyMain(main, picture, normals, depth, albedo, specular, jitter);
            FinishGrassMask(jitter);
            CheckGrassMask();
            if (!_loggedMain)
            {
                _loggedMain = true;
                EnvgFusionPlugin.Log?.LogInfo("F-PANO scene data from the main camera's own buffers (" + main.actualRenderingPath + ", jitter " + jitter.x.ToString("F5") + ", " + jitter.y.ToString("F5") + ")");
            }
        }

        EnvgChain.SetLook(Material!, ChainInput(width, height));
        FillOverlay(width, height);
        if (!_loggedRender)
        {
            _loggedRender = true;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO thermal rendered " + width + "x" + height + " for " + main.name);
        }
    }

    // Body heat: the people in the game world (its own list of the living), their body and gear drawn again over the
    // frame's depth into a mask, averaged to the sensor's pixels like the other copies. Each one's renderers are
    // collected once and again only when one of them is gone (gear changed).
    private static Material? _bodyMaterial;

    private static readonly Dictionary<EFT.Player, Renderer[]> _bodyRenderers = new Dictionary<EFT.Player, Renderer[]>();
    private static readonly List<EFT.Player> _bodyGone = new List<EFT.Player>();
    private static readonly int BodyTempId = Shader.PropertyToID("_EnvgBodyFull");
    private static readonly int BodyId = Shader.PropertyToID("_EnvgBody");
    private static bool _loggedBodies;

    // The dead, from the moment they die (as the game's own list of the living drops them): when, to cool them. Kept
    // per world; a corpse the game removes (destroyed) leaves the list. The one looking is never among them.
    // Times on the game's own clock (seven times real time in a raid), as the day's heat and the night's cooling: the
    // bare skin and the eyes reach the air in about one and a half to two hours (post-mortem infrared studies), a
    // clothed trunk more slowly, heat still coming through from inside: an exponential with two and a half hours.
    private const float CorpseCooling = 150f * 60f;

    // the game's clock in seconds (its date and time in a raid; real time where it has none)
    private static double GameSeconds()
    {
        try
        {
            if (Comfort.Common.Singleton<EFT.GameWorld>.Instantiated && Comfort.Common.Singleton<EFT.GameWorld>.Instance.GameDateTime != null)
                return Comfort.Common.Singleton<EFT.GameWorld>.Instance.GameDateTime.Calculate().Ticks / 1e7;
        }
        catch (System.Exception)
        {
        }
        return Time.time;
    }
    private static readonly HashSet<EFT.Player> _alive = new HashSet<EFT.Player>();
    private static readonly Dictionary<EFT.Player, double> _corpses = new Dictionary<EFT.Player, double>();
    private static readonly List<EFT.Player> _scratch = new List<EFT.Player>();
    private static object? _deathsWorld;
    private static bool _loggedCorpse;
    private static readonly int BodyCoolId = Shader.PropertyToID("_EnvgBodyCool");

    private static void TrackDeaths(List<EFT.Player> living)
    {
        object world = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
        if (!ReferenceEquals(world, _deathsWorld))
        {
            _deathsWorld = world;
            _alive.Clear();
            _corpses.Clear();
        }
        foreach (EFT.Player player in living)
            if (player != null && !player.IsYourPlayer)
                _alive.Add(player);
        // someone known alive is dead now (from this moment), or gone
        _scratch.Clear();
        foreach (EFT.Player known in _alive)
            if (known == null || known.HealthController == null || !known.HealthController.IsAlive)
                _scratch.Add(known!);
        foreach (EFT.Player gone in _scratch)
        {
            _alive.Remove(gone);
            if (gone != null && gone.PlayerBody != null)
                _corpses[gone] = GameSeconds();
        }
        // corpses the game has removed
        _scratch.Clear();
        foreach (KeyValuePair<EFT.Player, double> corpse in _corpses)
            if (corpse.Key == null || corpse.Key.PlayerBody == null)
                _scratch.Add(corpse.Key!);
        foreach (EFT.Player gone in _scratch)
        {
            _corpses.Remove(gone);
            _bodyRenderers.Remove(gone);
        }
    }

    private static int _probePlayers, _probeRenderers, _probeDraws;
    private static void DrawBodies(CommandBuffer cb)
    {
        _probePlayers = _probeRenderers = _probeDraws = 0;
        if (BodyMask == null)
            return;
        cb.SetRenderTarget(BodyMask);
        cb.ClearRenderTarget(false, true, Color.clear);
        cb.SetGlobalTexture(BodyId, BodyMask);
        float warmth = EnvgFusionPlugin.BodyHeat?.Value ?? 1f;
        if (_bodyMaterial == null || warmth <= 0f || !Comfort.Common.Singleton<EFT.GameWorld>.Instantiated)
            return;
        List<EFT.Player>? players = Comfort.Common.Singleton<EFT.GameWorld>.Instance.AllAlivePlayersList;
        if (players == null || players.Count == 0)
            return;
        cb.GetTemporaryRT(BodyTempId, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.R8);
        cb.SetRenderTarget(BodyTempId, BuiltinRenderTextureType.CameraTarget);
        cb.ClearRenderTarget(false, true, Color.clear);
        int drawn = 0;
        _probePlayers = players.Count; _probeRenderers = 0; _probeDraws = 0;
        TrackDeaths(players);
        foreach (EFT.Player player in players)
        {
            if (FpanoProbe.NoBodies)
                break;
            // not the one looking: his own head and body sit around the camera (unseen) and would cover the view
            if (player == null || player.PlayerBody == null)
                continue;
            if (!_bodyRenderers.TryGetValue(player, out Renderer[] renderers) || System.Array.Exists(renderers, r => r == null))
            {
                // the one looking: only his hands (and gloves), in view in front of him; his head and body sit around
                // the camera unseen and would cover the view
                if (player.IsYourPlayer)
                    renderers = player.PlayerBody.BodySkins.TryGetValue(EFT.EBodyModelPart.Hands, out EFT.Visual.LoddedSkin hands) && hands != null
                        ? System.Linq.Enumerable.ToArray(hands.GetRenderers()) : System.Array.Empty<Renderer>();
                else
                    renderers = player.PlayerBody.GetComponentsInChildren<Renderer>(true);
                _bodyRenderers[player] = renderers;
            }
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !renderer.enabled || !renderer.isVisible || !renderer.gameObject.activeInHierarchy
                    || renderer is ParticleSystemRenderer || renderer is LineRenderer || renderer is TrailRenderer)
                    continue;
                int subs = renderer.sharedMaterials.Length;
                for (int sub = 0; sub < subs; sub++)
                    cb.DrawRenderer(renderer, _bodyMaterial, sub, 0);
                drawn++;
                _probeRenderers++; _probeDraws += subs;
            }
        }
        // the dead, cooling: their warmth falls toward the air after death (Newton's law), the mask holds how warm
        if (_corpses.Count > 0)
        {
            double now = GameSeconds();
            foreach (KeyValuePair<EFT.Player, double> corpse in _corpses)
            {
                EFT.Player dead = corpse.Key;
                if (dead == null || dead.PlayerBody == null)
                    continue;
                float left = Mathf.Exp(-(float)System.Math.Max(now - corpse.Value, 0.0) / CorpseCooling);
                if (left < 0.02f)
                    continue;
                if (!_bodyRenderers.TryGetValue(dead, out Renderer[] renderers) || System.Array.Exists(renderers, r => r == null))
                {
                    renderers = dead.PlayerBody.GetComponentsInChildren<Renderer>(true);
                    _bodyRenderers[dead] = renderers;
                }
                cb.SetGlobalFloat(BodyCoolId, 1f - left);
                foreach (Renderer renderer in renderers)
                {
                    if (renderer == null || !renderer.enabled || !renderer.isVisible || !renderer.gameObject.activeInHierarchy
                        || renderer is ParticleSystemRenderer || renderer is LineRenderer || renderer is TrailRenderer)
                        continue;
                    int subs = renderer.sharedMaterials.Length;
                    for (int sub = 0; sub < subs; sub++)
                        cb.DrawRenderer(renderer, _bodyMaterial, sub, 0);
                }
                if (!_loggedCorpse)
                {
                    _loggedCorpse = true;
                    // (the body's surface as the chosen heat source has it: the game's method 34 C, our model 28 C)
                    bool native = EnvgFusionPlugin.HeatSource?.Value == EnvgFusionPlugin.SourceNative;
                    float air = EnvgFusionPlugin.AirTemperature?.Value ?? 15f, alive = native ? 34f : 28f;
                    EnvgFusionPlugin.Log?.LogInfo("F-PANO body heat: a body (" + dead.Profile?.Nickname + ") in the heat mask "
                        + ((now - corpse.Value) / 60.0).ToString("F0") + " game minutes after death, warmth " + left.ToString("F2") + " (about "
                        + (air + (alive - air) * left).ToString("F1") + " C at the surface against " + air.ToString("F0")
                        + " C air, cooling over " + (CorpseCooling / 60f).ToString("F0") + " game minutes)");
                }
            }
            cb.SetGlobalFloat(BodyCoolId, 0f);
        }
        cb.Blit(BodyTempId, BodyMask, Material, 25);
        cb.ReleaseTemporaryRT(BodyTempId);
        // forget the people no longer in the list
        if (_bodyRenderers.Count > players.Count + 8)
        {
            _bodyGone.Clear();
            foreach (EFT.Player known in _bodyRenderers.Keys)
                if (known == null || !players.Contains(known))
                    _bodyGone.Add(known!);
            foreach (EFT.Player gone in _bodyGone)
                _bodyRenderers.Remove(gone);
        }
        if (!_loggedBodies && drawn > 0)
        {
            _loggedBodies = true;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO body heat: " + drawn + " renderers of " + players.Count + " people drawn into the body mask");
        }
    }

    // The main camera's deferred buffers, lit picture and depth, copied (at the thermal processing's size) once its
    // opaque scene is drawn and before the transparent things.
    // A magnified scope's lens (the game's own renderer of the scope's picture), as the eye sees it over the frame's
    // depth, into a mask at the frame's size: the thermal picture is left out there (see the shader's scopeLens).
    private static Material? _lensMaskMaterial;
    private static RenderTexture? _lensMask;
    private static readonly int LensMaskId = Shader.PropertyToID("_EnvgLensMask"), LensMaskOnId = Shader.PropertyToID("_EnvgLensMaskOn");
    private static bool _loggedLensMask;
    private static void DrawScopeLens(CommandBuffer cb, Camera main, Vector2 jitter)
    {
        Renderer? lens = null;
        try { lens = EFT.CameraControl.CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight?.LensRenderer; } catch (System.Exception) { }
        if (lens == null || _lensMaskMaterial == null || !lens.gameObject.activeInHierarchy)
        {
            Shader.SetGlobalVector(LensMaskOnId, Vector4.zero);
            return;
        }
        int w = Mathf.Max(main.pixelWidth, 1), h = Mathf.Max(main.pixelHeight, 1);
        if (_lensMask == null || _lensMask.width != w || _lensMask.height != h)
        {
            if (_lensMask != null)
            {
                _lensMask.Release();
                Object.Destroy(_lensMask);
            }
            _lensMask = new RenderTexture(w, h, 0, RenderTextureFormat.R8) { name = "Justachae F-PANO scope lens", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _lensMask.Create();
        }
        cb.SetRenderTarget(_lensMask, BuiltinRenderTextureType.CameraTarget);
        cb.ClearRenderTarget(false, true, Color.clear);
        cb.DrawRenderer(lens, _lensMaskMaterial, 0, 0);
        cb.SetGlobalTexture(LensMaskId, _lensMask);
        Shader.SetGlobalVector(LensMaskOnId, new Vector4(1f, jitter.x, jitter.y, 0f));
        if (!_loggedLensMask)
        {
            _loggedLensMask = true;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO: a scope's lens (" + lens.name + ") left out of the thermal picture, its housing kept");
        }
    }

    private static void CopyMain(Camera main, RenderTexture picture, RenderTexture normals, RenderTexture depth, RenderTexture albedo, RenderTexture specular, Vector2 jitter)
    {
        if (_mainCopy == null || _mainCopyCamera != main)
        {
            if (_mainCopy != null && _mainCopyCamera != null)
                _mainCopyCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, _mainCopy);
            _mainCopy ??= new CommandBuffer { name = "Justachae F-PANO scene copy" };
            main.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, _mainCopy);
            _mainCopyCamera = main;
        }
        _mainCopy.Clear();
        _mainCopy.SetGlobalVector(JitterId, new Vector4(jitter.x, jitter.y, 0f, 0f));
        _mainCopy.SetGlobalVector(FootprintId, new Vector4(_crop.z / normals.width, _crop.w / normals.height, 0f, 0f));
        _mainCopy.BeginSample(FpanoProbe.Copy);
        _mainCopy.Blit(BuiltinRenderTextureType.CameraTarget, picture, Material, 25);
        _mainCopy.Blit(BuiltinRenderTextureType.GBuffer2, normals, Material, 25);
        _mainCopy.Blit(BuiltinRenderTextureType.GBuffer0, albedo, Material, 25);
        _mainCopy.Blit(BuiltinRenderTextureType.GBuffer1, specular, Material, 25);
        _mainCopy.Blit(normals, depth, Material, 12);
        _mainCopy.EndSample(FpanoProbe.Copy);
        _mainCopy.BeginSample(FpanoProbe.Bodies);
        long bodiesFrom = FpanoProbe.Now;
        DrawBodies(_mainCopy);
        FpanoProbe.AddBodies(bodiesFrom, _probePlayers, _probeRenderers, _probeDraws);
        _mainCopy.EndSample(FpanoProbe.Bodies);
        _mainCopy.BeginSample(FpanoProbe.Lens);
        DrawScopeLens(_mainCopy, main, jitter);
        _mainCopy.EndSample(FpanoProbe.Lens);
        _mainCopy.SetGlobalVector(JitterId, Vector4.zero);

        if ((main.depthTextureMode & DepthTextureMode.MotionVectors) == 0)
            main.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;
        if (_motionCopy == null)
        {
            _motionCopy = new CommandBuffer { name = "Justachae F-PANO motion copy" };
            main.AddCommandBuffer(CameraEvent.BeforeImageEffects, _motionCopy);
        }
        _motionCopy.Clear();
        _motionCopy.SetGlobalVector(JitterId, Vector4.zero);
        _motionCopy.SetGlobalVector(FootprintId, Vector4.zero);
        _motionCopy.BeginSample(FpanoProbe.Motion);
        _motionCopy.Blit(BuiltinRenderTextureType.MotionVectors, Motion!, Material, 25);
        _motionCopy.EndSample(FpanoProbe.Motion);
        _motionReady = true;
        if (!_loggedMotion)
        {
            _loggedMotion = true;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO temporal smoothing with the game's motion vectors (depthTextureMode " + main.depthTextureMode + ")");
        }
    }



}
