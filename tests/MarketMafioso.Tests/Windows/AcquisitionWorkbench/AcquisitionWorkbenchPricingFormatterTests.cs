using MarketMafioso.Windows.AcquisitionWorkbench;

namespace MarketMafioso.Tests.Windows.AcquisitionWorkbench;

public sealed class AcquisitionWorkbenchPricingFormatterTests
{
    [Fact]
    public void FormatOptionalGil_FormatsZeroAsUnset()
    {
        Assert.Equal("Unset", AcquisitionWorkbenchPricingFormatter.FormatOptionalGil(0));
    }

    [Fact]
    public void FormatOptionalGil_FormatsPositiveGil()
    {
        Assert.Equal("1,200 gil", AcquisitionWorkbenchPricingFormatter.FormatOptionalGil(1200));
    }
}
