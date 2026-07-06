using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.AcquisitionWorkbench;

namespace MarketMafioso.Tests.Windows.AcquisitionWorkbench;

public sealed class AcquisitionWorkbenchStockRequestBuilderTests
{
    [Fact]
    public void BuildSnapshotKey_IncludesItemRegionAndSweepScope()
    {
        var key = AcquisitionWorkbenchStockRequestBuilder.BuildSnapshotKey(
            CreateDraft(worldMode: "AllWorldSweep", sweepScope: "DataCenters", sweepDataCenters: ["Aether", "Primal"]),
            CreateLine(),
            currentWorld: "Siren");

        Assert.Equal(2u, key.ItemId);
        Assert.Equal("North America|AllWorldSweep|DataCenters|Aether,Primal", key.QueryTarget);
        Assert.Equal("Universalis", key.Source);
    }

    [Fact]
    public void BuildSnapshotKey_ForCurrentDataCenter_IncludesResolvedDataCenter()
    {
        var key = AcquisitionWorkbenchStockRequestBuilder.BuildSnapshotKey(
            CreateDraft(worldMode: "AllWorldSweep", sweepScope: "CurrentDataCenter"),
            CreateLine(),
            currentWorld: "Leviathan");

        Assert.Equal("North America|AllWorldSweep|CurrentDataCenter|Primal", key.QueryTarget);
    }

    [Fact]
    public void BuildRouteWorlds_ForDataCenterSweep_ResolvesSelectedWorlds()
    {
        var worlds = AcquisitionWorkbenchStockRequestBuilder.BuildRouteWorlds(
            CreateDraft(worldMode: "AllWorldSweep", sweepScope: "DataCenters", sweepDataCenters: ["Aether"]),
            currentWorld: "Siren");

        Assert.Contains("Siren", worlds);
        Assert.DoesNotContain("Leviathan", worlds);
    }

    [Fact]
    public void BuildRouteWorlds_ForRecommendedMode_ReturnsEmptyScopeFilter()
    {
        var worlds = AcquisitionWorkbenchStockRequestBuilder.BuildRouteWorlds(
            CreateDraft(worldMode: "Recommended"),
            currentWorld: "Siren");

        Assert.Empty(worlds);
    }

    [Fact]
    public void BuildRouteWorlds_ForCurrentDataCenter_ResolvesWorldsFromCurrentWorld()
    {
        var worlds = AcquisitionWorkbenchStockRequestBuilder.BuildRouteWorlds(
            CreateDraft(worldMode: "AllWorldSweep", sweepScope: "CurrentDataCenter"),
            currentWorld: "Leviathan");

        Assert.Contains("Leviathan", worlds);
        Assert.Contains("Behemoth", worlds);
        Assert.DoesNotContain("Siren", worlds);
    }

    [Fact]
    public void BuildRouteWorlds_ForDataCenterSweepWithoutDataCenters_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcquisitionWorkbenchStockRequestBuilder.BuildRouteWorlds(
                CreateDraft(worldMode: "AllWorldSweep", sweepScope: "DataCenters", sweepDataCenters: []),
                currentWorld: "Siren"));

        Assert.Contains("At least one data center", ex.Message);
    }

    [Fact]
    public void BuildRouteWorlds_ForUnknownSweepScope_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcquisitionWorkbenchStockRequestBuilder.BuildRouteWorlds(
                CreateDraft(worldMode: "AllWorldSweep", sweepScope: "CurrentShard"),
                currentWorld: "Siren"));

        Assert.Contains("Sweep scope", ex.Message);
    }

    [Fact]
    public void BuildAnalyzeRequest_ForUncappedAllBelowThreshold_LeavesPurchaseCapEmpty()
    {
        var request = AcquisitionWorkbenchStockRequestBuilder.BuildAnalyzeRequest(
            CreateDraft(),
            CreateLine(quantityMode: "AllBelowThreshold", maxQuantity: 0),
            currentWorld: "Siren");

        Assert.Equal("AllBelowThreshold", request.QuantityMode);
        Assert.Null(request.DesiredQuantity);
        Assert.Null(request.PurchaseCap);
        Assert.Equal(100u, request.MaxUnitPrice);
    }

    [Fact]
    public void BuildAnalyzeRequest_ForTargetQuantity_UsesDesiredQuantity()
    {
        var request = AcquisitionWorkbenchStockRequestBuilder.BuildAnalyzeRequest(
            CreateDraft(),
            CreateLine(quantityMode: "TargetQuantity", targetQuantity: 7),
            currentWorld: "Siren");

        Assert.Equal("TargetQuantity", request.QuantityMode);
        Assert.Equal(7u, request.DesiredQuantity);
        Assert.Null(request.PurchaseCap);
    }

    [Fact]
    public void BuildCheckContext_CapturesDraftAndLineValuesBeforeAsyncFetch()
    {
        var draft = CreateDraft(worldMode: "AllWorldSweep", sweepScope: "DataCenters", sweepDataCenters: ["Aether"]);
        var line = CreateLine(quantityMode: "TargetQuantity", targetQuantity: 5);

        var context = AcquisitionWorkbenchStockRequestBuilder.BuildCheckContext(draft, line, currentWorld: "Siren");

        draft.SweepDataCenters.Clear();
        draft.SweepDataCenters.Add("Primal");
        draft.Lines.Clear();

        Assert.Equal("North America", context.Region);
        Assert.Equal(2u, context.ItemId);
        Assert.Equal("Fire Shard", context.ItemName);
        Assert.Equal("North America|AllWorldSweep|DataCenters|Aether", context.SnapshotKey.QueryTarget);
        Assert.Equal(5u, context.AnalyzeRequest.DesiredQuantity);
        Assert.Equal(100u, context.AnalyzeRequest.MaxUnitPrice);
        var routeWorlds = Assert.IsAssignableFrom<IReadOnlySet<string>>(context.AnalyzeRequest.RouteWorlds);
        Assert.Contains("Siren", routeWorlds);
        Assert.DoesNotContain("Ragnarok", routeWorlds);
    }

    private static MarketAcquisitionQuickShopDraft CreateDraft(
        string worldMode = "AllWorldSweep",
        string sweepScope = "Region",
        IReadOnlyList<string>? sweepDataCenters = null) =>
        new()
        {
            Region = "North America",
            WorldMode = worldMode,
            SweepScope = sweepScope,
            SweepDataCenters = sweepDataCenters?.ToList() ?? [],
        };

    private static MarketAcquisitionQuickShopLineDraft CreateLine(
        string quantityMode = "TargetQuantity",
        uint targetQuantity = 5,
        uint maxQuantity = 0) =>
        new()
        {
            ItemId = 2,
            ItemName = "Fire Shard",
            QuantityMode = quantityMode,
            TargetQuantity = targetQuantity,
            MaxQuantity = maxQuantity,
            HqPolicy = "Either",
            MaxUnitPrice = 100,
        };
}
