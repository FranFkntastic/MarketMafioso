using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using MarketMafioso.Server;
using MarketMafioso.Server.Auth;
using MarketMafioso.Server.Dashboard;
using MarketMafioso.Server.Endpoints;
using MarketMafioso.Server.Migration;
using MarketMafioso.Server.Sqlite;
using MarketMafioso.Server.WorkshopHost;

var builder = WebApplication.CreateBuilder(args);
const string workshopHostBrowserCorsPolicy = "WorkshopHostBrowserClients";
builder.Services.AddCors(options =>
{
    options.AddPolicy(workshopHostBrowserCorsPolicy, policy =>
        policy
            .SetIsOriginAllowed(origin => IsAllowedWorkshopHostBrowserOrigin(builder.Configuration, origin))
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.AddSingleton<SqliteConnectionFactory>();
builder.Services.AddSingleton<SqliteSchemaMigrator>();
builder.Services.AddSingleton<DashboardPasswordHasher>();
builder.Services.AddSingleton<DashboardSessionStore>();
builder.Services.AddSingleton<ReceiverBootstrapper>();
builder.Services.AddSingleton<IngestKeyAccountResolver>();
builder.Services.AddSingleton<WorkshopHostCredentialStore>();
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

if (configuredBasePath.HasValue)
{
    app.UsePathBase(configuredBasePath);
}

app.UseDashboardStaticAssets();
app.UseStaticFiles();
app.UseRouting();
app.UseCors(workshopHostBrowserCorsPolicy);

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
app.MapDiagnosticEndpoints();

app.MapDashboardDataEndpoints(enableMarketAcquisition);
app.MapDashboardClientCredentialEndpoints();

app.MapInventoryReportEndpoints(requireApiKey, publicOrigin);
app.MapXivDataProxyEndpoints(xivDataBaseUrl);

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

    return null;
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
