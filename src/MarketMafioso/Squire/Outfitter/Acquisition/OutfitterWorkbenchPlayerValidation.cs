using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Outfitter.Acquisition;

internal readonly record struct RenderedPlayerAuthorityFingerprint(string Value)
{
    public static RenderedPlayerAuthorityFingerprint Capture(
        RenderedMinerBotanistBaseline baseline,
        RenderedEquipmentResolution equipment)
    {
        if (baseline is not
            {
                Status: RenderedMinerBotanistBaselineStatus.Complete,
                ClassJobId: { } classJobId,
                Level: { } level,
                TotalStats: { } stats,
            } || equipment.Status != RenderedEquipmentResolutionStatus.Complete || equipment.Slots.Count != 12)
        {
            throw new InvalidOperationException("A complete rendered player baseline is required for Workbench authority.");
        }

        var lineage = new StringBuilder()
            .Append(classJobId).Append('|')
            .Append(level).Append('|')
            .Append(stats.Gathering).Append('|')
            .Append(stats.Perception).Append('|')
            .Append(stats.GatheringPoints);
        foreach (var slot in equipment.Slots.OrderBy(value => value.Position))
        {
            lineage.Append('|')
                .Append((int)slot.Position).Append(':')
                .Append(slot.Definition.ItemId).Append(':')
                .Append((int)slot.Quality);
        }

        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lineage.ToString()))));
    }
}

internal sealed record OutfitterWorkbenchPlayerValidation(
    MinerBotanistReadOnlyAdvice Advice,
    string SelectedSolutionId,
    Guid EvidenceGenerationId,
    RenderedPlayerAuthorityFingerprint CapturedPlayer,
    RenderedPlayerAuthorityFingerprint ReobservedPlayer,
    bool DryRunOnly)
{
    public bool IsCurrentFor(
        MinerBotanistReadOnlyAdvice advice,
        string selectedSolutionId,
        OutfitterMarketEvidenceBook evidence) =>
        ReferenceEquals(Advice, advice) &&
        string.Equals(SelectedSolutionId, selectedSolutionId, StringComparison.Ordinal) &&
        EvidenceGenerationId == evidence.GenerationId &&
        CapturedPlayer == ReobservedPlayer;

    public static OutfitterWorkbenchPlayerValidation Create(
        MinerBotanistReadOnlyAdvice advice,
        string selectedSolutionId,
        OutfitterMarketEvidenceBook evidence,
        RenderedPlayerAuthorityFingerprint capturedPlayer,
        RenderedPlayerAuthorityFingerprint reobservedPlayer) =>
        new(advice, selectedSolutionId, evidence.GenerationId, capturedPlayer, reobservedPlayer, false);

#if DEBUG
    public static OutfitterWorkbenchPlayerValidation CreateDryRun(
        MinerBotanistReadOnlyAdvice advice,
        string selectedSolutionId,
        OutfitterMarketEvidenceBook evidence)
    {
        var fixture = new RenderedPlayerAuthorityFingerprint("debug-dry-run-fixture");
        return new(advice, selectedSolutionId, evidence.GenerationId, fixture, fixture, true);
    }
#endif
}
