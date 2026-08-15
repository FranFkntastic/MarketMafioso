using System.Text.Json;
using System.IO.Compression;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteDiagnosticsTests
{
    [Fact]
    public void Complete_FinalizesMachineStreamsButKeepsHotShelfArtifactsReadable()
    {
        var root = CreateTempDirectory();
        using var diagnostics = MarketAcquisitionRouteDiagnostics.CreateEnabled(
            root,
            DateTimeOffset.UtcNow,
            "route",
            MarketAcquisitionRouteDiagnosticsLevel.FullTrace,
            new MarketAcquisitionRouteDiagnosticRetentionPolicy { HotRetentionDays = 14, HotRetentionRunCount = 50 });
        diagnostics.Record("proof", "Lossless machine-stream proof.");
        diagnostics.Complete("Finished.");

        using var manifest = JsonDocument.Parse(File.ReadAllText(diagnostics.ManifestPath!));
        var artifacts = manifest.RootElement.GetProperty("artifacts");
        Assert.EndsWith(".gz", artifacts.GetProperty("routeEventsJsonl").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(diagnostics.FilePath));
        Assert.True(File.Exists(diagnostics.ObservedListingsCsvPath));
        Assert.Contains("Lossless machine-stream proof.", ReadArtifactText(diagnostics.ManifestPath!, "routeEventsJsonl"), StringComparison.Ordinal);
        Assert.Contains("gzip-finalized-artifacts-v1", ReadManifestCapabilities(diagnostics.ManifestPath!));
        Assert.True(File.Exists(Path.Combine(root, MarketAcquisitionRouteDiagnosticRetention.CatalogFileName)));
    }

    [Fact]
    public void Complete_OldSuccessBecomesColdWhilePinnedPackageRemainsHot()
    {
        var root = CreateTempDirectory();
        var old = DateTimeOffset.UtcNow.AddDays(-30);
        var policy = new MarketAcquisitionRouteDiagnosticRetentionPolicy
        {
            HotRetentionDays = 14,
            HotRetentionRunCount = 0,
            MaximumArchivesPerSweep = 20,
        };

        string recentLog;
        using (var recent = MarketAcquisitionRouteDiagnostics.CreateEnabled(root, DateTimeOffset.UtcNow, "route", MarketAcquisitionRouteDiagnosticsLevel.Summary, policy))
        {
            recentLog = recent.FilePath!;
            recent.Complete("Recent.");
        }

        string failedLog;
        using (var failed = MarketAcquisitionRouteDiagnostics.CreateEnabled(root, old.AddMinutes(2), "route", MarketAcquisitionRouteDiagnosticsLevel.Summary, policy))
        {
            failedLog = failed.FilePath!;
            failed.Fail("Failed.");
        }

        string stoppedLog;
        using (var stopped = MarketAcquisitionRouteDiagnostics.CreateEnabled(root, old.AddMinutes(3), "route", MarketAcquisitionRouteDiagnosticsLevel.Summary, policy))
        {
            stoppedLog = stopped.FilePath!;
            stopped.Record("stopped", "Stopped.");
        }

        string incompleteLog;
        using (var incomplete = MarketAcquisitionRouteDiagnostics.CreateEnabled(root, old.AddMinutes(4), "route", MarketAcquisitionRouteDiagnosticsLevel.Summary, policy))
        {
            incompleteLog = incomplete.FilePath!;
        }

        using (var pinned = MarketAcquisitionRouteDiagnostics.CreateEnabled(root, old.AddMinutes(1), "route", MarketAcquisitionRouteDiagnosticsLevel.Summary, policy))
        {
            File.WriteAllText(Path.Combine(pinned.PackageDirectoryPath!, MarketAcquisitionRouteDiagnosticRetention.KeepRawMarkerFileName), string.Empty);
            pinned.Complete("Pinned.");
            Assert.True(File.Exists(pinned.FilePath));
        }

        string coldManifestPath;
        using (var cold = MarketAcquisitionRouteDiagnostics.CreateEnabled(root, old, "route", MarketAcquisitionRouteDiagnosticsLevel.Summary, policy))
        {
            cold.Complete("Cold.");
            coldManifestPath = cold.ManifestPath!;
            var coldManifest = JsonSerializer.Deserialize<MarketAcquisitionRouteDiagnosticManifest>(
                File.ReadAllText(coldManifestPath),
                MarketAcquisitionRouteDiagnosticRetention.JsonOptions)! with
            {
                FinalizedAtUtc = old,
            };
            MarketAcquisitionRouteDiagnosticRetention.WriteManifest(coldManifestPath, coldManifest);
            var futureMaintenance = new MarketAcquisitionRouteDiagnosticRetention(new MarketAcquisitionGzipDiagnosticCompressor());
            futureMaintenance.Maintain(root, policy, DateTimeOffset.UtcNow);
            Assert.False(File.Exists(cold.FilePath));
            Assert.True(File.Exists(cold.FilePath + ".gz"));
        }

        var maintenance = new MarketAcquisitionRouteDiagnosticRetention(new MarketAcquisitionGzipDiagnosticCompressor());
        var secondSweep = maintenance.Maintain(root, policy, DateTimeOffset.UtcNow);
        Assert.Empty(secondSweep.ArchivedRunIds);
        using var coldManifestDocument = JsonDocument.Parse(File.ReadAllText(coldManifestPath));
        Assert.Equal("Cold", coldManifestDocument.RootElement.GetProperty("storageState").GetString());

        using var catalog = JsonDocument.Parse(File.ReadAllText(secondSweep.CatalogPath));
        var entries = catalog.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Contains(entries, entry => entry.GetProperty("storageState").GetString() == "Cold");
        Assert.Contains(entries, entry => entry.GetProperty("pinned").GetBoolean());
        Assert.True(File.Exists(recentLog));
        Assert.True(File.Exists(failedLog));
        Assert.True(File.Exists(stoppedLog));
        Assert.True(File.Exists(incompleteLog));
    }

    [Fact]
    public void RetentionPolicy_NormalizesUnsafeConfigurationValues()
    {
        var normalized = new MarketAcquisitionRouteDiagnosticRetentionPolicy
        {
            HotRetentionDays = -1,
            HotRetentionRunCount = -5,
            MaximumArchivesPerSweep = 500,
        }.Normalize();

        Assert.Equal(1, normalized.HotRetentionDays);
        Assert.Equal(0, normalized.HotRetentionRunCount);
        Assert.Equal(20, normalized.MaximumArchivesPerSweep);
    }

    [Fact]
    public void CompressionFailure_PreservesRawFilesAndLaterMaintenanceRetries()
    {
        var root = CreateTempDirectory();
        string manifestPath;
        string routeEventsPath;
        using (var diagnostics = MarketAcquisitionRouteDiagnostics.CreateEnabled(
                   root,
                   DateTimeOffset.UtcNow,
                   "route",
                   MarketAcquisitionRouteDiagnosticsLevel.Summary,
                   new MarketAcquisitionRouteDiagnosticRetentionPolicy(),
                   new ThrowingCompressor()))
        {
            manifestPath = diagnostics.ManifestPath!;
            routeEventsPath = diagnostics.RouteEventsJsonlPath!;
            diagnostics.Complete("Finished despite maintenance failure.");
        }

        Assert.True(File.Exists(routeEventsPath));
        using (var failedManifest = JsonDocument.Parse(File.ReadAllText(manifestPath)))
        {
            Assert.Equal("Complete", failedManifest.RootElement.GetProperty("captureStatus").GetString());
            Assert.NotEmpty(failedManifest.RootElement.GetProperty("maintenanceWarnings").EnumerateArray());
        }

        var maintenance = new MarketAcquisitionRouteDiagnosticRetention(new MarketAcquisitionGzipDiagnosticCompressor());
        maintenance.Maintain(root, new MarketAcquisitionRouteDiagnosticRetentionPolicy(), DateTimeOffset.UtcNow);
        Assert.False(File.Exists(routeEventsPath));
        Assert.True(File.Exists(routeEventsPath + ".gz"));
        Assert.Contains("Finished despite maintenance failure.", ReadArtifactText(manifestPath, "routeEventsJsonl"), StringComparison.Ordinal);
    }

    [Fact]
    public void Maintenance_UpgradesManifestV1PackageWithoutTouchingLooseLegacyLog()
    {
        var root = CreateTempDirectory();
        var looseLog = Path.Combine(root, "route-legacy.log");
        File.WriteAllText(looseLog, "legacy loose log");
        var package = Path.Combine(root, "route-legacy-package");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "route.log"), "[2026-07-01T00:00:00Z] complete\n");
        File.WriteAllText(Path.Combine(package, "route-events.jsonl"), "{\"eventName\":\"complete\"}\n");
        File.WriteAllText(
            Path.Combine(package, "manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                runId = "route-legacy-package",
                packageKind = "route",
                diagnosticsLevel = "Summary",
                captureStatus = "Complete",
                startedAtUtc = DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                assemblyName = "MarketMafioso",
                assemblyVersion = "1.3.1.0",
                informationalVersion = "1.3.1",
                artifacts = new Dictionary<string, string>
                {
                    ["manifest"] = "manifest.json",
                    ["routeLog"] = "route.log",
                    ["routeEventsJsonl"] = "route-events.jsonl",
                },
                captureCapabilities = new[] { "route-events-jsonl-v1", "route-log" },
            }));

        var maintenance = new MarketAcquisitionRouteDiagnosticRetention(new MarketAcquisitionGzipDiagnosticCompressor());
        maintenance.Maintain(
            root,
            new MarketAcquisitionRouteDiagnosticRetentionPolicy { EnableColdArchive = false },
            DateTimeOffset.UtcNow);

        Assert.True(File.Exists(looseLog));
        Assert.False(File.Exists(Path.Combine(package, "route-events.jsonl")));
        Assert.True(File.Exists(Path.Combine(package, "route-events.jsonl.gz")));
        using var upgraded = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "manifest.json")));
        Assert.Equal(2, upgraded.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void ObservedListingsCsv_PersistsRetainerNameAndProvenance()
    {
        var root = CreateTempDirectory();
        using var diagnostics = MarketAcquisitionRouteDiagnostics.CreateEnabled(
            root,
            DateTimeOffset.UtcNow,
            "route",
            MarketAcquisitionRouteDiagnosticsLevel.FullTrace);
        diagnostics.RecordObservedListings(
            "request:retainer-name",
            "Bahamut",
            "Gaia",
            new MarketAcquisitionWorldItemSubtask
            {
                LineId = "line:1",
                ItemId = 5059,
                ItemName = "Cobalt Ingot",
                WorldName = "Bahamut",
            },
            new MarketAcquisitionLiveCandidatePlan
            {
                Status = MarketAcquisitionLiveCandidateStatuses.Ready,
                Rows =
                [
                    new MarketAcquisitionLiveCandidateRow
                    {
                        Decision = "WouldBuy",
                        LiveListing = new MarketBoardLiveListing
                        {
                            ItemId = 5059,
                            WorldName = "Bahamut",
                            ListingId = "listing:1",
                            RetainerId = "retainer:1",
                            RetainerName = "Bulk Seller",
                            RetainerNameSource = "PreparedListingExactIdentityMatch",
                            UnitPrice = 659,
                            Quantity = 99,
                        },
                    },
                ],
            });
        diagnostics.Complete("Finished.");

        var csv = File.ReadAllLines(diagnostics.ObservedListingsCsvPath!);
        Assert.Contains("retainerNameSource", csv[0], StringComparison.Ordinal);
        Assert.Contains("Bulk Seller", csv[1], StringComparison.Ordinal);
        Assert.Contains("PreparedListingExactIdentityMatch", csv[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_ProjectsHighVolumeDetailsWhileFullTraceRetainsThem()
    {
        var root = CreateTempDirectory();
        var hugeValue = new string('x', 20_000);
        var hugeMessage = new string('m', 20_000);
        var details = new Dictionary<string, string?>
        {
            ["step"] = "BuyListing",
            ["phase"] = "Confirmation",
            ["observed"] = "ConfirmationSubmitted",
            ["outcome"] = "InProgress",
            ["nextAction"] = "AwaitPurchaseOutcome",
            ["candidateListingId"] = "listing-123",
            ["candidateWorld"] = "Anima",
            ["matchedListing"] = "retainer=Fgytujki; price=157; quantity=99",
            ["activationAfterDoubleClickUtc"] = "2026-08-01T04:00:00.0000000+00:00",
            ["activationAfterDoubleClickInfoProxyPreview"] = hugeValue,
            ["exceptionMessage"] = hugeValue,
        };

        using (var summary = MarketAcquisitionRouteDiagnostics.CreateEnabled(
                   Path.Combine(root, "summary"),
                   DateTimeOffset.UtcNow,
                   "route",
                   MarketAcquisitionRouteDiagnosticsLevel.Summary))
        {
            summary.Record("automation-snapshot", hugeMessage, details);
            summary.Complete("Finished.");

            var summaryCapabilities = ReadManifestCapabilities(summary.ManifestPath!);
            Assert.Contains("summary-projection-v1", summaryCapabilities);
            Assert.Contains("summary-omission-markers-v1", summaryCapabilities);

            var summaryText = ReadArtifactText(summary.ManifestPath!, "routeEventsJsonl");
            var snapshot = ReadEvents(summary.ManifestPath!, "routeEventsJsonl")
                .Single(eventDocument => eventDocument.RootElement.GetProperty("eventName").GetString() == "automation-snapshot");
            var summaryDetails = snapshot.RootElement.GetProperty("details");

            Assert.True(summaryText.Length < 10_000);
            Assert.True(snapshot.RootElement.GetProperty("message").GetString()!.Length < 700);
            Assert.Equal("listing-123", summaryDetails.GetProperty("candidateListingId").GetString());
            Assert.Equal("retainer=Fgytujki; price=157; quantity=99", summaryDetails.GetProperty("matchedListing").GetString());
            Assert.Equal("2026-08-01T04:00:00.0000000+00:00", summaryDetails.GetProperty("activationAfterDoubleClickUtc").GetString());
            Assert.False(summaryDetails.TryGetProperty("activationAfterDoubleClickInfoProxyPreview", out _));
            Assert.Equal("1", summaryDetails.GetProperty("summaryOmittedDetailCount").GetString());
            Assert.True(summaryDetails.GetProperty("exceptionMessage").GetString()!.Length < 700);
        }

        using (var fullTrace = MarketAcquisitionRouteDiagnostics.CreateEnabled(
                   Path.Combine(root, "full-trace"),
                   DateTimeOffset.UtcNow,
                   "route",
                   MarketAcquisitionRouteDiagnosticsLevel.FullTrace))
        {
            fullTrace.Record("automation-snapshot", hugeMessage, details);
            fullTrace.Complete("Finished.");

            Assert.Contains("full-trace-authoritative-v1", ReadManifestCapabilities(fullTrace.ManifestPath!));
            var tracePath = Directory.GetFiles(fullTrace.PackageDirectoryPath!, "trace-*.jsonl.gz").Single();
            var traceText = ReadText(tracePath);
            Assert.Contains(hugeMessage, traceText, StringComparison.Ordinal);
            Assert.Contains("activationAfterDoubleClickInfoProxyPreview", traceText, StringComparison.Ordinal);
            Assert.Contains(hugeValue, traceText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Summary_CollapsesConsecutiveStateEventsButKeepsTransitions()
    {
        var root = CreateTempDirectory();
        using var diagnostics = MarketAcquisitionRouteDiagnostics.CreateEnabled(
            root,
            DateTimeOffset.UtcNow,
            "route",
            MarketAcquisitionRouteDiagnosticsLevel.Summary);

        RecordCurrentWorld(diagnostics, "Anima", "2026-08-01T04:00:00.0000000+00:00");
        RecordCurrentWorld(diagnostics, "Anima", "2026-08-01T04:00:00.1000000+00:00");
        RecordCurrentWorld(diagnostics, "Atomos", "2026-08-01T04:00:00.2000000+00:00");
        RecordCurrentWorld(diagnostics, "Atomos", "2026-08-01T04:00:00.3000000+00:00");
        RecordCurrentWorld(diagnostics, "Anima", "2026-08-01T04:00:00.4000000+00:00");
        diagnostics.Complete("Finished.");

        var worlds = ReadEvents(diagnostics.ManifestPath!, "routeEventsJsonl")
            .Where(eventDocument => eventDocument.RootElement.GetProperty("eventName").GetString() == "current-world")
            .Select(eventDocument => eventDocument.RootElement.GetProperty("details").GetProperty("currentWorld").GetString()
                ?? throw new InvalidOperationException("current-world event omitted currentWorld."))
            .ToArray();

        Assert.Equal(["Anima", "Atomos", "Anima"], worlds);
    }

    private static void RecordCurrentWorld(MarketAcquisitionRouteDiagnostics diagnostics, string world, string observedAtUtc) =>
        diagnostics.Record(
            "current-world",
            $"Waiting for Bahamut; current world is {world}.",
            new Dictionary<string, string?>
            {
                ["currentWorld"] = world,
                ["success"] = "False",
                ["observedAtUtc"] = observedAtUtc,
            });

    private static JsonDocument[] ReadEvents(string manifestPath, string role) =>
        ReadArtifactText(manifestPath, role)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();

    private static string ReadArtifactText(string manifestPath, string role)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var fileName = manifest.RootElement.GetProperty("artifacts").GetProperty(role).GetString()
            ?? throw new InvalidOperationException($"Manifest artifact '{role}' was null.");
        return ReadText(Path.Combine(Path.GetDirectoryName(manifestPath)!, fileName));
    }

    private static string ReadText(string path)
    {
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllText(path);

        using var source = File.OpenRead(path);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    private static string[] ReadManifestCapabilities(string path)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        return manifest.RootElement
            .GetProperty("captureCapabilities")
            .EnumerateArray()
            .Select(capability => capability.GetString()
                ?? throw new InvalidOperationException("Manifest capability was null."))
            .ToArray();
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MarketMafioso.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class ThrowingCompressor : IMarketAcquisitionDiagnosticCompressor
    {
        public MarketAcquisitionDiagnosticCompressedFile Compress(string sourcePath) =>
            throw new IOException("Synthetic compression failure.");
    }
}
