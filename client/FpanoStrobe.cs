using EFT.CameraControl;
using UnityEngine;

namespace EnvgFusion;

// The CORE Survival HEL-STAR rail strobe on the RAILINK flashes as in the user's videos: every strobe together, from
// one clock, 100 ms on in each 1.03 s (at least one frame a flash, however slow the frame). During the flash the mode's
// own light is on (it lights the wall and the helmet) and a glow is drawn at the lens ("strobe_lens"): the white dome in
// the visible modes; in the IR modes the night-vision bloom with long star spikes, only while night vision is on (as the
// game's IkLight shows the IR light). Dim modes give a smaller, weaker flash. Lights, modes and the lens are found once,
// when the strobe's model is made; nothing is searched or allocated per frame.
internal sealed class FpanoStrobe : MonoBehaviour
{
    private const float Period = 1.03f, Flash = 0.10f;
    private const string StrobeA = "6a4f9a400000000000000061", StrobeB = "6a4f9a400000000000000062";

    // per mode (mode_001 IR dim, mode_002 IR bright, mode_003 visible dim, mode_004 visible bright): the glow
    private struct Look { public bool Ir; public Color Colour; public float Size; public Vector4 Shape; }
    private static readonly Look[] Looks =
    {
        new Look { Ir = true, Colour = new Color(1f, 1f, 1f) * 5f, Size = 0.15f, Shape = new Vector4(0.10f, 0.40f, 0.8f, 0.010f) },
        new Look { Ir = true, Colour = new Color(1f, 1f, 1f) * 14f, Size = 0.30f, Shape = new Vector4(0.10f, 0.45f, 1.2f, 0.010f) },
        new Look { Ir = false, Colour = new Color(1f, 1f, 1f) * 4f, Size = 0.02f, Shape = new Vector4(0.35f, 0.70f, 0f, 0f) },
        new Look { Ir = false, Colour = new Color(1f, 1f, 1f) * 10f, Size = 0.03f, Shape = new Vector4(0.35f, 0.70f, 0f, 0f) },
    };

    private static Shader? _shader;
    private static Material? _material;
    private static Mesh? _quad;
    private static bool _shaderTried;
    private static readonly int ColorId = Shader.PropertyToID("_Color"), SizeId = Shader.PropertyToID("_Size"), ShapeId = Shader.PropertyToID("_Shape");

    private Light[] _lights = new Light[0];
    private GameObject?[] _modes = new GameObject?[0];
    private MeshRenderer? _glow;
    private MaterialPropertyBlock? _block;
    private int _shownMode = -1;
    private int _lastCycle = -1;

    internal static bool IsStrobe(EFT.InventoryLogic.Item? item)
    {
        string id = item != null ? item.TemplateId.ToString() : "";
        return id == StrobeA || id == StrobeB;
    }

    // on the strobe's model (once; a pooled model keeps it)
    internal static void Attach(GameObject model)
    {
        if (model == null || model.GetComponent<FpanoStrobe>() != null)
            return;
        var strobe = model.AddComponent<FpanoStrobe>();
        var found = new System.Collections.Generic.List<Light>();
        foreach (Light light in model.GetComponentsInChildren<Light>(true))
            if (light.gameObject.name == "strobe_light")
                found.Add(light);
        strobe._lights = found.ToArray();
        strobe._modes = new GameObject?[Looks.Length];
        for (int m = 0; m < Looks.Length; m++)
            strobe._modes[m] = FindDeep(model.transform, "mode_00" + (m + 1))?.gameObject;
        Transform? lens = FindDeep(model.transform, "strobe_lens");
        if (lens != null && Glow() != null)
        {
            var glow = new GameObject("strobe_glow");
            glow.transform.SetParent(lens, false);
            glow.AddComponent<MeshFilter>().sharedMesh = _quad;
            strobe._glow = glow.AddComponent<MeshRenderer>();
            strobe._glow.sharedMaterial = _material;
            strobe._glow.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            strobe._glow.receiveShadows = false;
            strobe._glow.enabled = false;
            strobe._block = new MaterialPropertyBlock();
        }
        EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: strobe " + model.name + ": " + strobe._lights.Length + " light(s), " + (strobe._glow != null ? "a lens glow" : "no lens glow (no strobe_lens or no shader)"));
    }

    private static Transform? FindDeep(Transform root, string name)
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

    // the glow's material and quad, made once
    private static Material? Glow()
    {
        if (_material != null)
            return _material;
        if (_shaderTried)
            return null;
        _shaderTried = true;
        _shader = EnvgFusion.BundleShader("Justachae/FPANO/StrobeGlow");
        if (_shader == null || !_shader.isSupported)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: the strobe glow shader is missing or unsupported: the strobes flash their lights only");
            return null;
        }
        _material = new Material(_shader) { name = "Justachae F-PANO strobe glow", hideFlags = HideFlags.DontSave };
        _quad = new Mesh
        {
            name = "Justachae F-PANO strobe glow quad",
            vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) },
            triangles = new[] { 0, 1, 2, 0, 2, 3 },
            // (its bounds wide: the quad is laid out toward the camera in the shader)
            bounds = new Bounds(Vector3.zero, Vector3.one * 2f),
        };
        _quad.UploadMeshData(true);
        return _material;
    }

    private static bool NightVisionOn()
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
        if (_glow == null)
            return;
        int mode = -1;
        for (int m = 0; m < _modes.Length; m++)
            if (_modes[m] != null && _modes[m]!.activeInHierarchy)
            {
                mode = m;
                break;
            }
        bool show = on && mode >= 0 && (!Looks[mode].Ir || NightVisionOn());
        if (show && mode != _shownMode)
        {
            _shownMode = mode;
            Look look = Looks[mode];
            _block!.SetColor(ColorId, look.Colour);
            _block.SetFloat(SizeId, look.Size);
            _block.SetVector(ShapeId, look.Shape);
            _glow.SetPropertyBlock(_block);
        }
        if (_glow.enabled != show)
            _glow.enabled = show;
    }
}
