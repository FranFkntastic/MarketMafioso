using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Squire.Outfitter;

public sealed record OutfitterMarketStagingResult(
    IReadOnlyList<MarketAcquisitionRequestLineDocument> Lines,
    bool WasClamped);

public static class OutfitterMarketStaging
{
    public static OutfitterMarketStagingResult Build(IReadOnlyList<EquipmentLoadoutPlanEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var wasClamped = false;
        var lines = entries
            .Where(entry => entry.Recommended is not null)
            .GroupBy(entry => entry.Recommended!.Definition.ItemId)
            .Select(group =>
            {
                var offer = group.First().Recommended!;
                var unitClamped = false;
                var unitCeiling = offer.UnitPriceGil is { } quoted
                    ? AddTenPercentCeiling(quoted, out unitClamped)
                    : 0;
                var quantity = (uint)group.Count();
                var gilCap = MultiplySaturating(unitCeiling, quantity, out var totalClamped);
                wasClamped |= unitClamped || totalClamped;
                return new MarketAcquisitionRequestLineDocument
                {
                    ItemId = offer.Definition.ItemId,
                    ItemName = offer.Definition.Name,
                    ItemKind = "Equipment",
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = quantity,
                    MaxQuantity = quantity,
                    HqPolicy = "Either",
                    MaxUnitPrice = unitCeiling,
                    GilCap = gilCap,
                };
            })
            .ToArray();
        return new(lines, wasClamped);
    }

    public static uint AddTenPercentCeiling(uint quoted, out bool wasClamped)
    {
        var buffered = decimal.Ceiling(quoted * 1.10m);
        wasClamped = buffered > uint.MaxValue;
        return wasClamped ? uint.MaxValue : (uint)buffered;
    }

    public static uint MultiplySaturating(uint left, uint right, out bool wasClamped)
    {
        var product = (ulong)left * right;
        wasClamped = product > uint.MaxValue;
        return wasClamped ? uint.MaxValue : (uint)product;
    }
}
