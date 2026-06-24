using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace MarketMafioso;

public class InventoryScanner
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private static readonly InventoryType[] ArmouryContainers =
    [
        InventoryType.ArmoryBody,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryRings,
        InventoryType.ArmoryWrist,
        InventoryType.ArmorySoulCrystal,
    ];

    private static readonly InventoryType[] RetainerContainers =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
        InventoryType.RetainerGil,
        InventoryType.RetainerMarket,
    ];

    public InventoryScanner(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public List<InventoryBag> ScanPlayerInventory(Configuration config)
    {
        var bags = new List<InventoryBag>();

        bags.AddRange(ScanContainers(PlayerBags, config));

        if (config.IncludeEquipped)
            bags.AddRange(ScanContainers([InventoryType.EquippedItems], config));

        if (config.IncludeArmoury)
            bags.AddRange(ScanContainers(ArmouryContainers, config));

        if (config.IncludeCrystals)
            bags.AddRange(ScanContainers([InventoryType.Crystals], config));

        if (config.IncludeSaddlebag)
            bags.AddRange(ScanContainers(
            [
                InventoryType.SaddleBag1,
                InventoryType.SaddleBag2,
                InventoryType.PremiumSaddleBag1,
                InventoryType.PremiumSaddleBag2,
            ], config));

        return bags;
    }

    public IReadOnlyDictionary<uint, int> CountPlayerInventory(Configuration config)
    {
        return ScanPlayerInventory(config)
            .SelectMany(bag => bag.Items)
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => (int)item.Quantity));
    }

    public List<InventoryBag> ScanCurrentRetainer(Configuration config)
    {
        var bags = ScanContainers(RetainerContainers, config);
        var mergedBags = new List<InventoryBag>();
        var retainerPagesItems = new Dictionary<uint, ItemSlot>();

        foreach (var bag in bags)
        {
            if (bag.BagName.StartsWith("RetainerPage"))
            {
                foreach (var item in bag.Items)
                {
                    if (retainerPagesItems.TryGetValue(item.ItemId, out var existing))
                    {
                        retainerPagesItems[item.ItemId] = existing with
                        {
                            Quantity = existing.Quantity + item.Quantity,
                            Condition = Math.Max(existing.Condition, item.Condition)
                        };
                    }
                    else
                    {
                        retainerPagesItems[item.ItemId] = item;
                    }
                }
            }
            else
            {
                mergedBags.Add(bag);
            }
        }

        if (retainerPagesItems.Count > 0)
        {
            mergedBags.Insert(0, new InventoryBag
            {
                BagName = "RetainerInventory",
                Items = retainerPagesItems.Values.ToList()
            });
        }

        return mergedBags;
    }

    public string? ResolveItemName(uint itemId)
    {
        try
        {
            return dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId)?.Name.ToString();
        }
        catch (Exception ex)
        {
            log.Verbose(ex, $"[MarketMafioso] Could not resolve name for item {itemId}");
            return null;
        }
    }

    public string? ResolveItemType(uint itemId)
    {
        try
        {
            var item = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
            return item?.ItemUICategory.Value.Name.ToString();
        }
        catch (Exception ex)
        {
            log.Verbose(ex, $"[MarketMafioso] Could not resolve type for item {itemId}");
            return null;
        }
    }

    private unsafe List<InventoryBag> ScanContainers(InventoryType[] types, Configuration config)
    {
        var bags = new List<InventoryBag>();

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            log.Warning("[MarketMafioso] InventoryManager.Instance() returned null");
            return bags;
        }

        foreach (var type in types)
        {
            try
            {
                var container = inventoryManager->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;

                var itemGroups = new Dictionary<uint, ItemSlot>();

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        continue;

                    var itemId = slot->ItemId;
                    var quantity = (uint)slot->Quantity;
                    var condition = slot->Condition / 30000f;

                    if (itemGroups.TryGetValue(itemId, out var existing))
                    {
                        itemGroups[itemId] = existing with
                        {
                            Quantity = existing.Quantity + quantity,
                            Condition = Math.Max(existing.Condition, condition)
                        };
                    }
                    else
                    {
                        string? itemName = config.IncludeItemNames ? ResolveItemName(itemId) : null;
                        string? itemType = config.IncludeItemNames ? ResolveItemType(itemId) : null;
                        itemGroups[itemId] = new ItemSlot
                        {
                            ItemId = itemId,
                            ItemName = itemName,
                            ItemType = itemType,
                            Quantity = quantity,
                            IsHQ = false,
                            Condition = condition,
                        };
                    }
                }

                var items = new List<ItemSlot>(itemGroups.Values);

                if (items.Count > 0)
                {
                    bags.Add(new InventoryBag
                    {
                        BagName = type.ToString(),
                        Items = items,
                    });
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, $"[MarketMafioso] Error scanning container {type}");
            }
        }

        return bags;
    }
}
