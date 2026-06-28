using System;
using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionListing
{
    public string ListingId { get; init; } = string.Empty;
    public string WorldName { get; init; } = string.Empty;
    public uint WorldId { get; init; }
    public string RetainerName { get; init; } = string.Empty;
    public string RetainerId { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public uint UnitPrice { get; init; }
    public bool IsHq { get; init; }
    public DateTimeOffset LastReviewTimeUtc { get; init; }
    public uint TotalGil => checked(UnitPrice * Quantity);
}

public sealed record MarketAcquisitionPlannedListing
{
    public string ListingId { get; init; } = string.Empty;
    public string RetainerName { get; init; } = string.Empty;
    public string RetainerId { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public uint UnitPrice { get; init; }
    public uint TotalGil { get; init; }
    public bool IsHq { get; init; }
    public DateTimeOffset LastReviewTimeUtc { get; init; }
}

public sealed record MarketAcquisitionWorldBatch
{
    public string WorldName { get; init; } = string.Empty;
    public string DataCenter { get; init; } = string.Empty;
    public uint PlannedQuantity { get; init; }
    public uint PlannedGil { get; init; }
    public bool ExceedsRequestedQuantity { get; init; }
    public IReadOnlyList<MarketAcquisitionPlannedListing> Listings { get; init; } = [];
}

public sealed record MarketAcquisitionPlan
{
    public string RequestId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string WorldMode { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public uint RequestedQuantity { get; init; }
    public uint PlannedQuantity { get; init; }
    public uint PlannedGil { get; init; }
    public DateTimeOffset PreparedAtUtc { get; init; }
    public MarketAcquisitionPlanDiagnostics Diagnostics { get; init; } = new();
    public IReadOnlyList<MarketAcquisitionWorldBatch> WorldBatches { get; init; } = [];
}

public sealed record MarketAcquisitionPlanDiagnostics
{
    public int SourceListingCount { get; init; }
    public int NonZeroListingCount { get; init; }
    public int PriceSupportedListingCount { get; init; }
    public int HqSupportedListingCount { get; init; }
    public int WorldSupportedListingCount { get; init; }
    public int PlannedListingCount { get; init; }
}
