using System.Collections.Generic;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public enum SquireAssessment
{
    Protected,
    Candidate,
    NeedsReview,
    Unsupported,
}

public enum SquireDisposition
{
    Keep,
    Desynthesize,
    VendorSell,
    Discard,
    Unsupported,
}

public enum SquireReasonSeverity
{
    Information,
    Warning,
    Blocking,
}

public sealed record SquireReason(string Code, string Message, SquireReasonSeverity Severity);

public sealed record SquireDispositionCapabilities(bool? DesynthesisUnlocked);

public sealed record SquireProtectionPolicy(bool ProtectSignedGear = false);

public sealed record SquireCandidate(
    EquipmentInstanceSnapshot Instance,
    EquipmentItemDefinition Definition,
    SquireAssessment Assessment,
    SquireDisposition RecommendedDisposition,
    IReadOnlySet<SquireDisposition> SupportedDispositions,
    IReadOnlyList<SquireReason> Reasons,
    EquipmentUseAnalysis? UseAnalysis)
{
    public bool IsExecutable => Assessment == SquireAssessment.Candidate && SupportedDispositions.Count > 0;
}

public sealed record SquireAnalysis(
    CharacterEquipmentSnapshot Snapshot,
    IReadOnlyList<SquireCandidate> Candidates)
{
    public bool IsActionable => Snapshot.Diagnostics.IsComplete;
}
