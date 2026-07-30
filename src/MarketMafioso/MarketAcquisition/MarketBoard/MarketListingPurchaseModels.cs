using System;

namespace MarketMafioso.MarketAcquisition.MarketBoard;

internal enum MarketListingPurchasePhase
{
    AwaitingConfirmation,
    Sending,
    Sent,
    Confirmed,
    Failed,
    Cancelled,
    Conflicted,
    Indeterminate,
}

internal enum MarketListingBatchStatus
{
    Queued,
    Sending,
    Confirmed,
    Failed,
    Skipped,
}

internal sealed class MarketListingBatchItem(
    ulong listingId,
    MarketListingSelection selection,
    MarketListingBatchStatus status)
{
    public ulong ListingId { get; } = listingId;
    public MarketListingSelection Selection { get; set; } = selection;
    public MarketListingBatchStatus Status { get; set; } = status;
}

internal sealed record MarketListingSelection(
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    uint Quantity,
    uint UnitPrice,
    uint TotalTax,
    ulong TotalGil,
    ulong ListingId,
    ulong RetainerId);

internal sealed class MarketListingPurchaseAttempt(
    MarketListingSelection selection,
    uint territory,
    string position,
    DateTimeOffset stagedAtUtc)
{
    public MarketListingSelection Selection { get; } = selection;
    public uint Territory { get; } = territory;
    public string Position { get; } = position;
    public DateTimeOffset StagedAtUtc { get; } = stagedAtUtc;
    public DateTimeOffset? SentAtUtc { get; set; }
    public DateTimeOffset? DeadlineAtUtc { get; set; }
    public MarketListingPurchasePhase Phase { get; set; } =
        MarketListingPurchasePhase.AwaitingConfirmation;
    public bool PacketObserved { get; set; }
    public bool PacketMatchesIntent { get; set; }
    public uint? GilBeforeSend { get; set; }
    public uint? GilAfterResponse { get; set; }
    public string? FailureReason { get; set; }
    public string ItemName => Selection.ItemName;
    public uint Quantity => Selection.Quantity;
    public ulong TotalGil => Selection.TotalGil;

    public object ToEvidence() => new
    {
        StagedAtUtc,
        SentAtUtc,
        DeadlineAtUtc,
        Phase = Phase.ToString(),
        PacketObserved,
        PacketMatchesIntent,
        GilBeforeSend,
        GilAfterResponse,
        Territory,
        Position,
        Selection.ItemId,
        Selection.ItemName,
        Selection.IsHighQuality,
        Selection.Quantity,
        Selection.UnitPrice,
        Selection.TotalTax,
        Selection.TotalGil,
        Selection.ListingId,
        Selection.RetainerId,
    };
}
