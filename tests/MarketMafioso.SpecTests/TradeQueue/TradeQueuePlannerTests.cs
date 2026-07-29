using MarketMafioso.TradeQueue;
using MarketMafioso.Windows.TradeQueue;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.TradeQueue;

public sealed class TradeQueuePlannerTests
{
    [Fact]
    public void Planner_EnforcesInventoryBatchingEvidenceAndWorkshopHandoffContracts()
    {
        ValidateRequiresExactQualityAndEnoughTradeableInventory();
        BuildNextBatchSplitsSourceStacksAndStopsAtFiveSlots();
        InventoryDeltaAndCompletedBatchRequireExactEvidence();
        GilUsesCurrencyCapacityWithoutConsumingAnItemSlot();
        WorkshopHandoffExportsAvailableFinalMaterialsAndCapsAtRequirement();
        InventoryProjectionGroupsQueuedRowsBeforeAvailableInventory();
    }

    private static void ValidateRequiresExactQualityAndEnoughTradeableInventory()
    {
        var queue = new List<TradeQueueItem>
        {
            new() { ItemId = 100, ItemName = "Cobalt Ingot", IsHighQuality = false, Quantity = 5 },
        };
        var inventory = new List<TradeQueueInventoryStack>
        {
            Stack(0, 0, 100, "Cobalt Ingot", hq: true, 99),
            Stack(0, 1, 100, "Cobalt Ingot", hq: false, 4),
        };

        var result = TradeQueuePlanner.Validate(queue, inventory);

        Assert.False(result.Success);
        Assert.Equal(TradeQueueValidationCode.InsufficientInventory, result.Code);
        Assert.Contains("only 4", result.Message);
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
                Assert.False(item.IsHighQuality);
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
            Stack(0, 3, 200, "Adamantoise Shell", hq: false, 5),
        };
        var queue = new List<TradeQueueItem>
        {
            new() { ItemId = 400, ItemName = "Zinc Ore", Quantity = 2 },
            new() { ItemId = 300, ItemName = "Cobalt Ingot", IsHighQuality = true, Quantity = 4 },
            new() { ItemId = 500, ItemName = "Birch Lumber", Quantity = 1 },
        };

        var rows = TradeQueueInventoryProjection.Build(inventory, queue);

        Assert.Equal(
            ["Birch Lumber", "Cobalt Ingot", "Zinc Ore", "Adamantoise Shell", "Apple"],
            rows.Select(row => row.ItemName));
        Assert.Equal([1, 4, 2, 0, 0], rows.Select(row => row.SelectedQuantity));
        Assert.Equal(0, rows[0].AvailableQuantity);
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
