using System.Text.Json;
using System.Text.Json.Serialization;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class MinerBotanistOracleChallengeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void FrozenChallengeBookPassesEveryStratumIndependently()
    {
        var book = LoadBook();
        Assert.Equal("squire-min-btn-challenge/v1", book.SchemaVersion);
        Assert.Equal(MinerBotanistUtilityProfile.ProfileVersion, book.ProfileVersion);

        var failures = new List<string>();
        foreach (var challenge in book.Cases)
        {
            var profile = new MinerBotanistUtilityProfile(
                challenge.Context,
                challenge.Baseline,
                challenge.ClassJobId);
            var candidate = profile.Evaluate(challenge.Candidate);
            var authority = profile.AssessAuthorityForCalibration(
                candidate,
                challenge.AdditionalCostGil,
                challenge.EvidenceComplete,
                challenge.PatchMatches,
                challenge.HasUnmodeledRelevantEffect);

            if (candidate.Assessment != challenge.ExpectedAssessment)
                failures.Add($"{challenge.CaseId}: assessment {candidate.Assessment}, expected {challenge.ExpectedAssessment}");
            if (authority.AdvisorMayConsider != challenge.ExpectedAdvisorMayConsider)
                failures.Add($"{challenge.CaseId}: authority {authority.AdvisorMayConsider}, expected {challenge.ExpectedAdvisorMayConsider}");
            if (!authority.GainedCapabilityIds.SequenceEqual(challenge.ExpectedGainedCapabilityIds, StringComparer.Ordinal))
                failures.Add($"{challenge.CaseId}: gained [{string.Join(',', authority.GainedCapabilityIds)}], expected [{string.Join(',', challenge.ExpectedGainedCapabilityIds)}]");
            if (challenge.MaximumUtilityDelta is { } maximum &&
                candidate.UtilityScore - profile.BaselineEvaluation.UtilityScore > maximum)
            {
                failures.Add($"{challenge.CaseId}: utility delta leaked above {maximum}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        var strata = book.Cases.GroupBy(challenge => challenge.Stratum, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.True(strata["ordinary-resource"] >= 3);
        Assert.True(strata["legendary-node"] >= 4);
        Assert.True(strata["collectable"] >= 3);
        Assert.True(strata["authority-failure"] >= 3);
        Assert.True(strata["unsupported"] >= 1);
    }

    [Fact]
    public void HoldoutLineageIsNotADevelopmentGuideMirror()
    {
        var book = LoadBook();
        var ordinaryCases = book.Cases.Where(challenge => challenge.Stratum == "ordinary-resource").ToArray();

        Assert.NotEmpty(ordinaryCases);
        Assert.All(ordinaryCases, challenge => Assert.Equal("official-mechanics-holdout", challenge.Lineage));
        Assert.DoesNotContain(ordinaryCases, challenge => challenge.Lineage == "development-guide");
    }

    private static ChallengeBook LoadBook()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Squire", "OutfitterUtility", "min-btn-7.5-challenge-book.json");
        return JsonSerializer.Deserialize<ChallengeBook>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("MIN/BTN challenge book was empty.");
    }

    private sealed record ChallengeBook(
        string SchemaVersion,
        string ProfileVersion,
        IReadOnlyList<ChallengeCase> Cases);

    private sealed record ChallengeCase(
        string CaseId,
        string Stratum,
        string Lineage,
        MinerBotanistUtilityContextKind Context,
        uint ClassJobId,
        MinerBotanistUtilityStats Baseline,
        MinerBotanistUtilityStats Candidate,
        ulong AdditionalCostGil,
        UpgradeAssessment ExpectedAssessment,
        bool ExpectedAdvisorMayConsider,
        IReadOnlyList<string> ExpectedGainedCapabilityIds,
        bool EvidenceComplete = true,
        bool PatchMatches = true,
        bool HasUnmodeledRelevantEffect = false,
        double? MaximumUtilityDelta = null);
}
