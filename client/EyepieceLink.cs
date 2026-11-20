using System;
using System.Reflection;
using UnityEngine;

namespace EnvgFusion;

// K-3PS-C's eyepieces (EyepieceFusion v1), found by name so neither mod needs the other: when they show the F-PANO,
// they claim the frame and call our Fuse with the tube's picture; we then draw nothing over the screen and leave the
// tube's colour to them. Read once a frame through getters bound at load, so nothing allocates.
internal static class EyepieceLink
{
    private const string TypeName = "EFTNewCamera.Interop.EyepieceFusion";
    private const string AssemblyName = "JustachaeThirdPersonCamera";
    private static Func<int>? _claimedFrame;
    private static Func<Vector4>? _field;
    private static Func<float>? _deployed;
    // optional (not in every v1): the tubes' gain the eyepieces apply to both tubes (1 full, 0 dark)
    private static FieldInfo? _i2Gain;
    internal static bool TakesGain => _i2Gain != null;
    private static float _sentGain = -1f;
    internal static void SetGain(float gain)
    {
        if (_i2Gain == null || Mathf.Approximately(gain, _sentGain))
            return;
        _sentGain = gain;
        try { _i2Gain.SetValue(null, gain); } catch (Exception) { _i2Gain = null; }
    }
    private static bool _loggedClaim;

    internal static void Bind()
    {
        // (not yet: K-3PS-C's EyepieceFusion v1 takes one mod's picture, and binding here would take it from K-ENVG-B;
        // until it tells the devices apart, the F-PANO's picture goes over the screen)
        EnvgFusionPlugin.Log?.LogInfo("F-PANO: the picture goes over the screen (K-3PS-C's eyepieces take no F-PANO picture yet)");
        return;
        try
        {
            Type? type = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == AssemblyName)
                {
                    type = assembly.GetType(TypeName, false);
                    break;
                }
            if (type == null)
            {
                EnvgFusionPlugin.Log?.LogInfo("F-PANO: no K-3PS-C eyepieces (EyepieceFusion), the picture goes over the screen");
                return;
            }
            FieldInfo? version = type.GetField("Version", BindingFlags.Public | BindingFlags.Static);
            int v = version != null ? (int)version.GetValue(null) : 0;
            if (v != 1)
            {
                EnvgFusionPlugin.Log?.LogWarning("F-PANO: K-3PS-C's EyepieceFusion is version " + v + ", this mod knows 1: the picture goes over the screen");
                return;
            }
            _claimedFrame = Getter<int>(type, "ClaimedFrame");
            _field = Getter<Vector4>(type, "FieldInMainUv");
            _deployed = Getter<float>(type, "Deployed");
            FieldInfo? fuse = type.GetField("Fuse", BindingFlags.Public | BindingFlags.Static);
            if (_claimedFrame == null || _field == null || _deployed == null || fuse == null)
            {
                _claimedFrame = null;
                EnvgFusionPlugin.Log?.LogWarning("F-PANO: K-3PS-C's EyepieceFusion is missing a member: the picture goes over the screen");
                return;
            }
            fuse.SetValue(null, new Func<RenderTexture, RenderTexture, bool>(EnvgFusion.FuseInto));
            FieldInfo? gain = type.GetField("I2Gain", BindingFlags.Public | BindingFlags.Static);
            _i2Gain = gain != null && gain.FieldType == typeof(float) ? gain : null;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO: K-3PS-C eyepieces found (EyepieceFusion v1), the picture goes into them when they show the device"
                + (_i2Gain != null ? "; they take the tubes' gain for both eyes" : "; the tubes' gain set in the right eye's picture only"));
        }
        catch (Exception e)
        {
            _claimedFrame = null;
            EnvgFusionPlugin.Log?.LogWarning("F-PANO: binding K-3PS-C's EyepieceFusion failed, the picture goes over the screen: " + e.Message);
        }
    }

    private static Func<T>? Getter<T>(Type type, string name)
    {
        MethodInfo? get = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
        return get != null && get.ReturnType == typeof(T) ? (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), get) : null;
    }

    private static int Claimed => _claimedFrame != null ? _claimedFrame() : -1;

    // the eyepieces show the device this frame (they claim it at the main camera's pre-cull)
    internal static bool ClaimedNow
    {
        get
        {
            bool now = Claimed == Time.frameCount;
            if (now && !_loggedClaim)
            {
                _loggedClaim = true;
                EnvgFusionPlugin.Log?.LogInfo("F-PANO: K-3PS-C's eyepieces claimed the device, nothing drawn over the screen while they show it");
            }
            return now;
        }
    }

    // this frame or the last: the tube's colour stays theirs between the claim and our late update
    internal static bool ClaimedRecently
    {
        get
        {
            int c = Claimed;
            return c >= 0 && Time.frameCount - c <= 1;
        }
    }

    internal static Vector4 FieldInMainUv => _field != null ? _field() : new Vector4(0.5f, 0.5f, 0.5f, 16f / 9f);
    internal static float Deployed => _deployed != null ? _deployed() : 1f;
}
