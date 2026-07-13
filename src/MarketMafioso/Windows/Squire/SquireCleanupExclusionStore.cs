using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.Windows.Squire;

internal sealed class SquireCleanupExclusionStore(Configuration config)
{
    public IReadOnlySet<uint> Get(ulong? contentId)
    {
        if (contentId is null || !config.Squire.ExcludedItemIdsByCharacter.TryGetValue(contentId.Value.ToString(), out var values))
            return new HashSet<uint>();
        return values.ToHashSet();
    }

    public void Set(ulong? contentId, uint itemId, bool excluded)
    {
        if (contentId is null)
            return;
        var key = contentId.Value.ToString();
        if (!config.Squire.ExcludedItemIdsByCharacter.TryGetValue(key, out var values))
            config.Squire.ExcludedItemIdsByCharacter[key] = values = [];
        if (excluded && !values.Contains(itemId)) values.Add(itemId);
        if (!excluded) values.Remove(itemId);
        config.Save();
    }
}

