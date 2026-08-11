using System;
using Franthropy.Dalamud.Automation;

namespace MarketMafioso.MarketDiagnostics;

internal sealed class RetainerListingRefreshReadinessGate
{
    internal static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromSeconds(3);
    internal const int DefaultRequiredStableFrames = 6;

    private readonly TimeSpan quietPeriod;
    private readonly DalamudUiStabilityGate frameStability;
    private DateTimeOffset? readySinceUtc;

    public RetainerListingRefreshReadinessGate()
        : this(DefaultQuietPeriod, DefaultRequiredStableFrames)
    {
    }

    internal RetainerListingRefreshReadinessGate(TimeSpan quietPeriod, int requiredStableFrames)
    {
        if (quietPeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(quietPeriod));

        this.quietPeriod = quietPeriod;
        frameStability = new DalamudUiStabilityGate(requiredStableFrames);
    }

    public (bool Ready, string? Reason) Observe(
        DateTimeOffset nowUtc,
        bool immediatelyReady,
        string? immediateReason)
    {
        if (!immediatelyReady)
        {
            Reset();
            return (false, immediateReason);
        }

        if (readySinceUtc is null || nowUtc < readySinceUtc.Value)
        {
            readySinceUtc = nowUtc;
            frameStability.Reset();
        }

        var stableForEnoughFrames = frameStability.Observe(true);
        var quietForLongEnough = nowUtc - readySinceUtc.Value >= quietPeriod;
        if (!stableForEnoughFrames || !quietForLongEnough)
        {
            return (
                false,
                "Waiting for the game UI to remain settled before refreshing retainer listings.");
        }

        return (true, null);
    }

    private void Reset()
    {
        readySinceUtc = null;
        frameStability.Reset();
    }
}
