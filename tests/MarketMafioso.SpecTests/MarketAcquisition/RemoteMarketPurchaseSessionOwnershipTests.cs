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

    [Fact]
    public void Closing_market_agent_restores_ordinary_native_purchase_sends()
    {
        var ownership = new RemoteMarketPurchaseSessionOwnership();
        ownership.ObserveRemoteOpen(agentWasActive: false, agentIsActive: true);

        ownership.ObserveMarketAgentActive(false);

        Assert.False(ownership.IsRemoteSessionActive);
        Assert.False(ownership.ShouldBlockInterceptedSend);
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
