using System;
using System.Reflection;
using UnityEngine;

namespace EnvgFusion;

// K-3PS-C's eyepieces (EyepieceFusion v1, with devices told apart: Register / Configure / ClaimedDevice), found by name
// so neither mod needs the other. The F-PANO's device node is registered with our handler; on a frame the eyepieces show
// it (ClaimedFrame this frame and ClaimedDevice ours) they call it with the tubes' picture and lay ours into the right
// inner tube, and we draw nothing over the screen. The old single Fuse / FusedRect / I2Gain belong to K-ENVG-B and are
// left alone. An older K-3PS-C (no Register): the picture goes over the screen. Read once a frame through getters bound
// at load, so nothing allocates.
internal static class EyepieceLink
{
    private const string TypeName = "EFTNewCamera.Interop.EyepieceFusion";
    private const string AssemblyName = "JustachaeThirdPersonCamera";
    internal const string DeviceNode = "nvg_fpano_LOD0";
    // the thermal rectangle within the right inner tube's circle (its share of the diameter, wide and high): the picture
    // stays inside that tube's lens (its corners within the circle), not over the seam into the outer tube
    internal static readonly Vector2 FusedRect = new Vector2(0.68f, 0.52f);
    private static Func<int>? _claimedFrame;
    private static Func<string?>? _claimedDevice;
    private static Func<Vector4>? _field;
    private static Func<float>? _deployed;
    private static MethodInfo? _configure;
    private static readonly object?[] _configureArgs = new object?[3];
    internal static bool TakesGain => _configure != null;
    private static float _sentGain = -1f;
    private static int _ours = -10;   // the last frame the eyepieces showed our device
    private static bool _loggedClaim;

    // the tubes' gain the eyepieces apply to all four tubes (1 full, 0 dark), sent only when it changes
    internal static void SetGain(float gain)
    {
        if (_configure == null || Mathf.Approximately(gain, _sentGain))
            return;
        _sentGain = gain;
        _configureArgs[0] = DeviceNode;
        _configureArgs[1] = FusedRect;
        _configureArgs[2] = gain;
        try { _configure.Invoke(null, _configureArgs); } catch (Exception) { _configure = null; }
    }

    internal static void Bind()
    {
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
            MethodInfo? register = type.GetMethod("Register", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(Func<RenderTexture, RenderTexture, bool>) }, null);
            if (v != 1 || register == null)
            {
                EnvgFusionPlugin.Log?.LogInfo("F-PANO: K-3PS-C's EyepieceFusion " + (v != 1 ? "is version " + v : "has no Register (older K-3PS-C)") + ": the picture goes over the screen");
                return;
            }
            _claimedFrame = Getter<int>(type, "ClaimedFrame");
            _claimedDevice = Getter<string?>(type, "ClaimedDevice");
            _field = Getter<Vector4>(type, "FieldInMainUv");
            _deployed = Getter<float>(type, "Deployed");
            if (_claimedFrame == null || _claimedDevice == null || _field == null || _deployed == null)
            {
                _claimedFrame = null;
                EnvgFusionPlugin.Log?.LogWarning("F-PANO: K-3PS-C's EyepieceFusion is missing a member: the picture goes over the screen");
                return;
            }
            register.Invoke(null, new object[] { DeviceNode, new Func<RenderTexture, RenderTexture, bool>(EnvgFusion.FuseInto) });
            _configure = type.GetMethod("Configure", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(Vector2), typeof(float) }, null);
            SetGain(1f);
            EnvgFusionPlugin.Log?.LogInfo("F-PANO: registered with K-3PS-C's eyepieces as " + DeviceNode + "; the picture goes into the right inner tube when they show the device"
                + (_configure != null ? ", with our rectangle and the tubes' gain" : ""));
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

    // the last frame the eyepieces showed our device (claimed this frame, and the device claimed is ours)
    private static int Claimed
    {
        get
        {
            if (_claimedFrame != null && _claimedDevice != null && _claimedFrame() == Time.frameCount && _claimedDevice() == DeviceNode)
                _ours = Time.frameCount;
            return _ours;
        }
    }

    // the eyepieces show our device this frame (they claim it at the main camera's pre-cull)
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
    internal static bool ClaimedRecently => Time.frameCount - Claimed <= 1;

    internal static Vector4 FieldInMainUv => _field != null ? _field() : new Vector4(0.5f, 0.5f, 0.5f, 16f / 9f);
    internal static float Deployed => _deployed != null ? _deployed() : 1f;
}
