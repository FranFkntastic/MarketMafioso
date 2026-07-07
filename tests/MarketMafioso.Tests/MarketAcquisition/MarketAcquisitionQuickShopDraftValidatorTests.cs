using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionQuickShopDraftValidatorTests
{
    [Fact]
    public void Validate_ReturnsAllRelevantErrorsForBadDraft()
    {
        var draft = new MarketAcquisitionQuickShopDraft
        {
            Region = string.Empty,
            WorldMode = "Selected",
            Lines =
            [
                new MarketAcquisitionQuickShopLineDraft
                {
                    QuantityMode = "TargetQuantity",
                    HqPolicy = "CollectorOnly",
                },
            ],
        };

        var result = MarketAcquisitionQuickShopDraftValidator.Validate(
            draft,
            clientApiKey: "",
            characterName: "",
            world: "");

        Assert.False(result.IsValid);
        Assert.Contains("Client API key is required.", result.Errors);
        Assert.Contains("Current character name is required.", result.Errors);
        Assert.Contains("Current world is required.", result.Errors);
        Assert.Contains("Region is required.", result.Errors);
        Assert.Contains("World mode must be Recommended or AllWorldSweep.", result.Errors);
        Assert.Contains("Line 1: item id is required.", result.Errors);
        Assert.Contains("Line 1: max unit price is required before route sync.", result.Errors);
        Assert.Contains("Line 1: target quantity is required.", result.Errors);
        Assert.Contains("Line 1: HQ policy must be Either, HQOnly, or NQOnly.", result.Errors);
    }

    [Fact]
    public void Validate_RejectsDataCenterSweepWithoutDataCenters()
    {
        var draft = ValidDraft() with
        {
            WorldMode = "AllWorldSweep",
            SweepScope = "DataCenters",
            SweepDataCenters = [],
        };

        var result = MarketAcquisitionQuickShopDraftValidator.Validate(
            draft,
            "client-secret",
            "Wei Ning",
            "Gilgamesh");

        Assert.False(result.IsValid);
        Assert.Contains("At least one data center is required for a data-center sweep.", result.Errors);
    }

    [Fact]
    public void Validate_RejectsUnsetMaxUnitPriceOnlyAtRouteSyncBoundary()
    {
        var draft = ValidDraft() with
        {
            Lines =
            [
                new MarketAcquisitionQuickShopLineDraft
                {
                    ItemId = 5060,
                    ItemName = "Darksteel Ingot",
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = 3,
                    HqPolicy = "Either",
                    MaxUnitPrice = 0,
                    GilCap = 0,
                },
            ],
        };

        var result = MarketAcquisitionQuickShopDraftValidator.Validate(
            draft,
            "client-secret",
            "Wei Ning",
            "Siren");

        Assert.False(result.IsValid);
        Assert.Contains("Line 1: max unit price is required before route sync.", result.Errors);
    }

    [Fact]
    public void Validate_AcceptsValidMultiItemDraft()
    {
        var result = MarketAcquisitionQuickShopDraftValidator.Validate(
            ValidDraft(),
            "client-secret",
            "Wei Ning",
            "Gilgamesh");

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    private static MarketAcquisitionQuickShopDraft ValidDraft() => new()
    {
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
