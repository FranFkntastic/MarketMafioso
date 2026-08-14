using MarketMafioso.Quartermaster;

namespace MarketMafioso.SpecTests;

public sealed class QuartermasterChangeSchedulingTests
{
    [Theory]
    [InlineData(QuartermasterIpcClient.RetainerManagementChangedKind, true)]
    [InlineData("owner", true)]
    [InlineData(QuartermasterIpcClient.OperationChangedKind, false)]
    [InlineData(QuartermasterIpcClient.RetainerListingsChangedKind, false)]
    [InlineData("periodic", false)]
    [InlineData("opened", false)]
    [InlineData("state", true)]
    [InlineData("cache", true)]
    [InlineData(null, true)]
    public void OnlyManagementRelevantQuartermasterChangesScheduleInventoryReports(string? kind, bool expected)
    {
        Assert.Equal(expected, Plugin.ShouldScheduleInventoryReport(kind));
    }
}
