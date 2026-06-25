using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopAssemblyPlanBuilderTests
{
    [Fact]
    public void Build_rejects_empty_queue()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkshopAssemblyPlanBuilder.Build([], [BuildProject(10, "Project", 100, 2)]));

        Assert.Equal("Workshop assembly queue is empty.", ex.Message);
    }

    [Fact]
    public void Build_rejects_unknown_project()
    {
        var queue = new[] { new WorkshopPrepQueueItem { WorkshopItemId = 99, Quantity = 1 } };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkshopAssemblyPlanBuilder.Build(queue, [BuildProject(10, "Project", 100, 2)]));

        Assert.Equal("Unknown workshop project id 99 cannot be assembled.", ex.Message);
    }

    [Fact]
    public void Build_rejects_non_positive_quantity()
    {
        var queue = new[] { new WorkshopPrepQueueItem { WorkshopItemId = 10, Quantity = 0 } };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkshopAssemblyPlanBuilder.Build(queue, [BuildProject(10, "Project", 100, 2)]));

        Assert.Equal("Workshop project Project has invalid quantity 0.", ex.Message);
    }

    [Fact]
    public void Build_aggregates_materials_across_quantities()
    {
        var queue =
            new[]
            {
                new WorkshopPrepQueueItem { WorkshopItemId = 10, Quantity = 2 },
                new WorkshopPrepQueueItem { WorkshopItemId = 11, Quantity = 1 },
            };

        var plan = WorkshopAssemblyPlanBuilder.Build(
            queue,
            [
                BuildProject(10, "Project A", 100, 3),
                BuildProject(11, "Project B", 100, 5),
            ]);

        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal(2, plan.Entries[0].Quantity);
        var material = Assert.Single(plan.TotalMaterials);
        Assert.Equal((uint)100, material.ItemId);
        Assert.Equal(11, material.Quantity);
    }

    [Fact]
    public void Build_carries_estimate_metadata_into_entries()
    {
        var queue = new[] { new WorkshopPrepQueueItem { WorkshopItemId = 10, Quantity = 2 } };

        var plan = WorkshopAssemblyPlanBuilder.Build(
            queue,
            [BuildProject(10, "Project A", 100, 3, contributionSteps: 11, phaseCount: 3)]);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(11, entry.EstimatedContributionSteps);
        Assert.Equal(3, entry.EstimatedPhaseCount);
    }

    private static WorkshopProjectDefinition BuildProject(
        uint workshopItemId,
        string name,
        uint materialItemId,
        int materialQuantity,
        int contributionSteps = 1,
        int phaseCount = 1)
    {
        return new WorkshopProjectDefinition(
            workshopItemId,
            workshopItemId + 1000,
            name,
            0,
            [new WorkshopMaterialRequirement(materialItemId, "Material", 0, materialQuantity)],
            CategoryId: 0,
            TypeId: 0,
            EstimatedContributionSteps: contributionSteps,
            EstimatedPhaseCount: phaseCount);
    }
}
