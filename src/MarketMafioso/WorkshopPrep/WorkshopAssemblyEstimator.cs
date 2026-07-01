using System;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public sealed record WorkshopAssemblyEstimate(
    TimeSpan Duration,
    int TotalProjects,
    int ContributionSteps,
    int PhaseAdvancePrompts,
    int FinalConstructionPrompts,
    int ProductRetrievalPrompts,
    int CutsceneSkips);

public static class WorkshopAssemblyEstimator
{
    public static WorkshopAssemblyEstimate Estimate(WorkshopAssemblyPlan plan)
    {
        var totalProjects = plan.Entries.Sum(x => x.Quantity);
        var contributionSteps = plan.Entries.Sum(x => x.Quantity * x.EstimatedContributionSteps);
        var phaseAdvancePrompts = plan.Entries.Sum(x => x.Quantity * Math.Max(0, x.EstimatedPhaseCount - 1));
        var finalConstructionPrompts = totalProjects;
        var productRetrievalPrompts = totalProjects;
        var cutsceneSkips = totalProjects;

        var duration =
            (totalProjects * WorkshopAssemblyTiming.EstimatedProjectOpen) +
            (contributionSteps * WorkshopAssemblyTiming.EstimatedContributionStep) +
            (phaseAdvancePrompts * WorkshopAssemblyTiming.EstimatedPhaseAdvance) +
            (finalConstructionPrompts * WorkshopAssemblyTiming.EstimatedFinalConstruction) +
            (cutsceneSkips * WorkshopAssemblyTiming.EstimatedCutsceneSkip) +
            (productRetrievalPrompts * WorkshopAssemblyTiming.EstimatedProductRetrieval);

        return new WorkshopAssemblyEstimate(
            duration,
            totalProjects,
            contributionSteps,
            phaseAdvancePrompts,
            finalConstructionPrompts,
            productRetrievalPrompts,
            cutsceneSkips);
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return "0m";

        if (duration < TimeSpan.FromMinutes(1))
            return "<1m";

        var totalMinutes = (int)Math.Ceiling(duration.TotalMinutes);
        if (duration < TimeSpan.FromHours(1))
            return $"~{totalMinutes}m";

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return minutes == 0
            ? $"~{hours}h"
            : $"~{hours}h {minutes}m";
    }
}
