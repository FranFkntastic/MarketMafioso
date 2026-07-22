using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Franthropy.FFXIV.Filtering;
using Lumina.Excel.Sheets;

namespace MarketMafioso.Automation.Items;

public sealed class AutomationItemCatalog
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    public AutomationItemCatalog(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public AutomationItemMetadata Resolve(uint itemId, bool isHighQuality = false)
    {
        try
        {
            var item = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
            var name = item?.Name.ToString();
            var luminaStackSize = item?.StackSize ?? 0;
            var itemType = item is { } resolvedItem ? ResolveItemType(resolvedItem) : null;
            var supportsCondition = item is { } conditionItem &&
                                    conditionItem.StackSize == 1 &&
                                    conditionItem.EquipSlotCategory.RowId != 0;

            var isEquipment = item is { } equipmentItem && equipmentItem.EquipSlotCategory.RowId != 0;
            return new AutomationItemMetadata(
                new AutomationItemIdentity(itemId, name, isHighQuality),
                ItemStackRules.ResolveMaxStack(itemId, luminaStackSize),
                itemType,
                supportsCondition,
                item is { } definition ? definition.LevelItem.RowId : null,
                isEquipment ? item!.Value.LevelEquip : null,
                isEquipment ? ResolveEligibleJobs(item!.Value) : null,
                isEquipment ? ResolveSlots(item!.Value.EquipSlotCategory.RowId) : null,
                item is { } rarityItem ? ResolveRarity(rarityItem.Rarity) : null,
                item is { ItemUICategory.RowId: > 0 } categoryItem ? new FfxivUiCategoryKey(categoryItem.ItemUICategory.RowId) : null,
                itemType,
                item?.IsUnique,
                item is { } tradableItem ? !tradableItem.IsUntradable : null,
                item is { } desynthItem ? desynthItem.Desynth > 0 : null,
                item?.CanBeHq,
                item is { StackSize: > 0 } stackItem ? (long)stackItem.StackSize : null);
        }
        catch (Exception ex)
        {
            log.Verbose(ex, $"[MarketMafioso] Could not resolve metadata for item {itemId}");
            return new AutomationItemMetadata(
                new AutomationItemIdentity(itemId, null, isHighQuality),
                ItemStackRules.ResolveMaxStack(itemId, 0));
        }
    }

    private string? ResolveItemType(Item item)
    {
        if (item.ItemUICategory.RowId == 0)
            return null;

        try
        {
            var itemType = item.ItemUICategory.Value.Name.ToString();
            return string.IsNullOrWhiteSpace(itemType) ? null : itemType;
        }
        catch (Exception ex)
        {
            log.Verbose(ex, $"[MarketMafioso] Could not resolve UI category for item {item.RowId}");
            return null;
        }
    }

    public string? ResolveItemName(uint itemId) => Resolve(itemId).Identity.Name;

    private IReadOnlyList<AutomationItemJob> ResolveEligibleJobs(Item item)
    {
        if (item.ClassJobCategory.RowId == 0)
            return [];

        var category = item.ClassJobCategory.Value;
        return dataManager.GetExcelSheet<ClassJob>()?
            .Where(job => job.RowId > 0 && !string.IsNullOrWhiteSpace(job.Abbreviation.ToString()))
            .Where(job => IsEligible(category, job.Abbreviation.ToString()))
            .Select(job => new AutomationItemJob(
                new FfxivJobKey(job.RowId),
                job.Name.ToString(),
                job.Abbreviation.ToString()))
            .ToArray() ?? [];
    }

    private static bool IsEligible(ClassJobCategory category, string abbreviation)
    {
        var property = typeof(ClassJobCategory).GetProperty(abbreviation);
        return property?.PropertyType == typeof(bool) && property.GetValue(category) is true;
    }

    private static IReadOnlyCollection<FfxivEquipmentSlot> ResolveSlots(uint equipSlotCategoryId)
    {
        var slot = equipSlotCategoryId switch
        {
            1 or 13 or 14 => FfxivEquipmentSlot.MainHand,
            2 => FfxivEquipmentSlot.OffHand,
            3 => FfxivEquipmentSlot.Head,
            4 => FfxivEquipmentSlot.Body,
            5 => FfxivEquipmentSlot.Hands,
            7 => FfxivEquipmentSlot.Legs,
            8 => FfxivEquipmentSlot.Feet,
            9 => FfxivEquipmentSlot.Ears,
            10 => FfxivEquipmentSlot.Neck,
            11 => FfxivEquipmentSlot.Wrists,
            12 => FfxivEquipmentSlot.Ring,
            17 => FfxivEquipmentSlot.SoulCrystal,
            _ => (FfxivEquipmentSlot?)null,
        };
        return slot is { } value ? [value] : [];
    }

    private static FfxivItemRarity? ResolveRarity(byte rarity) => rarity switch
    {
        1 => FfxivItemRarity.Common,
        2 => FfxivItemRarity.Uncommon,
        3 => FfxivItemRarity.Rare,
        4 => FfxivItemRarity.Relic,
        _ => null,
    };
}
