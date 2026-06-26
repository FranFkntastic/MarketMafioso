namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteRunnerTests
{
    [Fact]
    public void Start_ConstructsRunningRoute()
    {
        using var runner = CreateRunner();

        var result = runner.Start(CreatePlan("Maduin", "Rafflesia"));

        Assert.True(result.Success);
        Assert.Equal("Running", runner.State);
        Assert.Equal("Maduin", runner.ActiveStop?.WorldName);
        Assert.Equal(["Pending", "Pending"], runner.Stops.Select(stop => stop.Status).ToArray());
        Assert.Contains("Maduin", runner.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_WithDiagnosticsCreatesRouteLog()
    {
        var directory = CreateTempDirectory();
        using var runner = new MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunner(directory);

        runner.Start(CreatePlan("Maduin"), enableDiagnostics: true);
        runner.ExecutePendingTravelCommand(_ => true);

        Assert.NotNull(runner.LastDiagnosticFilePath);
        var text = ReadLog(runner.LastDiagnosticFilePath!);
        Assert.Contains("Market acquisition route diagnostics started.", text, StringComparison.Ordinal);
        Assert.Contains("route-start", text, StringComparison.Ordinal);
        Assert.Contains("travel-command", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutePendingTravelCommand_SendsCurrentStopCommand()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        string? command = null;

        var result = runner.ExecutePendingTravelCommand(value =>
        {
            command = value;
            return true;
        });

        Assert.True(result.Success);
        Assert.Equal("/li Maduin mb", command);
        Assert.Equal("TravelCommandSent", runner.ActiveStop?.Status);
    }

    [Fact]
    public void PreparePendingStopForCurrentWorld_MarksArrivedWithoutSendingCommand()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));

        var result = runner.PreparePendingStopForCurrentWorld(
            currentWorldIsValid: true,
            currentWorld: "Maduin",
            _ => throw new InvalidOperationException("Should not execute Lifestream when already on the stop world."));

        Assert.True(result.Success);
        Assert.Equal("Arrived", runner.ActiveStop?.Status);
        Assert.Contains("Arrived on Maduin", runner.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteMarketBoardTravelCommand_SendsLocalMarketBoardCommandOnce()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        runner.RecordCurrentWorld("Maduin");
        List<string> commands = [];

        var first = runner.ExecuteMarketBoardTravelCommand(value =>
        {
            commands.Add(value);
            return true;
        });
        var second = runner.ExecuteMarketBoardTravelCommand(value =>
        {
            commands.Add(value);
            return true;
        });

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(["/li mb"], commands);
        Assert.Equal("Arrived", runner.ActiveStop?.Status);
        Assert.Contains("Waiting for Lifestream", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExecuteMarketBoardTravelCommand_ReportsUnhandledCommand()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        runner.RecordCurrentWorld("Maduin");

        var result = runner.ExecuteMarketBoardTravelCommand(_ => false);

        Assert.False(result.Success);
        Assert.Equal("Arrived", runner.ActiveStop?.Status);
        Assert.Contains("not handled", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreparePendingStopForCurrentWorld_SendsCommandWhenCurrentWorldDiffers()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        string? command = null;

        var result = runner.PreparePendingStopForCurrentWorld(
            currentWorldIsValid: true,
            currentWorld: "Zalera",
            value =>
            {
                command = value;
                return true;
            });

        Assert.True(result.Success);
        Assert.Equal("/li Maduin mb", command);
        Assert.Equal("TravelCommandSent", runner.ActiveStop?.Status);
    }

    [Fact]
    public void Pause_PreventsProgressionUntilResume()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));

        runner.Pause();
        var pausedResult = runner.ExecutePendingTravelCommand(_ => throw new InvalidOperationException("Should not execute."));

        Assert.False(pausedResult.Success);
        Assert.Equal("Paused", runner.State);
        Assert.Equal("Pending", runner.ActiveStop?.Status);

        runner.Resume();
        var resumedResult = runner.ExecutePendingTravelCommand(_ => true);

        Assert.True(resumedResult.Success);
        Assert.Equal("Running", runner.State);
        Assert.Equal("TravelCommandSent", runner.ActiveStop?.Status);
    }

    [Fact]
    public void Stop_MarksRouteStopped()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));

        var result = runner.Stop();

        Assert.True(result.Success);
        Assert.Equal("Stopped", runner.State);
        Assert.Null(runner.ActiveStop);
        Assert.Contains("stopped", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restart_RebuildsRouteFromPlan()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        runner.ExecutePendingTravelCommand(_ => true);

        var result = runner.Restart(CreatePlan("Rafflesia"));

        Assert.True(result.Success);
        Assert.Equal("Running", runner.State);
        Assert.Equal("Rafflesia", runner.ActiveStop?.WorldName);
        Assert.Equal("Pending", runner.ActiveStop?.Status);
    }

    [Fact]
    public void Reset_ReturnsRunnerToIdle()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));

        runner.Reset("No route has started.");

        Assert.Equal("Idle", runner.State);
        Assert.Null(runner.ActiveStop);
        Assert.Empty(runner.Stops);
        Assert.Equal("No route has started.", runner.StatusMessage);
    }

    [Fact]
    public void RecordProbe_AdvancesStopAndCompletesRoute()
    {
        using var runner = CreateRunner();
        runner.Start(CreatePlan("Maduin"));
        runner.ExecutePendingTravelCommand(_ => true);
        runner.RecordCurrentWorld("Maduin");

        var result = runner.RecordProbe("Maduin", CreateDryRun(status: "Ready", quantity: 10, gil: 100));

        Assert.True(result.Success);
        Assert.Equal("Completed", runner.State);
        Assert.Null(runner.ActiveStop);
        Assert.Equal("Complete", runner.Stops[0].Status);
        Assert.Equal(10u, runner.Stops[0].WouldBuyQuantity);
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionRouteRunner CreateRunner() =>
        new(CreateTempDirectory());

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionPlan CreatePlan(params string[] worlds) =>
        new()
        {
            RequestId = "request-1",
            Status = "Ready",
            WorldMode = "Recommended",
            ItemId = 7017,
            RequestedQuantity = 999,
            PlannedQuantity = 999,
            PlannedGil = 10_000,
            PreparedAtUtc = DateTimeOffset.UnixEpoch,
            WorldBatches = worlds
                .Select(world => new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldBatch
                {
                    WorldName = world,
                    PlannedQuantity = 10,
                    PlannedGil = 100,
                    Listings = [],
                })
                .ToArray(),
        };

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionLiveDryRun CreateDryRun(
        string status,
        uint quantity,
        uint gil) =>
        new()
        {
            Status = status,
            Message = "Dry run result.",
            RequestedQuantity = 999,
            WouldBuyQuantity = quantity,
            WouldSpendGil = gil,
            Rows = [],
        };

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MarketMafiosoTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ReadLog(string filePath)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
