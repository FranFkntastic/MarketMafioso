using System;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.MarketBoard;
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.MarketAcquisition.MarketBoard;

internal sealed class MarketListingBrowseCoordinator
{
    private readonly Func<
        uint,
        string?,
        MarketBoardItemSearchIntent,
        string?,
        MarketBoardItemSearchResult> searchDriver;
    private readonly IMarketBoardBrowseRuntime browseRuntime;
    private readonly DalamudMarketBoardListingObserver listingObserver;
    private readonly IPluginLog log;
    private readonly Action stateChanged;
    private readonly Action<MarketBoardListingObservation> observationReady;
    private readonly Action<string> browseFailed;
    private readonly Action<string> outcomeChanged;

    private string? operationId;
    private uint itemId;
    private string? itemName;
    private bool terminalReported;
    private bool searchActive;
    private MarketBoardItemSearchIntent intent;
    private string? previousOperationId;
    private DateTimeOffset nextPollUtc;

    public MarketListingBrowseCoordinator(
        Func<
            uint,
            string?,
            MarketBoardItemSearchIntent,
            string?,
            MarketBoardItemSearchResult> searchDriver,
        IMarketBoardBrowseRuntime browseRuntime,
        DalamudMarketBoardListingObserver listingObserver,
        IPluginLog log,
        Action stateChanged,
        Action<MarketBoardListingObservation> observationReady,
        Action<string> browseFailed,
        Action<string> outcomeChanged)
    {
        this.searchDriver = searchDriver;
        this.browseRuntime = browseRuntime;
        this.listingObserver = listingObserver;
        this.log = log;
        this.stateChanged = stateChanged;
        this.observationReady = observationReady;
        this.browseFailed = browseFailed;
        this.outcomeChanged = outcomeChanged;
    }

    public MarketBoardItemSearchResult Search(
        uint requestedItemId,
        string? requestedItemName,
        MarketBoardItemSearchIntent requestedIntent,
        string? requestedPreviousOperationId)
    {
        itemId = requestedItemId;
        itemName = requestedItemName;
        intent = requestedIntent;
        previousOperationId = requestedPreviousOperationId;
        searchActive = true;
        nextPollUtc = DateTimeOffset.UtcNow;
        return AdvanceSearch();
    }

    public void Tick(DateTimeOffset nowUtc)
    {
        if (searchActive && nowUtc >= nextPollUtc)
            AdvanceSearch();
        ObserveTerminalBrowse();
    }

    public void Stop(string reason)
    {
        var browse = browseRuntime.Snapshot;
        if (browse.IsActive &&
            browse.Owner == MarketBoardBrowseOwner.MarketListingAcquisition &&
            !string.IsNullOrWhiteSpace(browse.OperationId))
        {
            browseRuntime.TryAbandon(
                MarketBoardBrowseOwner.MarketListingAcquisition,
                browse.OperationId,
                reason,
                out _);
        }

        searchActive = false;
    }

    private MarketBoardItemSearchResult AdvanceSearch()
    {
        var result = searchDriver(itemId, itemName, intent, previousOperationId);
        nextPollUtc = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(500);
        if (result.BrowseEvidence is { } browse &&
            !string.IsNullOrWhiteSpace(browse.OperationId))
        {
            if (ShouldResetTerminalLatch(operationId, browse.OperationId))
                terminalReported = false;
            operationId = browse.OperationId;
        }

        if (!result.IsInProgress && !result.ReadyForListings)
        {
            outcomeChanged(result.Message);
            searchActive = false;
        }
        else if (result.ReadyForListings)
        {
            searchActive = false;
        }

        stateChanged();
        return result;
    }

    private void ObserveTerminalBrowse()
    {
        if (terminalReported || string.IsNullOrWhiteSpace(operationId))
            return;

        var browse = browseRuntime.Snapshot;
        if (!string.Equals(browse.OperationId, operationId, StringComparison.Ordinal) ||
            browse.ItemId != itemId ||
            !browse.IsTerminal)
        {
            return;
        }

        terminalReported = true;
        outcomeChanged(browse.Message);
        if (browse.IsFailed)
        {
            log.Warning(
                "[MarketMafioso] Market-listing browse failed closed. OperationId={OperationId} Code={Code} Message={Message}",
                browse.OperationId,
                browse.FailureCode ?? "Unknown",
                browse.Message);
            browseFailed(browse.Message);
            return;
        }

        log.Information(
            "[MarketMafioso] Market-listing browse completed. OperationId={OperationId} ItemId={ItemId} Listings={Listings} Pages={Pages}",
            browse.OperationId,
            browse.ItemId,
            browse.ExpectedListingCount,
            browse.PageCount);
        listingObserver.Refresh();
        observationReady(listingObserver.Snapshot);
    }

    internal static bool ShouldResetTerminalLatch(
        string? previousOperationId,
        string nextOperationId) =>
        !string.Equals(previousOperationId, nextOperationId, StringComparison.Ordinal);
}
