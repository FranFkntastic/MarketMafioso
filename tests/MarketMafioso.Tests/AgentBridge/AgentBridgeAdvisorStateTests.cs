using System.Text.Json;
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
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
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

        var json = JsonSerializer.Serialize(AgentBridgeAdvisorState.Create(source));

        Assert.Contains("No nomination.", json, StringComparison.Ordinal);
        Assert.DoesNotContain("OffersByAllocation", json, StringComparison.Ordinal);
    }
}
