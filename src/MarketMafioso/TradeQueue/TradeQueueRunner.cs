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
    private static readonly TimeSpan OfferItemsTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PartnerTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InventoryEvidenceSettleDelay = TimeSpan.FromSeconds(1);

    private readonly IList<TradeQueueItem> queue;
    private readonly TradeQueueTimingOptions timing;
    private readonly Action save;
    private readonly ITradeQueueIo io;
    private readonly IItemQualityLoweringAutomation qualityLowering;
    private readonly ExternalAutomationCoordinator externalAutomation;
    private readonly IPluginLog log;
    private readonly Func<DateTimeOffset> clock;

    private TradeQueuePartner? partner;
    private TradeQueueBatch? batch;
    private DateTimeOffset deadline;
    private DateTimeOffset nextActionAt;
    private DateTimeOffset verificationStartedAt;
    private int offeredLineIndex;
    private bool waitingForOfferedSlot;
    private bool quantitySubmitted;
    private bool gilInputRequested;
    private bool gilSubmitted;
    private bool readyClicked;
    private bool confirmationSubmitted;
    private int batchNumber;
    private int initialUnitCount;
    private int completedUnitCount;
    private int completedBatchCount;
    private string checkpointQueueSignature = string.Empty;
    private string? runId;

    public TradeQueueRunner(
        IList<TradeQueueItem> queue,
        TradeQueueTimingOptions timing,
        Action save,
        ITradeQueueIo io,
        IItemQualityLoweringAutomation qualityLowering,
        ExternalAutomationCoordinator externalAutomation,
        IPluginLog log)
        : this(queue, timing, save, io, qualityLowering, externalAutomation, log, () => DateTimeOffset.UtcNow)
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
        Func<DateTimeOffset> clock)
    {
        this.queue = queue;
        this.timing = timing;
        this.save = save;
        this.io = io;
        this.qualityLowering = qualityLowering;
        this.externalAutomation = externalAutomation;
        this.log = log;
        this.clock = clock;
    }

    public TradeQueueExecutionSnapshot Snapshot { get; private set; } =
        new(TradeQueueExecutionState.Idle, "Trade queue is idle.", null, null, 0, 0, 0, 0, 0, 0, 0, false);

    public bool IsActive => Snapshot.IsActive;

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
        if (IsActive)
            return new(false, "Trade queue is already running.");
        if (!io.TryGetSelectedPartner(out var selectedPartner))
            return new(false, "Select or focus-target the player who should receive this queue.");

        return Start(selectedPartner);
    }

    public TradeQueueStartResult Start(TradeQueuePartner selectedPartner)
    {
        if (IsActive)
            return new(false, "Trade queue is already running.");
        if (!io.PartnerIsAvailable(selectedPartner))
            return new(false, $"{selectedPartner.Name} @ {selectedPartner.HomeWorldName} is not an exact visible trade recipient.");

        var inventory = io.ScanTradeableInventory();
        var validation = TradeQueuePlanner.Validate(queue.ToList(), inventory);
        if (!validation.Success)
            return new(false, validation.Message);

        var isResume = CanResumeWith(selectedPartner);
        if (!isResume)
        {
            runId = Guid.NewGuid().ToString("N");
            initialUnitCount = queue.Sum(item => item.Quantity);
            completedUnitCount = 0;
            completedBatchCount = 0;
        }

        partner = selectedPartner;
        batchNumber = completedBatchCount + 1;
        checkpointQueueSignature = ComputeQueueSignature(queue);
        externalAutomation.SuppressTradeAutoConfirm();
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
                ? $"Resumed Trade Queue for {partner.Name} from the last verified batch."
                : $"Started Trade Queue for {partner.Name}.");
    }

    public void Tick()
    {
        if (!IsActive)
            return;

        try
        {
            var now = clock();
            if (deadline != default && now > deadline)
            {
                Fail($"Timed out while {DescribeState(Snapshot.State)}.");
                return;
            }

            switch (Snapshot.State)
            {
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
            Fail($"Trade Queue failed: {exception.Message}");
        }
    }

    public void Stop(string message = "Trade Queue stopped; unverified quantities remain queued.")
    {
        if (!IsActive)
            return;

        Finish(TradeQueueExecutionState.Stopped, message);
    }

    public void Dispose()
    {
        if (IsActive)
            Stop("Trade Queue stopped because MarketMafioso is unloading.");
        externalAutomation.RestoreTradeAutoConfirm();
    }

    private void TickOpeningTrade(DateTimeOffset now)
    {
        if (partner == null || !io.PartnerIsAvailable(partner))
        {
            Fail("The selected trade partner is no longer available. Queue execution stopped before opening another trade.");
            return;
        }

        if (io.IsTradeOpen)
        {
            offeredLineIndex = 0;
            waitingForOfferedSlot = false;
            quantitySubmitted = false;
            gilInputRequested = false;
            gilSubmitted = false;
            readyClicked = false;
            confirmationSubmitted = false;
            nextActionAt = now + timing.ActionDelay;
            SetActive(
                TradeQueueExecutionState.OfferingItems,
                $"Trade opened with {partner.Name}; offering batch {batchNumber:N0}.",
                OfferItemsTimeout);
            return;
        }

        if (now < nextActionAt)
            return;

        var commandSent = io.TryOpenTrade(partner);
        nextActionAt = now + (commandSent ? timing.TradeRetryDelay : timing.ActionDelay);
    }

    private void TickNormalizingQuality()
    {
        if (partner == null || !io.PartnerIsAvailable(partner))
        {
            Fail("The selected trade partner is no longer available before inventory quality normalization completed.");
            return;
        }

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
            PrepareBatch(io.ScanTradeableInventory(), "Inventory quality is ready; opening the first trade.");
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

        if (batch.GilAmount > 0 && !gilSubmitted)
        {
            if (now < nextActionAt)
                return;

            if (!gilInputRequested)
            {
                if (!io.TryOpenGilInput(out var gilError))
                {
                    if (!string.IsNullOrWhiteSpace(gilError))
                        Fail(gilError);
                    return;
                }

                gilInputRequested = true;
                nextActionAt = now + timing.ActionDelay;
                return;
            }

            if (!io.IsNumericInputOpen)
                return;
            if (!io.TrySubmitQuantity(batch.GilAmount, out var quantityError))
            {
                if (!string.IsNullOrWhiteSpace(quantityError))
                    Fail(quantityError);
                return;
            }

            gilSubmitted = true;
            nextActionAt = now + timing.ActionDelay;
            return;
        }

        if (offeredLineIndex >= batch.Lines.Count)
        {
            if (io.OfferedSlotCount != batch.Lines.Count)
                return;

            SetActive(
                TradeQueueExecutionState.WaitingForPartner,
                $"Batch {batchNumber:N0} is loaded; locking the trade when ready.",
                PartnerTimeout);
            return;
        }

        var offeredSlots = io.OfferedSlotCount;
        if (waitingForOfferedSlot)
        {
            if (offeredSlots > offeredLineIndex)
            {
                offeredLineIndex++;
                waitingForOfferedSlot = false;
                quantitySubmitted = false;
                return;
            }

            var pendingLine = batch.Lines[offeredLineIndex];
            if (!quantitySubmitted && pendingLine.SourceStackQuantity > 1 && io.IsNumericInputOpen)
            {
                if (!io.TrySubmitQuantity(pendingLine.Quantity, out var quantityError))
                {
                    if (!string.IsNullOrWhiteSpace(quantityError))
                        Fail(quantityError);
                    return;
                }
                quantitySubmitted = true;
                nextActionAt = now + timing.ActionDelay;
            }
            return;
        }

        if (offeredSlots != offeredLineIndex || now < nextActionAt)
            return;

        var line = batch.Lines[offeredLineIndex];
        if (!io.TryOfferItem(line, out var offerError))
        {
            if (!string.IsNullOrWhiteSpace(offerError))
                Fail(offerError);
            return;
        }

        waitingForOfferedSlot = true;
        quantitySubmitted = line.SourceStackQuantity <= 1;
        nextActionAt = now + timing.ActionDelay;
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

        if (!io.TryClickReady(out var error))
        {
            if (!string.IsNullOrWhiteSpace(error))
                Fail(error);
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

    private void TickVerifyingInventory(DateTimeOffset now)
    {
        if (io.IsTradeOpen)
            return;
        if (batch == null)
        {
            Fail("Trade batch state is unavailable during verification.");
            return;
        }

        var inventory = io.ScanTradeableInventory();
        if (!TradeQueuePlanner.HasExpectedInventoryDelta(batch, inventory, out var diagnostic))
        {
            if (verificationStartedAt == default)
                verificationStartedAt = now;
            if (now - verificationStartedAt < InventoryEvidenceSettleDelay)
            {
                Snapshot = Snapshot with
                {
                    Message = $"Trade closed; waiting for batch {batchNumber:N0} inventory evidence.",
                };
                return;
            }

            Fail(
                $"Batch {batchNumber:N0} was not completed. Verified progress through " +
                $"batch {completedBatchCount:N0} is saved; Resume continues with the remaining queue. {diagnostic}");
            return;
        }

        verificationStartedAt = default;
        TradeQueuePlanner.ApplyCompletedBatch(queue, batch);
        completedUnitCount = checked(completedUnitCount + batch.UnitCount);
        completedBatchCount++;
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

    private bool PrepareBatch(IReadOnlyList<TradeQueueInventoryStack> inventory, string message)
    {
        var validation = TradeQueuePlanner.Validate(queue.ToList(), inventory);
        if (!validation.Success)
        {
            Fail(validation.Message);
            return false;
        }

        batch = TradeQueuePlanner.BuildNextBatch(queue.ToList(), inventory);
        offeredLineIndex = 0;
        waitingForOfferedSlot = false;
        quantitySubmitted = false;
        gilInputRequested = false;
        gilSubmitted = false;
        readyClicked = false;
        confirmationSubmitted = false;
        verificationStartedAt = default;
        SetActive(TradeQueueExecutionState.OpeningTrade, message, OpenTradeTimeout);
        return true;
    }

    private void SetActive(TradeQueueExecutionState state, string message, TimeSpan? timeout)
    {
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
    }

    private void Fail(string message) => Finish(TradeQueueExecutionState.Failed, message);

    private void Finish(TradeQueueExecutionState state, string message)
    {
        if (qualityLowering.Snapshot.IsActive)
            qualityLowering.Stop("Trade Queue released quality-lowering ownership.");
        externalAutomation.RestoreTradeAutoConfirm();
        deadline = default;
        nextActionAt = default;
        verificationStartedAt = default;
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
        TradeQueueExecutionState.OpeningTrade => "opening the trade",
        TradeQueueExecutionState.OfferingItems => "offering items",
        TradeQueueExecutionState.WaitingForPartner => "waiting for the partner",
        TradeQueueExecutionState.ConfirmingTrade => "waiting for trade confirmation",
        TradeQueueExecutionState.VerifyingInventory => "verifying inventory changes",
        _ => state.ToString(),
    };
}
