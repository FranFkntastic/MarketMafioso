using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MarketMafioso.Server.Migration;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Tests;

public sealed class JsonSnapshotImporterTests
{
    [Fact]
    public async Task ImportAsync_ImportsExistingJsonSnapshotsOnlyOnce()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        var reportDirectory = Path.Combine(contentRoot, "data", "reports");
        Directory.CreateDirectory(reportDirectory);
        var databasePath = Path.Combine(contentRoot, "marketmafioso.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketMafioso:DatabasePath"] = databasePath,
            })
            .Build();
        var environment = new TestHostEnvironment(contentRoot);
        var connectionFactory = new SqliteConnectionFactory(configuration, environment);
        var migrator = new SqliteSchemaMigrator(connectionFactory, NullLogger<SqliteSchemaMigrator>.Instance);
        await migrator.MigrateAsync(CancellationToken.None);
        await CreateDefaultAccountAsync(connectionFactory);
        var store = new InventoryReportStore(connectionFactory, configuration, NullLogger<InventoryReportStore>.Instance);
        var importer = new JsonSnapshotImporter(
            environment,
            connectionFactory,
            store,
            NullLogger<JsonSnapshotImporter>.Instance);
        var stored = CreateStoredReport("imported-snapshot");
        await File.WriteAllTextAsync(
            Path.Combine(reportDirectory, $"{stored.Id}.json"),
            JsonSerializer.Serialize(stored, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CancellationToken.None);

        var first = await importer.ImportAsync(CancellationToken.None);
        var second = await importer.ImportAsync(CancellationToken.None);
        var summaries = await store.ListSummariesAsync(1, characterId: null, CancellationToken.None);

        Assert.Equal(1, first.Imported);
        Assert.Equal(0, second.Imported);
        Assert.Single(summaries);
        Assert.Equal("imported-snapshot", summaries[0].Id);
    }

    private static StoredInventoryReport CreateStoredReport(string id)
    {
        var receivedAt = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var report = new InventoryReport
        {
            CharacterName = "Imported Character",
            HomeWorld = "Gilgamesh",
            Timestamp = "2026-06-23T12:00:00.0000000Z",
            PlayerInventory =
            [
                new InventoryBag
                {
                    BagName = "Inventory1",
                    Items =
                    [
                        new ItemSlot
                        {
                            ItemId = 2,
                            ItemName = "Fire Shard",
                            Quantity = 99,
                            IsHQ = false,
                            Condition = 0,
                        },
                    ],
                },
            ],
        };

        return new StoredInventoryReport
        {
            Id = id,
            ReceivedAt = receivedAt,
            ApiKeyLabel = "provided",
            Report = report,
            Summary = new ReportSummary
            {
                Id = id,
                ReceivedAt = receivedAt,
                CharacterName = report.CharacterName,
                HomeWorld = report.HomeWorld,
                ReportTimestamp = report.Timestamp,
                PlayerBagCount = 1,
                PlayerItemStacks = 1,
                PlayerItemQuantity = 99,
            },
        };
    }

    private static async Task CreateDefaultAccountAsync(SqliteConnectionFactory connectionFactory)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts (display_name, created_at_utc)
            VALUES ('Default', $createdAt)
            """;
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MarketMafioso.Server.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
