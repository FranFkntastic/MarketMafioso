using System.Text.Json;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class ArtisanCraftingListExportTests
{
    [Fact]
    public void Create_MergesMatchingRecipesAndPreservesFirstSeenOrder()
    {
        var result = ArtisanCraftingListExport.Create(
            "Test crafting list",
            [
                new(7002, 2),
                new(7001, 1),
                new(7002, 3),
                new(7002, 4, NQOnly: false),
            ],
            listId: 1234);

        Assert.Equal(3, result.RecipeCount);
        Assert.Equal(10, result.ExpandedEntryCount);

        using var document = JsonDocument.Parse(result.Json);
        var root = document.RootElement;
        Assert.Equal(1234, root.GetProperty("ID").GetInt32());
        Assert.Equal("Test crafting list", root.GetProperty("Name").GetString());

        var recipes = root.GetProperty("Recipes").EnumerateArray().ToList();
        Assert.Equal(3, recipes.Count);
        AssertRecipe(recipes[0], 7002, 5, nqOnly: true, skipping: false);
        AssertRecipe(recipes[1], 7001, 1, nqOnly: true, skipping: false);
        AssertRecipe(recipes[2], 7002, 4, nqOnly: false, skipping: false);

        var expanded = root.GetProperty("ExpandedList").EnumerateArray().Select(x => x.GetUInt32()).ToList();
        Assert.Equal(
            [7002u, 7002u, 7002u, 7002u, 7002u, 7001u, 7002u, 7002u, 7002u, 7002u],
            expanded);
        Assert.False(root.GetProperty("SkipIfEnough").GetBoolean());
        Assert.False(root.GetProperty("SkipLiteral").GetBoolean());
        Assert.False(root.GetProperty("Materia").GetBoolean());
        Assert.False(root.GetProperty("Repair").GetBoolean());
        Assert.Equal(50, root.GetProperty("RepairPercent").GetInt32());
        Assert.False(root.GetProperty("AddAsQuickSynth").GetBoolean());
        Assert.True(root.GetProperty("TidyAfter").GetBoolean());
        Assert.False(root.GetProperty("OnlyRestockNonCrafted").GetBoolean());
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(1u, 0)]
    [InlineData(1u, -1)]
    public void Create_RejectsNonPositiveRecipeIdsAndCraftCounts(uint recipeId, int craftCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ArtisanCraftingListExport.Create(
            "Invalid crafting list",
            [new(recipeId, craftCount)],
            listId: 1));
    }

    [Fact]
    public void Create_EnforcesExpandedEntryBound()
    {
        var result = ArtisanCraftingListExport.Create(
            "Largest crafting list",
            [new(7001, ArtisanCraftingListExport.MaximumExpandedEntries)],
            listId: 1);

        Assert.Equal(ArtisanCraftingListExport.MaximumExpandedEntries, result.ExpandedEntryCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => ArtisanCraftingListExport.Create(
            "Oversized crafting list",
            [new(7001, ArtisanCraftingListExport.MaximumExpandedEntries), new(7002, 1)],
            listId: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_RejectsNonPositiveExplicitListId(int listId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ArtisanCraftingListExport.Create(
            "Invalid list ID",
            [new(7001, 1)],
            listId));
    }

    private static void AssertRecipe(JsonElement recipe, uint id, int quantity, bool nqOnly, bool skipping)
    {
        Assert.Equal(id, recipe.GetProperty("ID").GetUInt32());
        Assert.Equal(quantity, recipe.GetProperty("Quantity").GetInt32());
        Assert.Equal(nqOnly, recipe.GetProperty("ListItemOptions").GetProperty("NQOnly").GetBoolean());
        Assert.Equal(skipping, recipe.GetProperty("ListItemOptions").GetProperty("Skipping").GetBoolean());
    }
}
