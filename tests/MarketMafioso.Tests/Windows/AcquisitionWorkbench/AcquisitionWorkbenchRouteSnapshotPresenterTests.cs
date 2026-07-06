using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.AcquisitionWorkbench;
using MarketMafioso.Tests.MarketAcquisition;

namespace MarketMafioso.Tests.Windows.AcquisitionWorkbench;

public sealed class AcquisitionWorkbenchRouteSnapshotPresenterTests
{
    [Fact]
    public void Build_WithoutPreparedPlan_DisablesRouteCommands()
    {
        using var runner = CreateRunner();

        var snapshot = AcquisitionWorkbenchRouteSnapshotPresenter.Build(null, runner, isBusy: false, canPrepareRoute: false);

        Assert.Equal("Idle", snapshot.State);
        Assert.False(snapshot.CanPrepare);
        Assert.False(snapshot.CanStart);
        Assert.False(snapshot.CanPause);
        Assert.False(snapshot.CanResume);
        Assert.False(snapshot.CanStop);
        Assert.False(snapshot.CanRestart);
        Assert.False(snapshot.CanReprepare);
    }

    [Fact]
    public void Build_WithReadyPlan_EnablesStart()
    {
        using var runner = CreateRunner();

        var snapshot = AcquisitionWorkbenchRouteSnapshotPresenter.Build(
            MarketAcquisitionTestPlans.MultiLineTwoWorlds(),
            runner,
            isBusy: false,
            canPrepareRoute: true);

        Assert.True(snapshot.HasPreparedPlan);
        Assert.True(snapshot.CanPrepare);
        Assert.Equal(2, snapshot.PreparedWorldCount);
        Assert.True(snapshot.CanStart);
        Assert.True(snapshot.CanStartWithDiagnostics);
        Assert.False(snapshot.CanPause);
        Assert.False(snapshot.CanResume);
        Assert.False(snapshot.CanStop);
    }

    [Fact]
    public void Build_WhenPrepareIsAllowedButBusy_DisablesPrepare()
    {
        using var runner = CreateRunner();

        var snapshot = AcquisitionWorkbenchRouteSnapshotPresenter.Build(
            MarketAcquisitionTestPlans.MultiLineTwoWorlds(),
            runner,
            isBusy: true,
            canPrepareRoute: true);

        Assert.False(snapshot.CanPrepare);
        Assert.False(snapshot.CanStart);
    }

    [Fact]
    public void Build_WhenRunnerIsRunning_EnablesPauseAndStop()
    {
        using var runner = CreateRunner();
        var plan = MarketAcquisitionTestPlans.MultiLineTwoWorlds();
        runner.Start(plan);

        var snapshot = AcquisitionWorkbenchRouteSnapshotPresenter.Build(plan, runner, isBusy: false);

        Assert.Equal("Running", snapshot.State);
        Assert.False(snapshot.CanStart);
        Assert.True(snapshot.CanPause);
        Assert.False(snapshot.CanResume);
        Assert.True(snapshot.CanStop);
        Assert.Equal("Siren", snapshot.ActiveWorld);
        Assert.Equal(2, snapshot.RouteRows.Count);
    }

    [Fact]
    public void Build_WhenRunnerIsRunningAndBusy_StillEnablesPauseAndStop()
    {
        using var runner = CreateRunner();
        var plan = MarketAcquisitionTestPlans.MultiLineTwoWorlds();
        runner.Start(plan);

        var snapshot = AcquisitionWorkbenchRouteSnapshotPresenter.Build(plan, runner, isBusy: true);

        Assert.False(snapshot.CanStart);
        Assert.True(snapshot.CanPause);
        Assert.False(snapshot.CanResume);
        Assert.True(snapshot.CanStop);
    }

    [Fact]
    public void Build_WhenRunnerIsPaused_EnablesResumeAndStop()
    {
        using var runner = CreateRunner();
        var plan = MarketAcquisitionTestPlans.MultiLineTwoWorlds();
        runner.Start(plan);
        runner.Pause();

        var snapshot = AcquisitionWorkbenchRouteSnapshotPresenter.Build(plan, runner, isBusy: false);

        Assert.Equal("Paused", snapshot.State);
        Assert.False(snapshot.CanStart);
        Assert.False(snapshot.CanPause);
        Assert.True(snapshot.CanResume);
        Assert.True(snapshot.CanStop);
    }

    [Fact]
    public void Build_WhenRunnerIsPausedAndBusy_StillEnablesResumeAndStop()
    {
        using var runner = CreateRunner();
        var plan = MarketAcquisitionTestPlans.MultiLineTwoWorlds();
        runner.Start(plan);
        runner.Pause();

        var snapshot = AcquisitionWorkbenchRouteSnapshotPresenter.Build(plan, runner, isBusy: true);

        Assert.False(snapshot.CanStart);
        Assert.False(snapshot.CanPause);
        Assert.True(snapshot.CanResume);
        Assert.True(snapshot.CanStop);
    }

    [Fact]
    public void Build_PreservesPerLineRouteDetailsForMultiItemWorlds()
    {
        using var runner = CreateRunner();
        var plan = MarketAcquisitionTestPlans.MultiLineSingleWorld();
        runner.Start(plan);

        var snapshot = AcquisitionWorkbenchRouteSnapshotPresenter.Build(plan, runner, isBusy: false);

        var row = Assert.Single(snapshot.RouteRows);
        Assert.Equal(2, row.Lines.Count);
        Assert.Contains(row.Lines, line => line.Item.Contains("Fire Shard", StringComparison.Ordinal));
        Assert.Contains(row.Lines, line => line.Item.Contains("Lightning Shard", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithCompletedOrProbedStops_EnablesReprepare()
    {
        using var runner = CreateRunner();
        var plan = MarketAcquisitionTestPlans.MultiLineTwoWorlds();
        runner.Start(plan);
        runner.Stops[0].LineStates[0].Status = "SkippedNoLiveStock";
        runner.Stop();

        var snapshot = AcquisitionWorkbenchRouteSnapshotPresenter.Build(plan, runner, isBusy: false);

        Assert.True(snapshot.CanStart);
        Assert.True(snapshot.CanRestart);
        Assert.True(snapshot.CanReprepare);
        Assert.Equal(1, snapshot.CompletedOrProbedWorldCount);
    }

    private static MarketAcquisitionRouteRunner CreateRunner() =>
        new(Path.Combine(Path.GetTempPath(), $"mmf-route-snapshot-{Guid.NewGuid():N}"));
}
