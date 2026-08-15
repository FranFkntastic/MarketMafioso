using System;
using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionRouteDiagnosticEvent
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }

    public required long Sequence { get; init; }

    public required long ElapsedMilliseconds { get; init; }

    public required DateTimeOffset RecordedAtUtc { get; init; }

    public required string EventName { get; init; }

    public required string Message { get; init; }

    public required IReadOnlyDictionary<string, string> Details { get; init; }
}

public sealed record MarketAcquisitionRouteDiagnosticManifest
{
    public const int CurrentSchemaVersion = 2;

    public required int SchemaVersion { get; init; }

    public required string RunId { get; init; }

    public required string PackageKind { get; init; }

    public string DiagnosticsLevel { get; init; } = MarketAcquisitionRouteDiagnosticsLevel.FullTrace.ToString();

    public required string CaptureStatus { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public required string? AssemblyName { get; init; }

    public required string? AssemblyVersion { get; init; }

    public required string? InformationalVersion { get; init; }

    public required IReadOnlyDictionary<string, string> Artifacts { get; init; }

    public required IReadOnlyList<string> CaptureCapabilities { get; init; }

    public IReadOnlyList<MarketAcquisitionRouteDiagnosticTraceSegment> FullTraceSegments { get; init; } = [];

    public DateTimeOffset? FinalizedAtUtc { get; init; }

    public string? TerminalEventName { get; init; }

    public string StorageState { get; init; } = "Active";

    public string? RetentionReason { get; init; }

    public IReadOnlyList<string> MaintenanceWarnings { get; init; } = [];

    public IReadOnlyList<string> Worlds { get; init; } = [];

    public IReadOnlyList<uint> ItemIds { get; init; } = [];

    public IReadOnlyList<MarketAcquisitionRouteDiagnosticStoredArtifact> StoredArtifacts { get; init; } = [];
}

public sealed record MarketAcquisitionRouteDiagnosticTraceSegment
{
    public required string FileName { get; init; }

    public required long FirstSequence { get; init; }

    public required long LastSequence { get; init; }

    public required long ByteLength { get; init; }

    public required string Sha256 { get; init; }

    public string ContentEncoding { get; init; } = "identity";

    public long? StoredByteLength { get; init; }

    public string? StoredSha256 { get; init; }
}

public sealed record MarketAcquisitionRouteDiagnosticStoredArtifact
{
    public required string Role { get; init; }
    public required string FileName { get; init; }
    public string ContentEncoding { get; init; } = "identity";
    public required long RawByteLength { get; init; }
    public required string RawSha256 { get; init; }
    public required long StoredByteLength { get; init; }
    public required string StoredSha256 { get; init; }
}
