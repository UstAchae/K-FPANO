using UnityEngine;

namespace EnvgFusion;

// The HEL-STAR 6 on the RAILINK flashes as the real one does: a short flash (60 ms) about once a second, in whichever
// mode the game's light controller has on (white, or IR through the game's IkLight, which rewrites the intensity each
// camera's pre-cull, so the flash switches Light.enabled). Its lights (on objects named "strobe_light") are found
// once, when the strobe's model is made; nothing is searched or allocated per frame.
internal sealed class FpanoStrobe : MonoBehaviour
{
    private const float Period = 1.0f, Flash = 0.06f;
    private const string StrobeA = "6a4f9a400000000000000061", StrobeB = "6a4f9a400000000000000062";
    private Light[] _lights = new Light[0];
    private float _offset;
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
        strobe._offset = Random.value * Period;   // strobes on one helmet not in step
        EnvgFusionPlugin.Log?.LogInfo("F-PANO RAILINK: strobe " + model.name + ": " + strobe._lights.Length + " light(s) flashing");
    }

    private void Update()
    {
        float t = Time.time + _offset;
        int cycle = Mathf.FloorToInt(t / Period);
        // (at least one frame lit each cycle, however slow the frame)
        bool on = t - cycle * Period < Flash || cycle != _lastCycle;
        _lastCycle = cycle;
        for (int i = 0; i < _lights.Length; i++)
        {
            Light light = _lights[i];
            if (light != null && light.enabled != on)
                light.enabled = on;
        }
    }
}
