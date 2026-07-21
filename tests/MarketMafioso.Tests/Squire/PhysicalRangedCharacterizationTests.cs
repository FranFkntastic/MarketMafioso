using System.Text.Json;
using System.Text.Json.Serialization;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class PhysicalRangedCharacterizationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Frozen_synthetic_book_characterizes_shared_role_behavior()
    {
        var book = LoadBook();
        Assert.Equal("squire-physical-ranged-characterization/v1", book.SchemaVersion);
        Assert.Equal(PhysicalRangedUtilityProfile.ProfileVersion, book.ProfileVersion);
        Assert.Equal(AdvisorProfileCalibrationState.Experimental, PhysicalRangedUtilityProfile.CalibrationState);

        var failures = new List<string>();
        foreach (var challenge in book.Cases)
        {
            var profile = new PhysicalRangedUtilityProfile(
                challenge.Context,
                challenge.Baseline,
                challenge.ClassJobId,
                challenge.CharacterLevel);
            var candidate = profile.Evaluate(challenge.Candidate);
            var authority = profile.AssessAuthorityForCharacterization(
                candidate,
                challenge.AdditionalCostGil,
                challenge.EvidenceComplete,
                challenge.PatchMatches,
                challenge.HasUnmodeledRelevantEffect);
            if (candidate.Assessment != challenge.ExpectedAssessment)
                failures.Add($"{challenge.CaseId}: assessment {candidate.Assessment}, expected {challenge.ExpectedAssessment}");
            if (authority.AdvisorMayConsider != challenge.ExpectedCharacterizationAuthority)
                failures.Add($"{challenge.CaseId}: authority {authority.AdvisorMayConsider}, expected {challenge.ExpectedCharacterizationAuthority}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Characterization_book_is_not_an_independent_calibration_holdout()
    {
        var book = LoadBook();

        Assert.All(book.Cases, challenge => Assert.Equal("synthetic-characterization", challenge.Lineage));
        Assert.DoesNotContain(book.Cases, challenge => challenge.Lineage.Contains("holdout", StringComparison.OrdinalIgnoreCase));
    }

    private static CharacterizationBook LoadBook()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Squire", "OutfitterUtility", "physical-ranged-7.51-characterization-book.json");
        return JsonSerializer.Deserialize<CharacterizationBook>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("Physical-ranged characterization book was empty.");
    }

    private sealed record CharacterizationBook(
        string SchemaVersion,
        string ProfileVersion,
        IReadOnlyList<CharacterizationCase> Cases);

    private sealed record CharacterizationCase(
        string CaseId,
        string Lineage,
        PhysicalRangedUtilityContextKind Context,
        uint ClassJobId,
        PhysicalRangedUtilityStats Baseline,
        PhysicalRangedUtilityStats Candidate,
        ulong AdditionalCostGil,
        UpgradeAssessment ExpectedAssessment,
        bool ExpectedCharacterizationAuthority,
        uint CharacterLevel = 100,
        bool EvidenceComplete = true,
        bool PatchMatches = true,
        bool HasUnmodeledRelevantEffect = false);
}
