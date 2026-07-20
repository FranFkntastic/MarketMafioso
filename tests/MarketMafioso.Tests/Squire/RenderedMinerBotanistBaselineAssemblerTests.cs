using System;
using System.Collections.Generic;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Utility;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RenderedMinerBotanistBaselineAssemblerTests
{
    [Fact]
    public void Assemble_subtracts_rendered_base_and_materia_stats_from_character_total()
    {
        var result = RenderedMinerBotanistBaselineAssembler.Assemble(
            GathererStats(gathering: 5_400, perception: 5_600, gp: 960),
            CompleteEquipment(Item(
                stats: new Dictionary<string, int> { ["Gathering"] = 300, ["Perception"] = 400, ["GP"] = 20 },
                materia: new Dictionary<string, int> { ["Gathering"] = 100, ["Perception"] = 100, ["GP"] = 30 })),
            GathererAdvisorStatFamily.Instance);

        Assert.Equal(RenderedMinerBotanistBaselineStatus.Complete, result.Status);
        Assert.Equal(MinerBotanistUtilityProfile.MinerClassJobId, result.ClassJobId);
        Assert.Equal(new(5_000, 5_100, 910), result.FixedStats);
    }

    [Fact]
    public void Assemble_rejects_mixed_or_impossible_evidence()
    {
        var result = RenderedMinerBotanistBaselineAssembler.Assemble(
            GathererStats(gathering: 100, perception: 100, gp: 10),
            CompleteEquipment(Item(
                stats: new Dictionary<string, int> { ["Gathering"] = 101 },
                materia: new Dictionary<string, int>())),
            GathererAdvisorStatFamily.Instance);

        Assert.Equal(RenderedMinerBotanistBaselineStatus.Inconsistent, result.Status);
        Assert.Null(result.FixedStats);
    }

    [Fact]
    public void Assemble_requires_a_complete_twelve_slot_scan()
    {
        var incomplete = CompleteEquipment(Item(new Dictionary<string, int>(), new Dictionary<string, int>())) with
        {
            Status = RenderedEquipmentScanStatus.Failed,
        };

        Assert.Equal(
            RenderedMinerBotanistBaselineStatus.Incomplete,
            RenderedMinerBotanistBaselineAssembler.Assemble(GathererStats(5_000, 5_000, 900), incomplete, GathererAdvisorStatFamily.Instance).Status);
    }

    [Fact]
    public void Assemble_crafter_baseline_uses_crafting_labels_and_resolves_the_rendered_job()
    {
        var result = RenderedMinerBotanistBaselineAssembler.Assemble(
            CrafterStats(craftsmanship: 4_000, control: 3_900, cp: 600),
            CompleteEquipment(Item(
                stats: new Dictionary<string, int> { ["Craftsmanship"] = 250, ["Control"] = 200, ["CP"] = 10 },
                materia: new Dictionary<string, int> { ["Craftsmanship"] = 50, ["Control"] = 40 },
                jobLabel: "CRP BSM ARM GSM LTW WVR ALC CUL")),
            CrafterAdvisorStatFamily.Instance);

        Assert.Equal(RenderedMinerBotanistBaselineStatus.Complete, result.Status);
        Assert.Equal(CrafterUtilityProfile.BlacksmithClassJobId, result.ClassJobId);
        Assert.Equal(new(4_000, 3_900, 600), result.TotalStats);
        Assert.Equal(new(3_700, 3_660, 590), result.FixedStats);
    }

    [Fact]
    public void Assemble_crafter_baseline_ignores_gatherer_labels_on_crafter_gear()
    {
        var result = RenderedMinerBotanistBaselineAssembler.Assemble(
            CrafterStats(craftsmanship: 4_000, control: 3_900, cp: 600),
            CompleteEquipment(Item(
                stats: new Dictionary<string, int> { ["Craftsmanship"] = 100, ["Gathering"] = 999 },
                materia: new Dictionary<string, int>())),
            CrafterAdvisorStatFamily.Instance);

        Assert.Equal(RenderedMinerBotanistBaselineStatus.Complete, result.Status);
        Assert.Equal(new(3_900, 3_900, 600), result.FixedStats);
    }

    [Fact]
    public void Assemble_rejects_a_job_outside_every_landed_family()
    {
        var result = RenderedMinerBotanistBaselineAssembler.Assemble(
            new(RenderedCharacterObservationStatus.Complete, "Paladin", 100, new(3_000, 3_000, 10_000), "Complete"),
            CompleteEquipment(Item(new Dictionary<string, int>(), new Dictionary<string, int>())),
            GathererAdvisorStatFamily.Instance);

        Assert.Equal(RenderedMinerBotanistBaselineStatus.Incomplete, result.Status);
    }

    [Theory]
    [InlineData("Miner", 16u)]
    [InlineData("Botanist", 17u)]
    [InlineData("Carpenter", 8u)]
    [InlineData("Blacksmith", 9u)]
    [InlineData("Armorer", 10u)]
    [InlineData("Goldsmith", 11u)]
    [InlineData("Leatherworker", 12u)]
    [InlineData("Weaver", 13u)]
    [InlineData("Alchemist", 14u)]
    [InlineData("Culinarian", 15u)]
    public void AdvisorStatFamilies_resolves_every_supported_rendered_job(string jobName, uint expectedClassJobId)
    {
        Assert.Equal(expectedClassJobId, AdvisorStatFamilies.ClassJobIdForRenderedJob(jobName));
        var family = AdvisorStatFamilies.ResolveForRenderedJob(jobName);
        Assert.NotNull(family);
        Assert.Contains(expectedClassJobId, family!.SupportedClassJobIds);
    }

    [Theory]
    [InlineData("Paladin")]
    [InlineData("White Mage")]
    [InlineData(null)]
    public void AdvisorStatFamilies_abstains_on_unsupported_rendered_jobs(string? jobName)
    {
        Assert.Null(AdvisorStatFamilies.ClassJobIdForRenderedJob(jobName));
        Assert.Null(AdvisorStatFamilies.ResolveForRenderedJob(jobName));
    }

    private static RenderedAdvisorStatsObservation GathererStats(int gathering, int perception, int gp) =>
        new(RenderedCharacterObservationStatus.Complete, "Miner", 100, new(gathering, perception, gp), "Complete");

    private static RenderedAdvisorStatsObservation CrafterStats(int craftsmanship, int control, int cp) =>
        new(RenderedCharacterObservationStatus.Complete, "Blacksmith", 100, new(craftsmanship, control, cp), "Complete");

    private static RenderedEquipmentScanProgress CompleteEquipment(RenderedItemDetailObservation item)
    {
        var slots = new[]
        {
            EquipmentSlot.MainHand, EquipmentSlot.OffHand, EquipmentSlot.Head, EquipmentSlot.Body,
            EquipmentSlot.Hands, EquipmentSlot.Legs, EquipmentSlot.Feet, EquipmentSlot.Ears,
            EquipmentSlot.Neck, EquipmentSlot.Wrists, EquipmentSlot.Ring, EquipmentSlot.Ring,
        };
        var observations = new List<RenderedEquipmentSlotObservation>();
        for (var index = 0; index < slots.Length; index++)
            observations.Add(new(
                $"slot-{index}",
                slots[index],
                RenderedEquipmentSlotObservationStatus.Equipped,
                index == 0 ? item : item with
                {
                    Stats = new Dictionary<string, int>(),
                    MateriaStats = new Dictionary<string, int>(),
                }));
        return new(RenderedEquipmentScanStatus.Complete, 12, 12, null, observations, "Complete");
    }

    private static RenderedItemDetailObservation Item(
        IReadOnlyDictionary<string, int> stats,
        IReadOnlyDictionary<string, int> materia,
        string jobLabel = "MIN BTN") =>
        new(RenderedItemDetailStatus.Complete, "Observed item", RenderedItemQuality.High, 750, 100, jobLabel, null, stats, materia, "Complete");
}
