using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopVendorRestockRunner : IDisposable
{
    private readonly Configuration config;
    private readonly IGilVendorBuyRuntime runtime;
    private readonly IWorkshopQuartermasterRestockService quartermaster;
    private readonly Action save;
    private readonly Func<DateTimeOffset> utcNow;
    private GilVendorBuyCoordinator coordinator;
    private readonly Action<string>? diagnosticLog;
    private bool disposed;
    private readonly Func<string?> persistenceError;
    private readonly Func<string?> retainerAccessBlock;
    private string? warnedQueueSignature;

    public WorkshopVendorRestockRunner(
        Configuration config,
        IGilVendorBuyRuntime runtime,
        IWorkshopQuartermasterRestockService quartermaster,
        Action save,
        Action<string>? diagnosticLog = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string?>? persistenceError = null,
        Func<string?>? retainerAccessBlock = null)
    {
        this.retainerAccessBlock = retainerAccessBlock ?? (() => null);
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.quartermaster = quartermaster ?? throw new ArgumentNullException(nameof(quartermaster));
        this.save = save ?? throw new ArgumentNullException(nameof(save));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        this.persistenceError = persistenceError ?? (() => null);
        this.diagnosticLog = diagnosticLog;
        ConvertLegacyRunIfPresent(diagnosticLog);
        coordinator = new GilVendorBuyCoordinator(
            new ConfigurationGilVendorBuyRunStore(config, save),
            runtime,
            this.utcNow);
        if (config.ActiveWorkshopVendorBuyRun is null && IsPolicyRunning(config.ActiveWorkshopVendorRestockState))
            runtime.BeginAutomation();
    }

    public WorkshopVendorRestockRunView? ActiveRun => BuildView();

    public bool IsRunning => coordinator.IsRunning ||
                             (config.ActiveWorkshopVendorBuyRun is null &&
                              IsPolicyRunning(config.ActiveWorkshopVendorRestockState));

    private static bool HasUnresolvedPurchase(GilVendorBuyRunSnapshot? run) =>
        run is not null && (run.ArmedPurchase is not null ||
            run.Phase is GilVendorBuyPhase.Indeterminate or GilVendorBuyPhase.ReconcileReceipt);

    public string? UnresolvedPurchaseMessage
    {
        get
        {
            var run = config.ActiveWorkshopVendorBuyRun;
            if (!HasUnresolvedPurchase(run)) return null;
            var intent = run!.ArmedPurchase;
            var line = intent is not null
                ? run.Lines.FirstOrDefault(candidate => candidate.ItemId == intent.ItemId)
                : run.Lines.ElementAtOrDefault(run.LineIndex);
            var itemName = line?.ItemName;
            if (string.IsNullOrWhiteSpace(itemName))
                itemName = config.ActiveWorkshopVendorRestockState?.Lines
                    .FirstOrDefault(candidate => candidate.ItemId == (intent?.ItemId ?? line?.ItemId))?.ItemName;
            var purchase = intent is not null
                ? $"{intent.Quantity:N0} {(!string.IsNullOrWhiteSpace(itemName) ? itemName : $"item #{intent.ItemId}")}"
                : !string.IsNullOrWhiteSpace(itemName)
                    ? $"{itemName} (purchase quantity unavailable)"
                    : "an earlier purchase (item and quantity unavailable)";
            return $"Cannot start another restock: the outcome of buying {purchase} is still unknown. " +
                   "The saved purchase has been kept to prevent buying it twice." +
                   (string.IsNullOrWhiteSpace(run.Message) ? string.Empty : $" Details: {run.Message}");
        }
    }

    /// <summary>
    /// True for the few seconds a live run is buying or reading its receipt. Clearing then would forget a purchase
    /// that is about to be recorded, so the next restock could buy the same item again.
    /// </summary>
    public bool IsVerifyingPurchase =>
        coordinator.IsRunning &&
        config.ActiveWorkshopVendorBuyRun?.Phase is GilVendorBuyPhase.PurchaseLine or GilVendorBuyPhase.VerifyReceipt;

    public bool CanAcknowledgeUnresolvedPurchase =>
        HasUnresolvedPurchase(config.ActiveWorkshopVendorBuyRun) && !IsVerifyingPurchase;

    /// <summary>Stops the run and clears the current purchase record. Allowed in every state except a live receipt check.</summary>
    public bool AcknowledgeUnresolvedPurchase(out string message)
    {
        if (IsVerifyingPurchase)
        {
            message = "Verifying the purchase; it can be cleared once the receipt check finishes.";
            return false;
        }
        // Production routes this callback to Warning. Log before disposing or clearing anything; with no log
        // attached the clear still proceeds.
        diagnosticLog?.Invoke("[MarketMafioso] User acknowledged checking the unresolved vendor purchase in game; " +
                              "stopping the run and clearing its duplicate-purchase protection. Dropped record: " +
                              JsonSerializer.Serialize(new
                              {
                                  Run = config.ActiveWorkshopVendorBuyRun,
                                  Policy = config.ActiveWorkshopVendorRestockState,
                              }));
        // Disposing the coordinator ends its automation ownership; the policy stage holds its own.
        runtime.EndAutomation();
        coordinator.Dispose();
        config.ActiveWorkshopVendorBuyRun = null;
        config.ActiveWorkshopVendorRestockState = null;
        // The coordinator owns a separate snapshot: replace it too, or it could save the old run again.
        coordinator = new GilVendorBuyCoordinator(
            new ConfigurationGilVendorBuyRunStore(config, save), runtime, utcNow);
        save();
        message = "Saved purchase cleared after your in-game check. No purchase was submitted.";
        return true;
    }

    /// <summary>
    /// The signature the active run must be ticked and resumed against. A share run's demand is
    /// frozen, so it never follows the prep queue; prep-queue runs use <paramref name="prepQueueSignature"/>.
    /// </summary>
    public string ResolveDemandSignature(Func<string> prepQueueSignature)
    {
        ArgumentNullException.ThrowIfNull(prepQueueSignature);
        return config.ActiveWorkshopVendorRestockState?.FrozenRequirements is { } frozen
            ? WorkshopShareRestockDemand.Signature(frozen)
            : prepQueueSignature();
    }

    public bool TryStart(
        WorkshopVendorRestockReview review,
        QuartermasterOwnerScope owner,
        bool automaticallyBuyVendorMaterials,
        out string error,
        WorkshopRestockDemandSource? demand = null)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(owner);
        if (UnresolvedPurchaseMessage is { } unresolved)
        {
            error = unresolved;
            return false;
        }
        if (IsRunning || ActiveRun?.Phase == WorkshopVendorRestockPhase.Paused)
        {
            var active = config.ActiveWorkshopVendorRestockState?.DemandShareName;
            error = active is null
                ? "A workshop restock run is already active."
                : $"A workshop restock run for {active} is already active.";
            return false;
        }
        if (demand is not null &&
            WorkshopShareRestockDemand.Signature(demand.FrozenRequirements) is var frozenSignature &&
            !string.Equals(frozenSignature, review.QueueSignature, StringComparison.Ordinal))
        {
            diagnosticLog?.Invoke($"[MarketMafioso] Share restock started from a review that differs from the frozen demand: review signature {review.QueueSignature} != frozen signature {frozenSignature} for share {demand.ShareJobId}; continuing with the reviewed lines.");
        }
        if (!owner.IsAvailable || string.IsNullOrWhiteSpace(owner.CharacterName))
        {
            error = "Workshop restock requires the active character and home-world identity.";
            return false;
        }

        var selected = review.Materials.Where(line =>
                line.RetainerPlannedQuantity > 0 ||
                (automaticallyBuyVendorMaterials && line.Selected && line.ApprovedVendorQuantity > 0))
            .ToArray();
        if (selected.Length == 0)
        {
            error = "No reviewed workshop materials need restocking.";
            return false;
        }
        // The game refuses the summoning bell off the home world; Quartermaster would only see a retainer-list timeout.
        if (selected.Any(line => line.RetainerPlannedQuantity > 0) && retainerAccessBlock() is { } retainerBlock)
        {
            error = retainerBlock;
            diagnosticLog?.Invoke($"[MarketMafioso] Workshop restock refused before retainer retrieval: {retainerBlock}");
            return false;
        }
        if (automaticallyBuyVendorMaterials && review.VendorUnits > 0)
        {
            var vendorLines = selected
                .Where(line => line.Selected && line.ApprovedVendorQuantity > 0)
                .ToArray();
            var preflight = runtime.CaptureInventory(
                vendorLines.Select(line => line.Availability.ItemId).ToArray());
            if (!preflight.IsComplete || preflight.Gil is null)
            {
                error = preflight.Message;
                return false;
            }
            if (preflight.Gil.Value < review.MaximumGil)
            {
                error = $"The reviewed vendor plan requires up to {review.MaximumGil:N0} gil, but only {preflight.Gil.Value:N0} gil is available.";
                return false;
            }
            if (!runtime.HasCapacity(
                    vendorLines.ToDictionary(line => line.Availability.ItemId, line => line.ApprovedVendorQuantity),
                    out error))
                return false;
        }

        var now = utcNow().UtcDateTime;
        config.ActiveWorkshopVendorBuyRun = null;
        // The coordinator keeps its own copy of the previous (finished) vendor run; without a fresh one the
        // new run is reported through that stale engine (its phase, message and receipts), hiding this run's
        // retrieval progress and failures behind "Workshop vendor restock completed."
        coordinator.Dispose();
        coordinator = new GilVendorBuyCoordinator(
            new ConfigurationGilVendorBuyRunStore(config, save), runtime, utcNow);
        config.ActiveWorkshopVendorRestockState = new WorkshopVendorRestockState
        {
            LocalContentId = owner.LocalContentId!.Value,
            HomeWorldId = owner.HomeWorldId!.Value,
            CharacterName = owner.CharacterName!,
            QueueSignature = review.QueueSignature,
            DemandShareJobId = demand?.ShareJobId,
            DemandShareName = demand?.ShareName,
            FrozenRequirements = demand?.FrozenRequirements.Select(line => new WorkshopRestockDemandLine
            {
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                IconId = line.IconId,
                Quantity = line.Quantity,
            }).ToList(),
            AutomaticallyBuyVendorMaterials = automaticallyBuyVendorMaterials,
            Phase = selected.Any(line => line.RetainerPlannedQuantity > 0)
                ? WorkshopVendorRestockPhase.RetrieveFromQuartermaster
                : WorkshopVendorRestockPhase.RefreshInventory,
            Message = demand is null ? "Workshop restock started." : $"Workshop restock for {demand.ShareName} started.",
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            Lines = selected.Select(line => new WorkshopVendorRestockPolicyLine
            {
                ItemId = line.Availability.ItemId,
                ItemName = line.Availability.ItemName,
                RequiredQuantity = line.Availability.Required,
                ReviewedRetainerQuantity = line.RetainerPlannedQuantity,
                ApprovedVendorQuantity = automaticallyBuyVendorMaterials && line.Selected
                    ? line.ApprovedVendorQuantity
                    : 0,
                LivePlayerQuantity = line.Availability.PlayerInventory,
                UnitPriceGil = line.SelectedCandidate?.Offer.UnitPriceGil ?? 0,
                ApprovedGilCeiling = automaticallyBuyVendorMaterials ? line.ApprovedGil : 0,
                Offer = automaticallyBuyVendorMaterials && line.SelectedCandidate is not null
                    ? GilVendorBuyOfferSnapshot.From(line.SelectedCandidate.Offer)
                    : null,
                AlternativeOffers = automaticallyBuyVendorMaterials
                    ? line.Candidates
                        .Where(candidate => candidate.Access.IsEligible &&
                                            line.SelectedCandidate is not null &&
                                            !SameVendor(candidate.Offer.NpcId, candidate.Offer.ShopId,
                                                candidate.Offer.TerritoryId, line.SelectedCandidate.Offer.NpcId,
                                                line.SelectedCandidate.Offer.ShopId,
                                                line.SelectedCandidate.Offer.TerritoryId))
                        .Select(candidate => GilVendorBuyOfferSnapshot.From(candidate.Offer))
                        .ToList()
                    : [],
            }).ToList(),
            Stops = automaticallyBuyVendorMaterials
                ? review.Stops
                    .Where(stop => stop.Lines.Any(line => line.Selected && line.ApprovedVendorQuantity > 0))
                    .Select(stop => new GilVendorBuyStopSnapshot
                    {
                        NpcId = stop.NpcId,
                        ShopId = stop.ShopId,
                        TerritoryId = stop.TerritoryId,
                        NpcName = stop.NpcName,
                        ItemIds = stop.Lines
                            .Where(line => line.Selected && line.ApprovedVendorQuantity > 0)
                            .Select(line => line.Availability.ItemId)
                            .ToList(),
                    }).ToList()
                : [],
        };
        runtime.BeginAutomation();
        PersistPolicy();

        if (config.ActiveWorkshopVendorRestockState.Phase == WorkshopVendorRestockPhase.RetrieveFromQuartermaster)
        {
            error = string.Empty;
            return true;
        }
        if (TryStartVendorEngine(waitForInventory: false, out error))
            return true;

        config.ActiveWorkshopVendorRestockState = null;
        runtime.EndAutomation();
        save();
        return false;
    }

    public void Tick(string currentQueueSignature, QuartermasterOwnerScope currentOwner)
    {
        if (disposed || !IsRunning || config.ActiveWorkshopVendorRestockState is not { } state)
            return;
        if (!OwnerMatches(state, currentOwner))
        {
            Pause("The active character changed. Return to the reviewed owner to resume.");
            return;
        }
        if (!string.Equals(state.QueueSignature, currentQueueSignature, StringComparison.Ordinal) &&
            !string.Equals(warnedQueueSignature, currentQueueSignature, StringComparison.Ordinal))
        {
            warnedQueueSignature = currentQueueSignature;
            diagnosticLog?.Invoke($"[MarketMafioso] Workshop queue changed during restock ({state.QueueSignature} -> {currentQueueSignature}); continuing with the reviewed lines.");
        }

        if (config.ActiveWorkshopVendorBuyRun is not null)
        {
            coordinator.Tick(ComposeContextSignature(currentOwner, state.QueueSignature));
            return;
        }
        if (state.Phase == WorkshopVendorRestockPhase.RetrieveFromQuartermaster)
            TickQuartermaster(state, currentOwner);
        else if (state.Phase == WorkshopVendorRestockPhase.RefreshInventory)
            TryStartVendorEngine(waitForInventory: true, out _);
    }

    public bool Pause(string message = "Workshop restock paused.")
    {
        if (config.ActiveWorkshopVendorRestockState is not { } state || !IsRunning)
            return false;
        if (config.ActiveWorkshopVendorBuyRun is not null)
            return coordinator.Pause(message);
        state.ResumePhase = state.Phase;
        state.Phase = WorkshopVendorRestockPhase.Paused;
        state.Message = message;
        runtime.EndAutomation();
        PersistPolicy();
        return true;
    }

    public bool Resume(QuartermasterOwnerScope owner, string queueSignature, out string error)
    {
        if (config.ActiveWorkshopVendorRestockState is not { } state ||
            ActiveRun?.Phase != WorkshopVendorRestockPhase.Paused)
        {
            error = "No paused workshop restock run is available.";
            return false;
        }
        if (!OwnerMatches(state, owner))
        {
            error = "Return to the character that started this restock to resume it.";
            return false;
        }
        if (!string.Equals(state.QueueSignature, queueSignature, StringComparison.Ordinal))
            diagnosticLog?.Invoke($"[MarketMafioso] Resuming workshop restock although the workshop queue changed ({state.QueueSignature} -> {queueSignature}); continuing with the reviewed lines.");
        if (config.ActiveWorkshopVendorBuyRun is not null)
        {
            var resumed = coordinator.Resume(ComposeContextSignature(owner, state.QueueSignature), out error);
            if (!resumed) return false;
            error = string.Empty;
            return true;
        }
        state.Phase = state.ResumePhase is WorkshopVendorRestockPhase.Idle or WorkshopVendorRestockPhase.Paused
            ? WorkshopVendorRestockPhase.RefreshInventory
            : state.ResumePhase;
        // A pause must not spend the bounded retrieval reconcile window.
        state.RetrievalReconcileStartedAtUtc = null;
        state.Message = "Workshop restock resumed.";
        runtime.BeginAutomation();
        PersistPolicy();
        error = string.Empty;
        return true;
    }

    public bool Stop(string message = "Workshop restock stopped.")
    {
        if (config.ActiveWorkshopVendorRestockState is not { } state || ActiveRun?.Phase is
            WorkshopVendorRestockPhase.Completed or WorkshopVendorRestockPhase.Stopped or
            WorkshopVendorRestockPhase.Failed)
            return false;
        if (config.ActiveWorkshopVendorBuyRun is not null)
            return coordinator.Stop(message);
        state.Phase = WorkshopVendorRestockPhase.Stopped;
        state.Message = message;
        runtime.EndAutomation();
        PersistPolicy();
        return true;
    }

    public string? OpenQuartermasterOperation(QuartermasterOwnerScope owner) => quartermaster.OpenOperationId(owner);

    /// <summary>Drops the owner's Quartermaster operation record, stopping a run that is waiting on it.</summary>
    public bool ForgetQuartermasterOperation(QuartermasterOwnerScope owner)
    {
        if (config.ActiveWorkshopVendorBuyRun is null &&
            config.ActiveWorkshopVendorRestockState?.Phase == WorkshopVendorRestockPhase.RetrieveFromQuartermaster)
            Stop("Quartermaster operation forgotten; restock stopped.");
        return quartermaster.ForgetOperation(owner);
    }

    /// <summary>True when retainer retrieval ended indeterminate before any vendor purchase started.</summary>
    public bool CanRetryRetrieval =>
        config.ActiveWorkshopVendorBuyRun is null &&
        config.ActiveWorkshopVendorRestockState is { Phase: WorkshopVendorRestockPhase.Indeterminate } state &&
        state.Lines.Any(line => line.ReviewedRetainerQuantity > 0);

    /// <summary>Submits the reviewed retainer retrieval again with a fresh deadline.</summary>
    public bool RetryRetrieval(out string message)
    {
        if (!CanRetryRetrieval)
        {
            message = "No indeterminate retrieval to retry.";
            return false;
        }
        var state = config.ActiveWorkshopVendorRestockState!;
        diagnosticLog?.Invoke($"[MarketMafioso] Retrying indeterminate workshop retainer retrieval. Previous result: {state.Message}");
        state.Phase = WorkshopVendorRestockPhase.RetrieveFromQuartermaster;
        state.QuartermasterSubmitted = false;
        state.RetrievalReconcileStartedAtUtc = null;
        state.StartedAtUtc = utcNow().UtcDateTime;
        state.Message = "Retrying retainer retrieval.";
        runtime.BeginAutomation();
        PersistPolicy();
        message = state.Message;
        return true;
    }

    public static string ComposeContextSignature(QuartermasterOwnerScope owner, string queueSignature) =>
        $"{owner.LocalContentId!.Value.ToString(CultureInfo.InvariantCulture)}|{owner.HomeWorldId!.Value.ToString(CultureInfo.InvariantCulture)}|{queueSignature}";

    private static readonly TimeSpan QuartermasterSubmitRetryInterval = TimeSpan.FromSeconds(5);
    private DateTime? nextQuartermasterSubmitUtc;

    private void TickQuartermaster(WorkshopVendorRestockState state, QuartermasterOwnerScope owner)
    {
        if (utcNow().UtcDateTime - state.StartedAtUtc > TimeSpan.FromMinutes(15))
        {
            nextQuartermasterSubmitUtc = null;
            FinishPolicy(
                WorkshopVendorRestockPhase.Indeterminate,
                state.QuartermasterSubmitted
                    ? "Quartermaster retrieval deadline expired; no further retrieval was submitted."
                    : $"Quartermaster retrieval deadline expired before Quartermaster accepted the request; last error: {quartermaster.LastStatus}");
            return;
        }
        if (!state.QuartermasterSubmitted)
        {
            if (nextQuartermasterSubmitUtc is { } retryAt && utcNow().UtcDateTime < retryAt)
                return;
            var availability = state.Lines.Where(line => line.ReviewedRetainerQuantity > 0)
                .Select(line => new WorkshopMaterialAvailability(
                    line.ItemId, line.ItemName, 0, line.RequiredQuantity, line.LivePlayerQuantity,
                    line.ReviewedRetainerQuantity, line.ReviewedRetainerQuantity, 0, []))
                .ToArray();
            if (!quartermaster.Submit(owner, availability))
            {
                if (!quartermaster.LastFailureTransient)
                {
                    nextQuartermasterSubmitUtc = null;
                    FinishPolicy(WorkshopVendorRestockPhase.Failed, quartermaster.LastStatus);
                    return;
                }
                // Nothing new is minted: a transport failure replays the persisted request id next time.
                if (nextQuartermasterSubmitUtc is null)
                    diagnosticLog?.Invoke($"[MarketMafioso] Workshop retainer retrieval not accepted yet; retrying until the retrieval deadline. {quartermaster.LastStatus}");
                nextQuartermasterSubmitUtc = utcNow().UtcDateTime + QuartermasterSubmitRetryInterval;
                state.Message = $"Waiting for Quartermaster to accept the retrieval: {quartermaster.LastStatus}";
                return;
            }
            nextQuartermasterSubmitUtc = null;
            state.QuartermasterSubmitted = true;
            state.Message = "Retrieving reviewed workshop materials from retainers.";
            PersistPolicy();
            return;
        }

        var progress = quartermaster.GetProgress(owner);
        switch (progress.State)
        {
            case WorkshopQuartermasterProgressState.NotStarted:
            case WorkshopQuartermasterProgressState.Running:
                state.Message = progress.Message;
                return;
            case WorkshopQuartermasterProgressState.Completed:
            case WorkshopQuartermasterProgressState.PartiallySucceeded:
                if (progress.State == WorkshopQuartermasterProgressState.PartiallySucceeded)
                    state.QuartermasterPartialResult = progress.Message;
                state.Phase = WorkshopVendorRestockPhase.RefreshInventory;
                state.Message = progress.Message;
                PersistPolicy();
                return;
            case WorkshopQuartermasterProgressState.Indeterminate:
                ReconcileIndeterminateRetrieval(state, progress.Message);
                return;
            default:
                FinishPolicy(WorkshopVendorRestockPhase.Failed, progress.Message);
                return;
        }
    }

    private static readonly TimeSpan RetrievalReconcileDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// An indeterminate retrieval means movement may have happened. Bags are ground
    /// truth: when every reviewed retainer pull is present, continue to the vendor
    /// stage; otherwise stop. Never resubmits the retrieval, and the vendor stage
    /// keeps its own approved quantities and gil ceilings.
    /// </summary>
    private void ReconcileIndeterminateRetrieval(WorkshopVendorRestockState state, string quartermasterMessage)
    {
        var now = utcNow().UtcDateTime;
        if (state.RetrievalReconcileStartedAtUtc is null)
        {
            state.RetrievalReconcileStartedAtUtc = now;
            PersistPolicy();
        }
        var pulled = state.Lines.Where(line => line.ReviewedRetainerQuantity > 0).ToArray();
        var inventory = runtime.CaptureInventory(pulled.Select(line => line.ItemId).ToArray());
        if (!inventory.IsComplete)
        {
            if (now - state.RetrievalReconcileStartedAtUtc.Value > RetrievalReconcileDeadline)
            {
                FinishPolicy(WorkshopVendorRestockPhase.Indeterminate,
                    $"Quartermaster retrieval was indeterminate and inventory stayed unreadable past the {RetrievalReconcileDeadline.TotalSeconds:N0} s reconcile deadline ({inventory.Message}). Quartermaster: {quartermasterMessage}");
                return;
            }
            state.Message = "Quartermaster retrieval was indeterminate; waiting for inventory to confirm the retainer pulls.";
            return;
        }

        // LivePlayerQuantity still holds the pre-retrieval bag count in this phase.
        var shortfalls = pulled
            .Select(line => (Line: line,
                Expected: line.LivePlayerQuantity + line.ReviewedRetainerQuantity,
                Observed: inventory.ItemCounts.GetValueOrDefault(line.ItemId)))
            .Where(entry => entry.Observed < entry.Expected)
            .ToArray();
        if (shortfalls.Length > 0)
        {
            var detail = string.Join("; ", shortfalls.Select(entry =>
                $"{entry.Line.ItemName} bags {entry.Observed:N0}/{entry.Expected:N0} (short {entry.Expected - entry.Observed:N0})"));
            FinishPolicy(WorkshopVendorRestockPhase.Indeterminate,
                $"Quartermaster retrieval was indeterminate and bags do not hold every reviewed retainer pull: {detail}. No retrieval was resubmitted. Quartermaster: {quartermasterMessage}");
            return;
        }

        var confirmed = $"Retrieval was reported indeterminate; bags confirm all {pulled.Length} retainer pull(s).";
        diagnosticLog?.Invoke($"[MarketMafioso] Workshop restock retrieval reconciled from inventory: {confirmed} Continuing to vendor purchases. Quartermaster: {quartermasterMessage}");
        state.QuartermasterPartialResult = confirmed;
        state.Phase = WorkshopVendorRestockPhase.RefreshInventory;
        state.Message = confirmed;
        PersistPolicy();
    }

    private bool TryStartVendorEngine(bool waitForInventory, out string error)
    {
        var state = config.ActiveWorkshopVendorRestockState!;
        var inventory = runtime.CaptureInventory(state.Lines.Select(line => line.ItemId).ToArray());
        if (!inventory.IsComplete)
        {
            state.Message = inventory.Message;
            error = inventory.Message;
            if (waitForInventory && utcNow().UtcDateTime - state.UpdatedAtUtc > TimeSpan.FromSeconds(30))
                FinishPolicy(WorkshopVendorRestockPhase.Failed, "Inventory refresh deadline expired: " + inventory.Message);
            if (waitForInventory) return false;
            return false;
        }
        foreach (var line in state.Lines)
            line.LivePlayerQuantity = inventory.ItemCounts.GetValueOrDefault(line.ItemId);

        var vendorLines = state.Lines.Where(line =>
                state.AutomaticallyBuyVendorMaterials && line.ApprovedVendorQuantity > 0 && line.Offer is not null)
            .Select(line => new GilVendorBuyLineSnapshot
            {
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                ApprovedQuantity = line.ApprovedVendorQuantity,
                TargetTotalQuantity = line.RequiredQuantity,
                UnitPriceGil = line.UnitPriceGil,
                ApprovedGilCeiling = line.ApprovedGilCeiling,
                Offer = line.Offer,
                AlternativeOffers = [.. line.AlternativeOffers],
            })
            .Where(line => line.ApprovedQuantity > 0)
            .ToList();
        var vendorIds = vendorLines.Select(line => line.ItemId).ToHashSet();
        var liveQuantities = vendorLines
            .Select(line => new
            {
                line.ItemId,
                Quantity = Math.Min(
                    line.ApprovedQuantity,
                    Math.Max(0, line.TargetTotalQuantity!.Value - inventory.ItemCounts.GetValueOrDefault(line.ItemId))),
            })
            .Where(line => line.Quantity > 0)
            .ToDictionary(line => line.ItemId, line => line.Quantity);
        var stops = state.Stops.Select(stop => new GilVendorBuyStopSnapshot
            {
                NpcId = stop.NpcId,
                ShopId = stop.ShopId,
                TerritoryId = stop.TerritoryId,
                NpcName = stop.NpcName,
                ItemIds = stop.ItemIds.Where(vendorIds.Contains).ToList(),
            })
            .Where(stop => stop.ItemIds.Count > 0)
            .ToList();
        if (vendorLines.Count == 0 || liveQuantities.Count == 0 || stops.Count == 0)
        {
            foreach (var line in state.Lines)
                line.LivePlayerQuantity = inventory.ItemCounts.GetValueOrDefault(line.ItemId);
            FinishPolicy(
                WorkshopVendorRestockPhase.Completed,
                state.AutomaticallyBuyVendorMaterials
                    ? "Reviewed restock complete."
                    : "Quartermaster restock finished. Any remaining materials still need another source.");
            error = string.Empty;
            return true;
        }

        var plan = new GilVendorBuyPlan
        {
            MaximumApprovedGil = vendorLines.Aggregate(
                0UL,
                (sum, line) => checked(sum + line.ApprovedGilCeiling)),
            Lines = vendorLines,
            Stops = stops,
        };
        if (waitForInventory && !runtime.HasCapacity(
                liveQuantities,
                out var capacityError))
        {
            state.ResumePhase = WorkshopVendorRestockPhase.RefreshInventory;
            state.Phase = WorkshopVendorRestockPhase.Paused;
            state.Message = capacityError;
            runtime.EndAutomation();
            PersistPolicy();
            error = capacityError;
            return false;
        }
        if (!coordinator.TryStart(
                plan,
                ComposeContextSignature(new(state.LocalContentId, state.HomeWorldId, state.CharacterName, null), state.QueueSignature),
                out error))
        {
            error = NormalizeMessage(error);
            state.Message = error;
            if (waitForInventory)
                FinishPolicy(WorkshopVendorRestockPhase.Failed, error);
            return false;
        }
        return true;
    }

    private WorkshopVendorRestockRunView? BuildView()
    {
        if (config.ActiveWorkshopVendorRestockState is not { } state)
            return null;
        var engine = coordinator.ActiveRun;
        var receipts = engine?.Receipts ?? [];
        var engineLines = engine?.Lines.ToDictionary(line => line.ItemId) ?? [];
        return new WorkshopVendorRestockRunView
        {
            RunId = engine?.RunId ?? $"workshop-{state.StartedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture)}",
            QueueSignature = state.QueueSignature,
            DemandShareJobId = state.DemandShareJobId,
            DemandShareName = state.DemandShareName,
            AutomaticallyBuyVendorMaterials = state.AutomaticallyBuyVendorMaterials,
            Phase = engine is null ? state.Phase : ToWorkshopPhase(engine.Phase),
            Message = NormalizeMessage(engine?.Message ?? state.Message) +
                      (state.QuartermasterPartialResult is { } partial &&
                       !(engine?.Message ?? state.Message).Contains(partial, StringComparison.Ordinal)
                          ? $" {partial}" : string.Empty) +
                      (persistenceError() is { } error ? " " + error : string.Empty),
            Stops = engine?.Stops ?? state.Stops,
            Receipts = receipts,
            ArmedPurchase = engine?.ArmedPurchase,
            StopRequested = engine?.StopRequested ?? false,
            Lines = state.Lines.Select(policy =>
            {
                engineLines.TryGetValue(policy.ItemId, out var line);
                var lastReceipt = receipts.LastOrDefault(receipt => receipt.ItemId == policy.ItemId);
                return new WorkshopVendorRestockLineView
                {
                    ItemId = policy.ItemId,
                    ItemName = policy.ItemName,
                    RequiredQuantity = policy.RequiredQuantity,
                    ApprovedVendorQuantity = policy.ApprovedVendorQuantity,
                    PurchasedQuantity = line?.PurchasedQuantity ?? 0,
                    LivePlayerQuantity = lastReceipt?.AfterItemCount ?? policy.LivePlayerQuantity,
                    VendorUnavailable = line?.VendorUnavailable ?? false,
                    Status = line?.Status ??
                             (policy.LivePlayerQuantity >= policy.RequiredQuantity ? "Ready" : "Remaining"),
                    Offer = line?.Offer ?? policy.Offer,
                };
            }).ToArray(),
        };
    }

    private void ConvertLegacyRunIfPresent(Action<string>? diagnosticLog)
    {
        if (config.LegacyActiveWorkshopVendorRestock is not { } legacy)
            return;
        if (HasUnresolvedPurchase(config.ActiveWorkshopVendorBuyRun))
        {
            diagnosticLog?.Invoke("[MarketMafioso] Kept the unresolved vendor purchase instead of replacing it with a legacy run.");
            return;
        }
        var state = new WorkshopVendorRestockState
        {
            LocalContentId = legacy.LocalContentId,
            HomeWorldId = legacy.HomeWorldId,
            CharacterName = legacy.CharacterName,
            QueueSignature = legacy.QueueSignature,
            AutomaticallyBuyVendorMaterials = legacy.AutomaticallyBuyVendorMaterials,
            QuartermasterSubmitted = legacy.QuartermasterSubmitted,
            Phase = legacy.Phase,
            ResumePhase = legacy.ResumePhase,
            Message = legacy.Message,
            StartedAtUtc = legacy.StartedAtUtc,
            UpdatedAtUtc = legacy.UpdatedAtUtc,
            Lines = legacy.Lines.Select(line => new WorkshopVendorRestockPolicyLine
            {
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                RequiredQuantity = line.RequiredQuantity,
                ReviewedRetainerQuantity = line.ReviewedRetainerQuantity,
                ApprovedVendorQuantity = line.ApprovedVendorQuantity,
                LivePlayerQuantity = line.LivePlayerQuantity,
                UnitPriceGil = line.UnitPriceGil,
                ApprovedGilCeiling = line.ApprovedGilCeiling,
                Offer = line.Offer?.ToSnapshot(),
                AlternativeOffers = line.AlternativeOffers.Select(offer => offer.ToSnapshot()).ToList(),
            }).ToList(),
            Stops = legacy.Stops.Select(ToEngineStop).ToList(),
        };
        GilVendorBuyRunSnapshot? engineRun = null;
        var resumeNeedsQuartermaster = legacy.Phase == WorkshopVendorRestockPhase.RetrieveFromQuartermaster ||
                                      (legacy.Phase == WorkshopVendorRestockPhase.Paused &&
                                       legacy.ResumePhase == WorkshopVendorRestockPhase.RetrieveFromQuartermaster);
        if ((!resumeNeedsQuartermaster && legacy.AutomaticallyBuyVendorMaterials && legacy.Stops.Count > 0) ||
            legacy.ArmedPurchase is not null ||
            legacy.Phase is WorkshopVendorRestockPhase.Indeterminate or WorkshopVendorRestockPhase.ReconcileReceipt)
        {
            engineRun = new GilVendorBuyRunSnapshot
            {
                RunId = legacy.RunId,
                ContextSignature = ComposeContextSignature(
                    new(legacy.LocalContentId, legacy.HomeWorldId, legacy.CharacterName, null), legacy.QueueSignature),
                MaximumApprovedGil = legacy.MaximumApprovedGil,
                Phase = ToEnginePhase(legacy.Phase),
                ResumePhase = ToEnginePhase(legacy.ResumePhase),
                StopRequested = legacy.StopRequested,
                StopIndex = legacy.StopIndex,
                LineIndex = legacy.LineIndex,
                Message = legacy.Message,
                StartedAtUtc = legacy.StartedAtUtc,
                UpdatedAtUtc = legacy.UpdatedAtUtc,
                Lines = legacy.Lines.Where(line => line.ApprovedVendorQuantity > 0 && line.Offer is not null)
                    .Select(line => new GilVendorBuyLineSnapshot
                    {
                        ItemId = line.ItemId,
                        ItemName = line.ItemName,
                        ApprovedQuantity = line.ApprovedVendorQuantity,
                        TargetTotalQuantity = line.RequiredQuantity,
                        PurchasedQuantity = line.PurchasedQuantity,
                        PurchaseRetryCount = line.PurchaseRetryCount,
                        UnitPriceGil = line.UnitPriceGil,
                        ApprovedGilCeiling = line.ApprovedGilCeiling,
                        VendorUnavailable = line.VendorUnavailable,
                        Status = line.Status,
                        Offer = line.Offer!.ToSnapshot(),
                        AlternativeOffers = line.AlternativeOffers.Select(offer => offer.ToSnapshot()).ToList(),
                    }).ToList(),
                Stops = legacy.Stops.Select(ToEngineStop).ToList(),
                ArmedPurchase = legacy.ArmedPurchase is { } intent ? new GilVendorBuyArmedIntentSnapshot
                {
                    ItemId = intent.ItemId,
                    Quantity = intent.Quantity,
                    ExpectedGil = intent.ExpectedGil,
                    ShopRowIndex = intent.ShopRowIndex,
                    BeforeItemCount = intent.BeforeItemCount,
                    BeforeGil = intent.BeforeGil,
                    RetryCount = intent.RetryCount,
                    ArmedAtUtc = intent.ArmedAtUtc,
                } : null,
                Receipts = legacy.Receipts.Select(receipt => new GilVendorBuyReceiptSnapshot
                {
                    ItemId = receipt.ItemId,
                    Quantity = receipt.Quantity,
                    SpentGil = receipt.SpentGil,
                    BeforeItemCount = receipt.BeforeItemCount,
                    AfterItemCount = receipt.AfterItemCount,
                    BeforeGil = receipt.BeforeGil,
                    AfterGil = receipt.AfterGil,
                    VerifiedAtUtc = receipt.VerifiedAtUtc,
                }).ToList(),
            };
        }
        var previousState = config.ActiveWorkshopVendorRestockState;
        var previousEngineRun = config.ActiveWorkshopVendorBuyRun;
        var previousConversionCount = config.WorkshopVendorRestockLegacyConversions;
        config.ActiveWorkshopVendorRestockState = state;
        config.ActiveWorkshopVendorBuyRun = engineRun;
        config.WorkshopVendorRestockLegacyConversions = checked(previousConversionCount + 1);
        try
        {
            save();
        }
        catch
        {
            config.ActiveWorkshopVendorRestockState = previousState;
            config.ActiveWorkshopVendorBuyRun = previousEngineRun;
            config.WorkshopVendorRestockLegacyConversions = previousConversionCount;
            throw;
        }

        config.LegacyActiveWorkshopVendorRestock = null;
        diagnosticLog?.Invoke($"[MarketMafioso] Converted legacy workshop vendor restock run (conversion #{config.WorkshopVendorRestockLegacyConversions:N0}).");
    }

    private static GilVendorBuyStopSnapshot ToEngineStop(PersistedWorkshopVendorStop stop) => new()
    {
        NpcId = stop.NpcId,
        ShopId = stop.ShopId,
        TerritoryId = stop.TerritoryId,
        NpcName = stop.NpcName,
        ItemIds = [.. stop.ItemIds],
        MatchedShopRows = new(stop.MatchedShopRows),
        ShopValidated = stop.ShopValidated,
    };

    private static bool IsPolicyRunning(WorkshopVendorRestockState? state) => state?.Phase is
        WorkshopVendorRestockPhase.RetrieveFromQuartermaster or WorkshopVendorRestockPhase.RefreshInventory;

    private static bool OwnerMatches(WorkshopVendorRestockState state, QuartermasterOwnerScope owner) =>
        owner.LocalContentId == state.LocalContentId && owner.HomeWorldId == state.HomeWorldId;

    private static bool SameVendor(uint leftNpc, uint leftShop, uint leftTerritory, uint rightNpc, uint rightShop, uint rightTerritory) =>
        leftNpc == rightNpc && leftShop == rightShop && leftTerritory == rightTerritory;

    private void FinishPolicy(WorkshopVendorRestockPhase phase, string message)
    {
        var state = config.ActiveWorkshopVendorRestockState!;
        state.Phase = phase;
        state.Message = message;
        runtime.EndAutomation();
        PersistPolicy();
    }

    private void PersistPolicy()
    {
        if (config.ActiveWorkshopVendorRestockState is { } state)
            state.UpdatedAtUtc = utcNow().UtcDateTime;
        save();
    }

    private static WorkshopVendorRestockPhase ToWorkshopPhase(GilVendorBuyPhase phase) => phase switch
    {
        GilVendorBuyPhase.RefreshPreconditions => WorkshopVendorRestockPhase.RefreshInventory,
        GilVendorBuyPhase.ReachVendor => WorkshopVendorRestockPhase.ReachVendor,
        GilVendorBuyPhase.ValidateShop => WorkshopVendorRestockPhase.ValidateShop,
        GilVendorBuyPhase.PurchaseLine => WorkshopVendorRestockPhase.PurchaseLine,
        GilVendorBuyPhase.VerifyReceipt => WorkshopVendorRestockPhase.VerifyReceipt,
        GilVendorBuyPhase.Paused => WorkshopVendorRestockPhase.Paused,
        GilVendorBuyPhase.Completed => WorkshopVendorRestockPhase.Completed,
        GilVendorBuyPhase.Stopped => WorkshopVendorRestockPhase.Stopped,
        GilVendorBuyPhase.Failed => WorkshopVendorRestockPhase.Failed,
        GilVendorBuyPhase.ReconcileReceipt => WorkshopVendorRestockPhase.ReconcileReceipt,
        _ => WorkshopVendorRestockPhase.Indeterminate,
    };

    private static GilVendorBuyPhase ToEnginePhase(WorkshopVendorRestockPhase phase) => phase switch
    {
        WorkshopVendorRestockPhase.ReachVendor => GilVendorBuyPhase.ReachVendor,
        WorkshopVendorRestockPhase.ValidateShop => GilVendorBuyPhase.ValidateShop,
        WorkshopVendorRestockPhase.PurchaseLine => GilVendorBuyPhase.PurchaseLine,
        WorkshopVendorRestockPhase.VerifyReceipt => GilVendorBuyPhase.VerifyReceipt,
        WorkshopVendorRestockPhase.Paused => GilVendorBuyPhase.Paused,
        WorkshopVendorRestockPhase.Completed => GilVendorBuyPhase.Completed,
        WorkshopVendorRestockPhase.Stopped => GilVendorBuyPhase.Stopped,
        WorkshopVendorRestockPhase.Failed => GilVendorBuyPhase.Failed,
        WorkshopVendorRestockPhase.Indeterminate => GilVendorBuyPhase.Indeterminate,
        WorkshopVendorRestockPhase.ReconcileReceipt => GilVendorBuyPhase.ReconcileReceipt,
        _ => GilVendorBuyPhase.RefreshPreconditions,
    };

    private static string NormalizeMessage(string message)
    {
        var normalized = message
            .Replace("Vendor buy started.", "Workshop restock started.", StringComparison.Ordinal)
            .Replace("Vendor buy paused.", "Workshop restock paused.", StringComparison.Ordinal)
            .Replace("Vendor buy resumed.", "Workshop restock resumed.", StringComparison.Ordinal)
            .Replace("Vendor buy completed.", "Workshop vendor restock completed.", StringComparison.Ordinal)
            .Replace("vendor buy will resume", "restock will resume", StringComparison.Ordinal)
            .Replace("vendor buy is now stopped", "workshop restock is now stopped", StringComparison.Ordinal)
            .Replace("The vendor plan requires", "The reviewed vendor plan requires", StringComparison.Ordinal)
            .Replace("Remaining purchases require", "Remaining reviewed purchases require", StringComparison.Ordinal)
            .Replace("for the approved ", "for the reviewed ", StringComparison.Ordinal)
            .Replace(" item line(s) at ", " material line(s) at ", StringComparison.Ordinal)
            .Replace("continuing the vendor plan", "continuing the restock plan", StringComparison.Ordinal)
            .Replace("no accessible vendor remains", "no reviewed accessible vendor remains", StringComparison.Ordinal);
        if (!normalized.Contains("reviewed vendor stop(s)", StringComparison.Ordinal))
            normalized = normalized.Replace("vendor stop(s)", "reviewed vendor stop(s)", StringComparison.Ordinal);
        if (normalized.StartsWith("Choosing ", StringComparison.Ordinal) &&
            normalized.EndsWith("'s shop.", StringComparison.Ordinal))
            normalized = normalized[..^"'s shop.".Length] + "'s reviewed shop.";
        return normalized;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        coordinator.Dispose();
    }
}

internal sealed class ConfigurationGilVendorBuyRunStore : IGilVendorBuyRunStore
{
    private readonly Configuration config;
    private readonly Action save;

    public ConfigurationGilVendorBuyRunStore(Configuration config, Action save)
    {
        this.config = config;
        this.save = save;
    }

    public GilVendorBuyRunSnapshot? LoadCurrent() => config.ActiveWorkshopVendorBuyRun;

    public void Save(GilVendorBuyRunSnapshot snapshot)
    {
        config.ActiveWorkshopVendorBuyRun = snapshot;
        save();
    }
}
