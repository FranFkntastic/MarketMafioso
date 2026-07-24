using System.Net;
using System.Text.Json;

namespace MarketMafioso.Server.MarketDiagnostics;

public sealed class UniversalisMarketDiagnosticClient(IHttpClientFactory httpClientFactory)
{
    private static readonly Uri BaseUri = new("https://universalis.app/api/v2/");

    public async Task<IReadOnlyDictionary<uint, UniversalisItemEvidence>> FetchWorldAsync(
        string world,
        IReadOnlyCollection<uint> itemIds,
        int listingLimit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(world))
            throw new InvalidOperationException("A world is required for a market diagnostic request.");

        var normalizedItemIds = itemIds.Where(itemId => itemId != 0).Distinct().Take(100).ToArray();
        if (normalizedItemIds.Length == 0)
            return new Dictionary<uint, UniversalisItemEvidence>();

        var encodedWorld = Uri.EscapeDataString(world.Trim());
        var encodedItems = string.Join(",", normalizedItemIds);
        var limit = Math.Clamp(listingLimit, 1, 100);
        var requestUri = new Uri(BaseUri, $"{encodedWorld}/{encodedItems}?listings={limit}&entries=0");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd("MarketMafioso-MarketDiagnostics/1.0");

        using var response = await httpClientFactory
            .CreateClient(nameof(UniversalisMarketDiagnosticClient))
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateExceptionAsync(response, requestUri, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(document.RootElement);
    }

    internal static IReadOnlyDictionary<uint, UniversalisItemEvidence> Parse(JsonElement root)
    {
        var results = new Dictionary<uint, UniversalisItemEvidence>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in items.EnumerateObject())
            {
                if (uint.TryParse(item.Name, out var itemId))
                    results[itemId] = ParseItem(itemId, item.Value);
            }

            return results;
        }

        if (TryReadUInt(root, "itemID", out var singleItemId))
            results[singleItemId] = ParseItem(singleItemId, root);

        return results;
    }

    private static UniversalisItemEvidence ParseItem(uint itemId, JsonElement item)
    {
        DateTimeOffset? uploadedAt = null;
        if (TryReadLong(item, "lastUploadTime", out var uploadMilliseconds) && uploadMilliseconds > 0)
            uploadedAt = DateTimeOffset.FromUnixTimeMilliseconds(uploadMilliseconds);

        var listings = new List<UniversalisListingEvidence>();
        if (item.TryGetProperty("listings", out var listingArray) &&
            listingArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var listing in listingArray.EnumerateArray())
            {
                if (!TryReadUInt(listing, "pricePerUnit", out var price) ||
                    !TryReadUInt(listing, "quantity", out var quantity) ||
                    !TryReadLong(listing, "lastReviewTime", out var reviewSeconds) ||
                    !TryReadBool(listing, "hq", out var isHq))
                {
                    continue;
                }

                listings.Add(new UniversalisListingEvidence
                {
                    ItemId = itemId,
                    ListingId = ReadIdentifier(listing, "listingID"),
                    RetainerId = ReadIdentifier(listing, "retainerID"),
                    RetainerName = ReadString(listing, "retainerName"),
                    UnitPrice = price,
                    Quantity = quantity,
                    IsHq = isHq,
                    ReviewedAtUtc = DateTimeOffset.FromUnixTimeSeconds(reviewSeconds),
                });
            }
        }

        return new UniversalisItemEvidence
        {
            ItemId = itemId,
            UploadedAtUtc = uploadedAt,
            Listings = listings,
        };
    }

    private static async Task<UniversalisMarketDiagnosticException> CreateExceptionAsync(
        HttpResponseMessage response,
        Uri requestUri,
        CancellationToken cancellationToken)
    {
        var body = response.Content == null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var retryAfterUtc = response.Headers.RetryAfter?.Date ??
            (response.Headers.RetryAfter?.Delta is { } delta ? DateTimeOffset.UtcNow.Add(delta) : null);
        return new UniversalisMarketDiagnosticException(
            response.StatusCode,
            requestUri,
            body,
            retryAfterUtc);
    }

    private static string ReadIdentifier(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return string.Empty;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty,
        };
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static bool TryReadUInt(JsonElement element, string propertyName, out uint value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
               (property.TryGetUInt32(out value) ||
                (property.ValueKind == JsonValueKind.String &&
                 uint.TryParse(property.GetString(), out value)));
    }

    private static bool TryReadLong(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
               (property.TryGetInt64(out value) ||
                (property.ValueKind == JsonValueKind.String &&
                 long.TryParse(property.GetString(), out value)));
    }

    private static bool TryReadBool(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               bool.TryParse(property.GetString(), out value);
    }
}

public sealed class UniversalisMarketDiagnosticException(
    HttpStatusCode statusCode,
    Uri requestUri,
    string? responseBody,
    DateTimeOffset? retryAfterUtc)
    : HttpRequestException(
        $"Universalis market diagnostics failed with {(int)statusCode} {statusCode} at {requestUri}.",
        null,
        statusCode)
{
    public Uri RequestUri { get; } = requestUri;
    public string? ResponseBody { get; } = responseBody;
    public DateTimeOffset? RetryAfterUtc { get; } = retryAfterUtc;
}
