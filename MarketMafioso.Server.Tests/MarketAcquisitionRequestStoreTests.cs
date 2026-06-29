namespace MarketMafioso.Server.Tests;

public sealed class MarketAcquisitionRequestStoreTests
{
    [Fact]
    public async Task RecordLineProgressAsyncRejectsLineFromDifferentBatch()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var first = await fixture.CreateAcceptedBatchAsync("wrong-batch-first", lineCount: 1);
        var second = await fixture.CreateAcceptedBatchAsync("wrong-batch-second", lineCount: 1);

        await Assert.ThrowsAsync<MarketAcquisitionInvalidLineException>(() =>
            fixture.Store.RecordLineProgressAsync(
                first.Id,
                second.Lines[0].LineId,
                new MarketAcquisitionLineProgressRequest
                {
                    ClaimToken = first.ClaimToken,
                    IdempotencyKey = "wrong-batch-line-key",
                    AttemptId = "attempt-1",
                    Sequence = 1,
                    Status = "Running",
                    Message = "Wrong line."
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task RecordPurchaseAuditAsyncInsertsIdempotentPurchaseRecord()
    {
        using var fixture = await MarketAcquisitionStoreFixture.CreateAsync();
        var claimed = await fixture.CreateAcceptedBatchAsync("purchase-audit-idempotent", lineCount: 1);
        var request = new MarketAcquisitionPurchaseAuditRequest
        {
            ClaimToken = claimed.ClaimToken,
            IdempotencyKey = "purchase-audit-key",
            AttemptId = "attempt-1",
            Sequence = 1,
            LineId = claimed.Lines[0].LineId,
            WorldName = "Siren",
            ItemId = claimed.Lines[0].ItemId,
            ItemName = claimed.Lines[0].ItemName,
            ListingId = "listing-1",
            RetainerName = "Seller",
            RetainerId = "retainer-1",
            Quantity = 10,
            UnitPrice = 50,
            TotalGil = 500,
            IsHq = false,
            Result = "Purchased",
            Message = "Purchase confirmed."
        };

        var first = await fixture.Store.RecordPurchaseAuditAsync(claimed.Id, request, CancellationToken.None);
        var second = await fixture.Store.RecordPurchaseAuditAsync(claimed.Id, request, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.AuditId, second.AuditId);
    }
}
