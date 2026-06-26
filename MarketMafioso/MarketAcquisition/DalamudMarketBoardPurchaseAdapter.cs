using System;
using System.Linq;
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
    private static readonly uint[] CandidateListingListIds = Enumerable.Range(1, 80).Select(static id => (uint)id).ToArray();

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
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
        {
            return Fail(
                "MarketBoardNotOpen",
                "Market board listing results closed before the purchase click could be sent.",
                candidate);
        }

        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null)
        {
            return Fail(
                "InfoProxyUnavailable",
                "InfoProxyItemSearch is unavailable before the purchase click could be sent.",
                candidate);
        }

        if (!TryFindMatchingListing(infoProxy, candidate, freshListing, out var listingIndex, out var listing))
        {
            return Fail(
                "ListingMissing",
                "The exact guarded listing was not present in InfoProxyItemSearch at purchase time.",
                candidate);
        }

        if (!infoProxy->SetLastPurchasedItem(&listing))
        {
            return Fail(
                "SetLastPurchasedFailed",
                "The game rejected priming LastPurchasedMarketboardItem for the guarded listing.",
                candidate);
        }

        var listingList = FindListingList(addon, listingIndex);
        if (listingList == null)
        {
            return Fail(
                "ListingListUnavailable",
                $"Could not find a market-board listing list component containing row {listingIndex}.",
                candidate);
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
                candidate);
        }

        addon->AtkUnitBase.FireCallbackInt(0);
        log.Info(
            "[MarketMafioso] Accepted market board purchase confirmation for listing {ListingId} retainer {RetainerId}.",
            candidate.ListingId,
            candidate.RetainerId);

        return new MarketBoardPurchaseResult
        {
            Status = "ConfirmationAccepted",
            Message = $"Accepted market-board purchase confirmation: {text}",
            Candidate = candidate,
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
            if (!Matches(candidateListing, candidate, freshListing))
                continue;

            listingIndex = index;
            listing = candidateListing;
            return true;
        }

        return false;
    }

    private static unsafe AtkComponentList* FindListingList(AddonItemSearchResult* addon, int listingIndex)
    {
        foreach (var listId in CandidateListingListIds)
        {
            var list = addon->AtkUnitBase.GetComponentListById(listId);
            if (list == null)
                continue;

            if (list->GetItemCount() <= listingIndex)
                continue;

            if (!list->IsItemVisible(listingIndex, true))
                list->ScrollToItem((short)listingIndex);

            return list;
        }

        return null;
    }

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
        MarketBoardPurchaseCandidate candidate,
        MarketBoardLiveListing freshListing) =>
        listing.ItemId == candidate.ItemId &&
        listing.ItemId == freshListing.ItemId &&
        listing.ListingId.ToString().Equals(candidate.ListingId, StringComparison.Ordinal) &&
        listing.ListingId.ToString().Equals(freshListing.ListingId, StringComparison.Ordinal) &&
        listing.RetainerId.ToString().Equals(candidate.RetainerId, StringComparison.Ordinal) &&
        listing.RetainerId.ToString().Equals(freshListing.RetainerId, StringComparison.Ordinal) &&
        listing.UnitPrice == candidate.UnitPrice &&
        listing.UnitPrice == freshListing.UnitPrice &&
        listing.Quantity == candidate.Quantity &&
        listing.Quantity == freshListing.Quantity &&
        listing.IsHqItem == candidate.IsHq &&
        listing.IsHqItem == freshListing.IsHq;

    private static MarketBoardPurchaseResult Fail(
        string status,
        string message,
        MarketBoardPurchaseCandidate candidate) =>
        new()
        {
            Status = status,
            Message = message,
            Candidate = candidate,
        };
}
