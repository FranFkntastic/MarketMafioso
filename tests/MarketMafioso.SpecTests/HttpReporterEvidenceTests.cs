using MarketMafioso.Contracts.Inventory;

namespace MarketMafioso.SpecTests;

public sealed class HttpReporterEvidenceTests
{
    [Fact]
    public void UploadEvidencePolicy_DefersUnavailableCaptureAndAllowsObservedEmptyStorage()
    {
        var unavailable = new InventoryReport();
        var observedEmpty = unavailable with
        {
            PlayerStorage = new StorageSourceEvidence
            {
                RequestedSources = ["Inventory1"],
                ObservedSources = ["Inventory1"],
            },
        };

        Assert.False(HttpReporter.HasUploadEvidence(unavailable));
        Assert.True(HttpReporter.HasUploadEvidence(observedEmpty));
    }

    [Fact]
    public void PartialCapture_PreservesOnlyUnavailableAcknowledgedScope()
    {
        var acknowledged = new InventoryReport
        {
            PlayerGil = 100,
            PlayerInventory = [Bag("Inventory1", 1)],
            PlayerStorage = new StorageSourceEvidence { ObservedSources = ["Inventory1"] },
            Retainers = [Retainer(10, 2)],
        };
        var playerOnly = acknowledged with
        {
            PlayerGil = 200,
            PlayerInventory = [Bag("Inventory1", 3)],
            Retainers = [],
        };
        var retainerOnly = acknowledged with
        {
            PlayerGil = null,
            PlayerInventory = [],
            PlayerStorage = new StorageSourceEvidence(),
            Retainers = [Retainer(10, 4)],
        };

        var mergedPlayer = HttpReporter.PreserveUnavailableEvidence(
            playerOnly,
            acknowledged,
            captureHasRetainerEvidence: false);
        var mergedRetainer = HttpReporter.PreserveUnavailableEvidence(
            retainerOnly,
            acknowledged,
            captureHasRetainerEvidence: true);

        Assert.Equal(3u, mergedPlayer.PlayerInventory[0].Items[0].ItemId);
        Assert.Equal(2u, mergedPlayer.Retainers[0].Bags[0].Items[0].ItemId);
        Assert.Equal(1u, mergedRetainer.PlayerInventory[0].Items[0].ItemId);
        Assert.Equal(4u, mergedRetainer.Retainers[0].Bags[0].Items[0].ItemId);
        Assert.Equal((ulong)100, mergedRetainer.PlayerGil);
    }

    [Fact]
    public void ServiceUnavailable_IsClassifiedAsQuietRetryablePressure()
    {
        Assert.True(HttpReporter.IsTransientReceiverStatus(System.Net.HttpStatusCode.ServiceUnavailable));
        Assert.False(HttpReporter.IsTransientReceiverStatus(System.Net.HttpStatusCode.BadRequest));
        Assert.False(HttpReporter.IsTransientReceiverStatus(System.Net.HttpStatusCode.InternalServerError));
    }

    private static InventoryBag Bag(string name, uint itemId) => new()
    {
        BagName = name,
        Items = [new ItemSlot { ItemId = itemId, Quantity = 1 }],
    };

    private static RetainerReport Retainer(ulong id, uint itemId) => new()
    {
        RetainerId = id,
        Bags = [Bag("RetainerInventory", itemId)],
    };
}
