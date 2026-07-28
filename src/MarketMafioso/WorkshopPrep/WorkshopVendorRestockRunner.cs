using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Automation.Vendors;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopVendorRestockRunner : IDisposable
{
    private static readonly TimeSpan ReceiptTimeout = TimeSpan.FromSeconds(4);
    private readonly Configuration config;
    private readonly IWorkshopVendorRestockRuntime runtime;
    private readonly Action save;
    private readonly Func<DateTimeOffset> utcNow;
    private bool disposed;

    public WorkshopVendorRestockRunner(
        Configuration config,
        IWorkshopVendorRestockRuntime runtime,
        Action save,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.save = save ?? throw new ArgumentNullException(nameof(save));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        if (IsRunning)
            runtime.BeginAutomation();
    }

    public PersistedWorkshopVendorRestockRun? ActiveRun => config.ActiveWorkshopVendorRestock;
    public bool IsRunning => ActiveRun?.Phase is
        WorkshopVendorRestockPhase.RetrieveFromQuartermaster or
        WorkshopVendorRestockPhase.RefreshInventory or
        WorkshopVendorRestockPhase.ReachVendor or
        WorkshopVendorRestockPhase.ValidateShop or
        WorkshopVendorRestockPhase.PurchaseLine or
        WorkshopVendorRestockPhase.VerifyReceipt;

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

        var selected = review.Materials
            .Where(line => line.RetainerPlannedQuantity > 0 ||
                           (automaticallyBuyVendorMaterials &&
                            line.Selected &&
                            line.ApprovedVendorQuantity > 0))
            .ToArray();
        if (selected.Length == 0)
        {
            error = "No reviewed workshop materials need restocking.";
            return false;
        }

        if (automaticallyBuyVendorMaterials && review.VendorUnits > 0)
        {
            var vendorItemIds = selected
                .Where(line => line.Selected && line.ApprovedVendorQuantity > 0)
                .Select(line => line.Availability.ItemId)
                .ToArray();
            var preflight = runtime.CaptureInventory(vendorItemIds);
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
            var quantities = selected
                .Where(line => line.Selected && line.ApprovedVendorQuantity > 0)
                .ToDictionary(line => line.Availability.ItemId, line => line.ApprovedVendorQuantity);
            if (!runtime.HasCapacity(quantities, out error))
                return false;
        }

        var now = utcNow().UtcDateTime;
        var run = new PersistedWorkshopVendorRestockRun
        {
            RunId = Guid.NewGuid().ToString("N"),
            LocalContentId = owner.LocalContentId!.Value,
            HomeWorldId = owner.HomeWorldId!.Value,
            CharacterName = owner.CharacterName!,
            QueueSignature = review.QueueSignature,
            AutomaticallyBuyVendorMaterials = automaticallyBuyVendorMaterials,
            MaximumApprovedGil = automaticallyBuyVendorMaterials ? review.MaximumGil : 0,
            Phase = selected.Any(line => line.RetainerPlannedQuantity > 0)
                ? WorkshopVendorRestockPhase.RetrieveFromQuartermaster
                : WorkshopVendorRestockPhase.RefreshInventory,
            ResumePhase = WorkshopVendorRestockPhase.Idle,
            Message = "Workshop restock started.",
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            Lines = selected.Select(line => new PersistedWorkshopVendorRestockLine
            {
                ItemId = line.Availability.ItemId,
                ItemName = line.Availability.ItemName,
                RequiredQuantity = line.Availability.Required,
                ReviewedRetainerQuantity = line.RetainerPlannedQuantity,
                ApprovedVendorQuantity = automaticallyBuyVendorMaterials && line.Selected
                    ? line.ApprovedVendorQuantity
                    : 0,
                UnitPriceGil = line.SelectedCandidate?.Offer.UnitPriceGil ?? 0,
                ApprovedGilCeiling = automaticallyBuyVendorMaterials
                    ? line.ApprovedGil
                    : 0,
                LivePlayerQuantity = line.Availability.PlayerInventory,
                Offer = automaticallyBuyVendorMaterials && line.SelectedCandidate is not null
                    ? PersistedGilVendorOffer.From(line.SelectedCandidate.Offer)
                    : null,
                AlternativeOffers = automaticallyBuyVendorMaterials
                    ? line.Candidates
                        .Where(candidate =>
                            candidate.Access.IsEligible &&
                            line.SelectedCandidate is not null &&
                            !SameVendor(candidate.Offer, line.SelectedCandidate.Offer))
                        .Select(candidate => PersistedGilVendorOffer.From(candidate.Offer))
                        .ToList()
                    : [],
            }).ToList(),
            Stops = automaticallyBuyVendorMaterials
                ? review.Stops
                    .Where(stop => stop.Lines.Any(line => line.Selected && line.ApprovedVendorQuantity > 0))
                    .Select(stop => new PersistedWorkshopVendorStop
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

        config.ActiveWorkshopVendorRestock = run;
        runtime.BeginAutomation();
        Persist();
        error = string.Empty;
        return true;
    }

    public void Tick(
        string currentQueueSignature,
        QuartermasterOwnerScope currentOwner)
    {
        if (disposed || ActiveRun is not { } run || !IsRunning)
            return;
        if (!OwnerMatches(run, currentOwner))
        {
            Pause("The active character changed. Return to the reviewed owner to resume.");
            return;
        }
        if (!string.Equals(run.QueueSignature, currentQueueSignature, StringComparison.Ordinal))
        {
            Pause("The workshop queue changed. Restore the reviewed queue or stop this run.");
            return;
        }

        switch (run.Phase)
        {
            case WorkshopVendorRestockPhase.RetrieveFromQuartermaster:
                TickQuartermaster(run, currentOwner);
                break;
            case WorkshopVendorRestockPhase.RefreshInventory:
                TickRefreshInventory(run);
                break;
            case WorkshopVendorRestockPhase.ReachVendor:
                TickReachVendor(run);
                break;
            case WorkshopVendorRestockPhase.ValidateShop:
                TickValidateShop(run);
                break;
            case WorkshopVendorRestockPhase.PurchaseLine:
                TickPurchaseLine(run);
                break;
            case WorkshopVendorRestockPhase.VerifyReceipt:
                TickVerifyReceipt(run);
                break;
        }
    }

    public bool Pause(string message = "Workshop restock paused.")
    {
        if (ActiveRun is not { } run || !IsRunning)
            return false;
        run.ResumePhase = run.Phase;
        run.Phase = WorkshopVendorRestockPhase.Paused;
        run.Message = message;
        runtime.EndAutomation();
        Persist();
        return true;
    }

    public bool Resume(QuartermasterOwnerScope owner, string queueSignature, out string error)
    {
        if (ActiveRun is not { Phase: WorkshopVendorRestockPhase.Paused } run)
        {
            error = "No paused workshop restock run is available.";
            return false;
        }
        if (!OwnerMatches(run, owner) ||
            !string.Equals(run.QueueSignature, queueSignature, StringComparison.Ordinal))
        {
            error = "The active owner or workshop queue does not match the frozen restock review.";
            return false;
        }
        run.Phase = run.ResumePhase is WorkshopVendorRestockPhase.Idle or WorkshopVendorRestockPhase.Paused
            ? WorkshopVendorRestockPhase.RefreshInventory
            : run.ResumePhase;
        run.Message = "Workshop restock resumed.";
        runtime.BeginAutomation();
        Persist();
        error = string.Empty;
        return true;
    }

    public bool Stop(string message = "Workshop restock stopped.")
    {
        if (ActiveRun is not { } run || run.Phase is
            WorkshopVendorRestockPhase.Completed or
            WorkshopVendorRestockPhase.Stopped or
            WorkshopVendorRestockPhase.Failed or
            WorkshopVendorRestockPhase.Indeterminate)
        {
            return false;
        }
        if (run.ArmedPurchase is not null)
        {
            run.StopRequested = true;
            run.Phase = WorkshopVendorRestockPhase.VerifyReceipt;
            run.Message = "Stop requested; reconciling the already-submitted purchase before stopping.";
            Persist();
            return true;
        }
        run.Phase = WorkshopVendorRestockPhase.Stopped;
        run.Message = message;
        runtime.CloseShop();
        runtime.EndAutomation();
        Persist();
        return true;
    }

    private void TickQuartermaster(
        PersistedWorkshopVendorRestockRun run,
        QuartermasterOwnerScope owner)
    {
        if (!run.QuartermasterSubmitted)
        {
            var availability = run.Lines
                .Where(line => line.ReviewedRetainerQuantity > 0)
                .Select(line => new WorkshopMaterialAvailability(
                    line.ItemId,
                    line.ItemName,
                    0,
                    line.RequiredQuantity,
                    line.LivePlayerQuantity,
                    line.ReviewedRetainerQuantity,
                    line.ReviewedRetainerQuantity,
                    0,
                    []))
                .ToArray();
            if (!runtime.TryStartQuartermaster(owner, availability, out var error))
            {
                Fail(WorkshopVendorRestockPhase.Failed, error);
                return;
            }
            run.QuartermasterSubmitted = true;
            run.Message = "Retrieving reviewed workshop materials from retainers.";
            Persist();
            return;
        }

        var progress = runtime.GetQuartermasterProgress(owner);
        switch (progress.State)
        {
            case WorkshopQuartermasterProgressState.NotStarted:
            case WorkshopQuartermasterProgressState.Running:
                run.Message = progress.Message;
                return;
            case WorkshopQuartermasterProgressState.Completed:
            case WorkshopQuartermasterProgressState.PartiallySucceeded:
                run.Phase = WorkshopVendorRestockPhase.RefreshInventory;
                run.Message = progress.Message;
                Persist();
                return;
            case WorkshopQuartermasterProgressState.Indeterminate:
                Fail(WorkshopVendorRestockPhase.Indeterminate, progress.Message);
                return;
            default:
                Fail(WorkshopVendorRestockPhase.Failed, progress.Message);
                return;
        }
    }

    private void TickRefreshInventory(PersistedWorkshopVendorRestockRun run)
    {
        var itemIds = run.Lines.Select(line => line.ItemId).ToArray();
        var snapshot = runtime.CaptureInventory(itemIds);
        if (!snapshot.IsComplete)
        {
            run.Message = snapshot.Message;
            return;
        }
        foreach (var line in run.Lines)
        {
            line.LivePlayerQuantity = snapshot.ItemCounts.GetValueOrDefault(line.ItemId);
            if (line.ApprovedVendorQuantity == 0)
                line.Status = line.LivePlayerQuantity >= line.RequiredQuantity ? "Ready" : "Remaining";
        }

        if (!run.AutomaticallyBuyVendorMaterials || run.Stops.Count == 0)
        {
            Complete("Quartermaster restock finished. Any remaining materials still need another source.");
            return;
        }
        var quantities = RemainingPurchaseQuantities(run);
        if (quantities.Count == 0)
        {
            Complete("Reviewed restock complete.");
            return;
        }
        if (snapshot.Gil is null)
        {
            Pause("Player gil is temporarily unavailable; restock will resume after it can be observed.");
            return;
        }
        var remainingGil = RemainingApprovedGil(run);
        if (snapshot.Gil.Value < remainingGil)
        {
            Fail(
                WorkshopVendorRestockPhase.Failed,
                $"Remaining reviewed purchases require up to {remainingGil:N0} gil, but only {snapshot.Gil.Value:N0} gil is available.");
            return;
        }
        if (!runtime.HasCapacity(quantities, out var capacityError))
        {
            Pause(capacityError);
            return;
        }

        NormalizeCurrentStop(run);
        if (run.StopIndex >= run.Stops.Count)
        {
            Complete("Workshop vendor restock completed.");
            return;
        }
        run.Phase = WorkshopVendorRestockPhase.ReachVendor;
        run.Message = $"Traveling to {run.Stops[run.StopIndex].NpcName}.";
        runtime.ResetVendorApproach();
        Persist();
    }

    private void TickReachVendor(PersistedWorkshopVendorRestockRun run)
    {
        var stop = CurrentStop(run);
        var offer = run.Lines
            .First(line => stop.ItemIds.Contains(line.ItemId) && RemainingForLine(line) > 0)
            .Offer!.ToOffer();
        var result = runtime.AdvanceToOpenShop(offer);
        run.Message = result.Message;
        switch (result.State)
        {
            case WorkshopVendorReachState.Waiting:
                return;
            case WorkshopVendorReachState.ShopOpen:
                run.Phase = WorkshopVendorRestockPhase.ValidateShop;
                Persist();
                return;
            case WorkshopVendorReachState.Unavailable:
                if (!TryReplanCurrentStop(run, out var replanMessage))
                {
                    Fail(
                        WorkshopVendorRestockPhase.Failed,
                        DescribeReachFailure(run, stop.NpcName, result.Message, replanMessage));
                    return;
                }
                run.Phase = WorkshopVendorRestockPhase.ReachVendor;
                run.Message = replanMessage;
                runtime.ResetVendorApproach();
                Persist();
                return;
            default:
                Fail(
                    WorkshopVendorRestockPhase.Failed,
                    DescribeReachFailure(run, stop.NpcName, result.Message));
                return;
        }
    }

    private static string DescribeReachFailure(
        PersistedWorkshopVendorRestockRun run,
        string npcName,
        string reason,
        string? alternative = null)
    {
        var spend = run.Receipts.Count == 0
            ? "No gil was spent."
            : "Verified purchases from earlier stops were preserved.";
        var next = string.IsNullOrWhiteSpace(alternative)
            ? reason
            : alternative;
        return $"Couldn't reach {npcName}. {spend} {next}".Trim();
    }

    private void TickValidateShop(PersistedWorkshopVendorRestockRun run)
    {
        var stop = CurrentStop(run);
        var read = runtime.ReadShopRows();
        if (!read.IsSuccess)
        {
            Fail(WorkshopVendorRestockPhase.Failed, read.Message);
            return;
        }

        var matches = new Dictionary<uint, int>();
        foreach (var itemId in stop.ItemIds)
        {
            var line = run.Lines.First(candidate => candidate.ItemId == itemId);
            if (RemainingForLine(line) <= 0)
                continue;
            var requestResult = GilVendorBuyRequest.Create(line.Offer!.ToOffer(), 1);
            if (!requestResult.IsSuccess)
            {
                Fail(WorkshopVendorRestockPhase.Failed, requestResult.Message);
                return;
            }
            var match = GilVendorShopMatcher.FindMatchingRow(requestResult.Request!, read.Rows);
            if (!match.IsSuccess)
            {
                Fail(WorkshopVendorRestockPhase.Failed, $"{line.ItemName}: {match.Message}");
                return;
            }
            matches[itemId] = match.Row!.RowIndex;
        }

        stop.MatchedShopRows = matches;
        stop.ShopValidated = true;
        run.LineIndex = 0;
        run.Phase = WorkshopVendorRestockPhase.PurchaseLine;
        run.Message = $"Validated {matches.Count:N0} material line(s) at {stop.NpcName}.";
        Persist();
    }

    private void TickPurchaseLine(PersistedWorkshopVendorRestockRun run)
    {
        var stop = CurrentStop(run);
        while (run.LineIndex < stop.ItemIds.Count)
        {
            var line = run.Lines.First(candidate => candidate.ItemId == stop.ItemIds[run.LineIndex]);
            var snapshot = runtime.CaptureInventory([line.ItemId]);
            if (!snapshot.IsComplete || snapshot.Gil is null)
            {
                Pause(snapshot.Message);
                return;
            }
            line.LivePlayerQuantity = snapshot.ItemCounts.GetValueOrDefault(line.ItemId);
            var remaining = RemainingForLine(line);
            if (remaining <= 0)
            {
                line.Status = line.LivePlayerQuantity >= line.RequiredQuantity ? "Ready" : "Ceiling reached";
                run.LineIndex++;
                continue;
            }

            var batch = Math.Min(
                remaining,
                Math.Clamp(runtime.ResolveMaximumBatch(line.ItemId), 1, 99));
            var lineSpent = run.Receipts
                .Where(receipt => receipt.ItemId == line.ItemId)
                .Aggregate(0UL, (sum, receipt) => checked(sum + receipt.SpentGil));
            var totalSpent = run.Receipts.Aggregate(
                0UL,
                (sum, receipt) => checked(sum + receipt.SpentGil));
            var lineGilRemaining = line.ApprovedGilCeiling >= lineSpent
                ? line.ApprovedGilCeiling - lineSpent
                : 0;
            var totalGilRemaining = run.MaximumApprovedGil >= totalSpent
                ? run.MaximumApprovedGil - totalSpent
                : 0;
            var affordableWithinCeilings = line.UnitPriceGil == 0
                ? 0
                : checked((int)Math.Min(
                    int.MaxValue,
                    Math.Min(lineGilRemaining, totalGilRemaining) / line.UnitPriceGil));
            batch = Math.Min(batch, affordableWithinCeilings);
            if (batch <= 0)
            {
                line.Status = "Gil ceiling reached";
                run.LineIndex++;
                continue;
            }
            if (!runtime.HasCapacity(
                    new Dictionary<uint, int> { [line.ItemId] = batch },
                    out var capacityError))
            {
                Pause(capacityError);
                return;
            }
            var request = GilVendorBuyRequest.Create(line.Offer!.ToOffer(), checked((uint)batch));
            if (!request.IsSuccess)
            {
                Fail(WorkshopVendorRestockPhase.Failed, request.Message);
                return;
            }
            if (snapshot.Gil.Value < request.Request!.MaxTotalGil)
            {
                Fail(WorkshopVendorRestockPhase.Failed, $"Not enough gil remains for the reviewed {line.ItemName} batch.");
                return;
            }

            run.ArmedPurchase = new PersistedWorkshopVendorPurchaseIntent
            {
                ItemId = line.ItemId,
                Quantity = batch,
                ExpectedGil = request.Request.MaxTotalGil,
                ShopRowIndex = stop.MatchedShopRows[line.ItemId],
                BeforeItemCount = line.LivePlayerQuantity,
                BeforeGil = snapshot.Gil.Value,
                RetryCount = line.PurchaseRetryCount,
                ArmedAtUtc = utcNow().UtcDateTime,
            };
            line.Status = $"Buying {batch:N0}";
            run.Phase = WorkshopVendorRestockPhase.VerifyReceipt;
            run.Message = $"Buying {batch:N0} {line.ItemName}.";
            Persist();

            try
            {
                if (!runtime.TrySubmitPurchase(
                        new GilVendorShopRow(
                            run.ArmedPurchase.ShopRowIndex,
                            line.ItemId,
                            line.UnitPriceGil),
                        checked((uint)batch),
                        out var submitError))
                {
                    run.ArmedPurchase = null;
                    Fail(WorkshopVendorRestockPhase.Failed, submitError);
                    return;
                }
            }
            catch (Exception ex)
            {
                run.ArmedPurchase = null;
                Fail(WorkshopVendorRestockPhase.Failed, $"Vendor purchase submission failed before a receipt could be observed: {ex.Message}");
            }
            return;
        }

        runtime.CloseShop();
        stop.ShopValidated = false;
        stop.MatchedShopRows.Clear();
        run.StopIndex++;
        run.LineIndex = 0;
        run.Phase = WorkshopVendorRestockPhase.RefreshInventory;
        Persist();
    }

    private void TickVerifyReceipt(PersistedWorkshopVendorRestockRun run)
    {
        if (run.ArmedPurchase is not { } intent)
        {
            Fail(WorkshopVendorRestockPhase.Indeterminate, "Purchase verification lost its persisted armed intent.");
            return;
        }
        runtime.TryConfirmPurchasePrompt();
        var line = run.Lines.First(candidate => candidate.ItemId == intent.ItemId);
        var snapshot = runtime.CaptureInventory([line.ItemId]);
        if (!snapshot.IsComplete || snapshot.Gil is null)
        {
            run.Message = snapshot.Message;
            return;
        }

        var request = GilVendorBuyRequest.Create(line.Offer!.ToOffer(), checked((uint)intent.Quantity)).Request!;
        var evidence = GilVendorPurchaseEvidenceClassifier.Classify(
            request,
            new(intent.BeforeItemCount, intent.BeforeGil),
            new(snapshot.ItemCounts.GetValueOrDefault(line.ItemId), snapshot.Gil.Value));
        if (evidence.Evidence == GilVendorPurchaseEvidence.Verified)
        {
            var receipt = evidence.Receipt!;
            run.Receipts.Add(new PersistedWorkshopVendorPurchaseReceipt
            {
                ItemId = receipt.ItemId,
                Quantity = checked((int)receipt.Quantity),
                SpentGil = receipt.SpentGil,
                BeforeItemCount = receipt.BeforeItemCount,
                AfterItemCount = receipt.AfterItemCount,
                BeforeGil = receipt.BeforeGil,
                AfterGil = receipt.AfterGil,
                VerifiedAtUtc = utcNow().UtcDateTime,
            });
            line.PurchasedQuantity = checked(line.PurchasedQuantity + (int)receipt.Quantity);
            line.PurchaseRetryCount = 0;
            line.LivePlayerQuantity = receipt.AfterItemCount;
            line.Status = $"Verified {line.PurchasedQuantity:N0} bought";
            run.ArmedPurchase = null;
            if (run.StopRequested)
            {
                FinishStopped(run, "The already-submitted purchase was verified; workshop restock is now stopped.");
                return;
            }
            run.Phase = WorkshopVendorRestockPhase.PurchaseLine;
            run.Message = $"Verified {receipt.Quantity:N0} {line.ItemName} for {receipt.SpentGil:N0} gil.";
            Persist();
            return;
        }
        if (evidence.Evidence == GilVendorPurchaseEvidence.Indeterminate)
        {
            Fail(WorkshopVendorRestockPhase.Indeterminate, $"{line.ItemName}: {evidence.Message}");
            return;
        }
        if (utcNow().UtcDateTime - intent.ArmedAtUtc < ReceiptTimeout)
            return;

        if (run.StopRequested)
        {
            run.ArmedPurchase = null;
            FinishStopped(run, "No mutation was observed from the submitted purchase; workshop restock is now stopped.");
            return;
        }
        if (intent.RetryCount == 0)
        {
            line.PurchaseRetryCount = 1;
            run.ArmedPurchase = null;
            run.Phase = WorkshopVendorRestockPhase.PurchaseLine;
            run.Message = $"No {line.ItemName} mutation was observed; retrying the unchanged batch once.";
            Persist();
            return;
        }
        Fail(
            WorkshopVendorRestockPhase.Failed,
            $"No {line.ItemName} mutation was observed after the single safe retry.");
    }

    private void NormalizeCurrentStop(PersistedWorkshopVendorRestockRun run)
    {
        while (run.StopIndex < run.Stops.Count &&
               run.Stops[run.StopIndex].ItemIds.All(itemId =>
                   RemainingForLine(run.Lines.First(line => line.ItemId == itemId)) <= 0))
        {
            run.StopIndex++;
        }
    }

    private static bool TryReplanCurrentStop(
        PersistedWorkshopVendorRestockRun run,
        out string message)
    {
        var failed = CurrentStop(run);
        var remainingLines = failed.ItemIds
            .Select(itemId => run.Lines.First(line => line.ItemId == itemId))
            .Where(line => RemainingForLine(line) > 0)
            .ToList();
        var replacementStops = new List<PersistedWorkshopVendorStop>();
        while (remainingLines.Count > 0)
        {
            var best = remainingLines
                .SelectMany(line => line.AlternativeOffers.Select(offer => new
                {
                    Line = line,
                    Offer = offer,
                    Key = (offer.NpcId, offer.ShopId, offer.TerritoryId, offer.NpcName),
                }))
                .GroupBy(candidate => candidate.Key)
                .OrderByDescending(group => group.Select(candidate => candidate.Line.ItemId).Distinct().Count())
                .ThenBy(group => group.Key.NpcId)
                .FirstOrDefault();
            if (best is null)
            {
                message = $"No other reviewed accessible vendor can cover {string.Join(", ", remainingLines.Select(line => line.ItemName))}.";
                return false;
            }

            var selectedByItem = best
                .GroupBy(candidate => candidate.Line.ItemId)
                .ToDictionary(group => group.Key, group => group.First());
            foreach (var selected in selectedByItem.Values)
            {
                selected.Line.Offer = selected.Offer;
                selected.Line.UnitPriceGil = selected.Offer.UnitPriceGil;
                selected.Line.AlternativeOffers.RemoveAll(offer => SameVendor(offer, selected.Offer));
                remainingLines.Remove(selected.Line);
            }
            replacementStops.Add(new PersistedWorkshopVendorStop
            {
                NpcId = best.Key.NpcId,
                ShopId = best.Key.ShopId,
                TerritoryId = best.Key.TerritoryId,
                NpcName = best.Key.NpcName,
                ItemIds = selectedByItem.Keys.Order().ToList(),
            });
        }

        run.Stops.RemoveAt(run.StopIndex);
        run.Stops.InsertRange(run.StopIndex, replacementStops);
        run.LineIndex = 0;
        message = $"The first vendor was unavailable; replanned {replacementStops.Count:N0} reviewed stop(s) without expanding any quantity or gil ceiling.";
        return true;
    }

    private static Dictionary<uint, int> RemainingPurchaseQuantities(
        PersistedWorkshopVendorRestockRun run) =>
        run.Lines
            .Select(line => new { line.ItemId, Remaining = RemainingForLine(line) })
            .Where(line => line.Remaining > 0)
            .ToDictionary(line => line.ItemId, line => line.Remaining);

    private static ulong RemainingApprovedGil(PersistedWorkshopVendorRestockRun run) =>
        Math.Min(
            run.MaximumApprovedGil - Math.Min(
                run.MaximumApprovedGil,
                run.Receipts.Aggregate(0UL, (sum, receipt) => checked(sum + receipt.SpentGil))),
            run.Lines.Aggregate(
            0UL,
            (sum, line) => checked(sum + ((ulong)RemainingForLine(line) * line.UnitPriceGil))));

    private static int RemainingForLine(PersistedWorkshopVendorRestockLine line)
    {
        var liveNeed = Math.Max(0, line.RequiredQuantity - line.LivePlayerQuantity);
        var remainingApproval = Math.Max(0, line.ApprovedVendorQuantity - line.PurchasedQuantity);
        return Math.Min(liveNeed, remainingApproval);
    }

    private static bool OwnerMatches(
        PersistedWorkshopVendorRestockRun run,
        QuartermasterOwnerScope owner) =>
        owner.LocalContentId == run.LocalContentId &&
        owner.HomeWorldId == run.HomeWorldId;

    private static bool SameVendor(GilVendorOffer left, GilVendorOffer right) =>
        left.NpcId == right.NpcId &&
        left.ShopId == right.ShopId &&
        left.TerritoryId == right.TerritoryId;

    private static bool SameVendor(PersistedGilVendorOffer left, PersistedGilVendorOffer right) =>
        left.NpcId == right.NpcId &&
        left.ShopId == right.ShopId &&
        left.TerritoryId == right.TerritoryId;

    private static PersistedWorkshopVendorStop CurrentStop(PersistedWorkshopVendorRestockRun run) =>
        run.Stops[run.StopIndex];

    private void Complete(string message)
    {
        if (ActiveRun is not { } run)
            return;
        run.Phase = WorkshopVendorRestockPhase.Completed;
        run.Message = message;
        runtime.CloseShop();
        runtime.EndAutomation();
        Persist();
    }

    private void FinishStopped(PersistedWorkshopVendorRestockRun run, string message)
    {
        run.StopRequested = false;
        run.Phase = WorkshopVendorRestockPhase.Stopped;
        run.Message = message;
        runtime.CloseShop();
        runtime.EndAutomation();
        Persist();
    }

    private void Fail(WorkshopVendorRestockPhase phase, string message)
    {
        if (ActiveRun is not { } run)
            return;
        run.Phase = phase;
        run.Message = message;
        runtime.CloseShop();
        runtime.EndAutomation();
        Persist();
    }

    private void Persist()
    {
        if (ActiveRun is { } run)
            run.UpdatedAtUtc = utcNow().UtcDateTime;
        save();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        runtime.EndAutomation();
    }
}
