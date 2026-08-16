using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.Contracts.MarketIntelligence;

namespace MarketMafioso.MarketAcquisition;

internal sealed class MarketIntelligencePassiveReporter : IDisposable
{
    private const string ReportType = "passive-market-evidence.v1";
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
        outbox.Put($"passive|{evidence.OccurrenceId}", ReportType, evidence.OccurrenceId, evidence);
        _ = FlushAsync(lifetime.Token);
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
            foreach (var entry in outbox.Snapshot().Where(x => x.ReportType == ReportType))
            {
                try
                {
                    var body = outbox.Deserialize<MarketEvidenceUploadRequest>(entry);
                    using var request = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint(configuration.ServerUrl)) { Content = JsonContent.Create(body) };
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
