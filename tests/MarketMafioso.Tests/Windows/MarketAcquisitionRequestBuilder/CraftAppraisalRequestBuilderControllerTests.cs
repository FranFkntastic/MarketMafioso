using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.Tests.TestUtilities;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Tests.Windows.MarketAcquisitionRequestBuilder;

public sealed class CraftAppraisalRequestBuilderControllerTests
{
    [Fact]
    public async Task FetchQuoteAsync_RecordsQuoteAndDiagnosticPath()
    {
        using var directory = new TemporaryDirectory();
        var provider = new StubQuoteProvider(TestQuote("Darksteel Ingot", 5060, 1200m));
        var controller = new CraftAppraisalRequestBuilderController(
            provider,
            _ => Task.FromResult(true),
            directory.Path,
            () => DateTimeOffset.UnixEpoch);

        var quote = await controller.FetchQuoteAsync(TestRequest(5060, "Darksteel Ingot"));

        Assert.NotNull(quote);
        Assert.Equal(1200m, controller.State.LatestQuote?.EstimatedUnitCost);
        Assert.Contains("refreshed", controller.State.CraftQuoteStatus, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(controller.State.LastCraftQuoteDiagnosticFilePath);
        Assert.True(File.Exists(controller.State.LastCraftQuoteDiagnosticFilePath));
    }

    [Fact]
    public async Task FetchQuoteAsync_RecordsFailureStatusWithoutKeepingStaleQuote()
    {
        using var directory = new TemporaryDirectory();
        var provider = new StubQuoteProvider(TestQuote("Darksteel Ingot", 5060, 1200m));
        var controller = new CraftAppraisalRequestBuilderController(
            provider,
            _ => Task.FromResult(true),
            directory.Path,
            () => DateTimeOffset.UnixEpoch);
        await controller.FetchQuoteAsync(TestRequest(5060, "Darksteel Ingot"));
        provider.Exception = new InvalidOperationException("receiver unavailable");

        var quote = await controller.FetchQuoteAsync(TestRequest(5060, "Darksteel Ingot"));

        Assert.Null(quote);
        Assert.Null(controller.State.LatestQuote);
        Assert.Null(controller.State.LastCraftQuoteDiagnosticFilePath);
        Assert.Contains("receiver unavailable", controller.State.CraftQuoteStatus);
    }

    [Fact]
    public void TryGetQuoteThreshold_RoundsCompleteQuoteUpToWholeGil()
    {
        using var directory = new TemporaryDirectory();
        var controller = new CraftAppraisalRequestBuilderController(
            new StubQuoteProvider(TestQuote("Darksteel Ingot", 5060, 1200.49m)),
            _ => Task.FromResult(true),
            directory.Path,
            () => DateTimeOffset.UnixEpoch);
        controller.State.RecordQuote(TestQuote("Darksteel Ingot", 5060, 1200.49m), "quote.log");

        var threshold = controller.TryGetQuoteThreshold();

        Assert.Equal(1201u, threshold);
    }

    [Fact]
    public async Task EnsureWorkshopHostCapabilitiesFreshAsync_UsesTtl()
    {
        var checks = 0;
        var now = DateTimeOffset.UnixEpoch;
        using var directory = new TemporaryDirectory();
        var controller = new CraftAppraisalRequestBuilderController(
            new StubQuoteProvider(null),
            _ =>
            {
                checks++;
                return Task.FromResult(true);
            },
            directory.Path,
            () => now);
        controller.State.WorkshopHostEnabled = true;

        await controller.EnsureWorkshopHostCapabilitiesFreshAsync();
        await controller.EnsureWorkshopHostCapabilitiesFreshAsync();
        now = now.AddMinutes(6);
        await controller.EnsureWorkshopHostCapabilitiesFreshAsync();

        Assert.Equal(2, checks);
        Assert.True(controller.State.WorkshopHostAvailable);
        Assert.Contains("available", controller.State.WorkshopHostStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static MarketAppraisalRequest TestRequest(uint itemId, string itemName) => new()
    {
        ItemId = itemId,
        ItemName = itemName,
        Quantity = 1,
        HqPolicy = "Either",
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
    };

    private static CraftAppraisalQuote TestQuote(
        string itemName,
        uint itemId,
        decimal estimatedUnitCost) =>
        new()
        {
            ItemId = itemId,
            ItemName = itemName,
            RequestedQuantity = 1,
            EstimatedUnitCost = estimatedUnitCost,
            EstimatedTotalCost = estimatedUnitCost,
            Source = "WorkshopHostCraftArchitect",
            Confidence = "Medium",
            IsComplete = true,
            AppraisalStatus = "Complete",
        };

    private sealed class StubQuoteProvider(CraftAppraisalQuote? quote) : ICraftQuoteProvider
    {
        public string ProviderId => "Stub";
        public bool IsConfigured => true;
        public Exception? Exception { get; set; }

        public Task<CraftAppraisalQuote?> GetQuoteAsync(
            MarketAppraisalRequest request,
            CancellationToken cancellationToken = default)
        {
            return Exception is null
                ? Task.FromResult(quote)
                : Task.FromException<CraftAppraisalQuote?>(Exception);
        }
    }
}
