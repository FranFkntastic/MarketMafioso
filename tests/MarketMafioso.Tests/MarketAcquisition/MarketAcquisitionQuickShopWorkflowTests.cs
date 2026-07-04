using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionQuickShopWorkflowTests
{
    [Fact]
    public async Task CreateClaimAndAcceptAsync_CreatesClaimsAndAcceptsInOrder()
    {
        var client = new FakeClient();
        var workflow = new MarketAcquisitionQuickShopWorkflow(client);
        var draft = CreateDraft();

        var result = await workflow.CreateClaimAndAcceptAsync(
            new MarketAcquisitionQuickShopWorkflowRequest(
                "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
                "client-secret",
                "Wei Ning",
                "Gilgamesh",
                "plugin-instance",
                draft),
            CancellationToken.None);

        Assert.Equal(["create", "claim", "accept"], client.Calls);
        Assert.NotNull(client.LastCreateRequest);
        Assert.Equal("plugin-instance:quick-shop:draft-1:2", client.LastCreateRequest.IdempotencyKey);
        Assert.Equal("request-1", client.LastClaimRequestId);
        Assert.Equal("claim-token", client.LastAcceptClaimToken);
        Assert.Equal("plugin-instance:quick-shop:draft-1:2:accept", result.AcceptIdempotencyKey);
        Assert.Equal("AcceptedInPlugin", result.Accepted.Status);
    }

    [Fact]
    public async Task CreateClaimAndAcceptAsync_ValidationFailureAvoidsNetworkCalls()
    {
        var client = new FakeClient();
        var workflow = new MarketAcquisitionQuickShopWorkflow(client);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.CreateClaimAndAcceptAsync(
                new MarketAcquisitionQuickShopWorkflowRequest(
                    "server",
                    "",
                    "",
                    "",
                    "plugin-instance",
                    new MarketAcquisitionQuickShopDraft()),
                CancellationToken.None));

        Assert.Contains("Client API key is required.", ex.Message, StringComparison.Ordinal);
        Assert.Empty(client.Calls);
    }

    private static MarketAcquisitionQuickShopDraft CreateDraft() => new()
    {
        DraftId = "draft-1",
        DraftRevision = 2,
        Lines =
        [
            new MarketAcquisitionQuickShopLineDraft
            {
                ItemId = 2,
                ItemName = "Fire Shard",
                QuantityMode = "TargetQuantity",
                TargetQuantity = 10,
                HqPolicy = "Either",
                MaxUnitPrice = 99,
            },
        ],
    };

    private sealed class FakeClient : IMarketAcquisitionRequestClient
    {
        public List<string> Calls { get; } = new();
        public MarketAcquisitionBatchCreateRequest? LastCreateRequest { get; private set; }
        public string? LastClaimRequestId { get; private set; }
        public string? LastAcceptClaimToken { get; private set; }

        public Task<MarketAcquisitionRequestView> CreateBatchAsync(
            string serverUrl,
            string clientApiKey,
            MarketAcquisitionBatchCreateRequest createRequest,
            CancellationToken cancellationToken)
        {
            Calls.Add("create");
            LastCreateRequest = createRequest;
            return Task.FromResult(new MarketAcquisitionRequestView
            {
                Id = "request-1",
                Status = "PendingPickup",
                TargetCharacterName = createRequest.TargetCharacterName,
                TargetWorld = createRequest.TargetWorld,
                Region = createRequest.Region,
                Origin = createRequest.Origin,
                CreatedByPluginInstanceId = createRequest.CreatedByPluginInstanceId,
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
            LastClaimRequestId = requestId;
            return Task.FromResult(new MarketAcquisitionClaimView
            {
                Id = requestId,
                Status = "Claimed",
                TargetCharacterName = characterName,
                TargetWorld = world,
                Region = "North America",
                ClaimToken = "claim-token",
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
            LastAcceptClaimToken = claimToken;
            return Task.FromResult(new MarketAcquisitionRequestView
            {
                Id = requestId,
                Status = "AcceptedInPlugin",
            });
        }
    }
}
