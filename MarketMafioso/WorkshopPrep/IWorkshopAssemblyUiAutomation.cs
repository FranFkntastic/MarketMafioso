using System;

namespace MarketMafioso.WorkshopPrep;

public interface IWorkshopAssemblyUiAutomation : IDisposable
{
    WorkshopAssemblyDiagnostics Diagnostics { get; set; }

    bool IsFabricationStationUiReady();

    WorkshopAssemblyActionResult TryOpenFabricationStation();

    WorkshopAssemblyActionResult TryOpenProject(WorkshopAssemblyQueueEntry entry);

    WorkshopAssemblyActionResult TrySubmitNextMaterial(WorkshopAssemblyQueueEntry entry);

    WorkshopAssemblyActionResult TryConfirmContribution();

    WorkshopAssemblyActionResult TryWaitForContributionProgress(
        WorkshopAssemblyQueueEntry entry,
        uint materialItemId,
        uint previousStepsComplete);

    string DescribeUiState();
}
