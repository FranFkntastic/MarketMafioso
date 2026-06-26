namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionLiveDryRunPlannerTests
{
    [Fact]
    public void BuildDryRun_TargetQuantityUsesLowestConfirmedLivePriceAndAllowsWholeStackOverage()
    {
        var request = CreateRequest(quantityMode: "TargetQuantity", quantity: 10, maxUnitPrice: 100);
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing("planned-expensive", quantity: 10, unitPrice: 90),
            CreateLiveListing("new-cheap", quantity: 12, unitPrice: 50),
            CreateLiveListing("too-expensive", quantity: 99, unitPrice: 101),
        };

        var dryRun = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRunPlanner.BuildDryRun(
            request,
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings);

        Assert.Equal("Ready", dryRun.Status);
        Assert.Equal(12u, dryRun.WouldBuyQuantity);
        Assert.Equal(600u, dryRun.WouldSpendGil);
        Assert.Equal(["new-cheap"], dryRun.Rows.Where(row => row.Decision == "WouldBuy").Select(row => row.LiveListing.ListingId).ToArray());
        Assert.Equal("TargetSatisfied", dryRun.Rows.Single(row => row.LiveListing.ListingId == "planned-expensive").Reason);
        Assert.Equal("AboveThreshold", dryRun.Rows.Single(row => row.LiveListing.ListingId == "too-expensive").Reason);
    }

    [Fact]
    public void BuildDryRun_AllBelowThresholdIncludesEverySafeVisibleListing()
    {
        var request = CreateRequest(quantityMode: "AllBelowThreshold", quantity: 0, maxUnitPrice: 100, maxTotalGil: 0);
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing("planned", quantity: 3, unitPrice: 80),
            CreateLiveListing("new-cheap", quantity: 2, unitPrice: 70),
            CreateLiveListing("too-expensive", quantity: 1, unitPrice: 101),
        };

        var dryRun = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRunPlanner.BuildDryRun(
            request,
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings);

        Assert.Equal("Ready", dryRun.Status);
        Assert.Equal(5u, dryRun.WouldBuyQuantity);
        Assert.Equal(380u, dryRun.WouldSpendGil);
        Assert.Equal(["new-cheap", "planned"], dryRun.Rows.Where(row => row.Decision == "WouldBuy").Select(row => row.LiveListing.ListingId).ToArray());
    }

    [Fact]
    public void BuildDryRun_AllBelowThresholdRespectsMaxQuantityWhenProvided()
    {
        var request = CreateRequest(quantityMode: "AllBelowThreshold", quantity: 5, maxUnitPrice: 100, maxTotalGil: 0);
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing("first", quantity: 3, unitPrice: 70),
            CreateLiveListing("would-exceed-cap", quantity: 3, unitPrice: 80),
            CreateLiveListing("second", quantity: 2, unitPrice: 90),
        };

        var dryRun = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRunPlanner.BuildDryRun(
            request,
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings);

        Assert.Equal("Ready", dryRun.Status);
        Assert.Equal(5u, dryRun.WouldBuyQuantity);
        Assert.Equal(["first", "second"], dryRun.Rows.Where(row => row.Decision == "WouldBuy").Select(row => row.LiveListing.ListingId).ToArray());
        Assert.Equal("MaxQuantityExceeded", dryRun.Rows.Single(row => row.LiveListing.ListingId == "would-exceed-cap").Reason);
    }

    [Fact]
    public void BuildDryRun_ReportsUnderProcurementWhenTargetQuantityCannotBeMet()
    {
        var request = CreateRequest(quantityMode: "TargetQuantity", quantity: 10, maxUnitPrice: 100);
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing("available", quantity: 4, unitPrice: 50),
        };

        var dryRun = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRunPlanner.BuildDryRun(
            request,
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings);

        Assert.Equal("UnderProcured", dryRun.Status);
        Assert.Equal(4u, dryRun.WouldBuyQuantity);
    }

    [Theory]
    [InlineData("Exact")]
    [InlineData("UpTo")]
    public void BuildDryRun_RejectsRemovedQuantityModes(string quantityMode)
    {
        var request = CreateRequest(quantityMode: quantityMode, quantity: 10, maxUnitPrice: 100);
        var plan = CreatePlan();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRunPlanner.BuildDryRun(
                request,
                plan,
                "Gilgamesh",
                itemId: 2,
                []));

        Assert.Contains("Unknown quantity mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildDryRun_RespectsHqPolicyAndGilCap()
    {
        var request = CreateRequest(quantityMode: "AllBelowThreshold", quantity: 999, maxUnitPrice: 100, maxTotalGil: 500, hqPolicy: "HQOnly");
        var plan = CreatePlan();
        var liveListings = new[]
        {
            CreateLiveListing("nq", quantity: 1, unitPrice: 10, hq: false),
            CreateLiveListing("hq-first", quantity: 3, unitPrice: 100, hq: true),
            CreateLiveListing("hq-over-cap", quantity: 3, unitPrice: 100, hq: true),
        };

        var dryRun = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRunPlanner.BuildDryRun(
            request,
            plan,
            "Gilgamesh",
            itemId: 2,
            liveListings);

        Assert.Equal("Ready", dryRun.Status);
        Assert.Equal(["hq-first"], dryRun.Rows.Where(row => row.Decision == "WouldBuy").Select(row => row.LiveListing.ListingId).ToArray());
        Assert.Equal("HqPolicyMismatch", dryRun.Rows.Single(row => row.LiveListing.ListingId == "nq").Reason);
        Assert.Equal("GilCapExceeded", dryRun.Rows.Single(row => row.LiveListing.ListingId == "hq-over-cap").Reason);
    }

    [Fact]
    public void BuildDryRun_FailsClosedWhenCurrentWorldIsNotInPlan()
    {
        var request = CreateRequest();
        var plan = CreatePlan();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRunPlanner.BuildDryRun(
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
