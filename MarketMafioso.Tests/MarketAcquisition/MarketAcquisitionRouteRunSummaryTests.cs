namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteRunSummaryTests
{
    [Fact]
    public void Build_SummarizesCompletedRouteTotals()
    {
        var summary = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunSummary.Build(
            [
                CreateStop(
                    "Siren",
                    "Complete",
                    [
                        CreateLine("Raw Larimar", "Planned", "Complete", purchasedQuantity: 100, spentGil: 20_000),
                    ]),
                CreateStop(
                    "Maduin",
                    "Complete",
                    [
                        CreateLine("Topaz", "Planned", "Complete", purchasedQuantity: 50, spentGil: 10_000),
                    ]),
            ],
            new MarketMafioso.MarketAcquisition.MarketAcquisitionRunDiagnosticSummary(),
            diagnosticsPath: "route.log",
            observedListingsCsvPath: "observed.csv",
            purchaseRecordsCsvPath: "purchases.csv");

        Assert.Equal(150u, summary.PurchasedQuantity);
        Assert.Equal(30_000u, summary.SpentGil);
        Assert.Equal(2, summary.CompletedWorldCount);
        Assert.Equal(0, summary.PartialWorldCount);
        Assert.Equal(0, summary.FailedWorldCount);
        Assert.Equal(2, summary.CompletedLineCount);
        Assert.Equal("route.log", summary.DiagnosticsPath);
        Assert.Equal("observed.csv", summary.ObservedListingsCsvPath);
        Assert.Equal("purchases.csv", summary.PurchaseRecordsCsvPath);
    }

    [Fact]
    public void Build_SplitsPlannedAndOpportunisticPurchases()
    {
        var summary = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunSummary.Build(
            [
                CreateStop(
                    "Siren",
                    "Complete",
                    [
                        CreateLine("Raw Larimar", "Planned", "Complete", purchasedQuantity: 100, spentGil: 20_000),
                        CreateLine("Gold Ore", "Opportunistic", "Complete", purchasedQuantity: 25, spentGil: 4_000),
                    ]),
            ],
            new MarketMafioso.MarketAcquisition.MarketAcquisitionRunDiagnosticSummary(),
            diagnosticsPath: null,
            observedListingsCsvPath: null,
            purchaseRecordsCsvPath: null);

        Assert.Equal(100u, summary.PlannedPurchasedQuantity);
        Assert.Equal(20_000u, summary.PlannedSpentGil);
        Assert.Equal(25u, summary.OpportunisticPurchasedQuantity);
        Assert.Equal(4_000u, summary.OpportunisticSpentGil);
    }

    [Fact]
    public void Build_CountsPartialAndFailedWorlds()
    {
        var summary = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunSummary.Build(
            [
                CreateStop(
                    "Siren",
                    "Complete",
                    [
                        CreateLine("Raw Larimar", "Planned", "Complete", purchasedQuantity: 100, spentGil: 20_000),
                        CreateLine("Topaz", "Planned", "SkippedNoLiveStock"),
                    ]),
                CreateStop(
                    "Maduin",
                    "Failed",
                    [
                        CreateLine("Gold Ore", "Opportunistic", "Failed"),
                    ]),
            ],
            new MarketMafioso.MarketAcquisition.MarketAcquisitionRunDiagnosticSummary
            {
                FreshnessConfirmedCount = 1,
                FreshnessUnconfirmedCount = 2,
                FreshnessUnavailableCount = 3,
                Warnings = ["Freshness unavailable."],
            },
            diagnosticsPath: null,
            observedListingsCsvPath: null,
            purchaseRecordsCsvPath: null);

        Assert.Equal(0, summary.CompletedWorldCount);
        Assert.Equal(1, summary.PartialWorldCount);
        Assert.Equal(1, summary.FailedWorldCount);
        Assert.Equal(1, summary.CompletedLineCount);
        Assert.Equal(1, summary.SkippedLineCount);
        Assert.Equal(1, summary.FailedLineCount);
        Assert.Equal(1, summary.FreshnessConfirmedCount);
        Assert.Equal(2, summary.FreshnessUnconfirmedCount);
        Assert.Equal(3, summary.FreshnessUnavailableCount);
        Assert.Equal(["Freshness unavailable."], summary.Warnings);
    }

    [Fact]
    public void Build_OrdersTopItemsBySpentGil()
    {
        var summary = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunSummary.Build(
            [
                CreateStop(
                    "Siren",
                    "Complete",
                    [
                        CreateLine("Raw Larimar", "Planned", "Complete", itemId: 12551, purchasedQuantity: 10, spentGil: 100),
                        CreateLine("Topaz", "Planned", "Complete", itemId: 5190, purchasedQuantity: 5, spentGil: 500),
                    ]),
                CreateStop(
                    "Maduin",
                    "Complete",
                    [
                        CreateLine("Raw Larimar", "Planned", "Complete", itemId: 12551, purchasedQuantity: 20, spentGil: 300),
                    ]),
            ],
            new MarketMafioso.MarketAcquisition.MarketAcquisitionRunDiagnosticSummary(),
            diagnosticsPath: null,
            observedListingsCsvPath: null,
            purchaseRecordsCsvPath: null);

        Assert.Collection(
            summary.TopItemsBySpentGil,
            first =>
            {
                Assert.Equal("Topaz", first.ItemName);
                Assert.Equal(5190u, first.ItemId);
                Assert.Equal(5u, first.PurchasedQuantity);
                Assert.Equal(500u, first.SpentGil);
            },
            second =>
            {
                Assert.Equal("Raw Larimar", second.ItemName);
                Assert.Equal(12551u, second.ItemId);
                Assert.Equal(30u, second.PurchasedQuantity);
                Assert.Equal(400u, second.SpentGil);
            });
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionGuidedRouteStop CreateStop(
        string worldName,
        string status,
        IReadOnlyList<MarketMafioso.MarketAcquisition.MarketAcquisitionRouteLineState> lines) =>
        new()
        {
            WorldName = worldName,
            DataCenter = "Aether",
            Status = status,
            LineStates = lines,
            PurchasedQuantity = (uint)lines.Sum(line => line.PurchasedQuantity),
            SpentGil = (uint)lines.Sum(line => line.SpentGil),
        };

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionRouteLineState CreateLine(
        string itemName,
        string source,
        string status,
        uint itemId = 2,
        uint purchasedQuantity = 0,
        uint spentGil = 0) =>
        new()
        {
            LineId = $"{itemName}-{source}",
            ItemId = itemId,
            ItemName = itemName,
            Source = source,
            Status = status,
            PurchasedQuantity = purchasedQuantity,
            SpentGil = spentGil,
        };
}
