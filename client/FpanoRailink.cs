using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace EnvgFusion;

// The F-PANO as four items: the RAILINK rails (in the helmet's headset slot, or the new slot mod_equipment_railink),
// their battery, the mount (only in the rails' mod_nvg) and the goggle (in the mount). Every model the game makes is
// assembled slot by slot through ContainerCollectionView.SlotView.InsertItem; right after the rails go into a helmet
// (or the mount into the rails, or the goggle into the mount), the rails are placed onto that helmet:
//  - fpano_railink (the rails, battery and socket) at the helmet's mod_nvg x Rx(+90) x its fit (item/railink_fit.json,
//    keyed on the helmet's template), its sides and back from the fit's left/right/back;
//  - the rails' own mod_nvg at the helmet's mod_nvg, so the mount sits where a goggle on that helmet would;
//  - the goggle's power cable bound to the rails' socket.
// A helmet with no node for mod_equipment_railink gets an empty one before its slots are attached (the game would skip
// the slot and never make the rails). Only when a model is made; nothing per frame.
internal static class FpanoRailink
{
    // the group's place, turn and scale on the helmet, and its two sides' and back's own shifts (fpano_railink_L, _R, _back)
    private struct Fit { public Vector3 Pos; public Quaternion Rot; public Vector3 Scale; public Vector3 Left, Right, Back; }
    private static readonly string[] Parts = { "fpano_railink_L", "fpano_railink_R", "fpano_railink_back" };
    private static Dictionary<string, Fit>? _fits;

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
                    // one number, or [sx, sy, sz]
                    Scale = scale.Type == Newtonsoft.Json.Linq.JTokenType.Array ? new Vector3((float)scale[0]!, (float)scale[1]!, (float)scale[2]!) : Vector3.one * (float)scale,
                    Left = Vec(entry.Value["left"]),
                    Right = Vec(entry.Value["right"]),
                    Back = Vec(entry.Value["back"]),
                };
            }
        }
        catch (System.Exception e)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: reading the fit table failed: " + e.Message);
        }
        return _fits;
    }

    private static Vector3 Vec(Newtonsoft.Json.Linq.JToken? v) =>
        v != null && v.Type == Newtonsoft.Json.Linq.JTokenType.Array ? new Vector3((float)v[0]!, (float)v[1]!, (float)v[2]!) : Vector3.zero;

    private const string RailsA = "6a4f9a400000000000000011", RailsB = "6a4f9a400000000000000012";
    private const string MountA = "6a4f9a400000000000000031", MountB = "6a4f9a400000000000000032";
    private const string GoggleA = "6a4f9a400000000000000001", GoggleB = "6a4f9a400000000000000002";
    internal const string RailsSlot = "mod_equipment_railink";
    private static readonly Quaternion SlotTurn = Quaternion.Euler(90f, 0f, 0f);   // what SlotView.InsertItem gives a mod with no ModPlacer
    private static readonly HashSet<string> _logged = new HashSet<string>();

    internal static bool IsRails(EFT.InventoryLogic.Item? item) { string id = item != null ? item.TemplateId.ToString() : ""; return id == RailsA || id == RailsB; }
    private static int Kind(EFT.InventoryLogic.Item item)
    {
        string id = item.TemplateId.ToString();
        return id == RailsA || id == RailsB ? 1 : id == MountA || id == MountB ? 2 : id == GoggleA || id == GoggleB ? 3 : 0;
    }

    // the rails above this item (itself, or up to three holders up), null if none
    internal static EFT.InventoryLogic.Item? RailsAbove(EFT.InventoryLogic.Item? item)
    {
        for (int i = 0; i < 4 && item != null; i++)
        {
            if (IsRails(item))
                return item;
            item = item.Parent?.Container?.ParentItem;
        }
        return null;
    }

    internal static void Patch(HarmonyLib.Harmony harmony)
    {
        int patched = 0;
        var insert = HarmonyLib.AccessTools.Method(typeof(ContainerCollectionView.SlotView), nameof(ContainerCollectionView.SlotView.InsertItem));
        if (insert != null)
            try { harmony.Patch(insert, postfix: new HarmonyLib.HarmonyMethod(typeof(FpanoRailink), nameof(AfterInsert))); patched++; } catch (System.Exception e) { EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: InsertItem not hooked: " + e.Message); }
        var attach = HarmonyLib.AccessTools.Method(typeof(EFT.ObjectsFactory), "AttachMods");
        if (attach != null)
            try { harmony.Patch(attach, prefix: new HarmonyLib.HarmonyMethod(typeof(FpanoRailink), nameof(BeforeAttach))); patched++; } catch (System.Exception e) { EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: AttachMods not hooked: " + e.Message); }
        var post = new HarmonyLib.HarmonyMethod(typeof(FpanoRailink), nameof(AfterMade));
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
        EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: placed as each model is assembled (" + patched + " places hooked)");
    }

    // a helmet whose model has no node for the rails' slot: an empty one at its root, before the game looks for it
    private static void BeforeAttach(EFT.InventoryLogic.ContainerCollection __0, ContainerCollectionView __1)
    {
        try
        {
            if (__0 is not EFT.InventoryLogic.CompoundItem compound || compound.Slots == null || __1 == null || __1.GameObject == null)
                return;
            foreach (EFT.InventoryLogic.Slot slot in compound.Slots)
            {
                if (slot == null || !string.Equals(slot.ID, RailsSlot, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                Transform root = __1.GameObject.transform;
                if (Deep(root, RailsSlot, null) != null)
                    return;
                var node = new GameObject(RailsSlot);
                node.transform.SetParent(root, false);
                if (_logged.Add("node/" + compound.TemplateId))
                    EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: helmet " + compound.TemplateId + " (" + __1.GameObject.name + ") had no " + RailsSlot + " node: one added");
                return;
            }
        }
        catch (System.Exception e) { EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: adding the slot node failed: " + e.Message); }
    }

    private static void AfterInsert(ContainerCollectionView.SlotView __instance, EFT.InventoryLogic.Item item, GameObject itemView)
    {
        try
        {
            if (item == null || itemView == null)
                return;
            int kind = Kind(item);
            if (kind == 0)
                return;
            EFT.InventoryLogic.Item? rails = RailsAbove(item);
            Transform? railsRoot = kind == 1 ? itemView.transform : RailsRootAbove(__instance.Bone);
            if (rails != null && railsRoot != null)
                FitRails(railsRoot, rails);
            else
                Cables(itemView.transform, null);
        }
        catch (System.Exception e) { EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: placing failed: " + e.Message); }
    }

    // a model made on its own (an icon, loot, the inspect window): the rails as the prefab has them, cables to their own
    private static void AfterMade(EFT.InventoryLogic.Item __0, GameObject __result)
    {
        try
        {
            if (__0 == null || __result == null)
                return;
            int kind = Kind(__0);
            if (kind == 1)
                FitRails(__result.transform, __0);
            else if (kind != 0)
                Cables(__result.transform, null);
        }
        catch (System.Exception) { }
    }

    private static Transform? RailsRootAbove(Transform? t)
    {
        for (int i = 0; i < 16 && t != null; i++, t = t.parent)
            if (t.name.StartsWith("fpano_railink_rails_", System.StringComparison.Ordinal))
                return t;
        return null;
    }

    // the first node of that name under root (not root itself), breadth first, not inside `skip`
    private static readonly List<Transform> _queue = new List<Transform>();
    private static Transform? Deep(Transform root, string name, Transform? skip)
    {
        _queue.Clear();
        _queue.Add(root);
        for (int i = 0; i < _queue.Count; i++)
        {
            Transform t = _queue[i];
            if (t == skip)
                continue;
            if (i > 0 && string.Equals(t.name, name, System.StringComparison.OrdinalIgnoreCase))
            {
                _queue.Clear();
                return t;
            }
            for (int c = 0; c < t.childCount; c++)
                _queue.Add(t.GetChild(c));
        }
        _queue.Clear();
        return null;
    }

    // the helmet's own mod_nvg: in the nearest model above the rails (the one with its AssetPoolObject)
    private static Transform? HelmetNvg(Transform rails)
    {
        Transform? t = rails.parent;
        for (int i = 0; i < 16 && t != null; i++, t = t.parent)
            if (t.GetComponent<EFT.AssetsManager.AssetPoolObject>() != null)
                return Deep(t, "mod_nvg", rails);
        return null;
    }

    private struct Pose { public Vector3 P; public Quaternion R; public Vector3 S; }
    private static readonly Dictionary<int, Pose> _prefab = new Dictionary<int, Pose>();
    private static void Remember(Transform t)
    {
        int id = t.GetInstanceID();
        if (!_prefab.ContainsKey(id))
            _prefab[id] = new Pose { P = t.localPosition, R = t.localRotation, S = t.localScale };
    }
    private static void Restore(Transform t)
    {
        if (_prefab.TryGetValue(t.GetInstanceID(), out Pose p))
        {
            t.localPosition = p.P;
            t.localRotation = p.R;
            t.localScale = p.S;
        }
    }

    private static readonly List<Transform> _parts = new List<Transform>();
    private static void FitRails(Transform rails, EFT.InventoryLogic.Item railsItem)
    {
        Transform? group = rails.Find("fpano_railink");
        Transform? railsNvg = rails.Find("mod_nvg");
        if (group != null) Remember(group);
        if (railsNvg != null) Remember(railsNvg);
        EFT.InventoryLogic.Item? helmet = railsItem.Parent?.Container?.ParentItem;
        string helmetId = helmet != null ? helmet.TemplateId.ToString() : "";
        Transform? helmetNvg = helmet != null ? HelmetNvg(rails) : null;
        bool known = false;
        Fit fit = default;
        if (helmetNvg != null)
        {
            known = Fits().TryGetValue(helmetId, out fit);
            if (!known)
                fit = new Fit { Pos = Vector3.zero, Rot = Quaternion.identity, Scale = Vector3.one };
            if (group != null)
            {
                group.SetPositionAndRotation(helmetNvg.TransformPoint(SlotTurn * fit.Pos), helmetNvg.rotation * SlotTurn * fit.Rot);
                Vector3 above = group.parent != null ? group.parent.lossyScale : Vector3.one, there = helmetNvg.lossyScale;
                group.localScale = new Vector3(fit.Scale.x * there.x / Mathf.Max(above.x, 1e-6f), fit.Scale.y * there.y / Mathf.Max(above.y, 1e-6f), fit.Scale.z * there.z / Mathf.Max(above.z, 1e-6f));
            }
            if (railsNvg != null)
                railsNvg.SetPositionAndRotation(helmetNvg.position, helmetNvg.rotation);
        }
        else
        {
            if (group != null) Restore(group);
            if (railsNvg != null) Restore(railsNvg);
        }
        if (group != null)
        {
            group.GetComponentsInChildren(true, _parts);
            foreach (Transform t in _parts)
            {
                if (t.name == Parts[0])
                    t.localPosition = known ? fit.Left : Vector3.zero;
                else if (t.name == Parts[1])
                    t.localPosition = known ? fit.Right : Vector3.zero;
                else if (t.name == Parts[2])
                    t.localPosition = known ? fit.Back : Vector3.zero;
                else
                    continue;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }
            _parts.Clear();
        }
        Transform? socket = group != null ? group.Find("fpano_railink_L/fpano_power_socket") : null;
        int cables = Cables(rails, socket);
        string key = (helmetNvg != null ? helmetId : "-") + "/" + rails.name + "/" + cables;
        if (_logged.Add(key))
            EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: " + rails.name + (helmetNvg == null ? " on no helmet: as the prefab has it" : " on helmet " + helmetId + ", " + (known ? "fitted" : "no fit for it: at its mod_nvg as the prefab has it"))
                + (cables > 0 ? ", " + cables + " power cable(s) bound to " + (socket != null ? "the rails' socket" : "their own placeholder") : ""));
    }

    // every power cable under root bound to the socket (null: to its goggle's own placeholder); how many
    private static readonly List<SkinnedMeshRenderer> _skins = new List<SkinnedMeshRenderer>();
    private static int Cables(Transform root, Transform? socket)
    {
        root.GetComponentsInChildren(true, _skins);
        int n = 0;
        foreach (SkinnedMeshRenderer smr in _skins)
        {
            if (!smr.name.StartsWith("fpano_power_cable", System.StringComparison.Ordinal))
                continue;
            Transform[] bones = smr.bones;
            if (bones.Length < 2)
                continue;
            Transform? to = socket;
            for (Transform? t = smr.transform; to == null && t != null; t = t.parent)
                to = t.Find("fpano_power_socket");
            n++;
            if (to == null || bones[1] == to)
                continue;
            // (its bounds are laid around bone 1: they move to the new socket with it)
            if (smr.rootBone == bones[1])
                smr.rootBone = to;
            bones[1] = to;
            smr.bones = bones;
        }
        _skins.Clear();
        return n;
    }

    // ---- the battery: the rails above the worn goggle, their battery slot empty ----
    private static EFT.InventoryLogic.Item? _seenRails;
    private static EFT.InventoryLogic.Slot? _battery;
    private static bool _seenAny, _wasUnpowered;
    internal static bool Unpowered(EFT.InventoryLogic.Item? goggle)
    {
        EFT.InventoryLogic.Item? mount = goggle?.Parent?.Container?.ParentItem;
        EFT.InventoryLogic.Item? rails = mount?.Parent?.Container?.ParentItem;
        if (!_seenAny || !ReferenceEquals(rails, _seenRails))
        {
            _seenAny = true;
            _seenRails = rails;
            _battery = null;
            if (rails is EFT.InventoryLogic.CompoundItem compound && IsRails(rails) && compound.Slots != null)
                foreach (EFT.InventoryLogic.Slot slot in compound.Slots)
                    if (slot != null && string.Equals(slot.ID, "mod_battery", System.StringComparison.OrdinalIgnoreCase))
                        _battery = slot;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO: " + (_battery != null ? "powered from the RAILINK's battery slot" : "no RAILINK battery slot above the goggle: always powered"));
        }
        bool off = _battery != null && _battery.ContainedItem == null;
        if (off != _wasUnpowered)
        {
            _wasUnpowered = off;
            EnvgFusionPlugin.Log?.LogInfo(off ? "F-PANO: no battery in the RAILINK: the goggle goes down dark" : "F-PANO: battery in: powered");
        }
        return off;
    }
}
