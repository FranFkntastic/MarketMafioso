using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Tests.Windows.MarketAcquisitionRequestBuilder;

public sealed class CraftAppraisalRequestBuilderIntegrationTests
{
    [Fact]
    public void BuildQuoteRequest_UsesSelectedBuilderLine()
    {
        var document = TestDocument.WithLine(new MarketAcquisitionRequestLineDocument
        {
            ItemId = 5060,
            ItemName = "Darksteel Ingot",
            QuantityMode = "TargetQuantity",
            TargetQuantity = 3,
            HqPolicy = "Either",
            MaxUnitPrice = 1500,
            GilCap = 12000,
        });

        var request = CraftAppraisalRequestMapper.Build(document, document.Lines[0]);

        Assert.Equal(5060u, request.ItemId);
        Assert.Equal("Darksteel Ingot", request.ItemName);
        Assert.Equal(3u, request.Quantity);
        Assert.Equal("Either", request.HqPolicy);
        Assert.Equal(1500u, request.BuyThresholdUnitPrice);
        Assert.Equal(12000u, request.GilCap);
        Assert.Equal("North America", request.Region);
        Assert.Equal("Recommended", request.WorldMode);
        Assert.Equal("Region", request.SweepScope);
    }

    [Fact]
    public void BuildQuoteRequest_UsesMaxQuantityForCappedAllBelowThreshold()
    {
        var document = TestDocument.WithLine(new MarketAcquisitionRequestLineDocument
        {
            ItemId = 5060,
            ItemName = "Darksteel Ingot",
            QuantityMode = "AllBelowThreshold",
            MaxQuantity = 7,
            HqPolicy = "NqOnly",
            MaxUnitPrice = 1500,
        });

        var request = CraftAppraisalRequestMapper.Build(document, document.Lines[0]);

        Assert.Equal(7u, request.Quantity);
        Assert.Equal("NqOnly", request.HqPolicy);
    }

    [Fact]
    public void BuildQuoteRequest_AllowsUnsetThresholdForCraftAppraisal()
    {
        var document = TestDocument.WithLine(new MarketAcquisitionRequestLineDocument
        {
            ItemId = 5060,
            ItemName = "Darksteel Ingot",
            QuantityMode = "TargetQuantity",
            TargetQuantity = 3,
            HqPolicy = "Either",
            MaxUnitPrice = 0,
            GilCap = 0,
        });

        var request = CraftAppraisalRequestMapper.Build(document, document.Lines[0]);

        Assert.Equal(5060u, request.ItemId);
        Assert.Equal(3u, request.Quantity);
        Assert.Equal(0u, request.BuyThresholdUnitPrice);
        Assert.Equal(0u, request.GilCap);
    }

    [Fact]
    public void BuildQuoteRequest_UsesOneForUncappedAllBelowThreshold()
    {
        var document = TestDocument.WithLine(new MarketAcquisitionRequestLineDocument
        {
            ItemId = 5060,
            ItemName = "Darksteel Ingot",
            QuantityMode = "AllBelowThreshold",
            MaxQuantity = 0,
            HqPolicy = "Either",
            MaxUnitPrice = 1500,
        });

        var request = CraftAppraisalRequestMapper.Build(document, document.Lines[0]);

        Assert.Equal(1u, request.Quantity);
    }

    [Fact]
    public void ApplyMaxUnitPrice_UpdatesOnlySelectedLineAndAdvancesRevision()
    {
        var document = TestDocument.WithLines(
            new MarketAcquisitionRequestLineDocument
            {
                ItemId = 2,
                ItemName = "Fire Shard",
                QuantityMode = "TargetQuantity",
                TargetQuantity = 10,
                HqPolicy = "Either",
                MaxUnitPrice = 100,
            },
            new MarketAcquisitionRequestLineDocument
            {
                ItemId = 5060,
                ItemName = "Darksteel Ingot",
                QuantityMode = "TargetQuantity",
                TargetQuantity = 3,
                HqPolicy = "Either",
                MaxUnitPrice = 1500,
            });

        var updated = RequestDocumentMutation.ApplyMaxUnitPrice(
            document,
            selectedLineIndex: 1,
            maxUnitPrice: 1200);

        Assert.Equal(100u, updated.Lines[0].MaxUnitPrice);
        Assert.Equal(1200u, updated.Lines[1].MaxUnitPrice);
        Assert.Equal(document.LocalRevision + 1, updated.LocalRevision);
    }

    [Fact]
    public void ApplyPricing_UpdatesOnlySelectedLineAndAdvancesRevision()
    {
        var document = TestDocument.WithLines(
            new MarketAcquisitionRequestLineDocument
            {
                ItemId = 2,
                ItemName = "Fire Shard",
                QuantityMode = "TargetQuantity",
                TargetQuantity = 10,
                HqPolicy = "Either",
                MaxUnitPrice = 100,
                GilCap = 0,
            },
            new MarketAcquisitionRequestLineDocument
            {
                ItemId = 5060,
                ItemName = "Darksteel Ingot",
                QuantityMode = "TargetQuantity",
                TargetQuantity = 3,
                HqPolicy = "Either",
                MaxUnitPrice = 0,
                GilCap = 0,
            });

        var updated = RequestDocumentMutation.ApplyPricing(
            document,
            selectedLineIndex: 1,
            maxUnitPrice: 1200,
            gilCap: 5000);

        Assert.Equal(100u, updated.Lines[0].MaxUnitPrice);
        Assert.Equal(0u, updated.Lines[0].GilCap);
        Assert.Equal(1200u, updated.Lines[1].MaxUnitPrice);
        Assert.Equal(5000u, updated.Lines[1].GilCap);
        Assert.Equal(document.LocalRevision + 1, updated.LocalRevision);
    }

    [Fact]
    public void ApplyLineEdit_UpdatesEditableFieldsAndAdvancesRevision()
    {
        var document = TestDocument.WithLines(
            new MarketAcquisitionRequestLineDocument
            {
                ItemId = 5121,
                ItemName = "Darksteel Ore",
                QuantityMode = "AllBelowThreshold",
                MaxQuantity = 0,
                HqPolicy = "Either",
                MaxUnitPrice = 0,
                GilCap = 0,
            });

        var updated = RequestDocumentMutation.ApplyLineEdit(
            document,
            selectedLineIndex: 0,
            quantityMode: "TargetQuantity",
            targetQuantity: 40,
            maxQuantity: 0,
            hqPolicy: "NQOnly",
            maxUnitPrice: 639,
            gilCap: 25_000);

        var line = updated.Lines[0];
        Assert.Equal("TargetQuantity", line.QuantityMode);
        Assert.Equal(40u, line.TargetQuantity);
        Assert.Equal(0u, line.MaxQuantity);
        Assert.Equal("NQOnly", line.HqPolicy);
        Assert.Equal(639u, line.MaxUnitPrice);
        Assert.Equal(25_000u, line.GilCap);
        Assert.Equal(document.LocalRevision + 1, updated.LocalRevision);
    }

    private static class TestDocument
    {
        public static MarketAcquisitionRequestDocument WithLine(MarketAcquisitionRequestLineDocument line) =>
            WithLines(line);

        public static MarketAcquisitionRequestDocument WithLines(params MarketAcquisitionRequestLineDocument[] lines) =>
            MarketAcquisitionRequestDocument.CreateDefault() with
            {
                Region = "North America",
                WorldMode = "Recommended",
                SweepScope = "Region",
                Lines = lines.ToList(),
            };
    }
}
