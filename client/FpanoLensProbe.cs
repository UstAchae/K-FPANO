using BepInEx.Logging;
using UnityEngine;

// MEASURING BUILD ONLY (to be taken out once read): what a second camera at the thermal objective would cost a frame,
// the scene drawn once more from the lens's place (the "real" way of the thermal picture) instead of taken from the eye's
// camera. Once per game start, with the F-PANO down and settled, after K-3PS-C's own measurement ("NVG cost: done"):
// six rounds of 240 frames, the mean frame time of the last 200 of each: no extra camera; the extra camera as the game
// draws (deferred, with shadows); the same without shadows; and again. The camera is a bare one (no image effects, as
// the thermal picture needs none), at the thermal picture's own size and the eye camera's field, 5 cm above it.
// A lower bound: the game's own per-camera systems (GPU-instanced grass and trees, its culling helpers) don't draw for a
// camera they don't know. The user stands still, the goggle down. Nothing allocated per frame.
namespace EnvgFusion;

internal static class FpanoLensProbe
{
    private static readonly string[] States = { "no extra camera", "extra camera, with shadows", "extra camera, no shadows" };
    private const int Frames = 240, Kept = 200, Rounds = 6;
    private const float Settle = 3f, WaitForEyepieces = 120f, NoEyepieceProbe = 12f;

    private static int _round = -3;   // -3 waiting for the goggle, -2 settling, -1 waiting for K-3PS-C, 0.. rounds, Rounds done
    private static int _frame;
    private static float _downSince, _waitSince;
    private static bool _sawNvgCost, _nvgCostDone;
    private static readonly double[] _ms = new double[Rounds];
    private static Listener? _listener;
    private static Camera? _lens;
    private static RenderTexture? _target;
    private static float _shadowDistance = -1f;
    private static bool _noShadows;

    private sealed class Listener : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs e)
        {
            if (_nvgCostDone || e?.Data == null)
                return;
            string text = e.Data.ToString();
            if (text.StartsWith("NVG cost", System.StringComparison.Ordinal))
            {
                _sawNvgCost = true;
                if (text.StartsWith("NVG cost: done", System.StringComparison.Ordinal))
                    _nvgCostDone = true;
            }
        }
        public void Dispose() { }
    }

    internal static void Start()
    {
        _listener = new Listener();
        BepInEx.Logging.Logger.Listeners.Add(_listener);
        Camera.onPreCull += BeforeCull;
        Camera.onPostRender += AfterRender;
        EnvgFusionPlugin.Log?.LogInfo("F-PANO LENS PROBE: measuring build; runs once, with the F-PANO down, after K-3PS-C's \"NVG cost: done\"");
    }

    // (no shadows: the shadow distance at 0 for this camera's render only)
    private static void BeforeCull(Camera camera)
    {
        if (camera != _lens || !_noShadows)
            return;
        _shadowDistance = QualitySettings.shadowDistance;
        QualitySettings.shadowDistance = 0f;
    }
    private static void AfterRender(Camera camera)
    {
        if (camera != _lens || _shadowDistance < 0f)
            return;
        QualitySettings.shadowDistance = _shadowDistance;
        _shadowDistance = -1f;
    }

    internal static void Update()
    {
        if (_round >= Rounds)
            return;
        float now = Time.unscaledTime;
        bool down = EnvgFusion.EnvgbDown && EyepieceLink.Deployed >= 0.999f;
        if (_round == -3 || _round == -2)
        {
            if (!down) { _round = -3; return; }
            if (_round == -3) { _round = -2; _downSince = now; return; }
            if (now - _downSince < Settle) return;
            _round = -1;
            _waitSince = now;
            return;
        }
        if (_round == -1)
        {
            float waited = now - _waitSince;
            if (!_nvgCostDone && (_sawNvgCost ? waited < WaitForEyepieces : waited < NoEyepieceProbe))
                return;
            Camera? main = Camera.main;
            if (main == null)
                return;
            int w = EnvgFusion.Picture != null ? EnvgFusion.Picture.width : 1280, h = EnvgFusion.Picture != null ? EnvgFusion.Picture.height : 720;
            _target = new RenderTexture(w, h, 24, RenderTextureFormat.ARGBHalf) { name = "Justachae F-PANO lens probe" };
            var go = new GameObject("Justachae F-PANO lens probe camera");
            Object.DontDestroyOnLoad(go);
            _lens = go.AddComponent<Camera>();
            _lens.CopyFrom(main);
            _lens.targetTexture = _target;
            _lens.depth = main.depth - 1f;
            _lens.renderingPath = RenderingPath.DeferredShading;
            _lens.allowHDR = true;
            _lens.allowMSAA = false;
            _lens.enabled = false;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO LENS PROBE: start (" + (_nvgCostDone ? "after K-3PS-C's measurement" : _sawNvgCost ? "K-3PS-C's measurement not done in time" : "no K-3PS-C measurement seen")
                + "), extra camera " + w + "x" + h + " at a " + main.fieldOfView.ToString("F0") + " deg field, culling mask " + main.cullingMask + "; " + Rounds + " rounds of " + Frames + " frames; stand still");
            BeginRound(0);
            return;
        }
        if (!down)
            return;
        // the extra camera where the lens is (5 cm above the eye), turned as the eye
        Camera? eye = Camera.main;
        if (_lens != null && eye != null)
        {
            Transform e = eye.transform;
            _lens.transform.SetPositionAndRotation(e.position + e.up * 0.05f, e.rotation);
            _lens.fieldOfView = eye.fieldOfView;
        }
        _frame++;
        if (_frame > Frames - Kept)
            _ms[_round] += Time.unscaledDeltaTime * 1000.0;
        if (_frame >= Frames)
        {
            if (_round + 1 < Rounds)
                BeginRound(_round + 1);
            else
                Finish();
        }
    }

    private static void BeginRound(int round)
    {
        _round = round;
        _frame = 0;
        int state = round % 3;
        _noShadows = state == 2;
        if (_lens != null)
            _lens.enabled = state != 0;
    }

    private static void Finish()
    {
        _round = Rounds;
        if (_lens != null)
            Object.Destroy(_lens.gameObject);
        _lens = null;
        if (_target != null)
        {
            _target.Release();
            Object.Destroy(_target);
        }
        _target = null;
        double[] mean = new double[3];
        for (int r = 0; r < Rounds; r++)
            mean[r % 3] += _ms[r] / Kept / 2.0;
        EnvgFusionPlugin.Log?.LogInfo("F-PANO LENS PROBE: frame " + mean[0].ToString("F2") + " ms " + States[0] + "; " + mean[1].ToString("F2") + " ms " + States[1] + " (+"
            + (mean[1] - mean[0]).ToString("F2") + "); " + mean[2].ToString("F2") + " ms " + States[2] + " (+" + (mean[2] - mean[0]).ToString("F2") + "). Rounds: "
            + (_ms[0] / Kept).ToString("F2") + ", " + (_ms[1] / Kept).ToString("F2") + ", " + (_ms[2] / Kept).ToString("F2") + ", " + (_ms[3] / Kept).ToString("F2") + ", "
            + (_ms[4] / Kept).ToString("F2") + ", " + (_ms[5] / Kept).ToString("F2") + " ms");
        EnvgFusionPlugin.Log?.LogInfo("F-PANO LENS PROBE: done");
        if (_listener != null)
            BepInEx.Logging.Logger.Listeners.Remove(_listener);
    }
}
