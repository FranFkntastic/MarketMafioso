namespace MarketMafioso.Server;

public static class InventorySnapshotViewBuilder
{
    public static InventorySnapshotView Build(StoredInventoryReport stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var playerInventory = BuildPlayerInventory(stored.Report.PlayerInventory);
        var retainers = stored.Report.Retainers
            .Select(BuildRetainer)
            .ToList();

        var retainerStacks = retainers.Sum(r => r.Stacks);
        var retainerQuantity = retainers.Sum(r => r.Quantity);
        var retainerHqStacks = retainers.Sum(r => r.HqStacks);

        return new InventorySnapshotView
        {
            Id = stored.Id,
            Metadata = BuildMetadata(stored.Report.Metadata),
            ReceivedAt = stored.ReceivedAt,
            CharacterName = stored.Report.CharacterName,
            HomeWorld = stored.Report.HomeWorld,
            ReportTimestamp = stored.Report.Timestamp,
            PlayerInventory = playerInventory,
            Retainers = retainers,
            Totals = new InventorySnapshotTotals
            {
                Stacks = playerInventory.Stacks + retainerStacks,
                Quantity = checked(playerInventory.Quantity + retainerQuantity),
                HqStacks = playerInventory.HqStacks + retainerHqStacks,
                PlayerStacks = playerInventory.Stacks,
                PlayerQuantity = playerInventory.Quantity,
                RetainerStacks = retainerStacks,
                RetainerQuantity = retainerQuantity,
                Retainers = retainers.Count,
            },
        };
    }

    private static InventorySnapshotMetadata BuildMetadata(InventoryReportMetadata? metadata)
    {
        if (metadata == null)
            return new InventorySnapshotMetadata();

        return new InventorySnapshotMetadata
        {
            SchemaVersion = metadata.SchemaVersion,
            SourcePlugin = string.IsNullOrWhiteSpace(metadata.SourcePlugin)
                ? "Unknown"
                : metadata.SourcePlugin,
            PluginVersion = string.IsNullOrWhiteSpace(metadata.PluginVersion)
                ? "Unknown"
                : metadata.PluginVersion,
            GeneratedAtUtc = string.IsNullOrWhiteSpace(metadata.GeneratedAtUtc)
                ? "Unknown"
                : metadata.GeneratedAtUtc,
        };
    }

    private static InventoryOwnerView BuildPlayerInventory(IReadOnlyList<InventoryBag> bags) =>
        BuildOwner("Player Inventory", null, null, bags);

    private static InventoryOwnerView BuildRetainer(RetainerReport retainer) =>
        BuildOwner(
            retainer.RetainerName,
            retainer.RetainerId,
            retainer.LastUpdated,
            retainer.Bags);

    private static InventoryOwnerView BuildOwner(
        string name,
        ulong? retainerId,
        string? lastUpdated,
        IReadOnlyList<InventoryBag> bags)
    {
        var bagViews = bags.Select(BuildBag).ToList();

        return new InventoryOwnerView
        {
            Name = name,
            RetainerId = retainerId,
            LastUpdated = lastUpdated,
            Bags = bagViews,
            Stacks = bagViews.Sum(b => b.Stacks),
            Quantity = checked(bagViews.Sum(b => b.Quantity)),
            HqStacks = bagViews.Sum(b => b.HqStacks),
        };
    }

    private static InventoryBagView BuildBag(InventoryBag bag)
    {
        var items = bag.Items
            .Select(i => new InventoryItemView
            {
                ItemId = i.ItemId,
                DisplayName = string.IsNullOrWhiteSpace(i.ItemName)
                    ? $"Item {i.ItemId}"
                    : i.ItemName,
                Quantity = i.Quantity,
                IsHQ = i.IsHQ,
                Condition = i.Condition,
            })
            .ToList();

        return new InventoryBagView
        {
            Name = bag.BagName,
            Items = items,
            Stacks = items.Count,
            Quantity = checked((int)items.Sum(i => (long)i.Quantity)),
            HqStacks = items.Count(i => i.IsHQ),
        };
    }
}
