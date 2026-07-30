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
    private const string ItemSearchResultAddon = "ItemSearchResult";
    private static readonly TimeSpan PurchaseDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PurchaseVerificationDeadline = TimeSpan.FromSeconds(15);
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
    private readonly RemoteMarketNativeListingCache nativeListingCache;

    private readonly List<RemoteMarketBatchItem> batchItems = [];
    private RemoteMarketPurchaseAttempt? attempt;
    private RemoteMarketPendingPurchaseVerification? pendingPurchaseVerification;
    private ulong[] pendingSelectionRestoreIds = [];
    private string? lastOutcome;
    private readonly HashSet<ulong> purchasedListingIds = [];
    private RemoteMarketListingView[] listingSnapshot = [];
    private RemoteMarketListingSnapshotIdentity? listingSnapshotIdentity;
    private CmbMarketContext? marketContext;
    private long viewRevision;
    private RemoteMarketView cachedView = new(
        0,
        false,
        Array.Empty<RemoteMarketListingView>(),
        0,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        string.Empty);
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
        nativeListingCache = new RemoteMarketNativeListingCache(
            marketBoard,
            addonLifecycle,
            framework,
            gameGui,
            resolveItemName);
        nativeListingCache.SnapshotChanged += OnNativeListingSnapshotChanged;
        marketBoard.PurchaseRequested += OnPurchaseRequested;
        marketBoard.ItemPurchased += OnItemPurchased;
        framework.Update += OnFrameworkUpdate;
        cmbContext.ContextChanged += OnMarketContextChanged;
        condition.ConditionChange += OnConditionChange;
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
        framework.Update -= OnFrameworkUpdate;
        nativeListingCache.SnapshotChanged -= OnNativeListingSnapshotChanged;
        nativeListingCache.Dispose();
        nativePurchaseGuard.Dispose();
        cmbContext.ContextChanged -= OnMarketContextChanged;
        cmbContext.Dispose();
        condition.ConditionChange -= OnConditionChange;
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

    public IReadOnlyList<ulong> ConsumePendingSelectionRestoreIds()
    {
        var value = pendingSelectionRestoreIds;
        pendingSelectionRestoreIds = [];
        return value;
    }

    private bool OpenRemoteMarketIpc(uint itemId, uint? maxUnitPrice)
    {
        if (!IsAvailable || itemId == 0 || !clientState.IsLoggedIn)
            return false;
        if (!HasReusableVisibleBrowse(itemId))
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

    public string OpenMarketItem(uint itemId)
    {
        if (!IsAvailable || itemId == 0 || !clientState.IsLoggedIn)
            return "The listing search could not be opened.";

        var itemName = resolveItemName(itemId);
        if (string.IsNullOrWhiteSpace(itemName))
            return "The requested item name could not be resolved.";

        if (!HasReusableVisibleBrowse(itemId))
            OpenMarketBoard();
        var result = SearchItem(itemId, itemName);
        return result.IsInProgress || result.ReadyForListings
            ? $"Listing search queued for {itemName}."
            : result.Message;
    }

    private unsafe bool HasReusableVisibleBrowse(uint itemId)
    {
        var itemSearchResult = gameGui.GetAddonByName<AddonItemSearchResult>("ItemSearchResult", 1);
        var resultVisible =
            itemSearchResult != null &&
            itemSearchResult->AtkUnitBase.IsReady &&
            itemSearchResult->AtkUnitBase.IsVisible;
        var infoProxy = resultVisible ? InfoProxyItemSearch.Instance() : null;
        var openResultItemId = infoProxy == null ? 0 : infoProxy->SearchItemId;
        return MarketBoardItemSearchDriver.ShouldReuseOwnedTerminalResult(
            browseRuntime.Snapshot,
            MarketBoardBrowseOwner.RemoteMarketController,
            itemId,
            resultVisible,
            openResultItemId);
    }

    private void OnNativeListingSnapshotChanged(RemoteMarketNativeListingSnapshot snapshot) =>
        ApplyNativeListingSnapshot(snapshot);

    private void ApplyNativeListingSnapshot(RemoteMarketNativeListingSnapshot snapshot)
    {
        if (snapshot.Identity is not { } nativeIdentity)
            return;

        var browse = browseRuntime.Snapshot;
        var previousContextItemId = listingSnapshot.Length == 0 ? 0 : listingSnapshot[0].ItemId;
        var previousContextHighQuality = listingSnapshot.Length != 0 && listingSnapshot[0].IsHighQuality;
        listingSnapshot = snapshot.Listings.ToArray();
        listingSnapshotIdentity = new RemoteMarketListingSnapshotIdentity(
            nativeIdentity.ItemId,
            nativeIdentity.RequestId,
            nativeIdentity.ListingCount,
            listingSnapshot.Length,
            GetVerifiedBrowseOperationId(
                browse,
                nativeIdentity.ItemId,
                nativeIdentity.RequestId,
                nativeIdentity.ListingCount,
                listingSnapshot.Length));
        if (listingSnapshot.Length == 0)
        {
            marketContext = null;
        }
        else if (previousContextItemId != listingSnapshot[0].ItemId ||
                 previousContextHighQuality != listingSnapshot[0].IsHighQuality)
        {
            marketContext = cmbContext.Request(listingSnapshot[0].ItemId, listingSnapshot[0].IsHighQuality);
        }

        if (!TryCompletePendingPurchaseVerification())
            RebuildView();
    }

    private void OnMarketContextChanged(uint itemId, bool highQuality, CmbMarketContext? context) =>
        framework.RunOnTick(() =>
        {
            if (listingSnapshot.Length == 0 ||
                listingSnapshot[0].ItemId != itemId ||
                listingSnapshot[0].IsHighQuality != highQuality)
                return;

            marketContext = context;
            RebuildView();
        });

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (PurchaseBlockingConditions.Contains(flag))
            RebuildView();
    }

    public string? GetPurchaseContextBlockReason()
    {
        if (pendingPurchaseVerification is not null)
            return "Verifying prices...";
        if (!browseRuntime.IsAvailable)
            return browseRuntime.AvailabilityMessage;
        if (listingSnapshotIdentity is { } partial &&
            partial.CapturedListingCount < partial.ListingCount)
        {
            return $"Loading listings ({partial.CapturedListingCount} of {partial.ListingCount}).";
        }

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
        RebuildView();
    }

    public void SetDebugOutcome(string message)
    {
        lastOutcome = message;
        RebuildView();
    }

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

        RebuildView();
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

    public RemoteMarketView GetView() => cachedView;

    private void RebuildView()
    {
        var browse = browseRuntime.Snapshot;
        var listings = listingSnapshot
            .Select(listing => listing with
            {
                AlreadyPurchased = purchasedListingIds.Contains(listing.ListingId),
                BatchStatus = batchItems.FirstOrDefault(item => item.ListingId == listing.ListingId)?.Status,
            })
            .ToArray();

        var batch = batchItems.Count == 0
            ? null
            : new RemoteMarketBatchView(
                batchItems.Count,
                batchItems.Count(item => item.Status is RemoteMarketBatchItemStatus.Confirmed or RemoteMarketBatchItemStatus.Failed or RemoteMarketBatchItemStatus.Skipped),
                batchItems.Count(item => item.Status == RemoteMarketBatchItemStatus.Failed),
                attempt is not null);
        var verification = pendingPurchaseVerification is null
            ? null
            : new RemoteMarketPurchaseVerificationView(
                pendingPurchaseVerification.IntendedSelections.Count,
                pendingPurchaseVerification.IntendedSelections.Sum(selection => (long)selection.Quantity),
                pendingPurchaseVerification.IntendedSelections.Aggregate(
                    0UL,
                    (sum, selection) => sum + selection.TotalGil));
        var contextBlockReason = GetPurchaseContextBlockReason();

        cachedView = new RemoteMarketView(
            ++viewRevision,
            IsAvailable && contextBlockReason is null,
            listings,
            listingSnapshotIdentity?.ListingCount ?? listings.Length,
            batch,
            verification,
            lastOutcome,
            contextBlockReason,
            GetCurrentGil(),
            marketContext,
            BuildEconomics(listings, marketContext),
            BuildMarketContextSummary(listings, marketContext),
            browse.Message);
    }

    private static RemoteMarketEconomicsView? BuildEconomics(
        IReadOnlyCollection<RemoteMarketListingView> listings,
        CmbMarketContext? context)
    {
        var buyable = listings.Where(listing => !listing.AlreadyPurchased).ToArray();
        if (buyable.Length == 0)
            return null;

        var sortedPrices = buyable.Select(listing => listing.UnitPrice).OrderBy(price => price).ToArray();
        var cheapest = sortedPrices[0];
        var median = sortedPrices[sortedPrices.Length / 2];
        var mean = buyable.Average(listing => (double)listing.UnitPrice);
        double? trendDelta = null;
        if (context?.TrendAveragePrice is { } trend && trend > 0)
            trendDelta = (cheapest - trend) / trend;
        return new RemoteMarketEconomicsView(cheapest, median, mean, trendDelta);
    }

    private static string? BuildMarketContextSummary(
        IReadOnlyCollection<RemoteMarketListingView> listings,
        CmbMarketContext? context)
    {
        if (context is null || listings.Count == 0)
            return null;

        var parts = new List<string>(3);
        if (context.DatacenterBestPrice is { } dcBest &&
            !string.IsNullOrWhiteSpace(context.DatacenterBestWorld))
        {
            var cheapestLocal = listings.Min(listing => listing.UnitPrice);
            var delta = cheapestLocal > 0 ? ((double)cheapestLocal - dcBest) / cheapestLocal : 0;
            var deltaText = delta > 0.005
                ? $" (-{delta:P0})"
                : delta < -0.005
                    ? $" (+{-delta:P0})"
                    : string.Empty;
            parts.Add($"DC best {dcBest:N0}p ({context.DatacenterBestWorld}{deltaText})");
        }
        if (context.VelocityPerDay is { } velocity)
            parts.Add($"~{velocity:0.#}/day");
        if (context.TrendAveragePrice is { } trend)
            parts.Add($"sale avg {trend:0}p");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    public string? BeginBatch(IReadOnlyCollection<ulong> selectedListingIds)
    {
        if (!IsAvailable)
            return "Remote market is locked.";
        if (pendingPurchaseVerification is not null)
            return "Prices are already being verified.";
        if (batchItems.Count > 0)
            return "A purchase batch is already in progress.";
        if (GetPurchaseContextBlockReason() is { } contextBlock)
            return contextBlock;

        if (!TryReadSelections(selectedListingIds, out var staged, out var selectionError))
            return selectionError;

        if (RequiresAutomaticPurchaseVerification(listingSnapshotIdentity, browseRuntime.Snapshot))
            return BeginPendingPurchaseVerification(staged);

        return StartBatch(staged);
    }

    private string? BeginPendingPurchaseVerification(IReadOnlyList<RemoteMarketSelectionView> selections)
    {
        var itemId = selections[0].ItemId;
        if (selections.Any(selection => selection.ItemId != itemId))
            return "A purchase batch cannot span multiple items.";

        var itemName = resolveItemName(itemId) ?? selections[0].ItemName;
        pendingPurchaseVerification = new RemoteMarketPendingPurchaseVerification(
            itemId,
            itemName,
            selections.ToArray(),
            DateTimeOffset.UtcNow + PurchaseVerificationDeadline);
        lastOutcome = null;
        RebuildView();

        var search = SearchItem(itemId, itemName);
        if (search.IsInProgress || search.ReadyForListings)
        {
            log.Information(
                "[MarketMafioso] Verifying {ListingCount} selected native listing(s) through one correlated browse for item {ItemId}.",
                selections.Count,
                itemId);
            return null;
        }

        FailPendingPurchaseVerification($"Price verification could not start: {search.Message}");
        return lastOutcome;
    }

    private bool TryCompletePendingPurchaseVerification()
    {
        if (pendingPurchaseVerification is not { } pending ||
            listingSnapshotIdentity is not { } identity ||
            identity.ItemId != pending.ItemId ||
            identity.CapturedListingCount != identity.ListingCount ||
            !IsListingSnapshotVerifiedForPurchase(identity, browseRuntime.Snapshot))
        {
            return false;
        }

        var listingIds = pending.IntendedSelections
            .Select(selection => selection.ListingId)
            .ToArray();
        if (!TryReadSelections(listingIds, out var refreshed, out var selectionError))
        {
            FailPendingPurchaseVerification(selectionError ?? "The refreshed listings could not be read.");
            return true;
        }

        var reconciliation = RemoteMarketPurchaseVerification.Reconcile(
            pending.IntendedSelections,
            refreshed);
        if (!reconciliation.Succeeded)
        {
            FailPendingPurchaseVerification(
                $"{reconciliation.FailureReason} Review the refreshed results before purchasing.");
            return true;
        }

        pendingPurchaseVerification = null;
        var startError = StartBatch(reconciliation.RefreshedSelections);
        if (startError is not null)
        {
            RestoreVerifiedSelection(listingIds);
            lastOutcome = startError;
            RebuildView();
            return true;
        }

        log.Information(
            "[MarketMafioso] Price verification preserved {ListingCount} selected listing(s); continuing the requested purchase.",
            reconciliation.RefreshedSelections.Count);
        return true;
    }

    private void FailPendingPurchaseVerification(string reason)
    {
        if (pendingPurchaseVerification is not { } pending)
            return;

        RestoreVerifiedSelection(pending.IntendedSelections.Select(selection => selection.ListingId));
        pendingPurchaseVerification = null;
        lastOutcome = reason;
        log.Warning("[MarketMafioso] Remote market price verification stopped: {Reason}", reason);
        AbandonTrackedBrowse(reason);
    }

    private void RestoreVerifiedSelection(IEnumerable<ulong> listingIds)
    {
        var visibleListingIds = listingSnapshot
            .Select(listing => listing.ListingId)
            .ToHashSet();
        pendingSelectionRestoreIds = listingIds
            .Where(visibleListingIds.Contains)
            .Distinct()
            .ToArray();
    }

    private string? StartBatch(IReadOnlyList<RemoteMarketSelectionView> staged)
    {
        if (staged.Count == 0)
            return "Select at least one listing.";
        var stagedTotal = staged.Aggregate(0UL, (sum, selection) => sum + selection.TotalGil);
        if (GetCurrentGil() is { } gilOnHand && gilOnHand < stagedTotal)
            return $"Insufficient gil for this selection ({gilOnHand:N0} on hand).";

        foreach (var selection in staged)
            batchItems.Add(new RemoteMarketBatchItem(selection.ListingId, selection, RemoteMarketBatchItemStatus.Queued));
        RebuildView();
        AdvanceBatch();
        return null;
    }

    private unsafe bool TryReadSelections(
        IEnumerable<ulong> selectedListingIds,
        out IReadOnlyList<RemoteMarketSelectionView> selections,
        out string? error)
    {
        var staged = new List<RemoteMarketSelectionView>();
        var proxy = GetItemSearchProxy();
        if (proxy == null)
        {
            selections = Array.Empty<RemoteMarketSelectionView>();
            error = "ItemSearch proxy is unavailable.";
            return false;
        }

        foreach (var listingId in selectedListingIds.Distinct())
        {
            var listing = FindListing(proxy, listingId);
            if (listing is null)
            {
                selections = Array.Empty<RemoteMarketSelectionView>();
                error = "A selected listing is no longer available.";
                return false;
            }
            if (purchasedListingIds.Contains(listingId))
            {
                selections = Array.Empty<RemoteMarketSelectionView>();
                error = "One of the selected listings was already purchased.";
                return false;
            }

            staged.Add(ToSelection(
                listing.Value,
                resolveItemName(listing.Value.ItemId) ?? $"Item {listing.Value.ItemId}"));
        }

        if (staged.Count == 0)
        {
            selections = Array.Empty<RemoteMarketSelectionView>();
            error = "Select at least one listing.";
            return false;
        }

        selections = staged;
        error = null;
        return true;
    }

    public void CancelBatch()
    {
        foreach (var item in batchItems.Where(item => item.Status == RemoteMarketBatchItemStatus.Queued))
            item.Status = RemoteMarketBatchItemStatus.Skipped;
        if (attempt is null)
            FinishBatch("Batch cancelled.");
        else
            RebuildView();
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
        if (!IsListingSnapshotVerifiedForPurchase(listingSnapshotIdentity, browseRuntime.Snapshot))
        {
            foreach (var item in batchItems.Where(item => item.Status == RemoteMarketBatchItemStatus.Queued))
                item.Status = RemoteMarketBatchItemStatus.Skipped;
            FinishBatch("Price verification was lost before the purchase request could be sent.");
            return;
        }

        next.Status = RemoteMarketBatchItemStatus.Sending;
        var pending = new RemoteMarketPurchaseAttempt(
            next.Selection,
            clientState.TerritoryType,
            objectTable.LocalPlayer?.Position.ToString() ?? "unavailable",
            DateTimeOffset.UtcNow);
        attempt = pending;
        RebuildView();

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
        RebuildView();
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
        Complete(pending, "No purchase confirmation arrived before the deadline. Reconcile inventory and gil before retrying.");
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
        RebuildView();
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
        if (pendingPurchaseVerification is { } verification &&
            DateTimeOffset.UtcNow >= verification.DeadlineAtUtc)
        {
            FailPendingPurchaseVerification("Price verification timed out. Review the current results before purchasing.");
        }
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
            if (pendingPurchaseVerification is not null)
                FailPendingPurchaseVerification($"Price verification failed: {browse.Message}");
            else
                RebuildView();
        }
        else
        {
            log.Information(
                "[MarketMafioso] Remote market browse completed. OperationId={OperationId} ItemId={ItemId} Listings={Listings} Pages={Pages}",
                browse.OperationId,
                browse.ItemId,
                browse.ExpectedListingCount,
                browse.PageCount);
            nativeListingCache.Refresh();
            ApplyNativeListingSnapshot(nativeListingCache.Snapshot);
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
        if (pendingPurchaseVerification is not null)
            FailPendingPurchaseVerification(reason);
        RebuildView();
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
            RebuildView();
            notificationManager.AddNotification(new Notification
            {
                Title = "MMF Remote Market",
                Content = "MMF blocked the native purchase before any request was sent.",
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
        var addon = gameGui.GetAddonByName<AddonItemSearchResult>(ItemSearchResultAddon, 1);
        var resultVisible =
            addon != null &&
            addon->AtkUnitBase.IsReady &&
            addon->AtkUnitBase.IsVisible;
        var proxy = resultVisible ? GetItemSearchProxy() : null;
        return IsListingSnapshotCurrent(
            listingSnapshotIdentity,
            resultVisible,
            proxy == null ? 0 : proxy->SearchItemId,
            proxy == null ? 0 : proxy->ListingCount,
            proxy == null ? null : proxy->InfoProxyPageInterface.CurrentRequestId);
    }

    internal static bool IsListingSnapshotCurrent(
        RemoteMarketListingSnapshotIdentity? identity,
        bool resultVisible,
        uint nativeItemId,
        uint nativeListingCount,
        byte? nativeRequestId) =>
        resultVisible &&
        identity is not null &&
        nativeItemId == identity.ItemId &&
        nativeListingCount == (uint)identity.ListingCount &&
        nativeRequestId == identity.RequestId;

    internal static string? GetVerifiedBrowseOperationId(
        MarketBoardBrowseSnapshot browse,
        uint nativeItemId,
        byte nativeRequestId,
        int nativeListingCount,
        int capturedListingCount) =>
        browse.IsComplete &&
        browse.Owner == MarketBoardBrowseOwner.RemoteMarketController &&
        !string.IsNullOrWhiteSpace(browse.OperationId) &&
        browse.ItemId == nativeItemId &&
        browse.RequestId == nativeRequestId &&
        browse.ExpectedListingCount == nativeListingCount &&
        capturedListingCount == nativeListingCount
            ? browse.OperationId
            : null;

    internal static bool IsListingSnapshotVerifiedForPurchase(
        RemoteMarketListingSnapshotIdentity? identity,
        MarketBoardBrowseSnapshot browse) =>
        identity is not null &&
        !string.IsNullOrWhiteSpace(identity.VerifiedBrowseOperationId) &&
        string.Equals(identity.VerifiedBrowseOperationId, browse.OperationId, StringComparison.Ordinal) &&
        GetVerifiedBrowseOperationId(
            browse,
            identity.ItemId,
            identity.RequestId,
            identity.ListingCount,
            identity.CapturedListingCount) is not null;

    internal static bool RequiresAutomaticPurchaseVerification(
        RemoteMarketListingSnapshotIdentity? identity,
        MarketBoardBrowseSnapshot browse) =>
        !IsListingSnapshotVerifiedForPurchase(identity, browse);

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

internal sealed record RemoteMarketPurchaseVerificationView(
    int ListingCount,
    long Quantity,
    ulong TotalGil);

internal sealed record RemoteMarketEconomicsView(
    uint CheapestUnitPrice,
    uint MedianUnitPrice,
    double MeanUnitPrice,
    double? TrendDelta);

internal sealed record RemoteMarketListingSnapshotIdentity(
    uint ItemId,
    byte RequestId,
    int ListingCount,
    int CapturedListingCount,
    string? VerifiedBrowseOperationId);

internal sealed record RemoteMarketView(
    long Revision,
    bool Available,
    IReadOnlyList<RemoteMarketListingView> Listings,
    int ExpectedListingCount,
    RemoteMarketBatchView? Batch,
    RemoteMarketPurchaseVerificationView? Verification,
    string? LastOutcome,
    string? ContextBlockReason,
    uint? GilOnHand,
    CmbMarketContext? MarketContext,
    RemoteMarketEconomicsView? Economics,
    string? MarketContextSummary,
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
