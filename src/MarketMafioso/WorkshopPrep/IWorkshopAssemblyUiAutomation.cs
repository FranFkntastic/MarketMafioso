using System;

namespace MarketMafioso.WorkshopPrep;

public interface IWorkshopAssemblyUiAutomation : IDisposable
{
    void BeginPreparationRun() { }
    void NotePreparationProgress() { }
    WorkshopAssemblyDiagnostics Diagnostics { get; set; }

    void ResetState();

    bool IsFabricationStationUiReady();

    WorkshopAssemblyActionResult TrySkipCutscene();

    WorkshopAssemblyActionResult TryOpenFabricationStation();

    WorkshopAssemblyActionResult TryOpenProject(WorkshopAssemblyQueueEntry entry);

    WorkshopAssemblyActionResult TrySubmitNextMaterial(WorkshopAssemblyQueueEntry entry);

    WorkshopAssemblyActionResult TryPrepareMaterials(System.Collections.Generic.IReadOnlyList<WorkshopAssemblyQueueEntry> remainingEntries) =>
        new(false, "Assembly inventory preparation is unavailable from this automation provider.",
            MaterialBlocker: WorkshopMaterialBlocker.NativePreparationUnavailable);

    WorkshopAssemblyActionResult RecoverStalledMaterialRequest();

    WorkshopAssemblyActionResult TryConfirmContribution();

    WorkshopAssemblyActionResult TryWaitForContributionProgress(
        WorkshopAssemblyQueueEntry entry,
        uint materialItemId,
        uint previousStepsComplete);

    string DescribeUiState();

    // Arms the native-crash fuse for the duration of a run and, when asked, takes
    // TextAdvance control so phase cutscenes are skipped. Returns why the run
    // cannot begin (nothing is armed or held then), or null.
    string? BeginRun(bool requireCutsceneSkip);

    // Disarms the fuse and releases TextAdvance.
    void EndRun();

    // Non-null when the game crashed inside a UI handler since the last poll.
    string? PollNativeFault();

    // Non-null when assembly must not start (character already wedged, etc.).
    string? CheckPreflight();

    string CaptureForensics();

    // Crafting-log inspection: open the log without selecting anything, dump it.
    WorkshopAssemblyActionResult TryReachCraftingLog();

    string WriteCraftingLogInspection(System.Collections.Generic.IReadOnlyList<WorkshopAssemblyQueueEntry> entries);

    bool CloseCraftingLog();

    // End-of-run tidy-up: answers "Nothing." on the idle fabrication station menu.
    // ActionTaken = dismissed it; Success = no workshop window left to close.
    WorkshopAssemblyActionResult TryDismissStationMenu();
}
