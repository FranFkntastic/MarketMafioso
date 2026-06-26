using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketBoardListingReader
{
    private const string ItemSearchResultAddon = "ItemSearchResult";

    private readonly IGameGui gameGui;

    public MarketBoardListingReader(IGameGui gameGui)
    {
        this.gameGui = gameGui;
    }

    public unsafe MarketBoardReadResult ReadCurrentListings(string currentWorld)
    {
        if (string.IsNullOrWhiteSpace(currentWorld))
            throw new InvalidOperationException("Current world is required before reading market board listings.");

        var addon = gameGui.GetAddonByName<AddonItemSearchResult>(ItemSearchResultAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
        {
            return new MarketBoardReadResult
            {
                Status = "MarketBoardNotOpen",
                Message = "Open market board search results for the planned item before running the read-only probe.",
            };
        }

        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null)
        {
            return new MarketBoardReadResult
            {
                Status = "InfoProxyUnavailable",
                Message = "InfoProxyItemSearch is unavailable.",
            };
        }

        var itemId = infoProxy->SearchItemId;
        if (itemId == 0)
        {
            return new MarketBoardReadResult
            {
                Status = "NoSearchItem",
                Message = "Market board search results are open, but no searched item id is available.",
            };
        }

        var listings = new List<MarketBoardLiveListing>();
        var listingCount = Math.Min((int)infoProxy->ListingCount, infoProxy->Listings.Length);
        foreach (var listing in infoProxy->Listings[..listingCount])
        {
            listings.Add(new MarketBoardLiveListing
            {
                ItemId = listing.ItemId,
                WorldName = currentWorld,
                ListingId = listing.ListingId.ToString(),
                RetainerId = listing.RetainerId.ToString(),
                RetainerName = string.Empty,
                UnitPrice = listing.UnitPrice,
                Quantity = listing.Quantity,
                IsHq = listing.IsHqItem,
            });
        }

        return BuildReadResult(infoProxy->WaitingForListings, itemId, currentWorld, listings);
    }

    internal static MarketBoardReadResult BuildReadResult(
        bool waitingForListings,
        uint itemId,
        string currentWorld,
        IReadOnlyList<MarketBoardLiveListing> listings)
    {
        if (listings.Count > 0)
        {
            var waitingNote = waitingForListings
                ? " Waiting flag is still set, but visible listing rows were present."
                : string.Empty;
            return new MarketBoardReadResult
            {
                Status = "Ready",
                Message = $"Read {listings.Count} live market board listing(s).{waitingNote}",
                ItemId = itemId,
                WorldName = currentWorld,
                Listings = listings,
            };
        }

        return new MarketBoardReadResult
        {
            Status = waitingForListings ? "WaitingForListings" : "NoListings",
            Message = waitingForListings
                ? "Market board listings are still loading."
                : "No live market board listings were available for the current search.",
            ItemId = itemId,
            WorldName = currentWorld,
            Listings = listings,
        };
    }
}
