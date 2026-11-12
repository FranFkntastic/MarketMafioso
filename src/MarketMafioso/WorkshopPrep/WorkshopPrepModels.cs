using System;
using System.Collections.Generic;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopPrepQueueItem
{
    public uint WorkshopItemId { get; set; }
    public int Quantity { get; set; }
}

[Serializable]
public sealed class WorkshopFrozenQueue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<WorkshopPrepQueueItem> Items { get; set; } = new();
}

public sealed record WorkshopProjectDefinition(
    uint WorkshopItemId,
    uint ResultItemId,
    string Name,
    ushort IconId,
    IReadOnlyList<WorkshopMaterialRequirement> Materials,
    uint CategoryId = 0,
    uint TypeId = 0,
    int EstimatedContributionSteps = 0,
    int EstimatedPhaseCount = 1,
    uint DraftId = WorkshopDraftUnlocks.NoDraft,
    string? DraftName = null)
{
    public IReadOnlyList<WorkshopContributionBatch> ContributionSchedule { get; init; } = [];
}

public sealed record WorkshopMaterialRequirement(
    uint ItemId,
    string ItemName,
    ushort IconId,
    int Quantity);

public sealed record WorkshopMaterialAvailability(
    uint ItemId,
    string ItemName,
    ushort IconId,
    int Required,
    int PlayerInventory,
    int QuartermasterStock,
    int Shortage,
    int TotalMissing,
    IReadOnlyList<QuartermasterRetainerCandidate> QuartermasterRetainers)
{
    public int StockDifferential => PlayerInventory + QuartermasterStock - Required;
    public int RetrievableStock { get; init; }
    public string? RetrievalUnavailableReason { get; init; }
    public string? RetrievalNotice => RetrievalUnavailableReason ??
        (QuartermasterStock > RetrievableStock
            ? $"{QuartermasterStock - RetrievableStock:N0} seen on retainers, not yet in Quartermaster's cache — visit the bell to refresh."
            : null);
}

public sealed record QuartermasterRetainerCandidate(
    ulong RetainerId,
    string RetainerName,
    DateTimeOffset ObservedAtUtc,
    int Quantity);
