using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionQuickShopWorkflow
{
    private readonly IMarketAcquisitionRequestClient client;

    public MarketAcquisitionQuickShopWorkflow(IMarketAcquisitionRequestClient client)
    {
        this.client = client;
    }

    public async Task<MarketAcquisitionQuickShopWorkflowResult> CreateClaimAndAcceptAsync(
        MarketAcquisitionQuickShopWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        var validation = MarketAcquisitionQuickShopDraftValidator.Validate(
            request.Draft,
            request.ClientApiKey,
            request.CharacterName,
            request.World);
        if (!validation.IsValid)
            throw new InvalidOperationException(string.Join(" ", validation.Errors));

        var createRequest = MarketAcquisitionQuickShopRequestBuilder.Build(
            request.Draft,
            request.CharacterName,
            request.World,
            request.PluginInstanceId);
        var created = await client.CreateBatchAsync(
            request.ServerUrl,
            request.ClientApiKey,
            createRequest,
            cancellationToken).ConfigureAwait(false);
        var claimed = await client.ClaimAsync(
            request.ServerUrl,
            request.ClientApiKey,
            created.Id,
            request.CharacterName,
            request.World,
            request.PluginInstanceId,
            cancellationToken).ConfigureAwait(false);
        var acceptKey = MarketAcquisitionQuickShopRequestBuilder.BuildAcceptIdempotencyKey(
            request.PluginInstanceId,
            request.Draft);
        var accepted = await client.AcceptAsync(
            request.ServerUrl,
            request.ClientApiKey,
            claimed.Id,
            claimed.ClaimToken,
            acceptKey,
            cancellationToken).ConfigureAwait(false);

        return new MarketAcquisitionQuickShopWorkflowResult(created, claimed, accepted, acceptKey);
    }
}

public sealed record MarketAcquisitionQuickShopWorkflowRequest(
    string ServerUrl,
    string ClientApiKey,
    string CharacterName,
    string World,
    string PluginInstanceId,
    MarketAcquisitionQuickShopDraft Draft);

public sealed record MarketAcquisitionQuickShopWorkflowResult(
    MarketAcquisitionRequestView Created,
    MarketAcquisitionClaimView Claimed,
    MarketAcquisitionRequestView Accepted,
    string AcceptIdempotencyKey);
