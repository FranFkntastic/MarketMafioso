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

    [Fact]
    public void ChooseAction_ReturnsResetModeForWishlistMode()
    {
        Assert.Equal(MarketBoardItemSearchAction.ResetMode, MarketBoardItemSearchDriver.ChooseAction(5));
    }

    [Fact]
    public void ChooseAction_ReturnsSubmitSearchForNormalMode()
    {
        Assert.Equal(MarketBoardItemSearchAction.SubmitSearch, MarketBoardItemSearchDriver.ChooseAction(0));
    }

    [Fact]
    public void ShouldDisablePartialSearch_ReturnsTrueWhenPartialSearchIsEnabled()
    {
        Assert.True(MarketBoardItemSearchDriver.ShouldDisablePartialSearch(true));
    }

    [Fact]
    public void ShouldDisablePartialSearch_ReturnsFalseWhenPartialSearchIsAlreadyDisabled()
    {
        Assert.False(MarketBoardItemSearchDriver.ShouldDisablePartialSearch(false));
    }
}
