using CoreAppraisal = FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

namespace MarketMafioso.Server.WorkshopHost;

/// <summary>
/// Settings for the in-process Craft Architect appraisal engine. Quotes, plan snapshots and
/// item search all run inside Workshop Host; the only Craft Architect address involved is
/// <see cref="AppOrigin"/>, which a user's browser opens from a quote's plan link.
/// </summary>
public sealed record WorkshopHostCraftAppraisalSettings
{
    public const string PlanSnapshotPath = "/api/craft/plans";
    public const string PlanSnapshotCorsPolicy = "CraftArchitectPlanSnapshots";
    public const string DefaultAppOrigin = "https://xivcraftarchitect.com";

    // Matches the self-host bundle's documented PublicOrigin. Used only to satisfy the engine's
    // startup validation: with no configured origin, plan links are rebuilt from each request.
    private const string UnsetPublicOriginPlaceholder = "http://localhost:5088";

    /// <summary>True when <c>MarketMafioso:PublicOrigin</c> is set; otherwise plan links follow the request host.</summary>
    public bool PublicOriginConfigured { get; init; }

    /// <summary>Origin of the Craft Architect web app that plan links open and that may fetch snapshots.</summary>
    public string AppOrigin { get; init; } = DefaultAppOrigin;

    public CoreAppraisal.CraftAppraisalApiOptions Options { get; init; } = new();

    public static WorkshopHostCraftAppraisalSettings FromConfiguration(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var publicOrigin = TrimOrigin(configuration["MarketMafioso:PublicOrigin"]);
        var basePath = NormalizeBasePath(configuration["MarketMafioso:BasePath"]);
        var appOrigin = TrimOrigin(configuration["MarketMafioso:CraftArchitectAppOrigin"]);
        if (appOrigin.Length == 0)
            appOrigin = DefaultAppOrigin;
        if (!Uri.TryCreate(appOrigin, UriKind.Absolute, out var appOriginUri) ||
            appOriginUri.Scheme is not ("http" or "https") ||
            appOriginUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(appOriginUri.Query))
        {
            throw new InvalidOperationException(
                $"MarketMafioso:CraftArchitectAppOrigin must be an http(s) origin with no path, such as {DefaultAppOrigin}; got '{appOrigin}'.");
        }

        // Keep plan snapshots and the market cache next to the SQLite database so they live in
        // the same mounted volume and survive container replacement.
        var databasePath = configuration["MarketMafioso:DatabasePath"];
        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(
            string.IsNullOrWhiteSpace(databasePath)
                ? Path.Combine(environment.ContentRootPath, "data", "marketmafioso.db")
                : databasePath))!;

        var options = new CoreAppraisal.CraftAppraisalApiOptions
        {
            Enabled = true,
            CacheDirectory = Path.Combine(dataDirectory, CoreAppraisal.CraftAppraisalApiOptions.DefaultCacheDirectoryName),
            PlanDirectory = Path.Combine(dataDirectory, CoreAppraisal.CraftAppraisalApiOptions.DefaultPlanDirectoryName),
            PublicAppOrigin = appOrigin,
            PublicApiOrigin = (publicOrigin.Length == 0 ? UnsetPublicOriginPlaceholder : publicOrigin) + basePath,
            PlanSnapshotPath = PlanSnapshotPath,
        }.Normalize(dataDirectory);

        return new WorkshopHostCraftAppraisalSettings
        {
            PublicOriginConfigured = publicOrigin.Length > 0,
            AppOrigin = options.PublicAppOrigin,
            Options = options,
        };
    }

    /// <summary>
    /// The plan link for <paramref name="planId"/>. With a configured public origin this is the
    /// link the engine already built; otherwise the snapshot URL follows the host and path base
    /// the caller reached, the same way the server builds its other public links.
    /// </summary>
    public string? ResolvePlanUrl(HttpRequest request, string? planId, string? enginePlanUrl)
    {
        if (string.IsNullOrWhiteSpace(planId) || PublicOriginConfigured)
            return enginePlanUrl;

        var scheme = FirstHeaderValue(request.Headers["X-Forwarded-Proto"]) ?? request.Scheme;
        var host = FirstHeaderValue(request.Headers["X-Forwarded-Host"]) ?? request.Host.Value;
        if (string.IsNullOrWhiteSpace(host))
            return enginePlanUrl;

        var requestOptions = Options with { PublicApiOrigin = $"{scheme}://{host}{request.PathBase}" };
        return requestOptions.BuildPlanUrl(requestOptions.BuildPlanSnapshotUrl(planId));
    }

    private static string? FirstHeaderValue(Microsoft.Extensions.Primitives.StringValues values)
    {
        var value = values.Count == 0 ? null : values[0]?.Split(',')[0].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string TrimOrigin(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('/');

    private static string NormalizeBasePath(string? value)
    {
        var trimmed = value?.Trim().Trim('/') ?? string.Empty;
        return trimmed.Length == 0 ? string.Empty : "/" + trimmed;
    }
}

/// <summary>
/// Explicit mapping between Workshop Host's public craft quote contract and the engine's types,
/// so the wire shape the plugin reads stays owned here even if the engine's records change.
/// </summary>
internal static class CraftAppraisalContractMapper
{
    public static CoreAppraisal.CraftAppraisalRequest ToEngine(CraftAppraisalRequest request)
    {
        var defaultScope = new CraftAppraisalScope();
        var defaultOptions = new CraftAppraisalOptions();
        var scope = request.Scope ?? defaultScope;
        var options = request.Options ?? defaultOptions;
        return new CoreAppraisal.CraftAppraisalRequest
        {
            SchemaVersion = request.SchemaVersion,
            ItemId = request.ItemId,
            ItemName = request.ItemName ?? string.Empty,
            Quantity = request.Quantity,
            Scope = new CoreAppraisal.CraftAppraisalScope
            {
                Region = string.IsNullOrWhiteSpace(scope.Region) ? defaultScope.Region : scope.Region,
                DataCenter = scope.DataCenter,
                World = scope.World,
            },
            Options = new CoreAppraisal.CraftAppraisalOptions
            {
                HqPolicy = string.IsNullOrWhiteSpace(options.HqPolicy) ? defaultOptions.HqPolicy : options.HqPolicy,
                PricingMode = string.IsNullOrWhiteSpace(options.PricingMode) ? defaultOptions.PricingMode : options.PricingMode,
            },
        };
    }

    public static CraftAppraisalQuote FromEngine(CoreAppraisal.CraftAppraisalQuote quote, string? planUrl) => new()
    {
        SchemaVersion = quote.SchemaVersion,
        ItemId = quote.ItemId,
        ItemName = quote.ItemName,
        RequestedQuantity = quote.RequestedQuantity,
        OutputQuantity = quote.OutputQuantity,
        EstimatedUnitCost = quote.EstimatedUnitCost,
        EstimatedTotalCost = quote.EstimatedTotalCost,
        Currency = quote.Currency,
        QuotedAtUtc = quote.QuotedAtUtc,
        Source = string.IsNullOrWhiteSpace(quote.Source) ? "CraftArchitectHosted" : quote.Source,
        Confidence = quote.Confidence,
        IsComplete = quote.IsComplete,
        AppraisalStatus = quote.AppraisalStatus,
        PlanId = quote.PlanId,
        PlanUrl = planUrl,
        Materials = quote.Materials
            .Select(material => new CraftAppraisalMaterialQuote
            {
                ItemId = material.ItemId,
                ItemName = material.ItemName,
                QuantityPerCraft = material.QuantityPerCraft,
                TotalQuantity = material.TotalQuantity,
                UnitCost = material.UnitCost,
                TotalCost = material.TotalCost,
                AcquisitionSource = material.AcquisitionSource,
                CostSource = material.CostSource,
                CostSourceDetails = material.CostSourceDetails,
                Warnings = material.Warnings,
            })
            .ToArray(),
        Warnings = quote.Warnings,
    };
}
