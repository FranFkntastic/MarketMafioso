using System;
using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionResearchModePolicy
{
    public const string ContextKey = "evidenceCollectionMode";
    public const string DecisionReady = "DecisionReady";
    public const string Exhaustive = "Exhaustive";

    public static string Capture(bool exhaustiveResearchMode) =>
        exhaustiveResearchMode ? Exhaustive : DecisionReady;

    public static bool AllowsConclusivePrefixDeparture(IReadOnlyDictionary<string, string?> operationContext)
    {
        ArgumentNullException.ThrowIfNull(operationContext);
        return !operationContext.TryGetValue(ContextKey, out var mode) ||
               string.Equals(mode, DecisionReady, StringComparison.Ordinal);
    }
}
