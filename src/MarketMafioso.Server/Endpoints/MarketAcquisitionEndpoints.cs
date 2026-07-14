using System.Text.Json;

namespace MarketMafioso.Server.Endpoints;

internal static class MarketAcquisitionEndpoints
{
    public static void MapMarketAcquisitionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/acquisition/requests", ListRecentRequests);
        app.MapGet("/api/acquisition/requests/{id}/timeline", GetRequestTimeline);
        app.MapGet("/api/acquisition/batches/{id}", GetBatch);

        app.MapPost("/acquisition/requests", CreateRequest);
        app.MapPost("/api/acquisition/requests", CreateRequest);
        app.MapPost("/acquisition/batches", CreateBatch);
        app.MapPost("/api/acquisition/batches", CreateBatch);
        app.MapPut("/acquisition/batches/{id}", ReplaceBatch);
        app.MapPut("/api/acquisition/batches/{id}", ReplaceBatch);
        app.MapPost("/api/acquisition/batches/{id}/lines", AppendBatchLines);

        app.MapGet("/acquisition/requests/pending", ListPendingRequests);
        app.MapGet("/api/acquisition/requests/pending", ListPendingRequests);
        app.MapGet("/acquisition/batches/pending", ListPendingBatches);
        app.MapGet("/api/acquisition/batches/pending", ListPendingBatches);
        app.MapPost("/acquisition/requests/{id}/claim", ClaimRequest);
        app.MapPost("/api/acquisition/requests/{id}/claim", ClaimRequest);
        app.MapPost("/acquisition/requests/{id}/accept", AcceptRequest);
        app.MapPost("/api/acquisition/requests/{id}/accept", AcceptRequest);
        app.MapPost("/acquisition/requests/{id}/reject", RejectRequest);
        app.MapPost("/api/acquisition/requests/{id}/reject", RejectRequest);
        app.MapPost("/acquisition/requests/{id}/cancel", CancelRequest);
        app.MapPost("/api/acquisition/requests/{id}/cancel", CancelRequest);
        app.MapPost("/acquisition/requests/{id}/resend", ResendRequest);
        app.MapPost("/api/acquisition/requests/{id}/resend", ResendRequest);
        app.MapPost("/acquisition/requests/{id}/progress", ReportProgress);
        app.MapPost("/api/acquisition/requests/{id}/progress", ReportProgress);
        app.MapPost("/acquisition/batches/{id}/lines/{lineId}/progress", ReportLineProgress);
        app.MapPost("/api/acquisition/batches/{id}/lines/{lineId}/progress", ReportLineProgress);
        app.MapPost("/acquisition/batches/{id}/purchases", RecordPurchase);
        app.MapPost("/api/acquisition/batches/{id}/purchases", RecordPurchase);
        app.MapPost("/acquisition/batches/{id}/observations", RecordMarketObservation);
        app.MapPost("/api/acquisition/batches/{id}/observations", RecordMarketObservation);
        app.MapPost("/acquisition/requests/{id}/complete", CompleteRequest);
        app.MapPost("/api/acquisition/requests/{id}/complete", CompleteRequest);
        app.MapPost("/acquisition/requests/{id}/fail", FailRequest);
        app.MapPost("/api/acquisition/requests/{id}/fail", FailRequest);
    }

    private static async Task<IResult> ListRecentRequests(
        MarketAcquisitionRequestStore store,
        bool? includeTerminal,
        CancellationToken token)
    {
        var requests = await store.ListRecentAsync(100, includeTerminal ?? false, token);
        return Results.Ok(requests);
    }

    private static async Task<IResult> GetRequestTimeline(
        string id,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        var timeline = await store.GetTimelineAsync(id, token);
        return timeline == null ? Results.NotFound() : Results.Ok(timeline);
    }

    private static async Task<IResult> GetBatch(
        string id,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        var batch = await store.GetAsync(id, token);
        return batch == null ? Results.NotFound() : Results.Ok(batch);
    }

    private static async Task<IResult> CreateRequest(
        HttpRequest request,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        try
        {
            var isBrowserForm = request.HasFormContentType;
            var acquisitionRequest = isBrowserForm
                ? await ReadAcquisitionFormAsync(request, token)
                : await JsonSerializer.DeserializeAsync<MarketAcquisitionCreateRequest>(
                    request.Body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web),
                    token);
            if (acquisitionRequest == null)
                return Results.BadRequest(new { error = "Request body is required." });

            var created = await store.CreateAsync(acquisitionRequest, token);
            if (isBrowserForm && !IsDashboardApiRoute(request) && !WantsJsonResponse(request))
                return Results.Redirect($"{request.PathBase}/acquisition?acquisition={Uri.EscapeDataString(created.Request.Id)}");

            return created.IsReplay
                ? Results.Ok(created.Request)
                : Results.Created(AppUrl(request.PathBase, $"/acquisition/requests/{created.Request.Id}"), created.Request);
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

    private static async Task<IResult> CreateBatch(
        HttpRequest request,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        try
        {
            var acquisitionRequest = await JsonSerializer.DeserializeAsync<MarketAcquisitionBatchCreateRequest>(
                request.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                token);
            if (acquisitionRequest == null)
                return Results.BadRequest(new { error = "Request body is required." });

            var created = await store.CreateBatchAsync(acquisitionRequest, token);
            return created.IsReplay
                ? Results.Ok(created.Request)
                : Results.Created(AppUrl(request.PathBase, $"/acquisition/batches/{created.Request.Id}"), created.Request);
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

    private static async Task<IResult> AppendBatchLines(
        string id,
        MarketAcquisitionBatchAppendLinesRequest appendRequest,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        try
        {
            var updated = await store.AppendLinesAsync(id, appendRequest, token);
            return updated == null ? Results.NotFound() : Results.Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (MarketAcquisitionInvalidTransitionException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (MarketAcquisitionRevisionConflictException ex)
        {
            return Results.Conflict(new
            {
                error = ex.Message,
                expectedRevision = ex.ExpectedRevision,
                actualRevision = ex.ActualRevision,
            });
        }
    }

    private static async Task<IResult> ReplaceBatch(
        string id,
        MarketAcquisitionBatchReplaceRequest replaceRequest,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        try
        {
            var updated = await store.ReplaceBatchAsync(id, replaceRequest, token);
            return updated == null ? Results.NotFound() : Results.Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (MarketAcquisitionInvalidTransitionException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (MarketAcquisitionRevisionConflictException ex)
        {
            return Results.Conflict(new
            {
                error = ex.Message,
                expectedRevision = ex.ExpectedRevision,
                actualRevision = ex.ActualRevision,
            });
        }
    }

    private static async Task<IResult> ListPendingRequests(
        string? characterName,
        string? world,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(world))
            return Results.BadRequest(new { error = "characterName and world are required." });

        var pending = await store.ListPendingAsync(characterName, world, token);
        return Results.Ok(new MarketAcquisitionPendingResponse { Requests = pending });
    }

    private static async Task<IResult> ListPendingBatches(
        string? characterName,
        string? world,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(world))
            return Results.BadRequest(new { error = "characterName and world are required." });

        var pending = await store.ListPendingAsync(characterName, world, token);
        return Results.Ok(new MarketAcquisitionBatchPendingResponse { Batches = pending });
    }

    private static async Task<IResult> ClaimRequest(
        string id,
        MarketAcquisitionClaimRequest claimRequest,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        try
        {
            var claimed = await store.ClaimAsync(id, claimRequest, token);
            return claimed == null ? Results.NotFound() : Results.Ok(claimed);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> AcceptRequest(
        string id,
        MarketAcquisitionClaimTokenRequest acceptRequest,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        try
        {
            var accepted = await store.AcceptAsync(id, acceptRequest, token);
            return accepted == null ? Results.NotFound() : Results.Ok(accepted);
        }
        catch (UnauthorizedAccessException)
        {
            return InvalidApiKey();
        }
        catch (MarketAcquisitionInvalidTransitionException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> RejectRequest(
        string id,
        MarketAcquisitionLifecycleRequest lifecycleRequest,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        try
        {
            var result = await store.RejectAsync(id, lifecycleRequest, token);
            return result == null ? Results.NotFound() : Results.Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return InvalidApiKey();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (MarketAcquisitionIdempotencyConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (MarketAcquisitionInvalidTransitionException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> CancelRequest(
        HttpRequest request,
        string id,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        try
        {
            var cancelled = await store.CancelAsync(id, token);
            return cancelled == null
                ? Results.NotFound()
                : IsDashboardApiRoute(request) || WantsJsonResponse(request)
                    ? Results.Ok(cancelled)
                    : Results.Redirect($"{request.PathBase}/acquisition?cancelled={Uri.EscapeDataString(id)}");
        }
        catch (MarketAcquisitionInvalidTransitionException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ResendRequest(
        HttpRequest request,
        string id,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        try
        {
            var resent = await store.ResendAsync(id, token);
            return resent == null
                ? Results.NotFound()
                : IsDashboardApiRoute(request) || WantsJsonResponse(request)
                    ? Results.Ok(resent)
                    : Results.Redirect($"{request.PathBase}/acquisition?resent={Uri.EscapeDataString(id)}");
        }
        catch (MarketAcquisitionInvalidTransitionException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static Task<IResult> ReportProgress(
        HttpRequest request,
        string id,
        MarketAcquisitionRequestStore store,
        CancellationToken token) =>
        ApplyLifecycleAsync(
            store.ReportProgressAsync,
            store.ReportAttemptProgressAsync,
            id,
            request,
            token);

    private static async Task<IResult> ReportLineProgress(
        string id,
        string lineId,
        MarketAcquisitionLineProgressRequest progressRequest,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        try
        {
            var line = await store.RecordLineProgressAsync(id, lineId, progressRequest, token);
            return line == null ? Results.NotFound() : Results.Ok(line);
        }
        catch (UnauthorizedAccessException)
        {
            return InvalidApiKey();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (MarketAcquisitionInvalidLineException)
        {
            return Results.NotFound();
        }
        catch (MarketAcquisitionIdempotencyConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (MarketAcquisitionAttemptSequenceConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (MarketAcquisitionInvalidTransitionException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> RecordPurchase(
        string id,
        MarketAcquisitionPurchaseAuditRequest purchaseRequest,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        try
        {
            var audit = await store.RecordPurchaseAuditAsync(id, purchaseRequest, token);
            return audit == null ? Results.NotFound() : Results.Ok(audit);
        }
        catch (UnauthorizedAccessException)
        {
            return InvalidApiKey();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (MarketAcquisitionInvalidLineException)
        {
            return Results.NotFound();
        }
        catch (MarketAcquisitionIdempotencyConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (MarketAcquisitionAttemptSequenceConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<IResult> RecordMarketObservation(
        string id,
        MarketAcquisitionMarketObservationRequest observationRequest,
        MarketAcquisitionRequestStore store,
        CancellationToken token)
    {
        try
        {
            var observation = await store.RecordMarketObservationAsync(id, observationRequest, token);
            return observation == null ? Results.NotFound() : Results.Ok(observation);
        }
        catch (UnauthorizedAccessException)
        {
            return InvalidApiKey();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (MarketAcquisitionInvalidLineException)
        {
            return Results.NotFound();
        }
        catch (MarketAcquisitionIdempotencyConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (MarketAcquisitionAttemptSequenceConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static Task<IResult> CompleteRequest(
        HttpRequest request,
        string id,
        MarketAcquisitionRequestStore store,
        CancellationToken token) =>
        ApplyLifecycleAsync(
            store.CompleteAsync,
            store.CompleteAttemptAsync,
            id,
            request,
            token);

    private static Task<IResult> FailRequest(
        HttpRequest request,
        string id,
        MarketAcquisitionRequestStore store,
        CancellationToken token) =>
        ApplyLifecycleAsync(
            store.FailAsync,
            store.FailAttemptAsync,
            id,
            request,
            token);

    private static async Task<IResult> ApplyLifecycleAsync(
        Func<string, MarketAcquisitionLifecycleRequest, CancellationToken, Task<MarketAcquisitionRequestView?>> apply,
        Func<string, MarketAcquisitionAttemptEventRequest, CancellationToken, Task<MarketAcquisitionAttemptEventResult?>> applyAttempt,
        string id,
        HttpRequest request,
        CancellationToken token)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: token);
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            if (document.RootElement.TryGetProperty("attemptId", out var attemptIdElement) &&
                !string.IsNullOrWhiteSpace(attemptIdElement.GetString()))
            {
                var attemptRequest = document.Deserialize<MarketAcquisitionAttemptEventRequest>(jsonOptions)
                    ?? throw new ArgumentException("Attempt lifecycle payload is required.");
                var attemptResult = await applyAttempt(id, attemptRequest, token);
                return attemptResult == null ? Results.NotFound() : Results.Ok(attemptResult);
            }

            var lifecycleRequest = document.Deserialize<MarketAcquisitionLifecycleRequest>(jsonOptions)
                ?? throw new ArgumentException("Lifecycle payload is required.");
            var result = await apply(id, lifecycleRequest, token);
            return result == null ? Results.NotFound() : Results.Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return InvalidApiKey();
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (MarketAcquisitionIdempotencyConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (MarketAcquisitionAttemptSequenceConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (MarketAcquisitionInvalidTransitionException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    private static async Task<MarketAcquisitionCreateRequest> ReadAcquisitionFormAsync(
        HttpRequest request,
        CancellationToken token)
    {
        var form = await request.ReadFormAsync(token);
        var itemId = ParseUInt(form["itemId"].ToString(), "itemId");
        var itemName = form["itemName"].ToString().Trim();
        var quantityMode = form["quantityMode"].ToString();
        return new MarketAcquisitionCreateRequest
        {
            SchemaVersion = ParseInt(form["schemaVersion"].ToString(), "schemaVersion"),
            IdempotencyKey = form["idempotencyKey"].ToString(),
            TargetCharacterName = form["targetCharacterName"].ToString(),
            TargetWorld = form["targetWorld"].ToString(),
            Region = form["region"].ToString(),
            ItemId = itemId,
            ItemName = string.IsNullOrWhiteSpace(itemName) ? $"Item {itemId}" : itemName,
            QuantityMode = quantityMode,
            Quantity = quantityMode == "AllBelowThreshold"
                ? ParseOptionalUInt(form["quantity"].ToString(), "quantity")
                : ParseUInt(form["quantity"].ToString(), "quantity"),
            HqPolicy = form["hqPolicy"].ToString(),
            MaxUnitPrice = ParseUInt(form["maxUnitPrice"].ToString(), "maxUnitPrice"),
            MaxTotalGil = ParseOptionalUInt(form["maxTotalGil"].ToString(), "maxTotalGil"),
            WorldMode = form["worldMode"].ToString(),
            ExpiresInSeconds = ParseInt(form["expiresInSeconds"].ToString(), "expiresInSeconds"),
        };
    }

    private static int ParseInt(string value, string fieldName) =>
        int.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"{fieldName} must be a whole number.");

    private static uint ParseUInt(string value, string fieldName) =>
        uint.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"{fieldName} must be a positive whole number.");

    private static uint ParseOptionalUInt(string value, string fieldName) =>
        string.IsNullOrWhiteSpace(value)
            ? 0
            : ParseUInt(value, fieldName);

    private static bool WantsJsonResponse(HttpRequest request) =>
        request.Headers.Accept.Any(value =>
            value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);

    private static bool IsDashboardApiRoute(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    private static IResult InvalidApiKey() =>
        Results.Json(new { error = "invalid_api_key" }, statusCode: StatusCodes.Status401Unauthorized);

    private static string AppUrl(PathString pathBase, string path) =>
        $"{pathBase}{path}";
}
