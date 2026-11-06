using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.TradeQueue;

public sealed record TradeOfferedItem(uint ItemId, bool IsHighQuality, int Quantity);

// A readback of this trade, not a receipt and not an inference from dispatched callbacks.
public sealed record TradeOfferObservation(
    uint PartnerEntityId, int Gil, bool SyncPending, IReadOnlyList<TradeOfferedItem> Items)
{
    public string? Mismatch(TradeQueuePartner partner, TradeQueueBatch batch)
    {
        if (PartnerEntityId != (uint)partner.GameObjectId)
            return "The live trade recipient changed; the planned offer was not confirmed.";
        if (Gil != batch.GilAmount)
            return $"The live offer contains {Gil:N0} gil; expected {batch.GilAmount:N0}.";
        var expected = batch.Lines.GroupBy(x => new TradeQueueInventoryKey(x.ItemId, x.IsHighQuality))
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
        var actual = Items.GroupBy(x => new TradeQueueInventoryKey(x.ItemId, x.IsHighQuality))
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
        return actual.Count != expected.Count || expected.Any(x => actual.GetValueOrDefault(x.Key) != x.Value)
            ? "The live outgoing items or quantities differ from the planned batch." : null;
    }
}
