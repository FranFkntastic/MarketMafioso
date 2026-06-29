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
        var reportedListingCount = (int)infoProxy->ListingCount;
        var listingCapacity = infoProxy->Listings.Length;
        var listingCount = Math.Min(reportedListingCount, listingCapacity);
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

        return BuildReadResult(
            infoProxy->WaitingForListings,
            itemId,
            currentWorld,
            listings,
            reportedListingCount,
            listingCapacity);
    }

    internal static MarketBoardReadResult BuildReadResult(
        bool waitingForListings,
        uint itemId,
        string currentWorld,
        IReadOnlyList<MarketBoardLiveListing> listings,
        int? reportedListingCount = null,
        int? listingCapacity = null)
    {
        var effectiveReportedListingCount = Math.Max(reportedListingCount ?? listings.Count, listings.Count);
        var effectiveListingCapacity = Math.Max(listingCapacity ?? listings.Count, listings.Count);
        var isAtListingCapacity = effectiveListingCapacity > 0 && listings.Count >= effectiveListingCapacity;
        var isListingCountTruncated = effectiveReportedListingCount > listings.Count;
        var capacityNote = effectiveListingCapacity > 0
            ? $" Listing cache capacity {listings.Count}/{effectiveListingCapacity}."
            : string.Empty;
        var truncatedNote = isListingCountTruncated
            ? $" Reported listing count {effectiveReportedListingCount} was truncated to the readable cache."
            : string.Empty;
        if (listings.Count > 0)
        {
            var waitingNote = waitingForListings
                ? " Waiting flag is still set, but visible listing rows were present."
                : string.Empty;
            return new MarketBoardReadResult
            {
                Status = "Ready",
                Message = $"Read {listings.Count} live market board listing(s).{capacityNote}{truncatedNote}{waitingNote}",
                ItemId = itemId,
                WorldName = currentWorld,
                ReportedListingCount = effectiveReportedListingCount,
                ListingCapacity = effectiveListingCapacity,
                IsAtListingCapacity = isAtListingCapacity,
                IsListingCountTruncated = isListingCountTruncated,
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
            ReportedListingCount = effectiveReportedListingCount,
            ListingCapacity = effectiveListingCapacity,
            IsAtListingCapacity = isAtListingCapacity,
            IsListingCountTruncated = isListingCountTruncated,
            Listings = listings,
        };
    }
}
