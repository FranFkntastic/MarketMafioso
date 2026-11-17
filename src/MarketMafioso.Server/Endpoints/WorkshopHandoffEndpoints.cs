using MarketMafioso.Contracts.Workshop;
using MarketMafioso.Server.Auth;

namespace MarketMafioso.Server.Endpoints;

internal static class WorkshopHandoffEndpoints
{
    public static void MapWorkshopHandoffEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/workshop/handoffs");
        group.MapGet("", (HttpContext context, string characterName, string world, WorkshopHandoffStore store) =>
            Execute(context, null, new(characterName, world), null,
                account => store.ListAsync(account, new(characterName, world), context.RequestAborted)));
        group.MapPost("", (HttpContext context, WorkshopHandoffOffer request, WorkshopHandoffStore store) =>
            Execute(context, null, request.Sender, request.Target,
                account => store.OfferAsync(account, request, context.RequestAborted)));
        MapCommand(group, "withdraw", "Withdrawn");
        MapCommand(group, "accept", "Accepted");
        MapCommand(group, "decline", "Declined");
        group.MapPost("/{id:guid}/handover", (HttpContext context, Guid id, WorkshopHandoverReport request, WorkshopHandoffStore store) =>
            Execute(context, id, request.Actor, null,
                account => store.ReportHandoverAsync(account, id, request, context.RequestAborted)));
        app.MapGet("/api/workshop/holdings", (HttpContext context, string? characterName, string? world, string? itemIds,
            WorkshopHandoffStore store, InventoryReportStore reports) =>
            Execute(context, null, null, new(characterName!, world!),
                account => store.GetHoldingsAsync(account, new(characterName!, world!), itemIds, reports, context.RequestAborted)));
    }

    private static void MapCommand(RouteGroupBuilder group, string action, string state) =>
        group.MapPost("/{id:guid}/" + action, (HttpContext context, Guid id, WorkshopHandoffCommand request, WorkshopHandoffStore store) =>
            Execute(context, id, state == "Withdrawn" ? request.Actor : null, state == "Withdrawn" ? null : request.Actor,
                account => store.ChangeAsync(account, id, state, request, context.RequestAborted)));

    private static async Task<IResult> Execute<T>(HttpContext context, Guid? id, WorkshopCharacter? sender,
        WorkshopCharacter? target, Func<long, Task<T>> operation)
    {
        long? account = null;
        var log = context.RequestServices.GetRequiredService<ILogger<WorkshopHandoffStore>>();
        try
        {
            var headers = context.Request.Headers["X-Api-Key"];
            var key = headers.Count == 1 ? headers[0] : null;
            if (!await context.RequestServices.GetRequiredService<WorkshopHostCredentialStore>()
                    .IsAuthorizedAsync(key, WorkshopHostCredentialScope.InventoryWrite, context.RequestAborted))
                throw new WorkshopHandoffException(401, "A valid MarketMafioso ingest key is required.");
            account = await context.RequestServices.GetRequiredService<IngestKeyAccountResolver>()
                .ResolveAccountIdAsync(key, context.RequestAborted);
            if (account is null) throw new WorkshopHandoffException(401, "Ingest key has no host account.");
            return Results.Ok(await operation(account.Value));
        }
        catch (Exception ex) when (ex is WorkshopHandoffException or ArgumentException)
        {
            log.LogWarning("Workshop handoff {HandoffId} account {Account} sender {Sender} target {Target} refused: {Reason}", id, account, sender, target, ex.Message);
            return Results.Json(new { error = ex.Message }, statusCode: ex is WorkshopHandoffException failure ? failure.Status : 400);
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            log.LogWarning(ex, "Workshop handoff {Method} {Path} {HandoffId} account {Account} sender {Sender} target {Target} failed: {Reason}", context.Request.Method, context.Request.Path, id, account, sender, target, ex.Message);
            var reason = ex is Microsoft.Data.Sqlite.SqliteException sqlite
                ? $"Host storage failed (SQLite error {sqlite.SqliteErrorCode}); handoff outcome may be unknown."
                : $"Host handoff processing failed ({ex.GetType().Name}); handoff outcome may be unknown.";
            return Results.Json(new { error = reason }, statusCode: 503);
        }
    }
}
