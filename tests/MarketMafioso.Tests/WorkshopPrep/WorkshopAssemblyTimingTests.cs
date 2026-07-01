using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopAssemblyTimingTests
{
    [Fact]
    public void PostContributionLockout_starts_conservative()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), WorkshopAssemblyTiming.PostContributionLockout);
    }

    [Fact]
    public void AddonTimeout_allows_visible_state_diagnostics_before_failure()
    {
        Assert.True(WorkshopAssemblyTiming.AddonTimeout > WorkshopAssemblyTiming.PostContributionLockout);
    }
}
