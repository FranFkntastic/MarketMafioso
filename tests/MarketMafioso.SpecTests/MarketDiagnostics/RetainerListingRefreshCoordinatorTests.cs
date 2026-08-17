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
    public void Force_retry_requeues_only_selected_block_and_refuses_stale_replay()
    {
        var config = CreateConfig();
        config.RetainerListingRefresh.NeedsAttention = true;
        config.RetainerListingRefresh.Items =
        [
            new PersistedRetainerListingRefreshItem
            {
                ItemId = 100,
                ItemName = "Iron Ore",
                State = RetainerListingRefreshItemState.Blocked,
                Attempts = 3,
                RateLimitFailures = 2,
                LastCode = "RequestIdDiscontinuity",
                LastMessage = "Page request id changed inside one browse.",
                AttentionNotified = true,
            },
            new PersistedRetainerListingRefreshItem
            {
                ItemId = 200,
                ItemName = "Cobalt Ore",
                State = RetainerListingRefreshItemState.Blocked,
                LastCode = "HistoryItemMismatch",
                LastMessage = "Sale history named another item.",
                AttentionNotified = true,
            },
        ];
        var runtime = new FakeRuntime();
        var persistCalls = 0;
        var coordinator = new RetainerListingRefreshCoordinator(
            config,
            new FakeSource("Shared listing evidence is unavailable."),
            runtime,
            () => persistCalls++,
            _ => { },
            nextSuccessDelay: () => TimeSpan.Zero);

        Assert.True(coordinator.ForceRetry(100, Start));

        var retried = Assert.Single(config.RetainerListingRefresh.Items, item => item.ItemId == 100);
        Assert.Equal(RetainerListingRefreshItemState.Deferred, retried.State);
        Assert.Equal(Start.UtcDateTime, retried.NextAttemptAtUtc);
        Assert.Equal(3, retried.Attempts);
        Assert.Equal(2, retried.RateLimitFailures);
        Assert.Equal("RequestIdDiscontinuity", retried.LastCode);
        Assert.Equal("Page request id changed inside one browse.", retried.LastMessage);
        Assert.False(retried.AttentionNotified);
        Assert.Equal(RetainerListingRefreshItemState.Blocked, config.RetainerListingRefresh.Items.Single(item => item.ItemId == 200).State);
        Assert.True(config.RetainerListingRefresh.NeedsAttention);
        Assert.Equal("Blocked", config.RetainerListingRefresh.StatusCode);
        Assert.Equal(1, persistCalls);

        Assert.False(coordinator.ForceRetry(100, Start.AddSeconds(1)));
        Assert.Equal(1, persistCalls);

        Assert.True(coordinator.ForceRetry(200, Start.AddSeconds(2)));
        Assert.False(config.RetainerListingRefresh.NeedsAttention);
        Assert.Equal("Queued", config.RetainerListingRefresh.StatusCode);
        Assert.Equal(2, persistCalls);

        coordinator.Tick(Start, true);
        Assert.Equal([100u], runtime.RequestedItems);
    }

    [Fact]
    public void Rate_limit_retries_same_item_with_shared_cmb_backoff()
    {
        var source = new FakeSource(new RetainerListingRefreshCandidate[]
        {
            new(100, "Iron Ore"),
            new(200, "Cobalt Ore"),
        });
        var runtime = new FakeRuntime();
        var config = CreateConfig();
        var coordinator = CreateCoordinator(config, source, runtime);

        coordinator.Tick(Start, true);
        runtime.Fail("MarketBoardRateLimited", "The market board asked MMF to wait.");
        coordinator.Tick(Start.AddSeconds(1), true);

        var limited = Assert.Single(config.RetainerListingRefresh.Items, item => item.ItemId == 100);
        Assert.Equal(RetainerListingRefreshItemState.Deferred, limited.State);
        Assert.Equal(1, limited.RateLimitFailures);
        Assert.Equal(Start.AddSeconds(6).UtcDateTime, limited.NextAttemptAtUtc);
        Assert.False(config.RetainerListingRefresh.NeedsAttention);

        coordinator.Tick(Start.AddSeconds(5), true);
        Assert.Equal([100u], runtime.RequestedItems);
        coordinator.Tick(Start.AddSeconds(6), true);
        Assert.Equal([100u, 100u], runtime.RequestedItems);

        runtime.Fail("MarketBoardRateLimited", "The market board asked MMF to wait.");
        coordinator.Tick(Start.AddSeconds(7), true);
        Assert.Equal(Start.AddSeconds(22).UtcDateTime, limited.NextAttemptAtUtc);
        coordinator.Tick(Start.AddSeconds(21), true);
        Assert.Equal([100u, 100u], runtime.RequestedItems);
        coordinator.Tick(Start.AddSeconds(22), true);
        Assert.Equal([100u, 100u, 100u], runtime.RequestedItems);

        runtime.Fail("MarketBoardRateLimited", "The market board asked MMF to wait.");
        coordinator.Tick(Start.AddSeconds(23), true);
        Assert.Equal(Start.AddSeconds(53).UtcDateTime, limited.NextAttemptAtUtc);
        Assert.DoesNotContain(200u, runtime.RequestedItems);

        coordinator.Tick(Start.AddSeconds(53), true);
        Assert.Equal([100u, 100u, 100u, 100u], runtime.RequestedItems);
        runtime.Fail("MarketBoardRateLimited", "The market board asked MMF to wait.");
        coordinator.Tick(Start.AddSeconds(54), true);
        Assert.Equal(RetainerListingRefreshItemState.Deferred, limited.State);
        Assert.Equal(4, limited.RateLimitFailures);
        Assert.Equal(Start.AddSeconds(84).UtcDateTime, limited.NextAttemptAtUtc);
        Assert.False(config.RetainerListingRefresh.NeedsAttention);
    }

    [Fact]
    public void Expired_market_session_blocks_item_without_another_timed_retry()
    {
        var source = new FakeSource(new RetainerListingRefreshCandidate[] { new(100, "Iron Ore") });
        var runtime = new FakeRuntime();
        var config = CreateConfig();
        var coordinator = CreateCoordinator(config, source, runtime);

        coordinator.Tick(Start, true);
        runtime.Fail("MarketBoardSessionExpired", "Relog before resuming market searches.");
        coordinator.Tick(Start.AddSeconds(1), true);

        var blocked = Assert.Single(config.RetainerListingRefresh.Items);
        Assert.Equal(RetainerListingRefreshItemState.Blocked, blocked.State);
        Assert.Equal("MarketBoardSessionExpired", blocked.LastCode);
        Assert.Null(blocked.NextAttemptAtUtc);

        coordinator.Tick(Start.AddHours(1), true);
        Assert.Equal([100u], runtime.RequestedItems);
    }

    [Fact]
    public void Legacy_rate_limit_block_is_recovered_automatically()
    {
        var config = CreateConfig();
        config.RetainerListingRefresh.NeedsAttention = true;
        config.RetainerListingRefresh.Items =
        [
            new PersistedRetainerListingRefreshItem
            {
                ItemId = 100,
                ItemName = "Iron Ore",
                State = RetainerListingRefreshItemState.Blocked,
                Attempts = 2,
                LastCode = "ServerStatusRejected",
                LastMessage = "The market-board server rejected the browse with status 0x70000002.",
                AttentionNotified = true,
            },
        ];

        _ = CreateCoordinator(config, new FakeSource("Quartermaster unavailable."), new FakeRuntime());

        var recovered = Assert.Single(config.RetainerListingRefresh.Items);
        Assert.Equal(RetainerListingRefreshItemState.Deferred, recovered.State);
        Assert.Equal(2, recovered.RateLimitFailures);
        Assert.NotNull(recovered.NextAttemptAtUtc);
        Assert.Equal("RecoveredRateLimit", recovered.LastCode);
        Assert.False(config.RetainerListingRefresh.NeedsAttention);
        Assert.False(recovered.AttentionNotified);
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
    public void Event_driven_source_is_not_polled_again_after_a_failed_read()
    {
        var config = CreateConfig();
        var source = new FakeSource("Shared database unavailable.")
        {
            RetryOnReadFailure = false,
            SurfaceReadFailure = true,
        };
        var coordinator = CreateCoordinator(config, source, new FakeRuntime());

        coordinator.Tick(Start, true);
        coordinator.Tick(Start.AddSeconds(5), true);
        coordinator.Tick(Start.AddHours(1), true);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal("ListingEvidenceUnavailable", config.RetainerListingRefresh.StatusCode);

        coordinator.NotifyListingCaptureChanged();
        coordinator.Tick(Start.AddHours(1).AddSeconds(1), true);
        Assert.Equal(2, source.ReadCount);
    }

    [Fact]
    public void New_capture_id_accumulates_changed_items_exactly_once()
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
        Assert.Equal([100u, 200u], config.RetainerListingRefresh.Items.Select(item => item.ItemId));
        Assert.Equal("capture-2", config.RetainerListingRefresh.LastObservedCaptureId);

        coordinator.NotifyListingCaptureChanged();
        coordinator.Tick(Start.AddSeconds(2), false);
        Assert.Equal([100u, 200u], config.RetainerListingRefresh.Items.Select(item => item.ItemId));
    }

    [Fact]
    public void Capture_without_comparison_baseline_sends_nothing_and_records_baseline()
    {
        var source = new FakeSource(new RetainerListingRefreshSnapshot(
            [new RetainerListingRefreshCandidate(100, "Iron Ore")],
            "capture-1",
            ComparisonAvailable: false));
        var runtime = new FakeRuntime();
        var config = CreateConfig();
        var coordinator = CreateCoordinator(config, source, runtime);

        coordinator.Tick(Start, true);

        Assert.Empty(runtime.RequestedItems);
        Assert.Empty(config.RetainerListingRefresh.Items);
        Assert.Equal("capture-1", config.RetainerListingRefresh.LastObservedCaptureId);
        Assert.Equal("BaselineEstablished", config.RetainerListingRefresh.StatusCode);
    }

    [Fact]
    public void New_capture_preserves_blocked_and_reconciling_safety_history()
    {
        var source = new FakeSource(new RetainerListingRefreshCandidate[] { new(300, "Silver Ore") });
        var runtime = new FakeRuntime();
        var config = CreateConfig();
        config.RetainerListingRefresh.Items =
        [
            new PersistedRetainerListingRefreshItem
            {
                ItemId = 100,
                State = RetainerListingRefreshItemState.Blocked,
                LastCode = "RequestIdDiscontinuity",
            },
            new PersistedRetainerListingRefreshItem
            {
                ItemId = 200,
                State = RetainerListingRefreshItemState.NeedsReconciliation,
                Attempts = 1,
                NextAttemptAtUtc = Start.AddMinutes(2).UtcDateTime,
            },
        ];
        var coordinator = CreateCoordinator(config, source, runtime);

        coordinator.Tick(Start, false);

        Assert.Equal(
            [
                (100u, RetainerListingRefreshItemState.Blocked),
                (200u, RetainerListingRefreshItemState.NeedsReconciliation),
                (300u, RetainerListingRefreshItemState.Deferred),
            ],
            config.RetainerListingRefresh.Items
                .OrderBy(item => item.ItemId)
                .Select(item => (item.ItemId, item.State)));
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
        public bool RetryOnReadFailure { get; init; } = true;
        public bool SurfaceReadFailure { get; init; }
        public int ReadCount { get; private set; }

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
            ReadCount++;
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
