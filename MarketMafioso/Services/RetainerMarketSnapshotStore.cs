using System;
using System.Collections.Generic;

namespace MarketMafioso.Services;

public sealed class RetainerMarketSnapshotStore
{
    private readonly Dictionary<ulong, RetainerMarketSnapshot> _snapshots = new();

    public int Count => _snapshots.Count;

    public void Upsert(RetainerMarketSnapshot snapshot)
    {
        _snapshots[snapshot.RetainerId] = snapshot;
    }

    public bool TryGet(ulong retainerId, out RetainerMarketSnapshot snapshot)
    {
        return _snapshots.TryGetValue(retainerId, out snapshot!);
    }

    public IReadOnlyList<RetainerMarketSnapshot> GetAll()
    {
        var list = new List<RetainerMarketSnapshot>(_snapshots.Values);
        list.Sort((left, right) => string.CompareOrdinal(left.RetainerName, right.RetainerName));
        return list;
    }

    public void Clear()
    {
        _snapshots.Clear();
    }
}

public sealed record RetainerMarketSnapshot(
    ulong RetainerId,
    string RetainerName,
    DateTimeOffset CapturedAt,
    IReadOnlyList<ItemMarketSnapshot> Items);

public sealed record ItemMarketSnapshot(
    uint ItemId,
    string ItemName,
    IReadOnlyList<MarketListingSnapshot> Listings);

public sealed record MarketListingSnapshot(
    ulong ListingId,
    ulong SellingRetainerContentId,
    string SellingRetainerName,
    uint UnitPrice,
    uint Quantity,
    bool IsHq,
    byte TownId,
    uint TotalTax);
