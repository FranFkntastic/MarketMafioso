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

    /// <summary>The saved job this one was split from, when it is a delivered share.</summary>
    public Guid? SplitSourceJobId { get; set; }

    /// <summary>Character this share is handed to; null for ordinary saved jobs.</summary>
    public string? HandoffTargetName { get; set; }
    public string? HandoffTargetWorld { get; set; }

    /// <summary>The character that split this share off and may deliver or withdraw it.</summary>
    public string? HandoffSenderName { get; set; }

    public string? HandoffSenderWorld { get; set; }

    /// <summary>Workshop Host handoff id once the share was offered.</summary>
    public Guid? HandoffId { get; set; }

    /// <summary>Last handoff state seen, so the status survives while the host is unreachable.</summary>
    public WorkshopHandoffState? HandoffState { get; set; }

    /// <summary>Why delivery failed, shown in the inspector until a later offer succeeds.</summary>
    public string? HandoffError { get; set; }

    /// <summary>
    /// Sender only: material units verified as traded to this share's assembler, per item. Credited only
    /// from verified Trade Queue batches to the bound recipient; never reduced.
    /// </summary>
    public List<WorkshopMaterialLedgerEntry> HandedOver { get; set; } = new();

    /// <summary>
    /// Sender only: the assembler's holdings when the handover started. Frozen once the ledger has units, so
    /// later inventory reports (which include what was already handed over) are not subtracted twice.
    /// </summary>
    public List<WorkshopMaterialLedgerEntry>? HandoverBaseline { get; set; }

    public DateTimeOffset? HandoverBaselineCapturedAt { get; set; }

    /// <summary>Units the assembler needs from the sender: Σ max(0, Needed − TheyHold), as last reported.</summary>
    public int HandoverTotal { get; set; }

    /// <summary>True while the host has not acknowledged the current ledger and total; retried until it does.</summary>
    public bool HandoverReportPending { get; set; }
}

/// <summary>A quantity of one material item, stored with the saved job.</summary>
[Serializable]
public sealed class WorkshopMaterialLedgerEntry
{
    public uint ItemId { get; set; }
    public int Quantity { get; set; }
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
    /// <summary>Historical quantity, including uncovered sources; never a retrieval authorization.</summary>
    public int LastKnownRetainerStock { get; init; }
    /// <summary>When true, TotalMissing is an upper bound after verified stock, not proof of absence.</summary>
    public bool StockUnverified { get; init; }
    public string? RetrievalUnavailableReason { get; init; }
    public string? RetrievalNotice => RetrievalUnavailableReason ??
        (QuartermasterStock > RetrievableStock
            ? $"{QuartermasterStock - RetrievableStock:N0} verified on retainers but not covered by Quartermaster's current snapshot."
            : null);
}

public sealed record QuartermasterRetainerCandidate(
    ulong RetainerId,
    string RetainerName,
    DateTimeOffset ObservedAtUtc,
    int Quantity)
{
    public bool StockVerified { get; init; } = true;
}
