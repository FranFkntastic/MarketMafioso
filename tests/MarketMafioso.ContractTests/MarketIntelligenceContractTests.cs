using MarketMafioso.Contracts.MarketIntelligence;
using MarketMafioso.Server.Auth;
using MarketMafioso.Server.MarketIntelligence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace MarketMafioso.Server.ContractTests;

public sealed class MarketIntelligenceContractTests
{
    [Fact]
    public async Task PluginIngestBypassesDashboardSessionButLedgerDoesNot()
    {
        await using var application = ServerTestHost.Create(host =>
        {
            host.Configuration["MarketMafioso:RequireDashboardAuth"] = "true";
            host.Configuration["MarketMafioso:DashboardBootstrapUsername"] = "admin";
            host.Configuration["MarketMafioso:DashboardBootstrapPassword"] = "secret-password";
            host.Configuration["MarketMafioso:ClientApiKey"] = "ingest-key";
        });
        using var client = application.CreateClient();
        var acquisitionCredential = await application.Services
            .GetRequiredService<WorkshopHostCredentialStore>()
            .CreateAsync(
                1,
                "Primary acquisition",
                WorkshopHostCredentialPurposes.CraftArchitect,
                CancellationToken.None);

        using var ingestRequest = new HttpRequestMessage(HttpMethod.Post, "/api/market-intelligence/evidence")
        {
            Content = JsonContent.Create(Book("http-ingest", DateTimeOffset.UtcNow)),
        };
        ingestRequest.Headers.Add("X-Api-Key", acquisitionCredential.Secret);
        var ingest = await client.SendAsync(ingestRequest);
        using var invalidKeyRequest = new HttpRequestMessage(HttpMethod.Post, "/api/market-intelligence/evidence")
        {
            Content = JsonContent.Create(Book("invalid-key-ingest", DateTimeOffset.UtcNow)),
        };
        invalidKeyRequest.Headers.Add("X-Api-Key", "wrong-key");
        var invalidKeyIngest = await client.SendAsync(invalidKeyRequest);
        var anonymousIngest = await client.PostAsJsonAsync(
            "/api/market-intelligence/evidence",
            Book("anonymous-ingest", DateTimeOffset.UtcNow));
        var ledger = await client.GetAsync("/api/market-intelligence/ledger");

        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidKeyIngest.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousIngest.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ledger.StatusCode);
    }

    [Fact]
    public async Task SignedInUserWithoutAccountMappingCannotFallBackToAnotherAccountsEvidence()
    {
        var configuration = ServerTestHost.CreateConfiguration();
        configuration.Configuration["MarketMafioso:RequireDashboardAuth"] = "true";
        configuration.Configuration["MarketMafioso:DashboardBootstrapUsername"] = "admin";
        configuration.Configuration["MarketMafioso:DashboardBootstrapPassword"] = "secret-password";
        await using var application = ServerTestHost.Create(configuration);
        using var client = application.CreateClient();
        var store = application.Services.GetRequiredService<MarketIntelligenceStore>();
        await store.IngestAsync(1, Book("private-account-one", DateTimeOffset.UtcNow), CancellationToken.None);
        await store.ProjectPendingAsync(CancellationToken.None);
        await AddDashboardUserWithoutAccountAsync(configuration.DatabasePath, "unmapped", "unmapped-password");

        (await client.PostAsJsonAsync("/auth/login", new { username = "unmapped", password = "unmapped-password" })).EnsureSuccessStatusCode();
        var ledger = await client.GetFromJsonAsync<MarketIntelligenceLedgerView>("/api/market-intelligence/ledger");
        var detail = await client.GetAsync("/api/market-intelligence/markets/Diabolos/5060");
        var annotation = await client.PutAsJsonAsync("/api/market-intelligence/markets/Diabolos/5060/annotation", new MarketIntelligenceAnnotationUpdate());

        Assert.Empty(ledger!.Rows);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, annotation.StatusCode);
    }

    [Fact]
    public async Task RepeatedIndustrialBooksAreEvidenceBackedAndIdempotent()
    {
        var configuration = ServerTestHost.CreateConfiguration();
        await using var application = ServerTestHost.Create(configuration);
        using var client = application.CreateClient();
        var store = application.Services.GetRequiredService<MarketIntelligenceStore>();

        var first = Book("first", new DateTimeOffset(2026, 7, 2, 20, 33, 53, TimeSpan.Zero));
        var firstReceipt = await store.IngestAsync(1, first, CancellationToken.None);
        var retryReceipt = await store.IngestAsync(1, first, CancellationToken.None);
        var occurrenceRetry = await store.IngestAsync(1, first with { IdempotencyKey = "retry-with-new-delivery-key" }, CancellationToken.None);
        var samePayloadLater = await store.IngestAsync(1, first with
        {
            IdempotencyKey = "second-delivery",
            OccurrenceId = "route-two",
            ObservedAtUtc = new DateTimeOffset(2026, 7, 3, 20, 15, 40, TimeSpan.Zero),
        }, CancellationToken.None);
        await store.IngestAsync(1, first with
        {
            IdempotencyKey = "third-delivery",
            OccurrenceId = "route-three",
            ObservedAtUtc = new DateTimeOffset(2026, 7, 25, 21, 1, 41, TimeSpan.Zero),
        }, CancellationToken.None);
        await store.ProjectPendingAsync(CancellationToken.None);

        var ledger = await store.GetLedgerAsync([1], CancellationToken.None);
        var row = Assert.Single(ledger.Rows);
        Assert.False(firstReceipt.Duplicate);
        Assert.True(retryReceipt.Duplicate);
        Assert.True(occurrenceRetry.Duplicate);
        Assert.Equal(firstReceipt.ObservationId, retryReceipt.ObservationId);
        Assert.Equal(firstReceipt.ObservationId, occurrenceRetry.ObservationId);
        Assert.NotEqual(firstReceipt.ObservationId, samePayloadLater.ObservationId);
        Assert.Equal(firstReceipt.PayloadHash, samePayloadLater.PayloadHash);
        Assert.Equal(3, row.ObservationCount);
        Assert.Equal(3, row.DistinctDays);
        Assert.Equal(100, row.VisibleListings);
        Assert.Equal(7, row.DistinctSellers);
        Assert.Equal(.99, row.FullStackShare, 3);
        Assert.Contains(row.Findings, x => x.Kind == "DeepDominantShelf");
        Assert.Contains(row.Findings, x => x.Kind == "BulkShelfDominance");
        Assert.Contains(row.Findings, x => x.Kind == "RepeatedBulkShelfDominance" && x.ObservationIds.Count == 3);
        Assert.Contains(row.Findings, x => x.Kind == "SellerPersistence" && x.ObservationIds.Count == 3);
    }

    [Fact]
    public async Task AccountsAnnotationsAndSourceStatesRemainSeparate()
    {
        var configuration = ServerTestHost.CreateConfiguration();
        await using var application = ServerTestHost.Create(configuration);
        _ = application.CreateClient();
        var store = application.Services.GetRequiredService<MarketIntelligenceStore>();
        await AddAccountAsync(configuration.DatabasePath, 2);

        await store.IngestAsync(1, Book("account-one", DateTimeOffset.UtcNow), CancellationToken.None);
        await store.IngestAsync(2, new MarketEvidenceUploadRequest
        {
            IdempotencyKey = "universalis-one",
            OccurrenceId = "universalis-occurrence",
            SourceKind = MarketEvidenceSources.Universalis,
            SourceVersion = "fixture-v1",
            ItemId = 999,
            ItemName = "Future Fixture",
            DataCenter = "Aether",
            WorldName = "Siren",
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Coverage = MarketEvidenceCoverage.AggregateOnly,
            Aggregate = new MarketEvidenceAggregate
            {
                VisibleListingCount = 321,
                VisibleQuantity = 12_345,
                LowestUnitPrice = 456,
                HighestUnitPrice = 789,
            },
        }, CancellationToken.None);
        await store.UpdateAnnotationAsync(1, 5060, "Diabolos", new MarketIntelligenceAnnotationUpdate
        {
            Note = "Industrial supply candidate",
            Reviewed = true,
        }, CancellationToken.None);
        await store.ProjectPendingAsync(CancellationToken.None);

        var firstAccount = await store.GetLedgerAsync([1], CancellationToken.None);
        var secondAccount = await store.GetLedgerAsync([2], CancellationToken.None);
        var firstRow = Assert.Single(firstAccount.Rows);
        var secondRow = Assert.Single(secondAccount.Rows);
        Assert.Equal("Darksteel Ingot", firstRow.ItemName);
        Assert.Equal("Industrial supply candidate", firstRow.Note);
        Assert.True(firstRow.Reviewed);
        Assert.Equal("Future Fixture", secondRow.ItemName);
        Assert.Equal(MarketEvidenceCoverage.AggregateOnly, secondRow.LatestCoverage);
        Assert.Equal(321, secondRow.VisibleListings);
        Assert.Equal((uint)456, secondRow.LowestUnitPrice);
        Assert.Empty(secondRow.Findings);
        Assert.DoesNotContain(firstAccount.Rows, x => x.ItemId == 999);
        Assert.DoesNotContain(secondAccount.Rows, x => x.ItemId == 5060);
    }

    [Fact]
    public async Task FailedClassifierGenerationCannotReplaceCurrentAndRetryPublishesAtomically()
    {
        var configuration = ServerTestHost.CreateConfiguration();
        await using var application = ServerTestHost.Create(configuration);
        _ = application.CreateClient();
        var store = application.Services.GetRequiredService<MarketIntelligenceStore>();
        await store.IngestAsync(1, Book("versioned", DateTimeOffset.UtcNow), CancellationToken.None);
        await store.ProjectPendingAsync(CancellationToken.None);

        var v1 = await store.GetLedgerAsync([1], CancellationToken.None);
        var afterFailure = await store.RebuildAccountAsync(1, "market-intelligence-v2", true, CancellationToken.None);
        var stillV1 = await store.GetLedgerAsync([1], CancellationToken.None);
        var afterRetry = await store.RebuildAccountAsync(1, "market-intelligence-v2", false, CancellationToken.None);
        var reconciledRead = await store.GetLedgerAsync([1], CancellationToken.None);

        Assert.Equal(v1.Revision, afterFailure);
        Assert.Equal(MarketIntelligenceStore.ClassifierVersion, stillV1.ClassifierVersion);
        Assert.Equal(v1.Revision, stillV1.Revision);
        Assert.True(afterRetry > afterFailure);
        Assert.Equal("market-intelligence-v2", reconciledRead.ClassifierVersion);
        Assert.Equal(afterRetry, reconciledRead.Revision);
        Assert.All(Assert.Single(reconciledRead.Rows).Findings, finding => Assert.Equal("market-intelligence-v2", finding.ClassifierVersion));
    }

    [Fact]
    public async Task SourceShapesAndCoverageStatesCannotManufactureUnsupportedFindings()
    {
        var configuration = ServerTestHost.CreateConfiguration();
        await using var application = ServerTestHost.Create(configuration);
        using var client = application.CreateClient();
        var store = application.Services.GetRequiredService<MarketIntelligenceStore>();
        var now = DateTimeOffset.UtcNow;
        var local = Book("source-local", now) with { ItemId = 700, ItemName = "Mixed Evidence", WorldName = "Siren", DataCenter = "Aether", Listings = Book("x", now).Listings.Take(1).ToArray(), ReportedListingCount = 1, ListingCapacity = 100 };
        var evidence = new[]
        {
            local with { SourceKind = MarketEvidenceSources.MarketAcquisition },
            local with { IdempotencyKey = "source-passive", OccurrenceId = "source-passive", SourceKind = MarketEvidenceSources.PassiveMarketBoard, Coverage = MarketEvidenceCoverage.Partial, IsTruncated = true },
            local with { IdempotencyKey = "source-legacy-missing", OccurrenceId = "source-legacy-missing", SourceKind = MarketEvidenceSources.LegacyRouteImport, Coverage = MarketEvidenceCoverage.LegacyMissing },
            local with { IdempotencyKey = "source-empty", OccurrenceId = "source-empty", Coverage = MarketEvidenceCoverage.Empty, Listings = [], ReportedListingCount = 0 },
            local with { IdempotencyKey = "source-unavailable", OccurrenceId = "source-unavailable", Coverage = MarketEvidenceCoverage.Unavailable, Listings = [], ReportedListingCount = null },
            local with { IdempotencyKey = "source-universalis", OccurrenceId = "source-universalis", SourceKind = MarketEvidenceSources.Universalis, Coverage = MarketEvidenceCoverage.AggregateOnly, Listings = [], Aggregate = Aggregate(410) },
            local with { IdempotencyKey = "source-saddlebag", OccurrenceId = "source-saddlebag", SourceKind = MarketEvidenceSources.SaddlebagExchange, Coverage = MarketEvidenceCoverage.AggregateOnly, Listings = [], Aggregate = Aggregate(420) },
        };
        foreach (var item in evidence)
            await store.IngestAsync(1, item, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => store.IngestAsync(1, local with
        {
            IdempotencyKey = "invalid-saddlebag",
            OccurrenceId = "invalid-saddlebag",
            SourceKind = MarketEvidenceSources.SaddlebagExchange,
        }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.IngestAsync(1, local with
        {
            IdempotencyKey = "invalid-universalis-complete",
            OccurrenceId = "invalid-universalis-complete",
            SourceKind = MarketEvidenceSources.Universalis,
        }, CancellationToken.None));
        await store.ProjectPendingAsync(CancellationToken.None);

        var detail = await store.GetDetailAsync([1], 700, "Siren", CancellationToken.None);
        var row = Assert.Single((await store.GetLedgerAsync([1], CancellationToken.None)).Rows);
        Assert.NotNull(detail);
        Assert.Equal(7, detail.Observations.Count);
        Assert.Equal(5, detail.Observations.Select(x => x.SourceKind).Distinct().Count());
        Assert.Contains(detail.Observations, x => x.Coverage == MarketEvidenceCoverage.Complete);
        Assert.Contains(detail.Observations, x => x.Coverage == MarketEvidenceCoverage.Partial);
        Assert.Contains(detail.Observations, x => x.Coverage == MarketEvidenceCoverage.LegacyMissing);
        Assert.Contains(detail.Observations, x => x.Coverage == MarketEvidenceCoverage.Empty);
        Assert.Contains(detail.Observations, x => x.Coverage == MarketEvidenceCoverage.Unavailable);
        Assert.Contains(detail.Observations, x => x.Coverage == MarketEvidenceCoverage.AggregateOnly);
        Assert.DoesNotContain(row.Findings, x => x.Kind is "BulkShelfDominance" or "ReplacementDepth" or "SellerPersistence");
        var registry = await client.GetFromJsonAsync<MarketEvidenceSourceRegistryView>("/api/market-intelligence/sources");
        Assert.Equal("market-evidence-sources-v1", registry?.RegistryVersion);
        Assert.Equal(5, registry?.Sources.Count);
    }

    private static MarketEvidenceUploadRequest Book(string id, DateTimeOffset observedAt) => new()
    {
        IdempotencyKey = id,
        OccurrenceId = id,
        SourceKind = MarketEvidenceSources.LegacyRouteImport,
        SourceVersion = "corpus-v1",
        ItemId = 5060,
        ItemName = "Darksteel Ingot",
        DataCenter = "Crystal",
        WorldName = "Diabolos",
        ObservedAtUtc = observedAt,
        Coverage = MarketEvidenceCoverage.Complete,
        ReportedListingCount = 100,
        ListingCapacity = 100,
        IsTruncated = false,
        Listings = Enumerable.Range(1, 100).Select(index => new MarketEvidenceListing
        {
            ListingId = $"listing-{index}",
            RetainerId = $"seller-{SellerOrdinal(index)}",
            RetainerName = $"Retainer {SellerOrdinal(index)}",
            Quantity = index == 100 ? 1u : 99u,
            UnitPrice = index == 100 ? 5_999u : 6_000u,
            IsHq = index != 100,
        }).ToArray(),
    };

    private static int SellerOrdinal(int index) => index switch
    {
        <= 20 => 0,
        <= 40 => 1,
        _ => 2 + ((index - 41) / 12),
    };

    private static MarketEvidenceAggregate Aggregate(uint lowest) => new()
    {
        VisibleListingCount = 50,
        VisibleQuantity = 4_950,
        LowestUnitPrice = lowest,
        HighestUnitPrice = lowest + 20,
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

    private static async Task AddDashboardUserWithoutAccountAsync(string databasePath, string username, string password)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO dashboard_users(username, password_hash, created_at_utc) VALUES($username, $passwordHash, $now)";
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$passwordHash", new DashboardPasswordHasher().HashPassword(password));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
