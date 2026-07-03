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
    public void BuildCandidatePlan_FailsClosedWhenTruncatedRowsMightHideEligibleListings()
    {
        var request = CreateRequest(quantityMode: "AllBelowThreshold", quantity: 0, maxUnitPrice: 100, maxTotalGil: 0, hqPolicy: "HQOnly");
        var plan = CreatePlan();
        var readResult = new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
        {
            Status = "Ready",
            Message = "Read truncated market board listings.",
            ItemId = 2,
            WorldName = "Gilgamesh",
            ReportedListingCount = 120,
            ListingCapacity = 100,
            IsAtListingCapacity = true,
            IsListingCountTruncated = true,
            Listings =
            [
                CreateLiveListing("nq-visible", quantity: 1, unitPrice: 50, hq: false),
            ],
        };

        var candidatePlan = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            plan,
            "Gilgamesh",
            readResult);

        Assert.Equal("VisibleCacheExhausted", candidatePlan.Status);
        Assert.True(candidatePlan.IsVisibleListingCacheTruncated);
        Assert.Equal(120, candidatePlan.ReportedListingCount);
        Assert.Contains("only 1", candidatePlan.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCandidatePlan_TreatsTruncatedAllAboveThresholdRowsAsNoSafeListings()
    {
        var request = CreateRequest(quantityMode: "AllBelowThreshold", quantity: 0, maxUnitPrice: 100, maxTotalGil: 0);
        var plan = CreatePlan();
        var readResult = new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
        {
            Status = "Ready",
            Message = "Read truncated market board listings.",
            ItemId = 2,
            WorldName = "Gilgamesh",
            ReportedListingCount = 120,
            ListingCapacity = 100,
            IsAtListingCapacity = true,
            IsListingCountTruncated = true,
            Listings =
            [
                CreateLiveListing("too-expensive-1", quantity: 99, unitPrice: 101),
                CreateLiveListing("too-expensive-2", quantity: 99, unitPrice: 150),
            ],
        };

        var candidatePlan = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            plan,
            "Gilgamesh",
            readResult);

        Assert.Equal("NoSafeListings", candidatePlan.Status);
        Assert.True(candidatePlan.IsVisibleListingCacheTruncated);
        Assert.Equal(0u, candidatePlan.WouldBuyQuantity);
        Assert.All(candidatePlan.Rows, row => Assert.Equal("AboveThreshold", row.Reason));
    }

    [Fact]
    public void BuildCandidatePlan_IgnoresNonMeaningfulJokePriceRows()
    {
        var request = CreateRequest(quantityMode: "AllBelowThreshold", quantity: 0, maxUnitPrice: 100, maxTotalGil: 0);
        var plan = CreatePlan();
        var readResult = new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
        {
            Status = "Ready",
            Message = "Read truncated market board listings.",
            ItemId = 2,
            WorldName = "Gilgamesh",
            ReportedListingCount = 120,
            ListingCapacity = 100,
            IsAtListingCapacity = true,
            IsListingCountTruncated = true,
            Listings =
            [
                CreateLiveListing("joke-1", quantity: 99, unitPrice: 999_999_999),
                CreateLiveListing("joke-2", quantity: 99, unitPrice: 99_999_999),
            ],
        };

        var candidatePlan = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            plan,
            "Gilgamesh",
            readResult);

        Assert.Equal("NoSafeListings", candidatePlan.Status);
        Assert.Empty(candidatePlan.Rows);
        Assert.True(candidatePlan.IsVisibleListingCacheTruncated);
        Assert.Equal(120, candidatePlan.ReportedListingCount);
    }

    [Fact]
    public void BuildCandidatePlan_RejectsSwitchingItemRead()
    {
        var request = CreateRequest(itemId: 5121, itemName: "Darksteel Ore", quantityMode: "AllBelowThreshold", quantity: 0, maxUnitPrice: 720);
        var plan = CreatePlan(itemId: 5121, itemName: "Darksteel Ore");
        var activeSubtask = CreateActiveSubtask(itemId: 5121, itemName: "Darksteel Ore", source: "Planned");
        var readResult = new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
        {
            Status = "ListingCacheSwitching",
            ReadState = MarketMafioso.Automation.MarketBoard.MarketBoardListingReadState.SwitchingItem,
            ItemId = 5121,
            WorldName = "Gilgamesh",
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
                request,
                plan,
                activeSubtask,
                "Gilgamesh",
                readResult));

        Assert.Contains("not fresh enough", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCandidatePlan_UsesSafeListingFromAccumulatedLaterPage()
    {
        var request = CreateRequest(quantityMode: "AllBelowThreshold", quantity: 0, maxUnitPrice: 100, maxTotalGil: 0);
        var plan = CreatePlan();
        var firstPage = new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
        {
            Status = "Ready",
            ItemId = 2,
            WorldName = "Gilgamesh",
            ReportedListingCount = 3,
            ListingCapacity = 2,
            IsAtListingCapacity = true,
            IsListingCountTruncated = true,
            CurrentRequestId = 1,
            NextRequestId = 2,
            Listings =
            [
                CreateLiveListing("first-expensive", quantity: 1, unitPrice: 500),
                CreateLiveListing("second-expensive", quantity: 1, unitPrice: 400),
            ],
        };

        var secondPage = new MarketMafioso.Automation.MarketBoard.MarketBoardReadResult
        {
            Status = "Ready",
            ItemId = 2,
            WorldName = "Gilgamesh",
            ReportedListingCount = 3,
            ListingCapacity = 2,
            CurrentRequestId = 2,
            NextRequestId = 3,
            Listings =
            [
                CreateLiveListing("later-page-cheap", quantity: 4, unitPrice: 50),
            ],
        };

        var accumulated = MarketMafioso.Automation.MarketBoard.MarketBoardAccumulatedReadResult
            .FromReadResult(firstPage)
            .Append(secondPage);

        var candidatePlan = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            plan,
            "Gilgamesh",
            accumulated);

        Assert.Equal("Ready", candidatePlan.Status);
        Assert.False(candidatePlan.IsVisibleListingCacheTruncated);
        Assert.Equal(["later-page-cheap"], candidatePlan.Rows.Where(row => row.Decision == "WouldBuy").Select(row => row.LiveListing.ListingId).ToArray());
        Assert.Equal(3, candidatePlan.ReportedListingCount);
        Assert.Equal(3, candidatePlan.ReadableListingCount);
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

    [Fact]
    public void BuildCandidatePlan_AllowsActiveOpportunisticSubtaskThatWasNotInPreparedWorldBatch()
    {
        var request = CreateRequest(itemId: 4, itemName: "Lightning Shard", quantityMode: "AllBelowThreshold", quantity: 0, maxUnitPrice: 100);
        var plan = CreatePlan();
        var activeSubtask = CreateActiveSubtask(itemId: 4, itemName: "Lightning Shard", source: "Opportunistic");
        var liveListings = new[]
        {
            CreateLiveListing("opportunistic-cheap", quantity: 8, unitPrice: 50, itemId: 4),
        };

        var candidatePlan = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            request,
            plan,
            activeSubtask,
            "Gilgamesh",
            4,
            liveListings);

        Assert.Equal("Ready", candidatePlan.Status);
        Assert.Equal(8u, candidatePlan.WouldBuyQuantity);
        Assert.Equal(["opportunistic-cheap"], candidatePlan.Rows.Where(row => row.Decision == "WouldBuy").Select(row => row.LiveListing.ListingId).ToArray());
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionRequestView CreateRequest(
        uint itemId = 2,
        string itemName = "Fire Shard",
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
            ItemId = itemId,
            ItemName = itemName,
            QuantityMode = quantityMode,
            Quantity = quantity,
            HqPolicy = hqPolicy,
            MaxUnitPrice = maxUnitPrice,
            MaxTotalGil = maxTotalGil,
            WorldMode = "Recommended",
        };

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionPlan CreatePlan(
        uint itemId = 2,
        string itemName = "Fire Shard") =>
        new()
        {
            RequestId = "request-1",
            Status = "Ready",
            WorldMode = "Recommended",
            ItemId = itemId,
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
                    ItemSubtasks =
                    [
                        CreateActiveSubtask(itemId: itemId, itemName: itemName, source: "Planned"),
                    ],
                    Listings = [],
                },
            ],
        };

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionWorldItemSubtask CreateActiveSubtask(
        uint itemId,
        string itemName,
        string source) =>
        new()
        {
            LineId = $"line-{itemId}",
            LineOrdinal = 0,
            Source = source,
            ItemId = itemId,
            ItemName = itemName,
            WorldName = "Gilgamesh",
            DataCenter = "Aether",
            QuantityMode = "AllBelowThreshold",
            RequestedQuantity = 0,
            HqPolicy = "Either",
            MaxUnitPrice = 100,
            GilCap = 0,
            PlannedQuantity = 0,
            PlannedGil = 0,
            Listings = [],
        };

    private static MarketMafioso.Automation.MarketBoard.MarketBoardLiveListing CreateLiveListing(
        string listingId,
        uint quantity,
        uint unitPrice,
        uint itemId = 2,
        bool hq = false) =>
        new()
        {
            ItemId = itemId,
            WorldName = "Gilgamesh",
            ListingId = listingId,
            RetainerId = $"retainer-{listingId}",
            RetainerName = $"Retainer {listingId}",
            UnitPrice = unitPrice,
            Quantity = quantity,
            IsHq = hq,
        };
}

