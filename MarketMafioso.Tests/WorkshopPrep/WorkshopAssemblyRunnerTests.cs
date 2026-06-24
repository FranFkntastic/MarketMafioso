using System.Reflection;
using Dalamud.Plugin.Services;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopAssemblyRunnerTests
{
    [Fact]
    public void WaitingForFabricationStation_attempts_to_open_station_when_ui_is_not_ready()
    {
        var framework = TestFramework.Create();
        var automation = new FakeWorkshopAssemblyUiAutomation
        {
            IsReady = false,
            OpenResult = new WorkshopAssemblyActionResult(false, "Opened nearby fabrication station.", ActionTaken: true),
        };
        using var runner = new WorkshopAssemblyRunner(
            framework,
            TestPluginLog.Create(),
            automation,
            CreateTempDirectory());

        runner.Start(BuildPlan());
        ((TestFramework)(object)framework).RaiseUpdate(framework);

        Assert.Equal(1, automation.OpenAttempts);
        Assert.Equal(WorkshopAssemblyRunnerState.WaitingForFabricationStation, runner.Progress.State);
        Assert.Equal("Opened nearby fabrication station.", runner.Progress.Message);
    }

    [Fact]
    public void Confirmed_material_request_waits_for_contribution_prompt()
    {
        var framework = TestFramework.Create();
        var automation = new FakeWorkshopAssemblyUiAutomation
        {
            IsReady = true,
            OpenProjectResult = new WorkshopAssemblyActionResult(true, "Project opened."),
            SubmitResult = new WorkshopAssemblyActionResult(
                true,
                "Workshop material request confirmed.",
                ActiveMaterialItemId: 5379),
        };
        using var runner = new WorkshopAssemblyRunner(
            framework,
            TestPluginLog.Create(),
            automation,
            CreateTempDirectory());

        runner.Start(BuildPlan());
        ((TestFramework)(object)framework).RaiseUpdate(framework);
        ((TestFramework)(object)framework).RaiseUpdate(framework);
        ((TestFramework)(object)framework).RaiseUpdate(framework);

        Assert.Equal(WorkshopAssemblyRunnerState.ConfirmingContribution, runner.Progress.State);
        Assert.Equal("Workshop material request confirmed.", runner.Progress.Message);
        Assert.Equal(5379u, runner.Progress.ActiveMaterialItemId);
    }

    [Fact]
    public void Confirmed_contribution_prompt_starts_post_contribution_lockout()
    {
        var framework = TestFramework.Create();
        var automation = new FakeWorkshopAssemblyUiAutomation
        {
            IsReady = true,
            OpenProjectResult = new WorkshopAssemblyActionResult(true, "Project opened."),
            SubmitResult = new WorkshopAssemblyActionResult(
                true,
                "Workshop material request confirmed.",
                ActiveMaterialItemId: 5379),
            ConfirmResult = new WorkshopAssemblyActionResult(
                true,
                "Confirmed workshop material contribution.",
                IsContributionConfirmed: true,
                ActiveMaterialItemId: 5379),
        };
        using var runner = new WorkshopAssemblyRunner(
            framework,
            TestPluginLog.Create(),
            automation,
            CreateTempDirectory());

        runner.Start(BuildPlan());
        ((TestFramework)(object)framework).RaiseUpdate(framework);
        ((TestFramework)(object)framework).RaiseUpdate(framework);
        ((TestFramework)(object)framework).RaiseUpdate(framework);
        ((TestFramework)(object)framework).RaiseUpdate(framework);

        Assert.Equal(WorkshopAssemblyRunnerState.WaitingForContributionLockout, runner.Progress.State);
        Assert.Equal("Confirmed workshop material contribution.", runner.Progress.Message);
        Assert.Equal(5379u, runner.Progress.ActiveMaterialItemId);
    }

    [Fact]
    public void Lockout_waits_for_observed_material_progress_before_continuing()
    {
        var framework = TestFramework.Create();
        var automation = new FakeWorkshopAssemblyUiAutomation
        {
            IsReady = true,
            OpenProjectResult = new WorkshopAssemblyActionResult(true, "Project opened."),
            SubmitResult = new WorkshopAssemblyActionResult(
                false,
                "Submitted workshop material request.",
                ActionTaken: true,
                ActiveMaterialItemId: 5379,
                ActiveMaterialStepsComplete: 1),
            ConfirmResult = new WorkshopAssemblyActionResult(
                true,
                "Confirmed workshop material contribution.",
                IsContributionConfirmed: true,
                ActiveMaterialItemId: 5379),
            ProgressResult = new WorkshopAssemblyActionResult(
                false,
                "Waiting for workshop material progress.",
                ActiveMaterialItemId: 5379,
                ActiveMaterialStepsComplete: 1),
        };
        using var runner = new WorkshopAssemblyRunner(
            framework,
            TestPluginLog.Create(),
            automation,
            CreateTempDirectory());

        runner.Start(BuildPlan());
        ((TestFramework)(object)framework).RaiseUpdate(framework);
        ((TestFramework)(object)framework).RaiseUpdate(framework);
        ((TestFramework)(object)framework).RaiseUpdate(framework);
        automation.SubmitResult = new WorkshopAssemblyActionResult(
            true,
            "Workshop material request confirmed.",
            ActiveMaterialItemId: 5379);
        ((TestFramework)(object)framework).RaiseUpdate(framework);
        ((TestFramework)(object)framework).RaiseUpdate(framework);
        SetPrivateField(runner, "continueAt", DateTimeOffset.MinValue);
        ((TestFramework)(object)framework).RaiseUpdate(framework);

        Assert.Equal(WorkshopAssemblyRunnerState.WaitingForContributionLockout, runner.Progress.State);
        Assert.Equal("Waiting for workshop material progress.", runner.Progress.Message);
        Assert.Equal(1, automation.ProgressChecks);
    }

    [Fact]
    public void Ui_reset_action_reacquires_fabrication_station()
    {
        var framework = TestFramework.Create();
        var automation = new FakeWorkshopAssemblyUiAutomation
        {
            IsReady = true,
            OpenProjectResult = new WorkshopAssemblyActionResult(true, "Project opened."),
            SubmitResult = new WorkshopAssemblyActionResult(
                false,
                "Advanced workshop project phase.",
                ActionTaken: true,
                RequiresWorkshopReopen: true),
        };
        using var runner = new WorkshopAssemblyRunner(
            framework,
            TestPluginLog.Create(),
            automation,
            CreateTempDirectory());

        runner.Start(BuildPlan());
        ((TestFramework)(object)framework).RaiseUpdate(framework);
        ((TestFramework)(object)framework).RaiseUpdate(framework);
        ((TestFramework)(object)framework).RaiseUpdate(framework);

        Assert.Equal(WorkshopAssemblyRunnerState.WaitingForFabricationStation, runner.Progress.State);
        Assert.Equal("Advanced workshop project phase.", runner.Progress.Message);
    }

    [Fact]
    public void Cutscene_skip_reacquires_fabrication_station()
    {
        var framework = TestFramework.Create();
        var automation = new FakeWorkshopAssemblyUiAutomation
        {
            IsReady = true,
            CutsceneResult = new WorkshopAssemblyActionResult(
                false,
                "Selected workshop cutscene skip prompt.",
                ActionTaken: true,
                RequiresWorkshopReopen: true),
        };
        using var runner = new WorkshopAssemblyRunner(
            framework,
            TestPluginLog.Create(),
            automation,
            CreateTempDirectory());

        runner.Start(BuildPlan());
        ((TestFramework)(object)framework).RaiseUpdate(framework);

        Assert.Equal(WorkshopAssemblyRunnerState.WaitingForFabricationStation, runner.Progress.State);
        Assert.Equal("Selected workshop cutscene skip prompt.", runner.Progress.Message);
    }

    private static WorkshopAssemblyPlan BuildPlan()
    {
        return new WorkshopAssemblyPlan(
            [
                new WorkshopAssemblyQueueEntry(
                    531,
                    21793,
                    1,
                    19,
                    "Shark-class Bridge",
                    1,
                    []),
            ],
            []);
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MarketMafiosoTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FakeWorkshopAssemblyUiAutomation : IWorkshopAssemblyUiAutomation
    {
        public bool IsReady { get; set; }
        public int OpenAttempts { get; private set; }
        public WorkshopAssemblyActionResult OpenResult { get; set; } = new(false, "No station.");
        public WorkshopAssemblyActionResult OpenProjectResult { get; set; } = new(false, "Not used.");
        public WorkshopAssemblyActionResult SubmitResult { get; set; } = new(false, "Not used.");
        public WorkshopAssemblyActionResult ConfirmResult { get; set; } = new(false, "Not used.");
        public WorkshopAssemblyActionResult ProgressResult { get; set; } = new(false, "Not used.");
        public WorkshopAssemblyActionResult CutsceneResult { get; set; } = new(false, "No cutscene.");
        public WorkshopAssemblyDiagnostics Diagnostics { get; set; } = WorkshopAssemblyDiagnostics.Disabled;
        public int ProgressChecks { get; private set; }

        public bool IsFabricationStationUiReady() => IsReady;

        public WorkshopAssemblyActionResult TrySkipCutscene() => CutsceneResult;

        public WorkshopAssemblyActionResult TryOpenFabricationStation()
        {
            OpenAttempts++;
            return OpenResult;
        }

        public WorkshopAssemblyActionResult TryOpenProject(WorkshopAssemblyQueueEntry entry) => OpenProjectResult;

        public WorkshopAssemblyActionResult TrySubmitNextMaterial(WorkshopAssemblyQueueEntry entry) => SubmitResult;

        public WorkshopAssemblyActionResult TryConfirmContribution() => ConfirmResult;

        public WorkshopAssemblyActionResult TryWaitForContributionProgress(
            WorkshopAssemblyQueueEntry entry,
            uint materialItemId,
            uint previousStepsComplete)
        {
            ProgressChecks++;
            return ProgressResult;
        }

        public string DescribeUiState() => "Workshop UI state: test.";

        public void Dispose()
        {
        }
    }

    private class TestFramework : DispatchProxy
    {
        private IFramework.OnUpdateDelegate? update;

        public static IFramework Create() => DispatchProxy.Create<IFramework, TestFramework>();

        public void RaiseUpdate(IFramework framework) => update?.Invoke(framework);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "add_Update" => AddUpdate(args),
                "remove_Update" => RemoveUpdate(args),
                "get_LastUpdate" => DateTime.Now,
                "get_LastUpdateUTC" => DateTime.UtcNow,
                "get_UpdateDelta" => TimeSpan.FromMilliseconds(16),
                "get_IsInFrameworkUpdateThread" => true,
                "get_IsFrameworkUnloading" => false,
                "GetTaskFactory" => Task.Factory,
                _ => ReturnDefault(targetMethod?.ReturnType),
            };
        }

        private object? AddUpdate(object?[]? args)
        {
            update += (IFramework.OnUpdateDelegate)args![0]!;
            return null;
        }

        private object? RemoveUpdate(object?[]? args)
        {
            update -= (IFramework.OnUpdateDelegate)args![0]!;
            return null;
        }
    }

    private class TestPluginLog : DispatchProxy
    {
        public static IPluginLog Create() => DispatchProxy.Create<IPluginLog, TestPluginLog>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return ReturnDefault(targetMethod?.ReturnType);
        }
    }

    private static object? ReturnDefault(Type? returnType)
    {
        if (returnType == null || returnType == typeof(void))
            return null;

        if (returnType == typeof(Task))
            return Task.CompletedTask;

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var fromResult = typeof(Task)
                .GetMethods()
                .Single(x => x.Name == nameof(Task.FromResult) && x.IsGenericMethodDefinition);
            return fromResult.MakeGenericMethod(resultType).Invoke(null, [resultType.IsValueType ? Activator.CreateInstance(resultType) : null]);
        }

        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }
}
