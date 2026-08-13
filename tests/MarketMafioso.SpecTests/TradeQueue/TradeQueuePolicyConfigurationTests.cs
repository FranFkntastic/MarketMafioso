using MarketMafioso.TradeQueue;
using Newtonsoft.Json;

namespace MarketMafioso.SpecTests.TradeQueue;

public sealed class TradeQueuePolicyConfigurationTests
{
    [Fact]
    public void MissingPolicyDefaultsToQualityNormalization()
    {
        var config = JsonConvert.DeserializeObject<Configuration>("{}")!;

        Assert.True(config.TradeQueuePolicy.NormalizeHighQualityItems);
    }

    [Fact]
    public void DisabledPolicyRoundTrips()
    {
        var config = new Configuration
        {
            TradeQueuePolicy = new TradeQueuePolicyOptions
            {
                NormalizeHighQualityItems = false,
            },
        };

        var restored = JsonConvert.DeserializeObject<Configuration>(
            JsonConvert.SerializeObject(config))!;

        Assert.False(restored.TradeQueuePolicy.NormalizeHighQualityItems);
    }
}
