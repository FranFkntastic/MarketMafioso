using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Retainers;
using MarketMafioso.Automation.Inventory;
using MarketMafioso.Automation.Items;

namespace MarketMafioso;

public sealed record PlayerInventoryCaptureResult(
    List<InventoryBag> Bags,
    IReadOnlyList<string> RequestedSources,
    IReadOnlyList<string> ObservedSources);

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

    public InventoryScanner(IDataManager dataManager, IPluginLog log)
    {
        containerScanner = new AutomationInventoryContainerScanner(log);
        itemCatalog = new AutomationItemCatalog(dataManager, log);
        this.log = log;
    }

    public List<InventoryBag> ScanPlayerInventory(Configuration config) => CapturePlayerInventory(config).Bags;

    public PlayerInventoryCaptureResult CapturePlayerInventory(Configuration config)
    {
        var requested = PlayerBags
            .Concat(config.IncludeEquipped ? [InventoryType.EquippedItems] : [])
            .Concat(config.IncludeArmoury ? ArmouryContainers : [])
            .Concat(config.IncludeCrystals ? [InventoryType.Crystals] : [])
            .Concat(config.IncludeSaddlebag
                ? [InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2]
                : [])
            .ToArray();
        var snapshots = containerScanner.ScanLoadedContainers(requested);
        var bags = InventoryPayloadMapper.MapInventoryBags(
            snapshots,
            config.IncludeItemNames,
            ResolveItemName,
            itemId => itemCatalog.Resolve(itemId));
        var observed = snapshots.Where(snapshot => snapshot.IsLoaded).Select(snapshot => snapshot.ContainerName).ToArray();
        return new(bags, requested.Select(source => source.ToString()).ToArray(), observed);
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

    public string? ResolveItemName(uint itemId)
    {
        return itemCatalog.ResolveItemName(itemId);
    }

    public AutomationItemMetadata ResolveItemMetadata(uint itemId)
    {
        return itemCatalog.Resolve(itemId);
    }

}
