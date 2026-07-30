using System.Collections.Concurrent;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

namespace MarketMafioso.Server.WorkshopHost;

public sealed class WorkshopHostCraftQuoteCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<QuoteKey, CacheEntry> entries = new();

    public bool TryGet(CraftAppraisalRequest request, out CraftAppraisalQuote? quote)
    {
        var key = QuoteKey.From(request);
        if (entries.TryGetValue(key, out var entry) &&
            DateTimeOffset.UtcNow - entry.StoredAtUtc <= MaxAge)
        {
            quote = entry.Quote;
            return true;
        }

        entries.TryRemove(key, out _);
        quote = null;
        return false;
    }

    public void Set(CraftAppraisalRequest request, CraftAppraisalQuote quote)
    {
        var now = DateTimeOffset.UtcNow;
        entries[QuoteKey.From(request)] = new CacheEntry(quote, now);
        foreach (var stale in entries.Where(pair => now - pair.Value.StoredAtUtc > MaxAge))
            entries.TryRemove(stale.Key, out _);
    }

    private sealed record CacheEntry(CraftAppraisalQuote Quote, DateTimeOffset StoredAtUtc);

    private sealed record QuoteKey(
        uint ItemId,
        string ItemName,
        uint Quantity,
        string Region,
        string DataCenter,
        string World,
        string HqPolicy,
        string PricingMode)
    {
        public static QuoteKey From(CraftAppraisalRequest request) => new(
            request.ItemId,
            request.ItemName.Trim(),
            request.Quantity,
            request.Scope.Region.Trim(),
            request.Scope.DataCenter?.Trim() ?? string.Empty,
            request.Scope.World?.Trim() ?? string.Empty,
            request.Options.HqPolicy.Trim(),
            request.Options.PricingMode.Trim());
    }
}
