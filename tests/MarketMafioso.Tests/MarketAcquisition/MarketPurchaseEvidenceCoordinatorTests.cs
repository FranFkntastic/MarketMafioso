using MarketMafioso.MarketAcquisition;
using MarketMafioso.Tests.TestUtilities;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketPurchaseEvidenceCoordinatorTests
{
    [Fact]
    public void TryArm_PersistsExactIntentAndCurrentEpochFloorBeforePublishingPending()
    {
        var store = new MemoryStore();
        var queue = Queue();
        queue.Enqueue(Time(0), 99, false, 1);
        var coordinator = new MarketPurchaseEvidenceCoordinator(store, queue);

        var result = coordinator.TryArm(Draft());

        Assert.True(result.IsArmed);
        var pending = Assert.IsType<PendingMarketPurchase>(coordinator.State);
        Assert.Equal("epoch-a/1", pending.Intent.PacketFloor.Epoch);
        Assert.Equal(0, pending.Intent.PacketFloor.Sequence);
        Assert.Equal("listing-1", pending.Intent.ListingId);
        Assert.Equal(57u, pending.Intent.WorldId);
        Assert.Equal(297u, pending.Intent.TotalGil);
        Assert.IsType<PendingMarketPurchase>(store.Value?.State);
    }

    [Fact]
    public void TryArm_PersistenceFailureDoesNotExposeIntentOrPermitConfirmation()
    {
        var store = new MemoryStore { FailNextSave = true };
        var coordinator = new MarketPurchaseEvidenceCoordinator(store, Queue());

        var result = coordinator.TryArm(Draft());

        Assert.Equal(MarketPurchaseIntentArmStatus.PersistenceFailed, result.Status);
        Assert.Null(coordinator.State);
        Assert.Null(store.Value);
    }

    [Fact]
    public void TryArm_UnavailablePacketSourceFailsClosed()
    {
        var store = new MemoryStore();
        var coordinator = new MarketPurchaseEvidenceCoordinator(store, new MarketPurchasePacketEvidenceQueue(false, "epoch-a"));

        var result = coordinator.TryArm(Draft());

        Assert.Equal(MarketPurchaseIntentArmStatus.EvidenceUnavailable, result.Status);
        Assert.Equal(0, store.SaveCount);
    }

    [Theory]
    [InlineData(false, 42u)]
    [InlineData(true, 1_000_042u)]
    public void Advance_MatchingPacketConfirmsOnlyItemQualityAndQuantity(bool highQuality, uint rawCatalogId)
    {
        var queue = Queue();
        var coordinator = CreateArmed(queue, Draft() with { IsHighQuality = highQuality });
        queue.Enqueue(Time(2), rawCatalogId, highQuality, 3);

        var result = coordinator.AdvanceOnFrameworkThread(Time(3));

        Assert.Equal(MarketPurchaseEvidenceAdvanceStatus.Applied, result.Status);
        var confirmed = Assert.IsType<ConfirmedMarketPurchase>(coordinator.State);
        Assert.Equal(42u, confirmed.Evidence.ItemId);
        Assert.Equal(rawCatalogId, confirmed.Evidence.RawCatalogId);
        Assert.Equal("listing-1", confirmed.Intent.ListingId);
        Assert.Equal("Siren", confirmed.Intent.WorldName);
    }

    [Theory]
    [InlineData(43u, false, 3u)]
    [InlineData(42u, true, 3u)]
    [InlineData(42u, false, 2u)]
    public void Advance_FirstPostFloorMismatchIsTerminalConflict(uint itemId, bool highQuality, uint quantity)
    {
        var queue = Queue();
        var coordinator = CreateArmed(queue);
        queue.Enqueue(Time(2), itemId, highQuality, quantity);
        queue.Enqueue(Time(3), 42, false, 3);

        coordinator.AdvanceOnFrameworkThread(Time(4));

        Assert.IsType<ConflictingMarketPurchasePacket>(coordinator.State);
        Assert.Equal(2, coordinator.Snapshot().Observations.Count);
        Assert.Equal(
            MarketPurchaseIntentArmStatus.TerminalEvidenceRequiresReconciliation,
            coordinator.TryArm(Draft() with { IntentId = "next" }).Status);
    }

    [Fact]
    public void Advance_MatchingPacketBeforeConfirmationSubmissionIsStillAConflict()
    {
        var queue = Queue();
        var coordinator = CreateArmed(queue, markSubmitted: false);
        queue.Enqueue(Time(2), 42, false, 3);

        coordinator.AdvanceOnFrameworkThread(Time(3));

        Assert.IsType<ConflictingMarketPurchasePacket>(coordinator.State);
    }

    [Fact]
    public void Advance_UsesObservationTimeRatherThanLateFrameworkDrainTime()
    {
        var queue = Queue();
        var coordinator = CreateArmed(queue);
        queue.Enqueue(Time(4), 42, false, 3);

        coordinator.AdvanceOnFrameworkThread(Time(20));

        Assert.IsType<ConfirmedMarketPurchase>(coordinator.State);
    }

    [Fact]
    public void Advance_PostDeadlinePacketProducesTimedOutIndeterminateEvenWhenDrainedFirst()
    {
        var queue = Queue();
        var coordinator = CreateArmed(queue);
        queue.Enqueue(Time(6), 42, false, 3);

        coordinator.AdvanceOnFrameworkThread(Time(6));

        var timedOut = Assert.IsType<TimedOutIndeterminateMarketPurchase>(coordinator.State);
        Assert.Equal(Time(5), timedOut.TimedOutAtUtc);
    }

    [Fact]
    public void Advance_ExpiresPendingAtItsDurableDeadline()
    {
        var coordinator = CreateArmed(Queue());

        coordinator.AdvanceOnFrameworkThread(Time(6));

        Assert.IsType<TimedOutIndeterminateMarketPurchase>(coordinator.State);
    }

    [Fact]
    public void RestartedEpochCannotCorrelateWithPersistedPendingIntent()
    {
        var store = new MemoryStore();
        Assert.True(new MarketPurchaseEvidenceCoordinator(store, Queue("old-epoch")).TryArm(Draft()).IsArmed);
        var newQueue = Queue("new-epoch");
        var restored = new MarketPurchaseEvidenceCoordinator(store, newQueue);
        newQueue.Enqueue(Time(2), 42, false, 3);

        restored.AdvanceOnFrameworkThread(Time(3));

        Assert.IsType<PendingMarketPurchase>(restored.State);
        Assert.Single(restored.Snapshot().Observations);
    }

    [Fact]
    public void Advance_PersistsApplicationBeforeRemovingPacketFromQueue()
    {
        var store = new MemoryStore();
        var queue = Queue();
        var coordinator = CreateArmed(queue, store: store);
        queue.Enqueue(Time(2), 42, false, 3);
        store.FailNextSave = true;

        var failed = coordinator.AdvanceOnFrameworkThread(Time(3));
        var retried = coordinator.AdvanceOnFrameworkThread(Time(3));

        Assert.Equal(MarketPurchaseEvidenceAdvanceStatus.PersistenceFailed, failed.Status);
        Assert.Equal(0, failed.DequeuedObservationCount);
        Assert.Equal(1, retried.DequeuedObservationCount);
        Assert.IsType<ConfirmedMarketPurchase>(coordinator.State);
        Assert.Single(coordinator.Snapshot().Observations);
    }

    [Fact]
    public void ResolveTerminal_DurablyArchivesOutcomeBeforeAllowingAnotherIntent()
    {
        var store = new MemoryStore();
        var queue = Queue();
        var coordinator = CreateArmed(queue, store: store);
        queue.Enqueue(Time(2), 42, false, 3);
        coordinator.AdvanceOnFrameworkThread(Time(3));

        var resolved = coordinator.ResolveTerminal(
            "intent-1",
            MarketPurchaseTerminalDisposition.AppliedExactlyOnce,
            Time(4),
            "Applied to route sunk state.");
        var next = coordinator.TryArm(Draft() with { IntentId = "intent-2", AttemptId = "attempt-2" });

        Assert.True(resolved.IsResolved);
        Assert.True(next.IsArmed);
        var history = Assert.Single(coordinator.Snapshot().History);
        Assert.Equal(MarketPurchaseTerminalDisposition.AppliedExactlyOnce, history.Disposition);
        Assert.IsType<ConfirmedMarketPurchase>(history.TerminalState);
        Assert.Equal(5, store.SaveCount);
    }

    [Fact]
    public void ResolveTerminal_RequiresManualReconciliationForIndeterminateOrConflict()
    {
        var coordinator = CreateArmed(Queue());
        coordinator.AdvanceOnFrameworkThread(Time(6));

        var invalid = coordinator.ResolveTerminal(
            "intent-1",
            MarketPurchaseTerminalDisposition.AppliedExactlyOnce,
            Time(7),
            "Not actually reconciled.");
        var reconciled = coordinator.ResolveTerminal(
            "intent-1",
            MarketPurchaseTerminalDisposition.ManuallyReconciled,
            Time(8),
            "Inventory and fresh listings were reviewed.");

        Assert.Equal(MarketPurchaseTerminalResolutionStatus.InvalidDisposition, invalid.Status);
        Assert.True(reconciled.IsResolved);
    }

    [Fact]
    public void DiagnosticHistoriesRemainBounded()
    {
        var queue = Queue();
        var coordinator = CreateArmed(queue);
        queue.Enqueue(Time(2), 42, false, 3);
        coordinator.AdvanceOnFrameworkThread(Time(3));
        coordinator.ResolveTerminal("intent-1", MarketPurchaseTerminalDisposition.AppliedExactlyOnce, Time(4), "Applied.");

        for (var index = 0; index < 140; index++)
            queue.Enqueue(Time(10 + index), 100u + (uint)index, false, 1);
        coordinator.AdvanceOnFrameworkThread(Time(200));

        Assert.Equal(MarketPurchaseEvidenceCoordinator.MaxObservationHistory, coordinator.Snapshot().Observations.Count);
    }

    [Fact]
    public void ResolvedAttemptHistoryRemainsBounded()
    {
        var queue = Queue();
        var coordinator = new MarketPurchaseEvidenceCoordinator(new MemoryStore(), queue);
        var attemptCount = MarketPurchaseEvidenceCoordinator.MaxResolvedAttemptHistory + 5;

        for (var index = 0; index < attemptCount; index++)
        {
            var intentId = $"intent-{index}";
            var arm = coordinator.TryArm(Draft() with { IntentId = intentId, AttemptId = $"attempt-{index}" });
            Assert.True(arm.IsArmed);
            Assert.True(coordinator.MarkConfirmationSubmitted(intentId, Time(2)).IsRecorded);
            queue.Enqueue(Time(3), 42, false, 3);
            coordinator.AdvanceOnFrameworkThread(Time(4));
            Assert.True(coordinator.ResolveTerminal(
                intentId,
                MarketPurchaseTerminalDisposition.AppliedExactlyOnce,
                Time(4),
                "Applied.").IsResolved);
        }

        var history = coordinator.Snapshot().History;
        Assert.Equal(MarketPurchaseEvidenceCoordinator.MaxResolvedAttemptHistory, history.Count);
        Assert.Equal("intent-5", history[0].TerminalState.Intent.IntentId);
    }

    [Fact]
    public void ProductStateMutationIsRejectedOffFrameworkThread()
    {
        var coordinator = CreateArmed(Queue());
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                coordinator.AdvanceOnFrameworkThread(Time(2));
            }
            catch (Exception exception)
            {
                caught = exception;
            }
        });

        thread.Start();
        thread.Join();

        var exception = Assert.IsType<InvalidOperationException>(caught);
        Assert.Contains("framework thread", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileStore_RoundTripsTerminalHistoryAndRecoversBackupWhenPrimaryIsCorrupt()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "purchase-evidence.json");
        var store = new MarketPurchaseEvidenceFileStore(path);
        var queue = Queue();
        var coordinator = CreateArmed(queue, store: store);
        queue.Enqueue(Time(2), 42, false, 3);
        coordinator.AdvanceOnFrameworkThread(Time(3));
        coordinator.ResolveTerminal("intent-1", MarketPurchaseTerminalDisposition.AppliedExactlyOnce, Time(4), "Applied.");
        var roundTrip = store.Load();
        File.WriteAllText(path, "not json");

        var recovered = store.Load();

        Assert.NotNull(roundTrip);
        Assert.Null(roundTrip.State);
        Assert.Single(roundTrip.History);
        Assert.NotNull(recovered);
        Assert.IsType<ConfirmedMarketPurchase>(recovered.State);
        Assert.Empty(recovered.History);
    }

    [Fact]
    public void CatalogNormalizationPreservesCanonicalIdsAndRemovesQualityOffsets()
    {
        Assert.Equal(42u, MarketPurchaseCatalogId.Normalize(42));
        Assert.Equal(42u, MarketPurchaseCatalogId.Normalize(1_000_042));
        Assert.Equal(42u, MarketPurchaseCatalogId.Normalize(2_000_042));
    }

    private static MarketPurchaseEvidenceCoordinator CreateArmed(
        MarketPurchasePacketEvidenceQueue queue,
        MarketPurchaseIntentDraft? draft = null,
        IMarketPurchaseEvidenceStateStore? store = null,
        bool markSubmitted = true)
    {
        var coordinator = new MarketPurchaseEvidenceCoordinator(store ?? new MemoryStore(), queue);
        var arm = coordinator.TryArm(draft ?? Draft());
        Assert.True(arm.IsArmed);
        if (markSubmitted)
            Assert.True(coordinator.MarkConfirmationSubmitted(arm.Intent!.IntentId, Time(2)).IsRecorded);
        return coordinator;
    }

    private static MarketPurchaseIntentDraft Draft() => new()
    {
        IntentId = "intent-1",
        RouteId = "request-1",
        RouteRunId = "run-1",
        AttemptId = "attempt-1",
        LineId = "line-1",
        ItemId = 42,
        IsHighQuality = false,
        Quantity = 3,
        ListingId = "listing-1",
        RetainerId = "retainer-1",
        UnitPrice = 99,
        TotalGil = 297,
        WorldId = 57,
        WorldName = "Siren",
        ArmedAtUtc = Time(1),
        DeadlineUtc = Time(5),
    };

    private static MarketPurchasePacketEvidenceQueue Queue(string epoch = "epoch-a") => new(true, epoch);
    private static DateTimeOffset Time(int minute) => DateTimeOffset.UnixEpoch.AddMinutes(minute);

    private sealed class MemoryStore : IMarketPurchaseEvidenceStateStore
    {
        public MarketPurchaseEvidenceSnapshot? Value { get; private set; }
        public bool FailNextSave { get; set; }
        public int SaveCount { get; private set; }

        public MarketPurchaseEvidenceSnapshot? Load() => Value;

        public void Save(MarketPurchaseEvidenceSnapshot snapshot)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("simulated persistence failure");
            }
            SaveCount++;
            Value = snapshot;
        }
    }
}
