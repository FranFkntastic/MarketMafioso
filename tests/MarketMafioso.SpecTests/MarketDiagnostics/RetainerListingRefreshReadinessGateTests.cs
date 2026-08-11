using MarketMafioso.MarketDiagnostics;

namespace MarketMafioso.SpecTests.MarketDiagnostics;

public sealed class RetainerListingRefreshReadinessGateTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 11, 4, 55, 38, TimeSpan.Zero);

    [Fact]
    public void Ready_requires_both_quiet_time_and_consecutive_framework_frames()
    {
        var gate = new RetainerListingRefreshReadinessGate(TimeSpan.FromSeconds(3), 3);

        Assert.False(gate.Observe(Start, true, null).Ready);
        Assert.False(gate.Observe(Start.AddSeconds(1), true, null).Ready);
        Assert.False(gate.Observe(Start.AddSeconds(2), true, null).Ready);

        var settled = gate.Observe(Start.AddSeconds(3), true, null);
        Assert.True(settled.Ready);
        Assert.Null(settled.Reason);
    }

    [Fact]
    public void Unsafe_transition_resets_the_entire_quiet_window()
    {
        var gate = new RetainerListingRefreshReadinessGate(TimeSpan.FromSeconds(3), 3);

        Assert.False(gate.Observe(Start, true, null).Ready);
        Assert.False(gate.Observe(Start.AddSeconds(1), true, null).Ready);

        var blocked = gate.Observe(Start.AddSeconds(2), false, "The retainer session is still active.");
        Assert.False(blocked.Ready);
        Assert.Equal("The retainer session is still active.", blocked.Reason);

        Assert.False(gate.Observe(Start.AddSeconds(3), true, null).Ready);
        Assert.False(gate.Observe(Start.AddSeconds(5), true, null).Ready);
        Assert.True(gate.Observe(Start.AddSeconds(6), true, null).Ready);
    }

    [Fact]
    public void Clock_regression_starts_a_fresh_quiet_window()
    {
        var gate = new RetainerListingRefreshReadinessGate(TimeSpan.FromSeconds(3), 2);

        Assert.False(gate.Observe(Start, true, null).Ready);
        Assert.False(gate.Observe(Start.AddSeconds(-1), true, null).Ready);
        Assert.False(gate.Observe(Start.AddSeconds(1), true, null).Ready);
        Assert.True(gate.Observe(Start.AddSeconds(2), true, null).Ready);
    }
}
