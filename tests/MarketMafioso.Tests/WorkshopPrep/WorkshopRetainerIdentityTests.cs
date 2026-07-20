using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopRetainerIdentityTests
{
    [Fact]
    public void VerifyCandidateRetainerIdentity_RejectsSameNameRouteWhenStableIdsDiffer()
    {
        var result = WorkshopRetainerRestockDriver.VerifyCandidateRetainerIdentity(
            expectedRetainerId: 11,
            activeRetainerId: 22);

        Assert.False(result.Success);
        Assert.Contains("identity mismatch", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("11", result.Message);
        Assert.Contains("22", result.Message);
    }

    [Fact]
    public void VerifyCandidateRetainerIdentity_AcceptsMatchingStableId()
    {
        var result = WorkshopRetainerRestockDriver.VerifyCandidateRetainerIdentity(
            expectedRetainerId: 11,
            activeRetainerId: 11);

        Assert.True(result.Success);
    }
}
