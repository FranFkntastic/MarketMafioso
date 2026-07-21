#if DEBUG
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.UI.Plots;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class MinerBotanistAdvisorSyntheticReviewTests
{
    [Fact]
    public void LegendaryReplayExercisesRealFrontierAndExactQualityPresentation()
    {
        var advice = MinerBotanistAdvisorSyntheticReview.Build(
            MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield);

        Assert.Equal(MinerBotanistAdvisorStatus.Complete, advice.Status);
        Assert.NotNull(advice.Frontier);
        Assert.Equal(
            ["crafted-unmelded", "published-mid-crafted", "published-high-crafted"],
            advice.Frontier.Pareto.Frontier
                .OrderBy(solution => solution.AcquisitionCostGil)
                .Select(solution => solution.Candidate.SolutionId));
        Assert.DoesNotContain(
            advice.Frontier.Pareto.Frontier,
            solution => solution.Candidate.SolutionId.StartsWith("derived-", StringComparison.Ordinal));
        Assert.Equal("published-mid-crafted", advice.Nomination?.Candidate.SolutionId);
        Assert.All(advice.OffersByAllocation.Values, offer => Assert.Equal(EquipmentQuality.High, offer.Offer.ResolvedQuality));
        Assert.Contains(advice.OffersByAllocation.Values, offer => offer.Offer.Definition.Name == "Gold Thumb's Pickaxe");
        Assert.Contains(advice.OffersByAllocation.Values, offer => offer.Offer.Definition.Name == "Crested Coat of Gathering");
        Assert.All(advice.OffersByAllocation.Values, offer => Assert.InRange(offer.Offer.Definition.ItemId, 1u, 100_000u));
        Assert.All(advice.OffersByAllocation.Values, offer => Assert.DoesNotContain("illustrative", offer.Offer.SourceLabel, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(advice.OffersByAllocation.Values, offer => offer.Offer.SourceKind != EquipmentAcquisitionSourceKind.MarketBoard);
        Assert.DoesNotContain(advice.OffersByAllocation.Values, offer => offer.Offer.Definition.ItemId is 49334 or 49345 or 49352 or 49353 or 49354 or 49355 or 49356 or 49361 or 49362 or 49363 or 49364 or 51786);
        Assert.Equal(2_721_179UL, Solution(advice, "crafted-unmelded").AcquisitionCostGil);
        Assert.Equal(3_009_117UL, Solution(advice, "published-mid-crafted").AcquisitionCostGil);
        Assert.Equal(3_275_797UL, Solution(advice, "published-high-crafted").AcquisitionCostGil);
        Assert.Contains(Solution(advice, "published-high-crafted").VariantLabels, label => label.Contains("3,136,992 gil materia", StringComparison.Ordinal));
        Assert.Contains(Solution(advice, "published-high-crafted").VariantLabels, label => label.Contains("5,858,171 gil total", StringComparison.Ordinal));
        Assert.All(advice.Frontier.Pareto.Frontier, solution => Assert.Equal(
            solution.AcquisitionCostGil,
            solution.Candidate.Selections.Aggregate(0UL, (total, selection) =>
                checked(total + advice.OffersByAllocation[selection.AllocationKey].AcquisitionCostGil))));
    }

    [Fact]
    public void OrdinaryNodeReplayDisplaysFrontierAndConservativeDominanceNomination()
    {
        var advice = MinerBotanistAdvisorSyntheticReview.Build(
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark);

        Assert.NotNull(advice.Frontier);
        Assert.NotEmpty(advice.Frontier.Pareto.Frontier);
        Assert.Equal("published-mid-crafted", advice.Nomination?.Candidate.SolutionId);
        Assert.Contains(
            advice.AuthorityBySolutionId[advice.Nomination!.Candidate.SolutionId].GainedCapabilityIds,
            value => value == "ordinary-balanced-stat-dominance");
    }

    [Fact]
    public void ContextPlotsCanOverlayWithoutLosingSourceIdentity()
    {
        var builder = new ParetoFrontierPlotBuilder();
        var ordinary = MinerBotanistAdvisorSyntheticReview.Build(MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark);
        var legendary = MinerBotanistAdvisorSyntheticReview.Build(MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield);
        var collectables = MinerBotanistAdvisorSyntheticReview.Build(MinerBotanistUtilityContextKind.CollectableEfficiency);

        var overlay = PlotOverlayComposer.Compose("contexts",
        [
            new("ordinary", builder.Build(ordinary.Frontier!.Pareto).Spec),
            new("legendary", builder.Build(legendary.Frontier!.Pareto).Spec),
            new("collectables", builder.Build(collectables.Frontier!.Pareto).Spec),
        ]);

        Assert.Contains("ordinary/crafted-unmelded", overlay.DatumIdentities.Keys);
        Assert.Contains("legendary/published-high-crafted", overlay.DatumIdentities.Keys);
        Assert.Contains("collectables/published-mid-crafted", overlay.DatumIdentities.Keys);
        Assert.Equal("Acquisition cost", overlay.Spec.XAxis.Label);
        Assert.Equal("Job utility", overlay.Spec.YAxis.Label);
        Assert.InRange(overlay.Spec.YDomain.Maximum, 0d, 110d);
    }

    [Theory]
    [InlineData(MinerBotanistAdvisorSyntheticScenarioKind.Success, true, false, false)]
    [InlineData(MinerBotanistAdvisorSyntheticScenarioKind.Refreshing, true, true, true)]
    [InlineData(MinerBotanistAdvisorSyntheticScenarioKind.StaleEvidence, true, false, true)]
    [InlineData(MinerBotanistAdvisorSyntheticScenarioKind.IncompleteEvidence, true, false, true)]
    [InlineData(MinerBotanistAdvisorSyntheticScenarioKind.Abstention, false, false, false)]
    public void EvidenceStateReplaysPreserveOnlyTheLastAuthoritativeFrontier(
        MinerBotanistAdvisorSyntheticScenarioKind kind,
        bool showPriorFrontier,
        bool showProgress,
        bool retained)
    {
        var advice = MinerBotanistAdvisorSyntheticReview.Build(MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark);

        var presentation = MinerBotanistAdvisorSyntheticReview.Present(kind, advice);

        Assert.Equal(showPriorFrontier, presentation.ShowPriorFrontier);
        Assert.Equal(showProgress, presentation.ShowProgress);
        Assert.Equal(retained, presentation.AdviceIsRetained);
        Assert.True(presentation.Total >= presentation.Completed);
    }

    [Fact]
    public async Task CurrentListingFixtureBuildsTwoDistinctFullStackRowsThatLeaveOneRowAfterSeed()
    {
        var fixture = await MinerBotanistAdvisorSyntheticReview.BuildDryRunFixtureAsync(
            new DuplicateSlotListingSource(1, 1),
            "North America",
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark);
        var solution = fixture.Advice.Frontier!.Pareto.Frontier.Single().Candidate;
        var marketSelections = solution.Selections
            .Where(selection => fixture.Advice.OffersByAllocation[selection.AllocationKey].Offer.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard)
            .ToArray();

        Assert.Equal(2, marketSelections.Length);
        Assert.Equal(2, marketSelections.Select(selection => selection.Position).Distinct().Count());
        Assert.Single(marketSelections.Select(selection => selection.OfferKey.ItemId).Distinct());
        Assert.All(marketSelections, selection => Assert.Contains(
            selection.Position,
            new[] { EquipmentLoadoutPosition.LeftRing, EquipmentLoadoutPosition.RightRing }));
        Assert.All(marketSelections, selection =>
        {
            var offer = fixture.Advice.OffersByAllocation[selection.AllocationKey];
            var row = Assert.IsType<EquipmentMarketRowObservation>(offer.Offer.Observation!.ObservableMarketRow);
            Assert.Contains(fixture.Evidence.Items.SelectMany(item => item.Listings), listing =>
                listing.ListingId == row.RowId && listing.ItemId == row.ItemId &&
                listing.Quantity == row.Quantity && listing.UnitPriceGil == row.UnitPriceGil);
        });

        var validation = OutfitterWorkbenchPlayerValidation.CreateDryRun(
            fixture.Advice,
            fixture.SelectedSolutionId,
            fixture.Evidence);
        var transfer = OutfitterWorkbenchTransferBuilder.Build(
            fixture.Advice,
            fixture.SelectedSolutionId,
            fixture.Evidence,
            validation);
        var lotGroup = Assert.Single(transfer.MarketLots.GroupBy(lot => (lot.OfferKey.ItemId, lot.OfferKey.Quality)));
        Assert.Equal(2, lotGroup.Count());
        Assert.All(lotGroup, lot =>
        {
            Assert.Equal(1u, lot.RequiredQuantity);
            Assert.Equal(1u, lot.ObservedAvailableQuantity);
        });
        Assert.Equal(2, lotGroup.Select(lot => (lot.WorldName, lot.DiscoveryObservationId, lot.RetainerId)).Distinct().Count());
        Assert.Equal(2u, lotGroup.Aggregate(0u, (sum, lot) => checked(sum + lot.RequiredQuantity)));
        Assert.Equal([119_999u, 131_999u], lotGroup.Select(lot => lot.ObservedUnitPriceGil).Order().ToArray());
        Assert.Equal(251_998ul, transfer.ObservedMarketTotalGil);
        Assert.All(lotGroup, lot => Assert.Contains(fixture.Evidence.Items.SelectMany(item => item.Listings), listing =>
            listing.ListingId == lot.DiscoveryObservationId));

        var scenario = BuildSeedScenario(transfer);
        var store = new MemoryExecutionStateStore();
        var seed = OutfitterDryRunSunkStateSeeder.CreateSemanticSeed(
            scenario.Contract,
            scenario.Document,
            scenario.Claim,
            scenario.Plan);
        Assert.Equal(251_998ul, scenario.Contract.SquirePlanCapGil);
        Assert.Equal(251_998u, Assert.Single(scenario.Contract.Lines).MaxTotalGil);
        Assert.Equal(119_999u, seed.TotalGil);
        var seeded = OutfitterDryRunSunkStateSeeder.Seed(
            store,
            scenario.Contract,
            scenario.Document,
            scenario.Claim,
            scenario.Plan,
            seed,
            DateTimeOffset.UnixEpoch.AddMinutes(2));
        var remaining = OutfitterDryRunExecutionStateRestorer.RestoreRemainingPlan(
            scenario.Contract,
            scenario.Document,
            scenario.Claim,
            scenario.Plan,
            store.Restore());

        Assert.True(seeded.IsSuccess);
        Assert.Equal(1u, store.Restore()!.Lines.Single().PurchasedQuantity);
        Assert.Equal(1u, remaining.Lines.Single().RequestedQuantity);
        Assert.Equal(1u, remaining.Lines.Single().PlannedQuantity);
        Assert.Equal(131_999u, remaining.Lines.Single().PlannedGil);
        Assert.Equal(131_999u, remaining.Lines.Single().GilCap);
        var remainingListing = Assert.Single(remaining.WorldBatches.SelectMany(batch => batch.Listings));
        Assert.Equal("listing-47201-2", remainingListing.ListingId);
        Assert.Equal("retainer-listing-47201-2", remainingListing.RetainerId);
        Assert.Equal(131_999u, remainingListing.TotalGil);
        Assert.Equal(1u, remaining.WorldBatches.SelectMany(batch => batch.Listings).Aggregate(
            0u,
            (sum, listing) => checked(sum + listing.Quantity)));
    }

    [Fact]
    public async Task CurrentListingFixtureAbstainsFromSingleQuantityTwoStack()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MinerBotanistAdvisorSyntheticReview.BuildDryRunFixtureAsync(
                new DuplicateSlotListingSource(2),
                "North America",
                MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark));

        Assert.Contains("indivisible full stacks", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("abstained", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static EquipmentDecisionSolution Solution(MinerBotanistReadOnlyAdvice advice, string id) =>
        advice.Frontier!.Pareto.Frontier
            .Concat(advice.Frontier.Pareto.Dominated.Select(value => value.Solution))
            .Single(value => value.Candidate.SolutionId == id);

    private static SeedScenario BuildSeedScenario(OutfitterWorkbenchTransfer transfer)
    {
        var document = MarketAcquisitionRequestDocument.CreateDefault("Fran", "Siren");
        document = OutfitterWorkbenchAuthorityService.Stage(document, transfer);
        document = OutfitterWorkbenchAuthorityService.Finalize(document);
        var contract = document.OutfitterAuthority!.FinalizedContract!;
        var envelope = Assert.Single(contract.Lines);
        var lineId = "line-1";
        var claim = new MarketAcquisitionClaimView
        {
            Id = "request-1",
            ClaimToken = "claim-token",
            TargetCharacterName = "Fran",
            TargetWorld = "Siren",
            Lines =
            [
                new MarketAcquisitionBatchLineView
                {
                    LineId = lineId,
                    ItemId = envelope.ItemId,
                    ItemName = envelope.ItemName,
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = envelope.RequiredQuantity,
                    HqPolicy = envelope.Quality == EquipmentQuality.High ? "HqOnly" : "NqOnly",
                    MaxUnitPrice = envelope.MaxUnitPriceGil,
                    GilCap = envelope.MaxTotalGil,
                },
            ],
        };
        var listings = transfer.MarketLots.Select(lot => new MarketAcquisitionPlannedListing
        {
            LineId = lineId,
            ItemId = lot.OfferKey.ItemId,
            ItemName = lot.ItemName,
            ListingId = lot.DiscoveryObservationId,
            RetainerId = FixtureListingRetainerId(lot.DiscoveryObservationId),
            Quantity = lot.RequiredQuantity,
            UnitPrice = lot.ObservedUnitPriceGil,
            TotalGil = checked(lot.RequiredQuantity * lot.ObservedUnitPriceGil),
            IsHq = lot.OfferKey.Quality == EquipmentQuality.High,
            LastReviewTimeUtc = lot.ReviewedAtUtc,
        }).ToArray();
        var world = Assert.Single(transfer.MarketLots.Select(lot => lot.WorldName).Distinct(StringComparer.OrdinalIgnoreCase));
        var plannedGil = listings.Aggregate(0u, (sum, listing) => checked(sum + listing.TotalGil));
        var subtask = new MarketAcquisitionWorldItemSubtask
        {
            LineId = lineId,
            ItemId = envelope.ItemId,
            ItemName = envelope.ItemName,
            WorldName = world,
            DataCenter = MarketAcquisitionWorldCatalog.ResolveDataCenter(world),
            QuantityMode = "TargetQuantity",
            RequestedQuantity = envelope.RequiredQuantity,
            HqPolicy = envelope.Quality == EquipmentQuality.High ? "HqOnly" : "NqOnly",
            MaxUnitPrice = envelope.MaxUnitPriceGil,
            GilCap = envelope.MaxTotalGil,
            PlannedQuantity = envelope.RequiredQuantity,
            PlannedGil = plannedGil,
            Listings = listings,
        };
        var plan = new MarketAcquisitionPlan
        {
            RequestId = claim.Id,
            Status = "Ready",
            PreparedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            Lines =
            [
                new MarketAcquisitionPlanLine
                {
                    LineId = lineId,
                    ItemId = envelope.ItemId,
                    ItemName = envelope.ItemName,
                    QuantityMode = "TargetQuantity",
                    RequestedQuantity = envelope.RequiredQuantity,
                    HqPolicy = envelope.Quality == EquipmentQuality.High ? "HqOnly" : "NqOnly",
                    MaxUnitPrice = envelope.MaxUnitPriceGil,
                    GilCap = envelope.MaxTotalGil,
                    Status = "Ready",
                    PlannedQuantity = envelope.RequiredQuantity,
                    PlannedGil = plannedGil,
                },
            ],
            WorldBatches =
            [
                new MarketAcquisitionWorldBatch
                {
                    WorldName = world,
                    DataCenter = MarketAcquisitionWorldCatalog.ResolveDataCenter(world),
                    PlannedQuantity = envelope.RequiredQuantity,
                    PlannedGil = plannedGil,
                    ItemSubtasks = [subtask],
                    Listings = listings,
                },
            ],
        };
        return new(document, contract, claim, plan);

        static string FixtureListingRetainerId(string listingId) => $"retainer-{listingId}";
    }

    private sealed class DuplicateSlotListingSource : IMarketAcquisitionListingSource
    {
        private readonly uint[] quantities;

        public DuplicateSlotListingSource(params uint[] quantities) =>
            this.quantities = quantities.Where(quantity => quantity > 0).ToArray();

        public Task<IReadOnlyList<MarketAcquisitionListing>> FetchListingsAsync(
            string region,
            uint itemId,
            int listingLimit,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MarketAcquisitionListing>>(
            quantities.Select((quantity, index) => Listing(itemId, index + 1, quantity)).ToArray());

        public Task<IReadOnlyList<MarketAcquisitionListing>> FetchListingsForWorldAsync(
            string worldName,
            uint itemId,
            int listingLimit,
            CancellationToken cancellationToken) => FetchListingsAsync("North America", itemId, listingLimit, cancellationToken);

        private static MarketAcquisitionListing Listing(uint itemId, int ordinal, uint quantity)
        {
            var listingId = $"listing-{itemId}-{ordinal}";
            return new MarketAcquisitionListing
            {
                ItemId = itemId,
                ItemName = $"Fixture item {itemId}",
                ListingId = listingId,
                WorldName = "Siren",
                WorldId = 57,
                RetainerName = $"Retainer {ordinal}",
                RetainerId = $"retainer-{listingId}",
                Quantity = quantity,
                UnitPrice = ordinal == 1 ? 119_999u : 131_999u,
                IsHq = true,
                LastReviewTimeUtc = DateTimeOffset.UtcNow,
            };
        }
    }

    private sealed class MemoryExecutionStateStore : IOutfitterRouteExecutionStateStore
    {
        private OutfitterRouteExecutionState? state;
        public OutfitterRouteExecutionState? Restore() => state;
        public void Save(OutfitterRouteExecutionState value) => state = value;
        public void Clear() => state = null;
    }

    private sealed record SeedScenario(
        MarketAcquisitionRequestDocument Document,
        OutfitterExecutionContract Contract,
        MarketAcquisitionClaimView Claim,
        MarketAcquisitionPlan Plan);
}
#endif
