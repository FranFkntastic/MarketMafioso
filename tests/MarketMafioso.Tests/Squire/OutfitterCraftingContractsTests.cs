using System.Collections.Immutable;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Crafting;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterCraftingContractsTests
{
    [Fact]
    public void ValidActionablePlanAndQuote_AreSolverEligible()
    {
        var plan = Plan();
        var quote = Quote(plan);

        Assert.True(plan.Validate(requireActionable: true).IsValid);
        var validation = quote.Validate();
        Assert.True(validation.IsValid);
        Assert.True(validation.IsSolverOffer);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("sub")]
    public void ActionablePlan_RejectsMasterRootOrSubcraft(string nodeId)
    {
        var plan = Plan();
        plan = plan with { ExpandedNodes = plan.ExpandedNodes.Select(node => node.NodeId == nodeId ? node with { RecipeUnlockItemId = 999 } : node).ToImmutableArray() };

        var validation = plan.Validate(requireActionable: true);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("non-master", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_RejectsCircularAmbiguousAndIncompleteMaterialTrees()
    {
        var baseline = Plan();
        var circular = baseline with
        {
            ExpandedNodes = baseline.ExpandedNodes.Select(node => node.NodeId == "root" ? node with { ParentNodeId = "sub" } : node).ToImmutableArray(),
        };
        var ambiguous = baseline with { ExpandedNodes = baseline.ExpandedNodes.Add(baseline.ExpandedNodes[1]) };
        var incomplete = baseline with { TerminalMaterials = baseline.TerminalMaterials[..1] };

        Assert.Contains(circular.Validate().Errors, error => error.Contains("circular", StringComparison.Ordinal));
        Assert.Contains(ambiguous.Validate().Errors, error => error.Contains("ambiguous", StringComparison.Ordinal));
        Assert.Contains(incomplete.Validate().Errors, error => error.Contains("completely cover", StringComparison.Ordinal));
    }

    [Fact]
    public void Quote_RejectsEvidenceGenerationMismatch()
    {
        var quote = Quote(Plan()) with { Evidence = Evidence(Guid.NewGuid()) };

        var validation = quote.Validate();

        Assert.False(validation.IsValid);
        Assert.False(validation.IsSolverOffer);
        Assert.Contains(validation.Errors, error => error.Contains("generations", StringComparison.Ordinal));
    }

    [Fact]
    public void Quote_RejectsCheckedGilOverflow()
    {
        var plan = Plan();
        var expensive = plan.TerminalMaterials[0] with
        {
            RequiredQuantity = uint.MaxValue,
            Source = plan.TerminalMaterials[0].Source with { AvailableQuantity = uint.MaxValue, UnitPriceGil = uint.MaxValue },
        };
        plan = plan with
        {
            ExpandedNodes = plan.ExpandedNodes.Select(node => node.NodeId == "ore" ? node with { Quantity = uint.MaxValue } : node).ToImmutableArray(),
            TerminalMaterials = plan.TerminalMaterials.SetItem(0, expensive),
        };
        var quote = Quote(plan) with { TotalGil = ulong.MaxValue };

        var validation = quote.Validate();

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("overflowed", StringComparison.Ordinal));
    }

    [Fact]
    public void StableIdentity_IsDeterministicAcrossCollectionOrderAndVolatileMetadata()
    {
        var first = Plan();
        var second = first with
        {
            PlanId = "another-runtime-id",
            BuiltAtUtc = first.BuiltAtUtc.AddHours(1),
            ExpandedNodes = first.ExpandedNodes.Reverse().ToImmutableArray(),
            TerminalMaterials = first.TerminalMaterials.Reverse().ToImmutableArray(),
        };

        Assert.Equal(first.ComputeStableIdentity(), second.ComputeStableIdentity());
    }

    [Theory]
    [InlineData(CraftAcquisitionQuoteStatus.DisplayOnly)]
    [InlineData(CraftAcquisitionQuoteStatus.Abstained)]
    public void NonActionableStatuses_CannotBecomeSolverOffers(CraftAcquisitionQuoteStatus status)
    {
        var quote = Quote(Plan()) with { Status = status };

        Assert.False(quote.Validate().IsSolverOffer);
    }

    private static OutfitterCraftPlan Plan()
    {
        var generation = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var eligible = new OutfitterCraftEligibility(true, 8, 100, 90);
        var nodes = ImmutableArray.Create(
            new OutfitterCraftNode("root", null, OutfitterCraftNodeKind.Craft, 100, EquipmentQuality.Normal, 1, 500, 0, eligible),
            new OutfitterCraftNode("sub", "root", OutfitterCraftNodeKind.Craft, 200, EquipmentQuality.Normal, 2, 501, 0, eligible),
            new OutfitterCraftNode("ore", "sub", OutfitterCraftNodeKind.Material, 300, EquipmentQuality.Normal, 4),
            new OutfitterCraftNode("cloth", "root", OutfitterCraftNodeKind.Material, 400, EquipmentQuality.Normal, 3));
        var materials = ImmutableArray.Create(
            Material(300, 4, 10, "listing-300", generation),
            Material(400, 3, 20, "vendor-400", generation, OutfitterMaterialSourceKind.GilVendor));
        return new(
            OutfitterCraftPlan.CurrentSchemaVersion,
            "runtime-plan",
            100,
            EquipmentQuality.Normal,
            1,
            "root",
            eligible,
            nodes,
            materials,
            generation,
            7,
            DateTimeOffset.Parse("2026-07-20T00:00:00Z"),
            ImmutableArray<OutfitterCraftDiagnostic>.Empty);
    }

    private static OutfitterTerminalMaterialLine Material(uint itemId, uint quantity, uint unitGil, string sourceId, Guid generation, OutfitterMaterialSourceKind kind = OutfitterMaterialSourceKind.MarketListing)
    {
        var key = OutfitterCraftPlan.MaterialKey(itemId, EquipmentQuality.Normal);
        return new(key, itemId, EquipmentQuality.Normal, quantity, new(kind, "test", sourceId, itemId, EquipmentQuality.Normal, quantity, unitGil, generation, 7));
    }

    private static CraftAcquisitionQuote Quote(OutfitterCraftPlan plan) => new(
        CraftAcquisitionQuote.CurrentSchemaVersion,
        "quote-1",
        CraftAcquisitionQuoteStatus.Actionable,
        plan,
        100,
        100,
        new("market:100:nq", 100, EquipmentQuality.Normal, 1, 175),
        75,
        Evidence(plan.MarketEvidenceGenerationId),
        new(2, 1, 2, 1, 1),
        DateTimeOffset.Parse("2026-07-20T00:01:00Z"),
        ImmutableArray<string>.Empty);

    private static CraftMarketEvidenceReference Evidence(Guid generation) => new(generation, 7, "marketmafioso-outfitter-market-evidence/v1", "test", "NA");
}
