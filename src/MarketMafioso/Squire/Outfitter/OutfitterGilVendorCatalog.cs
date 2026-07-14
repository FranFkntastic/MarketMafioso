using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MarketMafioso.Squire.Outfitter;

public sealed record OutfitterGilVendorOffer(
    uint ItemId,
    uint ShopId,
    uint VendorId,
    string VendorName,
    uint TerritoryId,
    string TerritoryName,
    uint UnitPriceGil)
{
    public string SourceLabel => $"{VendorName} · {TerritoryName}";
}

/// <summary>
/// Builds the conservative, travel-ready subset of normal gil-shop offers.
/// A GilShopItem row alone is not enough: recovery and event shops share that
/// sheet, so an offer is admitted only when it has no unlock requirements and
/// can be tied to a concrete NPC spawn in the world.
/// </summary>
public sealed class OutfitterGilVendorCatalog
{
    private readonly IDataManager dataManager;
    private IReadOnlyDictionary<uint, IReadOnlyList<OutfitterGilVendorOffer>>? offersByItemId;

    public OutfitterGilVendorCatalog(IDataManager dataManager)
    {
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
    }

    public IReadOnlyList<OutfitterGilVendorOffer> FindOffers(uint itemId)
    {
        offersByItemId ??= Build();
        return offersByItemId.TryGetValue(itemId, out var offers) ? offers : [];
    }

    private IReadOnlyDictionary<uint, IReadOnlyList<OutfitterGilVendorOffer>> Build()
    {
        var shops = dataManager.GetExcelSheet<GilShop>()
            ?? throw new InvalidOperationException("GilShop sheet unavailable.");
        var residents = dataManager.GetExcelSheet<ENpcResident>()
            ?? throw new InvalidOperationException("ENpcResident sheet unavailable.");
        var levels = dataManager.GetExcelSheet<Level>()
            ?? throw new InvalidOperationException("Level sheet unavailable.");

        var directVendorsByShop = dataManager.GetExcelSheet<ENpcBase>()
            .SelectMany(npc => EnumerateGilShopIds(npc)
                .Select(shopId => new { ShopId = shopId, VendorId = npc.RowId }))
            .GroupBy(value => value.ShopId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(value => value.VendorId).Distinct().ToArray());

        var spawnsByVendor = levels
            .Where(level => level.Type == 8 && level.Object.Is<ENpcBase>())
            .GroupBy(level => level.Object.RowId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var offers = new List<OutfitterGilVendorOffer>();
        foreach (var row in dataManager.GetSubrowExcelSheet<GilShopItem>().Flatten())
        {
            if (row.Item.RowId == 0 || row.QuestRequired.Any(quest => quest.RowId != 0) ||
                row.AchievementRequired.RowId != 0)
                continue;

            var shop = shops.GetRowOrDefault(row.RowId);
            if (shop is null || shop.Value.Quest.RowId != 0 || shop.Value.FestivalId != 0 ||
                !directVendorsByShop.TryGetValue(row.RowId, out var vendorIds))
                continue;

            var item = row.Item.Value;
            if (item.PriceMid == 0)
                continue;

            foreach (var vendorId in vendorIds)
            {
                if (!spawnsByVendor.TryGetValue(vendorId, out var spawns))
                    continue;

                var vendorName = residents.GetRowOrDefault(vendorId)?.Singular.ToString();
                if (string.IsNullOrWhiteSpace(vendorName))
                    vendorName = shop.Value.Name.ToString();
                if (string.IsNullOrWhiteSpace(vendorName))
                    vendorName = "Merchant";
                vendorName = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(vendorName);

                foreach (var spawn in spawns)
                {
                    var territory = spawn.Territory.Value;
                    var territoryName = territory.PlaceName.Value.Name.ToString();
                    if (string.IsNullOrWhiteSpace(territoryName))
                        territoryName = territory.PlaceNameZone.Value.Name.ToString();
                    if (string.IsNullOrWhiteSpace(territoryName))
                        continue;

                    offers.Add(new(
                        row.Item.RowId,
                        row.RowId,
                        vendorId,
                        vendorName,
                        spawn.Territory.RowId,
                        territoryName,
                        item.PriceMid));
                }
            }
        }

        return offers
            .GroupBy(offer => offer.ItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<OutfitterGilVendorOffer>)group
                    .OrderBy(offer => offer.UnitPriceGil)
                    .ThenBy(offer => offer.TerritoryName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(offer => offer.VendorName, StringComparer.OrdinalIgnoreCase)
                    .DistinctBy(offer => (offer.ShopId, offer.VendorId, offer.TerritoryId))
                    .ToArray());
    }

    private static IEnumerable<uint> EnumerateGilShopIds(ENpcBase npc)
    {
        foreach (var data in npc.ENpcData)
        {
            if (data.Is<GilShop>())
            {
                yield return data.RowId;
                continue;
            }

            if (data.Is<PreHandler>() && data.TryGetValue(out PreHandler preHandler) &&
                preHandler.Target.Is<GilShop>())
            {
                yield return preHandler.Target.RowId;
                continue;
            }

            if (data.Is<TopicSelect>() && data.TryGetValue(out TopicSelect topic))
            {
                foreach (var shop in topic.Shop.Where(shop => shop.Is<GilShop>()))
                    yield return shop.RowId;
            }
        }
    }
}
