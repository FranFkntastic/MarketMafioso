using Dalamud.Game.Addon.Events;
using MarketMafioso.MarketAcquisition.RemoteMarket;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class RemoteMarketPurchaseSessionOwnershipTests
{
    [Fact]
    public void Remote_session_blocks_intercepted_native_purchase_sends()
    {
        var ownership = new RemoteMarketPurchaseSessionOwnership();

        ownership.ObserveRemoteOpen(agentWasActive: false, agentIsActive: true);

        Assert.True(ownership.IsRemoteSessionActive);
        Assert.True(ownership.ShouldBlockInterceptedSend);
    }

    [Theory]
    [InlineData(AddonEventType.ListButtonPress)]
    [InlineData(AddonEventType.ListItemClick)]
    [InlineData(AddonEventType.ListItemDoubleClick)]
    [InlineData(AddonEventType.ListItemSelect)]
    public void Remote_session_blocks_native_result_activation_before_confirmation(AddonEventType eventType)
    {
        var ownership = new RemoteMarketPurchaseSessionOwnership();
        ownership.ObserveRemoteOpen(agentWasActive: false, agentIsActive: true);

        Assert.True(ownership.ShouldBlockNativeListingActivation(eventType));
    }

    [Theory]
    [InlineData(AddonEventType.MouseWheel)]
    [InlineData(AddonEventType.ListItemRollOver)]
    [InlineData(AddonEventType.ListItemRollOut)]
    [InlineData(AddonEventType.ButtonClick)]
    public void Remote_session_preserves_non_activation_result_events(AddonEventType eventType)
    {
        var ownership = new RemoteMarketPurchaseSessionOwnership();
        ownership.ObserveRemoteOpen(agentWasActive: false, agentIsActive: true);

        Assert.False(ownership.ShouldBlockNativeListingActivation(eventType));
    }

    [Fact]
    public void Closing_market_agent_restores_ordinary_native_purchase_sends()
    {
        var ownership = new RemoteMarketPurchaseSessionOwnership();
        ownership.ObserveRemoteOpen(agentWasActive: false, agentIsActive: true);

        ownership.ObserveMarketAgentActive(false);

        Assert.False(ownership.IsRemoteSessionActive);
        Assert.False(ownership.ShouldBlockInterceptedSend);
        Assert.False(ownership.ShouldBlockNativeListingActivation(AddonEventType.ListItemClick));
    }

    [Fact]
    public void Active_market_agent_does_not_create_remote_ownership_by_itself()
    {
        var ownership = new RemoteMarketPurchaseSessionOwnership();

        ownership.ObserveMarketAgentActive(true);

        Assert.False(ownership.IsRemoteSessionActive);
        Assert.False(ownership.ShouldBlockInterceptedSend);
    }

    [Fact]
    public void Already_open_physical_board_is_not_claimed_as_a_remote_session()
    {
        var ownership = new RemoteMarketPurchaseSessionOwnership();

        ownership.ObserveRemoteOpen(agentWasActive: true, agentIsActive: true);

        Assert.False(ownership.IsRemoteSessionActive);
        Assert.False(ownership.ShouldBlockInterceptedSend);
    }
}
