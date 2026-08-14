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

    [Fact]
    public void PartialRetainerObservation_PreservesAcknowledgedFields()
    {
        var acknowledged = Retainer(10, 2) with
        {
            Gil = 123,
            GilObservedAtUtc = "2026-08-14T12:00:00Z",
            ListingsObservedAtUtc = "2026-08-14T12:00:00Z",
            MarketListings = [new RetainerMarketListing { ItemId = 9, Quantity = 1 }],
            Storage = new StorageSourceEvidence
            {
                RequestedSources = ["RetainerPage1"],
                ObservedSources = ["RetainerPage1"],
            },
        };
        var rosterOnly = new RetainerReport { RetainerId = 10, RetainerName = "Alpha" };

        var merged = Assert.Single(HttpReporter.PreserveMissingRetainerFields([rosterOnly], [acknowledged]));

        Assert.Equal((uint)2, merged.Bags[0].Items[0].ItemId);
        Assert.Equal((ulong)123, merged.Gil);
        Assert.Equal((uint)9, merged.MarketListings[0].ItemId);
    }

    [Fact]
    public void ManagementAvailability_IsIndependentFromRetainerAvailability()
    {
        var management = new QuartermasterStowageReport { ProviderInstanceId = "rq" };
        var acknowledged = new InventoryReport
        {
            Retainers = [Retainer(10, 2)],
            RetainerManagement = management,
        };
        var current = new InventoryReport { Retainers = [Retainer(10, 4)] };

        var merged = HttpReporter.PreserveUnavailableEvidence(
            current,
            acknowledged,
            captureHasRetainerEvidence: true,
            captureHasManagementEvidence: false);

        Assert.Equal((uint)4, merged.Retainers[0].Bags[0].Items[0].ItemId);
        Assert.Same(management, merged.RetainerManagement);
    }

    [Fact]
    public void PartialStorageObservation_ReplacesObservedBagAndPreservesOthers()
    {
        var acknowledged = new RetainerReport
        {
            RetainerId = 10,
            Bags = [Bag("RetainerPage1", 1), Bag("RetainerPage2", 2)],
        };
        var partial = new RetainerReport
        {
            RetainerId = 10,
            Bags = [Bag("RetainerPage1", 3)],
            Storage = new StorageSourceEvidence
            {
                RequestedSources = ["RetainerPage1", "RetainerPage2"],
                ObservedSources = ["RetainerPage1"],
            },
        };

        var merged = Assert.Single(HttpReporter.PreserveMissingRetainerFields([partial], [acknowledged]));

        Assert.Equal((uint)3, merged.Bags.Single(bag => bag.BagName == "RetainerPage1").Items[0].ItemId);
        Assert.Equal((uint)2, merged.Bags.Single(bag => bag.BagName == "RetainerPage2").Items[0].ItemId);
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
