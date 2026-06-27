using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using MarketMafioso.Server;
using MarketMafioso.Server.Auth;
using MarketMafioso.Server.Migration;
using MarketMafioso.Server.Sqlite;

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
var requireApiKey = app.Configuration.GetValue<bool>("MarketMafioso:RequireApiKey") ||
                    !string.IsNullOrWhiteSpace(clientApiKey);
var basePath = app.Configuration["MarketMafioso:BasePath"];
var configuredBasePath = NormalizeConfiguredBasePath(basePath);
var publicOrigin = app.Configuration["MarketMafioso:PublicOrigin"];
var storageLabel = app.Configuration["MarketMafioso:StorageLabel"];
var xivDataBaseUrl = NormalizeXivDataBaseUrl(FirstConfigured(
    app.Configuration["MarketMafioso:XivDataBaseUrl"],
    DefaultXivDataBaseUrl(publicOrigin)));

if (requireApiKey && string.IsNullOrWhiteSpace(clientApiKey))
    throw new InvalidOperationException("MarketMafioso:ClientApiKey is required when API key authentication is enabled.");

if (configuredBasePath.HasValue)
{
    app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments(configuredBasePath, out var remainingPath))
        {
            await next(context);
            return;
        }

        var originalPath = context.Request.Path;
        var originalPathBase = context.Request.PathBase;
        context.Request.PathBase = originalPathBase.Add(configuredBasePath);
        context.Request.Path = remainingPath.HasValue ? remainingPath : "/";
        try
        {
            await next(context);
        }
        finally
        {
            context.Request.Path = originalPath;
            context.Request.PathBase = originalPathBase;
        }
    });
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.Use(async (context, next) =>
{
    var purpose = RequiredApiKeyPurpose(context.Request, requireApiKey);
    if (purpose != ApiKeyPurpose.None &&
        !HasValidApiKey(
            context.Request,
            purpose,
            clientApiKey,
            previousClientApiKey))
    {
        await WriteUnauthorizedAsync(context);
        return;
    }

    await next(context);
});

app.UseMiddleware<DashboardSessionAuthMiddleware>();

app.MapPost("/auth/login", LoginDashboard);
app.MapPost("/auth/logout", LogoutDashboard);
app.MapGet("/auth/session", GetDashboardSession);

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    utc = DateTimeOffset.UtcNow,
}));

app.MapGet("/api/acquisition/requests", async (
    MarketAcquisitionRequestStore acquisitionStore,
    CancellationToken token) =>
{
    var acquisitionRequests = await acquisitionStore.ListRecentAsync(100, token);
    return Results.Ok(acquisitionRequests);
});

app.MapGet("/api/diagnostics/events", async (
    DiagnosticEventStore diagnostics,
    int? limit,
    string? category,
    string? severity,
    string? correlationId,
    CancellationToken token) =>
{
    var events = await diagnostics.ListRecentAsync(limit ?? 100, category, severity, correlationId, token);
    return Results.Ok(events);
});

app.MapGet("/api/diagnostics/events/stream", async (
    HttpResponse response,
    DiagnosticEventStore diagnostics,
    CancellationToken token) =>
{
    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";
    var events = await diagnostics.ListRecentAsync(100, null, null, null, token);
    await WriteSseEventAsync(response, "snapshot", events, token);
});

app.MapGet("/api/events/stream", async (
    HttpResponse response,
    MarketAcquisitionRequestStore acquisitionStore,
    DiagnosticEventStore diagnostics,
    CancellationToken token) =>
{
    response.Headers.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";
    response.Headers.Connection = "keep-alive";

    while (!token.IsCancellationRequested)
    {
        var acquisitionRequests = await acquisitionStore.ListRecentAsync(100, token);
        await WriteSseEventAsync(response, "acquisition", acquisitionRequests, token);

        var events = await diagnostics.ListRecentAsync(25, null, null, null, token);
        await WriteSseEventAsync(response, "diagnostics", events, token);

        await Task.Delay(TimeSpan.FromSeconds(3), token);
    }
});

app.MapGet("/acquisition/requests/recent", async (
    HttpRequest request,
    MarketAcquisitionRequestStore acquisitionStore,
    CancellationToken token) =>
{
    var acquisitionRequests = await acquisitionStore.ListRecentAsync(50, token);
    return Results.Json(BuildAcquisitionQueueUpdate(acquisitionRequests, request.PathBase));
});

app.MapPost("/inventory", SaveInventoryReport);
app.MapPost("/api/inventory", SaveInventoryReport);

app.MapPost("/acquisition/requests", CreateAcquisitionRequest);
app.MapPost("/api/acquisition/requests", CreateAcquisitionRequest);
app.MapGet("/acquisition/requests/pending", ListPendingAcquisitionRequests);
app.MapPost("/acquisition/requests/{id}/claim", ClaimAcquisitionRequest);
app.MapPost("/acquisition/requests/{id}/accept", AcceptAcquisitionRequest);
app.MapPost("/acquisition/requests/{id}/reject", RejectAcquisitionRequest);
app.MapPost("/acquisition/requests/{id}/cancel", CancelAcquisitionRequest);
app.MapPost("/api/acquisition/requests/{id}/cancel", CancelAcquisitionRequest);
app.MapPost("/acquisition/requests/{id}/resend", ResendAcquisitionRequest);
app.MapPost("/api/acquisition/requests/{id}/resend", ResendAcquisitionRequest);
app.MapPost("/acquisition/requests/{id}/progress", ReportAcquisitionProgress);
app.MapPost("/acquisition/requests/{id}/complete", CompleteAcquisitionRequest);
app.MapPost("/acquisition/requests/{id}/fail", FailAcquisitionRequest);

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

app.MapGet("/{*path:nonfile}", ServeBlazorIndex);

app.Run();

async Task<IResult> LoginDashboard(
    HttpRequest request,
    HttpResponse response,
    DashboardSessionStore sessions,
    DashboardLoginRequest login,
    CancellationToken token)
{
    var created = await sessions.CreateAsync(login.Username, login.Password, token);
    if (created == null)
        return Results.Unauthorized();

    response.Cookies.Append(
        DashboardSessionStore.CookieName,
        created.Token,
        CreateDashboardCookieOptions(request, created.Session.ExpiresAtUtc));

    return Results.Ok(new
    {
        user = new
        {
            created.Session.UserId,
            created.Session.Username,
        },
        created.Session.ExpiresAtUtc,
    });
}

async Task<IResult> LogoutDashboard(
    HttpRequest request,
    HttpResponse response,
    DashboardSessionStore sessions,
    CancellationToken token)
{
    await sessions.RevokeAsync(request.Cookies[DashboardSessionStore.CookieName], token);
    response.Cookies.Delete(
        DashboardSessionStore.CookieName,
        CreateDashboardCookieOptions(request, DateTimeOffset.UtcNow.AddDays(-1)));
    return Results.Ok(new { ok = true });
}

async Task<IResult> GetDashboardSession(
    HttpRequest request,
    DashboardSessionStore sessions,
    CancellationToken token)
{
    var session = await sessions.GetAsync(request.Cookies[DashboardSessionStore.CookieName], token);
    if (session == null)
        return Results.Unauthorized();

    return Results.Ok(new
    {
        user = new
        {
            session.UserId,
            session.Username,
        },
        session.ExpiresAtUtc,
    });
}

async Task<IResult> SaveInventoryReport(
    HttpRequest request,
    InventoryReportStore store,
    IngestKeyAccountResolver accountResolver,
    CancellationToken token)
{
    if (requireApiKey &&
        !HasValidApiKey(
            request,
            ApiKeyPurpose.Ingest,
            clientApiKey,
            previousClientApiKey))
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

async Task<IResult> CreateAcquisitionRequest(
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

static CookieOptions CreateDashboardCookieOptions(HttpRequest request, DateTimeOffset expiresAt) => new()
{
    HttpOnly = true,
    IsEssential = true,
    SameSite = SameSiteMode.Lax,
    Secure = request.IsHttps,
    Expires = expiresAt,
    Path = string.IsNullOrWhiteSpace(request.PathBase) ? "/" : request.PathBase.ToString(),
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

static async Task WriteSseEventAsync<T>(
    HttpResponse response,
    string eventName,
    T payload,
    CancellationToken cancellationToken)
{
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await response.WriteAsync($"event: {eventName}\n", cancellationToken);
    await response.WriteAsync($"data: {json}\n\n", cancellationToken);
    await response.Body.FlushAsync(cancellationToken);
}

static async Task<IResult> ServeBlazorIndex(
    HttpRequest request,
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
    return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
}

async Task<IResult> ListPendingAcquisitionRequests(
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

async Task<IResult> ClaimAcquisitionRequest(
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

async Task<IResult> AcceptAcquisitionRequest(
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

Task<IResult> RejectAcquisitionRequest(
    string id,
    MarketAcquisitionLifecycleRequest lifecycleRequest,
    MarketAcquisitionRequestStore store,
    CancellationToken token) =>
    ApplyAcquisitionLifecycleAsync(store.RejectAsync, id, lifecycleRequest, token);

async Task<IResult> CancelAcquisitionRequest(
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

async Task<IResult> ResendAcquisitionRequest(
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

Task<IResult> ReportAcquisitionProgress(
    string id,
    MarketAcquisitionLifecycleRequest lifecycleRequest,
    MarketAcquisitionRequestStore store,
    CancellationToken token) =>
    ApplyAcquisitionLifecycleAsync(store.ReportProgressAsync, id, lifecycleRequest, token);

Task<IResult> CompleteAcquisitionRequest(
    string id,
    MarketAcquisitionLifecycleRequest lifecycleRequest,
    MarketAcquisitionRequestStore store,
    CancellationToken token) =>
    ApplyAcquisitionLifecycleAsync(store.CompleteAsync, id, lifecycleRequest, token);

Task<IResult> FailAcquisitionRequest(
    string id,
    MarketAcquisitionLifecycleRequest lifecycleRequest,
    MarketAcquisitionRequestStore store,
    CancellationToken token) =>
    ApplyAcquisitionLifecycleAsync(store.FailAsync, id, lifecycleRequest, token);

static async Task<IResult> ApplyAcquisitionLifecycleAsync(
    Func<string, MarketAcquisitionLifecycleRequest, CancellationToken, Task<MarketAcquisitionRequestView?>> apply,
    string id,
    MarketAcquisitionLifecycleRequest lifecycleRequest,
    CancellationToken token)
{
    try
    {
        var result = await apply(id, lifecycleRequest, token);
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

static IResult RawJsonResult(RawInventoryReportJson? report)
{
    if (report == null)
        return Results.NotFound();

    if (report.RawJson == null)
        return Results.Json(new { error = "raw_json_pruned" }, statusCode: StatusCodes.Status410Gone);

    return Results.Text(report.RawJson, "application/json; charset=utf-8", Encoding.UTF8);
}

static ApiKeyPurpose RequiredApiKeyPurpose(HttpRequest request, bool requireApiKey)
{
    if (!requireApiKey)
        return ApiKeyPurpose.None;

    if (IsAcquisitionBrowserCreate(request))
        return ApiKeyPurpose.None;

    if (IsAcquisitionBrowserControl(request))
        return ApiKeyPurpose.None;

    if (IsAcquisitionBrowserRead(request))
        return ApiKeyPurpose.None;

    if (IsAcquisitionCreate(request))
        return ApiKeyPurpose.Read;

    if (IsAcquisitionPluginRoute(request))
        return ApiKeyPurpose.CommandPickup;

    if (IsReportsApiRead(request))
        return ApiKeyPurpose.Read;

    if (IsInventoryPost(request))
        return ApiKeyPurpose.Ingest;

    return ApiKeyPurpose.None;
}

static bool IsInventoryPost(HttpRequest request) =>
    HttpMethods.IsPost(request.Method) &&
    (request.Path.Equals("/inventory", StringComparison.OrdinalIgnoreCase) ||
     request.Path.Equals("/api/inventory", StringComparison.OrdinalIgnoreCase));

static bool IsReportsApiRead(HttpRequest request) =>
    HttpMethods.IsGet(request.Method) &&
    request.Path.StartsWithSegments("/api/reports");

static bool IsAcquisitionCreate(HttpRequest request) =>
    HttpMethods.IsPost(request.Method) &&
    request.Path.Equals("/acquisition/requests", StringComparison.OrdinalIgnoreCase);

static bool IsAcquisitionBrowserCreate(HttpRequest request) =>
    IsAcquisitionCreate(request) &&
    request.HasFormContentType;

static bool IsAcquisitionBrowserControl(HttpRequest request) =>
    HttpMethods.IsPost(request.Method) &&
    request.HasFormContentType &&
    request.Path.StartsWithSegments("/acquisition/requests") &&
    (request.Path.Value?.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase) == true ||
     request.Path.Value?.EndsWith("/resend", StringComparison.OrdinalIgnoreCase) == true);

static bool IsAcquisitionBrowserRead(HttpRequest request) =>
    HttpMethods.IsGet(request.Method) &&
    request.Path.Equals("/acquisition/requests/recent", StringComparison.OrdinalIgnoreCase);

static bool WantsJsonResponse(HttpRequest request)
{
    return request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
}

static bool IsDashboardApiRoute(HttpRequest request) =>
    request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

static bool IsAcquisitionPluginRoute(HttpRequest request) =>
    request.Path.StartsWithSegments("/acquisition/requests") &&
    !IsAcquisitionCreate(request) &&
    !IsAcquisitionBrowserControl(request);

static bool HasValidApiKey(
    HttpRequest request,
    ApiKeyPurpose purpose,
    string? clientApiKey,
    string? previousClientApiKey)
{
    var supplied = GetSingleApiKeyHeader(request.Headers["X-Api-Key"]);
    if (string.IsNullOrWhiteSpace(supplied))
        return false;

    return purpose switch
    {
        ApiKeyPurpose.Ingest or ApiKeyPurpose.Read or ApiKeyPurpose.CommandPickup =>
            MatchesConfiguredKey(supplied, clientApiKey) ||
            MatchesConfiguredKey(supplied, previousClientApiKey),
        _ => false,
    };
}

static string? GetSingleApiKeyHeader(StringValues values)
{
    if (values.Count != 1)
        return null;

    return values[0];
}

static bool MatchesConfiguredKey(string supplied, string? configured)
{
    if (string.IsNullOrWhiteSpace(configured))
        return false;

    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    var configuredBytes = Encoding.UTF8.GetBytes(configured);

    return suppliedBytes.Length == configuredBytes.Length &&
           CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
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

static string StorageDisplayName(string reportDirectory, string? storageLabel, bool isHosted) =>
    !string.IsNullOrWhiteSpace(storageLabel)
        ? storageLabel
        : isHosted
            ? "hosted receiver storage"
            : reportDirectory;

static async Task<MarketAcquisitionCreateRequest> ReadAcquisitionFormAsync(
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

static int ParseInt(string value, string fieldName) =>
    int.TryParse(value, out var parsed)
        ? parsed
        : throw new ArgumentException($"{fieldName} must be a whole number.");

static uint ParseUInt(string value, string fieldName) =>
    uint.TryParse(value, out var parsed)
        ? parsed
        : throw new ArgumentException($"{fieldName} must be a positive whole number.");

static uint ParseOptionalUInt(string value, string fieldName) =>
    string.IsNullOrWhiteSpace(value)
        ? 0
        : ParseUInt(value, fieldName);

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

static string RenderDashboard(
    IReadOnlyList<ReportSummary> reports,
    IReadOnlyList<CharacterSummary> characters,
    long? selectedCharacterId,
    bool allCharacters,
    string storageDisplayName,
    string? deleted,
    string? acquisition,
    PathString pathBase)
{
    var rows = new StringBuilder();
    foreach (var report in reports)
    {
        rows.AppendLine($"""
            <tr>
                <td><a href="{Html(AppUrl(pathBase, $"/reports/{report.Id}"))}">{Html(report.Id)}</a></td>
                <td>{Html(report.ReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"))}</td>
                <td>{Html(report.CharacterName ?? "-")}</td>
                <td>{Html(report.HomeWorld ?? "-")}</td>
                <td>{report.PlayerItemStacks:N0} stacks / {report.PlayerItemQuantity:N0} items</td>
                <td>{report.RetainerCount:N0} retainers / {report.RetainerItemStacks:N0} stacks</td>
                <td class="actions">
                    <a class="button" href="{Html(AppUrl(pathBase, $"/reports/{report.Id}"))}">View</a>
                    <a class="button" href="{Html(AppUrl(pathBase, $"/reports/{report.Id}/json"))}">JSON</a>
                    <form method="post" action="{Html(AppUrl(pathBase, $"/reports/{report.Id}/delete"))}" onsubmit="return confirm('Delete snapshot {Html(report.Id)}?');">
                        <button class="danger" type="submit">Delete</button>
                    </form>
                </td>
            </tr>
            """);
    }

    var latest = reports.Count == 0
        ? "Never"
        : reports.Max(r => r.ReceivedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
    var selectedCharacter = selectedCharacterId == null
        ? null
        : characters.FirstOrDefault(c => c.Id == selectedCharacterId.Value);
    var scope = allCharacters
        ? "All Characters"
        : selectedCharacter == null
            ? "Latest Character"
            : CharacterLabel(selectedCharacter);
    var characterFilters = RenderCharacterFilters(characters, selectedCharacterId, allCharacters, pathBase);
    var notice = string.IsNullOrWhiteSpace(deleted)
        ? string.Empty
        : $"""<p class="notice">Deleted <code>{Html(deleted)}</code>.</p>""";
    var acquisitionNotice = string.IsNullOrWhiteSpace(acquisition)
        ? string.Empty
        : $"""<p class="notice">Created acquisition request <code>{Html(acquisition)}</code>. Open <code>/mmf</code> in-game and fetch dashboard requests.</p>""";
    var emptyState = reports.Count == 0
        ? "<p class=\"empty\">No snapshots yet. Point MarketMafioso at <code>http://localhost:8080/inventory</code> and send one.</p>"
        : string.Empty;

    return $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>MarketMafioso Receiver</title>
            <style>
                :root {
                    color-scheme: dark;
                    --bg: #111316;
                    --panel: #191d21;
                    --panel-strong: #20262b;
                    --border: #323a41;
                    --text: #eef1f3;
                    --muted: #aeb6bd;
                    --accent: #9bd7ad;
                    --accent-strong: #bde8c8;
                    --danger: #e06c75;
                    --danger-bg: #3a2024;
                }
                body {
                    margin: 0;
                    font-family: "Segoe UI", system-ui, sans-serif;
                    background: var(--bg);
                    color: var(--text);
                }
                main { max-width: 1240px; margin: 0 auto; padding: 28px 20px; }
                header {
                    display: flex;
                    align-items: end;
                    justify-content: space-between;
                    gap: 20px;
                    margin-bottom: 18px;
                }
                h1 { margin: 0 0 4px; font-size: 26px; letter-spacing: 0; }
                h2 { margin: 26px 0 10px; font-size: 17px; letter-spacing: 0; }
                p { color: var(--muted); }
                code {
                    padding: 2px 5px;
                    border-radius: 4px;
                    background: var(--panel-strong);
                    color: var(--accent-strong);
                }
                .toolbar { display: flex; gap: 8px; align-items: center; }
                .filters {
                    display: flex;
                    gap: 8px;
                    align-items: center;
                    flex-wrap: wrap;
                    margin: 14px 0 12px;
                }
                .filter {
                    border: 1px solid var(--border);
                    border-radius: 5px;
                    background: var(--panel);
                    color: var(--text);
                    padding: 6px 9px;
                    text-decoration: none;
                    font-size: 13px;
                }
                .filter.active {
                    border-color: var(--accent);
                    background: #203028;
                    color: var(--accent-strong);
                }
                .cards {
                    display: grid;
                    grid-template-columns: repeat(3, minmax(0, 1fr));
                    gap: 10px;
                    margin: 16px 0;
                }
                .card {
                    border: 1px solid var(--border);
                    background: var(--panel);
                    border-radius: 6px;
                    padding: 12px;
                }
                .label {
                    color: var(--muted);
                    font-size: 12px;
                    text-transform: uppercase;
                }
                .value { margin-top: 4px; font-size: 22px; font-weight: 650; }
                .path {
                    margin: 12px 0 18px;
                    padding: 10px 12px;
                    border: 1px solid var(--border);
                    background: var(--panel);
                    border-radius: 6px;
                    color: var(--muted);
                    overflow-wrap: anywhere;
                }
                table {
                    width: 100%;
                    border-collapse: collapse;
                    margin-top: 20px;
                    background: var(--panel);
                    border: 1px solid var(--border);
                }
                th, td {
                    padding: 10px 12px;
                    border-bottom: 1px solid var(--border);
                    text-align: left;
                    white-space: nowrap;
                }
                th { color: var(--accent-strong); font-weight: 600; }
                a { color: var(--accent-strong); }
                form { display: inline; margin: 0; }
                button, .button {
                    border: 1px solid var(--border);
                    border-radius: 5px;
                    background: var(--panel-strong);
                    color: var(--text);
                    padding: 6px 10px;
                    font: inherit;
                    text-decoration: none;
                    cursor: pointer;
                }
                button:hover, .button:hover { border-color: var(--accent); }
                .danger {
                    background: var(--danger-bg);
                    border-color: #694047;
                    color: #ffd7db;
                }
                .danger:hover { border-color: var(--danger); }
                .actions { display: flex; gap: 6px; align-items: center; }
                .empty {
                    padding: 16px;
                    border: 1px solid var(--border);
                    background: var(--panel);
                    border-radius: 6px;
                }
                .notice {
                    padding: 10px 12px;
                    border: 1px solid #425a45;
                    background: #1b2a1f;
                    border-radius: 6px;
                    color: #cae8cf;
                }
                .form-grid {
                    display: grid;
                    grid-template-columns: repeat(4, minmax(0, 1fr));
                    gap: 10px;
                    margin: 10px 0;
                }
                label { display: grid; gap: 4px; color: var(--muted); font-size: 12px; }
                input, select {
                    width: 100%;
                    box-sizing: border-box;
                    border: 1px solid var(--border);
                    border-radius: 5px;
                    background: var(--panel-strong);
                    color: var(--text);
                    padding: 7px 8px;
                    font: inherit;
                }
                @media (max-width: 900px) {
                    header { display: block; }
                    .cards { grid-template-columns: repeat(1, minmax(0, 1fr)); }
                    .form-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
                    table { display: block; overflow-x: auto; }
                }
            </style>
        </head>
        <body>
            <main>
                <header>
                    <div>
                        <h1>MarketMafioso Receiver</h1>
                        <p>Local control panel for received inventory snapshots.</p>
                    </div>
                    <div class="toolbar">
                        <a class="button" href="{{Html(AppUrl(pathBase, "/inventory"))}}">Inventory</a>
                        <a class="button" href="{{Html(AppUrl(pathBase, "/acquisition"))}}">Acquisition</a>
                        <a class="button" href="{{Html(AppUrl(pathBase, "/diagnostics"))}}">Diagnostics</a>
                        <a class="button" href="{{Html(AppUrl(pathBase, "/"))}}">Refresh</a>
                        <form method="post" action="{{Html(AppUrl(pathBase, "/reports/delete-all"))}}" onsubmit="return confirm('Delete all stored snapshots?');">
                            <button class="danger" type="submit">Delete All</button>
                        </form>
                    </div>
                </header>
                {{notice}}
                {{acquisitionNotice}}
                {{characterFilters}}
                <section class="cards">
                    <div class="card"><div class="label">Snapshots</div><div class="value">{{reports.Count:N0}}</div></div>
                    <div class="card"><div class="label">Latest received</div><div class="value" style="font-size: 15px;">{{Html(latest)}}</div></div>
                    <div class="card"><div class="label">Scope</div><div class="value" style="font-size: 15px;">{{Html(scope)}}</div></div>
                </section>
                <div class="path">Storage: <code>{{Html(storageDisplayName)}}</code></div>
                <h2>Market Acquisition</h2>
                <form method="post" action="{{Html(AppUrl(pathBase, "/acquisition/requests"))}}">
                    <input type="hidden" name="schemaVersion" value="1">
                    <input type="hidden" name="idempotencyKey" value="{{Guid.NewGuid():N}}">
                    <div class="form-grid">
                        <label>Character<input name="targetCharacterName" autocomplete="off" required></label>
                        <label>World<input name="targetWorld" autocomplete="off" required></label>
                        <label>Region<input name="region" value="North America" required></label>
                        <label>Item ID<input name="itemId" inputmode="numeric" required></label>
                        <label>Item name<input name="itemName" autocomplete="off"></label>
                        <label>Quantity mode<select name="quantityMode"><option>TargetQuantity</option><option>AllBelowThreshold</option></select></label>
                        <label>Target / max quantity<input name="quantity" inputmode="numeric"></label>
                        <label>HQ policy<select name="hqPolicy"><option>Either</option><option>NQOnly</option><option>HQOnly</option></select></label>
                        <label>Max unit price<input name="maxUnitPrice" inputmode="numeric" required></label>
                        <label>Gil cap (optional)<input name="maxTotalGil" inputmode="numeric"></label>
                        <label>World mode<select name="worldMode"><option>Recommended</option><option>Selected</option><option>CurrentWorldOnly</option><option>AllWorldSweep</option></select></label>
                        <label>Pickup expiry seconds<input name="expiresInSeconds" inputmode="numeric" value="90" required></label>
                    </div>
                    <button type="submit">Create Request</button>
                </form>
                {{emptyState}}
                <h2>Snapshots</h2>
                <table>
                    <thead>
                        <tr>
                            <th>Snapshot</th>
                            <th>Received</th>
                            <th>Character</th>
                            <th>World</th>
                            <th>Player Inventory</th>
                            <th>Retainers</th>
                            <th>Actions</th>
                        </tr>
                    </thead>
                    <tbody>
                        {{rows}}
                    </tbody>
                </table>
            </main>
        </body>
        </html>
        """;
}

static string RenderAcquisitionDashboard(
    IReadOnlyList<CharacterSummary> characters,
    long? selectedCharacterId,
    CharacterSummary? selectedCharacter,
    IReadOnlyList<MarketAcquisitionRequestView> acquisitionRequests,
    string? acquisition,
    PathString pathBase,
    string xivDataBaseUrl)
{
    var acquisitionNotice = string.IsNullOrWhiteSpace(acquisition)
        ? string.Empty
        : $"""<p class="notice">Created request <code>{Html(acquisition)}</code>. Open <code>/mmf</code> in-game, select <code>Market Acquisition</code>, and fetch dashboard requests.</p>""";
    var targetCharacter = selectedCharacter?.CharacterName ?? string.Empty;
    var targetWorld = selectedCharacter?.HomeWorld ?? string.Empty;
    var visibleRequests = VisibleAcquisitionQueueRequests(acquisitionRequests);
    var activeCount = visibleRequests.Count(request =>
        request.Status is MarketAcquisitionStatuses.PendingPickup
            or MarketAcquisitionStatuses.Claimed
            or MarketAcquisitionStatuses.AcceptedInPlugin
            or MarketAcquisitionStatuses.Running);
    var latestRequest = visibleRequests.FirstOrDefault();
    var latestRequestLabel = latestRequest == null
        ? "No staged request"
        : FormatAcquisitionItem(latestRequest);
    var latestRequestStatus = latestRequest == null
        ? "Idle"
        : FormatAcquisitionStatus(latestRequest.Status);
    var latestRequestEvent = latestRequest == null
        ? "-"
        : FormatAcquisitionLatestEvent(latestRequest);
    var latestRequestExpiry = latestRequest == null
        ? "-"
        : FormatAcquisitionExpiry(latestRequest);
    var queueRows = RenderAcquisitionQueueRows(visibleRequests, pathBase);
    var characterHeader = RenderAcquisitionCharacterHeader(characters, selectedCharacterId, selectedCharacter, pathBase);
    var activeSummary = $"{activeCount:N0} active / {visibleRequests.Count:N0} recent";
    var selectedRequestName = latestRequest == null
        ? "None selected"
        : FormatAcquisitionItem(latestRequest);
    var refreshUrl = AppUrl(pathBase, "/acquisition/requests/recent");

    return $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>MarketMafioso Acquisition</title>
            <style>
                :root {
                    color-scheme: dark;
                    --bg: #101317;
                    --bar: #131820;
                    --panel: #171d23;
                    --panel-2: #1d252d;
                    --panel-3: #222c36;
                    --line: #2e3945;
                    --line-soft: #25303a;
                    --text: #e9eef4;
                    --muted: #9aa8b7;
                    --subtle: #718194;
                    --accent: #62b6ff;
                    --chip: #263442;
                    font-family: "Segoe UI", system-ui, sans-serif;
                }
                * { box-sizing: border-box; }
                body { margin: 0; min-height: 100vh; background: var(--bg); color: var(--text); font-size: 14px; }
                .sr-only {
                    position: absolute;
                    width: 1px;
                    height: 1px;
                    padding: 0;
                    margin: -1px;
                    overflow: hidden;
                    clip: rect(0, 0, 0, 0);
                    white-space: nowrap;
                    border: 0;
                }
                .shell { min-height: 100vh; display: grid; grid-template-rows: 54px 1fr 30px; }
                .topbar {
                    display: flex;
                    align-items: center;
                    justify-content: space-between;
                    gap: 18px;
                    padding: 0 22px;
                    border-bottom: 1px solid var(--line);
                    background: var(--bar);
                }
                .brand { display: flex; align-items: baseline; gap: 12px; min-width: 0; }
                .brand strong { font-size: 16px; font-weight: 700; }
                .brand span { color: var(--muted); white-space: nowrap; }
                .tabs { display: flex; gap: 4px; }
                .tab {
                    display: inline-flex;
                    align-items: center;
                    height: 34px;
                    padding: 0 11px;
                    border: 1px solid transparent;
                    border-radius: 6px;
                    color: var(--muted);
                    text-decoration: none;
                }
                .tab.active { color: var(--text); border-color: var(--line); background: var(--panel-2); }
                .acquisition-main {
                    min-width: 0;
                    padding: 18px 20px 22px;
                    display: grid;
                    grid-template-columns: minmax(420px, 520px) minmax(560px, 1fr);
                    gap: 16px;
                }
                code { color: var(--accent); }
                .notice {
                    grid-column: 1 / -1;
                    margin: 0;
                    padding: 10px 12px;
                    border: 1px solid #315270;
                    border-radius: 6px;
                    background: #152333;
                    color: #d8eaff;
                }
                .pane {
                    min-width: 0;
                    border: 1px solid var(--line);
                    border-radius: 8px;
                    background: var(--panel);
                    overflow: hidden;
                }
                .pane-head {
                    display: flex;
                    align-items: center;
                    justify-content: space-between;
                    gap: 12px;
                    padding: 11px 13px;
                    border-bottom: 1px solid var(--line);
                    background: #1b232c;
                }
                .pane-head h1, .pane-head h2 {
                    margin: 0;
                    font-size: 15px;
                    letter-spacing: 0;
                }
                .pane-head span {
                    color: var(--muted);
                    font-size: 12px;
                    white-space: nowrap;
                }
                .selected-character {
                    display: flex;
                    align-items: center;
                    justify-content: flex-end;
                    min-width: 0;
                }
                .selected-character select {
                    width: auto;
                    max-width: 210px;
                    height: 30px;
                    color: var(--muted);
                    border-color: transparent;
                    background: transparent;
                    padding-right: 24px;
                }
                .pane-body {
                    padding: 13px;
                }
                .section {
                    padding: 12px;
                    border: 1px solid var(--line-soft);
                    border-radius: 7px;
                    background: #141a20;
                    margin-bottom: 11px;
                }
                .section-title {
                    margin: 0 0 10px;
                    color: var(--accent);
                    font-size: 12px;
                    font-weight: 700;
                    text-transform: uppercase;
                    letter-spacing: .04em;
                }
                .request-form {
                    display: grid;
                    gap: 0;
                }
                .grid {
                    display: grid;
                    grid-template-columns: repeat(2, minmax(0, 1fr));
                    gap: 10px;
                }
                .grid.two { grid-template-columns: repeat(2, minmax(0, 1fr)); }
                .grid.three { grid-template-columns: repeat(3, minmax(0, 1fr)); }
                label { display: grid; gap: 5px; color: var(--muted); font-size: 12px; }
                input, select, button {
                    height: 34px;
                    border: 1px solid var(--line);
                    border-radius: 6px;
                    background: #0f141a;
                    color: var(--text);
                    font: inherit;
                }
                input, select { width: 100%; padding: 0 10px; }
                input[readonly] { color: var(--muted); background: #111820; }
                button {
                    padding: 0 12px;
                    background: var(--panel-2);
                    cursor: pointer;
                }
                button:hover { border-color: var(--accent); }
                button.primary {
                    border-color: #3e6688;
                    background: #19334a;
                    color: #d8ecff;
                    font-weight: 650;
                }
                .suggestions {
                    display: grid;
                    gap: 6px;
                    min-height: 0;
                    margin-top: 8px;
                }
                .suggestion {
                    display: grid;
                    height: auto;
                    min-height: 42px;
                    gap: 2px;
                    padding: 7px 9px;
                    text-align: left;
                    align-content: center;
                }
                .suggestion strong,
                .suggestion span {
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }
                .suggestion span {
                    color: var(--muted);
                    font-size: 12px;
                }
                .suggestion.muted,
                .suggestion.error {
                    min-height: 34px;
                    padding: 8px 9px;
                    border: 1px dashed var(--line);
                    border-radius: 6px;
                    color: var(--muted);
                    background: #10161d;
                }
                .suggestion.error {
                    color: #ffd2d6;
                    border-color: #5a3034;
                    background: #201417;
                }
                .stage-status {
                    min-height: 0;
                    padding: 8px 10px;
                    border: 1px solid var(--line);
                    border-radius: 6px;
                    color: var(--muted);
                    background: #10161d;
                    line-height: 1.45;
                }
                .stage-status:empty {
                    display: none;
                }
                .stage-status.error {
                    color: #ffd2d6;
                    border-color: #5a3034;
                    background: #201417;
                }
                .stage-status.good {
                    color: #c8f0d5;
                    border-color: #2e5a3f;
                    background: #14231a;
                }
                .button-row {
                    display: flex;
                    align-items: center;
                    justify-content: space-between;
                    gap: 10px;
                    margin-top: 12px;
                }
                .preview {
                    display: grid;
                    gap: 8px;
                    padding: 10px;
                    border: 1px solid #315270;
                    border-radius: 7px;
                    background: #142131;
                }
                .preview strong { font-size: 13px; }
                .preview-line {
                    color: var(--muted);
                    line-height: 1.45;
                }
                .chips {
                    display: flex;
                    flex-wrap: wrap;
                    gap: 6px;
                }
                .chip {
                    display: inline-flex;
                    align-items: baseline;
                    min-height: 24px;
                    padding: 3px 8px;
                    border-radius: 999px;
                    background: var(--chip);
                    color: var(--muted);
                    font-size: 12px;
                    white-space: nowrap;
                }
                .chip.good { color: #c8f0d5; background: #1e3a2b; }
                .chip.warn { color: #ffe4a8; background: #3a3020; }
                .toolbar {
                    display: grid;
                    grid-template-columns: minmax(220px, 1fr) 180px 140px;
                    gap: 9px;
                    padding: 12px 13px;
                    border-bottom: 1px solid var(--line);
                    background: #141a20;
                }
                .table-wrap { overflow: auto; }
                table {
                    width: 100%;
                    border-collapse: separate;
                    border-spacing: 0;
                    table-layout: fixed;
                }
                th, td {
                    padding: 8px 9px;
                    border-bottom: 1px solid var(--line-soft);
                    border-right: 1px solid var(--line);
                    vertical-align: middle;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }
                th:last-child, td:last-child { border-right: 0; }
                th {
                    color: var(--muted);
                    background: #202832;
                    font-size: 12px;
                    font-weight: 700;
                    text-align: left;
                    text-transform: uppercase;
                    letter-spacing: .03em;
                }
                tr:nth-child(even) td { background: #141a20; }
                tbody tr:hover td { background: rgba(98, 182, 255, .08); }
                .item {
                    display: grid;
                    gap: 2px;
                    min-width: 0;
                }
                .item strong, .item span {
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }
                .item span {
                    color: var(--subtle);
                    font-size: 12px;
                }
                .number { text-align: right; font-variant-numeric: tabular-nums; }
                .actions {
                    display: flex;
                    align-items: center;
                    gap: 6px;
                    overflow: visible;
                }
                .actions form { margin: 0; }
                .actions button { height: 28px; padding: 0 8px; font-size: 12px; }
                .empty-cell {
                    height: 74px;
                    color: var(--muted);
                    text-align: center;
                    white-space: normal;
                }
                .status {
                    display: inline-flex;
                    align-items: center;
                    gap: 6px;
                    min-height: 24px;
                    padding: 2px 8px;
                    border-radius: 999px;
                    font-size: 12px;
                    white-space: nowrap;
                }
                .status.pending { background: #2d3540; color: #cbd7e4; }
                .status.claimed { background: #26384b; color: #b8ddff; }
                .status.accepted { background: #1e3a2b; color: #c8f0d5; }
                .status.running { background: #3a3020; color: #ffe4a8; }
                .status.complete { background: #1e3a2b; color: #c8f0d5; }
                .status.failed { background: #3d2528; color: #ffd2d6; }
                .queue-table th {
                    padding: 0;
                    height: 34px;
                }
                .queue-table td[data-resize-col] { position: relative; }
                .queue-table td[data-resize-col].separator-hover { cursor: col-resize; }
                .queue-table td[data-resize-col].separator-hover::after {
                    content: "";
                    position: absolute;
                    top: 0;
                    right: -1px;
                    bottom: 0;
                    width: 3px;
                    background: var(--accent);
                    pointer-events: none;
                }
                .th-inner {
                    display: grid;
                    grid-template-columns: minmax(0, 1fr) 6px;
                    align-items: stretch;
                    height: 34px;
                }
                .th-label {
                    display: flex;
                    align-items: center;
                    min-width: 0;
                    padding: 0 9px;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }
                .resizer { cursor: col-resize; }
                .resizer:hover { background: var(--accent); }
                .status-stack {
                    display: grid;
                    gap: 4px;
                    min-width: 0;
                }
                .lifecycle-note {
                    color: var(--muted);
                    font-size: 12px;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }
                .detail {
                    display: grid;
                    grid-template-columns: repeat(4, minmax(0, 1fr));
                    gap: 8px;
                    padding: 11px;
                    border-top: 1px solid var(--line);
                    background: #11171d;
                }
                .metric {
                    min-width: 0;
                    border: 1px solid var(--line-soft);
                    border-radius: 6px;
                    padding: 8px;
                    background: #151c23;
                }
                .metric span {
                    display: block;
                    color: var(--muted);
                    font-size: 11px;
                    margin-bottom: 4px;
                }
                .metric strong {
                    display: block;
                    font-size: 13px;
                    overflow: hidden;
                    text-overflow: ellipsis;
                    white-space: nowrap;
                }
                .statusbar {
                    display: flex;
                    align-items: center;
                    justify-content: space-between;
                    gap: 12px;
                    padding: 0 20px;
                    border-top: 1px solid var(--line);
                    background: #11161c;
                    color: var(--muted);
                    font-size: 12px;
                }
                @media (max-width: 1120px) {
                    .acquisition-main { grid-template-columns: 1fr; }
                }
                @media (max-width: 720px) {
                    .topbar, .statusbar { display: block; padding-block: 8px; }
                    .tabs { margin-top: 10px; flex-wrap: wrap; }
                    .acquisition-main { padding: 12px; }
                    .grid, .grid.three, .toolbar, .detail { grid-template-columns: 1fr; }
                }
            </style>
        </head>
        <body>
            <div class="shell">
                <header class="topbar">
                    <div class="brand">
                        <strong>MarketMafioso</strong>
                        <span>Market Acquisition</span>
                    </div>
                    <nav class="tabs" aria-label="Dashboard sections">
                        <a class="tab" href="{{Html(AppUrl(pathBase, "/"))}}">Snapshots</a>
                        <a class="tab" href="{{Html(AppUrl(pathBase, "/inventory"))}}">Inventory</a>
                        <a class="tab active" href="{{Html(AppUrl(pathBase, "/acquisition"))}}">Acquisition</a>
                        <a class="tab" href="{{Html(AppUrl(pathBase, "/diagnostics"))}}">Diagnostics</a>
                    </nav>
                </header>
                <main class="acquisition-main">
                    {{acquisitionNotice}}
                    <section class="pane request-pane">
                        <div class="pane-head">
                            <h1>New Purchase Request</h1>
                            <span class="sr-only">Create Dashboard Request</span>
                            {{characterHeader}}
                        </div>
                        <div class="pane-body">
                            {{RenderAcquisitionRequestForm(pathBase, targetCharacter, targetWorld, xivDataBaseUrl)}}
                        </div>
                    </section>

                    <section class="pane queue-pane">
                        <div class="pane-head">
                            <h2>Request Queue</h2>
                            <span id="acquisitionActiveSummary">{{Html(activeSummary)}}</span>
                        </div>
                        <div class="toolbar">
                            <input id="acquisitionQueueFilter" type="search" placeholder="Filter by item, world, status" aria-label="Filter request queue">
                            <select id="acquisitionStatusFilter" aria-label="Request status filter">
                                <option>All statuses</option>
                                <option>Pending pickup</option>
                                <option>Claimed</option>
                                <option>Accepted</option>
                                <option>Failed</option>
                            </select>
                            <button type="button" onclick="refreshAcquisitionQueue()">Refresh</button>
                        </div>
                        <div class="table-wrap">
                                <table id="acquisitionQueueTable" class="queue-table">
                                    <colgroup>
                                        <col data-col="item" style="width: 22%">
                                        <col data-col="qty" style="width: 10%">
                                        <col data-col="max-unit" style="width: 10%">
                                        <col data-col="routing" style="width: 15%">
                                        <col data-col="target" style="width: 15%">
                                        <col data-col="status" style="width: 10%">
                                        <col data-col="age" style="width: 8%">
                                        <col data-col="actions" style="width: 10%">
                                    </colgroup>
                                    <thead>
                                        <tr>
                                            <th><div class="th-inner"><span class="th-label">Item</span><span class="resizer" data-resize="item"></span></div></th>
                                            <th class="number"><div class="th-inner"><span class="th-label">Qty</span><span class="resizer" data-resize="qty"></span></div></th>
                                            <th class="number"><div class="th-inner"><span class="th-label">Max Unit</span><span class="resizer" data-resize="max-unit"></span></div></th>
                                            <th><div class="th-inner"><span class="th-label">Routing</span><span class="resizer" data-resize="routing"></span></div></th>
                                            <th><div class="th-inner"><span class="th-label">Target</span><span class="resizer" data-resize="target"></span></div></th>
                                            <th><div class="th-inner"><span class="th-label">Status</span><span class="resizer" data-resize="status"></span></div></th>
                                            <th><div class="th-inner"><span class="th-label">Age</span><span class="resizer" data-resize="age"></span></div></th>
                                            <th><div class="th-inner"><span class="th-label">Actions</span><span class="resizer" data-resize="actions"></span></div></th>
                                        </tr>
                                    </thead>
                                    <tbody id="acquisitionQueueBody">
                                        {{queueRows}}
                                    </tbody>
                                </table>
                        </div>
                        <div class="detail">
                            <div class="metric"><span>Selected request</span><strong id="selectedRequestName">{{Html(selectedRequestName)}}</strong></div>
                            <div class="metric"><span>Pickup endpoint</span><strong>Ready</strong></div>
                            <div class="metric"><span>Expires in</span><strong id="latestRequestExpiry">{{Html(latestRequestExpiry)}}</strong></div>
                            <div class="metric"><span>Plugin status</span><strong id="latestRequestStatus">{{Html(latestRequestStatus)}}</strong><span id="latestRequestEvent">{{Html(latestRequestEvent)}}</span></div>
                        </div>
                    </section>
                </main>
                <footer class="statusbar">
                    <span>Plugin pickup uses the same client API key as inventory ingest.</span>
                    <span>Dashboard creates intent only; the plugin validates live market rows.</span>
                </footer>
            </div>
            <script>
                const acquisitionQueueRefreshUrl = '{{Html(refreshUrl)}}';
                let acquisitionResizeState = { active: false, current: null, neighbor: null, startX: 0, tableWidth: 0, currentStart: 0, neighborStart: 0, direction: 1 };
                let acquisitionRefreshTimer = null;
                let isStagingAcquisitionQueue = false;

                function getAcquisitionColumnPercent(col) { return Number.parseFloat(col.style.width || '0') || 0; }
                function findAcquisitionResizePair(columnKey) {
                    const cols = [...document.querySelectorAll('#acquisitionQueueTable col[data-col]')];
                    const index = cols.findIndex(col => col.dataset.col === columnKey);
                    if (index < 0) return null;
                    if (index < cols.length - 1) return { current: cols[index], neighbor: cols[index + 1], direction: 1 };
                    if (index > 0) return { current: cols[index], neighbor: cols[index - 1], direction: -1 };
                    return null;
                }
                function beginAcquisitionResize(columnKey, event) {
                    const pair = findAcquisitionResizePair(columnKey);
                    if (!pair) return false;
                    acquisitionResizeState = {
                        active: true,
                        current: pair.current,
                        neighbor: pair.neighbor,
                        direction: pair.direction,
                        startX: event.clientX,
                        tableWidth: document.getElementById('acquisitionQueueTable').getBoundingClientRect().width,
                        currentStart: getAcquisitionColumnPercent(pair.current),
                        neighborStart: getAcquisitionColumnPercent(pair.neighbor)
                    };
                    document.body.style.cursor = 'col-resize';
                    event.preventDefault();
                    event.stopPropagation();
                    return true;
                }
                function isNearAcquisitionSeparator(cell, event) {
                    const rect = cell.getBoundingClientRect();
                    return rect.right - event.clientX <= 7;
                }
                function wireAcquisitionResizeHandles() {
                    document.querySelectorAll('#acquisitionQueueTable .resizer').forEach(handle => {
                        if (handle.dataset.resizeWired === 'true') return;
                        handle.dataset.resizeWired = 'true';
                        handle.addEventListener('mousedown', event => beginAcquisitionResize(handle.dataset.resize, event));
                    });
                    document.querySelectorAll('#acquisitionQueueTable td[data-resize-col]').forEach(cell => {
                        if (cell.dataset.resizeWired === 'true') return;
                        cell.dataset.resizeWired = 'true';
                        cell.addEventListener('mousemove', event => cell.classList.toggle('separator-hover', isNearAcquisitionSeparator(cell, event)));
                        cell.addEventListener('mouseleave', () => cell.classList.remove('separator-hover'));
                        cell.addEventListener('mousedown', event => {
                            if (isNearAcquisitionSeparator(cell, event)) beginAcquisitionResize(cell.dataset.resizeCol, event);
                        });
                    });
                }
                window.addEventListener('mousemove', event => {
                    if (!acquisitionResizeState.active) return;
                    const minPercent = 6;
                    const deltaPercent = ((event.clientX - acquisitionResizeState.startX) / acquisitionResizeState.tableWidth) * 100 * acquisitionResizeState.direction;
                    const combined = acquisitionResizeState.currentStart + acquisitionResizeState.neighborStart;
                    const current = Math.min(combined - minPercent, Math.max(minPercent, acquisitionResizeState.currentStart + deltaPercent));
                    acquisitionResizeState.current.style.width = `${current.toFixed(2)}%`;
                    acquisitionResizeState.neighbor.style.width = `${(combined - current).toFixed(2)}%`;
                });
                window.addEventListener('mouseup', () => {
                    acquisitionResizeState.active = false;
                    acquisitionResizeState.current = null;
                    acquisitionResizeState.neighbor = null;
                    document.body.style.cursor = '';
                    document.querySelectorAll('#acquisitionQueueTable .separator-hover').forEach(cell => cell.classList.remove('separator-hover'));
                });

                async function refreshAcquisitionQueue(force = false) {
                    if (isStagingAcquisitionQueue && !force) return;
                    try {
                        const response = await fetch(acquisitionQueueRefreshUrl, {
                            headers: { 'Accept': 'application/json' },
                            cache: 'no-store',
                            credentials: 'same-origin'
                        });
                        if (!response.ok) {
                            document.getElementById('latestRequestEvent').textContent = `Queue refresh failed. HTTP ${response.status}.`;
                            return;
                        }
                        const payload = await response.json();
                        document.getElementById('acquisitionQueueBody').innerHTML = payload.queueRows;
                        document.getElementById('acquisitionActiveSummary').textContent = payload.activeSummary;
                        document.getElementById('selectedRequestName').textContent = payload.selectedRequestName;
                        document.getElementById('latestRequestExpiry').textContent = payload.latestRequestExpiry;
                        document.getElementById('latestRequestStatus').textContent = payload.latestRequestStatus;
                        document.getElementById('latestRequestEvent').textContent = payload.latestRequestEvent;
                        wireAcquisitionResizeHandles();
                        applyAcquisitionQueueFilter();
                    } catch (error) {
                        document.getElementById('latestRequestEvent').textContent = 'Queue refresh failed. Check browser network details.';
                    }
                }

                function startAcquisitionQueueRefresh() {
                    stopAcquisitionQueueRefresh();
                    acquisitionRefreshTimer = window.setInterval(() => refreshAcquisitionQueue(), 3000);
                }

                function stopAcquisitionQueueRefresh() {
                    if (acquisitionRefreshTimer == null) return;
                    window.clearInterval(acquisitionRefreshTimer);
                    acquisitionRefreshTimer = null;
                }

                function applyAcquisitionQueueFilter() {
                    const text = document.getElementById('acquisitionQueueFilter').value.trim().toLowerCase();
                    const status = document.getElementById('acquisitionStatusFilter').value.toLowerCase();
                    document.querySelectorAll('#acquisitionQueueBody tr[data-status]').forEach(row => {
                        const matchesText = !text || row.textContent.toLowerCase().includes(text);
                        const matchesStatus = status === 'all statuses' || row.dataset.status.toLowerCase().includes(status.replace(' pickup', ''));
                        row.hidden = !(matchesText && matchesStatus);
                    });
                }
                document.getElementById('acquisitionQueueFilter')?.addEventListener('input', applyAcquisitionQueueFilter);
                document.getElementById('acquisitionStatusFilter')?.addEventListener('change', applyAcquisitionQueueFilter);
                wireAcquisitionResizeHandles();
                startAcquisitionQueueRefresh();
            </script>
        </body>
        </html>
        """;
}

static string RenderAcquisitionCharacterHeader(
    IReadOnlyList<CharacterSummary> characters,
    long? selectedCharacterId,
    CharacterSummary? selectedCharacter,
    PathString pathBase)
{
    var selectedLabel = selectedCharacter == null
        ? "No character selected"
        : CharacterLabel(selectedCharacter);
    if (characters.Count < 2)
        return $"""<span>{Html(selectedLabel)}</span>""";

    return $$"""
        <form class="selected-character" method="get" action="{{Html(AppUrl(pathBase, "/acquisition"))}}">
            <select name="characterId" onchange="this.form.submit()" aria-label="Default character">{{RenderCharacterOptions(characters, selectedCharacterId)}}</select>
        </form>
        """;
}

static string RenderAcquisitionRequestForm(
    PathString pathBase,
    string targetCharacter,
    string targetWorld,
    string xivDataBaseUrl) =>
    $$"""
        <form class="request-form" method="post" action="{{Html(AppUrl(pathBase, "/acquisition/requests"))}}" data-xiv-data-base-url="{{Html(xivDataBaseUrl)}}">
            <input type="hidden" name="schemaVersion" value="1">
            <input type="hidden" name="idempotencyKey" value="{{Guid.NewGuid():N}}">
            <input id="selectedItemId" type="hidden" name="itemId">
            <input id="selectedItemName" type="hidden" name="itemName">
            <div class="section">
                <p class="section-title">Target</p>
                <div class="grid three">
                    <label>Character<input name="targetCharacterName" value="{{Html(targetCharacter)}}" autocomplete="off" required></label>
                    <label>Home world<input name="targetWorld" value="{{Html(targetWorld)}}" autocomplete="off" required></label>
                    <label>Region<input name="region" value="North America" required></label>
                </div>
            </div>
            <div class="section">
                <p class="section-title">Item</p>
                <div class="grid two">
                    <label>Item search<input id="acquisitionItemSearch" autocomplete="off" placeholder="Search by item name or ID"></label>
                    <label>Resolved item<input id="resolvedAcquisitionItem" readonly value="No item selected"></label>
                    <label>HQ policy<select name="hqPolicy"><option>Either</option><option>NQOnly</option><option>HQOnly</option></select></label>
                </div>
                <div id="acquisitionItemSuggestions" class="suggestions" aria-live="polite"></div>
                <div class="chips" style="margin-top: 10px;">
                    <span class="chip">Resolved before queueing</span>
                    <span class="chip">Type from market data</span>
                    <span class="chip">Stack size checked by plugin</span>
                </div>
            </div>
            <div class="section">
                <p class="section-title">Purchase Limits</p>
                <div class="grid">
                    <label>Quantity mode<select id="acquisitionQuantityMode" name="quantityMode"><option value="TargetQuantity">Target quantity</option><option value="AllBelowThreshold">All safe listings below max unit</option></select></label>
                    <label><span id="acquisitionQuantityLabelText">Target quantity</span><input id="acquisitionQuantityInput" name="quantity" inputmode="numeric" required></label>
                    <label>Max unit price<input name="maxUnitPrice" inputmode="numeric" required></label>
                    <label>Gil cap (optional)<input name="maxTotalGil" inputmode="numeric"></label>
                </div>
            </div>
            <div class="section">
                <p class="section-title">Routing</p>
                <div class="grid">
                    <label>World mode<select name="worldMode"><option>Recommended</option><option>Selected</option><option>CurrentWorldOnly</option><option>AllWorldSweep</option></select></label>
                    <label>Pickup expires<select name="expiresInSeconds"><option value="300">5 minutes</option><option value="900">15 minutes</option><option value="1800">30 minutes</option><option value="90">90 seconds</option></select></label>
                </div>
            </div>
            <div class="preview">
                <strong>Request Preview</strong>
                <div id="acquisitionQuantityModeHelp" class="preview-line">Target quantity buys safe whole listings until the requested quantity is satisfied.</div>
                <div class="preview-line">Stage a bounded purchase intent, then pick it up manually from the in-game Market Acquisition tab.</div>
                <div class="chips">
                    <span class="chip warn">Plugin pickup required</span>
                    <span class="chip">No background polling</span>
                </div>
            </div>
            <div class="button-row">
                <button type="reset">Clear</button>
                <button type="button" onclick="addAcquisitionQueueRow()">Add to Queue</button>
                <button id="acquisitionStageQueueButton" class="primary" type="button" onclick="stageAcquisitionQueue()">Stage Queue</button>
            </div>
            <div id="acquisitionStageStatus" class="stage-status" role="status" aria-live="polite"></div>
            <div class="section">
                <p class="section-title">Queued Items</p>
                <table>
                    <thead>
                        <tr><th>Item</th><th>Mode / Qty</th><th>Max Unit</th><th>Gil Cap</th><th></th></tr>
                    </thead>
                    <tbody id="acquisitionQueueRows">
                        <tr><td colspan="5" class="empty-cell">No queued items.</td></tr>
                    </tbody>
                </table>
            </div>
        </form>
        <script>
        const acquisitionQueue = [];
        let selectedAcquisitionItem = null;
        let acquisitionSearchTimer = null;

        document.getElementById('acquisitionItemSearch')?.addEventListener('input', event => {
            clearTimeout(acquisitionSearchTimer);
            acquisitionSearchTimer = setTimeout(() => searchAcquisitionItems(event.target.value), 180);
        });

        document.getElementById('acquisitionItemSuggestions')?.addEventListener('click', event => {
            const button = event.target.closest('button[data-item-id]');
            if (!button) return;
            selectAcquisitionItem(
                Number(button.dataset.itemId),
                button.dataset.itemName || '',
                button.dataset.itemType || '');
        });
        document.getElementById('acquisitionQuantityMode')?.addEventListener('change', updateAcquisitionQuantityMode);
        updateAcquisitionQuantityMode();

        async function searchAcquisitionItems(query) {
            const suggestions = document.getElementById('acquisitionItemSuggestions');
            const form = document.querySelector('.request-form');
            const baseUrl = form?.dataset.xivDataBaseUrl;
            if (!suggestions || !baseUrl) return;
            selectedAcquisitionItem = null;
            document.getElementById('selectedItemId').value = '';
            document.getElementById('selectedItemName').value = '';
            document.getElementById('resolvedAcquisitionItem').value = 'No item selected';
            const trimmed = (query || '').trim();
            if (!trimmed || (trimmed.length < 2 && !/^\d+$/.test(trimmed))) {
                suggestions.innerHTML = '';
                return;
            }
            suggestions.innerHTML = '<div class="suggestion muted">Searching...</div>';
            try {
                const endpoint = /^\d+$/.test(trimmed)
                    ? `${baseUrl}/items/${encodeURIComponent(trimmed)}`
                    : `${baseUrl}/items/search?q=${encodeURIComponent(trimmed)}&limit=12`;
                const response = await fetch(endpoint, { headers: { 'Accept': 'application/json' } });
                if (!response.ok) {
                    suggestions.innerHTML = '<div class="suggestion error">Item lookup failed.</div>';
                    return;
                }
                const payload = await response.json();
                const items = payload.items || [payload];
                suggestions.innerHTML = items.length
                    ? items.map(item => `<button type="button" class="suggestion" data-item-id="${item.itemId}" data-item-name="${escapeAttribute(item.name)}" data-item-type="${escapeAttribute(item.itemType || '')}"><strong>${escapeHtml(item.name)}</strong><span>Item ${item.itemId}${item.itemType ? ' / ' + escapeHtml(item.itemType) : ''}</span></button>`).join('')
                    : '<div class="suggestion muted">No matching items.</div>';
            } catch {
                suggestions.innerHTML = '<div class="suggestion error">XIV data gateway unavailable.</div>';
            }
        }

        function selectAcquisitionItem(itemId, name, itemType) {
            selectedAcquisitionItem = { itemId, name, itemType };
            document.getElementById('selectedItemId').value = itemId;
            document.getElementById('selectedItemName').value = name;
            document.getElementById('resolvedAcquisitionItem').value = `${name} (${itemId})`;
            document.getElementById('acquisitionItemSuggestions').innerHTML = '';
        }

        function addAcquisitionQueueRow() {
            const form = document.querySelector('.request-form');
            const data = new FormData(form);
            if (!selectedAcquisitionItem) {
                setAcquisitionStageStatus('Select a resolved item before queueing.', true);
                return;
            }
            const row = Object.fromEntries(data.entries());
            row.itemId = String(selectedAcquisitionItem.itemId);
            row.itemName = selectedAcquisitionItem.name;
            row.idempotencyKey = crypto.randomUUID ? crypto.randomUUID().replaceAll('-', '') : `${Date.now()}${Math.random()}`;
            const validationError = validateAcquisitionQueueRow(row);
            if (validationError) {
                setAcquisitionStageStatus(validationError, true);
                return;
            }
            acquisitionQueue.push(row);
            setAcquisitionStageStatus(`Queued ${row.itemName}. Stage Queue to persist the request.`, false, true);
            renderAcquisitionQueueRows();
        }

        function validateAcquisitionQueueRow(row) {
            if (row.quantityMode === 'TargetQuantity' && !isPositiveWholeNumber(row.quantity)) return 'Target quantity must be a positive whole number.';
            if (row.quantityMode === 'AllBelowThreshold' && String(row.quantity ?? '').trim() && !isWholeNumber(row.quantity)) return 'Max quantity must be blank, zero, or a positive whole number.';
            if (!isPositiveWholeNumber(row.maxUnitPrice)) return 'Max unit price must be a positive whole number.';
            if (String(row.maxTotalGil ?? '').trim() && !isWholeNumber(row.maxTotalGil)) return 'Gil cap must be blank, zero, or a positive whole number.';
            if (!row.quantityMode) return 'Quantity mode is required.';
            if (!row.hqPolicy) return 'HQ policy is required.';
            if (!row.worldMode) return 'World mode is required.';
            return '';
        }

        function updateAcquisitionQuantityMode() {
            const mode = document.getElementById('acquisitionQuantityMode')?.value || 'TargetQuantity';
            const label = document.getElementById('acquisitionQuantityLabelText');
            const input = document.getElementById('acquisitionQuantityInput');
            const help = document.getElementById('acquisitionQuantityModeHelp');
            if (label) label.textContent = mode === 'AllBelowThreshold' ? 'Max quantity (optional)' : 'Target quantity';
            if (help) {
                help.textContent = mode === 'AllBelowThreshold'
                    ? 'All safe listings below max unit buys every safe whole listing at or below the max price. Max quantity is optional; whole-stack overage is expected when stacks are larger than the remaining cap.'
                    : 'Target quantity buys safe whole listings until the requested quantity is satisfied. Whole-stack overage can happen on the final listing.';
            }
            if (!input) return;
            input.required = mode !== 'AllBelowThreshold';
            input.placeholder = mode === 'AllBelowThreshold' ? 'No cap' : '';
        }

        function isPositiveWholeNumber(value) {
            return /^[1-9]\d*$/.test(String(value ?? '').trim());
        }

        function isWholeNumber(value) {
            return /^\d+$/.test(String(value ?? '').trim());
        }

        function renderAcquisitionQueueRows() {
            const body = document.getElementById('acquisitionQueueRows');
            if (!body) return;
            body.innerHTML = acquisitionQueue.length
                ? acquisitionQueue.map((row, index) => `<tr><td>${escapeHtml(row.itemName)}<br><span>Item ${row.itemId}</span></td><td>${formatQuantityMode(row)}<br><span>${formatOptionalQuantity(row)}</span></td><td>${escapeHtml(row.maxUnitPrice)}</td><td>${formatOptionalGilCap(row.maxTotalGil)}</td><td><button type="button" onclick="removeAcquisitionQueueRow(${index})">Remove</button></td></tr>`).join('')
                : '<tr><td colspan="5" class="empty-cell">No queued items.</td></tr>';
        }

        function formatQuantityMode(row) {
            return row.quantityMode === 'AllBelowThreshold'
                ? 'All safe below max'
                : 'Target';
        }

        function formatOptionalQuantity(row) {
            const trimmed = String(row.quantity ?? '').trim();
            if (row.quantityMode === 'AllBelowThreshold' && (!trimmed || trimmed === '0')) return 'No cap';
            return escapeHtml(trimmed);
        }

        function formatOptionalGilCap(value) {
            const trimmed = String(value ?? '').trim();
            return trimmed && trimmed !== '0' ? escapeHtml(trimmed) : 'No cap';
        }

        function removeAcquisitionQueueRow(index) {
            acquisitionQueue.splice(index, 1);
            renderAcquisitionQueueRows();
        }

        async function stageAcquisitionQueue() {
            const form = document.querySelector('.request-form');
            if (!form || acquisitionQueue.length === 0) {
                setAcquisitionStageStatus('Queue at least one resolved item first.', true);
                return;
            }
            const startedAt = performance.now();
            const stageButton = document.getElementById('acquisitionStageQueueButton');
            isStagingAcquisitionQueue = true;
            stopAcquisitionQueueRefresh();
            if (stageButton) stageButton.disabled = true;
            setAcquisitionStageStatus(`Staging 0 of ${acquisitionQueue.length} acquisition rows...`, false);
            console.info('[MarketMafioso] Staging acquisition queue', { rows: acquisitionQueue.length });

            let results;
            try {
                results = await stageAcquisitionRowsInBatches(form, acquisitionQueue, 4, (completed, total, row, result) => {
                    const status = result.ok ? 'staged' : 'failed';
                    setAcquisitionStageStatus(`Staging ${completed} of ${total} acquisition rows... ${row.itemName} ${status}.`, !result.ok, result.ok);
                    console.debug('[MarketMafioso] Staged acquisition row', {
                        itemName: row.itemName,
                        itemId: row.itemId,
                        ok: result.ok,
                        status: result.status,
                        elapsedMs: Math.round(result.elapsedMs)
                    });
                });
            } finally {
                isStagingAcquisitionQueue = false;
                if (stageButton) stageButton.disabled = false;
                startAcquisitionQueueRefresh();
            }

            const staged = results.filter(result => result.ok).length;
            const failures = results
                .filter(result => !result.ok)
                .map(result => `${result.row.itemName}: ${result.error}`);
            console.info('[MarketMafioso] Acquisition queue staging finished', {
                staged,
                total: acquisitionQueue.length,
                failed: failures.length,
                elapsedMs: Math.round(performance.now() - startedAt)
            });

            if (staged === acquisitionQueue.length) {
                window.location.href = '{{Html(AppUrl(pathBase, "/acquisition"))}}';
            } else {
                setAcquisitionStageStatus(`${staged} of ${acquisitionQueue.length} acquisition rows staged. ${failures.join(' ')}`, true);
            }
        }

        async function stageAcquisitionRowsInBatches(form, rows, batchSize, onSettled) {
            const results = new Array(rows.length);
            let nextIndex = 0;
            let completed = 0;
            const workerCount = Math.min(batchSize, rows.length);

            async function worker() {
                while (nextIndex < rows.length) {
                    const index = nextIndex++;
                    const row = rows[index];
                    const result = await stageAcquisitionRow(form, row);
                    results[index] = result;
                    completed++;
                    onSettled(completed, rows.length, row, result);
                }
            }

            await Promise.all(Array.from({ length: workerCount }, () => worker()));
            return results;
        }

        async function stageAcquisitionRow(form, row) {
            const result = await postAcquisitionQueueRow(form, row);
            return { ...result, row };
        }

        async function postAcquisitionQueueRow(form, row) {
            const startedAt = performance.now();
            try {
                const response = await fetch(form.action, {
                    method: 'POST',
                    body: new URLSearchParams(row),
                    headers: {
                        'Accept': 'application/json'
                    },
                    credentials: 'same-origin'
                });
                if (response.ok) {
                    return { ok: true, status: response.status, elapsedMs: performance.now() - startedAt };
                }

                return {
                    ok: false,
                    status: response.status,
                    error: await readAcquisitionStageError(response),
                    elapsedMs: performance.now() - startedAt
                };
            } catch (error) {
                console.error('[MarketMafioso] Acquisition row staging request failed.', {
                    itemName: row.itemName,
                    itemId: row.itemId,
                    error
                });
                return {
                    ok: false,
                    status: 0,
                    error: error instanceof Error ? error.message : 'network_error',
                    elapsedMs: performance.now() - startedAt
                };
            }
        }

        async function readAcquisitionStageError(response) {
            try {
                const payload = await response.json();
                return payload.error || `HTTP ${response.status}`;
            } catch {
                try {
                    const text = await response.text();
                    return text || `HTTP ${response.status}`;
                } catch {
                    return `HTTP ${response.status}`;
                }
            }
        }

        function setAcquisitionStageStatus(message, isError, isGood = false) {
            const status = document.getElementById('acquisitionStageStatus');
            if (!status) return;
            status.textContent = message || '';
            status.classList.toggle('error', Boolean(isError));
            status.classList.toggle('good', Boolean(isGood) && !isError);
        }

        function escapeHtml(value) {
            return String(value ?? '').replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));
        }

        function escapeAttribute(value) {
            return escapeHtml(value).replace(/`/g, '&#96;');
        }
        </script>
        """;

static string RenderAcquisitionQueueRows(
    IReadOnlyList<MarketAcquisitionRequestView> requests,
    PathString pathBase)
{
    if (requests.Count == 0)
        return """<tr><td colspan="8" class="empty-cell">No acquisition requests yet.</td></tr>""";

    return string.Join(Environment.NewLine, requests.Select(request =>
    {
        var actions = RenderAcquisitionRequestActions(request, pathBase);
        var latestEvent = RenderAcquisitionLatestEvent(request);
        return
        $$"""
        <tr data-status="{{Html(FormatAcquisitionStatus(request.Status))}}">
            <td data-resize-col="item"><div class="item"><strong>{{Html(string.IsNullOrWhiteSpace(request.ItemName) ? $"Item {request.ItemId}" : request.ItemName)}}</strong><span>item {{request.ItemId}} / {{Html(request.HqPolicy)}}</span></div></td>
            <td class="number" data-resize-col="qty">{{request.Quantity:N0}}</td>
            <td class="number" data-resize-col="max-unit">{{FormatGil(request.MaxUnitPrice)}}</td>
            <td data-resize-col="routing">{{Html(FormatWorldMode(request.WorldMode))}}</td>
            <td data-resize-col="target">{{Html(request.TargetCharacterName)}} @ {{Html(request.TargetWorld)}}</td>
            <td data-resize-col="status"><div class="status-stack"><span class="status {{Html(AcquisitionStatusClass(request.Status))}}">{{Html(FormatAcquisitionStatus(request.Status))}}</span>{{latestEvent}}</div></td>
            <td data-resize-col="age">{{Html(FormatAcquisitionAge(request.CreatedAtUtc))}}</td>
            <td class="actions" data-resize-col="actions">{{actions}}</td>
        </tr>
        """;
    }));
}

static object BuildAcquisitionQueueUpdate(
    IReadOnlyList<MarketAcquisitionRequestView> acquisitionRequests,
    PathString pathBase)
{
    var visibleRequests = VisibleAcquisitionQueueRequests(acquisitionRequests);
    var activeCount = visibleRequests.Count(request =>
        request.Status is MarketAcquisitionStatuses.PendingPickup
            or MarketAcquisitionStatuses.Claimed
            or MarketAcquisitionStatuses.AcceptedInPlugin
            or MarketAcquisitionStatuses.Running);
    var latestRequest = visibleRequests.FirstOrDefault();

    return new
    {
        queueRows = RenderAcquisitionQueueRows(visibleRequests, pathBase),
        activeSummary = $"{activeCount:N0} active / {visibleRequests.Count:N0} recent",
        selectedRequestName = latestRequest == null ? "None selected" : FormatAcquisitionItem(latestRequest),
        latestRequestStatus = latestRequest == null ? "Idle" : FormatAcquisitionStatus(latestRequest.Status),
        latestRequestEvent = latestRequest == null ? "-" : FormatAcquisitionLatestEvent(latestRequest),
        latestRequestExpiry = latestRequest == null ? "-" : FormatAcquisitionExpiry(latestRequest),
    };
}

static IReadOnlyList<MarketAcquisitionRequestView> VisibleAcquisitionQueueRequests(
    IReadOnlyList<MarketAcquisitionRequestView> requests)
{
    return requests
        .Where(request => request.Status != MarketAcquisitionStatuses.Cancelled)
        .ToList();
}

static string RenderAcquisitionRequestActions(
    MarketAcquisitionRequestView request,
    PathString pathBase)
{
    var canCancel = request.Status is MarketAcquisitionStatuses.PendingPickup
        or MarketAcquisitionStatuses.Claimed
        or MarketAcquisitionStatuses.AcceptedInPlugin
        or MarketAcquisitionStatuses.Running
        or MarketAcquisitionStatuses.Expired
        or MarketAcquisitionStatuses.Rejected;
    var canResend = request.Status is MarketAcquisitionStatuses.PendingPickup
        or MarketAcquisitionStatuses.Claimed
        or MarketAcquisitionStatuses.AcceptedInPlugin
        or MarketAcquisitionStatuses.Expired
        or MarketAcquisitionStatuses.Rejected
        or MarketAcquisitionStatuses.Failed
        or MarketAcquisitionStatuses.Cancelled;

    var buttons = new StringBuilder();
    if (canCancel)
    {
        buttons.Append($$"""
            <form method="post" action="{{Html(AppUrl(pathBase, $"/acquisition/requests/{Uri.EscapeDataString(request.Id)}/cancel"))}}" onsubmit="return confirm('Cancel this acquisition request?');">
                <button type="submit">Cancel</button>
            </form>
            """);
    }

    if (canResend)
    {
        buttons.Append($$"""
            <form method="post" action="{{Html(AppUrl(pathBase, $"/acquisition/requests/{Uri.EscapeDataString(request.Id)}/resend"))}}">
                <button type="submit">Resend</button>
            </form>
            """);
    }

    return buttons.Length == 0
        ? """<span class="muted">-</span>"""
        : buttons.ToString();
}

static string FormatAcquisitionItem(MarketAcquisitionRequestView request)
{
    var name = string.IsNullOrWhiteSpace(request.ItemName)
        ? $"Item {request.ItemId}"
        : request.ItemName;
    return $"{name} ({request.ItemId})";
}

static string FormatAcquisitionStatus(string status) =>
    status switch
    {
        MarketAcquisitionStatuses.PendingPickup => "Pending",
        MarketAcquisitionStatuses.AcceptedInPlugin => "Accepted",
        MarketAcquisitionStatuses.Cancelled => "Cancelled",
        _ => status,
    };

static string RenderAcquisitionLatestEvent(MarketAcquisitionRequestView request)
{
    var latestEvent = FormatAcquisitionLatestEvent(request);
    return latestEvent == "-"
        ? string.Empty
        : $"""<span class="lifecycle-note" title="{Html(latestEvent)}">{Html(latestEvent)}</span>""";
}

static string FormatAcquisitionLatestEvent(MarketAcquisitionRequestView request)
{
    var message = request.LatestMessage ?? request.LatestReason ?? request.LatestRunnerState;
    if (string.IsNullOrWhiteSpace(message))
        return "-";

    var prefix = request.LatestRunnerState ?? request.LatestEventType;
    return string.IsNullOrWhiteSpace(prefix)
        ? message
        : $"{prefix}: {message}";
}

static string AcquisitionStatusClass(string status) =>
    status switch
    {
        MarketAcquisitionStatuses.PendingPickup => "pending",
        MarketAcquisitionStatuses.Claimed => "claimed",
        MarketAcquisitionStatuses.AcceptedInPlugin => "accepted",
        MarketAcquisitionStatuses.Running => "running",
        MarketAcquisitionStatuses.Complete => "complete",
        MarketAcquisitionStatuses.Failed => "failed",
        MarketAcquisitionStatuses.Rejected => "failed",
        MarketAcquisitionStatuses.Expired => "failed",
        MarketAcquisitionStatuses.Cancelled => "failed",
        _ => string.Empty,
    };

static string FormatWorldMode(string worldMode) =>
    worldMode switch
    {
        "AllWorldSweep" => "All-world sweep",
        "CurrentWorldOnly" => "Current world only",
        _ => worldMode,
    };

static string FormatGil(uint gil) => $"{gil:N0}";

static string FormatAcquisitionAge(DateTimeOffset createdAtUtc)
{
    var age = DateTimeOffset.UtcNow - createdAtUtc.ToUniversalTime();
    if (age.TotalSeconds < 60)
        return "now";
    if (age.TotalMinutes < 60)
        return $"{(int)age.TotalMinutes}m";
    if (age.TotalHours < 24)
        return $"{(int)age.TotalHours}h";

    return $"{(int)age.TotalDays}d";
}

static string FormatAcquisitionExpiry(MarketAcquisitionRequestView request)
{
    var expiresIn = request.ExpiresAtUtc.ToUniversalTime() - DateTimeOffset.UtcNow;
    if (expiresIn <= TimeSpan.Zero)
        return "expired";
    if (expiresIn.TotalMinutes < 1)
        return "<1m";
    if (expiresIn.TotalHours < 1)
        return $"{Math.Ceiling(expiresIn.TotalMinutes):N0}m";

    return $"{Math.Ceiling(expiresIn.TotalHours):N0}h";
}

static string RenderCharacterFilters(
    IReadOnlyList<CharacterSummary> characters,
    long? selectedCharacterId,
    bool allCharacters,
    PathString pathBase)
{
    if (characters.Count == 0)
        return string.Empty;

    var latestClass = !allCharacters && selectedCharacterId == characters[0].Id ? "filter active" : "filter";
    var allClass = allCharacters ? "filter active" : "filter";
    var links = new StringBuilder();
    links.AppendLine($"""<a class="{latestClass}" href="{Html(AppUrl(pathBase, "/"))}">Latest Character</a>""");
    links.AppendLine($"""<a class="{allClass}" href="{Html(AppUrl(pathBase, "/?allCharacters=true"))}">All Characters</a>""");

    foreach (var character in characters)
    {
        var activeClass = !allCharacters && selectedCharacterId == character.Id ? "filter active" : "filter";
        links.AppendLine($"""<a class="{activeClass}" href="{Html(AppUrl(pathBase, $"/?characterId={character.Id}"))}">{Html(CharacterLabel(character))}</a>""");
    }

    return $$"""
        <nav class="filters" aria-label="Character filters">
            {{links}}
        </nav>
        """;
}

static string CharacterLabel(CharacterSummary character) =>
    string.IsNullOrWhiteSpace(character.HomeWorld)
        ? character.CharacterName
        : $"{character.CharacterName} @ {character.HomeWorld}";

static string RenderInventoryBrowser(
    InventoryBrowserView view,
    IReadOnlyList<CharacterSummary> characters,
    long? selectedCharacterId,
    PathString pathBase)
{
    var characterOptions = RenderCharacterOptions(characters, selectedCharacterId);
    var rows = view.Items.Count == 0
        ? """<tr><td colspan="6" class="empty-cell">No matching items in the selected latest snapshot.</td></tr>"""
        : string.Join(Environment.NewLine, view.Items.Select(RenderInventoryBrowserItem));
    var scopeRows = RenderInventoryScopeList(view, selectedCharacterId, pathBase);
    var marketListings = RenderInventoryBrowserMarketListings(view);
    var characterTitle = string.IsNullOrWhiteSpace(view.CharacterName)
        ? "No character selected"
        : $"{view.CharacterName} @ {view.HomeWorld ?? "-"}";
    var received = view.ReceivedAt == null
        ? "No snapshots yet"
        : view.ReceivedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");

    return $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>MarketMafioso Inventory Browser</title>
            <style>
                :root {
                    color-scheme: dark;
                    --bg: #101317;
                    --panel: #171c22;
                    --panel-2: #1d242c;
                    --line: #2c3642;
                    --line-soft: #242c36;
                    --text: #e7edf3;
                    --muted: #95a3b3;
                    --subtle: #687789;
                    --accent: #62b6ff;
                    --accent-2: #7ddc9a;
                    --chip: #25303b;
                    --row: #141a20;
                    --row-alt: #11171d;
                    font-family: "Segoe UI", system-ui, sans-serif;
                }
                * { box-sizing: border-box; }
                body { margin: 0; min-height: 100vh; background: var(--bg); color: var(--text); font-size: 14px; }
                .shell { min-height: 100vh; display: grid; grid-template-rows: auto auto 1fr auto; }
                .topbar {
                    display: flex;
                    align-items: center;
                    justify-content: space-between;
                    gap: 18px;
                    min-height: 54px;
                    padding: 0 22px;
                    border-bottom: 1px solid var(--line);
                    background: #131820;
                }
                .brand { display: flex; align-items: baseline; gap: 12px; min-width: 0; }
                .brand strong { font-size: 16px; font-weight: 650; }
                .brand span { color: var(--muted); white-space: nowrap; }
                .tabs { display: flex; gap: 4px; }
                .tab {
                    display: inline-flex;
                    align-items: center;
                    height: 34px;
                    padding: 0 11px;
                    border: 1px solid transparent;
                    border-radius: 6px;
                    color: var(--muted);
                    text-decoration: none;
                }
                .tab.active { color: var(--text); border-color: var(--line); background: var(--panel-2); }
                .toolbar {
                    display: grid;
                    grid-template-columns: minmax(260px, 1fr) auto auto;
                    gap: 10px;
                    align-items: center;
                    padding: 14px 22px;
                    border-bottom: 1px solid var(--line);
                    background: var(--panel);
                }
                input, select, button {
                    height: 34px;
                    border: 1px solid var(--line);
                    border-radius: 6px;
                    background: #0f141a;
                    color: var(--text);
                    font: inherit;
                }
                input { width: 100%; padding: 0 12px; }
                select { min-width: 220px; padding: 0 10px; }
                button { padding: 0 12px; background: var(--panel-2); }
                .content { display: grid; grid-template-columns: 260px minmax(0, 1fr); min-height: 0; }
                .sidebar { border-right: 1px solid var(--line); background: #12171d; padding: 14px; overflow: auto; }
                .section-title {
                    margin: 4px 0 8px;
                    color: var(--muted);
                    font-size: 12px;
                    font-weight: 650;
                    text-transform: uppercase;
                    letter-spacing: .04em;
                }
                .list-item {
                    display: grid;
                    gap: 2px;
                    padding: 9px 10px;
                    border: 1px solid transparent;
                    border-radius: 6px;
                    background: transparent;
                    color: var(--text);
                    text-decoration: none;
                }
                .list-item.active { border-color: #315270; background: #182838; }
                .list-item strong { font-size: 13px; font-weight: 600; }
                .list-item span { color: var(--muted); font-size: 12px; }
                .main { min-width: 0; padding: 16px 20px 22px; overflow: auto; }
                .summary { display: grid; grid-template-columns: repeat(4, minmax(120px, 1fr)); gap: 10px; margin-bottom: 14px; }
                .metric { border: 1px solid var(--line); border-radius: 8px; background: var(--panel); padding: 10px 12px; }
                .metric span { display: block; color: var(--muted); font-size: 12px; margin-bottom: 5px; }
                .metric strong { font-size: 18px; font-weight: 650; }
                .table-wrap { border: 1px solid var(--line); border-radius: 8px; overflow: auto; background: var(--panel); }
                table { width: 100%; border-collapse: separate; border-spacing: 0; table-layout: fixed; }
                th, td { padding: 8px 10px; border-bottom: 1px solid var(--line-soft); border-right: 1px solid var(--line); vertical-align: middle; }
                th:last-child, td:last-child { border-right: 0; }
                th {
                    padding: 0;
                    height: 34px;
                    color: var(--muted);
                    background: #202832;
                    text-align: left;
                    font-size: 12px;
                    font-weight: 650;
                    text-transform: uppercase;
                    letter-spacing: .03em;
                }
                td[data-resize-col] { position: relative; }
                td[data-resize-col].separator-hover { cursor: col-resize; }
                td[data-resize-col].separator-hover::after {
                    content: "";
                    position: absolute;
                    top: 0;
                    right: -1px;
                    bottom: 0;
                    width: 3px;
                    background: var(--accent);
                    pointer-events: none;
                }
                .th-inner {
                    display: grid;
                    grid-template-columns: minmax(0, 1fr) 28px 6px;
                    align-items: stretch;
                    height: 34px;
                }
                .th-label { display: flex; align-items: center; min-width: 0; padding: 0 9px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; cursor: pointer; }
                .filter-button { width: 28px; border: 0; border-left: 1px solid var(--line); background: transparent; color: var(--muted); cursor: pointer; }
                .filter-button:hover { color: var(--accent); background: #2a3541; }
                .resizer { cursor: col-resize; }
                .resizer:hover { background: var(--accent); }
                .icon-column, .icon-cell { display: none; }
                tr.item-row:nth-child(even) td { background: var(--row-alt); }
                tr.item-row:nth-child(odd) td { background: var(--row); }
                .item-name { display: flex; align-items: baseline; gap: 8px; min-width: 0; }
                .item-name strong { font-weight: 620; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .item-name span { color: var(--subtle); font-size: 12px; }
                .number { text-align: right; font-variant-numeric: tabular-nums; }
                .where { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
                .chip {
                    display: inline-flex;
                    align-items: center;
                    min-height: 24px;
                    padding: 2px 8px;
                    border-radius: 999px;
                    background: var(--chip);
                    color: var(--muted);
                    font-size: 12px;
                    white-space: nowrap;
                }
                .small-button {
                    display: inline-flex;
                    align-items: center;
                    height: 26px;
                    padding: 0 9px;
                    border: 1px solid var(--line);
                    border-radius: 5px;
                    background: var(--panel-2);
                    color: var(--text);
                    text-decoration: none;
                    font-size: 12px;
                }
                .detail-row td { padding: 0; background: #0f151c; }
                .locations { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px; padding: 10px 12px 12px 10px; }
                .location { min-width: 0; border: 1px solid var(--line-soft); border-radius: 6px; padding: 8px 9px; background: #141b23; }
                .location strong { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 13px; }
                .location span { color: var(--muted); font-size: 12px; }
                .age { color: var(--accent); }
                .market-panel { margin-top: 12px; border: 1px solid var(--line); border-radius: 8px; overflow: hidden; background: var(--panel); }
                .panel-head { display: flex; justify-content: space-between; gap: 12px; padding: 10px 12px; border-bottom: 1px solid var(--line); background: #202832; }
                .market-list { display: grid; gap: 0; padding: 10px; }
                .listing { display: grid; grid-template-columns: minmax(220px, 1fr) 120px 100px 110px 130px; gap: 10px; align-items: center; padding: 8px 10px; border-bottom: 1px solid var(--line); background: #131a22; }
                .listing:last-child { border-bottom: 0; }
                .listing strong, .listing span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .listing span { color: var(--muted); font-size: 12px; }
                .price { color: var(--accent); text-align: right; font-variant-numeric: tabular-nums; }
                .empty-cell { color: var(--muted); text-align: center; }
                .statusbar {
                    display: flex;
                    justify-content: space-between;
                    gap: 12px;
                    padding: 8px 22px;
                    border-top: 1px solid var(--line);
                    background: #11161c;
                    color: var(--muted);
                    font-size: 12px;
                }
                @media (max-width: 980px) {
                    .toolbar { grid-template-columns: 1fr; }
                    .content { grid-template-columns: 1fr; }
                    .sidebar { display: none; }
                    .summary, .locations { grid-template-columns: 1fr 1fr; }
                }
            </style>
        </head>
        <body>
            <div class="shell">
                <header class="topbar">
                    <div class="brand">
                        <strong>MarketMafioso</strong>
                        <span>Inventory Browser</span>
                    </div>
                    <nav class="tabs" aria-label="Dashboard sections">
                        <a class="tab" href="{{Html(AppUrl(pathBase, "/"))}}">Snapshots</a>
                        <a class="tab active" href="{{Html(AppUrl(pathBase, "/inventory"))}}">Inventory</a>
                        <a class="tab" href="{{Html(AppUrl(pathBase, "/acquisition"))}}">Acquisition</a>
                        <a class="tab" href="{{Html(AppUrl(pathBase, "/diagnostics"))}}">Diagnostics</a>
                    </nav>
                </header>
                <form id="inventorySearchForm" class="toolbar" method="get" action="{{Html(AppUrl(pathBase, "/inventory"))}}">
                    <input id="inventorySearch" name="search" value="{{Html(view.Search)}}" aria-label="Search by item name or id" placeholder="Search by item name or id">
                    <input type="hidden" name="scope" value="{{Html(view.Scope)}}">
                    <select name="characterId" aria-label="Character">{{characterOptions}}</select>
                    <button type="submit">Search</button>
                </form>
                <main class="content">
                    <aside class="sidebar">
                        <div class="section-title">Snapshot</div>
                        <div class="list-item active">
                            <strong>Latest Snapshot</strong>
                            <span>{{Html(received)}}</span>
                        </div>
                        <div class="section-title" style="margin-top: 18px;">Scope</div>
                        <div class="list-item active">
                            <strong>{{Html(characterTitle)}}</strong>
                            <span>{{view.Items.Count:N0}} matching item rows</span>
                        </div>
                        {{scopeRows}}
                    </aside>
                    <section class="main">
                        <div class="summary">
                            <div class="metric"><span>Matching Items</span><strong>{{view.Items.Count:N0}}</strong></div>
                            <div class="metric"><span>Total Quantity</span><strong>{{view.TotalQuantity:N0}}</strong></div>
                            <div class="metric"><span>HQ Quantity</span><strong>{{view.HqQuantity:N0}}</strong></div>
                            <div class="metric"><span>Owners Matched</span><strong>{{view.OwnerCount:N0}}</strong></div>
                        </div>
                        <div class="table-wrap">
                            <table>
                                <colgroup>
                                    <col class="icon-column" style="width:48px">
                                    <col data-col="item" style="width:34%">
                                    <col data-col="type" style="width:14%">
                                    <col data-col="total" style="width:11%">
                                    <col data-col="hq" style="width:9%">
                                    <col data-col="where" style="width:32%">
                                </colgroup>
                                <thead>
                                    <tr>
                                        <th class="icon-column"><div class="th-inner"><span class="th-label">Icon</span><button class="filter-button" type="button" data-filter="icon">v</button><span class="resizer" data-resize="icon"></span></div></th>
                                        <th><div class="th-inner"><span class="th-label" data-sort="item">Item</span><button class="filter-button" type="button" data-filter="item">v</button><span class="resizer" data-resize="item"></span></div></th>
                                        <th><div class="th-inner"><span class="th-label" data-sort="type">Type</span><button class="filter-button" type="button" data-filter="type">v</button><span class="resizer" data-resize="type"></span></div></th>
                                        <th class="number"><div class="th-inner"><span class="th-label" data-sort="total">Total</span><button class="filter-button" type="button" data-filter="total">v</button><span class="resizer" data-resize="total"></span></div></th>
                                        <th class="number"><div class="th-inner"><span class="th-label" data-sort="hq">HQ</span><button class="filter-button" type="button" data-filter="hq">v</button><span class="resizer" data-resize="hq"></span></div></th>
                                        <th><div class="th-inner"><span class="th-label" data-sort="where">Where</span><button class="filter-button" type="button" data-filter="where">v</button><span class="resizer" data-resize="where"></span></div></th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {{rows}}
                                </tbody>
                            </table>
                        </div>
                        <section class="market-panel">
                            <div class="panel-head">
                                <strong>Retainer Market Listings</strong>
                                <span>Displayed separately from regular inventory totals</span>
                            </div>
                            <div class="market-list">{{marketListings}}</div>
                        </section>
                    </section>
                </main>
                <footer class="statusbar">
                    <span>Structured retention: 500 snapshots by default.</span>
                    <span>Item icons are reserved for a later server-side metadata cache.</span>
                </footer>
            </div>
            <script>
                const form = document.getElementById('inventorySearchForm');
                const search = document.getElementById('inventorySearch');
                let searchTimer;
                search.addEventListener('input', () => {
                    window.clearTimeout(searchTimer);
                    searchTimer = window.setTimeout(() => form.requestSubmit(), 250);
                });

                const resizeState = { active: false, current: null, neighbor: null, startX: 0, tableWidth: 0, currentStart: 0, neighborStart: 0, direction: 1 };
                function getColumnPercent(col) { return Number.parseFloat(col.style.width || '0') || 0; }
                function findResizePair(columnKey) {
                    const cols = [...document.querySelectorAll('col[data-col]')];
                    const index = cols.findIndex(col => col.dataset.col === columnKey);
                    if (index < 0) return null;
                    if (index < cols.length - 1) return { current: cols[index], neighbor: cols[index + 1], direction: 1 };
                    if (index > 0) return { current: cols[index], neighbor: cols[index - 1], direction: -1 };
                    return null;
                }
                function beginProportionalResize(columnKey, event) {
                    const pair = findResizePair(columnKey);
                    if (!pair) return false;
                    resizeState.active = true;
                    resizeState.current = pair.current;
                    resizeState.neighbor = pair.neighbor;
                    resizeState.direction = pair.direction;
                    resizeState.startX = event.clientX;
                    resizeState.tableWidth = document.querySelector('table').getBoundingClientRect().width;
                    resizeState.currentStart = getColumnPercent(pair.current);
                    resizeState.neighborStart = getColumnPercent(pair.neighbor);
                    document.body.style.cursor = 'col-resize';
                    event.preventDefault();
                    event.stopPropagation();
                    return true;
                }
                function isNearRightSeparator(cell, event) {
                    const rect = cell.getBoundingClientRect();
                    return rect.right - event.clientX <= 7;
                }
                document.querySelectorAll('.resizer').forEach(handle => {
                    handle.addEventListener('mousedown', event => beginProportionalResize(handle.dataset.resize, event));
                });
                document.querySelectorAll('td[data-resize-col]').forEach(cell => {
                    cell.addEventListener('mousemove', event => cell.classList.toggle('separator-hover', isNearRightSeparator(cell, event)));
                    cell.addEventListener('mouseleave', () => cell.classList.remove('separator-hover'));
                    cell.addEventListener('mousedown', event => {
                        if (isNearRightSeparator(cell, event)) beginProportionalResize(cell.dataset.resizeCol, event);
                    });
                });
                window.addEventListener('mousemove', event => {
                    if (!resizeState.active) return;
                    const minPercent = 6;
                    const deltaPercent = ((event.clientX - resizeState.startX) / resizeState.tableWidth) * 100 * resizeState.direction;
                    const combined = resizeState.currentStart + resizeState.neighborStart;
                    const current = Math.min(combined - minPercent, Math.max(minPercent, resizeState.currentStart + deltaPercent));
                    resizeState.current.style.width = `${current.toFixed(2)}%`;
                    resizeState.neighbor.style.width = `${(combined - current).toFixed(2)}%`;
                });
                window.addEventListener('mouseup', () => {
                    resizeState.active = false;
                    resizeState.current = null;
                    resizeState.neighbor = null;
                    document.body.style.cursor = '';
                    document.querySelectorAll('.separator-hover').forEach(cell => cell.classList.remove('separator-hover'));
                });
            </script>
        </body>
        </html>
        """;
}

static string RenderCharacterOptions(IReadOnlyList<CharacterSummary> characters, long? selectedCharacterId)
{
    if (characters.Count == 0)
        return """<option value="">No characters</option>""";

    var options = new StringBuilder();
    foreach (var character in characters)
    {
        var selected = selectedCharacterId == character.Id ? " selected" : string.Empty;
        options.AppendLine($"""<option value="{character.Id}"{selected}>{Html(CharacterLabel(character))}</option>""");
    }

    return options.ToString();
}

static string RenderInventoryScopeList(InventoryBrowserView view, long? selectedCharacterId, PathString pathBase)
{
    if (view.Scopes.Count == 0)
        return string.Empty;

    var rows = new StringBuilder();
    rows.AppendLine("""<div class="section-title" style="margin-top: 18px;">Inventories</div>""");
    var hasRenderedRetainerSection = false;

    foreach (var scope in view.Scopes)
    {
        if (!scope.ScopeKey.Equals("Player Inventory", StringComparison.OrdinalIgnoreCase) &&
            !hasRenderedRetainerSection)
        {
            rows.AppendLine("""<div class="section-title" style="margin-top: 18px;">Retainers</div>""");
            hasRenderedRetainerSection = true;
        }

        var activeClass = view.Scope.Equals(scope.ScopeKey, StringComparison.OrdinalIgnoreCase)
            ? "list-item active"
            : "list-item";
        var url = AppUrl(pathBase, $"/inventory?characterId={selectedCharacterId}&scope={Uri.EscapeDataString(scope.ScopeKey)}&search={Uri.EscapeDataString(view.Search)}");
        var age = FormatAge(scope.LastUpdated);
        var detail = scope.ScopeKey.Equals("Player Inventory", StringComparison.OrdinalIgnoreCase)
            ? $"{scope.StackCount:N0} stacks"
            : $"{scope.StackCount:N0} stacks / {scope.Gil:N0} gil / {scope.MarketListingCount:N0} listings";
        rows.AppendLine($$"""
            <a class="{{activeClass}}" href="{{Html(url)}}">
                <strong>{{Html(scope.DisplayName)}}</strong>
                <span>{{Html(detail)}}</span>
                <span class="age">{{Html(age)}}</span>
            </a>
            """);
    }

    return rows.ToString();
}

static string RenderInventoryBrowserItem(InventoryBrowserItemView item)
{
    var ownerText = item.OwnerCount == 1 ? "1 owner" : $"{item.OwnerCount:N0} owners";
    var locations = string.Join(Environment.NewLine, item.Locations.Select(RenderInventoryBrowserLocation));

    return $$"""
        <tr class="item-row">
            <td class="icon-cell" data-resize-col="icon"></td>
            <td data-resize-col="item">
                <div class="item-name">
                    <strong>{{Html(item.DisplayName)}}</strong>
                    <span>Item {{item.ItemId}}</span>
                </div>
            </td>
            <td data-resize-col="type">{{Html(string.IsNullOrWhiteSpace(item.ItemType) ? "Unknown" : item.ItemType)}}</td>
            <td class="number" data-resize-col="total">{{item.TotalQuantity:N0}}</td>
            <td class="number" data-resize-col="hq">{{item.HqQuantity:N0}}</td>
            <td data-resize-col="where">
                <div class="where">
                    <span class="chip">{{Html(ownerText)}}</span>
                    <span class="small-button">View</span>
                </div>
            </td>
        </tr>
        <tr class="detail-row">
            <td colspan="5">
                <div class="locations">
                    {{locations}}
                </div>
            </td>
        </tr>
        """;
}

static string RenderInventoryBrowserLocation(InventoryBrowserLocationView location)
{
    var quantity = location.HqQuantity > 0
        ? $"{location.Quantity:N0} ({location.HqQuantity:N0} HQ)"
        : $"{location.Quantity:N0}";

    return $$"""
        <div class="location">
            <strong>{{Html(location.OwnerName)}} / {{Html(location.BagName)}}: {{Html(quantity)}}</strong>
        </div>
        """;
}

static string RenderInventoryBrowserMarketListings(InventoryBrowserView view)
{
    if (view.MarketListings.Count == 0)
        return """<div class="empty-cell">No market listings for this scope.</div>""";

    return string.Join(Environment.NewLine, view.MarketListings.Select(listing =>
    {
        var unitPrice = listing.UnitPrice == null
            ? "-"
            : $"{listing.UnitPrice.Value:N0} gil each";
        var totalPrice = listing.UnitPrice == null
            ? "-"
            : $"{checked((long)listing.UnitPrice.Value * listing.Quantity):N0} gil total";
        var age = FormatAge(listing.ListedAt);

        return $$"""
            <div class="listing">
                <strong>{{Html(listing.DisplayName)}}</strong>
                <span>{{Html(listing.OwnerName)}}</span>
                <span>{{listing.Quantity:N0}} listed{{(listing.HqQuantity > 0 ? $" / {listing.HqQuantity:N0} HQ" : string.Empty)}}</span>
                <span>{{Html(string.IsNullOrWhiteSpace(listing.ItemType) ? "Unknown" : listing.ItemType)}} / Item {{listing.ItemId}}</span>
                <span class="price">{{Html(unitPrice)}}</span>
                <span>{{Html(totalPrice)}}</span>
                <span class="age">{{Html(age)}}</span>
            </div>
            """;
    }));
}

static string FormatAge(string? timestamp)
{
    if (string.IsNullOrWhiteSpace(timestamp) ||
        !DateTimeOffset.TryParse(timestamp, out var parsed))
    {
        return "unknown age";
    }

    var age = DateTimeOffset.UtcNow - parsed.ToUniversalTime();
    if (age.TotalMinutes < 1)
        return "just now";
    if (age.TotalHours < 1)
        return $"{Math.Max(1, (int)age.TotalMinutes):N0}m old";
    if (age.TotalDays < 1)
        return $"{Math.Max(1, (int)age.TotalHours):N0}h old";

    return $"{Math.Max(1, (int)age.TotalDays):N0}d old";
}

static string RenderDiagnostics(IReadOnlyList<ReportSummary> reports, PathString pathBase)
{
    var rows = reports.Count == 0
        ? """<tr><td colspan="4">No snapshots found.</td></tr>"""
        : string.Join(Environment.NewLine, reports.Take(100).Select(report => $$"""
            <tr>
                <td>{{Html(report.Id)}}</td>
                <td>{{Html(report.ReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"))}}</td>
                <td>{{Html(report.CharacterName ?? "-")}}</td>
                <td><a href="{{Html(AppUrl(pathBase, $"/reports/{report.Id}/json"))}}">Original payload</a></td>
            </tr>
            """));

    return $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>MarketMafioso Diagnostics</title>
            <style>
                :root { color-scheme: dark; --bg: #111316; --panel: #191d21; --border: #323a41; --text: #eef1f3; --muted: #aeb6bd; --accent: #bde8c8; font-family: "Segoe UI", system-ui, sans-serif; }
                body { margin: 0; background: var(--bg); color: var(--text); }
                main { max-width: 1120px; margin: 0 auto; padding: 28px 20px; }
                h1 { margin: 0 0 6px; font-size: 24px; }
                p { color: var(--muted); }
                a { color: var(--accent); }
                .tabs { display: flex; gap: 8px; margin: 16px 0; }
                .button { border: 1px solid var(--border); border-radius: 5px; background: #20262b; color: var(--text); padding: 6px 10px; text-decoration: none; }
                table { width: 100%; border-collapse: collapse; background: var(--panel); border: 1px solid var(--border); }
                th, td { padding: 10px 12px; border-bottom: 1px solid var(--border); text-align: left; }
                th { color: var(--accent); }
            </style>
        </head>
        <body>
            <main>
                <h1>Diagnostics</h1>
                <p>Raw payload access and receiver troubleshooting live here instead of in the main inventory browser.</p>
                <nav class="tabs">
                    <a class="button" href="{{Html(AppUrl(pathBase, "/"))}}">Snapshots</a>
                    <a class="button" href="{{Html(AppUrl(pathBase, "/inventory"))}}">Inventory</a>
                    <a class="button" href="{{Html(AppUrl(pathBase, "/acquisition"))}}">Acquisition</a>
                </nav>
                <table>
                    <thead><tr><th>Snapshot</th><th>Received</th><th>Character</th><th>Raw Data</th></tr></thead>
                    <tbody>{{rows}}</tbody>
                </table>
            </main>
        </body>
        </html>
        """;
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

enum ApiKeyPurpose
{
    None,
    Ingest,
    Read,
    CommandPickup,
}
