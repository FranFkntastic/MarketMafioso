using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.Windows.AcquisitionWorkbench;

namespace MarketMafioso.Tests.Windows.AcquisitionWorkbench;

public sealed class CraftAppraisalWorkbenchStateTests
{
    [Fact]
    public void UpdateSelection_ItemChangeClearsQuoteEvidence()
    {
        var state = new CraftAppraisalWorkbenchState();
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
        var state = new CraftAppraisalWorkbenchState();
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
        var state = new CraftAppraisalWorkbenchState();
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
    public void CreateDiagnosticsSnapshot_IncludesQuoteAndProviderStatus()
    {
        var checkedAt = DateTimeOffset.UnixEpoch.AddHours(1);
        var state = new CraftAppraisalWorkbenchState
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
