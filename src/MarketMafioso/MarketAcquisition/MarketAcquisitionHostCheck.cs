using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace MarketMafioso.MarketAcquisition;

internal sealed class MarketAcquisitionHostCheck(Configuration config, IFramework framework, IPluginLog log) : IDisposable
{
    private const string NotChecked = "Not checked yet.";
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 64 * 1024 };
    private CancellationTokenSource? pending;
    public string Status { get; private set; } = NotChecked;

    public void Start()
    {
        config.ServerUrlChanged += CheckInBackground;
        CheckInBackground();
    }

    private void CheckInBackground() => Check(onDemand: false);
    public void CheckOnDemand() => Check(onDemand: true);

    public void Reset()
    {
        pending?.Cancel();
        pending?.Dispose();
        pending = null;
        Status = NotChecked;
    }

    private void Check(bool onDemand)
    {
        Reset();
        if (string.IsNullOrWhiteSpace(config.ServerUrl)) return;
        var url = ReceiverEndpointClassifier.BuildWorkshopHostFeaturesUrl(config.ServerUrl);
        pending = new CancellationTokenSource();
        Status = "Checking...";
        _ = CheckAsync(url, onDemand, pending.Token);
    }

    private async Task CheckAsync(string? url, bool onDemand, CancellationToken token)
    {
        bool granted = false;
        string? failure = null;
        try
        {
            // Settings saves the URL as it is typed. Only query once the edit settles.
            if (!onDemand) await Task.Delay(750, token).ConfigureAwait(false);
            if (url == null)
                failure = "configured receiver URL is invalid";
            else
            {
                using var response = await http.GetAsync(url, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    failure = $"HTTP {(int)response.StatusCode} ({response.StatusCode})";
                else
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                    if (body.RootElement.ValueKind == JsonValueKind.Object &&
                        body.RootElement.TryGetProperty("enableMarketAcquisition", out var flag) &&
                        flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        granted = flag.GetBoolean();
                    else
                        failure = "unparseable body: expected boolean enableMarketAcquisition";
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { failure = "unreachable: request timed out"; }
        catch (HttpRequestException ex) { failure = $"unreachable: {ex.Message}"; }
        catch (JsonException ex) { failure = $"unparseable body: {ex.Message}"; }

        if (token.IsCancellationRequested) return;
        await framework.RunOnFrameworkThread(() =>
        {
            // A URL edit, manual lock, newer check or unload invalidates queued results too.
            if (token.IsCancellationRequested) return;
            if (failure != null)
            {
                var message = $"[MarketMafioso] Market Acquisition feature check ({url ?? "no valid features URL"}): {failure}.";
                if (onDemand) log.Warning(message);
                else log.Debug(message);
                Status = "Could not reach the server.";
                return;
            }

            if (granted && !MarketAcquisitionUnlock.IsUnlocked(config) && (onDemand || !config.MarketAcquisitionLockedByUser))
            {
                config.EnableMarketAcquisition = true;
                config.MarketAcquisitionLockedByUser = false;
                config.MarketAcquisitionUnlockedAtUtc = DateTime.UtcNow;
                config.Save();
            }
            Status = !granted ? "Server does not offer Market Acquisition."
                : MarketAcquisitionUnlock.IsUnlocked(config) ? "Unlocked." : "Server offers Market Acquisition.";
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        config.ServerUrlChanged -= CheckInBackground;
        Reset();
        http.Dispose();
    }
}
