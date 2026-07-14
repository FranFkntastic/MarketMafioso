using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterTargetCatalogTests
{
    [Fact]
    public void Build_KeepsJobsGearsetsAndIncompleteRetainersVisible()
    {
        var job = new CharacterJobSnapshot(
            1,
            "GLA",
            "gladiator",
            15,
            true,
            null,
            "Tank",
            EquipmentStatSemantic.Strength,
            EquipmentDiscipline.Combat);
        var snapshot = new CharacterEquipmentSnapshot(
            Guid.NewGuid(),
            new(null, null, null, DateTimeOffset.UtcNow, true, SnapshotComponentStatus.Complete),
            [job],
            [new(2, "Dungeon", 1, [], true)],
            [],
            new Dictionary<uint, EquipmentItemDefinition>(),
            new([new("jobs", SnapshotComponentStatus.Complete)]));
        var retainers = new Dictionary<ulong, CachedRetainer>
        {
            [9] = new()
            {
                RetainerId = 9,
                RetainerName = "Alice",
                LastUpdated = DateTime.UtcNow.AddHours(-3),
            },
        };

        var targets = new OutfitterTargetCatalog().Build(snapshot, retainers);

        Assert.Collection(
            targets,
            target =>
            {
                Assert.Equal(OutfitterTargetKind.Job, target.Kind);
                Assert.Equal("Gladiator", target.Name);
                Assert.True(target.IsReady);
            },
            target =>
            {
                Assert.Equal(OutfitterTargetKind.Gearset, target.Kind);
                Assert.Equal("Dungeon", target.Name);
            },
            target =>
            {
                Assert.Equal(OutfitterTargetKind.Retainer, target.Kind);
                Assert.Equal("Alice", target.Name);
                Assert.False(target.IsReady);
                Assert.Contains("worn equipment slots", target.Diagnostic);
            });
    }
}
