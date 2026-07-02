using MarketMafioso.Automation.Items;

namespace MarketMafioso.Tests.Automation.Items;

public sealed class ItemStackRulesTests
{
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(9, true)]
    [InlineData(10, true)]
    [InlineData(11, true)]
    [InlineData(12, true)]
    [InlineData(13, true)]
    [InlineData(14, false)]
    public void IsElementalCurrency_identifies_elemental_currency(uint itemId, bool expected)
    {
        Assert.Equal(expected, ItemStackRules.IsElementalCurrency(itemId));
    }

    [Fact]
    public void ResolveMaxStack_uses_lumina_value_when_available_before_fallbacks()
    {
        Assert.Equal(99, ItemStackRules.ResolveMaxStack(itemId: 5121, luminaStackSize: 99));
        Assert.Equal(9999, ItemStackRules.ResolveMaxStack(itemId: 2, luminaStackSize: 0));
        Assert.Equal(999, ItemStackRules.ResolveMaxStack(itemId: 5121, luminaStackSize: 0));
    }

    [Fact]
    public void ResolveMaxStack_rejects_values_too_large_for_inventory_math()
    {
        var exception = Assert.Throws<OverflowException>(
            () => ItemStackRules.ResolveMaxStack(itemId: 5121, luminaStackSize: 3000000000));

        Assert.Contains("too large", exception.Message, StringComparison.Ordinal);
    }
}
