using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Tests.Windows.MarketAcquisitionRequestBuilder;

public sealed class RequestPricingFormatterTests
{
    [Fact]
    public void FormatOptionalGil_FormatsZeroAsUnset()
    {
        Assert.Equal("Unset", RequestPricingFormatter.FormatOptionalGil(0));
    }

    [Fact]
    public void FormatOptionalGil_FormatsPositiveGil()
    {
        Assert.Equal("1,200 gil", RequestPricingFormatter.FormatOptionalGil(1200));
    }
}
