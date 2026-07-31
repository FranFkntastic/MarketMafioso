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
    string ProviderInstanceId,
    long Revision,
    DateTimeOffset? ListingsObservedAtUtc);

internal interface IRetainerListingRefreshSource
{
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

        var items = snapshot.Retainers
            .SelectMany(retainer => retainer.Listings)
            .Where(listing => listing.ItemId != 0)
            .GroupBy(listing => listing.ItemId)
            .Select(group => new RetainerListingRefreshCandidate(
                group.Key,
                group.Select(listing => listing.ItemName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))))
            .OrderBy(item => item.ItemId)
            .ToArray();
        var listingsObservedAtUtc = snapshot.Retainers
            .Where(retainer => retainer.ObservedSources.Contains("RetainerMarket", StringComparer.Ordinal))
            .Select(retainer => retainer.ListingsObservedAtUtc)
            .Where(observedAt => observedAt.HasValue)
            .Max();
        result = new RetainerListingRefreshSnapshot(
            items,
            snapshot.ProviderInstanceId,
            snapshot.Revision,
            listingsObservedAtUtc);
        return true;
    }
}

internal sealed class RetainerListingRefreshCoordinator
{
    private static readonly TimeSpan CloseStabilityDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TransientDeferral = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RejectedRequestDeferral = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan AmbiguousRequestDeferral = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MissingOperationReconciliationDelay = TimeSpan.FromSeconds(20);

    private readonly Configuration config;
    private readonly IRetainerListingRefreshSource source;
    private readonly IHeadlessMarketBoardBrowseRuntime browseRuntime;
    private readonly Action persist;
    private readonly Action<string> notifyAttention;
    private readonly Action<string> log;
    private readonly Func<TimeSpan> nextSuccessDelay;
    private bool sessionWasActive;

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
        bool retainerSessionActive,
        bool dispatchReady,
        string? dispatchDeferredReason = null)
    {
        if (!IsEnabled)
        {
            sessionWasActive = retainerSessionActive;
            return;
        }

        if (retainerSessionActive)
        {
            if (!sessionWasActive ||
                config.RetainerListingRefresh.SessionStartedAtUtc is null)
                CaptureSessionBaseline(nowUtc);
            sessionWasActive = true;
            return;
        }

        if (sessionWasActive)
        {
            sessionWasActive = false;
            QueuePostCloseCapture(nowUtc);
            return;
        }

        var state = config.RetainerListingRefresh;
        if (state.CapturePending &&
            state.CaptureNotBeforeUtc is { } captureNotBefore &&
            nowUtc >= captureNotBefore)
        {
            CapturePostCloseListings(nowUtc);
        }

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

    private void CaptureSessionBaseline(DateTimeOffset nowUtc)
    {
        var state = config.RetainerListingRefresh;
        state.SessionStartedAtUtc = nowUtc.UtcDateTime;
        state.SessionClosedAtUtc = null;
        state.SessionSnapshotProviderInstanceId = null;
        state.SessionSnapshotRevision = null;
        state.SessionListingsObservedAtUtc = null;
        state.SessionListings.Clear();
        if (!source.TryRead(out var snapshot, out _))
        {
            persist();
            return;
        }

        state.SessionSnapshotProviderInstanceId = snapshot!.ProviderInstanceId;
        state.SessionSnapshotRevision = snapshot.Revision;
        state.SessionListingsObservedAtUtc = snapshot.ListingsObservedAtUtc?.UtcDateTime;
        state.SessionListings = snapshot.Items
            .Select(item => new PersistedRetainerListingRefreshCandidate
            {
                ItemId = item.ItemId,
                ItemName = item.ItemName,
            })
            .ToList();
        persist();
    }

    private void QueuePostCloseCapture(DateTimeOffset nowUtc)
    {
        var state = config.RetainerListingRefresh;
        state.CapturePending = true;
        state.SessionClosedAtUtc = nowUtc.UtcDateTime;
        state.CaptureNotBeforeUtc = nowUtc.UtcDateTime + CloseStabilityDelay;
        state.CaptureAttempts = 0;
        state.NeedsAttention = state.Items.Any(item => item.State == RetainerListingRefreshItemState.Blocked);
        state.AttentionNotified = false;
        UpdateStatus(
            "VerifyingListings",
            "Retainer session closed; verifying the owner-scoped listing set before background refresh.",
            persistState: true);
    }

    private void CapturePostCloseListings(DateTimeOffset nowUtc)
    {
        var state = config.RetainerListingRefresh;
        if (!source.TryRead(out var snapshot, out var error) ||
            !HasFreshListingEvidence(state, snapshot, out error))
        {
            state.CaptureAttempts++;
            state.CaptureNotBeforeUtc = nowUtc.UtcDateTime + CaptureRetryDelay(state.CaptureAttempts);
            if (state.CaptureAttempts >= 3)
            {
                state.NeedsAttention = true;
                if (!state.AttentionNotified)
                {
                    state.AttentionNotified = true;
                    notifyAttention(
                        "Retainer listing refresh is still waiting for an owner-scoped Quartermaster snapshot. " +
                        "No market request has been sent; MMF will keep recovering in the background.");
                }
            }

            UpdateStatus(
                "SnapshotDeferred",
                $"Could not verify the retainer listing set. {error} MMF will retry without sending a market request.",
                persistState: true);
            return;
        }

        var candidates = state.SessionListings
            .Select(item => new RetainerListingRefreshCandidate(item.ItemId, item.ItemName))
            .Concat(snapshot!.Items)
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
                LastCode = "QueuedAfterRetainerClose",
                LastMessage = "Queued from the owner-scoped retainer listing snapshot.",
            });
        }

        state.CapturePending = false;
        state.CaptureNotBeforeUtc = null;
        state.CaptureAttempts = 0;
        state.SessionStartedAtUtc = null;
        state.SessionClosedAtUtc = null;
        state.SessionSnapshotProviderInstanceId = null;
        state.SessionSnapshotRevision = null;
        state.SessionListingsObservedAtUtc = null;
        state.SessionListings.Clear();
        state.NeedsAttention = state.Items.Any(item => item.State == RetainerListingRefreshItemState.Blocked);
        state.AttentionNotified = false;
        UpdateStatus(
            candidates.Length == 0 ? "Idle" : "Queued",
            candidates.Length == 0
                ? "The closed retainer session had no listed items to refresh."
                : $"Queued {candidates.Length} distinct listed item(s) for serialized background refresh.",
            persistState: true);
    }

    private static bool HasFreshListingEvidence(
        PersistedRetainerListingRefreshState state,
        RetainerListingRefreshSnapshot? snapshot,
        out string error)
    {
        if (snapshot is null)
        {
            error = "Quartermaster did not return a listing snapshot.";
            return false;
        }

        if (snapshot.ListingsObservedAtUtc is not { } listingsObservedAtUtc)
        {
            error = "Quartermaster has not observed the RetainerMarket source for this session.";
            return false;
        }

        var baselineObservedAtUtc = state.SessionListingsObservedAtUtc ??
                                    state.SessionStartedAtUtc ??
                                    state.SessionClosedAtUtc;
        if (baselineObservedAtUtc is { } baseline &&
            listingsObservedAtUtc.UtcDateTime <= baseline)
        {
            error = "Quartermaster's retainer-listing evidence has not advanced past the session baseline.";
            return false;
        }

        if (state.SessionSnapshotRevision is { } baselineRevision &&
            string.Equals(
                state.SessionSnapshotProviderInstanceId,
                snapshot.ProviderInstanceId,
                StringComparison.Ordinal) &&
            snapshot.Revision <= baselineRevision)
        {
            error = "Quartermaster's snapshot revision has not advanced past the session baseline.";
            return false;
        }

        error = string.Empty;
        return true;
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
        state.Items ??= [];
        state.SessionListings ??= [];
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
        }
    }

    private void UpdateStatus(string code, string message, bool persistState)
    {
        var state = config.RetainerListingRefresh;
        state.StatusCode = code;
        state.StatusMessage = message;
        if (persistState)
            persist();
    }

    private static TimeSpan CaptureRetryDelay(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(15),
        3 => TimeSpan.FromMinutes(1),
        _ => TimeSpan.FromMinutes(5),
    };

    private static string FormatItem(PersistedRetainerListingRefreshItem item) =>
        string.IsNullOrWhiteSpace(item.ItemName)
            ? $"item {item.ItemId}"
            : item.ItemName!;
}
