using System;
using System.Collections.Generic;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class RenderedMinerBotanistBaselineAssemblerTests
{
    [Fact]
    public void Assemble_subtracts_rendered_base_and_materia_stats_from_character_total()
    {
        var result = RenderedMinerBotanistBaselineAssembler.Assemble(
            Stats(gathering: 5_400, perception: 5_600, gp: 960),
            CompleteEquipment(Item(
                stats: new Dictionary<string, int> { ["Gathering"] = 300, ["Perception"] = 400, ["GP"] = 20 },
                materia: new Dictionary<string, int> { ["Gathering"] = 100, ["Perception"] = 100, ["GP"] = 30 })));

        Assert.Equal(RenderedMinerBotanistBaselineStatus.Complete, result.Status);
        Assert.Equal(new(5_000, 5_100, 910), result.FixedStats);
    }

    [Fact]
    public void Assemble_rejects_mixed_or_impossible_evidence()
    {
        var result = RenderedMinerBotanistBaselineAssembler.Assemble(
            Stats(gathering: 100, perception: 100, gp: 10),
            CompleteEquipment(Item(
                stats: new Dictionary<string, int> { ["Gathering"] = 101 },
                materia: new Dictionary<string, int>())));

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
            RenderedMinerBotanistBaselineAssembler.Assemble(Stats(5_000, 5_000, 900), incomplete).Status);
    }

    private static RenderedGatheringStatsObservation Stats(int gathering, int perception, int gp) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, RenderedCharacterObservationStatus.Complete, "Miner", 100, gathering, perception, gp, [], "Complete");

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
        IReadOnlyDictionary<string, int> materia) =>
        new(RenderedItemDetailStatus.Complete, "Observed item", RenderedItemQuality.High, 750, 100, "MIN BTN", null, stats, materia, "Complete");
}
