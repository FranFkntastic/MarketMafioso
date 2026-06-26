using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionLiveDryRun
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public uint RequestedQuantity { get; init; }
    public uint WouldBuyQuantity { get; init; }
    public uint WouldSpendGil { get; init; }
    public IReadOnlyList<MarketAcquisitionLiveDryRunRow> Rows { get; init; } = [];
}

public sealed record MarketAcquisitionLiveDryRunRow
{
    public string Decision { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public MarketBoardLiveListing LiveListing { get; init; } = new();
    public uint RunningQuantityAfter { get; init; }
    public uint RunningGilAfter { get; init; }
}
