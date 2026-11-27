using FFXIV_Craft_Architect.Core.Integrations.XivData;

namespace MarketMafioso.Server.Endpoints;

internal static class XivDataEndpoints
{
    private const int DefaultSearchLimit = 12;
    private const int MaximumSearchLimit = 50;

    public static void MapXivDataEndpoints(this WebApplication app)
    {
        app.MapGet("/api/xivdata/items/search", async (
            string? q,
            int? limit,
            IXivItemDataProvider itemData,
            ILoggerFactory loggerFactory,
            CancellationToken token) =>
        {
            var query = q?.Trim() ?? string.Empty;
            if (query.Length == 0)
                return Results.BadRequest(new { error = "query_required" });
            if (query.Length == 1 && !char.IsDigit(query[0]))
            {
                return Results.BadRequest(new XivDataErrorResponse(
                    "invalid_query",
                    "Query must contain at least two characters unless it is an item ID."));
            }

            var clampedLimit = Math.Clamp(limit ?? DefaultSearchLimit, 1, MaximumSearchLimit);
            try
            {
                var items = await itemData.SearchAsync(query, clampedLimit, token);
                return Results.Ok(new XivItemSearchResponse(items));
            }
            catch (HttpRequestException ex)
            {
                loggerFactory.CreateLogger("MarketMafioso.Server.XivData").LogWarning(
                    ex,
                    "Item search for '{Query}' failed: Garland Tools returned {UpstreamStatus}.",
                    query,
                    ex.StatusCode?.ToString() ?? "no response");
                return Results.Json(
                    new XivDataErrorResponse(
                        "upstream_unavailable",
                        $"The Garland item data source is unavailable ({ex.StatusCode?.ToString() ?? "no response"})."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("MarketMafioso.Server.XivData").LogWarning(
                    ex,
                    "Item search for '{Query}' failed: Garland Tools returned an unexpected response.",
                    query);
                return Results.Json(
                    new XivDataErrorResponse(
                        "upstream_invalid_response",
                        $"The Garland item data source returned an unexpected response ({ex.GetType().Name})."),
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}
