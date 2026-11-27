using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using MarketMafioso.Server.Auth;
using MarketMafioso.Server.WorkshopHost;
using CraftAppraisalQuote = MarketMafioso.Server.WorkshopHost.CraftAppraisalQuote;
using CraftAppraisalRequest = MarketMafioso.Server.WorkshopHost.CraftAppraisalRequest;
using EngineCraftAppraisalQuote = FFXIV_Craft_Architect.Core.Integrations.WorkshopHost.CraftAppraisalQuote;
using EngineCraftAppraisalRequest = FFXIV_Craft_Architect.Core.Integrations.WorkshopHost.CraftAppraisalRequest;

namespace MarketMafioso.Server.Endpoints;

internal static class WorkshopHostEndpoints
{
    public static void MapWorkshopHostEndpoints(
        this WebApplication app,
        bool enableMarketAcquisition,
        bool requireApiKey)
    {
        app.MapGet("/api/capabilities", (
            HttpRequest request,
            WorkshopHostCredentialStore credentialStore,
            CancellationToken token) =>
            GetCapabilitiesAsync(
                request,
                credentialStore,
                enableMarketAcquisition,
                requireApiKey,
                token));
        app.MapPost("/api/craft/appraise", AppraiseCraft);

        // Capability URL: the 64-hex content hash is the only credential. The Craft Architect
        // web app fetches it cross-origin from the user's browser.
        app.MapGet($"{WorkshopHostCraftAppraisalSettings.PlanSnapshotPath}/{{planId}}", OpenPlanSnapshotAsync)
            .RequireCors(WorkshopHostCraftAppraisalSettings.PlanSnapshotCorsPolicy);
    }

    private static async Task<IResult> GetCapabilitiesAsync(
        HttpRequest request,
        WorkshopHostCredentialStore credentialStore,
        bool enableMarketAcquisition,
        bool requireApiKey,
        CancellationToken cancellationToken)
    {
        var suppliedKey = request.Headers["X-Api-Key"].Count == 1
            ? request.Headers["X-Api-Key"][0]
            : null;
        async Task<bool> AllowsAsync(WorkshopHostCredentialScope scope) =>
            !requireApiKey || await credentialStore
                .IsAuthorizedAsync(suppliedKey, scope, cancellationToken)
                .ConfigureAwait(false);

        var capabilities = new List<WorkshopHostCapability>();
        if (await AllowsAsync(WorkshopHostCredentialScope.InventoryWrite))
        {
            capabilities.Add(new WorkshopHostCapability
            {
                Id = "inventory.write",
                SupportedSchemaVersions = [1],
                RequiredScopes = ["inventory:write"],
            });
        }
        if (await AllowsAsync(WorkshopHostCredentialScope.InventoryRead))
        {
            capabilities.Add(new WorkshopHostCapability
            {
                Id = "inventory.read",
                SupportedSchemaVersions = [1],
                RequiredScopes = ["inventory:read"],
            });
        }
        if (await AllowsAsync(WorkshopHostCredentialScope.DiagnosticsRead))
        {
            capabilities.Add(new WorkshopHostCapability
            {
                Id = "diagnostics.read",
                SupportedSchemaVersions = [1],
                RequiredScopes = ["diagnostics:read"],
            });
        }

        if (enableMarketAcquisition && await AllowsAsync(WorkshopHostCredentialScope.AcquisitionQueue))
        {
            capabilities.Add(new WorkshopHostCapability
            {
                Id = "acquisition.queue",
                SupportedSchemaVersions = [1],
                RequiredScopes = ["acquisition:queue"],
            });
            capabilities.Add(new WorkshopHostCapability
            {
                Id = "acquisition.work-orders",
                SupportedSchemaVersions = [1],
                RequiredScopes = ["acquisition:queue"],
            });
        }

        if (await AllowsAsync(WorkshopHostCredentialScope.CraftQuote))
        {
            capabilities.Add(new WorkshopHostCapability
            {
                Id = "craft.appraise",
                SupportedSchemaVersions = [1],
                RequiredScopes = ["craft:quote"],
            });
        }

        return Results.Ok(new WorkshopHostCapabilitiesResponse
        {
            ServerTimeUtc = DateTimeOffset.UtcNow,
            Capabilities = capabilities,
        });
    }

    private static async Task<IResult> AppraiseCraft(
        HttpRequest httpRequest,
        CraftAppraisalRequest quoteRequest,
        IHostedCraftAppraisalCoordinator coordinator,
        WorkshopHostCraftAppraisalSettings settings,
        ILoggerFactory loggerFactory,
        CancellationToken token)
    {
        var engineRequest = CraftAppraisalContractMapper.ToEngine(quoteRequest);
        var rejection = CraftAppraisalRequestValidation.GetRejection(engineRequest, settings.Options);
        if (rejection != null)
        {
            return Results.BadRequest(new
            {
                error = rejection,
                detail = DescribeRejection(rejection, engineRequest, settings.Options),
            });
        }

        var log = loggerFactory.CreateLogger("MarketMafioso.Server.CraftAppraisal");
        EngineCraftAppraisalQuote quote;
        try
        {
            quote = await coordinator.AppraiseAsync(engineRequest, token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            log.LogWarning(
                "Craft appraisal for item {ItemId} x{Quantity} in {Region} exceeded the {TimeoutSeconds}-second quote timeout.",
                engineRequest.ItemId,
                engineRequest.Quantity,
                engineRequest.Scope.Region,
                settings.Options.QuoteTimeout.TotalSeconds);
            return Results.Json(
                new
                {
                    error = "craft_architect_api_timeout",
                    detail = $"Craft appraisal exceeded the {settings.Options.QuoteTimeout.TotalSeconds:0}-second quote timeout.",
                },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (HttpRequestException ex)
        {
            var upstreamStatus = ex.StatusCode?.ToString() ?? "no response";
            log.LogWarning(
                ex,
                "Craft appraisal for item {ItemId} x{Quantity} in {Region} failed: a market or item data request returned {UpstreamStatus}.",
                engineRequest.ItemId,
                engineRequest.Quantity,
                engineRequest.Scope.Region,
                upstreamStatus);
            return Results.Json(
                new
                {
                    error = "craft_architect_api_error",
                    detail = $"A market or item data request failed ({upstreamStatus}): {ex.Message}",
                },
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(
                ex,
                "Craft appraisal for item {ItemId} x{Quantity} in {Region} failed unexpectedly.",
                engineRequest.ItemId,
                engineRequest.Quantity,
                engineRequest.Scope.Region);
            return Results.Json(
                new
                {
                    error = "craft_appraisal_failed",
                    detail = $"{ex.GetType().Name}: {ex.Message}",
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }

        CraftAppraisalQuote response = CraftAppraisalContractMapper.FromEngine(
            quote,
            settings.ResolvePlanUrl(httpRequest, quote.PlanId, quote.PlanUrl));
        return Results.Ok(response);
    }

    private static string DescribeRejection(
        string rejection,
        EngineCraftAppraisalRequest request,
        CraftAppraisalApiOptions options) =>
        rejection switch
        {
            CraftAppraisalRequestValidation.UnsupportedSchemaVersion =>
                $"schemaVersion {request.SchemaVersion} is not supported; this host accepts 1.",
            CraftAppraisalRequestValidation.ItemIdRequired => "itemId must be greater than zero.",
            CraftAppraisalRequestValidation.QuantityRequired => "quantity must be greater than zero.",
            CraftAppraisalRequestValidation.QuantityExceedsLimit =>
                $"quantity {request.Quantity} exceeds this host's limit of {options.MaximumQuantity}.",
            CraftAppraisalRequestValidation.UnsupportedPricingMode =>
                $"pricingMode '{request.Options.PricingMode}' is not supported; this host accepts CurrentMarketEvidence.",
            _ => rejection,
        };

    private static async Task<IResult> OpenPlanSnapshotAsync(
        string planId,
        CraftAppraisalPlanStore planStore,
        CancellationToken token)
    {
        if (planId.Length != 64 || !planId.All(Uri.IsHexDigit))
            return Results.NotFound(new { error = "craft_appraisal_plan_id_invalid" });

        var planJson = await planStore.ReadAsync(planId, token);
        return planJson == null
            ? Results.NotFound(new { error = CraftAppraisalRequestValidation.PlanNotFound })
            : Results.Text(planJson, "application/json");
    }
}
