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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compaction_PreservesQuarantineAndBackup(bool denyReplacement)
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

        // Old sendable reports must survive the same rewrite that expires conflicts.
        var oldTime = DateTimeOffset.UtcNow.AddDays(-40);
        var expired = recovered with
        {
            Id = "expired-conflict",
            Quarantine = recovered.Quarantine! with { QuarantinedAtUtc = oldTime },
        };
        var pending = original with { Id = "old-pending", EnqueuedAtUtc = oldTime };
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        foreach (var entry in new[] { expired, pending })
            File.AppendAllText(fixture.Path,
                System.Text.Json.JsonSerializer.Serialize(new { operation = "put", entry }, options) + Environment.NewLine);
        var uncompacted = File.ReadAllBytes(fixture.Path);
        var warnings = new List<string>();
        var information = new List<string>();
        using (var heldFile = denyReplacement
                   ? new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.Read)
                   : null)
        {
            var maintained = new FileMarketAcquisitionReportOutbox(fixture.Path,
                information.Add, (_, message) => warnings.Add(message));
            Assert.Contains(pending, maintained.Snapshot());
            Assert.Contains(recovered, maintained.Snapshot());
            if (denyReplacement)
            {
                Assert.Contains(expired, maintained.Snapshot());
                Assert.Equal(uncompacted, File.ReadAllBytes(fixture.Path));
                Assert.Single(warnings);
                Assert.Empty(information);
            }
            else
            {
                Assert.DoesNotContain(expired, maintained.Snapshot());
                Assert.Empty(warnings);
                Assert.Single(information);
            }
        }
        var reloaded = new FileMarketAcquisitionReportOutbox(fixture.Path).Snapshot();
        Assert.Equal(2, reloaded.Count);
        Assert.Contains(pending, reloaded);
        Assert.Contains(recovered, reloaded);
        Assert.Equal(before, File.ReadAllBytes(fixture.Backup));
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
