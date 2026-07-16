using MarketMafioso.Dashboard;

namespace MarketMafioso.Server.Tests;

public sealed class InventoryNavigationTests
{
    [Fact]
    public void BuildPath_PreservesTheDashboardBasePath()
    {
        var path = InventoryNavigation.BuildPath(
            InventoryBrowserMode.Stacks,
            "retainer:Sample Retainer",
            "quality:hq & condition:<50",
            3162);

        Assert.Equal(
            "inventory?mode=Stacks&scope=retainer%3ASample%20Retainer&filter=quality%3Ahq%20%26%20condition%3A%3C50&characterId=3162",
            path);
        Assert.False(path.StartsWith('/'));
    }
}
