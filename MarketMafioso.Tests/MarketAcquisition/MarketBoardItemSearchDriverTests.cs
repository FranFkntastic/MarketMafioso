using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardItemSearchDriverTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(6u)]
    [InlineData(7u)]
    public void ShouldResetToNormalSearch_ReturnsTrueForNonNormalModes(uint mode)
    {
        Assert.True(MarketBoardItemSearchDriver.ShouldResetToNormalSearch(mode));
    }

    [Fact]
    public void ShouldResetToNormalSearch_ReturnsFalseForNormalMode()
    {
        Assert.False(MarketBoardItemSearchDriver.ShouldResetToNormalSearch(0));
    }

    [Fact]
    public void ChooseAction_ReturnsResetModeForWishlistMode()
    {
        Assert.Equal(MarketBoardItemSearchAction.ResetMode, MarketBoardItemSearchDriver.ChooseAction(5));
    }

    [Fact]
    public void ChooseAction_ReturnsSubmitSearchForNormalMode()
    {
        Assert.Equal(MarketBoardItemSearchAction.SubmitSearch, MarketBoardItemSearchDriver.ChooseAction(0));
    }

    [Fact]
    public void ShouldDisablePartialSearch_ReturnsFalseWhenPartialSearchIsEnabled()
    {
        Assert.False(MarketBoardItemSearchDriver.ShouldDisablePartialSearch(true));
    }

    [Fact]
    public void ShouldDisablePartialSearch_ReturnsFalseWhenPartialSearchIsAlreadyDisabled()
    {
        Assert.False(MarketBoardItemSearchDriver.ShouldDisablePartialSearch(false));
    }

    [Fact]
    public void IsSubmittedSearchCurrent_MatchesSameItemAndTrimmedSearchText()
    {
        Assert.True(MarketBoardItemSearchDriver.IsSubmittedSearchCurrent(7017, "Varnish", 7017, " Varnish "));
    }

    [Fact]
    public void IsSubmittedSearchCurrent_ReturnsFalseForDifferentSearchText()
    {
        Assert.False(MarketBoardItemSearchDriver.IsSubmittedSearchCurrent(7017, "Varnish", 7017, "Manor Varnish"));
    }

    [Fact]
    public void IsSubmittedSearchCurrent_ReturnsFalseForDifferentItem()
    {
        Assert.False(MarketBoardItemSearchDriver.IsSubmittedSearchCurrent(7017, "Varnish", 7808, "Varnish"));
    }

    [Fact]
    public void ShouldWaitForSubmittedSearch_ReturnsFalseWhenAgentIsIdleAndExactItemIsMissingAfterRetryDelay()
    {
        Assert.False(MarketBoardItemSearchDriver.ShouldWaitForSubmittedSearch(
            searchMatches: true,
            exactItemVisible: false,
            agentIsPartialSearching: false,
            agentIsItemPushPending: false,
            elapsedSinceSubmit: TimeSpan.FromSeconds(4),
            retryDelay: TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void ShouldWaitForSubmittedSearch_ReturnsTrueWhenSubmittedSearchIsStillSettling()
    {
        Assert.True(MarketBoardItemSearchDriver.ShouldWaitForSubmittedSearch(
            searchMatches: true,
            exactItemVisible: false,
            agentIsPartialSearching: false,
            agentIsItemPushPending: false,
            elapsedSinceSubmit: TimeSpan.FromSeconds(2),
            retryDelay: TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void SubmittedSearchRetryDelay_IsShortEnoughForRouteAutomation()
    {
        Assert.True(MarketBoardItemSearchDriver.SubmittedSearchRetryDelay <= TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public void ShouldWaitForSubmittedSearch_ReturnsTrueWhenAgentIsStillSearching()
    {
        Assert.True(MarketBoardItemSearchDriver.ShouldWaitForSubmittedSearch(
            searchMatches: true,
            exactItemVisible: false,
            agentIsPartialSearching: true,
            agentIsItemPushPending: false,
            elapsedSinceSubmit: TimeSpan.FromSeconds(4),
            retryDelay: TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void ShouldWaitForSubmittedSearch_ReturnsTrueWhenExactItemIsVisible()
    {
        Assert.True(MarketBoardItemSearchDriver.ShouldWaitForSubmittedSearch(
            searchMatches: true,
            exactItemVisible: true,
            agentIsPartialSearching: false,
            agentIsItemPushPending: false,
            elapsedSinceSubmit: TimeSpan.FromSeconds(4),
            retryDelay: TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void IsOpenListingResultForRequestedItem_ReturnsTrueForMatchingListingItem()
    {
        Assert.True(MarketBoardItemSearchDriver.IsOpenListingResultForRequestedItem(5118, 5118));
    }

    [Theory]
    [InlineData(5118u, 12551u)]
    [InlineData(5118u, 0u)]
    [InlineData(0u, 5118u)]
    public void IsOpenListingResultForRequestedItem_ReturnsFalseForStaleOrMissingListingItem(uint requestedItemId, uint openResultItemId)
    {
        Assert.False(MarketBoardItemSearchDriver.IsOpenListingResultForRequestedItem(requestedItemId, openResultItemId));
    }

    [Fact]
    public void GetSearchSubmitCallbackSequence_PrimesInputBeforeEnter()
    {
        Assert.Equal(
            [MarketBoardItemSearchSubmitCallback.TextChanged, MarketBoardItemSearchSubmitCallback.Enter],
            MarketBoardItemSearchDriver.GetSearchSubmitCallbackSequence());
    }

    [Fact]
    public void ChooseSearchSubmitStrategy_UsesAutofocusedRewriteWhenFocusedInputIsIdle()
    {
        Assert.Equal(
            MarketBoardItemSearchSubmitStrategy.AutofocusedTextInputRewrite,
            MarketBoardItemSearchDriver.ChooseSearchSubmitStrategy(
                textInputWasActive: true,
                searchButtonWasEnabled: false,
                exactItemVisible: false,
                agentIsPartialSearching: false,
                agentIsItemPushPending: false));
    }

    [Fact]
    public void ChooseSearchSubmitStrategy_UsesNormalCallbackWhenInputIsNotAlreadyFocused()
    {
        Assert.Equal(
            MarketBoardItemSearchSubmitStrategy.TextInputEnterCallback,
            MarketBoardItemSearchDriver.ChooseSearchSubmitStrategy(
                textInputWasActive: false,
                searchButtonWasEnabled: false,
                exactItemVisible: false,
                agentIsPartialSearching: false,
                agentIsItemPushPending: false));
    }

    [Fact]
    public void ChooseSearchSubmitStrategy_UsesNormalCallbackWhenSearchIsAlreadyInFlight()
    {
        Assert.Equal(
            MarketBoardItemSearchSubmitStrategy.TextInputEnterCallback,
            MarketBoardItemSearchDriver.ChooseSearchSubmitStrategy(
                textInputWasActive: true,
                searchButtonWasEnabled: false,
                exactItemVisible: false,
                agentIsPartialSearching: true,
                agentIsItemPushPending: false));
    }

    [Fact]
    public void GetAutofocusedSubmitStepSequence_RewritesTextBeforeEnter()
    {
        Assert.Equal(
            [
                MarketBoardItemSearchSubmitStep.ClearSearchText,
                MarketBoardItemSearchSubmitStep.TextChanged,
                MarketBoardItemSearchSubmitStep.SetSearchText,
                MarketBoardItemSearchSubmitStep.TextChanged,
                MarketBoardItemSearchSubmitStep.Enter,
            ],
            MarketBoardItemSearchDriver.GetAutofocusedSubmitStepSequence());
    }

    [Fact]
    public void ShouldMirrorSubmitTextToAddonSearchStrings_DoesNotPrecommitEditableText()
    {
        Assert.False(MarketBoardItemSearchDriver.ShouldMirrorSubmitTextToAddonSearchStrings(
            MarketBoardItemSearchSubmitStrategy.TextInputEnterCallback));
        Assert.False(MarketBoardItemSearchDriver.ShouldMirrorSubmitTextToAddonSearchStrings(
            MarketBoardItemSearchSubmitStrategy.AutofocusedTextInputRewrite));
    }

    [Fact]
    public void IsSearchSubmitAccepted_ReturnsFalseWhenCallbacksLeaveSearchUiIdle()
    {
        Assert.False(MarketBoardItemSearchDriver.IsSearchSubmitAccepted(
            exactItemVisible: false,
            agentIsPartialSearching: false,
            agentIsItemPushPending: false,
            searchButtonClickSent: false,
            itemSearchResultVisible: false));
    }

    [Fact]
    public void IsSearchSubmitAccepted_ReturnsFalseWhenSearchButtonOnlyBecomesAvailable()
    {
        Assert.False(MarketBoardItemSearchDriver.IsSearchSubmitAccepted(
            exactItemVisible: false,
            agentIsPartialSearching: false,
            agentIsItemPushPending: false,
            searchButtonClickSent: false,
            itemSearchResultVisible: false));
    }

    [Fact]
    public void IsSearchSubmitAccepted_ReturnsTrueWhenSearchButtonClickWasSent()
    {
        Assert.True(MarketBoardItemSearchDriver.IsSearchSubmitAccepted(
            exactItemVisible: false,
            agentIsPartialSearching: false,
            agentIsItemPushPending: false,
            searchButtonClickSent: true,
            itemSearchResultVisible: false));
    }

    [Fact]
    public void GetResultActivationEventSequence_ActivatesRowsAsListItems()
    {
        Assert.Equal(
            [MarketBoardItemSearchResultActivationEvent.ListItemClick, MarketBoardItemSearchResultActivationEvent.ListItemDoubleClick],
            MarketBoardItemSearchDriver.GetResultActivationEventSequence());
    }

    [Fact]
    public void ChooseTextInputFocusTarget_PrefersCollisionNode()
    {
        Assert.Equal(
            MarketBoardItemSearchFocusTarget.CollisionNode,
            MarketBoardItemSearchDriver.ChooseTextInputFocusTarget(hasCollisionNode: true, hasOwnerNode: true));
    }

    [Fact]
    public void ChooseTextInputFocusTarget_FallsBackToOwnerNode()
    {
        Assert.Equal(
            MarketBoardItemSearchFocusTarget.OwnerNode,
            MarketBoardItemSearchDriver.ChooseTextInputFocusTarget(hasCollisionNode: false, hasOwnerNode: true));
    }
}
