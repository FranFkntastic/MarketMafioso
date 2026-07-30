using MarketMafioso.MarketAcquisition.RemoteMarket;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class RemoteMarketPurchaseVerificationTests
{
    [Fact]
    public void ExactRefreshedListings_PreserveIntentOrderAndCurrentValues()
    {
        var first = Selection(11, unitPrice: 100);
        var second = Selection(22, unitPrice: 200);
        var refreshedFirst = first with { SelectedIndex = 7, ItemName = "Current name" };
        var refreshedSecond = second with { SelectedIndex = 3, ItemName = "Current name" };

        var result = RemoteMarketPurchaseVerification.Reconcile(
            [first, second],
            [refreshedSecond, refreshedFirst]);

        Assert.True(result.Succeeded);
        Assert.Equal([refreshedFirst, refreshedSecond], result.RefreshedSelections);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void MissingListing_FailsWithoutSubstitutingAnotherListing()
    {
        var result = RemoteMarketPurchaseVerification.Reconcile(
            [Selection(11, unitPrice: 100)],
            [Selection(22, unitPrice: 100)]);

        Assert.False(result.Succeeded);
        Assert.Empty(result.RefreshedSelections);
        Assert.Contains("no longer available", result.FailureReason);
    }

    [Theory]
    [InlineData(101u, 1u, 5u)]
    [InlineData(100u, 2u, 5u)]
    [InlineData(100u, 1u, 6u)]
    public void ChangedPurchaseTerms_FailClosed(uint unitPrice, uint quantity, uint tax)
    {
        var original = Selection(11, unitPrice: 100, quantity: 1, tax: 5);
        var current = Selection(11, unitPrice, quantity, tax);

        var result = RemoteMarketPurchaseVerification.Reconcile([original], [current]);

        Assert.False(result.Succeeded);
        Assert.Empty(result.RefreshedSelections);
        Assert.Contains("changed", result.FailureReason);
    }

    private static RemoteMarketSelectionView Selection(
        ulong listingId,
        uint unitPrice,
        uint quantity = 1,
        uint tax = 5) =>
        new(
            (int)listingId,
            22528,
            "Example item",
            false,
            quantity,
            unitPrice,
            tax,
            (unitPrice * quantity) + tax,
            listingId,
            listingId + 1000);
}
