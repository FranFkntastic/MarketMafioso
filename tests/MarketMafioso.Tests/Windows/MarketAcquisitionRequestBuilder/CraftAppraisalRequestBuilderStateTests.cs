using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Tests.Windows.MarketAcquisitionRequestBuilder;

public sealed class CraftAppraisalRequestBuilderStateTests
{
    [Fact]
    public void UpdateSelection_ItemChangeClearsQuoteEvidence()
    {
        var state = new CraftAppraisalRequestBuilderState();
        state.RecordQuote(TestQuote("Darksteel Ingot", 5060, 1200m), "quote.log");

        state.UpdateSelectedLine(new CraftAppraisalLineIdentity(
            9999,
            "Cobalt Ingot",
            1,
            "Either",
            "North America"));

        Assert.Null(state.LatestQuote);
        Assert.Null(state.LastCraftQuoteDiagnosticFilePath);
    }

    [Fact]
    public void UpdateSelection_SameLineKeepsQuoteEvidence()
    {
        var state = new CraftAppraisalRequestBuilderState();
        var identity = new CraftAppraisalLineIdentity(
            5060,
            "Darksteel Ingot",
            1,
            "Either",
            "North America");
        state.UpdateSelectedLine(identity);
        state.RecordQuote(TestQuote("Darksteel Ingot", 5060, 1200m), "quote.log");

        state.UpdateSelectedLine(identity);

        Assert.NotNull(state.LatestQuote);
        Assert.Equal("quote.log", state.LastCraftQuoteDiagnosticFilePath);
    }

    [Fact]
    public void RecordThresholdChanged_DoesNotClearQuoteEvidence()
    {
        var state = new CraftAppraisalRequestBuilderState();
        var identity = new CraftAppraisalLineIdentity(
            5060,
            "Darksteel Ingot",
            1,
            "Either",
            "North America");
        state.UpdateSelectedLine(identity);
        state.RecordQuote(TestQuote("Darksteel Ingot", 5060, 1200m), "quote.log");

        state.RecordThresholdChanged(1500);

        Assert.NotNull(state.LatestQuote);
        Assert.Equal("quote.log", state.LastCraftQuoteDiagnosticFilePath);
    }

    [Fact]
    public void RecordLineQuote_KeepsQuotesForMultipleLines()
    {
        var state = new CraftAppraisalRequestBuilderState();
        var first = new CraftAppraisalLineIdentity(2, "Bronze Ingot", 10, "Either", "North America");
        var second = new CraftAppraisalLineIdentity(3, "Iron Ingot", 12, "Either", "North America");

        state.RecordLineQuote(first, TestQuote("Bronze Ingot", 2, 120m), "bronze.log");
        state.RecordLineQuote(second, TestQuote("Iron Ingot", 3, 240m), "iron.log");

        Assert.Equal(120m, state.GetLineQuote(first)?.Quote?.EstimatedUnitCost);
        Assert.Equal(240m, state.GetLineQuote(second)?.Quote?.EstimatedUnitCost);
        Assert.Equal(2, state.LineQuotes.Count);
    }

    [Fact]
    public void ClearLineQuote_RemovesOnlyOneLine()
    {
        var state = new CraftAppraisalRequestBuilderState();
        var first = new CraftAppraisalLineIdentity(2, "Bronze Ingot", 10, "Either", "North America");
        var second = new CraftAppraisalLineIdentity(3, "Iron Ingot", 12, "Either", "North America");
        state.RecordLineQuote(first, TestQuote("Bronze Ingot", 2, 120m), "bronze.log");
        state.RecordLineQuote(second, TestQuote("Iron Ingot", 3, 240m), "iron.log");

        state.ClearLineQuote(first);

        Assert.Null(state.GetLineQuote(first));
        Assert.NotNull(state.GetLineQuote(second));
    }

    [Fact]
    public void TryGetLineQuoteThreshold_ReturnsCeiledCompleteUnitCost()
    {
        var state = new CraftAppraisalRequestBuilderState();
        var line = new CraftAppraisalLineIdentity(2, "Bronze Ingot", 10, "Either", "North America");
        state.RecordLineQuote(line, TestQuote("Bronze Ingot", 2, 120.4m), "bronze.log");

        Assert.Equal(121u, state.TryGetLineQuoteThreshold(line));
    }

    [Fact]
    public void CreateDiagnosticsSnapshot_IncludesQuoteAndProviderStatus()
    {
        var checkedAt = DateTimeOffset.UnixEpoch.AddHours(1);
        var state = new CraftAppraisalRequestBuilderState
        {
            WorkshopHostEnabled = true,
            WorkshopHostAvailable = true,
            CapabilitiesCheckedAtUtc = checkedAt,
            WorkshopHostStatus = "craft.appraise available",
            CraftQuoteStatus = "Craft quote refreshed.",
            LastMarketDepthDiagnosticFilePath = "depth.log",
        };
        state.RecordQuote(TestQuote("Darksteel Ingot", 5060, 1200m, "WorkshopHostCraftArchitect (last-good)"), "quote.log");

        var snapshot = state.CreateDiagnosticsSnapshot();

        Assert.True(snapshot.WorkshopHostEnabled);
        Assert.True(snapshot.WorkshopHostAvailable);
        Assert.Equal(checkedAt, snapshot.CapabilitiesCheckedAtUtc);
        Assert.Equal("craft.appraise available", snapshot.WorkshopHostStatus);
        Assert.Equal("Craft quote refreshed.", snapshot.CraftQuoteStatus);
        Assert.Equal("quote.log", snapshot.LastCraftQuoteDiagnosticFilePath);
        Assert.Equal("depth.log", snapshot.LastMarketDepthDiagnosticFilePath);
        Assert.Equal("Darksteel Ingot", snapshot.LastQuoteItemName);
        Assert.Equal(5060u, snapshot.LastQuoteItemId);
        Assert.True(snapshot.LatestQuoteWasLastGood);
    }

    private static CraftAppraisalQuote TestQuote(
        string itemName,
        uint itemId,
        decimal estimatedUnitCost,
        string source = "WorkshopHostCraftArchitect") =>
        new()
        {
            ItemId = itemId,
            ItemName = itemName,
            RequestedQuantity = 1,
            EstimatedUnitCost = estimatedUnitCost,
            EstimatedTotalCost = estimatedUnitCost,
            Source = source,
            Confidence = "Medium",
            IsComplete = true,
            AppraisalStatus = "Complete",
        };
}
