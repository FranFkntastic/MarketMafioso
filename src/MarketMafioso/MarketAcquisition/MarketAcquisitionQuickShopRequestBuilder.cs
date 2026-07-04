using System;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionQuickShopRequestBuilder
{
    public const int DefaultExpiresInSeconds = 300;

    public static MarketAcquisitionBatchCreateRequest Build(
        MarketAcquisitionQuickShopDraft draft,
        string characterName,
        string world,
        string pluginInstanceId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(pluginInstanceId))
            throw new ArgumentException("Plugin instance id is required.", nameof(pluginInstanceId));

        var region = MarketAcquisitionWorldCatalog.NormalizeRegion(draft.Region);
        return new MarketAcquisitionBatchCreateRequest
        {
            SchemaVersion = 1,
            IdempotencyKey = BuildCreateIdempotencyKey(pluginInstanceId, draft),
            Origin = MarketAcquisitionOrigins.ClientQuickShop,
            CreatedByPluginInstanceId = pluginInstanceId,
            TargetCharacterName = characterName.Trim(),
            TargetWorld = world.Trim(),
            Region = region,
            WorldMode = draft.WorldMode.Trim(),
            SweepScope = string.IsNullOrWhiteSpace(draft.SweepScope) ? "Region" : draft.SweepScope.Trim(),
            SweepDataCenters = draft.SweepDataCenters
                .Where(dataCenter => !string.IsNullOrWhiteSpace(dataCenter))
                .Select(dataCenter => MarketAcquisitionWorldCatalog.NormalizeDataCenterName(region, dataCenter))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ExpiresInSeconds = DefaultExpiresInSeconds,
            Lines = draft.Lines.Select(ToCreateLine).ToList(),
        };
    }

    public static string BuildCreateIdempotencyKey(
        string pluginInstanceId,
        MarketAcquisitionQuickShopDraft draft)
    {
        if (string.IsNullOrWhiteSpace(pluginInstanceId))
            throw new ArgumentException("Plugin instance id is required.", nameof(pluginInstanceId));

        return $"{pluginInstanceId}:quick-shop:{draft.DraftId}:{draft.DraftRevision}";
    }

    public static string BuildAcceptIdempotencyKey(
        string pluginInstanceId,
        MarketAcquisitionQuickShopDraft draft)
    {
        if (string.IsNullOrWhiteSpace(pluginInstanceId))
            throw new ArgumentException("Plugin instance id is required.", nameof(pluginInstanceId));

        return $"{pluginInstanceId}:quick-shop:{draft.DraftId}:{draft.DraftRevision}:accept";
    }

    private static MarketAcquisitionBatchLineCreateRequest ToCreateLine(MarketAcquisitionQuickShopLineDraft line)
    {
        var mode = line.QuantityMode.Trim();
        return new MarketAcquisitionBatchLineCreateRequest
        {
            ItemId = line.ItemId,
            ItemName = string.IsNullOrWhiteSpace(line.ItemName) ? null : line.ItemName.Trim(),
            QuantityMode = mode,
            TargetQuantity = mode == "TargetQuantity" ? line.TargetQuantity : 0,
            MaxQuantity = mode == "AllBelowThreshold" ? line.MaxQuantity : 0,
            HqPolicy = MarketAcquisitionPolicy.NormalizeHqPolicy(line.HqPolicy),
            MaxUnitPrice = line.MaxUnitPrice,
            GilCap = line.GilCap,
        };
    }
}
