using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.MarketAcquisition;

public sealed class UniversalisMarketAcquisitionPlanSource
{
    private static readonly Uri DefaultBaseUri = new("https://universalis.app/api/v2/");
    private readonly HttpClient httpClient;
    private readonly Uri baseUri;

    public UniversalisMarketAcquisitionPlanSource(HttpClient httpClient)
        : this(httpClient, DefaultBaseUri)
    {
    }

    public UniversalisMarketAcquisitionPlanSource(HttpClient httpClient, Uri baseUri)
    {
        this.httpClient = httpClient;
        this.baseUri = baseUri;
    }

    public async Task<IReadOnlyList<MarketAcquisitionListing>> FetchListingsAsync(
        string region,
        uint itemId,
        int listingLimit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(region))
            throw new InvalidOperationException("Region is required to fetch market listings.");

        if (itemId == 0)
            throw new InvalidOperationException("Item id is required to fetch market listings.");

        var normalizedRegion = NormalizeRegion(region);
        var limit = Math.Clamp(listingLimit, 1, 100);
        var requestUri = new Uri(baseUri, $"{Uri.EscapeDataString(normalizedRegion)}/{itemId}?listings={limit}");

        using var response = await httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!json.RootElement.TryGetProperty("listings", out var listingsElement) ||
            listingsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Universalis response did not include a listings array.");

        var listings = new List<MarketAcquisitionListing>();
        foreach (var listingElement in listingsElement.EnumerateArray())
        {
            var listingId = RequiredString(listingElement, "listingID");
            var worldName = RequiredString(listingElement, "worldName");
            var retainerName = RequiredString(listingElement, "retainerName");
            var retainerId = RequiredString(listingElement, "retainerID");
            var lastReviewTime = RequiredLong(listingElement, "lastReviewTime");

            listings.Add(new MarketAcquisitionListing
            {
                ListingId = listingId,
                WorldName = worldName,
                WorldId = RequiredUInt(listingElement, "worldID"),
                RetainerName = retainerName,
                RetainerId = retainerId,
                Quantity = RequiredUInt(listingElement, "quantity"),
                UnitPrice = RequiredUInt(listingElement, "pricePerUnit"),
                IsHq = RequiredBool(listingElement, "hq"),
                LastReviewTimeUtc = DateTimeOffset.FromUnixTimeSeconds(lastReviewTime),
            });
        }

        return listings;
    }

    private static string NormalizeRegion(string region) =>
        region.Trim().Replace(' ', '-');

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"Universalis listing is missing required string field {propertyName}.");

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Universalis listing field {propertyName} was empty.")
            : value;
    }

    private static uint RequiredUInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            !property.TryGetUInt32(out var value))
            throw new InvalidOperationException($"Universalis listing is missing required unsigned integer field {propertyName}.");

        return value;
    }

    private static long RequiredLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            !property.TryGetInt64(out var value))
            throw new InvalidOperationException($"Universalis listing is missing required integer field {propertyName}.");

        return value;
    }

    private static bool RequiredBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            throw new InvalidOperationException($"Universalis listing is missing required boolean field {propertyName}.");

        return property.GetBoolean();
    }
}
