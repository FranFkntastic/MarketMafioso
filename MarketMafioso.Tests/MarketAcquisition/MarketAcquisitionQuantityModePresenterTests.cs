namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionQuantityModePresenterTests
{
    [Fact]
    public void FormatQuantity_ShowsNoCapForUnboundedAllBelowThreshold()
    {
        var text = MarketMafioso.MarketAcquisition.MarketAcquisitionQuantityModePresenter.FormatQuantity(
            "AllBelowThreshold",
            0);

        Assert.Equal("No quantity cap", text);
    }

    [Fact]
    public void FormatExecutionHint_ExplainsAllBelowThresholdWholeStackBehavior()
    {
        var text = MarketMafioso.MarketAcquisition.MarketAcquisitionQuantityModePresenter.FormatExecutionHint(
            "AllBelowThreshold");

        Assert.Contains("every safe whole listing", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Whole-stack overage", text, StringComparison.OrdinalIgnoreCase);
    }
}
