using System;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketBoardPurchaseSession
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public MarketBoardPurchaseCandidate Candidate { get; init; } = new();
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset DeadlineUtc { get; init; }
    public bool IsActive =>
        Status is "WaitingForConfirmation" or "WaitingForListingRemoval";

    public static MarketBoardPurchaseSession Start(
        MarketBoardPurchaseCandidate candidate,
        DateTimeOffset nowUtc,
        TimeSpan confirmationWatchdog) =>
        new()
        {
            Status = "WaitingForConfirmation",
            Message = "Purchase selection was sent; waiting for the market-board confirmation prompt.",
            Candidate = candidate,
            StartedAtUtc = nowUtc,
            DeadlineUtc = nowUtc.Add(confirmationWatchdog),
        };

    public MarketBoardPurchaseSession RecordConfirmationAttempt(
        MarketBoardPurchaseResult result,
        DateTimeOffset nowUtc,
        TimeSpan listingRemovalWatchdog)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!Status.Equals("WaitingForConfirmation", StringComparison.OrdinalIgnoreCase))
            return this;

        if (result.Status.Equals("ConfirmationAccepted", StringComparison.OrdinalIgnoreCase))
        {
            return this with
            {
                Status = "WaitingForListingRemoval",
                Message = "Purchase confirmation accepted; waiting for the purchased listing to disappear.",
                DeadlineUtc = nowUtc.Add(listingRemovalWatchdog),
            };
        }

        if (result.Status.Equals("ConfirmationPending", StringComparison.OrdinalIgnoreCase) &&
            nowUtc <= DeadlineUtc)
        {
            return this with { Message = result.Message };
        }

        return this with
        {
            Status = nowUtc > DeadlineUtc ? "ConfirmationTimeout" : result.Status,
            Message = nowUtc > DeadlineUtc
                ? "Market-board purchase confirmation did not appear before the watchdog expired."
                : result.Message,
        };
    }

    public MarketBoardPurchaseSession RecordFreshRead(MarketBoardReadResult freshRead, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(freshRead);

        if (!Status.Equals("WaitingForListingRemoval", StringComparison.OrdinalIgnoreCase))
            return this;

        var revalidation = MarketBoardPurchasePlanner.RevalidateCandidate(Candidate, freshRead);
        if (revalidation.Status.Equals("ListingMissing", StringComparison.OrdinalIgnoreCase))
        {
            return this with
            {
                Status = "Completed",
                Message = "Confirmed purchase: the guarded listing is no longer present in live market-board data.",
            };
        }

        if (nowUtc <= DeadlineUtc)
        {
            return this with
            {
                Message = "Purchase confirmation was accepted; waiting for live listings to reflect the purchase.",
            };
        }

        return this with
        {
            Status = "ListingRemovalTimeout",
            Message = $"Purchase confirmation was accepted, but the guarded listing is still present or unreadable: {revalidation.Message}",
        };
    }
}
