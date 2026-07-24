using MarketMafioso.Server.MarketDiagnostics;

namespace MarketMafioso.Server.Tests.MarketDiagnostics;

public sealed class MarketUndercutClassifierTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_FindsOneGilCompetitorAndExcludesOwnedRetainers()
    {
        var owned = Owned(unitPrice: 100);
        var evidence = Evidence(
            new UniversalisListingEvidence
            {
                ItemId = owned.ItemId,
                ListingId = "ours",
                RetainerId = owned.RetainerId.ToString(),
                RetainerName = owned.RetainerName,
                UnitPrice = 90,
                Quantity = 1,
                ReviewedAtUtc = ObservedAt.AddSeconds(-5),
            },
            new UniversalisListingEvidence
            {
                ItemId = owned.ItemId,
                ListingId = "competitor",
                RetainerId = "456",
                RetainerName = "Mechanical",
                UnitPrice = 99,
                Quantity = 1,
                ReviewedAtUtc = ObservedAt.AddSeconds(-5),
            });

        var result = MarketUndercutClassifier.Evaluate(
            owned,
            evidence,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { owned.RetainerName },
            new HashSet<string>(StringComparer.Ordinal) { owned.RetainerId.ToString() },
            ObservedAt,
            TimeSpan.FromMinutes(15));

        Assert.Equal(MarketObservationClassification.Undercut, result.Classification);
        Assert.Equal("Mechanical", result.Competitor?.RetainerName);
        Assert.Equal(1u, result.UndercutDelta);
    }

    [Fact]
    public void Evaluate_StaleEvidenceCannotStartUndercut()
    {
        var owned = Owned(unitPrice: 100);
        var result = MarketUndercutClassifier.Evaluate(
            owned,
            Evidence(
                new UniversalisListingEvidence
                {
                    ItemId = owned.ItemId,
                    ListingId = "competitor",
                    RetainerId = "456",
                    RetainerName = "Mechanical",
                    UnitPrice = 99,
                    Quantity = 1,
                    ReviewedAtUtc = ObservedAt.AddHours(-1),
                }) with
            {
                UploadedAtUtc = ObservedAt.AddHours(-1),
            },
            new HashSet<string>(),
            new HashSet<string>(),
            ObservedAt,
            TimeSpan.FromMinutes(15));

        Assert.Equal(MarketObservationClassification.UnknownStale, result.Classification);
        Assert.Null(result.Competitor);
    }

    [Fact]
    public void Evaluate_MissingEvidenceIsUnknownRatherThanClear()
    {
        var result = MarketUndercutClassifier.Evaluate(
            Owned(unitPrice: 100),
            null,
            new HashSet<string>(),
            new HashSet<string>(),
            ObservedAt,
            TimeSpan.FromMinutes(15));

        Assert.Equal(MarketObservationClassification.UnknownMissing, result.Classification);
    }

    [Fact]
    public void Evaluate_OppositeQualityAloneCannotProveListingClear()
    {
        var owned = Owned(unitPrice: 100) with { IsHq = true };
        var result = MarketUndercutClassifier.Evaluate(
            owned,
            Evidence(
                new UniversalisListingEvidence
                {
                    ItemId = owned.ItemId,
                    ListingId = "nq",
                    RetainerId = "456",
                    RetainerName = "Nq Seller",
                    UnitPrice = 1,
                    Quantity = 1,
                    IsHq = false,
                    ReviewedAtUtc = ObservedAt.AddSeconds(-5),
                }),
            new HashSet<string>(),
            new HashSet<string>(),
            ObservedAt,
            TimeSpan.FromMinutes(15));

        Assert.Equal(MarketObservationClassification.UnknownMissing, result.Classification);
    }

    [Fact]
    public void Evaluate_UndercutFloorWithoutListingIdentityStaysUnknown()
    {
        var owned = Owned(unitPrice: 100) with { IsHq = true };
        var result = MarketUndercutClassifier.Evaluate(
            owned,
            new UniversalisItemEvidence
            {
                ItemId = owned.ItemId,
                UploadedAtUtc = ObservedAt.AddSeconds(-5),
                MinimumHqPrice = 99,
                Listings = [],
            },
            new HashSet<string>(),
            new HashSet<string>(),
            ObservedAt,
            TimeSpan.FromMinutes(15));

        Assert.Equal(MarketObservationClassification.UnknownMissing, result.Classification);
        Assert.Equal("CompetitorIdentityMissing", result.SourceFreshness);
    }

    private static OwnedMarketListing Owned(uint unitPrice) =>
        new()
        {
            Id = 1,
            AccountId = 1,
            VersionKey = "version",
            ListingKey = "listing",
            SnapshotId = "snapshot",
            CharacterName = "Owner",
            World = "Cactuar",
            RetainerId = 123,
            RetainerName = "Our Retainer",
            ItemId = 4745,
            ItemName = "Orange Juice",
            Quantity = 1,
            UnitPrice = unitPrice,
            ListedAtUtc = ObservedAt.AddMinutes(-5),
            ListingsObservedAtUtc = ObservedAt.AddMinutes(-5),
            FirstObservedAtUtc = ObservedAt.AddMinutes(-5),
            LastObservedAtUtc = ObservedAt.AddMinutes(-5),
        };

    private static UniversalisItemEvidence Evidence(params UniversalisListingEvidence[] listings) =>
        new()
        {
            ItemId = 4745,
            UploadedAtUtc = ObservedAt.AddSeconds(-5),
            MinimumNqPrice = listings
                .Where(listing => !listing.IsHq)
                .Select(listing => (uint?)listing.UnitPrice)
                .Min(),
            MinimumHqPrice = listings
                .Where(listing => listing.IsHq)
                .Select(listing => (uint?)listing.UnitPrice)
                .Min(),
            Listings = listings,
        };
}
