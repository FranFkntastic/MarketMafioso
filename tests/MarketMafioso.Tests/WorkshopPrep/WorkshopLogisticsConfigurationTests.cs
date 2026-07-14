namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopLogisticsConfigurationTests
{
    [Fact]
    public void QueueAndMaterials_AreCombinedByDefault()
    {
        var config = new Configuration();

        Assert.False(config.SplitWorkshopQueueAndMaterials);
    }
}
