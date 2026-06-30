namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class DalamudMarketBoardPurchaseAdapterTests
{
    [Fact]
    public void MatchesListingForActiveSearch_IgnoresStaleRawRowItemIdWhenListingIdentityMatches()
    {
        var candidate = CreateCandidate(itemId: 5066);
        var freshListing = CreateFreshListing(itemId: 5066);

        var matches = MarketMafioso.MarketAcquisition.DalamudMarketBoardPurchaseAdapter.MatchesListingForActiveSearch(
            activeSearchItemId: 5066,
            rawListingItemId: 5121,
            listingId: "listing-1",
            retainerId: "retainer-1",
            unitPrice: 800,
            quantity: 4,
            isHq: false,
            candidate,
            freshListing);

        Assert.True(matches);
    }

    [Fact]
    public void MatchesListingForActiveSearch_RejectsWrongActiveSearchItem()
    {
        var candidate = CreateCandidate(itemId: 5066);
        var freshListing = CreateFreshListing(itemId: 5066);

        var matches = MarketMafioso.MarketAcquisition.DalamudMarketBoardPurchaseAdapter.MatchesListingForActiveSearch(
            activeSearchItemId: 5121,
            rawListingItemId: 5121,
            listingId: "listing-1",
            retainerId: "retainer-1",
            unitPrice: 800,
            quantity: 4,
            isHq: false,
            candidate,
            freshListing);

        Assert.False(matches);
    }

    [Fact]
    public void MatchesListingForActiveSearch_RejectsDefaultListingIdentity()
    {
        var candidate = CreateCandidate(itemId: 5066) with
        {
            ListingId = "0",
            RetainerId = "0",
            UnitPrice = 0,
            Quantity = 0,
        };
        var freshListing = CreateFreshListing(itemId: 5066) with
        {
            ListingId = "0",
            RetainerId = "0",
            UnitPrice = 0,
            Quantity = 0,
        };

        var matches = MarketMafioso.MarketAcquisition.DalamudMarketBoardPurchaseAdapter.MatchesListingForActiveSearch(
            activeSearchItemId: 5066,
            rawListingItemId: 0,
            listingId: "0",
            retainerId: "0",
            unitPrice: 0,
            quantity: 0,
            isHq: false,
            candidate,
            freshListing);

        Assert.False(matches);
    }

    [Fact]
    public void IsBetterListingListCandidate_AllowsVisibleListForOffscreenAbsoluteRow()
    {
        Assert.True(
            MarketMafioso.MarketAcquisition.MarketBoardListingListProbe.IsBetterListingListCandidate(
                itemCount: 9,
                isInteractive: true,
                bestItemCount: 0,
                bestIsInteractive: false));
    }

    [Fact]
    public void IsBetterListingListCandidate_KeepsLargestVisibleListCandidate()
    {
        Assert.False(
            MarketMafioso.MarketAcquisition.MarketBoardListingListProbe.IsBetterListingListCandidate(
                itemCount: 3,
                isInteractive: true,
                bestItemCount: 9,
                bestIsInteractive: true));
    }

    private static MarketMafioso.MarketAcquisition.MarketBoardPurchaseCandidate CreateCandidate(uint itemId) =>
        new()
        {
            ItemId = itemId,
            WorldName = "Siren",
            ListingId = "listing-1",
            RetainerId = "retainer-1",
            RetainerName = "Darkwinds",
            UnitPrice = 800,
            Quantity = 4,
            IsHq = false,
        };

    private static MarketMafioso.MarketAcquisition.MarketBoardLiveListing CreateFreshListing(uint itemId) =>
        new()
        {
            ItemId = itemId,
            RawItemId = 5121,
            WorldName = "Siren",
            ListingId = "listing-1",
            RetainerId = "retainer-1",
            RetainerName = "Darkwinds",
            UnitPrice = 800,
            Quantity = 4,
            IsHq = false,
        };
}
