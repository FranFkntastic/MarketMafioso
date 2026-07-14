using System.Collections.Generic;
using System.Linq;
using MarketMafioso.Squire;

namespace MarketMafioso.Windows.Squire;

internal sealed class SquireDuplicateRetentionStore(Configuration config)
{
    public IReadOnlyList<SquireDuplicateRetentionRule> Get(ulong? contentId)
    {
        if (contentId is null || !config.Squire.DuplicateRetentionByCharacter.TryGetValue(contentId.Value.ToString(), out var values))
            return [];
        return values
            .Where(value => value.ItemId != 0 && value.MinimumCopies > 0)
            .GroupBy(value => new { value.ItemId, value.IsHighQuality })
            .Select(group => new SquireDuplicateRetentionRule(group.Key.ItemId, group.Key.IsHighQuality, group.Max(value => value.MinimumCopies)))
            .ToArray();
    }

    public int Get(ulong? contentId, uint itemId, bool isHighQuality) =>
        Get(contentId)
            .Where(rule => rule.ItemId == itemId && rule.IsHighQuality == isHighQuality)
            .Select(rule => rule.MinimumCopies)
            .DefaultIfEmpty(0)
            .Max();

    public void Set(ulong? contentId, uint itemId, bool isHighQuality, int minimumCopies)
    {
        if (contentId is null || itemId == 0)
            return;
        var key = contentId.Value.ToString();
        if (!config.Squire.DuplicateRetentionByCharacter.TryGetValue(key, out var values))
            config.Squire.DuplicateRetentionByCharacter[key] = values = [];
        values.RemoveAll(value => value.ItemId == itemId && value.IsHighQuality == isHighQuality);
        if (minimumCopies > 0)
        {
            values.Add(new SquireDuplicateRetentionConfiguration
            {
                ItemId = itemId,
                IsHighQuality = isHighQuality,
                MinimumCopies = minimumCopies,
            });
        }
        config.Save();
    }
}
