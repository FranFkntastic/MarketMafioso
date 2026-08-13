using System;
using Franthropy.Dalamud.Runtime;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionTravelFrameThrottleSnapshot(
    bool IsActive,
    int MaximumFramesPerSecond,
    string? LeaseId,
    string? RouteRunId,
    string? TargetWorld,
    long TotalDelayedFrames,
    TimeSpan TotalRequestedDelay,
    string? LastReleaseReason);

public sealed record MarketAcquisitionTravelFrameThrottleRelease(
    bool Released,
    int MaximumFramesPerSecond,
    string? LeaseId,
    string? RouteRunId,
    string? TargetWorld,
    string Reason);

public sealed class MarketAcquisitionTravelFrameThrottle : IDisposable
{
    // World-arrival asset submission still exceeded the GPU safety target at 30 FPS.
    // Travel is unattended and short-lived, so favor thermal/load containment over presentation smoothness.
    internal const int MaximumFramesPerSecond = 10;

    private readonly object sync = new();
    private readonly FramePacingGovernor governor;
    private IFramePacingLease? activeLease;
    private string? activeRouteRunId;
    private string? activeTargetWorld;
    private string? lastReleaseReason;
    private bool disposed;

    public MarketAcquisitionTravelFrameThrottle(FramePacingGovernor governor)
    {
        this.governor = governor ?? throw new ArgumentNullException(nameof(governor));
    }

    public bool TryAcquire(
        string routeRunId,
        string targetWorld,
        out MarketAcquisitionTravelFrameThrottleSnapshot snapshot,
        out string message)
    {
        if (string.IsNullOrWhiteSpace(routeRunId))
            throw new ArgumentException("A route run id is required.", nameof(routeRunId));
        if (string.IsNullOrWhiteSpace(targetWorld))
            throw new ArgumentException("A target world is required.", nameof(targetWorld));

        lock (sync)
        {
            if (disposed)
            {
                snapshot = CreateSnapshot();
                message = "The market acquisition travel frame throttle has been disposed.";
                return false;
            }

            if (activeLease?.IsActive == true)
            {
                snapshot = CreateSnapshot();
                message = $"A frame throttle lease is already active for route {activeRouteRunId} to {activeTargetWorld}.";
                return false;
            }

            try
            {
                activeLease = governor.Acquire(
                    $"MarketMafioso.MarketAcquisition:{routeRunId}:{targetWorld}",
                    MaximumFramesPerSecond);
                activeRouteRunId = routeRunId.Trim();
                activeTargetWorld = targetWorld.Trim();
                lastReleaseReason = null;
                snapshot = CreateSnapshot();
                message = $"Market acquisition travel frame throttle acquired at {MaximumFramesPerSecond} FPS.";
                return true;
            }
            catch (Exception ex)
            {
                activeLease = null;
                activeRouteRunId = null;
                activeTargetWorld = null;
                snapshot = CreateSnapshot();
                message = $"Unable to acquire the market acquisition travel frame throttle: {ex.Message}";
                return false;
            }
        }
    }

    public MarketAcquisitionTravelFrameThrottleRelease Release(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A release reason is required.", nameof(reason));

        IFramePacingLease? lease;
        MarketAcquisitionTravelFrameThrottleRelease result;
        lock (sync)
        {
            lease = activeLease;
            result = new MarketAcquisitionTravelFrameThrottleRelease(
                Released: lease?.IsActive == true,
                MaximumFramesPerSecond,
                lease?.LeaseId,
                activeRouteRunId,
                activeTargetWorld,
                reason.Trim());
            activeLease = null;
            activeRouteRunId = null;
            activeTargetWorld = null;
            lastReleaseReason = reason.Trim();
        }

        lease?.Dispose();
        return result;
    }

    public MarketAcquisitionTravelFrameThrottleSnapshot Snapshot
    {
        get
        {
            lock (sync)
                return CreateSnapshot();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
        }

        Release("Dispose");
    }

    private MarketAcquisitionTravelFrameThrottleSnapshot CreateSnapshot()
    {
        var governorSnapshot = governor.Snapshot();
        return new MarketAcquisitionTravelFrameThrottleSnapshot(
            IsActive: !disposed && activeLease?.IsActive == true,
            MaximumFramesPerSecond,
            activeLease?.LeaseId,
            activeRouteRunId,
            activeTargetWorld,
            governorSnapshot.TotalDelayedFrames,
            governorSnapshot.TotalRequestedDelay,
            lastReleaseReason);
    }
}
