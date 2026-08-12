using MarketMafioso.Automation.Inventory;

namespace MarketMafioso.SpecTests;

public sealed class InventoryScannerTests
{
    [Fact]
    public void Workshop_usable_inventory_excludes_saddlebag_stock()
    {
        const uint material = 42;
        var snapshots = new[]
        {
            Container("Inventory1", Slot(material, 2)),
            Container("Inventory2"),
            Container("Inventory3"),
            Container("Inventory4"),
            Container("SaddleBag1", Slot(material, 4_815)),
        };

        var counts = InventoryScanner.CountWorkshopUsableInventory(snapshots);

        Assert.Equal(2, counts[material]);
    }

    [Fact]
    public void Workshop_usable_inventory_aggregates_all_four_player_bags()
    {
        const uint material = 42;
        var snapshots = new[]
        {
            Container("Inventory1", Slot(material, 1)),
            Container("Inventory2", Slot(material, 2)),
            Container("Inventory3", Slot(material, 3)),
            Container("Inventory4", Slot(material, 4)),
        };

        var counts = InventoryScanner.CountWorkshopUsableInventory(snapshots);

        Assert.Equal(10, counts[material]);
    }

    private static AutomationInventoryContainerSnapshot Container(
        string name,
        params AutomationInventorySlot[] slots) =>
        new(name, IsLoaded: true, SlotCount: 35, slots);

    private static AutomationInventorySlot Slot(uint itemId, int quantity) =>
        new(SlotIndex: 0, ItemId: itemId, Quantity: quantity, IsHighQuality: false);
}
