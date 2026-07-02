using System.Numerics;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class VNavmeshIpcTests
{
    [Fact]
    public void IsReady_ReturnsFalseWhenAdapterUnavailable()
    {
        var ipc = new VNavmeshIpc(new FakeAdapter(isAvailable: false, isReady: true));

        Assert.False(ipc.IsReady);
    }

    [Fact]
    public void IsReady_ReturnsAdapterReadinessWhenAvailable()
    {
        var ipc = new VNavmeshIpc(new FakeAdapter(isAvailable: true, isReady: true));

        Assert.True(ipc.IsReady);
    }

    [Fact]
    public void IsRunning_ReturnsFalseWhenAdapterUnavailable()
    {
        var ipc = new VNavmeshIpc(new FakeAdapter(isAvailable: false, isRunning: true));

        Assert.False(ipc.IsRunning);
    }

    [Fact]
    public void MoveCloseTo_ReturnsUnavailableWhenAdapterIsMissing()
    {
        var ipc = new VNavmeshIpc(new FakeAdapter(isAvailable: false, isReady: true, moveResult: true));

        var result = ipc.MoveCloseTo(Vector3.One, 5);

        Assert.False(result.Success);
        Assert.Contains("not loaded", result.Message);
    }

    [Fact]
    public void MoveCloseTo_ReturnsNotReadyWhenNavmeshIsNotReady()
    {
        var ipc = new VNavmeshIpc(new FakeAdapter(isAvailable: true, isReady: false, moveResult: true));

        var result = ipc.MoveCloseTo(Vector3.One, 5);

        Assert.False(result.Success);
        Assert.Contains("not ready", result.Message);
    }

    [Fact]
    public void MoveCloseTo_ReturnsSuccessWhenAdapterAcceptsMove()
    {
        var adapter = new FakeAdapter(isAvailable: true, isReady: true, moveResult: true);
        var ipc = new VNavmeshIpc(adapter);

        var result = ipc.MoveCloseTo(new Vector3(1, 2, 3), 5);

        Assert.True(result.Success);
        Assert.Equal(new Vector3(1, 2, 3), adapter.LastDestination);
        Assert.Equal(5, adapter.LastRange);
    }

    [Fact]
    public void Stop_DoesNothingWhenAdapterUnavailable()
    {
        var adapter = new FakeAdapter(isAvailable: false);
        var ipc = new VNavmeshIpc(adapter);

        ipc.Stop();

        Assert.False(adapter.StopCalled);
    }

    private sealed class FakeAdapter : IVNavmeshIpcAdapter
    {
        private readonly bool isReady;
        private readonly bool isRunning;
        private readonly bool moveResult;

        public FakeAdapter(
            bool isAvailable,
            bool isReady = false,
            bool isRunning = false,
            bool moveResult = false)
        {
            IsAvailable = isAvailable;
            this.isReady = isReady;
            this.isRunning = isRunning;
            this.moveResult = moveResult;
        }

        public bool IsAvailable { get; }
        public Vector3? LastDestination { get; private set; }
        public float? LastRange { get; private set; }
        public bool StopCalled { get; private set; }

        public bool IsReady()
        {
            return isReady;
        }

        public bool IsRunning()
        {
            return isRunning;
        }

        public bool MoveCloseTo(Vector3 destination, float range)
        {
            LastDestination = destination;
            LastRange = range;
            return moveResult;
        }

        public void Stop()
        {
            StopCalled = true;
        }
    }
}
