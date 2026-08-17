using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Franthropy.Dalamud.Persistence;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionRouteDiagnosticRetentionPolicy
{
    public bool EnableColdArchive { get; init; } = true;
    public int HotRetentionDays { get; init; } = 14;
    public int HotRetentionRunCount { get; init; } = 50;
    public int MaximumArchivesPerSweep { get; init; } = 4;

    public MarketAcquisitionRouteDiagnosticRetentionPolicy Normalize() => this with
    {
        HotRetentionDays = Math.Clamp(HotRetentionDays, 1, 3_650),
        HotRetentionRunCount = Math.Clamp(HotRetentionRunCount, 0, 10_000),
        MaximumArchivesPerSweep = Math.Clamp(MaximumArchivesPerSweep, 1, 20),
    };
}

public sealed class MarketAcquisitionRouteDiagnosticRetention
{
    public const string CatalogFileName = "catalog.json";
    public const string KeepRawMarkerFileName = "keep-raw";

    private static readonly Regex TerminalLogEvent = new(
        @"^\[[^\]]+\]\s+(complete|failed|stopped|input-capture-finalized)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly IMarketAcquisitionDiagnosticCompressor compressor;

    public MarketAcquisitionRouteDiagnosticRetention(IMarketAcquisitionDiagnosticCompressor compressor)
    {
        this.compressor = compressor ?? throw new ArgumentNullException(nameof(compressor));
    }

    public MarketAcquisitionRouteDiagnosticRetentionSweepResult Maintain(
        string rootDirectory,
        MarketAcquisitionRouteDiagnosticRetentionPolicy policy,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        policy = (policy ?? throw new ArgumentNullException(nameof(policy))).Normalize();
        Directory.CreateDirectory(rootDirectory);

        var warnings = new List<string>();
        var packages = LoadPackages(rootDirectory).ToList();
        foreach (var package in packages
                     .Where(IsFinalizedPackage)
                     .Where(NeedsMachineCompaction)
                     .OrderBy(package => PackageTimestamp(package.Manifest))
                     .Take(policy.MaximumArchivesPerSweep))
        {
            try
            {
                CompactFinalizedMachineArtifacts(package);
            }
            catch (Exception exception)
            {
                warnings.Add($"{package.Manifest.RunId}: {exception.Message}");
            }
        }

        packages = LoadPackages(rootDirectory).ToList();
        var successful = packages
            .Where(package => package.Manifest.CaptureStatus.Equals("Complete", StringComparison.OrdinalIgnoreCase) &&
                              ResolveTerminalEvent(package).Equals("complete", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(package => PackageTimestamp(package.Manifest))
            .ToList();
        var protectedRunIds = successful
            .Take(policy.HotRetentionRunCount)
            .Select(package => package.Manifest.RunId)
            .ToHashSet(StringComparer.Ordinal);
        var cutoff = now.Subtract(TimeSpan.FromDays(policy.HotRetentionDays));
        var archived = new List<string>();

        if (policy.EnableColdArchive)
        {
            foreach (var package in successful
                         .Where(package => PackageTimestamp(package.Manifest) < cutoff)
                         .Where(package => !protectedRunIds.Contains(package.Manifest.RunId))
                         .Where(package => !File.Exists(Path.Combine(package.DirectoryPath, KeepRawMarkerFileName)))
                         .Where(package => !string.Equals(package.Manifest.StorageState, "Cold", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(package => PackageTimestamp(package.Manifest))
                         .Take(policy.MaximumArchivesPerSweep))
            {
                try
                {
                    ArchivePackage(package);
                    archived.Add(package.Manifest.RunId);
                }
                catch (Exception exception)
                {
                    warnings.Add($"{package.Manifest.RunId}: {exception.Message}");
                }
            }
        }

        packages = LoadPackages(rootDirectory).ToList();
        WriteCatalog(rootDirectory, packages, policy, now);
        return new MarketAcquisitionRouteDiagnosticRetentionSweepResult
        {
            CatalogPath = Path.Combine(rootDirectory, CatalogFileName),
            PackageCount = packages.Count,
            ArchivedRunIds = archived,
            Warnings = warnings,
        };
    }

    private static bool IsFinalizedPackage(LoadedPackage package) =>
        package.Manifest.CaptureStatus.Equals("Complete", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(ResolveTerminalEvent(package));

    private static bool NeedsMachineCompaction(LoadedPackage package) =>
        (package.Manifest.Artifacts.TryGetValue("routeEventsJsonl", out var routeEventsFileName) &&
         !routeEventsFileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) &&
         File.Exists(Path.Combine(package.DirectoryPath, routeEventsFileName))) ||
        package.Manifest.FullTraceSegments.Any(segment =>
            !segment.FileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(package.DirectoryPath, segment.FileName))) ||
        package.Manifest.StoredArtifacts.Any(artifact =>
            artifact.ContentEncoding.Equals("gzip", StringComparison.OrdinalIgnoreCase) &&
            artifact.FileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(Path.Combine(package.DirectoryPath, artifact.FileName[..^3])));

    private void CompactFinalizedMachineArtifacts(LoadedPackage package)
    {
        var terminalEvent = ResolveTerminalEvent(package);
        var manifest = package.Manifest with
        {
            SchemaVersion = MarketAcquisitionRouteDiagnosticManifest.CurrentSchemaVersion,
            TerminalEventName = string.IsNullOrWhiteSpace(package.Manifest.TerminalEventName)
                ? terminalEvent
                : package.Manifest.TerminalEventName,
            StorageState = string.Equals(package.Manifest.StorageState, "Active", StringComparison.OrdinalIgnoreCase)
                ? "Hot"
                : package.Manifest.StorageState,
            RetentionReason = package.Manifest.RetentionReason ??
                "Legacy finalized package was indexed; human artifacts remain on the hot shelf.",
        };
        WriteManifest(package.ManifestPath, manifest);
        if (manifest.Artifacts.TryGetValue("routeEventsJsonl", out var routeEventsFileName) &&
            !routeEventsFileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            var routeEventsPath = Path.Combine(package.DirectoryPath, routeEventsFileName);
            if (File.Exists(routeEventsPath))
            {
                manifest = ApplyCompressedArtifact(manifest, "routeEventsJsonl", compressor.Compress(routeEventsPath));
                WriteManifest(package.ManifestPath, manifest);
                File.Delete(routeEventsPath);
            }
        }

        for (var index = 0; index < manifest.FullTraceSegments.Count; index++)
        {
            var segment = manifest.FullTraceSegments[index];
            if (segment.FileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                continue;

            var segmentPath = Path.Combine(package.DirectoryPath, segment.FileName);
            if (!File.Exists(segmentPath))
                continue;

            var compressed = compressor.Compress(segmentPath);
            var segments = manifest.FullTraceSegments.ToArray();
            segments[index] = segment with
            {
                FileName = compressed.StoredFileName,
                ContentEncoding = compressed.ContentEncoding,
                StoredByteLength = compressed.StoredByteLength,
                StoredSha256 = compressed.StoredSha256,
            };
            manifest = ApplyCompressedArtifact(manifest with { FullTraceSegments = segments }, $"fullTrace:{segment.FirstSequence}", compressed);
            WriteManifest(package.ManifestPath, manifest);
            File.Delete(segmentPath);
        }

        RemoveVerifiedRawDuplicates(package.DirectoryPath, manifest);
    }

    private void ArchivePackage(LoadedPackage package)
    {
        CompactFinalizedMachineArtifacts(package);
        package = package with
        {
            Manifest = JsonSerializer.Deserialize<MarketAcquisitionRouteDiagnosticManifest>(
                File.ReadAllText(package.ManifestPath),
                JsonOptions) ?? throw new InvalidDataException($"Unable to reload manifest '{package.ManifestPath}'."),
        };
        RemoveVerifiedRawDuplicates(package.DirectoryPath, package.Manifest);
        var manifest = package.Manifest with
        {
            SchemaVersion = MarketAcquisitionRouteDiagnosticManifest.CurrentSchemaVersion,
            StorageState = "Archiving",
            RetentionReason = "Older than both configured hot-shelf windows.",
        };
        WriteManifest(package.ManifestPath, manifest);

        foreach (var role in new[] { "routeLog", "observedListingsCsv", "purchaseRecordsCsv" })
        {
            if (!manifest.Artifacts.TryGetValue(role, out var fileName) || fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = Path.Combine(package.DirectoryPath, fileName);
            if (!File.Exists(path))
                continue;

            var compressed = compressor.Compress(path);
            manifest = ApplyCompressedArtifact(manifest, role, compressed);
            WriteManifest(package.ManifestPath, manifest);
            File.Delete(path);
        }

        manifest = manifest with
        {
            StorageState = "Cold",
            RetentionReason = "Successful package is outside the configured hot-day and hot-run windows.",
        };
        WriteManifest(package.ManifestPath, manifest);
    }

    private void RemoveVerifiedRawDuplicates(
        string packageDirectory,
        MarketAcquisitionRouteDiagnosticManifest manifest)
    {
        foreach (var artifact in manifest.StoredArtifacts.Where(artifact =>
                     artifact.ContentEncoding.Equals("gzip", StringComparison.OrdinalIgnoreCase) &&
                     artifact.FileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)))
        {
            var sourcePath = Path.Combine(packageDirectory, artifact.FileName[..^3]);
            if (!File.Exists(sourcePath))
                continue;

            var verified = compressor.Compress(sourcePath);
            if (!verified.StoredFileName.Equals(artifact.FileName, StringComparison.OrdinalIgnoreCase) ||
                !verified.RawSha256.Equals(artifact.RawSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Raw duplicate '{sourcePath}' does not match its manifest artifact.");
            }

            File.Delete(sourcePath);
        }
    }

    internal static MarketAcquisitionRouteDiagnosticManifest ApplyCompressedArtifact(
        MarketAcquisitionRouteDiagnosticManifest manifest,
        string role,
        MarketAcquisitionDiagnosticCompressedFile compressed)
    {
        var artifacts = new SortedDictionary<string, string>(
            manifest.Artifacts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal)
        {
            [role] = compressed.StoredFileName,
        };
        var stored = manifest.StoredArtifacts
            .Where(artifact => !artifact.Role.Equals(role, StringComparison.Ordinal))
            .Append(new MarketAcquisitionRouteDiagnosticStoredArtifact
            {
                Role = role,
                FileName = compressed.StoredFileName,
                ContentEncoding = compressed.ContentEncoding,
                RawByteLength = compressed.RawByteLength,
                RawSha256 = compressed.RawSha256,
                StoredByteLength = compressed.StoredByteLength,
                StoredSha256 = compressed.StoredSha256,
            })
            .OrderBy(artifact => artifact.Role, StringComparer.Ordinal)
            .ToArray();
        return manifest with
        {
            SchemaVersion = MarketAcquisitionRouteDiagnosticManifest.CurrentSchemaVersion,
            Artifacts = artifacts,
            StoredArtifacts = stored,
        };
    }

    private static IEnumerable<LoadedPackage> LoadPackages(string rootDirectory)
    {
        foreach (var manifestPath in Directory.EnumerateFiles(
                     rootDirectory,
                     "manifest.json",
                     SearchOption.AllDirectories))
        {
            var directory = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            MarketAcquisitionRouteDiagnosticManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<MarketAcquisitionRouteDiagnosticManifest>(File.ReadAllText(manifestPath), JsonOptions);
            }
            catch
            {
                continue;
            }

            if (manifest != null)
                yield return new LoadedPackage(directory, manifestPath, manifest);
        }
    }

    private static string ResolveTerminalEvent(LoadedPackage package)
    {
        if (!string.IsNullOrWhiteSpace(package.Manifest.TerminalEventName))
            return package.Manifest.TerminalEventName;

        if (!package.Manifest.Artifacts.TryGetValue("routeLog", out var fileName))
            return string.Empty;
        var path = Path.Combine(package.DirectoryPath, fileName);
        if (!File.Exists(path) || fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var tail = ReadTail(path, 128 * 1024);
        var matches = TerminalLogEvent.Matches(tail);
        return matches.Count == 0 ? string.Empty : matches[^1].Groups[1].Value.ToLowerInvariant();
    }

    private static string ReadTail(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = (int)Math.Min(stream.Length, maximumBytes);
        stream.Seek(-length, SeekOrigin.End);
        var buffer = new byte[length];
        _ = stream.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer);
    }

    private static DateTimeOffset PackageTimestamp(MarketAcquisitionRouteDiagnosticManifest manifest) =>
        manifest.FinalizedAtUtc ?? manifest.StartedAtUtc;

    private static void WriteCatalog(
        string rootDirectory,
        IReadOnlyList<LoadedPackage> packages,
        MarketAcquisitionRouteDiagnosticRetentionPolicy policy,
        DateTimeOffset now)
    {
        var protectedRunIds = packages
            .Where(package => package.Manifest.CaptureStatus.Equals("Complete", StringComparison.OrdinalIgnoreCase) &&
                              ResolveTerminalEvent(package).Equals("complete", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(package => PackageTimestamp(package.Manifest))
            .Take(policy.HotRetentionRunCount)
            .Select(package => package.Manifest.RunId)
            .ToHashSet(StringComparer.Ordinal);
        var entries = packages
            .OrderByDescending(package => PackageTimestamp(package.Manifest))
            .Select(package => BuildCatalogEntry(
                rootDirectory,
                package,
                policy,
                now,
                protectedRunIds.Contains(package.Manifest.RunId)))
            .ToArray();
        AtomicJsonFile.Write(
            Path.Combine(rootDirectory, CatalogFileName),
            new MarketAcquisitionRouteDiagnosticCatalog
            {
                SchemaVersion = 1,
                UpdatedAtUtc = now,
                Entries = entries,
            },
            JsonOptions);
    }

    private static MarketAcquisitionRouteDiagnosticCatalogEntry BuildCatalogEntry(
        string rootDirectory,
        LoadedPackage package,
        MarketAcquisitionRouteDiagnosticRetentionPolicy policy,
        DateTimeOffset now,
        bool protectedByRunWindow)
    {
        var terminalEvent = ResolveTerminalEvent(package);
        var pinned = File.Exists(Path.Combine(package.DirectoryPath, KeepRawMarkerFileName));
        var reason = package.Manifest.RetentionReason;
        if (pinned)
            reason = "Pinned by keep-raw marker.";
        else if (!package.Manifest.CaptureStatus.Equals("Complete", StringComparison.OrdinalIgnoreCase))
            reason = "Incomplete capture remains hot.";
        else if (!terminalEvent.Equals("complete", StringComparison.OrdinalIgnoreCase))
            reason = $"Terminal outcome '{terminalEvent}' remains hot.";
        else if (!policy.EnableColdArchive)
            reason = "Cold archive is disabled.";
        else if (protectedByRunWindow)
            reason = "Inside the configured hot-run window.";
        else if (PackageTimestamp(package.Manifest) >= now.Subtract(TimeSpan.FromDays(policy.HotRetentionDays)))
            reason = "Inside the configured hot-day window.";

        var describedFiles = package.Manifest.StoredArtifacts
            .ToDictionary(artifact => artifact.FileName, artifact => artifact, StringComparer.OrdinalIgnoreCase);
        long storedBytes = 0;
        long rawBytes = 0;
        foreach (var path in Directory.EnumerateFiles(package.DirectoryPath)
                     .Where(path => !Path.GetFileName(path).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                     .Where(path => !Path.GetFileName(path).Equals(KeepRawMarkerFileName, StringComparison.OrdinalIgnoreCase)))
        {
            var storedLength = new FileInfo(path).Length;
            storedBytes += storedLength;
            rawBytes += describedFiles.TryGetValue(Path.GetFileName(path), out var descriptor)
                ? descriptor.RawByteLength
                : storedLength;
        }
        return new MarketAcquisitionRouteDiagnosticCatalogEntry
        {
            RunId = package.Manifest.RunId,
            PackageDirectory = Path.GetRelativePath(rootDirectory, package.DirectoryPath),
            PackageKind = package.Manifest.PackageKind,
            StartedAtUtc = package.Manifest.StartedAtUtc,
            FinalizedAtUtc = package.Manifest.FinalizedAtUtc,
            CaptureStatus = package.Manifest.CaptureStatus,
            TerminalEventName = terminalEvent,
            DiagnosticsLevel = package.Manifest.DiagnosticsLevel,
            InformationalVersion = package.Manifest.InformationalVersion,
            StorageState = package.Manifest.StorageState,
            RetentionReason = reason,
            Pinned = pinned,
            Worlds = package.Manifest.Worlds,
            ItemIds = package.Manifest.ItemIds,
            RawByteLength = rawBytes,
            StoredByteLength = storedBytes,
            Artifacts = package.Manifest.Artifacts,
            FullTraceSegments = package.Manifest.FullTraceSegments,
            StoredArtifacts = package.Manifest.StoredArtifacts,
        };
    }

    internal static void WriteManifest(string path, MarketAcquisitionRouteDiagnosticManifest manifest) =>
        AtomicJsonFile.Write(path, manifest, JsonOptions);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private sealed record LoadedPackage(
        string DirectoryPath,
        string ManifestPath,
        MarketAcquisitionRouteDiagnosticManifest Manifest);
}

public sealed record MarketAcquisitionRouteDiagnosticRetentionSweepResult
{
    public required string CatalogPath { get; init; }
    public required int PackageCount { get; init; }
    public IReadOnlyList<string> ArchivedRunIds { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record MarketAcquisitionRouteDiagnosticCatalog
{
    public required int SchemaVersion { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public IReadOnlyList<MarketAcquisitionRouteDiagnosticCatalogEntry> Entries { get; init; } = [];
}

public sealed record MarketAcquisitionRouteDiagnosticCatalogEntry
{
    public required string RunId { get; init; }
    public required string PackageDirectory { get; init; }
    public required string PackageKind { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? FinalizedAtUtc { get; init; }
    public required string CaptureStatus { get; init; }
    public required string TerminalEventName { get; init; }
    public required string DiagnosticsLevel { get; init; }
    public string? InformationalVersion { get; init; }
    public required string StorageState { get; init; }
    public string? RetentionReason { get; init; }
    public bool Pinned { get; init; }
    public IReadOnlyList<string> Worlds { get; init; } = [];
    public IReadOnlyList<uint> ItemIds { get; init; } = [];
    public long RawByteLength { get; init; }
    public long StoredByteLength { get; init; }
    public IReadOnlyDictionary<string, string> Artifacts { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<MarketAcquisitionRouteDiagnosticTraceSegment> FullTraceSegments { get; init; } = [];
    public IReadOnlyList<MarketAcquisitionRouteDiagnosticStoredArtifact> StoredArtifacts { get; init; } = [];
}
