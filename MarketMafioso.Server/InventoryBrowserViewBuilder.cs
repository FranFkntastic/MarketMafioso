namespace MarketMafioso.Server;

public static class InventoryBrowserViewBuilder
{
    public static InventoryBrowserView Build(StoredInventoryReport? stored, string? search, string? scope)
    {
        var normalizedSearch = search?.Trim() ?? string.Empty;
        var normalizedScope = NormalizeScope(scope);
        if (stored == null)
        {
            return new InventoryBrowserView
            {
                Search = normalizedSearch,
                Scope = normalizedScope,
                Scopes = [CreateEmptyScope(InventoryBrowserScopeValue.All, "All Inventories", "all")],
            };
        }

        var locations = EnumerateLocations(stored.Report)
            .Where(x => MatchesSearch(x, normalizedSearch))
            .ToList();
        var scopes = CreateScopes(locations);
        if (scopes.All(x => !string.Equals(x.Value, normalizedScope, StringComparison.OrdinalIgnoreCase)))
            normalizedScope = InventoryBrowserScopeValue.All;

        var scopedLocations = locations
            .Where(x => ScopeMatches(x, normalizedScope))
            .ToList();
        var items = locations
            .Where(x => ScopeMatches(x, normalizedScope))
            .GroupBy(x => x.ItemId)
            .Select(group =>
            {
                var itemLocations = group
                    .GroupBy(x => new { x.OwnerName, x.BagName })
                    .Select(locationGroup => new InventoryBrowserLocationView
                    {
                        ScopeValue = locationGroup.First().ScopeValue,
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
            Scopes = scopes,
            Items = items,
            TotalQuantity = checked((int)items.Sum(x => (long)x.TotalQuantity)),
            HqQuantity = checked((int)items.Sum(x => (long)x.HqQuantity)),
            OwnerCount = scopedLocations.Select(x => x.OwnerName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        };
    }

    private static string NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return InventoryBrowserScopeValue.All;

        return scope.Trim();
    }

    private static bool ScopeMatches(ItemLocation location, string scope)
    {
        return string.Equals(scope, InventoryBrowserScopeValue.All, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(location.ScopeValue, scope, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<InventoryBrowserScopeView> CreateScopes(IReadOnlyList<ItemLocation> locations)
    {
        var scopes = new List<InventoryBrowserScopeView>
        {
            new()
            {
                Value = InventoryBrowserScopeValue.All,
                DisplayName = "All Inventories",
                ScopeType = "all",
                ItemCount = locations.Select(x => x.ItemId).Distinct().Count(),
                TotalQuantity = checked((int)locations.Sum(x => (long)x.Quantity)),
            },
        };

        scopes.Add(CreateScope(
            InventoryBrowserScopeValue.Player,
            "Player Inventory",
            "player",
            locations.Where(x => x.ScopeValue == InventoryBrowserScopeValue.Player)));

        scopes.AddRange(locations
            .Where(x => x.OwnerType == "retainer")
            .GroupBy(x => x.ScopeValue)
            .OrderBy(group => group.First().OwnerName, StringComparer.OrdinalIgnoreCase)
            .Select(group => CreateScope(group.Key, group.First().OwnerName, "retainer", group)));

        return scopes;
    }

    private static InventoryBrowserScopeView CreateScope(
        string value,
        string displayName,
        string scopeType,
        IEnumerable<ItemLocation> locations)
    {
        var materialized = locations.ToList();
        return new InventoryBrowserScopeView
        {
            Value = value,
            DisplayName = displayName,
            ScopeType = scopeType,
            ItemCount = materialized.Select(x => x.ItemId).Distinct().Count(),
            TotalQuantity = checked((int)materialized.Sum(x => (long)x.Quantity)),
        };
    }

    private static InventoryBrowserScopeView CreateEmptyScope(string value, string displayName, string scopeType) =>
        new()
        {
            Value = value,
            DisplayName = displayName,
            ScopeType = scopeType,
        };

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
                    item.IsHQ,
                    InventoryBrowserScopeValue.Player,
                    "player");
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
                        item.ItemType,
                        checked((int)item.Quantity),
                        item.IsHQ,
                        CreateRetainerScopeValue(retainer),
                        "retainer");
                }
            }
        }
    }

    private static string CreateRetainerScopeValue(RetainerReport retainer) =>
        retainer.RetainerId == 0
            ? $"retainer:{retainer.RetainerName}"
            : $"retainer:{retainer.RetainerId}";

    private sealed record ItemLocation(
        string OwnerName,
        string BagName,
        uint ItemId,
        string? ItemName,
        string? ItemType,
        int Quantity,
        bool IsHQ,
        string ScopeValue,
        string OwnerType);
}
