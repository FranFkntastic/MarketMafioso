using System;
using System.Collections.Generic;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RenderedEquipmentDefinitionResolverTests
{
    [Fact]
    public void Resolve_uses_rendered_name_quality_levels_slot_and_job_together()
    {
        var expected = Definition(100, highQuality: true);
        var wrongLevel = Definition(101, highQuality: true) with { ItemLevel = 749 };
        var result = RenderedEquipmentDefinitionResolver.Resolve(
            [Observation("Ceremonial Pickaxe", RenderedItemQuality.High)],
            classJobId: 16,
            _ => [wrongLevel, expected]);

        Assert.Equal(RenderedEquipmentResolutionStatus.Complete, result.Status);
        var slot = Assert.Single(result.Slots);
        Assert.Equal((uint)100, slot.Definition.ItemId);
        Assert.Equal(EquipmentQuality.High, slot.Quality);
        Assert.Equal("rendered-current:main-hand", slot.BaselineOffer.Key.SourceCatalogKey);
    }

    [Fact]
    public void Resolve_rejects_hq_when_static_hq_stats_are_unavailable()
    {
        var result = RenderedEquipmentDefinitionResolver.Resolve(
            [Observation("Ceremonial Pickaxe", RenderedItemQuality.High)],
            16,
            _ => [Definition(100, highQuality: false)]);

        Assert.Equal(RenderedEquipmentResolutionStatus.Unresolved, result.Status);
        Assert.Empty(result.Slots);
    }

    [Fact]
    public void Resolve_abstains_on_duplicate_contextual_matches_instead_of_using_ids()
    {
        var result = RenderedEquipmentDefinitionResolver.Resolve(
            [Observation("Ceremonial Pickaxe", RenderedItemQuality.Normal)],
            16,
            _ => [Definition(100, true), Definition(101, true)]);

        Assert.Equal(RenderedEquipmentResolutionStatus.Ambiguous, result.Status);
        Assert.Contains("raw IDs will not be used to guess", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_rejects_static_profile_that_disagrees_with_rendered_stats()
    {
        var result = RenderedEquipmentDefinitionResolver.Resolve(
            [Observation("Ceremonial Pickaxe", RenderedItemQuality.Normal, gathering: 99)],
            16,
            _ => [Definition(100, highQuality: true)]);

        Assert.Equal(RenderedEquipmentResolutionStatus.Unresolved, result.Status);
        Assert.Empty(result.Slots);
    }

    private static RenderedEquipmentSlotObservation Observation(
        string name,
        RenderedItemQuality quality,
        int gathering = 100) =>
        new("main-hand", EquipmentSlot.MainHand, RenderedEquipmentSlotObservationStatus.Equipped,
            new(RenderedItemDetailStatus.Complete, name, quality, 750, 100, "MIN",
                new Dictionary<string, int> { ["Gathering"] = gathering }, new Dictionary<string, int>(), "Complete"));

    private static EquipmentItemDefinition Definition(uint itemId, bool highQuality)
    {
        var profile = new EquipmentStatProfile(
            [new(72, EquipmentStatSemantic.Gathering, 100, false, "Gathering")],
            0, 0, 0, 0, true);
        return new(
            itemId,
            "Ceremonial Pickaxe",
            100,
            750,
            EquipmentSlot.MainHand,
            new HashSet<uint> { 16, 17 },
            1,
            true,
            false,
            true,
            true,
            1,
            true,
            null,
            null,
            false,
            StatProfile: profile,
            HighQualityStatProfile: highQuality ? profile : null);
    }
}
