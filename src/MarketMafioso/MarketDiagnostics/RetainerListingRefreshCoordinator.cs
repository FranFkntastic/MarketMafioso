using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Quartermaster;

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

internal sealed class QuartermasterRetainerListingRefreshSource(
    QuartermasterIpcClient quartermaster,
    IPlayerState playerState) : IRetainerListingRefreshSource
{
    public bool TryRead(out RetainerListingRefreshSnapshot? result, out string error)
    {
        result = null;
        error = string.Empty;
        var owner = new QuartermasterOwnerScope(
            playerState.ContentId == 0 ? null : playerState.ContentId,
            playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.RowId : null,
            playerState.CharacterName,
            playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : null);
        if (!owner.IsAvailable)
        {
            error = "The current character identity is unavailable.";
            return false;
        }

        if (!quartermaster.TryGetSnapshot(out var snapshot, out error))
            return false;
        if (!owner.Matches(snapshot!.Owner))
        {
            error = "Quartermaster's snapshot belongs to a different character.";
            return false;
        }

        if (snapshot.LatestRetainerListingCapture is not { } capture)
        {
            error = "Quartermaster has not published a retainer-listing capture yet.";
            return false;
        }
        result = new RetainerListingRefreshSnapshot(
            capture.Items
                .Select(item => new RetainerListingRefreshCandidate(item.ItemId, item.ItemName))
                .ToArray(),
            capture.CaptureId,
            capture.ComparisonAvailable);
        return true;
    }
}

internal sealed class RetainerListingRefreshCoordinator
{
    private static readonly TimeSpan CaptureReadRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TransientDeferral = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RejectedRequestDeferral = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan AmbiguousRequestDeferral = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MissingOperationReconciliationDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RateLimitInitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RateLimitMaximumRetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RateLimitFailureCooldown = TimeSpan.FromSeconds(30);

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

    public bool ForceRetry(uint itemId, DateTimeOffset? nowUtc = null)
    {
        var state = config.RetainerListingRefresh;
        var item = state.Items.FirstOrDefault(candidate =>
            candidate.ItemId == itemId &&
            candidate.State == RetainerListingRefreshItemState.Blocked);
        if (item is null)
            return false;

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

            case "MarketBoardRateLimited":
                DeferRateLimited(item, nowUtc, message);
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

    private void DeferRateLimited(
        PersistedRetainerListingRefreshItem item,
        DateTimeOffset nowUtc,
        string message)
    {
        item.RateLimitFailures++;
        var delay = GetRateLimitRetryDelay(item.RateLimitFailures);
        var retryAt = nowUtc.UtcDateTime + delay;
        item.State = RetainerListingRefreshItemState.Deferred;
        item.NextAttemptAtUtc = retryAt;
        item.OperationId = null;
        item.LastCode = "MarketBoardRateLimited";
        item.LastMessage = message;
        item.AttentionNotified = false;

        // CMB's recovery governor pauses every request behind one shared cooldown.
        // Keep this item first so the retry proves the same request before the batch advances.
        var followingRetryAt = retryAt.AddTicks(1);
        foreach (var pending in config.RetainerListingRefresh.Items.Where(pending =>
                     !ReferenceEquals(pending, item) &&
                     pending.State == RetainerListingRefreshItemState.Deferred &&
                     (pending.NextAttemptAtUtc is null || pending.NextAttemptAtUtc < followingRetryAt)))
        {
            pending.NextAttemptAtUtc = followingRetryAt;
        }

        config.RetainerListingRefresh.NeedsAttention = config.RetainerListingRefresh.Items.Any(pending =>
            pending.State == RetainerListingRefreshItemState.Blocked);
        UpdateStatus(
            "RateLimited",
            $"The market board asked MMF to wait; retrying {FormatItem(item)} in {delay.TotalSeconds:F0}s.",
            persistState: true);
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
        var recoveredRateLimits = 0;
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
                      item.LastCode == GamePatchCompatibility.FailureCode &&
                      browseRuntime.IsAvailable)
            {
                item.State = RetainerListingRefreshItemState.Deferred;
                item.NextAttemptAtUtc = DateTime.UtcNow;
                item.LastCode = "BuildContractRecovered";
                item.LastMessage = "The exact-build browse contract is available again.";
                item.AttentionNotified = false;
            }
            else if (item.State == RetainerListingRefreshItemState.Blocked && IsLegacyRateLimit(item))
            {
                item.State = RetainerListingRefreshItemState.Deferred;
                item.NextAttemptAtUtc = DateTime.UtcNow;
                item.RateLimitFailures = Math.Max(item.RateLimitFailures, Math.Max(1, item.Attempts));
                item.LastCode = "RecoveredRateLimit";
                item.LastMessage = "A previously terminal rate-limit response will be retried with adaptive backoff.";
                item.AttentionNotified = false;
                recoveredRateLimits++;
            }
        }

        state.NeedsAttention = state.Items.Any(item => item.State == RetainerListingRefreshItemState.Blocked);
        if (recoveredRateLimits > 0)
        {
            state.StatusCode = "Deferred";
            state.StatusMessage = $"Recovered {recoveredRateLimits} rate-limited item(s) for automatic retry.";
            state.AttentionNotified = false;
        }
    }

    private static TimeSpan GetRateLimitRetryDelay(int failureCount)
    {
        if (failureCount >= 3)
            return RateLimitFailureCooldown;

        return TimeSpan.FromMilliseconds(Math.Min(
            RateLimitMaximumRetryDelay.TotalMilliseconds,
            RateLimitInitialRetryDelay.TotalMilliseconds * Math.Pow(3, Math.Max(0, failureCount - 1))));
    }

    private static bool IsLegacyRateLimit(PersistedRetainerListingRefreshItem item) =>
        string.Equals(item.LastCode, "ServerStatusRejected", StringComparison.Ordinal) &&
        item.LastMessage?.Contains("0x70000002", StringComparison.OrdinalIgnoreCase) == true;

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
