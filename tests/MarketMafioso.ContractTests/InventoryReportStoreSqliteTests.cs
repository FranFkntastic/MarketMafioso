using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MarketMafioso.Contracts.Inventory;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.ContractTests;

public sealed class InventoryReportStoreSqliteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeWorldTransfer_ReconcilesCurrentInventoryAndRejectsReplay(bool existingSplit)
    {
        var fixture = await StoreFixture.CreateAsync();
        var old = CreateReport("Wei Ning", "Siren", 42) with
        {
            Retainers = [Retainer("Taffy-marauder", 12345, "Wei Ning", "Siren", 12)],
        };
        var previous = await fixture.Store.SaveAsync(fixture.AccountId, old, null, null, CancellationToken.None);
        var oldCharacter = Assert.Single(await fixture.Store.ListCharactersAsync(fixture.AccountId, CancellationToken.None));
        if (existingSplit)
        {
            // Simulate the pre-fix database: the incoming world has already been stored independently.
            await using var connection = await fixture.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE inventory_owners SET retainer_id = 54321 WHERE owner_type = 'retainer'";
            await command.ExecuteNonQueryAsync();
        }
        var moved = old with
        {
            HomeWorld = "Balmung", Timestamp = "2026-06-24T12:00:00Z",
            Retainers = [Retainer("Taffy-marauder", 12345, "Wei Ning", "Balmung", 28)],
        };
        var current = await fixture.Store.SaveAsync(fixture.AccountId, moved, null, null, CancellationToken.None);
        if (existingSplit)
        {
            await using var connection = await fixture.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE inventory_owners SET retainer_id = 12345 WHERE owner_type = 'retainer'";
            await command.ExecuteNonQueryAsync();
            await fixture.MigrateAsync();
            var revision = await fixture.Store.GetRevisionAsync([fixture.AccountId], CancellationToken.None);
            await fixture.MigrateAsync(); // Restart repair is idempotent.
            Assert.Equal(revision.Token, (await fixture.Store.GetRevisionAsync([fixture.AccountId], CancellationToken.None)).Token);
        }

        var reports = await fixture.Store.GetLatestByCharacterAsync([fixture.AccountId], CancellationToken.None);
        Assert.Equal(current.Id, Assert.Single(reports).Id);
        var view = InventoryBrowserViewBuilder.Build(reports, "", mode: InventoryBrowserMode.Items);
        Assert.Equal(28, Assert.Single(view.Items).Locations.Single(location => location.OwnerName == "Taffy-marauder").Quantity);
        Assert.Equal("Balmung", Assert.Single(await fixture.Store.ListCharactersAsync(fixture.AccountId, CancellationToken.None)).HomeWorld);
        Assert.Equal(current.Id, (await fixture.Store.GetLatestAsync(fixture.AccountId, oldCharacter.Id, CancellationToken.None))!.Id);
        Assert.NotNull(await fixture.Store.GetAsync(fixture.AccountId, previous.Id, CancellationToken.None));
        Assert.False(await fixture.Store.CanApplyDeltaBaseAsync(fixture.AccountId, previous.Id, CancellationToken.None));
        // Even a partial old-world replay without retainers must use the persisted lineage.
        var replay = await fixture.Store.SaveAsync(fixture.AccountId, old with { Retainers = [] }, null, null, CancellationToken.None);
        Assert.Equal(current.Id, replay.Id);
        var tiedReplay = await fixture.Store.SaveAsync(fixture.AccountId, old with { Timestamp = moved.Timestamp }, null, null, CancellationToken.None);
        Assert.Equal(current.Id, tiedReplay.Id);
        Assert.Single(await fixture.Store.GetLatestByCharacterAsync([fixture.AccountId], CancellationToken.None));
        await fixture.Store.SaveAsync(fixture.AccountId, old with { Timestamp = "2026-06-25T12:00:00Z" }, null, null, CancellationToken.None);
        await fixture.Store.SaveAsync(fixture.AccountId, moved with { HomeWorld = "Maduin", Timestamp = "2026-06-26T12:00:00Z" }, null, null, CancellationToken.None);
        Assert.Equal("Maduin", Assert.Single(await fixture.Store.GetLatestByCharacterAsync([fixture.AccountId], CancellationToken.None)).Report.HomeWorld);
        // Removing history must not erase the identity link.
        await using (var connection = await fixture.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM snapshots WHERE is_current = 0";
            await command.ExecuteNonQueryAsync();
        }
        await fixture.Store.SaveAsync(fixture.AccountId, old with { Retainers = [] }, null, null, CancellationToken.None);
        Assert.Equal("Maduin", Assert.Single(await fixture.Store.GetLatestByCharacterAsync([fixture.AccountId], CancellationToken.None)).Report.HomeWorld);
    }

    [Fact]
    public async Task HomeWorldTransfer_DoesNotMergeNamesZeroIdsOrAccounts()
    {
        var fixture = await StoreFixture.CreateAsync();
        var otherAccount = await fixture.CreateAccountAsync("Other");
        foreach (var (account, world, id) in new[]
                 { (fixture.AccountId, "Siren", 123UL), (fixture.AccountId, "Balmung", 456UL),
                   (fixture.AccountId, "Gilgamesh", 0UL), (fixture.AccountId, "Faerie", 0UL), (otherAccount, "Maduin", 123UL) })
            await fixture.Store.SaveAsync(account, CreateReport("Same Name", world, 42) with
            {
                Retainers = [Retainer("Same Retainer", id, "Same Name", world, 12)],
            }, null, null, CancellationToken.None);
        Assert.Equal(4, (await fixture.Store.GetLatestByCharacterAsync([fixture.AccountId], CancellationToken.None)).Count);
        Assert.Single(await fixture.Store.GetLatestByCharacterAsync([otherAccount], CancellationToken.None));
    }

    private static InventoryReport CreateReport(string characterName, string homeWorld, uint itemId) =>
        new()
        {
            Metadata = new InventoryReportMetadata
            {
                SchemaVersion = 1,
                SourcePlugin = "MarketMafioso",
                PluginVersion = "1.0.0.0",
                GeneratedAtUtc = "2026-06-23T12:00:00.0000000Z",
            },
            CharacterName = characterName,
            HomeWorld = homeWorld,
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
                            ItemId = itemId,
                            ItemName = $"Item {itemId}",
                            ItemType = "Test Item Type",
                            Quantity = 12,
                            IsHQ = itemId % 2 == 0,
                            Condition = 100,
                        },
                    ],
                },
            ],
        };

    private static InventoryBag Bag(string bagName, params ItemSlot[] items) => new()
    {
        BagName = bagName,
        Items = [.. items],
    };

    private static ItemSlot Slot(uint itemId, uint quantity, int slotIndex, float conditionPercent) => new()
    {
        ItemId = itemId,
        ItemName = $"Item {itemId}",
        ItemType = "Test Item Type",
        Quantity = quantity,
        SlotIndex = slotIndex,
        ConditionPercent = conditionPercent,
    };

    private static RetainerReport Retainer(
        string retainerName,
        ulong retainerId,
        string characterName,
        string homeWorld,
        uint quantity) => new()
    {
        RetainerName = retainerName,
        RetainerId = retainerId,
        OwnerCharacterName = characterName,
        OwnerHomeWorld = homeWorld,
        LastUpdated = "2026-06-23T12:00:00.0000000Z",
        Bags = [Bag("RetainerPage1", Slot(42, quantity, 0, 100))],
    };

    private sealed class StoreFixture
    {
        private readonly SqliteConnectionFactory connectionFactory;

        private StoreFixture(SqliteConnectionFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory;
        }

        public required InventoryReportStore Store { get; init; }
        public required long AccountId { get; init; }

        public static async Task<StoreFixture> CreateAsync(params KeyValuePair<string, string?>[] extraConfiguration)
        {
            var databasePath = CreateDatabasePath();
            var values = new Dictionary<string, string?>
            {
                ["MarketMafioso:DatabasePath"] = databasePath,
            };
            foreach (var item in extraConfiguration)
                values[item.Key] = item.Value;

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
            var environment = new TestHostEnvironment(Path.GetDirectoryName(databasePath)!);
            var connectionFactory = new SqliteConnectionFactory(configuration, environment);
            var migrator = new SqliteSchemaMigrator(connectionFactory, NullLogger<SqliteSchemaMigrator>.Instance);
            await migrator.MigrateAsync(CancellationToken.None);
            await using var connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO accounts (display_name, created_at_utc)
                VALUES ('Default', $createdAt);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            var accountId = (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;

            return new StoreFixture(connectionFactory)
            {
                Store = new InventoryReportStore(connectionFactory, configuration, NullLogger<InventoryReportStore>.Instance),
                AccountId = accountId,
            };
        }

        public async Task<long> CreateAccountAsync(string displayName)
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO accounts (display_name, created_at_utc)
                VALUES ($displayName, $createdAt);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$displayName", displayName);
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            return (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
        }

        public Task<Microsoft.Data.Sqlite.SqliteConnection> OpenConnectionAsync() =>
            connectionFactory.OpenConnectionAsync(CancellationToken.None);

        public Task MigrateAsync() => new SqliteSchemaMigrator(connectionFactory,
            NullLogger<SqliteSchemaMigrator>.Instance).MigrateAsync(CancellationToken.None);

        private static string CreateDatabasePath()
        {
            var directory = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "marketmafioso.db");
        }
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MarketMafioso.Server.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
