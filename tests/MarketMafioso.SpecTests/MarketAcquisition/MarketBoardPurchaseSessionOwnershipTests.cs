using Dalamud.Game.Addon.Events;
using MarketMafioso.MarketAcquisition.MarketBoard;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketBoardPurchaseSessionOwnershipTests
{
    [Fact]
    public void Owned_listing_session_blocks_intercepted_native_purchase_sends()
    {
        var ownership = new MarketBoardPurchaseSessionOwnership();

        ownership.ObserveAcquisitionOpen(agentWasActive: false, agentIsActive: true);

        Assert.True(ownership.IsAcquisitionSessionActive);
        Assert.True(ownership.ShouldBlockInterceptedSend);
    }

    [Theory]
    [InlineData(AddonEventType.ListButtonPress)]
    [InlineData(AddonEventType.ListItemClick)]
    [InlineData(AddonEventType.ListItemDoubleClick)]
    [InlineData(AddonEventType.ListItemSelect)]
    public void Owned_listing_session_blocks_native_result_activation_before_confirmation(AddonEventType eventType)
    {
        var ownership = new MarketBoardPurchaseSessionOwnership();
        ownership.ObserveAcquisitionOpen(agentWasActive: false, agentIsActive: true);

        Assert.True(ownership.ShouldBlockNativeListingActivation(eventType));
    }

    [Theory]
    [InlineData(AddonEventType.MouseWheel)]
    [InlineData(AddonEventType.ListItemRollOver)]
    [InlineData(AddonEventType.ListItemRollOut)]
    [InlineData(AddonEventType.ButtonClick)]
    public void Owned_listing_session_preserves_non_activation_result_events(AddonEventType eventType)
    {
        var ownership = new MarketBoardPurchaseSessionOwnership();
        ownership.ObserveAcquisitionOpen(agentWasActive: false, agentIsActive: true);

        Assert.False(ownership.ShouldBlockNativeListingActivation(eventType));
    }

    [Fact]
    public void Closing_market_agent_restores_ordinary_native_purchase_sends()
    {
        var ownership = new MarketBoardPurchaseSessionOwnership();
        ownership.ObserveAcquisitionOpen(agentWasActive: false, agentIsActive: true);

        ownership.ObserveMarketAgentActive(false);

        Assert.False(ownership.IsAcquisitionSessionActive);
        Assert.False(ownership.ShouldBlockInterceptedSend);
        Assert.False(ownership.ShouldBlockNativeListingActivation(AddonEventType.ListItemClick));
    }

    [Fact]
    public void Active_market_agent_does_not_create_acquisition_ownership_by_itself()
    {
        var ownership = new MarketBoardPurchaseSessionOwnership();

        ownership.ObserveMarketAgentActive(true);

        Assert.False(ownership.IsAcquisitionSessionActive);
        Assert.False(ownership.ShouldBlockInterceptedSend);
    }

    [Fact]
    public void Already_open_physical_board_is_not_claimed_as_an_acquisition_session()
    {
        var ownership = new MarketBoardPurchaseSessionOwnership();

        ownership.ObserveAcquisitionOpen(agentWasActive: true, agentIsActive: true);

        Assert.False(ownership.IsAcquisitionSessionActive);
        Assert.False(ownership.ShouldBlockInterceptedSend);
    }
}
