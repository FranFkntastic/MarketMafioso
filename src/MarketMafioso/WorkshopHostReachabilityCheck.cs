using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace MarketMafioso;

internal enum WorkshopHostReachability
{
    NotChecked,
    Checking,
    Reachable,
    Unreachable,
}

/// <summary>
/// Answers one question for Home: does the configured Workshop Host answer? It probes the host's public
/// feature document once at startup and after each Server URL edit. No feature flag is read or acted on.
/// </summary>
internal sealed class WorkshopHostReachabilityCheck(Configuration config, IFramework framework, IPluginLog log) : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 64 * 1024 };
    private CancellationTokenSource? pending;

    public WorkshopHostReachability Status { get; private set; } = WorkshopHostReachability.NotChecked;

    public void Start()
    {
        config.ServerUrlChanged += Check;
        Check();
    }

    private void Check()
    {
        pending?.Cancel();
        pending?.Dispose();
        pending = null;
        Status = WorkshopHostReachability.NotChecked;
        if (string.IsNullOrWhiteSpace(config.ServerUrl)) return;
        var url = ReceiverEndpointClassifier.BuildWorkshopHostFeaturesUrl(config.ServerUrl);
        pending = new CancellationTokenSource();
        Status = WorkshopHostReachability.Checking;
        _ = CheckAsync(url, pending.Token);
    }

    private async Task CheckAsync(string? url, CancellationToken token)
    {
        string? failure = null;
        try
        {
            // Settings saves the URL as it is typed. Only query once the edit settles.
            await Task.Delay(750, token).ConfigureAwait(false);
            if (url == null)
                failure = "configured receiver URL is invalid";
            else
            {
                using var response = await http.GetAsync(url, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    failure = $"HTTP {(int)response.StatusCode} ({response.StatusCode})";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { failure = "request timed out"; }
        catch (HttpRequestException ex) { failure = ex.Message; }

        if (token.IsCancellationRequested) return;
        await framework.RunOnFrameworkThread(() =>
        {
            // A URL edit, newer check or unload invalidates queued results too.
            if (token.IsCancellationRequested) return;
            if (failure != null)
                log.Debug($"[MarketMafioso] Workshop Host reachability check ({url ?? "no valid features URL"}): {failure}.");
            Status = failure == null ? WorkshopHostReachability.Reachable : WorkshopHostReachability.Unreachable;
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        config.ServerUrlChanged -= Check;
        pending?.Cancel();
        pending?.Dispose();
        pending = null;
        http.Dispose();
    }
}
