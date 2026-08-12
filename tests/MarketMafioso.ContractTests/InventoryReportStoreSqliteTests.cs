using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MarketMafioso.Contracts.Inventory;
using MarketMafioso.Dashboard.Components.Inventory;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.ContractTests;

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

        await VerifyOptionalStowageProjectionAsync(fixture);
    }

    private static async Task VerifyOptionalStowageProjectionAsync(StoreFixture fixture)
    {
        var report = CreateReport("Stowage Character", "Maduin", 100) with
        {
            RetainerManagement = new QuartermasterStowageReport
            {
                ProviderInstanceId = "quartermaster-a",
                Revision = 7,
                Owner = new QuartermasterStowageOwner { LocalContentId = 100, HomeWorldId = 40 },
                Plans =
                [
                    new QuartermasterStowagePlanReport
                    {
                        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        Revision = 3,
                        Name = "General",
                        Enabled = true,
                        Rules =
                        [
                            new QuartermasterStowageRuleReport
                            {
                                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                                ItemId = 100,
                                DesiredPlayerQuantity = 10,
                                Quality = "Any",
                                Action = "deposit",
                                Quantity = 4,
                                PlayerQuantity = 14,
                            },
                        ],
                    },
                ],
            },
        };

        var stored = await fixture.Store.SaveAsync(
            fixture.AccountId,
            report,
            "provided",
            "{}",
            CancellationToken.None);

        var loaded = await fixture.Store.GetAsync(fixture.AccountId, stored.Id, CancellationToken.None);

        var plan = Assert.Single(loaded!.Report.RetainerManagement!.Plans);
        Assert.Equal("General", plan.Name);
        Assert.Equal("deposit", Assert.Single(plan.Rules).Action);

        var view = InventoryBrowserViewBuilder.Build(
            loaded,
            "item 100",
            mode: InventoryBrowserMode.Items);
        var item = Assert.Single(view.Items);
        Assert.Equal("deposit", item.Stowage!.Action);
        Assert.All(view.Stacks, stack => Assert.Equal(item.Stowage, stack.Stowage));
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
    public async Task SaveAsync_DoesNotCreateCharacterFromIncompleteIdentity()
    {
        var fixture = await StoreFixture.CreateAsync();
        var report = CreateReport("Character One", "Siren", 2) with { HomeWorld = null };

        await fixture.Store.SaveAsync(fixture.AccountId, report, null, "{}", CancellationToken.None);
        await fixture.Store.SaveAsync(fixture.AccountId, report, null, "{}", CancellationToken.None);

        Assert.Equal(0, await fixture.CountAsync("characters"));
        Assert.Empty(await fixture.Store.ListCharactersAsync(fixture.AccountId, CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_IncompleteIdentityReusesUniqueCompleteCharacterWithoutErasingAccountNumber()
    {
        var fixture = await StoreFixture.CreateAsync();
        var complete = CreateReport("Character One", "Siren", 2) with { ServiceAccountNumber = 2 };
        var incomplete = CreateReport("Character One", "Siren", 3) with { HomeWorld = null };

        await fixture.Store.SaveAsync(fixture.AccountId, complete, null, "{}", CancellationToken.None);
        await fixture.Store.SaveAsync(fixture.AccountId, incomplete, null, "{}", CancellationToken.None);

        Assert.Equal(1, await fixture.CountAsync("characters"));
        var character = Assert.Single(await fixture.Store.ListCharactersAsync(fixture.AccountId, CancellationToken.None));
        Assert.Equal("Siren", character.HomeWorld);
        Assert.Equal(2, character.ServiceAccountNumber);
    }

    [Fact]
    public async Task SaveAsync_IncompleteIdentityWithConflictingAccountNumberStaysUnlinked()
    {
        var fixture = await StoreFixture.CreateAsync();
        var complete = CreateReport("Character One", "Siren", 2) with { ServiceAccountNumber = 1 };
        var incomplete = CreateReport("Character One", "Siren", 3) with
        {
            HomeWorld = null,
            ServiceAccountNumber = 2,
        };

        await fixture.Store.SaveAsync(fixture.AccountId, complete, null, "{}", CancellationToken.None);
        var stored = await fixture.Store.SaveAsync(fixture.AccountId, incomplete, null, "{}", CancellationToken.None);

        await using var connection = await fixture.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT character_id FROM snapshots WHERE id = $id";
        command.Parameters.AddWithValue("$id", stored.Id);
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_LegacyReportCannotEraseConfirmedAccountNumber()
    {
        var fixture = await StoreFixture.CreateAsync();
        var current = CreateReport("Character One", "Siren", 2) with { ServiceAccountNumber = 2 };
        var legacy = CreateReport("Character One", "Siren", 3) with
        {
            Metadata = new InventoryReportMetadata
            {
                SchemaVersion = 4,
                SourcePlugin = "MarketMafioso",
                PluginVersion = "legacy",
            },
            ServiceAccountKey = "legacy-profile-key",
            ServiceAccountNumber = null,
        };

        await fixture.Store.SaveAsync(fixture.AccountId, current, null, "{}", CancellationToken.None);
        await fixture.Store.SaveAsync(fixture.AccountId, legacy, null, "{}", CancellationToken.None);

        var character = Assert.Single(await fixture.Store.ListCharactersAsync(fixture.AccountId, CancellationToken.None));
        Assert.Equal(2, character.ServiceAccountNumber);
        Assert.Equal("legacy-profile-key", character.ServiceAccountKey);
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
    public async Task GetLatestAsync_AcrossAccountsReturnsTheNewestVisibleSnapshot()
    {
        var fixture = await StoreFixture.CreateAsync();
        var otherAccountId = await fixture.CreateAccountAsync("Other");
        var older = await fixture.Store.SaveAsync(
            fixture.AccountId,
            CreateReport("Older Character", "Leviathan", 2),
            null,
            "{}",
            CancellationToken.None);
        await fixture.SetReceivedAtAsync(older.Id, DateTimeOffset.UtcNow.AddMinutes(-1));
        var newer = await fixture.Store.SaveAsync(
            otherAccountId,
            CreateReport("Newer Character", "Siren", 3),
            null,
            "{}",
            CancellationToken.None);

        var latest = await fixture.Store.GetLatestAsync(
            [fixture.AccountId, otherAccountId],
            characterId: null,
            CancellationToken.None);
        var selected = await fixture.Store.GetAsync(
            [fixture.AccountId, otherAccountId],
            newer.Id,
            CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(newer.Id, latest.Id);
        Assert.NotEqual(older.Id, latest.Id);
        Assert.NotNull(selected);
        Assert.Equal("Newer Character", selected.Report.CharacterName);
    }

    [Fact]
    public async Task GetLatestByCharacterAsync_BuildsAuthorizedAllKnownInventoryWithoutPhysicalStackSpam()
    {
        var fixture = await StoreFixture.CreateAsync();
        var hiddenAccountId = await fixture.CreateAccountAsync("Hidden");
        var older = await fixture.Store.SaveAsync(
            fixture.AccountId,
            CreateReport("Eriana Ning", "Siren", 999),
            null,
            "{}",
            CancellationToken.None);
        await fixture.SetReceivedAtAsync(older.Id, DateTimeOffset.UtcNow.AddMinutes(-5));

        var eriana = CreateReport("Eriana Ning", "Siren", 42) with
        {
            PlayerInventory =
            [
                Bag("Inventory1", Slot(42, 2, 0, 25)),
                Bag("Inventory3", Slot(42, 3, 8, 50)),
            ],
            Retainers = [Retainer("Shared Name", 1001, "Eriana Ning", "Siren", 4)],
        };
        var wei = CreateReport("Wei Ning", "Siren", 42) with
        {
            PlayerInventory = [Bag("Inventory2", Slot(42, 6, 2, 75))],
            Retainers = [Retainer("Shared Name", 2001, "Wei Ning", "Siren", 7)],
        };
        var erianaStored = await fixture.Store.SaveAsync(fixture.AccountId, eriana, null, "{}", CancellationToken.None);
        var weiStored = await fixture.Store.SaveAsync(fixture.AccountId, wei, null, "{}", CancellationToken.None);
        await fixture.Store.SaveAsync(
            hiddenAccountId,
            CreateReport("Hidden Character", "Siren", 42),
            null,
            "{}",
            CancellationToken.None);

        var latest = await fixture.Store.GetLatestByCharacterAsync([fixture.AccountId], CancellationToken.None);
        var view = InventoryBrowserViewBuilder.Build(latest, "Item 42", mode: InventoryBrowserMode.Items);
        var grouped = Assert.Single(InventoryTableProjection.GroupedInventory(view.Stacks, new InventoryTableQueryState()));

        Assert.Equal(2, latest.Count);
        Assert.Contains(latest, report => report.Id == erianaStored.Id);
        Assert.Contains(latest, report => report.Id == weiStored.Id);
        Assert.DoesNotContain(latest, report => report.Id == older.Id);
        Assert.Null(view.SnapshotId);
        Assert.Null(view.CharacterName);
        Assert.Equal(4, view.Scopes.Count);
        Assert.Equal(4, view.Scopes.Select(scope => scope.ScopeKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(view.Scopes, scope => Assert.False(string.IsNullOrWhiteSpace(scope.OwnerCharacterName)));
        Assert.Equal(22, grouped.TotalQuantity);
        Assert.Equal(4, grouped.Locations.Count);
        var erianaPlayer = Assert.Single(grouped.Locations, location =>
            location.OwnerLabel == "Eriana Ning @ Siren" && location.ContextLabel == "Player inventory");
        Assert.Equal(5, erianaPlayer.Quantity);
        Assert.Equal(2, erianaPlayer.Stacks.Count);
        Assert.Contains("bag 1", InventoryDisplayFormatter.FormatStackStorage(erianaPlayer.Stacks[0]), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("slot 1", InventoryDisplayFormatter.FormatStackStorage(erianaPlayer.Stacks[0]), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Owner", Enum.GetNames<InventoryGroupedColumn>());
        Assert.DoesNotContain("Condition", Enum.GetNames<InventoryGroupedColumn>());
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
        await fixture.SetReceivedAtAsync(first.Id, DateTimeOffset.UtcNow.AddMinutes(-2));
        var second = await fixture.Store.SaveAsync(
            fixture.AccountId,
            CreateReport("Retained Character", "Leviathan", 3),
            null,
            "{}",
            CancellationToken.None);
        await fixture.SetReceivedAtAsync(second.Id, DateTimeOffset.UtcNow.AddMinutes(-1));
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
    public async Task SaveAsync_SerializesConcurrentInventoryWrites()
    {
        var fixture = await StoreFixture.CreateAsync(
            new KeyValuePair<string, string?>("MarketMafioso:SnapshotRetentionCount", "8"));

        var writes = Enumerable.Range(0, 16)
            .Select(index => fixture.Store.SaveAsync(
                fixture.AccountId,
                CreateReport("Burst Character", "Siren", checked((uint)(1000 + index))),
                null,
                "{}",
                CancellationToken.None))
            .ToArray();

        var stored = await Task.WhenAll(writes);

        Assert.Equal(16, stored.Select(snapshot => snapshot.Id).Distinct().Count());
        Assert.Equal(8, await fixture.CountAsync("snapshots"));
    }

    [Fact]
    public async Task SaveAsync_CompletesWhileAReaderTransactionRemainsOpen()
    {
        var fixture = await StoreFixture.CreateAsync();
        await fixture.Store.SaveAsync(
            fixture.AccountId,
            CreateReport("Reader Character", "Siren", 1000),
            null,
            "{}",
            CancellationToken.None);

        Task<StoredInventoryReport> write;
        bool completedWithReaderOpen;
        await using (var connection = await fixture.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM snapshots LIMIT 1";
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            Assert.True(await reader.ReadAsync(CancellationToken.None));

            write = fixture.Store.SaveAsync(
                fixture.AccountId,
                CreateReport("Writer Character", "Siren", 1001),
                null,
                "{}",
                CancellationToken.None);
            completedWithReaderOpen = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(2))) == write;
        }

        await write;
        Assert.True(completedWithReaderOpen);
    }

    [Fact]
    public async Task SaveAsync_RoundTripsRetainerGilMarketListingsAndItemType()
    {
        var fixture = await StoreFixture.CreateAsync();
        var report = CreateReport("Semantic Character", "Siren", 5057) with
        {
            ServiceAccountKey = "profile-a-service-account-0",
            ServiceAccountNumber = 2,
            PlayerGil = 560_530_934,
            PlayerStorage = new StorageSourceEvidence { RequestedSources = ["Inventory1", "Crystals"], ObservedSources = ["Inventory1"] },
            Retainers =
            [
                new RetainerReport
                {
                    RetainerName = "Scrongle",
                    RetainerId = 99,
                    LastUpdated = "2026-06-24T12:00:00.0000000Z",
                    Gil = 1_242_888,
                    GilObservedAtUtc = "2026-06-24T11:58:00.0000000Z",
                    ListingsObservedAtUtc = "2026-06-24T11:59:00.0000000Z",
                    Storage = new StorageSourceEvidence { RequestedSources = ["RetainerPage1", "RetainerMarket"], ObservedSources = ["RetainerPage1"] },
                    Bags =
                    [
                        new InventoryBag
                        {
                            BagName = "RetainerInventory",
                            Location = "Retainer",
                            ObservedAtUtc = "2026-06-24T11:57:00.0000000Z",
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

        Assert.NotNull(loaded);
        Assert.Equal("profile-a-service-account-0", loaded.Report.ServiceAccountKey);
        Assert.Equal(2, loaded.Report.ServiceAccountNumber);
        Assert.Equal((ulong)560_530_934, loaded.Report.PlayerGil);
        Assert.Equal(["Inventory1", "Crystals"], loaded.Report.PlayerStorage.RequestedSources);
        Assert.Equal(["Inventory1"], loaded.Report.PlayerStorage.ObservedSources);
        var summaries = await fixture.Store.ListSummariesAsync(fixture.AccountId, characterId: null, CancellationToken.None);

        Assert.NotNull(loaded);
        var retainer = Assert.Single(loaded.Report.Retainers);
        Assert.Equal((ulong)1_242_888, retainer.Gil);
        Assert.Equal("2026-06-24T11:58:00.0000000Z", retainer.GilObservedAtUtc);
        Assert.Equal("2026-06-24T11:59:00.0000000Z", retainer.ListingsObservedAtUtc);
        Assert.Equal(["RetainerPage1", "RetainerMarket"], retainer.Storage.RequestedSources);
        Assert.Equal(["RetainerPage1"], retainer.Storage.ObservedSources);
        Assert.Equal("Metal", retainer.Bags[0].Items[0].ItemType);
        Assert.Equal("Retainer", retainer.Bags[0].Location);
        Assert.Equal("2026-06-24T11:57:00.0000000Z", retainer.Bags[0].ObservedAtUtc);
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

        public async Task<int> CountAsync(string tableName)
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
            return checked((int)(long)(await command.ExecuteScalarAsync(CancellationToken.None))!);
        }

        public async Task SetReceivedAtAsync(string snapshotId, DateTimeOffset receivedAt)
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE snapshots SET received_at_utc = $receivedAt WHERE id = $id";
            command.Parameters.AddWithValue("$receivedAt", receivedAt.ToString("O"));
            command.Parameters.AddWithValue("$id", snapshotId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
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
