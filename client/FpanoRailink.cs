using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace EnvgFusion;

// The RAILINK rails, battery and cable socket (the node fpano_railink, under the F-PANO's prefab root) fitted onto the
// helmet that holds the goggle: each vanilla helmet that takes it has its own place, scale and turn for the group
// (item/railink_fit.json, fitted offline onto the helmet's own shell), set once when the goggle is attached or moved to
// another helmet. Any other holder (a modded helmet, a mask or plate in between) leaves the group as the prefab has it.
// The node is looked for only on such a change (and for a few frames after, as the game builds the model a frame or two
// later), or when it is gone (the model made anew). Never on a timer.
internal static class FpanoRailink
{
    private struct Fit { public Vector3 Pos; public Quaternion Rot; public float Scale; }
    private static Dictionary<string, Fit>? _fits;

    private static object? _device, _holder;
    private static string _holderId = "";
    private static Transform? _node;
    private static int _lookFrames;
    private static bool _applied;
    private const int LookFor = 30;
    private static readonly List<Transform> _found = new List<Transform>();

    private static Dictionary<string, Fit> Fits()
    {
        if (_fits != null)
            return _fits;
        _fits = new Dictionary<string, Fit>();
        try
        {
            using Stream? s = typeof(FpanoRailink).Assembly.GetManifestResourceStream("railink_fit.json");
            if (s == null)
            {
                EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: the fit table is missing from the DLL");
                return _fits;
            }
            using var reader = new StreamReader(s);
            var table = Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd());
            foreach (var entry in table.Properties())
            {
                var pos = entry.Value["pos"];
                var rot = entry.Value["rot"];
                var scale = entry.Value["scale"];
                if (pos == null || rot == null || scale == null)
                    continue;
                _fits[entry.Name] = new Fit
                {
                    Pos = new Vector3((float)pos[0]!, (float)pos[1]!, (float)pos[2]!),
                    Rot = new Quaternion((float)rot[0]!, (float)rot[1]!, (float)rot[2]!, (float)rot[3]!),
                    Scale = (float)scale,
                };
            }
        }
        catch (System.Exception e)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: reading the fit table failed: " + e.Message);
        }
        return _fits;
    }

    // once a frame, with the F-PANO worn on the local player's head (null when not)
    internal static void Update(EFT.Player? wearer, EFT.InventoryLogic.Item? device)
    {
        if (device == null || wearer == null)
        {
            _device = _holder = null;
            _node = null;
            return;
        }
        object? holder = null;
        string holderId = "";
        try
        {
            EFT.InventoryLogic.Item? parent = device.Parent?.Container?.ParentItem;
            holder = parent;
            holderId = parent != null ? parent.TemplateId.ToString() : "";
        }
        catch (System.Exception) { }
        if (!ReferenceEquals(device, _device) || !ReferenceEquals(holder, _holder))
        {
            _device = device;
            _holder = holder;
            _holderId = holderId;
            _node = null;
            _applied = false;
            _lookFrames = LookFor;
        }
        if (_node is not null && _node == null)
        {
            // the model made anew: look again, and fit again
            _node = null;
            _applied = false;
            _lookFrames = LookFor;
        }
        if (_node == null && _lookFrames > 0)
        {
            _lookFrames--;
            ((Component)wearer).transform.GetComponentsInChildren(true, _found);
            foreach (Transform t in _found)
                if (t.name == "fpano_railink")
                {
                    _node = t;
                    break;
                }
            _found.Clear();
        }
        if (_node != null && !_applied)
        {
            _applied = true;
            if (Fits().TryGetValue(_holderId, out Fit fit))
            {
                _node.localPosition = fit.Pos;
                _node.localRotation = fit.Rot;
                _node.localScale = Vector3.one * fit.Scale;
                EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: on helmet " + _holderId + ", fitted (scale " + fit.Scale.ToString("F2") + ")");
            }
            else
            {
                _node.localPosition = Vector3.zero;
                _node.localRotation = Quaternion.identity;
                _node.localScale = Vector3.one;
                EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: on " + (_holderId.Length > 0 ? "holder " + _holderId : "an unknown holder") + ", no fit for it: left as the prefab has it");
            }
        }
    }
}
