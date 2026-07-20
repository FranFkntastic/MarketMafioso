using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Crafting;

public enum CraftAcquisitionQuoteStatus
{
    Actionable,
    DisplayOnly,
    Abstained,
}

public sealed record CraftMarketEvidenceReference(
    Guid GenerationId,
    long Revision,
    string SchemaVersion,
    string SourceKey,
    string Region);

public sealed record ComparedGearAllocation(
    string AllocationKey,
    uint ItemId,
    EquipmentQuality Quality,
    uint Quantity,
    ulong TotalGil);

public sealed record CraftAcquisitionBurden(
    int CraftCount,
    int SubcraftCount,
    int DistinctMaterialCount,
    int MarketSourceCount,
    int VendorSourceCount);

public sealed record CraftAcquisitionQuoteValidation(bool IsValid, bool IsSolverOffer, ImmutableArray<string> Errors);

public sealed record CraftAcquisitionQuote(
    string SchemaVersion,
    string QuoteId,
    CraftAcquisitionQuoteStatus Status,
    OutfitterCraftPlan Plan,
    ulong TotalGil,
    uint EffectiveUnitGil,
    ComparedGearAllocation ComparedAllocation,
    long SavingsGil,
    CraftMarketEvidenceReference Evidence,
    CraftAcquisitionBurden Burden,
    DateTimeOffset BuiltAtUtc,
    ImmutableArray<string> Diagnostics)
{
    public const string CurrentSchemaVersion = "marketmafioso-squire-outfitter-craft-acquisition-quote/v1";

    public CraftAcquisitionQuoteValidation Validate()
    {
        var errors = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion || string.IsNullOrWhiteSpace(QuoteId))
            errors.Add("Quote schema and identity must be complete.");
        if (Plan is null)
            return new(false, false, ["A quote requires a craft plan."]);

        var planValidation = Plan.Validate(Status == CraftAcquisitionQuoteStatus.Actionable);
        errors.AddRange(planValidation.Errors);
        if (Evidence.GenerationId == Guid.Empty || Evidence.Revision <= 0 || string.IsNullOrWhiteSpace(Evidence.SchemaVersion) || string.IsNullOrWhiteSpace(Evidence.SourceKey) || string.IsNullOrWhiteSpace(Evidence.Region))
            errors.Add("Evidence lineage must be complete.");
        if (Evidence.GenerationId != Plan.MarketEvidenceGenerationId || Evidence.Revision != Plan.MarketEvidenceRevision)
            errors.Add("Quote and plan evidence generations must match exactly.");
        if (ComparedAllocation.ItemId != Plan.GearItemId || ComparedAllocation.Quality != Plan.GearQuality || ComparedAllocation.Quantity != Plan.GearQuantity || string.IsNullOrWhiteSpace(ComparedAllocation.AllocationKey))
            errors.Add("Compared gear allocation must exactly match plan item, quality, and quantity.");

        try
        {
            var materialTotal = Plan.TerminalMaterials.Aggregate(0ul, (sum, line) => checked(sum + checked((ulong)line.RequiredQuantity * line.Source.UnitPriceGil)));
            if (materialTotal != TotalGil)
                errors.Add("Quote total does not equal complete terminal material cost.");
            var expectedUnit = checked((uint)((TotalGil + Plan.GearQuantity - 1) / Plan.GearQuantity));
            if (EffectiveUnitGil != expectedUnit)
                errors.Add("Effective unit gil is inconsistent with total and exact gear quantity.");
            var expectedSavings = checked((long)ComparedAllocation.TotalGil - (long)TotalGil);
            if (SavingsGil != expectedSavings)
                errors.Add("Savings do not match the compared exact gear allocation.");
        }
        catch (OverflowException)
        {
            errors.Add("Quote gil arithmetic overflowed.");
        }

        if (Burden.CraftCount < 1 || Burden.SubcraftCount < 0 || Burden.DistinctMaterialCount != Plan.TerminalMaterials.Select(line => line.MaterialKey).Distinct(StringComparer.Ordinal).Count() || Burden.MarketSourceCount < 0 || Burden.VendorSourceCount < 0)
            errors.Add("Craft burden is incomplete or inconsistent.");
        if (Status == CraftAcquisitionQuoteStatus.Actionable && Diagnostics.Length != 0)
            errors.Add("An actionable quote cannot retain unresolved diagnostics.");

        var isSolverOffer = Status == CraftAcquisitionQuoteStatus.Actionable && errors.Count == 0;
        return new(errors.Count == 0, isSolverOffer, errors.Distinct(StringComparer.Ordinal).ToImmutableArray());
    }
}
