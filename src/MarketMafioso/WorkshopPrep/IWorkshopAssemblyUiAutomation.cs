using System;

namespace MarketMafioso.WorkshopPrep;

public interface IWorkshopAssemblyUiAutomation : IDisposable
{
    WorkshopAssemblyDiagnostics Diagnostics { get; set; }

    void ResetState();

    bool IsFabricationStationUiReady();

    WorkshopAssemblyActionResult TrySkipCutscene();

    WorkshopAssemblyActionResult TryOpenFabricationStation();

    WorkshopAssemblyActionResult TryOpenProject(WorkshopAssemblyQueueEntry entry);

    WorkshopAssemblyActionResult TrySubmitNextMaterial(WorkshopAssemblyQueueEntry entry);

    WorkshopAssemblyActionResult RecoverStalledMaterialRequest();

    WorkshopAssemblyActionResult TryConfirmContribution();

    WorkshopAssemblyActionResult TryWaitForContributionProgress(
        WorkshopAssemblyQueueEntry entry,
        uint materialItemId,
        uint previousStepsComplete);

    string DescribeUiState();

    // Arms the native-crash fuse for the duration of a run.
    void BeginRun();

    void EndRun();

    // Non-null when the game crashed inside a UI handler since the last poll.
    string? PollNativeFault();

    // Non-null when assembly must not start (character already wedged, etc.).
    string? CheckPreflight();

    string CaptureForensics();
}
