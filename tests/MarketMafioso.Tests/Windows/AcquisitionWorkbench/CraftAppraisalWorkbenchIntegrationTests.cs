using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.AcquisitionWorkbench;

namespace MarketMafioso.Tests.Windows.AcquisitionWorkbench;

public sealed class CraftAppraisalWorkbenchIntegrationTests
{
    [Fact]
    public void BuildQuoteRequest_UsesSelectedWorkbenchLine()
    {
        var draft = TestDraft.WithLine(new MarketAcquisitionQuickShopLineDraft
        {
            ItemId = 5060,
            ItemName = "Darksteel Ingot",
            QuantityMode = "TargetQuantity",
            TargetQuantity = 3,
            HqPolicy = "Either",
            MaxUnitPrice = 1500,
            GilCap = 12000,
        });

        var request = CraftAppraisalWorkbenchRequestBuilder.Build(draft, draft.Lines[0]);

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
        var draft = TestDraft.WithLine(new MarketAcquisitionQuickShopLineDraft
        {
            ItemId = 5060,
            ItemName = "Darksteel Ingot",
            QuantityMode = "AllBelowThreshold",
            MaxQuantity = 7,
            HqPolicy = "NqOnly",
            MaxUnitPrice = 1500,
        });

        var request = CraftAppraisalWorkbenchRequestBuilder.Build(draft, draft.Lines[0]);

        Assert.Equal(7u, request.Quantity);
        Assert.Equal("NqOnly", request.HqPolicy);
    }

    [Fact]
    public void BuildQuoteRequest_AllowsUnsetThresholdForCraftAppraisal()
    {
        var draft = TestDraft.WithLine(new MarketAcquisitionQuickShopLineDraft
        {
            ItemId = 5060,
            ItemName = "Darksteel Ingot",
            QuantityMode = "TargetQuantity",
            TargetQuantity = 3,
            HqPolicy = "Either",
            MaxUnitPrice = 0,
            GilCap = 0,
        });

        var request = CraftAppraisalWorkbenchRequestBuilder.Build(draft, draft.Lines[0]);

        Assert.Equal(5060u, request.ItemId);
        Assert.Equal(3u, request.Quantity);
        Assert.Equal(0u, request.BuyThresholdUnitPrice);
        Assert.Equal(0u, request.GilCap);
    }

    [Fact]
    public void BuildQuoteRequest_UsesOneForUncappedAllBelowThreshold()
    {
        var draft = TestDraft.WithLine(new MarketAcquisitionQuickShopLineDraft
        {
            ItemId = 5060,
            ItemName = "Darksteel Ingot",
            QuantityMode = "AllBelowThreshold",
            MaxQuantity = 0,
            HqPolicy = "Either",
            MaxUnitPrice = 1500,
        });

        var request = CraftAppraisalWorkbenchRequestBuilder.Build(draft, draft.Lines[0]);

        Assert.Equal(1u, request.Quantity);
    }

    [Fact]
    public void ApplyMaxUnitPrice_UpdatesOnlySelectedLineAndAdvancesRevision()
    {
        var draft = TestDraft.WithLines(
            new MarketAcquisitionQuickShopLineDraft
            {
                ItemId = 2,
                ItemName = "Fire Shard",
                QuantityMode = "TargetQuantity",
                TargetQuantity = 10,
                HqPolicy = "Either",
                MaxUnitPrice = 100,
            },
            new MarketAcquisitionQuickShopLineDraft
            {
                ItemId = 5060,
                ItemName = "Darksteel Ingot",
                QuantityMode = "TargetQuantity",
                TargetQuantity = 3,
                HqPolicy = "Either",
                MaxUnitPrice = 1500,
            });

        var updated = AcquisitionWorkbenchDraftMutation.ApplyMaxUnitPrice(
            draft,
            selectedLineIndex: 1,
            maxUnitPrice: 1200);

        Assert.Equal(100u, updated.Lines[0].MaxUnitPrice);
        Assert.Equal(1200u, updated.Lines[1].MaxUnitPrice);
        Assert.Equal(draft.DraftRevision + 1, updated.DraftRevision);
    }

    [Fact]
    public void ApplyPricing_UpdatesOnlySelectedLineAndAdvancesRevision()
    {
        var draft = TestDraft.WithLines(
            new MarketAcquisitionQuickShopLineDraft
            {
                ItemId = 2,
                ItemName = "Fire Shard",
                QuantityMode = "TargetQuantity",
                TargetQuantity = 10,
                HqPolicy = "Either",
                MaxUnitPrice = 100,
                GilCap = 0,
            },
            new MarketAcquisitionQuickShopLineDraft
            {
                ItemId = 5060,
                ItemName = "Darksteel Ingot",
                QuantityMode = "TargetQuantity",
                TargetQuantity = 3,
                HqPolicy = "Either",
                MaxUnitPrice = 0,
                GilCap = 0,
            });

        var updated = AcquisitionWorkbenchDraftMutation.ApplyPricing(
            draft,
            selectedLineIndex: 1,
            maxUnitPrice: 1200,
            gilCap: 5000);

        Assert.Equal(100u, updated.Lines[0].MaxUnitPrice);
        Assert.Equal(0u, updated.Lines[0].GilCap);
        Assert.Equal(1200u, updated.Lines[1].MaxUnitPrice);
        Assert.Equal(5000u, updated.Lines[1].GilCap);
        Assert.Equal(draft.DraftRevision + 1, updated.DraftRevision);
    }

    private static class TestDraft
    {
        public static MarketAcquisitionQuickShopDraft WithLine(MarketAcquisitionQuickShopLineDraft line) =>
            WithLines(line);

        public static MarketAcquisitionQuickShopDraft WithLines(params MarketAcquisitionQuickShopLineDraft[] lines) =>
            MarketAcquisitionQuickShopDraft.CreateDefault() with
            {
                Region = "North America",
                WorldMode = "Recommended",
                SweepScope = "Region",
                Lines = lines.ToList(),
            };
    }
}
