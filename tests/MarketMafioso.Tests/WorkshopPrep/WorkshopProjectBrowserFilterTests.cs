using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopProjectBrowserFilterTests
{
    [Fact]
    public void BuildVisibleProjects_PreservesProjectOrderWithoutSearch()
    {
        var projects = new[]
        {
            CreateProject(1, "Shark-class Bow", "Cedar Lumber"),
            CreateProject(2, "Shark-class Bridge", "Cobalt Ingot"),
            CreateProject(3, "Shark-class Stern", "Iron Nails"),
        };

        var visible = WorkshopProjectBrowserFilter.BuildVisibleProjects(
            projects,
            search: string.Empty);

        Assert.Equal([1U, 2U, 3U], visible.Select(x => x.WorkshopItemId));
    }

    [Fact]
    public void BuildVisibleProjects_SearchesProjectNamesIdsAndMaterials()
    {
        var projects = new[]
        {
            CreateProject(1, "Shark-class Bow", "Cedar Lumber"),
            CreateProject(2, "Shark-class Bridge", "Cobalt Ingot"),
            CreateProject(3, "Shark-class Stern", "Iron Nails"),
        };

        var visible = WorkshopProjectBrowserFilter.BuildVisibleProjects(
            projects,
            search: "Iron");

        var project = Assert.Single(visible);
        Assert.Equal(3U, project.WorkshopItemId);
    }

    private static WorkshopProjectDefinition CreateProject(uint workshopItemId, string name, string materialName)
    {
        return new WorkshopProjectDefinition(
            workshopItemId,
            workshopItemId + 100,
            name,
            0,
            [new WorkshopMaterialRequirement(workshopItemId + 200, materialName, 0, 1)]);
    }
}
