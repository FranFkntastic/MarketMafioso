using System.Text;
using MarketMafioso.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<InventoryReportStore>();

var app = builder.Build();
var requiredApiKey = app.Configuration["MarketMafioso:ApiKey"];

app.MapGet("/", async (InventoryReportStore store, CancellationToken token) =>
{
    var reports = await store.ListSummariesAsync(token);
    return Results.Content(RenderDashboard(reports), "text/html; charset=utf-8");
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

static string RenderDashboard(IReadOnlyList<ReportSummary> reports)
{
    var rows = new StringBuilder();
    foreach (var report in reports)
    {
        rows.AppendLine($"""
            <tr>
                <td><a href="/api/reports/{Html(report.Id)}">{Html(report.Id)}</a></td>
                <td>{Html(report.ReceivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"))}</td>
                <td>{Html(report.CharacterName ?? "-")}</td>
                <td>{Html(report.HomeWorld ?? "-")}</td>
                <td>{report.PlayerItemStacks} stacks / {report.PlayerItemQuantity} items</td>
                <td>{report.RetainerCount} retainers / {report.RetainerItemStacks} stacks</td>
            </tr>
            """);
    }

    var emptyState = reports.Count == 0
        ? "<p class=\"empty\">No reports yet. Point MarketMafioso at <code>http://localhost:8080/inventory</code> and send one.</p>"
        : string.Empty;

    return $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>MarketMafioso Local Server</title>
            <style>
                :root { color-scheme: dark; }
                body {
                    margin: 0;
                    font-family: "Segoe UI", system-ui, sans-serif;
                    background: #101418;
                    color: #eef3f7;
                }
                main { max-width: 1100px; margin: 0 auto; padding: 32px 20px; }
                h1 { margin: 0 0 4px; font-size: 28px; }
                p { color: #aebac4; }
                code {
                    padding: 2px 5px;
                    border-radius: 4px;
                    background: #202831;
                    color: #c8f2ff;
                }
                table {
                    width: 100%;
                    border-collapse: collapse;
                    margin-top: 20px;
                    background: #161c22;
                    border: 1px solid #2a3540;
                }
                th, td {
                    padding: 10px 12px;
                    border-bottom: 1px solid #2a3540;
                    text-align: left;
                    white-space: nowrap;
                }
                th { color: #93d7ff; font-weight: 600; }
                a { color: #8ed9ff; }
                .empty {
                    padding: 16px;
                    border: 1px solid #2a3540;
                    background: #161c22;
                }
            </style>
        </head>
        <body>
            <main>
                <h1>MarketMafioso Local Server</h1>
                <p>File-backed local receiver for inventory reports. JSON endpoints: <code>/api/reports</code>, <code>/api/reports/latest</code>, and <code>/api/reports/{id}</code>.</p>
                {{emptyState}}
                <table>
                    <thead>
                        <tr>
                            <th>Report</th>
                            <th>Received</th>
                            <th>Character</th>
                            <th>World</th>
                            <th>Player Inventory</th>
                            <th>Retainers</th>
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

static string Html(string value) =>
    value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
