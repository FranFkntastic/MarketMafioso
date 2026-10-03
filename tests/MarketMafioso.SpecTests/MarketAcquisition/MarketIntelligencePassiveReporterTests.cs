using System.Net;
using System.Collections.Concurrent;
using System.Text.Json;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Contracts.MarketIntelligence;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketIntelligencePassiveReporterTests
{
    [Fact]
    public async Task SamePassiveBrowse_AfterAcknowledgementDoesNotResubmitChangedTimestamp()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mmf-intelligence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var handler = new RecordingHandler();
            using var http = new HttpClient(handler);
            using var reporter = new MarketIntelligencePassiveReporter(TestConfiguration(), http, directory, _ => { });
            var evidence = Evidence();
            reporter.Enqueue(evidence);
            await WaitUntilAsync(() => handler.Requests.Count == 1 && reporter.Pending.Count == 0);

            reporter.Enqueue(evidence with { ObservedAtUtc = evidence.ObservedAtUtc.AddSeconds(1) });
            Assert.Empty(reporter.Pending);
            Assert.Single(handler.Requests);

            reporter.Enqueue(evidence with { IdempotencyKey = "passive-two", OccurrenceId = "browse-two" });
            await WaitUntilAsync(() => handler.Requests.Count == 2 && reporter.Pending.Count == 0);
            Assert.Equal("browse-two", JsonSerializer.Deserialize<MarketEvidenceUploadRequest>(
                handler.Requests[1].Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.OccurrenceId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RestartRetry_UsesExactOriginalBodyIdentityAndObservationTime()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mmf-intelligence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var failures = new ConcurrentQueue<Exception>();
            var failedHandler = new RecordingHandler(HttpStatusCode.ServiceUnavailable);
            string originalBody;
            using (var http = new HttpClient(failedHandler))
            using (var reporter = new MarketIntelligencePassiveReporter(TestConfiguration(), http, directory, failures.Enqueue))
            {
                reporter.Enqueue(Evidence());
                await WaitUntilAsync(() => !failures.IsEmpty);
                originalBody = Assert.Single(failedHandler.Requests).Body;
                Assert.Single(reporter.Pending);
            }

            var recoveredHandler = new RecordingHandler();
            using var recoveredHttp = new HttpClient(recoveredHandler);
            using var recovered = new MarketIntelligencePassiveReporter(TestConfiguration(), recoveredHttp, directory, failures.Enqueue);
            await WaitUntilAsync(() => recovered.Pending.Count == 0);
            Assert.Equal(originalBody, Assert.Single(recoveredHandler.Requests).Body);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Conflict_RetainsJournalBytesAndBlocksLaterReportsAcrossRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mmf-intelligence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "market-intelligence-outbox.jsonl");
            var outbox = new FileMarketAcquisitionReportOutbox(path);
            var oldest = Evidence();
            outbox.Put("evidence|PassiveMarketBoard|browse-one", "market-evidence.v2", oldest.OccurrenceId, oldest);
            var next = oldest with { IdempotencyKey = "passive-two", OccurrenceId = "browse-two", ItemId = 43 };
            outbox.Put("evidence|PassiveMarketBoard|browse-two", "market-evidence.v2", next.OccurrenceId, next);
            var before = File.ReadAllBytes(path);
            var failures = new ConcurrentQueue<Exception>();
            var handler = new RecordingHandler(HttpStatusCode.Conflict);
            using var http = new HttpClient(handler);
            for (var restart = 0; restart < 2; restart++)
            {
                using var reporter = new MarketIntelligencePassiveReporter(TestConfiguration(), http, directory, failures.Enqueue);
                await WaitUntilAsync(() => failures.Count == restart + 1);
                Assert.Equal(2, reporter.Pending.Count);
                Assert.Equal(before, File.ReadAllBytes(path));
            }

            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
            Assert.All(handler.Requests, request => Assert.Equal("browse-one", JsonSerializer.Deserialize<MarketEvidenceUploadRequest>(
                request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.OccurrenceId));
            Assert.All(failures, failure =>
            {
                var conflict = Assert.IsType<HttpRequestException>(failure);
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
                Assert.Contains("retained unchanged", conflict.Message);
                Assert.Contains("later intelligence reports remain blocked", conflict.Message);
                Assert.DoesNotContain("test-key", conflict.Message);
                Assert.DoesNotContain("Retainer One", conflict.Message);
            });
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Configuration TestConfiguration() => new()
    {
        ServerUrl = "https://example.test/api/inventory",
        ApiKey = "test-key",
        PluginInstanceId = "test-instance",
    };

    [Fact]
    public async Task RecreatedCollector_PreservesTwoObservationsDespiteSameCounterAndInstance()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mmf-intelligence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = TestConfiguration();
            var beforeRestart = new MarketBoardBrowseOperationGate();
            var afterRestart = new MarketBoardBrowseOperationGate();
            Assert.True(beforeRestart.TryBegin(MarketBoardBrowseOwner.MarketAcquisition, 42, out var first));
            Assert.True(afterRestart.TryBegin(MarketBoardBrowseOwner.MarketAcquisition, 42, out var next));
            Assert.EndsWith(":1", first.OperationId);
            Assert.EndsWith(":1", next.OperationId);
            var original = Evidence() with
            {
                OccurrenceId = first.OperationId,
                IdempotencyKey = $"{configuration.PluginInstanceId}:passive:{first.OperationId}",
            };
            var later = original with
            {
                OccurrenceId = next.OperationId,
                IdempotencyKey = $"{configuration.PluginInstanceId}:passive:{next.OperationId}",
                ObservedAtUtc = original.ObservedAtUtc.AddHours(8),
                Listings = [new() { ListingId = "3", RetainerId = "2", Quantity = 50, UnitPrice = 400 }],
            };
            var failures = new ConcurrentQueue<Exception>();
            using var http = new HttpClient(new RecordingHandler(HttpStatusCode.ServiceUnavailable));
            using var reporter = new MarketIntelligencePassiveReporter(configuration, http, directory, failures.Enqueue);
            reporter.Enqueue(original);
            await WaitUntilAsync(() => failures.Count == 1);
            var retained = Assert.Single(reporter.Pending).PayloadJson;
            reporter.Enqueue(later);
            await WaitUntilAsync(() => failures.Count == 2);

            Assert.Equal(2, reporter.Pending.Count);
            Assert.Equal(retained, reporter.Pending.Single(entry => entry.RequestId == original.OccurrenceId).PayloadJson);
            var requests = reporter.Pending.Select(entry => JsonSerializer.Deserialize<MarketEvidenceUploadRequest>(
                entry.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
            Assert.Equal(2, requests.Select(request => request.IdempotencyKey).Distinct().Count());
            Assert.Equal(2, requests.Select(request => request.OccurrenceId).Distinct().Count());
            Assert.Contains(requests, request => request.ObservedAtUtc == later.ObservedAtUtc && request.Listings[0].ListingId == "3");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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
            Assert.Equal("3", evidence.SourceVersion);
            var listing = Assert.Single(evidence.Listings);
            Assert.Equal("Local Seller", listing.RetainerName);
            Assert.Null(listing.SellerOwnerContentId);
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

    private sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
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
            return new HttpResponseMessage(status) { Content = new StringContent("{}") };
        }
    }

    private sealed record RecordedRequest(string Path, string ApiKey, string Body);
}
