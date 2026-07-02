using MarketMafioso.Automation.Inventory;

namespace MarketMafioso.Tests.Automation.Inventory;

public sealed class AutomationInventoryCapacityPlannerTests
{
    [Fact]
    public void CalculateCapacity_counts_partial_stacks_and_empty_slots()
    {
        var snapshot = new AutomationInventoryContainerSnapshot(
            "Inventory1",
            IsLoaded: true,
            SlotCount: 5,
            Slots:
            [
                new AutomationInventorySlot(0, 100, 100, false),
                new AutomationInventorySlot(1, 100, 998, false),
                new AutomationInventorySlot(2, 200, 10, false),
            ]);

        var capacity = AutomationInventoryCapacityPlanner.CalculateCapacity(
            [snapshot],
            itemId: 100,
            isHighQuality: false,
            maxStack: 999);

        Assert.True(capacity.IsKnown);
        Assert.Equal(899 + 1 + (2 * 999), capacity.AvailableQuantity);
        Assert.Equal(2, capacity.EmptySlots);
        Assert.Equal(2, capacity.PartialStackSlots);
    }

    [Fact]
    public void CalculateCapacity_marks_unknown_when_no_loaded_containers_exist()
    {
        var capacity = AutomationInventoryCapacityPlanner.CalculateCapacity(
            [],
            itemId: 100,
            isHighQuality: false,
            maxStack: 999);

        Assert.False(capacity.IsKnown);
        Assert.Equal(0, capacity.AvailableQuantity);
    }

    [Fact]
    public void CalculateCapacity_marks_unknown_when_all_containers_are_unloaded()
    {
        var snapshot = new AutomationInventoryContainerSnapshot(
            "Inventory1",
            IsLoaded: false,
            SlotCount: 5,
            Slots: []);

        var capacity = AutomationInventoryCapacityPlanner.CalculateCapacity(
            [snapshot],
            itemId: 100,
            isHighQuality: false,
            maxStack: 999);

        Assert.False(capacity.IsKnown);
        Assert.Equal(0, capacity.AvailableQuantity);
    }

    [Fact]
    public void CalculateCapacity_does_not_count_opposite_quality_partial_stack()
    {
        var snapshot = new AutomationInventoryContainerSnapshot(
            "Inventory1",
            IsLoaded: true,
            SlotCount: 2,
            Slots:
            [
                new AutomationInventorySlot(0, 100, 100, true),
            ]);

        var capacity = AutomationInventoryCapacityPlanner.CalculateCapacity(
            [snapshot],
            itemId: 100,
            isHighQuality: false,
            maxStack: 999);

        Assert.Equal(999, capacity.AvailableQuantity);
        Assert.Equal(1, capacity.EmptySlots);
        Assert.Equal(0, capacity.PartialStackSlots);
    }

    [Fact]
    public void CalculateCapacity_preserves_slot_condition_for_callers()
    {
        var slot = new AutomationInventorySlot(
            SlotIndex: 3,
            ItemId: 100,
            Quantity: 50,
            IsHighQuality: false,
            Condition: 0.75f);

        Assert.Equal(0.75f, slot.Condition);
    }
}
