using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

// A share restock spends gil against a raw bag target, while handover keeps min(bags, plan need) for the
// sender's own plan. If the two drift, a completed restock still shows the share short (its new units are
// re-kept for a short plan) and repeated restocks keep buying, with nothing in game saying why. This pins
// the translation, the post-restock handover outcome, and the Start/Tick signature it must reproduce.
public sealed class WorkshopShareRestockDemandTests
{
    [Fact]
    public void CompletedShareRestockLeavesTheOwedUnitsSpareAndKeepsItsSignature()
    {
        // bags, owed to the assembler, needed by the sender's own active plan
        var bags = new Dictionary<uint, int> { [1] = 240, [2] = 150, [3] = 160, [4] = 40, [5] = 100 };
        var owed = new[]
        {
            new WorkshopMaterialRequirement(1, "Ceruleum Tank", 0, 180),
            new WorkshopMaterialRequirement(2, "Hardsilver Nugget", 0, 180),
            new WorkshopMaterialRequirement(3, "Cobalt Ingot", 0, 160),
            new WorkshopMaterialRequirement(4, "Dark Chestnut Lumber", 0, 0),
            new WorkshopMaterialRequirement(5, "Mythrite Rivets", 0, 88),
        };
        var plan = new[]
        {
            new WorkshopMaterialRequirement(1, "Ceruleum Tank", 0, 180),
            new WorkshopMaterialRequirement(3, "Cobalt Ingot", 0, 400), // the plan itself is short here
            new WorkshopMaterialRequirement(4, "Dark Chestnut Lumber", 0, 40),
            new WorkshopMaterialRequirement(5, "Mythrite Rivets", 0, 60),
        };

        var demand = WorkshopShareRestockDemand.ToRawDemand(owed, WorkshopShareRestockDemand.PlanClaim(plan));
        // Nothing owed for item 4: the run must never target the sender's own plan by itself.
        Assert.Equal(new uint[] { 1, 2, 3, 5 }, demand.Select(line => line.ItemId));
        Assert.Equal(new[] { 360, 180, 560, 148 }, demand.Select(line => line.Quantity));

        var raw = WorkshopMaterialAvailabilityService.BuildAvailabilityFromRetainers(
            WorkshopShareRestockDemand.ToRequirements(demand), bags, []);
        Assert.Equal(new[] { 120, 30, 400, 48 }, raw.OrderBy(line => line.ItemId).Select(line => line.Shortage));

        // After the run fills every shortage, handover's kept = min(bags, need) must leave exactly the owed units spare.
        var after = new Dictionary<uint, int>(bags);
        foreach (var line in raw)
            after[line.ItemId] = after.GetValueOrDefault(line.ItemId) + line.Shortage;
        var kept = WorkshopShareRestockDemand.Reserve(after, plan);
        foreach (var item in owed.Where(item => item.Quantity > 0))
            Assert.True(
                after[item.ItemId] - kept.GetValueOrDefault(item.ItemId) >= item.Quantity,
                $"{item.ItemName}: spare {after[item.ItemId] - kept.GetValueOrDefault(item.ItemId)} < owed {item.Quantity}");

        // Start stores the planner's signature of the raw availability; Tick/Resume derive it from the frozen lines.
        var frozen = demand.Select(line => new WorkshopRestockDemandLine
        {
            ItemId = line.ItemId, ItemName = line.ItemName, IconId = line.IconId, Quantity = line.Quantity,
        }).ToList();
        Assert.Equal(WorkshopVendorProcurementPlanner.BuildQueueSignature(raw), WorkshopShareRestockDemand.Signature(frozen));
    }
}
