using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class CraftQuoteDiagnosticPrintoutTests
{
    [Fact]
    public void Write_CreatesTimestampedQuoteDiagnosticFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mmf-quote-diagnostics-" + Guid.NewGuid().ToString("N"));
        var request = new MarketAppraisalRequest
        {
            ItemId = 7017,
            ItemName = "Varnish",
            Quantity = 999,
            HqPolicy = "Either",
            Region = "North America",
        };
        var quote = new CraftAppraisalQuote
        {
            ItemId = 7017,
            ItemName = "Varnish",
            RequestedQuantity = 999,
            EstimatedUnitCost = 0m,
            EstimatedTotalCost = 0m,
            Source = "CraftArchitectLocal",
            Confidence = "Low",
            IsComplete = false,
            AppraisalStatus = "IncompletePriceEvidence",
            Warnings = ["4 active material(s) are missing price evidence."],
            Materials =
            [
                new CraftAppraisalMaterialQuote
                {
                    ItemName = "Flax",
                    TotalQuantity = 1998m,
                    UnitCost = 0m,
                    TotalCost = 0m,
                    CostSource = "MissingEvidence",
                    Warnings = ["Flax is missing price evidence."],
                },
            ],
        };

        var path = CraftQuoteDiagnosticPrintout.Write(
            directory,
            request,
            quote,
            DateTimeOffset.Parse("2026-07-05T15:45:00+00:00"));

        Assert.True(File.Exists(path));
        Assert.Contains("craft-quote-20260705-154500", Path.GetFileName(path), StringComparison.Ordinal);
        var text = File.ReadAllText(path);
        Assert.Contains("Craft Architect quote diagnostic", text, StringComparison.Ordinal);
        Assert.Contains("Request: Varnish (7017) x999", text, StringComparison.Ordinal);
        Assert.Contains("Quote: 0 gil / unit, total 0 gil, source CraftArchitectLocal, confidence Low", text, StringComparison.Ordinal);
        Assert.Contains("Status: IncompletePriceEvidence, complete False", text, StringComparison.Ordinal);
        Assert.Contains("Warning: 4 active material(s) are missing price evidence.", text, StringComparison.Ordinal);
        Assert.Contains("Material: Flax x1,998, 0 gil/unit, total 0 gil, source MissingEvidence; Flax is missing price evidence.", text, StringComparison.Ordinal);
    }
}
