namespace MarketMafioso.Server;

public static class InventoryBrowserViewBuilder
{
    public static InventoryBrowserView Build(StoredInventoryReport? stored, string? search)
    {
        var normalizedSearch = search?.Trim() ?? string.Empty;
        if (stored == null)
            return new InventoryBrowserView { Search = normalizedSearch };

        var locations = EnumerateLocations(stored.Report)
            .Where(x => MatchesSearch(x, normalizedSearch))
            .ToList();
        var items = locations
            .GroupBy(x => x.ItemId)
            .Select(group =>
            {
                var itemLocations = group
                    .GroupBy(x => new { x.OwnerName, x.BagName })
                    .Select(locationGroup => new InventoryBrowserLocationView
                    {
                        OwnerName = locationGroup.Key.OwnerName,
                        BagName = locationGroup.Key.BagName,
                        Quantity = checked((int)locationGroup.Sum(x => (long)x.Quantity)),
                        HqQuantity = checked((int)locationGroup.Sum(x => x.IsHQ ? (long)x.Quantity : 0)),
                    })
                    .OrderBy(x => x.OwnerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.BagName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var first = group.First();

                return new InventoryBrowserItemView
                {
                    ItemId = group.Key,
                    DisplayName = string.IsNullOrWhiteSpace(first.ItemName)
                        ? $"Item {group.Key}"
                        : first.ItemName,
                    TotalQuantity = checked((int)group.Sum(x => (long)x.Quantity)),
                    HqQuantity = checked((int)group.Sum(x => x.IsHQ ? (long)x.Quantity : 0)),
                    Locations = itemLocations,
                };
            })
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ItemId)
            .ToList();

        return new InventoryBrowserView
        {
            SnapshotId = stored.Id,
            ReceivedAt = stored.ReceivedAt,
            CharacterName = stored.Report.CharacterName,
            HomeWorld = stored.Report.HomeWorld,
            Search = normalizedSearch,
            Items = items,
            TotalQuantity = checked((int)items.Sum(x => (long)x.TotalQuantity)),
            HqQuantity = checked((int)items.Sum(x => (long)x.HqQuantity)),
            OwnerCount = items.SelectMany(x => x.Locations).Select(x => x.OwnerName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        };
    }

    private static bool MatchesSearch(ItemLocation location, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return location.ItemId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
               (location.ItemName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static IEnumerable<ItemLocation> EnumerateLocations(InventoryReport report)
    {
        foreach (var bag in report.PlayerInventory)
        {
            foreach (var item in bag.Items)
            {
                yield return new ItemLocation(
                    "Player Inventory",
                    bag.BagName,
                    item.ItemId,
                    item.ItemName,
                    checked((int)item.Quantity),
                    item.IsHQ);
            }
        }

        foreach (var retainer in report.Retainers)
        {
            foreach (var bag in retainer.Bags)
            {
                foreach (var item in bag.Items)
                {
                    yield return new ItemLocation(
                        retainer.RetainerName,
                        bag.BagName,
                        item.ItemId,
                        item.ItemName,
                        checked((int)item.Quantity),
                        item.IsHQ);
                }
            }
        }
    }

    private sealed record ItemLocation(
        string OwnerName,
        string BagName,
        uint ItemId,
        string? ItemName,
        int Quantity,
        bool IsHQ);
}
