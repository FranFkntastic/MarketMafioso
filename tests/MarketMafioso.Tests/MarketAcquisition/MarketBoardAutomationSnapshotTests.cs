namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardAutomationSnapshotTests
{
    [Fact]
    public void Create_SearchSnapshotPreservesStructuredFields()
    {
        var snapshot = MarketMafioso.Automation.MarketBoard.MarketBoardAutomationSnapshot.Create(
            "SearchItem",
            "AfterInput",
            "ItemSearchResultReady",
            "SearchSent",
            MarketMafioso.Automation.MarketBoard.MarketBoardAutomationOutcome.Recoverable,
            "RetryHumanEnterPath",
            new Dictionary<string, string?>
            {
                ["searchText"] = "Varnish",
                ["searchButtonEnabled"] = false.ToString(),
                ["blank"] = null,
            });

        Assert.Equal("SearchItem", snapshot.Step);
        Assert.Equal("AfterInput", snapshot.Phase);
        Assert.Equal("ItemSearchResultReady", snapshot.Expected);
        Assert.Equal("SearchSent", snapshot.Observed);
        Assert.Equal(MarketMafioso.Automation.MarketBoard.MarketBoardAutomationOutcome.Recoverable, snapshot.Outcome);
        Assert.Equal("RetryHumanEnterPath", snapshot.NextAction);
        Assert.Equal("Varnish", snapshot.Details["searchText"]);
        Assert.Equal(false.ToString(), snapshot.Details["searchButtonEnabled"]);
        Assert.False(snapshot.Details.ContainsKey("blank"));
    }

    [Fact]
    public void ToDetails_IncludesCoreClassificationBeforeObservedDetails()
    {
        var snapshot = MarketMafioso.Automation.MarketBoard.MarketBoardAutomationSnapshot.Create(
            "BuyListing",
            "AfterConfirmation",
            "ListingRemoved",
            "MarketBoardNotOpen",
            MarketMafioso.Automation.MarketBoard.MarketBoardAutomationOutcome.ExpectedAlternate,
            "TreatListingAsRemoved",
            new Dictionary<string, string?>
            {
                ["candidateListingId"] = "123",
            });

        var details = snapshot.ToDetails();

        Assert.Equal("BuyListing", details["step"]);
        Assert.Equal("AfterConfirmation", details["phase"]);
        Assert.Equal("ListingRemoved", details["expected"]);
        Assert.Equal("MarketBoardNotOpen", details["observed"]);
        Assert.Equal("ExpectedAlternate", details["outcome"]);
        Assert.Equal("TreatListingAsRemoved", details["nextAction"]);
        Assert.Equal("123", details["candidateListingId"]);
    }
}

