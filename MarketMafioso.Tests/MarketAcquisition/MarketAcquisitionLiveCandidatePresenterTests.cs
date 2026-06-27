namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionLiveCandidatePresenterTests
{
    [Fact]
    public void BuildSummary_CountsBuyAndSkippedRows()
    {
        var candidatePlan = new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
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

        var summary = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePresenter.BuildSummary(candidatePlan);

        Assert.Equal("Ready", summary.Status);
        Assert.Equal("Would buy confirmed live listings.", summary.Message);
        Assert.Equal(999u, summary.RequestedQuantity);
        Assert.Equal(596u, summary.WouldBuyQuantity);
        Assert.Equal(655_303u, summary.WouldSpendGil);
        Assert.Equal(2, summary.WouldBuyRows);
        Assert.Equal(1, summary.SkippedRows);
        Assert.Equal(3, summary.TotalRows);
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidateRow CreateRow(string decision) =>
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
