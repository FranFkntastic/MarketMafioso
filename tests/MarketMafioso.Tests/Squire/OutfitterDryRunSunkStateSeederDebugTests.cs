#if DEBUG
using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter.Acquisition;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterDryRunSunkStateSeederDebugTests
{
    [Fact]
    public void Seed_UsesRealConfigurationStoreAndDuplicateIsIdempotent()
    {
        var fixture = Fixture();
        var config = new Configuration();
        var saveCount = 0;
        var store = new ConfigurationOutfitterRouteExecutionStateStore(config, () => saveCount++);
        var seed = OutfitterDryRunSunkStateSeeder.CreateSemanticSeed(
            fixture.Contract, fixture.Document, fixture.Claim, fixture.Plan);

        var first = OutfitterDryRunSunkStateSeeder.Seed(
            store, fixture.Contract, fixture.Document, fixture.Claim, fixture.Plan, seed, Time(2));
        var persistedJson = config.OutfitterRouteExecutionStateJson;
        var duplicate = OutfitterDryRunSunkStateSeeder.Seed(
            store, fixture.Contract, fixture.Document, fixture.Claim, fixture.Plan, seed, Time(3));

        Assert.Equal(OutfitterDryRunSeedStatus.Seeded, first.Status);
        Assert.Equal(OutfitterDryRunSeedStatus.AlreadySeeded, duplicate.Status);
        Assert.Equal(1, saveCount);
        Assert.Equal(persistedJson, config.OutfitterRouteExecutionStateJson);
        var state = Assert.IsType<OutfitterRouteExecutionState>(store.Restore());
        Assert.Equal(OutfitterRouteAuthorityPhase.Paused, state.Phase);
        Assert.Equal(1u, state.Lines[0].PurchasedQuantity);
        Assert.Equal(100u, state.Lines[0].SpentGil);
        Assert.Equal(100ul, state.TotalSpentGil);
        Assert.Single(state.SunkPurchases);
    }

    [Theory]
    [InlineData("contract")]
    [InlineData("world")]
    [InlineData("line")]
    [InlineData("stale")]
    [InlineData("over-cap")]
    public void Seed_MismatchedStaleAndOverCapEvidenceFailsClosed(string invalidity)
    {
        var fixture = Fixture();
        var config = new Configuration();
        var saveCount = 0;
        var store = new ConfigurationOutfitterRouteExecutionStateStore(config, () => saveCount++);
        var seed = OutfitterDryRunSunkStateSeeder.CreateSemanticSeed(
            fixture.Contract, fixture.Document, fixture.Claim, fixture.Plan);
        seed = invalidity switch
        {
            "contract" => seed with { ContractId = "different-contract" },
            "world" => seed with { WorldName = "Ravana" },
            "line" => seed with { LineId = "different-line" },
            "stale" => seed with { PlanPreparedAtUtc = seed.PlanPreparedAtUtc.AddSeconds(-1) },
            "over-cap" => seed with { Quantity = 3, TotalGil = 300 },
            _ => seed,
        };
        seed = seed with { ReceiptId = OutfitterDryRunExecutionStateRestorer.ComputeReceiptId(seed) };

        var result = OutfitterDryRunSunkStateSeeder.Seed(
            store, fixture.Contract, fixture.Document, fixture.Claim, fixture.Plan, seed, Time(2));

        Assert.Equal(OutfitterDryRunSeedStatus.Rejected, result.Status);
        Assert.Null(config.OutfitterRouteExecutionStateJson);
        Assert.Equal(0, saveCount);
    }

    [Fact]
    public void Seed_RejectsSingleQuantityTwoListingAndPartialStackReceipt()
    {
        var fixture = Fixture(splitListings: false);
        var store = new ConfigurationOutfitterRouteExecutionStateStore(new Configuration(), () => { });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            OutfitterDryRunSunkStateSeeder.CreateSemanticSeed(
                fixture.Contract, fixture.Document, fixture.Claim, fixture.Plan));
        var partial = Receipt(fixture, quantity: 1);
        var result = OutfitterDryRunSunkStateSeeder.Seed(
            store, fixture.Contract, fixture.Document, fixture.Claim, fixture.Plan, partial, Time(2));

        Assert.Contains("complete planned listing stack", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OutfitterDryRunSeedStatus.Rejected, result.Status);
        Assert.Null(store.Restore());
    }

    [Fact]
    public void SubstitutedOrdinaryRowsCannotSeedButPinnedContractRowsRestart()
    {
        var fixture = Fixture();
        var substituted = SubstituteListingIdentities(fixture.Plan);
        var store = new ConfigurationOutfitterRouteExecutionStateStore(new Configuration(), () => { });

        Assert.Throws<InvalidOperationException>(() => OutfitterDryRunSunkStateSeeder.CreateSemanticSeed(
            fixture.Contract, fixture.Document, fixture.Claim, substituted));
        var rejected = OutfitterDryRunSunkStateSeeder.Seed(
            store,
            fixture.Contract,
            fixture.Document,
            fixture.Claim,
            substituted,
            Receipt(fixture, quantity: 1, plan: substituted),
            DateTimeOffset.Parse("2026-07-20T12:01:00Z"));

        var pinned = OutfitterDryRunPreparedPlanRestorer.Prepare(
            fixture.Contract,
            fixture.Document,
            fixture.Claim,
            DateTimeOffset.Parse("2026-07-20T12:00:00Z"));
        var seed = OutfitterDryRunSunkStateSeeder.CreateSemanticSeed(
            fixture.Contract, fixture.Document, fixture.Claim, pinned);
        var seeded = OutfitterDryRunSunkStateSeeder.Seed(
            store,
            fixture.Contract,
            fixture.Document,
            fixture.Claim,
            pinned,
            seed,
            DateTimeOffset.Parse("2026-07-20T12:01:00Z"));
        var persistedDocument = fixture.Document with
        {
            LastPlanHash = MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(fixture.Document),
        };
        var remaining = OutfitterDryRunPreparedPlanRestorer.Restore(
            fixture.Contract,
            persistedDocument,
            fixture.Claim,
            store.Restore()!);

        Assert.Equal(OutfitterDryRunSeedStatus.Rejected, rejected.Status);
        Assert.Equal(OutfitterDryRunSeedStatus.Seeded, seeded.Status);
        var listing = Assert.Single(remaining.WorldBatches.SelectMany(batch => batch.Listings));
        Assert.Equal("listing-2", listing.ListingId);
        Assert.Equal("retainer-2", listing.RetainerId);
        Assert.Equal(1u, listing.Quantity);
    }

    private static FixtureData Fixture(bool splitListings = true)
    {
        var document = MarketAcquisitionRequestDocument.CreateDefault("Fran", "Siren");
        var sourceTransfer = OutfitterWorkbenchAuthorityTests.Transfer();
        var sourceLot = sourceTransfer.MarketLots.Single();
        var transfer = sourceTransfer with
        {
            DryRunOnly = true,
            MarketLots = splitListings
                ?
                [
                    sourceLot with { RequiredQuantity = 1, ObservedAvailableQuantity = 1, ObservedTotalPriceGil = 100 },
                    sourceLot with
                    {
                        RequiredQuantity = 1,
                        ObservedAvailableQuantity = 1,
                        ObservedTotalPriceGil = 100,
                        DiscoveryObservationId = "listing-2",
                        RetainerName = "Retainer 2",
                        RetainerId = "retainer-2",
                    },
                ]
                : sourceTransfer.MarketLots,
        };
        document = OutfitterWorkbenchAuthorityService.Stage(
            document,
            transfer);
        document = OutfitterWorkbenchAuthorityService.Finalize(document);
        var contract = document.OutfitterAuthority!.FinalizedContract!;
        var claim = new MarketAcquisitionClaimView
        {
            Id = "request-1",
            ClaimToken = "claim-token",
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
                    HqPolicy = "HqOnly",
                    MaxUnitPrice = 100,
                    GilCap = 200,
                },
            ],
        };
        var listings = splitListings
            ? new[] { Listing("listing-1", "retainer-1", 1, sourceLot.ReviewedAtUtc), Listing("listing-2", "retainer-2", 1, sourceLot.ReviewedAtUtc) }
            : new[] { Listing("listing-1", "retainer-1", 2, sourceLot.ReviewedAtUtc) };
        var subtask = new MarketAcquisitionWorldItemSubtask
        {
            LineId = "line-1",
            ItemId = 10,
            ItemName = "Exact HQ Ring",
            WorldName = "Siren",
            DataCenter = "Aether",
            QuantityMode = "TargetQuantity",
            RequestedQuantity = 2,
            HqPolicy = "HqOnly",
            MaxUnitPrice = 100,
            GilCap = 200,
            PlannedQuantity = 2,
            PlannedGil = 200,
            Listings = listings,
        };
        var plan = new MarketAcquisitionPlan
        {
            RequestId = claim.Id,
            Status = "Ready",
            PreparedAtUtc = Time(1),
            Lines =
            [
                new MarketAcquisitionPlanLine
                {
                    LineId = "line-1",
                    ItemId = 10,
                    ItemName = "Exact HQ Ring",
                    QuantityMode = "TargetQuantity",
                    RequestedQuantity = 2,
                    HqPolicy = "HqOnly",
                    MaxUnitPrice = 100,
                    GilCap = 200,
                    Status = "Ready",
                    PlannedQuantity = 2,
                    PlannedGil = 200,
                },
            ],
            WorldBatches =
            [
                new MarketAcquisitionWorldBatch
                {
                    WorldName = "Siren",
                    DataCenter = "Aether",
                    PlannedQuantity = 2,
                    PlannedGil = 200,
                    ItemSubtasks = [subtask],
                    Listings = listings,
                },
            ],
        };
        return new(document, contract, claim, plan);
    }

    private static MarketAcquisitionPlannedListing Listing(
        string listingId,
        string retainerId,
        uint quantity,
        DateTimeOffset reviewedAt) => new()
    {
        LineId = "line-1",
        ItemId = 10,
        ItemName = "Exact HQ Ring",
        ListingId = listingId,
        RetainerId = retainerId,
        Quantity = quantity,
        UnitPrice = 100,
        TotalGil = checked(quantity * 100),
        IsHq = true,
        LastReviewTimeUtc = reviewedAt,
    };

    private static OutfitterRouteSunkPurchase Receipt(
        FixtureData fixture,
        uint quantity,
        MarketAcquisitionPlan? plan = null)
    {
        plan ??= fixture.Plan;
        var listing = plan.WorldBatches.Single().Listings.First();
        var draft = new OutfitterRouteSunkPurchase
        {
            SchemaVersion = OutfitterRouteSunkPurchase.CurrentSchemaVersion,
            ReceiptId = string.Empty,
            ContractId = fixture.Contract.ContractId,
            CanonicalIntentHash = fixture.Contract.CanonicalIntentHash,
            WorkbenchDocumentId = fixture.Document.LocalRequestId,
            WorkbenchRevision = fixture.Document.LocalRevision,
            PlanRequestId = plan.RequestId,
            PlanPreparedAtUtc = plan.PreparedAtUtc,
            WorldName = plan.WorldBatches.Single().WorldName,
            LineId = listing.LineId,
            ItemId = listing.ItemId,
            Quality = EquipmentQuality.High,
            ListingId = listing.ListingId,
            RetainerId = listing.RetainerId,
            Quantity = quantity,
            UnitPriceGil = listing.UnitPrice,
            TotalGil = checked(quantity * listing.UnitPrice),
        };
        return draft with { ReceiptId = OutfitterDryRunExecutionStateRestorer.ComputeReceiptId(draft) };
    }

    private static MarketAcquisitionPlan SubstituteListingIdentities(MarketAcquisitionPlan plan)
    {
        var batch = plan.WorldBatches.Single();
        var listings = batch.Listings.Select((listing, index) => listing with
        {
            ListingId = $"ordinary-listing-{index + 1}",
            RetainerId = $"ordinary-retainer-{index + 1}",
        }).ToArray();
        var subtask = batch.ItemSubtasks.Single() with { Listings = listings };
        return plan with
        {
            WorldBatches = [batch with { Listings = listings, ItemSubtasks = [subtask] }],
        };
    }

    private static DateTimeOffset Time(int minute) => DateTimeOffset.UnixEpoch.AddMinutes(minute);

    private sealed record FixtureData(
        MarketAcquisitionRequestDocument Document,
        OutfitterExecutionContract Contract,
        MarketAcquisitionClaimView Claim,
        MarketAcquisitionPlan Plan);
}
#endif
