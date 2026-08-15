using System;
using System.Collections.Generic;

namespace MarketMafioso.Automation.MarketBoard;

public enum MarketBoardBrowseOwner
{
    MarketAcquisition,
    MarketListingAcquisition,
    RetainerListingRefresh,
    RemoteAccessProbe,
}

public enum MarketBoardBrowsePhase
{
    Idle,
    Armed,
    ActivationDispatched,
    AwaitingHeader,
    AwaitingPagesAndHistory,
    AwaitingHistory,
    Completed,
    Failed,
}

public sealed record MarketBoardBrowseSnapshot
{
    public static MarketBoardBrowseSnapshot Idle { get; } = new();

    public string OperationId { get; init; } = string.Empty;
    public MarketBoardBrowseOwner? Owner { get; init; }
    public MarketBoardBrowsePhase Phase { get; init; } = MarketBoardBrowsePhase.Idle;
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
    public DateTimeOffset? AbsoluteDeadlineUtc { get; init; }
    public DateTimeOffset? LastProgressAtUtc { get; init; }
    public uint ItemId { get; init; }
    public bool ActivationClaimed { get; init; }
    public bool RequestObserved { get; init; }
    public bool RequestAccepted { get; init; }
    public bool HeaderObserved { get; init; }
    public uint HeaderStatus { get; init; }
    public int ExpectedListingCount { get; init; }
    public int ExpectedPageCount { get; init; }
    public byte? RequestId { get; init; }
    public int PageCount { get; init; }
    public int ListingCount { get; init; }
    public bool FirstPageObserved { get; init; }
    public bool TerminalPageObserved { get; init; }
    public bool HistoryObserved { get; init; }
    public uint? HistoryItemId { get; init; }
    public int HistoryEntryCount { get; init; }
    public IReadOnlyList<byte> ContinuationTokens { get; init; } = [];
    public string? FailureCode { get; init; }
    public string Message { get; init; } = "No market-board browse is active.";

    public bool IsActive =>
        Phase is not MarketBoardBrowsePhase.Idle
            and not MarketBoardBrowsePhase.Completed
            and not MarketBoardBrowsePhase.Failed;

    public bool IsComplete => Phase == MarketBoardBrowsePhase.Completed;
    public bool IsFailed => Phase == MarketBoardBrowsePhase.Failed;
    public bool IsTerminal => IsComplete || IsFailed;
}

public interface IMarketBoardBrowseRuntime
{
    bool IsAvailable { get; }
    string AvailabilityMessage { get; }
    MarketBoardBrowseSnapshot Snapshot { get; }

    bool TryBegin(
        MarketBoardBrowseOwner owner,
        uint itemId,
        out MarketBoardBrowseSnapshot snapshot);

    bool TryClaimActivation(
        MarketBoardBrowseOwner owner,
        uint itemId,
        out MarketBoardBrowseSnapshot snapshot);

    bool TryAbandon(
        MarketBoardBrowseOwner owner,
        string operationId,
        string reason,
        out MarketBoardBrowseSnapshot snapshot);
}

internal interface IHeadlessMarketBoardBrowseRuntime : IMarketBoardBrowseRuntime
{
    bool TryRequestExactItem(
        MarketBoardBrowseOwner owner,
        uint itemId,
        out MarketBoardBrowseSnapshot snapshot);
}

internal static class MarketBoardBrowseTimeoutPolicy
{
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(15);

    // One activation window plus accepted request, header, ten bounded listing pages, and history.
    // The inactivity deadline is the normal failure boundary; this remains an independent hard cap.
    public static readonly TimeSpan MarketAcquisitionAbsoluteTimeout = TimeSpan.FromTicks(
        InactivityTimeout.Ticks * 14);

    public static TimeSpan GetInactivityTimeout(MarketBoardBrowseOwner owner) =>
        owner == MarketBoardBrowseOwner.RemoteAccessProbe
            ? TimeSpan.FromSeconds(120)
            : InactivityTimeout;

    public static TimeSpan GetAbsoluteTimeout(MarketBoardBrowseOwner owner) => owner switch
    {
        MarketBoardBrowseOwner.MarketAcquisition => MarketAcquisitionAbsoluteTimeout,
        MarketBoardBrowseOwner.RemoteAccessProbe => TimeSpan.FromSeconds(120),
        _ => InactivityTimeout,
    };
}

internal sealed class MarketBoardBrowseOperationGate
{
    private const uint RateLimitStatus = 0x70000002;
    private const int ListingsPerPage = 10;
    private const int MaximumListings = 100;

    private readonly object sync = new();
    private readonly Func<DateTimeOffset> getUtcNow;
    private long operationSequence;
    private MarketBoardBrowseSnapshot snapshot = MarketBoardBrowseSnapshot.Idle;
    private readonly HashSet<byte> continuationTokens = [];

    public MarketBoardBrowseOperationGate(Func<DateTimeOffset>? getUtcNow = null)
    {
        this.getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public MarketBoardBrowseSnapshot Snapshot
    {
        get
        {
            lock (sync)
                return snapshot;
        }
    }

    public bool TryBegin(
        MarketBoardBrowseOwner owner,
        uint itemId,
        out MarketBoardBrowseSnapshot result)
    {
        lock (sync)
        {
            if (snapshot.IsActive)
            {
                result = snapshot;
                return false;
            }

            continuationTokens.Clear();
            var nowUtc = getUtcNow();
            var inactivityTimeout = MarketBoardBrowseTimeoutPolicy.GetInactivityTimeout(owner);
            snapshot = new MarketBoardBrowseSnapshot
            {
                OperationId = $"market-browse:{++operationSequence}",
                Owner = owner,
                Phase = MarketBoardBrowsePhase.Armed,
                StartedAtUtc = nowUtc,
                DeadlineUtc = nowUtc + inactivityTimeout,
                AbsoluteDeadlineUtc = nowUtc + MarketBoardBrowseTimeoutPolicy.GetAbsoluteTimeout(owner),
                LastProgressAtUtc = nowUtc,
                ItemId = itemId,
                Message = itemId == 0
                    ? "Armed for the next exact market-board RequestData call."
                    : $"Armed one market-board browse for item {itemId}.",
            };
            result = snapshot;
            return true;
        }
    }

    public bool TryAbandon(
        MarketBoardBrowseOwner owner,
        string operationId,
        string reason,
        out MarketBoardBrowseSnapshot result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (sync)
        {
            if (!snapshot.IsActive ||
                snapshot.Owner != owner ||
                !string.Equals(snapshot.OperationId, operationId, StringComparison.Ordinal))
            {
                result = snapshot;
                return false;
            }

            Fail("BrowseAbandoned", reason.Trim());
            result = snapshot;
            return true;
        }
    }

    public void Advance(DateTimeOffset nowUtc)
    {
        lock (sync)
        {
            if (!snapshot.IsActive)
            {
                return;
            }

            if (snapshot.Owner != MarketBoardBrowseOwner.MarketAcquisition)
            {
                if (snapshot.DeadlineUtc is { } fixedDeadline && nowUtc >= fixedDeadline)
                {
                    Fail(
                        "BrowseTimeout",
                        $"Market-board browse {snapshot.OperationId} timed out while in {snapshot.Phase}.");
                }
                return;
            }

            if (snapshot.AbsoluteDeadlineUtc is { } absoluteDeadline && nowUtc >= absoluteDeadline)
            {
                Fail(
                    "BrowseAbsoluteTimeout",
                    $"Market-board browse {snapshot.OperationId} reached its absolute limit while in {snapshot.Phase} after {snapshot.PageCount}/{snapshot.ExpectedPageCount} page(s).");
                return;
            }

            if (snapshot.DeadlineUtc is { } progressDeadline && nowUtc >= progressDeadline)
            {
                Fail(
                    "BrowseStalled",
                    $"Market-board browse {snapshot.OperationId} made no progress for {MarketBoardBrowseTimeoutPolicy.GetInactivityTimeout(snapshot.Owner!.Value).TotalSeconds:N0}s while in {snapshot.Phase} after {snapshot.PageCount}/{snapshot.ExpectedPageCount} page(s).");
            }
        }
    }

    public bool TryClaimActivation(
        MarketBoardBrowseOwner owner,
        uint itemId,
        out MarketBoardBrowseSnapshot result)
    {
        lock (sync)
        {
            if (snapshot.Phase != MarketBoardBrowsePhase.Armed ||
                snapshot.Owner != owner ||
                snapshot.ItemId == 0 ||
                snapshot.ItemId != itemId ||
                snapshot.ActivationClaimed)
            {
                result = snapshot;
                return false;
            }

            snapshot = snapshot with
            {
                Phase = MarketBoardBrowsePhase.ActivationDispatched,
                ActivationClaimed = true,
                Message = $"Dispatched the single permitted exact-item activation for item {itemId}; waiting for RequestData acceptance.",
            };
            result = snapshot;
            return true;
        }
    }

    public void ObserveRequest(uint itemId, bool accepted)
    {
        lock (sync)
        {
            if (!snapshot.IsActive)
                return;

            if (snapshot.RequestObserved)
            {
                Fail("RepeatedRequestData", "A second RequestData call overlapped the owned market-board browse.");
                return;
            }

            if (itemId == 0)
            {
                Fail("MissingRequestItem", "RequestData did not carry a nonzero searched item.");
                return;
            }

            if (snapshot.ItemId != 0 && snapshot.ItemId != itemId)
            {
                Fail(
                    "RequestItemMismatch",
                    $"RequestData targeted item {itemId}, but operation {snapshot.OperationId} owns item {snapshot.ItemId}.");
                return;
            }

            if (snapshot.Owner != MarketBoardBrowseOwner.RemoteAccessProbe &&
                !snapshot.ActivationClaimed)
            {
                Fail("UnownedRequestData", "RequestData arrived before the operation claimed its one exact-item activation.");
                return;
            }

            snapshot = snapshot with
            {
                ItemId = itemId,
                RequestObserved = true,
                RequestAccepted = accepted,
            };
            if (!accepted)
            {
                Fail("RequestRejected", $"The client rejected RequestData for item {itemId}.");
                return;
            }

            snapshot = snapshot with
            {
                Phase = MarketBoardBrowsePhase.AwaitingHeader,
                Message = $"RequestData accepted item {itemId}; waiting for the semantic result header.",
            };
            RecordProgress();
        }
    }

    public void ObserveHeader(uint status, uint listingCount)
    {
        lock (sync)
        {
            if (!snapshot.IsActive)
                return;

            if (!snapshot.RequestAccepted || snapshot.Phase != MarketBoardBrowsePhase.AwaitingHeader)
            {
                Fail("UnexpectedHeader", "A market-board result header arrived without one accepted owned request.");
                return;
            }

            if (snapshot.HeaderObserved)
            {
                Fail("RepeatedHeader", "More than one result header arrived for the owned market-board request.");
                return;
            }

            if (status != 0)
            {
                snapshot = snapshot with
                {
                    HeaderObserved = true,
                    HeaderStatus = status,
                };
                Fail(
                    status == RateLimitStatus ? "MarketBoardRateLimited" : "ServerStatusRejected",
                    status == RateLimitStatus
                        ? "The market-board server asked MMF to wait before searching again (status 0x70000002)."
                        : $"The market-board server rejected the browse with status 0x{status:X8}.");
                return;
            }

            if (listingCount > MaximumListings)
            {
                snapshot = snapshot with
                {
                    HeaderObserved = true,
                    HeaderStatus = status,
                    ExpectedListingCount = checked((int)listingCount),
                };
                Fail(
                    "ListingCountOutOfRange",
                    $"The result header declared {listingCount} listings; the fixed client cache supports at most {MaximumListings}.");
                return;
            }

            var expectedPageCount = checked((int)((listingCount + ListingsPerPage - 1) / ListingsPerPage));
            snapshot = snapshot with
            {
                HeaderObserved = true,
                HeaderStatus = status,
                ExpectedListingCount = checked((int)listingCount),
                ExpectedPageCount = expectedPageCount,
                TerminalPageObserved = expectedPageCount == 0,
                Phase = expectedPageCount == 0
                    ? MarketBoardBrowsePhase.AwaitingHistory
                    : MarketBoardBrowsePhase.AwaitingPagesAndHistory,
                Message = expectedPageCount == 0
                    ? "The server returned an authoritative zero-listing header; waiting for matching standard history."
                    : $"The server accepted {listingCount} listings across {expectedPageCount} expected page(s).",
            };
            RecordProgress();
            TryComplete();
        }
    }

    public void ObservePage(
        byte continuationToken,
        byte firstMarker,
        byte requestId,
        byte proxyCurrentRequestId,
        IReadOnlyList<uint> itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        lock (sync)
        {
            if (!snapshot.IsActive)
                return;

            if (!snapshot.HeaderObserved ||
                snapshot.HeaderStatus != 0 ||
                snapshot.ExpectedPageCount <= 0 ||
                snapshot.Phase is not MarketBoardBrowsePhase.AwaitingPagesAndHistory
                    and not MarketBoardBrowsePhase.AwaitingHistory)
            {
                Fail("UnexpectedPage", "A listings page arrived outside the accepted header lifecycle.");
                return;
            }

            if (snapshot.TerminalPageObserved)
            {
                Fail("PageAfterTerminal", "A listings page arrived after the terminal continuation token.");
                return;
            }

            var nextPageNumber = snapshot.PageCount + 1;
            if (nextPageNumber > snapshot.ExpectedPageCount)
            {
                Fail(
                    "PageOverrun",
                    $"Page {nextPageNumber} exceeded the header-bound limit of {snapshot.ExpectedPageCount}.");
                return;
            }

            if (requestId != proxyCurrentRequestId)
            {
                Fail(
                    "ProxyRequestIdMismatch",
                    $"Page request id {requestId} did not match proxy current request id {proxyCurrentRequestId}.");
                return;
            }

            if (snapshot.RequestId is { } ownedRequestId && ownedRequestId != requestId)
            {
                Fail(
                    "RequestIdDiscontinuity",
                    $"Page request id changed from {ownedRequestId} to {requestId} inside one browse.");
                return;
            }

            if (nextPageNumber == 1 && firstMarker != 0)
            {
                Fail(
                    "MissingFirstPageMarker",
                    $"The first correlated page carried marker {firstMarker} instead of zero.");
                return;
            }

            if (nextPageNumber > 1 && firstMarker == 0)
            {
                Fail("RepeatedFirstPageMarker", "A continuation page repeated the zero first-page marker.");
                return;
            }

            var expectedListingsOnPage = Math.Min(
                ListingsPerPage,
                snapshot.ExpectedListingCount - snapshot.ListingCount);
            if (itemIds.Count != expectedListingsOnPage)
            {
                Fail(
                    "PageListingCountMismatch",
                    $"Page {nextPageNumber} carried {itemIds.Count} real listing(s), but the header requires {expectedListingsOnPage}.");
                return;
            }

            foreach (var itemId in itemIds)
            {
                if (itemId != snapshot.ItemId)
                {
                    Fail(
                        "PageItemMismatch",
                        $"Page {nextPageNumber} carried item {itemId}, but operation {snapshot.OperationId} owns item {snapshot.ItemId}.");
                    return;
                }
            }

            var isExpectedFinalPage = nextPageNumber == snapshot.ExpectedPageCount;
            if (!isExpectedFinalPage && continuationToken == 0)
            {
                Fail(
                    "EarlyTerminalPage",
                    $"Page {nextPageNumber} terminated before the header-bound page count {snapshot.ExpectedPageCount}.");
                return;
            }

            if (isExpectedFinalPage && continuationToken != 0)
            {
                Fail(
                    "MissingTerminalPage",
                    $"Final expected page {nextPageNumber} requested continuation token {continuationToken}.");
                return;
            }

            if (continuationToken != 0 && !continuationTokens.Add(continuationToken))
            {
                Fail(
                    "RepeatedContinuationToken",
                    $"Continuation token {continuationToken} repeated inside one browse.");
                return;
            }

            snapshot = snapshot with
            {
                RequestId = requestId,
                PageCount = nextPageNumber,
                ListingCount = snapshot.ListingCount + itemIds.Count,
                FirstPageObserved = true,
                TerminalPageObserved = continuationToken == 0,
                ContinuationTokens = [.. continuationTokens],
                Phase = continuationToken == 0
                    ? MarketBoardBrowsePhase.AwaitingHistory
                    : MarketBoardBrowsePhase.AwaitingPagesAndHistory,
                Message = continuationToken == 0
                    ? $"Observed the terminal page for item {snapshot.ItemId}; waiting for matching standard history."
                    : $"Observed correlated page {nextPageNumber}/{snapshot.ExpectedPageCount} for item {snapshot.ItemId}.",
            };
            RecordProgress();
            TryComplete();
        }
    }

    public void ObserveHistory(uint itemId, bool structurallyValid, int entryCount = 0)
    {
        lock (sync)
        {
            if (!snapshot.IsActive)
                return;

            if (!snapshot.RequestAccepted)
            {
                Fail("UnexpectedHistory", "Standard history arrived without one accepted owned request.");
                return;
            }

            if (snapshot.HistoryObserved)
            {
                Fail("RepeatedHistory", "More than one standard-history packet arrived for the owned browse.");
                return;
            }

            if (itemId == 0 || itemId != snapshot.ItemId)
            {
                Fail(
                    "HistoryItemMismatch",
                    $"Standard history targeted item {itemId}, but operation {snapshot.OperationId} owns item {snapshot.ItemId}.");
                return;
            }

            if (!structurallyValid)
            {
                Fail(
                    "InvalidHistoryShape",
                    $"Standard history for item {itemId} contained a nonzero-price, zero-quantity row.");
                return;
            }

            snapshot = snapshot with
            {
                HistoryObserved = true,
                HistoryItemId = itemId,
                HistoryEntryCount = entryCount,
                Message = $"Observed matching standard history for item {itemId}; waiting for listing completion.",
            };
            RecordProgress();
            TryComplete();
        }
    }

    private void TryComplete()
    {
        if (!snapshot.HeaderObserved ||
            snapshot.HeaderStatus != 0 ||
            !snapshot.TerminalPageObserved ||
            !snapshot.HistoryObserved)
        {
            return;
        }

        if (snapshot.PageCount != snapshot.ExpectedPageCount ||
            snapshot.ListingCount != snapshot.ExpectedListingCount)
        {
            Fail(
                "IncompletePageSet",
                $"Terminal evidence contained {snapshot.PageCount}/{snapshot.ExpectedPageCount} page(s) and {snapshot.ListingCount}/{snapshot.ExpectedListingCount} listing(s).");
            return;
        }

        snapshot = snapshot with
        {
            Phase = MarketBoardBrowsePhase.Completed,
            Message = snapshot.ExpectedListingCount == 0
                ? $"Verified an authoritative empty market for item {snapshot.ItemId}."
                : $"Verified {snapshot.ExpectedListingCount} listing(s) and matching standard history for item {snapshot.ItemId}.",
        };
    }

    private void Fail(string code, string message)
    {
        snapshot = snapshot with
        {
            Phase = MarketBoardBrowsePhase.Failed,
            FailureCode = code,
            Message = message,
        };
    }

    private void RecordProgress()
    {
        if (snapshot.Owner is not { } owner)
            return;

        var nowUtc = getUtcNow();
        var absoluteDeadline = snapshot.AbsoluteDeadlineUtc ??
                               nowUtc + MarketBoardBrowseTimeoutPolicy.GetAbsoluteTimeout(owner);
        var candidateDeadline = nowUtc + MarketBoardBrowseTimeoutPolicy.GetInactivityTimeout(owner);
        snapshot = snapshot with
        {
            LastProgressAtUtc = nowUtc,
            DeadlineUtc = candidateDeadline < absoluteDeadline ? candidateDeadline : absoluteDeadline,
            AbsoluteDeadlineUtc = absoluteDeadline,
        };
    }
}
