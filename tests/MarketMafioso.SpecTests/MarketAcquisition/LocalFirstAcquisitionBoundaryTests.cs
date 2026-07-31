using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class LocalFirstAcquisitionBoundaryTests
{
    [Fact]
    public void LocalWorkbench_BuildsExecutableRequestWithoutHostedClaim()
    {
        var document = CreateDocument();

        var request = MarketAcquisitionRequestDocumentMapper.BuildLocalExecutionRequest(
            document,
            "Wei Ning",
            "Siren",
            DateTimeOffset.UnixEpoch);

        Assert.Equal("local:local-request", request.Id);
        Assert.Equal(MarketAcquisitionOrigins.LocalWorkbench, request.Origin);
        Assert.Equal(MarketAcquisitionStatuses.AcceptedInPlugin, request.Status);
        Assert.Empty(request.ClaimToken);
        Assert.Equal("Wei Ning", request.TargetCharacterName);
        Assert.Equal("Siren", request.TargetWorld);
        var line = Assert.Single(request.Lines);
        Assert.Equal("local:local-request:line:1", line.LineId);
        Assert.Equal(3u, line.TargetQuantity);
        Assert.Equal(100u, line.MaxUnitPrice);
    }

    [Fact]
    public async Task LocalWorkbench_PreparesUniversalisPlanWithoutHostedClaim()
    {
        var request = MarketAcquisitionRequestDocumentMapper.BuildLocalExecutionRequest(
            CreateDocument(),
            "Wei Ning",
            "Siren",
            DateTimeOffset.UnixEpoch);
        var service = new MarketAcquisitionPlanPreparationService(
            new StaticListingSource(),
            new MarketAcquisitionWorldVisitCatalog(new Configuration()));

        var result = await service.PrepareAsync(
            new MarketAcquisitionPlanPreparationRequest
            {
                Claim = request,
                CurrentWorld = "Siren",
                PreparedAtUtc = DateTimeOffset.UnixEpoch.AddHours(1),
                RecentWorldTtl = TimeSpan.FromHours(24),
            },
            CancellationToken.None);

        Assert.Equal("Ready", result.Plan.Status);
        Assert.Equal(request.Id, result.Plan.RequestId);
        Assert.Equal("Siren", Assert.Single(result.Plan.WorldBatches).WorldName);
    }

    [Fact]
    public void Finalization_DoesNotRequireHostedSynchronizationOrClaim()
    {
        var presentation = MarketAcquisitionWorkbenchFinalizationPresenter.Build(new(
            LineCount: 1,
            IsDraftValid: true,
            FirstDraftError: null,
            HasCharacterScope: true,
            IsBusy: false,
            IsRouteActive: false,
            IsSynchronizing: false,
            SyncStatus: "NewDraft",
            VisibleSyncStatus: "Local draft",
            ClaimStatus: null,
            HasClaimedRequest: false,
            HasCurrentPlan: false,
            IsCurrentPlanStale: false,
            WorkspaceStatus: "Local Workbench ready.",
            TotalSpendCeiling: 300,
            TargetQuantityTotal: 3));

        Assert.True(presentation.CanFinalize);
        Assert.Equal("Ready to finalize locally", presentation.Title);
        Assert.Contains("hosting optional", presentation.Detail);
    }

    [Fact]
    public void HostedSynchronizationInFlight_DoesNotBlockLocalFinalization()
    {
        var presentation = MarketAcquisitionWorkbenchFinalizationPresenter.Build(new(
            LineCount: 1,
            IsDraftValid: true,
            FirstDraftError: null,
            HasCharacterScope: true,
            IsBusy: false,
            IsRouteActive: false,
            IsSynchronizing: true,
            SyncStatus: "Synchronizing",
            VisibleSyncStatus: "Saving hosted copy...",
            ClaimStatus: null,
            HasClaimedRequest: false,
            HasCurrentPlan: false,
            IsCurrentPlanStale: false,
            WorkspaceStatus: "Local Workbench ready.",
            TotalSpendCeiling: 300,
            TargetQuantityTotal: 3));

        Assert.True(presentation.CanFinalize);
        Assert.Equal("Ready to finalize locally", presentation.Title);
    }

    [Fact]
    public void UnsyncedLocalIntent_DetachesHostedClaimFromExecution()
    {
        var hosted = new MarketAcquisitionClaimView { Id = "hosted-request" };
        var synced = CreateDocument() with { RemoteRequestId = hosted.Id };
        synced = synced with
        {
            LastSyncedHash = MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(synced),
        };
        var locallyEdited = synced with
        {
            LocalRevision = synced.LocalRevision + 1,
            Lines =
            [
                synced.Lines[0] with { TargetQuantity = 4 },
            ],
        };

        Assert.True(MarketAcquisitionRequestWorkspace.CanUseHostedClaim(synced, hosted));
        Assert.False(MarketAcquisitionRequestWorkspace.CanUseHostedClaim(locallyEdited, hosted));
    }

    private static MarketAcquisitionRequestDocument CreateDocument() =>
        new()
        {
            LocalRequestId = "local-request",
            LocalRevision = 4,
            TargetCharacterName = "Wei Ning",
            TargetWorld = "Siren",
            Region = "North America",
            WorldMode = "Recommended",
            Lines =
            [
                new MarketAcquisitionRequestLineDocument
                {
                    ItemId = 5339,
                    ItemName = "Rose Gold Ingot",
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = 3,
                    HqPolicy = "Either",
                    MaxUnitPrice = 100,
                    GilCap = 300,
                },
            ],
        };

    private sealed class StaticListingSource : IMarketAcquisitionListingSource
    {
        private static readonly IReadOnlyList<MarketAcquisitionListing> Listings =
        [
            new()
            {
                ItemId = 5339,
                ItemName = "Rose Gold Ingot",
                ListingId = "listing-1",
                WorldName = "Siren",
                WorldId = 64,
                RetainerName = "Seller",
                RetainerId = "retainer-1",
                Quantity = 3,
                UnitPrice = 50,
                LastReviewTimeUtc = DateTimeOffset.UnixEpoch,
            },
        ];

        public Task<IReadOnlyList<MarketAcquisitionListing>> FetchListingsAsync(
            string region,
            uint itemId,
            int listingLimit,
            CancellationToken cancellationToken) =>
            Task.FromResult(Listings);

        public Task<IReadOnlyList<MarketAcquisitionListing>> FetchListingsForWorldAsync(
            string worldName,
            uint itemId,
            int listingLimit,
            CancellationToken cancellationToken) =>
            Task.FromResult(Listings);
    }
}
