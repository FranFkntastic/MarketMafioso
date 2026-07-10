using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using MarketMafioso.Server.WorkshopHost;

namespace MarketMafioso.Server.Endpoints;

internal static class WorkshopHostEndpoints
{
    public static void MapWorkshopHostEndpoints(this WebApplication app, bool enableMarketAcquisition)
    {
        app.MapGet("/api/capabilities", (IWorkshopHostCraftQuoteService craftQuoteService) =>
            GetCapabilities(craftQuoteService, enableMarketAcquisition));
        app.MapPost("/api/craft/appraise", AppraiseCraft);
    }

    private static IResult GetCapabilities(
        IWorkshopHostCraftQuoteService craftQuoteService,
        bool enableMarketAcquisition)
    {
        var capabilities = new List<WorkshopHostCapability>
        {
            new()
            {
                Id = "inventory.write",
                SupportedSchemaVersions = [1],
                RequiredScopes = ["inventory:write"],
            },
            new()
            {
                Id = "inventory.read",
                SupportedSchemaVersions = [1],
                RequiredScopes = ["inventory:read"],
            },
            new()
            {
                Id = "diagnostics.read",
                SupportedSchemaVersions = [1],
                RequiredScopes = ["diagnostics:read"],
            },
        };

        if (enableMarketAcquisition)
        {
            capabilities.Add(new WorkshopHostCapability
            {
                Id = "acquisition.queue",
                SupportedSchemaVersions = [1],
                RequiredScopes = ["acquisition:queue"],
            });
        }

        if (craftQuoteService.IsAvailable)
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
        return quote == null
            ? Results.NotFound(new { error = "craft_appraisal_not_found" })
            : Results.Ok(string.IsNullOrWhiteSpace(quote.Source)
                ? quote with { Source = "WorkshopHostCraftArchitect" }
                : quote);
    }
}
