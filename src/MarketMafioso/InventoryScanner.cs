using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Retainers;
using MarketMafioso.Automation.Inventory;
using MarketMafioso.Automation.Items;

namespace MarketMafioso;

public class InventoryScanner
{
    private readonly AutomationInventoryContainerScanner containerScanner;
    private readonly AutomationItemCatalog itemCatalog;
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
        InventoryType.RetainerCrystals,
    ];

    public InventoryScanner(IDataManager dataManager, IPluginLog log)
    {
        containerScanner = new AutomationInventoryContainerScanner(log);
        itemCatalog = new AutomationItemCatalog(dataManager, log);
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

    public IReadOnlyDictionary<uint, int> CountPlayerCrystals()
    {
        return InventoryPayloadMapper.MapInventoryBags(
                containerScanner.ScanLoadedContainers([InventoryType.Crystals]),
                includeItemNames: false,
                ResolveItemName,
                itemId => itemCatalog.Resolve(itemId))
            .SelectMany(bag => bag.Items)
            .Where(item => ElementalCurrencyCatalog.IsShardOrCrystal(item.ItemId))
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => checked((int)item.Quantity)));
    }

    public List<InventoryBag> ScanCurrentRetainer(Configuration config)
    {
        return InventoryPayloadMapper.MapRetainerInventoryBags(
            containerScanner.ScanLoadedContainers(RetainerContainers),
            config.IncludeItemNames,
            ResolveItemName,
            itemId => itemCatalog.Resolve(itemId));
    }

    public ulong ScanCurrentRetainerGil()
    {
        var quantities = ScanContainerRawQuantities(InventoryType.RetainerGil);
        return quantities.Aggregate(0UL, (sum, quantity) => sum + quantity);
    }

    public unsafe ulong? ScanPlayerGil()
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            log.Warning("[MarketMafioso] Player gil was not captured because InventoryManager.Instance() returned null");
            return null;
        }

        try
        {
            return inventoryManager->GetGil();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] Player gil could not be captured");
            return null;
        }
    }

    public List<RetainerMarketListing> ScanCurrentRetainerMarketListings(Configuration config)
    {
        return InventoryPayloadMapper.MapRetainerMarketListings(
            containerScanner.ScanLoadedContainers([InventoryType.RetainerMarket]),
            config.IncludeItemNames,
            ResolveItemName,
            DateTime.UtcNow,
            itemId => itemCatalog.Resolve(itemId));
    }

    public string? ResolveItemName(uint itemId)
    {
        return itemCatalog.ResolveItemName(itemId);
    }

    public AutomationItemMetadata ResolveItemMetadata(uint itemId)
    {
        return itemCatalog.Resolve(itemId);
    }

    private unsafe List<InventoryBag> ScanContainers(InventoryType[] types, Configuration config)
    {
        return InventoryPayloadMapper.MapInventoryBags(
            containerScanner.ScanLoadedContainers(types),
            config.IncludeItemNames,
            ResolveItemName,
            itemId => itemCatalog.Resolve(itemId));
    }

    private unsafe List<ulong> ScanContainerRawQuantities(InventoryType type)
    {
        var quantities = new List<ulong>();

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            log.Warning("[MarketMafioso] InventoryManager.Instance() returned null");
            return quantities;
        }

        try
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                return quantities;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null)
                    continue;

                var quantity = checked((ulong)slot->Quantity);
                if (quantity > 0)
                    quantities.Add(quantity);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[MarketMafioso] Error scanning container {type}");
        }

        return quantities;
    }
}
