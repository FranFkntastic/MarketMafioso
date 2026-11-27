using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using MarketMafioso.Server;
using MarketMafioso.Server.Inventory;
using MarketMafioso.Server.Auth;
using MarketMafioso.Server.Dashboard;
using MarketMafioso.Server.Endpoints;
using MarketMafioso.Server.Migration;
using MarketMafioso.Server.MarketDiagnostics;
using MarketMafioso.Server.MarketIntelligence;
using MarketMafioso.Server.Sqlite;
using MarketMafioso.Server.WorkshopHost;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
const string workshopHostBrowserCorsPolicy = "WorkshopHostBrowserClients";
const string privateNetworkAccessRequestHeader = "Access-Control-Request-Private-Network";
const string privateNetworkAccessResponseHeader = "Access-Control-Allow-Private-Network";
var craftAppraisal = WorkshopHostCraftAppraisalSettings.FromConfiguration(
    builder.Configuration,
    builder.Environment);
builder.Services.AddCors(options =>
{
    options.AddPolicy(workshopHostBrowserCorsPolicy, policy =>
        policy
            .SetIsOriginAllowed(origin => IsAllowedWorkshopHostBrowserOrigin(builder.Configuration, origin))
            .AllowAnyHeader()
            .AllowAnyMethod());
    options.AddPolicy(WorkshopHostCraftAppraisalSettings.PlanSnapshotCorsPolicy, policy =>
        policy
            .WithOrigins(craftAppraisal.AppOrigin)
            .WithMethods(HttpMethods.Get)
            .AllowAnyHeader());
});
builder.Services.AddSingleton<SqliteConnectionFactory>();
builder.Services.AddSingleton<SqliteSchemaMigrator>();
builder.Services.AddSingleton<DashboardPasswordHasher>();
builder.Services.AddSingleton<DashboardSessionStore>();
builder.Services.AddSingleton<ReceiverBootstrapper>();
builder.Services.AddSingleton<IngestKeyAccountResolver>();
builder.Services.AddSingleton<WorkshopHostCredentialStore>();
builder.Services.AddSingleton<ClientPairingCodeStore>();
builder.Services.AddSingleton<InventoryReportStore>();
builder.Services.AddHostedService<InventoryRetentionWorker>();
builder.Services.AddSingleton<JsonSnapshotImporter>();
builder.Services.AddSingleton<MarketAcquisitionRequestStore>();
builder.Services.AddSingleton<WorkshopHandoffStore>();
builder.Services.AddSingleton<DiagnosticEventStore>();
builder.Services.AddSingleton<MarketDiagnosticStore>();
builder.Services.AddSingleton<UniversalisMarketDiagnosticClient>();
builder.Services.AddSingleton<MarketDiagnosticAlertSink>();
builder.Services.AddSingleton<MarketDiagnosticCollector>();
builder.Services.AddSingleton<MarketIntelligenceStore>();
builder.Services.AddHostedService<MarketIntelligenceProjectionWorker>();
if (builder.Configuration.GetValue<bool>("MarketMafioso:MarketDiagnostics:Enabled"))
    builder.Services.AddHostedService<MarketDiagnosticBackgroundService>();
builder.Services.AddSingleton(craftAppraisal);
builder.Services.AddHostedCraftAppraisal(craftAppraisal.Options, builder.Environment.ContentRootPath);
builder.Services.AddHttpClient();

var app = builder.Build();
app.Logger.LogInformation(
    "Craft appraisal runs in-process: plan snapshots at {PlanDirectory}, market cache at {CacheDirectory}, plan links open {AppOrigin} with snapshots served from {SnapshotOrigin}.",
    craftAppraisal.Options.PlanDirectory,
    craftAppraisal.Options.CacheDirectory,
    craftAppraisal.AppOrigin,
    craftAppraisal.PublicOriginConfigured
        ? craftAppraisal.Options.PublicApiOrigin + WorkshopHostCraftAppraisalSettings.PlanSnapshotPath
        : "the request host (MarketMafioso:PublicOrigin is not set)");
foreach (var retiredSetting in new[] { "CraftArchitectAppraiseUrl", "XivDataBaseUrl" })
{
    if (!string.IsNullOrWhiteSpace(app.Configuration[$"MarketMafioso:{retiredSetting}"]))
    {
        app.Logger.LogWarning(
            "MarketMafioso:{RetiredSetting} is set but no longer used: craft quotes and item search now run inside Workshop Host. Remove it from your configuration.",
            retiredSetting);
    }
}
await app.Services.GetRequiredService<SqliteSchemaMigrator>().MigrateAsync(CancellationToken.None);
await app.Services.GetRequiredService<ReceiverBootstrapper>().BootstrapAsync(CancellationToken.None);
await app.Services.GetRequiredService<JsonSnapshotImporter>().ImportAsync(CancellationToken.None);
var clientApiKey = FirstConfigured(
    app.Configuration["MarketMafioso:ClientApiKey"],
    app.Configuration["MarketMafioso:ApiKey"],
    app.Configuration["MarketMafioso:IngestApiKey"],
    app.Configuration["MarketMafioso:CommandPickupApiKey"]);
var requireApiKey = app.Configuration.GetValue<bool>("MarketMafioso:RequireApiKey") ||
                    !string.IsNullOrWhiteSpace(clientApiKey);
var basePath = app.Configuration["MarketMafioso:BasePath"];
var configuredBasePath = NormalizeConfiguredBasePath(basePath);
var publicOrigin = app.Configuration["MarketMafioso:PublicOrigin"];
var storageLabel = app.Configuration["MarketMafioso:StorageLabel"];
var enableMarketAcquisition = app.Configuration.GetValue("MarketMafioso:EnableMarketAcquisition", true);

if (configuredBasePath.HasValue)
{
    app.UsePathBase(configuredBasePath);
}

app.UseDashboardStaticAssets();
app.UseStaticFiles();
app.UseRouting();
// A public HTTPS Craft Architect page fetching a plan snapshot from a host on localhost or a
// LAN address triggers a private-network preflight; answer it for the plan route only.
app.Use(async (context, next) =>
{
    if (context.Request.Headers.ContainsKey(privateNetworkAccessRequestHeader) &&
        context.Request.Path.StartsWithSegments(WorkshopHostCraftAppraisalSettings.PlanSnapshotPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            context.Request.Headers.Origin.ToString().TrimEnd('/'),
            craftAppraisal.AppOrigin,
            StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers[privateNetworkAccessResponseHeader] = "true";
    }

    await next(context);
});
app.UseCors(workshopHostBrowserCorsPolicy);

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (SqliteException exception) when (
        !context.Response.HasStarted &&
        exception.SqliteErrorCode is 5 or 6)
    {
        app.Logger.LogWarning(
            exception,
            "SQLite busy in {Operation} while handling {Method} {Path}; returning a retryable response.",
            SqliteConnectionFactory.WaitingOperation(exception),
            context.Request.Method,
            context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers["Retry-After"] = "1";
        await context.Response.WriteAsJsonAsync(
            new { error = "receiver_busy" },
            CancellationToken.None);
    }
});

app.Use(async (context, next) =>
{
    var scope = RequiredWorkshopHostScope(context.Request, requireApiKey);
    if (scope is not null &&
        !await context.RequestServices
            .GetRequiredService<WorkshopHostCredentialStore>()
            .IsAuthorizedAsync(
                GetSingleApiKeyHeader(context.Request.Headers["X-Api-Key"]),
                scope.Value,
                context.RequestAborted))
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
app.MapWorkshopHostEndpoints(enableMarketAcquisition, requireApiKey);
app.MapMarketAcquisitionEndpoints();
app.MapMarketAcquisitionWorkOrderEndpoints();
app.MapWorkshopHandoffEndpoints();
app.MapDiagnosticEndpoints();
app.MapMarketDiagnosticEndpoints();
app.MapMarketIntelligenceEndpoints();

app.MapDashboardDataEndpoints(enableMarketAcquisition);
app.MapDashboardClientCredentialEndpoints();
app.MapClientPairingEndpoints();

app.MapInventoryReportEndpoints(requireApiKey, publicOrigin);
app.MapXivDataEndpoints();

app.MapDashboardShellEndpoints(enableMarketAcquisition);

app.Run();

static bool IsAllowedWorkshopHostBrowserOrigin(IConfiguration configuration, string origin)
{
    var normalizedOrigin = origin.Trim().TrimEnd('/');
    return configuration
        .GetSection("MarketMafioso:AllowedOrigins")
        .GetChildren()
        .Select(entry => entry.Value?.Trim().TrimEnd('/'))
        .Any(allowed => string.Equals(allowed, normalizedOrigin, StringComparison.OrdinalIgnoreCase));
}

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

static WorkshopHostCredentialScope? RequiredWorkshopHostScope(HttpRequest request, bool requireApiKey)
{
    if (!requireApiKey)
        return null;

    if (MarketAcquisitionEndpointClassifier.IsBrowserCreate(request))
        return null;

    if (MarketAcquisitionEndpointClassifier.IsBrowserControl(request))
        return null;

    if (MarketAcquisitionEndpointClassifier.IsBrowserListRead(request))
        return null;

    if (MarketAcquisitionEndpointClassifier.RequiresPluginCredential(request))
        return WorkshopHostCredentialScope.AcquisitionQueue;

    if (IsReportsApiRead(request))
        return WorkshopHostCredentialScope.InventoryRead;

    if (IsWorkshopHostCapabilitiesRead(request))
        return WorkshopHostCredentialScope.CapabilitiesRead;

    if (IsWorkshopHostCraftQuote(request))
        return WorkshopHostCredentialScope.CraftQuote;

    if (IsInventoryPost(request))
        return WorkshopHostCredentialScope.InventoryWrite;

    if (IsMarketDiagnosticSalePost(request))
        return WorkshopHostCredentialScope.InventoryWrite;

    return null;
}

static bool IsInventoryPost(HttpRequest request) =>
    HttpMethods.IsPost(request.Method) &&
    (request.Path.Equals("/inventory", StringComparison.OrdinalIgnoreCase) ||
     request.Path.Equals("/api/inventory", StringComparison.OrdinalIgnoreCase) ||
     request.Path.Equals("/inventory/delta", StringComparison.OrdinalIgnoreCase) ||
     request.Path.Equals("/api/inventory/delta", StringComparison.OrdinalIgnoreCase));

static bool IsMarketDiagnosticSalePost(HttpRequest request) =>
    HttpMethods.IsPost(request.Method) &&
    request.Path.Equals("/api/market-diagnostics/sales", StringComparison.OrdinalIgnoreCase);

static bool IsReportsApiRead(HttpRequest request) =>
    HttpMethods.IsGet(request.Method) &&
    request.Path.StartsWithSegments("/api/reports");

static bool IsWorkshopHostCapabilitiesRead(HttpRequest request) =>
    HttpMethods.IsGet(request.Method) &&
    request.Path.Equals("/api/capabilities", StringComparison.OrdinalIgnoreCase);

static bool IsWorkshopHostCraftQuote(HttpRequest request) =>
    HttpMethods.IsPost(request.Method) &&
    request.Path.Equals("/api/craft/appraise", StringComparison.OrdinalIgnoreCase);

static string? GetSingleApiKeyHeader(Microsoft.Extensions.Primitives.StringValues values)
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

public partial class Program;
