using System;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionLiveCandidateStatuses
{
    public const string Ready = "Ready";
    public const string UnderProcured = "UnderProcured";
    public const string NoSafeListings = "NoSafeListings";
    public const string OverageLimit = "OverageLimit";
    public const string IncompleteListingCoverage = "IncompleteListingCoverage";
    public const string LegalStockObserved = "LegalStockObserved";
    public const string Purchased = "Purchased";
    public const string PriceEnvelopeCleared = "PriceEnvelopeCleared";

    private const string LegacyVisibleCacheExhausted = "VisibleCacheExhausted";

    public static bool IsIncompleteListingCoverage(string? status) =>
        Equals(status, IncompleteListingCoverage) ||
        Equals(status, LegacyVisibleCacheExhausted);

    // Observing or purchasing stock is not proof that the price/HQ envelope is cleared.
    public static bool IsConclusiveWorldVisitResult(string? result) =>
        Equals(result, PriceEnvelopeCleared);

    private static bool Equals(string? left, string right) =>
        left?.Equals(right, StringComparison.OrdinalIgnoreCase) == true;
}
