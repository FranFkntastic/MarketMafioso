using System.Text.Json;
using MarketMafioso.Server.MarketDiagnostics;

namespace MarketMafioso.Server.Tests.MarketDiagnostics;

public sealed class UniversalisMarketDiagnosticClientTests
{
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
}
