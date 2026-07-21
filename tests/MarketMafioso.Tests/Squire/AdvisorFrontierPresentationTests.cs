using System.Diagnostics;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.UI.Plots;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class AdvisorFrontierPresentationTests
{
    [Fact]
    public void LargeFrontier_UsesBoundedExactWindowsAndKeepsEverySolutionReachable()
    {
        const int solutionCount = 2_030;
        var pareto = new EquipmentParetoResult(
            Enumerable.Range(0, solutionCount).Select(Solution).ToArray(),
            [],
            [],
            []);
        var stopwatch = Stopwatch.StartNew();

        var presentation = new AdvisorFrontierPresentation(pareto);
        var reached = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < solutionCount; offset += AdvisorFrontierPresentation.MaxFrameSolutionCount)
        {
            var page = presentation.WindowFrom(offset);
            Assert.InRange(page.Solutions.Count, 1, AdvisorFrontierPresentation.MaxFrameSolutionCount);
            foreach (var solution in page.Solutions)
                reached.Add(solution.Candidate.SolutionId);
        }

        var selected = presentation.At(1_000);
        var selectedWindow = presentation.WindowAround(selected.Candidate.SolutionId);
        var plot = new ParetoFrontierPlotBuilder().Build(selectedWindow.ToPlotResult());
        stopwatch.Stop();

        Assert.Equal(solutionCount, presentation.Count);
        Assert.Equal(solutionCount, reached.Count);
        Assert.Equal("solution-00999", presentation.Previous(selected.Candidate.SolutionId)!.Candidate.SolutionId);
        Assert.Equal("solution-01001", presentation.Next(selected.Candidate.SolutionId)!.Candidate.SolutionId);
        Assert.Contains(selected, selectedWindow.Solutions);
        Assert.Equal(AdvisorFrontierPresentation.MaxFrameSolutionCount, selectedWindow.Solutions.Count);
        Assert.Equal(AdvisorFrontierPresentation.MaxFrameSolutionCount, plot.SolutionsByDatumId.Count);
        Assert.True(
            plot.Spec.Layers.Sum(layer => layer switch
            {
                PlotPointLayer points => points.Data.Count,
                PlotPolylineLayer line => line.Data.Count,
                _ => 0,
            }) <= AdvisorFrontierPresentation.MaxFrameSolutionCount * 2);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Indexing, traversing, and plotting a {solutionCount:N0}-solution frontier took {stopwatch.Elapsed}.");
    }

    private static EquipmentDecisionSolution Solution(int index) => new(
        new($"solution-{index:D5}", []),
        new(
            new("test", "1"),
            new("test", 16, 100, "Test", []),
            index,
            new(index, index, []),
            UpgradeAssessment.ClearImprovement,
            [],
            [],
            [],
            EquipmentEvaluationConfidence.High,
            []),
        checked((ulong)index),
        new(0, 0, 0),
        new(0, 0, 0),
        []);
}
