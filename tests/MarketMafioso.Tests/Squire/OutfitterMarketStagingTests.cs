using MarketMafioso.Squire.Outfitter;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterMarketStagingTests
{
    [Fact]
    public void AddTenPercentCeiling_RoundsUpWithoutOverflow()
    {
        var result = OutfitterMarketStaging.AddTenPercentCeiling(101, out var clamped);

        Assert.Equal(112u, result);
        Assert.False(clamped);
    }

    [Fact]
    public void AddTenPercentCeiling_SaturatesAtRequestLimit()
    {
        var result = OutfitterMarketStaging.AddTenPercentCeiling(uint.MaxValue, out var clamped);

        Assert.Equal(uint.MaxValue, result);
        Assert.True(clamped);
    }

    [Fact]
    public void MultiplySaturating_SaturatesAtRequestLimit()
    {
        var result = OutfitterMarketStaging.MultiplySaturating(uint.MaxValue, 12, out var clamped);

        Assert.Equal(uint.MaxValue, result);
        Assert.True(clamped);
    }
}
