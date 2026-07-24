using MarketMafioso.Server.Auth;
using MarketMafioso.Server.MarketDiagnostics;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Endpoints;

internal static class MarketDiagnosticEndpoints
{
    public static void MapMarketDiagnosticEndpoints(this WebApplication app)
    {
        app.MapGet("/api/market-diagnostics/listings", ListActiveListings);
        app.MapGet("/api/market-diagnostics/episodes", ListEpisodes);
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
