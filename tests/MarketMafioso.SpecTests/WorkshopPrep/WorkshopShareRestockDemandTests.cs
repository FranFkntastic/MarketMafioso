using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

// A share restock spends gil against a raw bag target. If owed + kept drifts from the share-terms
// shortage, the runner silently buys the sender's own plan materials (or under-buys the share), and
// nothing in game says why. This pins the translation and the Start/Tick signature it must reproduce.
public sealed class WorkshopShareRestockDemandTests
{
    [Fact]
    public void RawDemandPlansTheSameShortageAsShareTermsAndKeepsItsSignature()
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
            new WorkshopMaterialRequirement(3, "Cobalt Ingot", 0, 400),
            new WorkshopMaterialRequirement(4, "Dark Chestnut Lumber", 0, 40),
            new WorkshopMaterialRequirement(5, "Mythrite Rivets", 0, 60),
        };

        var reserved = WorkshopShareRestockDemand.Reserve(bags, plan);
        Assert.Equal(new Dictionary<uint, int> { [1] = 180, [3] = 160, [4] = 40, [5] = 60 }, reserved);

        var demand = WorkshopShareRestockDemand.ToRawDemand(owed, reserved);
        // Nothing owed for item 4: the run must never target the sender's own plan by itself.
        Assert.Equal(new uint[] { 1, 2, 3, 5 }, demand.Select(line => line.ItemId));
        Assert.Equal(new[] { 360, 180, 320, 148 }, demand.Select(line => line.Quantity));

        var raw = WorkshopMaterialAvailabilityService.BuildAvailabilityFromRetainers(
            WorkshopShareRestockDemand.ToRequirements(demand), bags, []);
        var spare = bags.ToDictionary(entry => entry.Key, entry => entry.Value - reserved.GetValueOrDefault(entry.Key));
        var shareTerms = WorkshopMaterialAvailabilityService.BuildAvailabilityFromRetainers(
            owed.Where(item => item.Quantity > 0).ToArray(), spare, []);
        Assert.Equal(
            shareTerms.Select(line => (line.ItemId, line.Shortage)),
            raw.Select(line => (line.ItemId, line.Shortage)));
        Assert.Equal(new[] { 120, 30, 160, 48 }, raw.OrderBy(line => line.ItemId).Select(line => line.Shortage));

        // Start stores the planner's signature of the raw availability; Tick/Resume derive it from the frozen lines.
        var frozen = demand.Select(line => new WorkshopRestockDemandLine
        {
            ItemId = line.ItemId, ItemName = line.ItemName, IconId = line.IconId, Quantity = line.Quantity,
        }).ToList();
        Assert.Equal(WorkshopVendorProcurementPlanner.BuildQueueSignature(raw), WorkshopShareRestockDemand.Signature(frozen));
    }
}
