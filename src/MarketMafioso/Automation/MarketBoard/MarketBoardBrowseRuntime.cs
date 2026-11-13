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
    public int SessionRateLimitCount { get; init; }
    public bool SessionRelogRequired { get; init; }
    public DateTimeOffset? SessionExpiredAtUtc { get; init; }

    public bool IsActive =>
        Phase is not MarketBoardBrowsePhase.Idle
            and not MarketBoardBrowsePhase.Completed
            and not MarketBoardBrowsePhase.Failed;

    public bool IsComplete => Phase == MarketBoardBrowsePhase.Completed;
    public bool IsFailed => Phase == MarketBoardBrowsePhase.Failed;
    public bool IsTerminal => IsComplete || IsFailed;
}

public sealed record MarketBoardLockoutStatus
{
    public static MarketBoardLockoutStatus Clear { get; } = new();

    public bool Active { get; init; }
    public bool LogoutObserved { get; init; }
    public DateTimeOffset? LockedAtUtc { get; init; }
    public string? TriggerOwner { get; init; }
    public uint TriggerItemId { get; init; }
    public string? TriggerWorld { get; init; }
    public string? TriggerOperationId { get; init; }
}

public interface IMarketBoardBrowseRuntime
{
    bool IsAvailable { get; }
    string AvailabilityMessage { get; }
    MarketBoardBrowseSnapshot Snapshot { get; }

    /// <summary>The server-imposed market-board lockout that only a logout followed by a login clears.</summary>
    MarketBoardLockoutStatus Lockout { get; }

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

    public static TimeSpan GetInactivityTimeout(MarketBoardBrowseOwner owner) =>
        owner == MarketBoardBrowseOwner.RemoteAccessProbe
            ? TimeSpan.FromSeconds(120)
            : InactivityTimeout;
}

[Serializable]
public sealed class PersistedMarketBoardSessionCircuitBreakerState
{
    // Rate limits observed since the last relog, kept for diagnostics. Any nonzero count means relog-required.
    public int RateLimitCount { get; set; }
    public bool RelogRequired { get; set; }
    public bool LogoutObserved { get; set; }
    public DateTimeOffset? LastRateLimitAtUtc { get; set; }
    public DateTimeOffset? ExpiredAtUtc { get; set; }
    public string? TriggerOwner { get; set; }
    public uint TriggerItemId { get; set; }
    public string? TriggerWorld { get; set; }
    public string? TriggerOperationId { get; set; }
}

internal sealed class MarketBoardBrowseOperationGate
{
    internal const uint RateLimitStatus = 0x70000002;
    internal const string LockoutFailureCode = "MarketBoardSessionExpired";
    private const int ListingsPerPage = 10;
    private const int MaximumListings = 100;

    private readonly object sync = new();
    private readonly Func<DateTimeOffset> getUtcNow;
    private readonly PersistedMarketBoardSessionCircuitBreakerState sessionState;
    private readonly Action persistSessionState;
    private readonly Func<string?> getCurrentWorld;
    private readonly Action<string> log;
    private readonly string operationNamespace = Guid.NewGuid().ToString("N");
    private long operationSequence;
    private MarketBoardBrowseSnapshot snapshot = MarketBoardBrowseSnapshot.Idle;
    private readonly HashSet<byte> continuationTokens = [];

    public MarketBoardBrowseOperationGate(
        Func<DateTimeOffset>? getUtcNow = null,
        PersistedMarketBoardSessionCircuitBreakerState? sessionState = null,
        Action? persistSessionState = null,
        Func<string?>? getCurrentWorld = null,
        Action<string>? log = null)
    {
        this.getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        this.sessionState = sessionState ?? new PersistedMarketBoardSessionCircuitBreakerState();
        this.persistSessionState = persistSessionState ?? (() => { });
        this.getCurrentWorld = getCurrentWorld ?? (() => null);
        this.log = log ?? (_ => { });
        var repaired = false;
        if (this.sessionState.RateLimitCount < 0)
        {
            this.sessionState.RateLimitCount = 0;
            repaired = true;
        }
        // Earlier builds absorbed two strikes before expiring access, and a relog reset the count to zero.
        // A persisted nonzero count without RelogRequired therefore records a rate limit in a session that has
        // not relogged since, and that session is still locked out of the market board.
        if (!this.sessionState.RelogRequired && this.sessionState.RateLimitCount > 0)
        {
            this.sessionState.RelogRequired = true;
            repaired = true;
            this.log(
                $"Repaired a persisted market-board rate-limit count of {this.sessionState.RateLimitCount} " +
                "to a relog-required lockout; no relog has been observed since it was recorded.");
        }
        if (this.sessionState.RelogRequired)
        {
            if (this.sessionState.RateLimitCount < 1)
            {
                this.sessionState.RateLimitCount = 1;
                repaired = true;
            }
            if (this.sessionState.ExpiredAtUtc is null)
            {
                this.sessionState.ExpiredAtUtc = this.sessionState.LastRateLimitAtUtc ?? this.getUtcNow();
                repaired = true;
            }
        }
        if (repaired)
            this.persistSessionState();
    }

    public MarketBoardLockoutStatus Lockout
    {
        get
        {
            lock (sync)
            {
                return sessionState.RelogRequired
                    ? new MarketBoardLockoutStatus
                    {
                        Active = true,
                        LogoutObserved = sessionState.LogoutObserved,
                        LockedAtUtc = sessionState.ExpiredAtUtc,
                        TriggerOwner = sessionState.TriggerOwner,
                        TriggerItemId = sessionState.TriggerItemId,
                        TriggerWorld = sessionState.TriggerWorld,
                        TriggerOperationId = sessionState.TriggerOperationId,
                    }
                    : MarketBoardLockoutStatus.Clear;
            }
        }
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
            if (sessionState.RelogRequired)
            {
                result = CreateSessionExpiredSnapshot(owner, itemId);
                return false;
            }

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
                // Counters restart when the observer is recreated; evidence identities must not.
                OperationId = $"market-browse:{operationNamespace}:{++operationSequence}",
                Owner = owner,
                Phase = MarketBoardBrowsePhase.Armed,
                StartedAtUtc = nowUtc,
                DeadlineUtc = nowUtc + inactivityTimeout,
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

    /// <summary>
    /// Records the client session state when the runtime starts. Being logged out is conclusive evidence that
    /// the session which produced a persisted lockout has ended: the lobby clears its logged-in flag only in
    /// its logout callback, and a fresh game process starts logged out.
    /// </summary>
    public void ObserveInitialSession(bool isLoggedIn)
    {
        lock (sync)
        {
            if (isLoggedIn || !sessionState.RelogRequired || sessionState.LogoutObserved)
                return;

            sessionState.LogoutObserved = true;
            persistSessionState();
            log("MMF started while logged out; the market-board lockout clears at the next login.");
        }
    }

    public void ObserveLogout(int type, int code)
    {
        lock (sync)
        {
            if (!sessionState.RelogRequired)
            {
                log($"Logout observed (type {type}, code {code}); no market-board lockout is active.");
                return;
            }

            if (!sessionState.LogoutObserved)
            {
                sessionState.LogoutObserved = true;
                persistSessionState();
            }
            log($"Logout observed (type {type}, code {code}); the market-board lockout clears at the next login.");
        }
    }

    public bool ObserveLogin()
    {
        lock (sync)
        {
            if (!sessionState.RelogRequired)
            {
                log("Login observed; no market-board lockout is active.");
                return false;
            }

            if (!sessionState.LogoutObserved)
            {
                log("Login observed without a preceding logout; the market-board lockout remains.");
                return false;
            }

            var lockedAt = sessionState.ExpiredAtUtc;
            sessionState.RateLimitCount = 0;
            sessionState.RelogRequired = false;
            sessionState.LogoutObserved = false;
            sessionState.LastRateLimitAtUtc = null;
            sessionState.ExpiredAtUtc = null;
            sessionState.TriggerOwner = null;
            sessionState.TriggerItemId = 0;
            sessionState.TriggerWorld = null;
            sessionState.TriggerOperationId = null;
            persistSessionState();

            if (snapshot.IsFailed &&
                string.Equals(snapshot.FailureCode, LockoutFailureCode, StringComparison.Ordinal))
            {
                snapshot = MarketBoardBrowseSnapshot.Idle;
            }

            log($"Market-board lockout from {lockedAt:O} cleared by an observed logout followed by a login.");
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

    public void ObserveHeader(uint status, uint listingCount, byte? nativeRequestId = null)
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
                if (status == RateLimitStatus)
                {
                    RecordRateLimit();
                    Fail(
                        LockoutFailureCode,
                        "The market-board server locked MMF out (status 0x70000002). MMF will not search the market board again until you log out and back in.");
                }
                else
                {
                    Fail(
                        "ServerStatusRejected",
                        $"The market-board server rejected the browse with status 0x{status:X8}.");
                }
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
                RequestId = nativeRequestId,
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
            SessionRateLimitCount = sessionState.RateLimitCount,
            SessionRelogRequired = sessionState.RelogRequired,
            SessionExpiredAtUtc = sessionState.ExpiredAtUtc,
        };
    }

    private void RecordRateLimit()
    {
        var nowUtc = getUtcNow();
        string? world;
        try
        {
            world = getCurrentWorld();
        }
        catch (Exception)
        {
            world = null;
        }

        sessionState.RateLimitCount++;
        sessionState.LastRateLimitAtUtc = nowUtc;
        log(
            $"Market-board rate limit observed (status 0x{RateLimitStatus:X8}): owner {snapshot.Owner}, " +
            $"item {snapshot.ItemId}, world {world ?? "unknown"}, operation {snapshot.OperationId}.");
        if (!sessionState.RelogRequired)
        {
            // One server rate limit locks MMF out of the whole market board until a relog; no retry has
            // ever succeeded without one, so the first observation expires access immediately.
            sessionState.RelogRequired = true;
            sessionState.LogoutObserved = false;
            sessionState.ExpiredAtUtc = nowUtc;
            sessionState.TriggerOwner = snapshot.Owner?.ToString();
            sessionState.TriggerItemId = snapshot.ItemId;
            sessionState.TriggerWorld = world;
            sessionState.TriggerOperationId = snapshot.OperationId;
            log("Market-board lockout set: every MMF market search is refused until a logout followed by a login.");
        }
        persistSessionState();
    }

    private MarketBoardBrowseSnapshot CreateSessionExpiredSnapshot(
        MarketBoardBrowseOwner owner,
        uint itemId) => new()
    {
        Owner = owner,
        ItemId = itemId,
        Phase = MarketBoardBrowsePhase.Failed,
        FailureCode = LockoutFailureCode,
        Message = "The market-board server locked MMF out. Log out and back in before starting or resuming market searches.",
        SessionRateLimitCount = sessionState.RateLimitCount,
        SessionRelogRequired = true,
        SessionExpiredAtUtc = sessionState.ExpiredAtUtc,
    };

    private void RecordProgress()
    {
        if (snapshot.Owner is not { } owner)
            return;

        var nowUtc = getUtcNow();
        snapshot = snapshot with
        {
            LastProgressAtUtc = nowUtc,
            DeadlineUtc = nowUtc + MarketBoardBrowseTimeoutPolicy.GetInactivityTimeout(owner),
        };
    }
}
