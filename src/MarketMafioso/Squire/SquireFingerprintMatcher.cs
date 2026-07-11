using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public static class SquireFingerprintMatcher
{
    public static bool ExactMatch(EquipmentInstanceFingerprint expected, EquipmentInstanceFingerprint observed) =>
        expected.Character == observed.Character &&
        expected.Container == observed.Container &&
        expected.SlotIndex == observed.SlotIndex &&
        expected.ItemId == observed.ItemId &&
        expected.IsHighQuality == observed.IsHighQuality &&
        expected.Quantity == observed.Quantity &&
        expected.Condition == observed.Condition &&
        expected.Spiritbond == observed.Spiritbond &&
        expected.CrafterContentId == observed.CrafterContentId &&
        expected.MateriaIds.SequenceEqual(observed.MateriaIds) &&
        expected.GlamourId == observed.GlamourId &&
        expected.Stains.SequenceEqual(observed.Stains);
}

