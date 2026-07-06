using System;
using System.Linq;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.AcquisitionWorkbench;

public static class CraftAppraisalWorkbenchRequestBuilder
{
    public static MarketAppraisalRequest Build(
        MarketAcquisitionQuickShopDraft draft,
        MarketAcquisitionQuickShopLineDraft line)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(line);
        if (line.ItemId == 0)
            throw new InvalidOperationException("Selected line must have an item id before craft appraisal.");

        return new MarketAppraisalRequest
        {
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            Quantity = ResolveQuoteQuantity(line),
            HqPolicy = string.IsNullOrWhiteSpace(line.HqPolicy) ? "Either" : line.HqPolicy,
            BuyThresholdUnitPrice = line.MaxUnitPrice,
            GilCap = line.GilCap,
            Region = MarketAcquisitionWorldCatalog.NormalizeRegion(draft.Region),
            WorldMode = string.IsNullOrWhiteSpace(draft.WorldMode) ? "Recommended" : draft.WorldMode,
            SweepScope = string.IsNullOrWhiteSpace(draft.SweepScope) ? "Region" : draft.SweepScope,
            SweepDataCenters = draft.SweepDataCenters
                .Where(dataCenter => !string.IsNullOrWhiteSpace(dataCenter))
                .ToArray(),
        };
    }

    public static CraftAppraisalLineIdentity BuildLineIdentity(
        MarketAcquisitionQuickShopDraft draft,
        MarketAcquisitionQuickShopLineDraft line)
    {
        var request = Build(draft, line);
        return new CraftAppraisalLineIdentity(
            request.ItemId,
            request.ItemName,
            request.Quantity,
            request.HqPolicy,
            request.Region);
    }

    private static uint ResolveQuoteQuantity(MarketAcquisitionQuickShopLineDraft line)
    {
        if (line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase))
            return Math.Max(1, line.TargetQuantity);

        return line.MaxQuantity > 0 ? line.MaxQuantity : 1;
    }
}
