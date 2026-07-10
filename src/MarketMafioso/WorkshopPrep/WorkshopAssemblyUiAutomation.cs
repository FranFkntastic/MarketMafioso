using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using MarketMafioso.Automation.Runtime;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopAssemblyUiAutomation : IWorkshopAssemblyUiAutomation
{
    internal static readonly IReadOnlyList<string> MaterialDeliveryAddonNames =
        WorkshopAssemblyUiDriver.MaterialDeliveryAddonNames;

    private readonly WorkshopAssemblyUiDriver uiDriver;
    private WorkshopAssemblyDiagnostics diagnostics = WorkshopAssemblyDiagnostics.Disabled;
    private uint? pendingContributionItemId;
    private WorkshopAssemblyPendingConfirmationKind pendingConfirmationKind;

    public WorkshopAssemblyUiAutomation(
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        IPluginLog log,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition,
        ExternalAutomationCoordinator externalAutomationCoordinator)
    {
        uiDriver = new WorkshopAssemblyUiDriver(
            gameGui,
            addonLifecycle,
            log,
            objectTable,
            targetManager,
            condition,
            externalAutomationCoordinator,
            source => SetPendingConfirmation(WorkshopAssemblyPendingConfirmationKind.MaterialContribution, source));
    }

    public WorkshopAssemblyDiagnostics Diagnostics
    {
        get => diagnostics;
        set
        {
            diagnostics = value;
            uiDriver.Diagnostics = value;
        }
    }

    public void ResetState()
    {
        pendingContributionItemId = null;
        pendingConfirmationKind = WorkshopAssemblyPendingConfirmationKind.None;
        uiDriver.ResetRequestState();
    }

    public bool IsFabricationStationUiReady()
    {
        var isReady = uiDriver.IsFabricationStationUiReady(
            pendingConfirmationKind != WorkshopAssemblyPendingConfirmationKind.None);
        Diagnostics.Record("ui-ready-check", isReady ? "Fabrication station UI is ready." : "Fabrication station UI is not ready.");
        return isReady;
    }

    public WorkshopAssemblyActionResult TrySkipCutscene()
    {
        return uiDriver.TrySkipCutscene() switch
        {
            WorkshopCutsceneSkipState.CutsceneActive =>
                new(false, "Waiting for workshop cutscene skip prompt.", ActionTaken: true, RequiresWorkshopReopen: true),
            WorkshopCutsceneSkipState.PromptSelected =>
                new(false, "Selected workshop cutscene skip prompt.", ActionTaken: true, RequiresWorkshopReopen: true),
            _ => new(false, "No skippable workshop cutscene prompt is visible."),
        };
    }

    public WorkshopAssemblyActionResult TryOpenFabricationStation()
    {
        var result = uiDriver.TryOpenFabricationStation();
        return result.State switch
        {
            WorkshopFabricationStationOpenState.TargetSystemUnavailable =>
                new(false, $"Waiting for fabrication station UI. Target system is unavailable. {DescribeUiState()}"),
            WorkshopFabricationStationOpenState.Interacted =>
                new(false, $"Opened nearby fabrication station {result.StationName}.", ActionTaken: true),
            _ => new(false, $"Waiting for fabrication station UI. No nearby fabrication station target was found. {DescribeUiState()}"),
        };
    }

    public WorkshopAssemblyActionResult TryOpenProject(WorkshopAssemblyQueueEntry entry)
    {
        var activeCraft = uiDriver.ReadMaterialDelivery()?.CraftState;
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

        var confirmation = TryConfirmPendingConfirmation();
        if (confirmation != null)
            return confirmation;

        if (pendingConfirmationKind != WorkshopAssemblyPendingConfirmationKind.None)
            return new(false, $"Waiting for {pendingConfirmationKind} confirmation. {DescribeUiState()}");

        var activeProjectAction = TrySelectActiveProjectAction(entry);
        if (activeProjectAction != null)
            return activeProjectAction;

        var craftingLog = uiDriver.ReadCraftingLog();
        if (craftingLog != null)
        {
            if (craftingLog.VisibleItems.Any(x => x.WorkshopItemId == entry.WorkshopItemId))
            {
                Diagnostics.Record(
                    "select-project",
                    "Selected visible workshop project.",
                    new Dictionary<string, string?>
                    {
                        ["project"] = entry.ProjectName,
                        ["workshopItemId"] = entry.WorkshopItemId.ToString(),
                    });
                uiDriver.SelectCraft(craftingLog, entry);
                SetPendingConfirmation(WorkshopAssemblyPendingConfirmationKind.ProjectStart, $"selected workshop project {entry.ProjectName}");
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
            uiDriver.SelectCraftCategory(craftingLog, entry);
            return new(false, $"Selected workshop category/type for {entry.ProjectName}.", ActionTaken: true);
        }

        if (uiDriver.TrySelectString(text => text == "View company crafting log."))
            return new(false, "Selected company crafting log.", ActionTaken: true);

        return new(false, $"Workshop project {entry.ProjectName} cannot be opened. {DescribeUiState()}");
    }

    private WorkshopAssemblyActionResult? TrySelectActiveProjectAction(WorkshopAssemblyQueueEntry entry)
    {
        if (uiDriver.TrySelectString(WorkshopAssemblyPromptPolicy.IsContributeMaterialsEntry))
            return new(true, $"Selected active workshop material contribution for {entry.ProjectName}.", ActionTaken: true);

        if (uiDriver.TrySelectString(WorkshopAssemblyPromptPolicy.IsAdvancePhaseEntry))
            return new(false, $"Advanced workshop project phase for {entry.ProjectName}.", ActionTaken: true, RequiresWorkshopReopen: true);

        if (uiDriver.TrySelectString(WorkshopAssemblyPromptPolicy.IsCompleteConstructionEntry))
            return new(false, $"Selected final construction step for {entry.ProjectName}.", ActionTaken: true, RequiresWorkshopReopen: true);

        if (uiDriver.TrySelectString(WorkshopAssemblyPromptPolicy.IsCollectFinishedProductEntry))
        {
            SetPendingConfirmation(WorkshopAssemblyPendingConfirmationKind.ProductRetrieval, $"selected finished product collection for {entry.ProjectName}");
            return new(true, $"Selected finished product collection for {entry.ProjectName}.", ActionTaken: true);
        }

        return null;
    }

    public WorkshopAssemblyActionResult TrySubmitNextMaterial(WorkshopAssemblyQueueEntry entry)
    {
        var confirmation = TryConfirmPendingConfirmation();
        if (confirmation != null)
            return confirmation;

        if (pendingConfirmationKind != WorkshopAssemblyPendingConfirmationKind.None)
            return new(false, $"Waiting for {pendingConfirmationKind} confirmation. {DescribeUiState()}");

        if (uiDriver.TryConsumeRequestConfirmed())
        {
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

        if (uiDriver.TrySelectString(WorkshopAssemblyPromptPolicy.IsCollectFinishedProductEntry))
        {
            SetPendingConfirmation(WorkshopAssemblyPendingConfirmationKind.ProductRetrieval, $"selected finished product collection for {entry.ProjectName}");
            return new(false, $"Selected finished product collection for {entry.ProjectName}.", ActionTaken: true);
        }

        if (uiDriver.TrySelectString(WorkshopAssemblyPromptPolicy.IsCompleteConstructionEntry))
            return new(false, $"Selected final construction step for {entry.ProjectName}.", ActionTaken: true, RequiresWorkshopReopen: true);

        if (uiDriver.TrySelectString(WorkshopAssemblyPromptPolicy.IsAdvancePhaseEntry))
            return new(false, $"Advanced workshop project phase for {entry.ProjectName}.", ActionTaken: true, RequiresWorkshopReopen: true);

        if (uiDriver.TrySelectString(WorkshopAssemblyPromptPolicy.IsContributeMaterialsEntry))
            return new(false, $"Selected material contribution for {entry.ProjectName}.", ActionTaken: true);

        var materialDelivery = uiDriver.ReadMaterialDelivery();
        if (materialDelivery == null)
            return new(false, $"Workshop material request is not actionable for {entry.ProjectName}. {DescribeUiState()}");

        var craftState = materialDelivery.CraftState;

        if (craftState.ResultItem != entry.ResultItemId)
        {
            return new(
                false,
                $"Open workshop project result item {craftState.ResultItem} does not match queued project {entry.ProjectName} ({entry.ResultItemId}).");
        }

        if (craftState.IsPhaseComplete())
        {
            uiDriver.CloseMaterialDelivery(materialDelivery);
            return new(false, $"Closed completed material phase for {entry.ProjectName}.", ActionTaken: true);
        }

        var requiredMaterialIds = entry.Materials.Select(x => x.ItemId).ToHashSet();
        for (var index = 0; index < craftState.Items.Count; index++)
        {
            var item = craftState.Items[index];
            if (item.Finished || item.StepsComplete >= item.StepsTotal || !requiredMaterialIds.Contains(item.ItemId))
                continue;

            if (!WorkshopAssemblyUiDriver.HasItemInSingleSlot(item.ItemId, item.ItemCountPerStep))
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
            pendingContributionItemId = item.ItemId;
            uiDriver.BeginMaterialContribution(materialDelivery, index, item);
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
        var confirmation = TryConfirmPendingConfirmation();
        if (confirmation != null)
            return confirmation;

        return new(false, $"Workshop material contribution is not ready to confirm. {DescribeUiState()}");
    }

    private WorkshopAssemblyActionResult? TryConfirmPendingConfirmation()
    {
        if (pendingConfirmationKind == WorkshopAssemblyPendingConfirmationKind.None)
            return null;

        if (!uiDriver.TryGetSelectYesNoPrompt(out var text))
            return null;

        if (!WorkshopAssemblyPromptPolicy.IsPromptAllowedForPendingConfirmation(pendingConfirmationKind, text))
        {
            return new(
                false,
                $"Waiting for {pendingConfirmationKind} confirmation, but the visible SelectYesno prompt is not recognized for that action: {text}. {DescribeUiState()}");
        }

        var kind = pendingConfirmationKind;
        uiDriver.TrySelectYesNo(0, text, pendingConfirmationKind);

        switch (kind)
        {
            case WorkshopAssemblyPendingConfirmationKind.ProjectStart:
                ClearPendingConfirmation();
                return new(true, "Confirmed workshop project.", ActionTaken: true);

            case WorkshopAssemblyPendingConfirmationKind.MaterialContribution when WorkshopAssemblyPromptPolicy.IsHighQualityHandoffPrompt(text):
                return new(false, "Confirmed HQ workshop material handoff.", ActionTaken: true);

            case WorkshopAssemblyPendingConfirmationKind.MaterialContribution:
                {
                    var itemId = pendingContributionItemId;
                    pendingContributionItemId = null;
                    uiDriver.ClearMaterialRequest();
                    ClearPendingConfirmation();
                    return new(
                        true,
                        "Confirmed workshop material contribution.",
                        IsContributionConfirmed: true,
                        ActiveMaterialItemId: itemId);
                }

            case WorkshopAssemblyPendingConfirmationKind.PhaseAdvance:
                ClearPendingConfirmation();
                return new(false, "Confirmed workshop phase advance.", ActionTaken: true, RequiresWorkshopReopen: true);

            case WorkshopAssemblyPendingConfirmationKind.FinalConstruction:
                ClearPendingConfirmation();
                return new(false, "Confirmed workshop final construction.", ActionTaken: true, RequiresWorkshopReopen: true);

            case WorkshopAssemblyPendingConfirmationKind.ProductRetrieval:
                pendingContributionItemId = null;
                uiDriver.ClearMaterialRequest();
                ClearPendingConfirmation();
                return new(true, "Retrieved finished workshop project.", IsProjectComplete: true);

            default:
                return null;
        }
    }

    public WorkshopAssemblyActionResult TryWaitForContributionProgress(
        WorkshopAssemblyQueueEntry entry,
        uint materialItemId,
        uint previousStepsComplete)
    {
        var materialDelivery = uiDriver.ReadMaterialDelivery();
        if (materialDelivery == null)
        {
            if (uiDriver.HasSelectStringEntry(WorkshopAssemblyPromptPolicy.IsPostContributionMenuEntry))
            {
                return new(
                    true,
                    $"Observed workshop menu after contributing material {materialItemId}.",
                    ActiveMaterialItemId: materialItemId,
                    ActiveMaterialStepsComplete: previousStepsComplete);
            }

            return new(false, $"Waiting for workshop material progress for {materialItemId}. {DescribeUiState()}");
        }

        var craftState = materialDelivery.CraftState;

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

    public string DescribeUiState()
    {
        return uiDriver.DescribeUiState(pendingConfirmationKind);
    }

    public void Dispose()
    {
        uiDriver.Dispose();
    }

    private void SetPendingConfirmation(WorkshopAssemblyPendingConfirmationKind kind, string source)
    {
        pendingConfirmationKind = kind;
        Diagnostics.Record(
            "pending-confirmation",
            $"Waiting for {kind} confirmation.",
            new Dictionary<string, string?>
            {
                ["source"] = source,
            });
    }

    private void ClearPendingConfirmation()
    {
        pendingConfirmationKind = WorkshopAssemblyPendingConfirmationKind.None;
    }

}
