using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace EnvgFusion;

// BorkelRNVG (another author's mod, optional: nothing here needs it) draws the night vision when K-3PS-C's eyepieces
// do not. It knows each goggle by its item id (a folder of its own per goggle: mask, lens, tube settings) and falls back
// on an unknown one by its mask type, which gave our goggle a green tube and a round mask (the user's, 2026-11-26). Our
// goggle is put into its table, by reflection, as the goggle whose shape it shares (GPNVG-18's mask, lens and
// settings, as the user set them), with one exception: the tube's colour is ours, the P45 white phosphor the device's
// own picture has, fixed (its settings are bound to a file of our own that is never shown, so they cannot be changed in
// BorkelRNVG's F12 page either). Tried a few times until BorkelRNVG has loaded its goggles; once done, never again.
internal static class BrnvgLink
{
    private static readonly string[] OurIds = { "6a4f9a400000000000000001", "6a4f9a400000000000000002" };
    private const string SourceId = "5c0558060db834001b735271";   // GPNVG-18 in BorkelRNVG's own table
    private const string Category = "Justachae F-PANO (fixed)";
    // the device's P45 white phosphor, as its own picture has it (display R:G:B 0.61:0.85:1)
    private const float Red = 0.61f, Green = 0.85f, Blue = 1f;
    private static bool _done;
    // BorkelRNVG draws our goggle (joined): our own white phosphor is then not put on the game's night vision as well
    internal static bool Joined { get; private set; }
    private static float _nextTry;
    private static int _tries;

    internal static void Ensure()
    {
        if (_done || Time.unscaledTime < _nextTry)
            return;
        _nextTry = Time.unscaledTime + 2f;
        try
        {
            Type? helper = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == "BorkelRNVG")
                {
                    helper = assembly.GetType("BorkelRNVG.Helpers.AssetHelper", false);
                    break;
                }
            if (helper == null)
            {
                // (not installed, or its assembly not loaded yet: the plugins load before the first frame, so a few tries)
                if (++_tries > 5)
                    _done = true;
                return;
            }
            var table = helper.GetField("NvgData", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IDictionary;
            if (table == null || !table.Contains(SourceId))
                return;   // its goggles not loaded yet
            _done = true;
            object source = table[SourceId]!;
            Type dataType = source.GetType();
            object? sourceConfig = dataType.GetField("NightVisionConfig")?.GetValue(source);
            object? sourceValues = sourceConfig?.GetType().GetProperty("Values")?.GetValue(sourceConfig);
            object? values = sourceValues?.GetType().GetMethod("Clone", Type.EmptyTypes)?.Invoke(sourceValues, null);
            var layoutTable = helper.GetField("LensLayouts", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IDictionary;
            if (sourceConfig == null || values == null || layoutTable == null)
                throw new Exception("its table is not as expected");
            var layouts = new List<string>();
            foreach (object key in layoutTable.Keys)
                layouts.Add((string)key);
            SetColour(values);
            // (its settings bound to a file of our own, never saved and never shown: nothing can change them)
            var file = new ConfigFile(Path.Combine(BepInEx.Paths.ConfigPath, "justachae.fpano.brnvg.cfg"), false) { SaveOnConfigSet = false };
            ConstructorInfo? make = sourceConfig.GetType().GetConstructor(new[] { typeof(ConfigFile), typeof(string), values.GetType(), typeof(IEnumerable<string>) });
            object config = make!.Invoke(new object[] { file, Category, values, layouts });
            object? ourValues = config.GetType().GetProperty("Values")?.GetValue(config);
            if (ourValues != null)
                SetColour(ourValues);
            int added = 0;
            foreach (string id in OurIds)
            {
                if (table.Contains(id))
                    continue;
                object data = Activator.CreateInstance(dataType)!;
                foreach (string field in new[] { "NvgItemConfig", "MaskTexture", "LensTexture" })
                    dataType.GetField(field)?.SetValue(data, dataType.GetField(field)?.GetValue(source));
                dataType.GetField("NightVisionConfig")?.SetValue(data, config);
                table[id] = data;
                added++;
            }
            Joined = added > 0 || Joined;
            EnvgFusionPlugin.Log?.LogInfo("F-PANO with BorkelRNVG: " + added + " item(s) drawn as its GPNVG-18 (mask, lens, settings), the tube's colour fixed to our white phosphor");
        }
        catch (Exception e)
        {
            _done = true;
            EnvgFusionPlugin.Log?.LogWarning("F-PANO with BorkelRNVG: not joined, it draws the goggle by its own fallback: " + e.Message);
        }
    }

    private static void SetColour(object values)
    {
        Type t = values.GetType();
        t.GetProperty("PhosphorRed")?.SetValue(values, Red);
        t.GetProperty("PhosphorGreen")?.SetValue(values, Green);
        t.GetProperty("PhosphorBlue")?.SetValue(values, Blue);
    }
}
