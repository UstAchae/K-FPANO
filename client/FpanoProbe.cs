using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace EnvgFusion;

// MEASURING BUILD ONLY (to be taken out once read): where the frame time goes with the F-PANO on the RAILINK. In a
// raid it steps through four states, 6 s each, and logs the mean frame time of each (the first second after a switch
// left out), kept apart by the goggle down or up:
//   all      everything as it is;
//   noThermal the F-PANO's thermal picture off (the eyepieces still show the tubes);
//   noShadow the rail lamps' (HL2-A, MPLS) lights without shadows;
//   noLamps  the rail lamps' lights off.
// A summary every full round. Nothing allocated per frame; the lamps' lights are collected when their models are made.
internal static class FpanoProbe
{
    private static readonly string[] States = { "all", "noThermal", "noShadow", "noLamps" };
    private const float StateTime = 6f, Settle = 1f;
    internal static bool ThermalOff;
    internal static readonly List<Light> LampLights = new List<Light>();
    private static readonly List<LightShadows> _savedShadows = new List<LightShadows>();
    private static int _state = -1;
    private static float _stateStart;
    // [state, down] -> sum of ms, frames
    private static readonly double[,] _sum = new double[4, 2];
    private static readonly int[,] _frames = new int[4, 2];
    private static int _rounds;

    internal static void Update()
    {
        bool inRaid = false;
        try { inRaid = Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null; } catch (System.Exception) { }
        if (!inRaid)
        {
            if (_state >= 0)
                Enter(-1);
            return;
        }
        float now = Time.unscaledTime;
        if (_state < 0)
        {
            Enter(0);
            EnvgFusionPlugin.Log?.LogInfo("F-PANO PROBE: measuring, 4 states of " + StateTime + " s in turn: " + string.Join(", ", States) + " (" + LampLights.Count + " rail lamp light(s) known)");
        }
        else if (now - _stateStart >= StateTime)
        {
            int next = (_state + 1) % States.Length;
            if (next == 0)
                Summary();
            Enter(next);
        }
        if (now - _stateStart >= Settle)
        {
            int down = EnvgFusion.EnvgbDown ? 1 : 0;
            _sum[_state, down] += Time.unscaledDeltaTime * 1000.0;
            _frames[_state, down]++;
        }
    }

    private static void Enter(int state)
    {
        // undo the last state
        ThermalOff = false;
        if (_savedShadows.Count == LampLights.Count)
            for (int i = 0; i < LampLights.Count; i++)
                if (LampLights[i] != null)
                    LampLights[i].shadows = _savedShadows[i];
        foreach (Light light in LampLights)
            if (light != null && !light.enabled && state != 3)
                light.enabled = true;
        _savedShadows.Clear();
        LampLights.RemoveAll(l => l == null);
        _state = state;
        _stateStart = Time.unscaledTime;
        if (state < 0)
            return;
        foreach (Light light in LampLights)
            _savedShadows.Add(light.shadows);
        if (state == 1)
            ThermalOff = true;
        else if (state == 2)
            foreach (Light light in LampLights)
                light.shadows = LightShadows.None;
        else if (state == 3)
            foreach (Light light in LampLights)
                light.enabled = false;
    }

    private static void Summary()
    {
        _rounds++;
        var line = new System.Text.StringBuilder("F-PANO PROBE: after " + _rounds + " round(s), mean frame ms (frames)");
        for (int down = 1; down >= 0; down--)
        {
            line.Append(down == 1 ? " | goggle DOWN:" : " | goggle UP:");
            for (int s = 0; s < States.Length; s++)
                line.Append(" " + States[s] + " " + (_frames[s, down] > 0 ? (_sum[s, down] / _frames[s, down]).ToString("F1") : "-") + " (" + _frames[s, down] + ")");
        }
        int shadowed = 0, on = 0;
        foreach (Light light in LampLights)
            if (light != null)
            {
                if (light.isActiveAndEnabled) on++;
                if (light.shadows != LightShadows.None) shadowed++;
            }
        line.Append(" | rail lamp lights " + LampLights.Count + ", on " + on + ", with shadows " + shadowed + "; screen " + Screen.width + "x" + Screen.height);
        EnvgFusionPlugin.Log?.LogInfo(line.ToString());
    }
}
