using MarketMafioso.MarketAcquisition;
using MarketMafioso.Tests.TestUtilities;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketPurchaseEvidenceCoordinatorTests
{
    [Theory]
    [InlineData(false, 42u)]
    [InlineData(true, 1_000_042u)]
    public void MatchingPacket_ConfirmsItemQualityAndQuantityOnly(bool highQuality, uint rawId)
    {
        var coordinator = CreateArmed(highQuality: highQuality);
        coordinator.Enqueue(MarketPurchasePacketObservation.Create(11, Time(2), rawId, highQuality, 3));

        coordinator.DrainAndApply();

        var confirmed = Assert.IsType<Confirmed>(coordinator.State);
        Assert.Equal(42u, confirmed.Evidence.ItemId);
        Assert.Equal(rawId, confirmed.Evidence.RawCatalogId);
        Assert.Equal("listing-intent-only", confirmed.Intent.ListingId);
        Assert.Equal(99u, confirmed.Intent.IntendedUnitPrice);
        Assert.Equal("Siren", confirmed.Intent.IntendedWorldName);
    }

    [Fact]
    public void ExpireDue_ProducesIndeterminateOutcome()
    {
        var coordinator = CreateArmed();
        Assert.True(coordinator.ExpireDue(Time(6)));
        Assert.IsType<TimedOutIndeterminate>(coordinator.State);
    }

    [Theory]
    [InlineData(43u, false, 3u)]
    [InlineData(42u, true, 3u)]
    [InlineData(42u, false, 2u)]
    public void FirstPostFloorMismatch_ConflictsAndStops(uint itemId, bool highQuality, uint quantity)
    {
        var coordinator = CreateArmed();
        coordinator.Enqueue(MarketPurchasePacketObservation.Create(11, Time(2), itemId, highQuality, quantity));
        coordinator.Enqueue(MarketPurchasePacketObservation.Create(12, Time(3), 42, false, 3));

        coordinator.DrainAndApply();

        Assert.IsType<ConflictingPacket>(coordinator.State);
        Assert.Equal(2, coordinator.Snapshot().Observations.Count);
    }

    [Fact]
    public void PreFloorPacket_IsIgnored()
    {
        var coordinator = CreateArmed();
        coordinator.Enqueue(MarketPurchasePacketObservation.Create(10, Time(2), 99, true, 99));
        coordinator.DrainAndApply();
        Assert.IsType<Pending>(coordinator.State);
    }

    [Fact]
    public void DuplicateAfterTerminal_IsRetainedButDoesNotReapply()
    {
        var store = new MemoryStore();
        var coordinator = CreateArmed(store: store);
        var packet = MarketPurchasePacketObservation.Create(11, Time(2), 42, false, 3);
        coordinator.Enqueue(packet);
        coordinator.DrainAndApply();
        var terminal = coordinator.State;
        coordinator.Enqueue(packet with { Sequence = 12 });
        coordinator.DrainAndApply();

        Assert.Same(terminal, coordinator.State);
        Assert.Equal(2, coordinator.Snapshot().Observations.Count);
    }

    [Fact]
    public void TryArm_RejectsSecondPendingIntent()
    {
        var coordinator = CreateArmed();
        Assert.False(coordinator.TryArm(Intent() with { IntentId = "second" }));
        Assert.Equal("intent", coordinator.State!.Intent.IntentId);
    }

    [Fact]
    public void TryArm_DoesNotExposePendingUntilPersistenceSucceeds()
    {
        var store = new MemoryStore { FailSave = true };
        var coordinator = new MarketPurchaseEvidenceCoordinator(store);
        Assert.Throws<IOException>(() => coordinator.TryArm(Intent()));
        Assert.Null(coordinator.State);
    }

    [Fact]
    public void PacketCallback_DoesNotApplyUntilFrameworkDrain()
    {
        var coordinator = CreateArmed();
        coordinator.Enqueue(MarketPurchasePacketObservation.Create(11, Time(2), 42, false, 3));
        Assert.IsType<Pending>(coordinator.State);
        coordinator.DrainAndApply();
        Assert.IsType<Confirmed>(coordinator.State);
    }

    [Fact]
    public void Restore_ExpiredPendingBecomesDurableIndeterminate()
    {
        var store = new MemoryStore();
        Assert.True(new MarketPurchaseEvidenceCoordinator(store).TryArm(Intent()));
        var restored = MarketPurchaseEvidenceCoordinator.Restore(store, Time(6));
        Assert.IsType<TimedOutIndeterminate>(restored.State);
        Assert.IsType<TimedOutIndeterminate>(store.Value!.State);
    }

    [Fact]
    public void FileStore_RecoversFromBackupWhenPrimaryIsCorrupt()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "purchase-evidence.json");
        var store = new MarketPurchaseEvidenceFileStore(path);
        var coordinator = new MarketPurchaseEvidenceCoordinator(store);
        coordinator.TryArm(Intent());
        coordinator.Enqueue(MarketPurchasePacketObservation.Create(11, Time(2), 42, false, 3));
        coordinator.DrainAndApply();
        File.WriteAllText(path, "not json");

        var recovered = new MarketPurchaseEvidenceFileStore(path).Load();

        Assert.NotNull(recovered);
        Assert.IsType<Pending>(recovered.State);
    }

    [Fact]
    public void CatalogNormalization_PreservesCanonicalIdsAndRemovesGameOffset()
    {
        Assert.Equal(42u, MarketPurchaseCatalogId.Normalize(42));
        Assert.Equal(42u, MarketPurchaseCatalogId.Normalize(1_000_042));
        Assert.Equal(42u, MarketPurchaseCatalogId.Normalize(2_000_042));
    }

    private static MarketPurchaseEvidenceCoordinator CreateArmed(bool highQuality = false, MemoryStore? store = null)
    {
        var coordinator = new MarketPurchaseEvidenceCoordinator(store ?? new MemoryStore());
        Assert.True(coordinator.TryArm(Intent() with { IsHighQuality = highQuality }));
        return coordinator;
    }

    private static MarketPurchaseIntent Intent() => new()
    {
        IntentId = "intent", RouteId = "route", AttemptId = "attempt", LineId = "line",
        ItemId = 42, IsHighQuality = false, Quantity = 3,
        ListingId = "listing-intent-only", IntendedUnitPrice = 99, IntendedWorldId = 57, IntendedWorldName = "Siren",
        ArmedAtUtc = Time(1), SequenceFloor = 10, DeadlineUtc = Time(5),
    };

    private static DateTimeOffset Time(int minute) => DateTimeOffset.UnixEpoch.AddMinutes(minute);

    private sealed class MemoryStore : IMarketPurchaseEvidenceStateStore
    {
        public MarketPurchaseEvidenceSnapshot? Value { get; private set; }
        public bool FailSave { get; init; }
        public MarketPurchaseEvidenceSnapshot? Load() => Value;
        public void Save(MarketPurchaseEvidenceSnapshot snapshot)
        {
            if (FailSave) throw new IOException("simulated");
            Value = snapshot;
        }
    }
}
