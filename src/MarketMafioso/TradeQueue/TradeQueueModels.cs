using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.TradeQueue;

[Serializable]
public sealed class TradeQueueItem
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public sealed record TradeQueueInventoryStack(
    uint ContainerId,
    int SlotIndex,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    int Quantity);

public sealed record TradeQueueBatchLine(
    uint ContainerId,
    int SlotIndex,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    int Quantity,
    int SourceStackQuantity);

public sealed record TradeQueueBatch(
    IReadOnlyList<TradeQueueBatchLine> Lines,
    int GilAmount,
    IReadOnlyDictionary<TradeQueueInventoryKey, int> ExpectedInventoryBefore)
{
    public int SlotCount => Lines.Count;
    public int ItemUnitCount => Lines.Sum(line => line.Quantity);
    public int UnitCount => checked(ItemUnitCount + GilAmount);
}

public readonly record struct TradeQueueItemKey(uint ItemId);

public readonly record struct TradeQueueInventoryKey(uint ItemId, bool IsHighQuality);

public sealed record TradeQueuePartner(ulong GameObjectId, string Name, uint HomeWorldId);

public sealed record TradeQueueStartResult(bool Success, string Message);

public enum TradeQueueValidationCode
{
    Ready,
    Empty,
    InvalidQuantity,
    InsufficientInventory,
}

public sealed record TradeQueueValidationResult(
    bool Success,
    TradeQueueValidationCode Code,
    string Message);

public enum TradeQueueExecutionState
{
    Idle,
    NormalizingQuality,
    OpeningTrade,
    OfferingItems,
    WaitingForPartner,
    ConfirmingTrade,
    VerifyingInventory,
    Completed,
    Stopped,
    Failed,
}

public sealed record TradeQueueExecutionSnapshot(
    TradeQueueExecutionState State,
    string Message,
    string? PartnerName,
    int BatchNumber,
    int BatchSlotCount,
    int RemainingItemCount,
    int RemainingUnitCount,
    bool IsActive);
