using Franthropy.Dalamud.Automation.Retainers;
using MarketMafioso.MarketDiagnostics;

namespace MarketMafioso.Tests.MarketDiagnostics;

public sealed class AutoRetainerSuppressionLeaseTests
{
    [Fact]
    public void Acquire_WhenUnsuppressed_SuppressesThenRestores()
    {
        var autoRetainer = new FakeAutoRetainer { IsAvailable = true };

        Assert.True(AutoRetainerSuppressionLease.TryAcquire(autoRetainer, out var lease, out _));
        Assert.NotNull(lease);
        Assert.True(autoRetainer.IsSuppressed);
        Assert.Equal([true], autoRetainer.SuppressionChanges);

        lease.Dispose();

        Assert.False(autoRetainer.IsSuppressed);
        Assert.True(lease.Restored);
        Assert.Equal([true, false], autoRetainer.SuppressionChanges);
    }

    [Fact]
    public void Acquire_WhenAlreadySuppressed_PreservesPriorState()
    {
        var autoRetainer = new FakeAutoRetainer { IsAvailable = true, IsSuppressed = true };

        Assert.True(AutoRetainerSuppressionLease.TryAcquire(autoRetainer, out var lease, out _));
        lease!.Dispose();

        Assert.True(autoRetainer.IsSuppressed);
        Assert.True(lease.Restored);
        Assert.Empty(autoRetainer.SuppressionChanges);
    }

    [Fact]
    public void Acquire_WhenBusy_RefusesWithoutChangingSuppression()
    {
        var autoRetainer = new FakeAutoRetainer { IsAvailable = true, IsBusy = true };

        Assert.False(AutoRetainerSuppressionLease.TryAcquire(autoRetainer, out var lease, out var message));

        Assert.Null(lease);
        Assert.Contains("busy", message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(autoRetainer.SuppressionChanges);
    }

    [Fact]
    public void Acquire_WhenUnavailable_RequiresNoSuppression()
    {
        var autoRetainer = new FakeAutoRetainer();

        Assert.True(AutoRetainerSuppressionLease.TryAcquire(autoRetainer, out var lease, out _));
        lease!.Dispose();

        Assert.True(lease.Restored);
        Assert.Empty(autoRetainer.SuppressionChanges);
    }

    private sealed class FakeAutoRetainer : IAutoRetainerIpc
    {
        public bool IsAvailable { get; set; }
        public bool IsBusy { get; set; }
        public bool IsSuppressed { get; set; }
        public List<bool> SuppressionChanges { get; } = [];

        public void SetSuppressed(bool suppressed)
        {
            IsSuppressed = suppressed;
            SuppressionChanges.Add(suppressed);
        }

        public void Register(AutoRetainerIpcCallbacks callbacks) { }
        public void QueueRetainerListTask(string consumer) { }
        public void RequestPostprocess(string consumer) { }
        public void FinishPostprocess() { }
        public void Dispose() { }
    }
}
