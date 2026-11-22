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
    internal static readonly TimeSpan RetainerStockStaleThreshold = TimeSpan.FromHours(6);
    public MarketAcquisitionInventoryObservation Observe(uint itemId, string hqPolicy) => player.Observe(itemId, hqPolicy);

    public MarketAcquisitionCapacityObservation Capacity(uint itemId, bool isHq) => player.Capacity(itemId, isHq);

    /// <summary>
    /// Stock on other characters selected under "Count stock from", added to every on-hand basis. Returns
    /// null when nothing extra applies; an unavailable observation makes the whole count unknown.
    /// </summary>
    internal Func<uint, string, MarketAcquisitionInventoryObservation?>? AdditionalStock { get; set; }

    public MarketAcquisitionInventoryObservation Observe(uint itemId, string hqPolicy, string targetBasis)
    {
        var own = ObserveOwn(itemId, hqPolicy, targetBasis);
        if (!own.IsAvailable || !MarketAcquisitionTargetBases.CountsOnHand(targetBasis) ||
            AdditionalStock?.Invoke(itemId, hqPolicy) is not { } extra)
            return own;
        if (!extra.IsAvailable)
            return extra;
        return own with
        {
            Quantity = checked(own.Quantity + extra.Quantity),
            Message = $"{own.Message} Other characters {extra.Quantity:N0}.",
        };
    }

    private MarketAcquisitionInventoryObservation ObserveOwn(uint itemId, string hqPolicy, string targetBasis)
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
        DateTimeOffset? oldest = null;
        var hasUnverifiedBagTimestamp = false;
        foreach (var retainer in snapshot.Retainers)
        {
            // Bag timestamps are source-specific; the retainer timestamp can be newer because
            // only listings or gil were observed. Track the oldest covered bag, conservatively.
            var bags = retainer.Bags.Where(bag =>
                bag.BagName.Equals("RetainerCrystals", StringComparison.OrdinalIgnoreCase) == (itemId is >= 2 and <= 19)).ToArray();
            if (bags.Length == 0)
                return MarketAcquisitionInventoryObservation.Unavailable(
                    $"Retainer snapshot is missing bags for {itemName(itemId) ?? $"item {itemId}"}; stock is unknown, not zero.");
            foreach (var bag in bags)
            {
                if (bag.ObservedAtUtc is { } observedAt)
                {
                    if (oldest == null || observedAt < oldest.Value)
                        oldest = observedAt;
                }
                else
                {
                    hasUnverifiedBagTimestamp = true;
                }
            }
            foreach (var item in bags.SelectMany(bag => bag.Items).Where(item =>
                         item.ItemId == itemId && MarketAcquisitionPolicy.HqMatches(hqPolicy, item.IsHq)))
                quantity = checked(quantity + item.Quantity);
        }

        var observed = oldest ?? snapshot.GeneratedAtUtc;
        var age = DateTimeOffset.UtcNow - observed;
        var staleNote = age > RetainerStockStaleThreshold
            ? $" Last observed {age.TotalHours:N1}h ago."
            : hasUnverifiedBagTimestamp
                ? " Some retainer bag timestamps are unverified."
                : string.Empty;

        return new(true, quantity,
            $"Player {carried.Quantity:N0} + retainers {quantity - carried.Quantity:N0}. Retainer bags/crystals only; listings, saddlebag, armoury and FC chest excluded.{staleNote}")
        { RetainerObservedAtUtc = observed };
    }
}
