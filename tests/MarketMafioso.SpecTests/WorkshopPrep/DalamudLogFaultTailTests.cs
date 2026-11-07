using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

public sealed class DalamudLogFaultTailTests : IDisposable
{
    private const string FaultLine =
        "2026-11-06 23:16:07.281 -05:00 [ERR] [AgentVT] Caught exception when calling original Agent ReceiveEvent. This may be a bug in the game or another plugin hooking this method.\n";

    private readonly string path = Path.Combine(Path.GetTempPath(), $"mmf-fault-tail-{Guid.NewGuid():N}.log");

    [Fact]
    public void Faults_written_before_the_run_started_are_ignored()
    {
        File.WriteAllText(path, FaultLine + FaultLine);
        var tail = new DalamudLogFaultTail(path);
        tail.MarkStart();

        Assert.Equal(0, tail.ReadNewFaults());
    }

    [Fact]
    public void Appended_faults_are_counted_once()
    {
        File.WriteAllText(path, "start\n");
        var tail = new DalamudLogFaultTail(path);
        tail.MarkStart();

        File.AppendAllText(path, "[WRN] unrelated\n" + FaultLine + FaultLine);
        Assert.Equal(2, tail.ReadNewFaults());
        Assert.Equal(0, tail.ReadNewFaults());
    }

    [Fact]
    public void Fault_line_split_across_two_flushes_is_still_counted()
    {
        File.WriteAllText(path, "start\n");
        var tail = new DalamudLogFaultTail(path);
        tail.MarkStart();

        var split = FaultLine.IndexOf("original Agent", StringComparison.Ordinal);
        File.AppendAllText(path, FaultLine[..split]);
        Assert.Equal(0, tail.ReadNewFaults());

        File.AppendAllText(path, FaultLine[split..]);
        Assert.Equal(1, tail.ReadNewFaults());
    }

    [Fact]
    public void Rotated_log_is_read_from_the_top()
    {
        File.WriteAllText(path, new string('x', 4096));
        var tail = new DalamudLogFaultTail(path);
        tail.MarkStart();

        File.WriteAllText(path, FaultLine);
        Assert.Equal(1, tail.ReadNewFaults());
    }

    [Fact]
    public void Missing_log_reports_no_faults_and_is_unavailable()
    {
        var tail = new DalamudLogFaultTail(path);
        tail.MarkStart();

        Assert.False(tail.IsAvailable);
        Assert.Equal(0, tail.ReadNewFaults());
        Assert.Equal(0, new DalamudLogFaultTail(null).ReadNewFaults());
    }

    public void Dispose()
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
