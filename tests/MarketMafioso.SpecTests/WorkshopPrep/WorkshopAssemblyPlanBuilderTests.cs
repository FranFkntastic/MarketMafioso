using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopAssemblyPlanBuilderTests
{
    [Fact]
    public void Build_rejects_empty_queue()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WorkshopAssemblyPlanBuilder.Build([], [BuildProject(10, "Project", 100, 2)]));
    }

    [Fact]
    public void Build_rejects_unknown_project()
    {
        var queue = new[] { new WorkshopPrepQueueItem { WorkshopItemId = 99, Quantity = 1 } };

        Assert.Throws<InvalidOperationException>(() =>
            WorkshopAssemblyPlanBuilder.Build(queue, [BuildProject(10, "Project", 100, 2)]));
    }

    [Fact]
    public void Build_rejects_non_positive_quantity()
    {
        var queue = new[] { new WorkshopPrepQueueItem { WorkshopItemId = 10, Quantity = 0 } };

        Assert.Throws<InvalidOperationException>(() =>
            WorkshopAssemblyPlanBuilder.Build(queue, [BuildProject(10, "Project", 100, 2)]));
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
