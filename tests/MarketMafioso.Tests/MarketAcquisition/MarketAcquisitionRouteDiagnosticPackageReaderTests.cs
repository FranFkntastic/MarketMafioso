using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteDiagnosticPackageReaderTests
{
    [Fact]
    public void Read_UsesSanitizedArchivedTravelBlockRunAsLegacyFixture()
    {
        var package = MarketAcquisitionRouteDiagnosticPackageReader.Read(GetFixtureDirectory());

        Assert.Equal(MarketAcquisitionRouteDiagnosticPackageFormat.LegacyRouteLog, package.Format);
        Assert.Equal(0, package.SchemaVersion);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-10T13:26:01.9623234Z"),
            package.Events[2].RecordedAtUtc);
        Assert.Collection(
            package.Events,
            routeEvent => Assert.Equal("start", routeEvent.EventName),
            routeEvent => Assert.Equal("route-start", routeEvent.EventName),
            routeEvent =>
            {
                Assert.Equal("travel-ui-blocked", routeEvent.EventName);
                Assert.Equal(17, routeEvent.ElapsedMilliseconds);
                Assert.Equal("ItemSearch, ItemSearchResult", routeEvent.Details["blockingAddons"]);
            },
            routeEvent =>
            {
                Assert.Equal("repeat", routeEvent.EventName);
                Assert.Equal("travel-ui-blocked", routeEvent.Details["event"]);
            },
            routeEvent =>
            {
                Assert.Equal("stopped", routeEvent.EventName);
                Assert.Equal(6260, routeEvent.ElapsedMilliseconds);
            });
    }

    [Fact]
    public void Read_UsesVersionedJsonlWhenAvailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MarketMafiosoRouteReplayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var diagnostics = MarketAcquisitionRouteDiagnostics.CreateEnabled(directory, DateTimeOffset.UnixEpoch);
        var packageDirectory = diagnostics.PackageDirectoryPath!;
        diagnostics.Record(
            "travel-command",
            "Sent Lifestream command.",
            new Dictionary<string, string?>
            {
                ["world"] = "Maduin",
            });
        diagnostics.Dispose();

        var package = MarketAcquisitionRouteDiagnosticPackageReader.Read(packageDirectory);

        Assert.Equal(MarketAcquisitionRouteDiagnosticPackageFormat.JsonlV1, package.Format);
        Assert.Equal(MarketAcquisitionRouteDiagnosticEvent.CurrentSchemaVersion, package.SchemaVersion);
        Assert.False(package.IsComplete);
        Assert.Collection(
            package.Events,
            routeEvent => Assert.Equal("start", routeEvent.EventName),
            routeEvent =>
            {
                Assert.Equal("travel-command", routeEvent.EventName);
                Assert.Equal("Maduin", routeEvent.Details["world"]);
            });
    }

    [Fact]
    public void Read_RejectsJsonlWithMissingSequence()
    {
        var diagnostics = CreateJsonlPackage(out var packageDirectory);
        diagnostics.Record("travel-command", "Sent command.");
        diagnostics.Dispose();
        var eventsPath = Path.Combine(packageDirectory, "route-events.jsonl");
        var events = File.ReadAllText(eventsPath).Replace("\"sequence\":2", "\"sequence\":3", StringComparison.Ordinal);
        File.WriteAllText(eventsPath, events);

        Assert.Throws<InvalidDataException>(() => MarketAcquisitionRouteDiagnosticPackageReader.Read(packageDirectory));
    }

    [Fact]
    public void Read_RejectsEmptyJsonlPackage()
    {
        var diagnostics = CreateJsonlPackage(out var packageDirectory);
        diagnostics.Dispose();
        File.WriteAllText(Path.Combine(packageDirectory, "route-events.jsonl"), string.Empty);

        Assert.Throws<InvalidDataException>(() => MarketAcquisitionRouteDiagnosticPackageReader.Read(packageDirectory));
    }

    [Fact]
    public void Read_ParsesLegacyEventsAfterOneHourBoundary()
    {
        var packageDirectory = Path.Combine(Path.GetTempPath(), "MarketMafiosoRouteReplayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(
            Path.Combine(packageDirectory, "route.log"),
            "[00:00.000] start\n  Started.\n  startedAt: 2026-07-10T09:00:00-04:00\n\n[01:00:00.000] stopped\n  Route stopped.\n");

        var package = MarketAcquisitionRouteDiagnosticPackageReader.Read(packageDirectory);

        Assert.Equal(2, package.Events.Count);
        Assert.Equal(3_600_000, package.Events[1].ElapsedMilliseconds);
        Assert.True(package.IsComplete);
    }

    [Fact]
    public void Read_RecognizesCompletedJsonlCapture()
    {
        var diagnostics = CreateJsonlPackage(out var packageDirectory);
        diagnostics.Complete("Route complete.");
        diagnostics.Dispose();

        var package = MarketAcquisitionRouteDiagnosticPackageReader.Read(packageDirectory);

        Assert.True(package.IsComplete);
        Assert.Equal("Complete", package.CaptureStatus);
    }

    [Fact]
    public void Read_RejectsCompletedJsonlMissingTerminalEvent()
    {
        var diagnostics = CreateJsonlPackage(out var packageDirectory);
        diagnostics.Complete("Route complete.");
        diagnostics.Dispose();
        var eventsPath = Path.Combine(packageDirectory, "route-events.jsonl");
        var events = File.ReadAllLines(eventsPath);
        File.WriteAllLines(eventsPath, events[..^1]);

        Assert.Throws<InvalidDataException>(() => MarketAcquisitionRouteDiagnosticPackageReader.Read(packageDirectory));
    }

    [Fact]
    public void ArchivedTravelBlockFixture_CharacterizesNoTravelCommandWhileUiIsBlocked()
    {
        var package = MarketAcquisitionRouteDiagnosticPackageReader.Read(GetFixtureDirectory());
        var blocked = Assert.Single(package.Events, routeEvent => routeEvent.EventName == "travel-ui-blocked");
        var repeated = Assert.Single(package.Events, routeEvent => routeEvent.EventName == "repeat");
        var stopped = Assert.Single(package.Events, routeEvent => routeEvent.EventName == "stopped");
        Assert.Equal("ItemSearch, ItemSearchResult", blocked.Details["blockingAddons"]);

        using var harness = MarketAcquisitionRouteEngineHarness.Create();
        harness.Clock.UtcNow = blocked.RecordedAtUtc;
        harness.Context.CurrentWorld = "Siren";
        harness.Ui.TravelPreflightCanSend = false;
        harness.Engine.Start(
            MarketAcquisitionRouteEngineTestData.Plan("Coeurl"),
            MarketAcquisitionRouteEngineTestData.AcceptedClaim(),
            enableDiagnostics: false,
            includeOpportunisticChecks: true);

        harness.Engine.TickRoute(isRequestBusy: false);
        harness.Clock.UtcNow = repeated.RecordedAtUtc;
        harness.Engine.TickRoute(isRequestBusy: false);
        harness.Clock.UtcNow = stopped.RecordedAtUtc;
        harness.Engine.Stop();

        Assert.Empty(harness.Ui.Commands);
        Assert.Equal("Stopped", harness.Runner.State);
        Assert.Null(harness.Runner.ActiveStop);
        Assert.Equal("Route stopped.", harness.Runner.StatusMessage);
    }

    private static MarketAcquisitionRouteDiagnostics CreateJsonlPackage(out string packageDirectory)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MarketMafiosoRouteReplayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var diagnostics = MarketAcquisitionRouteDiagnostics.CreateEnabled(directory, DateTimeOffset.UnixEpoch);
        packageDirectory = diagnostics.PackageDirectoryPath!;
        return diagnostics;
    }

    private static string GetFixtureDirectory() => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "MarketAcquisition",
        "legacy-travel-ui-blocked");
}
