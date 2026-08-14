using System.Collections.Immutable;
using Franthropy.Dalamud.Observations;
using Franthropy.Observations.V1;
using MarketMafioso.Automation.Items;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests;

public sealed class FranthropyRetainerReportSourceTests
{
    [Fact]
    public void BuildsOwnerScopedStockWithoutQuartermasterSnapshot()
    {
        var owner = new ObservationOwner(100, 74);
        var roster = Observation(
            1,
            new ObservationScope(owner, ObservationSubject.Character(owner), ObservationContainerKind.RetainerRoster),
            ObservationPayloadContracts.RetainerRoster,
            new RetainerRosterPayload([new RetainerRosterObservation(200, "Alpha", 74)]));
        var inventory = Observation(
            2,
            new ObservationScope(owner, ObservationSubject.Retainer(200, owner), ObservationContainerKind.RetainerInventory),
            ObservationPayloadContracts.RetainerInventory,
            new InventoryObservationPayload([10000], [10000], [new InventoryItemObservation(10000, 3, 5333, 12, true)]));
        var snapshot = new SharedRetainerObservationSnapshot(owner, 2, [roster, inventory]);

        var available = FranthropyRetainerReportSource.TryBuildReports(
            snapshot,
            "Character",
            "World",
            includeOwnerFields: true,
            includeItemNames: true,
            itemId => new AutomationItemMetadata(new AutomationItemIdentity(itemId, "Mythril Ore"), 999, "Ore"),
            out var reports);

        Assert.True(available);
        var retainer = Assert.Single(reports);
        Assert.Equal("Alpha", retainer.RetainerName);
        var item = Assert.Single(Assert.Single(retainer.Bags).Items);
        Assert.Equal((uint)5333, item.ItemId);
        Assert.Equal((uint)12, item.Quantity);
        Assert.Equal("Mythril Ore", item.ItemName);
    }

    [Fact]
    public void CompleteEmptyRosterIsAvailableEvidence()
    {
        var owner = new ObservationOwner(100, 74);
        var roster = Observation(
            1,
            new ObservationScope(owner, ObservationSubject.Character(owner), ObservationContainerKind.RetainerRoster),
            ObservationPayloadContracts.RetainerRoster,
            new RetainerRosterPayload([]));

        Assert.True(FranthropyRetainerReportSource.TryBuildReports(
            new SharedRetainerObservationSnapshot(owner, 1, [roster]),
            null,
            null,
            false,
            false,
            itemId => new AutomationItemMetadata(new AutomationItemIdentity(itemId, null), 999),
            out var reports));
        Assert.Empty(reports);
    }

    [Fact]
    public void WorkshopAvailabilityUsesFranthropyRetainerStock()
    {
        var availability = WorkshopMaterialAvailabilityService.BuildAvailabilityFromRetainers(
            [new WorkshopMaterialRequirement(5333, "Mythril Ore", 0, 20)],
            new Dictionary<uint, int> { [5333] = 3 },
            [new MarketMafioso.Contracts.Inventory.RetainerReport
            {
                RetainerId = 200,
                RetainerName = "Alpha",
                LastUpdated = "2026-08-14T12:00:00.0000000Z",
                Bags = [new MarketMafioso.Contracts.Inventory.InventoryBag
                {
                    Items = [new MarketMafioso.Contracts.Inventory.ItemSlot { ItemId = 5333, Quantity = 12 }],
                }],
            }]);

        var item = Assert.Single(availability);
        Assert.Equal(12, item.QuartermasterStock);
        Assert.Equal(5, item.TotalMissing);
    }

    private static TrustedObservation Observation<T>(
        long revision,
        ObservationScope scope,
        string contract,
        T payload)
    {
        var observedAt = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero).AddMinutes(revision);
        return new TrustedObservation(
            revision,
            scope,
            new ObservationCapture(
                revision,
                observedAt,
                new ObservationProvenance("Test", "instance", "1.0", "test-build"),
                ObservationEvidence.CompleteAvailable),
            ObservationPayload.Create(contract, ObservationPayloadContracts.Version, payload),
            IsStale: false,
            StaleReason: null,
            StaleObservedAtUtc: null,
            LastConfirmedAtUtc: observedAt,
            ConfirmationCount: 1);
    }
}
