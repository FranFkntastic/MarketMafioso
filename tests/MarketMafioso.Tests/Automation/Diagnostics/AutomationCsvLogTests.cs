using MarketMafioso.Automation.Diagnostics;

namespace MarketMafioso.Tests.Automation.Diagnostics;

public sealed class AutomationCsvLogTests
{
    [Fact]
    public void Create_writes_header_and_rows_with_csv_escaping()
    {
        var directory = CreateTempDirectory();
        var startedAt = new DateTimeOffset(2026, 7, 2, 12, 34, 56, TimeSpan.Zero);

        using var csv = AutomationCsvLog.Create(
            directory,
            startedAt,
            filePrefix: "rows",
            headers: ["name", "message", "empty"]);

        csv.WriteRow(["plain", "contains,comma", null]);
        csv.WriteRow(["quote", "contains \"quote\"", string.Empty]);

        var text = ReadLog(csv.FilePath);
        Assert.Contains("name,message,empty", text, StringComparison.Ordinal);
        Assert.Contains("plain,\"contains,comma\",", text, StringComparison.Ordinal);
        Assert.Contains("quote,\"contains \"\"quote\"\"\",", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_uses_available_timestamped_path()
    {
        var directory = CreateTempDirectory();
        var startedAt = new DateTimeOffset(2026, 7, 2, 12, 34, 56, TimeSpan.Zero);
        using var first = AutomationCsvLog.Create(directory, startedAt, "rows", ["name"]);

        using var second = AutomationCsvLog.Create(directory, startedAt, "rows", ["name"]);

        Assert.EndsWith("rows-20260702-123456-1.csv", second.FilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(second.FilePath));
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
