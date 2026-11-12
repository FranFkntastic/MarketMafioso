using MarketMafioso.MarketAcquisition;
using MarketMafioso.SpecTests.TestUtilities;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketPurchaseEvidenceCoordinatorTests
{

    [Fact]
    public void FileStore_RepairsAnUncommittedTrailingFragmentBeforeTheNextTransition()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "purchase-evidence.json");
        var journalPath = path + ".journal";
        var store = new MarketPurchaseEvidenceFileStore(path);
        var queue = Queue();
        var coordinator = CreateArmed(queue, store: store, markSubmitted: false);
        var intent = Assert.IsType<PendingMarketPurchase>(coordinator.State).Intent;
        Assert.True(coordinator.MarkConfirmationSubmitted(intent.IntentId, Time(2)).IsRecorded);
        File.AppendAllText(journalPath, "{\"version\":1");
        var recoveredCoordinator = new MarketPurchaseEvidenceCoordinator(
            new MarketPurchaseEvidenceFileStore(path),
            queue);
        queue.Enqueue(Time(2), 42, false, 3);

        var result = recoveredCoordinator.AdvanceOnFrameworkThread(Time(3));
        var recovered = new MarketPurchaseEvidenceFileStore(path).Load();

        Assert.Equal(MarketPurchaseEvidenceAdvanceStatus.Applied, result.Status);
        Assert.IsType<ConfirmedMarketPurchase>(recovered!.State);
        Assert.False(File.Exists(journalPath));
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
