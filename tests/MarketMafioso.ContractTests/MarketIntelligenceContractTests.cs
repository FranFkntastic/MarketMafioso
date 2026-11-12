using MarketMafioso.Contracts.MarketIntelligence;
using MarketMafioso.Server.MarketIntelligence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace MarketMafioso.Server.ContractTests;

public sealed class MarketIntelligenceContractTests
{

    [Fact]
    public async Task ActorEvidenceIsOpaqueRebuildableAndAccountScoped()
    {
        var configuration = ServerTestHost.CreateConfiguration();
        await using var application = ServerTestHost.Create(configuration);
        using var client = application.CreateClient();
        var store = application.Services.GetRequiredService<MarketIntelligenceStore>();
        await AddAccountAsync(configuration.DatabasePath, 2);

        var firstReceipt = await store.IngestAsync(1, ActorBook("actors-one", DateTimeOffset.UtcNow), CancellationToken.None);
        await store.IngestAsync(2, ActorBook("actors-two", DateTimeOffset.UtcNow) with
        {
            IdempotencyKey = "actors-two",
            OccurrenceId = "actors-two",
        }, CancellationToken.None);
        await store.ProjectPendingAsync(CancellationToken.None);

        var row = Assert.Single((await store.GetLedgerAsync([1], CancellationToken.None)).Rows);
        Assert.Equal(2, row.DistinctSellerOwners);
        Assert.Equal(2, row.DistinctArtisans);
        Assert.Equal(1, row.SellerOwnerIdentityCoverage);
        Assert.Equal(1, row.ArtisanIdentityCoverage);
        Assert.Equal(1, row.MultiRetainerOwnerCount);
        Assert.Equal(.8, row.SelfCraftedListingShare, 3);
        Assert.Contains(row.Findings, finding => finding.Kind == "SellerOwnerConcentration");
        Assert.Contains(row.Findings, finding => finding.Kind == "ProducerConcentration");
        Assert.Contains(row.Findings, finding => finding.Kind == "MultiRetainerOwner");
        Assert.Contains(row.Findings, finding => finding.Kind == "SelfCraftedSupply");

        var detail = await store.GetDetailAsync([1], 5060, "Diabolos", CancellationToken.None);
        var listing = Assert.Single(detail!.Observations).Listings[0];
        Assert.StartsWith("actor-a1-", listing.SellerOwnerActorKey);
        Assert.Equal(MarketActorIdentityStates.Observed, listing.SellerOwnerIdentityState);
        Assert.Equal(MarketActorIdentityStates.Observed, listing.ArtisanIdentityState);

        var name = new MarketActorNameObservationUploadRequest
        {
            IdempotencyKey = "known-wei-name",
            ContentId = 100,
            Name = "Known Maker",
            ResolutionMethod = "ControlledFixture",
            ObservedAtUtc = DateTimeOffset.UtcNow,
            SourceObservationId = firstReceipt.ObservationId,
        };
        var nameReceipt = await store.RecordActorNameAsync(1, name, CancellationToken.None);
        var retry = await store.RecordActorNameAsync(1, name, CancellationToken.None);
        Assert.True(retry.Duplicate);
        var laterNameReceipt = await store.RecordActorNameAsync(1, name with
        {
            IdempotencyKey = "known-wei-name-later",
            ObservedAtUtc = name.ObservedAtUtc.AddMinutes(1),
        }, CancellationToken.None);
        Assert.False(laterNameReceipt.Duplicate);
        await Assert.ThrowsAsync<ArgumentException>(() => store.RecordActorNameAsync(2, name with
        {
            IdempotencyKey = "foreign-source-name",
        }, CancellationToken.None));
        var actor = await store.GetActorDetailAsync([1], nameReceipt.ActorKey, CancellationToken.None);
        Assert.Equal("Known Maker", actor!.Actor.CurrentName);
        Assert.Equal(2, actor.Names.Count);
        Assert.Equal(2, actor.Actor.RetainerCount);
        Assert.NotEmpty(actor.Listings);
        Assert.Null(await store.GetActorDetailAsync([2], nameReceipt.ActorKey, CancellationToken.None));

        var accountTwoDetail = await store.GetDetailAsync([2], 5060, "Diabolos", CancellationToken.None);
        Assert.NotEqual(listing.SellerOwnerActorKey, accountTwoDetail!.Observations[0].Listings[0].SellerOwnerActorKey);
        var response = await client.GetAsync($"/api/market-intelligence/markets/Diabolos/5060");
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sellerOwnerContentId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("artisanContentId", json, StringComparison.OrdinalIgnoreCase);

        await using (var connection = new SqliteConnection($"Data Source={configuration.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var payload = connection.CreateCommand();
            payload.CommandText = "SELECT listings_json FROM market_evidence_payloads WHERE payload_hash = $hash";
            payload.Parameters.AddWithValue("$hash", firstReceipt.PayloadHash);
            var storedJson = Assert.IsType<string>(await payload.ExecuteScalarAsync());
            Assert.DoesNotContain("contentId", storedJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("actor-a1-", storedJson, StringComparison.Ordinal);

            await using var keyScope = connection.CreateCommand();
            keyScope.CommandText = "SELECT key_scheme, length(key_material) FROM market_actor_key_scopes WHERE account_id = 1";
            await using var keyReader = await keyScope.ExecuteReaderAsync();
            Assert.True(await keyReader.ReadAsync());
            Assert.Equal(MarketIntelligenceStore.ActorKeyScheme, keyReader.GetString(0));
            Assert.Equal(32, keyReader.GetInt32(1));
        }

        var rebuilt = await store.RebuildAccountAsync(1, "market-intelligence-v2-rebuild", false, CancellationToken.None);
        Assert.True(rebuilt > 0);
        Assert.Equal(2, Assert.Single((await store.GetLedgerAsync([1], CancellationToken.None)).Rows).DistinctSellerOwners);
    }

    private static MarketEvidenceUploadRequest ActorBook(string id, DateTimeOffset observedAt) => new()
    {
        SchemaVersion = 2,
        IdempotencyKey = id,
        OccurrenceId = id,
        SourceKind = MarketEvidenceSources.MarketAcquisition,
        SourceVersion = "controlled-v2",
        ItemId = 5060,
        ItemName = "Darksteel Ingot",
        DataCenter = "Crystal",
        WorldName = "Diabolos",
        ObservedAtUtc = observedAt,
        Coverage = MarketEvidenceCoverage.Complete,
        ReportedListingCount = 10,
        ListingCapacity = 100,
        IsTruncated = false,
        Listings = Enumerable.Range(1, 10).Select(index => new MarketEvidenceUploadListing
        {
            ListingId = $"actor-listing-{index}",
            RetainerId = index <= 4 ? "retainer-a" : index <= 8 ? "retainer-b" : "retainer-c",
            RetainerName = index <= 4 ? "Known A" : index <= 8 ? "Known B" : "Known C",
            RetainerNameSource = "ControlledFixture",
            SellerOwnerContentId = index <= 8 ? 100UL : 200UL,
            ArtisanContentId = index <= 8 ? 100UL : 300UL,
            Quantity = 99,
            UnitPrice = 6000,
            IsHq = index % 2 == 0,
        }).ToArray(),
    };

    private static async Task AddAccountAsync(string databasePath, long accountId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO accounts(id, display_name, created_at_utc) VALUES($id, $name, $now)";
        command.Parameters.AddWithValue("$id", accountId);
        command.Parameters.AddWithValue("$name", $"Account {accountId}");
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

}
