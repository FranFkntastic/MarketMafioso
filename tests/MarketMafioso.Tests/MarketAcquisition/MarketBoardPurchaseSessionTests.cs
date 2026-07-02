namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardPurchaseSessionTests
{
    [Fact]
    public void RecordConfirmationAttempt_WaitsUntilPromptIsSubmitted()
    {
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var candidate = CreateCandidate("cheap");
        var session = MarketMafioso.MarketAcquisition.MarketBoardPurchaseSession.Start(
            candidate,
            now,
            TimeSpan.FromSeconds(15));

        var result = session.RecordConfirmationAttempt(
            new MarketMafioso.Automation.MarketBoard.MarketBoardPurchaseResult
            {
                Status = "ConfirmationPending",
                Message = "Waiting.",
                Candidate = candidate,
            },
            now.AddSeconds(3),
            TimeSpan.FromSeconds(15));

        Assert.Equal("WaitingForConfirmation", result.Status);
        Assert.Equal(MarketMafioso.MarketAcquisition.MarketBoardPurchaseSessionPhase.WaitingForConfirmation, result.Phase);
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
            new MarketMafioso.Automation.MarketBoard.MarketBoardPurchaseResult
            {
                Status = "ConfirmationPending",
                Message = "Waiting.",
                Candidate = candidate,
            },
            now.AddSeconds(16),
            TimeSpan.FromSeconds(15));

        Assert.Equal("ConfirmationTimeout", result.Status);
        Assert.Equal(MarketMafioso.MarketAcquisition.MarketBoardPurchaseSessionPhase.Failed, result.Phase);
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
                new MarketMafioso.Automation.MarketBoard.MarketBoardPurchaseResult
                {
                    Status = "ConfirmationSubmitted",
                    Message = "Submitted.",
                    Candidate = candidate,
                },
                now.AddSeconds(2),
                TimeSpan.FromSeconds(15));

        var result = session.RecordFreshRead(
            CreateRead(CreateListing("other")),
            now.AddSeconds(4));

        Assert.Equal("Completed", result.Status);
        Assert.Equal(MarketMafioso.MarketAcquisition.MarketBoardPurchaseSessionPhase.Completed, result.Phase);
        Assert.False(result.IsActive);
    }

    [Fact]
    public void RecordFreshRead_CompletesWhenConfirmedPurchaseClosesResultsWindow()
    {
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var candidate = CreateCandidate("cheap");
        var session = MarketMafioso.MarketAcquisition.MarketBoardPurchaseSession.Start(
                candidate,
                now,
                TimeSpan.FromSeconds(15))
            .RecordConfirmationAttempt(
                new MarketMafioso.Automation.MarketBoard.MarketBoardPurchaseResult
                {
                    Status = "ConfirmationSubmitted",
                    Message = "Submitted.",
                    Candidate = candidate,
                },
                now.AddSeconds(2),
                TimeSpan.FromSeconds(15));

        var result = session.RecordFreshRead(
            new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
            {
                Status = "MarketBoardNotOpen",
                Message = "Result window closed.",
            },
            now.AddSeconds(4));

        Assert.Equal("Completed", result.Status);
        Assert.False(result.IsActive);
    }

    [Fact]
    public void CreateFreshReadSnapshot_ClassifiesClosedResultsWindowAsExpectedAlternate()
    {
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var candidate = CreateCandidate("cheap");
        var session = MarketMafioso.MarketAcquisition.MarketBoardPurchaseSession.Start(
                candidate,
                now,
                TimeSpan.FromSeconds(15))
            .RecordConfirmationAttempt(
                new MarketMafioso.Automation.MarketBoard.MarketBoardPurchaseResult
                {
                    Status = "ConfirmationSubmitted",
                    Message = "Submitted.",
                    Candidate = candidate,
                },
                now.AddSeconds(2),
                TimeSpan.FromSeconds(15));

        var snapshot = session.CreateFreshReadSnapshot(new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
        {
            Status = "MarketBoardNotOpen",
            Message = "Result window closed.",
        });

        Assert.Equal("BuyListing", snapshot.Step);
        Assert.Equal("AfterConfirmation", snapshot.Phase);
        Assert.Equal("ListingRemoved", snapshot.Expected);
        Assert.Equal("MarketBoardNotOpen", snapshot.Observed);
        Assert.Equal(MarketMafioso.Automation.MarketBoard.MarketBoardAutomationOutcome.ExpectedAlternate, snapshot.Outcome);
        Assert.Equal("TreatListingAsRemoved", snapshot.NextAction);
        Assert.Equal("cheap", snapshot.Details["candidateListingId"]);
    }

    [Fact]
    public void RecordFreshRead_CompletesWhenConfirmedPurchaseLeavesNoListings()
    {
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var candidate = CreateCandidate("cheap");
        var session = MarketMafioso.MarketAcquisition.MarketBoardPurchaseSession.Start(
                candidate,
                now,
                TimeSpan.FromSeconds(15))
            .RecordConfirmationAttempt(
                new MarketMafioso.Automation.MarketBoard.MarketBoardPurchaseResult
                {
                    Status = "ConfirmationSubmitted",
                    Message = "Submitted.",
                    Candidate = candidate,
                },
                now.AddSeconds(2),
                TimeSpan.FromSeconds(15));

        var result = session.RecordFreshRead(
            new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
            {
                Status = "NoListings",
                Message = "No listings remain.",
                ItemId = 7017,
                WorldName = "Rafflesia",
            },
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
                new MarketMafioso.Automation.MarketBoard.MarketBoardPurchaseResult
                {
                    Status = "ConfirmationSubmitted",
                    Message = "Submitted.",
                    Candidate = candidate,
                },
                now.AddSeconds(2),
                TimeSpan.FromSeconds(15));

        var result = session.RecordFreshRead(
            CreateRead(CreateListing("cheap")),
            now.AddSeconds(4));

        Assert.Equal("WaitingForListingRemoval", result.Status);
        Assert.Equal(MarketMafioso.MarketAcquisition.MarketBoardPurchaseSessionPhase.WaitingForListingRemoval, result.Phase);
        Assert.True(result.IsActive);
    }

    [Fact]
    public void RecordFreshRead_ReportsUnknownOutcomeWhenSubmittedListingStillExistsAfterDeadline()
    {
        var now = DateTimeOffset.Parse("2026-06-26T12:00:00Z");
        var candidate = CreateCandidate("cheap");
        var session = MarketMafioso.MarketAcquisition.MarketBoardPurchaseSession.Start(
                candidate,
                now,
                TimeSpan.FromSeconds(15))
            .RecordConfirmationAttempt(
                new MarketMafioso.Automation.MarketBoard.MarketBoardPurchaseResult
                {
                    Status = "ConfirmationSubmitted",
                    Message = "Submitted.",
                    Candidate = candidate,
                },
                now.AddSeconds(2),
                TimeSpan.FromSeconds(15));

        var result = session.RecordFreshRead(
            CreateRead(CreateListing("cheap")),
            now.AddSeconds(18));

        Assert.Equal("PurchaseOutcomeUnknown", result.Status);
        Assert.Equal(MarketMafioso.MarketAcquisition.MarketBoardPurchaseSessionPhase.Failed, result.Phase);
        Assert.False(result.IsActive);
        Assert.Contains("confirmation was submitted", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cheap", result.Message, StringComparison.Ordinal);
        Assert.Contains("still present", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static MarketMafioso.Automation.MarketBoard.MarketBoardPurchaseCandidate CreateCandidate(string listingId) =>
        MarketMafioso.Automation.MarketBoard.MarketBoardPurchaseCandidate.FromLiveListing(CreateListing(listingId));

    private static MarketMafioso.Automation.MarketBoard.MarketBoardReadResult CreateRead(
        params MarketMafioso.Automation.MarketBoard.MarketBoardLiveListing[] listings) =>
        new()
        {
            Status = "Ready",
            ItemId = 7017,
            WorldName = "Rafflesia",
            Listings = listings,
        };

    private static MarketMafioso.Automation.MarketBoard.MarketBoardLiveListing CreateListing(string listingId) =>
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


