using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

/// <summary>Positional stat triple; each family defines the semantic meaning of the components.</summary>
public sealed record AdvisorStatTriple(long First, long Second, long Third);

/// <summary>
/// The seam between the shared advisor machinery (offers, solver, nomination, evidence)
/// and one job family's calibration: which stats matter, how vectors are built, how the
/// utility model is constructed, and how authority is assessed.
/// </summary>
public interface IAdvisorStatFamily
{
    IReadOnlySet<uint> SupportedClassJobIds { get; }
    string CoverageJobLabel { get; }
    bool IsRelevantSemantic(EquipmentStatSemantic semantic);
    IEquipmentExactSolverUtilityModel CreateUtilityModel(
        string contextId,
        AdvisorStatTriple baseline,
        AdvisorStatTriple? fixedStats,
        uint classJobId,
        uint characterLevel);
    AdvisorAuthorityAssessment AssessAuthority(
        IEquipmentExactSolverUtilityModel model,
        EquipmentUtilityEvaluation candidate,
        ulong additionalCostGil);
    MinerBotanistSolverReplay? CaptureReplay(
        EquipmentExactFrontierRequest request,
        string contextId,
        uint classJobId,
        uint characterLevel,
        MinerBotanistUtilityStats offerBaseline,
        MinerBotanistUtilityStats fixedStats);
    EquipmentSolverUtilityVector VectorFromRendered(IReadOnlyDictionary<string, int> stats, IReadOnlyDictionary<string, int> materia);
    EquipmentSolverUtilityVector VectorFromDefinition(EquipmentStatProfile profile);
    AdvisorStatTriple ToTriple(MinerBotanistUtilityStats stats);
}

public sealed class GathererAdvisorStatFamily : IAdvisorStatFamily
{
    public static readonly GathererAdvisorStatFamily Instance = new();

    private static readonly IReadOnlySet<uint> Jobs = new HashSet<uint>
    {
        MinerBotanistUtilityProfile.MinerClassJobId,
        MinerBotanistUtilityProfile.BotanistClassJobId,
    };

    public IReadOnlySet<uint> SupportedClassJobIds => Jobs;
    public string CoverageJobLabel => "MIN/BTN";

    public bool IsRelevantSemantic(EquipmentStatSemantic semantic) => semantic is
        EquipmentStatSemantic.Gathering or EquipmentStatSemantic.Perception or EquipmentStatSemantic.GatheringPoints;

    public IEquipmentExactSolverUtilityModel CreateUtilityModel(
        string contextId,
        AdvisorStatTriple baseline,
        AdvisorStatTriple? fixedStats,
        uint classJobId,
        uint characterLevel)
    {
        var contextKind = contextId switch
        {
            MinerBotanistUtilityProfile.LegendaryContextId => MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
            MinerBotanistUtilityProfile.CollectableContextId => MinerBotanistUtilityContextKind.CollectableEfficiency,
            _ => MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
        };
        return new MinerBotanistUtilityProfile(
            contextKind,
            FromTriple(baseline),
            classJobId,
            characterLevel,
            fixedStats is null ? null : FromTriple(fixedStats));
    }

    public AdvisorAuthorityAssessment AssessAuthority(
        IEquipmentExactSolverUtilityModel model,
        EquipmentUtilityEvaluation candidate,
        ulong additionalCostGil) =>
        ((MinerBotanistUtilityProfile)model).AssessAuthority(candidate, additionalCostGil);

    public MinerBotanistSolverReplay? CaptureReplay(
        EquipmentExactFrontierRequest request,
        string contextId,
        uint classJobId,
        uint characterLevel,
        MinerBotanistUtilityStats offerBaseline,
        MinerBotanistUtilityStats fixedStats)
    {
        var contextKind = contextId switch
        {
            MinerBotanistUtilityProfile.LegendaryContextId => MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
            MinerBotanistUtilityProfile.CollectableContextId => MinerBotanistUtilityContextKind.CollectableEfficiency,
            _ => MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
        };
        return MinerBotanistSolverReplay.Capture(request, contextKind, classJobId, characterLevel, offerBaseline, fixedStats);
    }

    public EquipmentSolverUtilityVector VectorFromRendered(IReadOnlyDictionary<string, int> stats, IReadOnlyDictionary<string, int> materia)
    {
        int Read(string key) => stats.GetValueOrDefault(key) + materia.GetValueOrDefault(key);
        return MinerBotanistUtilityProfile.ToVector(new(Read("Gathering"), Read("Perception"), Read("GP")));
    }

    public EquipmentSolverUtilityVector VectorFromDefinition(EquipmentStatProfile profile)
    {
        int Sum(EquipmentStatSemantic semantic) => profile.Parameters.Where(value => value.Semantic == semantic).Sum(value => value.Value);
        return MinerBotanistUtilityProfile.ToVector(new(
            Sum(EquipmentStatSemantic.Gathering),
            Sum(EquipmentStatSemantic.Perception),
            Sum(EquipmentStatSemantic.GatheringPoints)));
    }

    public AdvisorStatTriple ToTriple(MinerBotanistUtilityStats stats) =>
        new(stats.Gathering, stats.Perception, stats.GatheringPoints);

    private static MinerBotanistUtilityStats FromTriple(AdvisorStatTriple triple) =>
        new(checked((int)triple.First), checked((int)triple.Second), checked((int)triple.Third));
}

public sealed class CrafterAdvisorStatFamily : IAdvisorStatFamily
{
    public static readonly CrafterAdvisorStatFamily Instance = new();

    public IReadOnlySet<uint> SupportedClassJobIds => CrafterUtilityProfile.CrafterClassJobIds;
    public string CoverageJobLabel => "crafter";

    public bool IsRelevantSemantic(EquipmentStatSemantic semantic) => semantic is
        EquipmentStatSemantic.Craftsmanship or EquipmentStatSemantic.Control or EquipmentStatSemantic.CraftingPoints;

    public IEquipmentExactSolverUtilityModel CreateUtilityModel(
        string contextId,
        AdvisorStatTriple baseline,
        AdvisorStatTriple? fixedStats,
        uint classJobId,
        uint characterLevel) =>
        new CrafterUtilityProfile(
            CrafterUtilityContextKind.OrdinaryCraftBenchmark,
            FromTriple(baseline),
            classJobId,
            characterLevel,
            fixedStats is null ? null : FromTriple(fixedStats));

    public AdvisorAuthorityAssessment AssessAuthority(
        IEquipmentExactSolverUtilityModel model,
        EquipmentUtilityEvaluation candidate,
        ulong additionalCostGil) =>
        ((CrafterUtilityProfile)model).AssessAuthority(candidate, additionalCostGil);

    public MinerBotanistSolverReplay? CaptureReplay(
        EquipmentExactFrontierRequest request,
        string contextId,
        uint classJobId,
        uint characterLevel,
        MinerBotanistUtilityStats offerBaseline,
        MinerBotanistUtilityStats fixedStats) =>
        null;

    public EquipmentSolverUtilityVector VectorFromRendered(IReadOnlyDictionary<string, int> stats, IReadOnlyDictionary<string, int> materia)
    {
        int Read(string key) => stats.GetValueOrDefault(key) + materia.GetValueOrDefault(key);
        return CrafterUtilityProfile.ToVector(new(Read("Craftsmanship"), Read("Control"), Read("CP")));
    }

    public EquipmentSolverUtilityVector VectorFromDefinition(EquipmentStatProfile profile)
    {
        int Sum(EquipmentStatSemantic semantic) => profile.Parameters.Where(value => value.Semantic == semantic).Sum(value => value.Value);
        return CrafterUtilityProfile.ToVector(new(
            Sum(EquipmentStatSemantic.Craftsmanship),
            Sum(EquipmentStatSemantic.Control),
            Sum(EquipmentStatSemantic.CraftingPoints)));
    }

    public AdvisorStatTriple ToTriple(MinerBotanistUtilityStats stats) =>
        new(stats.Gathering, stats.Perception, stats.GatheringPoints);

    private static CrafterUtilityStats FromTriple(AdvisorStatTriple triple) =>
        new(checked((int)triple.First), checked((int)triple.Second), checked((int)triple.Third));
}
