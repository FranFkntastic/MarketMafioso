using System;

namespace MarketMafioso.MarketAcquisition;

internal sealed record MarketPurchaseEvidenceRoutePreflightResult(
    bool CanExecute,
    string? BlockReason);

internal static class MarketPurchaseEvidenceRoutePreflight
{
    public static MarketPurchaseEvidenceRoutePreflightResult Evaluate(
        MarketPurchaseEvidenceState? evidenceState,
        bool purchaseSessionActive,
        DateTimeOffset nowUtc)
    {
        if (evidenceState is null || purchaseSessionActive)
            return new(true, null);

        var intent = evidenceState.Intent;
        var reason = evidenceState switch
        {
            PendingMarketPurchase pending when pending.Intent.DeadlineUtc <= nowUtc =>
                $"Purchase intent {intent.IntentId} expired without server evidence for listing {intent.ListingId}; reconcile the purchase outcome before route execution can continue.",
            PendingMarketPurchase pending =>
                $"Purchase intent {intent.IntentId} for listing {intent.ListingId} is still awaiting server evidence until {pending.Intent.DeadlineUtc:O}; route execution is paused.",
            ConfirmedMarketPurchase =>
                $"Server-confirmed purchase intent {intent.IntentId} for listing {intent.ListingId} requires reconciliation before route execution can continue.",
            TimedOutIndeterminateMarketPurchase =>
                $"Purchase intent {intent.IntentId} timed out without server evidence for listing {intent.ListingId}; reconcile the outcome before route execution can continue.",
            ConflictingMarketPurchasePacket =>
                $"Purchase intent {intent.IntentId} has conflicting server evidence for listing {intent.ListingId}; reconcile the outcome before route execution can continue.",
            _ =>
                $"Purchase intent {intent.IntentId} has unresolved server evidence for listing {intent.ListingId}; reconcile the outcome before route execution can continue.",
        };

        return new(false, reason);
    }
}
