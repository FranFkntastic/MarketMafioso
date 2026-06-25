using System;

namespace MarketMafioso.WorkshopPrep;

public static class WorkshopAssemblyTiming
{
    public static readonly TimeSpan AddonTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan UiInteractionDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan PostContributionLockout = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan EstimatedProjectOpen = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan EstimatedContributionStep = PostContributionLockout + TimeSpan.FromMilliseconds(650);
    public static readonly TimeSpan EstimatedPhaseAdvance = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan EstimatedFinalConstruction = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan EstimatedCutsceneSkip = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan EstimatedProductRetrieval = TimeSpan.FromSeconds(2);
}
