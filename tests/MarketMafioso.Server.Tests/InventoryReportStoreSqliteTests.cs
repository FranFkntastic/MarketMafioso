using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Tests;

public sealed class InventoryReportStoreSqliteTests
{
    [Fact]
    public async Task SaveAsync_PersistsStructuredSnapshotForAccount()
    {
        var fixture = await StoreFixture.CreateAsync();

        var stored = await fixture.Store.SaveAsync(
            fixture.AccountId,
            CreateReport("Structured Character", "Gilgamesh", 42),
            "provided",
            """{"characterName":"Structured Character"}""",
            CancellationToken.None);

        var loaded = await fixture.Store.GetAsync(fixture.AccountId, stored.Id, CancellationToken.None);
        var summaries = await fixture.Store.ListSummariesAsync(fixture.AccountId, characterId: null, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("Structured Character", loaded.Report.CharacterName);
        Assert.Equal("Gilgamesh", loaded.Report.HomeWorld);
        Assert.Single(loaded.Report.PlayerInventory);
        Assert.Equal((uint)42, loaded.Report.PlayerInventory[0].Items[0].ItemId);
        Assert.Single(summaries);
        Assert.Equal(stored.Id, summaries[0].Id);
    }

    [Fact]
    public async Task SaveAsync_UpsertsCharacterForAccount()
    {
        var fixture = await StoreFixture.CreateAsync();

        await fixture.Store.SaveAsync(
            fixture.AccountId,
            CreateReport("Character One", "Cactuar", 2),
            null,
            "{}",
            CancellationToken.None);
        await fixture.Store.SaveAsync(
            fixture.AccountId,
            CreateReport("Character One", "Cactuar", 3),
            null,
            "{}",
            CancellationToken.None);

        Assert.Equal(1, await fixture.CountAsync("characters"));
    }

    [Fact]
    public async Task ListSummariesAsync_IsScopedByAccount()
    {
        var fixture = await StoreFixture.CreateAsync();
        var otherAccountId = await fixture.CreateAccountAsync("Other");

        await fixture.Store.SaveAsync(
            fixture.AccountId,
            CreateReport("Visible Character", "Leviathan", 2),
            null,
            "{}",
            CancellationToken.None);
        await fixture.Store.SaveAsync(
            otherAccountId,
            CreateReport("Hidden Character", "Leviathan", 3),
            null,
            "{}",
            CancellationToken.None);

        var summaries = await fixture.Store.ListSummariesAsync(fixture.AccountId, characterId: null, CancellationToken.None);

        Assert.Single(summaries);
        Assert.Equal("Visible Character", summaries[0].CharacterName);
    }

    [Fact]
    public async Task SaveAsync_PrunesStructuredSnapshotsPastConfiguredRetentionCount()
    {
        var fixture = await StoreFixture.CreateAsync(
            new KeyValuePair<string, string?>("MarketMafioso:SnapshotRetentionCount", "2"));

        var first = await fixture.Store.SaveAsync(
            fixture.AccountId,
            CreateReport("Retained Character", "Leviathan", 2),
            null,
            "{}",
            CancellationToken.None);
        await Task.Delay(10);
        var second = await fixture.Store.SaveAsync(
            fixture.AccountId,
            CreateReport("Retained Character", "Leviathan", 3),
            null,
            "{}",
            CancellationToken.None);
        await Task.Delay(10);
        var third = await fixture.Store.SaveAsync(
            fixture.AccountId,
            CreateReport("Retained Character", "Leviathan", 4),
            null,
            "{}",
            CancellationToken.None);

        var summaries = await fixture.Store.ListSummariesAsync(fixture.AccountId, characterId: null, CancellationToken.None);

        Assert.Equal(2, summaries.Count);
        Assert.DoesNotContain(summaries, x => x.Id == first.Id);
        Assert.Contains(summaries, x => x.Id == second.Id);
        Assert.Contains(summaries, x => x.Id == third.Id);
        Assert.Null(await fixture.Store.GetAsync(fixture.AccountId, first.Id, CancellationToken.None));
        Assert.Equal(2, await fixture.CountAsync("snapshots"));
    }

    [Fact]
    public async Task SaveAsync_RoundTripsRetainerGilMarketListingsAndItemType()
    {
        var fixture = await StoreFixture.CreateAsync();
        var report = CreateReport("Semantic Character", "Siren", 5057) with
        {
            Retainers =
            [
                new RetainerReport
                {
                    RetainerName = "Scrongle",
                    RetainerId = 99,
                    LastUpdated = "2026-06-24T12:00:00.0000000Z",
                    Gil = 1_242_888,
                    Bags =
                    [
                        new InventoryBag
                        {
                            BagName = "RetainerInventory",
                            Location = "Retainer",
                            Items =
                            [
                                new ItemSlot
                                {
                                    ItemId = 5057,
                                    ItemName = "Darksteel Nugget",
                                    ItemType = "Metal",
                                    Quantity = 20,
                                    IsHQ = false,
                                    Condition = 100,
                                    ContainerKey = "RetainerPage3",
                                    SlotIndex = 11,
                                    ConditionPercent = 0,
                                    Equipped = false,
                                },
                            ],
                        },
                    ],
                    MarketListings =
                    [
                        new RetainerMarketListing
                        {
                            ItemId = 5057,
                            ItemName = "Darksteel Nugget",
                            ItemType = "Metal",
                            Quantity = 20,
                            IsHQ = false,
                            Condition = 100,
                            ContainerKey = "RetainerMarket",
                            SlotIndex = 4,
                            ConditionPercent = 0,
                            UnitPrice = 1_800,
                            ListedAt = "2026-06-24T12:00:00.0000000Z",
                        },
                        new RetainerMarketListing
                        {
                            ItemId = 5057,
                            ItemName = "Darksteel Nugget",
                            ItemType = "Metal",
                            Quantity = 79,
                            IsHQ = false,
                            Condition = 100,
                            UnitPrice = 2_150,
                            ListedAt = "2026-06-24T12:00:00.0000000Z",
                        },
                    ],
                },
            ],
        };

        var stored = await fixture.Store.SaveAsync(
            fixture.AccountId,
            report,
            null,
            "{}",
            CancellationToken.None);

        var loaded = await fixture.Store.GetAsync(fixture.AccountId, stored.Id, CancellationToken.None);
        var summaries = await fixture.Store.ListSummariesAsync(fixture.AccountId, characterId: null, CancellationToken.None);

        Assert.NotNull(loaded);
        var retainer = Assert.Single(loaded.Report.Retainers);
        Assert.Equal((ulong)1_242_888, retainer.Gil);
        Assert.Equal("Metal", retainer.Bags[0].Items[0].ItemType);
        Assert.Equal("Retainer", retainer.Bags[0].Location);
        Assert.Equal("RetainerPage3", retainer.Bags[0].Items[0].ContainerKey);
        Assert.Equal(11, retainer.Bags[0].Items[0].SlotIndex);
        Assert.Equal(0, retainer.Bags[0].Items[0].ConditionPercent);
        Assert.False(retainer.Bags[0].Items[0].Equipped);
        Assert.Equal("Semantic Character", retainer.OwnerCharacterName);
        Assert.Equal("Siren", retainer.OwnerHomeWorld);
        Assert.Equal(2, retainer.MarketListings.Count);
        Assert.Equal((uint)1_800, retainer.MarketListings[0].UnitPrice);
        Assert.Equal((uint)2_150, retainer.MarketListings[1].UnitPrice);
        Assert.Equal("Metal", retainer.MarketListings[0].ItemType);
        Assert.Equal("RetainerMarket", retainer.MarketListings[0].ContainerKey);
        Assert.Equal(4, retainer.MarketListings[0].SlotIndex);
        Assert.Equal(0, retainer.MarketListings[0].ConditionPercent);
        Assert.Equal(1, stored.Summary.RetainerItemStacks);
        Assert.Equal(20, stored.Summary.RetainerItemQuantity);
        var summary = Assert.Single(summaries);
        Assert.Equal(1, summary.RetainerCount);
        Assert.Equal(1, summary.RetainerItemStacks);
        Assert.Equal(20, summary.RetainerItemQuantity);
    }

    [Fact]
    public async Task ItemMetadataCatalog_SelfHealsOlderSnapshotsWithoutCrossingAccountBoundaries()
    {
        var fixture = await StoreFixture.CreateAsync();
        var otherAccountId = await fixture.CreateAccountAsync("Other");
        var missingTypeReport = WithoutItemType(CreateReport("Catalog Character", "Siren", 5057));

        var older = await fixture.Store.SaveAsync(
            fixture.AccountId,
            missingTypeReport,
            null,
            "{}",
            CancellationToken.None);
        var known = await fixture.Store.SaveAsync(
            fixture.AccountId,
            CreateReport("Catalog Character", "Siren", 5057),
            null,
            "{}",
            CancellationToken.None);

        var otherReport = WithItemType(CreateReport("Other Character", "Siren", 5057), "Other Account Type");
        var other = await fixture.Store.SaveAsync(
            otherAccountId,
            otherReport,
            null,
            "{}",
            CancellationToken.None);

        var reloadedOlder = await fixture.Store.GetAsync(fixture.AccountId, older.Id, CancellationToken.None);
        var reloadedKnown = await fixture.Store.GetAsync(fixture.AccountId, known.Id, CancellationToken.None);
        var reloadedOther = await fixture.Store.GetAsync(otherAccountId, other.Id, CancellationToken.None);

        Assert.Equal("Test Item Type", reloadedOlder!.Report.PlayerInventory[0].Items[0].ItemType);
        Assert.Equal("Test Item Type", reloadedKnown!.Report.PlayerInventory[0].Items[0].ItemType);
        Assert.Equal("Other Account Type", reloadedOther!.Report.PlayerInventory[0].Items[0].ItemType);
        Assert.Equal(2, await fixture.CountAsync("item_metadata_catalog"));
    }

    private static InventoryReport WithoutItemType(InventoryReport report) => WithItemType(report, null);

    private static InventoryReport WithItemType(InventoryReport report, string? itemType) => report with
    {
        PlayerInventory =
        [
            report.PlayerInventory[0] with
            {
                Items = [report.PlayerInventory[0].Items[0] with { ItemType = itemType }],
            },
        ],
    };

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

        public async Task<int> CountAsync(string tableName)
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
            return checked((int)(long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
        }

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
