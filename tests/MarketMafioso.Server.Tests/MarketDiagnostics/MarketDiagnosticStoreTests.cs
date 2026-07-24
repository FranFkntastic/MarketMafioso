using MarketMafioso.Server.MarketDiagnostics;
using MarketMafioso.Server.Sqlite;
using MarketMafioso.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketMafioso.Server.Tests.MarketDiagnostics;

public sealed class MarketDiagnosticStoreTests
{
    [Fact]
    public async Task SynchronizeAndRecordObservation_CreatesBoundedOneGilEpisode()
    {
        var (store, _) = await CreateStoreAsync();

        var listing = Assert.Single(await store.SynchronizeOwnedListingsAsync(CancellationToken.None));
        var detectedAt = listing.FirstObservedAtUtc.AddMinutes(5);
        var transition = await store.RecordObservationAsync(
            new MarketListingEvaluation
            {
                OwnedListing = listing,
                Classification = MarketObservationClassification.Undercut,
                ObservedAtUtc = detectedAt,
                SourceUploadedAtUtc = detectedAt.AddSeconds(-5),
                SourceAgeSeconds = 5,
                SourceFreshness = "Fresh",
                Competitor = new UniversalisListingEvidence
                {
                    ItemId = listing.ItemId,
                    ListingId = "competitor-listing",
                    RetainerId = "456",
                    RetainerName = "Mechanical",
                    UnitPrice = listing.UnitPrice - 1,
                    Quantity = 1,
                    ReviewedAtUtc = detectedAt.AddSeconds(-5),
                },
                UndercutDelta = 1,
            },
            CancellationToken.None);

        Assert.NotNull(transition);
        Assert.Equal("UndercutStarted", transition.Type);
        var episode = Assert.Single(await store.ListEpisodesAsync([1], openOnly: true, 10, CancellationToken.None));
        Assert.Equal("Mechanical", episode.CompetitorRetainerName);
        Assert.True(episode.ExactOneGil);
        Assert.Equal(0, episode.ResponseLowerBoundMs);
        Assert.True(episode.ResponseUpperBoundMs >= 300_000);
    }

    [Fact]
    public async Task RegionConditions_ArePersistedAtAccountScopedCadence()
    {
        var (store, _) = await CreateStoreAsync();
        var observedAt = DateTimeOffset.Parse("2026-07-24T14:38:33Z");
        Assert.True(await store.ShouldCollectRegionAsync(
            1,
            "North-America",
            observedAt,
            TimeSpan.FromMinutes(5),
            CancellationToken.None));

        await store.RecordRegionConditionsAsync(
            1,
            "North-America",
            new Dictionary<uint, string?> { [4745] = "Orange Juice" },
            [
                new RegionMarketCondition
                {
                    ItemId = 4745,
                    MinimumListingPrice = 99,
                    MinimumListingWorldId = 40,
                    AverageSalePrice = 121.5,
                    DailySaleVelocity = 9.8,
                    RecentPurchasePrice = 100,
                    RecentPurchaseWorldId = 79,
                    RecentPurchaseAtUtc = observedAt.AddMinutes(-1),
                    FreshestWorldUploadAtUtc = observedAt.AddSeconds(-21),
                },
                new RegionMarketCondition
                {
                    ItemId = 4745,
                    IsHq = true,
                    FreshestWorldUploadAtUtc = observedAt.AddSeconds(-21),
                },
            ],
            observedAt,
            CancellationToken.None);

        Assert.False(await store.ShouldCollectRegionAsync(
            1,
            "North-America",
            observedAt.AddMinutes(4),
            TimeSpan.FromMinutes(5),
            CancellationToken.None));
        var observations = await store.ListRegionConditionsAsync(
            [1],
            4745,
            10,
            CancellationToken.None);
        Assert.Equal(2, observations.Count);
        var nq = Assert.Single(observations, observation => !observation.IsHq);
        Assert.Equal("Orange Juice", nq.ItemName);
        Assert.Equal((uint)99, nq.MinimumListingPrice);
        Assert.Equal(21, nq.SourceAgeSeconds);
    }

    [Fact]
    public async Task FreshLocalDisappearance_CreatesUnresolvedSaleEvidence()
    {
        var (store, factory) = await CreateStoreAsync();
        Assert.Single(await store.SynchronizeOwnedListingsAsync(CancellationToken.None));
        await SeedEmptyListingSnapshotAsync(factory);

        Assert.Empty(await store.SynchronizeOwnedListingsAsync(CancellationToken.None));
        var sale = Assert.Single(await store.ListSaleEventsAsync(
            [1],
            confidence: null,
            10,
            CancellationToken.None));
        Assert.Equal("LocalListingDiff", sale.Source);
        Assert.Equal("Unresolved", sale.Confidence);
        Assert.Equal("Our Retainer", sale.RetainerName);
        Assert.Equal((uint)4745, sale.ItemId);
        Assert.Equal((uint)100, sale.UnitPrice);
    }

    [Fact]
    public async Task ConfirmedSaleEvidence_IsDeduplicatedByEvidenceId()
    {
        var (store, _) = await CreateStoreAsync();
        var evidence = new RetainerSaleEvidenceCreateRequest
        {
            EvidenceId = "same-sale",
            ItemId = 4745,
            ItemName = "Orange Juice",
            TotalGil = 95,
            EventAtUtc = DateTimeOffset.Parse("2026-07-24T12:05:00Z"),
            CharacterName = "Owner Character",
            HomeWorld = "Cactuar",
            RawMessage = "Orange Juice has sold for 95 gil (after fees).",
        };

        var first = await store.RecordConfirmedSaleAsync(
            1,
            evidence,
            evidence.EventAtUtc.AddSeconds(1),
            CancellationToken.None);
        var second = await store.RecordConfirmedSaleAsync(
            1,
            evidence,
            evidence.EventAtUtc.AddSeconds(2),
            CancellationToken.None);

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal(first.Id, second.Id);
        var sale = Assert.Single(await store.ListSaleEventsAsync(
            [1],
            "Confirmed",
            10,
            CancellationToken.None));
        Assert.Equal((ulong)95, sale.TotalGil);
        Assert.Equal("Cactuar", sale.World);
    }

    private static async Task<(MarketDiagnosticStore Store, SqliteConnectionFactory Factory)> CreateStoreAsync()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "MarketMafioso.Server.Tests",
            Guid.NewGuid().ToString("N"),
            "marketmafioso.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketMafioso:DatabasePath"] = databasePath,
            })
            .Build();
        var factory = new SqliteConnectionFactory(
            configuration,
            new TestHostEnvironment(Path.GetDirectoryName(databasePath)!));
        await new SqliteSchemaMigrator(
            factory,
            NullLogger<SqliteSchemaMigrator>.Instance)
            .MigrateAsync(CancellationToken.None);
        await SeedOwnedListingAsync(factory);
        return (new MarketDiagnosticStore(factory), factory);
    }

    private static async Task SeedOwnedListingAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts (id, display_name, created_at_utc)
            VALUES (1, 'Default', '2026-07-24T12:00:00Z');

            INSERT INTO characters (
                id, account_id, character_name, home_world, first_seen_at_utc, last_seen_at_utc
            )
            VALUES (
                1, 1, 'Owner Character', 'Cactuar', '2026-07-24T12:00:00Z', '2026-07-24T12:00:00Z'
            );

            INSERT INTO snapshots (
                id, account_id, character_id, received_at_utc, character_name, home_world,
                report_timestamp, schema_version, source_plugin, plugin_version, generated_at_utc
            )
            VALUES (
                'snapshot-1', 1, 1, '2026-07-24T12:00:00Z', 'Owner Character', 'Cactuar',
                '2026-07-24T12:00:00Z', 1, 'MarketMafioso', '1.0.0', '2026-07-24T12:00:00Z'
            );

            INSERT INTO inventory_owners (
                id, snapshot_id, owner_type, owner_name, retainer_id, last_updated, gil,
                requested_sources_json, observed_sources_json, listings_observed_at_utc, sort_order
            )
            VALUES (
                1, 'snapshot-1', 'retainer', 'Our Retainer', 123, '2026-07-24T12:00:00Z', 0,
                '[]', '[]', '2026-07-24T12:00:00Z', 0
            );

            INSERT INTO retainer_market_listings (
                owner_id, item_id, item_name, quantity, is_hq, condition, container_key,
                slot_index, unit_price, listed_at, sort_order
            )
            VALUES (
                1, 4745, 'Orange Juice', 1, 0, 0, 'RetainerMarket',
                0, 100, '2026-07-24T12:00:00Z', 0
            );
            """;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task SeedEmptyListingSnapshotAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO snapshots (
                id, account_id, character_id, received_at_utc, character_name, home_world,
                report_timestamp, schema_version, source_plugin, plugin_version, generated_at_utc
            )
            VALUES (
                'snapshot-2', 1, 1, '2026-07-24T12:10:00Z', 'Owner Character', 'Cactuar',
                '2026-07-24T12:10:00Z', 1, 'MarketMafioso', '1.0.0', '2026-07-24T12:10:00Z'
            );

            INSERT INTO inventory_owners (
                id, snapshot_id, owner_type, owner_name, retainer_id, last_updated, gil,
                requested_sources_json, observed_sources_json, listings_observed_at_utc, sort_order
            )
            VALUES (
                2, 'snapshot-2', 'retainer', 'Our Retainer', 123, '2026-07-24T12:10:00Z', 0,
                '[]', '[]', '2026-07-24T12:10:00Z', 0
            );
            """;
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
