using MarketMafioso.MarketAcquisition;
using System.Net;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRequestSyncServiceTests
{
    [Fact]
    public async Task SyncAsync_CreatesClaimsAndAcceptsNewDocumentWithoutClearingLines()
    {
        var client = new FakeClient();
        var service = new MarketAcquisitionRequestSyncService(client);
        var document = CreateDocument();

        var result = await service.SyncAsync(
            new MarketAcquisitionRequestSyncRequest(
                "server",
                "client-secret",
                "Eriana Ning",
                "Siren",
                "plugin-instance",
                document,
                ExistingClaim: null),
            CancellationToken.None);

        Assert.Equal(["create", "claim", "accept"], client.Calls);
        Assert.Equal("AcceptedInPlugin", result.Claim.Status);
        Assert.Equal("batch-1", result.Document.RemoteRequestId);
        Assert.Equal(2, result.Document.RemoteRevision);
        Assert.Equal("SyncedClean", result.Document.SyncStatus);
        Assert.Single(result.Document.Lines);
        Assert.Equal(document.Lines[0].ItemId, result.Document.Lines[0].ItemId);
        Assert.False(string.IsNullOrWhiteSpace(result.Document.LastSyncedHash));
    }

    [Fact]
    public async Task SyncAsync_ReplacesExistingClaimAndPreservesClaimToken()
    {
        var client = new FakeClient();
        var service = new MarketAcquisitionRequestSyncService(client);
        var document = CreateDocument() with
        {
            RemoteRequestId = "batch-1",
            RemoteRevision = 5,
            LastSyncedHash = "old-hash",
        };
        var claim = new MarketAcquisitionClaimView
        {
            Id = "batch-1",
            Revision = 5,
            Status = "AcceptedInPlugin",
            ClaimToken = "claim-token",
            TargetCharacterName = "Eriana Ning",
            TargetWorld = "Siren",
            Region = "North America",
        };

        var result = await service.SyncAsync(
            new MarketAcquisitionRequestSyncRequest(
                "server",
                "client-secret",
                "Eriana Ning",
                "Siren",
                "plugin-instance",
                document,
                claim),
            CancellationToken.None);

        Assert.Equal(["replace"], client.Calls);
        Assert.Equal("claim-token", result.Claim.ClaimToken);
        Assert.Equal(6, result.Document.RemoteRevision);
        Assert.Equal(5, client.LastReplaceRequest?.ExpectedRevision);
        Assert.Equal("SyncedClean", result.Document.SyncStatus);
    }

    [Fact]
    public async Task SyncAsync_RecreatesCurrentDocumentWhenRemoteCopyIsMissing()
    {
        var client = new FakeClient { ReplaceNotFound = true };
        var service = new MarketAcquisitionRequestSyncService(client);
        var document = CreateDocument() with
        {
            RemoteRequestId = "missing-batch",
            RemoteRevision = 5,
        };
        var claim = new MarketAcquisitionClaimView
        {
            Id = "missing-batch",
            Revision = 5,
            Status = "AcceptedInPlugin",
            ClaimToken = "old-claim-token",
            TargetCharacterName = "Eriana Ning",
            TargetWorld = "Siren",
            Region = "North America",
        };

        var result = await service.SyncAsync(
            new MarketAcquisitionRequestSyncRequest(
                "server",
                "client-secret",
                "Eriana Ning",
                "Siren",
                "plugin-instance",
                document,
                claim),
            CancellationToken.None);

        Assert.Equal(["replace", "create", "claim", "accept"], client.Calls);
        Assert.Equal("batch-1", result.Document.RemoteRequestId);
        Assert.Equal("AcceptedInPlugin", result.Claim.Status);
        Assert.Equal("SyncedClean", result.Document.SyncStatus);
    }

    private static MarketAcquisitionRequestDocument CreateDocument() => new()
    {
        LocalRequestId = "local-1",
        LocalRevision = 3,
        TargetCharacterName = "Eriana Ning",
        TargetWorld = "Siren",
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
        Lines =
        [
            new MarketAcquisitionRequestLineDocument
            {
                ItemId = 19951,
                ItemName = "Koppranickel Ore",
                QuantityMode = "AllBelowThreshold",
                MaxQuantity = 25,
                HqPolicy = "Either",
                MaxUnitPrice = 276,
            },
        ],
    };

    private sealed class FakeClient : IMarketAcquisitionRequestClient
    {
        public List<string> Calls { get; } = [];
        public MarketAcquisitionBatchReplaceRequest? LastReplaceRequest { get; private set; }
        public bool ReplaceNotFound { get; init; }

        public Task<MarketAcquisitionRequestView> GetBatchAsync(
            string serverUrl,
            string clientApiKey,
            string requestId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MarketAcquisitionRequestView> CreateBatchAsync(
            string serverUrl,
            string clientApiKey,
            MarketAcquisitionBatchCreateRequest createRequest,
            CancellationToken cancellationToken)
        {
            Calls.Add("create");
            return Task.FromResult(new MarketAcquisitionRequestView
            {
                Id = "batch-1",
                Revision = 1,
                Status = "PendingPickup",
                Origin = createRequest.Origin,
                CreatedByPluginInstanceId = createRequest.CreatedByPluginInstanceId,
                TargetCharacterName = createRequest.TargetCharacterName,
                TargetWorld = createRequest.TargetWorld,
                Region = createRequest.Region,
                WorldMode = createRequest.WorldMode,
                Lines =
                [
                    new MarketAcquisitionBatchLineView
                    {
                        LineId = "line-1",
                        BatchId = "batch-1",
                        ItemId = createRequest.Lines[0].ItemId,
                        ItemName = createRequest.Lines[0].ItemName,
                        QuantityMode = createRequest.Lines[0].QuantityMode,
                        MaxQuantity = createRequest.Lines[0].MaxQuantity,
                        HqPolicy = createRequest.Lines[0].HqPolicy,
                        MaxUnitPrice = createRequest.Lines[0].MaxUnitPrice,
                    },
                ],
            });
        }

        public Task<MarketAcquisitionRequestView> ReplaceBatchAsync(
            string serverUrl,
            string clientApiKey,
            string requestId,
            MarketAcquisitionBatchReplaceRequest replaceRequest,
            CancellationToken cancellationToken)
        {
            Calls.Add("replace");
            LastReplaceRequest = replaceRequest;
            if (ReplaceNotFound)
            {
                throw new MarketAcquisitionLifecycleHttpException(
                    HttpStatusCode.NotFound,
                    "replace",
                    error: null,
                    responseBody: null);
            }

            return Task.FromResult(new MarketAcquisitionRequestView
            {
                Id = requestId,
                Revision = replaceRequest.ExpectedRevision + 1,
                Status = "AcceptedInPlugin",
                Origin = MarketAcquisitionOrigins.PluginBuilder,
                TargetCharacterName = "Eriana Ning",
                TargetWorld = "Siren",
                Region = replaceRequest.Region,
                WorldMode = replaceRequest.WorldMode,
                Lines =
                [
                    new MarketAcquisitionBatchLineView
                    {
                        LineId = "line-1",
                        BatchId = requestId,
                        ItemId = replaceRequest.Lines[0].ItemId,
                        ItemName = replaceRequest.Lines[0].ItemName,
                        QuantityMode = replaceRequest.Lines[0].QuantityMode,
                        MaxQuantity = replaceRequest.Lines[0].MaxQuantity,
                        HqPolicy = replaceRequest.Lines[0].HqPolicy,
                        MaxUnitPrice = replaceRequest.Lines[0].MaxUnitPrice,
                    },
                ],
            });
        }

        public Task<MarketAcquisitionClaimView> ClaimAsync(
            string serverUrl,
            string clientApiKey,
            string requestId,
            string characterName,
            string world,
            string pluginInstanceId,
            CancellationToken cancellationToken)
        {
            Calls.Add("claim");
            return Task.FromResult(new MarketAcquisitionClaimView
            {
                Id = requestId,
                Revision = 1,
                Status = "Claimed",
                ClaimToken = "claim-token",
                TargetCharacterName = characterName,
                TargetWorld = world,
                Region = "North America",
            });
        }

        public Task<MarketAcquisitionRequestView> AcceptAsync(
            string serverUrl,
            string clientApiKey,
            string requestId,
            string claimToken,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            Calls.Add("accept");
            return Task.FromResult(new MarketAcquisitionRequestView
            {
                Id = requestId,
                Revision = 2,
                Status = "AcceptedInPlugin",
                TargetCharacterName = "Eriana Ning",
                TargetWorld = "Siren",
                Region = "North America",
            });
        }
    }
}
