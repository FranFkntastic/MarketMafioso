using System.Text.Json;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteDiagnosticsTests
{
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

            var summaryText = File.ReadAllText(summary.RouteEventsJsonlPath!);
            var snapshot = ReadEvents(summary.RouteEventsJsonlPath!)
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
            var tracePath = Directory.GetFiles(fullTrace.PackageDirectoryPath!, "trace-*.jsonl").Single();
            var traceText = File.ReadAllText(tracePath);
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

        var worlds = ReadEvents(diagnostics.RouteEventsJsonlPath!)
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

    private static JsonDocument[] ReadEvents(string path) =>
        File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line))
            .ToArray();

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
}
