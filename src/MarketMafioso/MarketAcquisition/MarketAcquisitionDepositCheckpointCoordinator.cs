using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

public enum MarketAcquisitionDepositCheckpointPhase
{
    Ready,
    ClosingMarketBoard,
    ReturningHome,
    WaitingForHomeWorld,
    SubmittingStow,
    WaitingForStow,
    Failed,
}

/// <summary>
/// Cycle is the checkpoint the run is on (1 before the first one finishes). Stowed and Forwarded are this run's units
/// moved out of the bags by finished checkpoints; Unstowed is what was bought since the last one.
/// </summary>
public sealed record MarketAcquisitionDepositCheckpointSnapshot(
    bool IsEnabled,
    bool IsActive,
    bool IsFinal,
    int Cycle,
    MarketAcquisitionDepositCheckpointPhase Phase,
    string Message,
    string? RequestId,
    long UnstowedUnits,
    long StowedUnits,
    long ForwardedUnits);

public sealed record MarketAcquisitionDepositCheckpointResult(bool Enabled, string Message);

/// <summary>
/// Completed: the checkpoint settled. ResumeRoute: it made room mid-run, so the route continues. NoProgress: it settled
/// without moving a unit, so the route must not try again. Failed: it gave up (the route fails with Message).
/// </summary>
public sealed record MarketAcquisitionDepositCheckpointTickResult(
    bool Worked,
    bool Completed,
    bool Failed,
    bool ResumeRoute,
    bool NoProgress,
    string Message);

public interface IMarketAcquisitionDepositCheckpointRuntime
{
    DateTimeOffset UtcNow { get; }
    bool TryGetOwner(out QuartermasterOwner owner);
    string? CurrentWorldName { get; }
    /// <summary>Inventory1..4 (and the crystal pouch); null while any of them is not loaded.</summary>
    IReadOnlyList<CrossRegionBagStack>? ReadCarriedBags();
    bool TryCloseMarketBoardWindows();
    bool ProcessCommand(string command);
    bool TryIsLifestreamBusy(out bool busy);
    /// <summary>Why the character cannot act yet (zoning, a cutscene, an event), or null when it can.</summary>
    string? ActionBlocker { get; }
    /// <summary>True when Quartermaster advertises deliveredStowForward.v1, so the stow may forward to other characters.</summary>
    bool CanForward { get; }
}

public interface IMarketAcquisitionDepositCheckpointStateStore
{
    MarketAcquisitionDepositCheckpointState? Restore();
    void Save(MarketAcquisitionDepositCheckpointState? state);
}

/// <summary>Units of one item variant.</summary>
public sealed record MarketAcquisitionDepositVariant(uint ItemId, string ItemName, bool IsHq, int Quantity);

public sealed record MarketAcquisitionDepositCheckpointState
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string RouteRequestId { get; init; } = string.Empty;
    public string RouteRunId { get; init; } = string.Empty;
    public QuartermasterOwner Owner { get; init; } = new(0, 0, string.Empty, null);
    /// <summary>The plan's non-shard items; only these are ever stowed.</summary>
    public List<uint> ItemIds { get; init; } = [];
    public int Cycle { get; init; } = 1;
    /// <summary>The bags at Start, refreshed after every checkpoint: a stow never asks for more than the bags gained above it.</summary>
    public List<MarketAcquisitionDepositVariant> Baseline { get; init; } = [];
    /// <summary>Confirmed purchases since the last checkpoint.</summary>
    public List<MarketAcquisitionDepositVariant> Unstowed { get; init; } = [];
    /// <summary>This run's units stowed into the owner's own retainers.</summary>
    public List<MarketAcquisitionDepositVariant> Stowed { get; init; } = [];
    /// <summary>This run's units Quartermaster forwarded (or is forwarding) to the item's homes on another character.</summary>
    public List<MarketAcquisitionDepositVariant> Forwarded { get; init; } = [];
    /// <summary>A checkpoint made no room: no further checkpoint runs for this route.</summary>
    public bool Exhausted { get; init; }
    public MarketAcquisitionDepositCheckpointPhase Phase { get; init; } = MarketAcquisitionDepositCheckpointPhase.Ready;
    public bool IsFinal { get; init; }
    /// <summary>Persisted before the stow is submitted (I1); a reload adopts it, never resubmits under another id.</summary>
    public string? RequestId { get; init; }
    public List<MarketAcquisitionDepositVariant> Items { get; init; } = [];
    public bool AllowForward { get; init; }
    public bool Submitted { get; init; }
    public DateTimeOffset? SubmittedAtUtc { get; init; }
    public DateTimeOffset DeadlineUtc { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class ConfigurationMarketAcquisitionDepositCheckpointStateStore : IMarketAcquisitionDepositCheckpointStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Configuration configuration;
    private readonly Action save;

    public ConfigurationMarketAcquisitionDepositCheckpointStateStore(Configuration configuration, Action? save = null)
    {
        this.configuration = configuration;
        this.save = save ?? configuration.Save;
    }

    public MarketAcquisitionDepositCheckpointState? Restore()
    {
        if (string.IsNullOrWhiteSpace(configuration.MarketAcquisitionDepositCheckpointStateJson))
            return null;
        try
        {
            var state = JsonSerializer.Deserialize<MarketAcquisitionDepositCheckpointState>(
                configuration.MarketAcquisitionDepositCheckpointStateJson, JsonOptions);
            return state?.SchemaVersion == MarketAcquisitionDepositCheckpointState.CurrentSchemaVersion ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(MarketAcquisitionDepositCheckpointState? state)
    {
        configuration.MarketAcquisitionDepositCheckpointStateJson = state is null ? null : JsonSerializer.Serialize(state, JsonOptions);
        save();
    }
}

public interface IMarketAcquisitionDepositCheckpointCoordinator
{
    bool IsActive { get; }
    MarketAcquisitionDepositCheckpointSnapshot Snapshot { get; }
    MarketAcquisitionDepositCheckpointResult Prepare(MarketAcquisitionPlan plan, string routeRunId, bool stowAfterDelivery);
    void RecordConfirmedPurchase(MarketBoardPurchaseCandidate candidate, string? itemName);
    bool RequestCheckpoint(bool isFinal, string reason);
    MarketAcquisitionDepositCheckpointTickResult Tick();
    /// <summary>Units of this run a finished checkpoint moved out of the bags for a line's item and quality.</summary>
    uint MovedUnits(uint itemId, string hqPolicy, bool includeStowed);
    /// <summary>Withdraws an open checkpoint's stow (and its forward) in Quartermaster; the ledger stays.</summary>
    void Withdraw(string reason);
    void Reset();
}

/// <summary>
/// Mid-run storage for a local Market Acquisition route whose bags filled: pause, close the board, go home, and ask
/// Quartermaster's delivered stow to put what this run bought into each item's home retainers (forwarding to other
/// characters when allowed). Only the bought delta above the bag baseline is asked for (I2); the request id is durable
/// before submission (I1); a checkpoint that moves nothing ends the cycling (I3).
/// </summary>
public sealed class MarketAcquisitionDepositCheckpointCoordinator : IMarketAcquisitionDepositCheckpointCoordinator
{
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromMinutes(2);
    /// <summary>Submission (the character must be able to act) and the owner's own stow.</summary>
    public static readonly TimeSpan StowDeadline = TimeSpan.FromMinutes(15);
    /// <summary>Mid-run checkpoints wait for any courier forward to settle before resuming shopping so bags are actually empty.</summary>
    public static readonly TimeSpan ForwardDeadline = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TravelStartGrace = TimeSpan.FromSeconds(10);
    private const int MaxUnstartedTravelResends = 2;

    private readonly IQuartermasterRunCustodyClient custody;
    private readonly IMarketAcquisitionDepositCheckpointRuntime runtime;
    private readonly IMarketAcquisitionDepositCheckpointStateStore store;
    private readonly QuartermasterCancelOutbox? cancels;
    private readonly Action<string> warn;
    private MarketAcquisitionDepositCheckpointState? state;
    private DateTimeOffset nextPollUtc = DateTimeOffset.MinValue;
    private DateTimeOffset? homeTravelSentAtUtc;
    private string? homeTravelSourceWorld;
    private bool homeTravelStarted;
    private int homeTravelResends;
    private bool homeTravelResendPending;
    private bool submitMayHaveLanded;

    public MarketAcquisitionDepositCheckpointCoordinator(
        IQuartermasterRunCustodyClient custody,
        IMarketAcquisitionDepositCheckpointRuntime runtime,
        IMarketAcquisitionDepositCheckpointStateStore store,
        Action<string>? warn = null,
        QuartermasterCancelOutbox? cancels = null)
    {
        this.custody = custody;
        this.runtime = runtime;
        this.store = store;
        this.warn = warn ?? (_ => { });
        this.cancels = cancels;
        state = store.Restore();
        // A stow persisted before a reload may already be held by Quartermaster: look it up before any resubmit.
        submitMayHaveLanded = state?.RequestId is not null;
    }

    public bool IsActive => state?.Phase is not null and not MarketAcquisitionDepositCheckpointPhase.Ready and not MarketAcquisitionDepositCheckpointPhase.Failed;

    public MarketAcquisitionDepositCheckpointSnapshot Snapshot
    {
        get
        {
            var current = state;
            return current is null
                ? new(false, false, false, 1, MarketAcquisitionDepositCheckpointPhase.Ready, "Storage checkpoints are off for this route.", null, 0, 0, 0)
                : new(true, IsActive, current.IsFinal, current.Cycle, current.Phase, current.Message, current.RequestId,
                    Units(current.Unstowed), Units(current.Stowed), Units(current.Forwarded));
        }
    }

    public MarketAcquisitionDepositCheckpointResult Prepare(MarketAcquisitionPlan plan, string routeRunId, bool stowAfterDelivery)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var hasOwner = runtime.TryGetOwner(out var owner);
        if (state is not null && IsActive)
        {
            if (hasOwner && SameOwner(state.Owner, owner) && state.RouteRequestId.Equals(plan.RequestId, StringComparison.Ordinal))
            {
                // Same route again (recovery): keep following the open checkpoint under the new run id.
                state = state with { RouteRunId = routeRunId };
                Persist();
                return new(true, "Following the storage checkpoint already in progress.");
            }
            warn($"[MarketMafioso] Replacing an open storage checkpoint for another route or character: {Describe(state)}");
            Withdraw("The storage checkpoint was replaced by another route.");
        }
        var itemIds = plan.Lines.Select(line => line.ItemId).Where(IsStowable).Distinct().ToList();
        string? disabled = !stowAfterDelivery ? "Stow after delivery is off."
            : itemIds.Count == 0 ? "The route buys no stowable items."
            : !custody.HasDeliveredStow ? $"Quartermaster does not advertise {QuartermasterIpcClient.DeliveredStowCapability}."
            : !hasOwner ? "The current character is not known."
            : null;
        var bags = disabled is null ? runtime.ReadCarriedBags() : null;
        if (disabled is null && bags is null)
            disabled = "The bags could not be read at Start.";
        if (disabled is not null)
        {
            state = null;
            store.Save(null);
            return new(false, $"Storage checkpoints are off: {disabled}");
        }
        state = new MarketAcquisitionDepositCheckpointState
        {
            RouteRequestId = plan.RequestId,
            RouteRunId = routeRunId,
            Owner = owner,
            ItemIds = itemIds,
            Baseline = Variants(bags!, itemIds),
            Message = "When the bags fill, purchases are stowed into their home retainers and shopping continues.",
        };
        Persist();
        return new(true, state.Message);
    }

    public void RecordConfirmedPurchase(MarketBoardPurchaseCandidate candidate, string? itemName)
    {
        if (state is null || !IsStowable(candidate.ItemId) || candidate.Quantity == 0)
            return;
        state = state with
        {
            Unstowed = Add(state.Unstowed, new(candidate.ItemId, itemName ?? candidate.ItemId.ToString(), candidate.IsHq,
                checked((int)candidate.Quantity))),
        };
        Persist();
    }

    public bool RequestCheckpoint(bool isFinal, string reason)
    {
        if (state is null || IsActive || state.Exhausted || state.Unstowed.All(variant => variant.Quantity <= 0))
            return false;
        state = state with
        {
            Phase = MarketAcquisitionDepositCheckpointPhase.ClosingMarketBoard,
            IsFinal = isFinal,
            RequestId = null,
            Items = [],
            AllowForward = false,
            Submitted = false,
            SubmittedAtUtc = null,
            DeadlineUtc = runtime.UtcNow.Add(TravelTimeout),
            Message = reason,
        };
        homeTravelSentAtUtc = null;
        homeTravelResends = 0;
        homeTravelResendPending = false;
        submitMayHaveLanded = false;
        Persist();
        return true;
    }

    public MarketAcquisitionDepositCheckpointTickResult Tick()
    {
        if (state is null || state.Phase == MarketAcquisitionDepositCheckpointPhase.Ready)
            return new(false, false, false, false, false, Snapshot.Message);
        if (state.Phase == MarketAcquisitionDepositCheckpointPhase.Failed)
            return new(false, false, true, false, false, state.Message);
        if (runtime.UtcNow > state.DeadlineUtc)
            return Fail(state.Phase == MarketAcquisitionDepositCheckpointPhase.WaitingForStow
                ? $"Stow {state.RequestId} did not settle within {StowDeadline.TotalMinutes:N0} minutes; last status: {state.Message}. It was cancelled in Quartermaster."
                : $"Storage checkpoint timed out during {state.Phase}; last state: {state.Message}");
        if (!runtime.TryGetOwner(out var owner) || !SameOwner(owner, state.Owner))
            return Fail("Storage checkpoint stopped because the current character no longer matches its owner.");

        switch (state.Phase)
        {
            case MarketAcquisitionDepositCheckpointPhase.ClosingMarketBoard:
                if (runtime.TryCloseMarketBoardWindows())
                    return Waiting("Closing the market board before returning home.");
                return CurrentWorldIsHome()
                    ? MoveTo(MarketAcquisitionDepositCheckpointPhase.SubmittingStow, StowDeadline, "Home world; stowing purchases.")
                    : MoveTo(MarketAcquisitionDepositCheckpointPhase.ReturningHome, TravelTimeout, "Returning to the home world to stow purchases.");

            case MarketAcquisitionDepositCheckpointPhase.ReturningHome:
                if (CurrentWorldIsHome())
                    return MoveTo(MarketAcquisitionDepositCheckpointPhase.SubmittingStow, StowDeadline, "Home world reached; stowing purchases.");
                if (runtime.ActionBlocker is { } blocker)
                    return Waiting($"Waiting before home-world travel: {blocker}.");
                if (runtime.TryIsLifestreamBusy(out var busyBeforeHome) && busyBeforeHome)
                    return Waiting("Waiting for Lifestream to finish before home-world travel.");
                if (!runtime.ProcessCommand($"/li {state.Owner.HomeWorldName}"))
                    return Waiting("Lifestream did not take the home-world travel command; retrying.");
                homeTravelSentAtUtc = runtime.UtcNow;
                homeTravelSourceWorld = runtime.CurrentWorldName;
                homeTravelStarted = false;
                if (homeTravelResendPending)
                {
                    // A resend keeps the deadline the first command started.
                    homeTravelResendPending = false;
                    state = state with { Phase = MarketAcquisitionDepositCheckpointPhase.WaitingForHomeWorld, Message = $"Resent home-world travel; waiting to arrive on {state.Owner.HomeWorldName}." };
                    Persist();
                    return new(true, false, false, false, false, state.Message);
                }
                return MoveTo(MarketAcquisitionDepositCheckpointPhase.WaitingForHomeWorld, TravelTimeout, $"Waiting to arrive on {state.Owner.HomeWorldName}.");

            case MarketAcquisitionDepositCheckpointPhase.WaitingForHomeWorld:
                if (CurrentWorldIsHome())
                {
                    homeTravelResends = 0;
                    homeTravelSentAtUtc = null;
                    return MoveTo(MarketAcquisitionDepositCheckpointPhase.SubmittingStow, StowDeadline, "Home world reached; stowing purchases.");
                }
                return WaitForHomeWorld();

            case MarketAcquisitionDepositCheckpointPhase.SubmittingStow:
                return SubmitStow();

            case MarketAcquisitionDepositCheckpointPhase.WaitingForStow:
                return ObserveStow();

            default:
                return Fail($"Unsupported storage checkpoint phase {state.Phase}.");
        }
    }

    public uint MovedUnits(uint itemId, string hqPolicy, bool includeStowed)
    {
        if (state is null)
            return 0;
        var moved = (includeStowed ? state.Stowed : []).Concat(state.Forwarded)
            .Where(variant => variant.ItemId == itemId && MarketAcquisitionPolicy.HqMatches(hqPolicy, variant.IsHq))
            .Sum(variant => (long)variant.Quantity);
        return (uint)Math.Clamp(moved, 0, uint.MaxValue);
    }

    public void Withdraw(string reason)
    {
        if (state is null || !IsActive)
            return;
        CancelStow(reason);
        warn($"[MarketMafioso] Storage checkpoint withdrawn ({reason}): {Describe(state)}");
        state = state with
        {
            Phase = MarketAcquisitionDepositCheckpointPhase.Ready,
            IsFinal = false,
            RequestId = null,
            Items = [],
            Submitted = false,
            SubmittedAtUtc = null,
            DeadlineUtc = default,
            Message = $"Storage checkpoint withdrawn: {reason}",
        };
        Persist();
    }

    public void Reset()
    {
        Withdraw("The route was reset.");
        state = null;
        store.Save(null);
    }

    private MarketAcquisitionDepositCheckpointTickResult SubmitStow()
    {
        var current = state!;
        if (runtime.ActionBlocker is { } blocker)
            return Waiting($"Waiting to stow purchases: {blocker}.");
        if (runtime.TryIsLifestreamBusy(out var busy) && busy)
            return Waiting("Waiting for Lifestream to finish before stowing.");
        if (current.RequestId is null)
        {
            if (runtime.ReadCarriedBags() is not { } bags)
                return Waiting("Waiting for the bags to load before stowing.");
            var items = CapToBoughtDelta(current.Unstowed, current.Baseline, bags);
            if (items.Count == 0)
                return Settle([], [], "None of this run's purchases are in the bags above what they held before; nothing to stow.");
            // I1: identity, payload and forwarding are durable before Quartermaster sees the request.
            state = current = current with
            {
                RequestId = $"mmf-local-stow-{current.RouteRunId}-c{current.Cycle}",
                Items = items,
                AllowForward = runtime.CanForward,
                SubmittedAtUtc = runtime.UtcNow,
                Message = $"Stowing {Units(items):N0} purchased item(s) into their home retainers.",
            };
            Persist();
        }
        if (submitMayHaveLanded && custody.TryGetDeliveredStow(current.RequestId!, out var existing, out _) &&
            existing is { IsNotFound: false })
        {
            submitMayHaveLanded = false;
            return Accepted(existing);
        }
        var request = new QuartermasterDeliveredStowRequest(
            current.RequestId!,
            current.Owner,
            current.Items.Select(item => new QuartermasterDeliveredStowItem(item.ItemId, item.ItemName, item.IsHq, item.Quantity)).ToArray(),
            current.AllowForward);
        if (!custody.TrySubmitDeliveredStow(request, out var acknowledgement, out var error))
        {
            // The call may have reached Quartermaster before failing: look the id up before sending it again.
            submitMayHaveLanded = true;
            return Waiting($"Stow not submitted yet: {error}");
        }
        if (acknowledgement is { IsRejected: true })
            return Settle([], [], $"Quartermaster rejected the stow: {acknowledgement.ErrorCode ?? acknowledgement.Message ?? "no reason given"}.");
        return Accepted(acknowledgement);
    }

    private MarketAcquisitionDepositCheckpointTickResult Accepted(QuartermasterDeliveredStowStatus? status)
    {
        state = state! with
        {
            Phase = MarketAcquisitionDepositCheckpointPhase.WaitingForStow,
            Submitted = true,
            DeadlineUtc = (state.SubmittedAtUtc ?? runtime.UtcNow).Add(StowDeadline),
            Message = $"Quartermaster is stowing {Units(state.Items):N0} item(s){(status is null ? "." : $": {status.Status}.")}",
        };
        nextPollUtc = DateTimeOffset.MinValue;
        Persist();
        if (status is { IsLocalSettled: true } settled && (state.IsFinal || settled.Forward is not { IsSettled: false }))
            return SettleFrom(settled);
        return new(true, false, false, false, false, state.Message);
    }

    private MarketAcquisitionDepositCheckpointTickResult ObserveStow()
    {
        var now = runtime.UtcNow;
        if (now < nextPollUtc)
            return new(false, false, false, false, false, state!.Message);
        nextPollUtc = now + PollInterval;
        if (!custody.TryGetDeliveredStow(state!.RequestId!, out var status, out var error) || status is null)
            return Waiting($"Waiting for Quartermaster to report the stow: {error}");
        if (status.IsNotFound)
            return Settle([], [], "Quartermaster does not know the stow request; nothing was stowed.");
        if (!status.IsLocalSettled)
            return Waiting($"Stowing purchases: {status.Status}.");
        // Mid-run checkpoints must wait for any courier forward to finish so the bags are actually emptied and the route
        // does not teleport away while Quartermaster is trading items off this character.
        if (!state.IsFinal && status.Forward is { IsSettled: false })
        {
            var forwardDeadline = (state.SubmittedAtUtc ?? now).Add(ForwardDeadline);
            if (state.DeadlineUtc < forwardDeadline)
            {
                state = state with { DeadlineUtc = forwardDeadline };
                Persist();
            }
            return Waiting($"Forwarding purchases to alt homes before resuming shopping ({status.Forward.State}).");
        }
        return SettleFrom(status);
    }

    private MarketAcquisitionDepositCheckpointTickResult SettleFrom(QuartermasterDeliveredStowStatus status)
    {
        var stowed = new List<MarketAcquisitionDepositVariant>();
        var forwarded = new List<MarketAcquisitionDepositVariant>();
        var isFinal = state!.IsFinal;
        foreach (var line in status.Lines)
        {
            if (line.Stowed > 0)
                stowed = Add(stowed, new(line.ItemId, line.ItemName, line.IsHq, line.Stowed));
            // Mid-run checkpoints count only verified delivered forward units; final checkpoints may leave an open forward running.
            var moving = line.AllForwards
                .Where(forward => !forward.IsNeutral)
                .Sum(forward => forward.Delivered ?? (isFinal && forward.IsOpen ? forward.Planned : 0));
            if (moving > 0)
                forwarded = Add(forwarded, new(line.ItemId, line.ItemName, line.IsHq, moving));
        }
        var outcome = string.Join(", ", status.Lines.Select(line =>
            $"{(string.IsNullOrWhiteSpace(line.ItemName) ? line.ItemId.ToString() : line.ItemName)}{(line.IsHq ? " HQ" : string.Empty)} {line.Stowed}/{line.Requested} {line.Code}"));
        return Settle(stowed, forwarded, $"Stow {status.LocalStatus}{(outcome.Length > 0 ? $" ({outcome})" : string.Empty)}.");
    }

    /// <summary>I3: a checkpoint that moved units resumes the route; one that moved none ends the cycling.</summary>
    private MarketAcquisitionDepositCheckpointTickResult Settle(
        List<MarketAcquisitionDepositVariant> stowed,
        List<MarketAcquisitionDepositVariant> forwarded,
        string outcome)
    {
        var current = state!;
        var progress = Units(stowed) + Units(forwarded);
        var bags = runtime.ReadCarriedBags();
        var final = current.IsFinal;
        var resume = !final && progress > 0;
        var message = progress > 0
            ? $"Stowed {Units(stowed):N0}{(Units(forwarded) > 0 ? $", forwarding {Units(forwarded):N0}" : string.Empty)}. {outcome}" +
              (resume ? " Resuming shopping." : string.Empty)
            : $"Storage checkpoint made no room. {outcome}";
        if (progress == 0)
            warn($"[MarketMafioso] StorageCheckpointNoProgress: {message} {Describe(current)}");
        state = current with
        {
            Phase = MarketAcquisitionDepositCheckpointPhase.Ready,
            IsFinal = false,
            Cycle = progress > 0 ? current.Cycle + 1 : current.Cycle,
            Stowed = stowed.Aggregate(current.Stowed, Add),
            Forwarded = forwarded.Aggregate(current.Forwarded, Add),
            // What is left in the bags now is not the next checkpoint's to stow.
            Baseline = bags is null ? current.Baseline : Variants(bags, current.ItemIds),
            Unstowed = [],
            Exhausted = current.Exhausted || progress == 0,
            RequestId = null,
            Items = [],
            Submitted = false,
            SubmittedAtUtc = null,
            DeadlineUtc = default,
            Message = message,
        };
        submitMayHaveLanded = false;
        Persist();
        return new(true, true, false, resume, progress == 0, message);
    }

    /// <summary>
    /// Waits for the home world. A command Lifestream never started (never busy, world unchanged after the grace) goes
    /// back through ReturningHome to be resent, a bounded number of times, keeping the phase deadline.
    /// </summary>
    private MarketAcquisitionDepositCheckpointTickResult WaitForHomeWorld()
    {
        var arriving = $"Waiting to arrive on {state!.Owner.HomeWorldName}.";
        if (homeTravelSentAtUtc is not { } sentAt || homeTravelStarted)
            return Waiting(arriving);
        if (runtime.TryIsLifestreamBusy(out var busy) && busy ||
            !string.Equals(runtime.CurrentWorldName, homeTravelSourceWorld, StringComparison.OrdinalIgnoreCase))
        {
            homeTravelStarted = true;
            return Waiting(arriving);
        }
        if (runtime.UtcNow - sentAt < TravelStartGrace || homeTravelResends >= MaxUnstartedTravelResends)
            return Waiting(arriving);
        homeTravelResends++;
        homeTravelResendPending = true;
        warn($"[MarketMafioso] Lifestream never started home-world travel to {state.Owner.HomeWorldName}; resending ({homeTravelResends}/{MaxUnstartedTravelResends}).");
        state = state with { Phase = MarketAcquisitionDepositCheckpointPhase.ReturningHome, Message = $"Lifestream never started travel to {state.Owner.HomeWorldName}; resending." };
        Persist();
        return new(true, false, false, false, false, state.Message);
    }

    private MarketAcquisitionDepositCheckpointTickResult MoveTo(MarketAcquisitionDepositCheckpointPhase phase, TimeSpan timeout, string message)
    {
        state = state! with { Phase = phase, DeadlineUtc = runtime.UtcNow.Add(timeout), Message = message };
        Persist();
        return new(true, false, false, false, false, message);
    }

    private MarketAcquisitionDepositCheckpointTickResult Waiting(string message)
    {
        if (!state!.Message.Equals(message, StringComparison.Ordinal))
        {
            state = state with { Message = message };
            Persist();
        }
        return new(false, false, false, false, false, message);
    }

    private MarketAcquisitionDepositCheckpointTickResult Fail(string message)
    {
        // A stow that may exist in Quartermaster must not run after this checkpoint gave up on it.
        CancelStow($"MarketMafioso's storage checkpoint failed: {message}");
        warn($"[MarketMafioso] StorageCheckpointFailed: {message} {Describe(state!)}");
        state = state! with { Phase = MarketAcquisitionDepositCheckpointPhase.Failed, Message = message };
        Persist();
        return new(true, false, true, false, false, message);
    }

    /// <summary>Withdraws the stow and its courier forward; what Quartermaster does not confirm is retried by the cancel outbox.</summary>
    private void CancelStow(string reason)
    {
        if (state?.RequestId is not { } requestId)
            return;
        var owner = $"storage checkpoint for {state.Owner.CharacterName}";
        if (cancels is not null)
        {
            var outcome = cancels.Cancel(QuartermasterCancelKind.RequestedOperation, requestId, reason, owner);
            warn($"[MarketMafioso] Stow {requestId}: {outcome.Status}. {outcome.Message}");
            if (state.AllowForward)
                cancels.Cancel(QuartermasterCancelKind.DeliveredStowForward, requestId, reason, owner);
            return;
        }
        if (!custody.TryCancelRequestedOperation(requestId, reason, out _, out var error))
            warn($"[MarketMafioso] Stow {requestId} could not be cancelled: {error}");
        if (state.AllowForward && !custody.TryCancelDeliveredStowForward(requestId, reason, out _, out var forwardError))
            warn($"[MarketMafioso] Stow {requestId}'s forward could not be cancelled: {forwardError}");
    }

    /// <summary>I2: per variant, the confirmed purchases capped by what the bags hold above the baseline.</summary>
    internal static List<MarketAcquisitionDepositVariant> CapToBoughtDelta(
        IReadOnlyList<MarketAcquisitionDepositVariant> unstowed,
        IReadOnlyList<MarketAcquisitionDepositVariant> baseline,
        IReadOnlyList<CrossRegionBagStack> bags)
    {
        var live = bags.GroupBy(stack => (stack.ItemId, stack.IsHq))
            .ToDictionary(group => group.Key, group => group.Sum(stack => (long)stack.Quantity));
        var before = baseline.ToDictionary(variant => (variant.ItemId, variant.IsHq), variant => (long)variant.Quantity);
        return unstowed
            .Select(variant =>
            {
                var gained = Math.Max(0L, live.GetValueOrDefault((variant.ItemId, variant.IsHq)) - before.GetValueOrDefault((variant.ItemId, variant.IsHq)));
                return variant with { Quantity = (int)Math.Min(variant.Quantity, gained) };
            })
            .Where(variant => variant.Quantity > 0)
            .ToList();
    }

    private static List<MarketAcquisitionDepositVariant> Variants(IReadOnlyList<CrossRegionBagStack> bags, IReadOnlyCollection<uint> itemIds) =>
        bags.Where(stack => itemIds.Contains(stack.ItemId))
            .GroupBy(stack => (stack.ItemId, stack.IsHq))
            .Select(group => new MarketAcquisitionDepositVariant(group.Key.ItemId, string.Empty, group.Key.IsHq,
                (int)Math.Min(group.Sum(stack => (long)stack.Quantity), int.MaxValue)))
            .ToList();

    private static List<MarketAcquisitionDepositVariant> Add(List<MarketAcquisitionDepositVariant> list, MarketAcquisitionDepositVariant add)
    {
        var result = list.ToList();
        var index = result.FindIndex(variant => variant.ItemId == add.ItemId && variant.IsHq == add.IsHq);
        if (index < 0)
            result.Add(add);
        else
            result[index] = result[index] with
            {
                Quantity = checked(result[index].Quantity + add.Quantity),
                ItemName = string.IsNullOrWhiteSpace(result[index].ItemName) ? add.ItemName : result[index].ItemName,
            };
        return result;
    }

    private static long Units(IEnumerable<MarketAcquisitionDepositVariant> variants) => variants.Sum(variant => (long)variant.Quantity);

    /// <summary>Shards (item ids 2–19) stay with the shard checkpoint; crystals and clusters ride in the pouch, not the bags.</summary>
    private static bool IsStowable(uint itemId) => itemId > 19;

    private static bool SameOwner(QuartermasterOwner a, QuartermasterOwner b) =>
        a.LocalContentId == b.LocalContentId && a.HomeWorldId == b.HomeWorldId;

    private bool CurrentWorldIsHome() =>
        !string.IsNullOrWhiteSpace(runtime.CurrentWorldName) &&
        runtime.CurrentWorldName.Equals(state!.Owner.HomeWorldName, StringComparison.OrdinalIgnoreCase);

    private static string Describe(MarketAcquisitionDepositCheckpointState current) =>
        $"route={current.RouteRequestId} run={current.RouteRunId} owner={current.Owner.CharacterName} cycle={current.Cycle} " +
        $"phase={current.Phase} request={current.RequestId ?? "-"} unstowed={Units(current.Unstowed)} message={current.Message}";

    private void Persist() => store.Save(state);
}
