using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Franthropy.Dalamud.Automation.MarketBoard;
using NativeMarketBoardListing = FFXIVClientStructs.FFXIV.Client.UI.Info.MarketBoardListing;

namespace MarketMafioso.MarketAcquisition.MarketBoard;

internal sealed class MarketListingPurchaseCoordinator : IDisposable
{
    private static readonly TimeSpan PurchaseDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BatchPacingDelay = TimeSpan.FromMilliseconds(1600);

    private readonly Configuration configuration;
    private readonly IMarketBoard marketBoard;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly MarketBoardPurchaseGuard purchaseGuard;
    private readonly string evidenceDirectory;
    private readonly Func<string?> getContextBlockReason;
    private readonly Func<bool> isListingRevisionVerified;
    private readonly Action<ulong> reconcileConfirmedPurchase;
    private readonly Action stateChanged;
    private readonly Action<string> outcomeChanged;

    private readonly List<MarketListingBatchItem> batchItems = [];
    private readonly HashSet<ulong> purchasedListingIds = [];
    private MarketListingPurchaseAttempt? attempt;

    public MarketListingPurchaseCoordinator(
        Configuration configuration,
        IMarketBoard marketBoard,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        IGameGui gameGui,
        IChatGui chatGui,
        IPluginLog log,
        MarketBoardPurchaseGuard purchaseGuard,
        string evidenceDirectory,
        Func<string?> getContextBlockReason,
        Func<bool> isListingRevisionVerified,
        Action<ulong> reconcileConfirmedPurchase,
        Action stateChanged,
        Action<string> outcomeChanged)
    {
        this.configuration = configuration;
        this.marketBoard = marketBoard;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
        this.gameGui = gameGui;
        this.chatGui = chatGui;
        this.log = log;
        this.purchaseGuard = purchaseGuard;
        this.evidenceDirectory = evidenceDirectory;
        this.getContextBlockReason = getContextBlockReason;
        this.isListingRevisionVerified = isListingRevisionVerified;
        this.reconcileConfirmedPurchase = reconcileConfirmedPurchase;
        this.stateChanged = stateChanged;
        this.outcomeChanged = outcomeChanged;

        marketBoard.PurchaseRequested += OnPurchaseRequested;
        marketBoard.ItemPurchased += OnItemPurchased;
    }

    public bool HasBatch => batchItems.Count > 0;
    public bool IsActive => attempt is not null;

    public bool WasPurchased(ulong listingId) => purchasedListingIds.Contains(listingId);

    public MarketListingBatchStatus? GetStatus(ulong listingId) =>
        batchItems.FirstOrDefault(item => item.ListingId == listingId)?.Status;

    public MarketListingBatchView? GetView() =>
        batchItems.Count == 0
            ? null
            : new(
                batchItems.Count,
                batchItems.Count(item => item.Status is
                    MarketListingBatchStatus.Confirmed or
                    MarketListingBatchStatus.Failed or
                    MarketListingBatchStatus.Skipped),
                batchItems.Count(item => item.Status == MarketListingBatchStatus.Failed),
                attempt is not null);

    public string? Start(IReadOnlyList<MarketListingSelection> staged)
    {
        if (staged.Count == 0)
            return "Select at least one listing.";
        if (HasBatch)
            return "A purchase batch is already in progress.";

        var stagedTotal = staged.Aggregate(0UL, (sum, selection) => sum + selection.TotalGil);
        if (GetCurrentGil() is { } gilOnHand && gilOnHand < stagedTotal)
            return $"Insufficient gil for this selection ({gilOnHand:N0} on hand).";

        foreach (var selection in staged)
            batchItems.Add(new(selection.ListingId, selection, MarketListingBatchStatus.Queued));
        stateChanged();
        Advance();
        return null;
    }

    public void Cancel()
    {
        foreach (var item in batchItems.Where(item => item.Status == MarketListingBatchStatus.Queued))
            item.Status = MarketListingBatchStatus.Skipped;
        if (attempt is null)
            Finish("Batch cancelled.");
        else
            stateChanged();
    }

    public void ResetStagedState()
    {
        ClearStagedPurchase();
        DismissLingeringConfirmationDialogs();
    }

    private void Advance()
    {
        if (attempt is not null)
            return;
        var next = batchItems.FirstOrDefault(item => item.Status == MarketListingBatchStatus.Queued);
        if (next is null)
        {
            Finish(null);
            return;
        }
        if (getContextBlockReason() is { } contextBlock)
        {
            SkipQueued();
            Finish(contextBlock);
            return;
        }
        if (!isListingRevisionVerified())
        {
            SkipQueued();
            Finish("Price verification was lost before the purchase request could be sent.");
            return;
        }

        next.Status = MarketListingBatchStatus.Sending;
        var pending = new MarketListingPurchaseAttempt(
            next.Selection,
            clientState.TerritoryType,
            objectTable.LocalPlayer?.Position.ToString() ?? "unavailable",
            DateTimeOffset.UtcNow);
        attempt = pending;
        stateChanged();

        string? failure = null;
        unsafe
        {
            var proxy = GetItemSearchProxy();
            var listing = proxy == null ? null : FindListing(proxy, next.ListingId);
            if (listing is null)
            {
                failure = "The listing left the results before it could be staged.";
            }
            else
            {
                var staged = listing.Value;
                var listingPointer = (NativeMarketBoardListing*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref staged);
                if (!proxy->SetLastPurchasedItem(listingPointer))
                {
                    failure = "The client refused to stage the listing.";
                }
                else
                {
                    pending.Phase = MarketListingPurchasePhase.Sending;
                    pending.SentAtUtc = DateTimeOffset.UtcNow;
                    pending.DeadlineAtUtc = pending.SentAtUtc + PurchaseDeadline;
                    pending.GilBeforeSend = GetCurrentGil();
                    if (!purchaseGuard.SendOwned(proxy))
                        failure = "The client refused to send the purchase request.";
                }
            }
        }

        if (failure is not null)
        {
            next.Status = MarketListingBatchStatus.Failed;
            Fail(pending, failure);
            return;
        }

        if (!ReferenceEquals(attempt, pending))
            return;

        pending.Phase = MarketListingPurchasePhase.Sent;
        log.Information(
            "[MarketMafioso] Listing purchase sent. ListingId={ListingId} ItemId={ItemId} Quantity={Quantity} TotalGil={TotalGil}",
            pending.Selection.ListingId,
            pending.Selection.ItemId,
            pending.Selection.Quantity,
            pending.Selection.TotalGil);
        framework.RunOnTick(() =>
        {
            if (ReferenceEquals(attempt, pending) && pending.Phase == MarketListingPurchasePhase.Sent)
                ResolveIndeterminate(pending);
        }, PurchaseDeadline + TimeSpan.FromSeconds(1));
        framework.RunOnTick(DismissLingeringConfirmationDialogs, TimeSpan.FromMilliseconds(500));
    }

    private void OnPurchaseRequested(IMarketBoardPurchaseHandler purchase)
    {
        if (attempt is not { } pending ||
            !IsPurchaseRequestCorrelatablePhase(pending.Phase))
        {
            log.Information(
                "[MarketMafioso] Native-path purchase request observed outside an MMF listing attempt. ListingId={ListingId} CatalogId={CatalogId} Quantity={Quantity} PricePerUnit={PricePerUnit}",
                purchase.ListingId,
                purchase.CatalogId,
                purchase.ItemQuantity,
                purchase.PricePerUnit);
            return;
        }

        pending.PacketObserved = true;
        pending.PacketMatchesIntent = MarketBoardPurchaseEvidenceClassifier.PacketMatches(
            ToIntent(pending.Selection),
            new(
                purchase.ListingId,
                purchase.CatalogId,
                purchase.ItemQuantity,
                purchase.PricePerUnit));
        if (!pending.PacketMatchesIntent)
        {
            pending.Phase = MarketListingPurchasePhase.Conflicted;
            MarkActiveItem(MarketListingBatchStatus.Failed);
            Complete(pending, "The observed purchase packet did not match the staged listing.");
        }
    }

    private void OnItemPurchased(IMarketBoardPurchase purchase)
    {
        if (attempt is not { } pending ||
            !IsPurchaseRequestCorrelatablePhase(pending.Phase))
            return;

        pending.GilAfterResponse = GetCurrentGil();
        var evidence = MarketBoardPurchaseEvidenceClassifier.ClassifyResponse(
            ToIntent(pending.Selection),
            purchase.CatalogId,
            purchase.ItemQuantity,
            pending.GilBeforeSend,
            pending.GilAfterResponse);
        switch (evidence.Evidence)
        {
            case MarketBoardPurchaseEvidence.Unrelated:
                return;
            case MarketBoardPurchaseEvidence.Verified:
                purchasedListingIds.Add(pending.Selection.ListingId);
                reconcileConfirmedPurchase(pending.Selection.ListingId);
                pending.Phase = MarketListingPurchasePhase.Confirmed;
                MarkActiveItem(MarketListingBatchStatus.Confirmed);
                Complete(
                    pending,
                    $"Purchased {pending.Quantity}x {pending.ItemName} for {pending.TotalGil:N0} gil.");
                return;
            case MarketBoardPurchaseEvidence.Rejected:
                NoteRejectedTerritory();
                pending.Phase = MarketListingPurchasePhase.Failed;
                MarkActiveItem(MarketListingBatchStatus.Failed);
                Complete(pending, "The server rejected the purchase. No gil moved.");
                return;
            default:
                pending.Phase = MarketListingPurchasePhase.Indeterminate;
                MarkActiveItem(MarketListingBatchStatus.Failed);
                Complete(
                    pending,
                    evidence.GilDelta is { } delta
                        ? $"A purchase response arrived but gil moved by {delta:N0} instead of {pending.TotalGil:N0}. Reconcile before retrying."
                        : "A purchase response arrived but gil state was unavailable. Reconcile before retrying.");
                return;
        }
    }

    private void ResolveIndeterminate(MarketListingPurchaseAttempt pending)
    {
        pending.Phase = MarketListingPurchasePhase.Indeterminate;
        MarkActiveItem(MarketListingBatchStatus.Failed);
        Complete(
            pending,
            "No purchase confirmation arrived before the deadline. Reconcile inventory and gil before retrying.");
    }

    private void Fail(MarketListingPurchaseAttempt pending, string reason)
    {
        pending.Phase = MarketListingPurchasePhase.Failed;
        Complete(pending, reason);
    }

    private void Complete(MarketListingPurchaseAttempt pending, string message)
    {
        if (pending.SentAtUtc is not null)
            ClearStagedPurchase();
        pending.FailureReason = message;
        attempt = null;
        stateChanged();
        log.Information(
            "[MarketMafioso] Listing purchase {Phase}. ListingId={ListingId} PacketObserved={PacketObserved} PacketMatchesIntent={PacketMatchesIntent} Message={Message}",
            pending.Phase,
            pending.Selection.ListingId,
            pending.PacketObserved,
            pending.PacketMatchesIntent,
            message);
        WriteEvidence(pending);
        framework.RunOnTick(Advance, BatchPacingDelay);
    }

    private void Finish(string? abortReason)
    {
        if (batchItems.Count == 0)
            return;

        var confirmed = batchItems.Count(item => item.Status == MarketListingBatchStatus.Confirmed);
        var failed = batchItems.Count(item => item.Status == MarketListingBatchStatus.Failed);
        var skipped = batchItems.Count(item => item.Status == MarketListingBatchStatus.Skipped);
        var outcome = abortReason is not null
            ? $"Batch aborted: {abortReason} ({confirmed} confirmed, {failed} failed, {skipped} skipped)"
            : $"Batch complete: {confirmed} confirmed, {failed} failed, {skipped} skipped.";
        log.Information("[MarketMafioso] Market listings: {Outcome}", outcome);
        chatGui.Print($"[MMF] Market listings: {outcome}");
        batchItems.Clear();
        outcomeChanged(outcome);
    }

    private void SkipQueued()
    {
        foreach (var item in batchItems.Where(item => item.Status == MarketListingBatchStatus.Queued))
            item.Status = MarketListingBatchStatus.Skipped;
    }

    private static MarketBoardPurchaseIntent ToIntent(MarketListingSelection selection) =>
        new(
            selection.ListingId,
            selection.ItemId,
            selection.Quantity,
            selection.UnitPrice,
            selection.TotalGil);

    private void MarkActiveItem(MarketListingBatchStatus status)
    {
        var active = batchItems.FirstOrDefault(item => item.Status == MarketListingBatchStatus.Sending);
        if (active is not null)
            active.Status = status;
    }

    private void NoteRejectedTerritory()
    {
        var territory = clientState.TerritoryType;
        if (configuration.MarketListingRejectedTerritories.Contains(territory))
            return;
        configuration.MarketListingRejectedTerritories.Add(territory);
        configuration.Save();
        log.Information(
            "[MarketMafioso] Market listings recorded territory {Territory} as purchase-rejecting.",
            territory);
    }

    private void WriteEvidence(MarketListingPurchaseAttempt pending)
    {
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            var path = Path.Combine(
                evidenceDirectory,
                $"listing-purchase-{pending.StagedAtUtc:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    pending.ToEvidence(),
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[MarketMafioso] Listing-purchase evidence could not be written.");
        }
    }

    private static unsafe NativeMarketBoardListing? FindListing(
        InfoProxyItemSearch* proxy,
        ulong listingId)
    {
        var count = (int)Math.Min(proxy->ListingCount, 100);
        for (var index = 0; index < count; index++)
        {
            var listing = proxy->Listings[index];
            if (listing.ListingId == listingId)
                return listing;
        }
        return null;
    }

    private static unsafe InfoProxyItemSearch* GetItemSearchProxy()
    {
        var infoModule = InfoModule.Instance();
        return infoModule == null
            ? null
            : (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
    }

    private static unsafe uint? GetCurrentGil()
    {
        var inventoryManager = InventoryManager.Instance();
        return inventoryManager == null ? null : inventoryManager->GetGil();
    }

    private void ClearStagedPurchase()
    {
        unsafe
        {
            var proxy = GetItemSearchProxy();
            if (proxy != null)
                proxy->LastPurchasedMarketboardItem.ListingId = 0;
        }
    }

    private void DismissLingeringConfirmationDialogs()
    {
        string[] dialogAddons = ["SelectYesno", "SelectOk"];
        unsafe
        {
            foreach (var addonName in dialogAddons)
            {
                var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)gameGui
                    .GetAddonByName(addonName, 1)
                    .Address;
                if (addon == null || !addon->IsVisible)
                    continue;
                log.Information(
                    "[MarketMafioso] Market listings dismissing lingering dialog addon {AddonName}.",
                    addonName);
                addon->Close(false);
            }
        }
    }

    internal static bool IsPurchaseRequestCorrelatablePhase(MarketListingPurchasePhase phase) =>
        phase is MarketListingPurchasePhase.Sending or MarketListingPurchasePhase.Sent;

    public void Dispose()
    {
        marketBoard.PurchaseRequested -= OnPurchaseRequested;
        marketBoard.ItemPurchased -= OnItemPurchased;
    }
}
