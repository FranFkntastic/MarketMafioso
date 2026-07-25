using MarketMafioso.MarketAcquisition.RemoteMarket;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class RemoteMarketPurchaseMatcherTests
{
    [Fact]
    public void PacketMatchesIntent_ReturnsTrueWhenEveryFieldMatches()
    {
        Assert.True(RemoteMarketPurchaseMatcher.PacketMatchesIntent(
            intentListingId: 42,
            intentItemId: 5116,
            intentQuantity: 50,
            intentUnitPrice: 213,
            packetListingId: 42,
            packetItemId: 5116,
            packetQuantity: 50,
            packetUnitPrice: 213));
    }

    [Theory]
    [InlineData(43, 5116, 50, 213)]
    [InlineData(42, 5117, 50, 213)]
    [InlineData(42, 5116, 51, 213)]
    [InlineData(42, 5116, 50, 214)]
    public void PacketMatchesIntent_ReturnsFalseWhenAnyFieldDrifts(
        long packetListingId,
        int packetItemId,
        int packetQuantity,
        int packetUnitPrice)
    {
        Assert.False(RemoteMarketPurchaseMatcher.PacketMatchesIntent(
            intentListingId: 42,
            intentItemId: 5116,
            intentQuantity: 50,
            intentUnitPrice: 213,
            packetListingId: (ulong)packetListingId,
            packetItemId: (uint)packetItemId,
            packetQuantity: (uint)packetQuantity,
            packetUnitPrice: (uint)packetUnitPrice));
    }

    [Fact]
    public void ConfirmationMatchesIntent_ReturnsTrueWhenItemAndQuantityMatch()
    {
        Assert.True(RemoteMarketPurchaseMatcher.ConfirmationMatchesIntent(5116, 50, 5116, 50));
    }

    [Theory]
    [InlineData(5117, 50)]
    [InlineData(5116, 49)]
    public void ConfirmationMatchesIntent_ReturnsFalseWhenItemOrQuantityDrifts(int itemId, int quantity)
    {
        Assert.False(RemoteMarketPurchaseMatcher.ConfirmationMatchesIntent(5116, 50, (uint)itemId, (uint)quantity));
    }
}
