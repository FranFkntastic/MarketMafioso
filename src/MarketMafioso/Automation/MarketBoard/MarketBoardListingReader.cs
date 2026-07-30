using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace MarketMafioso.Automation.MarketBoard;

public sealed class MarketBoardListingReader
{
    private readonly IMarketBoardBrowseRuntime browseRuntime;

    public MarketBoardListingReader(IMarketBoardBrowseRuntime browseRuntime)
    {
        this.browseRuntime = browseRuntime ?? throw new ArgumentNullException(nameof(browseRuntime));
    }

    public unsafe MarketBoardReadResult ReadCurrentListings(string currentWorld)
    {
        if (string.IsNullOrWhiteSpace(currentWorld))
            throw new InvalidOperationException("Current world is required before reading market board listings.");

        if (!browseRuntime.IsAvailable)
        {
            return new MarketBoardReadResult
            {
                Status = "BrowseObserverUnavailable",
                Message = browseRuntime.AvailabilityMessage,
                ReadState = MarketBoardListingReadState.Unavailable,
            };
        }

        var browse = browseRuntime.Snapshot;
        if (browse.IsFailed)
        {
            return new MarketBoardReadResult
            {
                Status = browse.FailureCode ?? "BrowseEvidenceFailed",
                Message = browse.Message,
                ReadState = MarketBoardListingReadState.Unavailable,
                ItemId = browse.ItemId,
                WorldName = currentWorld,
                BrowseOperationId = browse.OperationId,
                BrowseHeaderStatus = browse.HeaderStatus,
                BrowseExpectedPageCount = browse.ExpectedPageCount,
                BrowseObservedPageCount = browse.PageCount,
                BrowseHistoryItemId = browse.HistoryItemId,
            };
        }

        if (!browse.IsComplete)
        {
            return new MarketBoardReadResult
            {
                Status = "AwaitingBrowseEvidence",
                Message = browse.Message,
                ReadState = MarketBoardListingReadState.Loading,
                ItemId = browse.ItemId,
                WorldName = currentWorld,
                BrowseOperationId = browse.OperationId,
                BrowseHeaderStatus = browse.HeaderStatus,
                BrowseExpectedPageCount = browse.ExpectedPageCount,
                BrowseObservedPageCount = browse.PageCount,
                BrowseHistoryItemId = browse.HistoryItemId,
            };
        }

        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null)
        {
            return new MarketBoardReadResult
            {
                Status = "InfoProxyUnavailable",
                Message = "InfoProxyItemSearch is unavailable.",
                ReadState = MarketBoardListingReadState.Unavailable,
            };
        }

        var itemId = browse.ItemId;
        if (itemId == 0 || infoProxy->SearchItemId != itemId)
        {
            return new MarketBoardReadResult
            {
                Status = "ListingCacheItemMismatch",
                Message =
                    $"Verified browse {browse.OperationId} owns item {itemId}, but the listing cache reports item {infoProxy->SearchItemId}.",
                ReadState = MarketBoardListingReadState.Unavailable,
                ItemId = itemId,
                WorldName = currentWorld,
                BrowseOperationId = browse.OperationId,
            };
        }

        var reportedListingCount = browse.ExpectedListingCount;
        if (infoProxy->ListingCount != reportedListingCount)
        {
            return new MarketBoardReadResult
            {
                Status = "ListingCacheCountMismatch",
                Message =
                    $"Verified browse {browse.OperationId} expects {reportedListingCount} listings, but the native cache reports {infoProxy->ListingCount}.",
                ReadState = MarketBoardListingReadState.Unavailable,
                ItemId = itemId,
                WorldName = currentWorld,
                ReportedListingCount = reportedListingCount,
                ListingCapacity = infoProxy->Listings.Length,
                BrowseOperationId = browse.OperationId,
                BrowseHeaderStatus = browse.HeaderStatus,
                BrowseExpectedPageCount = browse.ExpectedPageCount,
                BrowseObservedPageCount = browse.PageCount,
                BrowseHistoryItemId = browse.HistoryItemId,
            };
        }

        if (browse.RequestId is { } observedRequestId &&
            infoProxy->InfoProxyPageInterface.CurrentRequestId != observedRequestId)
        {
            return new MarketBoardReadResult
            {
                Status = "ListingCacheRequestMismatch",
                Message =
                    $"Verified browse {browse.OperationId} owns request {observedRequestId}, but the native cache reports {infoProxy->InfoProxyPageInterface.CurrentRequestId}.",
                ReadState = MarketBoardListingReadState.Unavailable,
                ItemId = itemId,
                WorldName = currentWorld,
                ReportedListingCount = reportedListingCount,
                ListingCapacity = infoProxy->Listings.Length,
                CurrentRequestId = infoProxy->InfoProxyPageInterface.CurrentRequestId,
                NextRequestId = infoProxy->InfoProxyPageInterface.NextRequestId,
                BrowseOperationId = browse.OperationId,
                BrowseHeaderStatus = browse.HeaderStatus,
                BrowseExpectedPageCount = browse.ExpectedPageCount,
                BrowseObservedPageCount = browse.PageCount,
                BrowseHistoryItemId = browse.HistoryItemId,
            };
        }

        var listings = new List<MarketBoardLiveListing>();
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
            itemId,
            currentWorld,
            listings,
            reportedListingCount,
            listingCapacity,
            infoProxy->InfoProxyPageInterface.CurrentRequestId,
            infoProxy->InfoProxyPageInterface.NextRequestId,
            browse);
    }

    internal static MarketBoardReadResult BuildReadResult(
        uint itemId,
        string currentWorld,
        IReadOnlyList<MarketBoardLiveListing> listings,
        int? reportedListingCount = null,
        int? listingCapacity = null,
        byte currentRequestId = 0,
        byte nextRequestId = 0,
        MarketBoardBrowseSnapshot? browse = null)
    {
        if (browse?.IsComplete != true ||
            browse.ItemId != itemId ||
            browse.HeaderStatus != 0 ||
            browse.HistoryItemId != itemId)
        {
            return new MarketBoardReadResult
            {
                Status = "UnverifiedBrowseEvidence",
                Message = "Native listing rows are not authoritative without a completed matching browse lifecycle.",
                ReadState = MarketBoardListingReadState.Unavailable,
                ItemId = itemId,
                WorldName = currentWorld,
                CurrentRequestId = currentRequestId,
                NextRequestId = nextRequestId,
                BrowseOperationId = browse?.OperationId ?? string.Empty,
                BrowseHeaderStatus = browse?.HeaderStatus ?? 0,
                BrowseExpectedPageCount = browse?.ExpectedPageCount ?? 0,
                BrowseObservedPageCount = browse?.PageCount ?? 0,
                BrowseHistoryItemId = browse?.HistoryItemId,
            };
        }

        var realListings = listings
            .Where(MarketBoardListingIntegrity.IsRealListing)
            .ToArray();
        var rawItemIdMismatchCounts = BuildRawItemIdMismatchCounts(itemId, realListings);
        var effectiveReportedListingCount = Math.Max(reportedListingCount ?? realListings.Length, realListings.Length);
        var effectiveListingCapacity = Math.Max(listingCapacity ?? realListings.Length, realListings.Length);
        var isAtListingCapacity = effectiveListingCapacity > 0 && realListings.Length >= effectiveListingCapacity;
        var isListingCountTruncated = effectiveReportedListingCount > realListings.Length;
        if (isListingCountTruncated)
        {
            return new MarketBoardReadResult
            {
                Status = "ListingCacheIncomplete",
                Message =
                    $"Verified browse expected {effectiveReportedListingCount} listings, but only {realListings.Length} valid native rows were readable.",
                ReadState = MarketBoardListingReadState.Unavailable,
                ItemId = itemId,
                WorldName = currentWorld,
                ReportedListingCount = effectiveReportedListingCount,
                ListingCapacity = effectiveListingCapacity,
                IsAtListingCapacity = isAtListingCapacity,
                IsListingCountTruncated = true,
                CurrentRequestId = currentRequestId,
                NextRequestId = nextRequestId,
                Listings = [],
                BrowseOperationId = browse?.OperationId ?? string.Empty,
                BrowseHeaderStatus = browse?.HeaderStatus ?? 0,
                BrowseExpectedPageCount = browse?.ExpectedPageCount ?? 0,
                BrowseObservedPageCount = browse?.PageCount ?? 0,
                BrowseHistoryItemId = browse?.HistoryItemId,
            };
        }

        var readState = isListingCountTruncated
            ? MarketBoardListingReadState.FreshPartial
            : MarketBoardListingReadState.FreshComplete;
        var capacityNote = effectiveListingCapacity > 0
            ? $" Listing cache capacity {realListings.Length}/{effectiveListingCapacity}."
            : string.Empty;
        var truncatedNote = isListingCountTruncated
            ? $" Reported listing count {effectiveReportedListingCount} was truncated to the readable cache."
            : string.Empty;
        if (rawItemIdMismatchCounts.Count > 0)
        {
            return new MarketBoardReadResult
            {
                Status = "ListingCacheSwitching",
                Message = $"Market board listing cache is still switching to item {itemId}; raw row item ids included {FormatRawItemIdMismatchCounts(rawItemIdMismatchCounts)}.",
                ReadState = MarketBoardListingReadState.SwitchingItem,
                ItemId = itemId,
                WorldName = currentWorld,
                ReportedListingCount = effectiveReportedListingCount,
                ListingCapacity = effectiveListingCapacity,
                IsAtListingCapacity = isAtListingCapacity,
                IsListingCountTruncated = isListingCountTruncated,
                CurrentRequestId = currentRequestId,
                NextRequestId = nextRequestId,
                RawItemIdMismatchCounts = rawItemIdMismatchCounts,
                Listings = [],
                BrowseOperationId = browse?.OperationId ?? string.Empty,
                BrowseHeaderStatus = browse?.HeaderStatus ?? 0,
                BrowseExpectedPageCount = browse?.ExpectedPageCount ?? 0,
                BrowseObservedPageCount = browse?.PageCount ?? 0,
                BrowseHistoryItemId = browse?.HistoryItemId,
            };
        }

        if (realListings.Length > 0)
        {
            return new MarketBoardReadResult
            {
                Status = "Ready",
                Message = $"Read {realListings.Length} verified live market board listing(s).{capacityNote}{truncatedNote}",
                ReadState = readState,
                ItemId = itemId,
                WorldName = currentWorld,
                ReportedListingCount = effectiveReportedListingCount,
                ListingCapacity = effectiveListingCapacity,
                IsAtListingCapacity = isAtListingCapacity,
                IsListingCountTruncated = isListingCountTruncated,
                CurrentRequestId = currentRequestId,
                NextRequestId = nextRequestId,
                RawItemIdMismatchCounts = rawItemIdMismatchCounts,
                Listings = realListings,
                BrowseOperationId = browse?.OperationId ?? string.Empty,
                BrowseHeaderStatus = browse?.HeaderStatus ?? 0,
                BrowseExpectedPageCount = browse?.ExpectedPageCount ?? 0,
                BrowseObservedPageCount = browse?.PageCount ?? 0,
                BrowseHistoryItemId = browse?.HistoryItemId,
            };
        }

        return new MarketBoardReadResult
        {
            Status = "NoListings",
            Message = "The correlated result header authoritatively reported no live listings.",
            ReadState = MarketBoardListingReadState.FreshComplete,
            ItemId = itemId,
            WorldName = currentWorld,
            ReportedListingCount = effectiveReportedListingCount,
            ListingCapacity = effectiveListingCapacity,
            IsAtListingCapacity = isAtListingCapacity,
            IsListingCountTruncated = isListingCountTruncated,
            CurrentRequestId = currentRequestId,
            NextRequestId = nextRequestId,
            RawItemIdMismatchCounts = rawItemIdMismatchCounts,
            Listings = [],
            BrowseOperationId = browse?.OperationId ?? string.Empty,
            BrowseHeaderStatus = browse?.HeaderStatus ?? 0,
            BrowseExpectedPageCount = browse?.ExpectedPageCount ?? 0,
            BrowseObservedPageCount = browse?.PageCount ?? 0,
            BrowseHistoryItemId = browse?.HistoryItemId,
        };
    }

    private static IReadOnlyDictionary<uint, int> BuildRawItemIdMismatchCounts(
        uint itemId,
        IReadOnlyList<MarketBoardLiveListing> listings)
    {
        if (itemId == 0 || listings.Count == 0)
            return new Dictionary<uint, int>();

        var counts = new Dictionary<uint, int>();
        foreach (var listing in listings)
        {
            var rawItemId = listing.RawItemId ?? listing.ItemId;
            if (rawItemId == itemId && listing.ItemId == itemId)
                continue;

            var mismatchItemId = rawItemId != itemId
                ? rawItemId
                : listing.ItemId;

            counts[mismatchItemId] = counts.GetValueOrDefault(mismatchItemId) + 1;
        }

        return counts;
    }

    private static string FormatRawItemIdMismatchCounts(IReadOnlyDictionary<uint, int> counts) =>
        string.Join(
            ", ",
            counts
                .OrderBy(count => count.Key)
                .Select(count => $"{count.Key}={count.Value}"));
}

