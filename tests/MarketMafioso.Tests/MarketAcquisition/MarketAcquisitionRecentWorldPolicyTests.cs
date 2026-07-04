namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRecentWorldPolicyTests
{
    [Fact]
    public void FilterListings_RemovesRecentWorldsForAllWorldSweep()
    {
        var config = new MarketMafioso.Configuration();
        var catalog = new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitCatalog(config);
        var now = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        catalog.RecordProbe(new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitRecord
        {
            WorldName = "Siren",
            DataCenter = "Aether",
            ItemId = 2,
            HqPolicy = "Either",
            MaxUnitPrice = 99,
            CheckedAtUtc = now.AddHours(-1),
            Result = "NoSafeListings",
            Source = "LiveMarketBoardProbe",
        });

        var listings = new[]
        {
            CreateListing("Siren", "recent"),
            CreateListing("Maduin", "fresh"),
        };

        var result = MarketMafioso.MarketAcquisition.MarketAcquisitionRecentWorldPolicy.FilterListings(
            CreateLine(),
            listings,
            catalog,
            now,
            TimeSpan.FromHours(18),
            ignoreRecentVisits: false,
            worldsWithNewerUsefulUniversalisEvidence: []);

        Assert.Equal(["fresh"], result.Listings.Select(listing => listing.ListingId).ToArray());
        Assert.Equal(["Siren"], result.SkippedRecentWorlds);
    }

    [Fact]
    public void FilterListings_KeepsRecentWorldWhenUniversalisHasNewerUsefulEvidence()
    {
        var config = new MarketMafioso.Configuration();
        var catalog = new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitCatalog(config);
        var now = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        catalog.RecordProbe(new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitRecord
        {
            WorldName = "Siren",
            DataCenter = "Aether",
            ItemId = 2,
            HqPolicy = "Either",
            MaxUnitPrice = 99,
            CheckedAtUtc = now.AddHours(-1),
            Result = "NoSafeListings",
            Source = "LiveMarketBoardProbe",
        });

        var result = MarketMafioso.MarketAcquisition.MarketAcquisitionRecentWorldPolicy.FilterListings(
            CreateLine(),
            [CreateListing("Siren", "recent")],
            catalog,
            now,
            TimeSpan.FromHours(18),
            ignoreRecentVisits: false,
            worldsWithNewerUsefulUniversalisEvidence: ["Siren"]);

        Assert.Equal(["recent"], result.Listings.Select(listing => listing.ListingId).ToArray());
        Assert.Empty(result.SkippedRecentWorlds);
    }

    [Fact]
    public void BuildSweepWorldExclusions_RemovesRecentWorldsEvenWithoutListings()
    {
        var config = new MarketMafioso.Configuration();
        var catalog = new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitCatalog(config);
        var now = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        catalog.RecordProbe(new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitRecord
        {
            WorldName = "Siren",
            DataCenter = "Aether",
            ItemId = 2,
            HqPolicy = "Either",
            MaxUnitPrice = 99,
            CheckedAtUtc = now.AddHours(-1),
            Result = "NoSafeListings",
            Source = "LiveMarketBoardProbe",
        });

        var exclusions = MarketMafioso.MarketAcquisition.MarketAcquisitionRecentWorldPolicy.BuildSweepWorldExclusions(
            CreateLine(),
            catalog,
            now,
            TimeSpan.FromHours(18),
            ignoreRecentVisits: false,
            worldsWithNewerUsefulUniversalisEvidence: []);

        var exclusion = Assert.Single(exclusions);
        Assert.Equal("line-1", exclusion.LineId);
        Assert.Equal("Siren", exclusion.WorldName);
        Assert.Equal(2u, exclusion.ItemId);
    }

    [Fact]
    public void FilterListings_IgnoreRecentVisitsKeepsEverything()
    {
        var config = new MarketMafioso.Configuration();
        var catalog = new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitCatalog(config);
        var now = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        catalog.RecordProbe(new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitRecord
        {
            WorldName = "Siren",
            DataCenter = "Aether",
            ItemId = 2,
            HqPolicy = "Either",
            MaxUnitPrice = 99,
            CheckedAtUtc = now.AddHours(-1),
            Result = "NoSafeListings",
            Source = "LiveMarketBoardProbe",
        });

        var result = MarketMafioso.MarketAcquisition.MarketAcquisitionRecentWorldPolicy.FilterListings(
            CreateLine(),
            [CreateListing("Siren", "recent")],
            catalog,
            now,
            TimeSpan.FromHours(18),
            ignoreRecentVisits: true,
            worldsWithNewerUsefulUniversalisEvidence: []);

        Assert.Equal(["recent"], result.Listings.Select(listing => listing.ListingId).ToArray());
        Assert.Empty(result.SkippedRecentWorlds);
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionBatchLineView CreateLine() =>
        new()
        {
            LineId = "line-1",
            ItemId = 2,
            ItemName = "Fire Shard",
            QuantityMode = "AllBelowThreshold",
            TargetQuantity = 0,
            MaxQuantity = 0,
            HqPolicy = "Either",
            MaxUnitPrice = 99,
            GilCap = 0,
        };

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionListing CreateListing(
        string worldName,
        string listingId) =>
        new()
        {
            ItemId = 2,
            ItemName = "Fire Shard",
            ListingId = listingId,
            WorldName = worldName,
            Quantity = 1,
            UnitPrice = 99,
            LastReviewTimeUtc = DateTimeOffset.UnixEpoch,
        };
}
