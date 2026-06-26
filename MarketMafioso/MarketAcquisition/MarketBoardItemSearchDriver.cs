using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketBoardItemSearchDriver
{
    private const string ItemSearchAddon = "ItemSearch";
    private const string ItemSearchResultAddon = "ItemSearchResult";

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
        var mode = (uint)addon->Mode;
        var partialSearchWasEnabled = addon->PartialMatch;
        var details = new Dictionary<string, string?>
        {
            ["mode"] = FormatMode(mode),
            ["modeRaw"] = mode.ToString(),
            ["partialSearch"] = partialSearchWasEnabled.ToString(),
            ["itemSearchResultVisible"] = IsAddonReady(gameGui.GetAddonByName<AtkUnitBase>(ItemSearchResultAddon, 1)).ToString(),
        };

        if (ChooseAction(mode) == MarketBoardItemSearchAction.ResetMode)
        {
            addon->SetModeFilter(AddonItemSearch.SearchMode.Normal, 0);
            return new MarketBoardItemSearchResult
            {
                Status = "ModeReset",
                Message = $"Resetting market board item search mode from {FormatMode(mode)} before searching for {searchText} ({itemId}).",
                Details = details,
            };
        }

        if (ShouldDisablePartialSearch(partialSearchWasEnabled))
        {
            addon->PartialMatch = false;
            if (addon->PartialSearchCheckBox != null)
                addon->PartialSearchCheckBox->AtkComponentButton.SetChecked(false);
        }

        addon->SearchText.SetString(searchText);
        addon->SearchText2.SetString(searchText);
        if (addon->SearchTextInput != null)
            addon->SearchTextInput->SetText(searchText);

        addon->RunSearch(ignoreFilters: true);
        return new MarketBoardItemSearchResult
        {
            Status = "SearchSent",
            Message = $"Searching market board for {searchText} ({itemId}).",
            Details = details,
        };
    }

    internal static bool ShouldResetToNormalSearch(uint mode)
    {
        return mode != (uint)AddonItemSearch.SearchMode.Normal;
    }

    internal static MarketBoardItemSearchAction ChooseAction(uint mode)
    {
        return ShouldResetToNormalSearch(mode)
            ? MarketBoardItemSearchAction.ResetMode
            : MarketBoardItemSearchAction.SubmitSearch;
    }

    internal static bool ShouldDisablePartialSearch(bool partialSearchEnabled)
    {
        return partialSearchEnabled;
    }

    private static unsafe bool IsAddonReady(AtkUnitBase* addon)
    {
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private static string FormatMode(uint mode)
    {
        return Enum.IsDefined(typeof(AddonItemSearch.SearchMode), mode)
            ? ((AddonItemSearch.SearchMode)mode).ToString()
            : mode.ToString();
    }
}

public enum MarketBoardItemSearchAction
{
    ResetMode,
    SubmitSearch,
}

public sealed record MarketBoardItemSearchResult
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string?> Details { get; init; } = new Dictionary<string, string?>();
    public bool SearchSent => string.Equals(Status, "SearchSent", StringComparison.OrdinalIgnoreCase);
}
