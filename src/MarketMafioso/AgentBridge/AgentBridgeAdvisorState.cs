using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.AgentBridge;

public sealed record AgentBridgeAdvisorSelection(
    string Position,
    uint ItemId,
    string ItemName,
    string Quality,
    string SourceKind,
    string SourceLabel,
    string? ObservationId,
    string? World,
    ulong AcquisitionCostGil);

public sealed record AgentBridgeAdvisorSolution(
    string SolutionId,
    ulong AcquisitionCostGil,
    double UtilityScore,
    bool AdvisorMayConsider,
    IReadOnlyList<string> AuthorityReasons,
    int WorldVisits,
    int VendorStops,
    int PurchaseTransactions,
    IReadOnlyList<AgentBridgeAdvisorSelection> Selections);

public sealed record AgentBridgeAdvisorAdvice(
    string Status,
    string AdvisoryRule,
    string Diagnostic,
    int OfferCount,
    int FrontierCount,
    int FrontierOffset,
    int FrontierReturnedCount,
    bool FrontierHasMore,
    long ExpandedStateCount,
    long RetainedCompletePathCount,
    double SolverElapsedMilliseconds,
    AgentBridgeAdvisorSolution? Nomination,
    IReadOnlyList<AgentBridgeAdvisorSolution> Frontier);

public sealed record AgentBridgeAdvisorState(
    string Stage,
    string Message,
    string CoverageLabel,
    int Completed,
    int? Total,
    string Context,
    string ProfileId,
    string ProfileVersion,
    string CalibrationState,
    uint? ClassJobId,
    AgentBridgeAdvisorAdvice? Advice,
    bool AdviceIsRetained,
    DateTimeOffset UpdatedAtUtc,
    bool IsBusy)
{
    public const int DefaultFrontierPageSize = 16;

    public static AgentBridgeAdvisorState Create(MinerBotanistAdvisorSessionState state, int? frontierOffset = null)
    {
        AgentBridgeAdvisorSolution Convert(EquipmentDecisionSolution solution)
        {
            var authority = state.Advice!.AuthorityBySolutionId.TryGetValue(solution.Candidate.SolutionId, out var resolvedAuthority)
                ? resolvedAuthority
                : new AdvisorAuthorityAssessment(false, solution.Utility.Assessment, [], ["Authority was not published for this frontier solution."]);
            var selections = solution.Candidate.Selections.Select(selection =>
            {
                var offer = state.Advice!.OffersByAllocation[selection.AllocationKey];
                return new AgentBridgeAdvisorSelection(
                    selection.Position.ToString(),
                    offer.Offer.Definition.ItemId,
                    offer.Offer.Definition.Name,
                    offer.Offer.ResolvedQuality.ToString(),
                    offer.Offer.SourceKind.ToString(),
                    offer.Offer.SourceLabel,
                    selection.ObservationId,
                    offer.WorldVisitKey,
                    offer.AcquisitionCostGil);
            }).ToArray();
            return new(
                solution.Candidate.SolutionId,
                solution.AcquisitionCostGil,
                solution.Utility.UtilityScore,
                authority.AdvisorMayConsider,
                authority.Reasons,
                solution.Burden.WorldVisits,
                solution.Burden.VendorStops,
                solution.Burden.PurchaseTransactions,
                selections);
        }

        var family = AdvisorStatFamilies.All.FirstOrDefault(candidate =>
            candidate.ProfileDescriptor.Contexts.Any(context => string.Equals(context.Id, state.Context.Id, StringComparison.Ordinal)));
        var profile = family?.ProfileDescriptor;
        AgentBridgeAdvisorAdvice? advice = null;
        uint? classJobId = null;
        if (state.Advice is { } source)
        {
            var frontier = source.Frontier?.Pareto.Frontier ?? [];
            classJobId = (source.Nomination ?? frontier.FirstOrDefault())?.Utility.Context.ClassJobId;
            var presentation = frontier.Count == 0 ? null : new AdvisorFrontierPresentation(source.Frontier!.Pareto);
            var window = presentation is null
                ? null
                : frontierOffset is { } offset
                    ? presentation.WindowFrom(offset, DefaultFrontierPageSize)
                    : presentation.WindowAround(source.Nomination?.Candidate.SolutionId, DefaultFrontierPageSize);
            advice = new(
                source.Status.ToString(),
                source.AdvisoryRule,
                source.Diagnostic,
                source.OffersByAllocation.Count,
                frontier.Count,
                window?.Offset ?? 0,
                window?.Solutions.Count ?? 0,
                window is { HasPrevious: true } or { HasNext: true },
                source.Frontier?.Diagnostics.ExpandedStateCount ?? 0,
                source.Frontier?.Diagnostics.RetainedCompletePathCount ?? 0,
                source.Frontier?.Diagnostics.Elapsed.TotalMilliseconds ?? 0,
                source.Nomination is null ? null : Convert(source.Nomination),
                window?.Solutions.Select(Convert).ToArray() ?? []);
        }

        return new(
            state.Stage.ToString(),
            state.Message,
            state.CoverageLabel,
            state.Completed,
            state.Total,
            state.Context.ConfigurationValue,
            profile?.Id ?? string.Empty,
            profile?.Version ?? string.Empty,
            profile?.CalibrationState.ToString() ?? AdvisorProfileCalibrationState.Experimental.ToString(),
            classJobId,
            advice,
            state.AdviceIsRetained,
            state.UpdatedAtUtc,
            state.IsBusy);
    }
}
