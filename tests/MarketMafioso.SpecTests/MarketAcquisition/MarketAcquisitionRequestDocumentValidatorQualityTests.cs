using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.MarketAcquisition.ExactAuthority;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionRequestDocumentValidatorQualityTests
{
    [Fact]
    public void ValidateDraft_AllowsQualityDistinctLinesForOneItem()
    {
        var validation = MarketAcquisitionRequestDocumentValidator.ValidateDraft(Document("NQOnly", "HQOnly"));

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
    }

    [Theory]
    [InlineData("NQOnly", "NQOnly")]
    [InlineData("HQOnly", "HQOnly")]
    [InlineData("Either", "NQOnly")]
    [InlineData("Either", "HQOnly")]
    public void ValidateDraft_RefusesOverlappingQualityDomains(string firstPolicy, string secondPolicy)
    {
        var validation = MarketAcquisitionRequestDocumentValidator.ValidateDraft(Document(firstPolicy, secondPolicy));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("overlapping acquisition lines", StringComparison.Ordinal));
    }

    [Fact]
    public void StageAndFinalize_AcceptsQualitySplitPortfolioItem()
    {
        var high = Offer(EquipmentQuality.High);
        var normal = Offer(EquipmentQuality.Normal);
        var baseline = ExactAcquisitionWorkbenchAuthorityTests.Transfer();
        var transfer = baseline with
        {
            SelectedLoadout =
            [
                new(EquipmentLoadoutPosition.Head, high, 1, "hq-observation", "Market board - Siren"),
                new(EquipmentLoadoutPosition.Body, normal, 1, "nq-observation", "Market board - Siren"),
            ],
            MarketLots =
            [
                Lot(high, "hq-listing"),
                Lot(normal, "nq-listing"),
            ],
            ObservedMarketTotalGil = 200,
        };

        var staged = ExactAcquisitionWorkbenchAuthorityService.Stage(
            MarketAcquisitionRequestDocument.CreateDefault("Wei Ning", "Gilgamesh"),
            transfer);
        var validation = MarketAcquisitionRequestDocumentValidator.ValidateDraft(staged);
        var finalized = ExactAcquisitionWorkbenchAuthorityService.Finalize(staged);

        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        var contract = Assert.IsType<ExactAcquisitionExecutionContract>(finalized.ExactAcquisitionAuthority!.FinalizedContract);
        Assert.Equal(2, contract.Lines.Count);
        Assert.Contains(contract.Lines, line => line.ItemId == 1752 && line.Quality == EquipmentQuality.Normal);
        Assert.Contains(contract.Lines, line => line.ItemId == 1752 && line.Quality == EquipmentQuality.High);
    }

    private static MarketAcquisitionRequestDocument Document(string firstPolicy, string secondPolicy) =>
        MarketAcquisitionRequestDocument.CreateDefault() with
        {
            Lines =
            [
                Line(firstPolicy),
                Line(secondPolicy),
            ],
        };

    private static MarketAcquisitionRequestLineDocument Line(string policy) => new()
    {
        ItemId = 1752,
        ItemName = "Mythril Ingot",
        QuantityMode = "TargetQuantity",
        TargetQuantity = 1,
        HqPolicy = policy,
        MaxUnitPrice = 100,
        GilCap = 100,
    };

    private static EquipmentOfferKey Offer(EquipmentQuality quality) => new(
        1752,
        quality,
        EquipmentAcquisitionSourceKind.MarketBoard,
        $"market:test:1752:{quality}");

    private static ExactAcquisitionWorkbenchMarketLot Lot(
        EquipmentOfferKey offer,
        string listingId) => new(
        offer,
        "Mythril Ingot",
        1,
        1,
        "Siren",
        100,
        100,
        listingId,
        "source-r1",
        new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero),
        "Retainer",
        $"retainer-{listingId}",
        "Crafting material");
}
