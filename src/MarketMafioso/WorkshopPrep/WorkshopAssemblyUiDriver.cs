using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.Automation.Runtime;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MarketMafioso.WorkshopPrep;

internal sealed class WorkshopAssemblyUiDriver : IDisposable
{
    private const byte MaxFabricationStationDistance = 8;
    private const string SelectStringAddon = "SelectString";
    private const string CutSceneSelectStringAddon = "CutSceneSelectString";
    private const string RequestAddon = "Request";
    private const string ContextIconMenuAddon = "ContextIconMenu";
    private const string SelectYesNoAddon = "SelectYesno";
    private const string CompanyCraftRecipeNoteBookAddon = "CompanyCraftRecipeNoteBook";
    private const string CompanyCraftMaterialAddon = "CompanyCraftMaterial";
    private const string SubmarinePartsMenuAddon = "SubmarinePartsMenu";
    private const string AirshipPartsMenuAddon = "AirshipPartsMenu";

    internal static readonly IReadOnlyList<string> MaterialDeliveryAddonNames =
    [
        CompanyCraftMaterialAddon,
        SubmarinePartsMenuAddon,
        AirshipPartsMenuAddon,
    ];

    private readonly IGameGui gameGui;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICondition condition;
    private readonly ExternalAutomationCoordinator externalAutomationCoordinator;
    private readonly Action<string> onMaterialRequestConfirmed;
    private uint? pendingContributionItemId;
    private bool requestItemSelectionStarted;
    private bool requestConfirmed;

    public WorkshopAssemblyUiDriver(
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        IPluginLog log,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition,
        ExternalAutomationCoordinator externalAutomationCoordinator,
        Action<string> onMaterialRequestConfirmed)
    {
        this.gameGui = gameGui;
        this.addonLifecycle = addonLifecycle;
        this.log = log;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.condition = condition;
        this.externalAutomationCoordinator = externalAutomationCoordinator;
        this.onMaterialRequestConfirmed = onMaterialRequestConfirmed;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, RequestAddon, RequestPostSetup);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, RequestAddon, RequestPostRefresh);
        addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, ContextIconMenuAddon, ContextIconMenuPostReceiveEvent);
    }

    public WorkshopAssemblyDiagnostics Diagnostics { get; set; } = WorkshopAssemblyDiagnostics.Disabled;

    public void ResetRequestState()
    {
        ClearMaterialRequest();
        externalAutomationCoordinator.RestoreTextAdvance();
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

        addon->AtkUnitBase.FireCallbackInt(0);
        Diagnostics.Record("cutscene-skip", "Selected workshop cutscene skip prompt.");
        log.Verbose("[MarketMafioso] Selected workshop cutscene skip prompt.");
        return WorkshopCutsceneSkipState.PromptSelected;
    }

    public unsafe WorkshopFabricationStationOpenResult TryOpenFabricationStation()
    {
        var station = FindFabricationStation();
        if (station == null)
            return new(WorkshopFabricationStationOpenState.NotFound);

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return new(WorkshopFabricationStationOpenState.TargetSystemUnavailable);

        targetManager.Target = station;
        var result = targetSystem->InteractWithObject((ClientGameObject*)station.Address, true);
        Diagnostics.Record(
            "open-station",
            "Interacted with nearby fabrication station.",
            new Dictionary<string, string?>
            {
                ["name"] = station.Name.TextValue,
                ["objectKind"] = station.ObjectKind.ToString(),
                ["gameObjectId"] = station.GameObjectId.ToString("X"),
                ["baseId"] = station.BaseId.ToString(),
                ["distanceX"] = station.YalmDistanceX.ToString(),
                ["distanceZ"] = station.YalmDistanceZ.ToString(),
                ["result"] = result.ToString(),
            });
        log.Verbose($"[MarketMafioso] Interacted with fabrication station {station.Name.TextValue} ({station.GameObjectId:X}).");
        return new(WorkshopFabricationStationOpenState.Interacted, station.Name.TextValue);
    }

    public unsafe WorkshopCraftingLogSnapshot? ReadCraftingLog()
    {
        var addon = GetCraftingLogAddon();
        return addon == null
            ? null
            : new WorkshopCraftingLogSnapshot((nint)addon, ReadVisibleCraftingLogItems(addon));
    }

    public unsafe void SelectCraftCategory(WorkshopCraftingLogSnapshot craftingLog, WorkshopAssemblyQueueEntry entry)
    {
        var addon = (AtkUnitBase*)craftingLog.AddonAddress;
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
        addon->FireCallback(8, values, true);
    }

    public unsafe void SelectCraft(WorkshopCraftingLogSnapshot craftingLog, WorkshopAssemblyQueueEntry entry)
    {
        var addon = (AtkUnitBase*)craftingLog.AddonAddress;
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
        pendingContributionItemId = item.ItemId;
        requestItemSelectionStarted = false;
        requestConfirmed = false;
        externalAutomationCoordinator.SuppressTextAdvance();

        var addon = (AtkUnitBase*)materialDelivery.AddonAddress;
        var values = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 0 },
            new() { Type = AtkValueType.UInt, UInt = (uint)materialIndex },
            new() { Type = AtkValueType.UInt, UInt = item.ItemCountPerStep },
            new() { Type = 0, Int = 0 },
        };
        addon->FireCallback(4, values, true);
    }

    public unsafe void CloseMaterialDelivery(WorkshopMaterialDeliverySnapshot materialDelivery)
    {
        var addon = (AtkUnitBase*)materialDelivery.AddonAddress;
        if (!IsAddonReady(addon))
            return;

        var values = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = -1 },
        };
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

    public bool TryConsumeRequestConfirmed()
    {
        if (!requestConfirmed)
            return false;

        requestConfirmed = false;
        return true;
    }

    public void ClearMaterialRequest()
    {
        pendingContributionItemId = null;
        requestItemSelectionStarted = false;
        requestConfirmed = false;
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

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, ContextIconMenuAddon, ContextIconMenuPostReceiveEvent);
        addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, RequestAddon, RequestPostRefresh);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, RequestAddon, RequestPostSetup);
        externalAutomationCoordinator.Dispose();
    }

    private bool IsCutsceneActive()
    {
        return condition[ConditionFlag.OccupiedInCutSceneEvent] ||
               condition[ConditionFlag.WatchingCutscene] ||
               condition[ConditionFlag.WatchingCutscene78];
    }

    private IGameObject? FindFabricationStation()
    {
        if (IsFabricationStationObject(targetManager.Target))
            return targetManager.Target;

        return objectTable
            .Where(IsFabricationStationObject)
            .OrderBy(x => x.YalmDistanceX + x.YalmDistanceZ)
            .FirstOrDefault();
    }

    private static bool IsFabricationStationObject(IGameObject? gameObject)
    {
        if (gameObject == null || !gameObject.IsTargetable)
            return false;

        if (gameObject.YalmDistanceX > MaxFabricationStationDistance ||
            gameObject.YalmDistanceZ > MaxFabricationStationDistance)
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
        if (atkValues == null || addon->AtkValuesCount <= 13)
            return [];

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
        if (!IsAddonReady(addon) || addon->AtkValues == null || addon->AtkValuesCount != 157)
            return null;

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

    private unsafe void RequestPostSetup(AddonEvent type, AddonArgs args)
    {
        if (pendingContributionItemId == null)
            return;

        var addon = (AddonRequest*)args.Addon.Address;
        if (addon == null || addon->EntryCount != 1)
            return;

        requestItemSelectionStarted = true;
        var values = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 2 },
            new() { Type = AtkValueType.UInt, UInt = 0 },
            new() { Type = AtkValueType.UInt, UInt = 44 },
            new() { Type = AtkValueType.UInt, UInt = 0 },
        };
        addon->AtkUnitBase.FireCallback(4, values, true);
        log.Verbose($"[MarketMafioso] Opened request item selector for workshop material {pendingContributionItemId}.");
        Diagnostics.Record(
            "request-setup",
            "Opened request item selector.",
            new Dictionary<string, string?>
            {
                ["itemId"] = pendingContributionItemId.Value.ToString(),
            });
    }

    private unsafe void ContextIconMenuPostReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (pendingContributionItemId == null || !requestItemSelectionStarted)
            return;

        var addon = (AddonContextIconMenu*)args.Addon.Address;
        if (addon == null)
            return;

        var values = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 0 },
            new() { Type = AtkValueType.Int, Int = 0 },
            new() { Type = AtkValueType.UInt, UInt = 0 },
            new() { Type = AtkValueType.UInt, UInt = 0 },
            new() { Type = 0, Int = 0 },
        };
        addon->AtkUnitBase.FireCallback(5, values, true);
        log.Verbose($"[MarketMafioso] Selected request item icon for workshop material {pendingContributionItemId}.");
        Diagnostics.Record(
            "request-icon",
            "Selected request item icon.",
            new Dictionary<string, string?>
            {
                ["itemId"] = pendingContributionItemId.Value.ToString(),
            });
    }

    private unsafe void RequestPostRefresh(AddonEvent type, AddonArgs args)
    {
        if (pendingContributionItemId == null || !requestItemSelectionStarted)
            return;

        var addon = (AddonRequest*)args.Addon.Address;
        if (addon == null || addon->EntryCount != 1)
            return;

        var values = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 0 },
            new() { Type = AtkValueType.UInt, UInt = 0 },
            new() { Type = AtkValueType.UInt, UInt = 0 },
            new() { Type = AtkValueType.UInt, UInt = 0 },
        };
        addon->AtkUnitBase.FireCallback(4, values, true);
        addon->AtkUnitBase.Close(false);
        requestConfirmed = true;
        onMaterialRequestConfirmed($"confirmed request item window for material {pendingContributionItemId}");
        externalAutomationCoordinator.RestoreTextAdvance();
        log.Verbose($"[MarketMafioso] Confirmed request item window for workshop material {pendingContributionItemId}.");
        Diagnostics.Record(
            "request-confirmed",
            "Confirmed request item window.",
            new Dictionary<string, string?>
            {
                ["itemId"] = pendingContributionItemId.Value.ToString(),
            });
    }
}

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
