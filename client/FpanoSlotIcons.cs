using System.Collections.Generic;
using System.IO;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using UnityEngine;

namespace EnvgFusion;

// The rails' battery slot ("mod_battery") and the helmets' new rails slot ("mod_equipment_railink") are slots the game
// has no picture for: ModSlotView.Show finds no "Slots/<name>" sprite and leaves the slot's back white. Our own
// silhouettes (item/ui/slot_<name>.png, built into this DLL) are put there instead, once Show has run. Made once, kept.
internal static class FpanoSlotIcons
{
    private static readonly string[] Names = { "mod_battery", "mod_equipment_railink" };
    private static readonly Dictionary<string, Sprite?> _sprites = new Dictionary<string, Sprite?>();

    internal static void Patch(Harmony harmony)
    {
        MethodInfo? show = AccessTools.Method(typeof(ModSlotView), nameof(ModSlotView.Show),
            new[] { typeof(Slot), typeof(ItemContext), typeof(ItemController), typeof(ItemUiContext) });
        if (show != null)
            harmony.Patch(show, postfix: new HarmonyMethod(typeof(FpanoSlotIcons), nameof(AfterShow)));
        else
            EnvgFusionPlugin.Log?.LogWarning("F-PANO: ModSlotView.Show not found; the battery and rails slots keep the game's blank picture");
    }

    private static void AfterShow(ModSlotView __instance, Slot slot)
    {
        if (slot == null || (slot.Name != Names[0] && slot.Name != Names[1]))
            return;
        Sprite? sprite = SpriteFor(slot.Name);
        if (sprite != null)
            __instance.SetSlotBackImage(sprite);
    }

    private static Sprite? SpriteFor(string name)
    {
        if (_sprites.TryGetValue(name, out Sprite? made))
            return made;
        Sprite? sprite = null;
        try
        {
            using Stream? stream = typeof(FpanoSlotIcons).Assembly.GetManifestResourceStream("slot_" + name + ".png");
            if (stream == null)
                EnvgFusionPlugin.Log?.LogWarning("F-PANO: the " + name + " slot picture is missing from the DLL");
            else
            {
                var bytes = new byte[stream.Length];
                stream.Read(bytes, 0, bytes.Length);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "Justachae F-PANO " + name + " slot", hideFlags = HideFlags.HideAndDontSave };
                if (!texture.LoadImage(bytes))
                    EnvgFusionPlugin.Log?.LogWarning("F-PANO: the " + name + " slot picture could not be read");
                else
                {
                    texture.wrapMode = TextureWrapMode.Clamp;
                    sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                    sprite.name = "Slots/" + name;
                    sprite.hideFlags = HideFlags.HideAndDontSave;
                    EnvgFusionPlugin.Log?.LogInfo("F-PANO: " + name + " slot picture made (" + texture.width + "x" + texture.height + ")");
                }
            }
        }
        catch (System.Exception e)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO: the " + name + " slot picture failed: " + e.Message);
        }
        _sprites[name] = sprite;
        return sprite;
    }
}
