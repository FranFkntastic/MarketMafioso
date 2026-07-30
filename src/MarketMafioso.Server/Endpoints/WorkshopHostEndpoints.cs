using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using MarketMafioso.Server.Auth;
using MarketMafioso.Server.WorkshopHost;

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
            IWorkshopHostCraftQuoteService craftQuoteService,
            WorkshopHostCredentialStore credentialStore,
            CancellationToken token) =>
            GetCapabilitiesAsync(
                request,
                craftQuoteService,
                credentialStore,
                enableMarketAcquisition,
                requireApiKey,
                token));
        app.MapPost("/api/craft/appraise", AppraiseCraft);
        app.MapGet("/api/craft/plans/{planId}", OpenCraftPlan);
    }

    private static async Task<IResult> GetCapabilitiesAsync(
        HttpRequest request,
        IWorkshopHostCraftQuoteService craftQuoteService,
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

        if (craftQuoteService.IsAvailable && await AllowsAsync(WorkshopHostCredentialScope.CraftQuote))
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
        CraftAppraisalRequest quoteRequest,
        IWorkshopHostCraftQuoteService quoteService,
        HttpRequest request,
        IConfiguration configuration,
        CancellationToken token)
    {
        if (quoteRequest.SchemaVersion != 1)
            return Results.BadRequest(new { error = "unsupported_schema_version" });
        if (quoteRequest.ItemId == 0)
            return Results.BadRequest(new { error = "item_id_required" });
        if (quoteRequest.Quantity == 0)
            return Results.BadRequest(new { error = "quantity_required" });
        if (!quoteService.IsAvailable)
        {
            return Results.Json(
                new { error = "craft_appraisal_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var quote = await quoteService.AppraiseAsync(quoteRequest, token);
        if (quote == null)
            return Results.NotFound(new { error = "craft_appraisal_not_found" });

        var response = string.IsNullOrWhiteSpace(quote.Source)
            ? quote with { Source = "WorkshopHostCraftArchitect" }
            : quote;
        if (!string.IsNullOrWhiteSpace(response.PlanId))
            response = response with { PlanUrl = BuildPlanUrl(request, configuration, response.PlanId) };
        return Results.Ok(response);
    }

    private static async Task<IResult> OpenCraftPlan(
        string planId,
        CraftAppraisalPlanStore planStore,
        CancellationToken cancellationToken)
    {
        var json = await planStore.ReadAsync(planId, cancellationToken);
        return json == null
            ? Results.NotFound(new { error = "craft_appraisal_plan_not_found" })
            : Results.Text(json, "application/json");
    }

    private static string BuildPlanUrl(
        HttpRequest request,
        IConfiguration configuration,
        string planId)
    {
        var configuredOrigin = configuration["MarketMafioso:PublicOrigin"]?.Trim().TrimEnd('/');
        var workshopOrigin = string.IsNullOrWhiteSpace(configuredOrigin)
            ? $"{request.Scheme}://{request.Host}"
            : configuredOrigin;
        var snapshotPath = $"{request.PathBase}/api/craft/plans/{Uri.EscapeDataString(planId)}";
        var snapshotUrl = $"{workshopOrigin}{snapshotPath}";
        var configuredAppOrigin = configuration["MarketMafioso:CraftArchitectAppOrigin"]?.Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configuredAppOrigin))
            return $"{configuredAppOrigin}/?appraisalPlan={Uri.EscapeDataString(snapshotUrl)}";

        return request.PathBase.HasValue
            ? $"{workshopOrigin}/?appraisalPlan={Uri.EscapeDataString(snapshotPath)}"
            : snapshotUrl;
    }
}
