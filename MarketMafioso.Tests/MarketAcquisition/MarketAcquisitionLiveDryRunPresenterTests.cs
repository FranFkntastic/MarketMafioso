namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionLiveDryRunPresenterTests
{
    [Fact]
    public void BuildSummary_CountsBuyAndSkippedRows()
    {
        var dryRun = new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRun
        {
            Status = "Ready",
            Message = "Would buy confirmed live listings.",
            RequestedQuantity = 999,
            WouldBuyQuantity = 596,
            WouldSpendGil = 655_303,
            Rows =
            [
                CreateRow("WouldBuy"),
                CreateRow("WouldBuy"),
                CreateRow("Skipped"),
            ],
        };

        var summary = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRunPresenter.BuildSummary(dryRun);

        Assert.Equal("Ready", summary.Status);
        Assert.Equal("Would buy confirmed live listings.", summary.Message);
        Assert.Equal(999u, summary.RequestedQuantity);
        Assert.Equal(596u, summary.WouldBuyQuantity);
        Assert.Equal(655_303u, summary.WouldSpendGil);
        Assert.Equal(2, summary.WouldBuyRows);
        Assert.Equal(1, summary.SkippedRows);
        Assert.Equal(3, summary.TotalRows);
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRunRow CreateRow(string decision) =>
        new()
        {
            Decision = decision,
            Reason = "Test",
            Message = "Test row",
            LiveListing = new MarketMafioso.MarketAcquisition.MarketBoardLiveListing
            {
                ItemId = 2,
                WorldName = "Gilgamesh",
                ListingId = decision,
                RetainerId = $"retainer-{decision}",
                RetainerName = $"Retainer {decision}",
                UnitPrice = 100,
                Quantity = 1,
            },
        };
}
