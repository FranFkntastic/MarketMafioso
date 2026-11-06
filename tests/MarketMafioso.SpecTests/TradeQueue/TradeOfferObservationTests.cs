using MarketMafioso.TradeQueue;

namespace MarketMafioso.SpecTests.TradeQueue;

public sealed class TradeOfferObservationTests
{
    [Theory]
    [InlineData(1, 100, 42, false, 3, true)]
    [InlineData(2, 100, 42, false, 3, false)]
    [InlineData(1, 99, 42, false, 3, false)]
    [InlineData(1, 100, 43, false, 3, false)]
    [InlineData(1, 100, 42, true, 3, false)]
    [InlineData(1, 100, 42, false, 2, false)]
    public void ExactOfferBindsRecipientGilItemQualityAndQuantity(uint recipient, int gil, uint item, bool hq, int quantity, bool matches)
    {
        var offer = new TradeOfferObservation(recipient, gil, false, [new(item, hq, quantity)]);
        var batch = new TradeQueueBatch([new(0, 0, 42, "Item", false, 3, 3)], 100, new Dictionary<TradeQueueInventoryKey, int>());
        Assert.Equal(matches, offer.Mismatch(new(1, "Recipient", 2, "Siren"), batch) == null);
    }
}
