using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MarketMafioso.MarketAcquisition;

public enum MarketAcquisitionRouteDiagnosticPackageFormat
{
    JsonlV1,
    LegacyRouteLog,
}

public sealed record MarketAcquisitionRouteDiagnosticPackage
{
    public required string RunId { get; init; }

    public required string PackageDirectoryPath { get; init; }

    public required MarketAcquisitionRouteDiagnosticPackageFormat Format { get; init; }

    public required int SchemaVersion { get; init; }

    public required string CaptureStatus { get; init; }

    public bool IsComplete => string.Equals(CaptureStatus, "Complete", StringComparison.OrdinalIgnoreCase);

    public required IReadOnlyList<MarketAcquisitionRouteDiagnosticEvent> Events { get; init; }
}

public static class MarketAcquisitionRouteDiagnosticPackageReader
{
    private const int LegacyRouteLogSchemaVersion = 0;
    private const string RouteEventsFileName = "route-events.jsonl";
    private const string LegacyRouteLogFileName = "route.log";
    private const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static MarketAcquisitionRouteDiagnosticPackage Read(string packageDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectoryPath);

        var routeEventsPath = Path.Combine(packageDirectoryPath, RouteEventsFileName);
        if (File.Exists(routeEventsPath))
        {
            var manifestPath = Path.Combine(packageDirectoryPath, ManifestFileName);
            if (!File.Exists(manifestPath))
                throw new InvalidDataException($"The JSONL diagnostics package has no {ManifestFileName}.");

            return ReadJsonlPackage(packageDirectoryPath, routeEventsPath, ReadManifest(manifestPath));
        }

        var routeLogPath = Path.Combine(packageDirectoryPath, LegacyRouteLogFileName);
        if (File.Exists(routeLogPath))
            return ReadLegacyRouteLogPackage(packageDirectoryPath, routeLogPath);

        throw new FileNotFoundException(
            $"No {RouteEventsFileName} or {LegacyRouteLogFileName} was found in the diagnostics package.",
            packageDirectoryPath);
    }

    private static MarketAcquisitionRouteDiagnosticPackage ReadJsonlPackage(
        string packageDirectoryPath,
        string routeEventsPath,
        MarketAcquisitionRouteDiagnosticManifest manifest)
    {
        var events = new List<MarketAcquisitionRouteDiagnosticEvent>();
        long previousSequence = 0;
        long previousElapsedMilliseconds = -1;

        foreach (var line in File.ReadLines(routeEventsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            MarketAcquisitionRouteDiagnosticEvent? routeEvent;
            try
            {
                routeEvent = JsonSerializer.Deserialize<MarketAcquisitionRouteDiagnosticEvent>(line, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Invalid route event JSON in {routeEventsPath}.", exception);
            }

            if (routeEvent == null ||
                routeEvent.SchemaVersion != MarketAcquisitionRouteDiagnosticEvent.CurrentSchemaVersion ||
                string.IsNullOrWhiteSpace(routeEvent.EventName) ||
                routeEvent.Sequence != previousSequence + 1 ||
                routeEvent.ElapsedMilliseconds < previousElapsedMilliseconds)
            {
                throw new InvalidDataException($"Invalid or out-of-order route event in {routeEventsPath}.");
            }

            previousSequence = routeEvent.Sequence;
            previousElapsedMilliseconds = routeEvent.ElapsedMilliseconds;
            events.Add(routeEvent with
            {
                Details = routeEvent.Details ?? new Dictionary<string, string>(),
            });
        }

        if (events.Count == 0 || !string.Equals(events[0].EventName, "start", StringComparison.Ordinal))
            throw new InvalidDataException($"The route event stream in {routeEventsPath} has no start event.");
        if (string.Equals(manifest.CaptureStatus, "Complete", StringComparison.Ordinal) &&
            !IsTerminalEvent(events[^1].EventName))
        {
            throw new InvalidDataException($"The completed route event stream in {routeEventsPath} has no terminal event.");
        }

        return new MarketAcquisitionRouteDiagnosticPackage
        {
            RunId = manifest.RunId,
            PackageDirectoryPath = packageDirectoryPath,
            Format = MarketAcquisitionRouteDiagnosticPackageFormat.JsonlV1,
            SchemaVersion = MarketAcquisitionRouteDiagnosticEvent.CurrentSchemaVersion,
            CaptureStatus = manifest.CaptureStatus,
            Events = events,
        };
    }

    private static MarketAcquisitionRouteDiagnosticPackage ReadLegacyRouteLogPackage(
        string packageDirectoryPath,
        string routeLogPath)
    {
        var events = new List<MarketAcquisitionRouteDiagnosticEvent>();
        LegacyRouteEventBuilder? current = null;
        long nextSequence = 0;

        foreach (var line in File.ReadLines(routeLogPath))
        {
            if (TryParseLegacyHeader(line, out var elapsed, out var eventName))
            {
                AddLegacyEvent(events, current, ref nextSequence);
                current = new LegacyRouteEventBuilder(elapsed, eventName);
                continue;
            }

            if (current == null || string.IsNullOrWhiteSpace(line))
                continue;

            var value = line.Trim();
            if (current.Message == null)
            {
                current.Message = value;
                continue;
            }

            var separatorIndex = value.IndexOf(": ", StringComparison.Ordinal);
            if (separatorIndex <= 0)
                continue;

            current.Details[value[..separatorIndex]] = value[(separatorIndex + 2)..];
        }

        AddLegacyEvent(events, current, ref nextSequence);

        if (events.Count == 0 || !string.Equals(events[0].EventName, "start", StringComparison.Ordinal))
            throw new InvalidDataException($"The legacy route log in {routeLogPath} has no start event.");

        var startedAtUtc = ResolveLegacyStartedAtUtc(events);
        var replayEvents = events
            .Select(routeEvent => routeEvent with
            {
                RecordedAtUtc = startedAtUtc.AddMilliseconds(routeEvent.ElapsedMilliseconds),
            })
            .ToArray();

        return new MarketAcquisitionRouteDiagnosticPackage
        {
            RunId = Path.GetFileName(packageDirectoryPath),
            PackageDirectoryPath = packageDirectoryPath,
            Format = MarketAcquisitionRouteDiagnosticPackageFormat.LegacyRouteLog,
            SchemaVersion = LegacyRouteLogSchemaVersion,
            CaptureStatus = IsTerminalEvent(replayEvents[^1].EventName) ? "Complete" : "Incomplete",
            Events = replayEvents,
        };
    }

    private static MarketAcquisitionRouteDiagnosticManifest ReadManifest(string manifestPath)
    {
        MarketAcquisitionRouteDiagnosticManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<MarketAcquisitionRouteDiagnosticManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid route diagnostics manifest in {manifestPath}.", exception);
        }

        if (manifest == null ||
            manifest.SchemaVersion != MarketAcquisitionRouteDiagnosticEvent.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(manifest.RunId) ||
            string.IsNullOrWhiteSpace(manifest.PackageKind) ||
            manifest.CaptureStatus is not ("Active" or "Complete" or "Incomplete") ||
            manifest.Artifacts == null ||
            !manifest.Artifacts.TryGetValue("routeEventsJsonl", out var routeEventsArtifact) ||
            !string.Equals(routeEventsArtifact, RouteEventsFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Invalid or unsupported route diagnostics manifest in {manifestPath}.");
        }

        return manifest;
    }

    private static bool IsTerminalEvent(string eventName) =>
        eventName is "complete" or "failed" or "stopped" or "input-capture-finalized";

    private static bool TryParseLegacyHeader(string line, out TimeSpan elapsed, out string eventName)
    {
        elapsed = default;
        eventName = string.Empty;
        if (!line.StartsWith("[", StringComparison.Ordinal))
            return false;

        var closingBracketIndex = line.IndexOf("] ", StringComparison.Ordinal);
        if (closingBracketIndex <= 1)
            return false;

        if (!TimeSpan.TryParseExact(
                line[1..closingBracketIndex],
                [@"mm\:ss\.fff", @"hh\:mm\:ss\.fff"],
                CultureInfo.InvariantCulture,
                out elapsed))
        {
            return false;
        }

        eventName = line[(closingBracketIndex + 2)..].Trim();
        return !string.IsNullOrWhiteSpace(eventName);
    }

    private static void AddLegacyEvent(
        ICollection<MarketAcquisitionRouteDiagnosticEvent> events,
        LegacyRouteEventBuilder? current,
        ref long nextSequence)
    {
        if (current == null)
            return;

        events.Add(new MarketAcquisitionRouteDiagnosticEvent
        {
            SchemaVersion = LegacyRouteLogSchemaVersion,
            Sequence = ++nextSequence,
            ElapsedMilliseconds = (long)current.Elapsed.TotalMilliseconds,
            RecordedAtUtc = DateTimeOffset.UnixEpoch.Add(current.Elapsed),
            EventName = current.EventName,
            Message = current.Message ?? string.Empty,
            Details = current.Details,
        });
    }

    private static DateTimeOffset ResolveLegacyStartedAtUtc(
        IReadOnlyList<MarketAcquisitionRouteDiagnosticEvent> events)
    {
        if (events.Count > 0 &&
            events[0].Details.TryGetValue("startedAt", out var startedAtText) &&
            DateTimeOffset.TryParse(
                startedAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var startedAt))
        {
            return startedAt.ToUniversalTime();
        }

        return DateTimeOffset.UnixEpoch;
    }

    private sealed class LegacyRouteEventBuilder(TimeSpan elapsed, string eventName)
    {
        public TimeSpan Elapsed { get; } = elapsed;

        public string EventName { get; } = eventName;

        public string? Message { get; set; }

        public SortedDictionary<string, string> Details { get; } = new(StringComparer.Ordinal);
    }
}
