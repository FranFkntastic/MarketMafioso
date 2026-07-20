using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Crafting;

public enum CraftCostComparisonStatus
{
    Complete,
    DisplayOnly,
    Abstained,
}

/// <summary>
/// Typed identity for the exact equipment allocation whose observed gil cost is being compared.
/// This reference does not turn the craft plan into a solver offer.
/// </summary>
public sealed record ComparedGearAllocation(
    EquipmentOfferAllocationKey AllocationKey,
    uint Quantity,
    ulong TotalGil);

public sealed record CraftAcquisitionBurden(
    int CraftNodeCount,
    int SubcraftNodeCount,
    int DistinctMaterialCount,
    int MarketSourceCount,
    int VendorSourceCount);

public sealed record CraftCostComparisonValidation(bool IsValid, ImmutableArray<string> Errors);

/// <summary>
/// Passive economy comparison over one frozen expanded recipe tree. It carries no solver,
/// Workbench, Artisan, route, or purchase authority.
/// </summary>
public sealed record CraftCostComparison(
    string SchemaVersion,
    string ComparisonId,
    CraftCostComparisonStatus Status,
    OutfitterCraftPlan Plan,
    OutfitterCraftPlanIdentity PlanIdentity,
    ulong TotalGil,
    uint EffectiveUnitGil,
    ComparedGearAllocation ComparedAllocation,
    long SavingsGil,
    CraftAcquisitionBurden Burden,
    DateTimeOffset BuiltAtUtc,
    ImmutableArray<string> Diagnostics)
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-outfitter-craft-cost-comparison/v1";

    public CraftCostComparisonValidation Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion || string.IsNullOrWhiteSpace(ComparisonId))
            errors.Add("Cost-comparison schema and identity must be complete.");
        if (!Enum.IsDefined(Status))
            errors.Add("Cost-comparison status is unsupported.");
        if (BuiltAtUtc == default)
            errors.Add("Cost comparisons require an explicit build time.");
        if (Diagnostics.IsDefault)
            errors.Add("Cost-comparison diagnostics must be initialized.");
        if (Plan is null)
        {
            errors.Add("A cost comparison requires a craft plan.");
            return Invalid(errors);
        }

        var planValidation = Plan.Validate(Status == CraftCostComparisonStatus.Complete);
        errors.AddRange(planValidation.Errors);
        if (BuiltAtUtc < Plan.BuiltAtUtc)
            errors.Add("A cost comparison cannot predate its frozen craft plan.");

        if (planValidation.IsValid)
        {
            if (PlanIdentity is null ||
                string.IsNullOrWhiteSpace(PlanIdentity.Sha256) ||
                PlanIdentity != Plan.ComputeStructuralIdentity())
            {
                errors.Add("Cost comparison must bind the exact structural craft-plan identity.");
            }
        }
        else if (PlanIdentity is null || string.IsNullOrWhiteSpace(PlanIdentity.Sha256))
        {
            errors.Add("Cost comparison must carry a structural craft-plan identity.");
        }

        var allocationValid = ValidateComparedAllocation(errors);
        if (planValidation.IsValid && allocationValid)
        {
            try
            {
                var materialTotal = Plan.TerminalMaterials.Aggregate(
                    0ul,
                    (sum, line) => checked(sum + checked((ulong)line.RequiredQuantity * line.Source.UnitPriceGil)));
                if (materialTotal != TotalGil)
                    errors.Add("Cost-comparison total does not equal complete terminal material cost.");

                var quotient = TotalGil / Plan.GearQuantity;
                var remainder = TotalGil % Plan.GearQuantity;
                var expectedUnit = checked((uint)(quotient + (remainder == 0 ? 0ul : 1ul)));
                if (EffectiveUnitGil != expectedUnit)
                    errors.Add("Effective unit gil is inconsistent with total and exact gear quantity.");

                var expectedSavings = checked(checked((long)ComparedAllocation.TotalGil) - checked((long)TotalGil));
                if (SavingsGil != expectedSavings)
                    errors.Add("Savings do not match the compared exact gear allocation.");
            }
            catch (OverflowException)
            {
                errors.Add("Cost-comparison gil arithmetic overflowed.");
            }

            ValidateBurden(errors);
        }

        if (!Diagnostics.IsDefault)
        {
            if (Status == CraftCostComparisonStatus.Complete && Diagnostics.Length != 0)
                errors.Add("A complete cost comparison cannot retain unresolved diagnostics.");
            if (Status != CraftCostComparisonStatus.Complete && Diagnostics.Length == 0)
                errors.Add("A display-only or abstained cost comparison requires an explanatory diagnostic.");
        }

        return errors.Count == 0 ? new(true, ImmutableArray<string>.Empty) : Invalid(errors);
    }

    private bool ValidateComparedAllocation(List<string> errors)
    {
        var key = ComparedAllocation?.AllocationKey;
        var offerKey = key?.OfferKey;
        if (key is null || offerKey is null ||
            offerKey.ItemId != Plan.GearItemId ||
            offerKey.Quality != Plan.GearQuality ||
            !Enum.IsDefined(offerKey.SourceKind) ||
            string.IsNullOrWhiteSpace(offerKey.SourceCatalogKey) ||
            ComparedAllocation!.Quantity != Plan.GearQuantity)
        {
            errors.Add("Compared gear allocation must use a typed exact offer identity matching plan item, quality, and quantity.");
            return false;
        }

        if (offerKey.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard && string.IsNullOrWhiteSpace(key.ObservationId))
        {
            errors.Add("A compared market allocation requires an exact observation identity.");
            return false;
        }
        return true;
    }

    private void ValidateBurden(List<string> errors)
    {
        var craftNodeCount = Plan.ExpandedNodes.Count(node => node.Kind == OutfitterCraftNodeKind.Craft);
        var subcraftNodeCount = Math.Max(0, craftNodeCount - 1);
        var distinctMaterialCount = Plan.TerminalMaterials
            .Select(line => line.MaterialKey)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var marketSourceCount = Plan.TerminalMaterials.Count(line => line.Source.Kind == OutfitterMaterialSourceKind.MarketListing);
        var vendorSourceCount = Plan.TerminalMaterials.Count(line => line.Source.Kind == OutfitterMaterialSourceKind.GilVendor);

        if (Burden is null ||
            Burden.CraftNodeCount != craftNodeCount ||
            Burden.SubcraftNodeCount != subcraftNodeCount ||
            Burden.DistinctMaterialCount != distinctMaterialCount ||
            Burden.MarketSourceCount != marketSourceCount ||
            Burden.VendorSourceCount != vendorSourceCount)
        {
            errors.Add("Craft acquisition burden is inconsistent with the frozen recipe tree and terminal sources.");
        }
    }

    private static CraftCostComparisonValidation Invalid(IEnumerable<string> errors) =>
        new(false, errors.Distinct(StringComparer.Ordinal).ToImmutableArray());
}
