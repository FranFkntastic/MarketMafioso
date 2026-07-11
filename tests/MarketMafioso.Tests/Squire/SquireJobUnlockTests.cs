using MarketMafioso.Squire.Observation;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireJobUnlockTests
{
    [Fact]
    public void UpgradedJobRequiresItsSoulCrystalEvenWhenClassLevelIsShared()
    {
        Assert.False(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(50, 4549, new HashSet<uint>()));
        Assert.True(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(50, 4549, new HashSet<uint> { 4549 }));
    }

    [Fact]
    public void BaseClassUsesObservedLevel()
    {
        Assert.False(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(0, 0, new HashSet<uint>()));
        Assert.True(DalamudCharacterEquipmentSnapshotSource.IsJobUnlocked(1, 0, new HashSet<uint>()));
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
