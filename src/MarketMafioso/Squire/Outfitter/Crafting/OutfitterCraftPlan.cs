using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Crafting;

public enum OutfitterCraftNodeKind
{
    Craft,
    Material,
}

public enum OutfitterCraftDiagnosticCode
{
    CircularRecipe,
    AmbiguousRecipe,
    MaximumDepthExceeded,
    IncompleteMaterialCoverage,
    IneligibleCrafter,
    MasterRecipe,
    InvalidIdentity,
}

public sealed record OutfitterCraftDiagnostic(OutfitterCraftDiagnosticCode Code, string Message, string? NodeId = null);

public sealed record OutfitterCraftEligibility(
    bool IsEligible,
    uint ClassJobId,
    int PlayerLevel,
    int RequiredLevel,
    string? Diagnostic = null);

public sealed record OutfitterCraftNode(
    string NodeId,
    string? ParentNodeId,
    OutfitterCraftNodeKind Kind,
    uint ItemId,
    EquipmentQuality Quality,
    uint Quantity,
    uint RecipeId = 0,
    uint RecipeUnlockItemId = 0,
    OutfitterCraftEligibility? Eligibility = null);

public enum OutfitterMaterialSourceKind
{
    MarketListing,
    GilVendor,
}

public sealed record OutfitterMaterialSourceIdentity(
    OutfitterMaterialSourceKind Kind,
    string SourceKey,
    string SourceIdentity,
    uint ItemId,
    EquipmentQuality Quality,
    uint AvailableQuantity,
    uint UnitPriceGil,
    Guid EvidenceGenerationId,
    long EvidenceRevision);

public sealed record OutfitterTerminalMaterialLine(
    string MaterialKey,
    uint ItemId,
    EquipmentQuality Quality,
    uint RequiredQuantity,
    OutfitterMaterialSourceIdentity Source);

public sealed record OutfitterCraftPlanValidation(bool IsValid, ImmutableArray<string> Errors)
{
    public static OutfitterCraftPlanValidation Valid { get; } = new(true, ImmutableArray<string>.Empty);
}

/// <summary>
/// Immutable, fully expanded recipe tree. Consumers must use ExpandedNodes and must never expand recipes again.
/// </summary>
public sealed record OutfitterCraftPlan(
    string SchemaVersion,
    string PlanId,
    uint GearItemId,
    EquipmentQuality GearQuality,
    uint GearQuantity,
    string RootNodeId,
    OutfitterCraftEligibility Eligibility,
    ImmutableArray<OutfitterCraftNode> ExpandedNodes,
    ImmutableArray<OutfitterTerminalMaterialLine> TerminalMaterials,
    Guid MarketEvidenceGenerationId,
    long MarketEvidenceRevision,
    DateTimeOffset BuiltAtUtc,
    ImmutableArray<OutfitterCraftDiagnostic> Diagnostics)
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-outfitter-craft-plan/v1";

    public OutfitterCraftPlanValidation Validate(bool requireActionable = false)
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
            errors.Add("Unsupported craft-plan schema version.");
        if (string.IsNullOrWhiteSpace(PlanId) || GearItemId == 0 || GearQuantity == 0 || string.IsNullOrWhiteSpace(RootNodeId))
            errors.Add("Plan, gear, root, and quantity identity must be complete.");
        if (!IsExactQuality(GearQuality))
            errors.Add("Gear quality must be exact NQ or HQ.");
        if (MarketEvidenceGenerationId == Guid.Empty || MarketEvidenceRevision <= 0)
            errors.Add("A published market evidence generation and revision are required.");
        if (ExpandedNodes.IsDefaultOrEmpty)
            errors.Add("The expanded recipe tree is empty.");

        var duplicateNodeIds = ExpandedNodes.GroupBy(node => node.NodeId, StringComparer.Ordinal).Where(group => group.Count() != 1).Select(group => group.Key).ToArray();
        if (duplicateNodeIds.Length != 0 || ExpandedNodes.Any(node => string.IsNullOrWhiteSpace(node.NodeId)))
            errors.Add("Expanded node identity is ambiguous.");

        var nodes = ExpandedNodes.Where(node => !string.IsNullOrWhiteSpace(node.NodeId)).GroupBy(node => node.NodeId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (!nodes.TryGetValue(RootNodeId, out var root) || root.ParentNodeId is not null || root.Kind != OutfitterCraftNodeKind.Craft || root.ItemId != GearItemId || root.Quality != GearQuality || root.Quantity != GearQuantity)
            errors.Add("The root node must exactly identify the requested gear and quantity.");

        foreach (var node in ExpandedNodes)
        {
            if (node.ItemId == 0 || node.Quantity == 0 || !IsExactQuality(node.Quality))
                errors.Add($"Node '{node.NodeId}' has incomplete item, quality, or quantity identity.");
            if (node.NodeId != RootNodeId && (node.ParentNodeId is null || !nodes.ContainsKey(node.ParentNodeId)))
                errors.Add($"Node '{node.NodeId}' is disconnected from the expanded tree.");
            if (node.Kind == OutfitterCraftNodeKind.Craft && (node.RecipeId == 0 || node.Eligibility is null))
                errors.Add($"Craft node '{node.NodeId}' lacks recipe or eligibility identity.");
            if (node.Kind == OutfitterCraftNodeKind.Material && (node.RecipeId != 0 || node.RecipeUnlockItemId != 0))
                errors.Add($"Material node '{node.NodeId}' cannot carry recipe identity.");
        }

        foreach (var node in ExpandedNodes)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { node.NodeId };
            var cursor = node;
            while (cursor.ParentNodeId is { } parentId && nodes.TryGetValue(parentId, out var parent))
            {
                if (!seen.Add(parentId))
                {
                    errors.Add("The expanded recipe tree is circular.");
                    break;
                }
                cursor = parent;
            }
        }

        var expected = new Dictionary<string, uint>(StringComparer.Ordinal);
        var actual = new Dictionary<string, uint>(StringComparer.Ordinal);
        try
        {
            foreach (var node in ExpandedNodes.Where(node => node.Kind == OutfitterCraftNodeKind.Material))
            {
                var key = MaterialKey(node.ItemId, node.Quality);
                expected[key] = checked(expected.GetValueOrDefault(key) + node.Quantity);
            }
            foreach (var line in TerminalMaterials)
            {
                if (line.MaterialKey != MaterialKey(line.ItemId, line.Quality) || line.ItemId == 0 || line.RequiredQuantity == 0 || !IsExactQuality(line.Quality))
                    errors.Add("A terminal material line has incomplete exact identity.");
                ValidateSource(line, errors);
                actual[line.MaterialKey] = checked(actual.GetValueOrDefault(line.MaterialKey) + line.RequiredQuantity);
            }
        }
        catch (OverflowException)
        {
            errors.Add("Terminal material quantity arithmetic overflowed.");
        }
        if (!expected.OrderBy(pair => pair.Key).SequenceEqual(actual.OrderBy(pair => pair.Key)))
            errors.Add("Terminal material lines do not completely cover the expanded tree.");

        if (Diagnostics.Any(diagnostic => diagnostic.Code is OutfitterCraftDiagnosticCode.CircularRecipe or OutfitterCraftDiagnosticCode.AmbiguousRecipe or OutfitterCraftDiagnosticCode.MaximumDepthExceeded or OutfitterCraftDiagnosticCode.IncompleteMaterialCoverage))
            errors.Add("Expansion diagnostics make the plan non-actionable.");

        if (requireActionable)
        {
            if (!Eligibility.IsEligible || ExpandedNodes.Any(node => node.Kind == OutfitterCraftNodeKind.Craft && (node.Eligibility?.IsEligible != true || node.RecipeUnlockItemId != 0)))
                errors.Add("Actionable plans require a fully eligible, non-master recipe tree.");
        }
        return errors.Count == 0 ? OutfitterCraftPlanValidation.Valid : new(false, errors.Distinct(StringComparer.Ordinal).ToImmutableArray());
    }

    public static string MaterialKey(uint itemId, EquipmentQuality quality) => $"{itemId}:{(int)quality}";

    public string ComputeStableIdentity()
    {
        var canonical = new StringBuilder().Append(CurrentSchemaVersion).Append('|').Append(GearItemId).Append('|').Append((int)GearQuality).Append('|').Append(GearQuantity);
        foreach (var node in ExpandedNodes.OrderBy(node => node.NodeId, StringComparer.Ordinal))
            canonical.Append('|').Append(node.NodeId).Append('>').Append(node.ParentNodeId).Append(':').Append((int)node.Kind).Append(':').Append(node.ItemId).Append(':').Append((int)node.Quality).Append(':').Append(node.Quantity).Append(':').Append(node.RecipeId).Append(':').Append(node.RecipeUnlockItemId);
        foreach (var line in TerminalMaterials.OrderBy(line => line.MaterialKey, StringComparer.Ordinal).ThenBy(line => line.Source.SourceIdentity, StringComparer.Ordinal))
            canonical.Append('|').Append(line.MaterialKey).Append(':').Append(line.RequiredQuantity).Append(':').Append((int)line.Source.Kind).Append(':').Append(line.Source.SourceKey).Append(':').Append(line.Source.SourceIdentity);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private void ValidateSource(OutfitterTerminalMaterialLine line, List<string> errors)
    {
        var source = line.Source;
        if (string.IsNullOrWhiteSpace(source.SourceKey) || string.IsNullOrWhiteSpace(source.SourceIdentity) || source.ItemId != line.ItemId || source.Quality != line.Quality || source.AvailableQuantity < line.RequiredQuantity || source.EvidenceGenerationId == Guid.Empty || source.EvidenceRevision <= 0)
            errors.Add($"Material '{line.MaterialKey}' has incomplete source identity.");
        if (source.EvidenceGenerationId != MarketEvidenceGenerationId || source.EvidenceRevision != MarketEvidenceRevision)
            errors.Add("All material sources must use the plan's single evidence generation and revision.");
    }

    private static bool IsExactQuality(EquipmentQuality quality) => quality is EquipmentQuality.Normal or EquipmentQuality.High;
}
