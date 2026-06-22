using System.Text;
using System.Text.Json;
using MarketMafioso.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<InventoryReportStore>();

var app = builder.Build();
var requiredApiKey = app.Configuration["MarketMafioso:ApiKey"];

app.MapGet("/", async (InventoryReportStore store, string? deleted, CancellationToken token) =>
{
    var reports = await store.ListSummariesAsync(token);
    return Results.Content(RenderDashboard(reports, store.ReportDirectory, deleted), "text/html; charset=utf-8");
});

app.MapGet("/health", () => Results.Ok(new
{
    ok = true,
    utc = DateTimeOffset.UtcNow,
}));

app.MapPost("/inventory", SaveInventoryReport);
app.MapPost("/api/inventory", SaveInventoryReport);

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

app.MapGet("/api/reports/{id}", async (string id, InventoryReportStore store, CancellationToken token) =>
{
    var report = await store.GetAsync(id, token);
    return report == null ? Results.NotFound() : Results.Ok(report);
});

app.MapDelete("/api/reports/{id}", async (string id, InventoryReportStore store, CancellationToken token) =>
{
    var deleted = await store.DeleteAsync(id, token);
    return deleted ? Results.NoContent() : Results.NotFound();
});

app.MapDelete("/api/reports", async (InventoryReportStore store, CancellationToken token) =>
{
    var deleted = await store.DeleteAllAsync(token);
    return Results.Ok(new { deleted });
});

app.MapGet("/reports/{id}", async (string id, InventoryReportStore store, CancellationToken token) =>
{
    var report = await store.GetAsync(id, token);
    return report == null
        ? Results.NotFound(RenderNotFound(id))
        : Results.Content(RenderReportDetails(report), "text/html; charset=utf-8");
});

app.MapPost("/reports/{id}/delete", async (string id, InventoryReportStore store, CancellationToken token) =>
{
    var deleted = await store.DeleteAsync(id, token);
    return deleted
        ? Results.Redirect($"/?deleted={Uri.EscapeDataString($"snapshot {id}")}")
        : Results.NotFound(RenderNotFound(id));
});

app.MapPost("/reports/delete-all", async (InventoryReportStore store, CancellationToken token) =>
{
    var deleted = await store.DeleteAllAsync(token);
    return Results.Redirect($"/?deleted={Uri.EscapeDataString($"{deleted:N0} snapshots")}");
});

app.Run();

async Task<IResult> SaveInventoryReport(
    HttpRequest request,
    InventoryReport report,
    InventoryReportStore store,
    CancellationToken token)
{
    var suppliedApiKey = request.Headers["X-Api-Key"].ToString();
    if (!string.IsNullOrWhiteSpace(requiredApiKey) && suppliedApiKey != requiredApiKey)
        return Results.Unauthorized();

    if (report.PlayerInventory.Count == 0 && report.Retainers.Count == 0)
        return Results.BadRequest(new { error = "Report must include at least one player inventory bag or retainer." });

    var stored = await store.SaveAsync(report, suppliedApiKey, token);
    return Results.Created($"/api/reports/{stored.Id}", stored.Summary);
}

static string RenderDashboard(IReadOnlyList<ReportSummary> reports, string reportDirectory, string? deleted)
{
    var rows = new StringBuilder();
    foreach (var report in reports)
    {
        rows.AppendLine($"""
            <tr>
                <td><a href="/reports/{Html(report.Id)}">{Html(report.Id)}</a></td>
                <td>{Html(report.ReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"))}</td>
                <td>{Html(report.CharacterName ?? "-")}</td>
                <td>{Html(report.HomeWorld ?? "-")}</td>
                <td>{report.PlayerItemStacks:N0} stacks / {report.PlayerItemQuantity:N0} items</td>
                <td>{report.RetainerCount:N0} retainers / {report.RetainerItemStacks:N0} stacks</td>
                <td class="actions">
                    <a class="button" href="/reports/{Html(report.Id)}">View</a>
                    <a class="button" href="/api/reports/{Html(report.Id)}">JSON</a>
                    <form method="post" action="/reports/{Html(report.Id)}/delete" onsubmit="return confirm('Delete snapshot {Html(report.Id)}?');">
                        <button class="danger" type="submit">Delete</button>
                    </form>
                </td>
            </tr>
            """);
    }

    var latest = reports.Count == 0
        ? "Never"
        : reports.Max(r => r.ReceivedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
    var notice = string.IsNullOrWhiteSpace(deleted)
        ? string.Empty
        : $"""<p class="notice">Deleted <code>{Html(deleted)}</code>.</p>""";
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
                .cards {
                    display: grid;
                    grid-template-columns: repeat(2, minmax(0, 1fr));
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
                @media (max-width: 900px) {
                    header { display: block; }
                    .cards { grid-template-columns: repeat(2, minmax(0, 1fr)); }
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
                        <a class="button" href="/">Refresh</a>
                        <a class="button" href="/api/reports/latest">Latest JSON</a>
                        <form method="post" action="/reports/delete-all" onsubmit="return confirm('Delete all stored snapshots?');">
                            <button class="danger" type="submit">Delete All</button>
                        </form>
                    </div>
                </header>
                {{notice}}
                <section class="cards">
                    <div class="card"><div class="label">Snapshots</div><div class="value">{{reports.Count:N0}}</div></div>
                    <div class="card"><div class="label">Latest received</div><div class="value" style="font-size: 15px;">{{Html(latest)}}</div></div>
                </section>
                <div class="path">Storage: <code>{{Html(reportDirectory)}}</code></div>
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

static string RenderReportDetails(StoredInventoryReport stored)
{
    var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    });

    return $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Snapshot {{Html(stored.Id)}}</title>
            <style>
                :root { color-scheme: dark; }
                body { margin: 0; font-family: "Segoe UI", system-ui, sans-serif; background: #111316; color: #eef1f3; }
                main { max-width: 980px; margin: 0 auto; padding: 28px 20px; }
                a { color: #bde8c8; }
                .top { display: flex; justify-content: space-between; gap: 12px; align-items: start; margin-bottom: 18px; }
                h1 { margin: 0 0 6px; font-size: 24px; letter-spacing: 0; }
                p { color: #aeb6bd; }
                .panel { border: 1px solid #323a41; background: #191d21; border-radius: 6px; padding: 14px; margin: 12px 0; }
                dl { display: grid; grid-template-columns: 150px 1fr; gap: 8px 14px; margin: 0; }
                dt { color: #aeb6bd; }
                dd { margin: 0; }
                form { display: inline; }
                button, .button { border: 1px solid #323a41; border-radius: 5px; background: #20262b; color: #eef1f3; padding: 6px 10px; font: inherit; text-decoration: none; cursor: pointer; }
                .danger { background: #3a2024; border-color: #694047; color: #ffd7db; }
                pre { overflow: auto; border: 1px solid #323a41; background: #191d21; border-radius: 6px; padding: 14px; }
            </style>
        </head>
        <body>
            <main>
                <div class="top">
                    <div>
                        <h1>Snapshot {{Html(stored.Id)}}</h1>
                        <p>{{Html(stored.Summary.CharacterName ?? "-")}} @ {{Html(stored.Summary.HomeWorld ?? "-")}}</p>
                    </div>
                    <div>
                        <a class="button" href="/">Back</a>
                        <a class="button" href="/api/reports/{{Html(stored.Id)}}">JSON</a>
                        <form method="post" action="/reports/{{Html(stored.Id)}}/delete" onsubmit="return confirm('Delete snapshot {{Html(stored.Id)}}?');">
                            <button class="danger" type="submit">Delete</button>
                        </form>
                    </div>
                </div>
                <section class="panel">
                    <dl>
                        <dt>Received</dt><dd>{{Html(stored.ReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"))}}</dd>
                        <dt>Report timestamp</dt><dd>{{Html(stored.Summary.ReportTimestamp)}}</dd>
                        <dt>Player inventory</dt><dd>{{stored.Summary.PlayerItemStacks:N0}} stacks / {{stored.Summary.PlayerItemQuantity:N0}} items</dd>
                        <dt>Retainers</dt><dd>{{stored.Summary.RetainerCount:N0}} retainers / {{stored.Summary.RetainerItemStacks:N0}} stacks / {{stored.Summary.RetainerItemQuantity:N0}} items</dd>
                    </dl>
                </section>
                <pre>{{Html(json)}}</pre>
            </main>
        </body>
        </html>
        """;
}

static string RenderNotFound(string id) =>
    $"""
    <!doctype html>
    <html lang="en">
    <head><meta charset="utf-8"><title>Snapshot not found</title></head>
    <body style="font-family: Segoe UI, system-ui, sans-serif; background: #111316; color: #eef1f3;">
        <main style="max-width: 760px; margin: 40px auto;">
            <h1>Snapshot not found</h1>
            <p>No stored snapshot exists for <code>{Html(id)}</code>.</p>
            <p><a style="color: #bde8c8;" href="/">Back to receiver</a></p>
        </main>
    </body>
    </html>
    """;

static string Html(string? value) =>
    (value ?? string.Empty)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
