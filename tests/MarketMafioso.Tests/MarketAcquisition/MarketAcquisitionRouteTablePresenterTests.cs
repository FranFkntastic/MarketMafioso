namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteTablePresenterTests
{
    [Fact]
    public void BuildRows_SummarizesSinglePlannedWorld()
    {
        var stop = new MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteStop
        {
            WorldName = "Siren",
            DataCenter = "Aether",
            Status = "Pending",
            PlannedQuantity = 10,
            PlannedGil = 1_000,
            LineStates =
            [
                new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteLineState
                {
                    LineId = "line-1",
                    ItemId = 2,
                    ItemName = "Fire Shard",
                    Source = "Planned",
                    Status = "Pending",
                    PlannedQuantity = 10,
                    PlannedGil = 1_000,
                },
            ],
        };

        var row = Assert.Single(MarketMafioso.MarketAcquisition.MarketAcquisitionRouteTablePresenter.BuildRows([stop]));

        Assert.Equal("Siren", row.WorldName);
        Assert.Equal("Aether", row.DataCenter);
        Assert.Equal("Fire Shard (2)", row.RouteLines);
        Assert.Equal("Pending", row.State);
        Assert.Equal("10 / 1,000 gil", row.Intent);
        Assert.Equal("-", row.Result);
        Assert.Equal("1 planned line", row.LineMix);
    }

    [Fact]
    public void BuildRows_SummarizesMultiItemWorldWithOpportunisticLines()
    {
        var stop = CreateMultiItemStop();

        var row = Assert.Single(MarketMafioso.MarketAcquisition.MarketAcquisitionRouteTablePresenter.BuildRows([stop]));

        Assert.Equal("Raw Larimar (12551), Topaz (5190) +1", row.RouteLines);
        Assert.Equal("2 planned / 1 opportunistic", row.LineMix);
        Assert.Equal("Buying", row.State);
        Assert.Equal("3,366 / 944,658 gil", row.Intent);
        Assert.Equal("990 / 275,022 gil", row.Result);
        Assert.Contains("Opportunistic", row.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRows_UsesLinePlannedTotalsForCollapsedIntent()
    {
        var stop = CreateMultiItemStop() with
        {
            PlannedQuantity = 1,
            PlannedGil = 2,
        };

        var row = Assert.Single(MarketMafioso.MarketAcquisition.MarketAcquisitionRouteTablePresenter.BuildRows([stop]));

        Assert.Equal("3,366 / 944,658 gil", row.Intent);
    }

    [Fact]
    public void BuildRows_ReportsPartialWhenCompletedAndSkippedLinesArePresent()
    {
        var stop = CreateMultiItemStop() with
        {
            Status = "Complete",
            LineStates =
            [
                new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteLineState
                {
                    LineId = "line-1",
                    ItemId = 12551,
                    ItemName = "Raw Larimar",
                    Source = "Planned",
                    Status = "Complete",
                    PurchasedQuantity = 990,
                    SpentGil = 275_022,
                },
                new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteLineState
                {
                    LineId = "line-2",
                    ItemId = 5190,
                    ItemName = "Topaz",
                    Source = "Planned",
                    Status = "SkippedNoLiveStock",
                    LatestMessage = "No safe live candidates remained.",
                },
            ],
        };

        var row = Assert.Single(MarketMafioso.MarketAcquisition.MarketAcquisitionRouteTablePresenter.BuildRows([stop]));

        Assert.Equal("Partial", row.State);
        Assert.Contains("1 skipped", row.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildRows_KeepsDiscoveredValuesPerExpandedLine()
    {
        var stop = CreateMultiItemStop();

        var row = Assert.Single(MarketMafioso.MarketAcquisition.MarketAcquisitionRouteTablePresenter.BuildRows([stop]));

        Assert.Collection(
            row.Lines,
            first =>
            {
                Assert.Equal("Raw Larimar (12551)", first.Item);
                Assert.Equal("Planned", first.Source);
                Assert.Equal("3,000 / 800,000 gil", first.Discovered);
                Assert.Equal("990 / 275,022 gil", first.Bought);
            },
            second =>
            {
                Assert.Equal("Topaz (5190)", second.Item);
                Assert.Equal("Planned", second.Source);
                Assert.Equal("-", second.Discovered);
                Assert.Equal("-", second.Bought);
            },
            third =>
            {
                Assert.Equal("Gold Ore (5118)", third.Item);
                Assert.Equal("Opportunistic", third.Source);
                Assert.Equal("200 / 44,000 gil", third.Discovered);
                Assert.Equal("-", third.Bought);
            });
    }

    [Fact]
    public void BuildRows_UsesLineBoughtTotalsForCollapsedResult()
    {
        var stop = CreateMultiItemStop() with
        {
            PurchasedQuantity = 12_345,
            SpentGil = 6_789_000,
        };

        var row = Assert.Single(MarketMafioso.MarketAcquisition.MarketAcquisitionRouteTablePresenter.BuildRows([stop]));

        Assert.Equal("990 / 275,022 gil", row.Result);
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteStop CreateMultiItemStop() =>
        new()
        {
            WorldName = "Kraken",
            DataCenter = "Dynamis",
            Status = "Purchasing",
            PlannedQuantity = 3_366,
            PlannedGil = 944_658,
            PurchasedQuantity = 990,
            SpentGil = 275_022,
            LineStates =
            [
                new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteLineState
                {
                    LineId = "line-1",
                    ItemId = 12551,
                    ItemName = "Raw Larimar",
                    Source = "Planned",
                    Status = "Complete",
                    PlannedQuantity = 2_000,
                    PlannedGil = 600_000,
                    LiveCandidateStatus = "Ready",
                    LiveObservedQuantity = 3_000,
                    LiveObservedGil = 800_000,
                    LiveReadableListingCount = 20,
                    LiveReportedListingCount = 25,
                    PurchasedQuantity = 990,
                    SpentGil = 275_022,
                },
                new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteLineState
                {
                    LineId = "line-2",
                    ItemId = 5190,
                    ItemName = "Topaz",
                    Source = "Planned",
                    Status = "Pending",
                    PlannedQuantity = 1_366,
                    PlannedGil = 344_658,
                },
                new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteLineState
                {
                    LineId = "line-3",
                    ItemId = 5118,
                    ItemName = "Gold Ore",
                    Source = "Opportunistic",
                    Status = "SkippedNoLiveStock",
                    LiveCandidateStatus = "NoSafeListings",
                    LiveObservedQuantity = 200,
                    LiveObservedGil = 44_000,
                    LiveReadableListingCount = 4,
                    LiveReportedListingCount = 4,
                },
            ],
        };
}
