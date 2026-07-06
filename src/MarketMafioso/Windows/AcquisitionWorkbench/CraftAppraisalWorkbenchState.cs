using System;
using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Windows.AcquisitionWorkbench;

public sealed record CraftAppraisalLineIdentity(
    uint ItemId,
    string ItemName,
    uint Quantity,
    string HqPolicy,
    string Region);

public sealed class CraftAppraisalWorkbenchState
{
    public CraftAppraisalLineIdentity? SelectedLine { get; private set; }
    public CraftAppraisalQuote? LatestQuote { get; private set; }
    public string? LastCraftQuoteDiagnosticFilePath { get; private set; }
    public string? LastMarketDepthDiagnosticFilePath { get; set; }
    public uint? LastThresholdUnitPrice { get; private set; }
    public bool WorkshopHostEnabled { get; set; }
    public bool WorkshopHostAvailable { get; set; }
    public DateTimeOffset? CapabilitiesCheckedAtUtc { get; set; }
    public string WorkshopHostStatus { get; set; } = "Workshop Host quote API not checked.";
    public string CraftQuoteStatus { get; set; } = "No craft quote yet.";

    public void UpdateSelectedLine(CraftAppraisalLineIdentity? selectedLine)
    {
        if (Equals(SelectedLine, selectedLine))
            return;

        SelectedLine = selectedLine;
        ClearQuoteEvidence();
    }

    public void RecordQuote(CraftAppraisalQuote? quote, string? diagnosticFilePath)
    {
        LatestQuote = quote;
        LastCraftQuoteDiagnosticFilePath = diagnosticFilePath;
    }

    public void RecordThresholdChanged(uint thresholdUnitPrice)
    {
        LastThresholdUnitPrice = thresholdUnitPrice;
    }

    public void ClearQuoteEvidence()
    {
        LatestQuote = null;
        LastCraftQuoteDiagnosticFilePath = null;
        CraftQuoteStatus = "No craft quote yet.";
    }

    public CraftAppraisalDiagnosticsSnapshot CreateDiagnosticsSnapshot()
    {
        return new CraftAppraisalDiagnosticsSnapshot
        {
            WorkshopHostEnabled = WorkshopHostEnabled,
            WorkshopHostAvailable = WorkshopHostAvailable,
            CapabilitiesCheckedAtUtc = CapabilitiesCheckedAtUtc,
            WorkshopHostStatus = WorkshopHostStatus,
            CraftQuoteStatus = CraftQuoteStatus,
            LastCraftQuoteDiagnosticFilePath = LastCraftQuoteDiagnosticFilePath,
            LastMarketDepthDiagnosticFilePath = LastMarketDepthDiagnosticFilePath,
            LastQuoteItemName = LatestQuote?.ItemName,
            LastQuoteItemId = LatestQuote?.ItemId ?? 0,
            LatestQuoteWasLastGood = LatestQuote?.Source.Contains(
                "last-good",
                StringComparison.OrdinalIgnoreCase) == true,
        };
    }
}
