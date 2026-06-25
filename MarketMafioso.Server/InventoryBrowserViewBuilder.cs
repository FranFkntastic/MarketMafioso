namespace MarketMafioso.Server;

public static class InventoryBrowserViewBuilder
{
    public static InventoryBrowserView Build(StoredInventoryReport? stored, string? search, string? scope = null)
    {
        var normalizedSearch = search?.Trim() ?? string.Empty;
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim();
        if (stored == null)
            return new InventoryBrowserView { Search = normalizedSearch, Scope = normalizedScope };

        var locations = EnumerateLocations(stored.Report)
            .Where(x => normalizedScope.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                        x.OwnerName.Equals(normalizedScope, StringComparison.OrdinalIgnoreCase))
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
                    ItemType = first.ItemType,
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
            Scope = normalizedScope,
            Items = items,
            Scopes = BuildScopes(stored.Report),
            MarketListings = BuildMarketListings(stored.Report, normalizedScope),
            TotalQuantity = checked((int)items.Sum(x => (long)x.TotalQuantity)),
            HqQuantity = checked((int)items.Sum(x => (long)x.HqQuantity)),
            OwnerCount = items.SelectMany(x => x.Locations).Select(x => x.OwnerName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            RetainerGil = GetRetainerGil(stored.Report, normalizedScope),
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
                    item.ItemType,
                    checked((int)item.Quantity),
                    item.IsHQ);
            }
        }

        foreach (var retainer in report.Retainers)
        {
            foreach (var bag in retainer.Bags)
            {
                if (IsNonInventoryRetainerBag(bag.BagName))
                    continue;

                foreach (var item in bag.Items)
                {
                    yield return new ItemLocation(
                        retainer.RetainerName,
                        bag.BagName,
                        item.ItemId,
                        item.ItemName,
                        item.ItemType,
                        checked((int)item.Quantity),
                        item.IsHQ);
                }
            }
        }
    }

    private static IReadOnlyList<InventoryBrowserScopeView> BuildScopes(InventoryReport report)
    {
        var scopes = new List<InventoryBrowserScopeView>
        {
            new()
            {
                ScopeKey = "Player Inventory",
                DisplayName = "Player Inventory",
                Description = "Player bags and configured inventory sections",
                StackCount = report.PlayerInventory.SelectMany(b => b.Items).Count(),
                LastUpdated = report.Timestamp,
            },
        };

        scopes.AddRange(report.Retainers.Select(retainer => new InventoryBrowserScopeView
        {
            ScopeKey = retainer.RetainerName,
            DisplayName = retainer.RetainerName,
            Description = "Retainer inventory",
            StackCount = retainer.Bags
                .Where(bag => !IsNonInventoryRetainerBag(bag.BagName))
                .SelectMany(bag => bag.Items)
                .Count(),
            Gil = retainer.Gil,
            MarketListingCount = retainer.MarketListings.Count + CountLegacyMarketListings(retainer),
            LastUpdated = retainer.LastUpdated,
        }));

        return scopes;
    }

    private static IReadOnlyList<InventoryBrowserMarketListingView> BuildMarketListings(InventoryReport report, string scope)
    {
        return report.Retainers
            .Where(retainer => scope.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                               retainer.RetainerName.Equals(scope, StringComparison.OrdinalIgnoreCase))
            .SelectMany(retainer => retainer.MarketListings.Select(listing => new InventoryBrowserMarketListingView
            {
                OwnerName = retainer.RetainerName,
                ItemId = listing.ItemId,
                DisplayName = string.IsNullOrWhiteSpace(listing.ItemName)
                    ? $"Item {listing.ItemId}"
                    : listing.ItemName,
                ItemType = listing.ItemType,
                Quantity = checked((int)listing.Quantity),
                HqQuantity = listing.IsHQ ? checked((int)listing.Quantity) : 0,
                UnitPrice = listing.UnitPrice,
                ListedAt = listing.ListedAt ?? retainer.LastUpdated,
            }))
            .ToList();
    }

    private static ulong GetRetainerGil(InventoryReport report, string scope)
    {
        return scope.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? report.Retainers.Aggregate(0UL, (sum, retainer) => sum + retainer.Gil)
            : report.Retainers
                .Where(retainer => retainer.RetainerName.Equals(scope, StringComparison.OrdinalIgnoreCase))
                .Aggregate(0UL, (sum, retainer) => sum + retainer.Gil);
    }

    private static bool IsNonInventoryRetainerBag(string bagName) =>
        bagName.Equals("RetainerGil", StringComparison.OrdinalIgnoreCase) ||
        bagName.Equals("RetainerMarket", StringComparison.OrdinalIgnoreCase);

    private static int CountLegacyMarketListings(RetainerReport retainer) =>
        retainer.Bags
            .Where(bag => bag.BagName.Equals("RetainerMarket", StringComparison.OrdinalIgnoreCase))
            .SelectMany(bag => bag.Items)
            .Count();

    private sealed record ItemLocation(
        string OwnerName,
        string BagName,
        uint ItemId,
        string? ItemName,
        string? ItemType,
        int Quantity,
        bool IsHQ);
}
