using MarketMafioso.Automation.Travel;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteTravelPreflightTests
{
    [Fact]
    public void Check_AllowsTravelWhenNoBlockingAddonsAreOpen()
    {
        var result = AutomationTravelPreflight.Check([]);

        Assert.True(result.CanSendCommand);
        Assert.Empty(result.BlockingAddons);
    }

    [Fact]
    public void Check_BlocksTravelWhenBlockingAddonsAreOpen()
    {
        var result = AutomationTravelPreflight.Check(
            ["ItemSearch", "SelectString"]);

        Assert.False(result.CanSendCommand);
        Assert.Equal(["ItemSearch", "SelectString"], result.BlockingAddons);
        Assert.Contains("Close blocking UI before Lifestream travel", result.Message, StringComparison.Ordinal);
        Assert.Contains("ItemSearch", result.Message, StringComparison.Ordinal);
        Assert.Contains("SelectString", result.Message, StringComparison.Ordinal);
    }
}
