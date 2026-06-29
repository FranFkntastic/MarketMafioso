using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
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
    app.UsePathBase(configuredBasePath);
}

app.Use(ServeDashboardStaticAssetAsync);
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

app.MapGet("/api/acquisition/requests/{id}/timeline", async (
    string id,
    MarketAcquisitionRequestStore acquisitionStore,
    CancellationToken token) =>
{
    var timeline = await acquisitionStore.GetTimelineAsync(id, token);
    return timeline == null ? Results.NotFound() : Results.Ok(timeline);
});

app.MapGet("/api/acquisition/batches/{id}", async (
    string id,
    MarketAcquisitionRequestStore acquisitionStore,
    CancellationToken token) =>
{
    var batch = await acquisitionStore.GetAsync(id, token);
    return batch == null ? Results.NotFound() : Results.Ok(batch);
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

app.MapGet("/api/inventory/characters", ListDashboardCharacters);
app.MapGet("/api/inventory/browser", GetInventoryBrowser);
app.MapGet("/api/inventory/snapshots", ListDashboardSnapshots);
app.MapGet("/api/settings/dashboard", GetDashboardSettings);
app.MapPut("/api/settings/dashboard", SaveDashboardSettings);
app.MapGet("/api/settings/storage", GetStorageSummary);

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

app.MapPost("/inventory", SaveInventoryReport);
app.MapPost("/api/inventory", SaveInventoryReport);

app.MapPost("/acquisition/requests", CreateAcquisitionRequest);
app.MapPost("/api/acquisition/requests", CreateAcquisitionRequest);
app.MapPost("/acquisition/batches", CreateAcquisitionBatch);
app.MapPost("/api/acquisition/batches", CreateAcquisitionBatch);
app.MapGet("/acquisition/requests/pending", ListPendingAcquisitionRequests);
app.MapGet("/api/acquisition/requests/pending", ListPendingAcquisitionRequests);
app.MapGet("/acquisition/batches/pending", ListPendingAcquisitionBatches);
app.MapGet("/api/acquisition/batches/pending", ListPendingAcquisitionBatches);
app.MapPost("/acquisition/requests/{id}/claim", ClaimAcquisitionRequest);
app.MapPost("/api/acquisition/requests/{id}/claim", ClaimAcquisitionRequest);
app.MapPost("/acquisition/requests/{id}/accept", AcceptAcquisitionRequest);
app.MapPost("/api/acquisition/requests/{id}/accept", AcceptAcquisitionRequest);
app.MapPost("/acquisition/requests/{id}/reject", RejectAcquisitionRequest);
app.MapPost("/api/acquisition/requests/{id}/reject", RejectAcquisitionRequest);
app.MapPost("/acquisition/requests/{id}/cancel", CancelAcquisitionRequest);
app.MapPost("/api/acquisition/requests/{id}/cancel", CancelAcquisitionRequest);
app.MapPost("/acquisition/requests/{id}/resend", ResendAcquisitionRequest);
app.MapPost("/api/acquisition/requests/{id}/resend", ResendAcquisitionRequest);
app.MapPost("/acquisition/requests/{id}/progress", ReportAcquisitionProgress);
app.MapPost("/api/acquisition/requests/{id}/progress", ReportAcquisitionProgress);
app.MapPost("/acquisition/batches/{id}/lines/{lineId}/progress", ReportAcquisitionLineProgress);
app.MapPost("/api/acquisition/batches/{id}/lines/{lineId}/progress", ReportAcquisitionLineProgress);
app.MapPost("/acquisition/batches/{id}/purchases", RecordAcquisitionPurchase);
app.MapPost("/api/acquisition/batches/{id}/purchases", RecordAcquisitionPurchase);
app.MapPost("/acquisition/requests/{id}/complete", CompleteAcquisitionRequest);
app.MapPost("/api/acquisition/requests/{id}/complete", CompleteAcquisitionRequest);
app.MapPost("/acquisition/requests/{id}/fail", FailAcquisitionRequest);
app.MapPost("/api/acquisition/requests/{id}/fail", FailAcquisitionRequest);

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
MapDashboardShellRoute("/acquisition");
MapDashboardShellRoute("/inventory");
MapDashboardShellRoute("/overview");
MapDashboardShellRoute("/settings");

app.Run();

void MapDashboardShellRoute(string route) => app.MapGet(route, ServeBlazorIndex);

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

async Task<IResult> ListDashboardCharacters(
    HttpContext context,
    SqliteConnectionFactory connectionFactory,
    InventoryReportStore store,
    CancellationToken token)
{
    var accountIds = await GetDashboardAccountIdsAsync(context, connectionFactory, token);
    var characters = new List<DashboardCharacterOption>();
    foreach (var accountId in accountIds)
    {
        var accountCharacters = await store.ListCharactersAsync(accountId, token);
        characters.AddRange(accountCharacters.Select(character => new DashboardCharacterOption(
            character.Id,
            character.CharacterName,
            character.HomeWorld,
            character.LastSeenAt)));
    }

    return Results.Ok(characters
        .GroupBy(character => character.Id)
        .Select(group => group.First())
        .OrderByDescending(character => character.LastSeenAt)
        .ThenBy(character => character.CharacterName, StringComparer.OrdinalIgnoreCase)
        .ToArray());
}

async Task<IResult> GetInventoryBrowser(
    HttpContext context,
    SqliteConnectionFactory connectionFactory,
    InventoryReportStore store,
    long? characterId,
    string? search,
    string? scope,
    CancellationToken token)
{
    if (characterId != null &&
        !await DashboardCanAccessCharacterAsync(context, connectionFactory, characterId.Value, token))
    {
        return Results.NotFound();
    }

    foreach (var accountId in await GetDashboardAccountIdsAsync(context, connectionFactory, token))
    {
        var report = await store.GetLatestAsync(accountId, characterId, token);
        if (report != null)
            return Results.Ok(InventoryBrowserViewBuilder.Build(report, search, scope));
    }

    return Results.Ok(InventoryBrowserViewBuilder.Build(null, search, scope));
}

async Task<IResult> ListDashboardSnapshots(
    HttpContext context,
    SqliteConnectionFactory connectionFactory,
    InventoryReportStore store,
    long? characterId,
    CancellationToken token)
{
    if (characterId != null &&
        !await DashboardCanAccessCharacterAsync(context, connectionFactory, characterId.Value, token))
    {
        return Results.NotFound();
    }

    var summaries = new List<ReportSummary>();
    foreach (var accountId in await GetDashboardAccountIdsAsync(context, connectionFactory, token))
        summaries.AddRange(await store.ListSummariesAsync(accountId, characterId, token));

    return Results.Ok(summaries
        .OrderByDescending(summary => summary.ReceivedAt)
        .Take(500)
        .ToArray());
}

async Task<IResult> GetDashboardSettings(
    HttpContext context,
    SqliteConnectionFactory connectionFactory,
    CancellationToken token)
{
    var owner = DashboardPreferenceOwner(context);
    var settings = await LoadDashboardSettingsAsync(connectionFactory, owner, token);
    if (settings != null)
        return Results.Ok(settings);

    return Results.Ok(new DashboardSettingsView());
}

async Task<IResult> SaveDashboardSettings(
    HttpContext context,
    SqliteConnectionFactory connectionFactory,
    DashboardSettingsUpdate update,
    CancellationToken token)
{
    if (!string.Equals(update.DefaultRegion, "North America", StringComparison.Ordinal))
        return Results.BadRequest(new { error = "unsupported_region" });

    if (update.DefaultWorldMode is not ("Recommended" or "CurrentWorld" or "AllWorldSweep"))
        return Results.BadRequest(new { error = "unsupported_world_mode" });

    if (update.DefaultPickupExpiresSeconds is < 60 or > 3600)
        return Results.BadRequest(new { error = "unsupported_pickup_expiry" });

    if (update.DefaultCharacterId != null &&
        !await DashboardCanAccessCharacterAsync(context, connectionFactory, update.DefaultCharacterId.Value, token))
    {
        return Results.BadRequest(new { error = "unknown_character" });
    }

    var now = DateTimeOffset.UtcNow;
    var view = new DashboardSettingsView
    {
        DefaultCharacterId = update.DefaultCharacterId,
        DefaultRegion = update.DefaultRegion,
        DefaultWorldMode = update.DefaultWorldMode,
        DefaultPickupExpiresSeconds = update.DefaultPickupExpiresSeconds,
        UpdatedAtUtc = now,
    };
    var owner = DashboardPreferenceOwner(context);
    await using var connection = await connectionFactory.OpenConnectionAsync(token);
    await using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO dashboard_preferences (
            owner_kind,
            owner_key,
            scope,
            preferences_json,
            updated_at_utc
        )
        VALUES (
            $ownerKind,
            $ownerKey,
            $scope,
            $preferencesJson,
            $updatedAt
        )
        ON CONFLICT(owner_kind, owner_key, scope)
        DO UPDATE SET
            preferences_json = excluded.preferences_json,
            updated_at_utc = excluded.updated_at_utc
        """;
    command.Parameters.AddWithValue("$ownerKind", owner.OwnerKind);
    command.Parameters.AddWithValue("$ownerKey", owner.OwnerKey);
    command.Parameters.AddWithValue("$scope", owner.Scope);
    command.Parameters.AddWithValue("$preferencesJson", JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    command.Parameters.AddWithValue("$updatedAt", now.ToString("O", CultureInfo.InvariantCulture));
    await command.ExecuteNonQueryAsync(token);

    return Results.Ok(view);
}

async Task<IResult> GetStorageSummary(
    HttpContext context,
    SqliteConnectionFactory connectionFactory,
    InventoryReportStore inventoryStore,
    DiagnosticEventStore diagnostics,
    IConfiguration configuration,
    CancellationToken token)
{
    var accountIds = await GetDashboardAccountIdsAsync(context, connectionFactory, token);
    var inventory = await inventoryStore.GetRetentionSummaryAsync(accountIds, token);
    var diagnosticCount = await diagnostics.CountAsync(token);

    return Results.Ok(new ReceiverStorageSummaryView
    {
        SnapshotRetentionCount = configuration.GetValue("MarketMafioso:SnapshotRetentionCount", 500),
        RawJsonRetentionCount = configuration.GetValue("MarketMafioso:RawJsonRetentionCount", 20),
        DiagnosticEventRetentionCount = Math.Max(1, configuration.GetValue("MarketMafioso:DiagnosticEventRetention", 5000)),
        SnapshotCount = inventory.SnapshotCount,
        RawJsonRetainedCount = inventory.RawJsonRetainedCount,
        RawJsonPrunedCount = inventory.RawJsonPrunedCount,
        DiagnosticEventCount = diagnosticCount,
        NewestSnapshotReceivedAtUtc = inventory.NewestSnapshotReceivedAtUtc,
        OldestSnapshotReceivedAtUtc = inventory.OldestSnapshotReceivedAtUtc,
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

async Task<IResult> CreateAcquisitionBatch(
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

async Task<IResult> ListPendingAcquisitionBatches(
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

async Task<IResult> RejectAcquisitionRequest(
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
    HttpRequest request,
    string id,
    MarketAcquisitionRequestStore store,
    CancellationToken token) =>
    ApplyAcquisitionLifecycleAsync(
        store.ReportProgressAsync,
        store.ReportAttemptProgressAsync,
        id,
        request,
        token);

async Task<IResult> ReportAcquisitionLineProgress(
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

async Task<IResult> RecordAcquisitionPurchase(
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

Task<IResult> CompleteAcquisitionRequest(
    HttpRequest request,
    string id,
    MarketAcquisitionRequestStore store,
    CancellationToken token) =>
    ApplyAcquisitionLifecycleAsync(
        store.CompleteAsync,
        store.CompleteAttemptAsync,
        id,
        request,
        token);

Task<IResult> FailAcquisitionRequest(
    HttpRequest request,
    string id,
    MarketAcquisitionRequestStore store,
    CancellationToken token) =>
    ApplyAcquisitionLifecycleAsync(
        store.FailAsync,
        store.FailAttemptAsync,
        id,
        request,
        token);

static async Task<IResult> ApplyAcquisitionLifecycleAsync(
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

    if (IsApiKeyAcquisitionCreate(request))
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
    HttpMethods.IsPost(request.Method) &&
    !request.Headers.ContainsKey("X-Api-Key") &&
    (request.Path.StartsWithSegments("/acquisition/requests") ||
     request.Path.StartsWithSegments("/api/acquisition/requests")) &&
    (request.Path.Value?.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase) == true ||
     request.Path.Value?.EndsWith("/resend", StringComparison.OrdinalIgnoreCase) == true);

static bool IsAcquisitionBrowserRead(HttpRequest request) =>
    HttpMethods.IsGet(request.Method) &&
    request.Path.Equals("/api/acquisition/requests", StringComparison.OrdinalIgnoreCase);

static bool WantsJsonResponse(HttpRequest request)
{
    return request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
}

static bool IsDashboardApiRoute(HttpRequest request) =>
    request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

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
               request.Path.Equals("/api/acquisition/batches/pending", StringComparison.OrdinalIgnoreCase);
    }

    if (!HttpMethods.IsPost(request.Method))
        return false;

    var path = request.Path.Value ?? string.Empty;
    return (path.StartsWith("/acquisition/requests/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/acquisition/requests/", StringComparison.OrdinalIgnoreCase)) &&
           (path.EndsWith("/claim", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/accept", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/reject", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/resend", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/progress", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/complete", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/fail", StringComparison.OrdinalIgnoreCase));
}

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

static DashboardPreferenceOwner DashboardPreferenceOwner(HttpContext context)
{
    var userId = DashboardUserId(context);
    return userId == null
        ? new DashboardPreferenceOwner("global", "default", "dashboard")
        : new DashboardPreferenceOwner("dashboard-user", userId.Value.ToString(CultureInfo.InvariantCulture), "dashboard");
}

static long? DashboardUserId(HttpContext context) =>
    context.Items.TryGetValue(DashboardSessionStore.DashboardUserIdItemKey, out var value) && value is long userId
        ? userId
        : null;

static async Task<IReadOnlyList<long>> GetDashboardAccountIdsAsync(
    HttpContext context,
    SqliteConnectionFactory connectionFactory,
    CancellationToken cancellationToken)
{
    var userId = DashboardUserId(context);
    if (userId == null)
        return [1];

    var accounts = new List<long>();
    await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT account_id
        FROM dashboard_user_accounts
        WHERE dashboard_user_id = $dashboardUserId
        ORDER BY is_default DESC, account_id
        """;
    command.Parameters.AddWithValue("$dashboardUserId", userId.Value);

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
        accounts.Add(reader.GetInt64(0));

    return accounts.Count == 0 ? [1] : accounts;
}

static async Task<bool> DashboardCanAccessCharacterAsync(
    HttpContext context,
    SqliteConnectionFactory connectionFactory,
    long characterId,
    CancellationToken cancellationToken)
{
    var accountIds = await GetDashboardAccountIdsAsync(context, connectionFactory, cancellationToken);
    await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = $"""
        SELECT 1
        FROM characters
        WHERE id = $characterId
          AND account_id IN ({string.Join(", ", accountIds.Select((_, index) => $"$account{index}"))})
        LIMIT 1
        """;
    command.Parameters.AddWithValue("$characterId", characterId);
    for (var i = 0; i < accountIds.Count; i++)
        command.Parameters.AddWithValue($"$account{i}", accountIds[i]);

    return await command.ExecuteScalarAsync(cancellationToken) != null;
}

static async Task<DashboardSettingsView?> LoadDashboardSettingsAsync(
    SqliteConnectionFactory connectionFactory,
    DashboardPreferenceOwner owner,
    CancellationToken cancellationToken)
{
    await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT preferences_json, updated_at_utc
        FROM dashboard_preferences
        WHERE owner_kind = $ownerKind
          AND owner_key = $ownerKey
          AND scope = $scope
        """;
    command.Parameters.AddWithValue("$ownerKind", owner.OwnerKind);
    command.Parameters.AddWithValue("$ownerKey", owner.OwnerKey);
    command.Parameters.AddWithValue("$scope", owner.Scope);

    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken))
        return null;

    var settings = JsonSerializer.Deserialize<DashboardSettingsView>(
        reader.GetString(0),
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    if (settings == null)
        return null;

    return settings with
    {
        UpdatedAtUtc = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
    };
}

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

sealed record DashboardPreferenceOwner(string OwnerKind, string OwnerKey, string Scope);

enum ApiKeyPurpose
{
    None,
    Ingest,
    Read,
    CommandPickup,
}
