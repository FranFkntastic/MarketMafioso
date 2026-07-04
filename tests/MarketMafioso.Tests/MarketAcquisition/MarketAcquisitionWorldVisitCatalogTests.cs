namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionWorldVisitCatalogTests
{
    [Fact]
    public void RecordProbe_UpsertsByWorldItemAndPolicy()
    {
        var config = new MarketMafioso.Configuration();
        var catalog = new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitCatalog(config);
        var checkedAt = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

        catalog.RecordProbe(new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitRecord
        {
            WorldName = "Maduin",
            DataCenter = "Dynamis",
            ItemId = 2,
            ItemName = "Fire Shard",
            HqPolicy = "Either",
            MaxUnitPrice = 99,
            CheckedAtUtc = checkedAt,
            Result = "NoSafeListings",
            ObservedLegalListingCount = 0,
            ObservedLegalQuantity = 0,
            ObservedLegalGil = 0,
            Source = "LiveMarketBoardProbe",
            RequestId = "request-1",
            RouteRunId = "route-1",
            RouteStopId = "stop-1",
        });

        catalog.RecordProbe(new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitRecord
        {
            WorldName = "maduin",
            DataCenter = "Dynamis",
            ItemId = 2,
            ItemName = "Fire Shard",
            HqPolicy = "Either",
            MaxUnitPrice = 99,
            CheckedAtUtc = checkedAt.AddMinutes(10),
            Result = "Purchased",
            PurchasedQuantity = 5,
            SpentGil = 250,
            Source = "PurchaseAudit",
            RequestId = "request-1",
            RouteRunId = "route-1",
            RouteStopId = "stop-1",
        });

        var visit = Assert.Single(config.MarketAcquisitionWorldVisits);
        Assert.Equal("Maduin", visit.WorldName);
        Assert.Equal(2u, visit.ItemId);
        Assert.Equal("Either", visit.HqPolicy);
        Assert.Equal(99u, visit.MaxUnitPrice);
        Assert.Equal("Purchased", visit.Result);
        Assert.Equal(5u, visit.PurchasedQuantity);
        Assert.Equal(250u, visit.SpentGil);
        Assert.Equal(checkedAt.AddMinutes(10).UtcDateTime, visit.CheckedAtUtc);
    }

    [Fact]
    public void WasRecentlyChecked_MatchesOnlyCompatibleItemWorldAndPolicy()
    {
        var config = new MarketMafioso.Configuration();
        var catalog = new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitCatalog(config);
        var now = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
        catalog.RecordProbe(new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitRecord
        {
            WorldName = "Siren",
            DataCenter = "Aether",
            ItemId = 2,
            ItemName = "Fire Shard",
            HqPolicy = "Either",
            MaxUnitPrice = 99,
            CheckedAtUtc = now.AddHours(-2),
            Result = "NoSafeListings",
            Source = "LiveMarketBoardProbe",
        });

        Assert.True(catalog.WasRecentlyChecked("siren", 2, "Either", 99, now, TimeSpan.FromHours(18)));
        Assert.False(catalog.WasRecentlyChecked("siren", 2, "HqOnly", 99, now, TimeSpan.FromHours(18)));
        Assert.False(catalog.WasRecentlyChecked("siren", 2, "Either", 100, now, TimeSpan.FromHours(18)));
        Assert.False(catalog.WasRecentlyChecked("maduin", 2, "Either", 99, now, TimeSpan.FromHours(18)));
        Assert.False(catalog.WasRecentlyChecked("siren", 4, "Either", 99, now, TimeSpan.FromHours(18)));
    }

    [Fact]
    public void Prune_RemovesOldestRecordsBeyondLimit()
    {
        var config = new MarketMafioso.Configuration();
        var catalog = new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitCatalog(config);
        var now = new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 505; i++)
        {
            catalog.RecordProbe(new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitRecord
            {
                WorldName = $"World-{i}",
                DataCenter = "Test",
                ItemId = 2,
                HqPolicy = "Either",
                MaxUnitPrice = 99,
                CheckedAtUtc = now.AddMinutes(i),
                Result = "NoSafeListings",
                Source = "LiveMarketBoardProbe",
            });
        }

        catalog.Prune(maxRecords: 500);

        Assert.Equal(500, config.MarketAcquisitionWorldVisits.Count);
        Assert.DoesNotContain(config.MarketAcquisitionWorldVisits, visit => visit.WorldName == "World-0");
        Assert.Contains(config.MarketAcquisitionWorldVisits, visit => visit.WorldName == "World-504");
    }
}
