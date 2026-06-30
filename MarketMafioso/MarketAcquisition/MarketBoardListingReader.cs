using System;
using System.Collections.Generic;
using System.Linq;
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
            if (listing.ListingId == 0 ||
                listing.RetainerId == 0 ||
                listing.UnitPrice == 0 ||
                listing.Quantity == 0)
            {
                continue;
            }

            listings.Add(new MarketBoardLiveListing
            {
                ItemId = listing.ItemId,
                RawItemId = listing.ItemId,
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
            listingCapacity,
            infoProxy->InfoProxyPageInterface.CurrentRequestId,
            infoProxy->InfoProxyPageInterface.NextRequestId);
    }

    internal static MarketBoardReadResult BuildReadResult(
        bool waitingForListings,
        uint itemId,
        string currentWorld,
        IReadOnlyList<MarketBoardLiveListing> listings,
        int? reportedListingCount = null,
        int? listingCapacity = null,
        byte currentRequestId = 0,
        byte nextRequestId = 0)
    {
        var realListings = listings
            .Where(MarketBoardListingIntegrity.IsRealListing)
            .ToArray();
        var normalizedListings = NormalizeListingItemIds(itemId, realListings);
        var effectiveReportedListingCount = Math.Max(reportedListingCount ?? normalizedListings.Count, normalizedListings.Count);
        var effectiveListingCapacity = Math.Max(listingCapacity ?? normalizedListings.Count, normalizedListings.Count);
        var isAtListingCapacity = effectiveListingCapacity > 0 && normalizedListings.Count >= effectiveListingCapacity;
        var isListingCountTruncated = effectiveReportedListingCount > normalizedListings.Count;
        var rawItemIdMismatchCount = normalizedListings.Count(listing => listing.RawItemId.HasValue && listing.RawItemId.Value != itemId);
        var capacityNote = effectiveListingCapacity > 0
            ? $" Listing cache capacity {normalizedListings.Count}/{effectiveListingCapacity}."
            : string.Empty;
        var truncatedNote = isListingCountTruncated
            ? $" Reported listing count {effectiveReportedListingCount} was truncated to the readable cache."
            : string.Empty;
        var rawItemIdMismatchNote = rawItemIdMismatchCount > 0
            ? $" Normalized {rawItemIdMismatchCount} proxy row item id mismatch(es) to the active search item."
            : string.Empty;
        if (normalizedListings.Count > 0)
        {
            var waitingNote = waitingForListings
                ? " Waiting flag is still set, but visible listing rows were present."
                : string.Empty;
            return new MarketBoardReadResult
            {
                Status = "Ready",
                Message = $"Read {normalizedListings.Count} live market board listing(s).{capacityNote}{truncatedNote}{rawItemIdMismatchNote}{waitingNote}",
                ItemId = itemId,
                WorldName = currentWorld,
                ReportedListingCount = effectiveReportedListingCount,
                ListingCapacity = effectiveListingCapacity,
                IsAtListingCapacity = isAtListingCapacity,
                IsListingCountTruncated = isListingCountTruncated,
                CurrentRequestId = currentRequestId,
                NextRequestId = nextRequestId,
                Listings = normalizedListings,
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
            CurrentRequestId = currentRequestId,
            NextRequestId = nextRequestId,
            Listings = normalizedListings,
        };
    }

    private static IReadOnlyList<MarketBoardLiveListing> NormalizeListingItemIds(
        uint itemId,
        IReadOnlyList<MarketBoardLiveListing> listings)
    {
        if (itemId == 0 || listings.Count == 0)
            return listings;

        List<MarketBoardLiveListing>? normalized = null;
        for (var index = 0; index < listings.Count; index++)
        {
            var listing = listings[index];
            if (listing.ItemId == itemId)
            {
                normalized?.Add(listing);
                continue;
            }

            normalized ??= new List<MarketBoardLiveListing>(listings.Count);
            for (var copyIndex = normalized.Count; copyIndex < index; copyIndex++)
                normalized.Add(listings[copyIndex]);

            normalized.Add(listing with
            {
                ItemId = itemId,
                RawItemId = listing.RawItemId ?? listing.ItemId,
            });
        }

        return normalized ?? listings;
    }
}
