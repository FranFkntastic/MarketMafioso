using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public enum SquireAssessment
{
    Protected,
    Candidate,
    EvaluationFailure,
    Unsupported,
}

public enum SquireDisposition
{
    Keep,
    ExpertDelivery,
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

public sealed record SquireDispositionCapabilities(
    bool? DesynthesisUnlocked,
    bool? MateriaRetrievalUnlocked = null);

public sealed record SquireDuplicateRetentionRule(
    uint ItemId,
    bool IsHighQuality,
    int MinimumCopies);

public sealed record SquireDuplicateStatus(
    int OwnedCopies,
    int UserMinimumCopies,
    int GearsetRequiredCopies)
{
    public int EffectiveMinimumCopies => Math.Max(UserMinimumCopies, GearsetRequiredCopies);
    public int CopiesAboveFloor => Math.Max(0, OwnedCopies - EffectiveMinimumCopies);
}

public sealed record SquireProtectionPolicy(
    bool ProtectSignedGear = false,
    bool ProtectFutureLevelingGear = false,
    bool ProtectBlueAndPurpleGear = true,
    IReadOnlySet<uint>? CleanupExcludedItemIds = null,
    bool AllowRiskyMateriaRetrieval = false,
    IReadOnlyList<SquireDuplicateRetentionRule>? DuplicateRetentionRules = null)
{
    public int MinimumCopiesToKeep(uint itemId, bool isHighQuality) => DuplicateRetentionRules?
        .Where(rule => rule.ItemId == itemId && rule.IsHighQuality == isHighQuality)
        .Select(rule => Math.Max(0, rule.MinimumCopies))
        .DefaultIfEmpty(0)
        .Max() ?? 0;
}

public sealed record SquireCandidate(
    EquipmentInstanceSnapshot Instance,
    EquipmentItemDefinition Definition,
    SquireAssessment Assessment,
    SquireDisposition RecommendedDisposition,
    IReadOnlySet<SquireDisposition> SupportedDispositions,
    IReadOnlyList<SquireReason> Reasons,
    EquipmentUseAnalysis? UseAnalysis,
    SquireDuplicateStatus? DuplicateStatus = null)
{
    public bool IsExecutable => Assessment == SquireAssessment.Candidate && SupportedDispositions.Count > 0;
}

public sealed record SquireAnalysis(
    CharacterEquipmentSnapshot Snapshot,
    IReadOnlyList<SquireCandidate> Candidates)
{
    public bool IsActionable => Snapshot.Diagnostics.IsComplete;
}
