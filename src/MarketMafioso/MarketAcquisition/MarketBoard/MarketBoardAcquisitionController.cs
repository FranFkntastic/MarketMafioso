using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.MarketBoard;
using MarketMafioso.Automation.MarketBoard;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.MarketAcquisition.MarketBoard;

internal sealed class MarketBoardAcquisitionController : IDisposable
{
    private const string ItemSearchResultAddon = "ItemSearchResult";
    private static readonly TimeSpan PurchaseVerificationDeadline = TimeSpan.FromSeconds(15);

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
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly ICondition condition;
    private readonly INotificationManager notificationManager;
    private readonly IPluginLog log;
    private readonly Func<uint, string?> resolveItemName;
    private readonly IMarketBoardBrowseRuntime browseRuntime;
    private readonly CmbMarketContextClient cmbContext;
    private readonly Dalamud.Plugin.Ipc.ICallGateProvider<uint, uint?, bool> openListingProvider;
    private readonly Dalamud.Plugin.Ipc.ICallGateProvider<bool> listingAvailabilityProvider;
    private readonly MarketBoardPurchaseGuard purchaseGuard;
    private readonly DalamudMarketBoardListingObserver listingObserver;
    private readonly MarketBoardListingSession listingSession = new();
    private readonly MarketListingPresentationSession presentationSession = new();
    private readonly MarketListingBrowseCoordinator browseCoordinator;
    private readonly MarketListingPurchaseCoordinator purchaseCoordinator;

    private PendingMarketListingPurchaseVerification? pendingPurchaseVerification;
    private ulong[] pendingSelectionRestoreIds = [];
    private string? lastOutcome;
    private MarketListingRowView[] listingSnapshot = [];
    private CmbMarketContext? marketContext;
    private long viewRevision;
    private MarketListingView cachedView = new(
        0,
        false,
        Array.Empty<MarketListingRowView>(),
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

    public MarketBoardAcquisitionController(
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
        Func<uint, string?, MarketBoardItemSearchIntent, string?, MarketBoardItemSearchResult> searchDriver,
        IMarketBoardBrowseRuntime browseRuntime,
        Dalamud.Plugin.IDalamudPluginInterface pluginInterface,
        string pluginConfigDirectory)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.framework = framework;
        this.gameGui = gameGui;
        this.condition = condition;
        this.notificationManager = notificationManager;
        this.log = log;
        this.resolveItemName = resolveItemName;
        this.browseRuntime = browseRuntime ?? throw new ArgumentNullException(nameof(browseRuntime));
        cmbContext = new CmbMarketContextClient(pluginInterface, log);
        var evidenceDirectory = Path.Combine(pluginConfigDirectory, "market-listing-purchases");
        purchaseGuard = new MarketBoardPurchaseGuard(
            interopProvider,
            addonLifecycle,
            log,
            ScheduleBlockedNativePurchaseRecovery);
        listingObserver = new DalamudMarketBoardListingObserver(
            marketBoard,
            addonLifecycle,
            framework,
            gameGui);
        browseCoordinator = new(
            searchDriver,
            browseRuntime,
            listingObserver,
            log,
            RebuildView,
            ApplyListingObservation,
            message =>
            {
                if (pendingPurchaseVerification is not null)
                    FailPendingPurchaseVerification($"Price verification failed: {message}");
                else
                    RebuildView();
            },
            message => lastOutcome = message);
        purchaseCoordinator = new(
            configuration,
            marketBoard,
            clientState,
            objectTable,
            framework,
            gameGui,
            chatGui,
            log,
            purchaseGuard,
            evidenceDirectory,
            GetPurchaseContextBlockReason,
            () => listingSession.IsVerifiedForPurchase(
                MarketListingBrowseEvidenceAdapter.FromRuntime(browseRuntime.Snapshot)),
            ReconcileConfirmedPurchase,
            RebuildView,
            outcome =>
            {
                lastOutcome = outcome;
                RebuildView();
            });
        listingObserver.Changed += OnListingObservationChanged;
        framework.Update += OnFrameworkUpdate;
        cmbContext.ContextChanged += OnMarketContextChanged;
        condition.ConditionChange += OnConditionChange;
        // Compatibility channel names remain stable for existing consumers. The
        // implementation itself is a market-listing acquisition session.
        openListingProvider = pluginInterface.GetIpcProvider<uint, uint?, bool>("MarketMafioso.OpenRemoteMarket");
        openListingProvider.RegisterFunc(OpenListingIpc);
        listingAvailabilityProvider = pluginInterface.GetIpcProvider<bool>("MarketMafioso.IsRemoteMarketAvailable");
        listingAvailabilityProvider.RegisterFunc(() => IsAvailable && clientState.IsLoggedIn);
    }

    public bool IsAvailable =>
        MarketAcquisitionUnlock.IsUnlocked(configuration) &&
        configuration.EnableMarketListingPurchases;

    public void Dispose()
    {
        CloseOwnedMarketBoardAgent();
        framework.Update -= OnFrameworkUpdate;
        purchaseCoordinator.Dispose();
        listingObserver.Changed -= OnListingObservationChanged;
        listingObserver.Dispose();
        purchaseGuard.Dispose();
        cmbContext.ContextChanged -= OnMarketContextChanged;
        cmbContext.Dispose();
        condition.ConditionChange -= OnConditionChange;
        openListingProvider.UnregisterFunc();
        listingAvailabilityProvider.UnregisterFunc();
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

    private bool OpenListingIpc(uint itemId, uint? maxUnitPrice)
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
            "[MarketMafioso] Market listings opened via IPC for item {ItemId} with max unit price {MaxUnitPrice}.",
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
            MarketBoardBrowseOwner.MarketListingAcquisition,
            itemId,
            resultVisible,
            openResultItemId);
    }

    private void OnListingObservationChanged(MarketBoardListingObservation observation) =>
        ApplyListingObservation(observation);

    private void ApplyListingObservation(MarketBoardListingObservation observation)
    {
        if (observation.Source is null)
            return;

        var browse = browseRuntime.Snapshot;
        if (!MarketListingBrowseEvidenceAdapter.CanAdoptNativeObservation(browse))
            return;

        var previousContextItemId = listingSnapshot.Length == 0 ? 0 : listingSnapshot[0].ItemId;
        var previousContextHighQuality = listingSnapshot.Length != 0 && listingSnapshot[0].IsHighQuality;
        var transition = listingSession.Observe(
            observation,
            MarketListingBrowseEvidenceAdapter.FromRuntime(browse));
        if (!transition.Changed || transition.Revision is not { } revision)
            return;

        listingSnapshot = revision.Listings
            .Select(ToListingView)
            .ToArray();
        presentationSession.ObserveSnapshot(clientState.TerritoryType);
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
        {
            RebuildView();
        }
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
        if (listingSession.Revision is { } partial &&
            partial.Listings.Count < partial.Source.ListingCount)
        {
            return $"Loading listings ({partial.Listings.Count} of {partial.Source.ListingCount}).";
        }

        if (!purchaseGuard.IsAvailable)
            return "The market-board purchase guard is unavailable, so purchases are blocked.";
        foreach (var flag in PurchaseBlockingConditions)
        {
            if (condition[flag])
                return $"Cannot purchase while {flag}.";
        }
        if (configuration.MarketListingRejectedTerritories.Contains(clientState.TerritoryType))
            return "Purchases have been rejected in this area before.";
        return null;
    }

    public void ClearRejectedTerritories()
    {
        configuration.MarketListingRejectedTerritories.Clear();
        configuration.Save();
        RebuildView();
    }

    public void SetDebugOutcome(string message)
    {
        lastOutcome = message;
        RebuildView();
    }

    public MarketBoardItemSearchResult SearchItem(
        uint itemId,
        string? itemName,
        MarketBoardItemSearchIntent intent = MarketBoardItemSearchIntent.PresentOrBrowse,
        string? previousOperationId = null) =>
        browseCoordinator.Search(itemId, itemName, intent, previousOperationId);

    public unsafe string RunNativePurchaseGuardSelfTest()
    {
        if (!IsAvailable || !purchaseGuard.IsAvailable)
            return "Native-purchase guard is unavailable.";
        if (!purchaseGuard.IsAcquisitionSessionActive)
            return "Open the board through MMF before testing the native-purchase guard.";

        var proxy = GetItemSearchProxy();
        if (proxy == null)
            return "ItemSearch proxy is unavailable.";
        if (proxy->LastPurchasedMarketboardItem.ListingId != 0)
            return "Guard test refused: a real listing is currently staged.";

        var blockedBefore = purchaseGuard.BlockedNativePurchaseCount;
        var sent = proxy->SendPurchaseRequestPacket();
        var blockedAfter = purchaseGuard.BlockedNativePurchaseCount;
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
        AbandonTrackedBrowse("Market listings closed during testing.");
        presentationSession.Close();
        purchaseGuard.ObserveMarketAgentActive(false);
        purchaseCoordinator.ResetStagedState();
        return "Market board closed and acquisition ownership released.";
    }

    public string OpenMarketBoard()
    {
        if (!IsAvailable)
            return "Market listing acquisition is locked.";
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
            purchaseGuard.ObserveAcquisitionOpen(wasActive, opened);
            log.Information("[MarketMafioso] Market board opened for listing acquisition. Territory={Territory} AgentActive={AgentActive}", clientState.TerritoryType, opened);
            return opened ? "Market board opened." : "Market board was shown but did not activate.";
        }
    }

    public MarketListingView GetView() => cachedView;

    public bool ShouldPresentOverlay() =>
        IsAvailable &&
        presentationSession.IsActive &&
        listingSession.Revision is not null;

    private void RebuildView()
    {
        var browse = browseRuntime.Snapshot;
        var listings = listingSnapshot
            .Select(listing => listing with
            {
                AlreadyPurchased = purchaseCoordinator.WasPurchased(listing.ListingId),
                BatchStatus = purchaseCoordinator.GetStatus(listing.ListingId),
            })
            .ToArray();

        var batch = purchaseCoordinator.GetView();
        var verification = pendingPurchaseVerification is null
            ? null
            : new MarketListingVerificationView(
                pendingPurchaseVerification.IntendedSelections.Count,
                pendingPurchaseVerification.IntendedSelections.Sum(selection => (long)selection.Quantity),
                pendingPurchaseVerification.IntendedSelections.Aggregate(
                    0UL,
                    (sum, selection) => sum + selection.TotalGil));
        var contextBlockReason = GetPurchaseContextBlockReason();

        cachedView = new MarketListingView(
            ++viewRevision,
            IsAvailable && contextBlockReason is null,
            listings,
            listingSession.Revision?.Source.ListingCount ?? listings.Length,
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

    private static MarketListingEconomics? BuildEconomics(
        IReadOnlyCollection<MarketListingRowView> listings,
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
        return new MarketListingEconomics(cheapest, median, mean, trendDelta);
    }

    private static string? BuildMarketContextSummary(
        IReadOnlyCollection<MarketListingRowView> listings,
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
            return "Market listing acquisition is locked.";
        if (pendingPurchaseVerification is not null)
            return "Prices are already being verified.";
        if (purchaseCoordinator.HasBatch)
            return "A purchase batch is already in progress.";
        if (GetPurchaseContextBlockReason() is { } contextBlock)
            return contextBlock;

        if (!TryReadSelections(selectedListingIds, out var staged, out var selectionError))
            return selectionError;

        if (!listingSession.IsVerifiedForPurchase(MarketListingBrowseEvidenceAdapter.FromRuntime(browseRuntime.Snapshot)))
            return BeginPendingPurchaseVerification(staged);

        return purchaseCoordinator.Start(staged);
    }

    public void CancelBatch() => purchaseCoordinator.Cancel();

    private string? BeginPendingPurchaseVerification(IReadOnlyList<MarketListingSelection> selections)
    {
        var itemId = selections[0].ItemId;
        if (selections.Any(selection => selection.ItemId != itemId))
            return "A purchase batch cannot span multiple items.";

        var itemName = resolveItemName(itemId) ?? selections[0].ItemName;
        pendingPurchaseVerification = new PendingMarketListingPurchaseVerification(
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
            listingSession.Revision is not { } revision ||
            revision.Source.ItemId != pending.ItemId ||
            !revision.IsComplete ||
            !listingSession.IsVerifiedForPurchase(MarketListingBrowseEvidenceAdapter.FromRuntime(browseRuntime.Snapshot)))
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

        var reconciliation = MarketListingPurchaseVerification.Reconcile(
            pending.IntendedSelections,
            refreshed);
        if (!reconciliation.Succeeded)
        {
            FailPendingPurchaseVerification(
                $"{reconciliation.FailureReason} Review the refreshed results before purchasing.");
            return true;
        }

        pendingPurchaseVerification = null;
        var startError = purchaseCoordinator.Start(reconciliation.RefreshedSelections);
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
        log.Warning("[MarketMafioso] Market-listing price verification stopped: {Reason}", reason);
        StopTrackedBrowse(reason);
        RebuildView();
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

    private bool TryReadSelections(
        IEnumerable<ulong> selectedListingIds,
        out IReadOnlyList<MarketListingSelection> selections,
        out string? error)
    {
        var staged = new List<MarketListingSelection>();
        foreach (var listingId in selectedListingIds.Distinct())
        {
            var matches = listingSnapshot
                .Where(listing => listing.ListingId == listingId)
                .ToArray();
            if (matches.Length != 1)
            {
                selections = Array.Empty<MarketListingSelection>();
                error = "A selected listing is no longer available.";
                return false;
            }
            if (purchaseCoordinator.WasPurchased(listingId))
            {
                selections = Array.Empty<MarketListingSelection>();
                error = "One of the selected listings was already purchased.";
                return false;
            }

            staged.Add(ToSelection(matches[0]));
        }

        if (staged.Count == 0)
        {
            selections = Array.Empty<MarketListingSelection>();
            error = "Select at least one listing.";
            return false;
        }

        selections = staged;
        error = null;
        return true;
    }

    private void ReconcileConfirmedPurchase(ulong listingId)
    {
        var transition = listingSession.ConfirmPurchase(listingId);
        if (transition.Transition != MarketBoardListingTransition.ConfirmedPurchase ||
            transition.Revision is not { } revision)
            return;

        listingSnapshot = revision.Listings
            .Select(ToListingView)
            .ToArray();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (pendingPurchaseVerification is { } verification &&
            DateTimeOffset.UtcNow >= verification.DeadlineAtUtc)
        {
            FailPendingPurchaseVerification("Price verification timed out. Review the current results before purchasing.");
        }
        browseCoordinator.Tick(DateTimeOffset.UtcNow);
        unsafe
        {
            var agentModule = AgentModule.Instance();
            var agent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.ItemSearch);
            var agentActive = agent != null && agent->IsAgentActive();
            purchaseGuard.ObserveMarketAgentActive(agentActive);
            var resultVisible = IsAddonVisible(ItemSearchResultAddon);
            presentationSession.ObserveNativeState(
                clientState.IsLoggedIn,
                clientState.TerritoryType,
                resultVisible,
                resultVisible && IsMarketBoardResultVisible(),
                IsAddonVisible("ItemSearch"),
                agentActive,
                pendingPurchaseVerification is not null);
        }
    }

    private unsafe void CloseOwnedMarketBoardAgent()
    {
        AbandonTrackedBrowse("Market-listing acquisition disposed or closed its owned agent.");
        presentationSession.Close();
        if (!purchaseGuard.IsAcquisitionSessionActive)
            return;

        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.ItemSearch);
        if (agent != null && agent->IsAgentActive())
        {
            log.Information("[MarketMafioso] Closing MMF-owned market-board agent during plugin disposal.");
            agent->Hide();
        }

        purchaseGuard.ObserveMarketAgentActive(false);
        purchaseCoordinator.ResetStagedState();
    }

    private void AbandonTrackedBrowse(string reason)
    {
        StopTrackedBrowse(reason);
        if (pendingPurchaseVerification is { } verification)
        {
            RestoreVerifiedSelection(
                verification.IntendedSelections.Select(selection => selection.ListingId));
            pendingPurchaseVerification = null;
            lastOutcome = reason;
        }
        RebuildView();
    }

    private void StopTrackedBrowse(string reason)
    {
        browseCoordinator.Stop(reason);
    }

    private void ScheduleBlockedNativePurchaseRecovery()
    {
        if (blockedNativePurchaseRecoveryScheduled)
            return;

        blockedNativePurchaseRecoveryScheduled = true;
        framework.RunOnTick(() =>
        {
            blockedNativePurchaseRecoveryScheduled = false;
            purchaseCoordinator.ResetStagedState();
            lastOutcome = "Native purchase blocked locally; no request was sent and this area was not marked as rejecting purchases.";
            RebuildView();
            notificationManager.AddNotification(new Notification
            {
                Title = "MMF Market Listings",
                Content = "MMF blocked the native purchase before any request was sent.",
                Type = NotificationType.Warning,
                InitialDuration = TimeSpan.FromSeconds(8),
                Minimized = false,
            });
        });
    }

    private static MarketListingSelection ToSelection(MarketListingRowView listing) => new(
        listing.ItemId,
        listing.ItemName,
        listing.IsHighQuality,
        listing.Quantity,
        listing.UnitPrice,
        listing.TotalTax,
        listing.TotalGil,
        listing.ListingId,
        listing.RetainerId);

    private MarketListingRowView ToListingView(
        Franthropy.Dalamud.Automation.MarketBoard.MarketBoardListing listing) =>
        new(
            listing.ListingId,
            listing.ItemId,
            resolveItemName(listing.ItemId) ?? $"Item {listing.ItemId}",
            listing.IsHighQuality,
            listing.Quantity,
            listing.UnitPrice,
            listing.TotalTax,
            listing.TotalGil,
            listing.MateriaCount,
            listing.RetainerId,
            listing.RetainerName,
            purchaseCoordinator.WasPurchased(listing.ListingId),
            purchaseCoordinator.GetStatus(listing.ListingId));

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
        return listingSession.IsCurrentNativePresentation(
            resultVisible,
            proxy == null ? 0 : proxy->SearchItemId,
            proxy == null ? 0 : proxy->ListingCount,
            proxy == null ? null : proxy->InfoProxyPageInterface.CurrentRequestId);
    }

    public unsafe MarketListingNativePresentation GetNativePresentationState()
    {
        var addon = gameGui.GetAddonByName<AddonItemSearchResult>(ItemSearchResultAddon, 1);
        var resultVisible =
            addon != null &&
            addon->AtkUnitBase.IsReady &&
            addon->AtkUnitBase.IsVisible;
        var proxy = GetItemSearchProxy();
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : agentModule->GetAgentByInternalId(AgentId.ItemSearch);
        var nativeItemId = proxy == null ? 0 : proxy->SearchItemId;
        var nativeListingCount = proxy == null ? 0 : proxy->ListingCount;
        var nativeRequestId = proxy == null
            ? (byte?)null
            : proxy->InfoProxyPageInterface.CurrentRequestId;
        return new MarketListingNativePresentation(
            resultVisible,
            agent != null && agent->IsAgentActive(),
            nativeItemId,
            nativeListingCount,
            nativeRequestId,
            listingSession.IsCurrentNativePresentation(
                resultVisible,
                nativeItemId,
                nativeListingCount,
                nativeRequestId));
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

    private unsafe bool IsAddonVisible(string addonName)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        return addon != null && addon->IsReady && addon->IsVisible;
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
