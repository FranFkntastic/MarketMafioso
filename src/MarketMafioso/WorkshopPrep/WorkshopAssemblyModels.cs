using System;
using System.Collections.Generic;

namespace MarketMafioso.WorkshopPrep;

public enum WorkshopAssemblyOrder
{
    Sequential,
    RoundRobin,
}

public enum WorkshopAssemblyRunnerState
{
    Idle,
    Preflight,
    WaitingForFabricationStation,
    OpeningProject,
    WaitingForMaterialRequest,
    SubmittingMaterial,
    WaitingForContributionLockout,
    ConfirmingContribution,
    AdvancingProject,
    Paused,
    Complete,
    Stopped,
    Failed,
    PreparingMaterials,
    // The retrieval prompt was confirmed; the unit counts only once it is in the bags.
    RetrievingProduct,
}

internal enum WorkshopAssemblyPendingConfirmationKind
{
    None,
    ProjectStart,
    MaterialContribution,
    PhaseAdvance,
    FinalConstruction,
    ProductRetrieval,
}

public sealed record WorkshopAssemblyQueueEntry(
    uint WorkshopItemId,
    uint ResultItemId,
    uint CategoryId,
    uint TypeId,
    string ProjectName,
    int Quantity,
    IReadOnlyList<WorkshopMaterialRequirement> Materials,
    int EstimatedContributionSteps,
    int EstimatedPhaseCount,
    uint DraftId = WorkshopDraftUnlocks.NoDraft,
    string? DraftName = null)
{
    public IReadOnlyList<WorkshopContributionBatch> ContributionSchedule { get; init; } = [];
}

public sealed record WorkshopContributionBatch(uint ItemId, string ItemName, int Quantity, int Count);

public enum WorkshopMaterialBlocker { None, InventoryUnknown, Shortage, Fragmented, NativePreparationUnavailable, MergeRefused }

public sealed record WorkshopAssemblyPlan(
    IReadOnlyList<WorkshopAssemblyQueueEntry> Entries,
    IReadOnlyList<WorkshopMaterialRequirement> TotalMaterials);

public sealed record WorkshopAssemblyProgress(
    WorkshopAssemblyRunnerState State,
    string Message,
    string? ActiveProjectName,
    uint? ActiveWorkshopItemId,
    uint? ActiveMaterialItemId,
    int CompletedProjects,
    int TotalProjects,
    DateTimeOffset UpdatedAt);

// Project-level AtkValues 0/6/7, not a material row's contribution counter.
public sealed record WorkshopAssemblyPhaseContext(uint ResultItemId, uint Phase, uint PhaseTotal);

public sealed record WorkshopAssemblyActionResult(
    bool Success,
    string Message,
    bool ActionTaken = false,
    bool RequiresWorkshopReopen = false,
    bool IsProjectComplete = false,
    bool IsContributionConfirmed = false,
    bool HasPendingMaterialRequest = false,
    uint? ActiveMaterialItemId = null,
    uint? ActiveMaterialStepsComplete = null,
    bool IsProjectUnavailable = false,
    bool IsWaiting = false,
    WorkshopMaterialBlocker MaterialBlocker = WorkshopMaterialBlocker.None,
    WorkshopAssemblyPhaseContext? ContributionPhase = null,
    uint? LiveResultItemId = null,
    // Retrieval was confirmed but the product is not yet proven to be in the bags.
    bool IsAwaitingProduct = false,
    // The bags cannot take the finished product, so it stays at the station.
    bool IsInventoryFull = false);

public sealed record WorkshopAssemblyPreflightResult(
    bool CanStart,
    string Message,
    WorkshopAssemblyPlan? Plan);
