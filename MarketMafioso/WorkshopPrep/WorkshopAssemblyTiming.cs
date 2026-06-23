using System;

namespace MarketMafioso.WorkshopPrep;

public static class WorkshopAssemblyTiming
{
    public static readonly TimeSpan AddonTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PostContributionLockout = TimeSpan.FromSeconds(1);
}
