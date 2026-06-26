using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardItemSearchDriverTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(6u)]
    [InlineData(7u)]
    public void ShouldResetToNormalSearch_ReturnsTrueForNonNormalModes(uint mode)
    {
        Assert.True(MarketBoardItemSearchDriver.ShouldResetToNormalSearch(mode));
    }

    [Fact]
    public void ShouldResetToNormalSearch_ReturnsFalseForNormalMode()
    {
        Assert.False(MarketBoardItemSearchDriver.ShouldResetToNormalSearch(0));
    }
}
