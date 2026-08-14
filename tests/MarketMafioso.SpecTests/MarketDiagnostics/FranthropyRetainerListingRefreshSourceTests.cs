using Franthropy.Dalamud.Observations;
using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;
using MarketMafioso.MarketDiagnostics;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.SpecTests.MarketDiagnostics;

public sealed class FranthropyRetainerListingRefreshSourceTests
{
    [Fact]
    public async Task Direct_source_reads_distinct_current_items_without_quartermaster()
    {
        var root = Path.Combine(Path.GetTempPath(), "MMF.SharedObservation.Tests", Guid.NewGuid().ToString("N"));
        var pluginConfig = Path.Combine(root, "XIVLauncher", "pluginConfigs", "MarketMafioso");
        Directory.CreateDirectory(pluginConfig);
        var paths = SharedObservationPaths.FromPluginConfigDirectory(pluginConfig);
        var options = new ObservationStoreOptions
        {
            DatabasePath = paths.DatabasePath,
            BackupDirectory = paths.BackupsDirectory,
            MigrationLockPath = paths.MigrationLockPath,
            ChangeSignalPath = paths.ChangeSignalPath,
        };
        var owner = new ObservationOwner(100, 74);
        var open = await SqliteObservationStore.OpenAsync(options);
        Assert.True(open.IsReady, open.Message);
        try
        {
            await open.Store!.WriteAsync(Listings(owner, 200, 1, [100, 200]));
            await open.Store.WriteAsync(Listings(owner, 201, 2, [200, 300]));
            await using var client = CreateClient(pluginConfig, () => owner);
            using var source = new FranthropyRetainerListingRefreshSource(client, () => owner);
            var initialSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Changed += () => initialSignal.TrySetResult();
            client.Start();
            await initialSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var success = source.TryRead(out var snapshot, out var error);

            Assert.True(success, error);
            Assert.Equal("franthropy:2", snapshot!.CaptureId);
            Assert.Equal([100u, 200u, 300u], snapshot.Items.Select(item => item.ItemId));
            Assert.False(snapshot.ComparisonAvailable);

            var unrelatedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Changed += () => unrelatedSignal.TrySetResult();
            await open.Store.WriteAsync(Inventory(owner, 200, 3, 5333));
            await Task.Delay(300);
            Assert.False(unrelatedSignal.Task.IsCompleted);

            var changedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Changed += () => changedSignal.TrySetResult();
            await open.Store.WriteAsync(Listings(owner, 200, 4, [200]));
            await changedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(source.TryRead(out var changed, out error), error);
            Assert.Equal("franthropy:4", changed!.CaptureId);
            Assert.Equal([100u, 200u, 300u], changed.Items.Select(item => item.ItemId));
            Assert.True(changed.ComparisonAvailable);
        }
        finally
        {
            await open.Store!.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Owner_switch_starts_a_new_baseline_without_prior_character_items()
    {
        var root = Path.Combine(Path.GetTempPath(), "MMF.SharedObservation.Tests", Guid.NewGuid().ToString("N"));
        var pluginConfig = Path.Combine(root, "XIVLauncher", "pluginConfigs", "MarketMafioso");
        Directory.CreateDirectory(pluginConfig);
        var paths = SharedObservationPaths.FromPluginConfigDirectory(pluginConfig);
        var options = new ObservationStoreOptions
        {
            DatabasePath = paths.DatabasePath,
            BackupDirectory = paths.BackupsDirectory,
            MigrationLockPath = paths.MigrationLockPath,
            ChangeSignalPath = paths.ChangeSignalPath,
        };
        var firstOwner = new ObservationOwner(100, 74);
        var secondOwner = new ObservationOwner(101, 74);
        var currentOwner = firstOwner;
        var open = await SqliteObservationStore.OpenAsync(options);
        Assert.True(open.IsReady, open.Message);
        try
        {
            await open.Store!.WriteAsync(Listings(firstOwner, 200, 1, [100]));
            await using var client = CreateClient(pluginConfig, () => currentOwner);
            using var source = new FranthropyRetainerListingRefreshSource(client, () => currentOwner);
            var initialSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Changed += () => initialSignal.TrySetResult();
            client.Start();
            await initialSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(source.TryRead(out var first, out var error), error);
            Assert.Equal([100u], first!.Items.Select(item => item.ItemId));

            currentOwner = secondOwner;
            var changedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Changed += () => changedSignal.TrySetResult();
            await open.Store.WriteAsync(Listings(secondOwner, 300, 1, [400]));
            await changedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(source.TryRead(out var second, out error), error);
            Assert.Equal([400u], second!.Items.Select(item => item.ItemId));
            Assert.False(second.ComparisonAvailable);
        }
        finally
        {
            await open.Store!.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task First_success_after_missing_evidence_is_still_a_baseline()
    {
        var root = Path.Combine(Path.GetTempPath(), "MMF.SharedObservation.Tests", Guid.NewGuid().ToString("N"));
        var pluginConfig = Path.Combine(root, "XIVLauncher", "pluginConfigs", "MarketMafioso");
        Directory.CreateDirectory(pluginConfig);
        var paths = SharedObservationPaths.FromPluginConfigDirectory(pluginConfig);
        var options = new ObservationStoreOptions
        {
            DatabasePath = paths.DatabasePath,
            BackupDirectory = paths.BackupsDirectory,
            MigrationLockPath = paths.MigrationLockPath,
            ChangeSignalPath = paths.ChangeSignalPath,
        };
        var owner = new ObservationOwner(100, 74);
        var otherOwner = new ObservationOwner(101, 74);
        var open = await SqliteObservationStore.OpenAsync(options);
        Assert.True(open.IsReady, open.Message);
        try
        {
            await open.Store!.WriteAsync(Listings(otherOwner, 300, 1, [900]));
            await using var client = CreateClient(pluginConfig, () => owner);
            using var source = new FranthropyRetainerListingRefreshSource(client, () => owner);
            var initialSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Changed += () => initialSignal.TrySetResult();
            client.Start();
            await initialSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(source.TryRead(out _, out _));

            var changedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Changed += () => changedSignal.TrySetResult();
            await open.Store.WriteAsync(Listings(owner, 200, 1, [100]));
            await changedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(source.TryRead(out var baseline, out var error), error);
            Assert.Equal([100u], baseline!.Items.Select(item => item.ItemId));
            Assert.False(baseline.ComparisonAvailable);
        }
        finally
        {
            await open.Store!.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static ObservationEnvelope Listings(
        ObservationOwner owner,
        ulong retainerId,
        long sourceRevision,
        IReadOnlyList<uint> itemIds) =>
        new(
            new ObservationScope(
                owner,
                ObservationSubject.Retainer(retainerId, owner),
                ObservationContainerKind.RetainerMarketListings),
            new ObservationCapture(
                sourceRevision,
                new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero).AddMinutes(sourceRevision),
                new ObservationProvenance("TestHost", "instance", "1.0.0", "2026.07.31.0000.0000"),
                ObservationEvidence.CompleteAvailable),
            ObservationPayload.Create(
                ObservationPayloadContracts.RetainerMarketListings,
                ObservationPayloadContracts.Version,
                new RetainerMarketListingsPayload(itemIds
                    .Select((itemId, slot) => new RetainerMarketListingObservation(slot, itemId, 1, 10, false))
                    .ToArray())));

    private static ObservationEnvelope Inventory(
        ObservationOwner owner,
        ulong retainerId,
        long sourceRevision,
        uint itemId) =>
        new(
            new ObservationScope(
                owner,
                ObservationSubject.Retainer(retainerId, owner),
                ObservationContainerKind.RetainerInventory),
            new ObservationCapture(
                sourceRevision,
                new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero).AddMinutes(sourceRevision),
                new ObservationProvenance("TestHost", "instance", "1.0.0", "2026.07.31.0000.0000"),
                ObservationEvidence.CompleteAvailable),
            ObservationPayload.Create(
                ObservationPayloadContracts.RetainerInventory,
                ObservationPayloadContracts.Version,
                new InventoryObservationPayload([10000], [10000], [new InventoryItemObservation(10000, 0, itemId, 1, false)])));

    private static DalamudSharedObservationClient CreateClient(
        string pluginConfig,
        Func<ObservationOwner?> currentOwner) =>
        new(new DalamudSharedObservationClientOptions
        {
            PluginConfigDirectory = pluginConfig,
            CurrentOwner = currentOwner,
        });
}
