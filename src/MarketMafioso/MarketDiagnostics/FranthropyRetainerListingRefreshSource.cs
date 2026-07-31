using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Observations;
using Franthropy.Observations.Storage;
using Franthropy.Observations.V1;

namespace MarketMafioso.MarketDiagnostics;

internal sealed class FranthropyRetainerListingRefreshSource : IRetainerListingRefreshSource, IDisposable
{
    private readonly Func<ObservationOwner?> currentOwner;
    private readonly ObservationStoreOptions options;
    private readonly ObservationDatabaseChangeMonitor monitor;
    private IReadOnlySet<uint> previousItems = new HashSet<uint>();
    private ObservationOwner? previousOwner;
    private int changeObserved;
    private bool disposed;

    public FranthropyRetainerListingRefreshSource(string pluginConfigDirectory, IPlayerState playerState)
        : this(pluginConfigDirectory, CreateOwnerProvider(playerState))
    {
    }

    internal FranthropyRetainerListingRefreshSource(
        string pluginConfigDirectory,
        Func<ObservationOwner?> currentOwner)
    {
        this.currentOwner = currentOwner ?? throw new ArgumentNullException(nameof(currentOwner));
        var paths = SharedObservationPaths.FromPluginConfigDirectory(pluginConfigDirectory);
        options = new ObservationStoreOptions
        {
            DatabasePath = paths.DatabasePath,
            BackupDirectory = paths.BackupsDirectory,
            MigrationLockPath = paths.MigrationLockPath,
            ChangeSignalPath = paths.ChangeSignalPath,
        };
        monitor = new ObservationDatabaseChangeMonitor(options);
        monitor.Changed += OnDatabaseChanged;
        monitor.StartAsync().AsTask().GetAwaiter().GetResult();
    }

    public event Action? Changed;
    public bool RetryOnReadFailure => false;
    public bool SurfaceReadFailure => true;

    public bool TryRead(out RetainerListingRefreshSnapshot? snapshot, out string error)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        snapshot = null;
        error = string.Empty;
        var owner = currentOwner();
        if (owner is null)
        {
            error = "The current character identity is unavailable.";
            return false;
        }
        if (owner != previousOwner)
        {
            previousOwner = owner;
            previousItems = new HashSet<uint>();
            Interlocked.Exchange(ref changeObserved, 0);
        }
        if (!string.IsNullOrWhiteSpace(monitor.LastNotificationError))
        {
            error = $"Shared listing change observation is unavailable: {monitor.LastNotificationError}";
            return false;
        }

        var opened = SqliteObservationReader.OpenAsync(options).AsTask().GetAwaiter().GetResult();
        if (!opened.IsReady)
        {
            error = opened.Message;
            return false;
        }

        try
        {
            var read = opened.Reader!.ReadCurrentByOwnerAsync(
                owner,
                ObservationContainerKind.RetainerMarketListings).AsTask().GetAwaiter().GetResult();
            if (read.Status != ObservationReadStatus.Found)
            {
                error = read.Message;
                return false;
            }

            var trustworthy = read.Observations.Where(observation => !observation.IsStale).ToArray();
            if (trustworthy.Length == 0)
            {
                error = "No current trusted retainer-listing observation exists for this character.";
                return false;
            }

            var revision = trustworthy.Max(observation => observation.Revision);
            var currentItems = trustworthy
                .SelectMany(observation => observation.Payload.Deserialize<RetainerMarketListingsPayload>(
                    ObservationPayloadContracts.RetainerMarketListings,
                    ObservationPayloadContracts.Version).Listings)
                .Where(listing => listing.ItemId != 0)
                .Select(listing => listing.ItemId)
                .Distinct()
                .ToHashSet();
            var items = currentItems
                .Concat(previousItems)
                .Distinct()
                .Order()
                .Select(itemId => new RetainerListingRefreshCandidate(itemId, null))
                .ToArray();
            previousItems = currentItems;
            snapshot = new RetainerListingRefreshSnapshot(
                items,
                $"franthropy:{revision}",
                ComparisonAvailable: Interlocked.Exchange(ref changeObserved, 0) != 0);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ObservationPayloadContractException)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            opened.Reader!.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        monitor.Changed -= OnDatabaseChanged;
        monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void OnDatabaseChanged(object? sender, ObservationDatabaseChanged change)
    {
        Interlocked.Exchange(ref changeObserved, 1);
        var subscribers = Changed;
        if (subscribers is null)
            return;
        foreach (var subscriber in subscribers.GetInvocationList().Cast<Action>())
        {
            try { subscriber(); }
            catch { }
        }
    }

    private static Func<ObservationOwner?> CreateOwnerProvider(IPlayerState playerState)
    {
        ArgumentNullException.ThrowIfNull(playerState);
        return () => playerState.ContentId == 0 || !playerState.HomeWorld.IsValid
            ? null
            : new ObservationOwner(playerState.ContentId, playerState.HomeWorld.Value.RowId);
    }
}
