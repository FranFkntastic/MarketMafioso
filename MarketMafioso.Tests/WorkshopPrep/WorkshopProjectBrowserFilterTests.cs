using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopProjectBrowserFilterTests
{
    [Fact]
    public void BuildVisibleProjects_ShowsFavoritesFirstAndCanFilterToFavoritesOnly()
    {
        var projects = new[]
        {
            CreateProject(1, "Shark-class Bow", "Cedar Lumber"),
            CreateProject(2, "Shark-class Bridge", "Cobalt Ingot"),
            CreateProject(3, "Shark-class Stern", "Iron Nails"),
        };
        var favorites = new HashSet<uint> { 3 };

        var allVisible = WorkshopProjectBrowserFilter.BuildVisibleProjects(
            projects,
            search: string.Empty,
            favoriteWorkshopItemIds: favorites,
            favoritesOnly: false);

        Assert.Equal([3U, 1U, 2U], allVisible.Select(x => x.WorkshopItemId));

        var favoritesOnly = WorkshopProjectBrowserFilter.BuildVisibleProjects(
            projects,
            search: string.Empty,
            favoriteWorkshopItemIds: favorites,
            favoritesOnly: true);

        Assert.Equal([3U], favoritesOnly.Select(x => x.WorkshopItemId));
    }

    [Fact]
    public void BuildVisibleProjects_CombinesSearchWithFavoritesOnly()
    {
        var projects = new[]
        {
            CreateProject(1, "Shark-class Bow", "Cedar Lumber"),
            CreateProject(2, "Shark-class Bridge", "Cobalt Ingot"),
            CreateProject(3, "Shark-class Stern", "Iron Nails"),
        };
        var favorites = new HashSet<uint> { 1, 3 };

        var visible = WorkshopProjectBrowserFilter.BuildVisibleProjects(
            projects,
            search: "Iron",
            favoriteWorkshopItemIds: favorites,
            favoritesOnly: true);

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
