using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Observations;
using Franthropy.Observations.V1;

namespace MarketMafioso.MarketDiagnostics;

internal sealed class FranthropyRetainerListingRefreshSource : IRetainerListingRefreshSource, IDisposable
{
    private readonly object gate = new();
    private readonly DalamudSharedObservationClient client;
    private readonly Func<ObservationOwner?> currentOwner;
    private IReadOnlySet<uint> previousItems = new HashSet<uint>();
    private ObservationOwner? previousOwner;
    private string? notifiedListingSignature;
    private bool hasSuccessfulBaseline;
    private int changeObserved;
    private bool disposed;

    public FranthropyRetainerListingRefreshSource(
        DalamudSharedObservationClient client,
        IPlayerState playerState)
        : this(client, CreateOwnerProvider(playerState))
    {
    }

    internal FranthropyRetainerListingRefreshSource(
        DalamudSharedObservationClient client,
        Func<ObservationOwner?> currentOwner)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.currentOwner = currentOwner ?? throw new ArgumentNullException(nameof(currentOwner));
        client.RetainersChanged += OnRetainersChanged;
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

        if (!client.TryGetRetainers(owner, out var current))
        {
            error = "No current shared retainer observation exists for this character.";
            return false;
        }

        var trustworthy = current!.Observations
            .Where(observation =>
                observation.Scope.Container == ObservationContainerKind.RetainerMarketListings &&
                !observation.IsStale)
            .ToArray();
        if (trustworthy.Length == 0)
        {
            error = "No current trusted retainer-listing observation exists for this character.";
            return false;
        }

        try
        {
            var revision = trustworthy.Max(observation => observation.Revision);
            var currentItems = trustworthy
                .SelectMany(observation => observation.Payload.Deserialize<RetainerMarketListingsPayload>(
                    ObservationPayloadContracts.RetainerMarketListings,
                    ObservationPayloadContracts.Version).Listings)
                .Where(listing => listing.ItemId != 0)
                .Select(listing => listing.ItemId)
                .Distinct()
                .ToHashSet();

            lock (gate)
            {
                if (owner != previousOwner)
                {
                    previousOwner = owner;
                    previousItems = new HashSet<uint>();
                    hasSuccessfulBaseline = false;
                    Interlocked.Exchange(ref changeObserved, 0);
                }

                var items = currentItems
                    .Concat(previousItems)
                    .Distinct()
                    .Order()
                    .Select(itemId => new RetainerListingRefreshCandidate(itemId, null))
                    .ToArray();
                var observedChange = Interlocked.Exchange(ref changeObserved, 0) != 0;
                snapshot = new RetainerListingRefreshSnapshot(
                    items,
                    $"franthropy:{revision}",
                    ComparisonAvailable: hasSuccessfulBaseline && observedChange);
                previousItems = currentItems;
                hasSuccessfulBaseline = true;
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ObservationPayloadContractException)
        {
            error = exception.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        client.RetainersChanged -= OnRetainersChanged;
    }

    private void OnRetainersChanged(object? sender, SharedRetainerObservationSnapshot snapshot)
    {
        var signature = CreateListingSignature(snapshot);
        lock (gate)
        {
            if (string.Equals(notifiedListingSignature, signature, StringComparison.Ordinal))
                return;
            notifiedListingSignature = signature;
            Interlocked.Exchange(ref changeObserved, 1);
        }

        var subscribers = Changed;
        if (subscribers is null)
            return;
        foreach (var subscriber in subscribers.GetInvocationList().Cast<Action>())
        {
            try { subscriber(); }
            catch { }
        }
    }

    private static string CreateListingSignature(SharedRetainerObservationSnapshot snapshot) =>
        $"{snapshot.Owner.LocalContentId:X16}:{snapshot.Owner.HomeWorldId}|" +
        string.Join('|', snapshot.Observations
            .Where(observation => observation.Scope.Container == ObservationContainerKind.RetainerMarketListings)
            .OrderBy(observation => observation.Scope.Subject.Id)
            .Select(observation => $"{observation.Scope.Subject.Id:X16}:{observation.Revision}:{observation.IsStale}"));

    private static Func<ObservationOwner?> CreateOwnerProvider(IPlayerState playerState)
    {
        ArgumentNullException.ThrowIfNull(playerState);
        return () => playerState.ContentId == 0 || !playerState.HomeWorld.IsValid
            ? null
            : new ObservationOwner(playerState.ContentId, playerState.HomeWorld.Value.RowId);
    }
}
