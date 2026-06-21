using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.Services;

public sealed class RetainerSnapshotStore
{
    private readonly ConcurrentDictionary<ulong, RetainerSnapshot> _snapshots = new();
    private readonly object _latestLock = new();
    private ulong? _latestRetainerId;

    public int Count => _snapshots.Count;

    public void Upsert(RetainerSnapshot snapshot)
    {
        _snapshots[snapshot.RetainerId] = snapshot;

        lock (_latestLock)
        {
            if (!_latestRetainerId.HasValue)
            {
                _latestRetainerId = snapshot.RetainerId;
                return;
            }

            if (!_snapshots.TryGetValue(_latestRetainerId.Value, out var currentLatest)
                || snapshot.CapturedAt >= currentLatest.CapturedAt)
            {
                _latestRetainerId = snapshot.RetainerId;
            }
        }
    }

    public bool TryGet(ulong retainerId, out RetainerSnapshot snapshot)
    {
        return _snapshots.TryGetValue(retainerId, out snapshot!);
    }

    public void Clear()
    {
        _snapshots.Clear();
        lock (_latestLock)
        {
            _latestRetainerId = null;
        }
    }

    public IReadOnlyList<RetainerSnapshot> GetAll()
    {
        return _snapshots.Values.OrderBy(x => x.RetainerName, StringComparer.Ordinal).ToList();
    }

    public bool TryGetLatest(out RetainerSnapshot snapshot)
    {
        lock (_latestLock)
        {
            if (_latestRetainerId.HasValue
                && _snapshots.TryGetValue(_latestRetainerId.Value, out snapshot!))
            {
                return true;
            }
        }

        snapshot = default!;
        return false;
    }
}

public sealed record RetainerSnapshot(
    ulong RetainerId,
    string RetainerName,
    DateTimeOffset CapturedAt,
    IReadOnlyList<RetainerListingSnapshot> Listings);

public sealed record RetainerListingSnapshot(
    uint ItemId,
    string ItemName,
    bool IsHq,
    uint Quantity,
    ulong UnitPrice,
    uint Slot);
