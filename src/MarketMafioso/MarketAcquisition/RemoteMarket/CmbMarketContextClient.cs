using System;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace MarketMafioso.MarketAcquisition.RemoteMarket;

internal sealed record CmbMarketContext(
    uint ItemId,
    bool HighQuality,
    uint? HomeWorldPrice,
    string? DatacenterBestWorld,
    uint? DatacenterBestPrice,
    double? VelocityPerDay,
    double? TrendAveragePrice,
    long FreshnessUtcMs,
    string Source);

internal sealed class CmbMarketContextClient : IDisposable
{
    private const string GetMarketContextChannel = "ComplicatedMarketBoard.GetMarketContext.v2";
    private const string MarketContextChangedChannel = "ComplicatedMarketBoard.MarketContextChanged";

    private readonly ICallGateSubscriber<uint, bool, string?> getter;
    private readonly ICallGateSubscriber<uint, bool, object> changed;
    private readonly IPluginLog log;

    public CmbMarketContextClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        getter = pluginInterface.GetIpcSubscriber<uint, bool, string?>(GetMarketContextChannel);
        changed = pluginInterface.GetIpcSubscriber<uint, bool, object>(MarketContextChangedChannel);
        changed.Subscribe(OnContextChanged);
    }

    public event Action<uint, bool, CmbMarketContext?>? ContextChanged;

    public void Dispose() => changed.Unsubscribe(OnContextChanged);

    public CmbMarketContext? Request(uint itemId, bool highQuality)
    {
        try
        {
            var json = getter.InvokeFunc(itemId, highQuality);
            return json is null ? null : JsonSerializer.Deserialize<CmbMarketContext>(json);
        }
        catch (Exception exception)
        {
            log.Verbose(
                "[MarketMafioso] CMB market context unavailable for {ItemId}: {Message}",
                itemId,
                exception.Message);
            return null;
        }
    }

    private void OnContextChanged(uint itemId, bool highQuality)
    {
        var context = Request(itemId, highQuality);
        ContextChanged?.Invoke(itemId, highQuality, context);
    }
}
