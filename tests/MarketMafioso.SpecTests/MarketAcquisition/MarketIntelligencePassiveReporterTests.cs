using System.Net;
using System.Text.Json;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Contracts.MarketIntelligence;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketIntelligencePassiveReporterTests
{
    [Fact]
    public async Task ClaimlessRouteUsesAuthenticatedDirectEvidenceWithoutHostedLifecycle()
    {
        Assert.Equal(
            MarketAcquisitionObservationDelivery.DirectEvidence,
            MarketAcquisitionObservationDeliveryPolicy.Resolve(
                hostedReportingAvailable: true,
                claimToken: string.Empty,
                directEvidenceAvailable: true));
        Assert.Equal(
            MarketAcquisitionObservationDelivery.HostedLifecycle,
            MarketAcquisitionObservationDeliveryPolicy.Resolve(
                hostedReportingAvailable: true,
                claimToken: "claim-token",
                directEvidenceAvailable: true));

        var directory = Path.Combine(Path.GetTempPath(), $"mmf-intelligence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new Configuration
            {
                ServerUrl = "https://example.test/api/inventory",
                ApiKey = "test-key",
                PluginInstanceId = "test-instance",
            };
            var handler = new RecordingHandler();
            using var http = new HttpClient(handler);
            using var reporter = new MarketIntelligencePassiveReporter(configuration, http, directory, _ => { });
            reporter.EnqueueRouteObservation(RouteObservation());

            await WaitUntilAsync(() => handler.Requests.Count == 1 && reporter.Pending.Count == 0);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("/api/market-intelligence/evidence", request.Path);
            Assert.Equal("test-key", request.ApiKey);
            var evidence = JsonSerializer.Deserialize<MarketEvidenceUploadRequest>(
                request.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(evidence);
            Assert.Equal("local:plan-one:route-run-one:7", evidence.OccurrenceId);
            Assert.Equal(MarketEvidenceSources.MarketAcquisition, evidence.SourceKind);
            Assert.Equal(MarketEvidenceCoverage.Complete, evidence.Coverage);
            Assert.Equal(2, evidence.SchemaVersion);
            var listing = Assert.Single(evidence.Listings);
            Assert.Equal("Local Seller", listing.RetainerName);
            Assert.Equal((ulong)100, listing.SellerOwnerContentId);
            Assert.Equal((ulong)200, listing.ArtisanContentId);
            Assert.Contains("\"requestId\":\"local:plan-one\"", evidence.ProvenanceJson);
            Assert.Contains("\"lineId\":\"local:line-one\"", evidence.ProvenanceJson);

            reporter.EnqueueActorName(200, "Known Maker", "ControlledFixture", DateTimeOffset.UnixEpoch);
            await WaitUntilAsync(() => handler.Requests.Count == 2 && reporter.Pending.Count == 0);
            var nameRequest = handler.Requests.Single(item => item.Path.EndsWith("/actors/names", StringComparison.Ordinal));
            var name = JsonSerializer.Deserialize<MarketActorNameObservationUploadRequest>(nameRequest.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal((ulong)200, name!.ContentId);
            Assert.Equal("Known Maker", name.Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HostedOutagePreservesOneOccurrenceAndRestartRetriesIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mmf-intelligence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new Configuration
            {
                ServerUrl = "https://example.test/api/inventory",
                ApiKey = "test-key",
                PluginInstanceId = "test-instance",
            };
            using (var failedHttp = new HttpClient(new StatusHandler(HttpStatusCode.ServiceUnavailable)))
            using (var failed = new MarketIntelligencePassiveReporter(configuration, failedHttp, directory, _ => { }))
            {
                var evidence = Evidence();
                failed.Enqueue(evidence);
                failed.Enqueue(evidence);
                await WaitUntilAsync(() => failed.Pending.Count == 1);
                Assert.Contains("Retainer One", failed.Pending[0].PayloadJson);
            }

            using var recoveredHttp = new HttpClient(new StatusHandler(HttpStatusCode.OK));
            using var recovered = new MarketIntelligencePassiveReporter(configuration, recoveredHttp, directory, _ => { });
            await WaitUntilAsync(() => recovered.Pending.Count == 0);
            Assert.Empty(recovered.Pending);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MarketEvidenceUploadRequest Evidence() => new()
    {
        IdempotencyKey = "passive-one",
        OccurrenceId = "browse-one",
        SourceKind = MarketEvidenceSources.PassiveMarketBoard,
        ItemId = 42,
        ItemName = "Test Item",
        DataCenter = "Aether",
        WorldName = "Siren",
        ObservedAtUtc = DateTimeOffset.UtcNow,
        Coverage = MarketEvidenceCoverage.Complete,
        Listings = [new() { ListingId = "1", RetainerId = "2", RetainerName = "Retainer One", Quantity = 99, UnitPrice = 500 }],
    };

    private static MarketAcquisitionMarketObservationReport RouteObservation() => new(
        "local:plan-one",
        string.Empty,
        "route-run-one",
        7,
        "local:line-one",
        5530,
        "Coke",
        "Primal",
        "Ultros",
        DateTimeOffset.UnixEpoch,
        new MarketBoardReadResult
        {
            ReadState = MarketBoardListingReadState.FreshComplete,
            ItemId = 5530,
            WorldName = "Ultros",
            ReportedListingCount = 1,
            ListingCapacity = 100,
            Listings =
            [
                new MarketBoardLiveListing
                {
                    ItemId = 5530,
                    WorldName = "Ultros",
                    ListingId = "listing-local",
                    RetainerId = "retainer-local",
                    RetainerName = "Local Seller",
                    RetainerNameSource = "ControlledFixture",
                    SellerOwnerContentId = 100,
                    ArtisanContentId = 200,
                    Quantity = 99,
                    UnitPrice = 150,
                },
            ],
        });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Condition was not reached.");
            await Task.Delay(20);
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly object sync = new();
        private readonly List<RecordedRequest> requests = [];

        public IReadOnlyList<RecordedRequest> Requests
        {
            get { lock (sync) return requests.ToArray(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.GetValues("X-Api-Key").Single(),
                await request.Content!.ReadAsStringAsync(cancellationToken));
            lock (sync) requests.Add(recorded);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private sealed record RecordedRequest(string Path, string ApiKey, string Body);
}
