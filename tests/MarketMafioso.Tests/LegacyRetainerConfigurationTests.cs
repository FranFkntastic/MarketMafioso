using MarketMafioso.RetainerRestock;
using MarketMafioso.Legacy;
using Newtonsoft.Json;
using System.Text.Json;

namespace MarketMafioso.Tests;

public sealed class LegacyRetainerConfigurationTests
{
    [Fact]
    public void LegacyRetainerFieldsRemainDeserializableAndPlanSurvivesMigrationWindow()
    {
        const string json = """
            {
              "RetainerCache": {
                "10": { "RetainerId": 10, "RetainerName": "Migration Source" }
              },
              "RetainerRestockPlanItems": [
                { "ItemId": 100, "ItemName": "Elm Lumber", "DesiredPlayerQuantity": 50 }
              ]
            }
            """;

        var config = JsonConvert.DeserializeObject<Configuration>(json)!;

        Assert.Equal("Migration Source", Assert.Single(config.RetainerCache.Values).RetainerName);
        Assert.Equal("Elm Lumber", Assert.Single(config.RetainerRestockPlanItems).ItemName);
        var serialized = JsonConvert.SerializeObject(config);
        Assert.DoesNotContain("RetainerCache", serialized, StringComparison.Ordinal);
        Assert.Contains("RetainerRestockPlanItems", serialized, StringComparison.Ordinal);
        Assert.Contains("Elm Lumber", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigOnlyRetainerCacheIsExportedBeforeConfigurationCanSuppressIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mmf-legacy-source-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "retainer-cache.json");
            var config = new Configuration();
            config.RetainerCache[10] = new CachedRetainer { RetainerId = 10, RetainerName = "Migration Source" };

            Assert.True(LegacyRetainerMigrationSource.Preserve(config, path));
            Assert.False(LegacyRetainerMigrationSource.Preserve(config, path));

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("Migration Source", document.RootElement.GetProperty("10").GetProperty("retainerName").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
