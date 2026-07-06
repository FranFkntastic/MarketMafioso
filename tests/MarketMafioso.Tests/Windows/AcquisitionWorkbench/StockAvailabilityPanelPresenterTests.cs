using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.AcquisitionWorkbench;

namespace MarketMafioso.Tests.Windows.AcquisitionWorkbench;

public sealed class StockAvailabilityPanelPresenterTests
{
    [Fact]
    public void Build_WhenNoLineSelected_AsksForLineSelection()
    {
        var view = StockAvailabilityPanelPresenter.Build(new StockAvailabilityPanelState());

        Assert.Equal("Select a line", view.Headline);
        Assert.Equal(StockAvailabilityPanelSeverity.Muted, view.Severity);
        Assert.Contains("queued line", view.Detail);
    }

    [Fact]
    public void Build_WhenUncappedAllBelowThresholdHasDepth_ReportsDepthNotSufficiency()
    {
        var view = StockAvailabilityPanelPresenter.Build(new StockAvailabilityPanelState
        {
            SelectedLine = CreateLine(quantityMode: "AllBelowThreshold", maxQuantity: 0),
            Result = new StockAvailabilityResult
            {
                Status = StockAvailabilityStatus.Depth,
                IsOpenEndedDepth = true,
                EligibleQuantity = 12,
                EligibleListingCount = 3,
            },
            Source = StockAvailabilityPanelSource.Cache,
            SnapshotFetchedAtUtc = DateTimeOffset.UnixEpoch,
            NowUtc = DateTimeOffset.UnixEpoch.AddMinutes(2),
        });

        Assert.Equal("12 under-threshold units observed", view.Headline);
        Assert.Equal(StockAvailabilityPanelSeverity.Success, view.Severity);
        Assert.Contains("Depth only", view.Detail);
        Assert.Contains("cache", view.SourceLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("enough", view.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("short", view.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_WhenTargetQuantityIsPartial_ReportsShortfall()
    {
        var view = StockAvailabilityPanelPresenter.Build(new StockAvailabilityPanelState
        {
            SelectedLine = CreateLine(quantityMode: "TargetQuantity", targetQuantity: 10),
            Result = new StockAvailabilityResult
            {
                Status = StockAvailabilityStatus.Partial,
                EligibleQuantity = 4,
                EligibleListingCount = 1,
                RequiredQuantity = 10,
                ShortfallQuantity = 6,
            },
            Source = StockAvailabilityPanelSource.FreshFetch,
            SnapshotFetchedAtUtc = DateTimeOffset.UnixEpoch,
            NowUtc = DateTimeOffset.UnixEpoch.AddSeconds(30),
        });

        Assert.Equal("4 of 10 units available", view.Headline);
        Assert.Equal(StockAvailabilityPanelSeverity.Warning, view.Severity);
        Assert.Contains("6 short", view.Detail);
        Assert.Contains("fresh", view.SourceLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_WhenFetchFailed_ReportsError()
    {
        var view = StockAvailabilityPanelPresenter.Build(new StockAvailabilityPanelState
        {
            SelectedLine = CreateLine(),
            ErrorMessage = "Universalis exploded.",
        });

        Assert.Equal("Stock check failed", view.Headline);
        Assert.Equal(StockAvailabilityPanelSeverity.Error, view.Severity);
        Assert.Contains("Universalis exploded.", view.Detail);
    }

    [Fact]
    public void BuildSideSummary_WhenLineHasStockResult_SummarizesSelectedLine()
    {
        var view = StockAvailabilityPanelPresenter.BuildSideSummary(new StockAvailabilityPanelState
        {
            SelectedLine = CreateLine(targetQuantity: 10),
            Result = new StockAvailabilityResult
            {
                Status = StockAvailabilityStatus.Enough,
                EligibleQuantity = 12,
                EligibleListingCount = 3,
                RequiredQuantity = 10,
            },
            Source = StockAvailabilityPanelSource.Cache,
            SnapshotFetchedAtUtc = DateTimeOffset.UnixEpoch,
            NowUtc = DateTimeOffset.UnixEpoch.AddMinutes(3),
        });

        Assert.Equal("Fire Shard", view.Title);
        Assert.Equal("12 of 10 units available", view.Headline);
        Assert.Equal("3 eligible listing(s) cover the requested quantity.", view.Detail);
        Assert.Contains("cache", view.Footer, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StockAvailabilityPanelSeverity.Success, view.Severity);
    }

    [Fact]
    public void BuildSideSummary_WhenNoLineSelected_UsesIdleCopy()
    {
        var view = StockAvailabilityPanelPresenter.BuildSideSummary(new StockAvailabilityPanelState());

        Assert.Equal("Selected Line", view.Title);
        Assert.Equal("No line selected", view.Headline);
        Assert.Contains("Select a queued line", view.Detail);
        Assert.Equal(StockAvailabilityPanelSeverity.Muted, view.Severity);
    }

    private static MarketAcquisitionQuickShopLineDraft CreateLine(
        string quantityMode = "TargetQuantity",
        uint targetQuantity = 1,
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
