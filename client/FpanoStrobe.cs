using EFT.CameraControl;
using UnityEngine;

namespace EnvgFusion;

// A glow at a lens (shader Justachae/FPANO/StrobeGlow), drawn with Graphics.DrawMesh on the frames it is to show and
// on no other: no renderer of ours sits in the player's model, where the game (or another mod) switching the model's
// renderers on would leave it lit. The material and quad are made once; each light keeps its own property block.
internal static class FpanoGlow
{
    internal struct Look { public bool Ir; public Color Colour; public float Size; public Vector4 Shape; public float Forward; }

    private static Material? _material;
    private static Mesh? _quad;
    private static bool _tried;
    private static readonly int ColorId = Shader.PropertyToID("_Color"), SizeId = Shader.PropertyToID("_Size"), ShapeId = Shader.PropertyToID("_Shape"), DirId = Shader.PropertyToID("_Dir");

    internal static bool Ready()
    {
        if (_material != null)
            return true;
        if (_tried)
            return false;
        _tried = true;
        Shader? shader = EnvgFusion.BundleShader("Justachae/FPANO/StrobeGlow");
        if (shader == null || !shader.isSupported)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: the lens glow shader is missing or unsupported: the lights shine without a glow");
            return false;
        }
        _material = new Material(shader) { name = "Justachae F-PANO lens glow", hideFlags = HideFlags.DontSave };
        _quad = new Mesh
        {
            name = "Justachae F-PANO lens glow quad",
            vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) },
            triangles = new[] { 0, 1, 2, 0, 2, 3 },
            // (wide bounds: the quad is laid out toward the camera in the shader, its size grows with the distance)
            bounds = new Bounds(Vector3.zero, Vector3.one * 4f),
        };
        _quad.UploadMeshData(true);
        return true;
    }

    internal static void Set(MaterialPropertyBlock block, Look look)
    {
        block.SetColor(ColorId, look.Colour);
        block.SetFloat(SizeId, look.Size);
        block.SetVector(ShapeId, look.Shape);
        block.SetVector(DirId, new Vector4(look.Forward, 0f, 0f, 0f));
    }

    internal static void Draw(Transform lens, MaterialPropertyBlock block)
    {
        if (_material != null && _quad != null)
            Graphics.DrawMesh(_quad, lens.localToWorldMatrix, _material, lens.gameObject.layer, null, 0, block, false, false, false);
    }

    // the game's night vision on (as IkLight shows the IR lights)
    internal static bool NightVisionOn()
    {
        try
        {
            CameraManager? manager = CameraManager.Instance;
            return manager != null && manager.NightVision != null && manager.NightVision.On;
        }
        catch (System.Exception)
        {
            return false;
        }
    }

    internal static Transform? FindDeep(Transform root, string name)
    {
        if (root.name == name)
            return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform? found = FindDeep(root.GetChild(i), name);
            if (found != null)
                return found;
        }
        return null;
    }

    // the light's modes (mode_001 ... mode_00N), found once
    internal static GameObject?[] Modes(Transform root, int count)
    {
        var modes = new GameObject?[count];
        for (int m = 0; m < count; m++)
            modes[m] = FindDeep(root, "mode_00" + (m + 1))?.gameObject;
        return modes;
    }

    internal static int ActiveMode(GameObject?[] modes)
    {
        for (int m = 0; m < modes.Length; m++)
            if (modes[m] != null && modes[m]!.activeInHierarchy)
                return m;
        return -1;
    }
}

// The CORE Survival HEL-STAR rail strobe on the RAILINK flashes as in the user's videos: every strobe together, from
// one clock, 100 ms on in each 1.03 s (at least one frame a flash, however slow the frame). During the flash, and only
// then, the clear dome ("strobe_dome") is lit from inside in the
// visible modes; in the IR modes the night-vision bloom with long star spikes, only while night vision is on. Dim modes
// give a smaller, weaker flash. Found once, when the strobe's model is made; nothing searched or allocated per frame.
internal sealed class FpanoStrobe : MonoBehaviour
{
    private const float Period = 1.03f, Flash = 0.10f;
    private const string StrobeA = "6a4f9a400000000000000061", StrobeB = "6a4f9a400000000000000062";

    // mode_001 IR dim, mode_002 IR bright, mode_003 visible dim, mode_004 visible bright
    private static readonly FpanoGlow.Look[] Looks =
    {
        new FpanoGlow.Look { Ir = true, Colour = Color.white * 5f, Size = 0.15f, Shape = new Vector4(0.10f, 0.40f, 0.8f, 0.010f) },
        new FpanoGlow.Look { Ir = true, Colour = Color.white * 14f, Size = 0.30f, Shape = new Vector4(0.10f, 0.45f, 1.2f, 0.010f) },
        // (the visible modes: no glow of this kind, the dome itself lit, below)
        new FpanoGlow.Look { Ir = false },
        new FpanoGlow.Look { Ir = false },
    };
    // the visible modes: the clear dome lit from inside, warm white (the user's daylight video), dim and bright
    private static readonly Color[] DomeLight = { Color.clear, Color.clear, new Color(1f, 0.93f, 0.75f) * 0.25f, new Color(1f, 0.93f, 0.75f) * 0.6f };
    private static Material? _domeMaterial;
    private static bool _domeTried;
    private static readonly int DomeColorId = Shader.PropertyToID("_Color");

    private Light[] _lights = new Light[0];
    private GameObject?[] _modes = new GameObject?[0];
    private EFT.InventoryLogic.LightComponent? _state;   // the strobe's own on / mode (kept by the item wherever it lies)
    private Transform? _lens;
    private MaterialPropertyBlock? _block;
    private int _blockMode = -1;
    private int _lastCycle = -1;
    private Transform? _dome;
    private Mesh? _domeMesh;
    private MaterialPropertyBlock? _domeBlock;
    private int _domeMode = -1;

    private static Material? DomeMaterial()
    {
        if (_domeMaterial != null || _domeTried)
            return _domeMaterial;
        _domeTried = true;
        Shader? shader = EnvgFusion.BundleShader("Justachae/FPANO/DomeLit");
        if (shader == null || !shader.isSupported)
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: the dome shader is missing or unsupported: the visible strobe flashes nothing");
        else
            _domeMaterial = new Material(shader) { name = "Justachae F-PANO strobe dome lit", hideFlags = HideFlags.DontSave };
        return _domeMaterial;
    }

    internal static bool IsStrobe(EFT.InventoryLogic.Item? item)
    {
        string id = item != null ? item.TemplateId.ToString() : "";
        return id == StrobeA || id == StrobeB;
    }

    // on the strobe's model (once; a pooled model keeps it, told each time which item it now shows)
    internal static void Attach(GameObject model, EFT.InventoryLogic.Item item)
    {
        if (model == null)
            return;
        FpanoStrobe? had = model.GetComponent<FpanoStrobe>();
        if (had != null)
        {
            had._state = item.GetItemComponent<EFT.InventoryLogic.LightComponent>();
            return;
        }
        var strobe = model.AddComponent<FpanoStrobe>();
        strobe._state = item.GetItemComponent<EFT.InventoryLogic.LightComponent>();
        var found = new System.Collections.Generic.List<Light>();
        foreach (Light light in model.GetComponentsInChildren<Light>(true))
            if (light.gameObject.name == "strobe_light")
                found.Add(light);
        strobe._lights = found.ToArray();
        strobe._modes = FpanoGlow.Modes(model.transform, Looks.Length);
        strobe._lens = FpanoGlow.FindDeep(model.transform, "strobe_lens");
        if (strobe._lens != null && FpanoGlow.Ready())
            strobe._block = new MaterialPropertyBlock();
        strobe._dome = FpanoGlow.FindDeep(model.transform, "strobe_dome");
        strobe._domeMesh = strobe._dome != null ? strobe._dome.GetComponent<MeshFilter>()?.sharedMesh : null;
        if (strobe._domeMesh != null && DomeMaterial() != null)
            strobe._domeBlock = new MaterialPropertyBlock();
        EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: strobe " + model.name + ": " + strobe._lights.Length + " light(s), " + (strobe._block != null ? "an IR lens glow" : "no IR lens glow")
            + ", " + (strobe._domeBlock != null ? "the dome lit (" + strobe._domeMesh!.name + ")" : "no dome (no strobe_dome mesh or no shader)"));
    }

    private void Update()
    {
        float t = Time.time;
        int cycle = Mathf.FloorToInt(t / Period);
        bool on = t - cycle * Period < Flash || cycle != _lastCycle;
        _lastCycle = cycle;
        for (int i = 0; i < _lights.Length; i++)
        {
            Light light = _lights[i];
            if (light != null && light.enabled != on)
                light.enabled = on;
        }
        if (!on)
            return;
        // the item's own state (on the head the game shows it on the model too; dropped, the model is left as it was,
        // but a strobe left on keeps flashing)
        int mode = _state != null ? (_state.IsActive ? Mathf.Clamp(_state.SelectedMode, 0, Looks.Length - 1) : -1) : FpanoGlow.ActiveMode(_modes);
        if (mode < 0)
            return;
        if (!Looks[mode].Ir)
        {
            // visible: the dome itself lit for the flash
            if (_domeBlock == null || _dome == null || _domeMesh == null || _domeMaterial == null)
                return;
            if (mode != _domeMode)
            {
                _domeMode = mode;
                _domeBlock.SetColor(DomeColorId, DomeLight[mode]);
            }
            int layer = _dome.gameObject.layer;
            Matrix4x4 at = _dome.localToWorldMatrix;
            for (int sub = 0; sub < _domeMesh.subMeshCount; sub++)
                Graphics.DrawMesh(_domeMesh, at, _domeMaterial, layer, null, sub, _domeBlock, false, false, false);
            return;
        }
        if (_block == null || _lens == null || !FpanoGlow.NightVisionOn())
            return;
        if (mode != _blockMode)
        {
            _blockMode = mode;
            FpanoGlow.Set(_block, Looks[mode]);
        }
        FpanoGlow.Draw(_lens, _block);
    }
}

// The SureFire HL2-A and Princeton Tec MPLS on the RAILINK look lit while a mode is on: a steady glow at the lamp's
// face ("lamp_lens", +z the beam), about the lens's size, in the mode's colour, brightest looking into the beam. The
// IR modes only through night vision, and faint, as a real IR lamp seen through a tube.
internal sealed class FpanoLamp : MonoBehaviour
{
    private const string HlA = "6a4f9a400000000000000041", HlB = "6a4f9a400000000000000042", MpA = "6a4f9a400000000000000051", MpB = "6a4f9a400000000000000052";

    // HL2-A: mode_001 IR low, 002 IR high, 003 white low, 004 white high
    private static readonly FpanoGlow.Look[] Hl2a =
    {
        new FpanoGlow.Look { Ir = true, Colour = Color.white * 0.6f, Size = 0.012f, Shape = new Vector4(0.45f, 0.5f, 0f, 0f), Forward = 2f },
        new FpanoGlow.Look { Ir = true, Colour = Color.white * 1.5f, Size = 0.014f, Shape = new Vector4(0.45f, 0.5f, 0f, 0f), Forward = 2f },
        new FpanoGlow.Look { Ir = false, Colour = new Color(1f, 0.97f, 0.9f) * 1.0f, Size = 0.012f, Shape = new Vector4(0.5f, 0.5f, 0f, 0f), Forward = 2f },
        new FpanoGlow.Look { Ir = false, Colour = new Color(1f, 0.97f, 0.9f) * 2.0f, Size = 0.015f, Shape = new Vector4(0.5f, 0.5f, 0f, 0f), Forward = 2f },
    };
    // MPLS: mode_001 red, 002 white
    private static readonly FpanoGlow.Look[] Mpls =
    {
        new FpanoGlow.Look { Ir = false, Colour = new Color(1f, 0.06f, 0.03f) * 1.5f, Size = 0.007f, Shape = new Vector4(0.5f, 0.5f, 0f, 0f), Forward = 1.5f },
        new FpanoGlow.Look { Ir = false, Colour = new Color(1f, 0.97f, 0.92f) * 1.2f, Size = 0.007f, Shape = new Vector4(0.5f, 0.5f, 0f, 0f), Forward = 1.5f },
    };

    private FpanoGlow.Look[] _looks = Hl2a;
    private GameObject?[] _modes = new GameObject?[0];
    private Transform? _lens;
    private MaterialPropertyBlock? _block;
    private int _blockMode = -1;

    internal static bool IsLamp(EFT.InventoryLogic.Item? item)
    {
        string id = item != null ? item.TemplateId.ToString() : "";
        return id == HlA || id == HlB || id == MpA || id == MpB;
    }

    internal static void Attach(GameObject model, EFT.InventoryLogic.Item item)
    {
        if (model == null || model.GetComponent<FpanoLamp>() != null)
            return;
        string id = item.TemplateId.ToString();
        var lamp = model.AddComponent<FpanoLamp>();
        lamp._looks = id == MpA || id == MpB ? Mpls : Hl2a;
        lamp._modes = FpanoGlow.Modes(model.transform, lamp._looks.Length);
        lamp._lens = FpanoGlow.FindDeep(model.transform, "lamp_lens");
        if (lamp._lens != null && FpanoGlow.Ready())
            lamp._block = new MaterialPropertyBlock();
        EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: lamp " + model.name + ": " + (lamp._block != null ? "a lamp-face glow" : "no lamp-face glow (no lamp_lens or no shader)"));
    }

    private void Update()
    {
        if (_block == null || _lens == null)
            return;
        int mode = FpanoGlow.ActiveMode(_modes);
        if (mode < 0 || _looks[mode].Ir && !FpanoGlow.NightVisionOn())
            return;
        if (mode != _blockMode)
        {
            _blockMode = mode;
            FpanoGlow.Set(_block, _looks[mode]);
        }
        FpanoGlow.Draw(_lens, _block);
    }
}
