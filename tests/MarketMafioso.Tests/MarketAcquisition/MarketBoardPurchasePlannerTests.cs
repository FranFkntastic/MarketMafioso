namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardPurchasePlannerTests
{
    [Fact]
    public void SelectFirstCandidate_UsesFirstWouldBuyRow()
    {
        var candidatePlan = new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
        {
            Status = "Ready",
            Rows =
            [
                CreateRow("Skipped", CreateListing("expensive", unitPrice: 2_000)),
                CreateRow("WouldBuy", CreateListing("cheap", unitPrice: 1_000)),
                CreateRow("WouldBuy", CreateListing("next", unitPrice: 1_100)),
            ],
        };

        var candidate = MarketMafioso.MarketAcquisition.MarketBoardPurchasePlanner.SelectFirstCandidate(candidatePlan);

        Assert.NotNull(candidate);
        Assert.Equal("cheap", candidate.ListingId);
        Assert.Equal(1_000u, candidate.UnitPrice);
    }

    [Fact]
    public void SelectFirstCandidate_ReturnsNullWhenNoWouldBuyRowsExist()
    {
        var candidatePlan = new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
        {
            Status = "NoSafeListings",
            Rows =
            [
                CreateRow("Skipped", CreateListing("expensive", unitPrice: 2_000)),
            ],
        };

        var candidate = MarketMafioso.MarketAcquisition.MarketBoardPurchasePlanner.SelectFirstCandidate(candidatePlan);

        Assert.Null(candidate);
    }

    [Fact]
    public void SelectFirstCandidate_IgnoresInvalidZeroWouldBuyRows()
    {
        var candidatePlan = new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
        {
            Status = "Ready",
            Rows =
            [
                CreateRow("WouldBuy", CreateInvalidListing()),
                CreateRow("WouldBuy", CreateListing("real", unitPrice: 900)),
            ],
        };

        var candidate = MarketMafioso.MarketAcquisition.MarketBoardPurchasePlanner.SelectFirstCandidate(candidatePlan);

        Assert.NotNull(candidate);
        Assert.Equal("real", candidate.ListingId);
        Assert.Equal(900u, candidate.UnitPrice);
    }

    [Fact]
    public void RevalidateCandidate_ReturnsReadyWhenFreshListingMatches()
    {
        var candidate = CreateCandidate("cheap");
        var freshRead = new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
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
        var freshRead = new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
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
        var freshRead = new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
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

    [Fact]
    public void RevalidateCandidate_RejectsInvalidZeroCandidate()
    {
        var candidate = MarketMafioso.Automation.MarketBoard.MarketBoardPurchaseCandidate.FromLiveListing(CreateInvalidListing());
        var freshRead = new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
        {
            Status = "Ready",
            ItemId = 7017,
            WorldName = "Rafflesia",
            Listings =
            [
                CreateInvalidListing(),
            ],
        };

        var result = MarketMafioso.MarketAcquisition.MarketBoardPurchasePlanner.RevalidateCandidate(candidate, freshRead);

        Assert.Equal("InvalidCandidate", result.Status);
        Assert.False(result.CanAttemptPurchase);
    }

    private static MarketMafioso.Automation.MarketBoard.MarketBoardPurchaseCandidate CreateCandidate(string listingId) =>
        MarketMafioso.Automation.MarketBoard.MarketBoardPurchaseCandidate.FromLiveListing(CreateListing(listingId));

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidateRow CreateRow(
        string decision,
        MarketMafioso.Automation.MarketBoard.MarketBoardLiveListing listing) =>
        new()
        {
            Decision = decision,
            Reason = decision == "WouldBuy" ? "SafeLiveCandidate" : "AboveThreshold",
            LiveListing = listing,
        };

    private static MarketMafioso.Automation.MarketBoard.MarketBoardLiveListing CreateListing(
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

    private static MarketMafioso.Automation.MarketBoard.MarketBoardLiveListing CreateInvalidListing() =>
        new()
        {
            ItemId = 7017,
            WorldName = "Rafflesia",
            ListingId = "0",
            RetainerId = "0",
            UnitPrice = 0,
            Quantity = 0,
            IsHq = false,
        };
}


