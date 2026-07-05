using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.CraftArchitectCompanion;

public sealed class ManualCraftQuoteProvider : ICraftQuoteProvider
{
    private readonly Func<MarketAppraisalRequest, decimal?> readUnitCost;
    private readonly Func<DateTimeOffset> getUtcNow;

    public ManualCraftQuoteProvider(
        Func<MarketAppraisalRequest, decimal?> readUnitCost,
        Func<DateTimeOffset>? getUtcNow = null)
    {
        this.readUnitCost = readUnitCost;
        this.getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string ProviderId => "Manual";

    public bool IsConfigured => true;

    public Task<CraftAppraisalQuote?> GetQuoteAsync(
        MarketAppraisalRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var unitCost = readUnitCost(request);
        if (unitCost is null || unitCost <= 0)
            return Task.FromResult<CraftAppraisalQuote?>(null);

        var quote = new CraftAppraisalQuote
        {
            SchemaVersion = 1,
            ItemId = request.ItemId,
            ItemName = request.ItemName,
            RequestedQuantity = request.Quantity,
            OutputQuantity = 1,
            EstimatedUnitCost = unitCost.Value,
            EstimatedTotalCost = unitCost.Value * request.Quantity,
            Currency = "gil",
            Source = ProviderId,
            QuotedAtUtc = getUtcNow(),
            Confidence = "Manual",
            IsComplete = true,
            AppraisalStatus = "Complete",
            Warnings = ["Manual craft quote is advisory evidence. User acquisition threshold remains authoritative."],
        };

        return Task.FromResult<CraftAppraisalQuote?>(quote);
    }
}
