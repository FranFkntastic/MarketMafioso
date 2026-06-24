using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopAssemblyDiagnosticsTests
{
    [Fact]
    public void CreateEnabled_creates_timestamped_log_file()
    {
        var directory = CreateTempDirectory();
        var startedAt = new DateTimeOffset(2026, 6, 23, 21, 30, 12, TimeSpan.Zero);

        using var diagnostics = WorkshopAssemblyDiagnostics.CreateEnabled(directory, startedAt);

        Assert.True(diagnostics.IsEnabled);
        Assert.NotNull(diagnostics.FilePath);
        Assert.StartsWith(directory, diagnostics.FilePath, StringComparison.Ordinal);
        Assert.EndsWith("assembly-20260623-213012.log", diagnostics.FilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(diagnostics.FilePath));
    }

    [Fact]
    public void Record_writes_elapsed_event_and_details()
    {
        var directory = CreateTempDirectory();
        var startedAt = new DateTimeOffset(2026, 6, 23, 21, 30, 12, TimeSpan.Zero);
        using var diagnostics = WorkshopAssemblyDiagnostics.CreateEnabled(directory, startedAt);

        diagnostics.Record(
            "state",
            "Entered SubmittingMaterial.",
            new Dictionary<string, string?>
            {
                ["project"] = "Shark-class Pressure Hull",
                ["material"] = "5378",
            });

        var text = ReadLog(diagnostics.FilePath!);
        Assert.Contains("] state", text);
        Assert.Contains("  Entered SubmittingMaterial.", text);
        Assert.Contains("  project: Shark-class Pressure Hull", text);
        Assert.Contains("  material: 5378", text);
    }

    [Fact]
    public void CreateEnabled_writes_assembly_provenance()
    {
        var directory = CreateTempDirectory();
        using var diagnostics = WorkshopAssemblyDiagnostics.CreateEnabled(
            directory,
            new DateTimeOffset(2026, 6, 23, 21, 30, 12, TimeSpan.Zero));

        var text = ReadLog(diagnostics.FilePath!);

        Assert.Contains("  assemblyName: MarketMafioso", text);
        Assert.Contains("  assemblyVersion:", text);
        Assert.Contains("  informationalVersion:", text);
        Assert.Contains("  assemblyLocation:", text);
    }

    [Fact]
    public void Record_summarizes_repeated_events_when_next_event_arrives()
    {
        var directory = CreateTempDirectory();
        using var diagnostics = WorkshopAssemblyDiagnostics.CreateEnabled(
            directory,
            new DateTimeOffset(2026, 6, 23, 21, 30, 12, TimeSpan.Zero));

        diagnostics.Record("ui-ready-check", "Fabrication station UI is not ready.");
        diagnostics.Record("ui-ready-check", "Fabrication station UI is not ready.");
        diagnostics.Record("ui-ready-check", "Fabrication station UI is not ready.");
        diagnostics.Record("state", "Entered OpeningProject.");

        var text = ReadLog(diagnostics.FilePath!);
        Assert.Contains("] ui-ready-check", text);
        Assert.Contains("  Fabrication station UI is not ready.", text);
        Assert.Contains("] repeat", text);
        Assert.Contains("  Previous event repeated 2 more time(s).", text);
        Assert.Contains("  event: ui-ready-check", text);
        Assert.Contains("] state", text);
    }

    [Fact]
    public void CreateEnabled_adds_suffix_when_timestamp_exists()
    {
        var directory = CreateTempDirectory();
        var startedAt = new DateTimeOffset(2026, 6, 23, 21, 30, 12, TimeSpan.Zero);
        using var first = WorkshopAssemblyDiagnostics.CreateEnabled(directory, startedAt);

        using var second = WorkshopAssemblyDiagnostics.CreateEnabled(directory, startedAt);

        Assert.EndsWith("assembly-20260623-213012-1.log", second.FilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(second.FilePath));
    }

    [Fact]
    public void Complete_marks_log_as_complete()
    {
        var directory = CreateTempDirectory();
        using var diagnostics = WorkshopAssemblyDiagnostics.CreateEnabled(
            directory,
            new DateTimeOffset(2026, 6, 23, 21, 30, 12, TimeSpan.Zero));

        diagnostics.Complete("Workshop assembly complete.");

        var text = ReadLog(diagnostics.FilePath!);
        Assert.Contains("] complete", text);
        Assert.Contains("  Workshop assembly complete.", text);
    }

    [Fact]
    public void Disabled_is_noop()
    {
        var diagnostics = WorkshopAssemblyDiagnostics.Disabled;

        diagnostics.Record("state", "Ignored.");
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
