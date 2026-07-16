namespace MarketMafioso.Server;

public static class MarketAcquisitionWorkOrderPolicy
{
    public static bool IsLeaseRenewableStatus(string status) => status is
        MarketAcquisitionStatuses.Claimed or
        MarketAcquisitionStatuses.AcceptedInPlugin or
        MarketAcquisitionStatuses.Running or
        MarketAcquisitionStatuses.RecoveryRequired;
}

public sealed class MarketAcquisitionMergeConflictException : Exception
{
    public MarketAcquisitionMergeConflictException(MarketAcquisitionWorkOrderMergePreview preview)
        : base("Work orders contain constraints that require an explicit choice before merging.")
    {
        Preview = preview;
    }

    public MarketAcquisitionWorkOrderMergePreview Preview { get; }
}
