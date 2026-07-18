using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;

namespace MarketMafioso.Squire.Outfitter.Utility;

public enum MinerBotanistAdvisorStatus
{
    Complete,
    Abstained,
}

public sealed record MinerBotanistReadOnlyAdvice(
    MinerBotanistAdvisorStatus Status,
    string AdvisoryRule,
    EquipmentExactFrontierResult? Frontier,
    EquipmentDecisionSolution? Nomination,
    IReadOnlyDictionary<string, MinerBotanistAuthorityAssessment> AuthorityBySolutionId,
    IReadOnlyDictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer> OffersByAllocation,
    string Diagnostic);

/// <summary>
/// First read-only MIN/BTN advisor slice. It consumes only reconciled rendered baseline evidence,
/// exact-quality market evidence, and version-matched static item definitions.
/// </summary>
public sealed class MinerBotanistReadOnlyAdvisor
{
    public const string AdvisoryRule =
        "Prefer the least-cost complete loadout that gains a supported capability. " +
        "Abstain when evidence is incomplete, the stat trade is context-dependent, or a paid gain remains entirely inside monotonic score space.";

    public MinerBotanistReadOnlyAdvice Build(
        RenderedMinerBotanistBaseline baseline,
        RenderedEquipmentResolution currentEquipment,
        OutfitterMarketEvidenceBook marketEvidence,
        Func<uint, IReadOnlyList<EquipmentItemDefinition>> findDefinitionsByItemId,
        MinerBotanistUtilityContextKind contextKind,
        IReadOnlyList<EquipmentLoadoutOffer>? vendorOffers = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(currentEquipment);
        ArgumentNullException.ThrowIfNull(marketEvidence);
        ArgumentNullException.ThrowIfNull(findDefinitionsByItemId);
        if (baseline is not { Status: RenderedMinerBotanistBaselineStatus.Complete, ClassJobId: { } classJobId,
                Level: { } characterLevel, TotalStats: { } total, FixedStats: { } fixedStats } ||
            characterLevel is < 1 or > 100 ||
            currentEquipment.Status != RenderedEquipmentResolutionStatus.Complete || currentEquipment.Slots.Count != 12)
            return Abstain("A complete rendered level 1-100 MIN/BTN baseline and twelve uniquely resolved slots are required.");
        if (!marketEvidence.IsPublishable)
            return Abstain("The exact-quality market evidence generation is incomplete or stale; the advisor will not nominate from it.");
        var unsupportedCurrent = currentEquipment.Slots.FirstOrDefault(value => MinerBotanistEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(value.Definition));
        if (unsupportedCurrent is not null)
            return Abstain($"Currently equipped {unsupportedCurrent.Definition.Name} has an unmodeled effect or equip restriction.");

        var offerStats = new MinerBotanistUtilityStats(
            total.Gathering - fixedStats.Gathering,
            total.Perception - fixedStats.Perception,
            total.GatheringPoints - fixedStats.GatheringPoints);
        var profile = new MinerBotanistUtilityProfile(contextKind, offerStats, classJobId, checked((uint)characterLevel), fixedStats);
        var offers = new List<EquipmentExactSolverOffer>();
        var required = currentEquipment.Slots.Select(value => value.Position).ToHashSet();
        var baselineKeys = required.ToDictionary(position => position, _ => (EquipmentOfferAllocationKey?)null);
        var observationsByPosition = baseline.EquippedSlots.ToDictionary(
            value => Position(value.PositionKey),
            value => value.Item!);
        foreach (var slot in currentEquipment.Slots)
        {
            var observed = observationsByPosition[slot.Position];
            var exact = new EquipmentExactSolverOffer(
                slot.BaselineOffer,
                $"rendered-current:{slot.PositionKey}",
                new HashSet<EquipmentLoadoutPosition> { slot.Position },
                1,
                Vector(observed.Stats, observed.MateriaStats),
                0,
                null,
                null,
                0,
                new(0, 0, 0),
                ["Currently equipped", slot.Quality == EquipmentQuality.High ? "HQ" : "NQ"]);
            offers.Add(exact);
            baselineKeys[slot.Position] = exact.AllocationKey;
        }

        foreach (var itemEvidence in marketEvidence.Items.Where(value => value.Status == OutfitterMarketEvidenceItemStatus.Fresh))
        {
            var definitions = findDefinitionsByItemId(itemEvidence.ItemId)
                .Where(value => value.ItemId == itemEvidence.ItemId && value.EquipLevel <= characterLevel && value.EligibleClassJobIds.Contains(classJobId))
                .ToArray();
            if (definitions.Length != 1)
                return Abstain($"Market item {itemEvidence.ItemId} did not resolve to exactly one eligible static equipment definition.");
            var definition = definitions[0];
            if (MinerBotanistEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(definition))
                return Abstain($"{definition.Name} has an unmodeled effect or equip restriction.");
            foreach (var listing in RelevantListings(itemEvidence.Listings))
            {
                var profileForQuality = definition.ResolveStatProfile(listing.Quality);
                if (profileForQuality is not { IsComplete: true })
                    return Abstain($"{definition.Name} has no complete {listing.Quality} stat profile.");
                var positions = Positions(definition);
                if (positions.Count == 0 || !positions.Overlaps(required))
                    continue;
                var sourceCatalogKey = $"market:{marketEvidence.SourceKey}:{definition.ItemId}:{listing.Quality}";
                var key = new EquipmentOfferKey(definition.ItemId, listing.Quality, EquipmentAcquisitionSourceKind.MarketBoard, sourceCatalogKey);
                var observation = new EquipmentOfferObservation(
                    key,
                    marketEvidence.GenerationId,
                    listing.ListingId,
                    listing.ListingReviewedAtUtc,
                    ObservableMarketRow: new(
                        listing.ListingId,
                        listing.ItemId,
                        listing.Quality,
                        listing.Quantity,
                        listing.UnitPriceGil,
                        listing.WorldName,
                        listing.RetainerName),
                    World: listing.WorldName,
                    AvailableQuantity: listing.Quantity,
                    UnitPriceGil: listing.UnitPriceGil);
                var offer = new EquipmentLoadoutOffer(
                    definition,
                    EquipmentAcquisitionSourceKind.MarketBoard,
                    $"Market board · {listing.WorldName}",
                    listing.UnitPriceGil,
                    PriceIsEstimate: false,
                    Quality: listing.Quality,
                    SourceCatalogKey: sourceCatalogKey,
                    Observation: observation);
                offers.Add(new(
                    offer,
                    listing.ListingId,
                    positions,
                    listing.Quantity,
                    Vector(profileForQuality),
                    listing.UnitPriceGil,
                    listing.WorldName,
                    null,
                    1,
                    new(0, 0, 0),
                    [listing.Quality == EquipmentQuality.High ? "HQ" : "NQ", listing.WorldName]));
            }
        }

        foreach (var vendor in vendorOffers ?? [])
        {
            if (vendor.SourceKind != EquipmentAcquisitionSourceKind.GilVendor || vendor.UnitPriceGil is not { } price ||
                !vendor.Definition.EligibleClassJobIds.Contains(classJobId) || vendor.Definition.EquipLevel > characterLevel)
                return Abstain("Vendor offer evidence did not match the supported rendered MIN/BTN target.");
            if (MinerBotanistEquipmentSupportPolicy.HasUnmodeledEffectOrRestriction(vendor.Definition))
                return Abstain($"{vendor.Definition.Name} has an unmodeled effect or equip restriction.");
            var statProfile = vendor.ResolveStatProfile();
            if (statProfile is not { IsComplete: true })
                return Abstain($"{vendor.Definition.Name} has no complete {vendor.ResolvedQuality} stat profile.");
            var positions = Positions(vendor.Definition);
            if (positions.Count == 0 || !positions.Overlaps(required))
                continue;
            offers.Add(new(
                vendor,
                null,
                positions,
                1,
                Vector(statProfile),
                price,
                null,
                vendor.Key.SourceCatalogKey,
                1,
                new(0, 0, 0),
                [vendor.ResolvedQuality == EquipmentQuality.High ? "HQ" : "NQ", vendor.SourceLabel]));
        }

        EquipmentExactFrontierResult frontier;
        try
        {
            frontier = new EquipmentExactFrontierSolver().Solve(new(
                offers,
                required,
                baselineKeys,
                profile));
        }
        catch (Exception ex)
        {
            return Abstain($"Exact frontier construction failed safely: {ex.Message}");
        }

        var authority = frontier.Pareto.Frontier.ToDictionary(
            solution => solution.Candidate.SolutionId,
            solution => profile.AssessAuthority(solution.Utility, solution.AcquisitionCostGil),
            StringComparer.Ordinal);
        var nomination = frontier.Pareto.Frontier
            .Where(solution => authority[solution.Candidate.SolutionId].AdvisorMayConsider)
            .OrderBy(solution => solution.AcquisitionCostGil)
            .ThenBy(solution => solution.Burden.WorldVisits)
            .ThenBy(solution => solution.Burden.PurchaseTransactions)
            .ThenByDescending(solution => solution.Utility.UtilityScore)
            .FirstOrDefault();
        return new(
            MinerBotanistAdvisorStatus.Complete,
            AdvisoryRule,
            frontier,
            nomination,
            authority,
            offers.ToDictionary(value => value.AllocationKey),
            nomination is null
                ? "Frontier is complete, but the advisor abstains under the displayed rule."
                : $"Advisor nominates {nomination.Candidate.SolutionId} under the displayed rule.");
    }

    private static EquipmentSolverUtilityVector Vector(
        IReadOnlyDictionary<string, int> stats,
        IReadOnlyDictionary<string, int> materia)
    {
        int Read(string key) => stats.GetValueOrDefault(key) + materia.GetValueOrDefault(key);
        return MinerBotanistUtilityProfile.ToVector(new(Read("Gathering"), Read("Perception"), Read("GP")));
    }

    private static EquipmentSolverUtilityVector Vector(EquipmentStatProfile profile)
    {
        int Sum(EquipmentStatSemantic semantic) => profile.Parameters.Where(value => value.Semantic == semantic).Sum(value => value.Value);
        return MinerBotanistUtilityProfile.ToVector(new(
            Sum(EquipmentStatSemantic.Gathering),
            Sum(EquipmentStatSemantic.Perception),
            Sum(EquipmentStatSemantic.GatheringPoints)));
    }

    private static HashSet<EquipmentLoadoutPosition> Positions(EquipmentItemDefinition definition) => definition.Slot switch
    {
        EquipmentSlot.MainHand when definition.OffHandOccupancy != 0 => [EquipmentLoadoutPosition.MainHand, EquipmentLoadoutPosition.OffHand],
        EquipmentSlot.MainHand => [EquipmentLoadoutPosition.MainHand],
        EquipmentSlot.OffHand => [EquipmentLoadoutPosition.OffHand],
        EquipmentSlot.Head => [EquipmentLoadoutPosition.Head],
        EquipmentSlot.Body => [EquipmentLoadoutPosition.Body],
        EquipmentSlot.Hands => [EquipmentLoadoutPosition.Hands],
        EquipmentSlot.Legs => [EquipmentLoadoutPosition.Legs],
        EquipmentSlot.Feet => [EquipmentLoadoutPosition.Feet],
        EquipmentSlot.Ears => [EquipmentLoadoutPosition.Ears],
        EquipmentSlot.Neck => [EquipmentLoadoutPosition.Neck],
        EquipmentSlot.Wrists => [EquipmentLoadoutPosition.Wrists],
        EquipmentSlot.Ring => [EquipmentLoadoutPosition.LeftRing, EquipmentLoadoutPosition.RightRing],
        _ => [],
    };

    private static EquipmentLoadoutPosition Position(string key) => key switch
    {
        "main-hand" => EquipmentLoadoutPosition.MainHand,
        "off-hand" => EquipmentLoadoutPosition.OffHand,
        "head" => EquipmentLoadoutPosition.Head,
        "body" => EquipmentLoadoutPosition.Body,
        "hands" => EquipmentLoadoutPosition.Hands,
        "legs" => EquipmentLoadoutPosition.Legs,
        "feet" => EquipmentLoadoutPosition.Feet,
        "ears" => EquipmentLoadoutPosition.Ears,
        "neck" => EquipmentLoadoutPosition.Neck,
        "wrists" => EquipmentLoadoutPosition.Wrists,
        "ring-left" => EquipmentLoadoutPosition.LeftRing,
        "ring-right" => EquipmentLoadoutPosition.RightRing,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unsupported rendered equipment position."),
    };

    private static IEnumerable<OutfitterMarketListingEvidence> RelevantListings(
        IReadOnlyList<OutfitterMarketListingEvidence> listings)
    {
        foreach (var qualityGroup in listings
                     .Where(value => value.Quantity > 0)
                     .GroupBy(value => value.Quality))
        {
            uint coveredUnits = 0;
            foreach (var listing in qualityGroup
                         .OrderBy(value => value.UnitPriceGil)
                         .ThenByDescending(value => value.ListingReviewedAtUtc)
                         .ThenBy(value => value.ListingId, StringComparer.Ordinal))
            {
                yield return listing;
                coveredUnits = checked(coveredUnits + listing.Quantity);
                if (coveredUnits >= 2)
                    break;
            }
        }
    }

    private static MinerBotanistReadOnlyAdvice Abstain(string diagnostic) =>
        new(MinerBotanistAdvisorStatus.Abstained, AdvisoryRule, null, null,
            new Dictionary<string, MinerBotanistAuthorityAssessment>(),
            new Dictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer>(),
            diagnostic);
}
