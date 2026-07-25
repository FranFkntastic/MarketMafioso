using System;
using System.Collections.Generic;
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

internal sealed class CmbMarketContextClient
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    private readonly ICallGateSubscriber<uint, bool, CmbMarketContext?> subscriber;
    private readonly IPluginLog log;
    private readonly Dictionary<(uint ItemId, bool Hq), (DateTimeOffset CachedAt, CmbMarketContext? Context)> cache = [];

    public CmbMarketContextClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        subscriber = pluginInterface.GetIpcSubscriber<uint, bool, CmbMarketContext?>(
            "ComplicatedMarketBoard.GetMarketContext");
    }

    public CmbMarketContext? Get(uint itemId, bool highQuality)
    {
        var key = (itemId, highQuality);
        if (cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheLifetime)
            return cached.Context;

        CmbMarketContext? context = null;
        try
        {
            context = subscriber.InvokeFunc(itemId, highQuality);
        }
        catch (Exception exception)
        {
            log.Verbose("[MarketMafioso] CMB market context unavailable for {ItemId}: {Message}", itemId, exception.Message);
        }

        cache[key] = (DateTimeOffset.UtcNow, context);
        return context;
    }
}
