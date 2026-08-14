using MarketMafioso.MarketAcquisition;
using MarketMafioso.Server.MarketAcquisition;

namespace MarketMafioso.ContractTests.MarketAcquisition;

public sealed class MarketAcquisitionRequestPolicyQualityTests
{
    [Fact]
    public void ValidateBatchCreateRequest_AllowsQualityDistinctLinesForOneItem()
    {
        var request = Request("NQOnly", "HQOnly");

        MarketAcquisitionRequestPolicy.ValidateBatchCreateRequest(request);
    }

    [Theory]
    [InlineData("NQOnly", "NQOnly")]
    [InlineData("Either", "HQOnly")]
    public void ValidateBatchCreateRequest_RefusesOverlappingQualityDomains(
        string firstPolicy,
        string secondPolicy)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            MarketAcquisitionRequestPolicy.ValidateBatchCreateRequest(Request(firstPolicy, secondPolicy)));

        Assert.Contains("overlapping acquisition lines", error.Message, StringComparison.Ordinal);
    }

    private static MarketAcquisitionBatchCreateRequest Request(string firstPolicy, string secondPolicy) => new()
    {
        IdempotencyKey = "quality-split-contract",
        Origin = MarketAcquisitionOrigins.PluginBuilder,
        TargetCharacterName = "Wei Ning",
        TargetWorld = "Gilgamesh",
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
        Lines =
        [
            Line(firstPolicy),
            Line(secondPolicy),
        ],
    };

    private static MarketAcquisitionBatchLineCreateRequest Line(string policy) => new()
    {
        ItemId = 1752,
        ItemName = "Mythril Ingot",
        ItemKind = "Crafting material",
        QuantityMode = "TargetQuantity",
        TargetQuantity = 1,
        HqPolicy = policy,
        MaxUnitPrice = 100,
        GilCap = 100,
    };
}
