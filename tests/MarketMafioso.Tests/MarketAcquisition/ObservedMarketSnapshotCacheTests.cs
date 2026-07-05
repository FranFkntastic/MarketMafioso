namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class ObservedMarketSnapshotCacheTests
{
    [Fact]
    public void TryGet_WhenKeyWasNeverStored_ReturnsMiss()
    {
        var cache = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotCache(
            maxEntries: 2,
            ttl: TimeSpan.FromMinutes(5));

        var lookup = cache.TryGet(
            new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotKey(2, "North-America", "Universalis"),
            DateTimeOffset.UnixEpoch);

        Assert.False(lookup.Found);
        Assert.Equal(MarketMafioso.MarketAcquisition.ObservedMarketSnapshotLookupStatus.Miss, lookup.Status);
        Assert.Null(lookup.Snapshot);
    }

    [Fact]
    public void TryGet_WhenSnapshotIsFresh_ReturnsRawListingsAndCacheMetadata()
    {
        var cache = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotCache(
            maxEntries: 2,
            ttl: TimeSpan.FromMinutes(5));
        var key = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotKey(2, "North-America", "Universalis");
        var listing = CreateListing("listing-1", quantity: 4, unitPrice: 80);

        cache.Replace(
            key,
            [listing],
            fetchedAtUtc: DateTimeOffset.UnixEpoch,
            sourceFreshness: "last upload 30s ago",
            diagnosticStatus: "Fresh",
            diagnosticSummary: "Fetched 1 listing.");

        var lookup = cache.TryGet(key, DateTimeOffset.UnixEpoch.AddMinutes(4));

        Assert.True(lookup.Found);
        Assert.Equal(MarketMafioso.MarketAcquisition.ObservedMarketSnapshotLookupStatus.Hit, lookup.Status);
        Assert.NotNull(lookup.Snapshot);
        Assert.Equal("last upload 30s ago", lookup.Snapshot.SourceFreshness);
        Assert.Equal("Fresh", lookup.Snapshot.DiagnosticStatus);
        Assert.Equal("Fetched 1 listing.", lookup.Snapshot.DiagnosticSummary);
        Assert.Equal(["listing-1"], lookup.Snapshot.Listings.Select(x => x.ListingId).ToArray());
    }

    [Fact]
    public void TryGet_WhenSnapshotExpired_ReturnsExpiredAndRemovesSnapshot()
    {
        var cache = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotCache(
            maxEntries: 2,
            ttl: TimeSpan.FromMinutes(5));
        var key = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotKey(2, "North-America", "Universalis");
        cache.Replace(
            key,
            [CreateListing("stale", quantity: 1, unitPrice: 1)],
            fetchedAtUtc: DateTimeOffset.UnixEpoch,
            sourceFreshness: "old",
            diagnosticStatus: "Fresh",
            diagnosticSummary: "Fetched.");

        var expired = cache.TryGet(key, DateTimeOffset.UnixEpoch.AddMinutes(6));
        var later = cache.TryGet(key, DateTimeOffset.UnixEpoch.AddMinutes(6));

        Assert.False(expired.Found);
        Assert.Equal(MarketMafioso.MarketAcquisition.ObservedMarketSnapshotLookupStatus.Expired, expired.Status);
        Assert.Null(expired.Snapshot);
        Assert.False(later.Found);
        Assert.Equal(MarketMafioso.MarketAcquisition.ObservedMarketSnapshotLookupStatus.Miss, later.Status);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Replace_ManualRefreshOverwritesRowsAndDiagnosticsForSameKey()
    {
        var cache = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotCache(
            maxEntries: 2,
            ttl: TimeSpan.FromMinutes(5));
        var key = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotKey(2, "North-America", "Universalis");
        cache.Replace(
            key,
            [CreateListing("old", quantity: 1, unitPrice: 1)],
            fetchedAtUtc: DateTimeOffset.UnixEpoch,
            sourceFreshness: "old source",
            diagnosticStatus: "Fresh",
            diagnosticSummary: "Old fetch.");

        cache.Replace(
            key,
            [CreateListing("new", quantity: 2, unitPrice: 2)],
            fetchedAtUtc: DateTimeOffset.UnixEpoch.AddMinutes(1),
            sourceFreshness: "new source",
            diagnosticStatus: "ManualRefresh",
            diagnosticSummary: "Manual refresh fetched 1 listing.");

        var lookup = cache.TryGet(key, DateTimeOffset.UnixEpoch.AddMinutes(1));

        Assert.True(lookup.Found);
        Assert.NotNull(lookup.Snapshot);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(1), lookup.Snapshot.FetchedAtUtc);
        Assert.Equal("new source", lookup.Snapshot.SourceFreshness);
        Assert.Equal("ManualRefresh", lookup.Snapshot.DiagnosticStatus);
        Assert.Equal(["new"], lookup.Snapshot.Listings.Select(x => x.ListingId).ToArray());
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Replace_WhenCapacityIsExceeded_EvictsLeastRecentlyUsedSnapshot()
    {
        var cache = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotCache(
            maxEntries: 2,
            ttl: TimeSpan.FromMinutes(30));
        var first = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotKey(2, "North-America", "Universalis");
        var second = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotKey(3, "North-America", "Universalis");
        var third = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotKey(4, "North-America", "Universalis");
        cache.Replace(first, [CreateListing("first", quantity: 1, unitPrice: 1, itemId: 2)], DateTimeOffset.UnixEpoch, "fresh", "Fresh", "Fetched.");
        cache.Replace(second, [CreateListing("second", quantity: 1, unitPrice: 1, itemId: 3)], DateTimeOffset.UnixEpoch, "fresh", "Fresh", "Fetched.");
        _ = cache.TryGet(first, DateTimeOffset.UnixEpoch.AddMinutes(1));

        cache.Replace(third, [CreateListing("third", quantity: 1, unitPrice: 1, itemId: 4)], DateTimeOffset.UnixEpoch.AddMinutes(2), "fresh", "Fresh", "Fetched.");

        Assert.True(cache.TryGet(first, DateTimeOffset.UnixEpoch.AddMinutes(2)).Found);
        Assert.False(cache.TryGet(second, DateTimeOffset.UnixEpoch.AddMinutes(2)).Found);
        Assert.True(cache.TryGet(third, DateTimeOffset.UnixEpoch.AddMinutes(2)).Found);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task ConcurrentTryGetReplaceAndCount_DoNotCorruptCache()
    {
        var cache = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotCache(
            maxEntries: 8,
            ttl: TimeSpan.FromMinutes(30));
        var tasks = Enumerable.Range(0, 8)
            .Select(worker => Task.Run(() =>
            {
                for (var i = 0; i < 250; i++)
                {
                    var itemId = (uint)((worker + i) % 16 + 1);
                    var key = new MarketMafioso.MarketAcquisition.ObservedMarketSnapshotKey(itemId, "North-America", "Universalis");
                    cache.Replace(
                        key,
                        [CreateListing($"listing-{worker}-{i}", quantity: 1, unitPrice: 1, itemId: itemId)],
                        DateTimeOffset.UnixEpoch.AddSeconds(i),
                        "fresh",
                        "Fresh",
                        "Fetched.");
                    _ = cache.TryGet(key, DateTimeOffset.UnixEpoch.AddSeconds(i + 1));
                    Assert.InRange(cache.Count, 0, 8);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        Assert.InRange(cache.Count, 0, 8);
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionListing CreateListing(
        string listingId,
        uint quantity,
        uint unitPrice,
        uint itemId = 2) =>
        new()
        {
            ItemId = itemId,
            ListingId = listingId,
            WorldName = "Siren",
            WorldId = 57,
            RetainerName = "Retainer",
            RetainerId = "retainer",
            Quantity = quantity,
            UnitPrice = unitPrice,
            LastReviewTimeUtc = DateTimeOffset.UnixEpoch,
        };
}
