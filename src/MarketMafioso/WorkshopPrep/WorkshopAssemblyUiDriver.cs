using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.Automation.Runtime;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MarketMafioso.WorkshopPrep;

internal sealed class WorkshopAssemblyUiDriver : IDisposable
{
    private const float MaxFabricationStationDistance = 8f;
    private const string SelectStringAddon = "SelectString";
    private const string CutSceneSelectStringAddon = "CutSceneSelectString";
    private const string RequestAddon = "Request";
    private const string ContextIconMenuAddon = "ContextIconMenu";
    private const string SelectYesNoAddon = "SelectYesno";
    private const string CompanyCraftRecipeNoteBookAddon = "CompanyCraftRecipeNoteBook";
    private const string CompanyCraftMaterialAddon = "CompanyCraftMaterial";
    private const string CompanyCraftSupplyAddon = "CompanyCraftSupply";
    private const string SubmarinePartsMenuAddon = "SubmarinePartsMenu";
    private const string AirshipPartsMenuAddon = "AirshipPartsMenu";

    internal static readonly IReadOnlyList<string> MaterialDeliveryAddonNames =
    [
        CompanyCraftSupplyAddon,
        CompanyCraftMaterialAddon,
        SubmarinePartsMenuAddon,
        AirshipPartsMenuAddon,
    ];

    private readonly IGameGui gameGui;
    private readonly IAgentLifecycle agentLifecycle;
    private readonly IPluginLog log;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICondition condition;
    private readonly ExternalAutomationCoordinator externalAutomationCoordinator;
    private readonly Action<string> onMaterialRequestConfirmed;
    private readonly WorkshopNativeCallbackCapability callbackCapability;
    private readonly WorkshopNativeFaultMonitor faultMonitor;
    private uint? pendingContributionItemId;
    private readonly WorkshopRequestTurnInStateMachine requestTurnIn = new();

    public WorkshopAssemblyUiDriver(
        IGameGui gameGui,
        IPluginLog log,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition,
        ISigScanner sigScanner,
        IAgentLifecycle agentLifecycle,
        ExternalAutomationCoordinator externalAutomationCoordinator,
        WorkshopNativeFaultMonitor faultMonitor,
        Action<string> onMaterialRequestConfirmed)
    {
        this.faultMonitor = faultMonitor;
        this.gameGui = gameGui;
        this.agentLifecycle = agentLifecycle;
        this.log = log;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.condition = condition;
        this.externalAutomationCoordinator = externalAutomationCoordinator;
        this.onMaterialRequestConfirmed = onMaterialRequestConfirmed;
        callbackCapability = new(sigScanner);
    }

    public WorkshopAssemblyDiagnostics Diagnostics { get; set; } = WorkshopAssemblyDiagnostics.Disabled;

    public void ResetRequestState()
    {
        ClearMaterialRequest();
        externalAutomationCoordinator.RestoreTextAdvance();
        externalAutomationCoordinator.RestoreWorkshopRequestAutomation();
    }

    public unsafe bool IsFabricationStationUiReady(bool hasPendingConfirmation)
    {
        return IsAddonReady(CompanyCraftRecipeNoteBookAddon) ||
               GetMaterialDeliveryAddon() != null ||
               IsAddonReady(SelectStringAddon) ||
               (hasPendingConfirmation && IsAddonReady(SelectYesNoAddon));
    }

    public unsafe WorkshopCutsceneSkipState TrySkipCutscene()
    {
        var addon = gameGui.GetAddonByName<AddonCutSceneSelectString>(CutSceneSelectStringAddon, 1);
        if (addon == null || !IsAddonReady(&addon->AtkUnitBase))
            return IsCutsceneActive() ? WorkshopCutsceneSkipState.CutsceneActive : WorkshopCutsceneSkipState.NoPrompt;

        using (faultMonitor.Dispatch("cutscene-skip"))
            addon->AtkUnitBase.FireCallbackInt(0);
        Diagnostics.Record("cutscene-skip", "Selected workshop cutscene skip prompt.");
        log.Verbose("[MarketMafioso] Selected workshop cutscene skip prompt.");
        return WorkshopCutsceneSkipState.PromptSelected;
    }

    public unsafe WorkshopFabricationStationOpenResult TryOpenFabricationStation()
    {
        // Never interact with a station while the character is already in an
        // event with no workshop UI to show for it: that is the wedged state, and
        // more interacts only pile onto it.
        if (IsCharacterBusyWithoutWorkshopUi())
            return new(WorkshopFabricationStationOpenState.CharacterBusy);

        var station = FindFabricationStation();
        if (station == null)
            return new(WorkshopFabricationStationOpenState.NotFound);

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return new(WorkshopFabricationStationOpenState.TargetSystemUnavailable);

        targetManager.Target = station;
        ulong result;
        using (faultMonitor.Dispatch("station-interact"))
            result = targetSystem->InteractWithObject((ClientGameObject*)station.Address, true);
        Diagnostics.Record(
            "open-station",
            "Interacted with nearby fabrication station.",
            new Dictionary<string, string?>
            {
                ["name"] = station.Name.TextValue,
                ["objectKind"] = station.ObjectKind.ToString(),
                ["gameObjectId"] = station.GameObjectId.ToString("X"),
                ["baseId"] = station.BaseId.ToString(),
                ["distance"] = DistanceToPlayer(station)?.ToString("0.00") ?? "unavailable",
                ["result"] = result.ToString(),
            });
        log.Verbose($"[MarketMafioso] Interacted with fabrication station {station.Name.TextValue} ({station.GameObjectId:X}).");
        return new(WorkshopFabricationStationOpenState.Interacted, station.Name.TextValue);
    }

    public unsafe WorkshopCraftingLogSnapshot? ReadCraftingLog()
    {
        var addon = GetCraftingLogAddon();
        if (addon == null)
            return null;

        // The addon reports ready a frame or two before its recipe values are
        // populated. Treat a mismatched layout as "still loading" here; the
        // callback paths re-validate strictly before anything is dispatched.
        try
        {
            return new WorkshopCraftingLogSnapshot((nint)addon, ReadVisibleCraftingLogItems(addon));
        }
        catch (InvalidOperationException)
        {
            Diagnostics.Record(
                "crafting-log-loading",
                "Workshop crafting log values do not match the recipe schema yet.",
                new Dictionary<string, string?> { ["layout"] = DescribeCraftingLogLayout(addon) });
            return null;
        }
    }

    public bool IsCraftingLogVisible => IsAddonReady(CompanyCraftRecipeNoteBookAddon);

    public unsafe string DescribeCraftingLogInternals()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(CompanyCraftRecipeNoteBookAddon, 1);
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.CompanyCraftRecipeNoteBook);
        return WorkshopCraftingLogInspector.Describe(addon, agent);
    }

    // The FC's unlocked schematics live at agent+0x38 (one bit per draft row);
    // the addon mirrors the low dword in value 2. Both must agree or the read is
    // discarded: a wrong "locked" answer would silently skip a craftable project.
    public unsafe WorkshopDraftUnlocks? ReadDraftUnlocks(WorkshopCraftingLogSnapshot craftingLog)
    {
        var addon = GetCraftingLogAddon();
        if (addon == null || (nint)addon != craftingLog.AddonAddress || addon->AtkValues == null || addon->AtkValuesCount <= 2)
            return null;

        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.CompanyCraftRecipeNoteBook);
        if (agent == null || !agent->IsAgentActive())
            return null;

        var mirror = addon->AtkValues[2];
        uint? lowWord = mirror.Type == AtkValueType.UInt ? mirror.UInt : null;
        var agentMask = *(ulong*)((byte*)agent + DraftUnlockMaskOffset);
        var unlocks = WorkshopDraftUnlocks.TryCreate(agentMask, lowWord);
        if (unlocks == null)
        {
            Diagnostics.Record(
                "draft-unlocks-unverified",
                "Could not verify the Free Company's unlocked schematics.",
                new Dictionary<string, string?>
                {
                    ["agentMask"] = agentMask.ToString("X16"),
                    ["addonValue2Type"] = mirror.Type.ToString(),
                    ["addonValue2"] = lowWord?.ToString("X8"),
                });
        }

        return unlocks;
    }

    private const int DraftUnlockMaskOffset = 0x38;

    public unsafe bool CloseCraftingLog()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(CompanyCraftRecipeNoteBookAddon, 1);
        if (addon == null || !addon->IsVisible)
            return false;

        using (faultMonitor.Dispatch("crafting-log.close"))
            addon->Close(true);
        return true;
    }

    private static unsafe string DescribeCraftingLogLayout(AtkUnitBase* addon)
    {
        var count = addon->AtkValuesCount;
        if (addon->AtkValues == null)
            return $"count={count} values=null";
        var types = new List<string>();
        for (var index = 13; index < Math.Min((int)count, 22); index++)
            types.Add($"{index}:{addon->AtkValues[index].Type}");
        return $"count={count} {string.Join(' ', types)}";
    }

    public unsafe void SelectCraftCategory(WorkshopCraftingLogSnapshot craftingLog, WorkshopAssemblyQueueEntry entry)
    {
        var addon = RequireCraftingLog(craftingLog);
        if (entry.CategoryId == 0 || entry.TypeId == 0)
            throw new InvalidOperationException("Workshop category/type data is missing; no callback was sent.");
        var values = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 2 },
            new() { Type = 0, Int = 0 },
            new() { Type = AtkValueType.UInt, UInt = entry.CategoryId },
            new() { Type = AtkValueType.UInt, UInt = entry.TypeId },
            new() { Type = AtkValueType.UInt, Int = 0 },
            new() { Type = AtkValueType.UInt, Int = 0 },
            new() { Type = AtkValueType.UInt, Int = 0 },
            new() { Type = 0, Int = 0 },
        };
        WorkshopCallbackProtocol.RequirePayload(WorkshopCallbackOperation.Category, new ReadOnlySpan<AtkValue>(values, 8));
        using (faultMonitor.Dispatch($"crafting-log.category {entry.CategoryId}/{entry.TypeId}"))
            addon->FireCallback(8, values, true);
    }

    public unsafe void SelectCraft(WorkshopCraftingLogSnapshot craftingLog, WorkshopAssemblyQueueEntry entry)
    {
        var addon = RequireCraftingLog(craftingLog);
        WorkshopCallbackProtocol.RequireProjectVisible(ReadVisibleCraftingLogItems(addon), entry.WorkshopItemId);
        var values = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 1 },
            new() { Type = 0, Int = 0 },
            new() { Type = 0, Int = 0 },
            new() { Type = 0, Int = 0 },
            new() { Type = AtkValueType.UInt, UInt = entry.WorkshopItemId },
            new() { Type = 0, Int = 0 },
            new() { Type = 0, Int = 0 },
            new() { Type = 0, Int = 0 },
        };
        WorkshopCallbackProtocol.RequirePayload(WorkshopCallbackOperation.Project, new ReadOnlySpan<AtkValue>(values, 8));
        using (faultMonitor.Dispatch($"crafting-log.project {entry.WorkshopItemId}"))
            addon->FireCallback(8, values, true);
    }

    public unsafe WorkshopMaterialDeliverySnapshot? ReadMaterialDelivery()
    {
        var addon = GetMaterialDeliveryAddon();
        var craftState = ReadCraftState(addon);
        return craftState == null ? null : new WorkshopMaterialDeliverySnapshot((nint)addon, craftState);
    }

    public unsafe void BeginMaterialContribution(
        WorkshopMaterialDeliverySnapshot materialDelivery,
        int materialIndex,
        WorkshopCraftMaterialState item)
    {
        var addon = RequireMaterialDelivery(materialDelivery);
        var current = ReadCraftState(addon) ?? throw new InvalidOperationException("Workshop material values changed; no contribution was sent.");
        var agent = AgentCompanyCraftMaterial.Instance();
        var nativeBytes = (byte*)agent;
        // The validated handler binds these mode/request/quantity accesses.
        // SDK fields independently bind the result and twelve supply slots.
        if (Marshal.OffsetOf<AgentCompanyCraftMaterial>(nameof(AgentCompanyCraftMaterial.ResultItem)).ToInt32() != 0x94 ||
            Marshal.OffsetOf<AgentCompanyCraftMaterial>("_supplyItems").ToInt32() != 0x9c ||
            AgentCompanyCraftMaterial.StructSize < 0xe5)
            throw new InvalidOperationException("Workshop native supply layout changed; no contribution was sent.");
        var quantities = new ReadOnlySpan<byte>(nativeBytes + 0xcc, agent->SupplyItems.Length).ToArray();
        WorkshopCallbackProtocol.RequireContribution(materialDelivery.CraftState, current, materialIndex, item,
            nativeBytes[0x98], nativeBytes[0xe4] != 0, agent->ResultItem, agent->SupplyItems.ToArray(), quantities);

        var values = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 0 },
            new() { Type = AtkValueType.UInt, UInt = (uint)materialIndex },
            new() { Type = AtkValueType.UInt, UInt = item.ItemCountPerStep },
            new() { Type = 0, Int = 0 },
        };
        WorkshopCallbackProtocol.RequirePayload(WorkshopCallbackOperation.Contribution, new ReadOnlySpan<AtkValue>(values, 4));
        pendingContributionItemId = item.ItemId;
        requestTurnIn.Begin(item.ItemId);
        try
        {
            externalAutomationCoordinator.SuppressWorkshopRequestAutomation();
            externalAutomationCoordinator.SuppressTextAdvance();
            using (faultMonitor.Dispatch($"material.contribute index={materialIndex} item={item.ItemId}"))
                addon->FireCallback(4, values, true);
        }
        catch { ClearMaterialRequest(); throw; }
    }

    public unsafe void CloseMaterialDelivery(WorkshopMaterialDeliverySnapshot materialDelivery)
    {
        var addon = RequireMaterialDelivery(materialDelivery);

        var values = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = -1 },
        };
        WorkshopCallbackProtocol.RequirePayload(WorkshopCallbackOperation.Close, new ReadOnlySpan<AtkValue>(values, 1));
        using (faultMonitor.Dispatch("material.close"))
            addon->FireCallback(1, values, true);
    }

    public static unsafe bool HasItemInSingleSlot(uint itemId, uint count)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return false;

        for (var type = InventoryType.Inventory1; type <= InventoryType.Inventory4; type++)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var item = container->GetInventorySlot(slotIndex);
                if (item != null && item->ItemId == itemId && item->Quantity >= count)
                    return true;
            }
        }

        return false;
    }

    public void ClearMaterialRequest()
    {
        pendingContributionItemId = null;
        requestTurnIn.Reset();
        externalAutomationCoordinator.RestoreTextAdvance();
        externalAutomationCoordinator.RestoreWorkshopRequestAutomation();
    }

    public unsafe WorkshopNativeRequestTurnInResult TryAdvanceMaterialRequest()
    {
        if (pendingContributionItemId == null)
            return new(false, "No workshop material request is active.", WorkshopRequestTurnInPhase.Idle, false, false, false, false);

        var request = gameGui.GetAddonByName<AddonRequest>(RequestAddon, 1);
        var contextMenu = gameGui.GetAddonByName<AddonContextIconMenu>(ContextIconMenuAddon, 1);
        var requestPresent = request != null;
        var contextMenuPresent = contextMenu != null;
        var requestReady = request != null && IsAddonReady(&request->AtkUnitBase);
        var contextMenuReady = contextMenu != null && IsAddonReady(&contextMenu->AtkUnitBase);
        var handOverEnabled = requestReady && request->HandOverButton != null && request->HandOverButton->IsEnabled;
        var decision = requestTurnIn.Advance(new(
            requestReady,
            requestReady ? request->EntryCount : 0,
            contextMenuReady,
            handOverEnabled));

        switch (decision.Action)
        {
            case WorkshopRequestTurnInAction.OpenItemSelector:
                {
                    var values = stackalloc AtkValue[]
                    {
                        new() { Type = AtkValueType.Int, Int = WorkshopRequestTurnInProtocol.OpenItemSelectorPayload[0] },
                        new() { Type = AtkValueType.Int, Int = WorkshopRequestTurnInProtocol.OpenItemSelectorPayload[1] },
                        new() { Type = AtkValueType.UInt, UInt = (uint)WorkshopRequestTurnInProtocol.OpenItemSelectorPayload[2] },
                        new() { Type = AtkValueType.UInt, UInt = (uint)WorkshopRequestTurnInProtocol.OpenItemSelectorPayload[3] },
                    };
                    using (faultMonitor.Dispatch("request.open-item-selector"))
                        request->AtkUnitBase.FireCallback(4, values, true);
                    break;
                }

            case WorkshopRequestTurnInAction.SelectEligibleItem:
                {
                    var values = stackalloc AtkValue[]
                    {
                        new() { Type = AtkValueType.Int, Int = WorkshopRequestTurnInProtocol.SelectEligibleItemPayload[0] },
                        new() { Type = AtkValueType.Int, Int = WorkshopRequestTurnInProtocol.SelectEligibleItemPayload[1] },
                        new() { Type = AtkValueType.UInt, UInt = (uint)WorkshopRequestTurnInProtocol.SelectEligibleItemPayload[2] },
                        new() { Type = AtkValueType.UInt, UInt = (uint)WorkshopRequestTurnInProtocol.SelectEligibleItemPayload[3] },
                        new() { Type = AtkValueType.UInt, UInt = (uint)WorkshopRequestTurnInProtocol.SelectEligibleItemPayload[4] },
                    };
                    using (faultMonitor.Dispatch("request.select-eligible-item"))
                        contextMenu->AtkUnitBase.FireCallback(5, values, true);
                    break;
                }

            case WorkshopRequestTurnInAction.HandOver:
                using (faultMonitor.Dispatch("request.hand-over"))
                    request->HandOverButton->ClickAddonButton(&request->AtkUnitBase);
                onMaterialRequestConfirmed($"handed over request item for material {pendingContributionItemId}");
                break;
        }

        if (decision.Action != WorkshopRequestTurnInAction.None)
        {
            log.Verbose("[MarketMafioso] {Message}", decision.Message);
            Diagnostics.Record(
                "request-turn-in",
                decision.Message,
                new Dictionary<string, string?>
                {
                    ["itemId"] = pendingContributionItemId.Value.ToString(),
                    ["phase"] = requestTurnIn.Phase.ToString(),
                    ["action"] = decision.Action.ToString(),
                });
        }
        else
        {
            Diagnostics.Record(
                "request-turn-in-wait",
                decision.Message,
                new Dictionary<string, string?>
                {
                    ["itemId"] = pendingContributionItemId.Value.ToString(),
                    ["phase"] = requestTurnIn.Phase.ToString(),
                    ["requestPresent"] = requestPresent.ToString(),
                    ["requestReady"] = requestReady.ToString(),
                    ["requestEntryCount"] = requestReady ? request->EntryCount.ToString() : null,
                    ["contextMenuPresent"] = contextMenuPresent.ToString(),
                    ["contextMenuReady"] = contextMenuReady.ToString(),
                    ["handOverEnabled"] = handOverEnabled.ToString(),
                });
        }

        return new(
            decision.Action != WorkshopRequestTurnInAction.None,
            decision.Message,
            requestTurnIn.Phase,
            requestPresent,
            requestReady,
            contextMenuPresent,
            contextMenuReady);
    }

    public unsafe WorkshopMaterialRequestRecoveryResult RecoverStalledMaterialRequest()
    {
        var itemId = pendingContributionItemId;
        var phase = requestTurnIn.Phase;
        var request = gameGui.GetAddonByName<AddonRequest>(RequestAddon, 1);
        var contextMenu = gameGui.GetAddonByName<AddonContextIconMenu>(ContextIconMenuAddon, 1);
        var requestPresent = request != null;
        var contextMenuPresent = contextMenu != null;
        var requestReady = request != null && IsAddonReady(&request->AtkUnitBase);
        var contextMenuReady = contextMenu != null && IsAddonReady(&contextMenu->AtkUnitBase);

        using (faultMonitor.Dispatch("request.recover-close"))
        {
            if (contextMenu != null)
                contextMenu->AtkUnitBase.Close(true);

            if (request != null)
                request->AtkUnitBase.Close(true);
        }

        ClearMaterialRequest();
        var message = requestPresent || contextMenuPresent
            ? $"Closed a stalled workshop material request for material {itemId}."
            : $"Cleared a stalled workshop material request for material {itemId}; its addons had already closed.";
        Diagnostics.Record(
            "request-turn-in-recovery",
            message,
            new Dictionary<string, string?>
            {
                ["itemId"] = itemId?.ToString(),
                ["phase"] = phase.ToString(),
                ["requestPresent"] = requestPresent.ToString(),
                ["requestReady"] = requestReady.ToString(),
                ["contextMenuPresent"] = contextMenuPresent.ToString(),
                ["contextMenuReady"] = contextMenuReady.ToString(),
            });
        log.Warning("[MarketMafioso] {Message}", message);
        return new(message, phase, requestPresent, requestReady, contextMenuPresent, contextMenuReady);
    }

    public unsafe bool IsSelectStringVisible
    {
        get
        {
            var addon = gameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
            return addon != null && IsAddonReady(&addon->AtkUnitBase);
        }
    }

    public bool TryLeaveStationForPreparation()
    {
        // Never select cancel-project or advance/collect while leaving. Match
        // only the non-mutating dismissal in an identified workshop menu.
        if (!HasSelectStringEntry(WorkshopAssemblyPromptPolicy.IsPostContributionMenuEntry) &&
            !HasSelectStringEntry(text => text == WorkshopAssemblyPromptPolicy.ViewCraftingLogEntry))
            return false;
        return TrySelectString(text => text == WorkshopAssemblyPromptPolicy.NothingEntry);
    }

    public unsafe bool TryDismissIdleStationMenu()
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
        if (addon == null || !IsAddonReady(&addon->AtkUnitBase))
            return false;

        var popup = addon->PopupMenu.PopupMenu;
        var entries = new List<string>();
        for (var index = 0; index < popup.EntryCount; index++)
            entries.Add(popup.EntryNames[index].ToString());
        if (!WorkshopAssemblyPromptPolicy.IsIdleStationMenu(entries))
            return false;

        return TrySelectString(text => text == WorkshopAssemblyPromptPolicy.NothingEntry);
    }

    public unsafe bool TrySelectString(Predicate<string> predicate)
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
        if (addon == null || !IsAddonReady(&addon->AtkUnitBase))
            return false;

        var popup = addon->PopupMenu.PopupMenu;
        for (var index = 0; index < popup.EntryCount; index++)
        {
            var text = popup.EntryNames[index].ToString();
            if (string.IsNullOrWhiteSpace(text) || !predicate(text))
                continue;

            using (faultMonitor.Dispatch($"select-string {index}:{text}"))
                addon->AtkUnitBase.FireCallbackInt(index);
            log.Verbose($"[MarketMafioso] Selected workshop menu entry {index}: {text}");
            Diagnostics.Record(
                "select-string",
                "Selected workshop menu entry.",
                new Dictionary<string, string?>
                {
                    ["index"] = index.ToString(),
                    ["text"] = text,
                });
            return true;
        }

        return false;
    }

    public unsafe bool HasSelectStringEntry(Predicate<string> predicate)
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
        if (addon == null || !IsAddonReady(&addon->AtkUnitBase))
            return false;

        var popup = addon->PopupMenu.PopupMenu;
        for (var index = 0; index < popup.EntryCount; index++)
        {
            var text = popup.EntryNames[index].ToString();
            if (!string.IsNullOrWhiteSpace(text) && predicate(text))
                return true;
        }

        return false;
    }

    public unsafe bool TryGetSelectYesNoPrompt(out string text)
    {
        text = string.Empty;
        var addon = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesNoAddon, 1);
        if (addon == null || !IsAddonReady(&addon->AtkUnitBase))
            return false;

        text = addon->PromptText->NodeText.ExtractText()
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal);

        return !string.IsNullOrWhiteSpace(text);
    }

    public unsafe bool TrySelectYesNo(
        int choice,
        string text,
        WorkshopAssemblyPendingConfirmationKind pendingConfirmationKind)
    {
        var addon = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesNoAddon, 1);
        if (addon == null || !IsAddonReady(&addon->AtkUnitBase))
            return false;

        using (faultMonitor.Dispatch($"select-yesno {choice}:{text}"))
            addon->AtkUnitBase.FireCallbackInt(choice);
        log.Verbose($"[MarketMafioso] Selected workshop confirmation {choice}: {text}");
        Diagnostics.Record(
            "select-yesno",
            "Selected workshop confirmation.",
            new Dictionary<string, string?>
            {
                ["choice"] = choice.ToString(),
                ["text"] = text,
                ["pendingConfirmation"] = pendingConfirmationKind.ToString(),
            });
        return true;
    }

    public unsafe string DescribeUiState(WorkshopAssemblyPendingConfirmationKind pendingConfirmationKind)
    {
        var trackedAddons = new[]
        {
            SelectStringAddon,
            RequestAddon,
            ContextIconMenuAddon,
            SelectYesNoAddon,
            CutSceneSelectStringAddon,
            CompanyCraftRecipeNoteBookAddon,
        }.Concat(MaterialDeliveryAddonNames);

        var activeAddons = new List<string>();
        foreach (var addonName in trackedAddons)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon == null)
                continue;

            activeAddons.Add($"{addonName}({(addon->IsReady ? "ready" : "not ready")}, {(addon->IsVisible ? "visible" : "hidden")})");
        }

        var state = activeAddons.Count == 0
            ? "Workshop UI state: no tracked addons present."
            : $"Workshop UI state: {string.Join(", ", activeAddons)}.";

        var details = new List<string>();
        var selectStringEntries = DescribeSelectStringEntries();
        if (selectStringEntries != null)
            details.Add($"SelectString entries: {selectStringEntries}");

        if (TryGetSelectYesNoPrompt(out var selectYesNoPrompt))
            details.Add($"SelectYesno prompt: {selectYesNoPrompt}");

        if (pendingConfirmationKind != WorkshopAssemblyPendingConfirmationKind.None)
            details.Add($"Pending confirmation: {pendingConfirmationKind}");

        return details.Count == 0
            ? state
            : $"{state} {string.Join(" ", details.Select(x => $"{x}."))}";
    }

    private static readonly ConditionFlag[] EventOccupancyFlags =
    [
        ConditionFlag.Occupied,
        ConditionFlag.Occupied30,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.Occupied33,
        ConditionFlag.Occupied38,
        ConditionFlag.Occupied39,
        ConditionFlag.OccupiedSummoningBell,
    ];

    private static readonly string[] LeftoverWorkshopWindowNames = [RequestAddon, ContextIconMenuAddon];

    public bool IsCharacterEventOccupied => condition.Any(EventOccupancyFlags);

    public unsafe bool IsCharacterBusyWithoutWorkshopUi()
    {
        if (!IsCharacterEventOccupied || IsCutsceneActive())
            return false;

        return !IsFabricationStationUiReady(hasPendingConfirmation: true) &&
               !IsAddonReady(RequestAddon) &&
               !IsAddonReady(ContextIconMenuAddon) &&
               !IsAddonReady(CutSceneSelectStringAddon);
    }

    // Hand-in windows left loaded but hidden by an earlier run (Primary showed a
    // hidden ContextIconMenu at its failure). Closing them is the same addon-level
    // close the stalled-request recovery already uses.
    public unsafe IReadOnlyList<string> CloseLeftoverHiddenWorkshopWindows()
    {
        var closed = new List<string>();
        foreach (var name in LeftoverWorkshopWindowNames)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(name, 1);
            if (addon == null || addon->IsVisible)
                continue;

            using (faultMonitor.Dispatch($"preflight.close-hidden {name}"))
                addon->Close(true);
            closed.Add(name);
        }

        if (closed.Count > 0)
        {
            Diagnostics.Record(
                "preflight-cleanup",
                "Closed hidden workshop windows left over from an earlier run.",
                new Dictionary<string, string?> { ["closed"] = string.Join(", ", closed) });
        }

        return closed;
    }

    // Everything needed to explain a lockup after the fact, without anyone having
    // to be at the keyboard while the character is stuck.
    public unsafe string CaptureForensics()
    {
        var lines = new List<string>();
        try
        {
            var active = condition.AsReadOnlySet().OrderBy(x => (int)x).Select(x => $"{x}({(int)x})");
            lines.Add($"conditions: {string.Join(", ", active)}");
            lines.Add($"eventOccupied={IsCharacterEventOccupied} busyWithoutWorkshopUi={IsCharacterBusyWithoutWorkshopUi()} cutscene={IsCutsceneActive()}");

            var player = objectTable.LocalPlayer;
            lines.Add(player == null
                ? "player: unavailable"
                : $"player: pos={player.Position:0.00} id={player.GameObjectId:X}");
            var target = targetManager.Target;
            lines.Add(target == null
                ? "target: none"
                : $"target: {target.Name.TextValue} kind={target.ObjectKind} id={target.GameObjectId:X} distance={DistanceToPlayer(target)?.ToString("0.00") ?? "?"}");
            var station = FindFabricationStation();
            lines.Add(station == null
                ? "nearest station: none in reach"
                : $"nearest station: id={station.GameObjectId:X} distance={DistanceToPlayer(station)?.ToString("0.00") ?? "?"}");

            var unitManager = RaptureAtkUnitManager.Instance();
            if (unitManager != null)
            {
                lines.Add($"focused: {DescribeUnitList(&unitManager->AtkUnitManager.FocusedUnitsList, visibleOnly: false)}");
                lines.Add($"loaded: {DescribeUnitList(&unitManager->AtkUnitManager.AllLoadedUnitsList, visibleOnly: false)}");
            }

            lines.Add($"fault monitor: {faultMonitor.Status}");
            var events = faultMonitor.RecentDispatchAgentEvents;
            lines.Add($"recent dispatch agent events: {(events.Count == 0 ? "none" : string.Join(" | ", events.TakeLast(8)))}");
        }
        catch (Exception ex)
        {
            lines.Add($"forensics incomplete: {ex.GetType().Name}: {ex.Message}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static unsafe string DescribeUnitList(AtkUnitList* list, bool visibleOnly)
    {
        var names = new List<string>();
        var count = Math.Min((int)list->Count, list->Entries.Length);
        for (var index = 0; index < count; index++)
        {
            var unit = list->Entries[index].Value;
            if (unit == null || (visibleOnly && !unit->IsVisible))
                continue;

            names.Add($"{unit->NameString}({(unit->IsVisible ? "v" : "h")}{(unit->IsReady ? "r" : "-")})");
        }

        return names.Count == 0 ? "none" : string.Join(" ", names);
    }

    public void Dispose()
    {
        faultMonitor.Dispose();
        externalAutomationCoordinator.Dispose();
    }

    private bool IsCutsceneActive()
    {
        return condition[ConditionFlag.OccupiedInCutSceneEvent] ||
               condition[ConditionFlag.WatchingCutscene] ||
               condition[ConditionFlag.WatchingCutscene78];
    }

    // Dalamud's YalmDistanceX/Z no longer report distance for event objects (they read 0),
    // which let an out-of-reach station on another floor win. Use real positions instead.
    private IGameObject? FindFabricationStation()
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
            return null;

        return WorkshopFabricationStationSelection.SelectNearest(
            objectTable.Where(IsFabricationStationObject),
            player.Position,
            x => x.Position,
            MaxFabricationStationDistance);
    }

    private float? DistanceToPlayer(IGameObject gameObject) =>
        objectTable.LocalPlayer is { } player ? Vector3.Distance(player.Position, gameObject.Position) : null;

    private static bool IsFabricationStationObject(IGameObject? gameObject)
    {
        if (gameObject == null || !gameObject.IsTargetable)
            return false;

        if (gameObject.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject or ObjectKind.ReactionEventObject))
            return false;

        return gameObject.Name.TextValue.Contains("Fabrication Station", StringComparison.OrdinalIgnoreCase);
    }

    private unsafe AtkUnitBase* GetCraftingLogAddon()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(CompanyCraftRecipeNoteBookAddon, 1);
        return IsAddonReady(addon) ? addon : null;
    }

    private unsafe AtkUnitBase* RequireCraftingLog(WorkshopCraftingLogSnapshot captured)
    {
        var expectedReceiver = callbackCapability.RequireRecipe();
        RequireCallbackAddresses();
        var addon = GetCraftingLogAddon();
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.CompanyCraftRecipeNoteBook);
        WorkshopCallbackProtocol.RequireOwner(captured.AddonAddress, (nint)addon, addon == null ? 0u : (uint)addon->Id,
            agent == null ? 0 : agent->AddonId, ResolveAgentReceiver(agent), expectedReceiver);
        if (addon->AtkValues == null)
            throw new InvalidOperationException("Workshop recipe values are unavailable; no callback was sent.");
        WorkshopCallbackProtocol.RequireCraftingLogSchema(new ReadOnlySpan<AtkValue>(addon->AtkValues, addon->AtkValuesCount));
        return addon;
    }

    private unsafe nint ResolveAgentReceiver(AgentInterface* agent)
    {
        if (agent == null || agent->VirtualTable == null)
            return 0;
        var original = (AgentInterface.AgentInterfaceVirtualTable*)agentLifecycle.GetOriginalVirtualTable((nint)agent->VirtualTable);
        return WorkshopCallbackProtocol.ResolveAgentReceiver(
            (nint)agent->VirtualTable->ReceiveEvent, original == null ? 0 : (nint)original->ReceiveEvent);
    }

    private unsafe AtkUnitBase* RequireMaterialDelivery(WorkshopMaterialDeliverySnapshot captured)
    {
        var expectedReceiver = callbackCapability.RequireMaterial();
        RequireCallbackAddresses();
        var addon = GetMaterialDeliveryAddon();
        var agent = AgentCompanyCraftMaterial.Instance();
        WorkshopCallbackProtocol.RequireOwner(captured.AddonAddress, (nint)addon, addon == null ? 0u : (uint)addon->Id,
            agent == null ? 0 : agent->AddonId, ResolveAgentReceiver((AgentInterface*)agent), expectedReceiver);
        var current = ReadCraftState(addon);
        if (current == null || current.ResultItem != captured.CraftState.ResultItem ||
            current.StepsComplete != captured.CraftState.StepsComplete || current.StepsTotal != captured.CraftState.StepsTotal)
            throw new InvalidOperationException("Workshop material values changed; no callback was sent.");
        return addon;
    }

    private static unsafe void RequireCallbackAddresses()
    {
        NativeCapability.RequireAddress((nint)AtkUnitBase.Addresses.FireCallback.Value, "Workshop callback dispatch");
        NativeCapability.RequireAddress((nint)AgentModule.Addresses.GetAgentByInternalId.Value, "Workshop agent lookup");
        if (AgentModule.Instance() == null)
            throw new InvalidOperationException("Workshop agent module is unavailable; no callback was sent.");
    }

    private unsafe AtkUnitBase* GetMaterialDeliveryAddon()
    {
        foreach (var addonName in MaterialDeliveryAddonNames)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (IsAddonReady(addon))
                return addon;
        }

        return null;
    }

    private unsafe bool IsAddonReady(string addonName)
    {
        return IsAddonReady(gameGui.GetAddonByName<AtkUnitBase>(addonName, 1));
    }

    private static unsafe bool IsAddonReady(AtkUnitBase* addon)
    {
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private unsafe string? DescribeSelectStringEntries()
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
        if (addon == null || !IsAddonReady(&addon->AtkUnitBase))
            return null;

        var popup = addon->PopupMenu.PopupMenu;
        var entries = new List<string>();
        for (var index = 0; index < popup.EntryCount; index++)
        {
            var text = popup.EntryNames[index].ToString();
            if (!string.IsNullOrWhiteSpace(text))
                entries.Add($"{index}:{text}");
        }

        return entries.Count == 0 ? null : string.Join(" | ", entries);
    }

    private static unsafe IReadOnlyList<WorkshopCraftingLogItem> ReadVisibleCraftingLogItems(AtkUnitBase* addon)
    {
        var atkValues = addon->AtkValues;
        if (atkValues == null)
            return [];
        WorkshopCallbackProtocol.RequireCraftingLogSchema(new ReadOnlySpan<AtkValue>(atkValues, addon->AtkValuesCount));

        var shownItemCount = atkValues[13].UInt;
        var visibleItems = new List<WorkshopCraftingLogItem>();
        for (var index = 0; index < shownItemCount; index++)
        {
            var baseIndex = 14 + 4 * index;
            if (baseIndex + 3 >= addon->AtkValuesCount)
                break;

            visibleItems.Add(new WorkshopCraftingLogItem(
                atkValues[baseIndex].UInt,
                atkValues[baseIndex + 3].GetValueAsString()));
        }

        return visibleItems;
    }

    private static unsafe WorkshopCraftState? ReadCraftState(AtkUnitBase* addon)
    {
        if (!IsAddonReady(addon) || addon->AtkValues == null)
            return null;

        try
        {
            var layout = default(AgentCompanyCraftMaterial);
            var capacity = layout.SupplyItems.Length;
            WorkshopCallbackProtocol.RequireMaterialSchema(new ReadOnlySpan<AtkValue>(addon->AtkValues, addon->AtkValuesCount), capacity);
        }
        catch (InvalidOperationException) { return null; }

        var atkValues = addon->AtkValues;
        var listItemCount = atkValues[11].UInt;
        var items = Enumerable.Range(0, (int)listItemCount)
            .Select(index => new WorkshopCraftMaterialState(
                atkValues[12 + index].UInt,
                atkValues[36 + index].GetValueAsString(),
                atkValues[60 + index].UInt,
                atkValues[108 + index].UInt,
                atkValues[120 + index].UInt,
                atkValues[132 + index].UInt > 0))
            .ToList();

        return new WorkshopCraftState(
            atkValues[0].UInt,
            atkValues[6].UInt,
            atkValues[7].UInt,
            items);
    }

}

internal sealed record WorkshopNativeRequestTurnInResult(
    bool ActionTaken,
    string Message,
    WorkshopRequestTurnInPhase Phase,
    bool RequestPresent,
    bool RequestReady,
    bool ContextMenuPresent,
    bool ContextMenuReady);

internal sealed record WorkshopMaterialRequestRecoveryResult(
    string Message,
    WorkshopRequestTurnInPhase Phase,
    bool RequestPresent,
    bool RequestReady,
    bool ContextMenuPresent,
    bool ContextMenuReady);

internal enum WorkshopCutsceneSkipState
{
    NoPrompt,
    CutsceneActive,
    PromptSelected,
}

internal enum WorkshopFabricationStationOpenState
{
    NotFound,
    TargetSystemUnavailable,
    Interacted,
    CharacterBusy,
}

internal sealed record WorkshopFabricationStationOpenResult(
    WorkshopFabricationStationOpenState State,
    string? StationName = null);

internal sealed record WorkshopCraftingLogSnapshot(
    nint AddonAddress,
    IReadOnlyList<WorkshopCraftingLogItem> VisibleItems);

internal sealed record WorkshopMaterialDeliverySnapshot(
    nint AddonAddress,
    WorkshopCraftState CraftState);

internal sealed record WorkshopCraftingLogItem(uint WorkshopItemId, string Name);

internal sealed record WorkshopCraftState(
    uint ResultItem,
    uint StepsComplete,
    uint StepsTotal,
    IReadOnlyList<WorkshopCraftMaterialState> Items)
{
    public bool IsPhaseComplete() => Items.All(x => x.Finished || x.StepsComplete == x.StepsTotal);
}

internal sealed record WorkshopCraftMaterialState(
    uint ItemId,
    string ItemName,
    uint ItemCountPerStep,
    uint StepsComplete,
    uint StepsTotal,
    bool Finished);
