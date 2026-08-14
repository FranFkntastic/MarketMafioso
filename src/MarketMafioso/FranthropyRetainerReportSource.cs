using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Observations;
using Franthropy.Observations.V1;
using MarketMafioso.Automation.Items;
using MarketMafioso.Contracts.Inventory;

namespace MarketMafioso;

public sealed class FranthropyRetainerReportSource(DalamudSharedObservationClient client)
{
    public bool TryGetReports(
        ObservationOwner owner,
        string? characterName,
        string? homeWorldName,
        bool includeOwnerFields,
        bool includeItemNames,
        Func<uint, AutomationItemMetadata> resolveItemMetadata,
        out List<RetainerReport> reports)
    {
        if (!client.TryGetRetainers(owner, out var snapshot))
        {
            reports = [];
            return false;
        }

        return TryBuildReports(
            snapshot!,
            characterName,
            homeWorldName,
            includeOwnerFields,
            includeItemNames,
            resolveItemMetadata,
            out reports);
    }

    public static bool TryBuildReports(
        SharedRetainerObservationSnapshot snapshot,
        string? characterName,
        string? homeWorldName,
        bool includeOwnerFields,
        bool includeItemNames,
        Func<uint, AutomationItemMetadata> resolveItemMetadata,
        out List<RetainerReport> reports)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(resolveItemMetadata);

        var rosterObservation = snapshot.Observations
            .Where(observation => observation.Scope.Container == ObservationContainerKind.RetainerRoster)
            .MaxBy(observation => observation.Revision);
        if (rosterObservation is null)
        {
            reports = [];
            return false;
        }

        var roster = rosterObservation.Payload.Deserialize<RetainerRosterPayload>(
            ObservationPayloadContracts.RetainerRoster,
            ObservationPayloadContracts.Version);
        var byRetainer = snapshot.Observations
            .Where(observation => observation.Scope.Subject.Kind == ObservationSubjectKind.Retainer)
            .GroupBy(observation => observation.Scope.Subject.Id)
            .ToDictionary(group => group.Key, group => group.ToArray());

        reports = roster.Retainers
            .Where(retainer => retainer.WorldId == snapshot.Owner.HomeWorldId)
            .OrderBy(retainer => retainer.DisplayOrder)
            .Select(retainer => BuildReport(
                retainer,
                rosterObservation,
                byRetainer.GetValueOrDefault(retainer.RetainerId) ?? [],
                characterName,
                homeWorldName,
                includeOwnerFields,
                includeItemNames,
                resolveItemMetadata))
            .ToList();
        return true;
    }

    private static RetainerReport BuildReport(
        RetainerRosterObservation retainer,
        TrustedObservation rosterObservation,
        IReadOnlyList<TrustedObservation> observations,
        string? characterName,
        string? homeWorldName,
        bool includeOwnerFields,
        bool includeItemNames,
        Func<uint, AutomationItemMetadata> resolveItemMetadata)
    {
        var inventory = Latest(observations, ObservationContainerKind.RetainerInventory);
        var gil = Latest(observations, ObservationContainerKind.RetainerGil);
        var listings = Latest(observations, ObservationContainerKind.RetainerMarketListings);
        var inventoryPayload = inventory?.Payload.Deserialize<InventoryObservationPayload>(
            ObservationPayloadContracts.RetainerInventory,
            ObservationPayloadContracts.Version);
        var gilPayload = gil?.Payload.Deserialize<RetainerGilPayload>(
            ObservationPayloadContracts.RetainerGil,
            ObservationPayloadContracts.Version);
        var listingPayload = listings?.Payload.Deserialize<RetainerMarketListingsPayload>(
            ObservationPayloadContracts.RetainerMarketListings,
            ObservationPayloadContracts.Version);
        var latest = new[] { rosterObservation, inventory, gil, listings }
            .Where(observation => observation is not null)
            .Max(observation => observation!.Capture.ObservedAtUtc);

        var bags = BuildBags(inventory, inventoryPayload, includeItemNames, resolveItemMetadata);
        return new RetainerReport
        {
            RetainerName = retainer.Name,
            RetainerId = retainer.RetainerId,
            OwnerCharacterName = includeOwnerFields ? characterName : null,
            OwnerHomeWorld = includeOwnerFields ? homeWorldName : null,
            LastUpdated = Format(latest),
            Gil = gilPayload?.Gil ?? 0,
            GilObservedAtUtc = gil is null ? null : Format(gil.Capture.ObservedAtUtc),
            ListingsObservedAtUtc = listings is null ? null : Format(listings.Capture.ObservedAtUtc),
            Storage = new StorageSourceEvidence
            {
                RequestedSources = inventoryPayload?.RequestedContainerIds.Select(ContainerName).ToList() ?? [],
                ObservedSources = inventoryPayload?.ObservedContainerIds.Select(ContainerName).ToList() ?? [],
            },
            Bags = bags,
            MarketListings = listingPayload?.Listings.Select(item =>
            {
                var metadata = resolveItemMetadata(item.ItemId);
                return new RetainerMarketListing
                {
                    ItemId = item.ItemId,
                    ItemName = includeItemNames ? metadata.Identity.Name : null,
                    ItemType = metadata.ItemType,
                    Quantity = checked((uint)item.Quantity),
                    IsHQ = item.IsHighQuality,
                    Condition = 0,
                    ContainerKey = InventoryType.RetainerMarket.ToString(),
                    SlotIndex = item.SlotIndex,
                    UnitPrice = checked((uint)item.UnitPrice),
                    ListedAt = Format(listings!.Capture.ObservedAtUtc),
                };
            }).ToList() ?? [],
        };
    }

    private static List<InventoryBag> BuildBags(
        TrustedObservation? inventory,
        InventoryObservationPayload? payload,
        bool includeItemNames,
        Func<uint, AutomationItemMetadata> resolveItemMetadata)
    {
        if (inventory is null || payload is null)
            return [];

        var observedAt = Format(inventory.Capture.ObservedAtUtc);
        var bags = payload.ObservedContainerIds
            .Select(ContainerName)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                name => name,
                name => new InventoryBag { BagName = name, Location = name, ObservedAtUtc = observedAt },
                StringComparer.Ordinal);
        foreach (var item in payload.Items)
        {
            var name = ContainerName(item.ContainerId);
            if (!bags.TryGetValue(name, out var bag))
                bags[name] = bag = new InventoryBag { BagName = name, Location = name, ObservedAtUtc = observedAt };
            var metadata = resolveItemMetadata(item.ItemId);
            bag.Items.Add(new ItemSlot
            {
                ItemId = item.ItemId,
                ItemName = includeItemNames ? metadata.Identity.Name : null,
                ItemType = metadata.ItemType,
                Quantity = checked((uint)item.Quantity),
                IsHQ = item.IsHighQuality,
                Condition = 0,
                ContainerKey = name,
                SlotIndex = item.SlotIndex,
                Equipped = false,
            });
        }

        return bags.Values.OrderBy(bag => bag.BagName, StringComparer.Ordinal).ToList();
    }

    private static TrustedObservation? Latest(
        IEnumerable<TrustedObservation> observations,
        ObservationContainerKind container) =>
        observations.Where(observation => observation.Scope.Container == container)
            .MaxBy(observation => observation.Revision);

    private static string ContainerName(int containerId) => ((InventoryType)containerId).ToString();

    private static string Format(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
