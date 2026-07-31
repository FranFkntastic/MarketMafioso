using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketDiagnostics;

namespace MarketMafioso.SpecTests.MarketDiagnostics;

public sealed class RetainerListingRefreshCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 30, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Private_lock_and_user_toggle_both_prevent_work()
    {
        var source = new FakeSource(new RetainerListingRefreshCandidate[] { new(100, "Iron Ore") });
        var runtime = new FakeRuntime();
        var config = CreateConfig();
        config.EnableMarketAcquisition = false;
        var coordinator = CreateCoordinator(config, source, runtime);

        coordinator.Tick(Start, true);
        Assert.Empty(runtime.RequestedItems);

        config.EnableMarketAcquisition = true;
        config.EnableRetainerListingRefresh = false;
        coordinator.Tick(Start.AddMinutes(1), true);
        Assert.Empty(runtime.RequestedItems);
    }

    [Fact]
    public void Explicit_capture_serializes_distinct_market_requests()
    {
        var source = new FakeSource(new RetainerListingRefreshCandidate[]
        {
            new(100, "Iron Ore"),
            new(200, "Cobalt Ore"),
        });
        var runtime = new FakeRuntime();
        var config = CreateConfig();
        var coordinator = CreateCoordinator(
            config,
            source,
            runtime,
            nextSuccessDelay: () => TimeSpan.FromSeconds(3));

        coordinator.Tick(Start, true);

        Assert.Equal([100u], runtime.RequestedItems);
        Assert.Equal([100u, 200u], config.RetainerListingRefresh.Items.Select(item => item.ItemId));

        runtime.Complete(26, 3);
        coordinator.Tick(Start.AddSeconds(1), true);
        coordinator.Tick(Start.AddSeconds(3), true);
        Assert.Equal([100u], runtime.RequestedItems);

        coordinator.Tick(Start.AddSeconds(4), true);
        Assert.Equal([100u, 200u], runtime.RequestedItems);
    }

    [Fact]
    public void Temporary_ui_ownership_defers_without_attention_then_recovers()
    {
        var source = new FakeSource(new RetainerListingRefreshCandidate[] { new(100, "Iron Ore") });
        var runtime = new FakeRuntime
        {
            DispatchFailureCode = "MarketBoardUiActive",
            DispatchFailureMessage = "The visible market board owns the cache.",
        };
        var notifications = new List<string>();
        var config = CreateConfig();
        var coordinator = CreateCoordinator(config, source, runtime, notifications);

        coordinator.Tick(Start, true);

        var deferred = Assert.Single(config.RetainerListingRefresh.Items);
        Assert.Equal(RetainerListingRefreshItemState.Deferred, deferred.State);
        Assert.False(config.RetainerListingRefresh.NeedsAttention);
        Assert.Empty(notifications);

        runtime.DispatchFailureCode = null;
        coordinator.Tick(Start.AddSeconds(14), true);
        Assert.Empty(runtime.RequestedItems);
        coordinator.Tick(Start.AddSeconds(15), true);
        Assert.Equal([100u], runtime.RequestedItems);
    }

    [Fact]
    public void Rejected_request_gets_spaced_recovery_instead_of_terminal_failure()
    {
        var source = new FakeSource(new RetainerListingRefreshCandidate[] { new(100, "Iron Ore") });
        var runtime = new FakeRuntime
        {
            DispatchFailureCode = "RequestRejected",
            DispatchFailureMessage = "RequestData returned false.",
            FailureObservedRequest = true,
        };
        var config = CreateConfig();
        var coordinator = CreateCoordinator(config, source, runtime);

        coordinator.Tick(Start, true);
        var deferred = Assert.Single(config.RetainerListingRefresh.Items);
        Assert.Equal(1, deferred.Attempts);
        Assert.Equal(RetainerListingRefreshItemState.Deferred, deferred.State);

        runtime.DispatchFailureCode = null;
        runtime.FailureObservedRequest = false;
        coordinator.Tick(Start.AddSeconds(44), true);
        Assert.Empty(runtime.RequestedItems);
        coordinator.Tick(Start.AddSeconds(45), true);
        Assert.Equal([100u], runtime.RequestedItems);
    }

    [Fact]
    public void Ambiguous_timeout_cools_down_once_before_a_fresh_request()
    {
        var source = new FakeSource(new RetainerListingRefreshCandidate[] { new(100, "Iron Ore") });
        var runtime = new FakeRuntime();
        var config = CreateConfig();
        var coordinator = CreateCoordinator(config, source, runtime);

        coordinator.Tick(Start, true);
        runtime.Fail("BrowseTimeout", "Accepted request did not complete.");
        coordinator.Tick(Start.AddSeconds(16), true);

        var reconciling = Assert.Single(config.RetainerListingRefresh.Items);
        Assert.Equal(RetainerListingRefreshItemState.NeedsReconciliation, reconciling.State);

        coordinator.Tick(Start.AddMinutes(2).AddSeconds(15), true);
        Assert.Equal([100u], runtime.RequestedItems);
        coordinator.Tick(Start.AddMinutes(2).AddSeconds(16), true);
        Assert.Equal([100u, 100u], runtime.RequestedItems);
    }

    [Fact]
    public void Protocol_contradiction_blocks_once_and_stays_visible()
    {
        var source = new FakeSource(new RetainerListingRefreshCandidate[] { new(100, "Iron Ore") });
        var runtime = new FakeRuntime
        {
            DispatchFailureCode = "RequestIdDiscontinuity",
            DispatchFailureMessage = "Page request id changed inside one browse.",
            FailureObservedRequest = true,
        };
        var notifications = new List<string>();
        var config = CreateConfig();
        var coordinator = CreateCoordinator(config, source, runtime, notifications);

        coordinator.Tick(Start, true);
        coordinator.Tick(Start.AddMinutes(1), true);

        var blocked = Assert.Single(config.RetainerListingRefresh.Items);
        Assert.Equal(RetainerListingRefreshItemState.Blocked, blocked.State);
        Assert.True(config.RetainerListingRefresh.NeedsAttention);
        Assert.Equal("Blocked", config.RetainerListingRefresh.StatusCode);
        Assert.Single(notifications);
    }

    [Fact]
    public void Missing_capture_stays_quiet_and_sends_nothing()
    {
        var source = new FakeSource("Quartermaster unavailable.");
        var runtime = new FakeRuntime();
        var notifications = new List<string>();
        var config = CreateConfig();
        var coordinator = CreateCoordinator(config, source, runtime, notifications);

        coordinator.Tick(Start, true);
        coordinator.Tick(Start.AddSeconds(5), true);
        coordinator.Tick(Start.AddMinutes(1), true);

        Assert.Empty(runtime.RequestedItems);
        Assert.False(config.RetainerListingRefresh.NeedsAttention);
        Assert.Empty(notifications);
    }

    [Fact]
    public void New_capture_id_reconciles_the_pending_item_set_exactly_once()
    {
        var source = new FakeSource(
            Snapshot("capture-1", new RetainerListingRefreshCandidate(100, "Iron Ore")),
            Snapshot("capture-2", new RetainerListingRefreshCandidate(200, "Cobalt Ore")));
        var runtime = new FakeRuntime();
        var config = CreateConfig();
        var coordinator = CreateCoordinator(config, source, runtime);

        coordinator.Tick(Start, false);

        Assert.Empty(runtime.RequestedItems);
        Assert.Equal([100u], config.RetainerListingRefresh.Items.Select(item => item.ItemId));

        coordinator.NotifyListingCaptureChanged();
        coordinator.Tick(Start.AddSeconds(1), false);

        Assert.Empty(runtime.RequestedItems);
        Assert.Equal([200u], config.RetainerListingRefresh.Items.Select(item => item.ItemId));
        Assert.Equal("capture-2", config.RetainerListingRefresh.LastObservedCaptureId);

        coordinator.NotifyListingCaptureChanged();
        coordinator.Tick(Start.AddSeconds(2), false);
        Assert.Equal([200u], config.RetainerListingRefresh.Items.Select(item => item.ItemId));
    }

    private static Configuration CreateConfig() => new()
    {
        EnableMarketAcquisition = true,
        EnableRetainerListingRefresh = true,
    };

    private static RetainerListingRefreshCoordinator CreateCoordinator(
        Configuration config,
        FakeSource source,
        FakeRuntime runtime,
        List<string>? notifications = null,
        Func<TimeSpan>? nextSuccessDelay = null) =>
        new(
            config,
            source,
            runtime,
            () => { },
            message => notifications?.Add(message),
            nextSuccessDelay: nextSuccessDelay ?? (() => TimeSpan.Zero));

    private static RetainerListingRefreshSnapshot Snapshot(
        string captureId,
        params RetainerListingRefreshCandidate[] items) =>
        new(
            items,
            captureId);

    private sealed class FakeSource : IRetainerListingRefreshSource
    {
        private readonly Queue<RetainerListingRefreshSnapshot> snapshots = [];
        private readonly string? failure;

        public FakeSource(params IReadOnlyList<RetainerListingRefreshCandidate>[] snapshots)
        {
            for (var index = 0; index < snapshots.Length; index++)
            {
                this.snapshots.Enqueue(Snapshot(
                    $"capture-{index + 1}",
                    snapshots[index].ToArray()));
            }
        }

        public FakeSource(params RetainerListingRefreshSnapshot[] snapshots)
        {
            foreach (var snapshot in snapshots)
                this.snapshots.Enqueue(snapshot);
        }

        public FakeSource(string failure)
        {
            this.failure = failure;
        }

        public bool TryRead(out RetainerListingRefreshSnapshot? snapshot, out string error)
        {
            if (failure is not null)
            {
                snapshot = null;
                error = failure;
                return false;
            }

            snapshot = snapshots.Count > 1
                ? snapshots.Dequeue()
                : snapshots.TryPeek(out var current)
                    ? current
                    : Snapshot("capture-empty", []);
            error = string.Empty;
            return true;
        }
    }

    private sealed class FakeRuntime : IHeadlessMarketBoardBrowseRuntime
    {
        private int operationSequence;

        public bool IsAvailable { get; set; } = true;
        public string AvailabilityMessage => IsAvailable ? "Available" : "Unavailable";
        public MarketBoardBrowseSnapshot Snapshot { get; private set; } = MarketBoardBrowseSnapshot.Idle;
        public List<uint> RequestedItems { get; } = [];
        public string? DispatchFailureCode { get; set; }
        public string? DispatchFailureMessage { get; set; }
        public bool FailureObservedRequest { get; set; }

        public bool TryRequestExactItem(
            MarketBoardBrowseOwner owner,
            uint itemId,
            out MarketBoardBrowseSnapshot snapshot)
        {
            if (DispatchFailureCode is not null)
            {
                Snapshot = new MarketBoardBrowseSnapshot
                {
                    OperationId = $"fake:{++operationSequence}",
                    Owner = owner,
                    ItemId = itemId,
                    Phase = MarketBoardBrowsePhase.Failed,
                    ActivationClaimed = FailureObservedRequest,
                    RequestObserved = FailureObservedRequest,
                    RequestAccepted = false,
                    FailureCode = DispatchFailureCode,
                    Message = DispatchFailureMessage ?? DispatchFailureCode,
                };
                snapshot = Snapshot;
                return false;
            }

            RequestedItems.Add(itemId);
            Snapshot = new MarketBoardBrowseSnapshot
            {
                OperationId = $"fake:{++operationSequence}",
                Owner = owner,
                ItemId = itemId,
                Phase = MarketBoardBrowsePhase.AwaitingHeader,
                ActivationClaimed = true,
                RequestObserved = true,
                RequestAccepted = true,
                Message = "Awaiting evidence.",
            };
            snapshot = Snapshot;
            return true;
        }

        public void Complete(int listingCount, int pageCount)
        {
            Snapshot = Snapshot with
            {
                Phase = MarketBoardBrowsePhase.Completed,
                HeaderObserved = true,
                HeaderStatus = 0,
                ExpectedListingCount = listingCount,
                ExpectedPageCount = pageCount,
                PageCount = pageCount,
                ListingCount = listingCount,
                TerminalPageObserved = true,
                HistoryObserved = true,
                HistoryItemId = Snapshot.ItemId,
                Message = "Complete.",
            };
        }

        public void Fail(string code, string message)
        {
            Snapshot = Snapshot with
            {
                Phase = MarketBoardBrowsePhase.Failed,
                FailureCode = code,
                Message = message,
            };
        }

        public bool TryBegin(
            MarketBoardBrowseOwner owner,
            uint itemId,
            out MarketBoardBrowseSnapshot snapshot)
        {
            snapshot = Snapshot;
            return false;
        }

        public bool TryClaimActivation(
            MarketBoardBrowseOwner owner,
            uint itemId,
            out MarketBoardBrowseSnapshot snapshot)
        {
            snapshot = Snapshot;
            return false;
        }

        public bool TryAbandon(
            MarketBoardBrowseOwner owner,
            string operationId,
            string reason,
            out MarketBoardBrowseSnapshot snapshot)
        {
            snapshot = Snapshot;
            return false;
        }
    }
}
