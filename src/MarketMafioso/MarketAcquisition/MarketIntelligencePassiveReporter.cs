using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.Contracts.MarketIntelligence;

namespace MarketMafioso.MarketAcquisition;

internal sealed class MarketIntelligencePassiveReporter : IMarketAcquisitionIntelligenceReporter, IDisposable
{
    private const string ReportType = "market-evidence.v2";
    private const string PreviousReportType = "market-evidence.v1";
    private const string LegacyPassiveReportType = "passive-market-evidence.v1";
    private const string ActorNameReportType = "market-actor-name.v1";
    private readonly Configuration configuration;
    private readonly HttpClient http;
    private readonly IMarketAcquisitionReportOutbox outbox;
    private readonly Action<Exception> reportFailure;
    private readonly SemaphoreSlim flushGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task retryLoop;

    public MarketIntelligencePassiveReporter(Configuration configuration, HttpClient http, string pluginConfigDirectory, Action<Exception> reportFailure)
    {
        this.configuration = configuration;
        this.http = http;
        this.reportFailure = reportFailure;
        outbox = new FileMarketAcquisitionReportOutbox(Path.Combine(pluginConfigDirectory, "market-intelligence-outbox.jsonl"));
        retryLoop = Task.Run(() => RetryLoopAsync(lifetime.Token));
        _ = FlushAsync(lifetime.Token);
    }

    public void Enqueue(MarketEvidenceUploadRequest evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.OccurrenceId)) return;
        outbox.Put($"evidence|{evidence.SourceKind}|{evidence.OccurrenceId}", ReportType, evidence.OccurrenceId, evidence);
        _ = FlushAsync(lifetime.Token);
    }

    public void EnqueueActorName(ulong contentId, string name, string resolutionMethod, DateTimeOffset observedAtUtc)
    {
        if (contentId == 0 || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(resolutionMethod)) return;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{contentId}|{name.Trim()}|{resolutionMethod.Trim()}|{observedAtUtc.ToUniversalTime():O}")));
        var request = new MarketActorNameObservationUploadRequest
        {
            IdempotencyKey = $"{configuration.PluginInstanceId}:actor-name:{fingerprint}",
            ContentId = contentId,
            Name = name.Trim(),
            ResolutionMethod = resolutionMethod.Trim(),
            ObservedAtUtc = observedAtUtc,
        };
        outbox.Put($"actor-name|{fingerprint}", ActorNameReportType, fingerprint, request);
        _ = FlushAsync(lifetime.Token);
    }

    public void EnqueueRouteObservation(MarketAcquisitionMarketObservationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var coverage = report.ReadResult.ReadState switch
        {
            MarketBoardListingReadState.FreshComplete when report.ReadResult.Listings.Count == 0 => MarketEvidenceCoverage.Empty,
            MarketBoardListingReadState.FreshComplete when !report.ReadResult.IsListingCountTruncated &&
                                                        !(report.HasIncompleteCoverage ?? report.ReadResult.HasIncompleteCoverage) => MarketEvidenceCoverage.Complete,
            MarketBoardListingReadState.FreshPartial => MarketEvidenceCoverage.Partial,
            _ => MarketEvidenceCoverage.Unavailable,
        };
        var occurrenceId = $"{report.RequestId}:{report.AttemptId}:{report.Sequence}";
        Enqueue(new MarketEvidenceUploadRequest
        {
            SchemaVersion = 2,
            IdempotencyKey = $"acquisition:{configuration.PluginInstanceId}:{report.AttemptId}:observation:{report.Sequence}",
            OccurrenceId = occurrenceId,
            SourceKind = MarketEvidenceSources.MarketAcquisition,
            SourceVersion = "3",
            SourceInstanceId = configuration.PluginInstanceId,
            SourceBuild = PluginBuildInfo.DisplayVersion,
            CaptureMode = MarketAcquisitionResearchModePolicy.Capture(configuration.MarketAcquisitionExhaustiveResearchMode),
            ItemId = report.ItemId,
            ItemName = report.ItemName,
            DataCenter = report.DataCenter,
            WorldName = report.WorldName,
            ObservedAtUtc = report.ObservedAtUtc,
            Coverage = coverage,
            ReportedListingCount = Math.Max(report.ReadResult.ReportedListingCount, report.ReadResult.Listings.Count),
            ListingCapacity = report.ReadResult.ListingCapacity,
            IsTruncated = report.ReadResult.IsListingCountTruncated ||
                          (report.HasIncompleteCoverage ?? report.ReadResult.HasIncompleteCoverage),
            ProvenanceJson = JsonSerializer.Serialize(new
            {
                requestId = report.RequestId,
                lineId = report.LineId,
                attemptId = report.AttemptId,
                sequence = report.Sequence,
                sourceBuild = PluginBuildInfo.DisplayVersion,
                captureMode = MarketAcquisitionResearchModePolicy.Capture(configuration.MarketAcquisitionExhaustiveResearchMode),
            }),
            Listings = report.ReadResult.Listings.Select(listing => new MarketEvidenceUploadListing
            {
                ListingId = listing.ListingId.ToString(),
                RetainerId = listing.RetainerId.ToString(),
                RetainerName = listing.RetainerName,
                RetainerNameSource = listing.RetainerNameSource,
                SellerOwnerContentId = null,
                ArtisanContentId = listing.ArtisanContentId,
                Quantity = listing.Quantity,
                UnitPrice = listing.UnitPrice,
                IsHq = listing.IsHq,
            }).ToArray(),
        });
    }

    internal IReadOnlyList<MarketAcquisitionReportOutboxEntry> Pending => outbox.Snapshot();

    private async Task RetryLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(cancellationToken)) await FlushAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        bool entered;
        try { entered = await flushGate.WaitAsync(0, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        if (!entered) return;
        try
        {
            foreach (var entry in outbox.Snapshot().Where(x => x.ReportType is ReportType or PreviousReportType or LegacyPassiveReportType or ActorNameReportType))
            {
                try
                {
                    var isActorName = entry.ReportType == ActorNameReportType;
                    var body = isActorName
                        ? (object)outbox.Deserialize<MarketActorNameObservationUploadRequest>(entry)
                        : outbox.Deserialize<MarketEvidenceUploadRequest>(entry);
                    using var request = new HttpRequestMessage(HttpMethod.Post, isActorName ? ResolveActorNameEndpoint(configuration.ServerUrl) : ResolveEndpoint(configuration.ServerUrl)) { Content = JsonContent.Create(body) };
                    request.Headers.Add("X-Api-Key", WorkshopHostApiKeyRouting.ResolveAcquisitionKey(configuration));
                    using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    outbox.Remove(entry.Id);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception exception) { reportFailure(exception); return; }
            }
        }
        finally { flushGate.Release(); }
    }

    private static string ResolveEndpoint(string serverUrl)
    {
        var acquisition = ReceiverEndpointClassifier.BuildAcquisitionBaseUrl(serverUrl)
            ?? throw new InvalidOperationException("The configured receiver URL cannot derive a market intelligence endpoint.");
        return acquisition.EndsWith("/acquisition", StringComparison.OrdinalIgnoreCase)
            ? acquisition[..^"/acquisition".Length] + "/market-intelligence/evidence"
            : throw new InvalidOperationException("The configured receiver URL produced an unexpected acquisition endpoint.");
    }

    private static string ResolveActorNameEndpoint(string serverUrl) =>
        ResolveEndpoint(serverUrl).Replace("/market-intelligence/evidence", "/market-intelligence/actors/names", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        lifetime.Cancel();
        try { retryLoop.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        flushGate.Wait();
        flushGate.Release();
        lifetime.Dispose();
        flushGate.Dispose();
    }
}
