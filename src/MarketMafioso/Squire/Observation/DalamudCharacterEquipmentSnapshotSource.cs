using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using Lumina.Excel.Sheets;

namespace MarketMafioso.Squire.Observation;

public sealed class DalamudCharacterEquipmentSnapshotSource : ICharacterEquipmentSnapshotSource
{
    private static readonly (InventoryType Type, EquipmentSlot Slot, bool Equipped)[] Containers =
    [
        (InventoryType.EquippedItems, EquipmentSlot.Unknown, true),
        (InventoryType.ArmoryMainHand, EquipmentSlot.MainHand, false),
        (InventoryType.ArmoryOffHand, EquipmentSlot.OffHand, false),
        (InventoryType.ArmoryHead, EquipmentSlot.Head, false),
        (InventoryType.ArmoryBody, EquipmentSlot.Body, false),
        (InventoryType.ArmoryHands, EquipmentSlot.Hands, false),
        (InventoryType.ArmoryLegs, EquipmentSlot.Legs, false),
        (InventoryType.ArmoryFeets, EquipmentSlot.Feet, false),
        (InventoryType.ArmoryEar, EquipmentSlot.Ears, false),
        (InventoryType.ArmoryNeck, EquipmentSlot.Neck, false),
        (InventoryType.ArmoryWrist, EquipmentSlot.Wrists, false),
        (InventoryType.ArmoryRings, EquipmentSlot.Ring, false),
        (InventoryType.ArmorySoulCrystal, EquipmentSlot.SoulCrystal, false),
        (InventoryType.Inventory1, EquipmentSlot.Unknown, false),
        (InventoryType.Inventory2, EquipmentSlot.Unknown, false),
        (InventoryType.Inventory3, EquipmentSlot.Unknown, false),
        (InventoryType.Inventory4, EquipmentSlot.Unknown, false),
    ];

    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    public DalamudCharacterEquipmentSnapshotSource(IPlayerState playerState, IDataManager dataManager, IPluginLog log)
    {
        this.playerState = playerState;
        this.dataManager = dataManager;
        this.log = log;
    }

    public CharacterEquipmentSnapshot Capture()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var diagnostics = new List<SnapshotComponentDiagnostic>();
        var identity = CaptureIdentity(capturedAt, diagnostics);
        if (identity.Scope is null)
            return Empty(identity, diagnostics);

        var instances = CaptureInventory(identity.Scope, capturedAt, diagnostics);
        var jobs = CaptureJobs(instances, diagnostics);
        var gearsets = CaptureGearsets(diagnostics);
        var definitions = CaptureDefinitions(instances, diagnostics);
        return new CharacterEquipmentSnapshot(Guid.NewGuid(), identity, jobs, gearsets, instances, definitions, new(diagnostics));
    }

    private CharacterIdentitySnapshot CaptureIdentity(DateTimeOffset capturedAt, List<SnapshotComponentDiagnostic> diagnostics)
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
        {
            diagnostics.Add(new("identity", SnapshotComponentStatus.Unavailable, "No active character is loaded."));
            return new(null, null, null, capturedAt, false, SnapshotComponentStatus.Unavailable, "No active character is loaded.");
        }

        var scope = new CharacterScope(playerState.ContentId, playerState.CharacterName.ToString(), playerState.HomeWorld.RowId);
        diagnostics.Add(new("identity", SnapshotComponentStatus.Complete));
        return new(scope, playerState.CurrentWorld.RowId, playerState.ClassJob.RowId, capturedAt, true, SnapshotComponentStatus.Complete);
    }

    private IReadOnlyList<CharacterJobSnapshot> CaptureJobs(
        IReadOnlyList<EquipmentInstanceSnapshot> instances,
        List<SnapshotComponentDiagnostic> diagnostics)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<ClassJob>();
            if (sheet is null)
                throw new InvalidOperationException("ClassJob sheet is unavailable.");
            var ownedItemIds = instances.Select(instance => instance.Fingerprint.ItemId).ToHashSet();
            var jobs = sheet
                .Where(job => job.RowId > 0 && !string.IsNullOrWhiteSpace(job.Abbreviation.ToString()))
                .Select(job =>
                {
                    var level = playerState.GetClassJobLevel(job);
                    var soulCrystalId = job.ItemSoulCrystal.RowId;
                    uint? parentClassJobId = job.ClassJobParent.RowId == 0 ? null : job.ClassJobParent.RowId;
                    var isUnlocked = IsJobUnlocked(level, job.RowId, parentClassJobId, soulCrystalId, ownedItemIds);
                    return new CharacterJobSnapshot(
                        job.RowId,
                        job.Abbreviation.ToString(),
                        job.Name.ToString(),
                        checked((uint)Math.Max(0, (int)level)),
                        isUnlocked,
                        parentClassJobId,
                        job.Role.ToString());
                })
                .ToArray();
            diagnostics.Add(new("jobs", SnapshotComponentStatus.Complete));
            return jobs;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Squire] Failed to capture job levels");
            diagnostics.Add(new("jobs", SnapshotComponentStatus.Unavailable, ex.Message));
            return [];
        }
    }

    internal static bool IsJobUnlocked(
        int level,
        uint classJobId,
        uint? parentClassJobId,
        uint soulCrystalId,
        IReadOnlySet<uint> ownedItemIds)
    {
        var isUpgradedJob = parentClassJobId is not null && parentClassJobId != classJobId;
        return isUpgradedJob
            ? soulCrystalId != 0 && ownedItemIds.Contains(soulCrystalId)
            : level > 0;
    }

    private static unsafe IReadOnlyList<GearsetSnapshot> CaptureGearsets(List<SnapshotComponentDiagnostic> diagnostics)
    {
        try
        {
            var ui = UIModule.Instance();
            var module = ui == null ? null : ui->GetRaptureGearsetModule();
            if (module == null)
                throw new InvalidOperationException("RaptureGearsetModule is unavailable.");

            var values = new List<GearsetSnapshot>();
            for (var index = 0; index < 100; index++)
            {
                var entry = module->GetGearset(index);
                if (entry == null || !entry->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                    continue;
                var items = new List<GearsetItemReference>();
                for (var itemIndex = 0; itemIndex < entry->Items.Length; itemIndex++)
                {
                    var item = entry->Items[itemIndex];
                    if (item.ItemId != 0)
                        items.Add(new(MapGearsetSlot((RaptureGearsetModule.GearsetItemIndex)itemIndex), NormalizeItemId(item.ItemId)));
                }
                values.Add(new(index, entry->NameString, entry->ClassJob, items, true));
            }
            diagnostics.Add(new("gearsets", SnapshotComponentStatus.Complete));
            return values;
        }
        catch (Exception ex)
        {
            diagnostics.Add(new("gearsets", SnapshotComponentStatus.Unavailable, ex.Message));
            return [];
        }
    }

    private static unsafe IReadOnlyList<EquipmentInstanceSnapshot> CaptureInventory(
        CharacterScope scope,
        DateTimeOffset capturedAt,
        List<SnapshotComponentDiagnostic> diagnostics)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            diagnostics.Add(new("inventory", SnapshotComponentStatus.Unavailable, "InventoryManager is unavailable."));
            return [];
        }

        var instances = new List<EquipmentInstanceSnapshot>();
        var statuses = new Dictionary<string, bool> { ["equipped"] = true, ["armoury"] = true, ["inventory"] = true };
        foreach (var (type, knownSlot, equipped) in Containers)
        {
            var container = manager->GetInventoryContainer(type);
            var component = equipped ? "equipped" : type.ToString().StartsWith("Armory", StringComparison.Ordinal) ? "armoury" : "inventory";
            if (container == null || !container->IsLoaded)
            {
                statuses[component] = false;
                continue;
            }
            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var item = container->GetInventorySlot(slotIndex);
                if (item == null || item->ItemId == 0)
                    continue;
                var materia = Enumerable.Range(0, 5).Select(index => (uint)item->GetMateriaId((byte)index)).Where(id => id != 0).ToArray();
                var slot = knownSlot;
                var fingerprint = new EquipmentInstanceFingerprint(
                    scope,
                    type.ToString(),
                    slotIndex,
                    NormalizeItemId(item->ItemId),
                    item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                    checked((uint)item->Quantity),
                    item->Condition,
                    item->SpiritbondOrCollectability,
                    item->CrafterContentId == 0 ? null : item->CrafterContentId,
                    materia,
                    item->GlamourId == 0 ? null : item->GlamourId,
                    [item->GetStain(0), item->GetStain(1)]);
                instances.Add(new(fingerprint, capturedAt, equipped));
            }
        }
        foreach (var status in statuses)
            diagnostics.Add(new(status.Key, status.Value ? SnapshotComponentStatus.Complete : SnapshotComponentStatus.Partial, status.Value ? null : "One or more required containers were not loaded."));
        return instances;
    }

    private IReadOnlyDictionary<uint, EquipmentItemDefinition> CaptureDefinitions(
        IReadOnlyList<EquipmentInstanceSnapshot> instances,
        List<SnapshotComponentDiagnostic> diagnostics)
    {
        try
        {
            var itemSheet = dataManager.GetExcelSheet<Item>() ?? throw new InvalidOperationException("Item sheet unavailable.");
            var jobSheet = dataManager.GetExcelSheet<ClassJob>() ?? throw new InvalidOperationException("ClassJob sheet unavailable.");
            var cabinetSheet = dataManager.GetExcelSheet<Cabinet>() ?? throw new InvalidOperationException("Cabinet sheet unavailable.");
            var jobs = jobSheet.Where(job => job.RowId > 0).ToArray();
            var cabinetItemIds = cabinetSheet.Select(entry => entry.Item.RowId).Where(id => id != 0).ToHashSet();
            var values = new Dictionary<uint, EquipmentItemDefinition>();
            foreach (var id in instances.Select(instance => instance.Fingerprint.ItemId).Distinct())
            {
                var item = itemSheet.GetRowOrDefault(id);
                if (item is null)
                    continue;
                var value = item.Value;
                var slot = MapEquipSlot(value.EquipSlotCategory.RowId);
                var category = value.ClassJobCategory.Value;
                var eligible = jobs.Where(job => IsEligible(category, job.Abbreviation.ToString())).Select(job => job.RowId).ToHashSet();
                values[id] = new(
                    id,
                    value.Name.ToString(),
                    value.LevelEquip,
                    value.LevelItem.RowId,
                    slot,
                    eligible,
                    value.Rarity,
                    slot != EquipmentSlot.Unknown,
                    slot == EquipmentSlot.SoulCrystal,
                    value.Desynth > 0,
                    value.PriceLow > 0 && !value.IsIndisposable,
                    value.PriceLow,
                    !value.IsIndisposable,
                    cabinetItemIds.Contains(id),
                    false,
                    value.IsUnique && value.IsUntradable && value.Rarity >= 4);
            }
            var complete = values.Count == instances.Select(instance => instance.Fingerprint.ItemId).Distinct().Count();
            diagnostics.Add(new("definitions", complete ? SnapshotComponentStatus.Complete : SnapshotComponentStatus.Partial, complete ? null : "One or more item definitions were unavailable."));
            return values;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Squire] Failed to resolve item definitions");
            diagnostics.Add(new("definitions", SnapshotComponentStatus.Unavailable, ex.Message));
            return new Dictionary<uint, EquipmentItemDefinition>();
        }
    }

    private static bool IsEligible(ClassJobCategory category, string abbreviation)
    {
        var property = typeof(ClassJobCategory).GetProperty(abbreviation);
        return property?.PropertyType == typeof(bool) && property.GetValue(category) is true;
    }

    private static uint NormalizeItemId(uint itemId) => itemId >= 1_000_000 ? itemId % 1_000_000 : itemId;

    internal static EquipmentSlot MapEquipSlot(uint rowId) => rowId switch
    {
        1 or 13 or 14 => EquipmentSlot.MainHand,
        2 => EquipmentSlot.OffHand,
        3 => EquipmentSlot.Head,
        4 => EquipmentSlot.Body,
        5 => EquipmentSlot.Hands,
        7 => EquipmentSlot.Legs,
        8 => EquipmentSlot.Feet,
        9 => EquipmentSlot.Ears,
        10 => EquipmentSlot.Neck,
        11 => EquipmentSlot.Wrists,
        12 => EquipmentSlot.Ring,
        17 => EquipmentSlot.SoulCrystal,
        _ => EquipmentSlot.Unknown,
    };

    private static EquipmentSlot MapGearsetSlot(RaptureGearsetModule.GearsetItemIndex slot) => slot switch
    {
        RaptureGearsetModule.GearsetItemIndex.MainHand => EquipmentSlot.MainHand,
        RaptureGearsetModule.GearsetItemIndex.OffHand => EquipmentSlot.OffHand,
        RaptureGearsetModule.GearsetItemIndex.Head => EquipmentSlot.Head,
        RaptureGearsetModule.GearsetItemIndex.Body => EquipmentSlot.Body,
        RaptureGearsetModule.GearsetItemIndex.Hands => EquipmentSlot.Hands,
        RaptureGearsetModule.GearsetItemIndex.Legs => EquipmentSlot.Legs,
        RaptureGearsetModule.GearsetItemIndex.Feet => EquipmentSlot.Feet,
        RaptureGearsetModule.GearsetItemIndex.Ears => EquipmentSlot.Ears,
        RaptureGearsetModule.GearsetItemIndex.Neck => EquipmentSlot.Neck,
        RaptureGearsetModule.GearsetItemIndex.Wrists => EquipmentSlot.Wrists,
        RaptureGearsetModule.GearsetItemIndex.RingLeft or RaptureGearsetModule.GearsetItemIndex.RingRight => EquipmentSlot.Ring,
        RaptureGearsetModule.GearsetItemIndex.SoulStone => EquipmentSlot.SoulCrystal,
        _ => EquipmentSlot.Unknown,
    };

    private static CharacterEquipmentSnapshot Empty(CharacterIdentitySnapshot identity, List<SnapshotComponentDiagnostic> diagnostics)
    {
        foreach (var component in new[] { "jobs", "gearsets", "equipped", "armoury", "inventory", "definitions" })
            diagnostics.Add(new(component, SnapshotComponentStatus.Unavailable, "Character identity is unavailable."));
        return new(Guid.NewGuid(), identity, [], [], [], new Dictionary<uint, EquipmentItemDefinition>(), new(diagnostics));
    }
}
