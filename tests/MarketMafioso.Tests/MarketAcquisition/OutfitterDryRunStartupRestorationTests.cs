using Franthropy.Dalamud.Equipment;
using MarketMafioso.AgentBridge;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Tests.Squire;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class OutfitterDryRunStartupRestorationTests
{
    private const uint PurchasedListingGil = 119_999;
    private const uint RemainingListingGil = 131_999;
    private const uint FinalizedLineGil = PurchasedListingGil + RemainingListingGil;

    [Fact]
    public void PluginReload_RestoresRemainingDryRunIdempotentlyWithoutNetworkOrPurchase()
    {
        var fixture = PersistedFixture();
        var listingSource = new CountingListingSource();
        var store = new ConfigurationOutfitterRouteExecutionStateStore(fixture.Config, () => fixture.SaveCount++);
        var persistedJson = fixture.Config.OutfitterRouteExecutionStateJson;

        using var first = CreateWorkspace(fixture.Config, listingSource);
        var firstDocument = MarketAcquisitionRequestDocumentPersistence.Restore(fixture.Config);
        Connect(first, firstDocument);
        Assert.True(first.RestoreFinalizedDryRunPlan(firstDocument, store));
        var firstPlan = Assert.IsType<MarketAcquisitionPlan>(first.PreparedPlan);

        Assert.Equal("Ready", firstPlan.Status);
        Assert.Equal(1u, Assert.Single(firstPlan.Lines).RequestedQuantity);
        Assert.Equal(1u, Assert.Single(firstPlan.Lines).PlannedQuantity);
        Assert.Equal(RemainingListingGil, Assert.Single(firstPlan.Lines).PlannedGil);
        var remainingListing = Assert.Single(Assert.Single(firstPlan.WorldBatches).Listings);
        Assert.Equal("listing-2", remainingListing.ListingId);
        Assert.Equal("retainer-2", remainingListing.RetainerId);
        Assert.Equal(1u, remainingListing.Quantity);
        Assert.Equal(RemainingListingGil, remainingListing.UnitPrice);
        Assert.Equal(RemainingListingGil, remainingListing.TotalGil);
        Assert.Equal(1u, store.Restore()!.Lines.Single().PurchasedQuantity);
        Assert.Equal(PurchasedListingGil, store.Restore()!.TotalSpentGil);
        Assert.Equal(0, listingSource.FetchCount);

        using (var harness = MarketAcquisitionRouteEngineHarness.Create(store))
        {
            var claim = Assert.IsType<MarketAcquisitionClaimView>(first.ClaimedRequest);
            var contract = firstDocument.OutfitterAuthority!.FinalizedContract!;
            Assert.True(harness.Engine.Start(
                firstPlan,
                claim,
                false,
                false,
                contract,
                firstDocument,
                MarketAcquisitionExecutionMode.DryRun).Success);
            var active = harness.Engine.CreateSnapshot();
            var activeLine = Assert.Single(active.OutfitterExecution!.Lines);
            Assert.Equal(FinalizedLineGil, activeLine.MaxTotalGil);
            Assert.Equal(PurchasedListingGil, activeLine.SpentGil);
            Assert.Equal(1u, activeLine.RequiredQuantity - activeLine.PurchasedQuantity);
            Assert.Equal(RemainingListingGil, AgentBridgeRouteTruthProjection.ResolveActiveOutfitterRemainingGil(active));
            harness.Runner.RecordCurrentWorld("Siren");
            harness.Runner.RecordProbe("Siren", CandidatePlan());
            harness.MarketBoard.Reads.Enqueue(CurrentListing());
            harness.Engine.BeginNextWorldPurchase();

            var simulated = Assert.Single(harness.Engine.CreateSnapshot().LiveCandidatePlan!.Rows, row => row.Decision == "WouldBuy");
            Assert.Equal("listing-2", simulated.LiveListing.ListingId);
            Assert.Equal("retainer-2", simulated.LiveListing.RetainerId);
            Assert.Equal(1u, simulated.LiveListing.Quantity);
            Assert.Equal(RemainingListingGil, simulated.LiveListing.UnitPrice);
            Assert.Equal(RemainingListingGil, harness.Runner.LastRunSummary!.SpentGil);
            Assert.Equal(0, harness.Purchase.ExecuteCallCount);
            Assert.Equal(0, harness.Purchase.ConfirmCallCount);
            Assert.Equal(PurchasedListingGil, store.Restore()!.TotalSpentGil);
            Assert.Single(store.Restore()!.SunkPurchases);
        }

        using var second = CreateWorkspace(fixture.Config, listingSource);
        var secondDocument = MarketAcquisitionRequestDocumentPersistence.Restore(fixture.Config);
        Connect(second, secondDocument);
        Assert.True(second.RestoreFinalizedDryRunPlan(secondDocument, store));
        var secondPlan = Assert.IsType<MarketAcquisitionPlan>(second.PreparedPlan);

        Assert.Equal(1u, Assert.Single(secondPlan.Lines).RequestedQuantity);
        Assert.Equal(1u, Assert.Single(secondPlan.Lines).PlannedQuantity);
        Assert.Equal(1u, store.Restore()!.Lines.Single().PurchasedQuantity);
        Assert.Equal(PurchasedListingGil, store.Restore()!.TotalSpentGil);
        Assert.Equal(persistedJson, fixture.Config.OutfitterRouteExecutionStateJson);
        Assert.Equal(0, fixture.SaveCount);
        Assert.Equal(0, listingSource.FetchCount);
    }

    [Fact]
    public void PluginReload_MismatchedPlanHashFailsClosedWithoutFetchingListings()
    {
        var fixture = PersistedFixture();
        fixture.Config.ActiveMarketAcquisitionRequestDocument!.LastPlanHash = "stale-plan-hash";
        var listingSource = new CountingListingSource();
        var store = new ConfigurationOutfitterRouteExecutionStateStore(fixture.Config, () => fixture.SaveCount++);
        using var workspace = CreateWorkspace(fixture.Config, listingSource);
        var document = MarketAcquisitionRequestDocumentPersistence.Restore(fixture.Config);
        Connect(workspace, document);

        Assert.False(workspace.RestoreFinalizedDryRunPlan(document, store));

        Assert.Null(workspace.PreparedPlan);
        Assert.Contains("paused", workspace.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("finalize", workspace.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, listingSource.FetchCount);
        Assert.Equal(0, fixture.SaveCount);
        Assert.Equal(OutfitterRouteAuthorityPhase.Paused, store.Restore()!.Phase);
        Assert.Equal(1u, store.Restore()!.Lines.Single().PurchasedQuantity);
    }

    [Fact]
    public void StartupRestoration_IgnoresLiveAndOrdinaryPlans()
    {
        var fixture = PersistedFixture(dryRunOnly: false);
        var listingSource = new CountingListingSource();
        var store = new ConfigurationOutfitterRouteExecutionStateStore(fixture.Config, () => fixture.SaveCount++);
        using var workspace = CreateWorkspace(fixture.Config, listingSource);
        var liveDocument = MarketAcquisitionRequestDocumentPersistence.Restore(fixture.Config);
        Connect(workspace, liveDocument);
        var initialStatus = workspace.Status;

        Assert.False(workspace.RestoreFinalizedDryRunPlan(liveDocument, store));
        Assert.Equal(initialStatus, workspace.Status);
        Assert.Null(workspace.PreparedPlan);

        var ordinaryDocument = MarketAcquisitionRequestDocument.CreateDefault("Fran", "Siren");
        Assert.False(workspace.RestoreFinalizedDryRunPlan(ordinaryDocument, store));
        Assert.Equal(initialStatus, workspace.Status);
        Assert.Equal(0, listingSource.FetchCount);
        Assert.Equal(0, fixture.SaveCount);
    }

    private static PersistedFixtureData PersistedFixture(bool dryRunOnly = true)
    {
        var config = new Configuration();
        var sourceTransfer = OutfitterWorkbenchAuthorityTests.Transfer();
        var sourceLot = sourceTransfer.MarketLots.Single();
        var transfer = sourceTransfer with
        {
            DryRunOnly = dryRunOnly,
            ObservedMarketTotalGil = FinalizedLineGil,
            MarketLots =
            [
                sourceLot with
                {
                    RequiredQuantity = 1,
                    ObservedAvailableQuantity = 1,
                    ObservedUnitPriceGil = PurchasedListingGil,
                    ObservedTotalPriceGil = PurchasedListingGil,
                },
                sourceLot with
                {
                    RequiredQuantity = 1,
                    ObservedAvailableQuantity = 1,
                    ObservedUnitPriceGil = RemainingListingGil,
                    ObservedTotalPriceGil = RemainingListingGil,
                    DiscoveryObservationId = "listing-2",
                    RetainerName = "Retainer 2",
                    RetainerId = "retainer-2",
                },
            ],
        };
        var document = OutfitterWorkbenchAuthorityService.Finalize(
            OutfitterWorkbenchAuthorityService.Stage(
                MarketAcquisitionRequestDocument.CreateDefault("Fran", "Siren"),
                transfer));
        document = document with { LastPlanHash = MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(document) };
        var contract = document.OutfitterAuthority!.FinalizedContract!;
        var claim = new MarketAcquisitionClaimView
        {
            Id = "request-1",
            ClaimToken = "claim-token",
            Status = "AcceptedInPlugin",
            TargetCharacterName = "Fran",
            TargetWorld = "Siren",
            Region = "North America",
            WorldMode = "Recommended",
            Lines =
            [
                new MarketAcquisitionBatchLineView
                {
                    LineId = "line-1",
                    ItemId = 10,
                    ItemName = "Exact HQ Ring",
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = 2,
                    HqPolicy = "HQOnly",
                    MaxUnitPrice = RemainingListingGil,
                    GilCap = FinalizedLineGil,
                },
            ],
        };
        var preparedAt = DateTimeOffset.Parse("2026-07-21T08:27:36Z");
        var draft = new OutfitterRouteSunkPurchase
        {
            SchemaVersion = OutfitterRouteSunkPurchase.CurrentSchemaVersion,
            ReceiptId = string.Empty,
            ContractId = contract.ContractId,
            CanonicalIntentHash = contract.CanonicalIntentHash,
            WorkbenchDocumentId = document.LocalRequestId,
            WorkbenchRevision = document.LocalRevision,
            PlanRequestId = claim.Id,
            PlanPreparedAtUtc = preparedAt,
            WorldName = "Siren",
            LineId = "line-1",
            ItemId = 10,
            Quality = EquipmentQuality.High,
            ListingId = "listing-1",
            RetainerId = "retainer-1",
            Quantity = 1,
            UnitPriceGil = PurchasedListingGil,
            TotalGil = PurchasedListingGil,
        };
        var receipt = draft with { ReceiptId = OutfitterDryRunExecutionStateRestorer.ComputeReceiptId(draft) };
        var state = new OutfitterRouteExecutionState(
            OutfitterRouteExecutionState.CurrentSchemaVersion,
            contract.ContractId,
            contract.CanonicalIntentHash,
            OutfitterRouteAuthorityPhase.Paused,
            [new("line-1", 10, "Exact HQ Ring", EquipmentQuality.High, 2, 1, PurchasedListingGil, RemainingListingGil, FinalizedLineGil)],
            PurchasedListingGil,
            0,
            null,
            0,
            "Persisted dry-run sunk state.",
            preparedAt.AddMinutes(1))
        {
            SunkPurchases = [receipt],
        };
        MarketAcquisitionRequestDocumentPersistence.Save(config, document);
        MarketAcquisitionClaimPersistence.Save(config, claim, "accept-key", "reject-key");
        config.OutfitterRouteExecutionStateJson = Newtonsoft.Json.JsonConvert.SerializeObject(state);
        return new(config);
    }

    private static MarketAcquisitionRequestWorkspace CreateWorkspace(
        Configuration config,
        IMarketAcquisitionListingSource listingSource)
    {
        var httpClient = new HttpClient();
        return new(
            config,
            new MarketAcquisitionRequestClient(httpClient),
            new MarketAcquisitionPlanPreparationService(listingSource, new MarketAcquisitionWorldVisitCatalog(config)),
            () => { },
            _ => { });
    }

    private static void Connect(
        MarketAcquisitionRequestWorkspace workspace,
        MarketAcquisitionRequestDocument document) => workspace.Connect(
        _ => { },
        _ => false,
        () => MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(document),
        _ => { },
        () => false,
        _ => { });

    private static MarketAcquisitionLiveCandidatePlan CandidatePlan() => new()
    {
        Status = "Ready",
        ListingReadState = MarketBoardListingReadState.FreshComplete,
        WouldBuyQuantity = 1,
        WouldSpendGil = RemainingListingGil,
    };

    private static MarketBoardReadResult CurrentListing() => new()
    {
        Status = "Ready",
        ReadState = MarketBoardListingReadState.FreshComplete,
        ItemId = 10,
        WorldName = "Siren",
        Listings =
        [
            new MarketBoardLiveListing
            {
                ItemId = 10,
                WorldName = "Siren",
                ListingId = "listing-2",
                RetainerId = "retainer-2",
                Quantity = 1,
                UnitPrice = RemainingListingGil,
                IsHq = true,
            },
        ],
    };

    private sealed class CountingListingSource(params MarketAcquisitionListing[] listings) : IMarketAcquisitionListingSource
    {
        public int FetchCount { get; private set; }

        public Task<IReadOnlyList<MarketAcquisitionListing>> FetchListingsAsync(
            string region,
            uint itemId,
            int listingLimit,
            CancellationToken cancellationToken)
        {
            FetchCount++;
            return Task.FromResult<IReadOnlyList<MarketAcquisitionListing>>(listings);
        }

        public Task<IReadOnlyList<MarketAcquisitionListing>> FetchListingsForWorldAsync(
            string worldName,
            uint itemId,
            int listingLimit,
            CancellationToken cancellationToken)
        {
            FetchCount++;
            return Task.FromResult<IReadOnlyList<MarketAcquisitionListing>>(listings);
        }
    }

    [Fact]
    public async Task FinalizedDryRunPreparationPinsContractLotsInsteadOfOrdinaryListingSource()
    {
        var fixture = PersistedFixture();
        fixture.Config.OutfitterRouteExecutionStateJson = null;
        var divergent = new MarketAcquisitionListing
        {
            ItemId = 10,
            ItemName = "Exact HQ Ring",
            ListingId = "ordinary-substitute",
            WorldName = "Siren",
            RetainerId = "ordinary-retainer",
            Quantity = 2,
            UnitPrice = 100_000,
            IsHq = true,
            LastReviewTimeUtc = DateTimeOffset.Parse("2026-07-21T08:30:00Z"),
        };
        var listingSource = new CountingListingSource(divergent);
        using var workspace = CreateWorkspace(fixture.Config, listingSource);
        var document = MarketAcquisitionRequestDocumentPersistence.Restore(fixture.Config);
        Connect(workspace, document);

        await workspace.PreparePlanAsync("Siren", TimeSpan.FromHours(18), false, document);

        var plan = Assert.IsType<MarketAcquisitionPlan>(workspace.PreparedPlan);
        var listings = plan.WorldBatches.SelectMany(batch => batch.Listings).ToArray();
        Assert.Equal(["listing-1", "listing-2"], listings.Select(listing => listing.ListingId).Order().ToArray());
        Assert.DoesNotContain(listings, listing => listing.ListingId == divergent.ListingId);
        Assert.Equal(0, listingSource.FetchCount);
        Assert.Contains("exact finalized listing authority", workspace.Status, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PersistedFixtureData(Configuration config)
    {
        public Configuration Config { get; } = config;
        public int SaveCount { get; set; }
    }
}
