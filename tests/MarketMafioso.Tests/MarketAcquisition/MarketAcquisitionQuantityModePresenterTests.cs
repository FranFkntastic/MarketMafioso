namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionQuantityModePresenterTests
{
    [Fact]
    public void FormatMode_UsesWorkbenchBuyingRuleLanguage()
    {
        Assert.Equal(
            "Buy below ceiling",
            MarketMafioso.MarketAcquisition.MarketAcquisitionQuantityModePresenter.FormatMode("AllBelowThreshold"));
        Assert.Equal(
            "Target quantity",
            MarketMafioso.MarketAcquisition.MarketAcquisitionQuantityModePresenter.FormatMode("TargetQuantity"));
    }

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
