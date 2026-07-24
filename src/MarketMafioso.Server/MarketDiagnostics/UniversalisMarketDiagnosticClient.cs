using System.Net;
using System.Text.Json;

namespace MarketMafioso.Server.MarketDiagnostics;

public sealed class UniversalisMarketDiagnosticClient(IHttpClientFactory httpClientFactory)
{
    private static readonly Uri BaseUri = new("https://universalis.app/api/v2/");
    private static readonly TimeSpan MinimumRequestSpacing = TimeSpan.FromMilliseconds(50);
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private DateTimeOffset nextRequestAtUtc = DateTimeOffset.MinValue;
    private int consecutiveFailures;

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

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateExceptionAsync(response, requestUri, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(document.RootElement);
    }

    public async Task<IReadOnlyList<RegionMarketCondition>> FetchRegionConditionsAsync(
        string region,
        IReadOnlyCollection<uint> itemIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(region))
            throw new InvalidOperationException("A region is required for a market diagnostic request.");

        var normalizedItemIds = itemIds.Where(itemId => itemId != 0).Distinct().Take(100).ToArray();
        if (normalizedItemIds.Length == 0)
            return [];

        var encodedRegion = Uri.EscapeDataString(region.Trim());
        var encodedItems = string.Join(",", normalizedItemIds);
        var requestUri = new Uri(BaseUri, $"aggregated/{encodedRegion}/{encodedItems}");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd("MarketMafioso-MarketDiagnostics/1.0");

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateExceptionAsync(response, requestUri, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseRegionConditions(document.RootElement);
    }

    public async Task<IReadOnlyList<UniversalisSaleEvidence>> FetchSaleHistoryAsync(
        string world,
        uint itemId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(world))
            throw new InvalidOperationException("A world is required for a market sale-history request.");
        if (itemId == 0)
            return [];

        var requestUri = new Uri(
            BaseUri,
            $"history/{Uri.EscapeDataString(world.Trim())}/{itemId}");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd("MarketMafioso-MarketDiagnostics/1.0");

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateExceptionAsync(response, requestUri, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseSaleHistory(itemId, document.RootElement);
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

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var delay = nextRequestAtUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            HttpResponseMessage response;
            try
            {
                response = await httpClientFactory
                    .CreateClient(nameof(UniversalisMarketDiagnosticClient))
                    .SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or TaskCanceledException)
            {
                RegisterFailure(retryAfterUtc: null);
                throw;
            }

            if (response.IsSuccessStatusCode)
            {
                consecutiveFailures = 0;
                nextRequestAtUtc = DateTimeOffset.UtcNow + MinimumRequestSpacing;
            }
            else if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                     (int)response.StatusCode >= 500)
            {
                var retryAfterUtc = response.Headers.RetryAfter?.Date ??
                    (response.Headers.RetryAfter?.Delta is { } retryAfter
                        ? DateTimeOffset.UtcNow + retryAfter
                        : null);
                RegisterFailure(retryAfterUtc);
            }
            else
            {
                consecutiveFailures = 0;
                nextRequestAtUtc = DateTimeOffset.UtcNow + MinimumRequestSpacing;
            }

            return response;
        }
        finally
        {
            requestGate.Release();
        }
    }

    private void RegisterFailure(DateTimeOffset? retryAfterUtc)
    {
        consecutiveFailures = Math.Min(consecutiveFailures + 1, 8);
        var exponentialBackoff = DateTimeOffset.UtcNow + CalculateBackoff(consecutiveFailures);
        nextRequestAtUtc = retryAfterUtc is { } retryAfter && retryAfter > exponentialBackoff
            ? retryAfter
            : exponentialBackoff;
    }

    internal static TimeSpan CalculateBackoff(int consecutiveFailures) =>
        TimeSpan.FromSeconds(Math.Min(300, 1 << Math.Clamp(consecutiveFailures, 1, 8)));

    internal static IReadOnlyList<RegionMarketCondition> ParseRegionConditions(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var conditions = new List<RegionMarketCondition>();
        foreach (var item in results.EnumerateArray())
        {
            if (!TryReadUInt(item, "itemId", out var itemId))
                continue;

            DateTimeOffset? freshestUpload = null;
            if (item.TryGetProperty("worldUploadTimes", out var uploads) &&
                uploads.ValueKind == JsonValueKind.Array)
            {
                foreach (var upload in uploads.EnumerateArray())
                {
                    if (!TryReadLong(upload, "timestamp", out var timestamp) || timestamp <= 0)
                        continue;

                    var uploadedAt = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
                    if (!freshestUpload.HasValue || uploadedAt > freshestUpload)
                        freshestUpload = uploadedAt;
                }
            }

            conditions.Add(ParseRegionCondition(item, itemId, isHq: false, "nq", freshestUpload));
            conditions.Add(ParseRegionCondition(item, itemId, isHq: true, "hq", freshestUpload));
        }

        return conditions;
    }

    internal static IReadOnlyList<UniversalisSaleEvidence> ParseSaleHistory(uint itemId, JsonElement root)
    {
        if (!root.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var sales = new List<UniversalisSaleEvidence>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (!TryReadUInt(entry, "pricePerUnit", out var unitPrice) ||
                !TryReadUInt(entry, "quantity", out var quantity) ||
                !TryReadLong(entry, "timestamp", out var timestamp) ||
                timestamp <= 0 ||
                !TryReadBool(entry, "hq", out var isHq))
            {
                continue;
            }

            sales.Add(new UniversalisSaleEvidence
            {
                ItemId = itemId,
                UnitPrice = unitPrice,
                Quantity = quantity,
                IsHq = isHq,
                BuyerName = ReadOptionalString(entry, "buyerName"),
                OnMannequin = TryReadBool(entry, "onMannequin", out var onMannequin) && onMannequin,
                SoldAtUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp),
            });
        }

        return sales;
    }

    private static RegionMarketCondition ParseRegionCondition(
        JsonElement item,
        uint itemId,
        bool isHq,
        string qualityProperty,
        DateTimeOffset? freshestUpload)
    {
        if (!item.TryGetProperty(qualityProperty, out var quality) ||
            quality.ValueKind != JsonValueKind.Object)
        {
            return new RegionMarketCondition
            {
                ItemId = itemId,
                IsHq = isHq,
                FreshestWorldUploadAtUtc = freshestUpload,
            };
        }

        var minimumListing = ReadRegionMetric(quality, "minListing");
        var recentPurchase = ReadRegionMetric(quality, "recentPurchase");
        var averageSalePrice = ReadRegionMetric(quality, "averageSalePrice");
        var dailySaleVelocity = ReadRegionMetric(quality, "dailySaleVelocity");
        return new RegionMarketCondition
        {
            ItemId = itemId,
            IsHq = isHq,
            MinimumListingPrice = ReadOptionalUInt(minimumListing, "price"),
            MinimumListingWorldId = ReadOptionalUInt(minimumListing, "worldId"),
            AverageSalePrice = ReadOptionalDouble(averageSalePrice, "price"),
            DailySaleVelocity = ReadOptionalDouble(dailySaleVelocity, "quantity"),
            RecentPurchasePrice = ReadOptionalUInt(recentPurchase, "price"),
            RecentPurchaseWorldId = ReadOptionalUInt(recentPurchase, "worldId"),
            RecentPurchaseAtUtc = ReadOptionalUnixMilliseconds(recentPurchase, "timestamp"),
            FreshestWorldUploadAtUtc = freshestUpload,
        };
    }

    private static JsonElement ReadRegionMetric(JsonElement quality, string propertyName)
    {
        if (quality.TryGetProperty(propertyName, out var metric) &&
            metric.ValueKind == JsonValueKind.Object &&
            metric.TryGetProperty("region", out var region) &&
            region.ValueKind == JsonValueKind.Object)
        {
            return region;
        }

        return default;
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
            MinimumNqPrice = TryReadUInt(item, "minPriceNQ", out var minimumNqPrice) && minimumNqPrice > 0
                ? minimumNqPrice
                : null,
            MinimumHqPrice = TryReadUInt(item, "minPriceHQ", out var minimumHqPrice) && minimumHqPrice > 0
                ? minimumHqPrice
                : null,
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

    private static string? ReadOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

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

    private static uint? ReadOptionalUInt(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        TryReadUInt(element, propertyName, out var value)
            ? value
            : null;

    private static double? ReadOptionalDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) => value,
            _ => null,
        };
    }

    private static DateTimeOffset? ReadOptionalUnixMilliseconds(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        TryReadLong(element, propertyName, out var timestamp) &&
        timestamp > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
            : null;
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
