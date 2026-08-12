namespace MarketMafioso.SpecTests;

public sealed class InventoryDeliveryCoalescerTests
{
    [Fact]
    public async Task Notify_CoalescesBurstIntoOneNewestDelivery()
    {
        var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var coalescer = new InventoryDeliveryCoalescer(
            TimeSpan.FromMilliseconds(25),
            _ =>
            {
                Interlocked.Increment(ref count);
                delivery.TrySetResult();
                return Task.CompletedTask;
            },
            (_, exception) => Assert.Null(exception));

        coalescer.Notify("player change 1");
        await Task.Delay(5);
        coalescer.Notify("player change 2");
        await Task.Delay(5);
        coalescer.Notify("Quartermaster revision 3");

        await delivery.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task Dispose_CancelsPendingDelivery()
    {
        var count = 0;
        var coalescer = new InventoryDeliveryCoalescer(
            TimeSpan.FromMilliseconds(25),
            _ =>
            {
                Interlocked.Increment(ref count);
                return Task.CompletedTask;
            },
            (_, exception) => Assert.Null(exception));

        coalescer.Notify("player change");
        coalescer.Dispose();
        await Task.Delay(75);

        Assert.Equal(0, Volatile.Read(ref count));
    }
}
