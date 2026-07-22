using System.Text.Json;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopMaterialManifestExportServiceTests
{
    [Fact]
    public void ExportCraftArchitectPlan_UsesNativeCraftPlanJsonFormat()
    {
        var exportedAt = new DateTime(2026, 6, 23, 21, 15, 0, DateTimeKind.Utc);
        var result = WorkshopMaterialManifestExportService.ExportCraftArchitectPlan(
            CreateQueue(),
            CreateProjects(),
            CreateAvailability(),
            WorkshopMaterialManifestQuantityMode.InventoryMissing,
            exportedAt);

        Assert.True(result.Success);
        Assert.Equal(2, result.ExportedCount);
        Assert.Equal("Copied Craft Architect .craftplan JSON: 2 materials.", result.Message);

        using var document = JsonDocument.Parse(result.Content);
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("Version").GetInt32());
        Assert.Equal("Workshop Materials - Shark-class Pressure Hull x16 + 1 more - Inventory Missing - 2026-06-23 2115", root.GetProperty("Name").GetString());
        Assert.Equal(string.Empty, root.GetProperty("DataCenter").GetString());
        Assert.Equal(string.Empty, root.GetProperty("World").GetString());

        var rootNodeIds = root.GetProperty("RootNodeIds").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(["mmf-5378", "mmf-6000"], rootNodeIds);

        var cobalt = FindNode(root, "mmf-5378");
        Assert.Equal(5378, cobalt.GetProperty("ItemId").GetInt32());
        Assert.Equal("Cobalt Ingot", cobalt.GetProperty("Name").GetString());
        Assert.Equal(20, cobalt.GetProperty("IconId").GetInt32());
        Assert.Equal(288, cobalt.GetProperty("Quantity").GetInt32());
        Assert.Equal(3, cobalt.GetProperty("Source").GetInt32());
        Assert.Equal(0, cobalt.GetProperty("SourceReason").GetInt32());
        Assert.False(cobalt.GetProperty("MustBeHq").GetBoolean());
        Assert.True(cobalt.GetProperty("CanBuyFromMarket").GetBoolean());
        Assert.False(cobalt.GetProperty("CanCraft").GetBoolean());
        Assert.Empty(cobalt.GetProperty("ChildNodeIds").EnumerateArray());

        var darksteel = FindNode(root, "mmf-6000");
        Assert.Equal(32, darksteel.GetProperty("Quantity").GetInt32());
    }

    [Fact]
    public void ExportCraftArchitectPlan_TotalMissingMode_ExportsOnlyUnownedQuantity()
    {
        var result = WorkshopMaterialManifestExportService.ExportCraftArchitectPlan(
            CreateQueue(),
            CreateProjects(),
            CreateAvailability(),
            WorkshopMaterialManifestQuantityMode.TotalMissing,
            new DateTime(2026, 6, 23, 21, 15, 0, DateTimeKind.Utc));

        Assert.True(result.Success);
        Assert.Equal(1, result.ExportedCount);

        using var document = JsonDocument.Parse(result.Content);
        var root = document.RootElement;
        var nodes = root.GetProperty("Nodes").EnumerateArray().ToList();
        Assert.Single(nodes);
        Assert.Equal("mmf-6000", nodes[0].GetProperty("NodeId").GetString());
        Assert.Equal("Darksteel Ore", nodes[0].GetProperty("Name").GetString());
        Assert.Equal(32, nodes[0].GetProperty("Quantity").GetInt32());
    }

    [Fact]
    public void ExportArtisanManifest_ResolvesRecipesAndUsesRecipeYieldForCraftCount()
    {
        var resolver = new FakeRecipeResolver(new Dictionary<uint, WorkshopMaterialCraftRecipe>
        {
            [5378] = new(7001, 3),
            [6000] = new(7002, 1),
        });

        var result = WorkshopMaterialManifestExportService.ExportArtisanManifest(
            CreateQueue(),
            CreateProjects(),
            CreateAvailability(),
            WorkshopMaterialManifestQuantityMode.InventoryMissing,
            new DateTime(2026, 6, 23, 21, 15, 0, DateTimeKind.Utc),
            resolver);

        Assert.True(result.Success);
        Assert.Equal(2, result.ExportedCount);
        Assert.Empty(result.SkippedItems);
        Assert.Equal("Copied Artisan manifest: 2 recipes.", result.Message);

        using var document = JsonDocument.Parse(result.Content);
        var root = document.RootElement;
        Assert.Equal("Workshop Materials - Shark-class Pressure Hull x16 + 1 more - Inventory Missing - 2026-06-23 2115", root.GetProperty("Name").GetString());

        var recipes = root.GetProperty("Recipes").EnumerateArray().ToList();
        Assert.Equal(2, recipes.Count);
        Assert.Equal((int)7001, recipes[0].GetProperty("ID").GetInt32());
        Assert.Equal(96, recipes[0].GetProperty("Quantity").GetInt32());
        Assert.True(recipes[0].GetProperty("ListItemOptions").GetProperty("NQOnly").GetBoolean());
        Assert.False(recipes[0].GetProperty("ListItemOptions").GetProperty("Skipping").GetBoolean());

        var expanded = root.GetProperty("ExpandedList").EnumerateArray().Select(x => x.GetInt32()).ToList();
        Assert.Equal(128, expanded.Count);
        Assert.Equal(96, expanded.Count(x => x == 7001));
        Assert.Equal(32, expanded.Count(x => x == 7002));
    }

    [Fact]
    public void ExportArtisanManifest_SkipsMaterialsWithoutCraftRecipes()
    {
        var resolver = new FakeRecipeResolver(new Dictionary<uint, WorkshopMaterialCraftRecipe>
        {
            [5378] = new(7001, 3),
        });

        var result = WorkshopMaterialManifestExportService.ExportArtisanManifest(
            CreateQueue(),
            CreateProjects(),
            CreateAvailability(),
            WorkshopMaterialManifestQuantityMode.InventoryMissing,
            new DateTime(2026, 6, 23, 21, 15, 0, DateTimeKind.Utc),
            resolver);

        Assert.True(result.Success);
        Assert.Equal(1, result.ExportedCount);
        Assert.Equal(["Darksteel Ore"], result.SkippedItems);
        Assert.Contains("Skipped 1", result.Message);
    }

    [Fact]
    public void ExportCraftArchitectPlan_ReturnsInfoWhenNoMissingMaterials()
    {
        var availability = CreateAvailability()
            .Select(item => item with { Shortage = 0, TotalMissing = 0 })
            .ToList();

        var result = WorkshopMaterialManifestExportService.ExportCraftArchitectPlan(
            CreateQueue(),
            CreateProjects(),
            availability,
            WorkshopMaterialManifestQuantityMode.InventoryMissing,
            new DateTime(2026, 6, 23, 21, 15, 0, DateTimeKind.Utc));

        Assert.False(result.Success);
        Assert.Equal(WorkshopMaterialManifestExportSeverity.Info, result.Severity);
        Assert.Equal(string.Empty, result.Content);
        Assert.Contains("No missing workshop materials", result.Message);
    }

    private static JsonElement FindNode(JsonElement root, string nodeId)
    {
        return root.GetProperty("Nodes")
            .EnumerateArray()
            .Single(x => x.GetProperty("NodeId").GetString() == nodeId);
    }

    private static IReadOnlyList<WorkshopPrepQueueItem> CreateQueue() =>
    [
        new() { WorkshopItemId = 1000, Quantity = 16 },
        new() { WorkshopItemId = 1001, Quantity = 2 },
    ];

    private static IReadOnlyList<WorkshopProjectDefinition> CreateProjects() =>
    [
        new(1000, 2000, "Shark-class Pressure Hull", 11, []),
        new(1001, 2001, "Shark-class Stern", 12, []),
    ];

    private static IReadOnlyList<WorkshopMaterialAvailability> CreateAvailability() =>
    [
        new(
            5378,
            "Cobalt Ingot",
            20,
            Required: 288,
            PlayerInventory: 0,
            QuartermasterStock: 75539,
            Shortage: 288,
            TotalMissing: 0,
            QuartermasterRetainers:
            [
                new(10, "Taffy-swordsman", new DateTime(2026, 6, 23, 20, 40, 0, DateTimeKind.Utc), 75539),
            ]),
        new(
            6000,
            "Darksteel Ore",
            21,
            Required: 80,
            PlayerInventory: 48,
            QuartermasterStock: 0,
            Shortage: 32,
            TotalMissing: 32,
            QuartermasterRetainers: []),
    ];

    private sealed class FakeRecipeResolver : IWorkshopMaterialCraftRecipeResolver
    {
        private readonly IReadOnlyDictionary<uint, WorkshopMaterialCraftRecipe> recipes;

        public FakeRecipeResolver(IReadOnlyDictionary<uint, WorkshopMaterialCraftRecipe> recipes)
        {
            this.recipes = recipes;
        }

        public bool TryResolveCraftRecipe(uint itemId, out WorkshopMaterialCraftRecipe recipe)
        {
            if (recipes.TryGetValue(itemId, out recipe!))
                return true;

            recipe = new WorkshopMaterialCraftRecipe(0, 1);
            return false;
        }
    }
}
