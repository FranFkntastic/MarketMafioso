using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.SpecTests.Automation.MarketBoard;

public sealed class MarketBoardBrowseRetirementTests
{
    private const MarketBoardBrowseOwner Owner = MarketBoardBrowseOwner.MarketAcquisition;

    [Fact]
    public void ClosedPrefix_AdmitsNextItemAndIgnoresOldPagesBeforeAndAfterNewHeader()
    {
        var gate = Prefix();
        var old = gate.Snapshot;
        Assert.False(gate.TryRetireAfterResultClosed(Owner, old.OperationId, false, out _));
        Assert.False(gate.TryBegin(Owner, 5067, out _));
        Assert.True(gate.TryRetireAfterResultClosed(Owner, old.OperationId, true, out var retired));
        Assert.False(retired.IsComplete);
        Assert.False(retired.IsActive);
        Assert.Equal(10, retired.ListingCount);
        Assert.Equal(30, retired.ExpectedListingCount);

        Assert.True(gate.TryBegin(Owner, 5067, out var next));
        Assert.True(gate.IsRetiredPage(7));
        gate.ObservePage(20, 10, 7, 7, Items(5059));
        gate.ObserveHistory(5059, true);
        Assert.Equal(next, gate.Snapshot);
        Assert.True(gate.TryClaimActivation(Owner, 5067, out _));
        gate.ObserveRequest(5067, true);
        gate.ObserveHeader(0, 10, 8);
        gate.ObservePage(0, 20, 7, 8, Items(5059));
        Assert.Equal(0, gate.Snapshot.PageCount);
        gate.ObservePage(0, 0, 8, 8, Items(5067));
        gate.ObserveHistory(5067, true);
        Assert.True(gate.Snapshot.IsComplete);
        Assert.Equal(next.OperationId, gate.Snapshot.OperationId);
    }

    [Fact]
    public void Retirement_RequiresMatchingOwnerGenerationAndAnAcceptedHeader()
    {
        var gate = Prefix();
        var op = gate.Snapshot.OperationId;
        Assert.False(gate.TryRetireAfterResultClosed(MarketBoardBrowseOwner.RetainerListingRefresh, op, true, out _));
        Assert.False(gate.TryRetireAfterResultClosed(Owner, "previous", true, out _));
        Assert.True(gate.Snapshot.IsActive);
        var armed = new MarketBoardBrowseOperationGate();
        Assert.True(armed.TryBegin(Owner, 5059, out var beforeHeader));
        Assert.False(armed.TryRetireAfterResultClosed(Owner, beforeHeader.OperationId, true, out _));
    }

    [Fact]
    public void SameItemRetry_CannotMisattributeUnfinishedHistoryAndWaitIsBounded()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = Prefix(() => now);
        var old = gate.Snapshot;
        Assert.True(gate.TryRetireAfterResultClosed(Owner, old.OperationId, true, out _));
        Assert.False(gate.TryBegin(Owner, 5059, out var pending));
        Assert.False(pending.IsFailed);
        now += TimeSpan.FromSeconds(16);
        Assert.False(gate.TryBegin(Owner, 5059, out var failed));
        Assert.Equal("RetiredHistoryUnresolved", failed.FailureCode);
        gate.ObserveHistory(5059, true);
        Assert.True(gate.TryBegin(Owner, 5059, out var retry));
        Assert.NotEqual(old.OperationId, retry.OperationId);
        Assert.False(retry.HistoryObserved);
    }

    [Fact]
    public void NewNativeHeader_ReleasesRetiredByteFenceIncludingManualBrowse()
    {
        var gate = Prefix();
        Assert.True(gate.TryRetireAfterResultClosed(Owner, gate.Snapshot.OperationId, true, out _));
        Assert.True(gate.IsRetiredPage(7));
        gate.ObserveHeader(0, 10, 7);
        Assert.False(gate.IsRetiredPage(7));
    }

    [Fact]
    public void FailedNativeClose_BlocksNewBrowseUntilClosureActuallyOccurs()
    {
        var gate = Prefix();
        var op = gate.Snapshot.OperationId;
        gate.ObserveResultCloseTimeout(op);
        Assert.False(gate.TryBegin(Owner, 5067, out var failed));
        Assert.Equal("BrowseCloseFailed", failed.FailureCode);
        Assert.True(gate.TryRetireAfterResultClosed(Owner, op, true, out _));
        Assert.True(gate.TryBegin(Owner, 5067, out _));
    }

    private static MarketBoardBrowseOperationGate Prefix(Func<DateTimeOffset>? clock = null)
    {
        var gate = new MarketBoardBrowseOperationGate(clock);
        Assert.True(gate.TryBegin(Owner, 5059, out _));
        Assert.True(gate.TryClaimActivation(Owner, 5059, out _));
        gate.ObserveRequest(5059, true);
        gate.ObserveHeader(0, 30, 7);
        gate.ObservePage(10, 0, 7, 7, Items(5059));
        return gate;
    }

    private static uint[] Items(uint item) => Enumerable.Repeat(item, 10).ToArray();
}
