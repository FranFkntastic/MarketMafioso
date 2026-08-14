using Franthropy.Dalamud.Equipment;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.MarketAcquisition.ExactAuthority;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionWorkbenchReviewedControlIdsTests
{
    [Fact]
    public void SelectLine_PreservesLegacyIdForUniqueItem()
    {
        MarketAcquisitionRequestLineDocument[] lines =
        [
            new() { ItemId = 1752, ItemName = "Mythril Ingot", HqPolicy = "Either" },
        ];

        Assert.Equal(
            "acquisition.workbench.line.1752.select",
            MarketAcquisitionWorkbenchReviewedControlIds.SelectLine(lines, 0));
    }

    [Fact]
    public void SelectLine_UsesExactQualityForLegitimateStagedItemRepeats()
    {
        var high = Offer(EquipmentQuality.High);
        var normal = Offer(EquipmentQuality.Normal);
        var transfer = ExactAcquisitionWorkbenchAuthorityTests.Transfer() with
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
            MarketAcquisitionRequestDocument.CreateDefault(),
            transfer);

        Assert.Equal(2, staged.Lines.Count(line => line.ItemId == 1752));
        var ids = staged.Lines
            .Select((_, index) => MarketAcquisitionWorkbenchReviewedControlIds.SelectLine(staged.Lines, index))
            .ToArray();
        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("acquisition.workbench.line.1752.nq.select", ids);
        Assert.Contains("acquisition.workbench.line.1752.hq.select", ids);
    }

    [Fact]
    public void SelectLine_UsesOccurrenceForInvalidSameQualityDuplicates()
    {
        MarketAcquisitionRequestLineDocument[] lines =
        [
            new() { ItemId = 1752, ItemName = "Mythril Ingot", HqPolicy = "Either" },
            new() { ItemId = 1752, ItemName = "Mythril Ingot", HqPolicy = "Either" },
        ];

        Assert.Equal(
            "acquisition.workbench.line.1752.either.1.select",
            MarketAcquisitionWorkbenchReviewedControlIds.SelectLine(lines, 0));
        Assert.Equal(
            "acquisition.workbench.line.1752.either.2.select",
            MarketAcquisitionWorkbenchReviewedControlIds.SelectLine(lines, 1));
    }

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
