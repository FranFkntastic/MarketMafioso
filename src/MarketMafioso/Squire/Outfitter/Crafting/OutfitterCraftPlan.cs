using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

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
    UnprovenCrafter,
    MasterRecipe,
    HqOutcomeUnproven,
    ArithmeticOverflow,
    InvalidIdentity,
}

public sealed record OutfitterCraftDiagnostic(OutfitterCraftDiagnosticCode Code, string Message, string? NodeId = null);

public enum OutfitterCraftEligibilityState
{
    ProvenEligible,
    ProvenIneligible,
    Unproven,
}

/// <summary>
/// Rendered active-job evidence for one exact recipe node. Eligibility is proved only when the
/// observed active crafting job and level satisfy that recipe's own requirements.
/// </summary>
public sealed record OutfitterCraftEligibilityEvidence(
    OutfitterCraftEligibilityState State,
    string EvidenceId,
    Guid EvidenceGenerationId,
    long EvidenceRevision,
    DateTimeOffset ObservedAtUtc,
    CharacterScope? Character,
    string NodeId,
    uint RecipeId,
    uint RequiredClassJobId,
    int RequiredLevel,
    uint ObservedClassJobId,
    int ObservedLevel,
    string? Diagnostic = null);

public sealed record OutfitterCrafterObservationIdentity(
    CharacterScope Character,
    Guid GenerationId,
    long Revision,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset FreshUntilUtc);

/// <summary>
/// Explicit proof boundary for a requested HQ craft outcome. Static recipe identity, crafter
/// eligibility, and the generic crafter utility profile do not constitute this proof.
/// </summary>
public sealed record OutfitterCraftHqCapabilityProof(
    string ProofId,
    string ProofModelId,
    string ProofModelVersion,
    string CrafterEvidenceId,
    Guid CrafterEvidenceGenerationId,
    long CrafterEvidenceRevision,
    DateTimeOffset ProvenAtUtc,
    string NodeId,
    uint RecipeId,
    uint ItemId,
    EquipmentQuality Quality,
    uint Quantity);

public sealed record OutfitterResolvedRecipeIngredient(
    string ChildNodeId,
    uint ItemId,
    EquipmentQuality Quality,
    uint QuantityPerCraft);

/// <summary>
/// Immutable static recipe resolution captured by a future trusted resolver. The expanded tree
/// must match this complete snapshot exactly; the contract never re-resolves recipes itself.
/// </summary>
public sealed record OutfitterResolvedRecipeSnapshot(
    string ResolverId,
    string ResolverVersion,
    string DefinitionFingerprint,
    uint RecipeId,
    uint OutputItemId,
    uint OutputQuantity,
    uint RequiredClassJobId,
    int RequiredLevel,
    uint RecipeUnlockItemId,
    ImmutableArray<OutfitterResolvedRecipeIngredient> Ingredients);

public sealed record OutfitterCraftNode(
    string NodeId,
    string? ParentNodeId,
    OutfitterCraftNodeKind Kind,
    uint ItemId,
    EquipmentQuality Quality,
    uint RequiredQuantity,
    uint QuantityPerParentCraft = 0,
    uint RecipeId = 0,
    uint RecipeOutputQuantity = 0,
    uint RecipeUnlockItemId = 0,
    OutfitterResolvedRecipeSnapshot? ResolvedRecipe = null,
    OutfitterCraftEligibilityEvidence? Eligibility = null,
    OutfitterCraftHqCapabilityProof? HqCapabilityProof = null);

public enum OutfitterMaterialSourceKind
{
    MarketListing,
    GilVendor,
}

public sealed record CraftMarketEvidenceReference(
    Guid GenerationId,
    long Revision,
    string SchemaVersion,
    string SourceKey,
    string Region);

public abstract record OutfitterMaterialSourceIdentity(
    uint ItemId,
    EquipmentQuality Quality,
    uint UnitPriceGil)
{
    public abstract OutfitterMaterialSourceKind Kind { get; }
}

public sealed record OutfitterMarketMaterialSourceIdentity(
    uint ItemId,
    EquipmentQuality Quality,
    uint UnitPriceGil,
    uint AvailableQuantity,
    string ListingId,
    uint WorldId,
    string WorldName,
    DateTimeOffset ReviewedAtUtc,
    string SourceRevision,
    Guid EvidenceGenerationId,
    long EvidenceRevision)
    : OutfitterMaterialSourceIdentity(ItemId, Quality, UnitPriceGil)
{
    public override OutfitterMaterialSourceKind Kind => OutfitterMaterialSourceKind.MarketListing;
}

public sealed record OutfitterGilVendorMaterialSourceIdentity(
    uint ItemId,
    EquipmentQuality Quality,
    uint UnitPriceGil,
    uint ShopId,
    uint VendorId,
    uint TerritoryId,
    string VendorName,
    string TerritoryName,
    string CatalogVersion)
    : OutfitterMaterialSourceIdentity(ItemId, Quality, UnitPriceGil)
{
    public override OutfitterMaterialSourceKind Kind => OutfitterMaterialSourceKind.GilVendor;
}

public sealed record OutfitterTerminalMaterialLine(
    string MaterialKey,
    uint ItemId,
    EquipmentQuality Quality,
    uint RequiredQuantity,
    OutfitterMaterialSourceIdentity Source);

public sealed record OutfitterCraftPlanIdentity(string Sha256)
{
    public override string ToString() => Sha256;
}

public sealed record OutfitterCraftPlanValidation(bool IsValid, ImmutableArray<string> Errors)
{
    public static OutfitterCraftPlanValidation Valid { get; } = new(true, ImmutableArray<string>.Empty);
}

/// <summary>
/// Immutable, fully expanded recipe tree. Consumers must use ExpandedNodes and must never expand recipes again.
/// This contract describes craft-cost evidence; it is not a solver offer, Workbench contract, or purchase authority.
/// </summary>
public sealed record OutfitterCraftPlan(
    string SchemaVersion,
    string PlanId,
    uint GearItemId,
    EquipmentQuality GearQuality,
    uint GearQuantity,
    string RootNodeId,
    OutfitterCrafterObservationIdentity CrafterObservation,
    int MaximumDepth,
    ImmutableArray<OutfitterCraftNode> ExpandedNodes,
    ImmutableArray<OutfitterTerminalMaterialLine> TerminalMaterials,
    CraftMarketEvidenceReference? MarketEvidence,
    DateTimeOffset BuiltAtUtc,
    ImmutableArray<OutfitterCraftDiagnostic> Diagnostics)
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-outfitter-craft-plan/v2";

    public OutfitterCraftPlanValidation Validate(bool requireEconomyReady = false)
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
            errors.Add("Unsupported craft-plan schema version.");
        if (string.IsNullOrWhiteSpace(PlanId) || GearItemId == 0 || GearQuantity == 0 || string.IsNullOrWhiteSpace(RootNodeId))
            errors.Add("Plan, gear, root, and quantity identity must be complete.");
        if (!IsExactQuality(GearQuality))
            errors.Add("Gear quality must be exact NQ or HQ.");
        if (MaximumDepth is < 1 or > 64)
            errors.Add("Maximum recipe depth must be between 1 and 64.");
        if (BuiltAtUtc == default)
            errors.Add("Craft plans require an explicit build time.");
        var crafterObservationValid = ValidCrafterObservation(CrafterObservation);
        if (!crafterObservationValid ||
            BuiltAtUtc < CrafterObservation.CapturedAtUtc ||
            BuiltAtUtc > CrafterObservation.FreshUntilUtc)
        {
            errors.Add("Craft plans require one complete, fresh crafter observation identity at build time.");
        }
        if (ExpandedNodes.IsDefaultOrEmpty)
            errors.Add("The expanded recipe tree is empty.");
        if (TerminalMaterials.IsDefault)
            errors.Add("Terminal material lines must be initialized.");
        if (Diagnostics.IsDefault)
            errors.Add("Craft diagnostics must be initialized.");
        if (ExpandedNodes.IsDefault || TerminalMaterials.IsDefault || Diagnostics.IsDefault)
            return Invalid(errors);
        if (ExpandedNodes.Any(node => node is null) ||
            TerminalMaterials.Any(line => line is null) ||
            Diagnostics.Any(diagnostic => diagnostic is null))
        {
            errors.Add("Craft-plan collections cannot contain null records.");
            return Invalid(errors);
        }
        if (!crafterObservationValid)
            return Invalid(errors);

        var duplicateNodeIds = ExpandedNodes
            .GroupBy(node => node.NodeId, StringComparer.Ordinal)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateNodeIds.Length != 0 || ExpandedNodes.Any(node => string.IsNullOrWhiteSpace(node.NodeId)))
            errors.Add("Expanded node identity is ambiguous.");

        var nodes = ExpandedNodes
            .Where(node => !string.IsNullOrWhiteSpace(node.NodeId))
            .GroupBy(node => node.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var childrenByParent = ExpandedNodes
            .Where(node => node.ParentNodeId is not null)
            .GroupBy(node => node.ParentNodeId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        if (!nodes.TryGetValue(RootNodeId, out var root) ||
            root.ParentNodeId is not null ||
            root.Kind != OutfitterCraftNodeKind.Craft ||
            root.ItemId != GearItemId ||
            root.Quality != GearQuality ||
            root.RequiredQuantity != GearQuantity ||
            root.QuantityPerParentCraft != 0)
        {
            errors.Add("The root node must exactly identify the requested gear and quantity.");
        }

        foreach (var node in ExpandedNodes)
        {
            if (!Enum.IsDefined(node.Kind))
            {
                errors.Add($"Node '{node.NodeId}' has an unsupported node kind.");
                continue;
            }
            if (node.ItemId == 0 || node.RequiredQuantity == 0 || !IsExactQuality(node.Quality))
                errors.Add($"Node '{node.NodeId}' has incomplete item, quality, or quantity identity.");
            if (node.NodeId != RootNodeId &&
                (node.ParentNodeId is null || !nodes.ContainsKey(node.ParentNodeId) || node.QuantityPerParentCraft == 0))
            {
                errors.Add($"Node '{node.NodeId}' is disconnected from the expanded tree or lacks parent quantity identity.");
            }

            var children = childrenByParent.GetValueOrDefault(node.NodeId) ?? [];
            switch (node.Kind)
            {
                case OutfitterCraftNodeKind.Craft:
                    if (node.RecipeId == 0 || node.RecipeOutputQuantity == 0 || node.ResolvedRecipe is null || node.Eligibility is null)
                        errors.Add($"Craft node '{node.NodeId}' lacks recipe, frozen resolution, yield, or eligibility identity.");
                    if (children.Length == 0)
                        errors.Add($"Craft node '{node.NodeId}' has no expanded ingredients.");
                    if (node.ResolvedRecipe is not null)
                        ValidateResolvedRecipe(node, children, errors);
                    if (node.Eligibility is not null)
                        ValidateEligibility(node, errors);
                    ValidateHqProof(node, requireEconomyReady, errors);
                    break;
                case OutfitterCraftNodeKind.Material:
                    if (node.RecipeId != 0 || node.RecipeOutputQuantity != 0 || node.RecipeUnlockItemId != 0 ||
                        node.ResolvedRecipe is not null || node.Eligibility is not null || node.HqCapabilityProof is not null || children.Length != 0)
                    {
                        errors.Add($"Material node '{node.NodeId}' cannot carry recipe, resolution, eligibility, proof, or child identity.");
                    }
                    break;
            }
        }

        ValidateTreeDepthAndCycles(nodes, errors);
        ValidateExpandedQuantities(nodes, errors);
        ValidateTerminalCoverage(errors);

        var blockingExpansionDiagnostics = Diagnostics.Any(diagnostic => diagnostic.Code is
            OutfitterCraftDiagnosticCode.CircularRecipe or
            OutfitterCraftDiagnosticCode.AmbiguousRecipe or
            OutfitterCraftDiagnosticCode.MaximumDepthExceeded or
            OutfitterCraftDiagnosticCode.IncompleteMaterialCoverage or
            OutfitterCraftDiagnosticCode.ArithmeticOverflow or
            OutfitterCraftDiagnosticCode.InvalidIdentity);
        if (blockingExpansionDiagnostics)
            errors.Add("Expansion diagnostics make the plan structurally invalid.");

        if (requireEconomyReady)
        {
            if (Diagnostics.Length != 0)
                errors.Add("Economy-ready plans cannot retain unresolved diagnostics.");
            if (ExpandedNodes.Any(node => node.Kind == OutfitterCraftNodeKind.Craft &&
                (node.Eligibility?.State != OutfitterCraftEligibilityState.ProvenEligible || node.RecipeUnlockItemId != 0)))
            {
                errors.Add("Economy-ready plans require proven active-job eligibility for every non-master recipe node.");
            }
            if (ExpandedNodes.Any(node => node.Kind == OutfitterCraftNodeKind.Craft &&
                node.Quality == EquipmentQuality.High && node.HqCapabilityProof is null))
            {
                errors.Add("Economy-ready HQ craft outcomes require an explicit recipe capability proof.");
            }
        }

        return errors.Count == 0 ? OutfitterCraftPlanValidation.Valid : Invalid(errors);
    }

    public static string MaterialKey(uint itemId, EquipmentQuality quality) => $"{itemId}:{(int)quality}";

    public OutfitterCraftPlanIdentity ComputeStructuralIdentity()
    {
        var canonical = new StringBuilder();
        AppendString(canonical, CurrentSchemaVersion);
        canonical.Append('|').Append(GearItemId).Append('|').Append((int)GearQuality).Append('|').Append(GearQuantity).Append('|').Append(MaximumDepth);
        AppendString(canonical, RootNodeId);
        AppendCrafterObservation(canonical, CrafterObservation);
        AppendMarketEvidence(canonical, MarketEvidence);

        foreach (var node in ExpandedNodes.OrderBy(node => node.NodeId, StringComparer.Ordinal))
        {
            canonical.Append("|node");
            AppendString(canonical, node.NodeId);
            AppendString(canonical, node.ParentNodeId);
            canonical.Append('|').Append((int)node.Kind)
                .Append('|').Append(node.ItemId)
                .Append('|').Append((int)node.Quality)
                .Append('|').Append(node.RequiredQuantity)
                .Append('|').Append(node.QuantityPerParentCraft)
                .Append('|').Append(node.RecipeId)
                .Append('|').Append(node.RecipeOutputQuantity)
                .Append('|').Append(node.RecipeUnlockItemId);
            AppendResolvedRecipe(canonical, node.ResolvedRecipe);
            AppendEligibility(canonical, node.Eligibility);
            AppendHqProof(canonical, node.HqCapabilityProof);
        }

        foreach (var line in TerminalMaterials
                     .OrderBy(line => line.MaterialKey, StringComparer.Ordinal)
                     .ThenBy(line => line.ItemId)
                     .ThenBy(line => line.Quality)
                     .ThenBy(line => line.RequiredQuantity)
                     .ThenBy(line => SourceSortKey(line.Source), StringComparer.Ordinal))
        {
            canonical.Append("|material");
            AppendString(canonical, line.MaterialKey);
            canonical.Append('|').Append(line.ItemId).Append('|').Append((int)line.Quality).Append('|').Append(line.RequiredQuantity);
            AppendSource(canonical, line.Source);
        }

        foreach (var diagnostic in Diagnostics
                     .OrderBy(diagnostic => diagnostic.Code)
                     .ThenBy(diagnostic => diagnostic.NodeId, StringComparer.Ordinal)
                     .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal))
        {
            canonical.Append("|diagnostic|").Append((int)diagnostic.Code);
            AppendString(canonical, diagnostic.NodeId);
            AppendString(canonical, diagnostic.Message);
        }

        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))));
    }

    private void ValidateTreeDepthAndCycles(IReadOnlyDictionary<string, OutfitterCraftNode> nodes, List<string> errors)
    {
        foreach (var node in ExpandedNodes)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { node.NodeId };
            var cursor = node;
            var depth = 0;
            while (cursor.ParentNodeId is { } parentId)
            {
                if (!nodes.TryGetValue(parentId, out var parent))
                    break;
                if (!seen.Add(parentId))
                {
                    errors.Add("The expanded recipe tree is circular.");
                    break;
                }
                depth = checked(depth + 1);
                if (depth > MaximumDepth)
                {
                    errors.Add($"Node '{node.NodeId}' exceeds maximum recipe depth {MaximumDepth}.");
                    break;
                }
                cursor = parent;
            }
            if (cursor.ParentNodeId is null && cursor.NodeId != RootNodeId)
                errors.Add($"Node '{node.NodeId}' does not descend from the declared root.");
        }
    }

    private void ValidateExpandedQuantities(IReadOnlyDictionary<string, OutfitterCraftNode> nodes, List<string> errors)
    {
        try
        {
            foreach (var node in ExpandedNodes.Where(node => node.ParentNodeId is not null))
            {
                if (!nodes.TryGetValue(node.ParentNodeId!, out var parent) ||
                    parent.Kind != OutfitterCraftNodeKind.Craft ||
                    parent.RecipeOutputQuantity == 0)
                {
                    continue;
                }

                var parentCraftCount = ((ulong)parent.RequiredQuantity + parent.RecipeOutputQuantity - 1) / parent.RecipeOutputQuantity;
                var expectedQuantity = checked(parentCraftCount * node.QuantityPerParentCraft);
                if (expectedQuantity != node.RequiredQuantity)
                    errors.Add($"Node '{node.NodeId}' required quantity does not match its expanded parent recipe quantity.");
            }
        }
        catch (OverflowException)
        {
            errors.Add("Expanded recipe quantity arithmetic overflowed.");
        }
    }

    private void ValidateTerminalCoverage(List<string> errors)
    {
        var expected = new Dictionary<string, uint>(StringComparer.Ordinal);
        var actual = new Dictionary<string, uint>(StringComparer.Ordinal);
        var marketSourceCount = 0;
        try
        {
            foreach (var node in ExpandedNodes.Where(node => node.Kind == OutfitterCraftNodeKind.Material))
            {
                var key = MaterialKey(node.ItemId, node.Quality);
                expected[key] = checked(expected.GetValueOrDefault(key) + node.RequiredQuantity);
            }

            foreach (var line in TerminalMaterials)
            {
                if (line.MaterialKey != MaterialKey(line.ItemId, line.Quality) ||
                    line.ItemId == 0 ||
                    line.RequiredQuantity == 0 ||
                    !IsExactQuality(line.Quality) ||
                    line.Source is null)
                {
                    errors.Add("A terminal material line has incomplete exact identity.");
                    continue;
                }

                ValidateSource(line, errors);
                if (line.Source.Kind == OutfitterMaterialSourceKind.MarketListing)
                    marketSourceCount = checked(marketSourceCount + 1);
                actual[line.MaterialKey] = checked(actual.GetValueOrDefault(line.MaterialKey) + line.RequiredQuantity);
            }

            foreach (var allocation in TerminalMaterials
                         .Where(line => line.Source is OutfitterMarketMaterialSourceIdentity)
                         .GroupBy(line =>
                         {
                             var market = (OutfitterMarketMaterialSourceIdentity)line.Source;
                             return (market.EvidenceGenerationId, market.EvidenceRevision, market.WorldId, market.ListingId);
                         }))
            {
                var sources = allocation
                    .Select(line => (OutfitterMarketMaterialSourceIdentity)line.Source)
                    .Distinct()
                    .ToArray();
                if (sources.Length != 1)
                {
                    errors.Add("One market listing identity cannot carry conflicting listing evidence.");
                    continue;
                }

                var allocatedQuantity = allocation.Aggregate(0ul, (sum, line) => checked(sum + line.RequiredQuantity));
                if (allocatedQuantity > sources[0].AvailableQuantity)
                    errors.Add("One market listing cannot be allocated beyond its available quantity.");
            }
        }
        catch (OverflowException)
        {
            errors.Add("Terminal material quantity arithmetic overflowed.");
        }

        if (!expected.OrderBy(pair => pair.Key).SequenceEqual(actual.OrderBy(pair => pair.Key)))
            errors.Add("Terminal material lines do not completely cover the expanded tree.");
        if (marketSourceCount > 0 && !ValidateMarketEvidenceReference(MarketEvidence))
            errors.Add("Market material sources require one complete market evidence reference.");
        if (marketSourceCount == 0 && MarketEvidence is not null)
            errors.Add("A vendor-only craft plan cannot retain unused market evidence lineage.");
    }

    private void ValidateSource(OutfitterTerminalMaterialLine line, List<string> errors)
    {
        var source = line.Source;
        if (source.ItemId != line.ItemId || source.Quality != line.Quality || source.UnitPriceGil == 0)
        {
            errors.Add($"Material '{line.MaterialKey}' has incomplete source identity.");
            return;
        }

        switch (source)
        {
            case OutfitterMarketMaterialSourceIdentity market:
                if (market.AvailableQuantity < line.RequiredQuantity ||
                    string.IsNullOrWhiteSpace(market.ListingId) ||
                    market.WorldId == 0 ||
                    string.IsNullOrWhiteSpace(market.WorldName) ||
                    market.ReviewedAtUtc == default ||
                    string.IsNullOrWhiteSpace(market.SourceRevision) ||
                    market.EvidenceGenerationId == Guid.Empty ||
                    market.EvidenceRevision <= 0)
                {
                    errors.Add($"Market material '{line.MaterialKey}' has incomplete listing evidence.");
                }
                if (MarketEvidence is null ||
                    market.EvidenceGenerationId != MarketEvidence.GenerationId ||
                    market.EvidenceRevision != MarketEvidence.Revision)
                {
                    errors.Add("Market material sources must use the plan's exact market evidence generation and revision.");
                }
                break;
            case OutfitterGilVendorMaterialSourceIdentity vendor:
                if (vendor.Quality != EquipmentQuality.Normal ||
                    vendor.ShopId == 0 ||
                    vendor.VendorId == 0 ||
                    vendor.TerritoryId == 0 ||
                    string.IsNullOrWhiteSpace(vendor.VendorName) ||
                    string.IsNullOrWhiteSpace(vendor.TerritoryName) ||
                    string.IsNullOrWhiteSpace(vendor.CatalogVersion))
                {
                    errors.Add($"Gil-vendor material '{line.MaterialKey}' has incomplete vendor catalog identity.");
                }
                break;
            default:
                errors.Add($"Material '{line.MaterialKey}' uses an unsupported source kind.");
                break;
        }
    }

    private static void ValidateResolvedRecipe(
        OutfitterCraftNode node,
        IReadOnlyCollection<OutfitterCraftNode> children,
        List<string> errors)
    {
        var recipe = node.ResolvedRecipe!;
        if (string.IsNullOrWhiteSpace(recipe.ResolverId) ||
            string.IsNullOrWhiteSpace(recipe.ResolverVersion) ||
            string.IsNullOrWhiteSpace(recipe.DefinitionFingerprint) ||
            recipe.RecipeId != node.RecipeId ||
            recipe.OutputItemId != node.ItemId ||
            recipe.OutputQuantity != node.RecipeOutputQuantity ||
            recipe.RequiredClassJobId != node.Eligibility?.RequiredClassJobId ||
            recipe.RequiredLevel != node.Eligibility?.RequiredLevel ||
            recipe.RecipeUnlockItemId != node.RecipeUnlockItemId ||
            recipe.Ingredients.IsDefault)
        {
            errors.Add($"Craft node '{node.NodeId}' does not match its frozen static recipe resolution.");
            return;
        }
        if (recipe.Ingredients.Any(ingredient => ingredient is null ||
            string.IsNullOrWhiteSpace(ingredient.ChildNodeId) ||
            ingredient.ItemId == 0 ||
            ingredient.QuantityPerCraft == 0 ||
            !IsExactQuality(ingredient.Quality)))
        {
            errors.Add($"Craft node '{node.NodeId}' has incomplete frozen ingredient identity.");
            return;
        }

        var resolvedIngredients = recipe.Ingredients
            .OrderBy(ingredient => ingredient.ChildNodeId, StringComparer.Ordinal)
            .ThenBy(ingredient => ingredient.ItemId)
            .ThenBy(ingredient => ingredient.Quality)
            .ThenBy(ingredient => ingredient.QuantityPerCraft)
            .Select(ingredient => (ingredient.ChildNodeId, ingredient.ItemId, ingredient.Quality, ingredient.QuantityPerCraft));
        var expandedIngredients = children
            .OrderBy(child => child.NodeId, StringComparer.Ordinal)
            .ThenBy(child => child.ItemId)
            .ThenBy(child => child.Quality)
            .ThenBy(child => child.QuantityPerParentCraft)
            .Select(child => (child.NodeId, child.ItemId, child.Quality, child.QuantityPerParentCraft));
        if (!resolvedIngredients.SequenceEqual(expandedIngredients))
            errors.Add($"Craft node '{node.NodeId}' expanded ingredients do not match its frozen static recipe resolution.");
    }

    private void ValidateEligibility(OutfitterCraftNode node, List<string> errors)
    {
        var evidence = node.Eligibility!;
        if (string.IsNullOrWhiteSpace(evidence.EvidenceId) ||
            evidence.EvidenceGenerationId == Guid.Empty ||
            evidence.EvidenceRevision <= 0 ||
            evidence.ObservedAtUtc == default ||
            evidence.NodeId != node.NodeId ||
            evidence.RecipeId != node.RecipeId ||
            evidence.RequiredClassJobId != node.ResolvedRecipe?.RequiredClassJobId ||
            evidence.RequiredLevel != node.ResolvedRecipe?.RequiredLevel ||
            !CrafterUtilityProfile.CrafterClassJobIds.Contains(evidence.RequiredClassJobId) ||
            evidence.RequiredLevel is < 1 or > 100 ||
            evidence.Character != CrafterObservation.Character ||
            evidence.EvidenceGenerationId != CrafterObservation.GenerationId ||
            evidence.EvidenceRevision != CrafterObservation.Revision ||
            evidence.ObservedAtUtc < CrafterObservation.CapturedAtUtc ||
            evidence.ObservedAtUtc > CrafterObservation.FreshUntilUtc ||
            evidence.ObservedAtUtc > BuiltAtUtc)
        {
            errors.Add($"Craft node '{node.NodeId}' has incomplete or mismatched active-job eligibility evidence.");
            return;
        }

        switch (evidence.State)
        {
            case OutfitterCraftEligibilityState.ProvenEligible:
                if (evidence.ObservedClassJobId != evidence.RequiredClassJobId ||
                    !CrafterUtilityProfile.CrafterClassJobIds.Contains(evidence.ObservedClassJobId) ||
                    evidence.ObservedLevel is < 1 or > 100 ||
                    evidence.ObservedLevel < evidence.RequiredLevel ||
                    !string.IsNullOrWhiteSpace(evidence.Diagnostic))
                {
                    errors.Add($"Craft node '{node.NodeId}' claims eligibility without matching active crafting job and level proof.");
                }
                break;
            case OutfitterCraftEligibilityState.ProvenIneligible:
                if (evidence.ObservedClassJobId == 0 ||
                    evidence.ObservedLevel is < 1 or > 100 ||
                    (evidence.ObservedClassJobId == evidence.RequiredClassJobId &&
                        evidence.ObservedLevel >= evidence.RequiredLevel) ||
                    string.IsNullOrWhiteSpace(evidence.Diagnostic))
                {
                    errors.Add($"Craft node '{node.NodeId}' has incomplete ineligibility evidence.");
                }
                break;
            case OutfitterCraftEligibilityState.Unproven:
                if (string.IsNullOrWhiteSpace(evidence.Diagnostic) ||
                    (evidence.ObservedClassJobId == 0) != (evidence.ObservedLevel == 0) ||
                    evidence.ObservedLevel is < 0 or > 100)
                {
                    errors.Add($"Craft node '{node.NodeId}' has incomplete unproven eligibility evidence.");
                }
                break;
            default:
                errors.Add($"Craft node '{node.NodeId}' has an unsupported eligibility state.");
                break;
        }
    }

    private void ValidateHqProof(OutfitterCraftNode node, bool requireEconomyReady, List<string> errors)
    {
        var proof = node.HqCapabilityProof;
        if (node.Quality == EquipmentQuality.Normal)
        {
            if (proof is not null)
                errors.Add($"NQ craft node '{node.NodeId}' cannot carry an HQ capability proof.");
            return;
        }
        if (proof is null)
        {
            if (requireEconomyReady)
                errors.Add($"HQ craft node '{node.NodeId}' lacks a recipe capability proof.");
            return;
        }
        if (string.IsNullOrWhiteSpace(proof.ProofId) ||
            string.IsNullOrWhiteSpace(proof.ProofModelId) ||
            string.IsNullOrWhiteSpace(proof.ProofModelVersion) ||
            string.IsNullOrWhiteSpace(proof.CrafterEvidenceId) ||
            proof.CrafterEvidenceGenerationId == Guid.Empty ||
            proof.CrafterEvidenceRevision <= 0 ||
            proof.ProvenAtUtc == default ||
            proof.NodeId != node.NodeId ||
            proof.RecipeId != node.RecipeId ||
            proof.ItemId != node.ItemId ||
            proof.Quality != EquipmentQuality.High ||
            proof.Quantity < node.RequiredQuantity ||
            proof.CrafterEvidenceId != node.Eligibility?.EvidenceId ||
            proof.CrafterEvidenceGenerationId != node.Eligibility?.EvidenceGenerationId ||
            proof.CrafterEvidenceRevision != node.Eligibility?.EvidenceRevision ||
            proof.CrafterEvidenceGenerationId != CrafterObservation.GenerationId ||
            proof.CrafterEvidenceRevision != CrafterObservation.Revision ||
            node.Eligibility is null ||
            proof.ProvenAtUtc < node.Eligibility.ObservedAtUtc ||
            proof.ProvenAtUtc < CrafterObservation.CapturedAtUtc ||
            proof.ProvenAtUtc > CrafterObservation.FreshUntilUtc ||
            proof.ProvenAtUtc > BuiltAtUtc)
        {
            errors.Add($"HQ craft node '{node.NodeId}' has incomplete or mismatched recipe capability proof.");
        }
    }

    private static bool ValidateMarketEvidenceReference(CraftMarketEvidenceReference? evidence) =>
        evidence is not null &&
        evidence.GenerationId != Guid.Empty &&
        evidence.Revision > 0 &&
        !string.IsNullOrWhiteSpace(evidence.SchemaVersion) &&
        !string.IsNullOrWhiteSpace(evidence.SourceKey) &&
        !string.IsNullOrWhiteSpace(evidence.Region);

    private static bool ValidCrafterObservation(OutfitterCrafterObservationIdentity? observation) =>
        observation is not null &&
        ValidCharacter(observation.Character) &&
        observation.GenerationId != Guid.Empty &&
        observation.Revision > 0 &&
        observation.CapturedAtUtc != default &&
        observation.FreshUntilUtc > observation.CapturedAtUtc;

    private static bool ValidCharacter(CharacterScope? character) =>
        character is { LocalContentId: > 0, HomeWorldId: > 0 } && !string.IsNullOrWhiteSpace(character.Name);

    private static bool IsExactQuality(EquipmentQuality quality) =>
        quality is EquipmentQuality.Normal or EquipmentQuality.High;

    private static OutfitterCraftPlanValidation Invalid(IEnumerable<string> errors) =>
        new(false, errors.Distinct(StringComparer.Ordinal).ToImmutableArray());

    private static string SourceSortKey(OutfitterMaterialSourceIdentity source)
    {
        var canonical = new StringBuilder();
        AppendSource(canonical, source);
        return canonical.ToString();
    }

    private static void AppendCrafterObservation(StringBuilder canonical, OutfitterCrafterObservationIdentity observation)
    {
        canonical.Append("|crafter-observation")
            .Append('|').Append(observation.Character.LocalContentId)
            .Append('|').Append(observation.Character.HomeWorldId);
        AppendString(canonical, observation.Character.Name);
        canonical.Append('|').Append(observation.GenerationId)
            .Append('|').Append(observation.Revision)
            .Append('|').Append(observation.CapturedAtUtc.UtcDateTime.Ticks)
            .Append('|').Append(observation.FreshUntilUtc.UtcDateTime.Ticks);
    }

    private static void AppendResolvedRecipe(StringBuilder canonical, OutfitterResolvedRecipeSnapshot? recipe)
    {
        canonical.Append("|resolved-recipe|").Append(recipe is null ? 0 : 1);
        if (recipe is null)
            return;
        AppendString(canonical, recipe.ResolverId);
        AppendString(canonical, recipe.ResolverVersion);
        AppendString(canonical, recipe.DefinitionFingerprint);
        canonical.Append('|').Append(recipe.RecipeId)
            .Append('|').Append(recipe.OutputItemId)
            .Append('|').Append(recipe.OutputQuantity)
            .Append('|').Append(recipe.RequiredClassJobId)
            .Append('|').Append(recipe.RequiredLevel)
            .Append('|').Append(recipe.RecipeUnlockItemId);
        foreach (var ingredient in recipe.Ingredients
                     .OrderBy(ingredient => ingredient.ChildNodeId, StringComparer.Ordinal)
                     .ThenBy(ingredient => ingredient.ItemId)
                     .ThenBy(ingredient => ingredient.Quality)
                     .ThenBy(ingredient => ingredient.QuantityPerCraft))
        {
            canonical.Append("|ingredient");
            AppendString(canonical, ingredient.ChildNodeId);
            canonical.Append('|').Append(ingredient.ItemId)
                .Append('|').Append((int)ingredient.Quality)
                .Append('|').Append(ingredient.QuantityPerCraft);
        }
    }

    private static void AppendMarketEvidence(StringBuilder canonical, CraftMarketEvidenceReference? evidence)
    {
        canonical.Append("|market-evidence|").Append(evidence is null ? 0 : 1);
        if (evidence is null)
            return;
        canonical.Append('|').Append(evidence.GenerationId).Append('|').Append(evidence.Revision);
        AppendString(canonical, evidence.SchemaVersion);
        AppendString(canonical, evidence.SourceKey);
        AppendString(canonical, evidence.Region);
    }

    private static void AppendEligibility(StringBuilder canonical, OutfitterCraftEligibilityEvidence? evidence)
    {
        canonical.Append("|eligibility|").Append(evidence is null ? 0 : 1);
        if (evidence is null)
            return;
        canonical.Append('|').Append((int)evidence.State);
        AppendString(canonical, evidence.EvidenceId);
        canonical.Append('|').Append(evidence.EvidenceGenerationId)
            .Append('|').Append(evidence.EvidenceRevision)
            .Append('|').Append(evidence.ObservedAtUtc.UtcDateTime.Ticks)
            .Append('|').Append(evidence.Character?.LocalContentId ?? 0)
            .Append('|').Append(evidence.Character?.HomeWorldId ?? 0);
        AppendString(canonical, evidence.Character?.Name);
        AppendString(canonical, evidence.NodeId);
        canonical.Append('|').Append(evidence.RecipeId)
            .Append('|').Append(evidence.RequiredClassJobId)
            .Append('|').Append(evidence.RequiredLevel)
            .Append('|').Append(evidence.ObservedClassJobId)
            .Append('|').Append(evidence.ObservedLevel);
        AppendString(canonical, evidence.Diagnostic);
    }

    private static void AppendHqProof(StringBuilder canonical, OutfitterCraftHqCapabilityProof? proof)
    {
        canonical.Append("|hq-proof|").Append(proof is null ? 0 : 1);
        if (proof is null)
            return;
        AppendString(canonical, proof.ProofId);
        AppendString(canonical, proof.ProofModelId);
        AppendString(canonical, proof.ProofModelVersion);
        AppendString(canonical, proof.CrafterEvidenceId);
        canonical.Append('|').Append(proof.CrafterEvidenceGenerationId)
            .Append('|').Append(proof.CrafterEvidenceRevision)
            .Append('|').Append(proof.ProvenAtUtc.UtcDateTime.Ticks);
        AppendString(canonical, proof.NodeId);
        canonical.Append('|').Append(proof.RecipeId)
            .Append('|').Append(proof.ItemId)
            .Append('|').Append((int)proof.Quality)
            .Append('|').Append(proof.Quantity);
    }

    private static void AppendSource(StringBuilder canonical, OutfitterMaterialSourceIdentity source)
    {
        canonical.Append('|').Append((int)source.Kind)
            .Append('|').Append(source.ItemId)
            .Append('|').Append((int)source.Quality)
            .Append('|').Append(source.UnitPriceGil);
        switch (source)
        {
            case OutfitterMarketMaterialSourceIdentity market:
                canonical.Append('|').Append(market.AvailableQuantity);
                AppendString(canonical, market.ListingId);
                canonical.Append('|').Append(market.WorldId);
                AppendString(canonical, market.WorldName);
                canonical.Append('|').Append(market.ReviewedAtUtc.UtcDateTime.Ticks);
                AppendString(canonical, market.SourceRevision);
                canonical.Append('|').Append(market.EvidenceGenerationId).Append('|').Append(market.EvidenceRevision);
                break;
            case OutfitterGilVendorMaterialSourceIdentity vendor:
                canonical.Append('|').Append(vendor.ShopId).Append('|').Append(vendor.VendorId).Append('|').Append(vendor.TerritoryId);
                AppendString(canonical, vendor.VendorName);
                AppendString(canonical, vendor.TerritoryName);
                AppendString(canonical, vendor.CatalogVersion);
                break;
        }
    }

    private static void AppendString(StringBuilder canonical, string? value)
    {
        canonical.Append('|');
        if (value is null)
        {
            canonical.Append("-1:");
            return;
        }
        canonical.Append(value.Length).Append(':').Append(value);
    }
}
