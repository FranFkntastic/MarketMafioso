using MarketMafioso.Squire.Observation;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireJobUnlockTests
{
    [Theory]
    [InlineData(1, EquipmentRarity.Normal)]
    [InlineData(2, EquipmentRarity.Uncommon)]
    [InlineData(3, EquipmentRarity.Rare)]
    [InlineData(4, EquipmentRarity.Relic)]
    [InlineData(5, EquipmentRarity.Unknown)]
    public void RarityMapping_IsExplicit(byte raw, EquipmentRarity expected) =>
        Assert.Equal(expected, DalamudCharacterEquipmentSnapshotSource.MapRarity(raw));

    [Theory]
    [InlineData("Strength", EquipmentStatSemantic.Strength)]
    [InlineData("Critical Hit", EquipmentStatSemantic.CriticalHit)]
    [InlineData("Craftsmanship", EquipmentStatSemantic.Craftsmanship)]
    [InlineData("Gathering Points", EquipmentStatSemantic.GatheringPoints)]
    [InlineData("Future Stat", EquipmentStatSemantic.Unknown)]
    public void BaseParameterMapping_FailsClosedForUnknownNames(string name, EquipmentStatSemantic expected) =>
        Assert.Equal(expected, DalamudCharacterEquipmentSnapshotSource.MapStatSemantic(999, name));
    [Fact]
    public void UpgradedJobRequiresItsSoulCrystalEvenWhenClassLevelIsShared()
    {
        Assert.False(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(50, 21, 3, 4549, new HashSet<uint>()));
        Assert.True(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(50, 21, 3, 4549, new HashSet<uint> { 4549 }));
    }

    [Fact]
    public void BaseClassUsesObservedLevel()
    {
        Assert.False(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(0, 3, 3, 0, new HashSet<uint>()));
        Assert.True(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(1, 3, 3, 0, new HashSet<uint>()));
    }

    [Fact]
    public void CraftingClassUsesLevelEvenWhenSheetHasSpecialistCrystal()
    {
        Assert.True(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(49, 11, 11, 9999, new HashSet<uint>()));
    }

    [Theory]
    [InlineData(2, EquipmentSlot.OffHand)]
    [InlineData(3, EquipmentSlot.Head)]
    [InlineData(4, EquipmentSlot.Body)]
    [InlineData(5, EquipmentSlot.Hands)]
    [InlineData(6, EquipmentSlot.Unknown)]
    [InlineData(7, EquipmentSlot.Legs)]
    public void EquipSlotCategoryMapping_MatchesGameSheet(uint rowId, EquipmentSlot expected)
    {
        Assert.Equal(expected, DalamudCharacterEquipmentSnapshotSource.MapEquipSlot(rowId));
    }
}
