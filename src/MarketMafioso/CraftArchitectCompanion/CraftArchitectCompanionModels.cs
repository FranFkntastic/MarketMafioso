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
    public string CostSource { get; init; } = "Unknown";
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

public sealed record MarketAppraisalWorldSummary
{
    public string WorldName { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public uint ListingCount { get; init; }
    public ulong TotalGil { get; init; }
    public uint LowestUnitPrice { get; init; }
    public uint HighestUnitPrice { get; init; }
    public DateTimeOffset? FreshestReviewTimeUtc { get; init; }
}

public sealed record MarketAppraisalResult
{
    public MarketAppraisalRequest Request { get; init; } = new();
    public CraftAppraisalQuote? CraftQuote { get; init; }
    public uint SupportedQuantity { get; init; }
    public uint SupportedListingCount { get; init; }
    public uint SupportedWorldCount { get; init; }
    public ulong SupportedTotalGil { get; init; }
    public IReadOnlyList<MarketAppraisalWorldSummary> Worlds { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
