using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionSortedPrefixPlannerTests
{
    [Fact]
    public void EarlyDeparture_DrainsOwnedResponseBeforeNextItemCanBrowse()
    {
        var fixture = CreateFixture();
        var gate = new MarketBoardBrowseOperationGate();
        Assert.True(gate.TryBegin(MarketBoardBrowseOwner.MarketAcquisition, 5059, out _));
        Assert.True(gate.TryClaimActivation(MarketBoardBrowseOwner.MarketAcquisition, 5059, out _));
        gate.ObserveRequest(5059, true);
        gate.ObserveHeader(0, 20);
        gate.ObservePage(10, 0, 7, 7, Enumerable.Repeat(5059u, 10).ToArray());
        var oldBrowse = gate.Snapshot;
        var oldRead = PrefixRead(Enumerable.Repeat(120u, 10).ToArray()) with
        {
            BrowseOperationId = oldBrowse.OperationId,
        };
        Assert.True(MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.TryBuildConclusiveSortedPrefixPlan(
            fixture.Request, fixture.Plan, fixture.Subtask, "Siren", oldRead, 0, 0, oldBrowse, out _));

        var nextRequest = fixture.Request with { ItemId = 5067, ItemName = "Rose Gold Ingot" };
        var nextSubtask = fixture.Subtask with { ItemId = 5067, ItemName = "Rose Gold Ingot", LineId = "line:next" };
        var nextPlan = fixture.Plan with
        {
            WorldBatches = [fixture.Plan.WorldBatches.Single() with { ItemSubtasks = [nextSubtask] }],
        };
        Assert.False(gate.TryBegin(MarketBoardBrowseOwner.MarketAcquisition, 5067, out var busy));
        Assert.False(MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.TryBuildConclusiveSortedPrefixPlan(
            nextRequest, nextPlan, nextSubtask, "Siren", oldRead, 0, 0, busy, out _));

        // Late pages and history still belong to the previous item, not the next one.
        gate.ObservePage(0, 10, 7, 7, Enumerable.Repeat(5059u, 10).ToArray());
        Assert.False(gate.TryBegin(MarketBoardBrowseOwner.MarketAcquisition, 5067, out _));
        gate.ObserveHistory(5059, true);
        Assert.True(gate.Snapshot.IsComplete);
        Assert.True(gate.TryBegin(MarketBoardBrowseOwner.MarketAcquisition, 5067, out var nextBrowse));
        Assert.NotEqual(oldBrowse.OperationId, nextBrowse.OperationId);
        Assert.True(gate.TryClaimActivation(MarketBoardBrowseOwner.MarketAcquisition, 5067, out _));
        gate.ObserveRequest(5067, true);
        gate.ObserveHeader(0, 20);
        gate.ObservePage(10, 0, 8, 8, Enumerable.Repeat(5067u, 10).ToArray());
        var nextRead = oldRead with
        {
            ItemId = 5067,
            BrowseOperationId = nextBrowse.OperationId,
            Listings = oldRead.Listings.Select(row => row with { ItemId = 5067, RawItemId = 5067 }).ToArray(),
        };
        Assert.True(MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.TryBuildConclusiveSortedPrefixPlan(
            nextRequest, nextPlan, nextSubtask, "Siren", nextRead, 0, 0, gate.Snapshot, out _));
    }

    [Theory]
    [InlineData("item")]
    [InlineData("world")]
    [InlineData("generation")]
    [InlineData("owner")]
    [InlineData("terminal")]
    [InlineData("missing")]
    public void UnrelatedPrefix_IsPendingRatherThanAPlanOrException(string mismatch)
    {
        var fixture = CreateFixture();
        var read = PrefixRead(120, 130);
        MarketBoardBrowseSnapshot? browse = Browse();
        if (mismatch == "item") read = read with { ItemId = 5067 };
        if (mismatch == "world") read = read with { WorldName = "Fenrir" };
        if (mismatch == "generation") read = read with { BrowseOperationId = "browse:previous" };
        if (mismatch == "owner") browse = browse with { Owner = MarketBoardBrowseOwner.MarketListingAcquisition };
        if (mismatch == "terminal") browse = browse with { Phase = MarketBoardBrowsePhase.Completed };
        if (mismatch == "missing") browse = null;
        Assert.False(MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.TryBuildConclusiveSortedPrefixPlan(
            fixture.Request, fixture.Plan, fixture.Subtask, "Siren", read, 0, 0, browse, out _));
    }

    [Fact]
    public void PrefixBeyondPriceCeiling_IsConclusiveWithoutRemainingPages()
    {
        var fixture = CreateFixture();
        var read = PrefixRead(120, 130);

        var conclusive = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.TryBuildConclusiveSortedPrefixPlan(
            fixture.Request,
            fixture.Plan,
            fixture.Subtask,
            "Siren",
            read,
            alreadyPurchasedQuantity: 0,
            alreadySpentGil: 0,
            Browse(),
            out var candidatePlan);

        Assert.True(conclusive);
        Assert.Equal(MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidateStatuses.NoSafeListings, candidatePlan.Status);
        Assert.Equal(0u, candidatePlan.WouldBuyQuantity);
        Assert.Contains("remaining pages", candidatePlan.Message);
    }

    [Fact]
    public void PrefixContainingEligibleListing_WaitsForCompleteBrowse()
    {
        var fixture = CreateFixture();

        var conclusive = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.TryBuildConclusiveSortedPrefixPlan(
            fixture.Request,
            fixture.Plan,
            fixture.Subtask,
            "Siren",
            PrefixRead(80, 120),
            0,
            0,
            Browse(),
            out _);

        Assert.False(conclusive);
    }

    [Fact]
    public void PrefixThatHasNotCrossedPriceCeiling_WaitsForCompleteBrowse()
    {
        var fixture = CreateFixture();

        var conclusive = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.TryBuildConclusiveSortedPrefixPlan(
            fixture.Request,
            fixture.Plan,
            fixture.Subtask,
            "Siren",
            PrefixRead(80, 90),
            0,
            0,
            Browse(),
            out _);

        Assert.False(conclusive);
    }

    [Fact]
    public void NonMonotonicPrefix_RefusesEarlyDecision()
    {
        var fixture = CreateFixture();

        var conclusive = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.TryBuildConclusiveSortedPrefixPlan(
            fixture.Request,
            fixture.Plan,
            fixture.Subtask,
            "Siren",
            PrefixRead(130, 120),
            0,
            0,
            Browse(),
            out _);

        Assert.False(conclusive);
    }

    [Fact]
    public void ExactPreparedListingMatch_EnrichesLiveRetainerNameForDiagnostics()
    {
        var fixture = CreateFixture();
        var planned = new MarketMafioso.MarketAcquisition.MarketAcquisitionPlannedListing
        {
            LineId = fixture.Subtask.LineId,
            ItemId = fixture.Subtask.ItemId,
            ListingId = "listing:known",
            RetainerId = "retainer:known",
            RetainerName = "Bulk Seller",
            Quantity = 99,
            UnitPrice = 80,
            TotalGil = 7_920,
        };
        var subtask = fixture.Subtask with { Listings = [planned] };
        var plan = fixture.Plan with
        {
            WorldBatches =
            [
                fixture.Plan.WorldBatches.Single() with { ItemSubtasks = [subtask] },
            ],
        };
        var read = PrefixRead(80) with
        {
            ReadState = MarketBoardListingReadState.FreshComplete,
            Status = "Ready",
            ReportedListingCount = 1,
            Listings =
            [
                PrefixRead(80).Listings.Single() with
                {
                    ListingId = planned.ListingId,
                    RetainerId = planned.RetainerId,
                    Quantity = planned.Quantity,
                },
            ],
        };

        var candidatePlan = MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            fixture.Request,
            plan,
            subtask,
            "Siren",
            read);

        var listing = Assert.Single(candidatePlan.Rows).LiveListing;
        Assert.NotNull(listing);
        Assert.Equal("Bulk Seller", listing.RetainerName);
        Assert.Equal("PreparedListingExactIdentityMatch", listing.RetainerNameSource);
    }

    private static (MarketMafioso.MarketAcquisition.MarketAcquisitionRequestView Request,
        MarketMafioso.MarketAcquisition.MarketAcquisitionPlan Plan,
        MarketMafioso.MarketAcquisition.MarketAcquisitionWorldItemSubtask Subtask) CreateFixture()
    {
        var request = new MarketMafioso.MarketAcquisition.MarketAcquisitionRequestView
        {
            Id = "request:prefix",
            Status = "AcceptedInPlugin",
            TargetCharacterName = "Wei Ning",
            TargetWorld = "Siren",
            Region = "North-America",
            ItemId = 5059,
            ItemName = "Cobalt Ingot",
            QuantityMode = "TargetQuantity",
            Quantity = 10,
            HqPolicy = "Either",
            MaxUnitPrice = 100,
            MaxTotalGil = 10_000,
            WorldMode = "Recommended",
        };
        var subtask = new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldItemSubtask
        {
            LineId = "line:prefix",
            ItemId = 5059,
            ItemName = "Cobalt Ingot",
            WorldName = "Siren",
            DataCenter = "Aether",
            QuantityMode = "TargetQuantity",
            RequestedQuantity = 10,
            HqPolicy = "Either",
            MaxUnitPrice = 100,
            GilCap = 10_000,
        };
        var plan = new MarketMafioso.MarketAcquisition.MarketAcquisitionPlan
        {
            RequestId = request.Id,
            Status = "Ready",
            ItemId = request.ItemId,
            RequestedQuantity = request.Quantity,
            WorldBatches =
            [
                new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldBatch
                {
                    WorldName = "Siren",
                    DataCenter = "Aether",
                    ItemSubtasks = [subtask],
                },
            ],
        };
        return (request, plan, subtask);
    }

    private static MarketBoardBrowseSnapshot Browse() => new()
    {
        OperationId = "browse:prefix",
        Owner = MarketBoardBrowseOwner.MarketAcquisition,
        ItemId = 5059,
        Phase = MarketBoardBrowsePhase.AwaitingPagesAndHistory,
    };

    private static MarketBoardReadResult PrefixRead(params uint[] prices) =>
        new()
        {
            Status = "VerifiedListingPrefix",
            ReadState = MarketBoardListingReadState.FreshPartial,
            ItemId = 5059,
            WorldName = "Siren",
            ReportedListingCount = 20,
            ListingCapacity = 100,
            IsListingCountTruncated = true,
            BrowseOperationId = "browse:prefix",
            BrowseHeaderStatus = 0,
            BrowseExpectedPageCount = 2,
            BrowseObservedPageCount = 1,
            Listings = prices.Select((price, index) => new MarketBoardLiveListing
            {
                ItemId = 5059,
                RawItemId = 5059,
                WorldName = "Siren",
                ListingId = $"listing:{index}",
                RetainerId = $"retainer:{index}",
                UnitPrice = price,
                Quantity = 1,
            }).ToArray(),
        };
}
