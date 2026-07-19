using System.Diagnostics;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class GenerationBoundComputationTests
{
    [Fact]
    public void Poll_DoesNotBlockFrameworkWhileWorkerIsBlocked()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var computation = new GenerationBoundComputation<int>();
        computation.Start(7, _ =>
        {
            release.Wait();
            return 42;
        }, cancellation.Token);
        var stopwatch = Stopwatch.StartNew();

        var result = computation.Poll(7);

        stopwatch.Stop();
        Assert.Equal(GenerationBoundComputationStatus.Pending, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250), $"Framework poll blocked for {stopwatch.Elapsed}.");
        release.Set();
    }

    [Fact]
    public async Task Invalidate_IgnoresLateCompletionFromCancelledGeneration()
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var computation = new GenerationBoundComputation<int>();
        computation.Start(7, _ =>
        {
            release.Wait();
            return 42;
        }, cancellation.Token);

        cancellation.Cancel();
        computation.Invalidate();
        release.Set();
        await Task.Delay(50);

        Assert.Equal(GenerationBoundComputationStatus.None, computation.Poll(8).Status);
    }
}
