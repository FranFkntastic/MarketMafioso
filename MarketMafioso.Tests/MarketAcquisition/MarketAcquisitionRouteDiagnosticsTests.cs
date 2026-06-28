namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteDiagnosticsTests
{
    [Fact]
    public void CreateEnabled_CreatesTimestampedRouteLog()
    {
        var directory = CreateTempDirectory();
        var startedAt = new DateTimeOffset(2026, 6, 25, 22, 45, 12, TimeSpan.Zero);

        using var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.CreateEnabled(
            directory,
            startedAt);

        Assert.True(diagnostics.IsEnabled);
        Assert.NotNull(diagnostics.FilePath);
        Assert.StartsWith(directory, diagnostics.FilePath, StringComparison.Ordinal);
        Assert.EndsWith("route-20260625-224512.log", diagnostics.FilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(diagnostics.FilePath));
    }

    [Fact]
    public void Record_WritesEventDetails()
    {
        var directory = CreateTempDirectory();
        using var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.CreateEnabled(
            directory,
            DateTimeOffset.UnixEpoch);

        diagnostics.Record(
            "travel-command",
            "Sent Lifestream command.",
            new Dictionary<string, string?>
            {
                ["world"] = "Maduin",
                ["command"] = "/li Maduin mb",
                ["empty"] = null,
            });

        var text = ReadLog(diagnostics.FilePath!);
        Assert.Contains("travel-command", text, StringComparison.Ordinal);
        Assert.Contains("Sent Lifestream command.", text, StringComparison.Ordinal);
        Assert.Contains("world: Maduin", text, StringComparison.Ordinal);
        Assert.Contains("command: /li Maduin mb", text, StringComparison.Ordinal);
        Assert.DoesNotContain("empty:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_WritesCompletionEvent()
    {
        var directory = CreateTempDirectory();
        using var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.CreateEnabled(
            directory,
            DateTimeOffset.UnixEpoch);

        diagnostics.Complete("Route complete.");

        var text = ReadLog(diagnostics.FilePath!);
        Assert.Contains("complete", text, StringComparison.Ordinal);
        Assert.Contains("Route complete.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordAutomationSnapshot_WritesClassifiedState()
    {
        var directory = CreateTempDirectory();
        using var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.CreateEnabled(
            directory,
            DateTimeOffset.UnixEpoch);

        diagnostics.RecordAutomationSnapshot(
            MarketMafioso.MarketAcquisition.MarketBoardAutomationSnapshot.Create(
                "SearchItem",
                "AfterInput",
                "ItemSearchResultReady",
                "SearchSent",
                MarketMafioso.MarketAcquisition.MarketBoardAutomationOutcome.Recoverable,
                "RetryHumanEnterPath",
                new Dictionary<string, string?>
                {
                    ["searchText"] = "Varnish",
                }));

        var text = ReadLog(diagnostics.FilePath!);
        Assert.Contains("automation-snapshot", text, StringComparison.Ordinal);
        Assert.Contains("step: SearchItem", text, StringComparison.Ordinal);
        Assert.Contains("phase: AfterInput", text, StringComparison.Ordinal);
        Assert.Contains("expected: ItemSearchResultReady", text, StringComparison.Ordinal);
        Assert.Contains("observed: SearchSent", text, StringComparison.Ordinal);
        Assert.Contains("outcome: Recoverable", text, StringComparison.Ordinal);
        Assert.Contains("nextAction: RetryHumanEnterPath", text, StringComparison.Ordinal);
        Assert.Contains("searchText: Varnish", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_IgnoresRecords()
    {
        var diagnostics = MarketMafioso.MarketAcquisition.MarketAcquisitionRouteDiagnostics.Disabled;

        diagnostics.Record("ignored", "Ignored.");
        diagnostics.Complete("Ignored.");

        Assert.False(diagnostics.IsEnabled);
        Assert.Null(diagnostics.FilePath);
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
