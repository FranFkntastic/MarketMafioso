#if !DEBUG
using System.Reflection;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Windows;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterDryRunReleaseBoundaryTests
{
    [Fact]
    public void ReleaseAssemblyContainsNoDebugSeedTypeOrMainWindowAction()
    {
        var assembly = typeof(OutfitterRouteExecutionState).Assembly;

        Assert.Null(assembly.GetType(
            "MarketMafioso.Squire.Outfitter.Acquisition.OutfitterDryRunSunkStateSeeder",
            throwOnError: false));
        Assert.Null(assembly.GetType(
            "MarketMafioso.Squire.Outfitter.Utility.MinerBotanistAdvisorSyntheticReview",
            throwOnError: false));
        Assert.NotNull(assembly.GetType(
            "MarketMafioso.Squire.Outfitter.Acquisition.OutfitterDryRunPreparedPlanRestorer",
            throwOnError: false));
        Assert.Null(typeof(MainWindow).GetMethod(
            "SeedOutfitterDryRunSunkState",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }
}
#endif
