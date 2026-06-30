using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.MarketAcquisition;

public sealed class DalamudMarketBoardPurchaseAdapter : IMarketBoardPurchaseAdapter
{
    private const string ItemSearchResultAddon = "ItemSearchResult";
    private const string SelectYesNoAddon = "SelectYesno";

    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    public DalamudMarketBoardPurchaseAdapter(IGameGui gameGui, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.log = log;
    }

    public unsafe MarketBoardPurchaseResult ExecutePurchase(
        MarketBoardPurchaseCandidate candidate,
        MarketBoardLiveListing freshListing)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(freshListing);

        var addon = gameGui.GetAddonByName<AddonItemSearchResult>(ItemSearchResultAddon, 1);
        var diagnostics = new Dictionary<string, string>
        {
            ["candidateItemId"] = candidate.ItemId.ToString(),
            ["candidateWorld"] = candidate.WorldName,
            ["candidateListingId"] = candidate.ListingId,
            ["candidateRetainerId"] = candidate.RetainerId,
            ["candidateQuantity"] = candidate.Quantity.ToString(),
            ["candidateUnitPrice"] = candidate.UnitPrice.ToString(),
            ["candidateHq"] = candidate.IsHq.ToString(),
            ["freshItemId"] = freshListing.ItemId.ToString(),
            ["freshWorld"] = freshListing.WorldName,
            ["freshListingId"] = freshListing.ListingId,
            ["freshRetainerId"] = freshListing.RetainerId,
            ["freshQuantity"] = freshListing.Quantity.ToString(),
            ["freshUnitPrice"] = freshListing.UnitPrice.ToString(),
            ["freshHq"] = freshListing.IsHq.ToString(),
        };

        diagnostics["addonPresent"] = (addon != null).ToString();
        if (!MarketBoardListingIntegrity.IsRealCandidate(candidate) ||
            !MarketBoardListingIntegrity.IsRealListing(freshListing))
        {
            return Fail(
                "InvalidListingIdentity",
                "The guarded purchase candidate did not contain a real market-board listing identity.",
                candidate,
                diagnostics);
        }

        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
        {
            if (addon != null)
            {
                diagnostics["addonReady"] = addon->AtkUnitBase.IsReady.ToString();
                diagnostics["addonVisible"] = addon->AtkUnitBase.IsVisible.ToString();
            }

            return Fail(
                "MarketBoardNotOpen",
                "Market board listing results closed before the purchase click could be sent.",
                candidate,
                diagnostics);
        }

        diagnostics["addonReady"] = addon->AtkUnitBase.IsReady.ToString();
        diagnostics["addonVisible"] = addon->AtkUnitBase.IsVisible.ToString();
        diagnostics["listComponentsBeforeMatch"] = MarketBoardListingListProbe.DescribeListingLists(addon);

        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null)
        {
            return Fail(
                "InfoProxyUnavailable",
                "InfoProxyItemSearch is unavailable before the purchase click could be sent.",
                candidate,
                diagnostics);
        }

        diagnostics["infoProxySearchItemId"] = infoProxy->SearchItemId.ToString();
        diagnostics["infoProxyListingCount"] = infoProxy->ListingCount.ToString();
        diagnostics["infoProxyListingCapacity"] = infoProxy->Listings.Length.ToString();
        diagnostics["infoProxyPreview"] = DescribeInfoProxyListings(infoProxy, 10);

        if (!TryFindMatchingListing(infoProxy, candidate, freshListing, out var listingIndex, out var listing))
        {
            return Fail(
                "ListingMissing",
                "The exact guarded listing was not present in InfoProxyItemSearch at purchase time.",
                candidate,
                diagnostics);
        }

        diagnostics["matchedRow"] = listingIndex.ToString();
        diagnostics["matchedListing"] = DescribeListing(listing);
        diagnostics["listComponentsBeforeSetLastPurchased"] = MarketBoardListingListProbe.DescribeListingLists(addon);

        if (!infoProxy->SetLastPurchasedItem(&listing))
        {
            diagnostics["listComponentsAfterSetLastPurchased"] = MarketBoardListingListProbe.DescribeListingLists(addon);
            return Fail(
                "SetLastPurchasedFailed",
                "The game rejected priming LastPurchasedMarketboardItem for the guarded listing.",
                candidate,
                diagnostics);
        }

        diagnostics["setLastPurchasedAccepted"] = true.ToString();
        diagnostics["listComponentsAfterSetLastPurchased"] = MarketBoardListingListProbe.DescribeListingLists(addon);

        var listProbe = MarketBoardListingListProbe.Probe(addon, listingIndex);
        diagnostics["listingListProbeReady"] = listProbe.IsReady.ToString();
        diagnostics["listingListComponentId"] = listProbe.ComponentId?.ToString() ?? string.Empty;
        diagnostics["listingListVisibleItemCount"] = listProbe.VisibleItemCount.ToString();
        diagnostics["listingListRequestedRow"] = listProbe.RequestedRow.ToString();
        diagnostics["findListingList"] = listProbe.Diagnostic;
        if (!listProbe.IsReady || listProbe.ComponentId == null)
        {
            return Fail(
                "ListingListNotReady",
                $"Market-board listing data is ready, but the clickable listing component is not ready yet. {listProbe.Diagnostic}",
                candidate,
                diagnostics);
        }

        var listingList = addon->AtkUnitBase.GetComponentListById(listProbe.ComponentId.Value);
        if (listingList == null)
        {
            return Fail(
                "ListingListNotReady",
                $"Market-board listing data was ready, but list component {listProbe.ComponentId.Value} disappeared before selection.",
                candidate,
                diagnostics);
        }

        listingList->ScrollToItem((short)listingIndex);
        listingList->SelectItem(listingIndex, true);
        listingList->DispatchItemEvent(listingIndex, AtkEventType.ListItemClick);
        listingList->DispatchItemEvent(listingIndex, AtkEventType.ListItemDoubleClick);
        log.Info(
            "[MarketMafioso] Sent market board purchase selection for listing {ListingId} retainer {RetainerId} at {UnitPrice} x {Quantity}.",
            candidate.ListingId,
            candidate.RetainerId,
            candidate.UnitPrice,
            candidate.Quantity);

        return new MarketBoardPurchaseResult
        {
            Status = "PurchaseSelectionSent",
            Message = "Sent one market-board listing selection; waiting for the purchase confirmation prompt.",
            Candidate = candidate,
            Diagnostics = diagnostics,
        };
    }

    public unsafe MarketBoardPurchaseResult TryConfirmPendingPurchase(MarketBoardPurchaseCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var addon = gameGui.GetAddonByName<AddonSelectYesno>(SelectYesNoAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
        {
            return new MarketBoardPurchaseResult
            {
                Status = "ConfirmationPending",
                Message = "Waiting for the market-board purchase confirmation prompt.",
                Candidate = candidate,
            };
        }

        var text = addon->PromptText->NodeText.ExtractText()
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal);
        if (!LooksLikeMarketPurchasePrompt(text))
        {
            return Fail(
                "UnexpectedConfirmation",
                $"A SelectYesno prompt appeared, but it did not look like a market-board purchase prompt: {text}",
                candidate) with
                {
                    ConfirmationPromptText = text,
                    ConfirmationAddonName = SelectYesNoAddon,
                };
        }

        addon->AtkUnitBase.FireCallbackInt(0);
        log.Info(
            "[MarketMafioso] Submitted market board purchase confirmation for listing {ListingId} retainer {RetainerId}.",
            candidate.ListingId,
            candidate.RetainerId);

        return new MarketBoardPurchaseResult
        {
            Status = "ConfirmationSubmitted",
            Message = $"Submitted market-board purchase confirmation: {text}",
            Candidate = candidate,
            ConfirmationPromptText = text,
            ConfirmationAddonName = SelectYesNoAddon,
        };
    }

    private static unsafe bool TryFindMatchingListing(
        InfoProxyItemSearch* infoProxy,
        MarketBoardPurchaseCandidate candidate,
        MarketBoardLiveListing freshListing,
        out int listingIndex,
        out MarketBoardListing listing)
    {
        listingIndex = -1;
        listing = default;

        var listingCount = Math.Min((int)infoProxy->ListingCount, infoProxy->Listings.Length);
        for (var index = 0; index < listingCount; index++)
        {
            var candidateListing = infoProxy->Listings[index];
            if (!MarketBoardListingIntegrity.HasRealListingIdentity(
                candidateListing.ListingId.ToString(),
                candidateListing.RetainerId.ToString(),
                candidateListing.UnitPrice,
                candidateListing.Quantity))
            {
                continue;
            }

            if (!Matches(candidateListing, infoProxy->SearchItemId, candidate, freshListing))
                continue;

            listingIndex = index;
            listing = candidateListing;
            return true;
        }

        return false;
    }

    private static unsafe string DescribeInfoProxyListings(InfoProxyItemSearch* infoProxy, int limit)
    {
        var listingCount = Math.Min((int)infoProxy->ListingCount, infoProxy->Listings.Length);
        var previewCount = Math.Min(listingCount, limit);
        if (previewCount <= 0)
            return "none";

        var preview = new List<string>();
        for (var index = 0; index < previewCount; index++)
            preview.Add($"{index}:{DescribeListing(infoProxy->Listings[index])}");

        return string.Join(" | ", preview);
    }

    private static string DescribeListing(MarketBoardListing listing) =>
        $"item={listing.ItemId},listing={listing.ListingId},retainer={listing.RetainerId},unit={listing.UnitPrice},qty={listing.Quantity},hq={listing.IsHqItem}";

    private static bool LooksLikeMarketPurchasePrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("purchase", StringComparison.OrdinalIgnoreCase) ||
               (text.Contains("buy", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("gil", StringComparison.OrdinalIgnoreCase));
    }

    private static bool Matches(
        MarketBoardListing listing,
        uint activeSearchItemId,
        MarketBoardPurchaseCandidate candidate,
        MarketBoardLiveListing freshListing) =>
        MatchesListingForActiveSearch(
            activeSearchItemId,
            listing.ItemId,
            listing.ListingId.ToString(),
            listing.RetainerId.ToString(),
            listing.UnitPrice,
            listing.Quantity,
            listing.IsHqItem,
            candidate,
            freshListing);

    internal static bool MatchesListingForActiveSearch(
        uint activeSearchItemId,
        uint rawListingItemId,
        string listingId,
        string retainerId,
        uint unitPrice,
        uint quantity,
        bool isHq,
        MarketBoardPurchaseCandidate candidate,
        MarketBoardLiveListing freshListing)
    {
        _ = rawListingItemId;

        return MarketBoardListingIntegrity.HasRealListingIdentity(listingId, retainerId, unitPrice, quantity) &&
               MarketBoardListingIntegrity.IsRealCandidate(candidate) &&
               MarketBoardListingIntegrity.IsRealListing(freshListing) &&
               activeSearchItemId == candidate.ItemId &&
               activeSearchItemId == freshListing.ItemId &&
               listingId.Equals(candidate.ListingId, StringComparison.Ordinal) &&
               listingId.Equals(freshListing.ListingId, StringComparison.Ordinal) &&
               retainerId.Equals(candidate.RetainerId, StringComparison.Ordinal) &&
               retainerId.Equals(freshListing.RetainerId, StringComparison.Ordinal) &&
               unitPrice == candidate.UnitPrice &&
               unitPrice == freshListing.UnitPrice &&
               quantity == candidate.Quantity &&
               quantity == freshListing.Quantity &&
               isHq == candidate.IsHq &&
               isHq == freshListing.IsHq;
    }

    private static MarketBoardPurchaseResult Fail(
        string status,
        string message,
        MarketBoardPurchaseCandidate candidate,
        IReadOnlyDictionary<string, string>? diagnostics = null) =>
        new()
        {
            Status = status,
            Message = message,
            Candidate = candidate,
            Diagnostics = diagnostics ?? new Dictionary<string, string>(),
        };
}
