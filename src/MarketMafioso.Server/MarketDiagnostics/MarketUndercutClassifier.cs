namespace MarketMafioso.Server.MarketDiagnostics;

public static class MarketUndercutClassifier
{
    public static MarketListingEvaluation Evaluate(
        OwnedMarketListing ownedListing,
        UniversalisItemEvidence? evidence,
        IReadOnlySet<string> ownedRetainerNames,
        IReadOnlySet<string> ownedRetainerIds,
        DateTimeOffset observedAtUtc,
        TimeSpan maximumEvidenceAge)
    {
        ArgumentNullException.ThrowIfNull(ownedListing);

        if (evidence?.UploadedAtUtc is not { } uploadedAt)
        {
            return Unknown(
                ownedListing,
                observedAtUtc,
                evidence?.UploadedAtUtc,
                MarketObservationClassification.UnknownMissing,
                "Missing");
        }

        var sourceAge = observedAtUtc - uploadedAt;
        if (sourceAge > maximumEvidenceAge)
        {
            return Unknown(
                ownedListing,
                observedAtUtc,
                uploadedAt,
                MarketObservationClassification.UnknownStale,
                "Stale",
                sourceAge);
        }

        var competitor = evidence.Listings
            .Where(listing =>
                listing.ItemId == ownedListing.ItemId &&
                listing.IsHq == ownedListing.IsHq &&
                !ownedRetainerNames.Contains(listing.RetainerName) &&
                !ownedRetainerIds.Contains(listing.RetainerId))
            .OrderBy(listing => listing.UnitPrice)
            .ThenBy(listing => listing.Quantity)
            .FirstOrDefault();

        if (competitor == null || competitor.UnitPrice >= ownedListing.UnitPrice)
        {
            return new MarketListingEvaluation
            {
                OwnedListing = ownedListing,
                Classification = MarketObservationClassification.Clear,
                ObservedAtUtc = observedAtUtc,
                SourceUploadedAtUtc = uploadedAt,
                SourceAgeSeconds = Math.Max(0, (long)sourceAge.TotalSeconds),
                SourceFreshness = "Fresh",
            };
        }

        return new MarketListingEvaluation
        {
            OwnedListing = ownedListing,
            Classification = MarketObservationClassification.Undercut,
            ObservedAtUtc = observedAtUtc,
            SourceUploadedAtUtc = uploadedAt,
            SourceAgeSeconds = Math.Max(0, (long)sourceAge.TotalSeconds),
            SourceFreshness = "Fresh",
            Competitor = competitor,
            UndercutDelta = ownedListing.UnitPrice - competitor.UnitPrice,
        };
    }

    private static MarketListingEvaluation Unknown(
        OwnedMarketListing ownedListing,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? uploadedAtUtc,
        MarketObservationClassification classification,
        string freshness,
        TimeSpan? sourceAge = null) =>
        new()
        {
            OwnedListing = ownedListing,
            Classification = classification,
            ObservedAtUtc = observedAtUtc,
            SourceUploadedAtUtc = uploadedAtUtc,
            SourceAgeSeconds = sourceAge.HasValue
                ? Math.Max(0, (long)sourceAge.Value.TotalSeconds)
                : null,
            SourceFreshness = freshness,
        };
}
