using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

// MEASURING BUILD ONLY (to be taken out once read): what the F-PANO's thermal fusion costs a frame, split by its steps.
// Once per game start, with the F-PANO down and settled, and after K-3PS-C's own eyepiece measurement ("NVG cost:
// done" in the log; it switches its parts off and on, so not timed together): four rounds of 240 frames, the thermal
// fusion on, off, on, off (off as in the I2-only mode: the tubes as they are, nothing of ours drawn or copied), the mean
// frame time of the last 200 of each. With it on, the GPU time of each step from the GPU's own timers (a sampler around
// each group of blits) and the CPU time of our code. The user stands still, the goggle down. Nothing allocated per frame.
namespace EnvgFusion;

internal static class FpanoCost
{
    internal enum Group { SceneCopy, Prep, Heat, HeatTemporal, BaseAgc, Edges, EdgeTemporal, LinesGlow, Composite, Count }
    private static readonly string[] Names = { "scene copy", "prep (geometry, soft light/colour, foliage)", "heat", "heat followed in time", "base, histogram, gain", "edges (blurs, local contrast)", "edges followed in time", "lines and glow", "into the eyepieces" };
    private const int Frames = 240, Kept = 200, Rounds = 4;
    private const float Settle = 3f, WaitForEyepieces = 120f, NoEyepieceProbe = 12f;

    // set while measuring with the fusion on: the chain marks its groups
    internal static CustomSampler?[]? Samplers { get => EnvgChain.Samplers; set => EnvgChain.Samplers = value; }
    internal static bool ThermalOff => _round >= 0 && _round < Rounds && (_round & 1) == 1;

    private static CustomSampler?[]? _made;
    private static Recorder?[]? _recorders;
    private static int _round = -3;    // -3 waiting for the goggle, -2 settling, -1 waiting for K-3PS-C, 0.. rounds, Rounds done
    private static int _frame;
    private static float _downSince, _waitSince;
    private static bool _sawNvgCost, _nvgCostDone;
    private static readonly double[] _frameMs = new double[Rounds];
    private static readonly double[] _cpuMs = new double[Rounds];
    private static readonly double[] _gpuNs = new double[(int)Group.Count];
    private static int _gpuFrames;
    private static double _cpuFrame;
    private static Listener? _listener;

    internal static long Now => System.Diagnostics.Stopwatch.GetTimestamp();
    internal static void AddCpu(long from) { _cpuFrame += (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }

    internal static void Mark(CommandBuffer cb, Group g, bool begin) => EnvgChain.Mark(cb, (int)g, begin);

    // K-3PS-C's measurement, seen in the log
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
        EnvgFusionPlugin.Log?.LogInfo("F-PANO COST: measuring build; runs once, with the F-PANO down, after K-3PS-C's \"NVG cost: done\"");
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
            EnvgFusionPlugin.Log?.LogInfo("F-PANO COST: start (" + (_nvgCostDone ? "after K-3PS-C's measurement" : _sawNvgCost ? "K-3PS-C's measurement not done in time" : "no K-3PS-C measurement seen")
                + "): " + Rounds + " rounds of " + Frames + " frames, the thermal fusion on, off, on, off; stand still");
            MakeSamplers();
            BeginRound(0);
            return;
        }
        // a round: frames counted only with the goggle down
        if (!down)
            return;
        _frame++;
        if (_frame > Frames - Kept)
        {
            _frameMs[_round] += Time.unscaledDeltaTime * 1000.0;
            _cpuMs[_round] += _cpuFrame;
            if ((_round & 1) == 0 && _recorders != null)
            {
                for (int g = 0; g < _recorders.Length; g++)
                    if (_recorders[g] != null && _recorders[g]!.isValid)
                        _gpuNs[g] += _recorders[g]!.gpuElapsedNanoseconds;
                _gpuFrames++;
            }
        }
        _cpuFrame = 0;
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
        _cpuFrame = 0;
        Samplers = (round & 1) == 0 ? _made : null;
    }

    private static void MakeSamplers()
    {
        _made = new CustomSampler?[(int)Group.Count];
        _recorders = new Recorder?[(int)Group.Count];
        int valid = 0;
        for (int g = 0; g < (int)Group.Count; g++)
        {
            try
            {
                CustomSampler s = CustomSampler.Create("Justachae F-PANO " + Names[g], true);
                Recorder r = s.GetRecorder();
                r.enabled = true;
                _made[g] = s;
                _recorders[g] = r;
                if (r.isValid) valid++;
            }
            catch (System.Exception) { }
        }
        EnvgFusionPlugin.Log?.LogInfo("F-PANO COST: GPU timers " + valid + " of " + (int)Group.Count + " valid (GPU recorder supported: " + SystemInfo.supportsGpuRecorder + ")");
    }

    private static void Finish()
    {
        _round = Rounds;
        Samplers = null;
        double on = (_frameMs[0] + _frameMs[2]) / (2.0 * Kept), off = (_frameMs[1] + _frameMs[3]) / (2.0 * Kept);
        double cpuOn = (_cpuMs[0] + _cpuMs[2]) / (2.0 * Kept);
        EnvgFusionPlugin.Log?.LogInfo("F-PANO COST: frame " + on.ToString("F2") + " ms with the thermal fusion, " + off.ToString("F2") + " ms without: it costs "
            + (on - off).ToString("F2") + " ms a frame (rounds " + (_frameMs[0] / Kept).ToString("F2") + ", " + (_frameMs[1] / Kept).ToString("F2") + ", "
            + (_frameMs[2] / Kept).ToString("F2") + ", " + (_frameMs[3] / Kept).ToString("F2") + " ms); our CPU time with it " + cpuOn.ToString("F2") + " ms");
        var line = new System.Text.StringBuilder("F-PANO COST: GPU time by step (mean over " + _gpuFrames + " frames):");
        double total = 0;
        for (int g = 0; g < (int)Group.Count; g++)
        {
            double ms = _gpuFrames > 0 ? _gpuNs[g] / _gpuFrames / 1e6 : 0;
            total += ms;
            line.Append(' ').Append(Names[g]).Append(' ').Append(ms.ToString("F2")).Append(" ms;");
        }
        line.Append(" together ").Append(total.ToString("F2")).Append(" ms");
        EnvgFusionPlugin.Log?.LogInfo(line.ToString());
        EnvgFusionPlugin.Log?.LogInfo("F-PANO COST: done");
        if (_recorders != null)
            foreach (Recorder? r in _recorders)
                if (r != null) r.enabled = false;
        if (_listener != null)
            BepInEx.Logging.Logger.Listeners.Remove(_listener);
    }
}
