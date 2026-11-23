using System.Collections.Generic;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

/// <summary>
/// A buy-list line's identity in the Workbench: its item, and which of that item's lines it is. An item
/// normally has one line; an exact-acquisition plan can hold an HQ and an NQ line for the same item.
/// </summary>
public readonly record struct WorkbenchLineKey(uint ItemId, int Occurrence)
{
    /// <summary>The key of each line, in order.</summary>
    public static IReadOnlyList<WorkbenchLineKey> For(IReadOnlyList<MarketAcquisitionRequestLineDocument> lines)
    {
        var seen = new Dictionary<uint, int>();
        var keys = new WorkbenchLineKey[lines.Count];
        for (var index = 0; index < lines.Count; index++)
        {
            var itemId = lines[index].ItemId;
            var occurrence = seen.GetValueOrDefault(itemId);
            keys[index] = new WorkbenchLineKey(itemId, occurrence);
            seen[itemId] = occurrence + 1;
        }
        return keys;
    }

    /// <summary>Stable text for widget and agent-bridge ids; an item's first line keeps its plain item id.</summary>
    public string Id => Occurrence == 0 ? ItemId.ToString() : $"{ItemId}-{Occurrence}";
}
