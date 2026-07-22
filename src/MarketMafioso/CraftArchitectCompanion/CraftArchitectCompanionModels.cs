using System;
using System.Collections.Generic;

namespace MarketMafioso.CraftArchitectCompanion;

public sealed record CraftAppraisalQuote
{
    public int SchemaVersion { get; init; } = 1;
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public uint RequestedQuantity { get; init; }
    public uint OutputQuantity { get; init; } = 1;
    public decimal EstimatedUnitCost { get; init; }
    public decimal EstimatedTotalCost { get; init; }
    public string Currency { get; init; } = "gil";
    public string Source { get; init; } = "Manual";
    public DateTimeOffset? QuotedAtUtc { get; init; }
    public string Confidence { get; init; } = "Unknown";
    public bool IsComplete { get; init; }
    public string AppraisalStatus { get; init; } = "Unknown";
    public IReadOnlyList<CraftAppraisalMaterialQuote> Materials { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record CraftAppraisalMaterialQuote
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public decimal QuantityPerCraft { get; init; }
    public decimal TotalQuantity { get; init; }
    public decimal UnitCost { get; init; }
    public decimal TotalCost { get; init; }
    public string AcquisitionSource { get; init; } = "Unknown";
    public string CostSource { get; init; } = "Unknown";
    public string CostSourceDetails { get; init; } = string.Empty;
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record MarketAppraisalRequest
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public string HqPolicy { get; init; } = "Either";
    public uint BuyThresholdUnitPrice { get; init; }
    public uint GilCap { get; init; }
    public string Region { get; init; } = "North America";
    public string WorldMode { get; init; } = "Recommended";
    public string SweepScope { get; init; } = "Region";
    public IReadOnlyList<string> SweepDataCenters { get; init; } = [];
}
