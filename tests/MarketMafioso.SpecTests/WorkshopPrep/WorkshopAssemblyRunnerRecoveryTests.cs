using System.Reflection;
using Dalamud.Plugin.Services;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class WorkshopAssemblyRunnerRecoveryTests
{
    [Fact]
    public void Same_phase_action_refreshes_the_bounded_wait_window()
    {
        var clock = new ManualTimeProvider();
        var (framework, frameworkDriver) = FrameworkDriver.Create();
        var ui = new FakeWorkshopAssemblyUiAutomation
        {
            FabricationStationReady = true,
        };
        ui.OpenProjectResults.Enqueue(new(true, "Project is open."));
        ui.SubmitMaterialResults.Enqueue(new(false, "Opened request item selector.", ActionTaken: true));
        ui.SubmitMaterialResults.Enqueue(new(false, "Waiting for request item selection."));

        using var runner = CreateRunner(framework, ui, clock);
        runner.Start(BuildPlan());
        frameworkDriver.Tick(framework);
        frameworkDriver.Tick(framework);
        Assert.Equal(WorkshopAssemblyRunnerState.SubmittingMaterial, runner.Progress.State);

        clock.Advance(TimeSpan.FromSeconds(9));
        frameworkDriver.Tick(framework);
        clock.Advance(TimeSpan.FromSeconds(2));
        frameworkDriver.Tick(framework);

        Assert.Equal(WorkshopAssemblyRunnerState.SubmittingMaterial, runner.Progress.State);
        Assert.True(runner.IsRunning);
    }

    [Fact]
    public void Missing_post_contribution_ui_returns_to_station_recovery_without_inventing_progress()
    {
        var clock = new ManualTimeProvider();
        var (framework, frameworkDriver) = FrameworkDriver.Create();
        var ui = new FakeWorkshopAssemblyUiAutomation
        {
            FabricationStationReady = true,
        };
        ui.OpenProjectResults.Enqueue(new(true, "Project is open."));
        ui.SubmitMaterialResults.Enqueue(new(
            false,
            "Submitted material request.",
            ActionTaken: true,
            ActiveMaterialItemId: 77,
            ActiveMaterialStepsComplete: 2));
        ui.SubmitMaterialResults.Enqueue(new(
            true,
            "Confirmed contribution.",
            IsContributionConfirmed: true,
            ActiveMaterialItemId: 77));
        ui.ProgressResults.Enqueue(new(
            false,
            "Workshop UI closed; reopening the fabrication station.",
            RequiresWorkshopReopen: true,
            ActiveMaterialItemId: 77,
            ActiveMaterialStepsComplete: 2));

        using var runner = CreateRunner(framework, ui, clock);
        runner.Start(BuildPlan());
        frameworkDriver.Tick(framework);
        frameworkDriver.Tick(framework);
        frameworkDriver.Tick(framework);
        frameworkDriver.Tick(framework);
        Assert.Equal(WorkshopAssemblyRunnerState.WaitingForContributionLockout, runner.Progress.State);

        clock.Advance(WorkshopAssemblyTiming.PostContributionLockout);
        frameworkDriver.Tick(framework);

        Assert.Equal(WorkshopAssemblyRunnerState.WaitingForFabricationStation, runner.Progress.State);
        Assert.Null(runner.Progress.ActiveMaterialItemId);
        Assert.Equal(0, runner.Progress.CompletedProjects);
        Assert.True(runner.IsRunning);
    }

    private static WorkshopAssemblyRunner CreateRunner(
        IFramework framework,
        IWorkshopAssemblyUiAutomation ui,
        TimeProvider clock) =>
        new(
            framework,
            ProxyLog.Create(),
            ui,
            Path.GetTempPath(),
            timeProvider: clock);

    private static WorkshopAssemblyPlan BuildPlan() => new(
        [new WorkshopAssemblyQueueEntry(1, 2, 3, 4, "Test project", 1, [], 1, 1)],
        []);

    private sealed class FakeWorkshopAssemblyUiAutomation : IWorkshopAssemblyUiAutomation
    {
        public WorkshopAssemblyDiagnostics Diagnostics { get; set; } = WorkshopAssemblyDiagnostics.Disabled;
        public bool FabricationStationReady { get; init; }
        public Queue<WorkshopAssemblyActionResult> OpenProjectResults { get; } = [];
        public Queue<WorkshopAssemblyActionResult> SubmitMaterialResults { get; } = [];
        public Queue<WorkshopAssemblyActionResult> ProgressResults { get; } = [];

        public void ResetState() { }

        public bool IsFabricationStationUiReady() => FabricationStationReady;

        public WorkshopAssemblyActionResult TrySkipCutscene() => new(false, "No cutscene.");

        public WorkshopAssemblyActionResult TryOpenFabricationStation() => new(false, "Station unavailable.");

        public WorkshopAssemblyActionResult TryOpenProject(WorkshopAssemblyQueueEntry entry) =>
            OpenProjectResults.Dequeue();

        public WorkshopAssemblyActionResult TrySubmitNextMaterial(WorkshopAssemblyQueueEntry entry) =>
            SubmitMaterialResults.Dequeue();

        public WorkshopAssemblyActionResult TryConfirmContribution() => new(false, "No confirmation.");

        public WorkshopAssemblyActionResult TryWaitForContributionProgress(
            WorkshopAssemblyQueueEntry entry,
            uint materialItemId,
            uint previousStepsComplete) =>
            ProgressResults.Dequeue();

        public string DescribeUiState() => "test";

        public void Dispose() { }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan elapsed) => utcNow += elapsed;
    }

    private class FrameworkDriver : DispatchProxy
    {
        private Delegate? update;

        public static (IFramework Framework, FrameworkDriver Driver) Create()
        {
            var framework = DispatchProxy.Create<IFramework, FrameworkDriver>();
            return (framework, (FrameworkDriver)(object)framework);
        }

        public void Tick(IFramework framework) => update?.DynamicInvoke(framework);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "add_Update")
            {
                update = Delegate.Combine(update, (Delegate)args![0]!);
                return null;
            }

            if (targetMethod?.Name == "remove_Update")
            {
                update = Delegate.Remove(update, (Delegate)args![0]!);
                return null;
            }

            return DefaultValue(targetMethod?.ReturnType);
        }
    }

    private class ProxyLog : DispatchProxy
    {
        public static IPluginLog Create() => DispatchProxy.Create<IPluginLog, ProxyLog>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            DefaultValue(targetMethod?.ReturnType);
    }

    private static object? DefaultValue(Type? type) =>
        type == null || type == typeof(void) || !type.IsValueType
            ? null
            : Activator.CreateInstance(type);
}
