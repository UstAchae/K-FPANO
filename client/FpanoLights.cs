using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace EnvgFusion;

// The RAILINK's lights on the local player's head, each switched on its own (the user: Ctrl+H the strobes' effect,
// Ctrl+← / → the light on that side): the game's own head-light switching (LightComponent.GetLightState / SetLightState,
// the headlight packet, the hand animation), only for the lights wanted. H (the game's toggle) still turns all of them
// on and off. The game's mode switch (its SwitchHeadLight, Ctrl+H) passes the rails' front lights by; the arrows switch
// the light in the rails' mod_flashlight_000 (the wearer's left) or _001 (right).
internal static class FpanoLights
{
    private const string LeftSlot = "mod_flashlight_000", RightSlot = "mod_flashlight_001";
    private static readonly HarmonyLib.AccessTools.FieldRef<Player, IEnumerable<TacticalComboVisualController>> Controllers =
        HarmonyLib.AccessTools.FieldRefAccess<Player, IEnumerable<TacticalComboVisualController>>("_helmetLightControllers");
    private static readonly HarmonyLib.AccessTools.FieldRef<Player, bool> Animating =
        HarmonyLib.AccessTools.FieldRefAccess<Player, bool>("IsHeadLightsAnimationActive");

    internal static void Patch(HarmonyLib.Harmony harmony)
    {
        var switchLights = HarmonyLib.AccessTools.Method(typeof(Player), nameof(Player.SwitchHeadLights));
        if (switchLights == null)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: Player.SwitchHeadLights not found: Ctrl+H switches every head light");
            return;
        }
        harmony.Patch(switchLights, prefix: new HarmonyLib.HarmonyMethod(typeof(FpanoLights), nameof(BeforeSwitchHeadLights)));
    }

    // a light hanging in one of the rails' front light slots
    private static string FrontSide(TacticalComboVisualController controller)
    {
        Item? item = controller?.LightMod?.Item;
        var slot = item?.CurrentAddress?.Container;
        if (slot == null || !FpanoRailink.IsRails(slot.ParentItem))
            return "";
        return slot.ID == LeftSlot || slot.ID == RightSlot ? slot.ID : "";
    }

    // the game's mode switch (not its on / off toggle): the rails' front lights left as they are
    private static bool BeforeSwitchHeadLights(Player __instance, bool togglesActive, bool changesState)
    {
        try
        {
            if (togglesActive || !changesState || __instance == null || !__instance.IsYourPlayer)
                return true;
            IEnumerable<TacticalComboVisualController> all = Controllers(__instance);
            bool any = false;
            foreach (TacticalComboVisualController c in all)
                if (FrontSide(c).Length > 0) { any = true; break; }
            if (!any)
                return true;
            Switch(__instance, "");
            return false;
        }
        catch (System.Exception e)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: switching the head lights failed: " + e.Message);
            return true;
        }
    }

    // the mode of the lights wanted moved on, as the game's SwitchHeadLights does it: side "" = all but the rails' front
    // lights; LeftSlot / RightSlot = only the light in that slot
    private static void Switch(Player player, string side)
    {
        if (Animating(player) || !player.StateIsSuitableForHandInput)
            return;
        bool changed = false;
        foreach (TacticalComboVisualController c in Controllers(player))
        {
            if (c?.LightMod == null || FrontSide(c) != side)
                continue;
            c.LightMod.SetLightState(c.LightMod.GetLightState(false, true));
            changed = true;
        }
        if (!changed)
            return;
        player.SendHeadlightsPacket(false);
        player.SwitchHeadLightsAnimation();
    }

    // once a frame, from the plugin's Update: the two side keys
    internal static void Update()
    {
        var left = EnvgFusionPlugin.LeftLight;
        var right = EnvgFusionPlugin.RightLight;
        bool l = left != null && left.Value.IsDown(), r = right != null && right.Value.IsDown();
        if (!l && !r || Cursor.visible)
            return;
        try
        {
            if (!Singleton<GameWorld>.Instantiated)
                return;
            Player? player = Singleton<GameWorld>.Instance.MainPlayer;
            if (player == null)
                return;
            Switch(player, l ? LeftSlot : RightSlot);
        }
        catch (System.Exception e)
        {
            EnvgFusionPlugin.Log?.LogWarning("F-PANO RAILINK: switching a side light failed: " + e.Message);
        }
    }
}
