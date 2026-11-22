using BepInEx.Logging;
using UnityEngine;

// MEASURING BUILD ONLY (to be taken out once read): what the F-PANO's thermal fusion costs a frame in a raid, as it is
// now (the lens camera) and as it was (the main camera's own buffers). Once per game start, in a raid (not the hideout),
// with the F-PANO down and settled, after K-3PS-C's own measurement ("NVG cost: done"; it switches its parts off and on):
// six rounds of 340 frames, the mean frame time of the last 300 of each, in turn: the fusion from the lens camera, the
// fusion from the main camera's buffers, no thermal fusion at all (as in the I2-only mode); twice. The user stands still,
// the goggle down. Nothing allocated per frame.
namespace EnvgFusion;

internal static class FpanoCost
{
    private static readonly string[] States = { "thermal fusion from the lens camera", "thermal fusion from the main camera's buffers", "no thermal fusion" };
    private const int Frames = 340, Kept = 300, Rounds = 6;
    private const float Settle = 3f, WaitForEyepieces = 150f, NoEyepieceProbe = 15f;

    internal static bool LensOff => _round >= 0 && _round < Rounds && _round % 3 == 1;
    internal static bool ThermalOff => _round >= 0 && _round < Rounds && _round % 3 == 2;

    private static int _round = -3;    // -3 waiting for the goggle in a raid, -2 settling, -1 waiting for K-3PS-C, 0.. rounds, Rounds done
    private static int _frame;
    private static float _downSince, _waitSince;
    private static bool _sawNvgCost, _nvgCostDone;
    private static readonly double[] _ms = new double[Rounds];
    private static Listener? _listener;

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
        EnvgFusionPlugin.Log?.LogInfo("F-PANO COST: measuring build; runs once in a raid, with the F-PANO down, after K-3PS-C's \"NVG cost: done\"");
    }

    private static bool InRaid()
    {
        try
        {
            if (!Comfort.Common.Singleton<EFT.GameWorld>.Instantiated)
                return false;
            EFT.GameWorld world = Comfort.Common.Singleton<EFT.GameWorld>.Instance;
            return world.MainPlayer != null && !world.GetType().Name.Contains("Hideout");
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    internal static void Update()
    {
        if (_round >= Rounds)
            return;
        float now = Time.unscaledTime;
        bool down = EnvgFusion.EnvgbDown && EyepieceLink.Deployed >= 0.999f && InRaid();
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
            EnvgFusionPlugin.Log?.LogInfo("F-PANO COST: start (" + (_nvgCostDone ? "after K-3PS-C's measurement" : _sawNvgCost ? "K-3PS-C's measurement not done in time" : "no K-3PS-C measurement seen")
                + "): " + Rounds + " rounds of " + Frames + " frames: " + string.Join(", ", States) + ", twice; stand still");
            _round = 0;
            _frame = 0;
            return;
        }
        if (!down)
            return;
        _frame++;
        if (_frame > Frames - Kept)
            _ms[_round] += Time.unscaledDeltaTime * 1000.0;
        if (_frame >= Frames)
        {
            _round++;
            _frame = 0;
            if (_round >= Rounds)
                Finish();
        }
    }

    private static void Finish()
    {
        double lens = (_ms[0] + _ms[3]) / (2.0 * Kept), main = (_ms[1] + _ms[4]) / (2.0 * Kept), none = (_ms[2] + _ms[5]) / (2.0 * Kept);
        EnvgFusionPlugin.Log?.LogInfo("F-PANO COST: frame " + lens.ToString("F2") + " ms with the thermal fusion from the lens camera, " + main.ToString("F2")
            + " ms from the main camera's buffers, " + none.ToString("F2") + " ms without: the fusion costs " + (lens - none).ToString("F2") + " ms (of which the lens camera "
            + (lens - main).ToString("F2") + " ms); rounds " + (_ms[0] / Kept).ToString("F2") + ", " + (_ms[1] / Kept).ToString("F2") + ", " + (_ms[2] / Kept).ToString("F2") + ", "
            + (_ms[3] / Kept).ToString("F2") + ", " + (_ms[4] / Kept).ToString("F2") + ", " + (_ms[5] / Kept).ToString("F2") + " ms");
        EnvgFusionPlugin.Log?.LogInfo("F-PANO COST: done");
        if (_listener != null)
            BepInEx.Logging.Logger.Listeners.Remove(_listener);
    }
}
