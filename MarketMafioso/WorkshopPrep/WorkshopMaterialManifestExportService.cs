using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MarketMafioso.WorkshopPrep;

public enum WorkshopMaterialManifestQuantityMode
{
    InventoryMissing,
    TotalMissing,
}

public enum WorkshopMaterialManifestExportSeverity
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record WorkshopMaterialCraftRecipe(uint RecipeId, int Yield);

public interface IWorkshopMaterialCraftRecipeResolver
{
    bool TryResolveCraftRecipe(uint itemId, out WorkshopMaterialCraftRecipe recipe);
}

public sealed record WorkshopMaterialManifestExportResult(
    bool Success,
    WorkshopMaterialManifestExportSeverity Severity,
    string Message,
    string Content,
    int ExportedCount,
    IReadOnlyList<string> SkippedItems);

public sealed class WorkshopMaterialManifestExportService
{
    private static readonly JsonSerializerOptions ArtisanJsonOptions = new()
    {
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions CraftArchitectJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly IWorkshopMaterialCraftRecipeResolver recipeResolver;

    public WorkshopMaterialManifestExportService(IWorkshopMaterialCraftRecipeResolver recipeResolver)
    {
        this.recipeResolver = recipeResolver;
    }

    public WorkshopMaterialManifestExportResult ExportArtisanManifest(
        IReadOnlyList<WorkshopPrepQueueItem> queue,
        IReadOnlyList<WorkshopProjectDefinition> projects,
        IReadOnlyList<WorkshopMaterialAvailability> availability,
        WorkshopMaterialManifestQuantityMode quantityMode,
        DateTime exportedAt)
    {
        return ExportArtisanManifest(queue, projects, availability, quantityMode, exportedAt, recipeResolver);
    }

    public static WorkshopMaterialManifestExportResult ExportCraftArchitectPlan(
        IReadOnlyList<WorkshopPrepQueueItem> queue,
        IReadOnlyList<WorkshopProjectDefinition> projects,
        IReadOnlyList<WorkshopMaterialAvailability> availability,
        WorkshopMaterialManifestQuantityMode quantityMode,
        DateTime exportedAt)
    {
        return ExportCraftArchitectPlan(queue, projects, availability, quantityMode, exportedAt, null);
    }

    public static WorkshopMaterialManifestExportResult ExportCraftArchitectPlan(
        IReadOnlyList<WorkshopPrepQueueItem> queue,
        IReadOnlyList<WorkshopProjectDefinition> projects,
        IReadOnlyList<WorkshopMaterialAvailability> availability,
        WorkshopMaterialManifestQuantityMode quantityMode,
        DateTime exportedAt,
        string? nameOverride)
    {
        var materials = BuildExportMaterials(availability, quantityMode);
        if (materials.Count == 0)
            return Info("No missing workshop materials to export.");

        var name = string.IsNullOrWhiteSpace(nameOverride)
            ? BuildExportName(queue, projects, quantityMode, exportedAt)
            : nameOverride.Trim();

        return new WorkshopMaterialManifestExportResult(
            true,
            WorkshopMaterialManifestExportSeverity.Success,
            $"Copied Craft Architect .craftplan JSON: {materials.Count} materials.",
            BuildCraftArchitectPlanJson(name, materials, quantityMode, exportedAt),
            materials.Count,
            []);
    }

    public static WorkshopMaterialManifestExportResult ExportArtisanManifest(
        IReadOnlyList<WorkshopPrepQueueItem> queue,
        IReadOnlyList<WorkshopProjectDefinition> projects,
        IReadOnlyList<WorkshopMaterialAvailability> availability,
        WorkshopMaterialManifestQuantityMode quantityMode,
        DateTime exportedAt,
        IWorkshopMaterialCraftRecipeResolver recipeResolver)
    {
        var materials = BuildExportMaterials(availability, quantityMode);
        if (materials.Count == 0)
            return Info("No missing workshop materials to export.");

        var skipped = new List<string>();
        var recipes = new List<ArtisanListItem>();
        foreach (var material in materials)
        {
            if (!recipeResolver.TryResolveCraftRecipe(material.ItemId, out var recipe))
            {
                skipped.Add(material.ItemName);
                continue;
            }

            var craftCount = (int)Math.Ceiling((double)GetExportQuantity(material, quantityMode) / Math.Max(1, recipe.Yield));
            AddOrMergeRecipe(recipes, new ArtisanListItem(
                recipe.RecipeId,
                craftCount,
                new ArtisanListItemOptions(NQOnly: true, Skipping: false)));
        }

        if (recipes.Count == 0)
        {
            return new WorkshopMaterialManifestExportResult(
                false,
                WorkshopMaterialManifestExportSeverity.Warning,
                $"No craftable missing workshop materials were available for Artisan export. Skipped {skipped.Count}.",
                string.Empty,
                0,
                skipped);
        }

        var artisanList = new ArtisanCraftingList(
            Random.Shared.Next(100, 50000),
            BuildExportName(queue, projects, quantityMode, exportedAt),
            recipes,
            recipes.SelectMany(recipe => Enumerable.Repeat(recipe.ID, recipe.Quantity)).ToList(),
            SkipIfEnough: false,
            SkipLiteral: false,
            Materia: false,
            Repair: false,
            RepairPercent: 50,
            AddAsQuickSynth: false,
            TidyAfter: true,
            OnlyRestockNonCrafted: false);
        var message = skipped.Count == 0
            ? $"Copied Artisan manifest: {recipes.Count} recipes."
            : $"Copied Artisan manifest: {recipes.Count} recipes. Skipped {skipped.Count} non-craftable materials: {FormatSkippedItems(skipped)}.";

        return new WorkshopMaterialManifestExportResult(
            true,
            skipped.Count == 0 ? WorkshopMaterialManifestExportSeverity.Success : WorkshopMaterialManifestExportSeverity.Warning,
            message,
            JsonSerializer.Serialize(artisanList, ArtisanJsonOptions),
            recipes.Count,
            skipped);
    }

    private static WorkshopMaterialManifestExportResult Info(string message)
    {
        return new WorkshopMaterialManifestExportResult(
            false,
            WorkshopMaterialManifestExportSeverity.Info,
            message,
            string.Empty,
            0,
            []);
    }

    private static List<WorkshopMaterialAvailability> BuildExportMaterials(
        IReadOnlyList<WorkshopMaterialAvailability> availability,
        WorkshopMaterialManifestQuantityMode quantityMode)
    {
        return availability
            .Where(x => GetExportQuantity(x, quantityMode) > 0)
            .OrderBy(x => x.ItemName)
            .ToList();
    }

    private static int GetExportQuantity(
        WorkshopMaterialAvailability availability,
        WorkshopMaterialManifestQuantityMode quantityMode)
    {
        return quantityMode switch
        {
            WorkshopMaterialManifestQuantityMode.InventoryMissing => availability.Shortage,
            WorkshopMaterialManifestQuantityMode.TotalMissing => availability.TotalMissing,
            _ => throw new ArgumentOutOfRangeException(nameof(quantityMode), quantityMode, null),
        };
    }

    private static string BuildCraftArchitectPlanJson(
        string name,
        IReadOnlyList<WorkshopMaterialAvailability> materials,
        WorkshopMaterialManifestQuantityMode quantityMode,
        DateTime exportedAt)
    {
        var nodes = materials
            .Select(material => new CraftArchitectPlanNode(
                ItemId: (int)material.ItemId,
                Name: material.ItemName,
                IconId: material.IconId,
                Quantity: GetExportQuantity(material, quantityMode),
                IsBuy: false,
                Source: 3,
                SourceReason: 0,
                RequiresHq: false,
                MustBeHq: false,
                CanBeHq: false,
                CanBuyFromMarket: true,
                IsUncraftable: false,
                RecipeLevel: 0,
                RecipeDisplayLevel: 0,
                RecipeStars: 0,
                RecipeUnlockItemId: 0,
                Job: string.Empty,
                Yield: 1,
                MarketPrice: 0,
                HqMarketPrice: 0,
                VendorPrice: 0,
                CanBuyFromVendor: false,
                CanCraft: false,
                Vendors: [],
                SelectedVendorIndex: -1,
                NodeId: BuildCraftArchitectNodeId(material.ItemId),
                ParentNodeId: null,
                Notes: "Exported from MarketMafioso Workshop Logistics.",
                ChildNodeIds: []))
            .ToList();
        var plan = new CraftArchitectPlan(
            Version: 2,
            Id: Guid.NewGuid(),
            Name: name,
            CreatedAt: exportedAt,
            ModifiedAt: exportedAt,
            DataCenter: string.Empty,
            World: string.Empty,
            RootNodeIds: nodes.Select(x => x.NodeId).ToList(),
            Nodes: nodes);

        return JsonSerializer.Serialize(plan, CraftArchitectJsonOptions);
    }

    private static string BuildCraftArchitectNodeId(uint itemId)
    {
        return $"mmf-{itemId}";
    }

    private sealed record CraftArchitectPlan(
        int Version,
        Guid Id,
        string Name,
        DateTime CreatedAt,
        DateTime ModifiedAt,
        string DataCenter,
        string World,
        IReadOnlyList<string> RootNodeIds,
        IReadOnlyList<CraftArchitectPlanNode> Nodes);

    private sealed record CraftArchitectPlanNode(
        int ItemId,
        string Name,
        uint IconId,
        int Quantity,
        bool IsBuy,
        int Source,
        int SourceReason,
        bool RequiresHq,
        bool MustBeHq,
        bool CanBeHq,
        bool CanBuyFromMarket,
        bool IsUncraftable,
        int RecipeLevel,
        int RecipeDisplayLevel,
        int RecipeStars,
        int RecipeUnlockItemId,
        string Job,
        int Yield,
        decimal MarketPrice,
        decimal HqMarketPrice,
        decimal VendorPrice,
        bool CanBuyFromVendor,
        bool CanCraft,
        IReadOnlyList<object> Vendors,
        int SelectedVendorIndex,
        string NodeId,
        string? ParentNodeId,
        string? Notes,
        IReadOnlyList<string> ChildNodeIds);

    private static string BuildExportName(
        IReadOnlyList<WorkshopPrepQueueItem> queue,
        IReadOnlyList<WorkshopProjectDefinition> projects,
        WorkshopMaterialManifestQuantityMode quantityMode,
        DateTime exportedAt)
    {
        var projectLookup = projects.ToDictionary(x => x.WorkshopItemId);
        var queuedProjects = queue
            .Where(x => x.Quantity > 0 && projectLookup.ContainsKey(x.WorkshopItemId))
            .Select(x => (Project: projectLookup[x.WorkshopItemId], x.Quantity))
            .ToList();
        var projectSummary = queuedProjects.Count switch
        {
            0 => "No projects",
            1 => $"{queuedProjects[0].Project.Name} x{queuedProjects[0].Quantity}",
            _ when queuedProjects[0].Project.Name.Length <= 48 => $"{queuedProjects[0].Project.Name} x{queuedProjects[0].Quantity} + {queuedProjects.Count - 1} more",
            _ => $"{queuedProjects.Count} projects",
        };

        return $"Workshop Materials - {projectSummary} - {FormatQuantityMode(quantityMode)} - {exportedAt:yyyy-MM-dd HHmm}";
    }

    private static string FormatQuantityMode(WorkshopMaterialManifestQuantityMode quantityMode)
    {
        return quantityMode switch
        {
            WorkshopMaterialManifestQuantityMode.InventoryMissing => "Inventory Missing",
            WorkshopMaterialManifestQuantityMode.TotalMissing => "Total Missing",
            _ => quantityMode.ToString(),
        };
    }

    private static void AddOrMergeRecipe(List<ArtisanListItem> recipes, ArtisanListItem recipe)
    {
        var existingIndex = recipes.FindIndex(x =>
            x.ID == recipe.ID &&
            x.ListItemOptions.NQOnly == recipe.ListItemOptions.NQOnly &&
            x.ListItemOptions.Skipping == recipe.ListItemOptions.Skipping);
        if (existingIndex < 0)
        {
            recipes.Add(recipe);
            return;
        }

        recipes[existingIndex] = recipes[existingIndex] with
        {
            Quantity = recipes[existingIndex].Quantity + recipe.Quantity,
        };
    }

    private static string FormatSkippedItems(IReadOnlyList<string> skipped)
    {
        var preview = string.Join(", ", skipped.Take(3));
        if (skipped.Count > 3)
            preview += $" and {skipped.Count - 3} more";

        return preview;
    }

    private sealed record ArtisanCraftingList(
        [property: JsonPropertyName("ID")] int ID,
        [property: JsonPropertyName("Name")] string Name,
        [property: JsonPropertyName("Recipes")] IReadOnlyList<ArtisanListItem> Recipes,
        [property: JsonPropertyName("ExpandedList")] IReadOnlyList<uint> ExpandedList,
        [property: JsonPropertyName("SkipIfEnough")] bool SkipIfEnough,
        [property: JsonPropertyName("SkipLiteral")] bool SkipLiteral,
        [property: JsonPropertyName("Materia")] bool Materia,
        [property: JsonPropertyName("Repair")] bool Repair,
        [property: JsonPropertyName("RepairPercent")] int RepairPercent,
        [property: JsonPropertyName("AddAsQuickSynth")] bool AddAsQuickSynth,
        [property: JsonPropertyName("TidyAfter")] bool TidyAfter,
        [property: JsonPropertyName("OnlyRestockNonCrafted")] bool OnlyRestockNonCrafted);

    private sealed record ArtisanListItem(
        [property: JsonPropertyName("ID")] uint ID,
        [property: JsonPropertyName("Quantity")] int Quantity,
        [property: JsonPropertyName("ListItemOptions")] ArtisanListItemOptions ListItemOptions);

    private sealed record ArtisanListItemOptions(
        [property: JsonPropertyName("NQOnly")] bool NQOnly,
        [property: JsonPropertyName("Skipping")] bool Skipping);
}

public sealed class LuminaWorkshopMaterialCraftRecipeResolver : IWorkshopMaterialCraftRecipeResolver
{
    private readonly IDataManager dataManager;

    public LuminaWorkshopMaterialCraftRecipeResolver(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public bool TryResolveCraftRecipe(uint itemId, out WorkshopMaterialCraftRecipe recipe)
    {
        var row = dataManager.GetExcelSheet<Recipe>()
            .Where(x => x.RowId > 0 && x.ItemResult.RowId == itemId)
            .OrderBy(x => x.RowId)
            .FirstOrDefault();
        if (row.RowId == 0)
        {
            recipe = new WorkshopMaterialCraftRecipe(0, 1);
            return false;
        }

        recipe = new WorkshopMaterialCraftRecipe(row.RowId, Math.Max(1, (int)row.AmountResult));
        return true;
    }
}
