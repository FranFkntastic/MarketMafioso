namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionLiveCandidatePlannerTests
{
    [Fact]
    public void BuildCandidatePlan_TargetQuantityUsesLowestConfirmedLivePriceAndAllowsWholeStackOverage()
    {
        var request = CreateRequest(quantityMode: "TargetQuantity", quantity: 10, maxUnitPrice: 100);
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing("planned-expensive", quantity: 10, unitPrice: 90),
            CreateLiveListing("new-cheap", quantity: 12, unitPrice: 50),
            CreateLiveListing("too-expensive", quantity: 99, unitPrice: 101),
        };

        var candidatePlan = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings);

        Assert.Equal("Ready", candidatePlan.Status);
        Assert.Equal(12u, candidatePlan.WouldBuyQuantity);
        Assert.Equal(600u, candidatePlan.WouldSpendGil);
        Assert.Equal(["new-cheap"], candidatePlan.Rows.Where(row => row.Decision == "WouldBuy").Select(row => row.LiveListing.ListingId).ToArray());
        Assert.Equal("TargetSatisfied", candidatePlan.Rows.Single(row => row.LiveListing.ListingId == "planned-expensive").Reason);
        Assert.Equal("AboveThreshold", candidatePlan.Rows.Single(row => row.LiveListing.ListingId == "too-expensive").Reason);
    }

    [Fact]
    public void BuildCandidatePlan_AllBelowThresholdIncludesEverySafeVisibleListing()
    {
        var request = CreateRequest(quantityMode: "AllBelowThreshold", quantity: 0, maxUnitPrice: 100, maxTotalGil: 0);
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing("planned", quantity: 3, unitPrice: 80),
            CreateLiveListing("new-cheap", quantity: 2, unitPrice: 70),
            CreateLiveListing("too-expensive", quantity: 1, unitPrice: 101),
        };

        var candidatePlan = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings);

        Assert.Equal("Ready", candidatePlan.Status);
        Assert.Equal(5u, candidatePlan.WouldBuyQuantity);
        Assert.Equal(380u, candidatePlan.WouldSpendGil);
        Assert.Equal(["new-cheap", "planned"], candidatePlan.Rows.Where(row => row.Decision == "WouldBuy").Select(row => row.LiveListing.ListingId).ToArray());
    }

    [Fact]
    public void BuildCandidatePlan_AllBelowThresholdRespectsMaxQuantityWhenProvided()
    {
        var request = CreateRequest(quantityMode: "AllBelowThreshold", quantity: 5, maxUnitPrice: 100, maxTotalGil: 0);
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing("first", quantity: 3, unitPrice: 70),
            CreateLiveListing("would-exceed-cap", quantity: 3, unitPrice: 80),
            CreateLiveListing("second", quantity: 2, unitPrice: 90),
        };

        var candidatePlan = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings);

        Assert.Equal("Ready", candidatePlan.Status);
        Assert.Equal(5u, candidatePlan.WouldBuyQuantity);
        Assert.Equal(["first", "second"], candidatePlan.Rows.Where(row => row.Decision == "WouldBuy").Select(row => row.LiveListing.ListingId).ToArray());
        Assert.Equal("MaxQuantityExceeded", candidatePlan.Rows.Single(row => row.LiveListing.ListingId == "would-exceed-cap").Reason);
    }

    [Fact]
    public void BuildCandidatePlan_TargetQuantityUsesRemainingQuantityAfterPurchases()
    {
        var request = CreateRequest(quantityMode: "TargetQuantity", quantity: 10, maxUnitPrice: 100);
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing("needed", quantity: 4, unitPrice: 50),
            CreateLiveListing("extra", quantity: 6, unitPrice: 60),
        };

        var candidatePlan = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings,
            alreadyPurchasedQuantity: 6,
            alreadySpentGil: 300);

        Assert.Equal("Ready", candidatePlan.Status);
        Assert.Equal(4u, candidatePlan.WouldBuyQuantity);
        Assert.Equal(["needed"], candidatePlan.Rows.Where(row => row.Decision == "WouldBuy").Select(row => row.LiveListing.ListingId).ToArray());
        Assert.Equal("TargetSatisfied", candidatePlan.Rows.Single(row => row.LiveListing.ListingId == "extra").Reason);
    }

    [Fact]
    public void BuildCandidatePlan_AllBelowThresholdUsesRemainingMaxQuantityAndGilCapAfterPurchases()
    {
        var request = CreateRequest(quantityMode: "AllBelowThreshold", quantity: 11, maxUnitPrice: 100, maxTotalGil: 1_000);
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing("fits", quantity: 4, unitPrice: 50),
            CreateLiveListing("quantity-overage", quantity: 2, unitPrice: 50),
            CreateLiveListing("gil-overage", quantity: 1, unitPrice: 100),
        };

        var candidatePlan = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings,
            alreadyPurchasedQuantity: 6,
            alreadySpentGil: 800);

        Assert.Equal("Ready", candidatePlan.Status);
        Assert.Equal(["fits"], candidatePlan.Rows.Where(row => row.Decision == "WouldBuy").Select(row => row.LiveListing.ListingId).ToArray());
        Assert.Equal("MaxQuantityExceeded", candidatePlan.Rows.Single(row => row.LiveListing.ListingId == "quantity-overage").Reason);
        Assert.Equal("GilCapExceeded", candidatePlan.Rows.Single(row => row.LiveListing.ListingId == "gil-overage").Reason);
    }

    [Fact]
    public void BuildCandidatePlan_ReportsUnderProcurementWhenTargetQuantityCannotBeMet()
    {
        var request = CreateRequest(quantityMode: "TargetQuantity", quantity: 10, maxUnitPrice: 100);
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing("available", quantity: 4, unitPrice: 50),
        };

        var candidatePlan = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings);

        Assert.Equal("UnderProcured", candidatePlan.Status);
        Assert.Equal(4u, candidatePlan.WouldBuyQuantity);
    }

    [Theory]
    [InlineData("Exact")]
    [InlineData("UpTo")]
    public void BuildCandidatePlan_RejectsRemovedQuantityModes(string quantityMode)
    {
        var request = CreateRequest(quantityMode: quantityMode, quantity: 10, maxUnitPrice: 100);
        var plan = CreatePlan();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
                request,
                plan,
                "Gilgamesh",
                itemId: 2,
                []));

        Assert.Contains("Unknown quantity mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCandidatePlan_RespectsHqPolicyAndGilCap()
    {
        var request = CreateRequest(quantityMode: "AllBelowThreshold", quantity: 999, maxUnitPrice: 100, maxTotalGil: 500, hqPolicy: "HQOnly");
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing("nq", quantity: 1, unitPrice: 10, hq: false),
            CreateLiveListing("hq-first", quantity: 3, unitPrice: 100, hq: true),
            CreateLiveListing("hq-over-cap", quantity: 3, unitPrice: 100, hq: true),
        };

        var candidatePlan = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings);

        Assert.Equal("Ready", candidatePlan.Status);
        Assert.Equal(["hq-first"], candidatePlan.Rows.Where(row => row.Decision == "WouldBuy").Select(row => row.LiveListing.ListingId).ToArray());
        Assert.Equal("HqPolicyMismatch", candidatePlan.Rows.Single(row => row.LiveListing.ListingId == "nq").Reason);
        Assert.Equal("GilCapExceeded", candidatePlan.Rows.Single(row => row.LiveListing.ListingId == "hq-over-cap").Reason);
    }

    [Fact]
    public void BuildCandidatePlan_FailsClosedWhenCurrentWorldIsNotInPlan()
    {
        var request = CreateRequest();
        var plan = CreatePlan();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
                request,
                plan,
                "Faerie",
                itemId: 2,
                []));

        Assert.Contains("prepared plan", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionRequestView CreateRequest(
        string quantityMode = "TargetQuantity",
        uint quantity = 10,
        uint maxUnitPrice = 100,
        uint maxTotalGil = 0,
        string hqPolicy = "Either") =>
        new()
        {
            Id = "request-1",
            Status = "AcceptedInPlugin",
            TargetCharacterName = "Wei Ning",
            TargetWorld = "Gilgamesh",
            Region = "North America",
            ItemId = 2,
            ItemName = "Fire Shard",
            QuantityMode = quantityMode,
            Quantity = quantity,
            HqPolicy = hqPolicy,
            MaxUnitPrice = maxUnitPrice,
            MaxTotalGil = maxTotalGil,
            WorldMode = "Recommended",
        };

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionPlan CreatePlan() =>
        new()
        {
            RequestId = "request-1",
            Status = "Ready",
            WorldMode = "Recommended",
            ItemId = 2,
            RequestedQuantity = 10,
            PlannedQuantity = 10,
            PlannedGil = 900,
            PreparedAtUtc = DateTimeOffset.UnixEpoch,
            WorldBatches =
            [
                new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldBatch
                {
                    WorldName = "Gilgamesh",
                    PlannedQuantity = 10,
                    PlannedGil = 900,
                    Listings = [],
                },
            ],
        };

    private static MarketMafioso.MarketAcquisition.MarketBoardLiveListing CreateLiveListing(
        string listingId,
        uint quantity,
        uint unitPrice,
        bool hq = false) =>
        new()
        {
            ItemId = 2,
            WorldName = "Gilgamesh",
            ListingId = listingId,
            RetainerId = $"retainer-{listingId}",
            RetainerName = $"Retainer {listingId}",
            UnitPrice = unitPrice,
            Quantity = quantity,
            IsHq = hq,
        };
}
