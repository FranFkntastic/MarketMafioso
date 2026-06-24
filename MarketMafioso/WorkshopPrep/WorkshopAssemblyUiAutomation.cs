using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopAssemblyUiAutomation : IWorkshopAssemblyUiAutomation
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
    private uint? pendingContributionItemId;
    private bool requestItemSelectionStarted;
    private bool requestConfirmed;

    public WorkshopAssemblyUiAutomation(
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        IPluginLog log,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition)
    {
        this.gameGui = gameGui;
        this.addonLifecycle = addonLifecycle;
        this.log = log;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.condition = condition;

        addonLifecycle.RegisterListener(AddonEvent.PostSetup, RequestAddon, RequestPostSetup);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, RequestAddon, RequestPostRefresh);
        addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, ContextIconMenuAddon, ContextIconMenuPostReceiveEvent);
    }

    public WorkshopAssemblyDiagnostics Diagnostics { get; set; } = WorkshopAssemblyDiagnostics.Disabled;

    public unsafe bool IsFabricationStationUiReady()
    {
        var isReady = IsAddonReady(CompanyCraftRecipeNoteBookAddon) ||
                      GetMaterialDeliveryAddon() != null ||
                      IsAddonReady(SelectStringAddon);
        Diagnostics.Record("ui-ready-check", isReady ? "Fabrication station UI is ready." : "Fabrication station UI is not ready.");
        return isReady;
    }

    public unsafe WorkshopAssemblyActionResult TrySkipCutscene()
    {
        var addon = gameGui.GetAddonByName<AddonCutSceneSelectString>(CutSceneSelectStringAddon, 1);
        if (addon == null || !IsAddonReady(&addon->AtkUnitBase))
        {
            if (IsCutsceneActive())
                return new(false, "Waiting for workshop cutscene skip prompt.", ActionTaken: true, RequiresWorkshopReopen: true);

            return new(false, "No skippable workshop cutscene prompt is visible.");
        }

        addon->AtkUnitBase.FireCallbackInt(0);
        Diagnostics.Record("cutscene-skip", "Selected workshop cutscene skip prompt.");
        log.Verbose("[MarketMafioso] Selected workshop cutscene skip prompt.");
        return new(false, "Selected workshop cutscene skip prompt.", ActionTaken: true, RequiresWorkshopReopen: true);
    }

    public unsafe WorkshopAssemblyActionResult TryOpenFabricationStation()
    {
        var station = FindFabricationStation();
        if (station == null)
            return new(false, $"Waiting for fabrication station UI. No nearby fabrication station target was found. {DescribeUiState()}");

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return new(false, $"Waiting for fabrication station UI. Target system is unavailable. {DescribeUiState()}");

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
        return new(false, $"Opened nearby fabrication station {station.Name.TextValue}.", ActionTaken: true);
    }

    public unsafe WorkshopAssemblyActionResult TryOpenProject(WorkshopAssemblyQueueEntry entry)
    {
        var activeCraft = ReadCraftState(GetMaterialDeliveryAddon());
        if (activeCraft != null)
        {
            Diagnostics.Record(
                "active-project",
                activeCraft.ResultItem == entry.ResultItemId
                    ? "Matched already-open workshop project."
                    : "Found already-open workshop project for a different result item.",
                new Dictionary<string, string?>
                {
                    ["project"] = entry.ProjectName,
                    ["expectedResultItemId"] = entry.ResultItemId.ToString(),
                    ["activeResultItemId"] = activeCraft.ResultItem.ToString(),
                    ["stepsComplete"] = activeCraft.StepsComplete.ToString(),
                    ["stepsTotal"] = activeCraft.StepsTotal.ToString(),
                    ["materialRows"] = activeCraft.Items.Count.ToString(),
                });

            if (activeCraft.ResultItem == entry.ResultItemId)
                return new(true, $"Matching workshop project {entry.ProjectName} is already open.");
        }

        if (TrySelectYesNo(0, text => text.StartsWith("Craft ", StringComparison.Ordinal)))
            return new(true, $"Confirmed workshop project {entry.ProjectName}.", ActionTaken: true);

        if (TrySelectString(IsContributeMaterialsEntry))
            return new(true, $"Selected active workshop material contribution for {entry.ProjectName}.", ActionTaken: true);

        var craftingLog = GetCraftingLogAddon();
        if (craftingLog != null)
        {
            var visibleItems = ReadVisibleCraftingLogItems(craftingLog);
            if (visibleItems.Any(x => x.WorkshopItemId == entry.WorkshopItemId))
            {
                Diagnostics.Record(
                    "select-project",
                    "Selected visible workshop project.",
                    new Dictionary<string, string?>
                    {
                        ["project"] = entry.ProjectName,
                        ["workshopItemId"] = entry.WorkshopItemId.ToString(),
                    });
                SelectCraft(craftingLog, entry);
                return new(false, $"Selected workshop project {entry.ProjectName}.", ActionTaken: true);
            }

            if (entry.CategoryId == 0 || entry.TypeId == 0)
                return new(false, $"Workshop project {entry.ProjectName} cannot be selected because category/type data is missing. {DescribeUiState()}");

            Diagnostics.Record(
                "select-category",
                "Selected workshop category/type.",
                new Dictionary<string, string?>
                {
                    ["project"] = entry.ProjectName,
                    ["categoryId"] = entry.CategoryId.ToString(),
                    ["typeId"] = entry.TypeId.ToString(),
                });
            SelectCraftCategory(craftingLog, entry);
            return new(false, $"Selected workshop category/type for {entry.ProjectName}.", ActionTaken: true);
        }

        if (TrySelectString(text => text == "View company crafting log."))
            return new(false, "Selected company crafting log.", ActionTaken: true);

        return new(false, $"Workshop project {entry.ProjectName} cannot be opened. {DescribeUiState()}");
    }

    public unsafe WorkshopAssemblyActionResult TrySubmitNextMaterial(WorkshopAssemblyQueueEntry entry)
    {
        var confirmation = TryConfirmContribution();
        if (confirmation.Success || confirmation.ActionTaken || confirmation.IsProjectComplete)
            return confirmation;

        if (requestConfirmed)
        {
            requestConfirmed = false;
            return new(
                true,
                $"Workshop material request confirmed for {entry.ProjectName}.",
                ActiveMaterialItemId: pendingContributionItemId);
        }

        if (pendingContributionItemId != null)
        {
            return new(
                false,
                $"Waiting for request item selection for material {pendingContributionItemId}. {DescribeUiState()}");
        }

        if (TrySelectString(text => text.StartsWith("Collect finished product.", StringComparison.Ordinal)))
            return new(false, $"Selected finished product collection for {entry.ProjectName}.", ActionTaken: true, RequiresWorkshopReopen: true);

        if (TrySelectString(text => text.StartsWith("Complete the construction of", StringComparison.Ordinal)))
            return new(false, $"Selected final construction step for {entry.ProjectName}.", ActionTaken: true, RequiresWorkshopReopen: true);

        if (TrySelectString(text => text.StartsWith("Advance to the next phase of production.", StringComparison.Ordinal)))
            return new(false, $"Advanced workshop project phase for {entry.ProjectName}.", ActionTaken: true, RequiresWorkshopReopen: true);

        if (TrySelectString(IsContributeMaterialsEntry))
            return new(false, $"Selected material contribution for {entry.ProjectName}.", ActionTaken: true);

        var materialDelivery = GetMaterialDeliveryAddon();
        var craftState = ReadCraftState(materialDelivery);
        if (craftState == null)
            return new(false, $"Workshop material request is not actionable for {entry.ProjectName}. {DescribeUiState()}");

        if (craftState.ResultItem != entry.ResultItemId)
        {
            return new(
                false,
                $"Open workshop project result item {craftState.ResultItem} does not match queued project {entry.ProjectName} ({entry.ResultItemId}).");
        }

        if (craftState.IsPhaseComplete())
        {
            CloseMaterialDelivery(materialDelivery);
            return new(false, $"Closed completed material phase for {entry.ProjectName}.", ActionTaken: true);
        }

        var requiredMaterialIds = entry.Materials.Select(x => x.ItemId).ToHashSet();
        for (var index = 0; index < craftState.Items.Count; index++)
        {
            var item = craftState.Items[index];
            if (item.Finished || item.StepsComplete >= item.StepsTotal || !requiredMaterialIds.Contains(item.ItemId))
                continue;

            if (!HasItemInSingleSlot(item.ItemId, item.ItemCountPerStep))
            {
                return new(
                    false,
                    $"Player inventory does not contain {item.ItemCountPerStep}x {item.ItemName} in one slot for workshop contribution.");
            }

            Diagnostics.Record(
                "contribute-material",
                "Submitted workshop material request.",
                new Dictionary<string, string?>
                {
                    ["project"] = entry.ProjectName,
                    ["itemId"] = item.ItemId.ToString(),
                    ["itemName"] = item.ItemName,
                    ["quantity"] = item.ItemCountPerStep.ToString(),
                    ["materialIndex"] = index.ToString(),
                    ["stepsComplete"] = item.StepsComplete.ToString(),
                    ["stepsTotal"] = item.StepsTotal.ToString(),
                });
            ContributeMaterial(materialDelivery, index, item);
            pendingContributionItemId = item.ItemId;
            requestItemSelectionStarted = false;
            requestConfirmed = false;
            return new(
                false,
                $"Submitted workshop material request for {item.ItemCountPerStep}x {item.ItemName}.",
                ActionTaken: true,
                ActiveMaterialItemId: item.ItemId,
                ActiveMaterialStepsComplete: item.StepsComplete);
        }

        return new(false, $"No unfinished queued material was found for {entry.ProjectName}. {DescribeUiState()}");
    }

    public WorkshopAssemblyActionResult TryConfirmContribution()
    {
        if (TrySelectYesNo(0, text => text == "You are about to hand over an HQ item. Proceed?"))
            return new(false, "Confirmed HQ workshop material handoff.", ActionTaken: true);

        if (TrySelectYesNo(0, IsContributeItemsPrompt))
        {
            var itemId = pendingContributionItemId;
            pendingContributionItemId = null;
            requestItemSelectionStarted = false;
            return new(
                true,
                "Confirmed workshop material contribution.",
                IsContributionConfirmed: true,
                ActiveMaterialItemId: itemId);
        }

        if (TrySelectYesNo(0, text => text.StartsWith("Retrieve from the company workshop?", StringComparison.Ordinal)))
        {
            pendingContributionItemId = null;
            requestItemSelectionStarted = false;
            return new(true, "Retrieved finished workshop project.", IsProjectComplete: true);
        }

        return new(false, $"Workshop material contribution is not ready to confirm. {DescribeUiState()}");
    }

    public unsafe WorkshopAssemblyActionResult TryWaitForContributionProgress(
        WorkshopAssemblyQueueEntry entry,
        uint materialItemId,
        uint previousStepsComplete)
    {
        var materialDelivery = GetMaterialDeliveryAddon();
        var craftState = ReadCraftState(materialDelivery);
        if (craftState == null)
        {
            if (HasSelectStringEntry(IsPostContributionMenuEntry))
            {
                return new(
                    true,
                    $"Observed workshop menu after contributing material {materialItemId}.",
                    ActiveMaterialItemId: materialItemId,
                    ActiveMaterialStepsComplete: previousStepsComplete);
            }

            return new(false, $"Waiting for workshop material progress for {materialItemId}. {DescribeUiState()}");
        }

        if (craftState.ResultItem != entry.ResultItemId)
        {
            return new(
                false,
                $"Open workshop project result item {craftState.ResultItem} does not match queued project {entry.ProjectName} ({entry.ResultItemId}).",
                ActiveMaterialItemId: materialItemId,
                ActiveMaterialStepsComplete: previousStepsComplete);
        }

        var material = craftState.Items.FirstOrDefault(x => x.ItemId == materialItemId);
        if (material == null || material.Finished || material.StepsComplete > previousStepsComplete)
        {
            return new(
                true,
                $"Observed workshop material progress for {entry.ProjectName}.",
                ActiveMaterialItemId: materialItemId,
                ActiveMaterialStepsComplete: material?.StepsComplete);
        }

        return new(
            false,
            $"Waiting for workshop material {material.ItemName} to advance beyond {previousStepsComplete}/{material.StepsTotal}.",
            ActiveMaterialItemId: material.ItemId,
            ActiveMaterialStepsComplete: material.StepsComplete);
    }

    public unsafe string DescribeUiState()
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

        var selectStringEntries = DescribeSelectStringEntries();
        return selectStringEntries == null
            ? state
            : $"{state} SelectString entries: {selectStringEntries}.";
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, ContextIconMenuAddon, ContextIconMenuPostReceiveEvent);
        addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, RequestAddon, RequestPostRefresh);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, RequestAddon, RequestPostSetup);
    }

    internal static bool IsContributeItemsPrompt(string text)
    {
        return text.StartsWith("Contribute ", StringComparison.Ordinal) &&
               text.Contains(" to the company project?", StringComparison.Ordinal);
    }

    internal static bool IsContributeMaterialsEntry(string text)
    {
        return text.StartsWith("Contribute materials.", StringComparison.Ordinal);
    }

    internal static bool IsPostContributionMenuEntry(string text)
    {
        return IsContributeMaterialsEntry(text) ||
               text.StartsWith("Advance to the next phase of production.", StringComparison.Ordinal) ||
               text.StartsWith("Complete the construction of", StringComparison.Ordinal) ||
               text.StartsWith("Collect finished product.", StringComparison.Ordinal);
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

    private unsafe bool TrySelectString(Predicate<string> predicate)
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

    private unsafe bool HasSelectStringEntry(Predicate<string> predicate)
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

    private unsafe bool TrySelectYesNo(int choice, Predicate<string> predicate)
    {
        var addon = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesNoAddon, 1);
        if (addon == null || !IsAddonReady(&addon->AtkUnitBase))
            return false;

        var text = addon->PromptText->NodeText.ExtractText()
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal);
        if (!predicate(text))
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
            });
        return true;
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

    private static unsafe void SelectCraftCategory(AtkUnitBase* addon, WorkshopAssemblyQueueEntry entry)
    {
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

    private static unsafe void SelectCraft(AtkUnitBase* addon, WorkshopAssemblyQueueEntry entry)
    {
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

    private static unsafe bool HasItemInSingleSlot(uint itemId, uint count)
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

    private static unsafe void ContributeMaterial(
        AtkUnitBase* addon,
        int materialIndex,
        WorkshopCraftMaterialState item)
    {
        var values = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = 0 },
            new() { Type = AtkValueType.UInt, UInt = (uint)materialIndex },
            new() { Type = AtkValueType.UInt, UInt = item.ItemCountPerStep },
            new() { Type = 0, Int = 0 },
        };
        addon->FireCallback(4, values, true);
    }

    private static unsafe void CloseMaterialDelivery(AtkUnitBase* addon)
    {
        if (!IsAddonReady(addon))
            return;

        var values = stackalloc AtkValue[]
        {
            new() { Type = AtkValueType.Int, Int = -1 },
        };
        addon->FireCallback(1, values, true);
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
