using System;
using System.Linq;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

/// <summary>Combines live carried stock with the latest owner-matched Quartermaster evidence.</summary>
public sealed class RetainerAwareMarketAcquisitionInventoryObserver(
    IMarketAcquisitionInventoryObserver player,
    QuartermasterIpcClient quartermaster,
    Func<QuartermasterOwnerScope> currentOwner,
    Func<uint, string?> itemName) : IMarketAcquisitionInventoryObserver
{
    private static readonly TimeSpan RetainerStockAgeLimit = TimeSpan.FromHours(6);
    public MarketAcquisitionInventoryObservation Observe(uint itemId, string hqPolicy) => player.Observe(itemId, hqPolicy);

    public MarketAcquisitionCapacityObservation Capacity(uint itemId, bool isHq) => player.Capacity(itemId, isHq);

    public MarketAcquisitionInventoryObservation Observe(uint itemId, string hqPolicy, string targetBasis)
    {
        var carried = player.Observe(itemId, hqPolicy);
        if (MarketAcquisitionTargetBases.Normalize(targetBasis) != MarketAcquisitionTargetBases.OnHandIncludingRetainers ||
            !carried.IsAvailable)
            return carried;

        var owner = currentOwner();
        if (!quartermaster.TryGetSnapshot(out var snapshot, out var error) || snapshot is null ||
            !owner.Matches(snapshot.Owner) || !currentOwner().Matches(snapshot.Owner))
            return MarketAcquisitionInventoryObservation.Unavailable($"No owner-matched Quartermaster retainer snapshot. {error}");
        if (snapshot.Retainers.IsDefaultOrEmpty || snapshot.Retainers.Any(retainer =>
                !QuartermasterStockCoverage.CoversItem(retainer, itemId)))
            return MarketAcquisitionInventoryObservation.Unavailable("Retainer snapshot unavailable or incomplete for this item; stock is unknown, not zero.");

        var quantity = carried.Quantity;
        var oldest = snapshot.GeneratedAtUtc;
        foreach (var retainer in snapshot.Retainers)
        {
            // Bag timestamps are source-specific; the retainer timestamp can be newer because
            // only listings or gil were observed. Use the oldest covered bag, conservatively.
            var bags = retainer.Bags.Where(bag =>
                bag.BagName.Equals("RetainerCrystals", StringComparison.OrdinalIgnoreCase) == (itemId is >= 2 and <= 19)).ToArray();
            if (bags.Length == 0 || bags.Any(bag => bag.ObservedAtUtc == null))
                return MarketAcquisitionInventoryObservation.Unavailable(
                    $"Retainer stock for {itemName(itemId) ?? $"item {itemId}"} has no verified observation time; open a summoning bell to refresh.");
            var observedAt = bags.Min(bag => bag.ObservedAtUtc!.Value);
            if (observedAt < oldest) oldest = observedAt;
            foreach (var item in bags.SelectMany(bag => bag.Items).Where(item =>
                         item.ItemId == itemId && MarketAcquisitionPolicy.HqMatches(hqPolicy, item.IsHq)))
                quantity = checked(quantity + item.Quantity);
        }

        var age = DateTimeOffset.UtcNow - oldest;
        if (age > RetainerStockAgeLimit)
            return MarketAcquisitionInventoryObservation.Unavailable(
                $"Retainer stock for {itemName(itemId) ?? $"item {itemId}"} is {age.TotalHours:N1}h old (limit {RetainerStockAgeLimit.TotalHours:N0}h); open a summoning bell to refresh.");

        return new(true, quantity,
            $"Player {carried.Quantity:N0} + retainers {quantity - carried.Quantity:N0}. Retainer bags/crystals only; listings, saddlebag, armoury and FC chest excluded.")
        { RetainerObservedAtUtc = oldest };
    }
}
