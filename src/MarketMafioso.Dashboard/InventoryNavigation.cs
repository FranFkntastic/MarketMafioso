using MarketMafioso.Contracts.Inventory;

namespace MarketMafioso.Dashboard;

public static class InventoryNavigation
{
    public static string BuildPath(
        InventoryBrowserMode mode,
        string scope,
        string? filter,
        long? characterId,
        string? snapshotId = null)
    {
        var query = new List<string>
        {
            $"mode={Uri.EscapeDataString(mode.ToString())}",
            $"scope={Uri.EscapeDataString(scope)}",
        };

        if (!string.IsNullOrWhiteSpace(filter))
            query.Add($"filter={Uri.EscapeDataString(filter)}");
        if (characterId is { } selectedCharacterId)
            query.Add($"characterId={selectedCharacterId}");
        if (!string.IsNullOrWhiteSpace(snapshotId))
            query.Add($"snapshotId={Uri.EscapeDataString(snapshotId)}");

        return $"inventory?{string.Join("&", query)}";
    }
}
