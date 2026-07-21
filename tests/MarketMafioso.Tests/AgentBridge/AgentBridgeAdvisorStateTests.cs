using System.Text.Json;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.AgentBridge;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.AgentBridge;

public sealed class AgentBridgeAdvisorStateTests
{
    [Fact]
    public void AbstainedState_SerializesWithoutComplexDictionaryKeys()
    {
        var source = new MinerBotanistAdvisorSessionState(
            MinerBotanistAdvisorSessionStage.Abstained,
            "Stopped safely.",
            "Rendered evidence.",
            2,
            2,
            GathererAdvisorStatFamily.OrdinaryResourceContext,
            new(
                MinerBotanistAdvisorStatus.Abstained,
                MinerBotanistReadOnlyAdvisor.AdvisoryRule,
                null,
                null,
                new Dictionary<string, AdvisorAuthorityAssessment>(),
                new Dictionary<Franthropy.Dalamud.Equipment.EquipmentOfferAllocationKey, Franthropy.Dalamud.Equipment.EquipmentExactSolverOffer>(),
                "No nomination."),
            false,
            DateTimeOffset.Parse("2026-07-18T23:00:00Z"));

        var projected = AgentBridgeAdvisorState.Create(source);
        var json = JsonSerializer.Serialize(projected);

        Assert.Equal("OrdinaryResourceBenchmark", projected.Context);
        Assert.Contains("\"Context\":\"OrdinaryResourceBenchmark\"", json, StringComparison.Ordinal);
        Assert.Contains("No nomination.", json, StringComparison.Ordinal);
        Assert.DoesNotContain("OffersByAllocation", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CrafterState_SerializesItsFamilyOwnedContextWithoutAGatheringEnum()
    {
        var source = new MinerBotanistAdvisorSessionState(
            MinerBotanistAdvisorSessionStage.Idle,
            "Ready.",
            "Coverage unavailable.",
            0,
            null,
            CrafterAdvisorStatFamily.OrdinaryCraftContext,
            null,
            false,
            DateTimeOffset.Parse("2026-07-21T00:00:00Z"));

        var projected = AgentBridgeAdvisorState.Create(source);

        Assert.Equal("OrdinaryCraftBenchmark", projected.Context);
    }

    [Fact]
    public void PhysicalRangedState_SerializesItsRoleOwnedContext()
    {
        var source = new MinerBotanistAdvisorSessionState(
            MinerBotanistAdvisorSessionStage.Idle,
            "Ready.",
            "BRD/MCH/DNC coverage.",
            0,
            null,
            PhysicalRangedAdvisorStatFamily.GeneralCombatContext,
            null,
            false,
            DateTimeOffset.Parse("2026-07-21T00:00:00Z"));

        var projected = AgentBridgeAdvisorState.Create(source);
        var json = JsonSerializer.Serialize(projected);

        Assert.Equal("GeneralCombat", projected.Context);
        Assert.Contains("\"Context\":\"GeneralCombat\"", json, StringComparison.Ordinal);
        Assert.Contains("BRD/MCH/DNC coverage.", json, StringComparison.Ordinal);
    }

    [Fact]
    public void LargeFrontier_DefaultsToBoundedNominationWindowAndSupportsPaging()
    {
        var solutions = Enumerable.Range(0, 2_030).Select(Solution).ToArray();
        var nomination = solutions[1_500];
        var advice = new MinerBotanistReadOnlyAdvice(
            MinerBotanistAdvisorStatus.Complete,
            MinerBotanistReadOnlyAdvisor.AdvisoryRule,
            new(
                new(solutions, [], [], []),
                new(1_159_355, 0, 0, 0, 0, solutions.Length, solutions.Length, 16, "baseline", TimeSpan.FromSeconds(36.42)),
                []),
            nomination,
            solutions.ToDictionary(
                solution => solution.Candidate.SolutionId,
                _ => new AdvisorAuthorityAssessment(true, UpgradeAssessment.ClearImprovement, [], []),
                StringComparer.Ordinal),
            new Dictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer>(),
            "Complete.");
        var state = new MinerBotanistAdvisorSessionState(
            MinerBotanistAdvisorSessionStage.Complete,
            "Complete.",
            "Exact coverage.",
            262,
            262,
            GathererAdvisorStatFamily.OrdinaryResourceContext,
            advice,
            false,
            DateTimeOffset.Parse("2026-07-21T00:00:00Z"));

        var projected = AgentBridgeAdvisorState.Create(state);
        var paged = AgentBridgeAdvisorState.Create(state, 2_000);
        var json = JsonSerializer.Serialize(projected);

        Assert.Equal(2_030, projected.Advice!.FrontierCount);
        Assert.Equal(AgentBridgeAdvisorState.DefaultFrontierPageSize, projected.Advice.FrontierReturnedCount);
        Assert.True(projected.Advice.FrontierHasMore);
        Assert.Contains(projected.Advice.Frontier, solution => solution.SolutionId == nomination.Candidate.SolutionId);
        Assert.Equal(2_000, paged.Advice!.FrontierOffset);
        Assert.Equal("solution-02000", paged.Advice.Frontier[0].SolutionId);
        Assert.DoesNotContain("solution-00000", json, StringComparison.Ordinal);
        Assert.True(json.Length < 32_000, $"Default Advisor bridge payload was {json.Length:N0} characters.");
    }

    private static EquipmentDecisionSolution Solution(int index) => new(
        new($"solution-{index:D5}", []),
        new(
            new("test", "1"),
            new("test", 16, 100, "Test", []),
            index,
            new(index, index, []),
            UpgradeAssessment.ClearImprovement,
            [],
            [],
            [],
            EquipmentEvaluationConfidence.High,
            []),
        checked((ulong)index),
        new(0, 0, 0),
        new(0, 0, 0),
        []);
}
