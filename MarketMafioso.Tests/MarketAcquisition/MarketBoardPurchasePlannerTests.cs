namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardPurchasePlannerTests
{
    [Fact]
    public void SelectFirstCandidate_UsesFirstWouldBuyRow()
    {
        var dryRun = new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRun
        {
            Status = "Ready",
            Rows =
            [
                CreateRow("Skipped", CreateListing("expensive", unitPrice: 2_000)),
                CreateRow("WouldBuy", CreateListing("cheap", unitPrice: 1_000)),
                CreateRow("WouldBuy", CreateListing("next", unitPrice: 1_100)),
            ],
        };

        var candidate = MarketMafioso.MarketAcquisition.MarketBoardPurchasePlanner.SelectFirstCandidate(dryRun);

        Assert.NotNull(candidate);
        Assert.Equal("cheap", candidate.ListingId);
        Assert.Equal(1_000u, candidate.UnitPrice);
    }

    [Fact]
    public void SelectFirstCandidate_ReturnsNullWhenNoWouldBuyRowsExist()
    {
        var dryRun = new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRun
        {
            Status = "NoSafeListings",
            Rows =
            [
                CreateRow("Skipped", CreateListing("expensive", unitPrice: 2_000)),
            ],
        };

        var candidate = MarketMafioso.MarketAcquisition.MarketBoardPurchasePlanner.SelectFirstCandidate(dryRun);

        Assert.Null(candidate);
    }

    [Fact]
    public void RevalidateCandidate_ReturnsReadyWhenFreshListingMatches()
    {
        var candidate = CreateCandidate("cheap");
        var freshRead = new MarketMafioso.MarketAcquisition.MarketBoardReadResult
        {
            Status = "Ready",
            ItemId = 7017,
            WorldName = "Rafflesia",
            Listings =
            [
                CreateListing("cheap"),
            ],
        };

        var result = MarketMafioso.MarketAcquisition.MarketBoardPurchasePlanner.RevalidateCandidate(candidate, freshRead);

        Assert.Equal("Ready", result.Status);
        Assert.True(result.CanAttemptPurchase);
        Assert.Equal("cheap", result.Candidate?.ListingId);
    }

    [Fact]
    public void RevalidateCandidate_RejectsChangedUnitPrice()
    {
        var candidate = CreateCandidate("cheap");
        var freshRead = new MarketMafioso.MarketAcquisition.MarketBoardReadResult
        {
            Status = "Ready",
            ItemId = 7017,
            WorldName = "Rafflesia",
            Listings =
            [
                CreateListing("cheap", unitPrice: 1_200),
            ],
        };

        var result = MarketMafioso.MarketAcquisition.MarketBoardPurchasePlanner.RevalidateCandidate(candidate, freshRead);

        Assert.Equal("ListingChanged", result.Status);
        Assert.False(result.CanAttemptPurchase);
    }

    [Fact]
    public void RevalidateCandidate_RejectsMissingListing()
    {
        var candidate = CreateCandidate("cheap");
        var freshRead = new MarketMafioso.MarketAcquisition.MarketBoardReadResult
        {
            Status = "Ready",
            ItemId = 7017,
            WorldName = "Rafflesia",
            Listings =
            [
                CreateListing("other"),
            ],
        };

        var result = MarketMafioso.MarketAcquisition.MarketBoardPurchasePlanner.RevalidateCandidate(candidate, freshRead);

        Assert.Equal("ListingMissing", result.Status);
        Assert.False(result.CanAttemptPurchase);
    }

    private static MarketMafioso.MarketAcquisition.MarketBoardPurchaseCandidate CreateCandidate(string listingId) =>
        MarketMafioso.MarketAcquisition.MarketBoardPurchaseCandidate.FromLiveListing(CreateListing(listingId));

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRunRow CreateRow(
        string decision,
        MarketMafioso.MarketAcquisition.MarketBoardLiveListing listing) =>
        new()
        {
            Decision = decision,
            Reason = decision == "WouldBuy" ? "SafeLiveCandidate" : "AboveThreshold",
            LiveListing = listing,
        };

    private static MarketMafioso.MarketAcquisition.MarketBoardLiveListing CreateListing(
        string listingId,
        uint unitPrice = 1_000,
        uint quantity = 5) =>
        new()
        {
            ItemId = 7017,
            WorldName = "Rafflesia",
            ListingId = listingId,
            RetainerId = "retainer-1",
            RetainerName = "Pann",
            UnitPrice = unitPrice,
            Quantity = quantity,
            IsHq = false,
        };
}
