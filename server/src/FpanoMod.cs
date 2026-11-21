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
    public SemanticVersioning.Version Version { get; init; } = new("0.1.8");  // one version for the whole K-FPANO mod (client and server); bump on every change
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.6");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "All rights reserved";
}

// The F-PANO items (one per colour, db/items.json) are the GPNVG-18's template with their own bundle and white
// phosphor, and go everywhere the GPNVG-18 goes (helmet and mount slots, conflicts). The GPNVG-18's own panoramic
// mask is kept: the F-PANO is the same four-tube, 97 degree goggle with a thermal channel added.
// Runs after the database and other content mods, so modded helmets and mounts that take the GPNVG-18 take these too.
[Injectable(InjectionType.Transient, TypePriority = OnLoadOrder.Preload + 50000)]
public class FpanoMod(
    ISptLogger<FpanoMod> logger, ModHelper modHelper, CustomItemService customItemService,
    TemplateTable templateTable, TradersTable tradersTable
) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        string modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var options = JsonUtil.JsonSerializerOptionsNoIndent ?? new JsonSerializerOptions();
        var db = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(modPath, "db", "items.json")))!.AsObject();
        var clone = new MongoId(db["clone"]!.GetValue<string>());
        if (!templateTable.Items.TryGetValue(clone, out var source))
        {
            logger.Error("F-PANO not loaded: the GPNVG-18 template it is cloned from is missing");
            return Task.CompletedTask;
        }
        string sourceJson = JsonSerializer.Serialize(source, options);
        var added = new List<MongoId>();
        foreach (var entry in db["items"]!.AsArray())
        {
            var e = entry!.AsObject();
            string bundle = e["bundle"]!.GetValue<string>();
            if (!File.Exists(System.IO.Path.Combine(modPath, "bundles", bundle)))
            {
                logger.Error($"F-PANO {e["name"]} not loaded: bundles/{bundle} is missing from {modPath}. Reinstall the mod.");
                continue;
            }
            var node = JsonNode.Parse(sourceJson)!.AsObject();
            node["_id"] = e["id"]!.GetValue<string>();
            node["_name"] = e["name"]!.GetValue<string>();
            var props = node["_props"]!.AsObject();
            props["Prefab"] = new JsonObject { ["path"] = bundle, ["rcid"] = "" };
            // White phosphor, the same blue-white as the K-ENVG-B goggle (measured from the look the user picked).
            props["Color"] = new JsonObject { ["r"] = 156, ["g"] = 217, ["b"] = 255, ["a"] = 254 };
            props["Intensity"] = 1.4;
            if (Create(node, e, options, db["handbookParentId"]!.GetValue<string>(), db["price"]!.GetValue<double>()) is not MongoId made) continue;
            AddListing(made, new MongoId(e["assortId"]!.GetValue<string>()), db["listing"]!.AsObject());
            added.Add(made);
        }
        int slots = GoWhereItGoes(clone, added);
        if (added.Count > 0) logger.Success($"F-PANO loaded: {added.Count} colours, fits {slots} slots");
        return Task.CompletedTask;
    }

    // Every slot that takes the GPNVG-18 takes the F-PANO; every item that conflicts with it conflicts with these.
    int GoWhereItGoes(MongoId clone, List<MongoId> added)
    {
        int slots = 0;
        foreach (var item in templateTable.Items.Values)
        {
            if (added.Contains(item.Id) || item.Properties is null) continue;
            foreach (var slot in item.Properties.Slots ?? [])
                foreach (var f in slot.Properties?.Filters ?? [])
                    if (f.Filter is not null && f.Filter.Contains(clone))
                    {
                        foreach (var id in added) f.Filter.Add(id);
                        slots++;
                    }
            if (item.Properties.ConflictingItems is { } conflicts && conflicts.Contains(clone))
                foreach (var id in added) conflicts.Add(id);
        }
        return slots;
    }

    MongoId? Create(JsonObject node, JsonObject e, JsonSerializerOptions options, string handbookParent, double price)
    {
        var template = JsonSerializer.Deserialize<TemplateItem>(node, options)
            ?? throw new InvalidOperationException($"{e["name"]}: template did not deserialize");
        var locales = new Dictionary<string, LocaleDetails>();
        foreach (var pair in e["locales"]!.AsObject())
            locales[pair.Key] = new LocaleDetails
            {
                Name = pair.Value!["name"]!.GetValue<string>(),
                ShortName = pair.Value!["shortName"]!.GetValue<string>(),
                Description = pair.Value!["description"]!.GetValue<string>()
            };
        var result = customItemService.CreateItem(new NewItemDetails
        {
            NewItem = template, AddToHandbook = true, HandbookParentId = handbookParent,
            HandbookPriceRoubles = price, FleaPriceRoubles = price, AddToFleaPriceDb = true, Locales = locales
        }, Assembly.GetExecutingAssembly());
        if (result.Success) return template.Id;
        logger.Error($"F-PANO {e["name"]} was not created: " + string.Join("; ", result.Errors ?? []));
        return null;
    }

    void AddListing(MongoId item, MongoId assortId, JsonObject listing)
    {
        if (!tradersTable.TryGetValue(new MongoId(listing["traderId"]!.GetValue<string>()), out var trader) || trader.Assort is null)
        {
            logger.Warning("F-PANO trader missing; item is only on the flea market");
            return;
        }
        if (trader.Assort.Items.Any(i => i.Id == assortId)) return;
        trader.Assort.Items.Add(new Item
        {
            Id = assortId,
            Template = item,
            ParentId = "hideout",
            SlotId = "hideout",
            Upd = new Upd { UnlimitedCount = true, StackObjectsCount = 999999, BuyRestrictionMax = listing["limit"]?.GetValue<int>() ?? 1, BuyRestrictionCurrent = 0 }
        });
        trader.Assort.BarterScheme[assortId] =
        [
            [new BarterScheme { Count = listing["price"]!.GetValue<double>(), Template = new MongoId(listing["currency"]!.GetValue<string>()) }]
        ];
        trader.Assort.LoyalLevelItems[assortId] = listing["loyalty"]!.GetValue<int>();
    }
}
