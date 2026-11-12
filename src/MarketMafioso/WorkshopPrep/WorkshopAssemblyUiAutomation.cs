using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using MarketMafioso.Automation.Runtime;
using Franthropy.Dalamud.Automation.Inventory;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopAssemblyUiAutomation : IWorkshopAssemblyUiAutomation
{
    internal static readonly IReadOnlyList<string> MaterialDeliveryAddonNames =
        WorkshopAssemblyUiDriver.MaterialDeliveryAddonNames;

    private readonly WorkshopAssemblyUiDriver uiDriver;
    private readonly WorkshopNativeFaultMonitor faultMonitor;
    private readonly ExternalAutomationCoordinator externalAutomationCoordinator;
    private readonly IPluginLog log;
    private bool cutsceneSkipRequired;
    private bool runActive;
    private readonly WorkshopAssemblyInventoryPreparation preparation;
    private WorkshopCraftState? preparationCraft;
    private bool preparing;
    private string? preparationExit;
    private WorkshopAssemblyDiagnostics diagnostics = WorkshopAssemblyDiagnostics.Disabled;
    private uint? pendingContributionItemId;
    private WorkshopAssemblyPendingConfirmationKind pendingConfirmationKind;
    private uint? categorySelectedForItemId;
    private long categorySelectedAtMs;
    private int categorySelectAttempts;
    private const int MaxCategorySelectAttempts = 2;

    public WorkshopAssemblyUiAutomation(
        IGameGui gameGui,
        IPluginLog log,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition,
        ISigScanner sigScanner,
        IAgentLifecycle agentLifecycle,
        ExternalAutomationCoordinator externalAutomationCoordinator,
        WorkshopNativeFaultMonitor faultMonitor,
        IGameInteropProvider interopProvider,
        IDataManager dataManager)
    {
        this.log = log;
        this.faultMonitor = faultMonitor;
        this.externalAutomationCoordinator = externalAutomationCoordinator;
        preparation = new(new DalamudInventoryMover(sigScanner, interopProvider, dataManager), dataManager);
        uiDriver = new WorkshopAssemblyUiDriver(
            gameGui,
            log,
            objectTable,
            targetManager,
            condition,
            sigScanner,
            agentLifecycle,
            externalAutomationCoordinator,
            faultMonitor,
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

    public void BeginPreparationRun() => preparation.BeginRun();
    public void NotePreparationProgress() => preparation.NoteProgress();

    public void ResetState()
    {
        preparation.Suspend();
        preparationCraft = null;
        preparing = false;
        preparationExit = null;
        pendingContributionItemId = null;
        pendingConfirmationKind = WorkshopAssemblyPendingConfirmationKind.None;
        categorySelectedForItemId = null;
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
        var state = uiDriver.TrySkipCutscene();
        if (state == WorkshopCutsceneSkipState.CutsceneActive && cutsceneSkipRequired)
        {
            // TextAdvance does the skipping. Lift MMF's own hand-in pause on it and
            // re-assert control in case TextAdvance reloaded mid-run; waiting the
            // cutscene out instead is not acceptable, so losing control ends the run.
            externalAutomationCoordinator.RestoreTextAdvance();
            var reason = externalAutomationCoordinator.AcquireTextAdvanceCutsceneSkip();
            if (reason != null)
                throw new InvalidOperationException($"TextAdvance stopped skipping workshop cutscenes: {reason}");
        }

        return state switch
        {
            WorkshopCutsceneSkipState.CutsceneActive =>
                new(false, "Waiting for TextAdvance to skip the workshop cutscene.", ActionTaken: true, RequiresWorkshopReopen: true, IsWaiting: true),
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
            WorkshopFabricationStationOpenState.CharacterBusy =>
                new(false, $"Waiting: the character is occupied in an event with no workshop window open, so the station was not touched. {DescribeUiState()}"),
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
                categorySelectedForItemId = null;
                uiDriver.SelectCraft(craftingLog, entry);
                Diagnostics.Record(
                    "select-project",
                    "Selected visible workshop project.",
                    new Dictionary<string, string?>
                    {
                        ["project"] = entry.ProjectName,
                        ["workshopItemId"] = entry.WorkshopItemId.ToString(),
                    });
                SetPendingConfirmation(WorkshopAssemblyPendingConfirmationKind.ProjectStart, $"selected workshop project {entry.ProjectName}");
                return new(false, $"Selected workshop project {entry.ProjectName}.", ActionTaken: true);
            }

            if (entry.CategoryId == 0 || entry.TypeId == 0)
                return new(false, $"Workshop project {entry.ProjectName} cannot be selected because category/type data is missing. {DescribeUiState()}");

            var unavailable = CheckProjectAvailable(craftingLog, entry);
            if (unavailable != null)
                return unavailable;

            uiDriver.SelectCraftCategory(craftingLog, entry);
            categorySelectedForItemId = entry.WorkshopItemId;
            categorySelectedAtMs = Environment.TickCount64;
            categorySelectAttempts++;
            Diagnostics.Record(
                "select-category",
                "Selected workshop category/type.",
                new Dictionary<string, string?>
                {
                    ["project"] = entry.ProjectName,
                    ["categoryId"] = entry.CategoryId.ToString(),
                    ["typeId"] = entry.TypeId.ToString(),
                });
            return new(false, $"Selected workshop category/type for {entry.ProjectName}.", ActionTaken: true);
        }

        if (uiDriver.IsCraftingLogVisible)
            return new(false, $"Waiting for the workshop crafting log to load before opening {entry.ProjectName}. {DescribeUiState()}");

        if (uiDriver.TrySelectString(text => text == WorkshopAssemblyPromptPolicy.ViewCraftingLogEntry))
            return new(false, "Selected company crafting log.", ActionTaken: true);

        return new(false, $"Workshop project {entry.ProjectName} cannot be opened. {DescribeUiState()}");
    }

    // Selecting a category whose schematic the FC lacks crashes the game's
    // crafting log handler, so locked projects are refused before any callback.
    // Returns null when the category may be selected now.
    private WorkshopAssemblyActionResult? CheckProjectAvailable(
        WorkshopCraftingLogSnapshot craftingLog,
        WorkshopAssemblyQueueEntry entry)
    {
        var draftName = string.IsNullOrWhiteSpace(entry.DraftName) ? $"draft {entry.DraftId}" : entry.DraftName;
        if (categorySelectedForItemId == entry.WorkshopItemId)
        {
            if (Environment.TickCount64 - categorySelectedAtMs < (long)WorkshopAssemblyTiming.CategoryListingTimeout.TotalMilliseconds)
                return new(false, $"Waiting for {entry.ProjectName} to appear in the company crafting log.");

            // One retry covers a select the game dropped; after that, re-selecting
            // would only loop until the repeat guard halts the whole run.
            if (categorySelectAttempts >= MaxCategorySelectAttempts)
            {
                categorySelectedForItemId = null;
                Diagnostics.Record(
                    "project-not-listed",
                    "Workshop project is not listed after selecting its category.",
                    new Dictionary<string, string?>
                    {
                        ["project"] = entry.ProjectName,
                        ["workshopItemId"] = entry.WorkshopItemId.ToString(),
                        ["attempts"] = categorySelectAttempts.ToString(),
                        ["visibleItems"] = string.Join(",", craftingLog.VisibleItems.Select(x => x.WorkshopItemId)),
                    });
                return new(
                    false,
                    $"Skipping {entry.ProjectName}: it isn't listed in the company crafting log after selecting its category (the Free Company may not have the {draftName} schematic).",
                    IsProjectUnavailable: true);
            }
        }
        else
        {
            categorySelectAttempts = 0;
        }

        if (entry.DraftId == WorkshopDraftUnlocks.NoDraft)
            return null;

        var unlocks = uiDriver.ReadDraftUnlocks(craftingLog);
        var unlocked = unlocks?.IsUnlocked(entry.DraftId);
        Diagnostics.Record(
            "draft-check",
            unlocked switch
            {
                true => "The Free Company has unlocked this project's schematic.",
                false => "The Free Company has not unlocked this project's schematic.",
                null => "This project's schematic unlock could not be verified; proceeding under the crash guard.",
            },
            new Dictionary<string, string?>
            {
                ["project"] = entry.ProjectName,
                ["draftId"] = entry.DraftId.ToString(),
                ["draftName"] = entry.DraftName,
                ["unlockMask"] = unlocks?.Mask.ToString("X16"),
            });

        if (unlocked != false)
            return null;

        categorySelectedForItemId = null;
        return new(
            false,
            $"Skipping {entry.ProjectName}: the Free Company hasn't unlocked the {draftName} schematic.",
            IsProjectUnavailable: true);
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

        if (pendingContributionItemId != null)
        {
            var turnIn = uiDriver.TryAdvanceMaterialRequest();
            return new(
                false,
                turnIn.ActionTaken
                    ? turnIn.Message
                    : $"{turnIn.Message} {DescribeUiState()}",
                ActionTaken: turnIn.ActionTaken,
                HasPendingMaterialRequest: true,
                ActiveMaterialItemId: pendingContributionItemId);
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

            var inventory = WorkshopAssemblyInventoryPreparation.CheckContribution(item.ItemId, item.ItemName, item.ItemCountPerStep);
            if (!inventory.Success)
            {
                if (inventory.MaterialBlocker == WorkshopMaterialBlocker.Fragmented) preparation.Require(item);
                return inventory;
            }

            uiDriver.BeginMaterialContribution(materialDelivery, index, item);
            pendingContributionItemId = item.ItemId;
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
            return new(
                false,
                $"Submitted workshop material request for {item.ItemCountPerStep}x {item.ItemName}.",
                ActionTaken: true,
                ActiveMaterialItemId: item.ItemId,
                ActiveMaterialStepsComplete: item.StepsComplete);
        }

        return new(false, $"No unfinished queued material was found for {entry.ProjectName}. {DescribeUiState()}");
    }

    public WorkshopAssemblyActionResult TryPrepareMaterials(IReadOnlyList<WorkshopAssemblyQueueEntry> remainingEntries)
    {
        if (!runActive)
            return new(false, "Assembly no longer owns inventory preparation.");
        if (!preparing)
        {
            preparationCraft = uiDriver.ReadMaterialDelivery()?.CraftState;
            // Read-only evaluation first: suitable stock must not cause an
            // unnecessary station exit, hook installation, or inventory move.
            var preview = preparation.Evaluate(remainingEntries, preparationCraft, Diagnostics, () => false);
            if (preview.MaterialBlocker != WorkshopMaterialBlocker.Fragmented)
                return preview;
            var availability = preparation.Preflight();
            if (!availability.Success) return availability;
            preparation.BeginRecovery(string.Join(";", remainingEntries.Select(x => $"{x.WorkshopItemId}:{x.Quantity}")) +
                (preparationCraft == null ? "" : $"|{preparationCraft.ResultItem}:{preparationCraft.StepsComplete}:" +
                    string.Join(",", preparationCraft.Items.Select(x => $"{x.ItemId}:{x.ItemCountPerStep}:{x.StepsComplete}:{x.StepsTotal}"))));
            preparing = true;
            preparationExit = null;
        }
        if (pendingContributionItemId != null)
        {
            if (pendingConfirmationKind == WorkshopAssemblyPendingConfirmationKind.MaterialContribution &&
                uiDriver.TryGetSelectYesNoPrompt(out var prompt) &&
                WorkshopAssemblyPromptPolicy.IsPromptAllowedForPendingConfirmation(pendingConfirmationKind, prompt) &&
                uiDriver.TrySelectYesNo(1, prompt, pendingConfirmationKind))
                return PreparationWait("Declined interrupted material confirmation; will reread native progress after recovery.", true);
            // A paused request may still be open. Cancel its UI, then reread
            // authoritative project progress on reentry; never infer a hand-in.
            RecoverStalledMaterialRequest();
            return PreparationWait("Closing interrupted material request before inventory recovery.", true);
        }
        var delivery = uiDriver.ReadMaterialDelivery();
        if (delivery != null)
        {
            if (preparationExit == "material") return PreparationWait("Waiting for material delivery to close.");
            preparationCraft = delivery.CraftState;
            preparationExit = "material";
            uiDriver.CloseMaterialDelivery(delivery);
            return PreparationWait("Leaving material delivery before preparing all assembly materials.", true);
        }
        if (uiDriver.IsCraftingLogVisible)
        {
            if (preparationExit == "log") return PreparationWait("Waiting for company crafting log to close.");
            preparationExit = "log";
            CloseCraftingLog();
            return PreparationWait("Closing company crafting log for inventory preparation.", true);
        }
        if (preparationExit == "station" && uiDriver.IsSelectStringVisible)
            return PreparationWait("Waiting for fabrication station dismissal.");
        if (uiDriver.TryLeaveStationForPreparation())
        {
            preparationExit = "station";
            return PreparationWait("Leaving fabrication station for batched inventory preparation.", true);
        }
        if (!DalamudInventoryMover.CanMove)
            return PreparationWait("Waiting for native inventory movement permission after station exit.");
        var result = preparation.Evaluate(remainingEntries, preparationCraft, Diagnostics, () => runActive);
        if (result.Success)
        {
            preparing = false;
            preparationCraft = null;
        }
        return result;
    }

    private static WorkshopAssemblyActionResult PreparationWait(string message, bool action = false) =>
        new(false, message, ActionTaken: action, IsWaiting: true, MaterialBlocker: WorkshopMaterialBlocker.InventoryUnknown);

    public WorkshopAssemblyActionResult RecoverStalledMaterialRequest()
    {
        var itemId = pendingContributionItemId;
        var recovery = uiDriver.RecoverStalledMaterialRequest();
        pendingContributionItemId = null;
        pendingConfirmationKind = WorkshopAssemblyPendingConfirmationKind.None;
        return new(
            true,
            $"{recovery.Message} Reopening the fabrication station to reread authoritative workshop progress.",
            ActionTaken: recovery.RequestPresent || recovery.ContextMenuPresent,
            RequiresWorkshopReopen: true,
            ActiveMaterialItemId: itemId);
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
            if (uiDriver.HasSelectStringEntry(text =>
                    WorkshopAssemblyPromptPolicy.IsAdvancePhaseEntry(text) ||
                    WorkshopAssemblyPromptPolicy.IsCompleteConstructionEntry(text) ||
                    WorkshopAssemblyPromptPolicy.IsCollectFinishedProductEntry(text)))
            {
                // These actions are offered only after every contribution in
                // the phase has completed. Unlike the general contribute menu,
                // they establish progress without reopening material delivery.
                return new(true, $"Observed completed workshop phase for {entry.ProjectName}.",
                    ActiveMaterialItemId: materialItemId);
            }
            if (uiDriver.HasSelectStringEntry(WorkshopAssemblyPromptPolicy.IsPostContributionMenuEntry))
            {
                return new(
                    false,
                    $"Workshop menu observed; reopening material delivery to verify contribution {materialItemId} progress.",
                    RequiresWorkshopReopen: true,
                    ActiveMaterialItemId: materialItemId,
                    ActiveMaterialStepsComplete: previousStepsComplete);
            }

            return new(
                false,
                $"Workshop UI closed while waiting for material {materialItemId} progress; reopening the fabrication station. {DescribeUiState()}",
                RequiresWorkshopReopen: true,
                ActiveMaterialItemId: materialItemId,
                ActiveMaterialStepsComplete: previousStepsComplete);
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

    public string? BeginRun(bool requireCutsceneSkip)
    {
        if (requireCutsceneSkip)
        {
            var reason = externalAutomationCoordinator.AcquireTextAdvanceCutsceneSkip();
            if (reason != null)
                return $"Workshop assembly needs TextAdvance to skip the phase cutscenes, but {reason}";
        }

        cutsceneSkipRequired = requireCutsceneSkip;
        faultMonitor.Arm();
        runActive = true;
        return null;
    }

    public void EndRun()
    {
        runActive = false;
        preparation.Suspend();
        faultMonitor.Disarm();
        cutsceneSkipRequired = false;
        externalAutomationCoordinator.ReleaseTextAdvanceCutsceneSkip();
    }

    public string? PollNativeFault() => faultMonitor.Poll()?.Describe();

    public string? CheckPreflight()
    {
        var closed = uiDriver.CloseLeftoverHiddenWorkshopWindows();
        if (closed.Count > 0)
            log.Warning("[MarketMafioso] Closed hidden workshop windows left over from an earlier run: {Windows}", string.Join(", ", closed));

        if (uiDriver.IsCharacterBusyWithoutWorkshopUi())
        {
            return "The character is already occupied in an event with no workshop window open, so assembly was not started. " +
                   "Finish or cancel what the character is doing first; if it cannot move or log out, the client needs a restart. " +
                   DescribeUiState();
        }

        return null;
    }

    public string CaptureForensics() => uiDriver.CaptureForensics();

    public WorkshopAssemblyActionResult TryReachCraftingLog()
    {
        if (uiDriver.ReadCraftingLog() != null)
            return new(true, "The workshop crafting log is open.");

        if (uiDriver.IsCraftingLogVisible)
            return new(false, $"Waiting for the workshop crafting log to load. {DescribeUiState()}");

        if (uiDriver.TrySelectString(text => text == WorkshopAssemblyPromptPolicy.ViewCraftingLogEntry))
            return new(false, "Selected company crafting log.", ActionTaken: true);

        return new(false, $"Waiting for the workshop menu to offer the company crafting log. {DescribeUiState()}");
    }

    public string WriteCraftingLogInspection(IReadOnlyList<WorkshopAssemblyQueueEntry> entries)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine($"Workshop crafting log inspection {DateTimeOffset.Now:O}");
        report.AppendLine("queued projects:");
        foreach (var entry in entries.Where(x => x.WorkshopItemId != 0))
            report.AppendLine($"  {entry.WorkshopItemId} {entry.ProjectName}: draft={entry.DraftId} category={entry.CategoryId} type={entry.TypeId}");
        report.AppendLine();
        report.AppendLine(uiDriver.DescribeCraftingLogInternals());

        var path = Diagnostics.FilePath is { } logPath
            ? System.IO.Path.ChangeExtension(logPath, ".craftlog.txt")
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mmf-craftlog-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt");
        System.IO.File.WriteAllText(path, report.ToString());
        Diagnostics.Record("crafting-log-inspection", "Wrote workshop crafting log inspection.", new Dictionary<string, string?> { ["path"] = path });
        return path;
    }

    public bool CloseCraftingLog() => uiDriver.CloseCraftingLog();

    public WorkshopAssemblyActionResult TryDismissStationMenu()
    {
        if (uiDriver.IsCraftingLogVisible)
            return new(false, "Waiting for the company crafting log to close.");
        if (uiDriver.TryDismissIdleStationMenu())
            return new(true, "Closed the fabrication station menu.", ActionTaken: true);
        return uiDriver.IsSelectStringVisible
            ? new(false, "A workshop menu other than the idle station menu is open; leaving it alone.")
            : new(true, "No workshop menu is open.");
    }

    public string DescribeUiState()
    {
        return uiDriver.DescribeUiState(pendingConfirmationKind);
    }

    public void Dispose()
    {
        runActive = false;
        preparation.Dispose();
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
