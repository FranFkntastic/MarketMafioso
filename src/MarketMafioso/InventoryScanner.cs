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

public sealed record PlayerBagPurchaseState(
    bool IsComplete,
    IReadOnlyDictionary<uint, int> ItemCounts,
    int FreeSlots,
    IReadOnlyList<AutomationInventorySlot> OccupiedSlots,
    string Message);

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

    /// <summary>
    /// Counts only the four player bags that the native workshop contribution UI can consume.
    /// Reporting preferences may include other owned storage such as saddlebags, but that stock
    /// is not on hand at the fabrication station until it is moved into a player bag.
    /// </summary>
    public IReadOnlyDictionary<uint, int> CountWorkshopUsableInventory() =>
        CountWorkshopUsableInventory(containerScanner.ScanLoadedContainers(PlayerBags));

    internal static IReadOnlyDictionary<uint, int> CountWorkshopUsableInventory(
        IReadOnlyList<AutomationInventoryContainerSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var usableContainers = PlayerBags
            .Select(type => type.ToString())
            .ToHashSet(StringComparer.Ordinal);
        return snapshots
            .Where(snapshot => snapshot.IsLoaded && usableContainers.Contains(snapshot.ContainerName))
            .SelectMany(snapshot => snapshot.Slots)
            .GroupBy(slot => slot.ItemId)
            .ToDictionary(
                group => group.Key,
                group => checked(group.Sum(slot => slot.Quantity)));
    }

    public PlayerBagPurchaseState CapturePlayerBagPurchaseState()
    {
        var snapshots = containerScanner.ScanLoadedContainers(PlayerBags);
        var observed = snapshots.Select(snapshot => snapshot.ContainerName).ToHashSet(StringComparer.Ordinal);
        var missing = PlayerBags
            .Select(type => type.ToString())
            .Where(name => !observed.Contains(name))
            .ToArray();
        if (missing.Length > 0)
        {
            return new(
                false,
                new Dictionary<uint, int>(),
                0,
                [],
                $"Player inventory is still loading ({string.Join(", ", missing)} unavailable).");
        }

        var slots = snapshots.SelectMany(snapshot => snapshot.Slots).ToArray();
        var counts = slots
            .GroupBy(slot => slot.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(slot => slot.Quantity));
        var freeSlots = snapshots.Sum(snapshot => snapshot.SlotCount - snapshot.Slots.Count);
        return new(
            true,
            counts,
            freeSlots,
            slots,
            "Player inventory and stack capacity are ready.");
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
