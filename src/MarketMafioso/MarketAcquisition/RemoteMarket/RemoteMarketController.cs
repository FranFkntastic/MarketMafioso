using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Network.Structures;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using MarketMafioso.Automation.MarketBoard;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace MarketMafioso.MarketAcquisition.RemoteMarket;

internal sealed class RemoteMarketController : IDisposable
{
    private static readonly TimeSpan PurchaseDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BatchPacingDelay = TimeSpan.FromMilliseconds(1600);

    private static readonly ConditionFlag[] PurchaseBlockingConditions =
    [
        ConditionFlag.Emoting,
        ConditionFlag.Mounted,
        ConditionFlag.Crafting,
        ConditionFlag.Gathering,
        ConditionFlag.PlayingMiniGame,
        ConditionFlag.Occupied,
        ConditionFlag.InCombat,
        ConditionFlag.Occupied30,
        ConditionFlag.OccupiedInEvent,
        ConditionFlag.OccupiedInQuestEvent,
    ];

    private readonly Configuration configuration;
    private readonly IMarketBoard marketBoard;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly IChatGui chatGui;
    private readonly INotificationManager notificationManager;
    private readonly IPluginLog log;
    private readonly Func<uint, string?> resolveItemName;
    private readonly Func<uint, string?, MarketBoardItemSearchResult> searchDriver;
    private readonly IMarketBoardBrowseRuntime browseRuntime;
    private readonly CmbMarketContextClient cmbContext;
    private readonly string evidenceDirectory;
    private readonly Dalamud.Plugin.Ipc.ICallGateProvider<uint, uint?, bool> openRemoteMarketProvider;
    private readonly Dalamud.Plugin.Ipc.ICallGateProvider<bool> remoteMarketAvailableProvider;
    private readonly RemoteMarketNativePurchaseGuard nativePurchaseGuard;

    private readonly List<RemoteMarketBatchItem> batchItems = [];
    private RemoteMarketPurchaseAttempt? attempt;
    private string? lastOutcome;
    private readonly HashSet<ulong> purchasedListingIds = [];
    private readonly Dictionary<uint, Dictionary<ulong, string>> retainerNamesByItem = [];
    private uint? pendingSelectionMaxPrice;
    private DateTimeOffset pendingSelectionExpiresAtUtc;
    private bool blockedNativePurchaseRecoveryScheduled;
    private string? trackedBrowseOperationId;
    private uint trackedBrowseItemId;
    private string? trackedBrowseItemName;
    private bool trackedBrowseTerminalReported;
    private bool trackedBrowseSearchActive;
    private DateTimeOffset nextTrackedBrowsePollUtc;

    public RemoteMarketController(
        Configuration configuration,
        IMarketBoard marketBoard,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        IGameGui gameGui,
        ICondition condition,
        IChatGui chatGui,
        INotificationManager notificationManager,
        IGameInteropProvider interopProvider,
        IAddonLifecycle addonLifecycle,
        IPluginLog log,
        Func<uint, string?> resolveItemName,
        Func<uint, string?, MarketBoardItemSearchResult> searchDriver,
        IMarketBoardBrowseRuntime browseRuntime,
        Dalamud.Plugin.IDalamudPluginInterface pluginInterface,
        string pluginConfigDirectory)
    {
        this.configuration = configuration;
        this.marketBoard = marketBoard;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
        this.gameGui = gameGui;
        this.condition = condition;
        this.chatGui = chatGui;
        this.notificationManager = notificationManager;
        this.log = log;
        this.resolveItemName = resolveItemName;
        this.searchDriver = searchDriver;
        this.browseRuntime = browseRuntime ?? throw new ArgumentNullException(nameof(browseRuntime));
        cmbContext = new CmbMarketContextClient(pluginInterface, log);
        evidenceDirectory = Path.Combine(pluginConfigDirectory, "remote-market");
        nativePurchaseGuard = new RemoteMarketNativePurchaseGuard(
            interopProvider,
            addonLifecycle,
            log,
            ScheduleBlockedNativePurchaseRecovery);
        marketBoard.PurchaseRequested += OnPurchaseRequested;
        marketBoard.ItemPurchased += OnItemPurchased;
        marketBoard.OfferingsReceived += OnOfferingsReceived;
        framework.Update += OnFrameworkUpdate;
        openRemoteMarketProvider = pluginInterface.GetIpcProvider<uint, uint?, bool>("MarketMafioso.OpenRemoteMarket");
        openRemoteMarketProvider.RegisterFunc(OpenRemoteMarketIpc);
        remoteMarketAvailableProvider = pluginInterface.GetIpcProvider<bool>("MarketMafioso.IsRemoteMarketAvailable");
        remoteMarketAvailableProvider.RegisterFunc(() => IsAvailable && clientState.IsLoggedIn);
    }

    public bool IsAvailable =>
        MarketAcquisitionUnlock.IsUnlocked(configuration) &&
        configuration.EnableRemoteMarketPurchase;

    public void Dispose()
    {
        CloseOwnedRemoteMarketAgent();
        marketBoard.PurchaseRequested -= OnPurchaseRequested;
        marketBoard.ItemPurchased -= OnItemPurchased;
        marketBoard.OfferingsReceived -= OnOfferingsReceived;
        framework.Update -= OnFrameworkUpdate;
        nativePurchaseGuard.Dispose();
        openRemoteMarketProvider.UnregisterFunc();
        remoteMarketAvailableProvider.UnregisterFunc();
    }

    public uint? ConsumePendingSelectionMaxPrice()
    {
        if (pendingSelectionMaxPrice is null || DateTimeOffset.UtcNow > pendingSelectionExpiresAtUtc)
        {
            pendingSelectionMaxPrice = null;
            return null;
        }
        var value = pendingSelectionMaxPrice;
        pendingSelectionMaxPrice = null;
        return value;
    }

    private bool OpenRemoteMarketIpc(uint itemId, uint? maxUnitPrice)
    {
        if (!IsAvailable || itemId == 0 || !clientState.IsLoggedIn)
            return false;
        OpenMarketBoard();
        var result = SearchItem(itemId, resolveItemName(itemId));
        if (!result.IsInProgress && !result.ReadyForListings)
            return false;
        pendingSelectionMaxPrice = maxUnitPrice ?? 0;
        pendingSelectionExpiresAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        log.Information(
            "[MarketMafioso] Remote market opened via IPC for item {ItemId} with max unit price {MaxUnitPrice}.",
            itemId,
            maxUnitPrice?.ToString() ?? "(none)");
        return true;
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        var browse = browseRuntime.Snapshot;
        if (!browse.RequestAccepted ||
            browse.IsFailed ||
            browse.ItemId == 0 ||
            browse.RequestId is not { } requestId ||
            requestId != unchecked((byte)offerings.RequestId) ||
            offerings.ItemListings.Any(listing => listing.ItemId != browse.ItemId))
        {
            return;
        }

        var itemId = offerings.ItemListings.Count > 0 ? offerings.ItemListings[0].ItemId : 0u;
        if (itemId == 0)
            return;
        if (!retainerNamesByItem.TryGetValue(itemId, out var byListing))
        {
            byListing = [];
            retainerNamesByItem[itemId] = byListing;
        }
        foreach (var listing in offerings.ItemListings)
            byListing[listing.ListingId] = listing.RetainerName;
    }

    public string? GetPurchaseContextBlockReason()
    {
        if (!browseRuntime.IsAvailable)
            return browseRuntime.AvailabilityMessage;
        var browse = browseRuntime.Snapshot;
        if (!browse.IsComplete)
            return $"Remote listings are not verified: {browse.Message}";
        if (!nativePurchaseGuard.IsAvailable)
            return "The native-purchase guard is unavailable, so remote purchases are blocked.";
        foreach (var flag in PurchaseBlockingConditions)
        {
            if (condition[flag])
                return $"Cannot purchase while {flag}.";
        }
        if (configuration.RemoteMarketRejectedTerritories.Contains(clientState.TerritoryType))
            return "Purchases have been rejected in this area before.";
        return null;
    }

    public void ClearRejectedTerritories()
    {
        configuration.RemoteMarketRejectedTerritories.Clear();
        configuration.Save();
    }

    public void SetDebugOutcome(string message) => lastOutcome = message;

    public MarketBoardItemSearchResult SearchItem(uint itemId, string? itemName)
    {
        trackedBrowseItemId = itemId;
        trackedBrowseItemName = itemName;
        trackedBrowseSearchActive = true;
        nextTrackedBrowsePollUtc = DateTimeOffset.UtcNow;
        return AdvanceTrackedBrowseSearch();
    }

    private MarketBoardItemSearchResult AdvanceTrackedBrowseSearch()
    {
        var result = searchDriver(trackedBrowseItemId, trackedBrowseItemName);
        nextTrackedBrowsePollUtc = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(500);
        if (result.BrowseEvidence is { } browse &&
            !string.IsNullOrWhiteSpace(browse.OperationId))
        {
            trackedBrowseOperationId = browse.OperationId;
            trackedBrowseTerminalReported = false;
        }

        if (!result.IsInProgress && !result.ReadyForListings)
        {
            lastOutcome = result.Message;
            trackedBrowseSearchActive = false;
        }
        else if (result.ReadyForListings)
        {
            trackedBrowseSearchActive = false;
        }

        return result;
    }

    public unsafe string RunNativePurchaseGuardSelfTest()
    {
        if (!IsAvailable || !nativePurchaseGuard.IsAvailable)
            return "Native-purchase guard is unavailable.";
        if (!nativePurchaseGuard.IsRemoteSessionActive)
            return "Open the board through MMF before testing the native-purchase guard.";

        var proxy = GetItemSearchProxy();
        if (proxy == null)
            return "ItemSearch proxy is unavailable.";
        if (proxy->LastPurchasedMarketboardItem.ListingId != 0)
            return "Guard test refused: a real listing is currently staged.";

        var blockedBefore = nativePurchaseGuard.BlockedNativePurchaseCount;
        var sent = proxy->SendPurchaseRequestPacket();
        var blockedAfter = nativePurchaseGuard.BlockedNativePurchaseCount;
        if (sent || blockedAfter <= blockedBefore)
            return "Guard test failed: the normal client entry point was not intercepted.";

        const string result = "Guard test passed: the normal client entry point was blocked with no listing staged.";
        log.Information("[MarketMafioso] {Result}", result);
        return result;
    }

    public unsafe string CloseMarketBoardForTesting()
    {
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.ItemSearch);
        if (agent == null || !agent->IsAgentActive())
            return "Market board is already closed.";

        agent->Hide();
        AbandonTrackedBrowse("Remote market board closed during testing.");
        nativePurchaseGuard.ObserveMarketAgentActive(false);
        ClearStagedPurchase();
        DismissLingeringConfirmationDialogs();
        return "Market board closed and remote ownership released.";
    }

    public string OpenMarketBoard()
    {
        if (!IsAvailable)
            return "Remote market is locked.";
        if (!clientState.IsLoggedIn)
            return "Log in first.";
        unsafe
        {
            var agentModule = AgentModule.Instance();
            var agent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.ItemSearch);
            if (agent == null)
                return "ItemSearch agent is unavailable.";
            var wasActive = agent->IsAgentActive();
            agent->Show();
            var opened = agent->IsAgentActive();
            nativePurchaseGuard.ObserveRemoteOpen(wasActive, opened);
            log.Information("[MarketMafioso] Remote market board opened. Territory={Territory} AgentActive={AgentActive}", clientState.TerritoryType, opened);
            return opened ? "Market board opened." : "Market board was shown but did not activate.";
        }
    }

    public RemoteMarketView GetView()
    {
        var listings = new List<RemoteMarketListingView>();
        var browse = browseRuntime.Snapshot;
        unsafe
        {
            var proxy = GetItemSearchProxy();
            if (proxy != null &&
                browse.IsComplete &&
                browse.ItemId != 0 &&
                proxy->SearchItemId == browse.ItemId &&
                proxy->ListingCount == browse.ExpectedListingCount &&
                (browse.RequestId is null ||
                 proxy->InfoProxyPageInterface.CurrentRequestId == browse.RequestId))
            {
                var count = Math.Min(browse.ExpectedListingCount, 50);
                for (var index = 0; index < count; index++)
                {
                    var listing = proxy->Listings[index];
                    if (listing.ItemId != browse.ItemId ||
                        listing.ListingId == 0 ||
                        listing.RetainerId == 0 ||
                        listing.UnitPrice == 0 ||
                        listing.Quantity == 0)
                    {
                        listings.Clear();
                        break;
                    }

                    listings.Add(new RemoteMarketListingView(
                        listing.ListingId,
                        listing.ItemId,
                        resolveItemName(listing.ItemId) ?? $"Item {listing.ItemId}",
                        listing.IsHqItem,
                        listing.Quantity,
                        listing.UnitPrice,
                        listing.TotalTax,
                        (listing.UnitPrice * (ulong)listing.Quantity) + listing.TotalTax,
                        listing.MateriaCount,
                        retainerNamesByItem.TryGetValue(listing.ItemId, out var byListing) && byListing.TryGetValue(listing.ListingId, out var retainerName)
                            ? retainerName
                            : string.Empty,
                        purchasedListingIds.Contains(listing.ListingId),
                        batchItems.FirstOrDefault(item => item.ListingId == listing.ListingId)?.Status));
                }
            }
        }

        var batch = batchItems.Count == 0
            ? null
            : new RemoteMarketBatchView(
                batchItems.Count,
                batchItems.Count(item => item.Status is RemoteMarketBatchItemStatus.Confirmed or RemoteMarketBatchItemStatus.Failed or RemoteMarketBatchItemStatus.Skipped),
                batchItems.Count(item => item.Status == RemoteMarketBatchItemStatus.Failed),
                attempt is not null);

        CmbMarketContext? context = null;
        if (listings.Count > 0)
            context = cmbContext.Get(listings[0].ItemId, listings[0].IsHighQuality);

        return new RemoteMarketView(
            IsAvailable && browse.IsComplete,
            listings,
            batch,
            lastOutcome,
            GetPurchaseContextBlockReason(),
            GetCurrentGil(),
            context,
            browse.Message);
    }

    public string? BeginBatch(IReadOnlyCollection<ulong> selectedListingIds)
    {
        if (!IsAvailable)
            return "Remote market is locked.";
        if (batchItems.Count > 0)
            return "A purchase batch is already in progress.";
        if (GetPurchaseContextBlockReason() is { } contextBlock)
            return contextBlock;

        var staged = new List<RemoteMarketSelectionView>();
        unsafe
        {
            var proxy = GetItemSearchProxy();
            if (proxy == null)
                return "ItemSearch proxy is unavailable.";
            foreach (var listingId in selectedListingIds)
            {
                var listing = FindListing(proxy, listingId);
                if (listing is null)
                    return "A selected listing is no longer in the results. Re-search to refresh.";
                if (purchasedListingIds.Contains(listingId))
                    return "One of the selected listings was already purchased. Re-search to refresh.";
                staged.Add(ToSelection(listing.Value, resolveItemName(listing.Value.ItemId) ?? $"Item {listing.Value.ItemId}"));
            }
        }

        if (staged.Count == 0)
            return "Select at least one listing.";

        foreach (var selection in staged)
            batchItems.Add(new RemoteMarketBatchItem(selection.ListingId, selection, RemoteMarketBatchItemStatus.Queued));
        AdvanceBatch();
        return null;
    }

    public void CancelBatch()
    {
        foreach (var item in batchItems.Where(item => item.Status == RemoteMarketBatchItemStatus.Queued))
            item.Status = RemoteMarketBatchItemStatus.Skipped;
        if (attempt is null)
            FinishBatch("Batch cancelled.");
    }

    private void AdvanceBatch()
    {
        if (attempt is not null)
            return;
        var next = batchItems.FirstOrDefault(item => item.Status == RemoteMarketBatchItemStatus.Queued);
        if (next is null)
        {
            FinishBatch(null);
            return;
        }
        if (GetPurchaseContextBlockReason() is { } contextBlock)
        {
            foreach (var item in batchItems.Where(item => item.Status == RemoteMarketBatchItemStatus.Queued))
                item.Status = RemoteMarketBatchItemStatus.Skipped;
            FinishBatch(contextBlock);
            return;
        }

        next.Status = RemoteMarketBatchItemStatus.Sending;
        var pending = new RemoteMarketPurchaseAttempt(
            next.Selection,
            clientState.TerritoryType,
            objectTable.LocalPlayer?.Position.ToString() ?? "unavailable",
            DateTimeOffset.UtcNow);
        attempt = pending;

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
                var listingPointer = (MarketBoardListing*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref staged);
                if (!proxy->SetLastPurchasedItem(listingPointer))
                    failure = "The client refused to stage the listing.";
                else if (!nativePurchaseGuard.SendOwned(proxy))
                    failure = "The client refused to send the purchase request.";
            }
        }

        if (failure is not null)
        {
            next.Status = RemoteMarketBatchItemStatus.Failed;
            Fail(pending, failure);
            return;
        }

        pending.Phase = RemoteMarketPurchasePhase.Sent;
        pending.SentAtUtc = DateTimeOffset.UtcNow;
        pending.DeadlineAtUtc = pending.SentAtUtc + PurchaseDeadline;
        pending.GilBeforeSend = GetCurrentGil();
        log.Information(
            "[MarketMafioso] Remote market purchase sent. ListingId={ListingId} ItemId={ItemId} Quantity={Quantity} TotalGil={TotalGil}",
            pending.Selection.ListingId,
            pending.Selection.ItemId,
            pending.Selection.Quantity,
            pending.Selection.TotalGil);
        framework.RunOnTick(() =>
        {
            if (ReferenceEquals(attempt, pending) && pending.Phase == RemoteMarketPurchasePhase.Sent)
                ResolveIndeterminate(pending);
        }, PurchaseDeadline + TimeSpan.FromSeconds(1));
        DismissLingeringConfirmationDialogsSoon();
    }

    private void FinishBatch(string? abortReason)
    {
        if (batchItems.Count == 0)
            return;
        var confirmed = batchItems.Count(item => item.Status == RemoteMarketBatchItemStatus.Confirmed);
        var failed = batchItems.Count(item => item.Status == RemoteMarketBatchItemStatus.Failed);
        var skipped = batchItems.Count(item => item.Status == RemoteMarketBatchItemStatus.Skipped);
        lastOutcome = abortReason is not null
            ? $"Batch aborted: {abortReason} ({confirmed} confirmed, {failed} failed, {skipped} skipped)"
            : $"Batch complete: {confirmed} confirmed, {failed} failed, {skipped} skipped.";
        log.Information("[MarketMafioso] Remote market {Outcome}", lastOutcome);
        chatGui.Print($"[MMF] Remote market: {lastOutcome}");
        batchItems.Clear();
    }

    private void OnPurchaseRequested(IMarketBoardPurchaseHandler purchase)
    {
        if (attempt is not { Phase: RemoteMarketPurchasePhase.Sent } pending)
        {
            log.Information(
                "[MarketMafioso] Native-path purchase request observed outside a staged remote attempt. ListingId={ListingId} CatalogId={CatalogId} Quantity={Quantity} PricePerUnit={PricePerUnit}",
                purchase.ListingId,
                purchase.CatalogId,
                purchase.ItemQuantity,
                purchase.PricePerUnit);
            return;
        }
        pending.PacketObserved = true;
        pending.PacketMatchesIntent = RemoteMarketPurchaseMatcher.PacketMatchesIntent(
            pending.Selection.ListingId,
            pending.Selection.ItemId,
            pending.Selection.Quantity,
            pending.Selection.UnitPrice,
            purchase.ListingId,
            purchase.CatalogId,
            purchase.ItemQuantity,
            purchase.PricePerUnit);
        if (!pending.PacketMatchesIntent)
        {
            pending.Phase = RemoteMarketPurchasePhase.Conflicted;
            MarkActiveItem(RemoteMarketBatchItemStatus.Failed);
            Complete(pending, "The observed purchase packet did not match the staged listing.");
        }
    }

    private void OnItemPurchased(IMarketBoardPurchase purchase)
    {
        if (attempt is not { Phase: RemoteMarketPurchasePhase.Sent } pending)
            return;
        if (!RemoteMarketPurchaseMatcher.ConfirmationMatchesIntent(
                pending.Selection.ItemId,
                pending.Selection.Quantity,
                purchase.CatalogId,
                purchase.ItemQuantity))
            return;
        pending.GilAfterResponse = GetCurrentGil();
        if (pending.GilBeforeSend is { } before && pending.GilAfterResponse is { } after)
        {
            var delta = (long)before - after;
            if (delta == (long)pending.TotalGil)
            {
                purchasedListingIds.Add(pending.Selection.ListingId);
                pending.Phase = RemoteMarketPurchasePhase.Confirmed;
                MarkActiveItem(RemoteMarketBatchItemStatus.Confirmed);
                Complete(pending, $"Purchased {pending.Quantity}x {pending.ItemName} for {pending.TotalGil:N0} gil.");
                return;
            }
            if (delta == 0)
            {
                NoteRejectedTerritory();
                pending.Phase = RemoteMarketPurchasePhase.Failed;
                MarkActiveItem(RemoteMarketBatchItemStatus.Failed);
                Complete(pending, "The server rejected the purchase. No gil moved.");
                return;
            }
            pending.Phase = RemoteMarketPurchasePhase.Indeterminate;
            MarkActiveItem(RemoteMarketBatchItemStatus.Failed);
            Complete(pending, $"A purchase response arrived but gil moved by {delta:N0} instead of {pending.TotalGil:N0}. Reconcile before retrying.");
            return;
        }
        pending.Phase = RemoteMarketPurchasePhase.Indeterminate;
        MarkActiveItem(RemoteMarketBatchItemStatus.Failed);
        Complete(pending, "A purchase response arrived but gil state was unavailable. Reconcile before retrying.");
    }

    private void MarkActiveItem(RemoteMarketBatchItemStatus status)
    {
        var active = batchItems.FirstOrDefault(item => item.Status == RemoteMarketBatchItemStatus.Sending);
        if (active is not null)
            active.Status = status;
    }

    private void ResolveIndeterminate(RemoteMarketPurchaseAttempt pending)
    {
        pending.Phase = RemoteMarketPurchasePhase.Indeterminate;
        MarkActiveItem(RemoteMarketBatchItemStatus.Failed);
        Complete(pending, "No purchase confirmation arrived before the deadline. The server silently drops invalid requests, so re-search the item, then reconcile inventory and gil before retrying.");
    }

    private void Fail(RemoteMarketPurchaseAttempt pending, string reason)
    {
        pending.Phase = RemoteMarketPurchasePhase.Failed;
        Complete(pending, reason);
    }

    private void Complete(RemoteMarketPurchaseAttempt pending, string message)
    {
        if (pending.SentAtUtc is not null)
            ClearStagedPurchase();
        pending.FailureReason = message;
        attempt = null;
        log.Information(
            "[MarketMafioso] Remote market purchase {Phase}. ListingId={ListingId} PacketObserved={PacketObserved} PacketMatchesIntent={PacketMatchesIntent} Message={Message}",
            pending.Phase,
            pending.Selection.ListingId,
            pending.PacketObserved,
            pending.PacketMatchesIntent,
            message);
        WriteEvidence(pending);
        framework.RunOnTick(AdvanceBatch, BatchPacingDelay);
    }

    private void NoteRejectedTerritory()
    {
        var territory = clientState.TerritoryType;
        if (configuration.RemoteMarketRejectedTerritories.Contains(territory))
            return;
        configuration.RemoteMarketRejectedTerritories.Add(territory);
        configuration.Save();
        log.Information("[MarketMafioso] Remote market recorded territory {Territory} as purchase-rejecting.", territory);
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

    private void WriteEvidence(RemoteMarketPurchaseAttempt pending)
    {
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            var path = Path.Combine(
                evidenceDirectory,
                $"remote-purchase-{pending.StagedAtUtc:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(pending.ToEvidence(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[MarketMafioso] Remote market purchase evidence could not be written.");
        }
    }

    private void DismissLingeringConfirmationDialogsSoon() =>
        framework.RunOnTick(DismissLingeringConfirmationDialogs, TimeSpan.FromMilliseconds(500));

    private void OnFrameworkUpdate(IFramework _)
    {
        if (trackedBrowseSearchActive && DateTimeOffset.UtcNow >= nextTrackedBrowsePollUtc)
            AdvanceTrackedBrowseSearch();
        ObserveTrackedBrowse();
        unsafe
        {
            var agentModule = AgentModule.Instance();
            var agent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.ItemSearch);
            nativePurchaseGuard.ObserveMarketAgentActive(agent != null && agent->IsAgentActive());
        }
    }

    private void ObserveTrackedBrowse()
    {
        if (trackedBrowseTerminalReported ||
            string.IsNullOrWhiteSpace(trackedBrowseOperationId))
        {
            return;
        }

        var browse = browseRuntime.Snapshot;
        if (!string.Equals(browse.OperationId, trackedBrowseOperationId, StringComparison.Ordinal) ||
            browse.ItemId != trackedBrowseItemId ||
            !browse.IsTerminal)
        {
            return;
        }

        trackedBrowseTerminalReported = true;
        lastOutcome = browse.Message;
        if (browse.IsFailed)
        {
            log.Warning(
                "[MarketMafioso] Remote market browse failed closed. OperationId={OperationId} Code={Code} Message={Message}",
                browse.OperationId,
                browse.FailureCode ?? "Unknown",
                browse.Message);
        }
        else
        {
            log.Information(
                "[MarketMafioso] Remote market browse completed. OperationId={OperationId} ItemId={ItemId} Listings={Listings} Pages={Pages}",
                browse.OperationId,
                browse.ItemId,
                browse.ExpectedListingCount,
                browse.PageCount);
        }
    }

    private unsafe void CloseOwnedRemoteMarketAgent()
    {
        AbandonTrackedBrowse("Remote market controller disposed or closed its owned agent.");
        if (!nativePurchaseGuard.IsRemoteSessionActive)
            return;

        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.ItemSearch);
        if (agent != null && agent->IsAgentActive())
        {
            log.Information("[MarketMafioso] Closing MMF-owned remote market agent during plugin disposal.");
            agent->Hide();
        }

        nativePurchaseGuard.ObserveMarketAgentActive(false);
        ClearStagedPurchase();
    }

    private void AbandonTrackedBrowse(string reason)
    {
        var browse = browseRuntime.Snapshot;
        if (browse.IsActive &&
            browse.Owner == MarketBoardBrowseOwner.RemoteMarketController &&
            !string.IsNullOrWhiteSpace(browse.OperationId))
        {
            browseRuntime.TryAbandon(
                MarketBoardBrowseOwner.RemoteMarketController,
                browse.OperationId,
                reason,
                out _);
        }

        trackedBrowseSearchActive = false;
    }

    private void ScheduleBlockedNativePurchaseRecovery()
    {
        if (blockedNativePurchaseRecoveryScheduled)
            return;

        blockedNativePurchaseRecoveryScheduled = true;
        framework.RunOnTick(() =>
        {
            blockedNativePurchaseRecoveryScheduled = false;
            ClearStagedPurchase();
            DismissLingeringConfirmationDialogs();
            lastOutcome = "Native purchase blocked locally; no request was sent and this area was not marked as rejecting purchases.";
            notificationManager.AddNotification(new Notification
            {
                Title = "MMF Remote Market",
                Content = "Native market-board purchases aren't available remotely. Select the listing in MMF and confirm it there.",
                Type = NotificationType.Warning,
                InitialDuration = TimeSpan.FromSeconds(8),
                Minimized = false,
            });
        });
    }

    private void DismissLingeringConfirmationDialogs()
    {
        string[] dialogAddons = ["SelectYesno", "SelectOk"];
        unsafe
        {
            foreach (var addonName in dialogAddons)
            {
                var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)gameGui.GetAddonByName(addonName, 1).Address;
                if (addon == null || !addon->IsVisible)
                    continue;
                log.Information("[MarketMafioso] Remote market dismissing lingering dialog addon {AddonName}.", addonName);
                addon->Close(false);
            }
        }
    }

    private static RemoteMarketSelectionView ToSelection(MarketBoardListing listing, string itemName) => new(
        (int)listing.ListingId,
        listing.ItemId,
        itemName,
        listing.IsHqItem,
        listing.Quantity,
        listing.UnitPrice,
        listing.TotalTax,
        (listing.UnitPrice * (ulong)listing.Quantity) + listing.TotalTax,
        listing.ListingId,
        listing.RetainerId);

    private static unsafe MarketBoardListing? FindListing(InfoProxyItemSearch* proxy, ulong listingId)
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

    private unsafe InfoProxyItemSearch* GetItemSearchProxy()
    {
        var infoModule = InfoModule.Instance();
        return infoModule == null ? null : (InfoProxyItemSearch*)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch);
    }

    public unsafe bool IsMarketBoardResultVisible()
    {
        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)gameGui.GetAddonByName("ItemSearchResult", 1).Address;
        return addon != null && addon->IsVisible;
    }

    public unsafe bool TryGetResultAnchor(out System.Numerics.Vector2 anchor)
    {
        anchor = default;
        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)gameGui.GetAddonByName("ItemSearchResult", 1).Address;
        if (addon == null || !addon->IsVisible)
            return false;
        anchor = new System.Numerics.Vector2(addon->X + addon->GetScaledWidth(true) + 8f, addon->Y + 4f);
        return true;
    }

    public unsafe bool TryGetResultBounds(out System.Numerics.Vector2 anchor, out float maxHeight)
    {
        anchor = default;
        maxHeight = 0f;
        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)gameGui.GetAddonByName("ItemSearchResult", 1).Address;
        if (addon == null || !addon->IsVisible)
            return false;
        anchor = new System.Numerics.Vector2(addon->X + addon->GetScaledWidth(true) + 8f, addon->Y + 4f);
        maxHeight = Math.Max(0f, addon->GetScaledHeight(true) - 8f);
        return true;
    }

    private static unsafe uint? GetCurrentGil()
    {
        var inventoryManager = InventoryManager.Instance();
        return inventoryManager == null ? null : inventoryManager->GetGil();
    }
}

internal enum RemoteMarketPurchasePhase
{
    AwaitingConfirmation,
    Sent,
    Confirmed,
    Failed,
    Cancelled,
    Conflicted,
    Indeterminate,
}

internal enum RemoteMarketBatchItemStatus
{
    Queued,
    Sending,
    Confirmed,
    Failed,
    Skipped,
}

internal sealed class RemoteMarketBatchItem(ulong listingId, RemoteMarketSelectionView selection, RemoteMarketBatchItemStatus status)
{
    public ulong ListingId { get; } = listingId;
    public RemoteMarketSelectionView Selection { get; } = selection;
    public RemoteMarketBatchItemStatus Status { get; set; } = status;
}

internal sealed record RemoteMarketSelectionView(
    int SelectedIndex,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    uint Quantity,
    uint UnitPrice,
    uint TotalTax,
    ulong TotalGil,
    ulong ListingId,
    ulong RetainerId);

internal sealed record RemoteMarketListingView(
    ulong ListingId,
    uint ItemId,
    string ItemName,
    bool IsHighQuality,
    uint Quantity,
    uint UnitPrice,
    uint TotalTax,
    ulong TotalGil,
    byte MateriaCount,
    string RetainerName,
    bool AlreadyPurchased,
    RemoteMarketBatchItemStatus? BatchStatus);

internal sealed record RemoteMarketBatchView(
    int TotalCount,
    int CompletedCount,
    int FailedCount,
    bool Active);

internal sealed record RemoteMarketView(
    bool Available,
    IReadOnlyList<RemoteMarketListingView> Listings,
    RemoteMarketBatchView? Batch,
    string? LastOutcome,
    string? ContextBlockReason,
    uint? GilOnHand,
    CmbMarketContext? MarketContext,
    string BrowseMessage);

internal sealed class RemoteMarketPurchaseAttempt(
    RemoteMarketSelectionView selection,
    uint territory,
    string position,
    DateTimeOffset stagedAtUtc)
{
    public RemoteMarketSelectionView Selection { get; } = selection;
    public uint Territory { get; } = territory;
    public string Position { get; } = position;
    public DateTimeOffset StagedAtUtc { get; } = stagedAtUtc;
    public DateTimeOffset? SentAtUtc { get; set; }
    public DateTimeOffset? DeadlineAtUtc { get; set; }
    public RemoteMarketPurchasePhase Phase { get; set; } = RemoteMarketPurchasePhase.AwaitingConfirmation;
    public bool PacketObserved { get; set; }
    public bool PacketMatchesIntent { get; set; }
    public uint? GilBeforeSend { get; set; }
    public uint? GilAfterResponse { get; set; }
    public string? FailureReason { get; set; }
    public string ItemName => Selection.ItemName;
    public bool IsHighQuality => Selection.IsHighQuality;
    public uint Quantity => Selection.Quantity;
    public ulong TotalGil => Selection.TotalGil;

    public object ToEvidence() => new
    {
        StagedAtUtc,
        SentAtUtc,
        DeadlineAtUtc,
        Phase = Phase.ToString(),
        PacketObserved,
        PacketMatchesIntent,
        GilBeforeSend,
        GilAfterResponse,
        Territory,
        Position,
        Selection.SelectedIndex,
        Selection.ItemId,
        Selection.ItemName,
        Selection.IsHighQuality,
        Selection.Quantity,
        Selection.UnitPrice,
        Selection.TotalTax,
        Selection.TotalGil,
        Selection.ListingId,
        Selection.RetainerId,
    };
}
