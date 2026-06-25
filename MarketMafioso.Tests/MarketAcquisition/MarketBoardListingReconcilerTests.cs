namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardListingReconcilerTests
{
    [Fact]
    public void Reconcile_MatchesPlannedListingByStableIdentityAndPriceFacts()
    {
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing(listingId: "listing-1", retainerId: "retainer-1", unitPrice: 50, quantity: 10),
        };

        var result = MarketMafioso.MarketAcquisition.MarketBoardListingReconciler.Reconcile(
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings);

        Assert.Equal("Ready", result.Status);
        var listing = Assert.Single(result.Listings);
        Assert.Equal("Matched", listing.Status);
        Assert.True(listing.IsExactMatch);
    }

    [Fact]
    public void Reconcile_FlagsPriceIncrease()
    {
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing(listingId: "listing-1", retainerId: "retainer-1", unitPrice: 55, quantity: 10),
        };

        var result = MarketMafioso.MarketAcquisition.MarketBoardListingReconciler.Reconcile(
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings);

        Assert.Equal("Blocked", result.Status);
        var listing = Assert.Single(result.Listings);
        Assert.Equal("PriceChanged", listing.Status);
        Assert.Contains("unit price", listing.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reconcile_FailsClosedWhenLiveWorldOrItemDoesNotMatch()
    {
        var plan = CreatePlan();

        var wrongWorld = Assert.Throws<InvalidOperationException>(() =>
            MarketMafioso.MarketAcquisition.MarketBoardListingReconciler.Reconcile(
                plan,
                "Faerie",
                itemId: 2,
                []));
        Assert.Contains("world", wrongWorld.Message, StringComparison.OrdinalIgnoreCase);

        var wrongItem = Assert.Throws<InvalidOperationException>(() =>
            MarketMafioso.MarketAcquisition.MarketBoardListingReconciler.Reconcile(
                plan,
                "Gilgamesh",
                itemId: 4,
                []));
        Assert.Contains("item", wrongItem.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reconcile_FlagsMissingListing()
    {
        var plan = CreatePlan();

        var result = MarketMafioso.MarketAcquisition.MarketBoardListingReconciler.Reconcile(
            plan,
            "Gilgamesh",
            itemId: 2,
            []);

        Assert.Equal("Blocked", result.Status);
        var listing = Assert.Single(result.Listings);
        Assert.Equal("Missing", listing.Status);
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionPlan CreatePlan() =>
        new()
        {
            RequestId = "request-1",
            Status = "Ready",
            WorldMode = "Recommended",
            ItemId = 2,
            RequestedQuantity = 10,
            PlannedQuantity = 10,
            PlannedGil = 500,
            PreparedAtUtc = DateTimeOffset.UnixEpoch,
            WorldBatches =
            [
                new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldBatch
                {
                    WorldName = "Gilgamesh",
                    PlannedQuantity = 10,
                    PlannedGil = 500,
                    Listings =
                    [
                        new MarketMafioso.MarketAcquisition.MarketAcquisitionPlannedListing
                        {
                            ListingId = "listing-1",
                            RetainerId = "retainer-1",
                            RetainerName = "Retainer",
                            Quantity = 10,
                            UnitPrice = 50,
                            TotalGil = 500,
                            IsHq = false,
                            LastReviewTimeUtc = DateTimeOffset.UnixEpoch,
                        },
                    ],
                },
            ],
        };

    private static MarketMafioso.MarketAcquisition.MarketBoardLiveListing CreateLiveListing(
        string listingId,
        string retainerId,
        uint unitPrice,
        uint quantity) =>
        new()
        {
            ItemId = 2,
            WorldName = "Gilgamesh",
            ListingId = listingId,
            RetainerId = retainerId,
            RetainerName = "Retainer",
            UnitPrice = unitPrice,
            Quantity = quantity,
            IsHq = false,
        };
}
