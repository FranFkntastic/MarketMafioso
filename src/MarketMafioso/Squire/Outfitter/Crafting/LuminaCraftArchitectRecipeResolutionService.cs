using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using LuminaRecipe = Lumina.Excel.Sheets.Recipe;

namespace MarketMafioso.Squire.Outfitter.Crafting;

/// <summary>
/// Supplies version-matched recipe-book evidence when Garland omits its optional unlock field.
/// Missing or malformed Lumina rows remain unknown and therefore fail closed in Craft Architect.
/// </summary>
internal sealed class LuminaCraftArchitectRecipeResolutionService : IRecipeResolutionService
{
    private readonly IRecipeResolutionService inner;
    private readonly IReadOnlyDictionary<uint, int> unlockItemIds;

    internal LuminaCraftArchitectRecipeResolutionService(
        IRecipeResolutionService inner,
        IReadOnlyDictionary<uint, int> unlockItemIds)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.unlockItemIds = unlockItemIds ?? throw new ArgumentNullException(nameof(unlockItemIds));
    }

    public RecipeResolutionResult Resolve(PlanNode node, GarlandItem? itemData)
    {
        var result = inner.Resolve(node, itemData);
        return result.Kind == RecipeOperationKind.StandardCraft &&
               result.RecipeId is { } recipeId &&
               result.RecipeUnlockItemId is null &&
               unlockItemIds.TryGetValue(recipeId, out var unlockItemId)
            ? result with { RecipeUnlockItemId = unlockItemId }
            : result;
    }

    internal static LuminaCraftArchitectRecipeResolutionService Create(IDataManager dataManager)
    {
        ArgumentNullException.ThrowIfNull(dataManager);
        var recipes = dataManager.GetExcelSheet<LuminaRecipe>() ??
                      throw new InvalidOperationException("The Recipe sheet is unavailable for craft unlock evidence.");
        var unlockItemIds = new Dictionary<uint, int>();
        foreach (var recipe in recipes)
        {
            if (recipe.RowId == 0)
                continue;
            if (recipe.SecretRecipeBook.RowId == 0)
            {
                unlockItemIds[recipe.RowId] = 0;
                continue;
            }
            var itemId = recipe.SecretRecipeBook.Value.Item.RowId;
            if (itemId is > 0 and <= int.MaxValue)
                unlockItemIds[recipe.RowId] = (int)itemId;
        }
        if (unlockItemIds.Count == 0)
            throw new InvalidOperationException("The Recipe sheet exposed no authoritative craft unlock evidence.");
        return new(new RecipeResolutionService(), unlockItemIds);
    }

    internal static bool TryReadUnlockItemId(object recipe, out int unlockItemId)
    {
        unlockItemId = 0;
        var secretBook = recipe.GetType().GetProperty("SecretRecipeBook")?.GetValue(recipe);
        if (secretBook is null || !TryReadRowId(secretBook, out var secretBookId))
            return false;
        if (secretBookId == 0)
            return true;

        try
        {
            var secretBookValue = secretBook.GetType().GetProperty("Value")?.GetValue(secretBook);
            var item = secretBookValue?.GetType().GetProperty("Item")?.GetValue(secretBookValue);
            if (item is null || !TryReadRowId(item, out var itemId) || itemId == 0 || itemId > int.MaxValue)
                return false;
            unlockItemId = (int)itemId;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadRowId(object value, out uint rowId)
    {
        rowId = 0;
        var raw = value.GetType().GetProperty("RowId")?.GetValue(value);
        if (raw is null)
            return false;
        try
        {
            rowId = Convert.ToUInt32(raw);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }
}
