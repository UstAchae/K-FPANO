using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils;

namespace UstAchae.Fpano;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.ustachae.fpano";
    public string Name { get; init; } = "Justachae's F-PANO";
    public string Author { get; init; } = "UstAchae";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.2.23");  // one version for the whole K-FPANO mod (client and server); bump on every change
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.6");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "All rights reserved";
}

// Four items per colour (db/items.json), at the user's design:
//   rails   the Ops-Core RAILINK powered rails. They take the helmet's headset slot (the one that holds the Ops-Core RAC
//           or the EXFIL ComTac headsets) or, on NVG helmets without one, a new slot (newHelmetSlot). Their own slots:
//           mod_equipment (those helmet headsets), mod_nvg (the F-PANO), mod_mount_000 / 001 (the two Ops-Core ARC rail
//           adapters, so every helmet light). The Battery Pack+ is part of them since 0.2.15 (user: "把电池和 RALINK 做成一个
//           物品吧"). They conflict with every other NVG and NVG mount, so a helmet with them takes no other night vision;
//   battery the old separate Battery Pack+: still known (owned copies load), fits nowhere, not sold;
//   mount   the old separate F-PANO mount: the same;
//   goggle  the F-PANO with its own mount (a GPNVG-18 clone): in the rail helmets' own mod_nvg (no rails needed since
//           0.2.15, user: "取消 F-PANO 只能装在有 RALINK 的头盔上的限制，但还是只能装那 9 种头盔") and in the rails' mod_nvg.
//           Powered (picture, cable, the icon's green light) only with the rails on its helmet; the client does that.
[Injectable(InjectionType.Transient, TypePriority = OnLoadOrder.Preload + 50000)]
public class FpanoMod(
    ISptLogger<FpanoMod> logger, ModHelper modHelper, CustomItemService customItemService,
    TemplateTable templateTable, TradersTable tradersTable, LocaleTable localeTable
) : IOnLoad
{
    const string Head = "5a341c4086f77401f2541505", SlotProto = "55d30c4c4bdc2db4468b457e";
    int _slotCounter;
    string _slotPrefix = "";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        string modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var options = JsonUtil.JsonSerializerOptionsNoIndent ?? new JsonSerializerOptions();
        var db = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(modPath, "db", "items.json")))!.AsObject();
        _slotPrefix = db["slotIds"]!.GetValue<string>();
        var kinds = db["kinds"]!.AsObject(); var colours = db["colours"]!.AsObject();
        var gpnvg = new MongoId(db["gpnvg"]!.GetValue<string>());

        foreach (var (key, names) in db["slotLocales"]!.AsObject())
            foreach (var (lang, table) in localeTable.Global)
            {
                string label = names![lang]?.GetValue<string>() ?? names["en"]!.GetValue<string>();
                table.AddTransformer(d => { if (d is not null) d[key] = label; return d; });
            }

        var entries = db["items"]!.AsArray().Select(e => e!.AsObject()).ToList();
        var made = new Dictionary<string, List<MongoId>>();
        foreach (var e in entries)
        {
            string kind = e["kind"]!.GetValue<string>();
            if (!made.ContainsKey(kind)) made[kind] = [];
            made[kind].Add(new MongoId(e["id"]!.GetValue<string>()));
        }
        JsonArray Ids(IEnumerable<MongoId> ids) { var a = new JsonArray(); foreach (var i in ids) a.Add(i.ToString()); return a; }
        JsonArray Strs(JsonNode? n) { var a = new JsonArray(); foreach (var x in n!.AsArray()) a.Add(x!.GetValue<string>()); return a; }

        // Every NVG and NVG mount that some item's mod_nvg takes, ours left out: the rails conflict with them all.
        var ours = made.Values.SelectMany(v => v).ToHashSet();
        var otherNvgs = new HashSet<MongoId>();
        foreach (var it in templateTable.Items.Values)
            foreach (var s in it.Properties?.Slots ?? [])
                if (s.Name == "mod_nvg")
                    foreach (var f in s.Properties?.Filters ?? [])
                        foreach (var fid in f.Filter ?? []) if (!ours.Contains(fid)) otherNvgs.Add(fid);

        int created = 0;
        foreach (var e in entries)
        {
            string kind = e["kind"]!.GetValue<string>(), colour = e["colour"]!.GetValue<string>(), bundle = e["bundle"]!.GetValue<string>();
            var k = kinds[kind]!.AsObject(); var id = e["id"]!.GetValue<string>();
            if (!File.Exists(System.IO.Path.Combine(modPath, "bundles", bundle))) { logger.Error($"F-PANO {kind} {colour}: bundles/{bundle} is missing. Reinstall the mod."); continue; }
            if (!templateTable.Items.TryGetValue(new MongoId(k["clone"]!.GetValue<string>()), out var source)) { logger.Error($"F-PANO {kind}: clone template missing"); continue; }
            var node = JsonNode.Parse(JsonSerializer.Serialize(source, options))!.AsObject();
            node["_id"] = id; node["_name"] = $"fpano_{kind}_{colour}";
            var props = node["_props"]!.AsObject();
            props["Prefab"] = new JsonObject { ["path"] = bundle, ["rcid"] = "" };
            if (k["weight"] is { } w) props["Weight"] = w.GetValue<double>();
            if (k["width"] is { } wd) { props["Width"] = wd.GetValue<int>(); props["Height"] = k["height"]!.GetValue<int>(); }
            props["ConflictingItems"] = new JsonArray();
            var slots = new JsonArray();
            JsonObject Slot(string name, JsonArray filter) => new()
            {
                ["_id"] = NextSlotId(), ["_name"] = name, ["_parent"] = id,
                ["_props"] = new JsonObject { ["filters"] = new JsonArray(new JsonObject { ["Shift"] = 0, ["Filter"] = filter }) },
                ["_required"] = false, ["_mergeSlotWithChildren"] = false, ["_proto"] = SlotProto
            };
            switch (kind)
            {
                case "rails":
                    slots.Add(Slot("mod_equipment", Strs(db["railHeadsets"])));
                    slots.Add(Slot("mod_nvg", Ids(made["goggle"])));      // the F-PANO on the rails (user: "让 F-PANO 也能装在 RALINK 上面")
                    slots.Add(Slot("mod_mount_000", Strs(db["railAdapters"])));
                    slots.Add(Slot("mod_mount_001", Strs(db["railAdapters"])));
                    // the RAILINK lights (user: "做成 2"): two front light slots, two rear strobe slots
                    foreach (var (slotName, lightKinds) in db["railLights"]!.AsObject())
                        slots.Add(Slot(slotName, Ids(lightKinds!.AsArray().SelectMany(lk => made.GetValueOrDefault(lk!.GetValue<string>()) ?? []))));
                    props["ConflictingItems"] = Ids(otherNvgs);
                    break;
                case "hl2a": case "mpls": case "strobe":
                    props["ModesCount"] = k["modes"]!.GetValue<int>();   // mode_001..mode_00N in the prefab
                    break;
                case "mount":
                    slots.Add(Slot("mod_nvg", Ids(made["goggle"])));
                    break;
                case "goggle":
                    // White phosphor, the same blue-white as the K-ENVG-B goggle (measured from the look the user picked).
                    props["Color"] = new JsonObject { ["r"] = 156, ["g"] = 217, ["b"] = 255, ["a"] = 254 };
                    props["Intensity"] = 1.4;
                    break;
            }
            props["Slots"] = slots;
            var loc = new JsonObject();
            foreach (var (lang, v) in k["locales"]!.AsObject())
            {
                var a = v!.AsArray(); string c = colours[colour]![lang]?.GetValue<string>() ?? colours[colour]!["en"]!.GetValue<string>();
                loc[lang] = new JsonObject { ["name"] = $"{a[0]!.GetValue<string>()} ({c})", ["shortName"] = a[1]!.GetValue<string>(), ["description"] = a[2]!.GetValue<string>() };
            }
            if (Create(node, loc, options, k["handbook"]!.GetValue<string>(), k["price"]!.GetValue<double>(), id) is not MongoId newId) continue;
            if (kind != "mount" && kind != "battery") AddListing(newId, new MongoId(e["assort"]!.GetValue<string>()), db["trader"]!.GetValue<string>(), db["currency"]!.GetValue<string>(),
                       k["usd"]!.GetValue<double>(), k["loyalty"]!.GetValue<int>());
            created++;
        }

        // The goggle goes in the helmet's own mod_nvg, on the helmets that take the rails (user: "支架装在头盔夜视仪槽").
        // Since 0.2.10 the goggle carries its mount (user: "要不支架和 F-PANO 做成一个物品吧"): the goggle goes there; the
        // separate mount stays a known item (owned copies load) but fits nowhere and is not sold.
        var mountHelmets = db["railHelmets"]!.AsArray().Select(x => new MongoId(x!.GetValue<string>())).ToHashSet();
        foreach (var hid in mountHelmets)
            if (templateTable.Items.TryGetValue(hid, out var h))
                foreach (var s in h.Properties?.Slots ?? [])
                    if (s.Name == "mod_nvg") foreach (var f in s.Properties?.Filters ?? []) foreach (var m in made["goggle"]) f.Filter!.Add(m);

        // Conflicts both ways: every other NVG and mount conflicts with the rails.
        foreach (var nv in otherNvgs)
            if (templateTable.Items.TryGetValue(nv, out var t) && t.Properties is not null)
            {
                t.Properties.ConflictingItems ??= [];
                foreach (var r in made["rails"]) t.Properties.ConflictingItems.Add(r);
            }

        // The rails on the helmets whose own rails come off (railHelmets: the nine shapes whose rails are separate pieces;
        // the client hides them while the RAILINK is on, user: "那就只让它能够装在这 9 种头盔上面"): in the slot that holds
        // the helmet headsets, or a new slot on those without one.
        var railHelmets = db["railHelmets"]!.AsArray().Select(x => new MongoId(x!.GetValue<string>())).ToHashSet();
        var headsets = db["railHeadsets"]!.AsArray().Select(x => new MongoId(x!.GetValue<string>())).ToHashSet();
        string newSlot = db["newHelmetSlot"]!.GetValue<string>();
        int joined = 0, added = 0;
        foreach (var it in templateTable.Items.Values)
        {
            if (it.Parent != Head || it.Properties?.Slots is not { } hs || !railHelmets.Contains(it.Id)) continue;
            var headsetSlot = hs.FirstOrDefault(s => (s.Properties?.Filters ?? []).Any(f => f.Filter?.Any(headsets.Contains) == true));
            if (headsetSlot is not null)
            {
                foreach (var f in headsetSlot.Properties!.Filters!) foreach (var r in made["rails"]) f.Filter!.Add(r);
                joined++;
            }
            else
            {
                var s = JsonSerializer.Deserialize<Slot>(new JsonObject
                {
                    ["_id"] = NextSlotId(), ["_name"] = newSlot, ["_parent"] = it.Id.ToString(),
                    ["_props"] = new JsonObject { ["filters"] = new JsonArray(new JsonObject { ["Shift"] = 0, ["Filter"] = Ids(made["rails"]) }) },
                    ["_required"] = false, ["_mergeSlotWithChildren"] = false, ["_proto"] = SlotProto
                }, options)!;
                it.Properties.Slots = hs.Append(s).ToList();
                added++;
            }
        }
        logger.Success($"F-PANO loaded: {created} items; RAILINK rails in {joined} helmets' headset slots and {added} new helmet slots; {otherNvgs.Count} other NVGs and mounts conflict with them");
        return Task.CompletedTask;
    }

    string NextSlotId() => _slotPrefix + (_slotCounter++).ToString("x2");

    MongoId? Create(JsonObject node, JsonObject locs, JsonSerializerOptions options, string handbookParent, double price, string id)
    {
        var template = JsonSerializer.Deserialize<TemplateItem>(node, options) ?? throw new InvalidOperationException($"{id}: template did not deserialize");
        var locales = new Dictionary<string, LocaleDetails>();
        foreach (var (lang, v) in locs)
            locales[lang] = new LocaleDetails { Name = v!["name"]!.GetValue<string>(), ShortName = v["shortName"]!.GetValue<string>(), Description = v["description"]!.GetValue<string>() };
        var result = customItemService.CreateItem(new NewItemDetails
        {
            NewItem = template, AddToHandbook = true, HandbookParentId = handbookParent,
            HandbookPriceRoubles = price, FleaPriceRoubles = price, AddToFleaPriceDb = true, Locales = locales
        }, Assembly.GetExecutingAssembly());
        if (result.Success) return template.Id;
        logger.Error($"F-PANO {id} was not created: " + string.Join("; ", result.Errors ?? []));
        return null;
    }

    void AddListing(MongoId item, MongoId assortId, string traderId, string currency, double price, int loyalty)
    {
        if (!tradersTable.TryGetValue(new MongoId(traderId), out var trader) || trader.Assort is null) { logger.Warning("F-PANO trader missing; items are only on the flea market"); return; }
        if (trader.Assort.Items.Any(i => i.Id == assortId)) return;
        trader.Assort.Items.Add(new Item
        {
            Id = assortId, Template = item, ParentId = "hideout", SlotId = "hideout",
            Upd = new Upd { UnlimitedCount = true, StackObjectsCount = 999999, BuyRestrictionMax = 4, BuyRestrictionCurrent = 0 }
        });
        trader.Assort.BarterScheme[assortId] = [[new BarterScheme { Count = price, Template = new MongoId(currency) }]];
        trader.Assort.LoyalLevelItems[assortId] = loyalty;
    }
}
