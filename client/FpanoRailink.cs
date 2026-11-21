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
    // ---- every other model of the goggle: icons, the inspect window, the inventory, traders, bots, loot ----
    // The game makes an item's model in ObjectsFactory (an item and what hangs in its slots, as one) and parents a worn
    // one in PlayerBody's slot view: right after, each F-PANO in the item's slots gets its rails fitted onto the item that
    // holds it. Only when such a model is made; nothing per frame.
    private static readonly string[] FpanoIds = { "6a4f9a400000000000000001", "6a4f9a400000000000000002" };
    private static readonly List<Transform> _made = new List<Transform>();
    private static readonly HashSet<string> _loggedModel = new HashSet<string>();

    internal static void Patch(HarmonyLib.Harmony harmony)
    {
        var post = new HarmonyLib.HarmonyMethod(typeof(FpanoRailink), nameof(AfterMade));
        int patched = 0;
        foreach (string name in new[] { "CreateItem", "CreateLootPrefab", "CreateCleanLootPrefab" })
            foreach (var m in typeof(EFT.ObjectsFactory).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
            {
                if (m.Name != name || m.ReturnType != typeof(GameObject))
                    continue;
                var ps = m.GetParameters();
                if (ps.Length == 0 || ps[0].ParameterType != typeof(EFT.InventoryLogic.Item))
                    continue;
                try { harmony.Patch(m, postfix: post); patched++; } catch (System.Exception) { }
            }
        var parent = HarmonyLib.AccessTools.Method(typeof(EFT.PlayerBody).GetNestedType("SlotView", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic), "CreateAndParent");
        if (parent != null)
        {
            try { harmony.Patch(parent, postfix: new HarmonyLib.HarmonyMethod(typeof(FpanoRailink), nameof(AfterParented))); patched++; } catch (System.Exception) { }
        }
        EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: fitted on every model the game makes (" + patched + " places hooked)");
    }

    private static void AfterMade(EFT.InventoryLogic.Item __0, GameObject __result)
    {
        try { FitModel(__result, __0); } catch (System.Exception) { }
    }

    private static readonly System.Reflection.FieldInfo? SlotViewItem = HarmonyLib.AccessTools.Field(typeof(EFT.PlayerBody).GetNestedType("SlotView", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic), "_item");
    private static void AfterParented(object __instance, GameObject __1)
    {
        try
        {
            if (SlotViewItem?.GetValue(__instance) is EFT.InventoryLogic.Item item)
                FitModel(__1, item);
        }
        catch (System.Exception) { }
    }

    // the goggle in this item's slots, at any depth (null: none)
    private static EFT.InventoryLogic.Item? FpanoIn(EFT.InventoryLogic.Item item, int depth)
    {
        string id = item.TemplateId.ToString();
        if (id == FpanoIds[0] || id == FpanoIds[1])
            return item;
        if (depth > 6 || item is not EFT.InventoryLogic.CompoundItem compound || compound.Slots == null)
            return null;
        foreach (EFT.InventoryLogic.Slot slot in compound.Slots)
        {
            EFT.InventoryLogic.Item? inner = slot?.ContainedItem;
            if (inner == null)
                continue;
            EFT.InventoryLogic.Item? found = FpanoIn(inner, depth + 1);
            if (found != null)
                return found;
        }
        return null;
    }

    private static void FitModel(GameObject? model, EFT.InventoryLogic.Item? item)
    {
        if (model == null || item == null)
            return;
        EFT.InventoryLogic.Item? fpano = FpanoIn(item, 0);
        if (fpano == null)
            return;
        string holderId = "";
        try { holderId = fpano.Parent?.Container?.ParentItem?.TemplateId.ToString() ?? ""; } catch (System.Exception) { }
        model.GetComponentsInChildren(true, _made);
        bool fitted = false;
        foreach (Transform t in _made)
            if (t.name == "fpano_railink")
                fitted |= Place(t, holderId);
        _made.Clear();
        if (_loggedModel.Add(holderId + "/" + model.name))
            EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: model " + model.name + " on " + (holderId.Length > 0 ? holderId : "no holder") + ", " + (fitted ? "fitted" : "no fit: left as the prefab has it"));
    }

    // the group placed for that holder (true), or put back as the prefab has it (false)
    private static bool Place(Transform node, string holderId)
    {
        if (Fits().TryGetValue(holderId, out Fit fit))
        {
            node.localPosition = fit.Pos;
            node.localRotation = fit.Rot;
            node.localScale = Vector3.one * fit.Scale;
            return true;
        }
        node.localPosition = Vector3.zero;
        node.localRotation = Quaternion.identity;
        node.localScale = Vector3.one;
        return false;
    }
}
