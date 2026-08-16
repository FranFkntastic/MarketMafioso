using System.Net;
using MarketMafioso.Contracts.MarketIntelligence;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketIntelligencePassiveReporterTests
{
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
}
