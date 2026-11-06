using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopRestockPersistenceTests
{
    [Fact]
    public void Requests_coalesce_and_do_not_write_until_framework_flush()
    {
        var saves = 0;
        var queue = new WorkshopRestockPersistence(() => saves++, _ => { });
        queue.Request();
        queue.Request();
        Assert.Equal(0, saves);
        queue.Flush();
        queue.Flush();
        Assert.Equal(1, saves);
        Assert.Null(queue.Error);
    }

    [Fact]
    public void Failed_checkpoint_is_reported_without_throwing_or_unbounded_retry()
    {
        var saves = 0;
        var failures = 0;
        var queue = new WorkshopRestockPersistence(() =>
        {
            if (++saves == 1) throw new IOException("locked checkpoint");
        }, _ => failures++);
        queue.Request();
        queue.Flush();
        Assert.Contains("locked checkpoint", queue.Error);
        queue.Flush();
        Assert.Equal(1, saves);
        Assert.Equal(1, failures);
        queue.Request();
        queue.Flush();
        Assert.Null(queue.Error);
        Assert.Equal(2, saves);
    }
}
