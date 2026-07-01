using System.Text.Json;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Migration;

public sealed class JsonSnapshotImporter
{
    private readonly IHostEnvironment environment;
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly InventoryReportStore store;
    private readonly ILogger<JsonSnapshotImporter> log;
    private readonly JsonSerializerOptions jsonOptions;

    public JsonSnapshotImporter(
        IHostEnvironment environment,
        SqliteConnectionFactory connectionFactory,
        InventoryReportStore store,
        ILogger<JsonSnapshotImporter> log)
    {
        this.environment = environment;
        this.connectionFactory = connectionFactory;
        this.store = store;
        this.log = log;
        jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };
    }

    public async Task<JsonSnapshotImportResult> ImportAsync(CancellationToken cancellationToken)
    {
        var reportDirectory = Path.Combine(environment.ContentRootPath, "data", "reports");
        if (!Directory.Exists(reportDirectory))
            return new JsonSnapshotImportResult(0, 0);

        var accountId = await GetDefaultAccountIdAsync(cancellationToken);
        var imported = 0;
        var skipped = 0;

        foreach (var path in Directory.EnumerateFiles(reportDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var rawJson = await File.ReadAllTextAsync(path, cancellationToken);
                var stored = JsonSerializer.Deserialize<StoredInventoryReport>(rawJson, jsonOptions);
                if (stored == null || string.IsNullOrWhiteSpace(stored.Id))
                {
                    skipped++;
                    continue;
                }

                if (await store.ExistsAsync(accountId, stored.Id, cancellationToken))
                {
                    skipped++;
                    continue;
                }

                await store.SaveImportedAsync(accountId, stored, rawJson, cancellationToken);
                imported++;
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
            {
                skipped++;
                log.LogWarning(ex, "Skipping inventory snapshot import file {Path}.", path);
            }
        }

        return new JsonSnapshotImportResult(imported, skipped);
    }

    private async Task<long> GetDefaultAccountIdAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM accounts ORDER BY id LIMIT 1";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long accountId
            ? accountId
            : throw new InvalidOperationException("Cannot import JSON snapshots because no receiver account exists.");
    }
}

public sealed record JsonSnapshotImportResult(int Imported, int Skipped);
