using System;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.MarketDiagnostics;

internal sealed record ControlledMarketActorBrowseProbeView(
    string State,
    string Message,
    bool Active,
    string? ItemName,
    uint? ItemId,
    string? WorldName,
    string? BrowseOperationId,
    int? ListingCount,
    int? ArtisanObservedCount,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Drives one read-only cross-observer market-book capture. Lifestream owns the complete
/// trip to the board; MMF starts its existing fresh-browse pipeline only after the board arrives.
/// </summary>
internal sealed class ControlledMarketActorBrowseProbe
{
    private readonly Configuration configuration;
    private readonly IDataManager dataManager;
    private readonly Func<string, bool> processCommand;
    private readonly Func<bool> marketBoardReady;
    private readonly Func<uint, string, MarketBoardItemSearchResult> searchItem;
    private readonly Func<string, MarketBoardReadResult> readListings;
    private ControlledMarketActorBrowseProbeView view = new(
        "Idle", "No controlled cross-observer browse has started.", false,
        null, null, null, null, null, null, DateTimeOffset.UtcNow);

    public ControlledMarketActorBrowseProbe(
        Configuration configuration,
        IDataManager dataManager,
        Func<string, bool> processCommand,
        Func<bool> marketBoardReady,
        Func<uint, string, MarketBoardItemSearchResult> searchItem,
        Func<string, MarketBoardReadResult> readListings)
    {
        this.configuration = configuration;
        this.dataManager = dataManager;
        this.processCommand = processCommand;
        this.marketBoardReady = marketBoardReady;
        this.searchItem = searchItem;
        this.readListings = readListings;
    }

    public ControlledMarketActorBrowseProbeView Snapshot() => view;

    public ControlledMarketActorBrowseProbeView Begin(string itemName, string worldName, bool otherAutomationBusy)
    {
        if (view.Active)
            return view with { Message = "A controlled cross-observer browse is already active." };
        if (!configuration.EnableMarketDiagnostics)
            return view with { Message = "Market Diagnostics must be enabled for controlled browse evidence." };
        if (otherAutomationBusy)
            return view with { Message = "Another MarketMafioso automation owns the client." };
        if (string.IsNullOrWhiteSpace(worldName))
            return view with { Message = "The current world is unavailable." };
        var resolved = ResolveExactItem(itemName);
        if (resolved is null)
            return view with { Message = $"Exactly one marketable item named '{itemName.Trim()}' was not found." };

        if (!processCommand($"/li {worldName} mb"))
            return view with { State = "Failed", Message = "Lifestream rejected the complete market-board trip.", UpdatedAtUtc = DateTimeOffset.UtcNow };

        view = new(
            "Traveling",
            $"Lifestream is taking the client to {worldName}'s market board for {resolved.Value.Name}.",
            true,
            resolved.Value.Name,
            resolved.Value.ItemId,
            worldName,
            null,
            null,
            null,
            DateTimeOffset.UtcNow);
        return view;
    }

    public void Tick()
    {
        if (!view.Active || view.ItemId is null || string.IsNullOrWhiteSpace(view.ItemName) || string.IsNullOrWhiteSpace(view.WorldName))
            return;

        if (view.State == "Traveling")
        {
            if (!marketBoardReady())
                return;
            var search = searchItem(view.ItemId.Value, view.ItemName);
            if (!search.IsInProgress && !search.ReadyForListings)
            {
                view = view with { State = "Failed", Message = search.Message, Active = false, UpdatedAtUtc = DateTimeOffset.UtcNow };
                return;
            }
            view = view with
            {
                State = "Searching",
                Message = search.Message,
                BrowseOperationId = search.BrowseEvidence?.OperationId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            return;
        }

        if (view.State != "Searching")
            return;
        var read = readListings(view.WorldName);
        if (read.ItemId != view.ItemId || !read.IsFresh || !read.IsBrowseVerified)
            return;
        view = view with
        {
            State = "Complete",
            Message = $"Captured a verified {view.WorldName} market book with {read.Listings.Count:N0} readable listing(s).",
            Active = false,
            BrowseOperationId = read.BrowseOperationId,
            ListingCount = read.Listings.Count,
            ArtisanObservedCount = read.Listings.Count(listing => listing.ArtisanContentId is > 0),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private (uint ItemId, string Name)? ResolveExactItem(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return null;
        var matches = dataManager.GetExcelSheet<Item>()
            .Where(item => item.ItemSearchCategory.RowId != 0 && item.Name.ToString().Equals(itemName.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(item => (ItemId: item.RowId, Name: item.Name.ToString()))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
