using System.Text.Json;
using MarketMafioso.Server.MarketDiagnostics;

namespace MarketMafioso.Server.Tests.MarketDiagnostics;

public sealed class UniversalisMarketDiagnosticClientTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(8, 256)]
    [InlineData(99, 256)]
    public void CalculateBackoff_IsExponentialAndBounded(int failures, int expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            UniversalisMarketDiagnosticClient.CalculateBackoff(failures));
    }

    [Fact]
    public void BackgroundJitter_IsStableAndBounded()
    {
        var interval = TimeSpan.FromMinutes(1);
        var first = MarketDiagnosticBackgroundService.CalculateJitter("installation-a", interval);
        var second = MarketDiagnosticBackgroundService.CalculateJitter("installation-a", interval);

        Assert.Equal(first, second);
        Assert.InRange(first, TimeSpan.Zero, TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void Parse_PreservesQualityFloorsAndListingIdentity()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "itemID": 4745,
              "lastUploadTime": 1784915990000,
              "minPriceNQ": 99,
              "minPriceHQ": 120,
              "listings": [
                {
                  "listingID": "listing-1",
                  "retainerID": "retainer-1",
                  "retainerName": "Mechanical",
                  "pricePerUnit": 99,
                  "quantity": 3,
                  "hq": false,
                  "lastReviewTime": 1784915990
                }
              ]
            }
            """);

        var evidence = Assert.Single(UniversalisMarketDiagnosticClient.Parse(document.RootElement)).Value;

        Assert.Equal((uint)99, evidence.MinimumNqPrice);
        Assert.Equal((uint)120, evidence.MinimumHqPrice);
        Assert.Equal("Mechanical", Assert.Single(evidence.Listings).RetainerName);
    }

    [Fact]
    public void ParseRegionConditions_PreservesQualityMetricsAndFreshestUpload()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "results": [
                {
                  "itemId": 4745,
                  "nq": {
                    "minListing": { "region": { "price": 20999, "worldId": 40 } },
                    "recentPurchase": { "region": { "price": 21000, "timestamp": 1784916000000, "worldId": 79 } },
                    "averageSalePrice": { "region": { "price": 22104.5 } },
                    "dailySaleVelocity": { "region": { "quantity": 9.8 } }
                  },
                  "hq": {
                    "minListing": {},
                    "recentPurchase": {},
                    "averageSalePrice": {},
                    "dailySaleVelocity": {}
                  },
                  "worldUploadTimes": [
                    { "worldId": 40, "timestamp": 1784915900000 },
                    { "worldId": 79, "timestamp": 1784915990000 }
                  ]
                }
              ],
              "failedItems": []
            }
            """);

        var conditions = UniversalisMarketDiagnosticClient.ParseRegionConditions(document.RootElement);

        Assert.Equal(2, conditions.Count);
        var nq = Assert.Single(conditions, condition => !condition.IsHq);
        Assert.Equal((uint)4745, nq.ItemId);
        Assert.Equal((uint)20999, nq.MinimumListingPrice);
        Assert.Equal((uint)40, nq.MinimumListingWorldId);
        Assert.Equal(22104.5, nq.AverageSalePrice);
        Assert.Equal(9.8, nq.DailySaleVelocity);
        Assert.Equal((uint)21000, nq.RecentPurchasePrice);
        Assert.Equal((uint)79, nq.RecentPurchaseWorldId);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1784915990000), nq.FreshestWorldUploadAtUtc);

        var hq = Assert.Single(conditions, condition => condition.IsHq);
        Assert.Null(hq.MinimumListingPrice);
        Assert.Equal(nq.FreshestWorldUploadAtUtc, hq.FreshestWorldUploadAtUtc);
    }

    [Fact]
    public void ParseSaleHistory_PreservesExactPublicSaleTuple()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "itemID": 4745,
              "entries": [
                {
                  "hq": false,
                  "pricePerUnit": 100,
                  "quantity": 3,
                  "timestamp": 1784916000,
                  "buyerName": "A Buyer",
                  "onMannequin": false
                },
                {
                  "pricePerUnit": 1,
                  "quantity": 1,
                  "timestamp": 0
                }
              ]
            }
            """);

        var sale = Assert.Single(
            UniversalisMarketDiagnosticClient.ParseSaleHistory(4745, document.RootElement));

        Assert.Equal((uint)4745, sale.ItemId);
        Assert.Equal((uint)100, sale.UnitPrice);
        Assert.Equal((uint)3, sale.Quantity);
        Assert.False(sale.IsHq);
        Assert.Equal("A Buyer", sale.BuyerName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1784916000), sale.SoldAtUtc);
    }
}
