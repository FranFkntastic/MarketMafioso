using MarketMafioso.TradeQueue;

namespace MarketMafioso.SpecTests.TradeQueue;

public sealed class TradeButtonIdentityTests
{
    [Theory]
    [InlineData("Trade", "Cancel")]
    [InlineData("Anbieten", "Abbrechen")]
    [InlineData("Échanger", "Annuler")]
    [InlineData("条件提示", "トレード中止")]
    public void ReorderedButtonsResolveByTheClientsLocalizedIdentity(string trade, string cancel)
    {
        TradeButtonCandidate[] buttons = [new(2, cancel, true, true), new(9, "Other", true, true), new(3, trade, true, true)];
        Assert.Equal((nint)3, TradeButtonIdentity.FindUnique(buttons, trade));
        Assert.Equal((nint)2, TradeButtonIdentity.FindUnique(buttons.Reverse().ToArray(), cancel));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void HiddenOrDisabledTradeControlCannotBeClicked(bool visible, bool enabled) =>
        Assert.Equal((nint)0, TradeButtonIdentity.FindUnique([new(3, "Trade", visible, enabled)], "Trade"));

    [Fact]
    public void AnotherEnabledButtonAtTheOldIndexCannotBecomeTrade() =>
        Assert.Equal((nint)0, TradeButtonIdentity.FindUnique([new(3, "Cancel", true, true)], "Trade"));

    [Fact]
    public void TwoVisibleTradeControlsAreAmbiguousEvenIfOneIsDisabled() =>
        Assert.Equal((nint)0, TradeButtonIdentity.FindUnique([new(3, "Trade", true, true), new(4, "Trade", true, false)], "Trade"));

    [Fact]
    public void RepeatedReferenceToTheSameButtonIsNotAmbiguous() =>
        Assert.Equal((nint)3, TradeButtonIdentity.FindUnique([new(3, "Trade", true, true), new(3, "Trade", true, true)], "Trade"));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Ready")]
    public void MissingOrUnexpectedGameLabelFailsClosed(string label) =>
        Assert.Equal((nint)0, TradeButtonIdentity.FindUnique([new(3, "Trade", true, true)], label));
}
