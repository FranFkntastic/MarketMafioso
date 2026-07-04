using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionQuickShopRequestBuilderTests
{
    [Fact]
    public void Build_MapsValidMultiItemDraftToBatchCreateRequest()
    {
        var draft = CreateDraft();

        var request = MarketAcquisitionQuickShopRequestBuilder.Build(
            draft,
            " Wei Ning ",
            " Gilgamesh ",
            "plugin-instance");

        Assert.Equal(1, request.SchemaVersion);
        Assert.Equal("plugin-instance:quick-shop:draft-1:3", request.IdempotencyKey);
        Assert.Equal(MarketAcquisitionOrigins.ClientQuickShop, request.Origin);
        Assert.Equal("plugin-instance", request.CreatedByPluginInstanceId);
        Assert.Equal("Wei Ning", request.TargetCharacterName);
        Assert.Equal("Gilgamesh", request.TargetWorld);
        Assert.Equal("North America", request.Region);
        Assert.Equal("Recommended", request.WorldMode);
        Assert.Equal("Region", request.SweepScope);
        Assert.Equal(300, request.ExpiresInSeconds);
        Assert.Equal(2, request.Lines.Count);
        Assert.Equal(2u, request.Lines[0].ItemId);
        Assert.Equal("Fire Shard", request.Lines[0].ItemName);
        Assert.Equal("TargetQuantity", request.Lines[0].QuantityMode);
        Assert.Equal(10u, request.Lines[0].TargetQuantity);
        Assert.Equal(0u, request.Lines[0].MaxQuantity);
        Assert.Equal("Either", request.Lines[0].HqPolicy);
        Assert.Equal(4u, request.Lines[1].ItemId);
        Assert.Equal("AllBelowThreshold", request.Lines[1].QuantityMode);
        Assert.Equal(0u, request.Lines[1].TargetQuantity);
        Assert.Equal(999u, request.Lines[1].MaxQuantity);
        Assert.Equal("HqOnly", request.Lines[1].HqPolicy);
    }

    [Fact]
    public void BuildCreateIdempotencyKey_IsStableForSameDraftRevision()
    {
        var draft = CreateDraft();

        var first = MarketAcquisitionQuickShopRequestBuilder.BuildCreateIdempotencyKey("plugin", draft);
        var second = MarketAcquisitionQuickShopRequestBuilder.BuildCreateIdempotencyKey("plugin", draft);

        Assert.Equal(first, second);
    }

    [Fact]
    public void BuildCreateIdempotencyKey_ChangesWithDraftRevision()
    {
        var draft = CreateDraft();

        var first = MarketAcquisitionQuickShopRequestBuilder.BuildCreateIdempotencyKey("plugin", draft);
        var second = MarketAcquisitionQuickShopRequestBuilder.BuildCreateIdempotencyKey("plugin", draft.WithNextRevision());

        Assert.NotEqual(first, second);
    }

    private static MarketAcquisitionQuickShopDraft CreateDraft() => new()
    {
        DraftId = "draft-1",
        DraftRevision = 3,
        Lines =
        [
            new MarketAcquisitionQuickShopLineDraft
            {
                ItemId = 2,
                ItemName = " Fire Shard ",
                QuantityMode = "TargetQuantity",
                TargetQuantity = 10,
                HqPolicy = "Either",
                MaxUnitPrice = 99,
                GilCap = 990,
            },
            new MarketAcquisitionQuickShopLineDraft
            {
                ItemId = 4,
                ItemName = "Lightning Shard",
                QuantityMode = "AllBelowThreshold",
                MaxQuantity = 999,
                HqPolicy = "HQOnly",
                MaxUnitPrice = 120,
            },
        ],
    };
}
