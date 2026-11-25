using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Inventory;
using MarketMafioso.Automation.Runtime;

namespace MarketMafioso.TradeQueue;

public sealed class TradeQueueRunner : IDisposable
{
    private static readonly TimeSpan OpenTradeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InventoryPreparationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OfferItemsTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PartnerTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ClosingTradeTimeout = TimeSpan.FromSeconds(60);

    private readonly IList<TradeQueueItem> queue;
    private readonly TradeQueueTimingOptions timing;
    private readonly TradeQueuePolicyOptions policy;
    private readonly TradeQueueRecoveryState recovery;
    private readonly Action save;
    private readonly ITradeQueueIo io;
    private readonly IItemQualityLoweringAutomation qualityLowering;
    private readonly ExternalAutomationCoordinator externalAutomation;
    private bool restoreAutoConfirmWhenTradeCloses;
    private readonly IPluginLog log;
    private readonly Func<DateTimeOffset> clock;

    private TradeQueuePartner? partner;
    private TradeQueueBatch? batch;
    private DateTimeOffset deadline;
    private DateTimeOffset nextActionAt;
    private (TradeQueueExecutionState State, string Message)? pendingFinish;
    private bool cancellationCheckpointSaved;
    private DateTimeOffset batchRecoveryDeadline;
    private DateTimeOffset closingDeadline;
    private TradeQueueOfferLineOperation? offerLineOperation;
    private TradeQueueOfferGilOperation? offerGilOperation;
    private bool readyClicked;
    private bool confirmationSubmitted;
    private int batchNumber;
    private int initialUnitCount;
    private int completedUnitCount;
    private int completedBatchCount;
    private string checkpointQueueSignature = string.Empty;
    private string? runId;
    // A partner can drop out of targeting for a moment (zoning in, a cutscene, a step out of range); with no trade
    // open nothing is in flight, so the run waits this long before stopping.
    private static readonly TimeSpan PartnerMissingGrace = TimeSpan.FromSeconds(12);
    private DateTimeOffset? partnerMissingSinceUtc;
    private bool normalizeHighQualityItemsForRun = true;

    public TradeQueueRunner(
        IList<TradeQueueItem> queue,
        TradeQueueTimingOptions timing,
        Action save,
        ITradeQueueIo io,
        IItemQualityLoweringAutomation qualityLowering,
        ExternalAutomationCoordinator externalAutomation,
        IPluginLog log,
        TradeQueuePolicyOptions? policy = null,
        TradeQueueRecoveryState? recovery = null)
        : this(queue, timing, save, io, qualityLowering, externalAutomation, log, () => DateTimeOffset.UtcNow, policy, recovery)
    {
    }

    internal TradeQueueRunner(
        IList<TradeQueueItem> queue,
        TradeQueueTimingOptions timing,
        Action save,
        ITradeQueueIo io,
        IItemQualityLoweringAutomation qualityLowering,
        ExternalAutomationCoordinator externalAutomation,
        IPluginLog log,
        Func<DateTimeOffset> clock,
        TradeQueuePolicyOptions? policy = null,
        TradeQueueRecoveryState? recovery = null)
    {
        this.queue = queue;
        this.timing = timing;
        this.policy = policy ?? new();
        this.recovery = recovery ?? new();
        this.save = save;
        this.io = io;
        this.qualityLowering = qualityLowering;
        this.externalAutomation = externalAutomation;
        this.log = log;
        this.clock = clock;
        if (this.recovery.CancellationPending)
        {
            cancellationCheckpointSaved = true;
            restoreAutoConfirmWhenTradeCloses = false;
            externalAutomation.SuppressTradeAutoConfirm();
            pendingFinish = (TradeQueueExecutionState.Stopped, "Recovering the previous trade cancellation.");
            closingDeadline = clock() + ClosingTradeTimeout;
            SetActive(TradeQueueExecutionState.ClosingTrade, pendingFinish.Value.Message, null);
        }
        else if (externalAutomation is not null)
        {
            // An earlier load may have unloaded while it held auto-confirm off for a still-open trade window.
            if (TradeWindowMayBeOpen())
            {
                externalAutomation.SuppressTradeAutoConfirm();
                restoreAutoConfirmWhenTradeCloses = true;
            }
            else
            {
                externalAutomation.ReleaseStaleTradeSuppression();
            }
        }
    }

    /// <summary>True while a released trade closure keeps auto-confirm off until the trade window closes.</summary>
    public bool IsHoldingAutoConfirmUntilTradeCloses => restoreAutoConfirmWhenTradeCloses;

    /// <summary>Reads the trade window state; a failed read counts as open.</summary>
    private bool TradeWindowMayBeOpen()
    {
        try
        {
            return io.IsTradeOpen;
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[MarketMafioso] Could not read the trade window state; treating it as open.");
            return true;
        }
    }

    public TradeQueueExecutionSnapshot Snapshot { get; private set; } =
        new(TradeQueueExecutionState.Idle, "Trade queue is idle.", null, null, 0, 0, 0, 0, 0, 0, 0, false);

    internal ExternalTradeHold? ExternalHold { get; set; }
    internal string? HoldReason => ExternalHold?.Reason;
    internal void ReleaseHold() => ExternalHold?.Clear();
    public bool IsActive => Snapshot.IsActive;

    /// <summary>
    /// Optional recipient restriction checked before any run starts. Returns null to allow the partner, or the
    /// refusal message (which must name the expected and the actual recipient).
    /// </summary>
    public Func<TradeQueuePartner, string?>? RecipientGuard { get; set; }

    /// <summary>Raised on the framework thread after each batch's inventory delta is verified, before the queue is saved.</summary>
    public event Action<TradeQueuePartner, TradeQueueBatch>? BatchVerified;

    /// <summary>
    /// Raised on the framework thread when a batch is planned, before its trade is opened or offered. A rebuilt
    /// batch raises it again; the latest one replaces the earlier plan.
    /// </summary>
    public event Action<TradeQueuePartner, TradeQueueBatch>? BatchStarting;

    /// <summary>
    /// Raised on the framework thread just before this client clicks Ready on a batch's offer (possibly on several
    /// ticks until the click lands). A trade cannot complete without that click, so a batch that never raised this
    /// cannot have been handed over.
    /// </summary>
    public event Action<TradeQueuePartner, TradeQueueBatch>? BatchLocking;

    /// <summary>Raised when a run fails or stops with a planned batch still unverified.</summary>
    public event Action<TradeQueuePartner, TradeQueueBatch>? BatchAbandoned;

    /// <summary>Resolves capability evidence without selecting a recipient or starting any operation.</summary>
    public (bool Available, string Message) InspectExecutionCapability()
    {
        if (HoldReason is {} reason) return (false, reason);
        var available = io.TryGetExecutionReadiness(out var error);
        return (available, available ? "Trade UI capabilities are available; item offering is checked when needed." : error);
    }

    public bool HasResumeCheckpoint =>
        Snapshot.State is TradeQueueExecutionState.Failed or TradeQueueExecutionState.Stopped &&
        queue.Count > 0 &&
        checkpointQueueSignature == ComputeQueueSignature(queue) &&
        partner != null;

    public bool CanResume
    {
        get
        {
            if (!io.TryGetSelectedPartner(out var selectedPartner))
                return false;

            return CanResumeWith(selectedPartner);
        }
    }

    public TradeQueueStartResult Start()
    {
        if (HoldReason is {} reason) return new(false, reason);
        if (IsActive)
            return new(false, "Trade queue is already running.");
        var hasSelectedPartner = io.TryGetSelectedPartner(out var selectedPartner);
        if (!io.TryGetExecutionReadiness(out var readinessError))
            return RejectStart(hasSelectedPartner ? selectedPartner : null, readinessError);
        if (!hasSelectedPartner)
            return RejectStart(null, "Select or focus-target the player who should receive this queue.");

        return Start(selectedPartner);
    }

    public TradeQueueStartResult Start(TradeQueuePartner selectedPartner)
    {
        if (HoldReason is {} reason) return new(false, reason);
        if (IsActive)
            return new(false, "Trade queue is already running.");
        if (!io.TryGetExecutionReadiness(out var readinessError))
            return RejectStart(selectedPartner, readinessError);
        if (RecipientGuard?.Invoke(selectedPartner) is { } refusal)
            return RejectStart(selectedPartner, refusal);
        if (!io.PartnerIsAvailable(selectedPartner))
            return RejectStart(selectedPartner, $"{selectedPartner.Name} @ {selectedPartner.HomeWorldName} is not an exact visible trade recipient.");

        partner = selectedPartner;
        normalizeHighQualityItemsForRun = policy.NormalizeHighQualityItems;
        var observation = io.ObserveTradeableInventory();
        if (!observation.IsAuthoritative)
        {
            runId = Guid.NewGuid().ToString("N");
            initialUnitCount = queue.Sum(item => item.Quantity);
            completedUnitCount = 0;
            completedBatchCount = 0;
            batchNumber = 1;
            SetActive(
                TradeQueueExecutionState.PreparingInventory,
                "Checking current tradeable inventory...",
                InventoryPreparationTimeout);
            return new(true, $"Started Trade Queue for {partner.Name}; checking current inventory.");
        }

        return StartFromAuthoritativeInventory(observation.Stacks);
    }

    private TradeQueueStartResult RejectStart(TradeQueuePartner? recipient, string reason)
    {
        var attemptId = Guid.NewGuid().ToString("N");
        var preserveCheckpoint = HasResumeCheckpoint;
        if (!preserveCheckpoint)
        {
            runId = null;
            partner = null;
            checkpointQueueSignature = string.Empty;
            batchNumber = initialUnitCount = completedUnitCount = completedBatchCount = 0;
        }
        Snapshot = new(
            TradeQueueExecutionState.Failed, reason, runId ?? attemptId, partner?.Name ?? recipient?.Name,
            batchNumber, 0, completedBatchCount,
            initialUnitCount > 0 ? initialUnitCount : queue.Sum(item => item.Quantity), completedUnitCount,
            queue.Count, queue.Sum(item => item.Quantity), false);
        log.Warning(
            "[MarketMafioso] Trade Queue start rejected attempt={AttemptId} recipient={Recipient}@{World} stage=preflight remainingLines={RemainingLines} remainingUnits={RemainingUnits} reason={Reason}",
            attemptId, recipient?.Name ?? "unselected", recipient?.HomeWorldName ?? "",
            queue.Count, Snapshot.RemainingUnitCount, reason);
        return new(false, reason);
    }

    private TradeQueueStartResult StartFromAuthoritativeInventory(
        IReadOnlyList<TradeQueueInventoryStack> inventory)
    {
        var currentPartner = partner ?? throw new InvalidOperationException("Trade Queue has no captured recipient.");
        var queueChanged = TradeQueueInventoryReconciler.Reconcile(queue, inventory);
        if (queueChanged)
            save();

        var validation = TradeQueuePlanner.Validate(queue.ToList(), inventory);
        if (!validation.Success)
        {
            if (validation.Code == TradeQueueValidationCode.Empty)
            {
                runId = Guid.NewGuid().ToString("N");
                initialUnitCount = 0;
                completedUnitCount = 0;
                completedBatchCount = 0;
                batchNumber = 0;
                Finish(TradeQueueExecutionState.Completed, "Nothing currently available remains queued.");
                return new(true, Snapshot.Message);
            }

            return RejectStart(currentPartner, validation.Message);
        }

        var isResume = CanResumeWith(currentPartner);
        if (!isResume)
        {
            runId = Guid.NewGuid().ToString("N");
            initialUnitCount = queue.Sum(item => item.Quantity);
            completedUnitCount = 0;
            completedBatchCount = 0;
        }

        batchNumber = completedBatchCount + 1;
        checkpointQueueSignature = ComputeQueueSignature(queue);
        restoreAutoConfirmWhenTradeCloses = false;
        externalAutomation.SuppressTradeAutoConfirm();
        if (!normalizeHighQualityItemsForRun)
        {
            if (!PrepareBatch(inventory, "Quality normalization is disabled; opening the first trade."))
                return new(false, Snapshot.Message);

            return new(
                true,
                isResume
                    ? $"Resumed Trade Queue for {currentPartner.Name} from the last verified batch."
                    : $"Started Trade Queue for {currentPartner.Name}.");
        }

        var qualitySnapshot = qualityLowering.Begin(
            queue
                .Where(item => item.ItemId != TradeQueuePlanner.GilItemId)
                .GroupBy(item => item.ItemId)
                .Select(group => new ItemQualityLoweringRequirement(
                    group.Key,
                    group.First().ItemName,
                    checked(group.Sum(item => item.Quantity))))
                .ToArray());
        if (qualitySnapshot.State == ItemQualityLoweringAutomationState.Failed)
        {
            Finish(TradeQueueExecutionState.Failed, qualitySnapshot.Message);
            return new(false, qualitySnapshot.Message);
        }

        SetActive(
            TradeQueueExecutionState.NormalizingQuality,
            qualitySnapshot.Message,
            null);

        return new(
            true,
            isResume
                ? $"Resumed Trade Queue for {currentPartner.Name} from the last verified batch."
                : $"Started Trade Queue for {currentPartner.Name}.");
    }

    public void Tick()
    {
        try
        {
            if (restoreAutoConfirmWhenTradeCloses && !TradeWindowMayBeOpen())
            {
                restoreAutoConfirmWhenTradeCloses = false;
                externalAutomation.RestoreTradeAutoConfirm();
                log.Information("[MarketMafioso] Trade window closed; restored trade auto-confirm after a released trade closure.");
            }
            if (Snapshot.State == TradeQueueExecutionState.ClosingTrade)
            {
                TickClosingTrade();
                return;
            }
            if (!IsActive)
            {
                var idleObservation = io.ObserveTradeableInventory();
                if (idleObservation.IsAuthoritative &&
                    TradeQueueInventoryReconciler.Reconcile(queue, idleObservation.Stacks))
                {
                    save();
                }
                return;
            }

            var now = clock();
            if (deadline != default && now > deadline)
            {
                if (Snapshot.State == TradeQueueExecutionState.OfferingItems &&
                    now < batchRecoveryDeadline && io.IsTradeOpen)
                {
                    BeginClosing(TradeQueueExecutionState.OpeningTrade,
                        "Offer acknowledgement timed out; rebuilding the uncommitted trade.");
                    return;
                }
                Fail($"Timed out while {DescribeState(Snapshot.State)}. Last observation: {Snapshot.Message}");
                return;
            }

            switch (Snapshot.State)
            {
                case TradeQueueExecutionState.PreparingInventory:
                    TickPreparingInventory();
                    break;
                case TradeQueueExecutionState.NormalizingQuality:
                    TickNormalizingQuality();
                    break;
                case TradeQueueExecutionState.OpeningTrade:
                    TickOpeningTrade(now);
                    break;
                case TradeQueueExecutionState.OfferingItems:
                    TickOfferingItems(now);
                    break;
                case TradeQueueExecutionState.WaitingForPartner:
                    TickWaitingForPartner(now);
                    break;
                case TradeQueueExecutionState.ConfirmingTrade:
                    TickConfirmingTrade();
                    break;
                case TradeQueueExecutionState.VerifyingInventory:
                    TickVerifyingInventory(now);
                    break;
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Trade Queue runtime failed.");
            if (Snapshot.State == TradeQueueExecutionState.ClosingTrade)
                Snapshot = Snapshot with { Message = $"Waiting to close the owned trade. {exception.Message}" };
            else
                Fail($"Trade Queue failed: {exception.Message}");
        }
    }

    private void TickPreparingInventory()
    {
        if (!PartnerAvailableOrWaiting("The selected trade partner is no longer available while checking current inventory."))
            return;

        var observation = io.ObserveTradeableInventory();
        if (!observation.IsAuthoritative)
            return;

        StartFromAuthoritativeInventory(observation.Stacks);
    }

    public void Stop(string message = "Trade Queue stopped; unverified quantities remain queued.")
    {
        if (!IsActive)
            return;

        Finish(TradeQueueExecutionState.Stopped, message);
    }

    /// <summary>Stops waiting for a trade window to close: drops the pending cancellation and finishes the run.</summary>
    public void ForgetClosingTrade() =>
        ReleaseClosingTrade(TradeQueueExecutionState.Stopped, "Stopped waiting for the trade window to close.");

    private void ReleaseClosingTrade(TradeQueueExecutionState state, string message)
    {
        if (Snapshot.State != TradeQueueExecutionState.ClosingTrade && !recovery.CancellationPending)
            return;
        log.Warning(
            "[MarketMafioso] Trade Queue released trade closure ownership run={RunId} recipient={Recipient} tradeOpen={TradeOpen} pending={Pending} reason={Reason}",
            runId ?? "none", partner?.Name ?? "unselected", io.IsTradeOpen, pendingFinish?.Message ?? "none", message);
        recovery.CancellationPending = false;
        cancellationCheckpointSaved = false;
        pendingFinish = null;
        try
        {
            save();
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[MarketMafioso] Could not persist the released trade closure; continuing.");
        }
        // Ownership is released now, but auto-confirm must not come back while the window is still open.
        CompleteFinish(state, message, deferAutoConfirmWhileTradeOpen: true);
    }

    public void Dispose()
    {
        try
        {
            if (IsActive)
                Stop("Trade Queue stopped because MarketMafioso is unloading.");
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[MarketMafioso] Trade Queue stop during unload failed; continuing the unload.");
        }
        // The next load re-suppresses if the window is still open, or clears the leftover pause if it closed.
        if (recovery.CancellationPending || (restoreAutoConfirmWhenTradeCloses && TradeWindowMayBeOpen()))
            externalAutomation.RetainTradeSuppressionOnDispose();
        else
            externalAutomation.RestoreTradeAutoConfirm();
    }

    private void TickOpeningTrade(DateTimeOffset now)
    {
        if (!PartnerAvailableOrWaiting("The selected trade partner is no longer available. Queue execution stopped before opening another trade."))
            return;

        if (io.IsTradeOpen)
        {
            offerLineOperation = null;
            offerGilOperation = null;
            readyClicked = false;
            confirmationSubmitted = false;
            nextActionAt = now + timing.ActionDelay;
            SetActive(
                TradeQueueExecutionState.OfferingItems,
                $"Trade opened with {partner.Name}; offering batch {batchNumber:N0}.",
                OfferItemsTimeout);
            if (deadline > batchRecoveryDeadline)
                deadline = batchRecoveryDeadline;
            return;
        }

        if (now < nextActionAt)
            return;

        var commandSent = io.TryOpenTrade(partner);
        nextActionAt = now + (commandSent ? timing.TradeRetryDelay : timing.ActionDelay);
    }

    private void TickNormalizingQuality()
    {
        if (!PartnerAvailableOrWaiting("The selected trade partner is no longer available before inventory quality normalization completed."))
            return;

        var result = qualityLowering.Advance(
            () => IsActive &&
                  Snapshot.State == TradeQueueExecutionState.NormalizingQuality &&
                  partner != null &&
                  io.PartnerIsAvailable(partner));
        if (result.State == ItemQualityLoweringAutomationState.Failed)
        {
            Fail(result.Message);
            return;
        }
        if (result.State == ItemQualityLoweringAutomationState.Completed)
        {
            var observation = io.ObserveTradeableInventory();
            if (!observation.IsAuthoritative)
            {
                Snapshot = Snapshot with { Message = "Quality is ready; checking current inventory..." };
                return;
            }
            PrepareBatch(observation.Stacks, "Inventory quality is ready; opening the first trade.");
            return;
        }

        Snapshot = Snapshot with { Message = result.Message };
    }

    private void TickOfferingItems(DateTimeOffset now)
    {
        if (!io.IsTradeOpen)
        {
            Fail("Trade closed before MMF finished offering the current batch.");
            return;
        }
        if (batch == null)
        {
            Fail("Trade batch state is unavailable.");
            return;
        }

        var liveOffer = io.ObserveOffer();
        if (liveOffer == null)
        {
            Snapshot = Snapshot with { Message = "Verifying the trade recipient..." };
            return;
        }
        if (partner == null || liveOffer.PartnerEntityId != (uint)partner.GameObjectId)
        {
            Fail("The live trade recipient differs from the selected recipient.");
            return;
        }

        if (offerLineOperation != null)
        {
            var result = offerLineOperation.Advance(now);
            Snapshot = Snapshot with { Message = result.Message };
            if (result.IsFailed)
                Fail(result.Message);
            else if (result.IsCompleted || !offerLineOperation.HasDispatched)
            {
                offerLineOperation = null;
                nextActionAt = now + timing.ActionDelay;
            }
            return;
        }

        if (liveOffer.SyncPending)
            return;
        if (offerGilOperation == null && io.IsNumericInputOpen)
            return;
        // During our numeric-input protocol the provisional slot need not yet
        // contain the requested amount. Outside it, never lock an excess offer.
        if (offerGilOperation == null || offerGilOperation.IsCompleted)
        {
            var conflict = liveOffer.Mismatch(partner, batch, allowPartial: true);
            if (conflict != null)
            {
                Snapshot = Snapshot with { Message = $"Waiting for the offer to match the queue. {conflict}" };
                return;
            }
        }

        if (liveOffer.Gil != batch.GilAmount || offerGilOperation is { IsCompleted: false })
        {
            if (now < nextActionAt)
                return;

            if (offerGilOperation == null || offerGilOperation.IsCompleted)
                offerGilOperation = new TradeQueueOfferGilOperation(io, batch.GilAmount);
            var gilResult = offerGilOperation.Advance();
            Snapshot = Snapshot with { Message = gilResult.Message };
            if (gilResult.IsFailed)
            {
                Fail(gilResult.Message);
                return;
            }

            if (gilResult.ActionIssued || gilResult.IsCompleted)
                nextActionAt = now + timing.ActionDelay;
            return;
        }

        var inventory = io.ObserveTradeableInventory();
        if (!inventory.IsAuthoritative)
        {
            Snapshot = Snapshot with { Message = "Refreshing source inventory for the remaining offer..." };
            return;
        }
        if (liveOffer.Mismatch(partner, batch) == null)
        {
            batch = batch with { ExpectedInventoryBefore = TradeQueuePlanner.CountExactInventory(inventory.Stacks) };
            SetActive(
                TradeQueueExecutionState.WaitingForPartner,
                $"Batch {batchNumber:N0} is loaded; locking the trade when ready.",
                OfferItemsTimeout);
            if (deadline > batchRecoveryDeadline)
                deadline = batchRecoveryDeadline;
            return;
        }

        if (now < nextActionAt)
            return;

        var nextLine = TradeQueueOfferProgress.NextLine(batch, liveOffer, inventory.Stacks);
        if (nextLine == null)
        {
            // A manual partial offer can occupy the only usable source stack or
            // all five slots. Reopen before locking, rather than guessing how a
            // second native offer call would alter an already-offered stack.
            if (liveOffer.Items.Count > 0 && now < batchRecoveryDeadline)
            {
                BeginClosing(TradeQueueExecutionState.OpeningTrade, "Rebuilding the remaining trade from current inventory.");
            }
            else
                Snapshot = Snapshot with { Message = "Waiting for sufficient eligible inventory for the planned offer..." };
            return;
        }
        offerLineOperation = new TradeQueueOfferLineOperation(io, nextLine, liveOffer, deadline);
        var dispatched = offerLineOperation.Advance(now);
        Snapshot = Snapshot with { Message = dispatched.Message };
        if (dispatched.IsFailed)
            Fail(dispatched.Message);
        else if (!offerLineOperation.HasDispatched)
        {
            offerLineOperation = null;
            nextActionAt = now + timing.ActionDelay;
        }
    }

    private void TickWaitingForPartner(DateTimeOffset now)
    {
        if (!io.IsTradeOpen)
        {
            SetActive(
                TradeQueueExecutionState.VerifyingInventory,
                $"Trade closed; verifying batch {batchNumber:N0}.",
                CompletionTimeout);
            return;
        }
        if (readyClicked || now < nextActionAt)
            return;

        var offer = io.ObserveOffer();
        if (offer == null || offer.SyncPending || io.IsNumericInputOpen)
            return;
        if (partner == null || batch == null)
        {
            Fail("The expected trade recipient or batch is unavailable.");
            return;
        }
        if (offer.PartnerEntityId != (uint)partner.GameObjectId)
        {
            Fail("The live trade recipient changed before locking.");
            return;
        }
        if (offer.Mismatch(partner, batch) != null)
        {
            SetActive(TradeQueueExecutionState.OfferingItems, "Reconciling the changed offer before locking...", null);
            deadline = batchRecoveryDeadline;
            return;
        }

        var inventory = io.ObserveTradeableInventory();
        if (!inventory.IsAuthoritative)
            return;
        batch = batch with { ExpectedInventoryBefore = TradeQueuePlanner.CountExactInventory(inventory.Stacks) };

        NotifyBatch(BatchLocking, batch, "locking");
        if (!io.TryClickReady(out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                Fail(error);
            else
                Snapshot = Snapshot with { Message = $"Waiting for trade readiness. {io.ReadyDiagnostic}" };
            return;
        }

        readyClicked = true;
        SetActive(
            TradeQueueExecutionState.ConfirmingTrade,
            $"Batch {batchNumber:N0} is locked; waiting for partner confirmation.",
            PartnerTimeout);
    }

    private void TickConfirmingTrade()
    {
        if (!io.IsTradeOpen)
        {
            SetActive(
                TradeQueueExecutionState.VerifyingInventory,
                $"Trade closed; verifying batch {batchNumber:N0}.",
                CompletionTimeout);
            return;
        }
        if (confirmationSubmitted)
            return;

        if (!VerifyOffer())
            return;

        if (!io.TryConfirmTrade(out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                Fail(error);
            return;
        }

        confirmationSubmitted = true;
        SetActive(
            TradeQueueExecutionState.VerifyingInventory,
            $"Confirmed batch {batchNumber:N0}; waiting for inventory evidence.",
            CompletionTimeout);
    }

    private bool VerifyOffer()
    {
        var offer = io.ObserveOffer();
        if (offer == null || offer.SyncPending || io.IsNumericInputOpen)
        {
            Snapshot = Snapshot with { Message = "Verifying the live trade offer..." };
            return false;
        }
        if (partner == null || batch == null)
        {
            Fail("The expected trade recipient or batch is unavailable.");
            return false;
        }
        var mismatch = offer.Mismatch(partner, batch);
        if (mismatch == null)
            return true;
        Fail(mismatch);
        return false;
    }

    private void TickVerifyingInventory(DateTimeOffset now)
    {
        if (io.IsTradeOpen)
            return;
        if (batch == null)
        {
            Fail("Trade batch state is unavailable during verification.");
            return;
        }

        var observation = io.ObserveTradeableInventory();
        if (!observation.IsAuthoritative)
        {
            Snapshot = Snapshot with
            {
                Message = $"Trade closed; checking batch {batchNumber:N0} inventory evidence.",
            };
            return;
        }
        var inventory = observation.Stacks;
        if (!TradeQueuePlanner.HasExpectedInventoryDelta(batch, inventory, out var diagnostic))
        {
            Snapshot = Snapshot with
            {
                Message = $"Trade closed; waiting for batch {batchNumber:N0} inventory evidence. {diagnostic}",
            };
            return;
        }

        TradeQueuePlanner.ApplyCompletedBatch(queue, batch);
        completedUnitCount = checked(completedUnitCount + batch.UnitCount);
        completedBatchCount++;
        NotifyBatchVerified(batch);
        save();
        checkpointQueueSignature = ComputeQueueSignature(queue);
        if (queue.Count == 0)
        {
            Finish(
                TradeQueueExecutionState.Completed,
                $"Trade Queue completed with {partner?.Name} across {completedBatchCount:N0} verified batch(es).");
            return;
        }

        batchNumber = completedBatchCount + 1;
        nextActionAt = now;
        if (!PrepareBatch(
                inventory,
                $"Batch {completedBatchCount:N0} completed; opening batch {batchNumber:N0}."))
            return;

        TickOpeningTrade(now);
    }

    private void NotifyBatchVerified(TradeQueueBatch verified) => NotifyBatch(BatchVerified, verified, "verified");

    private void NotifyBatch(Action<TradeQueuePartner, TradeQueueBatch>? listeners, TradeQueueBatch planned, string stage)
    {
        if (partner == null || listeners == null)
            return;
        try
        {
            listeners(partner, planned);
        }
        catch (Exception exception)
        {
            // A ledger listener must never fail the trade run.
            log.Error(exception, "[MarketMafioso] Trade Queue batch {Stage} listener failed run={RunId} batch={Batch}.", stage, runId ?? "", batchNumber);
        }
    }

    private bool PrepareBatch(IReadOnlyList<TradeQueueInventoryStack> inventory, string message)
    {
        var validation = TradeQueuePlanner.Validate(queue.ToList(), inventory);
        if (!validation.Success)
        {
            Fail(validation.Message);
            return false;
        }

        batch = TradeQueuePlanner.BuildNextBatch(
            queue.ToList(),
            inventory,
            allowHighQualityItems: !normalizeHighQualityItemsForRun);
        NotifyBatch(BatchStarting, batch, "starting");
        batchRecoveryDeadline = clock() + OpenTradeTimeout + OfferItemsTimeout;
        offerLineOperation = null;
        offerGilOperation = null;
        readyClicked = false;
        confirmationSubmitted = false;
        SetActive(TradeQueueExecutionState.OpeningTrade, message, OpenTradeTimeout);
        return true;
    }

    private void SetActive(TradeQueueExecutionState state, string message, TimeSpan? timeout)
    {
        var previousState = Snapshot.State;
        partnerMissingSinceUtc = null;
        deadline = timeout is { } bounded && bounded > TimeSpan.Zero
            ? clock() + bounded
            : default;
        Snapshot = new(
            state,
            message,
            runId,
            partner?.Name,
            batchNumber,
            batch?.SlotCount ?? 0,
            completedBatchCount,
            initialUnitCount,
            completedUnitCount,
            queue.Count,
            queue.Sum(item => item.Quantity),
            true);
        if (previousState != state)
            LogTransition(previousState, state, message);
    }

    /// <summary>
    /// True when the partner is available. While no trade is open a missing partner is waited on for
    /// <see cref="PartnerMissingGrace"/>; after that, or with a trade open, the run fails with <paramref name="failure"/>.
    /// </summary>
    private bool PartnerAvailableOrWaiting(string failure)
    {
        if (partner != null && io.PartnerIsAvailable(partner))
        {
            partnerMissingSinceUtc = null;
            return true;
        }
        if (partner != null && !io.IsTradeOpen)
        {
            var now = clock();
            partnerMissingSinceUtc ??= now;
            if (now - partnerMissingSinceUtc.Value < PartnerMissingGrace)
            {
                Snapshot = Snapshot with { Message = $"Waiting for {partner.Name} to be targetable again." };
                return false;
            }
            failure += $" ({partner.Name} was not targetable for {PartnerMissingGrace.TotalSeconds:N0} s.)";
        }
        partnerMissingSinceUtc = null;
        Fail(failure);
        return false;
    }

    private void Fail(string message) => Finish(TradeQueueExecutionState.Failed, message);

    private void Finish(TradeQueueExecutionState state, string message)
    {
        if (state is TradeQueueExecutionState.Failed or TradeQueueExecutionState.Stopped &&
            io.IsTradeOpen)
        {
            BeginClosing(state, message);
            return;
        }

        CompleteFinish(state, message);
    }

    private void BeginClosing(TradeQueueExecutionState state, string message)
    {
        pendingFinish = (state, message);
        if (!recovery.CancellationPending)
            cancellationCheckpointSaved = false;
        if (Snapshot.State != TradeQueueExecutionState.ClosingTrade)
            closingDeadline = clock() + ClosingTradeTimeout;
        recovery.CancellationPending = true;
        recovery.PartnerEntityId = partner == null ? recovery.PartnerEntityId : (uint)partner.GameObjectId;
        restoreAutoConfirmWhenTradeCloses = false;
        externalAutomation.SuppressTradeAutoConfirm();
        nextActionAt = default;
        SetActive(TradeQueueExecutionState.ClosingTrade, $"{message} Waiting for trade closure.", null);
        TickClosingTrade();
    }

    private void TickClosingTrade()
    {
        if (pendingFinish is not { } terminal)
            throw new InvalidOperationException("Trade closure has no terminal result.");
        if (clock() > closingDeadline)
        {
            ReleaseClosingTrade(
                TradeQueueExecutionState.Failed,
                $"{terminal.Message} The trade window did not close within {ClosingTradeTimeout.TotalSeconds:N0}s; released it as is.");
            return;
        }
        if (clock() < nextActionAt)
            return;
        nextActionAt = clock() + timing.TradeRetryDelay;
        if (!cancellationCheckpointSaved)
        {
            if (!TrySaveCancellationState(true))
                return;
            cancellationCheckpointSaved = true;
        }
        if (io.IsTradeOpen)
        {
            // Invalid offer contents prevent confirmation, not cancellation of
            // our own session. Read recipient ownership independently.
            var partnerEntityId = io.ObserveTradePartnerEntityId();
            if (partnerEntityId == null)
                return;
            if (partnerEntityId != recovery.PartnerEntityId)
            {
                CompleteFinish(TradeQueueExecutionState.Stopped, "The previous trade session ended; a different trade was left untouched.");
                return;
            }
            io.TryCancelTrade(out var error);
            Snapshot = Snapshot with
            {
                Message = $"{terminal.Message} Waiting for trade closure." +
                          (string.IsNullOrWhiteSpace(error) ? string.Empty : $" {error}"),
            };
        }
        if (!io.IsTradeOpen)
        {
            if (terminal.State == TradeQueueExecutionState.OpeningTrade)
            {
                if (clock() > batchRecoveryDeadline)
                {
                    CompleteFinish(TradeQueueExecutionState.Failed, "Trade closed; the batch recovery deadline expired before rebuilding the offer.");
                    return;
                }
                var inventory = io.ObserveTradeableInventory();
                if (!inventory.IsAuthoritative)
                {
                    Snapshot = Snapshot with { Message = "Trade closed; refreshing inventory before rebuilding the offer..." };
                    return;
                }
                var recoveryDeadline = batchRecoveryDeadline;
                if (!TrySaveCancellationState(false))
                    return;
                if (!PrepareBatch(inventory.Stacks, terminal.Message))
                    return;
                pendingFinish = null;
                batchRecoveryDeadline = recoveryDeadline;
                if (deadline > recoveryDeadline)
                    deadline = recoveryDeadline;
                nextActionAt = clock();
            }
            else
            {
                CompleteFinish(terminal.State, $"{terminal.Message} The trade is closed.");
            }
        }
    }

    private bool TrySaveCancellationState(bool pending)
    {
        var previous = recovery.CancellationPending;
        recovery.CancellationPending = pending;
        try
        {
            save();
            return true;
        }
        catch (Exception exception)
        {
            recovery.CancellationPending = previous;
            log.Error(exception, "[MarketMafioso] Could not persist trade cancellation ownership.");
            Snapshot = Snapshot with { Message = "Retaining trade ownership while retrying cancellation checkpoint storage." };
            return false;
        }
    }

    private void CompleteFinish(TradeQueueExecutionState state, string message, bool deferAutoConfirmWhileTradeOpen = false)
    {
        var previousState = Snapshot.State;
        pendingFinish = (state, message);
        if (recovery.CancellationPending && !TrySaveCancellationState(false))
            return;
        pendingFinish = null;

        if (qualityLowering.Snapshot.IsActive)
            qualityLowering.Stop("Trade Queue released quality-lowering ownership.");
        if (deferAutoConfirmWhileTradeOpen && TradeWindowMayBeOpen())
            restoreAutoConfirmWhenTradeCloses = true;
        else
            externalAutomation.RestoreTradeAutoConfirm();
        deadline = default;
        nextActionAt = default;
        if (batch != null && state is TradeQueueExecutionState.Failed or TradeQueueExecutionState.Stopped)
            NotifyBatch(BatchAbandoned, batch, "abandoned");
        batch = null;
        Snapshot = new(
            state,
            message,
            runId,
            partner?.Name,
            batchNumber,
            0,
            completedBatchCount,
            initialUnitCount,
            completedUnitCount,
            queue.Count,
            queue.Sum(item => item.Quantity),
            false);
        LogTransition(previousState, state, message);
    }

    private void LogTransition(TradeQueueExecutionState previousState, TradeQueueExecutionState state, string reason)
    {
        const string template = "[MarketMafioso] Trade Queue transition run={RunId} recipient={Recipient}@{World} previous={PreviousState} state={State} batch={Batch} completedBatches={CompletedBatches} remainingLines={RemainingLines} remainingUnits={RemainingUnits} reason={Reason}";
        object[] values = [runId ?? "none", partner?.Name ?? "unselected", partner?.HomeWorldName ?? "",
            previousState, state, batchNumber, completedBatchCount, queue.Count, Snapshot.RemainingUnitCount, reason];
        if (state == TradeQueueExecutionState.Failed)
            log.Warning(template, values);
        else
            log.Information(template, values);
    }

    private static string ComputeQueueSignature(IEnumerable<TradeQueueItem> items) =>
        string.Join(
            "|",
            items
                .Where(item => item.Quantity > 0)
                .GroupBy(item => item.ItemId)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}:{group.Sum(item => item.Quantity)}"));

    private bool CanResumeWith(TradeQueuePartner selectedPartner) =>
        HasResumeCheckpoint &&
        selectedPartner.GameObjectId == partner!.GameObjectId &&
        selectedPartner.HomeWorldId == partner.HomeWorldId;

    private static string DescribeState(TradeQueueExecutionState state) => state switch
    {
        TradeQueueExecutionState.NormalizingQuality => "normalizing HQ inventory",
        TradeQueueExecutionState.PreparingInventory => "checking current inventory",
        TradeQueueExecutionState.OpeningTrade => "opening the trade",
        TradeQueueExecutionState.OfferingItems => "offering items",
        TradeQueueExecutionState.WaitingForPartner => "waiting for the partner",
        TradeQueueExecutionState.ConfirmingTrade => "waiting for trade confirmation",
        TradeQueueExecutionState.VerifyingInventory => "verifying inventory changes",
        _ => state.ToString(),
    };
}
