using System.Net;
using MarketMafioso.MarketAcquisition;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MarketMafioso.ContractTests.MarketAcquisition;

public sealed class MarketAcquisitionRequestClientServerContractTests
{
    [Fact]
    public async Task ClientCompletesBatchFlowAndPreservesTerminalConflict()
    {
        await using var application = new HostedApplication();
        using var httpClient = application.CreateClient();
        var client = new MarketAcquisitionRequestClient(httpClient);
        var serverUrl = new Uri(httpClient.BaseAddress!, "/marketmafioso/api/inventory").ToString();

        var created = await client.CreateBatchAsync(
            serverUrl,
            "client-secret",
            CreateBatchRequest(),
            CancellationToken.None);

        Assert.Equal("PendingPickup", created.Status);
        Assert.Equal(MarketAcquisitionOrigins.PluginBuilder, created.Origin);
        Assert.Equal("plugin-contract-instance", created.CreatedByPluginInstanceId);
        Assert.Equal(1, created.Revision);
        Assert.Equal(2u, Assert.Single(created.Lines).ItemId);

        var fetched = await client.GetBatchAsync(
            serverUrl,
            "client-secret",
            created.Id,
            CancellationToken.None);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(created.Revision, fetched.Revision);
        Assert.Equal(created.Status, fetched.Status);
        Assert.Equal(created.Lines, fetched.Lines);

        var pending = await client.FetchPendingAsync(
            serverUrl,
            "client-secret",
            "Wei Ning",
            "Gilgamesh",
            CancellationToken.None);
        Assert.Equal(created.Id, Assert.Single(pending).Id);

        var replaced = await client.ReplaceBatchAsync(
            serverUrl,
            "client-secret",
            created.Id,
            CreateReplacement(created.Revision),
            CancellationToken.None);
        var line = Assert.Single(replaced.Lines);
        Assert.Equal(created.Revision + 1, replaced.Revision);
        Assert.Equal(5064u, line.ItemId);
        Assert.Equal("Silver Ingot", line.ItemName);
        Assert.Equal(10u, line.TargetQuantity);
        Assert.Equal(50u, line.MaxUnitPrice);

        var claimed = await client.ClaimAsync(
            serverUrl,
            "client-secret",
            created.Id,
            "Wei Ning",
            "Gilgamesh",
            "plugin-contract-instance",
            CancellationToken.None);
        Assert.Equal("Claimed", claimed.Status);
        Assert.False(string.IsNullOrWhiteSpace(claimed.ClaimToken));

        var accepted = await client.AcceptAsync(
            serverUrl,
            "client-secret",
            created.Id,
            claimed.ClaimToken,
            "contract-accept",
            CancellationToken.None);
        Assert.Equal("AcceptedInPlugin", accepted.Status);

        var attempt = await client.ReportAttemptProgressAsync(
            serverUrl,
            "client-secret",
            created.Id,
            claimed.ClaimToken,
            "plugin-contract-instance",
            "attempt-1",
            1,
            "stop-siren",
            "Siren",
            "SearchingItem",
            "Searching for Silver Ingot.",
            "1.2.3-contract",
            CancellationToken.None);
        Assert.Equal(MarketAcquisitionAttemptEventResults.Accepted, attempt.Result);
        Assert.Equal("Running", attempt.Request.Status);
        Assert.Equal("attempt-1", attempt.Request.LatestAttemptId);
        Assert.Equal("SearchingItem", attempt.Request.LatestAttemptPhase);
        Assert.Equal("Siren", attempt.Request.LatestAttemptWorld);

        var observation = await client.PostMarketObservationAsync(
            serverUrl,
            "client-secret",
            created.Id,
            CreateObservation(claimed.ClaimToken, line.LineId),
            CancellationToken.None);
        Assert.Equal(created.Id, observation.RequestId);
        Assert.Equal("Complete", observation.ReadState);
        Assert.Equal(1, observation.ReportedListingCount);
        Assert.Equal("listing-1", Assert.Single(observation.Listings).ListingId);

        var purchase = await client.PostPurchaseAuditAsync(
            serverUrl,
            "client-secret",
            created.Id,
            CreatePurchase(claimed.ClaimToken, line.LineId),
            CancellationToken.None);
        Assert.Equal(created.Id, purchase.RequestId);
        Assert.Equal(line.LineId, purchase.LineId);
        Assert.Equal("Purchased", purchase.Result);
        Assert.Equal(500u, purchase.TotalGil);

        var completedLine = await client.PostLineProgressAsync(
            serverUrl,
            "client-secret",
            created.Id,
            line.LineId,
            CreateLineProgress(claimed.ClaimToken),
            CancellationToken.None);
        Assert.Equal("Complete", completedLine.Status);
        Assert.Equal(10u, completedLine.PurchasedQuantity);
        Assert.Equal(500u, completedLine.SpentGil);

        var finalView = await client.GetBatchAsync(
            serverUrl,
            "client-secret",
            created.Id,
            CancellationToken.None);
        Assert.Equal(completedLine, Assert.Single(finalView.Lines));
        Assert.Equal("attempt-1", finalView.LatestAttemptId);

        var completed = await client.CompleteAsync(
            serverUrl,
            "client-secret",
            created.Id,
            claimed.ClaimToken,
            "contract-complete",
            "Acquisition complete.",
            CancellationToken.None);
        Assert.Equal("Complete", completed.Status);

        var conflict = await Assert.ThrowsAsync<MarketAcquisitionLifecycleHttpException>(() =>
            client.ReportProgressAsync(
                serverUrl,
                "client-secret",
                created.Id,
                claimed.ClaimToken,
                "contract-late-progress",
                "Running",
                "Too late.",
                CancellationToken.None));

        const string conflictReason = "Cannot move acquisition request from Complete to Running.";
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("progress", conflict.Action);
        Assert.Equal(conflictReason, conflict.Error);
        Assert.Contains(conflictReason, conflict.ResponseBody, StringComparison.Ordinal);
    }

    private static MarketAcquisitionBatchCreateRequest CreateBatchRequest() => new()
    {
        IdempotencyKey = "contract-create",
        Origin = MarketAcquisitionOrigins.PluginBuilder,
        CreatedByPluginInstanceId = "plugin-contract-instance",
        TargetCharacterName = "Wei Ning",
        TargetWorld = "Gilgamesh",
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
        ExpiresInSeconds = 300,
        Lines =
        [
            new()
            {
                ItemId = 2,
                ItemName = "Fire Shard",
                ItemKind = "Crystal",
                QuantityMode = "TargetQuantity",
                TargetQuantity = 10,
                HqPolicy = "Either",
                MaxUnitPrice = 99,
                GilCap = 990,
            },
        ],
    };

    private static MarketAcquisitionBatchReplaceRequest CreateReplacement(int revision) => new()
    {
        ExpectedRevision = revision,
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
        ExpiresInSeconds = 300,
        Lines =
        [
            new()
            {
                ItemId = 5064,
                ItemName = "Silver Ingot",
                ItemKind = "Metal",
                QuantityMode = "TargetQuantity",
                TargetQuantity = 10,
                HqPolicy = "Either",
                MaxUnitPrice = 50,
                GilCap = 500,
            },
        ],
    };

    private static MarketAcquisitionMarketObservationRequest CreateObservation(
        string claimToken,
        string lineId) => new()
    {
        ClaimToken = claimToken,
        IdempotencyKey = "contract-observation",
        AttemptId = "attempt-1",
        Sequence = 1,
        LineId = lineId,
        ItemId = 5064,
        ItemName = "Silver Ingot",
        DataCenter = "Aether",
        WorldName = "Siren",
        ReadState = "Complete",
        ReportedListingCount = 1,
        ListingCapacity = 100,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        Listings =
        [
            new()
            {
                ListingId = "listing-1",
                RetainerId = "retainer-1",
                RetainerName = "Seller",
                Quantity = 10,
                UnitPrice = 50,
            },
        ],
    };

    private static MarketAcquisitionPurchaseAuditRequest CreatePurchase(
        string claimToken,
        string lineId) => new()
    {
        ClaimToken = claimToken,
        IdempotencyKey = "contract-purchase",
        AttemptId = "attempt-1",
        Sequence = 2,
        LineId = lineId,
        WorldName = "Siren",
        ItemId = 5064,
        ItemName = "Silver Ingot",
        ListingId = "listing-1",
        RetainerName = "Seller",
        RetainerId = "retainer-1",
        Quantity = 10,
        UnitPrice = 50,
        TotalGil = 500,
        Result = "Purchased",
    };

    private static MarketAcquisitionLineProgressRequest CreateLineProgress(string claimToken) => new()
    {
        ClaimToken = claimToken,
        IdempotencyKey = "contract-line-progress",
        AttemptId = "attempt-1",
        Sequence = 3,
        Status = "Complete",
        PurchasedQuantity = 10,
        SpentGil = 500,
        Message = "Line complete.",
    };

    private sealed class HostedApplication : IAsyncDisposable
    {
        private readonly string contentRoot = Path.Combine(
            Path.GetTempPath(),
            "MarketMafioso.AcquisitionClientServerContract.Tests",
            Guid.NewGuid().ToString("N"));
        private readonly WebApplicationFactory<Program> application;

        public HostedApplication()
        {
            Directory.CreateDirectory(contentRoot);
            var values = new Dictionary<string, string?>
            {
                ["MarketMafioso:RequireApiKey"] = "true",
                ["MarketMafioso:ClientApiKey"] = "client-secret",
                ["MarketMafioso:BasePath"] = "/marketmafioso",
                ["MarketMafioso:EnableMarketAcquisition"] = "true",
                ["MarketMafioso:DatabasePath"] = Path.Combine(contentRoot, "marketmafioso.db"),
            };

            application = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseContentRoot(contentRoot);
                    builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(values));
                });
        }

        public HttpClient CreateClient() => application.CreateClient();

        public async ValueTask DisposeAsync()
        {
            await application.DisposeAsync();
            try
            {
                Directory.Delete(contentRoot, recursive: true);
            }
            catch (IOException)
            {
                // Microsoft.Data.Sqlite may retain its pooled file handle until process exit.
            }
        }
    }
}
