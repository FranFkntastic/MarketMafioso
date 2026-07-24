using MarketMafioso.Server.Auth;
using MarketMafioso.Server.MarketDiagnostics;
using MarketMafioso.Server.Sqlite;
using MarketMafioso.Contracts;

namespace MarketMafioso.Server.Endpoints;

internal static class MarketDiagnosticEndpoints
{
    public static void MapMarketDiagnosticEndpoints(this WebApplication app)
    {
        app.MapGet("/api/market-diagnostics/listings", ListActiveListings);
        app.MapGet("/api/market-diagnostics/episodes", ListEpisodes);
        app.MapGet("/api/market-diagnostics/region-conditions", ListRegionConditions);
        app.MapGet("/api/market-diagnostics/sales", ListSales);
        app.MapPost("/api/market-diagnostics/sales", RecordSale);
    }

    private static async Task<IResult> ListActiveListings(
        HttpContext context,
        MarketDiagnosticStore store,
        SqliteConnectionFactory connectionFactory,
        CancellationToken token)
    {
        var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
        var allowed = accountIds.ToHashSet();
        var listings = await store.ListActiveOwnedListingsAsync(token);
        return Results.Ok(listings.Where(listing => allowed.Contains(listing.AccountId)));
    }

    private static async Task<IResult> ListEpisodes(
        HttpContext context,
        MarketDiagnosticStore store,
        SqliteConnectionFactory connectionFactory,
        bool? openOnly,
        int? limit,
        CancellationToken token)
    {
        var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
        return Results.Ok(await store.ListEpisodesAsync(
            accountIds,
            openOnly ?? false,
            limit ?? 250,
            token));
    }

    private static async Task<IResult> ListRegionConditions(
        HttpContext context,
        MarketDiagnosticStore store,
        SqliteConnectionFactory connectionFactory,
        uint? itemId,
        int? limit,
        CancellationToken token)
    {
        var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
        return Results.Ok(await store.ListRegionConditionsAsync(
            accountIds,
            itemId,
            limit ?? 1000,
            token));
    }

    private static async Task<IResult> ListSales(
        HttpContext context,
        MarketDiagnosticStore store,
        SqliteConnectionFactory connectionFactory,
        string? confidence,
        int? limit,
        CancellationToken token)
    {
        var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
        return Results.Ok(await store.ListSaleEventsAsync(
            accountIds,
            confidence,
            limit ?? 250,
            token));
    }

    private static async Task<IResult> RecordSale(
        HttpRequest request,
        RetainerSaleEvidenceCreateRequest evidence,
        MarketDiagnosticStore store,
        IngestKeyAccountResolver accountResolver,
        CancellationToken token)
    {
        if (evidence.ItemId == 0 ||
            evidence.TotalGil == 0 ||
            string.IsNullOrWhiteSpace(evidence.EvidenceId) ||
            evidence.EvidenceId.Length > 128 ||
            evidence.RawMessage?.Length > 2000)
        {
            return Results.BadRequest(new { error = "invalid_sale_evidence" });
        }

        var suppliedApiKey = request.Headers["X-Api-Key"].Count == 1
            ? request.Headers["X-Api-Key"][0]
            : null;
        var accountId = await accountResolver.ResolveAccountIdAsync(suppliedApiKey, token) ?? 1;
        var result = await store.RecordConfirmedSaleAsync(
            accountId,
            evidence,
            DateTimeOffset.UtcNow,
            token);
        return result.Duplicate
            ? Results.Ok(result)
            : Results.Created($"{request.PathBase}/api/market-diagnostics/sales/{result.Id}", result);
    }

    private static async Task<IReadOnlyList<long>> GetAccountIdsAsync(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        CancellationToken token)
    {
        if (!context.Items.TryGetValue(DashboardSessionStore.DashboardUserIdItemKey, out var value) ||
            value is not long userId)
        {
            return [1];
        }

        var accounts = new List<long>();
        await using var connection = await connectionFactory.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account_id
            FROM dashboard_user_accounts
            WHERE dashboard_user_id = $dashboardUserId
            ORDER BY is_default DESC, account_id;
            """;
        command.Parameters.AddWithValue("$dashboardUserId", userId);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            accounts.Add(reader.GetInt64(0));

        return accounts.Count == 0 ? [1] : accounts;
    }
}
