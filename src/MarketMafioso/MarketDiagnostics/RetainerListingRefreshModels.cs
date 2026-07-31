using System;
using System.Collections.Generic;

namespace MarketMafioso;

[Serializable]
public sealed class PersistedRetainerListingRefreshState
{
    public string? LastObservedCaptureId { get; set; }
    // Retained only so existing typed plugin configuration can deserialize and be normalized.
    public bool CapturePending { get; set; }
    public DateTime? SessionStartedAtUtc { get; set; }
    public DateTime? SessionClosedAtUtc { get; set; }
    public DateTime? CaptureNotBeforeUtc { get; set; }
    public int CaptureAttempts { get; set; }
    public string? SessionSnapshotProviderInstanceId { get; set; }
    public long? SessionSnapshotRevision { get; set; }
    public DateTime? SessionListingsObservedAtUtc { get; set; }
    public List<PersistedRetainerListingRefreshCandidate> SessionListings { get; set; } = [];
    public List<PersistedRetainerListingRefreshItem> Items { get; set; } = [];
    public DateTime? LastCompletedAtUtc { get; set; }
    public string StatusCode { get; set; } = "Idle";
    public string StatusMessage { get; set; } = "No retainer listing refresh is pending.";
    public bool NeedsAttention { get; set; }
    public bool AttentionNotified { get; set; }
}

[Serializable]
public sealed class PersistedRetainerListingRefreshCandidate
{
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
}

public enum RetainerListingRefreshItemState
{
    Deferred,
    AwaitingEvidence,
    NeedsReconciliation,
    Blocked,
}

[Serializable]
public sealed class PersistedRetainerListingRefreshItem
{
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public RetainerListingRefreshItemState State { get; set; } = RetainerListingRefreshItemState.Deferred;
    public int Attempts { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public string? OperationId { get; set; }
    public string? LastCode { get; set; }
    public string? LastMessage { get; set; }
    public bool AttentionNotified { get; set; }
}
