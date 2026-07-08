using MarketMafioso.Windows.ItemAutocomplete;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Tests.Windows.MarketAcquisitionRequestBuilder;

public sealed class RequestLineInputValidatorTests
{
    [Fact]
    public void CanAddIntentLine_AllowsBlankMaxUnitPrice()
    {
        var result = RequestLineInputValidator.CanAddIntentLine(
            selectedItem: new AcquisitionItemOption(5060, "Darksteel Ingot"),
            quantityMode: "TargetQuantity",
            targetQuantityBuffer: "3",
            maxQuantityBuffer: "",
            maxUnitPriceBuffer: "",
            gilCapBuffer: "");

        Assert.True(result);
    }

    [Fact]
    public void CanAddIntentLine_RejectsNonNumericMaxUnitPrice()
    {
        var result = RequestLineInputValidator.CanAddIntentLine(
            selectedItem: new AcquisitionItemOption(5060, "Darksteel Ingot"),
            quantityMode: "TargetQuantity",
            targetQuantityBuffer: "3",
            maxQuantityBuffer: "",
            maxUnitPriceBuffer: "abc",
            gilCapBuffer: "");

        Assert.False(result);
    }

    [Fact]
    public void CanAddIntentLine_RequiresTargetQuantityForTargetMode()
    {
        var result = RequestLineInputValidator.CanAddIntentLine(
            selectedItem: new AcquisitionItemOption(5060, "Darksteel Ingot"),
            quantityMode: "TargetQuantity",
            targetQuantityBuffer: "",
            maxQuantityBuffer: "",
            maxUnitPriceBuffer: "",
            gilCapBuffer: "");

        Assert.False(result);
    }

    [Fact]
    public void CanAddIntentLine_AllowsBlankMaxQuantityForAllBelowThreshold()
    {
        var result = RequestLineInputValidator.CanAddIntentLine(
            selectedItem: new AcquisitionItemOption(5060, "Darksteel Ingot"),
            quantityMode: "AllBelowThreshold",
            targetQuantityBuffer: "",
            maxQuantityBuffer: "",
            maxUnitPriceBuffer: "",
            gilCapBuffer: "");

        Assert.True(result);
    }
}
