using System.Globalization;
using MarketMafioso.Contracts.MarketIntelligence;
using MarketMafioso.Server.Auth;
using MarketMafioso.Server.MarketIntelligence;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Endpoints;

internal static class MarketIntelligenceEndpoints
{
    public static void MapMarketIntelligenceEndpoints(this WebApplication app)
    {
        app.MapPost("/api/market-intelligence/evidence", IngestEvidence);
        app.MapPost("/api/market-intelligence/import-receipts", RecordImportReceipt);
        app.MapPost("/api/market-intelligence/rebuild", Rebuild);
        app.MapGet("/api/market-intelligence/ledger", GetLedger);
        app.MapGet("/api/market-intelligence/sources", () => Results.Ok(MarketEvidenceSourceRegistry.View));
        app.MapGet("/api/market-intelligence/markets/{worldName}/{itemId}", GetMarketDetail);
        app.MapPut("/api/market-intelligence/markets/{worldName}/{itemId}/annotation", UpdateAnnotation);
        app.MapGet("/api/market-intelligence/events/stream", StreamEvents);
    }

    private static async Task<IResult> IngestEvidence(
        HttpRequest httpRequest,
        MarketEvidenceUploadRequest request,
        bool? deferProjection,
        IngestKeyAccountResolver accounts,
        MarketIntelligenceStore store,
        CancellationToken token)
    {
        try
        {
            var accountId = await accounts.ResolveAccountIdAsync(httpRequest.Headers["X-Api-Key"].SingleOrDefault(), token);
            if (accountId is null)
                return Results.Unauthorized();
            if (deferProjection == true && request.SourceKind != MarketEvidenceSources.LegacyRouteImport)
                return Results.BadRequest(new { error = "Only the historical importer may defer projection." });
            return Results.Ok(await store.IngestAsync(accountId.Value, request, token, projectImmediately: deferProjection != true));
        }
        catch (MarketEvidenceIdempotencyConflictException exception)
        {
            return Results.Conflict(new { error = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<IResult> RecordImportReceipt(
        HttpRequest request,
        MarketIntelligenceImportReceiptRequest receipt,
        IngestKeyAccountResolver accounts,
        MarketIntelligenceStore store,
        CancellationToken token)
    {
        try
        {
            var accountId = await accounts.ResolveAccountIdAsync(request.Headers["X-Api-Key"].SingleOrDefault(), token);
            if (accountId is null)
                return Results.Unauthorized();
            await store.RecordImportReceiptAsync(accountId.Value, receipt, token);
            return Results.NoContent();
        }
        catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
    }

    private static async Task<IResult> Rebuild(
        HttpRequest request,
        IngestKeyAccountResolver accounts,
        MarketIntelligenceStore store,
        CancellationToken token)
    {
        var accountId = await accounts.ResolveAccountIdAsync(request.Headers["X-Api-Key"].SingleOrDefault(), token);
        return accountId is null
            ? Results.Unauthorized()
            : Results.Ok(new { revision = await store.ProjectDeferredAccountAsync(accountId.Value, token) });
    }

    private static async Task<IResult> GetLedger(
        HttpContext context,
        SqliteConnectionFactory connections,
        MarketIntelligenceStore store,
        CancellationToken token) =>
        Results.Ok(await store.GetLedgerAsync(await GetAccountIdsAsync(context, connections, token), token));

    private static async Task<IResult> GetMarketDetail(
        HttpContext context,
        string worldName,
        uint itemId,
        SqliteConnectionFactory connections,
        MarketIntelligenceStore store,
        CancellationToken token)
    {
        var detail = await store.GetDetailAsync(await GetAccountIdsAsync(context, connections, token), itemId, worldName, token);
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    private static async Task<IResult> UpdateAnnotation(
        HttpContext context,
        string worldName,
        uint itemId,
        MarketIntelligenceAnnotationUpdate update,
        SqliteConnectionFactory connections,
        MarketIntelligenceStore store,
        CancellationToken token)
    {
        var accountIds = await GetAccountIdsAsync(context, connections, token);
        if (accountIds.Count == 0)
            return Results.NotFound();
        await store.UpdateAnnotationAsync(accountIds[0], itemId, worldName, update, token);
        return Results.NoContent();
    }

    private static async Task StreamEvents(
        HttpContext context,
        SqliteConnectionFactory connections,
        MarketIntelligenceStore store,
        CancellationToken token)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        var accountIds = await GetAccountIdsAsync(context, connections, token);
        long lastRevision = -1;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            var ledger = await store.GetLedgerAsync(accountIds, token);
            if (ledger.Revision != lastRevision)
            {
                lastRevision = ledger.Revision;
                await context.Response.WriteAsync($"event: intelligence\ndata: {lastRevision.ToString(CultureInfo.InvariantCulture)}\n\n", token);
                await context.Response.Body.FlushAsync(token);
            }
        }
        while (await timer.WaitForNextTickAsync(token));
    }

    private static long? DashboardUserId(HttpContext context) =>
        context.Items.TryGetValue(DashboardSessionStore.DashboardUserIdItemKey, out var value) && value is long userId
            ? userId : null;

    private static async Task<IReadOnlyList<long>> GetAccountIdsAsync(
        HttpContext context,
        SqliteConnectionFactory connections,
        CancellationToken token)
    {
        var userId = DashboardUserId(context);
        if (userId is null) return [1];
        var result = new List<long>();
        await using var connection = await connections.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id FROM dashboard_user_accounts WHERE dashboard_user_id = $userId ORDER BY is_default DESC, account_id LIMIT 1";
        command.Parameters.AddWithValue("$userId", userId.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) result.Add(reader.GetInt64(0));
        return result;
    }
}
