using MarketMafioso.Server.Auth;
using MarketMafioso.Server.MarketDiagnostics;
using MarketMafioso.Server.Sqlite;
using MarketMafioso.Contracts;
using System.Text.Json;

namespace MarketMafioso.Server.Endpoints;

internal static class MarketDiagnosticEndpoints
{
    public static void MapMarketDiagnosticEndpoints(this WebApplication app)
    {
        app.MapGet("/api/market-diagnostics/listings", ListActiveListings);
        app.MapGet("/api/market-diagnostics/workbench", GetWorkbench);
        app.MapGet("/api/market-diagnostics/listings/{listingId:long}", GetListingDetail);
        app.MapGet("/api/market-diagnostics/episodes", ListEpisodes);
        app.MapGet("/api/market-diagnostics/region-conditions", ListRegionConditions);
        app.MapGet("/api/market-diagnostics/sales", ListSales);
        app.MapPost("/api/market-diagnostics/sales", RecordSale);
    }

    private static async Task<IResult> GetWorkbench(
        HttpContext context,
        MarketDiagnosticStore store,
        SqliteConnectionFactory connectionFactory,
        string? characterName,
        string? scope,
        string? search,
        CancellationToken token)
    {
        var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
        var listings = (await store.ListWorkbenchListingsAsync(accountIds, token))
            .Where(listing => MatchesWorkbenchScope(
                listing.CharacterName,
                listing.RetainerName,
                listing.ItemName,
                listing.World,
                characterName,
                scope,
                search))
            .ToArray();
        var sales = (await store.ListSaleEventsAsync(accountIds, null, 1000, token))
            .Where(sale => MatchesWorkbenchScope(
                sale.CharacterName,
                sale.RetainerName,
                sale.ItemName,
                sale.World,
                characterName,
                scope,
                search))
            .ToArray();
        var latestRegion = await store.ListRegionConditionsAsync(accountIds, null, 1, token);
        return Results.Ok(new MarketDiagnosticWorkbenchView
        {
            ActiveListings = listings,
            History = sales.Select(sale => new MarketDiagnosticSaleRow
            {
                Id = sale.Id,
                OwnedListingVersionId = sale.OwnedListingVersionId,
                Source = sale.Source,
                Confidence = sale.Confidence,
                RetainerName = sale.RetainerName,
                CharacterName = sale.CharacterName,
                World = sale.World,
                ItemId = sale.ItemId,
                ItemName = sale.ItemName,
                Quantity = sale.Quantity,
                IsHq = sale.IsHq,
                UnitPrice = sale.UnitPrice,
                TotalGil = sale.TotalGil,
                EventAtUtc = sale.EventAtUtc,
                EarliestEventAtUtc = sale.EarliestEventAtUtc,
                LatestEventAtUtc = sale.LatestEventAtUtc,
                CandidateCount = sale.CandidateCount,
                ObservedAtUtc = sale.ObservedAtUtc,
            }).ToArray(),
            CollectorUpdatedAtUtc = listings
                .Select(listing => listing.MarketObservedAtUtc)
                .Where(value => value.HasValue)
                .Max(),
            RegionObservedAtUtc = latestRegion.FirstOrDefault()?.ObservedAtUtc,
        });
    }

    private static bool MatchesWorkbenchScope(
        string? rowCharacterName,
        string? retainerName,
        string? itemName,
        string? world,
        string? characterName,
        string? scope,
        string? search)
    {
        if (!string.IsNullOrWhiteSpace(characterName) &&
            !string.IsNullOrWhiteSpace(rowCharacterName) &&
            !string.Equals(rowCharacterName, characterName, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(scope, "Player Inventory", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(scope) &&
            !string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(retainerName, scope, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(search))
            return true;

        var haystack = string.Join(' ', new[] { itemName, retainerName, world }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return search.Split(
                ' ',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<IResult> GetListingDetail(
        HttpContext context,
        MarketDiagnosticStore store,
        SqliteConnectionFactory connectionFactory,
        long listingId,
        CancellationToken token)
    {
        var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
        var detail = await store.GetListingDetailAsync(accountIds, listingId, token);
        return detail == null ? Results.NotFound() : Results.Ok(detail);
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
        DiagnosticEventStore diagnosticEvents,
        MarketDiagnosticAlertSink alertSink,
        IngestKeyAccountResolver accountResolver,
        CancellationToken token)
    {
        if (evidence.ItemId == 0 ||
            evidence.TotalGil == 0 ||
            evidence.EventAtUtc == default ||
            string.IsNullOrWhiteSpace(evidence.EvidenceId) ||
            evidence.EvidenceId.Length > 128 ||
            evidence.Source?.Length > 64 ||
            evidence.RetainerName?.Length > 64 ||
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
        if (!result.Duplicate)
        {
            await diagnosticEvents.WriteAsync(
                new DiagnosticEventCreate
                {
                    Source = "MarketMafioso",
                    Category = "MarketDiagnostics",
                    Type = "SaleConfirmed",
                    Severity = "Info",
                    Outcome = "Confirmed",
                    Message =
                        $"SaleConfirmed: {evidence.ItemName ?? $"item {evidence.ItemId}"} on " +
                        $"{evidence.HomeWorld ?? "unknown world"}; {evidence.TotalGil:N0} gil.",
                    CorrelationId = result.OwnedListingVersionId is { } listingId
                        ? $"market-listing:{listingId}"
                        : $"market-sale:{result.Id}",
                    AccountId = accountId,
                    ItemId = evidence.ItemId,
                    ItemName = evidence.ItemName,
                    World = evidence.HomeWorld,
                    CharacterName = evidence.CharacterName,
                    PayloadSummaryJson = JsonSerializer.Serialize(new
                    {
                        evidence.Source,
                        evidence.RetainerId,
                        evidence.RetainerName,
                        evidence.Quantity,
                        evidence.UnitPrice,
                        evidence.TotalGil,
                        evidence.EventAtUtc,
                        result.OwnedListingVersionId,
                    }),
                },
                token);
            await alertSink.SendSaleAsync(evidence, result, token);
        }
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
