using MarketMafioso.Automation.Diagnostics;

namespace MarketMafioso.Tests.Automation.Diagnostics;

public sealed class AutomationDiagnosticsLogTests
{
    [Fact]
    public void CreateEnabled_creates_timestamped_log_and_writes_start_metadata()
    {
        var directory = CreateTempDirectory();
        var startedAt = new DateTimeOffset(2026, 7, 2, 12, 34, 56, TimeSpan.Zero);

        using var log = AutomationDiagnosticsLog.CreateEnabled(
            directory,
            startedAt,
            filePrefix: "core",
            startMessage: "Core diagnostics started.",
            metadata: new Dictionary<string, string?> { ["component"] = "Diagnostics" });

        Assert.True(log.IsEnabled);
        Assert.NotNull(log.FilePath);
        Assert.StartsWith(directory, log.FilePath, StringComparison.Ordinal);
        Assert.EndsWith("core-20260702-123456.log", log.FilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(log.FilePath));

        var text = ReadLog(log.FilePath);
        Assert.Contains("Core diagnostics started.", text, StringComparison.Ordinal);
        Assert.Contains("component: Diagnostics", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateEnabled_adds_suffix_when_timestamp_exists()
    {
        var directory = CreateTempDirectory();
        var startedAt = new DateTimeOffset(2026, 7, 2, 12, 34, 56, TimeSpan.Zero);
        using var first = AutomationDiagnosticsLog.CreateEnabled(directory, startedAt, "core", "Started.", null);

        using var second = AutomationDiagnosticsLog.CreateEnabled(directory, startedAt, "core", "Started.", null);

        Assert.EndsWith("core-20260702-123456-1.log", second.FilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(second.FilePath!));
    }

    [Fact]
    public void Record_writes_event_details_and_filters_null_values()
    {
        var directory = CreateTempDirectory();
        using var log = AutomationDiagnosticsLog.CreateEnabled(directory, DateTimeOffset.UnixEpoch, "core", "Started.", null);

        log.Record(
            "state",
            "Entered state.",
            new Dictionary<string, string?>
            {
                ["component"] = "Runtime",
                ["empty"] = null,
            });

        var text = ReadLog(log.FilePath!);
        Assert.Contains("] state", text, StringComparison.Ordinal);
        Assert.Contains("  Entered state.", text, StringComparison.Ordinal);
        Assert.Contains("  component: Runtime", text, StringComparison.Ordinal);
        Assert.DoesNotContain("empty:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_coalesces_repeated_events_when_next_event_arrives()
    {
        var directory = CreateTempDirectory();
        using var log = AutomationDiagnosticsLog.CreateEnabled(directory, DateTimeOffset.UnixEpoch, "core", "Started.", null);

        log.Record("state", "Waiting.");
        log.Record("state", "Waiting.");
        log.Record("state", "Waiting.");
        log.Record("state", "Ready.");

        var text = ReadLog(log.FilePath!);
        Assert.Contains("Previous event repeated 2 more time(s).", text, StringComparison.Ordinal);
        Assert.Contains("event: state", text, StringComparison.Ordinal);
        Assert.Contains("message: Waiting.", text, StringComparison.Ordinal);
        Assert.Contains("Ready.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_writes_completion_event()
    {
        var directory = CreateTempDirectory();
        using var log = AutomationDiagnosticsLog.CreateEnabled(directory, DateTimeOffset.UnixEpoch, "core", "Started.", null);

        log.Complete("Done.");

        var text = ReadLog(log.FilePath!);
        Assert.Contains("] complete", text, StringComparison.Ordinal);
        Assert.Contains("Done.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Fail_writes_exception_details()
    {
        var directory = CreateTempDirectory();
        using var log = AutomationDiagnosticsLog.CreateEnabled(directory, DateTimeOffset.UnixEpoch, "core", "Started.", null);

        log.Fail("Operation failed.", new InvalidOperationException("Bad state."));

        var text = ReadLog(log.FilePath!);
        Assert.Contains("failed", text, StringComparison.Ordinal);
        Assert.Contains("exceptionType: System.InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("exceptionMessage: Bad state.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_ignores_records()
    {
        var log = AutomationDiagnosticsLog.Disabled;

        log.Record("ignored", "Ignored.");
        log.Complete("Ignored.");

        Assert.False(log.IsEnabled);
        Assert.Null(log.FilePath);
    }

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
