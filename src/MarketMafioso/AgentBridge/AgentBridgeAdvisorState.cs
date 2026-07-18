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
    long ExpandedStateCount,
    long ExactCompleteVariantCount,
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
    AgentBridgeAdvisorAdvice? Advice,
    bool AdviceIsRetained,
    DateTimeOffset UpdatedAtUtc,
    bool IsBusy)
{
    public static AgentBridgeAdvisorState Create(MinerBotanistAdvisorSessionState state)
    {
        AgentBridgeAdvisorSolution Convert(EquipmentDecisionSolution solution)
        {
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
                solution.Burden.WorldVisits,
                solution.Burden.VendorStops,
                solution.Burden.PurchaseTransactions,
                selections);
        }

        AgentBridgeAdvisorAdvice? advice = null;
        if (state.Advice is { } source)
        {
            var frontier = source.Frontier?.Pareto.Frontier ?? [];
            advice = new(
                source.Status.ToString(),
                source.AdvisoryRule,
                source.Diagnostic,
                source.OffersByAllocation.Count,
                frontier.Count,
                source.Frontier?.Diagnostics.ExpandedStateCount ?? 0,
                source.Frontier?.Diagnostics.ExactCompleteVariantCount ?? 0,
                source.Frontier?.Diagnostics.Elapsed.TotalMilliseconds ?? 0,
                source.Nomination is null ? null : Convert(source.Nomination),
                frontier.Take(128).Select(Convert).ToArray());
        }

        return new(
            state.Stage.ToString(),
            state.Message,
            state.CoverageLabel,
            state.Completed,
            state.Total,
            state.Context.ToString(),
            advice,
            state.AdviceIsRetained,
            state.UpdatedAtUtc,
            state.IsBusy);
    }
}
