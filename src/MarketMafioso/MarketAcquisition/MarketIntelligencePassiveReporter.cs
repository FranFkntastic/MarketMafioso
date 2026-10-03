using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.Contracts.MarketIntelligence;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketIntelligenceReportingStatus(
    int PendingCount,
    int QuarantinedCount,
    DateTimeOffset? LastQuarantinedAtUtc,
    string? QuarantineReason);

internal sealed class MarketIntelligencePassiveReporter : IMarketAcquisitionIntelligenceReporter, IDisposable
{
    private const string ReportType = "market-evidence.v2";
    private const string PreviousReportType = "market-evidence.v1";
    private const string LegacyPassiveReportType = "passive-market-evidence.v1";
    private const string ActorNameReportType = "market-actor-name.v1";
    private readonly Configuration configuration;
    private readonly HttpClient http;
    private readonly FileMarketAcquisitionReportOutbox outbox;
    private readonly Action<Exception> reportFailure;
    private readonly object enqueueSync = new();
    private string? lastPassiveEvidenceId;
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
        var id = $"evidence|{evidence.SourceKind}|{evidence.OccurrenceId}";
        lock (enqueueSync)
        {
            // The current complete browse can be observed repeatedly, including after its
            // upload succeeds. Keep its first payload instead of generating a new timestamp.
            if (evidence.SourceKind == MarketEvidenceSources.PassiveMarketBoard && id == lastPassiveEvidenceId)
                return;
            outbox.Put(id, ReportType, evidence.OccurrenceId, evidence);
            if (evidence.SourceKind == MarketEvidenceSources.PassiveMarketBoard)
                lastPassiveEvidenceId = id;
        }
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

    internal IReadOnlyList<MarketAcquisitionReportOutboxEntry> Pending =>
        outbox.Snapshot().Where(entry => entry.Quarantine is null).ToArray();

    internal IReadOnlyList<MarketAcquisitionReportOutboxEntry> Quarantined =>
        outbox.Snapshot().Where(entry => entry.Quarantine is not null).ToArray();

    public MarketIntelligenceReportingStatus CreateStatus()
    {
        var entries = outbox.Snapshot();
        var conflicts = entries.Where(entry => entry.Quarantine is not null).ToArray();
        var last = conflicts.OrderByDescending(entry => entry.Quarantine!.QuarantinedAtUtc).FirstOrDefault()?.Quarantine;
        return new(entries.Count - conflicts.Length, conflicts.Length, last?.QuarantinedAtUtc, last?.Reason);
    }

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
            foreach (var entry in outbox.Snapshot().Where(x => x.Quarantine is null &&
                         x.ReportType is ReportType or PreviousReportType or LegacyPassiveReportType or ActorNameReportType))
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
                    if (response.StatusCode == HttpStatusCode.Conflict)
                    {
                        if (await IsConfirmedIdentityConflictAsync(response, cancellationToken).ConfigureAwait(false))
                        {
                            outbox.QuarantineConflict(entry.Id);
                            reportFailure(new HttpRequestException(
                                "The server confirmed an evidence identity conflict. The original report is retained in durable quarantine; " +
                                "later pending intelligence reports can continue. Quarantined reports require reconciliation and are not retried automatically.",
                                inner: null, statusCode: response.StatusCode));
                            continue;
                        }
                        throw new HttpRequestException(
                            "Market intelligence upload returned an unrecognized HTTP 409. " +
                            "The report remains pending; no report was quarantined and later uploads remain blocked.",
                            inner: null,
                            statusCode: response.StatusCode);
                    }
                    response.EnsureSuccessStatusCode();
                    outbox.Remove(entry.Id);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception exception) { reportFailure(exception); return; }
            }
        }
        finally { flushGate.Release(); }
    }

    private static async Task<bool> IsConfirmedIdentityConflictAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
            return false;
        // Never log an arbitrary error body or allow an unbounded proxy response here.
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[4097];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length > 4096)
            return false;
        try
        {
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            if (json.RootElement.TryGetProperty("code", out var code))
                return code.ValueKind == JsonValueKind.String && code.GetString() == MarketEvidenceErrors.IdempotencyConflict;
            // Compatibility with the receiver deployed before the additive error code.
            return json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
                   error.GetString() == MarketEvidenceErrors.IdempotencyConflictMessage;
        }
        catch (JsonException)
        {
            return false;
        }
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
