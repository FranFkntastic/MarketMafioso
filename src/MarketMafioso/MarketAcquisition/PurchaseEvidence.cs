using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketPurchaseCatalogId
{
    public static uint Normalize(uint rawCatalogId) =>
        rawCatalogId >= 1_000_000 ? rawCatalogId % 1_000_000 : rawCatalogId;
}

public sealed record MarketPurchasePacketObservation(
    long Sequence,
    DateTimeOffset ObservedAtUtc,
    uint RawCatalogId,
    uint ItemId,
    bool IsHighQuality,
    uint Quantity)
{
    public static MarketPurchasePacketObservation Create(
        long sequence,
        DateTimeOffset observedAtUtc,
        uint rawCatalogId,
        bool isHighQuality,
        uint quantity) =>
        new(sequence, observedAtUtc, rawCatalogId, MarketPurchaseCatalogId.Normalize(rawCatalogId), isHighQuality, quantity);
}

public sealed record MarketPurchaseIntent
{
    public string IntentId { get; init; } = string.Empty;
    public string RouteId { get; init; } = string.Empty;
    public string AttemptId { get; init; } = string.Empty;
    public string LineId { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public bool IsHighQuality { get; init; }
    public uint Quantity { get; init; }
    public string? ListingId { get; init; }
    public uint? IntendedUnitPrice { get; init; }
    public uint? IntendedWorldId { get; init; }
    public string? IntendedWorldName { get; init; }
    public DateTimeOffset ArmedAtUtc { get; init; }
    public long SequenceFloor { get; init; }
    public DateTimeOffset DeadlineUtc { get; init; }
}

public enum MarketPurchaseEvidenceStateKind
{
    Pending,
    Confirmed,
    TimedOutIndeterminate,
    ConflictingPacket,
}

public abstract record MarketPurchaseEvidenceState(MarketPurchaseEvidenceStateKind Kind, MarketPurchaseIntent Intent);
public sealed record Pending(MarketPurchaseIntent PendingIntent) : MarketPurchaseEvidenceState(MarketPurchaseEvidenceStateKind.Pending, PendingIntent);
public sealed record Confirmed(MarketPurchaseIntent ConfirmedIntent, MarketPurchasePacketObservation Evidence) : MarketPurchaseEvidenceState(MarketPurchaseEvidenceStateKind.Confirmed, ConfirmedIntent);
public sealed record TimedOutIndeterminate(MarketPurchaseIntent TimedOutIntent, DateTimeOffset TimedOutAtUtc) : MarketPurchaseEvidenceState(MarketPurchaseEvidenceStateKind.TimedOutIndeterminate, TimedOutIntent);
public sealed record ConflictingPacket(MarketPurchaseIntent ConflictingIntent, MarketPurchasePacketObservation Evidence) : MarketPurchaseEvidenceState(MarketPurchaseEvidenceStateKind.ConflictingPacket, ConflictingIntent);

public sealed record MarketPurchaseEvidenceSnapshot
{
    public MarketPurchaseEvidenceState? State { get; init; }
    public IReadOnlyList<MarketPurchasePacketObservation> Observations { get; init; } = [];
}

public interface IMarketPurchaseEvidenceStateStore
{
    MarketPurchaseEvidenceSnapshot? Load();
    void Save(MarketPurchaseEvidenceSnapshot snapshot);
}

public sealed class MarketPurchaseEvidenceCoordinator
{
    private readonly IMarketPurchaseEvidenceStateStore store;
    private readonly ConcurrentQueue<MarketPurchasePacketObservation> queued = new();
    private MarketPurchaseEvidenceSnapshot snapshot;

    public MarketPurchaseEvidenceCoordinator(IMarketPurchaseEvidenceStateStore store)
        : this(store, store.Load() ?? new MarketPurchaseEvidenceSnapshot())
    {
    }

    private MarketPurchaseEvidenceCoordinator(IMarketPurchaseEvidenceStateStore store, MarketPurchaseEvidenceSnapshot snapshot)
    {
        this.store = store;
        this.snapshot = Clone(snapshot);
    }

    public MarketPurchaseEvidenceState? State => snapshot.State;
    public MarketPurchaseEvidenceSnapshot Snapshot() => Clone(snapshot);

    public bool TryArm(MarketPurchaseIntent intent)
    {
        Validate(intent);
        if (snapshot.State is Pending)
            return false;

        var next = new MarketPurchaseEvidenceSnapshot
        {
            State = new Pending(intent),
            Observations = snapshot.Observations.ToArray(),
        };
        store.Save(next);
        snapshot = next;
        return true;
    }

    public void Enqueue(MarketPurchasePacketObservation observation) => queued.Enqueue(observation);

    public int DrainAndApply()
    {
        var count = 0;
        while (queued.TryDequeue(out var observation))
        {
            Apply(observation);
            count++;
        }
        return count;
    }

    public bool ExpireDue(DateTimeOffset nowUtc)
    {
        if (snapshot.State is not Pending pending || nowUtc < pending.Intent.DeadlineUtc)
            return false;

        Publish(new TimedOutIndeterminate(pending.Intent, nowUtc), snapshot.Observations);
        return true;
    }

    public static MarketPurchaseEvidenceCoordinator Restore(IMarketPurchaseEvidenceStateStore store, DateTimeOffset nowUtc)
    {
        var coordinator = new MarketPurchaseEvidenceCoordinator(store);
        coordinator.ExpireDue(nowUtc);
        return coordinator;
    }

    private void Apply(MarketPurchasePacketObservation observation)
    {
        var observations = snapshot.Observations.Append(observation).ToArray();
        if (snapshot.State is not Pending pending)
        {
            PersistObservations(observations);
            return;
        }
        if (observation.Sequence <= pending.Intent.SequenceFloor)
        {
            PersistObservations(observations);
            return;
        }

        var intent = pending.Intent;
        var matches = observation.ItemId == intent.ItemId
            && observation.IsHighQuality == intent.IsHighQuality
            && observation.Quantity == intent.Quantity;
        Publish(matches ? new Confirmed(intent, observation) : new ConflictingPacket(intent, observation), observations);
    }

    private void PersistObservations(IReadOnlyList<MarketPurchasePacketObservation> observations)
    {
        var next = new MarketPurchaseEvidenceSnapshot { State = snapshot.State, Observations = observations.ToArray() };
        store.Save(next);
        snapshot = next;
    }

    private void Publish(MarketPurchaseEvidenceState state, IReadOnlyList<MarketPurchasePacketObservation> observations)
    {
        var next = new MarketPurchaseEvidenceSnapshot { State = state, Observations = observations.ToArray() };
        store.Save(next);
        snapshot = next;
    }

    private static MarketPurchaseEvidenceSnapshot Clone(MarketPurchaseEvidenceSnapshot value) =>
        new() { State = value.State, Observations = value.Observations.ToArray() };

    private static void Validate(MarketPurchaseIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.IntentId) || string.IsNullOrWhiteSpace(intent.RouteId)
            || string.IsNullOrWhiteSpace(intent.AttemptId) || string.IsNullOrWhiteSpace(intent.LineId))
            throw new ArgumentException("Purchase intent identity fields are required.", nameof(intent));
        if (intent.ItemId == 0 || intent.Quantity == 0)
            throw new ArgumentException("Purchase intent item and quantity must be non-zero.", nameof(intent));
        if (intent.DeadlineUtc <= intent.ArmedAtUtc)
            throw new ArgumentException("Purchase intent deadline must follow its armed time.", nameof(intent));
    }
}
