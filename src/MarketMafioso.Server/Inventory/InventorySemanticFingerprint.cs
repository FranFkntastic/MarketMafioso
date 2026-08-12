using System.Security.Cryptography;
using System.Text.Json;

namespace MarketMafioso.Server.Inventory;

internal static class InventorySemanticFingerprint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Compute(InventoryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var semantic = new
        {
            SchemaVersion = report.Metadata.SchemaVersion,
            CharacterName = Normalize(report.CharacterName),
            HomeWorld = Normalize(report.HomeWorld),
            report.ServiceAccountNumber,
            report.PlayerGil,
            PlayerStorage = Storage(report.PlayerStorage),
            PlayerInventory = report.PlayerInventory
                .OrderBy(bag => bag.BagName, StringComparer.Ordinal)
                .ThenBy(bag => bag.Location, StringComparer.Ordinal)
                .Select(Bag)
                .ToArray(),
            Retainers = report.Retainers
                .Select(retainer => new
                {
                    retainer.RetainerId,
                    RetainerName = Normalize(retainer.RetainerName),
                    OwnerCharacterName = Normalize(retainer.OwnerCharacterName),
                    OwnerHomeWorld = Normalize(retainer.OwnerHomeWorld),
                    retainer.Gil,
                    Storage = Storage(retainer.Storage),
                    Bags = retainer.Bags
                        .OrderBy(bag => bag.BagName, StringComparer.Ordinal)
                        .ThenBy(bag => bag.Location, StringComparer.Ordinal)
                        .Select(Bag)
                        .ToArray(),
                    Listings = retainer.MarketListings
                        .Select(Listing)
                        .OrderBy(listing => listing.ContainerKey, StringComparer.Ordinal)
                        .ThenBy(listing => listing.SlotIndex)
                        .ThenBy(listing => listing.ItemId)
                        .ThenBy(listing => listing.IsHq)
                        .ToArray(),
                })
                .OrderBy(retainer => retainer.RetainerId)
                .ThenBy(retainer => retainer.RetainerName, StringComparer.Ordinal)
                .ToArray(),
        };

        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(semantic, JsonOptions)));
    }

    private static object Bag(InventoryBag bag) => new
    {
        BagName = Normalize(bag.BagName),
        Location = Normalize(bag.Location),
        Items = bag.Items
            .Select(Item)
            .OrderBy(item => item.ContainerKey, StringComparer.Ordinal)
            .ThenBy(item => item.SlotIndex)
            .ThenBy(item => item.ItemId)
            .ThenBy(item => item.IsHq)
            .ToArray(),
    };

    private static SemanticItem Item(ItemSlot item) => new(
        item.ItemId,
        Normalize(item.ItemName),
        Normalize(item.ItemType),
        item.Quantity,
        item.IsHQ,
        Normalize(item.ContainerKey),
        item.SlotIndex,
        item.Equipped);

    private static SemanticListing Listing(RetainerMarketListing listing) => new(
        listing.ItemId,
        Normalize(listing.ItemName),
        Normalize(listing.ItemType),
        listing.Quantity,
        listing.IsHQ,
        Normalize(listing.ContainerKey),
        listing.SlotIndex,
        listing.UnitPrice,
        Normalize(listing.ListedAt));

    private static object Storage(StorageSourceEvidence storage) => new
    {
        RequestedSources = storage.RequestedSources
            .Select(Normalize)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray(),
        ObservedSources = storage.ObservedSources
            .Select(Normalize)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray(),
    };

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private sealed record SemanticItem(
        uint ItemId,
        string ItemName,
        string ItemType,
        uint Quantity,
        bool IsHq,
        string ContainerKey,
        int? SlotIndex,
        bool? Equipped);

    private sealed record SemanticListing(
        uint ItemId,
        string ItemName,
        string ItemType,
        uint Quantity,
        bool IsHq,
        string ContainerKey,
        int? SlotIndex,
        uint? UnitPrice,
        string ListedAt);
}
