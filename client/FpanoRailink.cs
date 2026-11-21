using System.Collections.Generic;
using System.IO;
using Diz.LanguageExtensions;
using UnityEngine;

namespace EnvgFusion;

// The F-PANO as four items: the RAILINK rails (in the helmet's headset slot, or the new slot mod_equipment_railink),
// their battery, the mount (only in the rails' mod_nvg) and the goggle (in the mount). Every model the game makes is
// assembled slot by slot through ContainerCollectionView.SlotView.InsertItem; right after the rails go into a helmet
// (or the mount into the rails, or the goggle into the mount), the rails are placed onto that helmet:
//  - fpano_railink (the rails, battery and socket) at the helmet's mod_nvg x Rx(+90) x its fit (item/railink_fit.json,
//    keyed on the helmet's template), its sides and back from the fit's left/right/back;
//  - the goggle's power cable (the goggle in the mount, the mount in the helmet's own mod_nvg) bound to the socket of
//    the rails on the same helmet, whichever of them goes in last.
// The F-PANO goes into a rail helmet's own mod_nvg or the rails' mod_nvg (at the rails' root, set onto the helmet's
// mod_nvg when the rails are on one), one F-PANO a helmet. Powered (its picture, the cable plugged in, the green light)
// only with the RAILINK (its battery is part of the rails): on the rails, or on a helmet that has them.
// A helmet with no node for mod_equipment_railink gets an empty one before its slots are attached (the game would skip
// the slot and never make the rails). Only when a model is made; nothing per frame.
internal static class FpanoRailink
{
    // the group's place, turn and scale on the helmet, and its two sides' and back's own shifts (fpano_railink_L, _R, _back)
    private struct Fit { public Vector3 Pos; public Quaternion Rot; public Vector3 Scale; public Vector3 Left, Right, Back; }
    private static readonly string[] Parts = { "fpano_railink_L", "fpano_railink_R", "fpano_railink_back" };
    private static readonly string[] RealSize = { "mod_flashlight_000", "mod_flashlight_001", "mod_tactical_000", "mod_tactical_001", "mod_mount_000", "mod_mount_001" };
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

    // where each helmet headset sits on the rails (item/railink_headsets.json): fpano_railink/mod_equipment's local pose
    // under fpano_railink, mapped from that headset's own helmet; a headset not in it: the prefab's place (the RAC's)
    private static Dictionary<string, Pose>? _headsets;
    // the same on rails with no helmet: in the rails' root frame, the rails then fitted as on the headset's own helmet
    private static readonly Dictionary<string, Pose> _onRails = new Dictionary<string, Pose>();
    private static readonly Dictionary<string, string> _onRailsFit = new Dictionary<string, string>();
    private static readonly Dictionary<string, Pose> _onGroup = new Dictionary<string, Pose>();
    // per rail helmet (the rails' sides sit closer on narrow shells): the slot node's pose under fpano_railink and the
    // headset squeezed sideways (S.x) so both arm clips meet the rails; keyed headset + "/" + helmet
    private static readonly Dictionary<string, Pose> _perHelmet = new Dictionary<string, Pose>();
    private static Dictionary<string, Pose> Headsets()
    {
        if (_headsets != null)
            return _headsets;
        _headsets = new Dictionary<string, Pose>();
        try
        {
            using Stream? s = typeof(FpanoRailink).Assembly.GetManifestResourceStream("railink_headsets.json");
            if (s == null)
            {
                EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: the headset table is missing from the DLL");
                return _headsets;
            }
            using var reader = new StreamReader(s);
            foreach (var entry in Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd()).Properties())
            {
                var pos = entry.Value["pos"];
                var rot = entry.Value["rot"];
                if (pos == null || rot == null)
                    continue;
                _headsets[entry.Name] = new Pose { P = Vec(pos), R = new Quaternion((float)rot[0]!, (float)rot[1]!, (float)rot[2]!, (float)rot[3]!), S = Vector3.one };
                var bare = entry.Value["onRails"];
                var barePos = bare?["pos"];
                var bareRot = bare?["rot"];
                if (barePos != null && bareRot != null)
                {
                    _onRails[entry.Name] = new Pose { P = Vec(barePos), R = new Quaternion((float)bareRot[0]!, (float)bareRot[1]!, (float)bareRot[2]!, (float)bareRot[3]!), S = Vector3.one };
                    string? fitOf = (string?)bare!["fit"];
                    if (!string.IsNullOrEmpty(fitOf))
                        _onRailsFit[entry.Name] = fitOf!;
                }
                // on a helmet: the slot node's pose in fpano_railink's own space (the group's scaled units), so the
                // headset follows the rails as fitted to that helmet
                var grouped = entry.Value["onGroup"];
                var groupedPos = grouped?["pos"];
                var groupedRot = grouped?["rot"];
                if (groupedPos != null && groupedRot != null)
                    _onGroup[entry.Name] = new Pose { P = Vec(groupedPos), R = new Quaternion((float)groupedRot[0]!, (float)groupedRot[1]!, (float)groupedRot[2]!, (float)groupedRot[3]!), S = Vector3.one };
                if (entry.Value["perHelmet"] is Newtonsoft.Json.Linq.JObject perHelmet)
                    foreach (var helmet in perHelmet.Properties())
                    {
                        var hp = helmet.Value["pos"];
                        var hr = helmet.Value["rot"];
                        if (hp == null || hr == null)
                            continue;
                        float squeeze = (float?)helmet.Value["scaleX"] ?? 1f;
                        _perHelmet[entry.Name + "/" + helmet.Name] = new Pose { P = Vec(hp), R = new Quaternion((float)hr[0]!, (float)hr[1]!, (float)hr[2]!, (float)hr[3]!), S = new Vector3(squeeze, 1f, 1f) };
                        var axis = helmet.Value["bendAxis"];
                        if (axis != null && helmet.Value["top"] != null && helmet.Value["clip"] != null)
                            _bends[entry.Name + "/" + helmet.Name] = new Bend
                            {
                                Axis = Vec(axis), Top = (float)helmet.Value["top"]!, Clip = (float)helmet.Value["clip"]!,
                                NegX = (float?)helmet.Value["bendNegX"] ?? 0f, PosX = (float?)helmet.Value["bendPosX"] ?? 0f,
                            };
                    }
            }
        }
        catch (System.Exception e)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: reading the headset table failed: " + e.Message);
        }
        return _headsets;
    }

    // the rails' headset slot node placed for the headset in it: the table's pose is relative to the helmet model's root
    // (the cups on the ears as on the headset's own helmet); none, one not in the table, or the rails on no helmet: as
    // the prefab has it
    private static void PlaceHeadset(Transform node, EFT.InventoryLogic.Item? headset, Transform? helmetRoot, Transform railsRoot, string helmetId)
    {
        Remember(node);
        string id = headset != null ? headset.TemplateId.ToString() : "";
        if (id.Length > 0 && BakedSets().TryGetValue(id, out BakedSet set))
        {
            PlaceBaked(node, id, set, helmetRoot, railsRoot, helmetId);
            return;
        }
        bool placed = false;
        // the entry for this helmet (bare rails: the headset's own helmet, whose fit the rails then have)
        string fitOn = helmetRoot != null ? helmetId : (id.Length > 0 && Headsets() != null && _onRailsFit.TryGetValue(id, out string own0) ? own0 : "");
        string bendKey = "";
        if (id.Length > 0 && Headsets() != null && node.parent != null && fitOn.Length > 0 && _perHelmet.ContainsKey(id + "/" + fitOn))
            bendKey = id + "/" + fitOn;
        if (id.Length > 0 && Headsets() != null && node.parent != null
            && (bendKey.Length > 0 && _perHelmet.TryGetValue(bendKey, out Pose g) || helmetRoot != null && _onGroup.TryGetValue(id, out g)))
        {
            // with the rails (their place on this helmet: its own fit where the table has one), at the rails' own size
            // taken off so the headset keeps its own, squeezed sideways where the rails sit narrower (g.S.x)
            node.localPosition = g.P;
            node.localRotation = g.R;
            Vector3 own = _prefab[node.GetInstanceID()].S, scale = node.parent.localScale;
            node.localScale = new Vector3(g.S.x * own.x / Mathf.Max(scale.x, 1e-6f), g.S.y * own.y / Mathf.Max(scale.y, 1e-6f), g.S.z * own.z / Mathf.Max(scale.z, 1e-6f));
            placed = true;
        }
        else if (helmetRoot != null && id.Length > 0 && Headsets().TryGetValue(id, out Pose p))
        {
            node.SetPositionAndRotation(helmetRoot.TransformPoint(p.P), helmetRoot.rotation * p.R);
            placed = true;
        }
        else if (helmetRoot == null && id.Length > 0 && Headsets() != null && _onRails.TryGetValue(id, out Pose bare))
        {
            // the rails on no helmet (an icon, loot): the headset where it sits on them, from the rails' root
            node.SetPositionAndRotation(railsRoot.TransformPoint(bare.P), railsRoot.rotation * bare.R);
            if (node.parent != null)
            {
                // (the rails fitted as on that helmet, scaled: the headset keeps its own size)
                Vector3 own = _prefab[node.GetInstanceID()].S, scale = node.parent.localScale;
                node.localScale = new Vector3(own.x / Mathf.Max(scale.x, 1e-6f), own.y / Mathf.Max(scale.y, 1e-6f), own.z / Mathf.Max(scale.z, 1e-6f));
            }
        }
        else
            Restore(node);
        BendArms(node, placed ? bendKey : "");
        if (id.Length > 0 && _logged.Add("headset/" + id + "/" + fitOn))
            EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: headset " + id + " on the rails" + (helmetRoot != null ? " on helmet " : " (bare, as on ") + fitOn + (helmetRoot != null ? "" : ")") + ", "
                + (placed ? "at its own place" + (_bends.ContainsKey(bendKey) ? ", arms bent to the rails" : "") : "not in the table: at the prefab's place"));
    }

    // ---- a headset's arms bent onto the rails (the band between its top and the clips; above, nothing; below, each side
    // moved whole): a copy of each of its meshes, made once per mesh and helmet, the original put back when it leaves ----
    private struct Bend { public Vector3 Axis; public float Top, Clip, NegX, PosX; }
    private static readonly Dictionary<string, Bend> _bends = new Dictionary<string, Bend>();
    private const string BentSuffix = " (RAILINK arms bent)";
    private static readonly Dictionary<int, Mesh> _unbent = new Dictionary<int, Mesh>();          // a filter -> its own mesh
    private static readonly Dictionary<string, Mesh?> _bentCopies = new Dictionary<string, Mesh?>();
    private static readonly List<MeshFilter> _headsetFilters = new List<MeshFilter>();

    // ---- headsets whose meshes are not readable (the ComTacs): whole shapes baked offline per rail helmet, in the
    // headset root's frame for the table's helmet-root pose (item/railink_headset_meshes.json, bundle
    // ustachae_fpano/railink_headsets.bundle); swapped onto every filter of the headset, the originals put back after ----
    private struct BakedSet { public Dictionary<string, string> Helmets; public string Bare; }
    private static Dictionary<string, BakedSet>? _bakedSets;
    private static Dictionary<string, Mesh>? _bakedMeshes;
    private static readonly HashSet<Mesh> _ours = new HashSet<Mesh>();      // meshes this file put on filters
    private const string BakedBundleKey = "ustachae_fpano/railink_headsets.bundle";

    private static Dictionary<string, BakedSet> BakedSets()
    {
        if (_bakedSets != null)
            return _bakedSets;
        _bakedSets = new Dictionary<string, BakedSet>();
        try
        {
            using Stream? s = typeof(FpanoRailink).Assembly.GetManifestResourceStream("railink_headset_meshes.json");
            if (s == null)
            {
                EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: the baked headset index is missing from the DLL");
                return _bakedSets;
            }
            using var reader = new StreamReader(s);
            foreach (var entry in Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd()).Properties())
            {
                var set = new BakedSet { Helmets = new Dictionary<string, string>(), Bare = (string?)entry.Value["bare"] ?? "" };
                if (entry.Value["helmets"] is Newtonsoft.Json.Linq.JObject helmets)
                    foreach (var helmet in helmets.Properties())
                        set.Helmets[helmet.Name] = (string?)helmet.Value ?? "";
                _bakedSets[entry.Name] = set;
            }
        }
        catch (System.Exception e)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: reading the baked headset index failed: " + e.Message);
        }
        return _bakedSets;
    }

    // the baked bundle, loaded the first time a baked headset goes on the rails; its file from SPT's own bundle table
    private static Mesh? BakedMesh(string asset)
    {
        if (_bakedMeshes == null)
        {
            _bakedMeshes = new Dictionary<string, Mesh>();
            try
            {
                AssetBundle? bundle = null;
                foreach (AssetBundle loaded in AssetBundle.GetAllLoadedAssetBundles())
                    if (loaded != null && loaded.name.EndsWith("railink_headsets.bundle", System.StringComparison.OrdinalIgnoreCase))
                        bundle = loaded;
                string? path = bundle == null ? BakedBundlePath() : null;
                if (bundle == null && path != null && File.Exists(path))
                    bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null)
                    EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: baked headset bundle not found (" + (path ?? "not in SPT's bundle table") + "): ComTacs keep their own meshes");
                else
                {
                    foreach (Mesh m in bundle.LoadAllAssets<Mesh>())
                        _bakedMeshes[m.name] = m;
                    EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: baked headset bundle loaded, " + _bakedMeshes.Count + " meshes");
                }
            }
            catch (System.Exception e)
            {
                EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: loading the baked headset bundle failed: " + e.Message);
            }
        }
        return asset.Length > 0 && _bakedMeshes.TryGetValue(asset, out Mesh mesh) ? mesh : null;
    }

    private static string? BakedBundlePath()
    {
        var manager = HarmonyLib.AccessTools.TypeByName("SPT.Custom.Utils.BundleManager");
        var table = HarmonyLib.AccessTools.Field(manager, "Bundles")?.GetValue(null) as System.Collections.IDictionary;
        var getPath = HarmonyLib.AccessTools.Method(manager, "GetBundleFilePath");
        if (table == null || getPath == null)
            return null;
        object? item = table.Contains(BakedBundleKey) ? table[BakedBundleKey] : null;
        if (item == null)
            foreach (System.Collections.DictionaryEntry e in table)
                if (e.Key is string k && k.EndsWith("railink_headsets.bundle", System.StringComparison.OrdinalIgnoreCase))
                    item = e.Value;
        return item != null ? getPath.Invoke(null, new[] { item }) as string : null;
    }

    // a baked headset: on a helmet its slot node at the helmet root x the table's pos/rot at the helmet's own scale,
    // on bare rails as before (rails root x onRails); then that helmet's baked shape on all its filters
    private static void PlaceBaked(Transform node, string id, BakedSet set, Transform? helmetRoot, Transform railsRoot, string helmetId)
    {
        Headsets();
        string asset = "";
        string on = helmetRoot != null ? helmetId : "bare rails";
        if (helmetRoot != null && Headsets().TryGetValue(id, out Pose p))
        {
            node.SetPositionAndRotation(helmetRoot.TransformPoint(p.P), helmetRoot.rotation * p.R);
            if (node.parent != null)
            {
                Vector3 hs = helmetRoot.lossyScale, ps = node.parent.lossyScale;
                node.localScale = new Vector3(hs.x / Mathf.Max(ps.x, 1e-6f), hs.y / Mathf.Max(ps.y, 1e-6f), hs.z / Mathf.Max(ps.z, 1e-6f));
            }
            set.Helmets.TryGetValue(helmetId, out asset);
        }
        else if (helmetRoot == null && _onRails.TryGetValue(id, out Pose bare))
        {
            node.SetPositionAndRotation(railsRoot.TransformPoint(bare.P), railsRoot.rotation * bare.R);
            if (node.parent != null)
            {
                Vector3 own = _prefab[node.GetInstanceID()].S, scale = node.parent.localScale;
                node.localScale = new Vector3(own.x / Mathf.Max(scale.x, 1e-6f), own.y / Mathf.Max(scale.y, 1e-6f), own.z / Mathf.Max(scale.z, 1e-6f));
            }
            asset = set.Bare;
        }
        else
            Restore(node);
        Mesh? baked = BakedMesh(asset ?? "");
        BendArms(node, "", baked);
        if (_logged.Add("baked/" + id + "/" + on))
            EnvgFusionPlugin.Log?.LogInfo(baked != null
                ? "F-PANO RAILINK: ComTac baked mesh " + asset + " on " + on
                : "F-PANO RAILINK: ComTac " + id + " on " + on + ": no baked mesh" + (string.IsNullOrEmpty(asset) ? " in the index" : " " + asset + " in the bundle") + ", its own mesh");
    }

    // the headset's model under its slot node: a baked shape on every filter, else bent for that key ("" = as it is)
    private static void BendArms(Transform slot, string key, Mesh? baked = null)
    {
        _bends.TryGetValue(key, out Bend bend);
        bool bent = baked == null && key.Length > 0 && _bends.ContainsKey(key);
        for (int c = 0; c < slot.childCount; c++)
        {
            Transform root = slot.GetChild(c);
            root.GetComponentsInChildren(true, _headsetFilters);
            foreach (MeshFilter mf in _headsetFilters)
            {
                Mesh? shown = mf.sharedMesh;
                if (shown == null)
                    continue;
                int fid = mf.GetInstanceID();
                if (!_ours.Contains(shown))
                    _unbent[fid] = shown;
                if (!_unbent.TryGetValue(fid, out Mesh original) || original == null)
                    continue;
                Mesh? want = baked != null ? baked : bent ? BentCopy(original, key, bend, root.worldToLocalMatrix * mf.transform.localToWorldMatrix) : original;
                if (want != null && mf.sharedMesh != want)
                {
                    if (want != original)
                        _ours.Add(want);
                    mf.sharedMesh = want;
                }
            }
            _headsetFilters.Clear();
        }
    }

    // a headset model leaving the rails (or made anew): its own meshes back
    internal static void Unbend(Transform root)
    {
        if (_unbent.Count == 0)
            return;
        root.GetComponentsInChildren(true, _headsetFilters);
        foreach (MeshFilter mf in _headsetFilters)
            if (mf.sharedMesh != null && _ours.Contains(mf.sharedMesh) && _unbent.TryGetValue(mf.GetInstanceID(), out Mesh original) && original != null)
                mf.sharedMesh = original;
        _headsetFilters.Clear();
    }

    private static Mesh? BentCopy(Mesh original, string key, Bend bend, Matrix4x4 meshToRoot)
    {
        string cacheKey = original.GetInstanceID() + "/" + key;
        if (_bentCopies.TryGetValue(cacheKey, out Mesh? made))
            return made;
        Mesh? copy = null;
        try
        {
            if (!original.isReadable)
                EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: headset mesh " + original.name + " is not readable: its arms left as they are");
            else
            {
                Matrix4x4 rootToMesh = meshToRoot.inverse;
                Vector3[] v = original.vertices;
                float span = Mathf.Max(bend.Top - bend.Clip, 1e-5f);
                float most = 0f;
                for (int i = 0; i < v.Length; i++)
                {
                    Vector3 r = meshToRoot.MultiplyPoint3x4(v[i]);
                    float h = Vector3.Dot(r, bend.Axis);
                    float t = Mathf.Clamp01((bend.Top - h) / span);
                    float w = t * t * (3f - 2f * t);
                    float dx = (r.x < 0f ? bend.NegX : bend.PosX) * w;
                    if (dx == 0f)
                        continue;
                    r.x += dx;
                    v[i] = rootToMesh.MultiplyPoint3x4(r);
                    most = Mathf.Max(most, Mathf.Abs(dx));
                }
                copy = Object.Instantiate(original);
                copy.name = original.name + BentSuffix;
                copy.vertices = v;
                copy.RecalculateBounds();
                copy.UploadMeshData(true);
                EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: " + original.name + " arms bent for " + key + " (up to " + (most * 1000f).ToString("F1") + " mm)");
            }
        }
        catch (System.Exception e)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: bending " + original.name + " failed: " + e.Message);
            copy = null;
        }
        _bentCopies[cacheKey] = copy;
        return copy;
    }

    // ---- the helmet's own rails, taken off while the RAILINK is on (item/railink_hide.json, by the helmet's shape) ----
    // Each entry: a renderer of the helmet (by name), the mesh it shows (by name, with its triangle count), and the
    // triangles to drop, numbered over the mesh's submeshes in turn. The renderer gets a copy without them (made once per
    // mesh), the helmet is held at LOD0 (the indices are LOD0's), and all goes back when the rails come off. The game's
    // own swap of a helmet's mesh (CustomItem, for a face cover) is followed: the cut laid again on what it shows.
    private struct Hide { public string Renderer, Mesh; public int Triangles; public int[] Drop; }
    private static Dictionary<string, List<Hide>>? _hides;
    private static Dictionary<string, List<Hide>> Hides()
    {
        if (_hides != null)
            return _hides;
        _hides = new Dictionary<string, List<Hide>>();
        try
        {
            using Stream? s = typeof(FpanoRailink).Assembly.GetManifestResourceStream("railink_hide.json");
            if (s == null)
            {
                EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: the hide table is missing from the DLL");
                return _hides;
            }
            using var reader = new StreamReader(s);
            foreach (var shape in Newtonsoft.Json.Linq.JObject.Parse(reader.ReadToEnd()).Properties())
            {
                var list = new List<Hide>();
                foreach (var e in shape.Value)
                {
                    var drop = e["drop"];
                    if (drop == null)
                        continue;
                    var idx = new int[((Newtonsoft.Json.Linq.JArray)drop).Count];
                    int n = 0;
                    foreach (var d in drop)
                        idx[n++] = (int)d;
                    list.Add(new Hide { Renderer = (string?)e["renderer"] ?? "", Mesh = (string?)e["mesh"] ?? "", Triangles = (int?)e["triangles"] ?? -1, Drop = idx });
                }
                _hides[shape.Name] = list;
            }
        }
        catch (System.Exception e)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: reading the hide table failed: " + e.Message);
        }
        return _hides;
    }

    private const string CutSuffix = " (RAILINK on)";
    private static readonly Dictionary<int, Mesh?> _cuts = new Dictionary<int, Mesh?>();
    // a copy of the mesh without the dropped triangles of every entry for it (null: the mesh is not as the table says)
    private static Mesh? CutOf(Mesh src, List<Hide> entries, string rendererName)
    {
        if (_cuts.TryGetValue(src.GetInstanceID(), out Mesh? made))
            return made;
        Mesh? cut = null;
        try
        {
            var drop = new HashSet<int>();
            int expected = -1;
            foreach (Hide h in entries)
                if (h.Renderer == rendererName && h.Mesh == src.name)
                {
                    expected = h.Triangles;
                    foreach (int i in h.Drop)
                        drop.Add(i);
                }
            int total = 0;
            for (int s = 0; s < src.subMeshCount; s++)
                total += (int)src.GetIndexCount(s) / 3;
            if (drop.Count == 0 || total != expected || !src.isReadable)
                EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: mesh " + src.name + " has " + total + " triangles" + (src.isReadable ? "" : ", not readable") + ", the table says " + expected + ": its rails left on");
            else
            {
                cut = Object.Instantiate(src);
                cut.name = src.name + CutSuffix;
                int at = 0, dropped = 0;
                var keep = new List<int>();
                for (int s = 0; s < src.subMeshCount; s++)
                {
                    int[] tris = src.GetTriangles(s);
                    keep.Clear();
                    for (int t = 0; t < tris.Length / 3; t++, at++)
                    {
                        if (drop.Contains(at)) { dropped++; continue; }
                        keep.Add(tris[3 * t]); keep.Add(tris[3 * t + 1]); keep.Add(tris[3 * t + 2]);
                    }
                    cut.SetTriangles(keep, s, false);
                }
                cut.UploadMeshData(true);
                EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: " + src.name + ": " + dropped + " of " + total + " triangles (its own rails) taken off");
            }
        }
        catch (System.Exception e)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: cutting " + src.name + " failed: " + e.Message);
            cut = null;
        }
        _cuts[src.GetInstanceID()] = cut;
        return cut;
    }

    private sealed class Railed { public string Shape = ""; public readonly List<KeyValuePair<MeshFilter, Mesh>> Originals = new List<KeyValuePair<MeshFilter, Mesh>>(); public LODGroup? Lod; }
    private static readonly Dictionary<int, Railed> _railed = new Dictionary<int, Railed>();
    private static readonly List<MeshFilter> _filters = new List<MeshFilter>();

    private static string ShapeOf(EFT.InventoryLogic.Item helmet)
    {
        string path = helmet.Template?.Prefab.path ?? "";
        string[] parts = path.Split('/');
        return parts.Length >= 2 ? parts[parts.Length - 2] : "";
    }

    // the helmet model with the RAILINK on: its own rails cut away, held at LOD0
    private static void Rail(Transform helmetRoot, string shape, Transform? rails)
    {
        if (shape.Length == 0 || !Hides().TryGetValue(shape, out List<Hide> entries))
            return;
        int key = helmetRoot.GetInstanceID();
        if (!_railed.TryGetValue(key, out Railed? railed))
            _railed[key] = railed = new Railed { Shape = shape };
        helmetRoot.GetComponentsInChildren(true, _filters);
        foreach (MeshFilter mf in _filters)
        {
            Mesh? shown = mf.sharedMesh;
            if (shown == null || shown.name.EndsWith(CutSuffix, System.StringComparison.Ordinal) || (rails != null && mf.transform.IsChildOf(rails)))
                continue;
            string name = mf.gameObject.name;
            bool listed = false;
            foreach (Hide h in entries)
                if (h.Renderer == name && h.Mesh == shown.name) { listed = true; break; }
            if (!listed)
                continue;
            Mesh? cut = CutOf(shown, entries, name);
            if (cut == null)
                continue;
            railed.Originals.RemoveAll(kv => kv.Key == mf);
            railed.Originals.Add(new KeyValuePair<MeshFilter, Mesh>(mf, shown));
            mf.sharedMesh = cut;
        }
        _filters.Clear();
        if (railed.Lod == null)
        {
            railed.Lod = helmetRoot.GetComponent<LODGroup>();
            railed.Lod?.ForceLOD(0);
        }
        if (_logged.Add("railed/" + shape + "/" + helmetRoot.name))
        {
            string seen = "";
            if (railed.Originals.Count == 0)
            {
                // nothing the table names: what this model shows, for the table
                helmetRoot.GetComponentsInChildren(true, _filters);
                foreach (MeshFilter mf in _filters)
                    if (rails == null || !mf.transform.IsChildOf(rails))
                        seen += " [" + mf.gameObject.name + " / " + (mf.sharedMesh != null ? mf.sharedMesh.name + " " + (mf.sharedMesh.isReadable ? mf.sharedMesh.triangles.Length / 3 : -1) : "none") + "]";
                _filters.Clear();
            }
            EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: helmet " + shape + " (" + helmetRoot.name + "): " + railed.Originals.Count + " mesh(es) without their own rails, " + (railed.Lod != null ? "held at LOD0" : "no LODGroup")
                + (seen.Length > 0 ? "; none in the hide table, the model shows:" + seen : ""));
        }
    }

    // the rails off that helmet model (or it made anew): its meshes and LODs back
    private static void Unrail(Transform helmetRoot)
    {
        int key = helmetRoot.GetInstanceID();
        if (!_railed.TryGetValue(key, out Railed? railed))
            return;
        _railed.Remove(key);
        foreach (var kv in railed.Originals)
            if (kv.Key != null && kv.Key.sharedMesh != null && kv.Key.sharedMesh.name.EndsWith(CutSuffix, System.StringComparison.Ordinal))
                kv.Key.sharedMesh = kv.Value;
        railed.Lod?.ForceLOD(-1);
    }

    private static Transform? HelmetRootOf(Transform? t)
    {
        for (int i = 0; i < 24 && t != null; i++, t = t.parent)
            if (t.GetComponent<EFT.AssetsManager.AssetPoolObject>() != null)
                return t;
        return null;
    }

    // the game swapped the helmet's mesh (base / custom): the cut laid again on the one it shows
    private static void AfterSetIsCustom(CustomItem __instance)
    {
        try
        {
            if (_railed.Count == 0 || __instance == null)
                return;
            Transform? root = HelmetRootOf(__instance.transform);
            if (root != null && _railed.TryGetValue(root.GetInstanceID(), out Railed? railed))
                Rail(root, railed.Shape, null);
        }
        catch (System.Exception) { }
    }

    // the rails taken out of a helmet's slot: that helmet's own rails back
    private static void BeforeRemove(ContainerCollectionView.SlotView __instance)
    {
        try
        {
            if (__instance?.Item == null || !IsRails(__instance.Item) || __instance.Bone == null)
                return;
            Transform? root = HelmetRootOf(__instance.Bone);
            if (root != null)
            {
                Unrail(root);
                Cables(root, null);
            }
        }
        catch (System.Exception) { }
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

    // the rails in one of this item's slots (the helmet's headset or railink slot), null if none
    internal static EFT.InventoryLogic.Item? RailsOn(EFT.InventoryLogic.Item? helmet, out EFT.InventoryLogic.Slot? where)
    {
        where = null;
        if (helmet is not EFT.InventoryLogic.CompoundItem compound || compound.Slots == null)
            return null;
        foreach (EFT.InventoryLogic.Slot slot in compound.Slots)
        {
            EFT.InventoryLogic.Item? inside = slot?.ContainedItem;
            if (inside != null && IsRails(inside))
            {
                where = slot;
                return inside;
            }
        }
        return null;
    }
    internal static bool IsMount(EFT.InventoryLogic.Item? item) { string id = item != null ? item.TemplateId.ToString() : ""; return id == MountA || id == MountB; }
    // the F-PANO (with its mount, one item) or the old separate mount: what needs the rails on its helmet
    internal static bool NeedsRails(EFT.InventoryLogic.Item? item) { string id = item != null ? item.TemplateId.ToString() : ""; return id == GoggleA || id == GoggleB || id == MountA || id == MountB; }

    private static EFT.InventoryLogic.Slot? SlotNamed(EFT.InventoryLogic.Item? item, string id)
    {
        if (item is not EFT.InventoryLogic.CompoundItem compound || compound.Slots == null)
            return null;
        foreach (EFT.InventoryLogic.Slot slot in compound.Slots)
            if (slot != null && slot.ID == id)
                return slot;
        return null;
    }
    // that slot holds an F-PANO other than this item
    private static bool HoldsOther(EFT.InventoryLogic.Slot? slot, EFT.InventoryLogic.Item item) =>
        slot?.ContainedItem != null && !ReferenceEquals(slot.ContainedItem, item) && NeedsRails(slot.ContainedItem);

    // ---- the rules: one F-PANO a helmet (its own mod_nvg or the rails' one, not both); with the rails, adapters only on
    // their two mounts ----
    private static void AfterCheckConditions(EFT.InventoryLogic.Slot __instance, EFT.InventoryLogic.Item item, bool ignoreRestrictions, ref Option<bool> __result)
    {
        try
        {
            if (ignoreRestrictions || __result.Failed || item == null || __instance == null)
                return;
            bool second = false;
            if (__instance.ID == "mod_nvg" && NeedsRails(item))
            {
                EFT.InventoryLogic.Item? holder = __instance.ParentItem;
                second = IsRails(holder)
                    ? HoldsOther(SlotNamed(holder!.CurrentAddress?.Container?.ParentItem, "mod_nvg"), item)   // onto the rails: the helmet's own
                    : HoldsOther(SlotNamed(RailsOn(holder, out _), "mod_nvg"), item);                      // onto the helmet: its rails'
            }
            else if (IsRails(item))
            {
                // rails carrying an F-PANO onto a helmet that has one
                EFT.InventoryLogic.Item? carried = SlotNamed(item, "mod_nvg")?.ContainedItem;
                second = carried != null && NeedsRails(carried) && HoldsOther(SlotNamed(__instance.ParentItem, "mod_nvg"), carried);
            }
            // the adapters: with the rails on, only their two mounts (left, right) take one; the helmet's own mod_mount
            // sits on its hidden rail under one of them, so it takes nothing, and rails don't go onto a helmet whose own
            // mod_mount holds something
            bool mount = false;
            if (__instance.ID == "mod_mount" && !IsRails(__instance.ParentItem) && RailsOn(__instance.ParentItem, out _) != null)
                mount = true;
            else if (IsRails(item) && SlotNamed(__instance.ParentItem, "mod_mount")?.ContainedItem != null)
                mount = true;
            if (!second && !mount)
                return;
            __result = new EFT.InventoryLogic.Slot.ItemFiltersWontAllowError(item, __instance);
            if (_logged.Add(second ? "refused-second" : "refused-mount"))
                EnvgFusionPlugin.Log?.LogInfo(second ? "F-PANO RAILINK: a second F-PANO on one helmet refused"
                    : "F-PANO RAILINK: the helmet's own mod_mount and the RAILINK together refused (adapters go on the RAILINK's two mounts)");
        }
        catch (System.Exception) { }
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
        var remove = HarmonyLib.AccessTools.Method(typeof(ContainerCollectionView.SlotView), nameof(ContainerCollectionView.SlotView.RemoveItem));
        if (remove != null)
            try { harmony.Patch(remove, prefix: new HarmonyLib.HarmonyMethod(typeof(FpanoRailink), nameof(BeforeRemove))); patched++; } catch (System.Exception e) { EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: RemoveItem not hooked: " + e.Message); }
        var custom = HarmonyLib.AccessTools.Method(typeof(CustomItem), nameof(CustomItem.SetIsCustom));
        if (custom != null)
            try { harmony.Patch(custom, postfix: new HarmonyLib.HarmonyMethod(typeof(FpanoRailink), nameof(AfterSetIsCustom))); patched++; } catch (System.Exception e) { EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: SetIsCustom not hooked: " + e.Message); }
        var check = HarmonyLib.AccessTools.Method(typeof(EFT.InventoryLogic.Slot), nameof(EFT.InventoryLogic.Slot.CheckConditions));
        if (check != null)
            try { harmony.Patch(check, postfix: new HarmonyLib.HarmonyMethod(typeof(FpanoRailink), nameof(AfterCheckConditions))); patched++; } catch (System.Exception e) { EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: CheckConditions not hooked: " + e.Message); }
        var info = HarmonyLib.AccessTools.Method(typeof(EFT.UI.DragAndDrop.GridItemView), nameof(EFT.UI.DragAndDrop.GridItemView.UpdateInfo));
        if (info != null)
            try { harmony.Patch(info, postfix: new HarmonyLib.HarmonyMethod(typeof(FpanoRailink), nameof(AfterUpdateInfo))); patched++; } catch (System.Exception e) { EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: GridItemView.UpdateInfo not hooked: " + e.Message); }
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
        // (the inventory's icons are made through the async factory, from the pool: a model last worn on the player could
        // come back with its cable still plugged into those rails, far away, and the icon framed around both)
        var made = HarmonyLib.AccessTools.Method(typeof(EFT.ObjectsFactory), "CreateItemAsync");
        if (made != null && made.ReturnType == typeof(System.Threading.Tasks.Task<GameObject>))
            try { harmony.Patch(made, postfix: new HarmonyLib.HarmonyMethod(typeof(FpanoRailink), nameof(AfterMadeAsync))); patched++; } catch (System.Exception e) { EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: CreateItemAsync not hooked: " + e.Message); }
        else
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: CreateItemAsync not found: icons keep the pool's state");
        EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: placed as each model is assembled (" + patched + " places hooked)");
    }

    // a helmet whose model has no node for the rails' slot: an empty one at its root, before the game looks for it
    private static void BeforeAttach(EFT.InventoryLogic.ContainerCollection __0, ContainerCollectionView __1)
    {
        try
        {
            if (__0 is not EFT.InventoryLogic.CompoundItem compound || compound.Slots == null || __1 == null || __1.GameObject == null)
                return;
            // a helmet model made anew (from the pool): its own rails back until the RAILINK is placed on it again
            if (_railed.Count > 0)
                Unrail(__1.GameObject.transform);
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
            if (FpanoStrobe.IsStrobe(item))
                FpanoStrobe.Attach(itemView, item);
            else if (FpanoLamp.IsLamp(item))
                FpanoLamp.Attach(itemView, item);
            int kind = Kind(item);
            if (kind == 0)
            {
                // a headset into the rails' headset slot: the rails placed again (on bare rails they take the fit of the
                // headset's own helmet), its node at the headset's own place
                var into = item.CurrentAddress?.Container;
                if (into != null && into.ID == "mod_equipment" && IsRails(into.ParentItem) && __instance.Bone != null)
                {
                    Transform? railsRoot = RailsRootAbove(__instance.Bone);
                    if (railsRoot != null)
                        FitRails(railsRoot, into.ParentItem);
                }
                else if (_unbent.Count > 0 && Headsets().ContainsKey(item.TemplateId.ToString()))
                    Unbend(itemView.transform);
                return;
            }
            if (kind == 1)
            {
                FitRails(itemView.transform, item);
                return;
            }
            // the mount into a helmet, or the goggle into a mount: the model this went into, or the one above it,
            // holds the rails (the helmet); the cable bound to their socket there, else to its own
            Transform? model = HelmetRootOf(__instance.Bone);
            for (int up = 0; up < 3 && model != null; up++, model = HelmetRootOf(model.parent))
            {
                Transform? railsRoot = RailsIn(model);
                if (railsRoot != null)
                {
                    BindCables(model, railsRoot);
                    return;
                }
            }
            Cables(itemView.transform, null);
        }
        catch (System.Exception e) { EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: placing failed: " + e.Message); }
    }

    // the same once an async model is ready (only ours are touched)
    private static void AfterMadeAsync(EFT.InventoryLogic.Item __0, ref System.Threading.Tasks.Task<GameObject> __result)
    {
        if (__0 != null && __result != null && Ours(__0))
            __result = FinishMade(__0, __result);
    }
    private static async System.Threading.Tasks.Task<GameObject> FinishMade(EFT.InventoryLogic.Item item, System.Threading.Tasks.Task<GameObject> making)
    {
        GameObject model = await making;
        if (model != null)
        {
            AfterMade(item, model);
            if (_logged.Add("async/" + Kind(item)))
                EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: async model (icon) of " + item.TemplateId + " set up: " + Framed(model.transform));
        }
        return model;
    }

    // whether this item's model, or one inside it, is one of ours
    private static bool Ours(EFT.InventoryLogic.Item item)
    {
        if (Kind(item) != 0 || FpanoStrobe.IsStrobe(item) || FpanoLamp.IsLamp(item))
            return true;
        if (item is EFT.InventoryLogic.CompoundItem compound && compound.Slots != null)
            foreach (EFT.InventoryLogic.Slot slot in compound.Slots)
                if (slot?.ContainedItem != null && Ours(slot.ContainedItem))
                    return true;
        return false;
    }

    // what an icon frames: the active renderers' bounds together (for the log)
    private static readonly List<Renderer> _framed = new List<Renderer>();
    private static string Framed(Transform root)
    {
        root.GetComponentsInChildren(false, _framed);
        bool any = false;
        Bounds all = default;
        foreach (Renderer r in _framed)
        {
            if (!any) { all = r.bounds; any = true; }
            else all.Encapsulate(r.bounds);
        }
        int n = _framed.Count;
        _framed.Clear();
        return n + " renderers, bounds " + (any ? all.size.ToString("F3") + " around " + (all.center - root.position).ToString("F3") + " from the root" : "none");
    }

    // ---- the rails' icon with an F-PANO on their mod_nvg: the prefab's framing is for the rails alone and cut the
    // lenses off; fitted on the real outlines (rails + goggle, cable plugged) for each of the goggle's two positions.
    // The prefab's own values back without a goggle (the pool reuses the model) ----
    private static readonly Dictionary<int, (Vector3 pos, bool offset, float scale)> _iconPrefab = new Dictionary<int, (Vector3, bool, float)>();
    private static void IconFraming(Transform rails, EFT.InventoryLogic.Item railsItem)
    {
        PreviewPivot? pivot = rails.GetComponent<PreviewPivot>();
        if (pivot == null || pivot.Icon == null)
            return;
        int id = pivot.GetInstanceID();
        if (!_iconPrefab.TryGetValue(id, out var own))
            _iconPrefab[id] = own = (pivot.Icon.position, pivot.Icon.hasOffset, pivot.Icon.boundsScale);
        EFT.InventoryLogic.Item? goggle = null;
        if (railsItem is EFT.InventoryLogic.CompoundItem compound && compound.Slots != null)
            foreach (EFT.InventoryLogic.Slot slot in compound.Slots)
                if (slot != null && slot.ID == "mod_nvg" && slot.ContainedItem != null && Kind(slot.ContainedItem) == 3)
                    goggle = slot.ContainedItem;
        if (goggle == null)
        {
            pivot.Icon.position = own.pos;
            pivot.Icon.hasOffset = own.offset;
            pivot.Icon.boundsScale = own.scale;
            return;
        }
        bool down = goggle.GetItemComponent<EFT.InventoryLogic.TogglableComponent>()?.On ?? false;
        pivot.Icon.boundsScale = down ? 1.1204f : 1.0348f;
        pivot.Icon.position = down ? new Vector3(0.01557f, -0.00680f, 0f) : new Vector3(0.00498f, -0.01048f, 0f);
        pivot.Icon.hasOffset = true;
    }

    // a model made on its own (an icon, loot, the inspect window): the rails as the prefab has them, cables to their own
    private static void AfterMade(EFT.InventoryLogic.Item __0, GameObject __result)
    {
        try
        {
            if (__0 == null || __result == null)
                return;
            if (FpanoStrobe.IsStrobe(__0))
                FpanoStrobe.Attach(__result, __0);
            else if (FpanoLamp.IsLamp(__0))
                FpanoLamp.Attach(__result, __0);
            int kind = Kind(__0);
            if (kind == 1)
            {
                FitRails(__result.transform, __0);
                IconFraming(__result.transform, __0);
            }
            else if (kind != 0)
                Cables(__result.transform, null);
            else if (_unbent.Count > 0 && Headsets().ContainsKey(__0.TemplateId.ToString()))
                Unbend(__result.transform);
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
        EFT.InventoryLogic.Item? helmet = railsItem.CurrentAddress?.Container?.ParentItem;
        string helmetId = helmet != null ? helmet.TemplateId.ToString() : "";
        Transform? helmetRoot = helmet != null ? HelmetRootOf(rails.parent) : null;
        Transform? helmetNvg = helmetRoot != null ? Deep(helmetRoot, "mod_nvg", rails) : null;
        bool known = false;
        Fit fit = default;
        EFT.InventoryLogic.Item? headset = null;
        if (railsItem is EFT.InventoryLogic.CompoundItem withSlots && withSlots.Slots != null)
            foreach (EFT.InventoryLogic.Slot slot in withSlots.Slots)
                if (slot != null && slot.ID == "mod_equipment")
                    headset = slot.ContainedItem;
        string headsetId = headset != null ? headset.TemplateId.ToString() : "";
        if (helmetNvg == null && headsetId.Length > 0 && Headsets() != null && _onRailsFit.TryGetValue(headsetId, out string fitOf) && Fits().TryGetValue(fitOf, out fit))
        {
            // bare rails with a headset: fitted as on the headset's own helmet (in the rails' root frame, which is the
            // goggle's root frame, as the group is in under a helmet's mod_nvg)
            known = true;
            if (railsNvg != null) Restore(railsNvg);
            if (group != null)
            {
                group.localPosition = fit.Pos;
                group.localRotation = fit.Rot;
                group.localScale = fit.Scale;
            }
            if (_logged.Add("bare/" + headsetId))
                EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: bare rails with headset " + headsetId + ": fitted as on helmet " + fitOf);
        }
        else if (helmetNvg != null)
        {
            known = Fits().TryGetValue(helmetId, out fit);
            if (!known)
                fit = new Fit { Pos = Vector3.zero, Rot = Quaternion.identity, Scale = Vector3.one };
            // an F-PANO on the rails where one in the helmet's own mod_nvg would be
            if (railsNvg != null)
                railsNvg.SetPositionAndRotation(helmetNvg.position, helmetNvg.rotation);
            if (group != null)
            {
                group.SetPositionAndRotation(helmetNvg.TransformPoint(SlotTurn * fit.Pos), helmetNvg.rotation * SlotTurn * fit.Rot);
                Vector3 above = group.parent != null ? group.parent.lossyScale : Vector3.one, there = helmetNvg.lossyScale;
                group.localScale = new Vector3(fit.Scale.x * there.x / Mathf.Max(above.x, 1e-6f), fit.Scale.y * there.y / Mathf.Max(above.y, 1e-6f), fit.Scale.z * there.z / Mathf.Max(above.z, 1e-6f));
            }
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
                {
                    // the lights' and adapters' slots: the group's fit scale taken off, so what hangs there keeps its size
                    // (the prefab's own scale kept: the right-hand slots are the left ones mirrored, x = -1)
                    if (System.Array.IndexOf(RealSize, t.name) >= 0)
                    {
                        Remember(t);
                        Vector3 own = _prefab[t.GetInstanceID()].S, g = group.localScale;
                        t.localScale = new Vector3(own.x / Mathf.Max(g.x, 1e-6f), own.y / Mathf.Max(g.y, 1e-6f), own.z / Mathf.Max(g.z, 1e-6f));
                    }
                    continue;
                }
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
            }
            _parts.Clear();
        }
        // the headset (its node under the group: placed after it)
        Transform? headsetNode = group != null ? group.Find("mod_equipment") : null;
        if (headsetNode != null)
            PlaceHeadset(headsetNode, headset, helmetNvg != null ? helmetRoot : null, rails, helmetId);
        // the helmet's own rails off
        if (helmetRoot != null && helmet != null && helmetNvg != null)
            Rail(helmetRoot, ShapeOf(helmet), rails);
        int cables = BindCables(helmetNvg != null && helmetRoot != null ? helmetRoot : rails, rails);
        string key = (helmetNvg != null ? helmetId : "-") + "/" + rails.name + "/" + cables;
        if (_logged.Add(key))
            EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: " + rails.name + (helmetNvg == null ? " on no helmet: as the prefab has it" : " on helmet " + helmetId + ", " + (known ? "fitted" : "no fit for it: at its mod_nvg as the prefab has it"))
                + (cables > 0 ? ", the goggle's power cable bound to the rails' socket" : ""));
    }

    // the rails' model within this model (by its root's name), null if none
    private static Transform? RailsIn(Transform model)
    {
        _queue.Clear();
        _queue.Add(model);
        for (int i = 0; i < _queue.Count; i++)
        {
            Transform t = _queue[i];
            if (t.name.StartsWith("fpano_railink_rails_", System.StringComparison.Ordinal))
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

    // every power cable in the helmet's model bound to these rails' socket; how many
    private static int BindCables(Transform helmetRoot, Transform rails)
    {
        Transform? socket = rails.Find("fpano_railink/fpano_railink_L/fpano_power_socket");
        return socket != null ? Cables(helmetRoot, socket) : 0;
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
            // shown only plugged into the rails (its object off on its own placeholder: the icon frames every renderer
            // under an active object, a disabled one too, so only an inactive object keeps a lone F-PANO's icon tight)
            bool plugged = socket != null && to == socket;
            if (smr.gameObject.activeSelf != plugged)
                smr.gameObject.SetActive(plugged);
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

    // ---- the power: the RAILINK's battery is part of the rails ----
    // The F-PANO is powered on the rails' own mod_nvg, or in a helmet that has the rails on (or, as owned from 0.2.9, in
    // the old separate mount on such a helmet).
    internal static bool PoweredAt(EFT.InventoryLogic.Item? goggle)
    {
        EFT.InventoryLogic.Item? holder = goggle?.CurrentAddress?.Container?.ParentItem;
        if (IsMount(holder))
            holder = holder!.CurrentAddress?.Container?.ParentItem;
        return holder != null && (IsRails(holder) || RailsOn(holder, out _) != null);
    }

    // the icon's toggle light: on only when powered (the game shows the toggle's own state)
    private static void AfterUpdateInfo(EFT.UI.DragAndDrop.GridItemView __instance)
    {
        try
        {
            var item = __instance?.Item;
            if (item == null || Kind(item) != 3 || __instance!._togglableIcon == null || !__instance._togglableIcon.gameObject.activeSelf)
                return;
            if (!PoweredAt(item))
                __instance._togglableIcon.sprite = EFTHardSettings.Instance.StaticIcons.TogglableOff;
        }
        catch (System.Exception) { }
    }

    // the worn goggle, once a frame: the holder's slots compared by reference (nothing allocated); looked at again only
    // when the holder or what its slots hold changes
    private static EFT.InventoryLogic.Item? _seenGoggle, _seenHolder;
    private static EFT.InventoryLogic.Item?[] _seenContents = new EFT.InventoryLogic.Item?[0];
    private static bool _seenAny, _wasUnpowered, _off;
    internal static bool Unpowered(EFT.InventoryLogic.Item? goggle)
    {
        EFT.InventoryLogic.Item? holder = goggle?.CurrentAddress?.Container?.ParentItem;
        bool changed = !_seenAny || !ReferenceEquals(goggle, _seenGoggle) || !ReferenceEquals(holder, _seenHolder);
        var slots = (holder as EFT.InventoryLogic.CompoundItem)?.Slots;
        if (!changed && slots != null)
        {
            int k = 0;
            foreach (EFT.InventoryLogic.Slot slot in slots)
            {
                if (k >= _seenContents.Length || !ReferenceEquals(slot?.ContainedItem, _seenContents[k])) { changed = true; break; }
                k++;
            }
            if (k != _seenContents.Length)
                changed = true;
        }
        if (changed)
        {
            _seenAny = true;
            _seenGoggle = goggle;
            _seenHolder = holder;
            var contents = new List<EFT.InventoryLogic.Item?>();
            if (slots != null)
                foreach (EFT.InventoryLogic.Slot slot in slots)
                    contents.Add(slot?.ContainedItem);
            _seenContents = contents.ToArray();
            _off = !PoweredAt(goggle);
        }
        if (_off != _wasUnpowered || changed && !_loggedPower)
        {
            _loggedPower = true;
            _wasUnpowered = _off;
            EnvgFusionPlugin.Log?.LogInfo(_off ? "F-PANO: no RAILINK on it or its helmet: the goggle goes down dark" : "F-PANO: on the RAILINK: powered");
        }
        return _off;
    }
    private static bool _loggedPower;
}
