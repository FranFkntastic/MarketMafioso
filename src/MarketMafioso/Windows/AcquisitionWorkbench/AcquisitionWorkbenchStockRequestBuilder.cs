using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.AcquisitionWorkbench;

public static class AcquisitionWorkbenchStockRequestBuilder
{
    public static AcquisitionWorkbenchStockCheckContext BuildCheckContext(
        MarketAcquisitionQuickShopDraft draft,
        MarketAcquisitionQuickShopLineDraft line,
        string currentWorld)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(line);

        var snapshotKey = BuildSnapshotKey(draft, line, currentWorld);
        var analyzeRequest = BuildAnalyzeRequest(draft, line, currentWorld);
        return new AcquisitionWorkbenchStockCheckContext(
            Region: MarketAcquisitionWorldCatalog.NormalizeRegion(draft.Region),
            ItemId: line.ItemId,
            ItemName: line.ItemName,
            SnapshotKey: snapshotKey,
            AnalyzeRequest: analyzeRequest,
            StateKey: BuildStateKey(snapshotKey, line));
    }

    public static ObservedMarketSnapshotKey BuildSnapshotKey(
        MarketAcquisitionQuickShopDraft draft,
        MarketAcquisitionQuickShopLineDraft line,
        string currentWorld)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(line);

        return new ObservedMarketSnapshotKey(
            line.ItemId,
            BuildQueryTarget(draft, currentWorld),
            "Universalis");
    }

    public static StockAvailabilityRequest BuildAnalyzeRequest(
        MarketAcquisitionQuickShopDraft draft,
        MarketAcquisitionQuickShopLineDraft line,
        string currentWorld)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(line);

        var quantityMode = string.IsNullOrWhiteSpace(line.QuantityMode)
            ? "AllBelowThreshold"
            : line.QuantityMode.Trim();
        return new StockAvailabilityRequest(
            LineId: BuildLineId(line),
            ItemId: line.ItemId,
            QuantityMode: quantityMode,
            HqPolicy: line.HqPolicy,
            MaxUnitPrice: line.MaxUnitPrice,
            DesiredQuantity: quantityMode == "TargetQuantity" ? line.TargetQuantity : null,
            PurchaseCap: quantityMode == "AllBelowThreshold" && line.MaxQuantity > 0 ? line.MaxQuantity : null,
            RouteWorlds: BuildRouteWorlds(draft, currentWorld));
    }

    public static IReadOnlySet<string> BuildRouteWorlds(
        MarketAcquisitionQuickShopDraft draft,
        string currentWorld)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (!draft.WorldMode.Equals("AllWorldSweep", StringComparison.OrdinalIgnoreCase))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sweepScope = string.IsNullOrWhiteSpace(draft.SweepScope)
            ? "Region"
            : draft.SweepScope.Trim();

        if (sweepScope.Equals("DataCenters", StringComparison.OrdinalIgnoreCase))
        {
            return MarketAcquisitionWorldCatalog.ResolveWorldsForDataCenters(
                    draft.Region,
                    draft.SweepDataCenters)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (sweepScope.Equals("CurrentDataCenter", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(currentWorld))
                throw new InvalidOperationException("Current world is required for current data-center stock checks.");

            return MarketAcquisitionWorldCatalog.ResolveWorldsForDataCenters(
                    draft.Region,
                    [MarketAcquisitionWorldCatalog.ResolveDataCenter(currentWorld)])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (!sweepScope.Equals("Region", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Sweep scope must be Region, CurrentDataCenter, or DataCenters.");

        return MarketAcquisitionWorldCatalog.ResolveDataCenters(draft.Region)
            .SelectMany(entry => entry.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildQueryTarget(
        MarketAcquisitionQuickShopDraft draft,
        string currentWorld)
    {
        var region = MarketAcquisitionWorldCatalog.NormalizeRegion(draft.Region);
        if (!draft.WorldMode.Equals("AllWorldSweep", StringComparison.OrdinalIgnoreCase))
            return $"{region}|Recommended|Region|";

        var sweepScope = string.IsNullOrWhiteSpace(draft.SweepScope)
            ? "Region"
            : draft.SweepScope.Trim();
        var dataCenters = sweepScope switch
        {
            var scope when scope.Equals("DataCenters", StringComparison.OrdinalIgnoreCase) => string.Join(",", draft.SweepDataCenters
                .Where(dataCenter => !string.IsNullOrWhiteSpace(dataCenter))
                .Select(dataCenter => MarketAcquisitionWorldCatalog.NormalizeDataCenterName(region, dataCenter))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(dataCenter => dataCenter, StringComparer.OrdinalIgnoreCase)),
            var scope when scope.Equals("CurrentDataCenter", StringComparison.OrdinalIgnoreCase) &&
                           !string.IsNullOrWhiteSpace(currentWorld) =>
                MarketAcquisitionWorldCatalog.ResolveDataCenter(currentWorld),
            _ => string.Empty,
        };

        return $"{region}|AllWorldSweep|{sweepScope}|{dataCenters}";
    }

    private static string BuildLineId(MarketAcquisitionQuickShopLineDraft line) =>
        $"{line.ItemId}:{line.QuantityMode}:{line.HqPolicy}:{line.MaxUnitPrice}";

    private static string BuildStateKey(
        ObservedMarketSnapshotKey snapshotKey,
        MarketAcquisitionQuickShopLineDraft line) =>
        $"{snapshotKey.ItemId}|{snapshotKey.QueryTarget}|{line.QuantityMode}|{line.HqPolicy}|{line.TargetQuantity}|{line.MaxQuantity}|{line.MaxUnitPrice}";
}

public sealed record AcquisitionWorkbenchStockCheckContext(
    string Region,
    uint ItemId,
    string ItemName,
    ObservedMarketSnapshotKey SnapshotKey,
    StockAvailabilityRequest AnalyzeRequest,
    string StateKey);
