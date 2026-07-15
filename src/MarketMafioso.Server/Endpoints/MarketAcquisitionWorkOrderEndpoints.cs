namespace MarketMafioso.Server.Endpoints;

internal static class MarketAcquisitionWorkOrderEndpoints
{
    public static void MapMarketAcquisitionWorkOrderEndpoints(this WebApplication app)
    {
        app.MapGet("/api/acquisition/work-orders", ListWorkOrders);
        app.MapPost("/api/acquisition/work-orders", CreateWorkOrder);
        app.MapGet("/api/acquisition/work-orders/{id}", GetWorkOrder);
        app.MapGet("/api/acquisition/work-orders/{id}/history", GetHistory);
        app.MapGet("/api/acquisition/work-orders/{id}/merge-preview/{sourceId}", PreviewMerge);
        app.MapPost("/api/acquisition/work-orders/{id}/shelf", Shelf);
        app.MapPost("/api/acquisition/work-orders/{id}/restore", Restore);
        app.MapPost("/api/acquisition/work-orders/{id}/archive", Archive);
        app.MapPost("/api/acquisition/work-orders/{id}/clone", Clone);
        app.MapPost("/api/acquisition/work-orders/{id}/merge", Merge);
        app.MapPost("/api/acquisition/work-orders/{id}/lease/renew", RenewLease);
    }

    private static async Task<IResult> CreateWorkOrder(
        MarketAcquisitionBatchCreateRequest request,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        try
        {
            var created = await store.CreateBatchAsync(request, token);
            var workOrder = await store.GetWorkOrderAsync(created.Request.Id, token);
            return created.IsReplay ? Results.Ok(workOrder) : Results.Created($"/api/acquisition/work-orders/{created.Request.Id}", workOrder);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (MarketAcquisitionIdempotencyConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ListWorkOrders(
        MarketAcquisitionRequestStore store,
        string? characterName,
        string? world,
        bool? includeArchived,
        CancellationToken token) =>
        Results.Ok(await store.ListWorkOrdersAsync(characterName, world, includeArchived ?? false, token));

    private static async Task<IResult> GetWorkOrder(string id, MarketAcquisitionRequestStore store, CancellationToken token)
    {
        var result = await store.GetWorkOrderAsync(id, token);
        return result == null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> GetHistory(string id, MarketAcquisitionRequestStore store, CancellationToken token)
    {
        var result = await store.GetWorkOrderHistoryAsync(id, token);
        return result == null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> PreviewMerge(string id, string sourceId, MarketAcquisitionRequestStore store, CancellationToken token)
    {
        var result = await store.PreviewWorkOrderMergeAsync(id, sourceId, token);
        return result == null ? Results.NotFound() : Results.Ok(result);
    }

    private static Task<IResult> Shelf(string id, MarketAcquisitionWorkOrderCommand command, MarketAcquisitionRequestStore store, CancellationToken token) =>
        ApplyCommand(() => store.ShelfWorkOrderAsync(id, command, token));

    private static Task<IResult> Restore(string id, MarketAcquisitionWorkOrderCommand command, MarketAcquisitionRequestStore store, CancellationToken token) =>
        ApplyCommand(() => store.RestoreWorkOrderAsync(id, command, token));

    private static Task<IResult> Archive(string id, MarketAcquisitionWorkOrderCommand command, MarketAcquisitionRequestStore store, CancellationToken token) =>
        ApplyCommand(() => store.ArchiveWorkOrderAsync(id, command, token));

    private static Task<IResult> Clone(string id, MarketAcquisitionWorkOrderCloneRequest command, MarketAcquisitionRequestStore store, CancellationToken token) =>
        ApplyCommand(() => store.CloneWorkOrderAsync(id, command, token));

    private static Task<IResult> Merge(string id, MarketAcquisitionWorkOrderMergeRequest command, MarketAcquisitionRequestStore store, CancellationToken token) =>
        ApplyCommand(() => store.MergeWorkOrdersAsync(id, command, token));

    private static async Task<IResult> RenewLease(string id, MarketAcquisitionLeaseRenewRequest command, MarketAcquisitionRequestStore store, CancellationToken token)
    {
        try
        {
            var result = await store.RenewLeaseAsync(id, command, token);
            return result == null ? Results.NotFound() : Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> ApplyCommand(Func<Task<MarketAcquisitionWorkOrderView?>> action)
    {
        try
        {
            var result = await action();
            return result == null ? Results.NotFound() : Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (MarketAcquisitionRevisionConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message, expectedRevision = ex.ExpectedRevision, actualRevision = ex.ActualRevision });
        }
        catch (MarketAcquisitionInvalidTransitionException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (MarketAcquisitionMergeConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message, preview = ex.Preview });
        }
    }
}
