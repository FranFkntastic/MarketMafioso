namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionPlanPreparationServiceTests
{
    [Fact]
    public async Task PrepareAsync_ForLegacySingleLineClaim_BuildsReadyPlan()
    {
        var source = new FakeListingSource();
        source.RegionListings[(("North America", 2u))] =
        [
            CreateListing(2, "Fire Shard", "Siren", quantity: 5, unitPrice: 10),
        ];
        var service = new MarketMafioso.MarketAcquisition.MarketAcquisitionPlanPreparationService(
            source,
            new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitCatalog(new MarketMafioso.Configuration()));

        var result = await service.PrepareAsync(new MarketMafioso.MarketAcquisition.MarketAcquisitionPlanPreparationRequest
        {
            Claim = CreateClaim(worldMode: "Recommended", lines: []),
            CurrentWorld = "Siren",
            PreparedAtUtc = DateTimeOffset.UnixEpoch,
            RecentWorldTtl = TimeSpan.FromHours(18),
        }, CancellationToken.None);

        Assert.Equal("Ready", result.Plan.Status);
        Assert.Single(result.Plan.Lines);
        Assert.Single(result.Plan.WorldBatches);
        Assert.Equal("Prepared 1 world batch(es).", result.StatusMessage);
    }

    [Fact]
    public async Task PrepareAsync_ForAllWorldSweep_SkipsRecentWorldsWithoutFreshEvidence()
    {
        var preparedAtUtc = DateTimeOffset.Parse("2026-07-05T12:00:00Z");
        var config = new MarketMafioso.Configuration();
        var catalog = new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitCatalog(config);
        catalog.RecordProbe(new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitRecord
        {
            WorldName = "Siren",
            DataCenter = "Aether",
            ItemId = 2,
            ItemName = "Fire Shard",
            HqPolicy = "Either",
            MaxUnitPrice = 100,
            CheckedAtUtc = preparedAtUtc.AddHours(-1),
            Result = "Probed",
            Source = "Route",
        });
        var source = new FakeListingSource();
        source.RegionListings[(("North America", 2u))] =
        [
            CreateListing(2, "Fire Shard", "Siren", quantity: 5, unitPrice: 10, lastReviewTimeUtc: preparedAtUtc.AddHours(-2)),
            CreateListing(2, "Fire Shard", "Leviathan", quantity: 5, unitPrice: 10, lastReviewTimeUtc: preparedAtUtc.AddHours(-2)),
        ];
        var service = new MarketMafioso.MarketAcquisition.MarketAcquisitionPlanPreparationService(source, catalog);

        var result = await service.PrepareAsync(new MarketMafioso.MarketAcquisition.MarketAcquisitionPlanPreparationRequest
        {
            Claim = CreateClaim(worldMode: "AllWorldSweep", lines: [CreateLine()]),
            CurrentWorld = "Siren",
            PreparedAtUtc = preparedAtUtc,
            RecentWorldTtl = TimeSpan.FromHours(18),
        }, CancellationToken.None);

        Assert.Equal(1, result.RecentSkippedWorldCount);
        Assert.Equal(0, result.FreshEvidenceWorldCount);
        Assert.DoesNotContain(result.Plan.WorldBatches, batch => batch.WorldName == "Siren");
        Assert.Contains(result.Plan.WorldBatches, batch => batch.WorldName == "Leviathan");
        Assert.Contains("Skipped 1 recent sweep world/item check(s).", result.StatusMessage);
    }

    [Fact]
    public async Task PrepareAsync_ForAllWorldSweep_ReopensRecentWorldWithNewerUsefulEvidence()
    {
        var preparedAtUtc = DateTimeOffset.Parse("2026-07-05T12:00:00Z");
        var config = new MarketMafioso.Configuration();
        var catalog = new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitCatalog(config);
        catalog.RecordProbe(new MarketMafioso.MarketAcquisition.MarketAcquisitionWorldVisitRecord
        {
            WorldName = "Siren",
            DataCenter = "Aether",
            ItemId = 2,
            ItemName = "Fire Shard",
            HqPolicy = "Either",
            MaxUnitPrice = 100,
            CheckedAtUtc = preparedAtUtc.AddHours(-1),
            Result = "Probed",
            Source = "Route",
        });
        var source = new FakeListingSource();
        source.RegionListings[(("North America", 2u))] =
        [
            CreateListing(2, "Fire Shard", "Siren", quantity: 5, unitPrice: 10, lastReviewTimeUtc: preparedAtUtc.AddMinutes(-10)),
        ];
        source.WorldListings[(("Siren", 2u))] =
        [
            CreateListing(2, "Fire Shard", "Siren", quantity: 5, unitPrice: 10, lastReviewTimeUtc: preparedAtUtc.AddMinutes(-10)),
        ];
        var service = new MarketMafioso.MarketAcquisition.MarketAcquisitionPlanPreparationService(source, catalog);

        var result = await service.PrepareAsync(new MarketMafioso.MarketAcquisition.MarketAcquisitionPlanPreparationRequest
        {
            Claim = CreateClaim(worldMode: "AllWorldSweep", lines: [CreateLine()]),
            CurrentWorld = "Siren",
            PreparedAtUtc = preparedAtUtc,
            RecentWorldTtl = TimeSpan.FromHours(18),
        }, CancellationToken.None);

        Assert.Equal(0, result.RecentSkippedWorldCount);
        Assert.Equal(1, result.FreshEvidenceWorldCount);
        Assert.Contains(result.Plan.WorldBatches, batch => batch.WorldName == "Siren");
        Assert.Contains("Reopened 1 recent world/item check(s) with fresh Universalis evidence.", result.StatusMessage);
    }

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionClaimView CreateClaim(
        string worldMode,
        List<MarketMafioso.MarketAcquisition.MarketAcquisitionBatchLineView> lines) =>
        new()
        {
            Id = "batch-1",
            Status = "AcceptedInPlugin",
            TargetCharacterName = "Tester",
            TargetWorld = "Siren",
            Region = "North America",
            ItemId = 2,
            ItemName = "Fire Shard",
            QuantityMode = "TargetQuantity",
            Quantity = 5,
            HqPolicy = "Either",
            MaxUnitPrice = 100,
            WorldMode = worldMode,
            SweepScope = "Region",
            ClaimToken = "claim-token",
            Lines = lines,
        };

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionBatchLineView CreateLine() =>
        new()
        {
            LineId = "line-1",
            BatchId = "batch-1",
            Ordinal = 0,
            ItemId = 2,
            ItemName = "Fire Shard",
            QuantityMode = "TargetQuantity",
            TargetQuantity = 5,
            HqPolicy = "Either",
            MaxUnitPrice = 100,
            Status = "AcceptedInPlugin",
        };

    private static MarketMafioso.MarketAcquisition.MarketAcquisitionListing CreateListing(
        uint itemId,
        string itemName,
        string worldName,
        uint quantity,
        uint unitPrice,
        DateTimeOffset? lastReviewTimeUtc = null) =>
        new()
        {
            ItemId = itemId,
            ItemName = itemName,
            ListingId = $"{worldName}-{itemId}",
            WorldName = worldName,
            RetainerName = "Seller",
            RetainerId = "retainer",
            Quantity = quantity,
            UnitPrice = unitPrice,
            LastReviewTimeUtc = lastReviewTimeUtc ?? DateTimeOffset.UnixEpoch,
        };

    private sealed class FakeListingSource : MarketMafioso.MarketAcquisition.IMarketAcquisitionListingSource
    {
        public Dictionary<(string Region, uint ItemId), IReadOnlyList<MarketMafioso.MarketAcquisition.MarketAcquisitionListing>> RegionListings { get; } = [];
        public Dictionary<(string WorldName, uint ItemId), IReadOnlyList<MarketMafioso.MarketAcquisition.MarketAcquisitionListing>> WorldListings { get; } = [];

        public Task<IReadOnlyList<MarketMafioso.MarketAcquisition.MarketAcquisitionListing>> FetchListingsAsync(
            string region,
            uint itemId,
            int listingLimit,
            CancellationToken cancellationToken) =>
            Task.FromResult(RegionListings.GetValueOrDefault((region, itemId)) ?? []);

        public Task<IReadOnlyList<MarketMafioso.MarketAcquisition.MarketAcquisitionListing>> FetchListingsForWorldAsync(
            string worldName,
            uint itemId,
            int listingLimit,
            CancellationToken cancellationToken) =>
            Task.FromResult(WorldListings.GetValueOrDefault((worldName, itemId)) ?? []);
    }
}
