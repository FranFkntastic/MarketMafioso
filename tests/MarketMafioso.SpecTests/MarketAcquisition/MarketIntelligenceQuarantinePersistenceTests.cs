using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketIntelligenceQuarantinePersistenceTests
{
    [Fact]
    public void Quarantine_PreservesOriginalAndImmutableBackupAcrossRestartAndRepeatedMarking()
    {
        using var fixture = new Fixture();
        var original = fixture.Seed();
        var journal = File.ReadAllBytes(fixture.Path);
        var quarantined = fixture.Outbox.QuarantineConflict(original.Id);
        var after = File.ReadAllBytes(fixture.Path);
        Assert.Equal(original, quarantined with { Quarantine = null });
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(original.PayloadJson))), quarantined.Quarantine!.PayloadSha256);
        Assert.Equal(journal, File.ReadAllBytes(fixture.Backup));
        Assert.Equal(quarantined, new FileMarketAcquisitionReportOutbox(fixture.Path).QuarantineConflict(original.Id));
        Assert.Equal(after, File.ReadAllBytes(fixture.Path));
        Assert.Equal(journal, File.ReadAllBytes(fixture.Backup));
        Assert.Throws<InvalidOperationException>(() => fixture.Outbox.Remove(original.Id));
        Assert.Equal(after, File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public void CrashAfterDurableAppendBeforeMemoryUpdate_RestoresQuarantineWithoutRetry()
    {
        using var fixture = new Fixture();
        var original = fixture.Seed();
        var crash = new FileMarketAcquisitionReportOutbox(fixture.Path, _ => throw new IOException("Simulated process interruption"));
        Assert.Throws<IOException>(() => crash.QuarantineConflict(original.Id));
        Assert.Null(Assert.Single(crash.Snapshot()).Quarantine);
        var recovered = Assert.Single(new FileMarketAcquisitionReportOutbox(fixture.Path).Snapshot());
        Assert.NotNull(recovered.Quarantine);
        Assert.Equal(original, recovered with { Quarantine = null });
    }

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

    [Fact]
    public void QuarantineCannotChangeOriginalEvenWithRecomputedPayloadDigest()
    {
        using var fixture = new Fixture();
        var original = fixture.Seed();
        fixture.Outbox.QuarantineConflict(original.Id);
        var lines = File.ReadAllLines(fixture.Path);
        var record = JsonNode.Parse(lines[^1])!;
        const string changed = "{\"changed\":true}";
        record["entry"]!["payloadJson"] = changed;
        record["entry"]!["quarantine"]!["payloadSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(changed)));
        File.WriteAllText(fixture.Path, lines[0] + Environment.NewLine + record.ToJsonString() + Environment.NewLine);
        Assert.Throws<InvalidDataException>(() => new FileMarketAcquisitionReportOutbox(fixture.Path));
    }

    [Fact]
    public void BackupFailure_DoesNotMarkOrChangePendingJournal()
    {
        using var fixture = new Fixture();
        var original = fixture.Seed();
        var before = File.ReadAllBytes(fixture.Path);
        Directory.CreateDirectory(fixture.Backup);
        Assert.ThrowsAny<IOException>(() => fixture.Outbox.QuarantineConflict(original.Id));
        Assert.Null(Assert.Single(fixture.Outbox.Snapshot()).Quarantine);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path));
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
