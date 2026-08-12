using MarketMafioso.TradeQueue;
using MarketMafioso.Windows.TradeQueue;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.TradeQueue;

public sealed class TradeQueuePlannerTests
{
    [Theory]
    [InlineData(TradeQueuePanel.QueuedColumnWidth, 110f)]
    [InlineData(1f, 1f)]
    public void Quantity_editor_owns_the_left_half_of_the_queued_cell(float availableWidth, float expected)
    {
        Assert.Equal(expected, TradeQueuePanel.ResolveQuantityEditTargetWidth(availableWidth));
    }

    [Theory]
    [InlineData(-1, 36, 0)]
    [InlineData(12, 36, 12)]
    [InlineData(40, 36, 36)]
    public void Granular_quantity_clamps_to_the_observed_available_range(int requested, int available, int expected)
    {
        Assert.Equal(expected, TradeQueuePanel.ClampQueuedQuantity(requested, available));
    }

    [Fact]
    public void Quantity_tab_advance_skips_gil_and_wraps_through_editable_rows()
    {
        TradeQueueInventoryRow[] rows =
        [
            new(new TradeQueueItemKey(5366), "Cedar Lumber", 45, 0),
            new(new TradeQueueItemKey(TradeQueuePlanner.GilItemId), "Gil", 100, 0),
            new(new TradeQueueItemKey(11), "Earth Crystal", 4, 0),
        ];

        Assert.Equal(11u, TradeQueuePanel.FindNextEditableQuantityRow(rows, 5366)?.Key.ItemId);
        Assert.Equal(5366u, TradeQueuePanel.FindNextEditableQuantityRow(rows, 11)?.Key.ItemId);
        Assert.Null(TradeQueuePanel.FindNextEditableQuantityRow(rows, 999));
    }

    [Fact]
    public void Planner_EnforcesInventoryBatchingEvidenceAndWorkshopHandoffContracts()
    {
        ValidateCountsHqAndNqTogether();
        BuildNextBatchSplitsSourceStacksAndStopsAtFiveSlots();
        InventoryDeltaAndCompletedBatchRequireExactEvidence();
        GilUsesCurrencyCapacityWithoutConsumingAnItemSlot();
        WorkshopHandoffExportsAvailableFinalMaterialsAndCapsAtRequirement();
        InventoryProjectionGroupsQueuedRowsBeforeAvailableInventory();
        BulkEditQueuesCurrentStockWithoutTouchingGilOrUnobservedLines();
        BulkEditSetsOneClampedQuantityAcrossSelectedRows();
        BulkEditRemovesOnlySelectedObservableRows();
    }

    private static void ValidateCountsHqAndNqTogether()
    {
        var queue = new List<TradeQueueItem>
        {
            new() { ItemId = 100, ItemName = "Cobalt Ingot", Quantity = 5 },
        };
        var inventory = new List<TradeQueueInventoryStack>
        {
            Stack(0, 0, 100, "Cobalt Ingot", hq: true, 99),
            Stack(0, 1, 100, "Cobalt Ingot", hq: false, 4),
        };

        var result = TradeQueuePlanner.Validate(queue, inventory);

        Assert.True(result.Success);
        var exception = Assert.Throws<InvalidOperationException>(
            () => TradeQueuePlanner.BuildNextBatch(queue, inventory));
        Assert.Contains("after quality normalization", exception.Message);
    }

    private static void BuildNextBatchSplitsSourceStacksAndStopsAtFiveSlots()
    {
        var queue = new List<TradeQueueItem>
        {
            new() { ItemId = 100, ItemName = "Cobalt Ingot", Quantity = 10 },
        };
        var inventory = Enumerable.Range(0, 6)
            .Select(index => Stack(0, index, 100, "Cobalt Ingot", hq: false, 2))
            .ToList();

        var batch = TradeQueuePlanner.BuildNextBatch(queue, inventory);

        Assert.Equal(5, batch.SlotCount);
        Assert.Equal(10, batch.UnitCount);
        Assert.Equal([0, 1, 2, 3, 4], batch.Lines.Select(line => line.SlotIndex));
    }

    private static void InventoryDeltaAndCompletedBatchRequireExactEvidence()
    {
        var queue = new List<TradeQueueItem>
        {
            new() { ItemId = 100, ItemName = "Cobalt Ingot", Quantity = 7 },
        };
        var before = new List<TradeQueueInventoryStack>
        {
            Stack(0, 0, 100, "Cobalt Ingot", hq: false, 5),
            Stack(0, 1, 100, "Cobalt Ingot", hq: false, 2),
        };
        var batch = TradeQueuePlanner.BuildNextBatch(queue, before, maximumSlots: 1);
        var after = new List<TradeQueueInventoryStack>
        {
            Stack(0, 1, 100, "Cobalt Ingot", hq: false, 2),
        };

        Assert.True(TradeQueuePlanner.HasExpectedInventoryDelta(batch, after, out _));
        TradeQueuePlanner.ApplyCompletedBatch(queue, batch);
        Assert.Equal(2, Assert.Single(queue).Quantity);

        var unchanged = TradeQueuePlanner.HasExpectedInventoryDelta(batch, before, out var diagnostic);
        Assert.False(unchanged);
        Assert.Contains("observed 0", diagnostic);
    }

    private static void WorkshopHandoffExportsAvailableFinalMaterialsAndCapsAtRequirement()
    {
        var result = WorkshopTradeQueueHandoffService.Build(
        [
            Availability(100, "Cobalt Ingot", required: 20, playerInventory: 25),
            Availability(200, "Darksteel Ingot", required: 10, playerInventory: 4),
            Availability(300, "Garlean Steel Joint", required: 8, playerInventory: 0),
        ]);

        Assert.True(result.Success);
        Assert.Collection(
            result.Items,
            item =>
            {
                Assert.Equal("Cobalt Ingot", item.ItemName);
                Assert.Equal(20, item.Quantity);
            },
            item =>
            {
                Assert.Equal("Darksteel Ingot", item.ItemName);
                Assert.Equal(4, item.Quantity);
            });
    }

    private static void GilUsesCurrencyCapacityWithoutConsumingAnItemSlot()
    {
        var queue = new List<TradeQueueItem>
        {
            new() { ItemId = TradeQueuePlanner.GilItemId, ItemName = "Gil", Quantity = 1_500_000 },
        };
        var before = new List<TradeQueueInventoryStack>
        {
            Stack(uint.MaxValue, -1, TradeQueuePlanner.GilItemId, "Gil", hq: false, 3_757_109),
        };

        var batch = TradeQueuePlanner.BuildNextBatch(queue, before);

        Assert.Equal(1_000_000, batch.GilAmount);
        Assert.Equal(0, batch.SlotCount);
        var after = new List<TradeQueueInventoryStack>
        {
            Stack(uint.MaxValue, -1, TradeQueuePlanner.GilItemId, "Gil", hq: false, 2_757_109),
        };
        Assert.True(TradeQueuePlanner.HasExpectedInventoryDelta(batch, after, out var diagnostic));
        Assert.Contains("1,000,000 gil", diagnostic);
        TradeQueuePlanner.ApplyCompletedBatch(queue, batch);
        Assert.Equal(500_000, Assert.Single(queue).Quantity);
    }

    private static void InventoryProjectionGroupsQueuedRowsBeforeAvailableInventory()
    {
        var inventory = new List<TradeQueueInventoryStack>
        {
            Stack(0, 0, 400, "Zinc Ore", hq: false, 9),
            Stack(0, 1, 100, "Apple", hq: false, 3),
            Stack(0, 2, 300, "Cobalt Ingot", hq: true, 7),
            Stack(0, 4, 300, "Cobalt Ingot", hq: false, 2),
            Stack(0, 3, 200, "Adamantoise Shell", hq: false, 5),
        };
        var queue = new List<TradeQueueItem>
        {
            new() { ItemId = 400, ItemName = "Zinc Ore", Quantity = 2 },
            new() { ItemId = 300, ItemName = "Cobalt Ingot", Quantity = 4 },
            new() { ItemId = 500, ItemName = "Birch Lumber", Quantity = 1 },
        };

        var rows = TradeQueueInventoryProjection.Build(inventory, queue);

        Assert.Equal(
            ["Birch Lumber", "Cobalt Ingot", "Zinc Ore", "Adamantoise Shell", "Apple"],
            rows.Select(row => row.ItemName));
        Assert.Equal([1, 4, 2, 0, 0], rows.Select(row => row.SelectedQuantity));
        Assert.Equal(0, rows[0].AvailableQuantity);
        Assert.Equal(9, rows[1].AvailableQuantity);
    }

    private static void BulkEditQueuesCurrentStockWithoutTouchingGilOrUnobservedLines()
    {
        var inventory = new List<TradeQueueInventoryStack>
        {
            Stack(0, 0, 100, "Apple", hq: false, 3),
            Stack(0, 1, 400, "Zinc Ore", hq: false, 9),
            Stack(uint.MaxValue, -1, TradeQueuePlanner.GilItemId, "Gil", hq: false, 5_000),
        };
        var queue = new List<TradeQueueItem>
        {
            new() { ItemId = 400, ItemName = "Zinc Ore", Quantity = 2 },
            new() { ItemId = 500, ItemName = "Birch Lumber", Quantity = 7 },
            new() { ItemId = TradeQueuePlanner.GilItemId, ItemName = "Gil", Quantity = 50 },
        };
        var rows = TradeQueueInventoryProjection.Build(inventory, queue);

        var updated = TradeQueueBulkEdit.Apply(
            queue,
            rows,
            new HashSet<uint> { 100, 400, 500, TradeQueuePlanner.GilItemId },
            TradeQueueBulkAction.QueueAllAvailable);

        Assert.Equal(3, updated.Single(item => item.ItemId == 100).Quantity);
        Assert.Equal(9, updated.Single(item => item.ItemId == 400).Quantity);
        Assert.Equal(7, updated.Single(item => item.ItemId == 500).Quantity);
        Assert.Equal(50, updated.Single(item => item.ItemId == TradeQueuePlanner.GilItemId).Quantity);
        Assert.Equal(2, queue.Single(item => item.ItemId == 400).Quantity);
    }

    private static void BulkEditRemovesOnlySelectedObservableRows()
    {
        var inventory = new List<TradeQueueInventoryStack>
        {
            Stack(0, 0, 100, "Apple", hq: false, 3),
            Stack(0, 1, 400, "Zinc Ore", hq: false, 9),
        };
        var queue = new List<TradeQueueItem>
        {
            new() { ItemId = 100, ItemName = "Apple", Quantity = 3 },
            new() { ItemId = 400, ItemName = "Zinc Ore", Quantity = 2 },
            new() { ItemId = 500, ItemName = "Birch Lumber", Quantity = 7 },
            new() { ItemId = TradeQueuePlanner.GilItemId, ItemName = "Gil", Quantity = 50 },
        };
        var rows = TradeQueueInventoryProjection.Build(inventory, queue);

        var updated = TradeQueueBulkEdit.Apply(
            queue,
            rows,
            new HashSet<uint> { 100, 500, TradeQueuePlanner.GilItemId },
            TradeQueueBulkAction.RemoveFromQueue);

        Assert.DoesNotContain(updated, item => item.ItemId == 100);
        Assert.Equal(2, updated.Single(item => item.ItemId == 400).Quantity);
        Assert.Equal(7, updated.Single(item => item.ItemId == 500).Quantity);
        Assert.Equal(50, updated.Single(item => item.ItemId == TradeQueuePlanner.GilItemId).Quantity);
    }

    private static void BulkEditSetsOneClampedQuantityAcrossSelectedRows()
    {
        var inventory = new List<TradeQueueInventoryStack>
        {
            Stack(0, 0, 100, "Apple", hq: false, 3),
            Stack(0, 1, 400, "Zinc Ore", hq: false, 9),
        };
        var queue = new List<TradeQueueItem>
        {
            new() { ItemId = 500, ItemName = "Birch Lumber", Quantity = 7 },
        };
        var rows = TradeQueueInventoryProjection.Build(inventory, queue);

        var updated = TradeQueueBulkEdit.Apply(
            queue,
            rows,
            new HashSet<uint> { 100, 400 },
            TradeQueueBulkAction.SetQuantity,
            quantity: 5);

        Assert.Equal(3, updated.Single(item => item.ItemId == 100).Quantity);
        Assert.Equal(5, updated.Single(item => item.ItemId == 400).Quantity);
        Assert.Equal(7, updated.Single(item => item.ItemId == 500).Quantity);
    }

    private static TradeQueueInventoryStack Stack(
        uint container,
        int slot,
        uint itemId,
        string name,
        bool hq,
        int quantity) =>
        new(container, slot, itemId, name, hq, quantity);

    private static WorkshopMaterialAvailability Availability(
        uint itemId,
        string name,
        int required,
        int playerInventory) =>
        new(
            itemId,
            name,
            0,
            required,
            playerInventory,
            0,
            Math.Max(0, required - playerInventory),
            Math.Max(0, required - playerInventory),
            []);
}
