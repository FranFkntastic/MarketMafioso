using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.MarketDiagnostics;

internal sealed record RetainerListingRefreshCandidate(uint ItemId, string? ItemName);

internal sealed record RetainerListingRefreshSnapshot(
    IReadOnlyList<RetainerListingRefreshCandidate> Items,
    string CaptureId,
    bool ComparisonAvailable = true);

internal interface IRetainerListingRefreshSource
{
    bool RetryOnReadFailure => true;
    bool SurfaceReadFailure => false;
    bool TryRead(out RetainerListingRefreshSnapshot? snapshot, out string error);
}

internal sealed class RetainerListingRefreshCoordinator
{
    private static readonly TimeSpan CaptureReadRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TransientDeferral = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RejectedRequestDeferral = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan AmbiguousRequestDeferral = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MissingOperationReconciliationDelay = TimeSpan.FromSeconds(20);
    internal const string WaitingForRelogCode = "WaitingForRelog";

    private readonly Configuration config;
    private readonly IRetainerListingRefreshSource source;
    private readonly IHeadlessMarketBoardBrowseRuntime browseRuntime;
    private readonly Action persist;
    private readonly Action<string> notifyAttention;
    private readonly Action<string> log;
    private readonly Func<TimeSpan> nextSuccessDelay;
    private int captureReadRequested = 1;
    private DateTimeOffset nextCaptureReadAt;

    public RetainerListingRefreshCoordinator(
        Configuration config,
        IRetainerListingRefreshSource source,
        IHeadlessMarketBoardBrowseRuntime browseRuntime,
        Action persist,
        Action<string> notifyAttention,
        Action<string>? log = null,
        Func<TimeSpan>? nextSuccessDelay = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.browseRuntime = browseRuntime ?? throw new ArgumentNullException(nameof(browseRuntime));
        this.persist = persist ?? throw new ArgumentNullException(nameof(persist));
        this.notifyAttention = notifyAttention ?? throw new ArgumentNullException(nameof(notifyAttention));
        this.log = log ?? (_ => { });
        this.nextSuccessDelay = nextSuccessDelay ?? (() =>
            TimeSpan.FromMilliseconds(Random.Shared.Next(2500, 4501)));
        config.RetainerListingRefresh ??= new PersistedRetainerListingRefreshState();
        NormalizePersistedState();
    }

    public bool IsEnabled =>
        MarketAcquisitionUnlock.IsUnlocked(config) &&
        config.EnableRetainerListingRefresh;

    public void Tick(
        DateTimeOffset nowUtc,
        bool dispatchReady,
        string? dispatchDeferredReason = null)
    {
        if (!IsEnabled)
            return;

        ObserveLatestCapture(nowUtc);

        var state = config.RetainerListingRefresh;

        if (ObserveActiveRequest(nowUtc))
            return;

        RecoverCooledDownItems(nowUtc);

        if (ObserveMarketLockout(nowUtc))
            return;

        if (browseRuntime.Snapshot.IsActive)
            return;

        var next = state.Items
            .Where(item =>
                item.State == RetainerListingRefreshItemState.Deferred &&
                (item.NextAttemptAtUtc is null || nowUtc >= item.NextAttemptAtUtc))
            .OrderBy(item => item.NextAttemptAtUtc ?? DateTime.MinValue)
            .ThenBy(item => item.ItemId)
            .FirstOrDefault();
        if (next is null)
            return;

        if (!dispatchReady)
        {
            next.NextAttemptAtUtc = nowUtc.UtcDateTime + TransientDeferral;
            UpdateStatus(
                "Deferred",
                string.IsNullOrWhiteSpace(dispatchDeferredReason)
                    ? $"Waiting for a safe moment to refresh {FormatItem(next)}."
                    : dispatchDeferredReason.Trim(),
                persistState: true);
            return;
        }

        Dispatch(next, nowUtc);
    }

    public void NotifyListingCaptureChanged() =>
        System.Threading.Interlocked.Exchange(ref captureReadRequested, 1);

    /// <summary>Items that keep refresh intent and are not terminally blocked.</summary>
    public int PendingCount => config.RetainerListingRefresh.Items.Count(item =>
        item.State != RetainerListingRefreshItemState.Blocked);

    public int BlockedCount => config.RetainerListingRefresh.Items.Count(item =>
        item.State == RetainerListingRefreshItemState.Blocked);

    public bool ForceRetry(uint itemId, DateTimeOffset? nowUtc = null)
    {
        var state = config.RetainerListingRefresh;
        var item = state.Items.FirstOrDefault(candidate =>
            candidate.ItemId == itemId &&
            candidate.State == RetainerListingRefreshItemState.Blocked);
        if (item is null)
        {
            log($"User force retry for item {itemId} refused: the item is not blocked.");
            return false;
        }

        log($"User force retry accepted for {FormatItem(item)} (blocked by {item.LastCode}).");

        item.State = RetainerListingRefreshItemState.Deferred;
        item.NextAttemptAtUtc = (nowUtc ?? DateTimeOffset.UtcNow).UtcDateTime;
        item.OperationId = null;
        item.AttentionNotified = false;

        var remainingBlocked = state.Items.Count(candidate =>
            candidate.State == RetainerListingRefreshItemState.Blocked);
        state.NeedsAttention = remainingBlocked > 0;
        state.AttentionNotified = false;
        UpdateStatus(
            remainingBlocked > 0 ? "Blocked" : "Queued",
            remainingBlocked > 0
                ? $"Queued a force retry for {FormatItem(item)}; {remainingBlocked} other item(s) remain blocked."
                : $"Queued a force retry for {FormatItem(item)}.",
            persistState: true);
        return true;
    }

    private void ObserveLatestCapture(DateTimeOffset nowUtc)
    {
        if (System.Threading.Volatile.Read(ref captureReadRequested) == 0 && nowUtc < nextCaptureReadAt)
            return;

        System.Threading.Interlocked.Exchange(ref captureReadRequested, 0);
        nextCaptureReadAt = DateTimeOffset.MaxValue;
        if (!source.TryRead(out var snapshot, out var readError))
        {
            nextCaptureReadAt = source.RetryOnReadFailure
                ? nowUtc + CaptureReadRetryDelay
                : DateTimeOffset.MaxValue;
            if (source.SurfaceReadFailure)
            {
                UpdateStatus(
                    "ListingEvidenceUnavailable",
                    string.IsNullOrWhiteSpace(readError)
                        ? "Shared retainer-listing evidence is unavailable."
                        : readError,
                    persistState: true);
            }
            return;
        }

        var state = config.RetainerListingRefresh;
        if (string.Equals(state.LastObservedCaptureId, snapshot!.CaptureId, StringComparison.Ordinal))
            return;

        if (!snapshot.ComparisonAvailable)
        {
            state.LastObservedCaptureId = snapshot.CaptureId;
            state.NeedsAttention = state.Items.Any(item => item.State == RetainerListingRefreshItemState.Blocked);
            state.AttentionNotified = false;
            UpdateStatus(
                "BaselineEstablished",
                "Captured the current retainer listings as a baseline; no background market request was queued.",
                persistState: true);
            return;
        }

        var candidates = snapshot.Items
            .Where(item => item.ItemId != 0)
            .GroupBy(item => item.ItemId)
            .Select(group => new RetainerListingRefreshCandidate(
                group.Key,
                group.Select(item => item.ItemName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))))
            .OrderBy(item => item.ItemId)
            .ToArray();
        foreach (var candidate in candidates)
        {
            var existing = state.Items.FirstOrDefault(item => item.ItemId == candidate.ItemId);
            if (existing is not null)
            {
                if (string.IsNullOrWhiteSpace(existing.ItemName))
                    existing.ItemName = candidate.ItemName;
                if (existing.State == RetainerListingRefreshItemState.Blocked)
                {
                    // A new listing observation is fresh intent; it supersedes the evidence that blocked the item.
                    var blockedBy = existing.LastCode;
                    existing.State = RetainerListingRefreshItemState.Deferred;
                    existing.NextAttemptAtUtc = nowUtc.UtcDateTime;
                    existing.Attempts = 0;
                    existing.OperationId = null;
                    existing.AttentionNotified = false;
                    existing.LastCode = "RequeuedFromListingDelta";
                    existing.LastMessage = "Re-queued because a new retainer-listing observation included this item.";
                    log($"Retainer listing refresh re-queued {FormatItem(existing)} from a new listing observation (was blocked by {blockedBy}).");
                }
                continue;
            }

            state.Items.Add(new PersistedRetainerListingRefreshItem
            {
                ItemId = candidate.ItemId,
                ItemName = candidate.ItemName,
                State = RetainerListingRefreshItemState.Deferred,
                NextAttemptAtUtc = nowUtc.UtcDateTime,
                LastCode = "QueuedFromListingDelta",
                LastMessage = "Queued because a trusted retainer-listing observation changed.",
            });
        }

        state.LastObservedCaptureId = snapshot.CaptureId;
        state.NeedsAttention = state.Items.Any(item => item.State == RetainerListingRefreshItemState.Blocked);
        state.AttentionNotified = false;
        UpdateStatus(
            candidates.Length == 0 ? "Idle" : "Queued",
            candidates.Length == 0
                ? "No retainer listing changes need a background refresh."
                : $"Queued {candidates.Length} changed listed item(s) for background refresh.",
            persistState: true);
    }

    private bool ObserveActiveRequest(DateTimeOffset nowUtc)
    {
        var state = config.RetainerListingRefresh;
        var active = state.Items.FirstOrDefault(item =>
            item.State == RetainerListingRefreshItemState.AwaitingEvidence);
        if (active is null)
            return false;

        var browse = browseRuntime.Snapshot;
        if (string.Equals(browse.OperationId, active.OperationId, StringComparison.Ordinal))
        {
            if (!browse.IsTerminal)
                return true;
            if (browse.IsComplete)
            {
                Complete(active, browse, nowUtc);
                return false;
            }

            HandleFailure(active, browse.FailureCode ?? "UnknownBrowseFailure", browse.Message, nowUtc);
            return false;
        }

        if (active.LastAttemptAtUtc is not { } lastAttempt ||
            nowUtc < lastAttempt + MissingOperationReconciliationDelay)
        {
            return true;
        }

        active.State = RetainerListingRefreshItemState.NeedsReconciliation;
        active.NextAttemptAtUtc = nowUtc.UtcDateTime + AmbiguousRequestDeferral;
        active.LastCode = "OperationEvidenceLost";
        active.LastMessage =
            "The in-memory browse operation no longer matches the persisted refresh. " +
            "MMF will wait before making one fresh read-only attempt.";
        active.OperationId = null;
        UpdateStatus("Reconciling", active.LastMessage, persistState: true);
        return false;
    }

    private void RecoverCooledDownItems(DateTimeOffset nowUtc)
    {
        var changed = false;
        foreach (var item in config.RetainerListingRefresh.Items.Where(item =>
                     item.State == RetainerListingRefreshItemState.NeedsReconciliation &&
                     item.NextAttemptAtUtc is { } next &&
                     nowUtc >= next))
        {
            if (item.Attempts >= 2)
            {
                Block(
                    item,
                    "AmbiguousBrowseRepeated",
                    "Two accepted or ambiguous background browses failed to produce owned completion evidence.");
            }
            else
            {
                item.State = RetainerListingRefreshItemState.Deferred;
                item.NextAttemptAtUtc = nowUtc.UtcDateTime;
                item.LastCode = "ReconciledForRetry";
                item.LastMessage = "The ambiguity cooldown elapsed; one fresh read-only attempt is permitted.";
            }
            changed = true;
        }

        if (changed)
            persist();
    }

    private void Dispatch(PersistedRetainerListingRefreshItem item, DateTimeOffset nowUtc)
    {
        var accepted = browseRuntime.TryRequestExactItem(
            MarketBoardBrowseOwner.RetainerListingRefresh,
            item.ItemId,
            out var browse);

        if (browse.IsActive &&
            browse.Owner != MarketBoardBrowseOwner.RetainerListingRefresh)
        {
            item.NextAttemptAtUtc = nowUtc.UtcDateTime + TransientDeferral;
            UpdateStatus(
                "Deferred",
                "Another owned market browse is active; the retainer listing refresh remains queued.",
                persistState: true);
            return;
        }

        if (browse.ActivationClaimed || browse.RequestObserved)
        {
            item.Attempts++;
            item.LastAttemptAtUtc = nowUtc.UtcDateTime;
        }

        if (accepted)
        {
            item.State = RetainerListingRefreshItemState.AwaitingEvidence;
            item.OperationId = browse.OperationId;
            item.LastCode = "AwaitingEvidence";
            item.LastMessage = browse.Message;
            UpdateStatus(
                "Refreshing",
                $"Refreshing {FormatItem(item)} in the background.",
                persistState: true);
            return;
        }

        var code = browse.FailureCode ?? "BackgroundDispatchDeferred";
        var message = string.IsNullOrWhiteSpace(browse.Message)
            ? $"The background refresh for {FormatItem(item)} was deferred before RequestData acceptance."
            : browse.Message;
        HandleFailure(item, code, message, nowUtc);
    }

    private void Complete(
        PersistedRetainerListingRefreshItem item,
        MarketBoardBrowseSnapshot browse,
        DateTimeOffset nowUtc)
    {
        var state = config.RetainerListingRefresh;
        state.Items.Remove(item);
        state.LastCompletedAtUtc = nowUtc.UtcDateTime;
        var nextAt = nowUtc.UtcDateTime + nextSuccessDelay();
        foreach (var pending in state.Items.Where(pending =>
                     pending.State == RetainerListingRefreshItemState.Deferred &&
                     (pending.NextAttemptAtUtc is null || pending.NextAttemptAtUtc < nextAt)))
        {
            pending.NextAttemptAtUtc = nextAt;
        }

        state.NeedsAttention = state.Items.Any(pending =>
            pending.State == RetainerListingRefreshItemState.Blocked);
        state.AttentionNotified = false;
        UpdateStatus(
            state.Items.Count == 0 ? "Current" : "Refreshing",
            state.Items.Count == 0
                ? $"Refreshed {FormatItem(item)} with {browse.ExpectedListingCount} listing(s) and matching sale history."
                : $"Refreshed {FormatItem(item)}; {state.Items.Count} queued item(s) remain.",
            persistState: true);
        log(
            $"Retainer listing refresh completed for item {item.ItemId}: " +
            $"{browse.ExpectedListingCount} listings, {browse.PageCount} pages, operation {browse.OperationId}.");
    }

    private void HandleFailure(
        PersistedRetainerListingRefreshItem item,
        string code,
        string message,
        DateTimeOffset nowUtc)
    {
        item.OperationId = null;
        item.LastCode = code;
        item.LastMessage = message;

        switch (code)
        {
            case "MarketBoardUiActive":
            case "InfoProxyUnavailable":
            case "BrowseAbandoned":
            case "BackgroundDispatchDeferred":
                Defer(item, nowUtc, TransientDeferral, code, message);
                return;

            case "RequestRejected":
                if (item.Attempts < 3)
                {
                    Defer(item, nowUtc, RejectedRequestDeferral, code, message);
                    return;
                }
                Block(item, code, "RequestData rejected three spaced background attempts.");
                return;

            case MarketBoardBrowseOperationGate.LockoutFailureCode:
            case "MarketBoardRateLimited":
                WaitForRelog(item, nowUtc, message);
                return;

            case "BrowseTimeout":
            case "OperationEvidenceLost":
                item.State = RetainerListingRefreshItemState.NeedsReconciliation;
                item.NextAttemptAtUtc = nowUtc.UtcDateTime + AmbiguousRequestDeferral;
                UpdateStatus(
                    "Reconciling",
                    $"Completion evidence for {FormatItem(item)} was ambiguous. MMF will wait before one fresh attempt.",
                    persistState: true);
                return;

            default:
                Block(item, code, message);
                return;
        }
    }

    private void Defer(
        PersistedRetainerListingRefreshItem item,
        DateTimeOffset nowUtc,
        TimeSpan delay,
        string code,
        string message)
    {
        item.State = RetainerListingRefreshItemState.Deferred;
        item.NextAttemptAtUtc = nowUtc.UtcDateTime + delay;
        item.LastCode = code;
        item.LastMessage = message;
        UpdateStatus(
            "Deferred",
            $"Background refresh for {FormatItem(item)} was deferred safely; no immediate retry will occur.",
            persistState: true);
    }

    private void WaitForRelog(
        PersistedRetainerListingRefreshItem item,
        DateTimeOffset nowUtc,
        string message)
    {
        // The lockout is market-wide and says nothing about this item. Keep the intent and never retry before
        // the relog; the queue-level pause owns the wait.
        item.State = RetainerListingRefreshItemState.Deferred;
        item.NextAttemptAtUtc = nowUtc.UtcDateTime;
        item.Attempts = 0;
        item.OperationId = null;
        item.LastCode = WaitingForRelogCode;
        item.LastMessage = message;
        item.AttentionNotified = false;
        log($"Retainer listing refresh for {FormatItem(item)} is waiting for a relog after a market-board lockout.");
        persist();
    }

    private bool ObserveMarketLockout(DateTimeOffset nowUtc)
    {
        var state = config.RetainerListingRefresh;
        var lockout = browseRuntime.Lockout;
        if (lockout.Active)
        {
            var waiting = PendingCount;
            var message = $"Waiting for relog: the market board locked MMF out. {waiting} item(s) keep their refresh.";
            if (!state.WaitingForRelog)
            {
                state.WaitingForRelog = true;
                state.WaitingForRelogSinceUtc = nowUtc.UtcDateTime;
                log($"Retainer listing refresh queue paused for relog; {waiting} item(s) waiting.");
            }
            if (!string.Equals(state.StatusCode, WaitingForRelogCode, StringComparison.Ordinal) ||
                !string.Equals(state.StatusMessage, message, StringComparison.Ordinal))
            {
                UpdateStatus(WaitingForRelogCode, message, persistState: true);
            }
            return true;
        }

        if (!state.WaitingForRelog)
            return false;

        state.WaitingForRelog = false;
        state.WaitingForRelogSinceUtc = null;
        foreach (var pending in state.Items.Where(pending =>
                     pending.State == RetainerListingRefreshItemState.Deferred &&
                     string.Equals(pending.LastCode, WaitingForRelogCode, StringComparison.Ordinal)))
        {
            pending.NextAttemptAtUtc = nowUtc.UtcDateTime;
        }
        var resumed = PendingCount;
        log($"Retainer listing refresh queue resumed after relog; {resumed} item(s) queued.");
        UpdateStatus(
            resumed == 0 ? "Idle" : "Queued",
            resumed == 0
                ? "Market access restored; no retainer listing refresh is pending."
                : $"Market access restored; {resumed} queued item(s) will refresh.",
            persistState: true);
        return false;
    }

    private void Block(PersistedRetainerListingRefreshItem item, string code, string message)
    {
        item.State = RetainerListingRefreshItemState.Blocked;
        item.NextAttemptAtUtc = null;
        item.OperationId = null;
        item.LastCode = code;
        item.LastMessage = message;
        config.RetainerListingRefresh.NeedsAttention = true;
        UpdateStatus(
            "Blocked",
            $"Background refresh for {FormatItem(item)} stopped: {message}",
            persistState: true);
        if (item.AttentionNotified)
            return;

        item.AttentionNotified = true;
        persist();
        notifyAttention(
            $"Retainer listing refresh stopped for {FormatItem(item)} ({code}). " +
            "MMF will not retry contradictory evidence automatically.");
    }

    private void NormalizePersistedState()
    {
        var state = config.RetainerListingRefresh;
        var recoveredLockoutBlocks = 0;
        state.Items ??= [];
        state.SessionListings ??= [];
        state.CapturePending = false;
        state.SessionStartedAtUtc = null;
        state.SessionClosedAtUtc = null;
        state.CaptureNotBeforeUtc = null;
        state.CaptureAttempts = 0;
        state.SessionSnapshotProviderInstanceId = null;
        state.SessionSnapshotRevision = null;
        state.SessionListingsObservedAtUtc = null;
        state.SessionListings.Clear();
        if (state.StatusCode is "SnapshotDeferred" or "VerifyingListings")
        {
            state.StatusCode = "Idle";
            state.StatusMessage = "No retainer listing refresh is pending.";
            state.NeedsAttention = state.Items.Any(item => item.State == RetainerListingRefreshItemState.Blocked);
            state.AttentionNotified = false;
        }
        foreach (var item in state.Items)
        {
            if (item.State == RetainerListingRefreshItemState.AwaitingEvidence)
            {
                item.State = RetainerListingRefreshItemState.NeedsReconciliation;
                item.OperationId = null;
                item.NextAttemptAtUtc ??= DateTime.UtcNow + AmbiguousRequestDeferral;
                item.LastCode = "RecoveredAfterRestart";
                item.LastMessage =
                    "MMF restarted while a read-only browse was active; the item is cooling down before reconciliation.";
            }
            else if (item.State == RetainerListingRefreshItemState.Blocked &&
                      item.LastCode is NativeCapability.FailureCode or "UnsupportedGameBuild" &&
                      browseRuntime.IsAvailable)
            {
                item.State = RetainerListingRefreshItemState.Deferred;
                item.NextAttemptAtUtc = DateTime.UtcNow;
                item.LastCode = "BuildContractRecovered";
                item.LastMessage = "The native browse capability is available again.";
                item.AttentionNotified = false;
            }
            else if (item.State is RetainerListingRefreshItemState.Blocked or RetainerListingRefreshItemState.Deferred &&
                     IsLockoutRecord(item))
            {
                // Earlier builds blocked items terminally (or ran a retry ladder) on a market-wide lockout.
                // The lockout says nothing about the item, so it rejoins the queue behind the relog pause.
                var wasBlocked = item.State == RetainerListingRefreshItemState.Blocked;
                item.State = RetainerListingRefreshItemState.Deferred;
                item.NextAttemptAtUtc = DateTime.UtcNow;
                item.Attempts = 0;
                item.LastCode = WaitingForRelogCode;
                item.LastMessage = "Recovered from a market-board lockout; waits for a relog before refreshing.";
                item.AttentionNotified = false;
                if (wasBlocked)
                    recoveredLockoutBlocks++;
            }
            item.RateLimitFailures = 0;
        }

        state.NeedsAttention = state.Items.Any(item => item.State == RetainerListingRefreshItemState.Blocked);
        if (recoveredLockoutBlocks > 0)
        {
            state.StatusCode = "Deferred";
            state.StatusMessage = $"Recovered {recoveredLockoutBlocks} item(s) blocked by a market-board lockout.";
            state.AttentionNotified = false;
            log($"Retainer listing refresh recovered {recoveredLockoutBlocks} item(s) that an earlier build blocked on a market-board lockout.");
            persist();
        }
    }

    private static bool IsLockoutRecord(PersistedRetainerListingRefreshItem item) =>
        item.LastCode is MarketBoardBrowseOperationGate.LockoutFailureCode or "MarketBoardRateLimited" or "RecoveredRateLimit" ||
        (string.Equals(item.LastCode, "ServerStatusRejected", StringComparison.Ordinal) &&
         item.LastMessage?.Contains("0x70000002", StringComparison.OrdinalIgnoreCase) == true);

    private void UpdateStatus(string code, string message, bool persistState)
    {
        var state = config.RetainerListingRefresh;
        state.StatusCode = code;
        state.StatusMessage = message;
        if (persistState)
            persist();
    }

    private static string FormatItem(PersistedRetainerListingRefreshItem item) =>
        string.IsNullOrWhiteSpace(item.ItemName)
            ? $"item {item.ItemId}"
            : item.ItemName!;
}
