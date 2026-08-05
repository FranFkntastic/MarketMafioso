using System.Net;
using System.Text;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteRequestReporterTests
{
    [Fact]
    public void ResolveRetainerName_PreservesObservedName()
    {
        var candidate = new MarketBoardPurchaseCandidate
        {
            RetainerId = "retainer-1",
            RetainerName = "Darkwinds",
        };

        Assert.Equal("Darkwinds", MarketAcquisitionRouteRequestReporter.ResolveRetainerName(candidate));
    }

    [Fact]
    public void ResolveRetainerName_FallsBackToStableRetainerId()
    {
        var candidate = new MarketBoardPurchaseCandidate
        {
            RetainerId = "retainer-1",
        };

        Assert.Equal("Retainer retainer-1", MarketAcquisitionRouteRequestReporter.ResolveRetainerName(candidate));
    }

    [Fact]
    public async Task RouteReplay_ReusesPersistedDeliveryMetadata()
    {
        var handler = new RecordingHandler();
        var reporter = new MarketAcquisitionRouteRequestReporter(
            new Configuration
            {
                ServerUrl = "http://localhost/api/inventory",
                ApiKey = "api-key",
                PluginInstanceId = "current-plugin-instance",
            },
            new MarketAcquisitionRequestClient(new HttpClient(handler)));
        var report = new MarketAcquisitionRouteProgressReport(
            "request-a",
            "claim-token",
            "Running",
            "attempt-1",
            42,
            "Aether:Faerie",
            "Faerie",
            "Running",
            "Progress",
            "persisted-plugin-instance",
            "1.3.0-persisted",
            new DateTimeOffset(2026, 7, 31, 1, 2, 3, TimeSpan.Zero));

        await reporter.ReportRouteProgressAsync(report, CancellationToken.None);
        await reporter.ReportRouteProgressAsync(report, CancellationToken.None);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.Contains("\"pluginInstanceId\":\"persisted-plugin-instance\"", handler.Bodies[0]);
        Assert.Contains("\"pluginVersion\":\"1.3.0-persisted\"", handler.Bodies[0]);
        Assert.Contains("\"clientTimestampUtc\":\"2026-07-31T01:02:03+00:00\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task MarketObservation_SendsCoverageWithoutRawListingRows()
    {
        var handler = new RecordingHandler();
        var reporter = new MarketAcquisitionRouteRequestReporter(
            new Configuration
            {
                ServerUrl = "http://localhost/api/inventory",
                ApiKey = "api-key",
                PluginInstanceId = "plugin-instance",
            },
            new MarketAcquisitionRequestClient(new HttpClient(handler)));
        var report = new MarketAcquisitionMarketObservationReport(
            "request-a",
            "claim-token",
            "attempt-1",
            7,
            "line-1",
            5339,
            "Rose Gold Ingot",
            "Aether",
            "Siren",
            DateTimeOffset.UnixEpoch,
            new MarketBoardReadResult
            {
                ReadState = MarketBoardListingReadState.FreshComplete,
                ItemId = 5339,
                WorldName = "Siren",
                ReportedListingCount = 1,
                ListingCapacity = 100,
                Listings =
                [
                    new MarketBoardLiveListing
                    {
                        ItemId = 5339,
                        WorldName = "Siren",
                        ListingId = "listing-1",
                        RetainerId = "retainer-1",
                        RetainerName = "Seller",
                        Quantity = 3,
                        UnitPrice = 50,
                    },
                ],
            });

        await reporter.ReportMarketObservationAsync(report, CancellationToken.None);

        var body = Assert.Single(handler.Bodies);
        Assert.Contains("\"reportedListingCount\":1", body);
        Assert.Contains("\"listingCapacity\":100", body);
        Assert.Contains("\"isTruncated\":false", body);
        Assert.Contains("\"listings\":[]", body);
        Assert.DoesNotContain("listing-1", body);
        Assert.DoesNotContain("Seller", body);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri?.AbsolutePath.EndsWith("/observations", StringComparison.Ordinal) == true
                        ? """{"observationId":"observation-1"}"""
                        : """{"request":{"id":"request-a","status":"Running"},"result":"accepted"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
