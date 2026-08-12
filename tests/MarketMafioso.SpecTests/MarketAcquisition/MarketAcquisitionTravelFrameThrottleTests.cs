using Franthropy.Dalamud.Runtime;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionTravelFrameThrottleTests
{
    [Fact]
    public void Acquire_owns_a_bounded_route_lease_until_explicit_release()
    {
        using var governor = new FramePacingGovernor();
        using var throttle = new MarketAcquisitionTravelFrameThrottle(governor);

        var acquired = throttle.TryAcquire("route-1", "Jenova", out var active, out var message);

        Assert.True(acquired, message);
        Assert.True(active.IsActive);
        Assert.Equal(30, active.MaximumFramesPerSecond);
        Assert.Equal("route-1", active.RouteRunId);
        Assert.Equal("Jenova", active.TargetWorld);
        Assert.NotNull(active.LeaseId);
        Assert.Equal(30, governor.Snapshot().EffectiveMaximumFramesPerSecond);

        var released = throttle.Release("Arrival");

        Assert.True(released.Released);
        Assert.Equal(active.LeaseId, released.LeaseId);
        Assert.False(throttle.Snapshot.IsActive);
        Assert.Equal("Arrival", throttle.Snapshot.LastReleaseReason);
        Assert.False(governor.Snapshot().IsActive);
    }

    [Fact]
    public void Duplicate_acquisition_fails_closed_without_replacing_the_owner()
    {
        using var governor = new FramePacingGovernor();
        using var throttle = new MarketAcquisitionTravelFrameThrottle(governor);
        Assert.True(throttle.TryAcquire("route-1", "Jenova", out var first, out _));

        var acquired = throttle.TryAcquire("route-2", "Sargatanas", out var retained, out var message);

        Assert.False(acquired);
        Assert.Contains("already active", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first.LeaseId, retained.LeaseId);
        Assert.Equal("route-1", retained.RouteRunId);
        Assert.Equal("Jenova", retained.TargetWorld);
    }

    [Fact]
    public void Dispose_releases_the_route_lease_and_refuses_future_travel()
    {
        using var governor = new FramePacingGovernor();
        var throttle = new MarketAcquisitionTravelFrameThrottle(governor);
        Assert.True(throttle.TryAcquire("route-1", "Jenova", out _, out _));

        throttle.Dispose();

        Assert.False(governor.Snapshot().IsActive);
        Assert.False(throttle.TryAcquire("route-2", "Siren", out var snapshot, out var message));
        Assert.False(snapshot.IsActive);
        Assert.Contains("disposed", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Route_wiring_acquires_before_command_and_releases_on_arrival_and_cleanup()
    {
        var source = ReadSource("src", "MarketMafioso", "MarketAcquisition", "MarketAcquisitionRouteEngine.cs");
        var acquire = source.IndexOf("travelFrameThrottle.TryAcquire(", StringComparison.Ordinal);
        var command = source.IndexOf("runner.PreparePendingStopForCurrentWorld(", acquire, StringComparison.Ordinal);

        Assert.True(acquire >= 0, "Route frame throttle acquisition was not found.");
        Assert.True(command > acquire, "The Lifestream command must be submitted after frame throttle acquisition.");
        Assert.Contains("ReleaseTravelFrameThrottle(\"ArrivalConfirmed\")", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseTravelFrameThrottle(terminalReason)", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseTravelFrameThrottle(\"LifestreamCommandRejected\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Framework_pacing_is_independent_of_window_visibility_and_runs_in_finally()
    {
        var source = ReadSource("src", "MarketMafioso", "Plugin.cs");
        var updateStart = source.IndexOf("private void OnFrameworkUpdate", StringComparison.Ordinal);
        var nextMethod = source.IndexOf("private static (bool Ready", updateStart, StringComparison.Ordinal);
        var update = source[updateStart..nextMethod];

        Assert.Contains("finally", update, StringComparison.Ordinal);
        Assert.Contains("framePacingGovernor.PaceFrame()", update, StringComparison.Ordinal);
        Assert.DoesNotContain("mainWindow.IsOpen", update, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. segments]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "MarketMafioso")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests", "MarketMafioso.SpecTests")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the MarketMafioso repository root.");
    }
}
