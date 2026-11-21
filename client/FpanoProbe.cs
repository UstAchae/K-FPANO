using System.Diagnostics;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Profiling;

namespace EnvgFusion;

// MEASURING BUILD ONLY (to be taken out once read): where the thermal picture's frame time goes, step by step, while
// the F-PANO is down in a raid. Three states in turn, 6 s each (the first second after a switch left out):
//   all       everything as it is;
//   noBodies  the people not drawn into the body mask (the mask cleared only);
//   noChain   the thermal chain not run (the copies and the body mask still made).
// Each frame, by state: the frame time; the CPU time of building the thermal frame and of the body mask in it; the
// people alive, the body renderers and draw calls issued; and, where the GPU reports them, each step's GPU time
// (profiler samples around the command buffers' steps). Logged every round (that round alone, so the change over the
// raid shows) and in total. Nothing allocated per frame.
internal static class FpanoProbe
{
    internal const string Copy = "FPANO copy", Bodies = "FPANO bodies", Lens = "FPANO lens", Motion = "FPANO motion", Chain = "FPANO chain", Fuse = "FPANO fuse";
    private static readonly string[] Steps = { Copy, Bodies, Lens, Motion, Chain, Fuse };
    private static readonly string[] States = { "all", "noBodies", "noChain" };
    private const float StateTime = 6f, Settle = 1f;

    internal static bool NoBodies => _state == 1;
    internal static bool NoChain => _state == 2;

    private static int _state = -1;
    private static float _stateStart;
    private static int _rounds;
    private static Recorder?[] _recorders = new Recorder?[0];
    private static bool _recordersMade;
    private static int _recorderTries;

    // per state: frames, frame ms, CPU thermal ms, CPU bodies ms, players, renderers, draws; GPU ms per step
    private sealed class Tally
    {
        public int Frames; public double FrameMs, CpuThermal, CpuBodies, Players, Renderers, Draws;
        public readonly double[] Gpu = new double[6];
        public readonly int[] GpuFrames = new int[6];
        public void Clear() { Frames = 0; FrameMs = CpuThermal = CpuBodies = Players = Renderers = Draws = 0; for (int i = 0; i < 6; i++) { Gpu[i] = 0; GpuFrames[i] = 0; } }
    }
    private static readonly Tally[] _round = { new Tally(), new Tally(), new Tally() };
    private static readonly Tally[] _total = { new Tally(), new Tally(), new Tally() };

    // filled by the thermal code during the frame
    private static readonly Stopwatch _watch = new Stopwatch();
    private static double _cpuThermal, _cpuBodies;
    private static int _players, _renderers, _draws;
    internal static long Now => Stopwatch.GetTimestamp();
    internal static void AddThermal(long from) { _cpuThermal += (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency; }
    internal static void AddBodies(long from, int players, int renderers, int draws)
    {
        _cpuBodies += (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;
        _players = players; _renderers = renderers; _draws = draws;
    }

    internal static void Update()
    {
        bool inRaid = false;
        try { inRaid = Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null; } catch (System.Exception) { }
        if (!inRaid)
        {
            _state = -1;
            return;
        }
        if (!_recordersMade && Time.frameCount % 30 == 0)
        {
            if (_recorders.Length != Steps.Length)
                _recorders = new Recorder?[Steps.Length];
            int found = 0;
            for (int i = 0; i < Steps.Length; i++)
            {
                if (_recorders[i] == null)
                {
                    Recorder r = Recorder.Get(Steps[i]);
                    if (r != null && r.isValid) { r.enabled = true; _recorders[i] = r; }
                }
                if (_recorders[i] != null) found++;
            }
            if (found == Steps.Length || ++_recorderTries > 60)
            {
                _recordersMade = true;
                EnvgFusionPlugin.Log?.LogInfo("F-PANO PROBE: GPU samplers found " + found + " of " + Steps.Length);
            }
        }
        float now = Time.unscaledTime;
        if (_state < 0)
        {
            _state = 0;
            _stateStart = now;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO PROBE: measuring the thermal picture, " + States.Length + " states of " + StateTime + " s in turn: " + string.Join(", ", States));
        }
        else if (now - _stateStart >= StateTime)
        {
            _state = (_state + 1) % States.Length;
            _stateStart = now;
            if (_state == 0)
                Summary();
        }
        // last frame's numbers, counted when the goggle is down and the state has settled
        if (EnvgFusion.EnvgbDown && now - _stateStart >= Settle)
        {
            Add(_round[_state]);
            Add(_total[_state]);
        }
        _cpuThermal = _cpuBodies = 0;
        _players = _renderers = _draws = 0;
    }

    private static void Add(Tally t)
    {
                t.Frames++;
                t.FrameMs += Time.unscaledDeltaTime * 1000.0;
                t.CpuThermal += _cpuThermal;
                t.CpuBodies += _cpuBodies;
                t.Players += _players;
                t.Renderers += _renderers;
                t.Draws += _draws;
                for (int i = 0; i < _recorders.Length; i++)
                {
                    Recorder? r = _recorders[i];
                    if (r == null) continue;
                    long ns = r.gpuElapsedNanoseconds;
                    if (ns > 0) { t.Gpu[i] += ns / 1e6; t.GpuFrames[i]++; }
                }
    }

    private static void Summary()
    {
        _rounds++;
        Write("round " + _rounds, _round);
        Write("total", _total);
        foreach (Tally t in _round) t.Clear();
    }

    private static void Write(string what, Tally[] tallies)
    {
        var line = new System.Text.StringBuilder("F-PANO PROBE " + what + ":");
        for (int s = 0; s < States.Length; s++)
        {
            Tally t = tallies[s];
            if (t.Frames == 0) { line.Append(" | " + States[s] + " -"); continue; }
            double n = t.Frames;
            line.Append(" | " + States[s] + " frame " + (t.FrameMs / n).ToString("F1") + " ms (" + t.Frames + "), cpu thermal " + (t.CpuThermal / n).ToString("F2")
                + " bodies " + (t.CpuBodies / n).ToString("F2") + ", people " + (t.Players / n).ToString("F0") + " renderers " + (t.Renderers / n).ToString("F0") + " draws " + (t.Draws / n).ToString("F0") + ", gpu");
            for (int i = 0; i < Steps.Length; i++)
                line.Append(" " + Steps[i].Substring(6) + " " + (t.GpuFrames[i] > 0 ? (t.Gpu[i] / t.GpuFrames[i]).ToString("F2") : "-"));
        }
        EnvgFusionPlugin.Log?.LogInfo(line.ToString());
    }
}
