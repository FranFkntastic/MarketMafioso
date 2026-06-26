using System;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionLiveDryRunPresenter
{
    public static MarketAcquisitionLiveDryRunSummary BuildSummary(MarketAcquisitionLiveDryRun dryRun)
    {
        ArgumentNullException.ThrowIfNull(dryRun);

        return new MarketAcquisitionLiveDryRunSummary
        {
            Status = dryRun.Status,
            Message = dryRun.Message,
            RequestedQuantity = dryRun.RequestedQuantity,
            WouldBuyQuantity = dryRun.WouldBuyQuantity,
            WouldSpendGil = dryRun.WouldSpendGil,
            WouldBuyRows = dryRun.Rows.Count(row => row.Decision == "WouldBuy"),
            SkippedRows = dryRun.Rows.Count(row => row.Decision != "WouldBuy"),
            TotalRows = dryRun.Rows.Count,
        };
    }
}

public sealed record MarketAcquisitionLiveDryRunSummary
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public uint RequestedQuantity { get; init; }
    public uint WouldBuyQuantity { get; init; }
    public uint WouldSpendGil { get; init; }
    public int WouldBuyRows { get; init; }
    public int SkippedRows { get; init; }
    public int TotalRows { get; init; }
}
