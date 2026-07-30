using System.Buffers.Binary;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.Automation.MarketBoard;

public sealed class MarketBoardBrowseOperationGateTests
{
    private const uint ItemId = 5116;

    [Fact]
    public void HappyPath_RequiresAcceptedRequestHeaderExactPagesTerminalAndHistory()
    {
        var gate = BeginAccepted(ItemId);

        gate.ObserveHeader(0, 44);
        gate.ObserveHistory(ItemId, true, 20);
        gate.ObservePage(10, 0, 7, 7, Items(10));
        gate.ObservePage(20, 10, 7, 7, Items(10));
        gate.ObservePage(30, 20, 7, 7, Items(10));
        gate.ObservePage(40, 30, 7, 7, Items(10));
        gate.ObservePage(0, 40, 7, 7, Items(4));

        var result = gate.Snapshot;
        Assert.True(result.IsComplete);
        Assert.Equal(44, result.ListingCount);
        Assert.Equal(5, result.PageCount);
        Assert.Equal(5, result.ExpectedPageCount);
        Assert.Equal((byte)7, result.RequestId);
        Assert.True(result.TerminalPageObserved);
        Assert.Equal(ItemId, result.HistoryItemId);
        Assert.Equal(20, result.HistoryEntryCount);
    }

    [Fact]
    public void ZeroListingHeader_IsNotAuthoritativeUntilMatchingHistoryArrives()
    {
        var gate = BeginAccepted(ItemId);

        gate.ObserveHeader(0, 0);

        Assert.Equal(MarketBoardBrowsePhase.AwaitingHistory, gate.Snapshot.Phase);
        Assert.False(gate.Snapshot.IsComplete);

        gate.ObserveHistory(ItemId, true);

        Assert.True(gate.Snapshot.IsComplete);
        Assert.Equal(0, gate.Snapshot.ExpectedPageCount);
        Assert.True(gate.Snapshot.TerminalPageObserved);
    }

    [Fact]
    public void RequestRejection_FailsClosed()
    {
        var gate = new MarketBoardBrowseOperationGate();
        Assert.True(gate.TryBegin(MarketBoardBrowseOwner.MarketAcquisition, ItemId, out _));
        Assert.True(gate.TryClaimActivation(MarketBoardBrowseOwner.MarketAcquisition, ItemId, out _));

        gate.ObserveRequest(ItemId, false);

        AssertFailure(gate, "RequestRejected");
    }

    [Fact]
    public void Activation_CanBeClaimedOnlyOnce()
    {
        var gate = new MarketBoardBrowseOperationGate();
        Assert.True(gate.TryBegin(MarketBoardBrowseOwner.MarketAcquisition, ItemId, out _));

        Assert.True(gate.TryClaimActivation(MarketBoardBrowseOwner.MarketAcquisition, ItemId, out _));
        Assert.False(gate.TryClaimActivation(MarketBoardBrowseOwner.MarketAcquisition, ItemId, out var result));
        Assert.True(result.ActivationClaimed);
    }

    [Fact]
    public void SecondRequestDataCall_FailsOwnedAttempt()
    {
        var gate = BeginAccepted(ItemId);

        gate.ObserveRequest(ItemId, true);

        AssertFailure(gate, "RepeatedRequestData");
    }

    [Theory]
    [InlineData(0x181u)]
    [InlineData(0x70000002u)]
    [InlineData(0x70000003u)]
    public void NonzeroHeaderStatus_IsRejected(uint status)
    {
        var gate = BeginAccepted(ItemId);

        gate.ObserveHeader(status, 10);

        AssertFailure(gate, "ServerStatusRejected");
        Assert.Equal(status, gate.Snapshot.HeaderStatus);
    }

    [Fact]
    public void HeaderCountAboveFixedCache_IsRejected()
    {
        var gate = BeginAccepted(ItemId);

        gate.ObserveHeader(0, 101);

        AssertFailure(gate, "ListingCountOutOfRange");
    }

    [Fact]
    public void PageRequestIdDiscontinuity_IsRejected()
    {
        var gate = BeginAccepted(ItemId);
        gate.ObserveHeader(0, 20);
        gate.ObservePage(10, 0, 7, 7, Items(10));

        gate.ObservePage(0, 10, 8, 8, Items(10));

        AssertFailure(gate, "RequestIdDiscontinuity");
    }

    [Fact]
    public void PageMustMatchProxyCurrentRequestId()
    {
        var gate = BeginAccepted(ItemId);
        gate.ObserveHeader(0, 10);

        gate.ObservePage(0, 0, 7, 8, Items(10));

        AssertFailure(gate, "ProxyRequestIdMismatch");
    }

    [Fact]
    public void RepeatedContinuationToken_IsRejected()
    {
        var gate = BeginAccepted(ItemId);
        gate.ObserveHeader(0, 30);
        gate.ObservePage(5, 0, 7, 7, Items(10));

        gate.ObservePage(5, 10, 7, 7, Items(10));

        AssertFailure(gate, "RepeatedContinuationToken");
    }

    [Fact]
    public void EarlyTerminalPage_IsRejected()
    {
        var gate = BeginAccepted(ItemId);
        gate.ObserveHeader(0, 20);

        gate.ObservePage(0, 0, 7, 7, Items(10));

        AssertFailure(gate, "EarlyTerminalPage");
    }

    [Fact]
    public void FinalPageMustCarryTerminalContinuation()
    {
        var gate = BeginAccepted(ItemId);
        gate.ObserveHeader(0, 10);

        gate.ObservePage(3, 0, 7, 7, Items(10));

        AssertFailure(gate, "MissingTerminalPage");
    }

    [Fact]
    public void ContinuationCannotRepeatFirstPageMarker()
    {
        var gate = BeginAccepted(ItemId);
        gate.ObserveHeader(0, 20);
        gate.ObservePage(5, 0, 7, 7, Items(10));

        gate.ObservePage(0, 0, 7, 7, Items(10));

        AssertFailure(gate, "RepeatedFirstPageMarker");
    }

    [Fact]
    public void PageItemMismatch_IsRejected()
    {
        var gate = BeginAccepted(ItemId);
        gate.ObserveHeader(0, 10);

        gate.ObservePage(0, 0, 7, 7, Enumerable.Repeat(ItemId + 1, 10).ToArray());

        AssertFailure(gate, "PageItemMismatch");
    }

    [Fact]
    public void PageMustContainHeaderBoundRealListingCount()
    {
        var gate = BeginAccepted(ItemId);
        gate.ObserveHeader(0, 9);

        gate.ObservePage(0, 0, 7, 7, Items(8));

        AssertFailure(gate, "PageListingCountMismatch");
    }

    [Fact]
    public void HistoryItemMismatch_IsRejected()
    {
        var gate = BeginAccepted(ItemId);
        gate.ObserveHeader(0, 0);

        gate.ObserveHistory(ItemId + 1, true);

        AssertFailure(gate, "HistoryItemMismatch");
    }

    [Fact]
    public void InvalidHistoryShape_IsRejected()
    {
        var gate = BeginAccepted(ItemId);
        gate.ObserveHeader(0, 0);

        gate.ObserveHistory(ItemId, false);

        AssertFailure(gate, "InvalidHistoryShape");
    }

    [Fact]
    public void ProbeAttempt_BindsItemFromFirstAcceptedRequest()
    {
        var gate = new MarketBoardBrowseOperationGate();
        Assert.True(gate.TryBegin(MarketBoardBrowseOwner.RemoteAccessProbe, 0, out _));

        gate.ObserveRequest(ItemId, true);

        Assert.Equal(ItemId, gate.Snapshot.ItemId);
        Assert.True(gate.Snapshot.RequestAccepted);
        Assert.Equal(MarketBoardBrowsePhase.AwaitingHeader, gate.Snapshot.Phase);
    }

    [Fact]
    public void OverlappingOwner_IsRejected()
    {
        var gate = new MarketBoardBrowseOperationGate();
        Assert.True(gate.TryBegin(MarketBoardBrowseOwner.RemoteAccessProbe, 0, out var first));

        Assert.False(gate.TryBegin(MarketBoardBrowseOwner.MarketAcquisition, ItemId, out var blocked));
        Assert.Equal(first.OperationId, blocked.OperationId);
        Assert.Equal(MarketBoardBrowseOwner.RemoteAccessProbe, blocked.Owner);
    }

    [Fact]
    public void ActiveBrowse_TimesOutFailClosed()
    {
        var gate = new MarketBoardBrowseOperationGate();
        Assert.True(gate.TryBegin(MarketBoardBrowseOwner.MarketAcquisition, ItemId, out var started));

        gate.Advance(started.DeadlineUtc!.Value);

        AssertFailure(gate, "BrowseTimeout");
    }

    [Fact]
    public void OwnerCanAbandonExactOperationWithoutAffectingAnother()
    {
        var gate = new MarketBoardBrowseOperationGate();
        Assert.True(gate.TryBegin(MarketBoardBrowseOwner.RemoteAccessProbe, 0, out var started));

        Assert.False(
            gate.TryAbandon(
                MarketBoardBrowseOwner.MarketAcquisition,
                started.OperationId,
                "wrong owner",
                out _));
        Assert.True(gate.Snapshot.IsActive);
        Assert.True(
            gate.TryAbandon(
                MarketBoardBrowseOwner.RemoteAccessProbe,
                started.OperationId,
                "probe closed",
                out var abandoned));
        Assert.Equal("BrowseAbandoned", abandoned.FailureCode);
    }

    [Fact]
    public void BrowseContract_IsApprovedOnlyForExactMappedBuild()
    {
        var approved = GamePatchCompatibilityGate.Evaluate(
            DalamudMarketBoardBrowseObserver.PatchContractId,
            DalamudMarketBoardBrowseObserver.ApprovedGameVersion,
            DalamudMarketBoardBrowseObserver.ApprovedGameVersion);
        var drifted = GamePatchCompatibilityGate.Evaluate(
            DalamudMarketBoardBrowseObserver.PatchContractId,
            DalamudMarketBoardBrowseObserver.ApprovedGameVersion,
            "different-build");

        Assert.True(approved.IsApproved);
        Assert.False(drifted.IsApproved);
        Assert.Equal(GamePatchCompatibility.FailureCode, "UnsupportedGameBuild");
    }

    [Fact]
    public void PacketDecoder_ReadsWireItemIdsAndPageMetadataBoundary()
    {
        var packet = new byte[(10 * 0x90) + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x2C), ItemId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x90 + 0x2C), ItemId);
        packet[0x5A0] = 12;
        packet[0x5A1] = 0;
        packet[0x5A2] = 7;

        var itemIds = DalamudMarketBoardBrowseObserver.DecodePageItemIds(packet);

        Assert.Equal([ItemId, ItemId], itemIds);
    }

    [Fact]
    public void HistoryDecoder_RejectsNonzeroPriceWithZeroQuantity()
    {
        var packet = new byte[4 + (20 * 0x30)];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, ItemId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 0);

        Assert.False(
            DalamudMarketBoardBrowseObserver.TryCountStandardHistoryEntries(
                packet,
                out var entryCount));
        Assert.Equal(0, entryCount);
    }

    [Fact]
    public void HistoryDecoder_CountsRowsUntilNativeTerminator()
    {
        var packet = new byte[4 + (20 * 0x30)];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, ItemId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4 + 0x30), 200);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12 + 0x30), 1);

        Assert.True(
            DalamudMarketBoardBrowseObserver.TryCountStandardHistoryEntries(
                packet,
                out var entryCount));
        Assert.Equal(2, entryCount);
    }

    [Fact]
    public void ActivationSequence_ContainsOnlyNativeHandledClick()
    {
        Assert.Equal(
            [MarketBoardItemSearchResultActivationEvent.ListItemClick],
            MarketBoardItemSearchDriver.GetResultActivationEventSequence());
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void ResultListInteractionReadiness_AcceptsEitherNativeInteractionFlag(
        bool isItemInteractionEnabled,
        bool isItemClickEnabled,
        bool expected)
    {
        Assert.Equal(
            expected,
            MarketBoardItemSearchDriver.IsResultListInteractionReady(
                isItemInteractionEnabled,
                isItemClickEnabled));
    }

    [Fact]
    public void CompletedSameItemBrowse_ReusesVisibleListingsOnReentry()
    {
        var browse = CompletedBrowse(ItemId, listingCount: 31, pageCount: 4, requestId: 3);

        Assert.True(
            MarketBoardItemSearchDriver.ShouldReuseOwnedTerminalResult(
                browse,
                MarketBoardBrowseOwner.MarketAcquisition,
                ItemId,
                resultVisible: true,
                openResultItemId: ItemId));
    }

    [Fact]
    public void FailedSameItemBrowse_PreservesVisibleListingsForReadOnlyConsumers()
    {
        var browse = CompletedBrowse(ItemId, listingCount: 0, pageCount: 0, requestId: null) with
        {
            Phase = MarketBoardBrowsePhase.Failed,
            FailureCode = "BrowseTimeout",
        };

        Assert.True(
            MarketBoardItemSearchDriver.ShouldReuseOwnedTerminalResult(
                browse,
                MarketBoardBrowseOwner.MarketAcquisition,
                ItemId,
                resultVisible: true,
                openResultItemId: ItemId));
    }

    [Theory]
    [InlineData(false, ItemId, MarketBoardBrowseOwner.MarketAcquisition)]
    [InlineData(true, ItemId + 1, MarketBoardBrowseOwner.MarketAcquisition)]
    [InlineData(true, ItemId, MarketBoardBrowseOwner.RemoteAccessProbe)]
    public void TerminalBrowse_DoesNotReuseMissingMismatchedOrForeignListings(
        bool resultVisible,
        uint openResultItemId,
        MarketBoardBrowseOwner owner)
    {
        var browse = CompletedBrowse(ItemId, listingCount: 31, pageCount: 4, requestId: 3);

        Assert.False(
            MarketBoardItemSearchDriver.ShouldReuseOwnedTerminalResult(
                browse,
                owner,
                ItemId,
                resultVisible,
                openResultItemId));
    }

    [Fact]
    public void ListingRowsWithoutCompletedBrowseEvidence_AreUnavailable()
    {
        var result = MarketBoardListingReader.BuildReadResult(
            ItemId,
            "Siren",
            [],
            reportedListingCount: 0);

        Assert.Equal(MarketBoardListingReadState.Unavailable, result.ReadState);
        Assert.Equal("UnverifiedBrowseEvidence", result.Status);
        Assert.False(result.IsFresh);
    }

    [Fact]
    public void VerifiedZeroHeader_IsTheOnlyAuthoritativeNoListingsResult()
    {
        var browse = CompletedBrowse(ItemId, listingCount: 0, pageCount: 0, requestId: null);

        var result = MarketBoardListingReader.BuildReadResult(
            ItemId,
            "Siren",
            [],
            reportedListingCount: 0,
            listingCapacity: 100,
            browse: browse);

        Assert.Equal("NoListings", result.Status);
        Assert.Equal(MarketBoardListingReadState.FreshComplete, result.ReadState);
        Assert.True(result.IsBrowseVerified);
    }

    [Fact]
    public void Accumulator_RejectsDifferentBrowseOperation()
    {
        var accumulator = new MarketBoardListingReadAccumulator();
        var first = PartialRead("browse:1", requestId: 7);
        var second = PartialRead("browse:2", requestId: 7);
        accumulator.Merge(first);

        var exception = Assert.Throws<InvalidOperationException>(() => accumulator.Merge(second));

        Assert.Contains("different browse operations", exception.Message);
    }

    [Fact]
    public void Accumulator_RejectsRequestIdDiscontinuity()
    {
        var accumulator = new MarketBoardListingReadAccumulator();
        var first = PartialRead("browse:1", requestId: 7);
        var second = PartialRead("browse:1", requestId: 8);
        accumulator.Merge(first);

        var exception = Assert.Throws<InvalidOperationException>(() => accumulator.Merge(second));

        Assert.Contains("discontinuous", exception.Message);
    }

    private static MarketBoardBrowseOperationGate BeginAccepted(uint itemId)
    {
        var gate = new MarketBoardBrowseOperationGate();
        Assert.True(gate.TryBegin(MarketBoardBrowseOwner.MarketAcquisition, itemId, out _));
        Assert.True(gate.TryClaimActivation(MarketBoardBrowseOwner.MarketAcquisition, itemId, out _));
        gate.ObserveRequest(itemId, true);
        return gate;
    }

    private static uint[] Items(int count) => Enumerable.Repeat(ItemId, count).ToArray();

    private static void AssertFailure(MarketBoardBrowseOperationGate gate, string failureCode)
    {
        Assert.True(gate.Snapshot.IsFailed);
        Assert.Equal(failureCode, gate.Snapshot.FailureCode);
    }

    private static MarketBoardBrowseSnapshot CompletedBrowse(
        uint itemId,
        int listingCount,
        int pageCount,
        byte? requestId) =>
        new()
        {
            OperationId = "browse:complete",
            Owner = MarketBoardBrowseOwner.MarketAcquisition,
            Phase = MarketBoardBrowsePhase.Completed,
            ItemId = itemId,
            RequestObserved = true,
            RequestAccepted = true,
            HeaderObserved = true,
            HeaderStatus = 0,
            ExpectedListingCount = listingCount,
            ExpectedPageCount = pageCount,
            RequestId = requestId,
            PageCount = pageCount,
            ListingCount = listingCount,
            TerminalPageObserved = true,
            HistoryObserved = true,
            HistoryItemId = itemId,
        };

    private static MarketBoardReadResult PartialRead(string operationId, byte requestId) =>
        new()
        {
            Status = "Ready",
            ReadState = MarketBoardListingReadState.FreshPartial,
            ItemId = ItemId,
            WorldName = "Siren",
            ReportedListingCount = 2,
            ListingCapacity = 100,
            IsListingCountTruncated = true,
            CurrentRequestId = requestId,
            NextRequestId = unchecked((byte)(requestId + 1)),
            BrowseOperationId = operationId,
            BrowseHeaderStatus = 0,
            BrowseExpectedPageCount = 1,
            BrowseObservedPageCount = 1,
            BrowseHistoryItemId = ItemId,
            Listings =
            [
                new MarketBoardLiveListing
                {
                    ItemId = ItemId,
                    RawItemId = ItemId,
                    WorldName = "Siren",
                    ListingId = operationId,
                    RetainerId = "retainer",
                    UnitPrice = 100,
                    Quantity = 1,
                },
            ],
        };
}
