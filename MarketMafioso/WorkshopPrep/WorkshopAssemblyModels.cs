using System;
using System.Collections.Generic;

namespace MarketMafioso.WorkshopPrep;

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
    Complete,
    Stopped,
    Failed,
}

public sealed record WorkshopAssemblyQueueEntry(
    uint WorkshopItemId,
    uint ResultItemId,
    uint CategoryId,
    uint TypeId,
    string ProjectName,
    int Quantity,
    IReadOnlyList<WorkshopMaterialRequirement> Materials);

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

public sealed record WorkshopAssemblyActionResult(
    bool Success,
    string Message,
    bool ActionTaken = false,
    bool IsProjectComplete = false,
    bool IsContributionConfirmed = false,
    uint? ActiveMaterialItemId = null);

public sealed record WorkshopAssemblyPreflightResult(
    bool CanStart,
    string Message,
    WorkshopAssemblyPlan? Plan);
