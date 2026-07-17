using System.Text.Json;
using System.Text.Json.Serialization;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class MinerBotanistModelGearsetOracleTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void FrozenOracleIsVersionedTraceableAndExplicitlyExcludedFromRuntimePolicy()
    {
        var oracle = LoadOracle();

        Assert.Equal("squire-min-btn-model-gearset-oracle/v1", oracle.SchemaVersion);
        Assert.Equal(MinerBotanistUtilityProfile.ProfileVersion, oracle.ProfileVersion);
        Assert.Contains("runtime must not load", oracle.RuntimePolicy, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(oracle.Sources.Count, oracle.Sources.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(oracle.Sources, source =>
        {
            Assert.True(Uri.TryCreate(source.Url, UriKind.Absolute, out _));
            Assert.False(string.IsNullOrWhiteSpace(source.Lineage));
            Assert.False(string.IsNullOrWhiteSpace(source.Locator));
        });

        var sourceIds = oracle.Sources.Select(source => source.SourceId).ToHashSet(StringComparer.Ordinal);
        Assert.All(
            oracle.Loadouts.Where(loadout => loadout.Origin == "published-model"),
            loadout => Assert.Contains(loadout.SourceId!, sourceIds));
        Assert.All(
            oracle.Loadouts.Where(loadout => loadout.Origin == "derived-adversarial"),
            loadout => Assert.NotNull(loadout.DerivedFromLoadoutId));
        Assert.All(
            oracle.Loadouts.Where(loadout => loadout.Origin != "derived-adversarial"),
            loadout => Assert.Contains(loadout.SourceId!, sourceIds));
        Assert.DoesNotContain(oracle.Loadouts, loadout => loadout.LoadoutId.Contains("budget", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(oracle.Loadouts, loadout => loadout.Assumptions.Any(value =>
            value.Contains("scrip", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("cosmic", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void PublishedModelFamilyAndAdversarialVariantsMatchEveryExpectedDecision()
    {
        var oracle = LoadOracle();
        var loadouts = oracle.Loadouts.ToDictionary(loadout => loadout.LoadoutId, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var comparison in oracle.Comparisons)
        {
            var baseline = loadouts[comparison.BaselineLoadoutId];
            var candidateLoadout = loadouts[comparison.CandidateLoadoutId];
            var profile = new MinerBotanistUtilityProfile(
                comparison.Context,
                baseline.Stats,
                MinerBotanistUtilityProfile.MinerClassJobId);
            var candidate = profile.Evaluate(candidateLoadout.Stats);
            var authority = profile.AssessAuthorityForCalibration(
                candidate,
                comparison.CandidateHasAdditionalAcquisitionCost ? 1UL : 0UL,
                hasUnmodeledRelevantEffect: comparison.HasUnmodeledRelevantEffect);

            if (candidate.Assessment != comparison.ExpectedAssessment)
                failures.Add($"{comparison.CaseId}: assessment {candidate.Assessment}, expected {comparison.ExpectedAssessment}");
            if (authority.AdvisorMayConsider != comparison.ExpectedAdvisorMayConsider)
                failures.Add($"{comparison.CaseId}: authority {authority.AdvisorMayConsider}, expected {comparison.ExpectedAdvisorMayConsider}");
            if (!authority.GainedCapabilityIds.SequenceEqual(comparison.ExpectedGainedCapabilityIds, StringComparer.Ordinal))
            {
                failures.Add(
                    $"{comparison.CaseId}: gained [{string.Join(',', authority.GainedCapabilityIds)}], " +
                    $"expected [{string.Join(',', comparison.ExpectedGainedCapabilityIds)}]");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void PublishedCostUtilityNeighborhoodSurvivesWhileDerivedNoiseIsDominated()
    {
        var oracle = LoadOracle();
        var profile = new MinerBotanistUtilityProfile(
            MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
            new(0, 0, 0),
            MinerBotanistUtilityProfile.MinerClassJobId);
        var evaluated = oracle.Loadouts
            .Select(loadout => new EvaluatedLoadout(
                loadout.LoadoutId,
                loadout.AcquisitionBurdenRank,
                profile.Evaluate(loadout.Stats).UtilityScore))
            .ToArray();

        var frontier = evaluated
            .Where(candidate => !evaluated.Any(other =>
                other.LoadoutId != candidate.LoadoutId &&
                other.AcquisitionBurdenRank <= candidate.AcquisitionBurdenRank &&
                other.UtilityScore >= candidate.UtilityScore &&
                (other.AcquisitionBurdenRank < candidate.AcquisitionBurdenRank ||
                    other.UtilityScore > candidate.UtilityScore)))
            .Select(candidate => candidate.LoadoutId)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(oracle.ExpectedLegendaryFrontierLoadoutIds.Order(StringComparer.Ordinal), frontier);
        Assert.DoesNotContain("derived-high-regression", frontier);
        Assert.DoesNotContain("derived-high-cost-only", frontier);
    }

    private static ModelGearsetOracle LoadOracle()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Squire",
            "OutfitterUtility",
            "min-btn-7.51-model-gearset-oracle.json");
        return JsonSerializer.Deserialize<ModelGearsetOracle>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("MIN/BTN model-gearset oracle was empty.");
    }

    private sealed record ModelGearsetOracle(
        string SchemaVersion,
        string OracleVersion,
        string ProfileVersion,
        DateTimeOffset ResearchedAtUtc,
        string RuntimePolicy,
        IReadOnlyList<OracleSource> Sources,
        IReadOnlyList<OracleLoadout> Loadouts,
        IReadOnlyList<OracleComparison> Comparisons,
        IReadOnlyList<string> ExpectedLegendaryFrontierLoadoutIds);

    private sealed record OracleSource(
        string SourceId,
        string Lineage,
        string Role,
        string Url,
        string Locator);

    private sealed record OracleLoadout(
        string LoadoutId,
        string Label,
        string Origin,
        int AcquisitionBurdenRank,
        MinerBotanistUtilityStats Stats,
        IReadOnlyList<string> Assumptions,
        string? SourceId = null,
        string? DerivedFromLoadoutId = null);

    private sealed record OracleComparison(
        string CaseId,
        MinerBotanistUtilityContextKind Context,
        string BaselineLoadoutId,
        string CandidateLoadoutId,
        bool CandidateHasAdditionalAcquisitionCost,
        UpgradeAssessment ExpectedAssessment,
        bool ExpectedAdvisorMayConsider,
        IReadOnlyList<string> ExpectedGainedCapabilityIds,
        bool HasUnmodeledRelevantEffect = false);

    private sealed record EvaluatedLoadout(
        string LoadoutId,
        int AcquisitionBurdenRank,
        double UtilityScore);
}
