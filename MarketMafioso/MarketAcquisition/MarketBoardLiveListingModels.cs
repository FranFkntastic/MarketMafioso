using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketBoardLiveListing
{
    public uint ItemId { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public string ListingId { get; init; } = string.Empty;
    public string RetainerId { get; init; } = string.Empty;
    public string RetainerName { get; init; } = string.Empty;
    public uint UnitPrice { get; init; }
    public uint Quantity { get; init; }
    public bool IsHq { get; init; }
}

public sealed record MarketBoardReadResult
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public IReadOnlyList<MarketBoardLiveListing> Listings { get; init; } = [];
}

public sealed record MarketBoardListingReconciliation
{
    public string Status { get; init; } = string.Empty;
    public IReadOnlyList<MarketBoardListingReconciliationRow> Listings { get; init; } = [];
}

public sealed record MarketBoardListingReconciliationRow
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool IsExactMatch { get; init; }
    public MarketAcquisitionPlannedListing PlannedListing { get; init; } = new();
    public MarketBoardLiveListing? LiveListing { get; init; }
}
