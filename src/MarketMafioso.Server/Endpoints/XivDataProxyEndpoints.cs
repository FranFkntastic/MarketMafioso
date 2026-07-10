namespace MarketMafioso.Server.Endpoints;

internal static class XivDataProxyEndpoints
{
    public static void MapXivDataProxyEndpoints(this WebApplication app, string xivDataBaseUrl)
    {
        app.MapGet("/api/xivdata/items/search", async (
            IHttpClientFactory httpClientFactory,
            string q,
            int? limit,
            CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "query_required" });

            var client = httpClientFactory.CreateClient();
            var url = $"{xivDataBaseUrl}/items/search?q={Uri.EscapeDataString(q)}&limit={Math.Clamp(limit ?? 12, 1, 50)}";
            using var response = await client.GetAsync(url, token);
            var body = await response.Content.ReadAsStringAsync(token);
            return Results.Content(body, "application/json; charset=utf-8", statusCode: (int)response.StatusCode);
        });
    }
}
