namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardPurchaseSessionTests
{
    [Fact]
    public void RecordConfirmationAttempt_WaitsUntilPromptIsAccepted()
    {
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var candidate = CreateCandidate("cheap");
        var session = MarketMafioso.MarketAcquisition.MarketBoardPurchaseSession.Start(
            candidate,
            now,
            TimeSpan.FromSeconds(15));

        var result = session.RecordConfirmationAttempt(
            new MarketMafioso.MarketAcquisition.MarketBoardPurchaseResult
            {
                Status = "ConfirmationPending",
                Message = "Waiting.",
                Candidate = candidate,
            },
            now.AddSeconds(3),
            TimeSpan.FromSeconds(15));

        Assert.Equal("WaitingForConfirmation", result.Status);
        Assert.True(result.IsActive);
    }

    [Fact]
    public void RecordConfirmationAttempt_TimesOutWhenPromptNeverAppears()
    {
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var candidate = CreateCandidate("cheap");
        var session = MarketMafioso.MarketAcquisition.MarketBoardPurchaseSession.Start(
            candidate,
            now,
            TimeSpan.FromSeconds(15));

        var result = session.RecordConfirmationAttempt(
            new MarketMafioso.MarketAcquisition.MarketBoardPurchaseResult
            {
                Status = "ConfirmationPending",
                Message = "Waiting.",
                Candidate = candidate,
            },
            now.AddSeconds(16),
            TimeSpan.FromSeconds(15));

        Assert.Equal("ConfirmationTimeout", result.Status);
        Assert.False(result.IsActive);
    }

    [Fact]
    public void RecordFreshRead_CompletesWhenConfirmedListingDisappears()
    {
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var candidate = CreateCandidate("cheap");
        var session = MarketMafioso.MarketAcquisition.MarketBoardPurchaseSession.Start(
                candidate,
                now,
                TimeSpan.FromSeconds(15))
            .RecordConfirmationAttempt(
                new MarketMafioso.MarketAcquisition.MarketBoardPurchaseResult
                {
                    Status = "ConfirmationAccepted",
                    Message = "Accepted.",
                    Candidate = candidate,
                },
                now.AddSeconds(2),
                TimeSpan.FromSeconds(15));

        var result = session.RecordFreshRead(
            CreateRead(CreateListing("other")),
            now.AddSeconds(4));

        Assert.Equal("Completed", result.Status);
        Assert.False(result.IsActive);
    }

    [Fact]
    public void RecordFreshRead_WaitsWhileConfirmedListingStillExists()
    {
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var candidate = CreateCandidate("cheap");
        var session = MarketMafioso.MarketAcquisition.MarketBoardPurchaseSession.Start(
                candidate,
                now,
                TimeSpan.FromSeconds(15))
            .RecordConfirmationAttempt(
                new MarketMafioso.MarketAcquisition.MarketBoardPurchaseResult
                {
                    Status = "ConfirmationAccepted",
                    Message = "Accepted.",
                    Candidate = candidate,
                },
                now.AddSeconds(2),
                TimeSpan.FromSeconds(15));

        var result = session.RecordFreshRead(
            CreateRead(CreateListing("cheap")),
            now.AddSeconds(4));

        Assert.Equal("WaitingForListingRemoval", result.Status);
        Assert.True(result.IsActive);
    }

    private static MarketMafioso.MarketAcquisition.MarketBoardPurchaseCandidate CreateCandidate(string listingId) =>
        MarketMafioso.MarketAcquisition.MarketBoardPurchaseCandidate.FromLiveListing(CreateListing(listingId));

    private static MarketMafioso.MarketAcquisition.MarketBoardReadResult CreateRead(
        params MarketMafioso.MarketAcquisition.MarketBoardLiveListing[] listings) =>
        new()
        {
            Status = "Ready",
            ItemId = 7017,
            WorldName = "Rafflesia",
            Listings = listings,
        };

    private static MarketMafioso.MarketAcquisition.MarketBoardLiveListing CreateListing(string listingId) =>
        new()
        {
            ItemId = 7017,
            WorldName = "Rafflesia",
            ListingId = listingId,
            RetainerId = "retainer-1",
            RetainerName = "Pann",
            UnitPrice = 1_000,
            Quantity = 5,
            IsHq = false,
        };
}
