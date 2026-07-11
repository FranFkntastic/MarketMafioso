using MarketMafioso.Squire.Observation;

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
}
