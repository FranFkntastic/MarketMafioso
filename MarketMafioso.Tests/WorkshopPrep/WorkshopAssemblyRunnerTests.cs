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
        public WorkshopAssemblyDiagnostics Diagnostics { get; set; } = WorkshopAssemblyDiagnostics.Disabled;

        public bool IsFabricationStationUiReady() => IsReady;

        public WorkshopAssemblyActionResult TryOpenFabricationStation()
        {
            OpenAttempts++;
            return OpenResult;
        }

        public WorkshopAssemblyActionResult TryOpenProject(WorkshopAssemblyQueueEntry entry) => new(false, "Not used.");

        public WorkshopAssemblyActionResult TrySubmitNextMaterial(WorkshopAssemblyQueueEntry entry) => new(false, "Not used.");

        public WorkshopAssemblyActionResult TryConfirmContribution() => new(false, "Not used.");

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
}
