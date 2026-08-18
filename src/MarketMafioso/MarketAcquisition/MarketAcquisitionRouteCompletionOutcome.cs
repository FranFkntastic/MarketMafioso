namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionRouteCompletionKinds
{
    public const string TargetSatisfied = "TargetSatisfied";
    public const string ScopeExhaustedBelowTarget = "ScopeExhaustedBelowTarget";
    public const string IncompleteOverageLimit = "IncompleteOverageLimit";
    public const string ScopeExhausted = "ScopeExhausted";
    public const string EvidenceRefreshCompleted = "EvidenceRefreshCompleted";
}

public sealed record MarketAcquisitionRouteCompletionOutcome(
    string Kind,
    uint TargetRequestedQuantity,
    uint TargetPurchasedQuantity,
    uint TargetRemainingQuantity);
