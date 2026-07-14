using System.Security.Cryptography;
using System.Text;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using Microsoft.Extensions.Primitives;
using MarketMafioso.Server;
using MarketMafioso.Server.Auth;
using MarketMafioso.Server.Dashboard;
using MarketMafioso.Server.Endpoints;
using MarketMafioso.Server.Migration;
using MarketMafioso.Server.Sqlite;
using MarketMafioso.Server.WorkshopHost;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SqliteConnectionFactory>();
builder.Services.AddSingleton<SqliteSchemaMigrator>();
builder.Services.AddSingleton<DashboardPasswordHasher>();
builder.Services.AddSingleton<DashboardSessionStore>();
builder.Services.AddSingleton<ReceiverBootstrapper>();
builder.Services.AddSingleton<IngestKeyAccountResolver>();
builder.Services.AddSingleton<InventoryReportStore>();
builder.Services.AddSingleton<JsonSnapshotImporter>();
builder.Services.AddSingleton<MarketAcquisitionRequestStore>();
builder.Services.AddSingleton<DiagnosticEventStore>();
builder.Services.AddWorkshopHostCraftAppraisal();
builder.Services.AddScoped<IWorkshopHostCraftQuoteService, CraftArchitectWorkshopHostCraftQuoteService>();
builder.Services.AddHttpClient();

var app = builder.Build();
await app.Services.GetRequiredService<SqliteSchemaMigrator>().MigrateAsync(CancellationToken.None);
await app.Services.GetRequiredService<ReceiverBootstrapper>().BootstrapAsync(CancellationToken.None);
await app.Services.GetRequiredService<JsonSnapshotImporter>().ImportAsync(CancellationToken.None);
var clientApiKey = FirstConfigured(
    app.Configuration["MarketMafioso:ClientApiKey"],
    app.Configuration["MarketMafioso:ApiKey"],
    app.Configuration["MarketMafioso:IngestApiKey"],
    app.Configuration["MarketMafioso:CommandPickupApiKey"]);
var previousClientApiKey = FirstConfigured(
    app.Configuration["MarketMafioso:PreviousClientApiKey"],
    app.Configuration["MarketMafioso:PreviousIngestApiKey"],
    app.Configuration["MarketMafioso:PreviousReadApiKey"]);
var workshopHostApiKeys = WorkshopHostApiKeys.FromConfiguration(
    app.Configuration,
    clientApiKey,
    previousClientApiKey);
var requireApiKey = app.Configuration.GetValue<bool>("MarketMafioso:RequireApiKey") ||
                    !string.IsNullOrWhiteSpace(clientApiKey);
var basePath = app.Configuration["MarketMafioso:BasePath"];
var configuredBasePath = NormalizeConfiguredBasePath(basePath);
var publicOrigin = app.Configuration["MarketMafioso:PublicOrigin"];
var storageLabel = app.Configuration["MarketMafioso:StorageLabel"];
var enableMarketAcquisition = app.Configuration.GetValue<bool>("MarketMafioso:EnableMarketAcquisition");
var xivDataBaseUrl = NormalizeXivDataBaseUrl(FirstConfigured(
    app.Configuration["MarketMafioso:XivDataBaseUrl"],
    DefaultXivDataBaseUrl(publicOrigin)));

if (requireApiKey && string.IsNullOrWhiteSpace(clientApiKey))
    throw new InvalidOperationException("MarketMafioso:ClientApiKey is required when API key authentication is enabled.");

if (configuredBasePath.HasValue)
{
    app.UsePathBase(configuredBasePath);
}

app.UseDashboardStaticAssets();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    var scope = RequiredWorkshopHostScope(context.Request, requireApiKey);
    if (scope != WorkshopHostScope.None &&
        !HasValidApiKey(
            context.Request,
            scope,
            workshopHostApiKeys))
    {
        await WriteUnauthorizedAsync(context);
        return;
    }

    await next(context);
});

app.UseMiddleware<DashboardSessionAuthMiddleware>();

app.MapDashboardAuthEndpoints();

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    utc = DateTimeOffset.UtcNow,
}));
app.MapWorkshopHostEndpoints(enableMarketAcquisition);
app.MapMarketAcquisitionEndpoints();
app.MapDiagnosticEndpoints();

app.MapDashboardDataEndpoints(enableMarketAcquisition);

app.MapInventoryReportEndpoints(requireApiKey, workshopHostApiKeys, publicOrigin);
app.MapXivDataProxyEndpoints(xivDataBaseUrl);

app.MapDashboardShellEndpoints(enableMarketAcquisition);

app.Run();

static PathString NormalizeConfiguredBasePath(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return PathString.Empty;

    var trimmed = value.Trim().TrimEnd('/');
    if (trimmed.Length == 0 || trimmed == "/")
        return PathString.Empty;

    return trimmed.StartsWith("/", StringComparison.Ordinal)
        ? new PathString(trimmed)
        : new PathString($"/{trimmed}");
}

static WorkshopHostScope RequiredWorkshopHostScope(HttpRequest request, bool requireApiKey)
{
    if (!requireApiKey)
        return WorkshopHostScope.None;

    if (IsAcquisitionBrowserCreate(request))
        return WorkshopHostScope.None;

    if (IsAcquisitionBrowserControl(request))
        return WorkshopHostScope.None;

    if (IsAcquisitionBrowserRead(request))
        return WorkshopHostScope.None;

    if (IsApiKeyAcquisitionCreate(request))
        return WorkshopHostScope.AcquisitionQueue;

    if (IsAcquisitionPluginRoute(request))
        return WorkshopHostScope.AcquisitionQueue;

    if (IsReportsApiRead(request))
        return WorkshopHostScope.InventoryRead;

    if (IsWorkshopHostCapabilitiesRead(request))
        return WorkshopHostScope.CapabilitiesRead;

    if (IsWorkshopHostCraftQuote(request))
        return WorkshopHostScope.CraftQuote;

    if (IsInventoryPost(request))
        return WorkshopHostScope.InventoryWrite;

    return WorkshopHostScope.None;
}

static bool IsInventoryPost(HttpRequest request) =>
    HttpMethods.IsPost(request.Method) &&
    (request.Path.Equals("/inventory", StringComparison.OrdinalIgnoreCase) ||
     request.Path.Equals("/api/inventory", StringComparison.OrdinalIgnoreCase));

static bool IsReportsApiRead(HttpRequest request) =>
    HttpMethods.IsGet(request.Method) &&
    request.Path.StartsWithSegments("/api/reports");

static bool IsWorkshopHostCapabilitiesRead(HttpRequest request) =>
    HttpMethods.IsGet(request.Method) &&
    request.Path.Equals("/api/capabilities", StringComparison.OrdinalIgnoreCase);

static bool IsWorkshopHostCraftQuote(HttpRequest request) =>
    HttpMethods.IsPost(request.Method) &&
    request.Path.Equals("/api/craft/appraise", StringComparison.OrdinalIgnoreCase);

static bool IsAcquisitionCreate(HttpRequest request) =>
    HttpMethods.IsPost(request.Method) &&
    (request.Path.Equals("/acquisition/requests", StringComparison.OrdinalIgnoreCase) ||
     request.Path.Equals("/api/acquisition/requests", StringComparison.OrdinalIgnoreCase) ||
     request.Path.Equals("/acquisition/batches", StringComparison.OrdinalIgnoreCase) ||
     request.Path.Equals("/api/acquisition/batches", StringComparison.OrdinalIgnoreCase));

static bool IsApiKeyAcquisitionCreate(HttpRequest request) =>
    IsAcquisitionCreate(request) &&
    request.Headers.ContainsKey("X-Api-Key");

static bool IsAcquisitionBrowserCreate(HttpRequest request) =>
    IsAcquisitionCreate(request) &&
    request.HasFormContentType;

static bool IsAcquisitionBrowserControl(HttpRequest request) =>
    !request.Headers.ContainsKey("X-Api-Key") &&
    ((HttpMethods.IsPost(request.Method) &&
      (request.Path.StartsWithSegments("/acquisition/requests") ||
       request.Path.StartsWithSegments("/api/acquisition/requests")) &&
      (request.Path.Value?.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase) == true ||
       request.Path.Value?.EndsWith("/resend", StringComparison.OrdinalIgnoreCase) == true)) ||
     (HttpMethods.IsPut(request.Method) &&
      (request.Path.StartsWithSegments("/acquisition/batches/") ||
       request.Path.StartsWithSegments("/api/acquisition/batches/"))));

static bool IsAcquisitionBrowserRead(HttpRequest request) =>
    HttpMethods.IsGet(request.Method) &&
    request.Path.Equals("/api/acquisition/requests", StringComparison.OrdinalIgnoreCase);

static bool IsAcquisitionPluginRoute(HttpRequest request) =>
    IsKnownAcquisitionPluginRoute(request) &&
    !IsAcquisitionCreate(request) &&
    !IsAcquisitionBrowserControl(request);

static bool IsKnownAcquisitionPluginRoute(HttpRequest request)
{
    if (HttpMethods.IsGet(request.Method))
    {
        return request.Path.Equals("/acquisition/requests/pending", StringComparison.OrdinalIgnoreCase) ||
               request.Path.Equals("/api/acquisition/requests/pending", StringComparison.OrdinalIgnoreCase) ||
               request.Path.Equals("/acquisition/batches/pending", StringComparison.OrdinalIgnoreCase) ||
               request.Path.Equals("/api/acquisition/batches/pending", StringComparison.OrdinalIgnoreCase) ||
               IsAcquisitionTimelinePath(request.Path) ||
               IsAcquisitionBatchDetailPath(request.Path);
    }

    var path = request.Path.Value ?? string.Empty;
    if (HttpMethods.IsPut(request.Method))
    {
        return path.StartsWith("/acquisition/batches/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/api/acquisition/batches/", StringComparison.OrdinalIgnoreCase);
    }

    if (!HttpMethods.IsPost(request.Method))
        return false;

    return (path.StartsWith("/acquisition/requests/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/acquisition/requests/", StringComparison.OrdinalIgnoreCase)) &&
           (path.EndsWith("/claim", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/accept", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/reject", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/resend", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/progress", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/complete", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/fail", StringComparison.OrdinalIgnoreCase)) ||
           ((path.StartsWith("/acquisition/batches/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/api/acquisition/batches/", StringComparison.OrdinalIgnoreCase)) &&
            (path.EndsWith("/purchases", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/observations", StringComparison.OrdinalIgnoreCase) ||
             (path.Contains("/lines/", StringComparison.OrdinalIgnoreCase) &&
              path.EndsWith("/progress", StringComparison.OrdinalIgnoreCase))));
}

static bool IsAcquisitionTimelinePath(PathString requestPath)
{
    var path = requestPath.Value ?? string.Empty;
    if (!path.StartsWith("/api/acquisition/requests/", StringComparison.OrdinalIgnoreCase) ||
        !path.EndsWith("/timeline", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    return path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 5;
}

static bool IsAcquisitionBatchDetailPath(PathString requestPath)
{
    var path = requestPath.Value ?? string.Empty;
    if (!path.StartsWith("/acquisition/batches/", StringComparison.OrdinalIgnoreCase) &&
        !path.StartsWith("/api/acquisition/batches/", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    return path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length is 3 or 4;
}

static bool HasValidApiKey(
    HttpRequest request,
    WorkshopHostScope scope,
    WorkshopHostApiKeys apiKeys)
{
    var supplied = GetSingleApiKeyHeader(request.Headers["X-Api-Key"]);
    if (string.IsNullOrWhiteSpace(supplied))
        return false;

    return apiKeys.HasKeyForScope(supplied, scope);
}

static string? GetSingleApiKeyHeader(StringValues values)
{
    if (values.Count != 1)
        return null;

    return values[0];
}

static Task WriteUnauthorizedAsync(HttpContext context)
{
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    context.Response.ContentType = "application/json; charset=utf-8";
    return context.Response.WriteAsJsonAsync(new { error = "invalid_api_key" });
}

static string? FirstConfigured(params string?[] values) =>
    values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

static string NormalizeXivDataBaseUrl(string? value) =>
    string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Trim().TrimEnd('/');

static string DefaultXivDataBaseUrl(string? publicOrigin)
{
    var normalizedPublicOrigin = NormalizeXivDataBaseUrl(publicOrigin);
    return string.IsNullOrWhiteSpace(normalizedPublicOrigin)
        ? "https://dev.xivcraftarchitect.com/api/xivdata"
        : $"{normalizedPublicOrigin}/api/xivdata";
}

public partial class Program;

sealed record WorkshopHostApiKeys(
    string? Client,
    string? PreviousClient,
    string? InventoryWrite,
    string? InventoryRead,
    string? CraftQuote,
    string? AcquisitionQueue,
    string? DiagnosticsRead,
    string? AutomationRun)
{
    public static WorkshopHostApiKeys FromConfiguration(
        IConfiguration configuration,
        string? clientApiKey,
        string? previousClientApiKey) => new(
            clientApiKey,
            previousClientApiKey,
            FirstNonBlank(
                configuration["MarketMafioso:InventoryWriteApiKey"],
                configuration["MarketMafioso:IngestWriteApiKey"]),
            FirstNonBlank(
                configuration["MarketMafioso:InventoryReadApiKey"],
                configuration["MarketMafioso:ReportReadApiKey"]),
            configuration["MarketMafioso:CraftQuoteApiKey"],
            FirstNonBlank(
                configuration["MarketMafioso:AcquisitionQueueApiKey"],
                configuration["MarketMafioso:CommandPickupScopedApiKey"]),
            configuration["MarketMafioso:DiagnosticsReadApiKey"],
            configuration["MarketMafioso:AutomationRunApiKey"]);

    public bool HasKeyForScope(string supplied, WorkshopHostScope scope)
    {
        if (MatchesConfiguredKey(supplied, Client) ||
            MatchesConfiguredKey(supplied, PreviousClient))
        {
            return true;
        }

        return scope switch
        {
            WorkshopHostScope.CapabilitiesRead =>
                MatchesConfiguredKey(supplied, InventoryWrite) ||
                MatchesConfiguredKey(supplied, InventoryRead) ||
                MatchesConfiguredKey(supplied, CraftQuote) ||
                MatchesConfiguredKey(supplied, AcquisitionQueue) ||
                MatchesConfiguredKey(supplied, DiagnosticsRead) ||
                MatchesConfiguredKey(supplied, AutomationRun),
            WorkshopHostScope.InventoryWrite => MatchesConfiguredKey(supplied, InventoryWrite),
            WorkshopHostScope.InventoryRead => MatchesConfiguredKey(supplied, InventoryRead),
            WorkshopHostScope.CraftQuote => MatchesConfiguredKey(supplied, CraftQuote),
            WorkshopHostScope.AcquisitionQueue => MatchesConfiguredKey(supplied, AcquisitionQueue),
            WorkshopHostScope.DiagnosticsRead => MatchesConfiguredKey(supplied, DiagnosticsRead),
            WorkshopHostScope.AutomationRun => MatchesConfiguredKey(supplied, AutomationRun),
            _ => false,
        };
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool MatchesConfiguredKey(string supplied, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return false;

        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var configuredBytes = Encoding.UTF8.GetBytes(configured);

        return suppliedBytes.Length == configuredBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
    }
}

enum WorkshopHostScope
{
    None,
    CapabilitiesRead,
    InventoryWrite,
    InventoryRead,
    CraftQuote,
    AcquisitionQueue,
    DiagnosticsRead,
    AutomationRun,
}
