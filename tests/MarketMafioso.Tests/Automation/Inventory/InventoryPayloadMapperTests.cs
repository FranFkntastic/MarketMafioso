using MarketMafioso.Automation.Inventory;
using MarketMafioso.Automation.Items;

namespace MarketMafioso.Tests.Automation.Inventory;

public sealed class InventoryPayloadMapperTests
{
    [Fact]
    public void MapInventoryBags_preserves_physical_stacks_and_slot_evidence()
    {
        var snapshot = CreateSnapshot(
            "Inventory1",
            new AutomationInventorySlot(0, 100, 5, false, 0.25f, 25f),
            new AutomationInventorySlot(1, 100, 7, false, 0.80f, 80f));

        var bags = InventoryPayloadMapper.MapInventoryBags([snapshot], includeItemNames: true, ResolveItemName);

        var bag = Assert.Single(bags);
        Assert.Equal("Inventory", bag.Location);
        Assert.Collection(
            bag.Items,
            item =>
            {
                Assert.Equal(100u, item.ItemId);
                Assert.Equal(5u, item.Quantity);
                Assert.Equal("Inventory1", item.ContainerKey);
                Assert.Equal(0, item.SlotIndex);
                Assert.Equal(25f, item.ConditionPercent);
                Assert.Equal("Item 100", item.ItemName);
            },
            item =>
            {
                Assert.Equal(7u, item.Quantity);
                Assert.Equal(1, item.SlotIndex);
                Assert.Equal(80f, item.ConditionPercent);
            });
    }

    [Fact]
    public void MapInventoryBags_preserves_each_stacks_quality()
    {
        var snapshot = CreateSnapshot(
            "Inventory1",
            new AutomationInventorySlot(0, 100, 5, false, 0.25f),
            new AutomationInventorySlot(1, 100, 7, true, 0.80f));

        var items = Assert.Single(InventoryPayloadMapper.MapInventoryBags([snapshot], includeItemNames: true, ResolveItemName)).Items;

        Assert.Collection(items, item => Assert.False(item.IsHQ), item => Assert.True(item.IsHQ));
    }

    [Fact]
    public void MapInventoryBags_adds_catalog_details_and_omits_inapplicable_condition()
    {
        var snapshot = CreateSnapshot("Inventory1", new AutomationInventorySlot(0, 100, 5, false, 0, 0));

        var item = Assert.Single(Assert.Single(InventoryPayloadMapper.MapInventoryBags(
            [snapshot],
            includeItemNames: true,
            ResolveItemName,
            itemId => new AutomationItemMetadata(
                new AutomationItemIdentity(itemId, "Cobalt Ingot", false),
                MaxStack: 999,
                ItemType: "Metal",
                SupportsCondition: false))).Items);

        Assert.Equal("Cobalt Ingot", item.ItemName);
        Assert.Equal("Metal", item.ItemType);
        Assert.Null(item.ConditionPercent);
    }

    [Fact]
    public void MapInventoryBags_does_not_resolve_names_when_names_are_disabled()
    {
        var resolved = false;
        var snapshot = CreateSnapshot("Inventory1", new AutomationInventorySlot(0, 100, 5, false));

        var item = Assert.Single(Assert.Single(InventoryPayloadMapper.MapInventoryBags(
            [snapshot],
            includeItemNames: false,
            itemId =>
            {
                resolved = true;
                return $"Item {itemId}";
            })).Items);

        Assert.Null(item.ItemName);
        Assert.False(resolved);
    }

    [Fact]
    public void MapInventoryBags_omits_empty_loaded_containers()
    {
        var snapshot = new AutomationInventoryContainerSnapshot(
            "Inventory1",
            IsLoaded: true,
            SlotCount: 35,
            Slots: []);

        var bags = InventoryPayloadMapper.MapInventoryBags([snapshot], includeItemNames: true, ResolveItemName);

        Assert.Empty(bags);
    }

    [Fact]
    public void MapRetainerInventoryBags_merges_retainer_pages_into_single_bag()
    {
        var pageOne = CreateSnapshot("RetainerPage1", new AutomationInventorySlot(0, 100, 5, false, 0.25f));
        var pageTwo = CreateSnapshot("RetainerPage2", new AutomationInventorySlot(0, 100, 7, false, 0.80f));

        var bags = InventoryPayloadMapper.MapRetainerInventoryBags([pageOne, pageTwo], includeItemNames: true, ResolveItemName);

        var bag = Assert.Single(bags);
        Assert.Equal("Retainer", bag.Location);
        var items = bag.Items;
        Assert.Equal("RetainerInventory", bag.BagName);
        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal(5u, item.Quantity);
                Assert.Equal("RetainerPage1", item.ContainerKey);
                Assert.Equal(0, item.SlotIndex);
            },
            item =>
            {
                Assert.Equal(7u, item.Quantity);
                Assert.Equal("RetainerPage2", item.ContainerKey);
                Assert.Equal(0, item.SlotIndex);
            });
    }

    [Fact]
    public void MapRetainerInventoryBags_preserves_empty_loaded_crystal_container()
    {
        var crystals = new AutomationInventoryContainerSnapshot(
            "RetainerCrystals",
            IsLoaded: true,
            SlotCount: 18,
            Slots: []);

        var bag = Assert.Single(InventoryPayloadMapper.MapRetainerInventoryBags(
            [crystals],
            includeItemNames: true,
            ResolveItemName));

        Assert.Equal("RetainerCrystals", bag.BagName);
        Assert.Empty(bag.Items);
    }

    [Fact]
    public void MapRetainerMarketListings_keeps_condition_and_listed_time()
    {
        var listedAt = new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = CreateSnapshot("RetainerMarket", new AutomationInventorySlot(0, 100, 5, true, 0.35f, 35f));

        var listing = Assert.Single(InventoryPayloadMapper.MapRetainerMarketListings(
            [snapshot],
            includeItemNames: true,
            ResolveItemName,
            listedAt));

        Assert.Equal(100u, listing.ItemId);
        Assert.Equal("Item 100", listing.ItemName);
        Assert.Equal(5u, listing.Quantity);
        Assert.True(listing.IsHQ);
        Assert.Equal(0.35f, listing.Condition);
        Assert.Equal(35f, listing.ConditionPercent);
        Assert.Equal("RetainerMarket", listing.ContainerKey);
        Assert.Equal(0, listing.SlotIndex);
        Assert.Equal("2026-07-02T12:00:00.0000000Z", listing.ListedAt);
    }

    private static AutomationInventoryContainerSnapshot CreateSnapshot(
        string containerName,
        params AutomationInventorySlot[] slots) =>
        new(containerName, IsLoaded: true, SlotCount: 35, Slots: slots);

    private static string ResolveItemName(uint itemId) => $"Item {itemId}";
}
