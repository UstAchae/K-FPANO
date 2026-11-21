using Comfort.Common;
using EFT;
using UnityEngine;

// MEASURING BUILD ONLY (to be taken out once read): is the game slow all the time, or does it stall now and then,
// and how much of either is the F-PANO's thermal picture? In a raid, two states in turn, 8 s each (the first second
// after a switch left out): "all" as it is, "noThermal" the thermal picture off (the eyepieces still show the tubes).
// Each frame's time is kept, apart by state and by the goggle down or up; every round: mean, median, the slowest 5% and
// 1%, the slowest frame, how many frames over 33 and 50 ms, the garbage collections in that time, and the CPU time of
// building the thermal frame. Every frame over 50 ms is logged as it happens (a few a round), with its state. Nothing
// allocated per frame: the times go into arrays made once.
namespace EnvgFusion;

internal static class FpanoProbe
{
    private static readonly string[] States = { "all", "noThermal" };
    private const float StateTime = 8f, Settle = 1f;
    private const int Capacity = 8192;
    internal static bool ThermalOff => _state == 1;

    private static int _state = -1;
    private static float _stateStart;
    private static int _rounds, _spikesLogged;
    // [state * 2 + down]
    private static readonly float[][] _times = { new float[Capacity], new float[Capacity], new float[Capacity], new float[Capacity] };
    private static readonly int[] _count = new int[4];
    private static readonly double[] _cpu = new double[4];
    private static readonly int[] _gc = new int[4];
    private static int _gcWas;
    private static double _cpuFrame;

    internal static long Now => System.Diagnostics.Stopwatch.GetTimestamp();
    internal static void AddThermal(long from) { _cpuFrame += (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }

    internal static void Update()
    {
        bool inRaid = false;
        try { inRaid = Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null; } catch (System.Exception) { }
        if (!inRaid)
        {
            _state = -1;
            return;
        }
        float now = Time.unscaledTime;
        int gcNow = System.GC.CollectionCount(0);
        if (_state < 0)
        {
            _state = 0;
            _stateStart = now;
            _gcWas = gcNow;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO PROBE: measuring stalls, " + States.Length + " states of " + StateTime + " s in turn: " + string.Join(", ", States));
        }
        else if (now - _stateStart >= StateTime)
        {
            _state = (_state + 1) % States.Length;
            _stateStart = now;
            if (_state == 0)
                Summary();
        }
        int slot = _state * 2 + (EnvgFusion.EnvgbDown ? 1 : 0);
        float ms = Time.unscaledDeltaTime * 1000f;
        if (now - _stateStart >= Settle && _count[slot] < Capacity)
        {
            _times[slot][_count[slot]++] = ms;
            _cpu[slot] += _cpuFrame;
            _gc[slot] += gcNow - _gcWas;
            if (ms > 50f && _spikesLogged < 12)
            {
                _spikesLogged++;
                EnvgFusionPlugin.Log?.LogInfo("F-PANO PROBE: a " + ms.ToString("F0") + " ms frame (" + States[_state] + ", goggle " + (EnvgFusion.EnvgbDown ? "down" : "up")
                    + (gcNow != _gcWas ? ", a garbage collection in it" : "") + ")");
            }
        }
        _gcWas = gcNow;
        _cpuFrame = 0;
    }

    private static void Summary()
    {
        _rounds++;
        _spikesLogged = 0;
        var line = new System.Text.StringBuilder("F-PANO PROBE round " + _rounds + ":");
        for (int down = 1; down >= 0; down--)
            for (int s = 0; s < States.Length; s++)
            {
                int slot = s * 2 + down, n = _count[slot];
                line.Append(" | goggle " + (down == 1 ? "down" : "up") + " " + States[s] + ": ");
                if (n == 0) { line.Append("-"); continue; }
                float[] t = _times[slot];
                System.Array.Sort(t, 0, n);
                double sum = 0; int over33 = 0, over50 = 0;
                for (int i = 0; i < n; i++) { sum += t[i]; if (t[i] > 33f) over33++; if (t[i] > 50f) over50++; }
                line.Append(n + " frames, mean " + (sum / n).ToString("F1") + " ms, median " + t[n / 2].ToString("F1") + ", slowest 5% " + t[Mathf.Min(n - 1, (int)(n * 0.95f))].ToString("F1")
                    + ", 1% " + t[Mathf.Min(n - 1, (int)(n * 0.99f))].ToString("F1") + ", max " + t[n - 1].ToString("F0") + ", over 33 ms " + over33 + ", over 50 ms " + over50
                    + ", GC " + _gc[slot] + ", thermal CPU " + (_cpu[slot] / n).ToString("F2") + " ms");
                _count[slot] = 0; _cpu[slot] = 0; _gc[slot] = 0;
            }
        EnvgFusionPlugin.Log?.LogInfo(line.ToString());
    }
}
