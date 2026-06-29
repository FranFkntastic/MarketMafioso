namespace MarketMafioso.Tests.MarketAcquisition;

internal static class MarketAcquisitionTestPlans
{
    public static MarketMafioso.MarketAcquisition.MarketAcquisitionPlan MultiLineSingleWorld() =>
        MultiLineTwoWorlds(firstWorldHasOnlyFirstLine: false) with
        {
            WorldBatches =
            [
                CreateWorldBatch("Siren", includeFirstLine: true, includeSecondLine: true),
            ],
        };

    public static MarketMafioso.MarketAcquisition.MarketAcquisitionPlan MultiLineTwoWorlds(
        bool firstWorldHasOnlyFirstLine = false) =>
        new()
        {
            RequestId = "batch-1",
            Status = "Ready",
            WorldMode = "Recommended",
            ItemId = 2,
            RequestedQuantity = 999,
            PlannedQuantity = 30,
            PlannedGil = 3_000,
            PreparedAtUtc = DateTimeOffset.UnixEpoch,
            Lines =
            [
                CreateLine("batch-1-line-1", 0, 2, "Fire Shard", 10, 1_000),
                CreateLine("batch-1-line-2", 1, 4, "Lightning Shard", 20, 2_000),
            ],
            WorldBatches =
            [
                CreateWorldBatch("Siren", includeFirstLine: true, includeSecondLine: !firstWorldHasOnlyFirstLine),
                CreateWorldBatch("Maduin", includeFirstLine: false, includeSecondLine: true),
            ],
        };

    public static MarketMafioso.MarketAcquisition.MarketAcquisitionLiveCandidatePlan ReadyCandidatePlan(
        uint quantity,
        uint gil) =>
        new()
        {
            Status = "Ready",
            Message = "Live candidate result.",
            RequestedQuantity = 999,
            WouldBuyQuantity = quantity,
            WouldSpendGil = gil,
            Rows = [],
        };

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionPlanLine CreateLine(
        string lineId,
        int ordinal,
        uint itemId,
        string itemName,
        uint plannedQuantity,
        uint plannedGil) =>
        new()
        {
            LineId = lineId,
            Ordinal = ordinal,
            ItemId = itemId,
            ItemName = itemName,
            QuantityMode = "TargetQuantity",
            RequestedQuantity = plannedQuantity,
            HqPolicy = "Either",
            MaxUnitPrice = 100,
            GilCap = 0,
            Status = "Ready",
            PlannedQuantity = plannedQuantity,
            PlannedGil = plannedGil,
        };

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionWorldBatch CreateWorldBatch(
        string worldName,
        bool includeFirstLine,
        bool includeSecondLine)
    {
        var subtasks = new List<MarketMafioso.MarketAcquisition.MarketAcquisitionWorldItemSubtask>();
        if (includeFirstLine)
            subtasks.Add(CreateSubtask("batch-1-line-1", 0, 2, "Fire Shard", worldName, 10, 1_000));
        if (includeSecondLine)
            subtasks.Add(CreateSubtask("batch-1-line-2", 1, 4, "Lightning Shard", worldName, 20, 2_000));

        return new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldBatch
        {
            WorldName = worldName,
            DataCenter = MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.ResolveNorthAmericaDataCenter(worldName),
            PlannedQuantity = (uint)subtasks.Sum(subtask => subtask.PlannedQuantity),
            PlannedGil = (uint)subtasks.Sum(subtask => subtask.PlannedGil),
            ItemSubtasks = subtasks,
            Listings = [],
        };
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionWorldItemSubtask CreateSubtask(
        string lineId,
        int lineOrdinal,
        uint itemId,
        string itemName,
        string worldName,
        uint plannedQuantity,
        uint plannedGil) =>
        new()
        {
            LineId = lineId,
            LineOrdinal = lineOrdinal,
            ItemId = itemId,
            ItemName = itemName,
            WorldName = worldName,
            DataCenter = MarketMafioso.MarketAcquisition.MarketAcquisitionPlanner.ResolveNorthAmericaDataCenter(worldName),
            QuantityMode = "TargetQuantity",
            RequestedQuantity = plannedQuantity,
            HqPolicy = "Either",
            MaxUnitPrice = 100,
            GilCap = 0,
            PlannedQuantity = plannedQuantity,
            PlannedGil = plannedGil,
            Listings = [],
        };
}
