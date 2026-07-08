using MarketMafioso.Tests.TestUtilities;
using Newtonsoft.Json;

namespace MarketMafioso.Tests;

public sealed class RetainerCachePersistenceTests
{
    [Fact]
    public void ConfigurationSerialization_OmitsRetainerCache()
    {
        var config = new Configuration
        {
            RetainerCache =
            {
                [123] = BuildRetainer(123, "Eris-morne"),
            },
        };

        var json = JsonConvert.SerializeObject(config);

        Assert.DoesNotContain("RetainerCache", json);
    }

    [Fact]
    public void ConfigurationDeserialization_LoadsLegacyRetainerCache()
    {
        const string json = """
        {
          "RetainerCache": {
            "123": {
              "RetainerId": 123,
              "RetainerName": "Eris-morne",
              "OwnerCharacterName": "Wei Ning",
              "OwnerHomeWorld": "Maduin",
              "LastUpdated": "2026-07-08T01:55:28Z",
              "Gil": 42,
              "Bags": [
                {
                  "BagName": "RetainerInventory",
                  "Items": [
                    {
                      "ItemId": 5114,
                      "ItemName": "Darksteel Ore",
                      "Quantity": 37
                    }
                  ]
                }
              ],
              "MarketListings": []
            }
          }
        }
        """;

        var config = JsonConvert.DeserializeObject<Configuration>(json)!;

        var retainer = Assert.Single(config.RetainerCache.Values);
        Assert.Equal((ulong)123, retainer.RetainerId);
        Assert.Equal("Eris-morne", retainer.RetainerName);
        Assert.Equal("Wei Ning", retainer.OwnerCharacterName);
        Assert.Equal("Maduin", retainer.OwnerHomeWorld);
        Assert.Equal((uint)37, Assert.Single(Assert.Single(retainer.Bags).Items).Quantity);
    }

    [Fact]
    public void RetainerCacheFileStore_SaveAndLoad_RoundTripsCache()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "retainer-cache.json");
        var store = new RetainerCacheFileStore(path);
        var cache = new Dictionary<ulong, CachedRetainer>
        {
            [123] = BuildRetainer(123, "Eris-morne"),
        };

        store.Save(cache);
        var loaded = store.Load();

        var retainer = Assert.Single(loaded.Values);
        Assert.Equal("Eris-morne", retainer.RetainerName);
        Assert.Equal((uint)37, Assert.Single(Assert.Single(retainer.Bags).Items).Quantity);
    }

    private static CachedRetainer BuildRetainer(ulong id, string name) =>
        new()
        {
            RetainerId = id,
            RetainerName = name,
            OwnerCharacterName = "Wei Ning",
            OwnerHomeWorld = "Maduin",
            LastUpdated = new DateTime(2026, 7, 8, 1, 55, 28, DateTimeKind.Utc),
            Gil = 42,
            Bags =
            [
                new CachedBag
                {
                    BagName = "RetainerInventory",
                    Items =
                    [
                        new CachedItem { ItemId = 5114, ItemName = "Darksteel Ore", Quantity = 37 },
                    ],
                },
            ],
        };
}
