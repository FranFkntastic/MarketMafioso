using System.Text.Json;

namespace MarketMafioso.Server;

public sealed class InventoryReportStore
{
    private readonly string reportDirectory;
    private readonly JsonSerializerOptions jsonOptions;

    public InventoryReportStore(IHostEnvironment environment)
    {
        reportDirectory = Path.Combine(environment.ContentRootPath, "data", "reports");
        Directory.CreateDirectory(reportDirectory);

        jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }

    public async Task<StoredInventoryReport> SaveAsync(
        InventoryReport report,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var id = $"{receivedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..26];
        var stored = new StoredInventoryReport
        {
            Id = id,
            ReceivedAt = receivedAt,
            ApiKeyLabel = string.IsNullOrWhiteSpace(apiKey) ? null : "provided",
            Report = report,
            Summary = CreateSummary(id, receivedAt, report),
        };

        var path = GetReportPath(id);
        var json = JsonSerializer.Serialize(stored, jsonOptions);

        await File.WriteAllTextAsync(path, json, cancellationToken);
        return stored;
    }

    public async Task<IReadOnlyList<ReportSummary>> ListSummariesAsync(CancellationToken cancellationToken)
    {
        var reports = await ListReportsAsync(cancellationToken);
        return reports
            .Select(r => r.Summary)
            .OrderByDescending(r => r.ReceivedAt)
            .ToList();
    }

    public async Task<StoredInventoryReport?> GetLatestAsync(CancellationToken cancellationToken)
    {
        var reports = await ListReportsAsync(cancellationToken);
        return reports.OrderByDescending(r => r.ReceivedAt).FirstOrDefault();
    }

    public async Task<StoredInventoryReport?> GetAsync(string id, CancellationToken cancellationToken)
    {
        var path = GetReportPath(id);
        if (!File.Exists(path)) return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StoredInventoryReport>(
            stream,
            jsonOptions,
            cancellationToken);
    }

    private async Task<IReadOnlyList<StoredInventoryReport>> ListReportsAsync(CancellationToken cancellationToken)
    {
        var reports = new List<StoredInventoryReport>();
        foreach (var path in Directory.EnumerateFiles(reportDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var stream = File.OpenRead(path);
            var report = await JsonSerializer.DeserializeAsync<StoredInventoryReport>(
                stream,
                jsonOptions,
                cancellationToken);

            if (report != null)
                reports.Add(report);
        }

        return reports;
    }

    private string GetReportPath(string id)
    {
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Report id contains invalid filename characters.", nameof(id));

        return Path.Combine(reportDirectory, $"{id}.json");
    }

    private static ReportSummary CreateSummary(string id, DateTimeOffset receivedAt, InventoryReport report)
    {
        var playerItems = report.PlayerInventory.SelectMany(b => b.Items).ToList();
        var retainerItems = report.Retainers.SelectMany(r => r.Bags).SelectMany(b => b.Items).ToList();

        return new ReportSummary
        {
            Id = id,
            ReceivedAt = receivedAt,
            CharacterName = report.CharacterName,
            HomeWorld = report.HomeWorld,
            ReportTimestamp = report.Timestamp,
            PlayerBagCount = report.PlayerInventory.Count,
            PlayerItemStacks = playerItems.Count,
            PlayerItemQuantity = checked((int)playerItems.Sum(i => (long)i.Quantity)),
            RetainerCount = report.Retainers.Count,
            RetainerItemStacks = retainerItems.Count,
            RetainerItemQuantity = checked((int)retainerItems.Sum(i => (long)i.Quantity)),
        };
    }
}
