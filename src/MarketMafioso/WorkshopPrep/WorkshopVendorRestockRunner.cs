using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    private readonly GilVendorBuyCoordinator coordinator;
    private bool disposed;

    public WorkshopVendorRestockRunner(
        Configuration config,
        IGilVendorBuyRuntime runtime,
        IWorkshopQuartermasterRestockService quartermaster,
        Action save,
        Action<string>? diagnosticLog = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.quartermaster = quartermaster ?? throw new ArgumentNullException(nameof(quartermaster));
        this.save = save ?? throw new ArgumentNullException(nameof(save));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
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

    public bool TryStart(
        WorkshopVendorRestockReview review,
        QuartermasterOwnerScope owner,
        bool automaticallyBuyVendorMaterials,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(owner);
        if (IsRunning || ActiveRun?.Phase == WorkshopVendorRestockPhase.Paused)
        {
            error = "A workshop restock run is already active.";
            return false;
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
        config.ActiveWorkshopVendorRestockState = new WorkshopVendorRestockState
        {
            LocalContentId = owner.LocalContentId!.Value,
            HomeWorldId = owner.HomeWorldId!.Value,
            CharacterName = owner.CharacterName!,
            QueueSignature = review.QueueSignature,
            AutomaticallyBuyVendorMaterials = automaticallyBuyVendorMaterials,
            Phase = selected.Any(line => line.RetainerPlannedQuantity > 0)
                ? WorkshopVendorRestockPhase.RetrieveFromQuartermaster
                : WorkshopVendorRestockPhase.RefreshInventory,
            Message = "Workshop restock started.",
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
        if (!string.Equals(state.QueueSignature, currentQueueSignature, StringComparison.Ordinal))
        {
            Pause("The workshop queue changed. Restore the reviewed queue or stop this run.");
            return;
        }

        if (config.ActiveWorkshopVendorBuyRun is not null)
        {
            coordinator.Tick(ComposeContextSignature(currentOwner, currentQueueSignature));
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
        if (!OwnerMatches(state, owner) || !string.Equals(state.QueueSignature, queueSignature, StringComparison.Ordinal))
        {
            error = "The active owner or workshop queue does not match the frozen restock review.";
            return false;
        }
        if (config.ActiveWorkshopVendorBuyRun is not null)
        {
            var resumed = coordinator.Resume(ComposeContextSignature(owner, queueSignature), out error);
            if (!resumed) return false;
            error = string.Empty;
            return true;
        }
        state.Phase = state.ResumePhase is WorkshopVendorRestockPhase.Idle or WorkshopVendorRestockPhase.Paused
            ? WorkshopVendorRestockPhase.RefreshInventory
            : state.ResumePhase;
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
            WorkshopVendorRestockPhase.Failed or WorkshopVendorRestockPhase.Indeterminate)
            return false;
        if (config.ActiveWorkshopVendorBuyRun is not null)
            return coordinator.Stop(message);
        state.Phase = WorkshopVendorRestockPhase.Stopped;
        state.Message = message;
        runtime.EndAutomation();
        PersistPolicy();
        return true;
    }

    public static string ComposeContextSignature(QuartermasterOwnerScope owner, string queueSignature) =>
        $"{owner.LocalContentId!.Value.ToString(CultureInfo.InvariantCulture)}|{owner.HomeWorldId!.Value.ToString(CultureInfo.InvariantCulture)}|{queueSignature}";

    private void TickQuartermaster(WorkshopVendorRestockState state, QuartermasterOwnerScope owner)
    {
        if (!state.QuartermasterSubmitted)
        {
            var availability = state.Lines.Where(line => line.ReviewedRetainerQuantity > 0)
                .Select(line => new WorkshopMaterialAvailability(
                    line.ItemId, line.ItemName, 0, line.RequiredQuantity, line.LivePlayerQuantity,
                    line.ReviewedRetainerQuantity, line.ReviewedRetainerQuantity, 0, []))
                .ToArray();
            if (!quartermaster.Submit(owner, availability))
            {
                FinishPolicy(WorkshopVendorRestockPhase.Failed, quartermaster.LastStatus);
                return;
            }
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
                state.Phase = WorkshopVendorRestockPhase.RefreshInventory;
                state.Message = progress.Message;
                PersistPolicy();
                return;
            case WorkshopQuartermasterProgressState.Indeterminate:
                FinishPolicy(WorkshopVendorRestockPhase.Indeterminate, progress.Message);
                return;
            default:
                FinishPolicy(WorkshopVendorRestockPhase.Failed, progress.Message);
                return;
        }
    }

    private bool TryStartVendorEngine(bool waitForInventory, out string error)
    {
        var state = config.ActiveWorkshopVendorRestockState!;
        var inventory = runtime.CaptureInventory(state.Lines.Select(line => line.ItemId).ToArray());
        if (!inventory.IsComplete)
        {
            state.Message = inventory.Message;
            error = inventory.Message;
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
                ApprovedQuantity = Math.Min(
                    line.ApprovedVendorQuantity,
                    Math.Max(0, line.RequiredQuantity - line.LivePlayerQuantity)),
                UnitPriceGil = line.UnitPriceGil,
                ApprovedGilCeiling = line.ApprovedGilCeiling,
                Offer = line.Offer,
                AlternativeOffers = [.. line.AlternativeOffers],
            })
            .Where(line => line.ApprovedQuantity > 0)
            .ToList();
        var vendorIds = vendorLines.Select(line => line.ItemId).ToHashSet();
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
        if (vendorLines.Count == 0 || stops.Count == 0)
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
                (sum, line) => checked(sum + Math.Min(
                    line.ApprovedGilCeiling,
                    checked((ulong)line.ApprovedQuantity * line.UnitPriceGil)))),
            Lines = vendorLines,
            Stops = stops,
        };
        if (waitForInventory && !runtime.HasCapacity(
                vendorLines.ToDictionary(line => line.ItemId, line => line.ApprovedQuantity),
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
            AutomaticallyBuyVendorMaterials = state.AutomaticallyBuyVendorMaterials,
            Phase = engine is null ? state.Phase : ToWorkshopPhase(engine.Phase),
            Message = NormalizeMessage(engine?.Message ?? state.Message),
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
        config.ActiveWorkshopVendorRestockState = state;

        var resumeNeedsQuartermaster = legacy.Phase == WorkshopVendorRestockPhase.RetrieveFromQuartermaster ||
                                      (legacy.Phase == WorkshopVendorRestockPhase.Paused &&
                                       legacy.ResumePhase == WorkshopVendorRestockPhase.RetrieveFromQuartermaster);
        if (!resumeNeedsQuartermaster && legacy.AutomaticallyBuyVendorMaterials && legacy.Stops.Count > 0)
        {
            config.ActiveWorkshopVendorBuyRun = new GilVendorBuyRunSnapshot
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
                        ApprovedQuantity = checked(line.PurchasedQuantity + Math.Min(
                            Math.Max(0, line.RequiredQuantity - line.LivePlayerQuantity),
                            Math.Max(0, line.ApprovedVendorQuantity - line.PurchasedQuantity))),
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
        config.LegacyActiveWorkshopVendorRestock = null;
        config.WorkshopVendorRestockLegacyConversions++;
        diagnosticLog?.Invoke($"[MarketMafioso] Converted legacy workshop vendor restock run (conversion #{config.WorkshopVendorRestockLegacyConversions:N0}).");
        save();
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
        _ => GilVendorBuyPhase.RefreshPreconditions,
    };

    private static string NormalizeMessage(string message) => message
        .Replace("Vendor buy resumed.", "Workshop restock resumed.", StringComparison.Ordinal)
        .Replace("Vendor buy completed.", "Workshop vendor restock completed.", StringComparison.Ordinal)
        .Replace("vendor buy is now stopped", "workshop restock is now stopped", StringComparison.Ordinal);

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
