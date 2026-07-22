using System.Text;
using System.Text.Json;
using MarketMafioso.Server.Auth;
using MarketMafioso.Server.Sqlite;
using Microsoft.Extensions.Primitives;

namespace MarketMafioso.Server.Endpoints;

internal static class InventoryReportEndpoints
{
    public static void MapInventoryReportEndpoints(
        this WebApplication app,
        bool requireApiKey,
        string? publicOrigin)
    {
        app.MapPost("/inventory", (
            HttpRequest request,
            InventoryReportStore store,
            IngestKeyAccountResolver accountResolver,
            WorkshopHostCredentialStore credentialStore,
            CancellationToken token) => SaveInventoryReport(
                request,
                store,
                accountResolver,
                credentialStore,
                requireApiKey,
                publicOrigin,
                token));
        app.MapPost("/api/inventory", (
            HttpRequest request,
            InventoryReportStore store,
            IngestKeyAccountResolver accountResolver,
            WorkshopHostCredentialStore credentialStore,
            CancellationToken token) => SaveInventoryReport(
                request,
                store,
                accountResolver,
                credentialStore,
                requireApiKey,
                publicOrigin,
                token));

        app.MapGet("/api/reports", async (
            HttpContext context,
            SqliteConnectionFactory connectionFactory,
            InventoryReportStore store,
            CancellationToken token) =>
        {
            var reports = new List<ReportSummary>();
            foreach (var accountId in await GetAccountIdsAsync(context, connectionFactory, token))
                reports.AddRange(await store.ListSummariesAsync(accountId, null, token));
            return Results.Ok(reports.OrderByDescending(report => report.ReceivedAt).ToArray());
        });

        app.MapGet("/api/reports/latest", async (
            HttpContext context,
            SqliteConnectionFactory connectionFactory,
            InventoryReportStore store,
            CancellationToken token) =>
        {
            var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
            var report = await store.GetLatestAsync(accountIds, null, token);
            return report == null ? Results.NotFound() : Results.Ok(report);
        });

        app.MapGet("/api/reports/{id}/view", async (
            HttpContext context,
            string id,
            SqliteConnectionFactory connectionFactory,
            InventoryReportStore store,
            CancellationToken token) =>
        {
            var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
            var report = await store.GetAsync(accountIds, id, token);
            return report == null
                ? Results.NotFound()
                : Results.Ok(InventorySnapshotViewBuilder.Build(report));
        });

        app.MapGet("/api/reports/{id}", async (
            HttpContext context,
            string id,
            SqliteConnectionFactory connectionFactory,
            InventoryReportStore store,
            CancellationToken token) =>
        {
            var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
            var report = await store.GetAsync(accountIds, id, token);
            return report == null ? Results.NotFound() : Results.Ok(report);
        });

        app.MapGet("/reports/latest/json", async (
            HttpContext context,
            SqliteConnectionFactory connectionFactory,
            InventoryReportStore store,
            CancellationToken token) =>
        {
            var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
            var report = await GetLatestRawJsonAsync(store, accountIds, token);
            return RawJsonResult(report);
        });

        app.MapGet("/reports/{id}/json", async (
            HttpContext context,
            string id,
            SqliteConnectionFactory connectionFactory,
            InventoryReportStore store,
            CancellationToken token) =>
        {
            var accountIds = await GetAccountIdsAsync(context, connectionFactory, token);
            var report = await GetRawJsonAsync(store, accountIds, id, token);
            return RawJsonResult(report);
        });

        app.MapDelete("/api/reports/{id}", async (
            HttpContext context,
            string id,
            SqliteConnectionFactory connectionFactory,
            InventoryReportStore store,
            CancellationToken token) =>
        {
            if (requireApiKey)
                return Results.NotFound();

            var deleted = await DeleteAsync(
                store,
                await GetAccountIdsAsync(context, connectionFactory, token),
                id,
                token);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        app.MapDelete("/api/reports", async (
            HttpContext context,
            SqliteConnectionFactory connectionFactory,
            InventoryReportStore store,
            CancellationToken token) =>
        {
            if (requireApiKey)
                return Results.NotFound();

            var deleted = await DeleteAllAsync(
                store,
                await GetAccountIdsAsync(context, connectionFactory, token),
                token);
            return Results.Ok(new { deleted });
        });

        app.MapGet("/reports/{id}", async (
            HttpRequest request,
            string id,
            SqliteConnectionFactory connectionFactory,
            InventoryReportStore store,
            CancellationToken token) =>
        {
            var accountIds = await GetAccountIdsAsync(request.HttpContext, connectionFactory, token);
            var report = await store.GetAsync(accountIds, id, token);
            return report == null
                ? Results.NotFound(RenderNotFound(id, request.PathBase))
                : Results.Content(
                    RenderReportDetails(report, InventorySnapshotViewBuilder.Build(report), request.PathBase),
                    "text/html; charset=utf-8");
        });

        app.MapPost("/reports/{id}/delete", async (
            HttpRequest request,
            string id,
            SqliteConnectionFactory connectionFactory,
            InventoryReportStore store,
            CancellationToken token) =>
        {
            var deleted = await DeleteAsync(
                store,
                await GetAccountIdsAsync(request.HttpContext, connectionFactory, token),
                id,
                token);
            return deleted
                ? Results.Redirect($"{request.PathBase}/?deleted={Uri.EscapeDataString($"snapshot {id}")}")
                : Results.NotFound(RenderNotFound(id, request.PathBase));
        });

        app.MapPost("/reports/delete-all", async (
            HttpRequest request,
            SqliteConnectionFactory connectionFactory,
            InventoryReportStore store,
            CancellationToken token) =>
        {
            var deleted = await DeleteAllAsync(
                store,
                await GetAccountIdsAsync(request.HttpContext, connectionFactory, token),
                token);
            return Results.Redirect($"{request.PathBase}/?deleted={Uri.EscapeDataString($"{deleted:N0} snapshots")}");
        });
    }

    private static async Task<IResult> SaveInventoryReport(
        HttpRequest request,
        InventoryReportStore store,
        IngestKeyAccountResolver accountResolver,
        WorkshopHostCredentialStore credentialStore,
        bool requireApiKey,
        string? publicOrigin,
        CancellationToken token)
    {
        var suppliedApiKey = request.Headers["X-Api-Key"].Count == 1
            ? request.Headers["X-Api-Key"][0]
            : null;
        if (requireApiKey &&
            !await credentialStore.IsAuthorizedAsync(
                suppliedApiKey,
                WorkshopHostCredentialScope.InventoryWrite,
                token))
        {
            return InvalidApiKey();
        }

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

        var accountId = await accountResolver.ResolveAccountIdAsync(suppliedApiKey, token) ?? 1;
        var stored = await store.SaveAsync(accountId, report, suppliedApiKey ?? string.Empty, rawJson, token);
        return Results.Created(
            AppUrl(request.PathBase, $"/api/reports/{stored.Id}"),
            CreateInventoryReportResponse(request, publicOrigin, stored));
    }

    private static object CreateInventoryReportResponse(
        HttpRequest request,
        string? publicOrigin,
        StoredInventoryReport stored) => new
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
            DashboardUrl = PublicAppUrl(request, publicOrigin, "/inventory"),
            ReportUrl = PublicAppUrl(request, publicOrigin, $"/inventory?snapshotId={Uri.EscapeDataString(stored.Id)}"),
            ApiReportUrl = PublicAppUrl(request, publicOrigin, $"/api/reports/{stored.Id}"),
        };

    private static IResult RawJsonResult(RawInventoryReportJson? report)
    {
        if (report == null)
            return Results.NotFound();

        if (report.RawJson == null)
            return Results.Json(new { error = "raw_json_pruned" }, statusCode: StatusCodes.Status410Gone);

        return Results.Text(report.RawJson, "application/json; charset=utf-8", Encoding.UTF8);
    }

    private static IResult InvalidApiKey() =>
        Results.Json(new { error = "invalid_api_key" }, statusCode: StatusCodes.Status401Unauthorized);

    private static async Task<IReadOnlyList<long>> GetAccountIdsAsync(
        HttpContext context,
        SqliteConnectionFactory connectionFactory,
        CancellationToken token)
    {
        if (!context.Items.TryGetValue(DashboardSessionStore.DashboardUserIdItemKey, out var value) || value is not long userId)
            return [1];

        var accounts = new List<long>();
        await using var connection = await connectionFactory.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account_id
            FROM dashboard_user_accounts
            WHERE dashboard_user_id = $dashboardUserId
            ORDER BY is_default DESC, account_id
            """;
        command.Parameters.AddWithValue("$dashboardUserId", userId);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            accounts.Add(reader.GetInt64(0));
        return accounts.Count == 0 ? [1] : accounts;
    }

    private static async Task<RawInventoryReportJson?> GetLatestRawJsonAsync(
        InventoryReportStore store,
        IReadOnlyList<long> accountIds,
        CancellationToken token)
    {
        var latest = await store.GetLatestAsync(accountIds, null, token);
        return latest == null ? null : await GetRawJsonAsync(store, accountIds, latest.Id, token);
    }

    private static async Task<RawInventoryReportJson?> GetRawJsonAsync(
        InventoryReportStore store,
        IReadOnlyList<long> accountIds,
        string id,
        CancellationToken token)
    {
        foreach (var accountId in accountIds)
        {
            var report = await store.GetRawJsonAsync(accountId, id, token);
            if (report != null)
                return report;
        }
        return null;
    }

    private static async Task<bool> DeleteAsync(
        InventoryReportStore store,
        IReadOnlyList<long> accountIds,
        string id,
        CancellationToken token)
    {
        foreach (var accountId in accountIds)
        {
            if (await store.DeleteAsync(accountId, id, token))
                return true;
        }
        return false;
    }

    private static async Task<int> DeleteAllAsync(
        InventoryReportStore store,
        IReadOnlyList<long> accountIds,
        CancellationToken token)
    {
        var deleted = 0;
        foreach (var accountId in accountIds)
            deleted += await store.DeleteAllAsync(accountId, token);
        return deleted;
    }

    private static string RenderReportDetails(StoredInventoryReport stored, InventorySnapshotView view, PathString pathBase)
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

    private static string RenderOwnerSection(InventoryOwnerView owner, string emptyMessage)
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

    private static string RenderBagSection(InventoryBagView bag)
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

    private static string RenderItemRow(InventoryItemView item) =>
        $$"""
            <tr>
                <td>{{Html(item.DisplayName)}}</td>
                <td class="number">{{item.ItemId}}</td>
                <td class="number">{{item.Quantity:N0}}</td>
                <td>{{(item.IsHQ ? "Yes" : "No")}}</td>
                <td class="number">{{Html(FormatCondition(item.Condition))}}</td>
            </tr>
            """;

    private static string FormatCondition(float condition) =>
        condition <= 0 ? "-" : $"{condition:0.#}%";

    private static string RenderNotFound(string id, PathString pathBase) =>
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

    private static string AppUrl(PathString pathBase, string path) =>
        $"{pathBase}{path}";

    private static string PublicAppUrl(HttpRequest request, string? publicOrigin, string path)
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

    private static string? FirstHeaderValue(StringValues values)
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

    private static string Html(string? value) =>
        (value ?? string.Empty)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
