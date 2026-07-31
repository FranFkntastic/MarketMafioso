using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Franthropy.FFXIV.Market;

namespace MarketMafioso.MarketAcquisition;

public sealed class UniversalisMarketAcquisitionPlanSource : IMarketAcquisitionListingSource
{
    private readonly UniversalisBulkClient bulkClient;

    public UniversalisMarketAcquisitionPlanSource(HttpClient httpClient)
        : this(new UniversalisBulkClient(httpClient))
    {
    }

    public UniversalisMarketAcquisitionPlanSource(HttpClient httpClient, Uri baseUri)
        : this(new UniversalisBulkClient(httpClient, baseUri))
    {
    }

    internal UniversalisMarketAcquisitionPlanSource(UniversalisBulkClient bulkClient)
    {
        this.bulkClient = bulkClient ?? throw new ArgumentNullException(nameof(bulkClient));
    }

    public async Task<IReadOnlyDictionary<uint, IReadOnlyList<MarketAcquisitionListing>>> FetchListingsAsync(
        string worldDataCenterOrRegion,
        IReadOnlyCollection<uint> itemIds,
        int listingLimit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worldDataCenterOrRegion))
            throw new InvalidOperationException("A world, data center, or region is required to fetch market listings.");
        if (itemIds.Count == 0)
            return new Dictionary<uint, IReadOnlyList<MarketAcquisitionListing>>();
        if (itemIds.Any(itemId => itemId == 0))
            throw new InvalidOperationException("Item IDs are required to fetch market listings.");

        var result = await bulkClient.FetchAsync<UniversalisItemResponse>(
            new UniversalisBulkRequest
            {
                WorldOrDataCenter = worldDataCenterOrRegion,
                ItemIds = itemIds,
                ListingsPerItem = Math.Clamp(listingLimit, 1, 100),
                HistoryEntriesPerItem = 0,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.MissingItemIds.Count > 0)
            throw new UniversalisMarketListingsUnavailableException(result.MissingItemIds, result.Failures);

        return result.Items.ToDictionary(
            pair => pair.Key,
            pair => MapListings(pair.Key, pair.Value));
    }

    private static IReadOnlyList<MarketAcquisitionListing> MapListings(
        uint itemId,
        UniversalisItemResponse item)
    {
        if (item.Listings is null)
            throw new InvalidOperationException($"Universalis response for item {itemId} had no listings array.");
        return item.Listings.Select(listing => MapListing(itemId, listing)).ToArray();
    }

    private static MarketAcquisitionListing MapListing(uint itemId, UniversalisListingResponse listing)
    {
        if (string.IsNullOrWhiteSpace(listing.ListingId))
            throw new InvalidOperationException($"Universalis listing for item {itemId} had no listing ID.");
        if (string.IsNullOrWhiteSpace(listing.WorldName))
            throw new InvalidOperationException($"Universalis listing {listing.ListingId} had no world name.");
        if (string.IsNullOrWhiteSpace(listing.RetainerName))
            throw new InvalidOperationException($"Universalis listing {listing.ListingId} had no retainer name.");
        if (string.IsNullOrWhiteSpace(listing.RetainerId))
            throw new InvalidOperationException($"Universalis listing {listing.ListingId} had no retainer ID.");
        if (listing.WorldId is null)
            throw new InvalidOperationException($"Universalis listing {listing.ListingId} had no world ID.");
        if (listing.Quantity is null)
            throw new InvalidOperationException($"Universalis listing {listing.ListingId} had no quantity.");
        if (listing.UnitPrice is null)
            throw new InvalidOperationException($"Universalis listing {listing.ListingId} had no unit price.");
        if (listing.IsHq is null)
            throw new InvalidOperationException($"Universalis listing {listing.ListingId} had no explicit HQ flag.");
        if (listing.LastReviewTime is null)
            throw new InvalidOperationException($"Universalis listing {listing.ListingId} had no review time.");

        return new MarketAcquisitionListing
        {
            ItemId = itemId,
            ListingId = listing.ListingId,
            WorldName = listing.WorldName,
            WorldId = listing.WorldId.Value,
            RetainerName = listing.RetainerName,
            RetainerId = listing.RetainerId,
            Quantity = listing.Quantity.Value,
            UnitPrice = listing.UnitPrice.Value,
            IsHq = listing.IsHq.Value,
            LastReviewTimeUtc = DateTimeOffset.FromUnixTimeSeconds(listing.LastReviewTime.Value),
        };
    }

    private sealed record UniversalisItemResponse
    {
        [JsonPropertyName("listings")]
        public IReadOnlyList<UniversalisListingResponse>? Listings { get; init; }
    }

    private sealed record UniversalisListingResponse
    {
        [JsonPropertyName("listingID")]
        public string ListingId { get; init; } = string.Empty;

        [JsonPropertyName("worldName")]
        public string WorldName { get; init; } = string.Empty;

        [JsonPropertyName("worldID")]
        public uint? WorldId { get; init; }

        [JsonPropertyName("retainerName")]
        public string RetainerName { get; init; } = string.Empty;

        [JsonPropertyName("retainerID")]
        public string RetainerId { get; init; } = string.Empty;

        [JsonPropertyName("quantity")]
        public uint? Quantity { get; init; }

        [JsonPropertyName("pricePerUnit")]
        public uint? UnitPrice { get; init; }

        [JsonPropertyName("hq")]
        public bool? IsHq { get; init; }

        [JsonPropertyName("lastReviewTime")]
        public long? LastReviewTime { get; init; }
    }
}

public sealed class UniversalisMarketListingsUnavailableException : HttpRequestException
{
    public UniversalisMarketListingsUnavailableException(
        IReadOnlyList<uint> missingItemIds,
        IReadOnlyList<UniversalisBulkFailure> failures)
        : base(BuildMessage(missingItemIds, failures))
    {
        MissingItemIds = missingItemIds;
        Failures = failures;
    }

    public IReadOnlyList<uint> MissingItemIds { get; }

    public IReadOnlyList<UniversalisBulkFailure> Failures { get; }

    private static string BuildMessage(
        IReadOnlyList<uint> missingItemIds,
        IReadOnlyList<UniversalisBulkFailure> failures)
    {
        var message = $"Universalis did not return item(s) {string.Join(", ", missingItemIds)} after retry.";
        var detail = failures.FirstOrDefault()?.Message;
        return string.IsNullOrWhiteSpace(detail) ? message : $"{message} {detail}";
    }
}
