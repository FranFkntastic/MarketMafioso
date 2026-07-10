using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using MarketMafioso.Server;
using MarketMafioso.Server.Auth;
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

app.Use(ServeDashboardStaticAssetAsync);
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
app.MapGet("/api/capabilities", (
    IWorkshopHostCraftQuoteService craftQuoteService) =>
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
});
app.MapPost("/api/craft/appraise", async (
    CraftAppraisalRequest quoteRequest,
    IWorkshopHostCraftQuoteService quoteService,
    CancellationToken token) =>
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
});

app.MapMarketAcquisitionEndpoints();
app.MapDiagnosticEndpoints();

app.MapDashboardDataEndpoints(enableMarketAcquisition);

app.MapPost("/inventory", SaveInventoryReport);
app.MapPost("/api/inventory", SaveInventoryReport);

app.MapGet("/api/xivdata/items/search", async (
    IHttpClientFactory httpClientFactory,
    string q,
    int? limit,
    CancellationToken token) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "query_required" });

    var client = httpClientFactory.CreateClient();
    var url = $"{xivDataBaseUrl}/items/search?q={Uri.EscapeDataString(q)}&limit={Math.Clamp(limit ?? 12, 1, 50)}";
    using var response = await client.GetAsync(url, token);
    var body = await response.Content.ReadAsStringAsync(token);
    return Results.Content(body, "application/json; charset=utf-8", statusCode: (int)response.StatusCode);
});

app.MapGet("/api/reports", async (InventoryReportStore store, CancellationToken token) =>
{
    var reports = await store.ListSummariesAsync(token);
    return Results.Ok(reports);
});

app.MapGet("/api/reports/latest", async (InventoryReportStore store, CancellationToken token) =>
{
    var report = await store.GetLatestAsync(token);
    return report == null ? Results.NotFound() : Results.Ok(report);
});

app.MapGet("/api/reports/{id}/view", async (string id, InventoryReportStore store, CancellationToken token) =>
{
    var report = await store.GetAsync(id, token);
    return report == null
        ? Results.NotFound()
        : Results.Ok(InventorySnapshotViewBuilder.Build(report));
});

app.MapGet("/api/reports/{id}", async (string id, InventoryReportStore store, CancellationToken token) =>
{
    var report = await store.GetAsync(id, token);
    return report == null ? Results.NotFound() : Results.Ok(report);
});

app.MapGet("/reports/latest/json", async (InventoryReportStore store, CancellationToken token) =>
{
    var report = await store.GetLatestRawJsonAsync(token);
    return RawJsonResult(report);
});

app.MapGet("/reports/{id}/json", async (string id, InventoryReportStore store, CancellationToken token) =>
{
    var report = await store.GetRawJsonAsync(id, token);
    return RawJsonResult(report);
});

app.MapDelete("/api/reports/{id}", async (string id, InventoryReportStore store, CancellationToken token) =>
{
    if (requireApiKey)
        return Results.NotFound();

    var deleted = await store.DeleteAsync(id, token);
    return deleted ? Results.NoContent() : Results.NotFound();
});

app.MapDelete("/api/reports", async (InventoryReportStore store, CancellationToken token) =>
{
    if (requireApiKey)
        return Results.NotFound();

    var deleted = await store.DeleteAllAsync(token);
    return Results.Ok(new { deleted });
});

app.MapGet("/reports/{id}", async (HttpRequest request, string id, InventoryReportStore store, CancellationToken token) =>
{
    var report = await store.GetAsync(id, token);
    return report == null
        ? Results.NotFound(RenderNotFound(id, request.PathBase))
        : Results.Content(
            RenderReportDetails(report, InventorySnapshotViewBuilder.Build(report), request.PathBase),
            "text/html; charset=utf-8");
});

app.MapPost("/reports/{id}/delete", async (HttpRequest request, string id, InventoryReportStore store, CancellationToken token) =>
{
    var deleted = await store.DeleteAsync(id, token);
    return deleted
        ? Results.Redirect($"{request.PathBase}/?deleted={Uri.EscapeDataString($"snapshot {id}")}")
        : Results.NotFound(RenderNotFound(id, request.PathBase));
});

app.MapPost("/reports/delete-all", async (HttpRequest request, InventoryReportStore store, CancellationToken token) =>
{
    var deleted = await store.DeleteAllAsync(token);
    return Results.Redirect($"{request.PathBase}/?deleted={Uri.EscapeDataString($"{deleted:N0} snapshots")}");
});

MapDashboardShellRoute("/");
if (enableMarketAcquisition)
    MapDashboardShellRoute("/acquisition");
MapDashboardShellRoute("/inventory");
MapDashboardShellRoute("/overview");
MapDashboardShellRoute("/settings");

app.Run();

void MapDashboardShellRoute(string route) => app.MapGet(route, ServeBlazorIndex);

async Task<IResult> SaveInventoryReport(
    HttpRequest request,
    InventoryReportStore store,
    IngestKeyAccountResolver accountResolver,
    CancellationToken token)
{
    if (requireApiKey &&
        !HasValidApiKey(
            request,
            WorkshopHostScope.InventoryWrite,
            workshopHostApiKeys))
        return InvalidApiKey();

    string rawJson;
    InventoryReport? report;
    try
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        rawJson = await reader.ReadToEndAsync(token);
        report = JsonSerializer.Deserialize<InventoryReport>(
            rawJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "invalid_json" });
    }

    if (report == null)
        return Results.BadRequest(new { error = "invalid_json" });

    if (report.PlayerInventory.Count == 0 && report.Retainers.Count == 0)
        return Results.BadRequest(new { error = "Report must include at least one player inventory bag or retainer." });

    var suppliedApiKey = request.Headers["X-Api-Key"].ToString();
    var accountId = await accountResolver.ResolveAccountIdAsync(suppliedApiKey, token) ?? 1;
    var stored = await store.SaveAsync(accountId, report, suppliedApiKey, rawJson, token);
    return Results.Created(
        AppUrl(request.PathBase, $"/api/reports/{stored.Id}"),
        CreateInventoryReportResponse(request, publicOrigin, stored));
}

static object CreateInventoryReportResponse(HttpRequest request, string? publicOrigin, StoredInventoryReport stored) => new
{
    stored.Summary.Id,
    stored.Summary.ReceivedAt,
    stored.Summary.CharacterName,
    stored.Summary.HomeWorld,
    stored.Summary.ReportTimestamp,
    stored.Summary.PlayerBagCount,
    stored.Summary.PlayerItemStacks,
    stored.Summary.PlayerItemQuantity,
    stored.Summary.RetainerCount,
    stored.Summary.RetainerItemStacks,
    stored.Summary.RetainerItemQuantity,
    DashboardUrl = PublicAppUrl(request, publicOrigin, "/"),
    ReportUrl = PublicAppUrl(request, publicOrigin, $"/reports/{stored.Id}"),
    ApiReportUrl = PublicAppUrl(request, publicOrigin, $"/api/reports/{stored.Id}"),
};

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

static async Task<IResult> ServeBlazorIndex(
    HttpRequest request,
    HttpResponse response,
    IWebHostEnvironment environment,
    CancellationToken cancellationToken)
{
    var file = environment.WebRootFileProvider.GetFileInfo("index.html");
    if (!file.Exists)
        return Results.NotFound();

    await using var stream = file.CreateReadStream();
    using var reader = new StreamReader(stream, Encoding.UTF8);
    var html = await reader.ReadToEndAsync(cancellationToken);
    var baseHref = string.IsNullOrWhiteSpace(request.PathBase)
        ? "/"
        : $"{request.PathBase.ToString().TrimEnd('/')}/";
    html = html.Replace("<base href=\"/\" />", $"<base href=\"{Html(baseHref)}\" />", StringComparison.Ordinal);
    html = html.Replace(
        "css/app.css",
        $"css/app.css?v={ResolveDashboardAssetVersion(environment, "css/app.css")}",
        StringComparison.Ordinal);
    html = html.Replace(
        "_framework/blazor.webassembly#[.{fingerprint}].js",
        ResolveBlazorBootScript(environment),
        StringComparison.Ordinal);
    response.Headers.CacheControl = "no-cache";
    return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
}

static string ResolveDashboardAssetVersion(IWebHostEnvironment environment, string path)
{
    var file = environment.WebRootFileProvider.GetFileInfo(path);
    if (!file.Exists)
        return "missing";

    return $"{file.Length:x}-{file.LastModified.UtcTicks:x}";
}

static string ResolveBlazorBootScript(IWebHostEnvironment environment)
{
    var directory = environment.WebRootFileProvider.GetDirectoryContents("_framework");
    if (!directory.Exists)
        throw new DirectoryNotFoundException("Dashboard framework assets were not found under wwwroot/_framework.");

    var bootScript = directory
        .Where(file => !file.IsDirectory &&
                       file.Name.StartsWith("blazor.webassembly.", StringComparison.Ordinal) &&
                       file.Name.EndsWith(".js", StringComparison.Ordinal))
        .OrderBy(file => file.Name, StringComparer.Ordinal)
        .FirstOrDefault();

    if (bootScript == null)
        throw new FileNotFoundException("Dashboard Blazor WebAssembly boot script was not found under wwwroot/_framework.");

    return $"_framework/{bootScript.Name}";
}

static async Task ServeDashboardStaticAssetAsync(HttpContext context, RequestDelegate next)
{
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        await next(context);
        return;
    }

    var path = context.Request.Path.Value?.TrimStart('/');
    if (string.IsNullOrWhiteSpace(path) || !IsDashboardStaticAssetPath(path))
    {
        await next(context);
        return;
    }

    var environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
    var resolvedPath = ResolveDashboardStaticAssetPath(environment, path);
    var file = environment.WebRootFileProvider.GetFileInfo(resolvedPath);
    if (!file.Exists || file.IsDirectory)
    {
        await next(context);
        return;
    }

    context.Response.ContentType = ContentTypeForDashboardAsset(resolvedPath);
    if (path.StartsWith("css/", StringComparison.Ordinal) ||
        path.Equals("MarketMafioso.Dashboard.styles.css", StringComparison.Ordinal))
    {
        context.Response.Headers.CacheControl = "no-cache";
    }

    if (HttpMethods.IsHead(context.Request.Method))
        return;

    await using var stream = file.CreateReadStream();
    await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
}

static bool IsDashboardStaticAssetPath(string path) =>
    path.StartsWith("_framework/", StringComparison.Ordinal) ||
    path.StartsWith("_content/", StringComparison.Ordinal) ||
    path.StartsWith("css/", StringComparison.Ordinal) ||
    path.StartsWith("js/", StringComparison.Ordinal) ||
    path.Equals("favicon.png", StringComparison.Ordinal) ||
    path.Equals("icon-192.png", StringComparison.Ordinal) ||
    path.Equals("MarketMafioso.Dashboard.styles.css", StringComparison.Ordinal);

static string ResolveDashboardStaticAssetPath(IWebHostEnvironment environment, string path) =>
    path switch
    {
        "_framework/dotnet.js" => ResolveSingleDashboardAsset(environment, "_framework", "dotnet.", ".js", static name =>
            !name.StartsWith("dotnet.native.", StringComparison.Ordinal) &&
            !name.StartsWith("dotnet.runtime.", StringComparison.Ordinal)),
        _ => path,
    };

static string ResolveSingleDashboardAsset(
    IWebHostEnvironment environment,
    string directoryPath,
    string prefix,
    string suffix,
    Func<string, bool>? predicate = null)
{
    var directory = environment.WebRootFileProvider.GetDirectoryContents(directoryPath);
    if (!directory.Exists)
        return $"{directoryPath}/{prefix.TrimEnd('.')}{suffix}";

    var asset = directory
        .Where(file => !file.IsDirectory &&
                       file.Name.StartsWith(prefix, StringComparison.Ordinal) &&
                       file.Name.EndsWith(suffix, StringComparison.Ordinal) &&
                       (predicate == null || predicate(file.Name)))
        .OrderBy(file => file.Name, StringComparer.Ordinal)
        .FirstOrDefault();

    return asset == null
        ? $"{directoryPath}/{prefix.TrimEnd('.')}{suffix}"
        : $"{directoryPath}/{asset.Name}";
}

static string ContentTypeForDashboardAsset(string path)
{
    var extension = Path.GetExtension(path);
    return extension switch
    {
        ".css" => "text/css; charset=utf-8",
        ".dat" => "application/octet-stream",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".map" => "application/json; charset=utf-8",
        ".png" => "image/png",
        ".wasm" => "application/wasm",
        _ => "application/octet-stream",
    };
}

static IResult RawJsonResult(RawInventoryReportJson? report)
{
    if (report == null)
        return Results.NotFound();

    if (report.RawJson == null)
        return Results.Json(new { error = "raw_json_pruned" }, statusCode: StatusCodes.Status410Gone);

    return Results.Text(report.RawJson, "application/json; charset=utf-8", Encoding.UTF8);
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
             (path.Contains("/lines/", StringComparison.OrdinalIgnoreCase) &&
              path.EndsWith("/progress", StringComparison.OrdinalIgnoreCase))));
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

static IResult InvalidApiKey() => Results.Json(new { error = "invalid_api_key" }, statusCode: StatusCodes.Status401Unauthorized);

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

static string RenderReportDetails(StoredInventoryReport stored, InventorySnapshotView view, PathString pathBase)
{
    var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    });
    var playerSection = RenderOwnerSection(
        view.PlayerInventory,
        "No player inventory items were included in this snapshot.");
    var retainerSections = view.Retainers.Count == 0
        ? "<p class=\"empty\">No retainer inventory was included in this snapshot.</p>"
        : string.Join(Environment.NewLine, view.Retainers.Select(r => RenderOwnerSection(
            r,
            "This retainer has no cached inventory items in this snapshot.")));

    return $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Snapshot {{Html(stored.Id)}}</title>
            <style>
                :root {
                    color-scheme: dark;
                    --bg: #111316;
                    --panel: #191d21;
                    --panel-strong: #20262b;
                    --border: #323a41;
                    --text: #eef1f3;
                    --muted: #aeb6bd;
                    --accent: #bde8c8;
                    --danger: #ffd7db;
                }
                body { margin: 0; font-family: "Segoe UI", system-ui, sans-serif; background: var(--bg); color: var(--text); }
                main { max-width: 1180px; margin: 0 auto; padding: 28px 20px; }
                a { color: var(--accent); }
                .top { display: flex; justify-content: space-between; gap: 12px; align-items: flex-start; margin-bottom: 18px; }
                h1 { margin: 0 0 6px; font-size: 24px; letter-spacing: 0; }
                h2 { margin: 26px 0 10px; font-size: 18px; letter-spacing: 0; }
                h3 { margin: 18px 0 8px; font-size: 15px; letter-spacing: 0; color: var(--accent); }
                p { color: var(--muted); }
                .panel { border: 1px solid var(--border); background: var(--panel); border-radius: 6px; padding: 14px; margin: 12px 0; }
                .summary { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 10px; margin: 16px 0; }
                .metric { border: 1px solid var(--border); background: var(--panel); border-radius: 6px; padding: 12px; }
                .label { color: var(--muted); font-size: 12px; text-transform: uppercase; }
                .value { margin-top: 4px; font-size: 20px; font-weight: 650; }
                dl { display: grid; grid-template-columns: 150px 1fr; gap: 8px 14px; margin: 0; }
                dt { color: var(--muted); }
                dd { margin: 0; }
                form { display: inline; }
                button, .button { border: 1px solid var(--border); border-radius: 5px; background: var(--panel-strong); color: var(--text); padding: 6px 10px; font: inherit; text-decoration: none; cursor: pointer; }
                .danger { background: #3a2024; border-color: #694047; color: var(--danger); }
                .owner { margin: 12px 0 22px; }
                .owner-meta { color: var(--muted); margin: 0 0 10px; }
                .inventory-table { width: 100%; border-collapse: collapse; background: var(--panel); border: 1px solid var(--border); margin: 8px 0 14px; }
                .inventory-table th, .inventory-table td { padding: 8px 10px; border-bottom: 1px solid var(--border); text-align: left; }
                .inventory-table th { color: var(--accent); font-weight: 600; white-space: nowrap; }
                .number { text-align: right; font-variant-numeric: tabular-nums; }
                .empty { padding: 12px; border: 1px solid var(--border); background: var(--panel); border-radius: 6px; }
                pre { overflow: auto; border: 1px solid var(--border); background: var(--panel); border-radius: 6px; padding: 14px; }
                @media (max-width: 900px) {
                    .top { display: block; }
                    .summary { grid-template-columns: repeat(2, minmax(0, 1fr)); }
                    .inventory-table { display: block; overflow-x: auto; }
                }
            </style>
        </head>
        <body>
            <main>
                <div class="top">
                    <div>
                        <h1>Snapshot {{Html(stored.Id)}}</h1>
                        <p>{{Html(view.CharacterName ?? "-")}} @ {{Html(view.HomeWorld ?? "-")}}</p>
                    </div>
                    <div>
                        <a class="button" href="{{Html(AppUrl(pathBase, "/"))}}">Back</a>
                        <a class="button" href="{{Html(AppUrl(pathBase, $"/reports/{stored.Id}/json"))}}">JSON</a>
                        <a class="button" href="{{Html(AppUrl(pathBase, $"/reports/{stored.Id}/json"))}}">Parsed JSON</a>
                        <form method="post" action="{{Html(AppUrl(pathBase, $"/reports/{stored.Id}/delete"))}}" onsubmit="return confirm('Delete snapshot {{Html(stored.Id)}}?');">
                            <button class="danger" type="submit">Delete</button>
                        </form>
                    </div>
                </div>
                <section class="panel">
                    <dl>
                        <dt>Received</dt><dd>{{Html(view.ReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"))}}</dd>
                        <dt>Report timestamp</dt><dd>{{Html(view.ReportTimestamp)}}</dd>
                        <dt>Schema</dt><dd>{{view.Metadata.SchemaVersion}}</dd>
                        <dt>Source</dt><dd>{{Html(view.Metadata.SourcePlugin)}} {{Html(view.Metadata.PluginVersion)}}</dd>
                        <dt>Generated</dt><dd>{{Html(view.Metadata.GeneratedAtUtc)}}</dd>
                    </dl>
                </section>
                <section class="summary">
                    <div class="metric"><div class="label">Total stacks</div><div class="value">{{view.Totals.Stacks:N0}}</div></div>
                    <div class="metric"><div class="label">Total quantity</div><div class="value">{{view.Totals.Quantity:N0}}</div></div>
                    <div class="metric"><div class="label">HQ stacks</div><div class="value">{{view.Totals.HqStacks:N0}}</div></div>
                    <div class="metric"><div class="label">Retainers</div><div class="value">{{view.Totals.Retainers:N0}}</div></div>
                </section>
                <h2>Player Inventory</h2>
                {{playerSection}}
                <h2>Retainers</h2>
                {{retainerSections}}
                <h2>Raw JSON</h2>
                <pre>{{Html(json)}}</pre>
            </main>
        </body>
        </html>
        """;
}

static string RenderOwnerSection(InventoryOwnerView owner, string emptyMessage)
{
    var meta = owner.RetainerId == null
        ? $"{owner.Stacks:N0} stacks / {owner.Quantity:N0} items"
        : $"ID {owner.RetainerId} / updated {owner.LastUpdated ?? "-"} / {owner.Stacks:N0} stacks / {owner.Quantity:N0} items";
    var bags = owner.Bags.Count == 0
        ? $"""<p class="empty">{Html(emptyMessage)}</p>"""
        : string.Join(Environment.NewLine, owner.Bags.Select(RenderBagSection));

    return $$"""
        <section class="owner">
            <h3>{{Html(owner.Name)}}</h3>
            <p class="owner-meta">{{Html(meta)}}</p>
            {{bags}}
        </section>
        """;
}

static string RenderBagSection(InventoryBagView bag)
{
    var rows = bag.Items.Count == 0
        ? """<tr><td colspan="5">No items in this bag.</td></tr>"""
        : string.Join(Environment.NewLine, bag.Items.Select(RenderItemRow));

    return $$"""
        <h3>{{Html(bag.Name)}} <span class="owner-meta">({{bag.Stacks:N0}} stacks / {{bag.Quantity:N0}} items)</span></h3>
        <table class="inventory-table">
            <thead>
                <tr>
                    <th>Item</th>
                    <th class="number">ID</th>
                    <th class="number">Quantity</th>
                    <th>HQ</th>
                    <th class="number">Condition</th>
                </tr>
            </thead>
            <tbody>
                {{rows}}
            </tbody>
        </table>
        """;
}

static string RenderItemRow(InventoryItemView item) =>
    $$"""
        <tr>
            <td>{{Html(item.DisplayName)}}</td>
            <td class="number">{{item.ItemId}}</td>
            <td class="number">{{item.Quantity:N0}}</td>
            <td>{{(item.IsHQ ? "Yes" : "No")}}</td>
            <td class="number">{{Html(FormatCondition(item.Condition))}}</td>
        </tr>
        """;

static string FormatCondition(float condition) =>
    condition <= 0 ? "-" : $"{condition:0.#}%";

static string RenderNotFound(string id, PathString pathBase) =>
    $"""
    <!doctype html>
    <html lang="en">
    <head><meta charset="utf-8"><title>Snapshot not found</title></head>
    <body style="font-family: Segoe UI, system-ui, sans-serif; background: #111316; color: #eef1f3;">
        <main style="max-width: 760px; margin: 40px auto;">
            <h1>Snapshot not found</h1>
            <p>No stored snapshot exists for <code>{Html(id)}</code>.</p>
            <p><a style="color: #bde8c8;" href="{Html(AppUrl(pathBase, "/"))}">Back to receiver</a></p>
        </main>
    </body>
    </html>
    """;

static string AppUrl(PathString pathBase, string path) =>
    $"{pathBase}{path}";

static string PublicAppUrl(HttpRequest request, string? publicOrigin, string path)
{
    var relativeUrl = AppUrl(request.PathBase, path);
    if (!string.IsNullOrWhiteSpace(publicOrigin))
        return $"{publicOrigin.TrimEnd('/')}{relativeUrl}";

    var scheme = FirstHeaderValue(request.Headers["X-Forwarded-Proto"]) ?? request.Scheme;
    var host = FirstHeaderValue(request.Headers["X-Forwarded-Host"]) ?? request.Host.Value;
    if (string.IsNullOrWhiteSpace(host))
        throw new InvalidOperationException("Cannot build public dashboard URL without a request host.");

    return $"{scheme}://{host}{relativeUrl}";
}

static string? FirstHeaderValue(StringValues values)
{
    if (values.Count == 0)
        return null;

    var value = values[0];
    if (string.IsNullOrWhiteSpace(value))
        return null;

    var commaIndex = value.IndexOf(',', StringComparison.Ordinal);
    return commaIndex < 0
        ? value.Trim()
        : value[..commaIndex].Trim();
}

static string Html(string? value) =>
    (value ?? string.Empty)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

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
