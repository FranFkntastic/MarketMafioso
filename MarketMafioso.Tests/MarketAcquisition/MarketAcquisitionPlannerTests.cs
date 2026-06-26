namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionPlannerTests
{
    [Fact]
    public void BuildPlan_RecommendedModeKeepsOnlyListingsUnderThreshold()
    {
        var request = CreateRequest(quantity: 7, maxUnitPrice: 100, maxTotalGil: 700);
        var listings = new[]
        {
            CreateListing("Gilgamesh", quantity: 4, unitPrice: 80, listingId: "g-1"),
            CreateListing("Gilgamesh", quantity: 4, unitPrice: 90, listingId: "g-2"),
            CreateListing("Jenova", quantity: 99, unitPrice: 125, listingId: "j-1"),
            CreateListing("Faerie", quantity: 2, unitPrice: 75, listingId: "f-1"),
        };

        var plan = MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.BuildPlan(
            request,
            listings,
            DateTimeOffset.UnixEpoch);

        Assert.Equal("Ready", plan.Status);
        var batch = Assert.Single(plan.WorldBatches);
        Assert.Equal("Gilgamesh", batch.WorldName);
        Assert.Equal(8u, batch.PlannedQuantity);
        Assert.Equal(680u, batch.PlannedGil);
        Assert.True(batch.ExceedsRequestedQuantity);
        Assert.Equal(8u, plan.PlannedQuantity);
        Assert.Equal(680u, plan.PlannedGil);
        Assert.Equal(["g-1", "g-2"], batch.Listings.Select(x => x.ListingId).ToArray());
    }

    [Fact]
    public void BuildPlan_RespectsHqOnlyPolicyAndGilCap()
    {
        var request = CreateRequest(quantity: 10, maxUnitPrice: 100, maxTotalGil: 550, hqPolicy: "HqOnly");
        var listings = new[]
        {
            CreateListing("Gilgamesh", quantity: 4, unitPrice: 80, hq: false, listingId: "nq"),
            CreateListing("Gilgamesh", quantity: 4, unitPrice: 90, hq: true, listingId: "hq-1"),
            CreateListing("Gilgamesh", quantity: 4, unitPrice: 95, hq: true, listingId: "hq-2"),
            CreateListing("Gilgamesh", quantity: 4, unitPrice: 100, hq: true, listingId: "hq-3"),
        };

        var plan = MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.BuildPlan(
            request,
            listings,
            DateTimeOffset.UnixEpoch);

        var batch = Assert.Single(plan.WorldBatches);
        Assert.Equal(4u, batch.PlannedQuantity);
        Assert.Equal(360u, batch.PlannedGil);
        Assert.Equal(["hq-1"], batch.Listings.Select(x => x.ListingId).ToArray());
    }

    [Fact]
    public void BuildPlan_AcceptsDashboardHqPolicyAliases()
    {
        var request = CreateRequest(quantity: 10, maxUnitPrice: 100, maxTotalGil: 550, hqPolicy: "HQOnly");
        var listings = new[]
        {
            CreateListing("Gilgamesh", quantity: 4, unitPrice: 80, hq: false, listingId: "nq"),
            CreateListing("Gilgamesh", quantity: 4, unitPrice: 90, hq: true, listingId: "hq"),
        };

        var plan = MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.BuildPlan(
            request,
            listings,
            DateTimeOffset.UnixEpoch);

        var batch = Assert.Single(plan.WorldBatches);
        Assert.Equal(["hq"], batch.Listings.Select(x => x.ListingId).ToArray());
    }

    [Fact]
    public void BuildPlan_TreatsZeroGilCapAsNoTotalCap()
    {
        var request = CreateRequest(quantity: 10, maxUnitPrice: 100, maxTotalGil: 0);
        var listings = new[]
        {
            CreateListing("Gilgamesh", quantity: 4, unitPrice: 90, listingId: "first"),
            CreateListing("Gilgamesh", quantity: 4, unitPrice: 95, listingId: "second"),
            CreateListing("Gilgamesh", quantity: 4, unitPrice: 100, listingId: "third"),
        };

        var plan = MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.BuildPlan(
            request,
            listings,
            DateTimeOffset.UnixEpoch);

        var batch = Assert.Single(plan.WorldBatches);
        Assert.Equal(12u, batch.PlannedQuantity);
        Assert.Equal(1_140u, batch.PlannedGil);
        Assert.Equal(["first", "second", "third"], batch.Listings.Select(x => x.ListingId).ToArray());
    }

    [Fact]
    public void BuildPlan_CurrentWorldOnlyIgnoresOtherWorlds()
    {
        var request = CreateRequest(quantity: 10, worldMode: "CurrentWorldOnly", targetWorld: "Gilgamesh");
        var listings = new[]
        {
            CreateListing("Faerie", quantity: 10, unitPrice: 1, listingId: "wrong-world"),
            CreateListing("Gilgamesh", quantity: 3, unitPrice: 50, listingId: "current-world"),
        };

        var plan = MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.BuildPlan(
            request,
            listings,
            DateTimeOffset.UnixEpoch);

        var batch = Assert.Single(plan.WorldBatches);
        Assert.Equal("Gilgamesh", batch.WorldName);
        Assert.Equal(3u, batch.PlannedQuantity);
    }

    [Fact]
    public void BuildPlan_RejectsSelectedModeUntilSelectedWorldsAreCarried()
    {
        var request = CreateRequest(quantity: 1, worldMode: "Selected");
        var listings = new[] { CreateListing("Gilgamesh", quantity: 1, unitPrice: 1) };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.BuildPlan(
                request,
                listings,
                DateTimeOffset.UnixEpoch));
        Assert.Contains("selected worlds", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPlan_AllWorldSweepMarksPlanMode()
    {
        var request = CreateRequest(quantity: 1, worldMode: "AllWorldSweep");
        var listings = new[] { CreateListing("Gilgamesh", quantity: 1, unitPrice: 1) };

        var plan = MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.BuildPlan(
            request,
            listings,
            DateTimeOffset.UnixEpoch);

        Assert.Equal("AllWorldSweep", plan.WorldMode);
    }

    [Fact]
    public void BuildPlan_RouteOrdersCurrentWorldThenCurrentDataCenterBeforeOtherDataCenters()
    {
        var request = CreateRequest(quantity: 1_500, maxUnitPrice: 2_000, maxTotalGil: 0, targetWorld: "Siren");
        var listings = new[]
        {
            CreateListing("Rafflesia", quantity: 800, unitPrice: 1_000, listingId: "ra cheap"),
            CreateListing("Zalera", quantity: 400, unitPrice: 1_050, listingId: "za cheaper remote"),
            CreateListing("Maduin", quantity: 300, unitPrice: 1_100, listingId: "ma same dc"),
        };

        var plan = MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.BuildPlan(
            request,
            listings,
            DateTimeOffset.UnixEpoch,
            currentWorld: "Rafflesia");

        Assert.Equal(["Rafflesia", "Maduin", "Zalera"], plan.WorldBatches.Select(batch => batch.WorldName).ToArray());
    }

    [Fact]
    public void BuildPlan_FailsWhenRouteWorldDataCenterIsUnknown()
    {
        var request = CreateRequest(quantity: 10, maxUnitPrice: 2_000, maxTotalGil: 0);
        var listings = new[]
        {
            CreateListing("Notaworld", quantity: 10, unitPrice: 1, listingId: "unknown"),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.BuildPlan(
                request,
                listings,
                DateTimeOffset.UnixEpoch,
                currentWorld: "Rafflesia"));
        Assert.Contains("data center", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Notaworld", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlan_RejectsUnknownHqPolicy()
    {
        var request = CreateRequest(quantity: 1, hqPolicy: "ProbablyFine");
        var listings = new[] { CreateListing("Gilgamesh", quantity: 1, unitPrice: 1) };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.BuildPlan(
                request,
                listings,
                DateTimeOffset.UnixEpoch));
        Assert.Contains("HQ policy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlan_RejectsUnknownWorldMode()
    {
        var request = CreateRequest(quantity: 1, worldMode: "WhateverWorks");
        var listings = new[] { CreateListing("Gilgamesh", quantity: 1, unitPrice: 1) };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.BuildPlan(
                request,
                listings,
                DateTimeOffset.UnixEpoch));
        Assert.Contains("world mode", ex.Message, StringComparison.Ordinal);
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionRequestView CreateRequest(
        uint quantity,
        string worldMode = "Recommended",
        string targetWorld = "Gilgamesh",
        uint maxUnitPrice = 100,
        uint maxTotalGil = 1_000,
        string hqPolicy = "Either") =>
        new()
        {
            Id = "request-1",
            Status = "AcceptedInPlugin",
            TargetCharacterName = "Wei Ning",
            TargetWorld = targetWorld,
            Region = "North-America",
            ItemId = 2,
            ItemName = "Fire Shard",
            QuantityMode = "Exact",
            Quantity = quantity,
            HqPolicy = hqPolicy,
            MaxUnitPrice = maxUnitPrice,
            MaxTotalGil = maxTotalGil,
            WorldMode = worldMode,
        };

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionListing CreateListing(
        string worldName,
        uint quantity,
        uint unitPrice,
        bool hq = false,
        string listingId = "listing") =>
        new()
        {
            ListingId = listingId,
            WorldName = worldName,
            WorldId = 63,
            RetainerName = "Retainer",
            RetainerId = "retainer",
            Quantity = quantity,
            UnitPrice = unitPrice,
            IsHq = hq,
            LastReviewTimeUtc = DateTimeOffset.UnixEpoch,
        };
}
