using System;
using System.Linq;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.CraftArchitectCompanion;

public static class CraftArchitectQuickShopDraftBuilder
{
    public static MarketAcquisitionQuickShopDraft Build(
        MarketAppraisalRequest request,
        CraftAppraisalQuote? craftQuote = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new MarketAcquisitionQuickShopDraft
        {
            Region = request.Region.Trim(),
            WorldMode = request.WorldMode.Trim(),
            SweepScope = string.IsNullOrWhiteSpace(request.SweepScope) ? "Region" : request.SweepScope.Trim(),
            SweepDataCenters = request.SweepDataCenters
                .Where(dataCenter => !string.IsNullOrWhiteSpace(dataCenter))
                .Select(dataCenter => dataCenter.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Lines =
            [
                new MarketAcquisitionQuickShopLineDraft
                {
                    ItemId = request.ItemId,
                    ItemName = request.ItemName.Trim(),
                    QuantityMode = "AllBelowThreshold",
                    TargetQuantity = 0,
                    MaxQuantity = request.Quantity,
                    HqPolicy = request.HqPolicy.Trim(),
                    MaxUnitPrice = request.BuyThresholdUnitPrice,
                    GilCap = request.GilCap,
                },
            ],
        };
    }
}
