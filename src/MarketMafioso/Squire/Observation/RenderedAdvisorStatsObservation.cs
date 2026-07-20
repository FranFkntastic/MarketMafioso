using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Observation;

/// <summary>
/// Family-neutral rendered stat tuple from the Character window. The resolved advisor stat
/// family assigns semantic meaning to the three positional components (gathering: Gathering,
/// Perception, GP; crafting: Craftsmanship, Control, CP).
/// </summary>
public sealed record RenderedAdvisorStatsObservation(
    RenderedCharacterObservationStatus Status,
    string? JobName,
    int? Level,
    AdvisorStatTriple? Stats,
    string Diagnostic)
{
    public static RenderedAdvisorStatsObservation FromGathering(RenderedGatheringStatsObservation observation) => new(
        observation.Status,
        observation.JobName,
        observation.Level,
        observation is { Gathering: { } gathering, Perception: { } perception, GatheringPoints: { } gatheringPoints }
            ? new(gathering, perception, gatheringPoints)
            : null,
        observation.Diagnostic);

    public static RenderedAdvisorStatsObservation FromCrafting(RenderedCraftingStatsObservation observation) => new(
        observation.Status,
        observation.JobName,
        observation.Level,
        observation is { Craftsmanship: { } craftsmanship, Control: { } control, CraftingPoints: { } craftingPoints }
            ? new(craftsmanship, control, craftingPoints)
            : null,
        observation.Diagnostic);
}
