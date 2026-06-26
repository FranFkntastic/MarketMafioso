using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketBoardItemSearchDriver
{
    private const string ItemSearchAddon = "ItemSearch";

    private readonly IGameGui gameGui;

    public MarketBoardItemSearchDriver(IGameGui gameGui)
    {
        this.gameGui = gameGui;
    }

    public unsafe MarketBoardItemSearchResult Search(uint itemId, string? itemName)
    {
        if (itemId == 0)
            throw new InvalidOperationException("Item id is required before searching the market board.");

        if (string.IsNullOrWhiteSpace(itemName))
            throw new InvalidOperationException($"Item name is required before searching the market board for item {itemId}.");

        var addon = gameGui.GetAddonByName<AddonItemSearch>(ItemSearchAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
        {
            return new MarketBoardItemSearchResult
            {
                Status = "MarketBoardNotOpen",
                Message = "Waiting for the market board item search window to open.",
            };
        }

        var searchText = itemName.Trim();
        addon->SearchText.SetString(searchText);
        addon->SearchText2.SetString(searchText);
        if (addon->SearchTextInput != null)
            addon->SearchTextInput->SetText(searchText);

        addon->RunSearch(ignoreFilters: true);
        return new MarketBoardItemSearchResult
        {
            Status = "SearchSent",
            Message = $"Searching market board for {searchText} ({itemId}).",
        };
    }
}

public sealed record MarketBoardItemSearchResult
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool SearchSent => string.Equals(Status, "SearchSent", StringComparison.OrdinalIgnoreCase);
}
