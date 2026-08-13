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
using ECommons.GameHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MarketMafioso.Automation.Runtime;

namespace MarketMafioso.TradeQueue;

public interface ITradeQueueIo
{
    TradeQueueInventoryObservation ObserveTradeableInventory();
    IReadOnlyList<TradeQueuePartner> GetAvailablePartners();
    bool TryGetSelectedPartner(out TradeQueuePartner partner);
    bool TryGetPartner(string name, string homeWorld, out TradeQueuePartner partner);
    bool PartnerIsAvailable(TradeQueuePartner partner);
    bool IsTradeOpen { get; }
    bool IsNumericInputOpen { get; }
    int OfferedSlotCount { get; }
    bool CanClickReady { get; }
    bool CanConfirmTrade { get; }
    bool CanCancelTrade { get; }
    bool TryOpenTrade(TradeQueuePartner partner);
    bool TryOpenGilInput(out string error);
    bool TryOfferItem(TradeQueueBatchLine line, out string error);
    bool TrySubmitQuantity(int quantity, out string error);
    bool TryClickReady(out string error);
    bool TryConfirmTrade(out string error);
    bool TryCancelTrade(out string error);
}

public sealed class DalamudTradeQueueIo : ITradeQueueIo, ITradeAutoAcceptIo
{
    private const string TradeAddon = "Trade";
    private const string NumericInputAddon = "InputNumeric";
    private const string SelectYesNoAddon = "SelectYesno";
    private const uint TradeInventoryContainerId = 2005;
    private const uint TradeConfirmationAddonRowId = 102223;
    private const string ApprovedGameVersion = "2026.08.05.0000.0000";
    private const string PatchContractId = "mmf.trade-ui-and-offer-command";
    private const string OfferItemTradeSignature =
        "48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 83 B9 ?? ?? ?? ?? ?? 41 8B F0";

    internal static readonly InventoryType[] SupportedInventories =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.Crystals,
    ];

    private readonly IGameGui gameGui;
    private readonly ITargetManager targetManager;
    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly ISigScanner sigScanner;
    private readonly IPluginLog log;
    private readonly HashSet<uint> tradeableItems;
    private readonly IReadOnlyDictionary<uint, string> itemNames;
    private readonly string tradeConfirmationText;
    private OfferItemTradeDelegate? offerItemTrade;
    private bool patchBlockLogged;

    public DalamudTradeQueueIo(
        IGameGui gameGui,
        ITargetManager targetManager,
        IObjectTable objectTable,
        ICondition condition,
        ISigScanner sigScanner,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.gameGui = gameGui;
        this.targetManager = targetManager;
        this.objectTable = objectTable;
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

    public bool IsPartnerReadyForTrade =>
        IsTradeOpen && TradeDetectionManager.PartnerReadyForTrade;

    public unsafe bool CanClickReady => TryGetReadyButton(out _, out _);

    public unsafe bool CanConfirmTrade => TryGetTradeConfirmation(out _);

    public unsafe bool CanCancelTrade => TryGetCancelButton(out _, out _);

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

    public unsafe TradeQueueInventoryObservation ObserveTradeableInventory()
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return TradeQueueInventoryObservation.Unavailable;

        foreach (var inventoryType in SupportedInventories)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                return TradeQueueInventoryObservation.Unavailable;
        }

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

        return TradeQueueInventoryObservation.Authoritative(stacks);
    }

    public bool TryGetSelectedPartner(out TradeQueuePartner partner)
    {
        var selected = targetManager.Target as IPlayerCharacter;
        var focused = targetManager.FocusTarget as IPlayerCharacter;
        var player = selected is { IsTargetable: true }
            ? selected
            : focused is { IsTargetable: true } ? focused : null;
        if (player == null)
        {
            partner = new(0, string.Empty, 0);
            return false;
        }

        partner = CreatePartner(player);
        return true;
    }

    public IReadOnlyList<TradeQueuePartner> GetAvailablePartners() =>
        EnumeratePartnerCandidates()
            .Where(player => player.IsTargetable)
            .GroupBy(player => player.GameObjectId)
            .Select(group => CreatePartner(group.First()))
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.HomeWorldName, StringComparer.Ordinal)
            .ToArray();

    public bool TryGetPartner(string name, string homeWorld, out TradeQueuePartner partner)
    {
        var player = EnumeratePartnerCandidates()
            .FirstOrDefault(candidate =>
                candidate.IsTargetable &&
                string.Equals(candidate.Name.TextValue, name, StringComparison.Ordinal) &&
                string.Equals(ResolveHomeWorldName(candidate), homeWorld, StringComparison.Ordinal));
        if (player == null)
        {
            partner = new(0, string.Empty, 0);
            return false;
        }

        partner = CreatePartner(player);
        return true;
    }

    public bool PartnerIsAvailable(TradeQueuePartner partner) =>
        TryResolvePartner(partner, out _);

    public bool TryOpenTrade(TradeQueuePartner partner)
    {
        if (!TryAuthorizePatchContract(out _))
            return false;

        if (!TryResolvePartner(partner, out var player))
            return false;

        if (targetManager.Target?.GameObjectId != player.GameObjectId)
        {
            targetManager.Target = player;
            return false;
        }

        Chat.SendMessage("/trade");
        return true;
    }

    private bool TryResolvePartner(TradeQueuePartner partner, out IPlayerCharacter player)
    {
        player = EnumeratePartnerCandidates()
            .FirstOrDefault(candidate =>
                candidate.IsTargetable &&
                candidate.GameObjectId == partner.GameObjectId &&
                candidate.HomeWorld.RowId == partner.HomeWorldId)!;
        return player != null;
    }

    private IEnumerable<IPlayerCharacter> EnumeratePartnerCandidates()
    {
        var selected = targetManager.Target as IPlayerCharacter;
        if (selected != null)
            yield return selected;
        var focused = targetManager.FocusTarget as IPlayerCharacter;
        if (focused != null &&
            focused.GameObjectId != selected?.GameObjectId)
        {
            yield return focused;
        }

        foreach (var player in objectTable.OfType<IPlayerCharacter>())
        {
            if (player.GameObjectId != selected?.GameObjectId &&
                player.GameObjectId != focused?.GameObjectId)
            {
                yield return player;
            }
        }
    }

    public unsafe bool TryOpenGilInput(out string error)
    {
        if (!TryAuthorizePatchContract(out error))
            return false;

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
        if (!TryAuthorizePatchContract(out error))
            return false;

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
        if (!TryAuthorizePatchContract(out error))
            return false;

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
        if (!TryAuthorizePatchContract(out error))
            return false;

        if (!TryGetReadyButton(out var addon, out var button))
            return false;

        button->ClickAddonButton(addon);
        return true;
    }

    public unsafe bool TryConfirmTrade(out string error)
    {
        if (!TryAuthorizePatchContract(out error))
            return false;

        if (!TryGetTradeConfirmation(out var addon))
            return false;

        new AddonMaster.SelectYesno(addon).Yes();
        return true;
    }

    public unsafe bool TryCancelTrade(out string error)
    {
        if (!TryAuthorizePatchContract(out error))
            return false;

        if (!TryGetCancelButton(out var addon, out var button))
            return false;

        button->ClickAddonButton(addon);
        return true;
    }

    private unsafe bool TryGetReadyButton(
        out AtkUnitBase* addon,
        out AtkComponentButton* button)
    {
        addon = gameGui.GetAddonByName<AtkUnitBase>(TradeAddon, 1);
        button = null;
        if (!IsReady(addon) || addon->UldManager.NodeListCount <= 3)
            return false;

        var node = addon->UldManager.NodeList[3];
        button = node == null ? null : (AtkComponentButton*)node->GetComponent();
        return button != null && button->IsEnabled;
    }

    private unsafe bool TryGetTradeConfirmation(out AddonSelectYesno* addon)
    {
        addon = null;
        for (var index = 1; index < 100; index++)
        {
            var candidate = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesNoAddon, index);
            if (candidate == null)
                return false;
            if (!IsReady(&candidate->AtkUnitBase))
                continue;

            var prompt = candidate->PromptText->NodeText.ExtractText();
            if (string.Equals(prompt, tradeConfirmationText, StringComparison.Ordinal))
            {
                addon = candidate;
                return true;
            }
        }

        return false;
    }

    private unsafe bool TryGetCancelButton(
        out AtkUnitBase* addon,
        out AtkComponentButton* button)
    {
        addon = gameGui.GetAddonByName<AtkUnitBase>(TradeAddon, 1);
        button = null;
        if (!IsReady(addon) || addon->UldManager.NodeListCount <= 2)
            return false;

        var node = addon->UldManager.NodeList[2];
        button = node == null ? null : (AtkComponentButton*)node->GetComponent();
        return button != null &&
               button->IsEnabled &&
               button->ButtonTextNode != null &&
               string.Equals(
                   button->ButtonTextNode->NodeText.ExtractText(),
                   "Cancel",
                   StringComparison.Ordinal);
    }

    private static TradeQueuePartner CreatePartner(IPlayerCharacter player) =>
        new(
            player.GameObjectId,
            player.Name.TextValue,
            player.HomeWorld.RowId,
            ResolveHomeWorldName(player));

    private static string ResolveHomeWorldName(IPlayerCharacter player) =>
        player.HomeWorld.IsValid
            ? player.HomeWorld.Value.Name.ToString()
            : string.Empty;

    private static unsafe bool IsReady(AtkUnitBase* addon) =>
        addon != null && addon->IsReady && addon->IsVisible;

    private bool TryAuthorizePatchContract(out string error)
    {
        var compatibility = GamePatchCompatibilityGate.Evaluate(PatchContractId, ApprovedGameVersion);
        if (compatibility.IsApproved)
        {
            error = string.Empty;
            return true;
        }

        error = compatibility.Message;
        if (!patchBlockLogged)
        {
            patchBlockLogged = true;
            log.Warning("[MarketMafioso] {Message}", compatibility.Message);
        }

        return false;
    }

    private delegate void OfferItemTradeDelegate(nint tradeAddress, ushort slot, InventoryType inventoryType);
}
