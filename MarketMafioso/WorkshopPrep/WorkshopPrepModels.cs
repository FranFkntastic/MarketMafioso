using System;
using System.Collections.Generic;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopPrepQueueItem
{
    public uint WorkshopItemId { get; set; }
    public int Quantity { get; set; }
}

public sealed record WorkshopProjectDefinition(
    uint WorkshopItemId,
    uint ResultItemId,
    string Name,
    ushort IconId,
    IReadOnlyList<WorkshopMaterialRequirement> Materials,
    uint CategoryId = 0,
    uint TypeId = 0);

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
    int RetainerCache,
    int Shortage,
    int TotalMissing,
    IReadOnlyList<RetainerMaterialCandidate> CandidateRetainers)
{
    public int StockDifferential => PlayerInventory + RetainerCache - Required;
}

public sealed record RetainerMaterialCandidate(
    ulong RetainerId,
    string RetainerName,
    DateTime LastUpdated,
    int Quantity);
