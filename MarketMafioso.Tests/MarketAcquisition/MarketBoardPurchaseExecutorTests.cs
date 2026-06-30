namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardPurchaseExecutorTests
{
    [Fact]
    public void ExecuteFirstCandidate_ReturnsNoCandidateWhenCandidatePlanHasNoWouldBuyRows()
    {
        var adapter = new RecordingPurchaseAdapter();
        var executor = new MarketMafioso.MarketAcquisition.MarketBoardPurchaseExecutor(adapter);

        var result = executor.ExecuteFirstCandidate(
            new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
            {
                Rows = [CreateRow("Skipped", CreateListing("expensive"))],
            },
            CreateRead(CreateListing("expensive")));

        Assert.Equal("NoCandidate", result.Status);
        Assert.Equal(0, adapter.Attempts);
    }

    [Fact]
    public void ExecuteFirstCandidate_ReturnsRevalidationFailureWithoutCallingAdapter()
    {
        var adapter = new RecordingPurchaseAdapter();
        var executor = new MarketMafioso.MarketAcquisition.MarketBoardPurchaseExecutor(adapter);

        var result = executor.ExecuteFirstCandidate(
            new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
            {
                Rows = [CreateRow("WouldBuy", CreateListing("cheap", unitPrice: 1_000))],
            },
            CreateRead(CreateListing("cheap", unitPrice: 1_200)));

        Assert.Equal("ListingChanged", result.Status);
        Assert.Equal(0, adapter.Attempts);
    }

    [Fact]
    public void ExecuteFirstCandidate_CallsAdapterOnceForValidatedCandidate()
    {
        var adapter = new RecordingPurchaseAdapter
        {
            Result = new MarketMafioso.MarketAcquisition.MarketBoardPurchaseResult
            {
                Status = "ConfirmationOpened",
                Message = "Opened purchase confirmation.",
            },
        };
        var executor = new MarketMafioso.MarketAcquisition.MarketBoardPurchaseExecutor(adapter);

        var result = executor.ExecuteFirstCandidate(
            new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
            {
                Rows =
                [
                    CreateRow("WouldBuy", CreateListing("cheap", unitPrice: 1_000)),
                    CreateRow("WouldBuy", CreateListing("second", unitPrice: 1_100)),
                ],
            },
            CreateRead(CreateListing("cheap", unitPrice: 1_000), CreateListing("second", unitPrice: 1_100)));

        Assert.Equal("ConfirmationOpened", result.Status);
        Assert.Equal(1, adapter.Attempts);
        Assert.Equal("cheap", adapter.LastCandidate?.ListingId);
    }

    [Fact]
    public void ExecuteFirstCandidate_PrefersFirstFreshSafeRowOverCheapestPlannedIdentity()
    {
        var adapter = new RecordingPurchaseAdapter();
        var executor = new MarketMafioso.MarketAcquisition.MarketBoardPurchaseExecutor(adapter);

        var result = executor.ExecuteFirstCandidate(
            new MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan
            {
                Rows =
                [
                    CreateRow("WouldBuy", CreateListing("offscreen-cheapest", unitPrice: 900)),
                    CreateRow("WouldBuy", CreateListing("visible-first", unitPrice: 1_000)),
                ],
            },
            CreateRead(CreateListing("visible-first", unitPrice: 1_000), CreateListing("offscreen-cheapest", unitPrice: 900)));

        Assert.Equal("AdapterCalled", result.Status);
        Assert.Equal(1, adapter.Attempts);
        Assert.Equal("visible-first", adapter.LastCandidate?.ListingId);
    }

    private sealed class RecordingPurchaseAdapter : MarketMafioso.MarketAcquisition.IMarketBoardPurchaseAdapter
    {
        public int Attempts { get; private set; }
        public MarketMafioso.MarketAcquisition.MarketBoardPurchaseCandidate? LastCandidate { get; private set; }
        public MarketMafioso.MarketAcquisition.MarketBoardPurchaseResult Result { get; init; } = new()
        {
            Status = "AdapterCalled",
            Message = "Adapter was called.",
        };

        public MarketMafioso.MarketAcquisition.MarketBoardPurchaseResult ExecutePurchase(
            MarketMafioso.MarketAcquisition.MarketBoardPurchaseCandidate candidate,
            MarketMafioso.MarketAcquisition.MarketBoardLiveListing freshListing)
        {
            Attempts++;
            LastCandidate = candidate;
            return Result with { Candidate = candidate };
        }
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidateRow CreateRow(
        string decision,
        MarketMafioso.MarketAcquisition.MarketBoardLiveListing listing) =>
        new()
        {
            Decision = decision,
            Reason = decision == "WouldBuy" ? "SafeLiveCandidate" : "AboveThreshold",
            LiveListing = listing,
        };

    private static MarketMafioso.MarketAcquisition.MarketBoardReadResult CreateRead(
        params MarketMafioso.MarketAcquisition.MarketBoardLiveListing[] listings) =>
        new()
        {
            Status = "Ready",
            ItemId = 7017,
            WorldName = "Rafflesia",
            Listings = listings,
        };

    private static MarketMafioso.MarketAcquisition.MarketBoardLiveListing CreateListing(
        string listingId,
        uint unitPrice = 1_000) =>
        new()
        {
            ItemId = 7017,
            WorldName = "Rafflesia",
            ListingId = listingId,
            RetainerId = "retainer-1",
            RetainerName = "Pann",
            UnitPrice = unitPrice,
            Quantity = 5,
            IsHq = false,
        };
}
