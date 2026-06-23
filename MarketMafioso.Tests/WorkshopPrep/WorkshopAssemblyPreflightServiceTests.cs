using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopAssemblyPreflightServiceTests
{
    [Fact]
    public void Check_returns_blocked_when_materials_are_missing()
    {
        var queue = new[] { new WorkshopPrepQueueItem { WorkshopItemId = 10, Quantity = 1 } };
        var projects = new[] { BuildProject(10, "Project", 100, "Cedar Lumber", 9) };
        var playerInventory = new Dictionary<uint, int> { [100] = 4 };

        var result = WorkshopAssemblyPreflightService.Check(queue, projects, playerInventory);

        Assert.False(result.CanStart);
        Assert.Null(result.Plan);
        Assert.Equal("Missing player materials: Cedar Lumber x5.", result.Message);
    }

    [Fact]
    public void Check_returns_plan_when_materials_are_available()
    {
        var queue = new[] { new WorkshopPrepQueueItem { WorkshopItemId = 10, Quantity = 2 } };
        var projects = new[] { BuildProject(10, "Project", 100, "Cedar Lumber", 9) };
        var playerInventory = new Dictionary<uint, int> { [100] = 18 };

        var result = WorkshopAssemblyPreflightService.Check(queue, projects, playerInventory);

        Assert.True(result.CanStart);
        Assert.NotNull(result.Plan);
        Assert.Equal("Workshop assembly preflight complete.", result.Message);
    }

    private static WorkshopProjectDefinition BuildProject(
        uint workshopItemId,
        string name,
        uint materialItemId,
        string materialName,
        int materialQuantity)
    {
        return new WorkshopProjectDefinition(
            workshopItemId,
            workshopItemId + 1000,
            name,
            0,
            [new WorkshopMaterialRequirement(materialItemId, materialName, 0, materialQuantity)]);
    }
}
