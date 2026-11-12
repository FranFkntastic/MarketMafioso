using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketIntelligenceQuarantinePersistenceTests
{

    [Fact]
    public void TornQuarantineAppend_RetainsPendingOriginalAndBackupThenCanMarkDurably()
    {
        using var fixture = new Fixture();
        var original = fixture.Seed();
        var before = File.ReadAllBytes(fixture.Path);
        fixture.Outbox.QuarantineConflict(original.Id);
        var complete = File.ReadAllBytes(fixture.Path);
        File.WriteAllBytes(fixture.Path, complete[..^5]);
        var recovered = new FileMarketAcquisitionReportOutbox(fixture.Path);
        Assert.Equal(original, Assert.Single(recovered.Snapshot()));
        Assert.Equal(before, File.ReadAllBytes(fixture.Backup));
        recovered.QuarantineConflict(original.Id);
        Assert.NotNull(Assert.Single(new FileMarketAcquisitionReportOutbox(fixture.Path).Snapshot()).Quarantine);
        Assert.Equal(before, File.ReadAllBytes(fixture.Backup));
    }

    [Fact]
    public void Compaction_PreservesQuarantineAndBackup()
    {
        using var fixture = new Fixture();
        var original = fixture.Seed();
        var firstPut = File.ReadAllText(fixture.Path);
        var before = File.ReadAllBytes(fixture.Path);
        fixture.Outbox.QuarantineConflict(original.Id);
        File.AppendAllText(fixture.Path, string.Concat(Enumerable.Repeat(firstPut, 1024)));
        var recovered = Assert.Single(new FileMarketAcquisitionReportOutbox(fixture.Path).Snapshot());
        Assert.NotNull(recovered.Quarantine);
        Assert.Equal(original, recovered with { Quarantine = null });
        Assert.Single(File.ReadAllLines(fixture.Path));
        Assert.Equal(before, File.ReadAllBytes(fixture.Backup));
        Assert.Equal(recovered, Assert.Single(new FileMarketAcquisitionReportOutbox(fixture.Path).Snapshot()));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"mmf-quarantine-{Guid.NewGuid():N}"));
        public string Path => System.IO.Path.Combine(directory, "outbox.jsonl");
        public string Backup => Path + ".pre-quarantine.bak";
        public FileMarketAcquisitionReportOutbox Outbox { get; }
        public Fixture()
        {
            Directory.CreateDirectory(directory);
            Outbox = new FileMarketAcquisitionReportOutbox(Path);
        }
        public MarketAcquisitionReportOutboxEntry Seed() => Outbox.Put("first", "market-evidence.v2", "occurrence-one",
            new { idempotencyKey = "original-key", occurrenceId = "occurrence-one", privateValue = "fixture-only" });
        public void Dispose()
        {
            if (!directory.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture path escaped temporary directory");
            Directory.Delete(directory, recursive: true);
        }
    }
}
