using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace MarketMafioso.TradeQueue;

public interface ITradeQueueIo
{
    IReadOnlyList<TradeQueueInventoryStack> ScanTradeableInventory();
    bool TryGetFocusPartner(out TradeQueuePartner partner);
    bool FocusPartnerMatches(TradeQueuePartner partner);
    bool IsTradeOpen { get; }
    bool IsNumericInputOpen { get; }
    int OfferedSlotCount { get; }
    bool TryOpenTrade(TradeQueuePartner partner);
    bool TryOpenGilInput(out string error);
    bool TryOfferItem(TradeQueueBatchLine line, out string error);
    bool TrySubmitQuantity(int quantity, out string error);
    bool TryClickReady(out string error);
    bool TryConfirmTrade(out string error);
}

public sealed class DalamudTradeQueueIo : ITradeQueueIo
{
    private const string TradeAddon = "Trade";
    private const string NumericInputAddon = "InputNumeric";
    private const string SelectYesNoAddon = "SelectYesno";
    private const uint TradeInventoryContainerId = 2005;
    private const uint TradeConfirmationAddonRowId = 102223;
    private const string OfferItemTradeSignature =
        "48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 83 B9 ?? ?? ?? ?? ?? 41 8B F0";

    private static readonly InventoryType[] SupportedInventories =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.Crystals,
    ];

    private readonly IGameGui gameGui;
    private readonly ITargetManager targetManager;
    private readonly ICondition condition;
    private readonly ISigScanner sigScanner;
    private readonly IPluginLog log;
    private readonly HashSet<uint> tradeableItems;
    private readonly IReadOnlyDictionary<uint, string> itemNames;
    private readonly string tradeConfirmationText;
    private OfferItemTradeDelegate? offerItemTrade;

    public DalamudTradeQueueIo(
        IGameGui gameGui,
        ITargetManager targetManager,
        ICondition condition,
        ISigScanner sigScanner,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.gameGui = gameGui;
        this.targetManager = targetManager;
        this.condition = condition;
        this.sigScanner = sigScanner;
        this.log = log;
        var items = dataManager.GetExcelSheet<Item>()
            .Where(item => item.RowId > 0 && !item.IsUntradable)
            .ToList();
        tradeableItems = items.Select(item => item.RowId).ToHashSet();
        itemNames = items.ToDictionary(item => item.RowId, item => item.Name.ToString());
        tradeConfirmationText = dataManager.GetExcelSheet<Addon>()
            .GetRow(TradeConfirmationAddonRowId)
            .Text.ToString();
    }

    public bool IsTradeOpen => condition[ConditionFlag.TradeOpen];

    public unsafe bool IsNumericInputOpen
    {
        get
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(NumericInputAddon, 1);
            return IsReady(addon);
        }
    }

    public unsafe int OfferedSlotCount
    {
        get
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager == null)
                return 0;

            var container = inventoryManager->GetInventoryContainer((InventoryType)TradeInventoryContainerId);
            if (container == null || !container->IsLoaded)
                return 0;

            var count = 0;
            for (var slot = 0; slot < Math.Min(TradeQueuePlanner.MaximumTradeSlots, container->Size); slot++)
            {
                if (container->GetInventorySlot(slot)->ItemId != 0)
                    count++;
            }
            return count;
        }
    }

    public unsafe IReadOnlyList<TradeQueueInventoryStack> ScanTradeableInventory()
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return [];

        var stacks = new List<TradeQueueInventoryStack>();
        var gil = checked((int)inventoryManager->GetInventoryItemCount(
            TradeQueuePlanner.GilItemId,
            false,
            true,
            true,
            (short)0));
        if (gil > 0)
        {
            stacks.Add(new(
                uint.MaxValue,
                -1,
                TradeQueuePlanner.GilItemId,
                "Gil",
                false,
                gil));
        }

        foreach (var inventoryType in SupportedInventories)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null ||
                    slot->ItemId == 0 ||
                    slot->Quantity <= 0 ||
                    !tradeableItems.Contains(slot->ItemId) ||
                    slot->SpiritbondOrCollectability != 0 ||
                    slot->GlamourId != 0)
                {
                    continue;
                }

                var name = itemNames.GetValueOrDefault(slot->ItemId, $"Item {slot->ItemId}");
                stacks.Add(new(
                    (uint)inventoryType,
                    slotIndex,
                    slot->ItemId,
                    name,
                    slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                    checked((int)slot->Quantity)));
            }
        }

        return stacks;
    }

    public bool TryGetFocusPartner(out TradeQueuePartner partner)
    {
        if (targetManager.FocusTarget is not IPlayerCharacter player || !player.IsTargetable)
        {
            partner = new(0, string.Empty, 0);
            return false;
        }

        partner = new(player.GameObjectId, player.Name.TextValue, player.HomeWorld.RowId);
        return true;
    }

    public bool FocusPartnerMatches(TradeQueuePartner partner) =>
        TryGetFocusPartner(out var current) &&
        current.GameObjectId == partner.GameObjectId &&
        current.HomeWorldId == partner.HomeWorldId;

    public bool TryOpenTrade(TradeQueuePartner partner)
    {
        if (!FocusPartnerMatches(partner) || targetManager.FocusTarget is not IPlayerCharacter player)
            return false;

        if (targetManager.Target?.GameObjectId != player.GameObjectId)
        {
            targetManager.Target = player;
            return false;
        }

        Chat.SendMessage("/trade");
        return true;
    }

    public unsafe bool TryOpenGilInput(out string error)
    {
        error = string.Empty;
        var addon = gameGui.GetAddonByName<AtkUnitBase>(TradeAddon, 1);
        if (!IsReady(addon))
            return false;

        try
        {
            Callback.Fire(addon, true, 2, Callback.ZeroAtkValue);
            return true;
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Unable to open the trade gil input.");
            error = $"The trade-gil command is unavailable: {exception.Message}";
            return false;
        }
    }

    public unsafe bool TryOfferItem(TradeQueueBatchLine line, out string error)
    {
        error = string.Empty;
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            error = "Inventory manager is unavailable.";
            return false;
        }

        var inventoryType = (InventoryType)line.ContainerId;
        var container = inventoryManager->GetInventoryContainer(inventoryType);
        if (container == null || !container->IsLoaded || line.SlotIndex < 0 || line.SlotIndex >= container->Size)
        {
            error = $"Source container {inventoryType} is unavailable.";
            return false;
        }

        var slot = container->GetInventorySlot(line.SlotIndex);
        if (slot == null ||
            slot->ItemId != line.ItemId ||
            slot->Quantity != line.SourceStackQuantity ||
            slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) != line.IsHighQuality)
        {
            error = $"Source slot changed before offering {line.ItemName}.";
            return false;
        }

        var agent = AgentTrade.Instance();
        if (agent == null || !agent->IsAgentActive())
        {
            error = "Trade agent is unavailable.";
            return false;
        }

        try
        {
            offerItemTrade ??= Marshal.GetDelegateForFunctionPointer<OfferItemTradeDelegate>(
                sigScanner.ScanText(OfferItemTradeSignature));
            offerItemTrade((nint)agent + 40, checked((ushort)line.SlotIndex), inventoryType);
            return true;
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Unable to offer an inventory item to trade.");
            error = $"The trade-item command is unavailable: {exception.Message}";
            return false;
        }
    }

    public unsafe bool TrySubmitQuantity(int quantity, out string error)
    {
        error = string.Empty;
        var addon = gameGui.GetAddonByName<AtkUnitBase>(NumericInputAddon, 1);
        if (!IsReady(addon))
            return false;
        if (addon->AtkValuesCount <= 3)
        {
            error = "Numeric quantity input did not expose a maximum.";
            return false;
        }

        var maximum = checked((int)addon->AtkValues[3].UInt);
        if (quantity <= 0 || quantity > maximum)
        {
            error = $"Requested quantity {quantity:N0} exceeds the trade input maximum {maximum:N0}.";
            return false;
        }

        addon->FireCallbackInt(quantity);
        return true;
    }

    public unsafe bool TryClickReady(out string error)
    {
        error = string.Empty;
        var addon = gameGui.GetAddonByName<AtkUnitBase>(TradeAddon, 1);
        if (!IsReady(addon) || addon->UldManager.NodeListCount <= 3)
            return false;

        var node = addon->UldManager.NodeList[3];
        var button = node == null ? null : (AtkComponentButton*)node->GetComponent();
        if (button == null || !button->IsEnabled)
            return false;

        button->ClickAddonButton(addon);
        return true;
    }

    public unsafe bool TryConfirmTrade(out string error)
    {
        error = string.Empty;
        var addon = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesNoAddon, 1);
        if (addon == null || !IsReady(&addon->AtkUnitBase))
            return false;

        var prompt = addon->PromptText->NodeText.ExtractText();
        if (!string.Equals(prompt, tradeConfirmationText, StringComparison.Ordinal))
        {
            error = $"An unrelated confirmation prompt is open: {prompt}";
            return false;
        }

        addon->AtkUnitBase.FireCallbackInt(0);
        return true;
    }

    private static unsafe bool IsReady(AtkUnitBase* addon) =>
        addon != null && addon->IsReady && addon->IsVisible;

    private delegate void OfferItemTradeDelegate(nint tradeAddress, ushort slot, InventoryType inventoryType);
}
