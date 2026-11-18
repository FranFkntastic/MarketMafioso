using System;
using System.Collections.Generic;
using System.Linq;

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

    /// <summary>Where the job came from. Configs written before this field load as <see cref="WorkshopSavedJobOrigin.Manual"/>.</summary>
    public WorkshopSavedJobOrigin Origin { get; set; }

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
    /// Sender only: the assembler's bag holdings when the handover started (legacy unsplit values stay as-is). Frozen once the ledger has units, so
    /// later inventory reports (which include what was already handed over) are not subtracted twice.
    /// </summary>
    public List<WorkshopMaterialLedgerEntry>? HandoverBaseline { get; set; }

    /// <summary>Observed retainer holdings at the same baseline; null for legacy unsplit baselines.</summary>
    public List<WorkshopMaterialLedgerEntry>? HandoverRetainerBaseline { get; set; }

    public DateTimeOffset? HandoverBaselineCapturedAt { get; set; }

    /// <summary>Units the assembler needs from the sender: Σ max(0, Needed − TheyHold), as last reported.</summary>
    public int HandoverTotal { get; set; }

    /// <summary>
    /// Sender only: per-item units owed before any handover, max(0, Needed − TheyHold-if-counted), kept in step with
    /// <see cref="HandoverTotal"/>. Progress and host reports cap each item's handed units at its due.
    /// </summary>
    public List<WorkshopMaterialLedgerEntry>? HandoverDue { get; set; }

    /// <summary>True while the host has not acknowledged the current ledger and total; retried until it does.</summary>
    public bool HandoverReportPending { get; set; }

    /// <summary>Why the host permanently refused the last handover report (HTTP 404/409); cleared by a later success.</summary>
    public string? HandoverReportRefusal { get; set; }

    /// <summary>
    /// Sender only: the Trade Queue batch started for this share but not yet verified. Cleared when the batch is
    /// verified and credited; otherwise resolved by the user (count it or discard it). Never auto-credited.
    /// </summary>
    public WorkshopHandoverInFlight? HandoverInFlight { get; set; }
}

public enum WorkshopSavedJobOrigin
{
    /// <summary>Saved, imported or accepted by hand.</summary>
    Manual = 0,

    /// <summary>Sent by another plugin over the saved-job IPC channel; removed automatically once finished.</summary>
    MarketRun = 1,
}

/// <summary>A bound Trade Queue batch that was started but not verified: its material lines and start time.</summary>
[Serializable]
public sealed class WorkshopHandoverInFlight
{
    public List<WorkshopMaterialLedgerEntry> Items { get; set; } = new();
    public DateTimeOffset StartedAt { get; set; }
    public string Recipient { get; set; } = string.Empty;

    /// <summary>True once this client was about to click Ready on the batch; only then can it have traded.</summary>
    public bool Locked { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public int Units => Items.Sum(item => Math.Max(0, item.Quantity));
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
