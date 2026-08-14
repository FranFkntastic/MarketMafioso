using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketMafioso.Contracts.Inventory;

public sealed record InventoryReport
{
    [JsonPropertyName("metadata")]
    public InventoryReportMetadata Metadata { get; init; } = new();

    [JsonPropertyName("characterName")]
    public string? CharacterName { get; init; }

    [JsonPropertyName("homeWorld")]
    public string? HomeWorld { get; init; }

    [JsonPropertyName("serviceAccountKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceAccountKey { get; init; }

    [JsonPropertyName("serviceAccountNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ServiceAccountNumber { get; init; }

    [JsonPropertyName("playerGil")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? PlayerGil { get; init; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("O");

    [JsonPropertyName("playerInventory")]
    public List<InventoryBag> PlayerInventory { get; init; } = [];

    [JsonPropertyName("retainers")]
    public List<RetainerReport> Retainers { get; init; } = [];

    [JsonPropertyName("playerStorage")]
    public StorageSourceEvidence PlayerStorage { get; init; } = new();

    [JsonPropertyName("retainerManagement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QuartermasterStowageReport? RetainerManagement { get; init; }
}

public sealed record StorageSourceEvidence
{
    [JsonPropertyName("requestedSources")]
    public List<string> RequestedSources { get; init; } = [];

    [JsonPropertyName("observedSources")]
    public List<string> ObservedSources { get; init; } = [];
}

public static class InventoryReportEvidence
{
    public static bool HasSnapshotEvidence(InventoryReport report) =>
        HasPlayerStorageEvidence(report) || report.Retainers?.Count > 0;

    public static bool HasPlayerStorageEvidence(InventoryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.PlayerInventory?.Count > 0 ||
               report.PlayerStorage?.ObservedSources?.Count > 0;
    }
}

public sealed record InventoryReportMetadata
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("sourcePlugin")]
    public string SourcePlugin { get; init; } = string.Empty;

    [JsonPropertyName("pluginVersion")]
    public string PluginVersion { get; init; } = string.Empty;

    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; init; } = string.Empty;
}

public sealed record InventoryBag
{
    [JsonPropertyName("bagName")]
    public string BagName { get; init; } = string.Empty;

    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Location { get; init; }

    [JsonPropertyName("observedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservedAtUtc { get; init; }

    [JsonPropertyName("items")]
    public List<ItemSlot> Items { get; init; } = [];
}

public sealed record RetainerReport
{
    [JsonPropertyName("retainerName")]
    public string RetainerName { get; init; } = string.Empty;

    [JsonPropertyName("retainerId")]
    public ulong RetainerId { get; init; }

    [JsonPropertyName("ownerCharacterName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerCharacterName { get; init; }

    [JsonPropertyName("ownerHomeWorld")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerHomeWorld { get; init; }

    [JsonPropertyName("lastUpdated")]
    public string LastUpdated { get; init; } = string.Empty;

    [JsonPropertyName("gil")]
    public ulong Gil { get; init; }

    [JsonPropertyName("gilObservedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GilObservedAtUtc { get; init; }

    [JsonPropertyName("listingsObservedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ListingsObservedAtUtc { get; init; }

    [JsonPropertyName("bags")]
    public List<InventoryBag> Bags { get; init; } = [];

    [JsonPropertyName("marketListings")]
    public List<RetainerMarketListing> MarketListings { get; init; } = [];

    [JsonPropertyName("storage")]
    public StorageSourceEvidence Storage { get; init; } = new();
}

public sealed record ItemSlot
{
    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ItemName { get; init; }

    [JsonPropertyName("itemType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ItemType { get; init; }

    [JsonPropertyName("quantity")]
    public uint Quantity { get; init; }

    [JsonPropertyName("isHQ")]
    public bool IsHQ { get; init; }

    [JsonPropertyName("condition")]
    public float Condition { get; init; }

    [JsonPropertyName("containerKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContainerKey { get; init; }

    [JsonPropertyName("slotIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SlotIndex { get; init; }

    [JsonPropertyName("conditionPercent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? ConditionPercent { get; init; }

    [JsonPropertyName("equipped")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Equipped { get; init; }
}

public sealed record RetainerMarketListing
{
    [JsonPropertyName("itemId")]
    public uint ItemId { get; init; }

    [JsonPropertyName("itemName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ItemName { get; init; }

    [JsonPropertyName("itemType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ItemType { get; init; }

    [JsonPropertyName("quantity")]
    public uint Quantity { get; init; }

    [JsonPropertyName("isHQ")]
    public bool IsHQ { get; init; }

    [JsonPropertyName("condition")]
    public float Condition { get; init; }

    [JsonPropertyName("containerKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContainerKey { get; init; }

    [JsonPropertyName("slotIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SlotIndex { get; init; }

    [JsonPropertyName("conditionPercent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? ConditionPercent { get; init; }

    [JsonPropertyName("unitPrice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? UnitPrice { get; init; }

    [JsonPropertyName("listedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ListedAt { get; init; }
}

public sealed record InventoryBagKey
{
    [JsonPropertyName("bagName")]
    public string BagName { get; init; } = string.Empty;

    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Location { get; init; }

    public static InventoryBagKey From(InventoryBag bag) =>
        new() { BagName = bag.BagName, Location = bag.Location };
}

public sealed record RetainerDeltaHeader
{
    [JsonPropertyName("retainerName")]
    public string RetainerName { get; init; } = string.Empty;

    [JsonPropertyName("ownerCharacterName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerCharacterName { get; init; }

    [JsonPropertyName("ownerHomeWorld")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnerHomeWorld { get; init; }

    [JsonPropertyName("lastUpdated")]
    public string LastUpdated { get; init; } = string.Empty;

    [JsonPropertyName("gil")]
    public ulong Gil { get; init; }

    [JsonPropertyName("gilObservedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GilObservedAtUtc { get; init; }

    [JsonPropertyName("listingsObservedAtUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ListingsObservedAtUtc { get; init; }

    [JsonPropertyName("storage")]
    public StorageSourceEvidence Storage { get; init; } = new();

    public static RetainerDeltaHeader From(RetainerReport report) => new()
    {
        RetainerName = report.RetainerName,
        OwnerCharacterName = report.OwnerCharacterName,
        OwnerHomeWorld = report.OwnerHomeWorld,
        LastUpdated = report.LastUpdated,
        Gil = report.Gil,
        GilObservedAtUtc = report.GilObservedAtUtc,
        ListingsObservedAtUtc = report.ListingsObservedAtUtc,
        Storage = report.Storage,
    };
}

public sealed record RetainerInventoryDelta
{
    [JsonPropertyName("retainerId")]
    public ulong RetainerId { get; init; }

    [JsonPropertyName("replacement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RetainerReport? Replacement { get; init; }

    [JsonPropertyName("header")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RetainerDeltaHeader? Header { get; init; }

    [JsonPropertyName("upsertedBags")]
    public List<InventoryBag> UpsertedBags { get; init; } = [];

    [JsonPropertyName("removedBags")]
    public List<InventoryBagKey> RemovedBags { get; init; } = [];

    [JsonPropertyName("replaceMarketListings")]
    public bool ReplaceMarketListings { get; init; }

    [JsonPropertyName("marketListings")]
    public List<RetainerMarketListing> MarketListings { get; init; } = [];
}

public sealed record InventoryReportDelta
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("baseSnapshotId")]
    public string BaseSnapshotId { get; init; } = string.Empty;

    [JsonPropertyName("metadata")]
    public InventoryReportMetadata Metadata { get; init; } = new();

    [JsonPropertyName("characterName")]
    public string? CharacterName { get; init; }

    [JsonPropertyName("homeWorld")]
    public string? HomeWorld { get; init; }

    [JsonPropertyName("serviceAccountKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceAccountKey { get; init; }

    [JsonPropertyName("serviceAccountNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ServiceAccountNumber { get; init; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = string.Empty;

    [JsonPropertyName("replacePlayerGil")]
    public bool ReplacePlayerGil { get; init; }

    [JsonPropertyName("playerGil")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? PlayerGil { get; init; }

    [JsonPropertyName("upsertedPlayerBags")]
    public List<InventoryBag> UpsertedPlayerBags { get; init; } = [];

    [JsonPropertyName("removedPlayerBags")]
    public List<InventoryBagKey> RemovedPlayerBags { get; init; } = [];

    [JsonPropertyName("replacePlayerStorage")]
    public bool ReplacePlayerStorage { get; init; }

    [JsonPropertyName("playerStorage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StorageSourceEvidence? PlayerStorage { get; init; }

    [JsonPropertyName("retainerChanges")]
    public List<RetainerInventoryDelta> RetainerChanges { get; init; } = [];

    [JsonPropertyName("removedRetainerIds")]
    public List<ulong> RemovedRetainerIds { get; init; } = [];

    [JsonPropertyName("replaceRetainerManagement")]
    public bool ReplaceRetainerManagement { get; init; }

    [JsonPropertyName("retainerManagement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QuartermasterStowageReport? RetainerManagement { get; init; }
}

public enum InventoryDeltaBuildDisposition
{
    Delta,
    Unchanged,
    FullSnapshotRequired,
}

public sealed record InventoryDeltaBuildResult(
    InventoryDeltaBuildDisposition Disposition,
    InventoryReportDelta? Delta,
    string? Reason = null);

public static class InventoryReportDeltaBuilder
{
    private static readonly JsonSerializerOptions ComparisonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static InventoryDeltaBuildResult Build(
        string? baseSnapshotId,
        InventoryReport? before,
        InventoryReport after)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (before is null || string.IsNullOrWhiteSpace(baseSnapshotId))
            return Full("No acknowledged base snapshot is available.");
        if (before.Metadata.SchemaVersion != after.Metadata.SchemaVersion)
            return Full("The inventory report schema changed.");
        if (!SameIdentity(before, after))
            return Full("The report owner identity changed.");
        if (!TryIndexBags(before.PlayerInventory, out var beforePlayerBags) ||
            !TryIndexBags(after.PlayerInventory, out var afterPlayerBags))
            return Full("Player bag identity is ambiguous.");
        if (!TryIndexRetainers(before.Retainers, out var beforeRetainers) ||
            !TryIndexRetainers(after.Retainers, out var afterRetainers))
            return Full("Retainer identity is ambiguous.");

        var upsertedPlayerBags = afterPlayerBags
            .Where(entry => !beforePlayerBags.TryGetValue(entry.Key, out var oldBag) || !EquivalentBagContents(oldBag, entry.Value))
            .Select(entry => entry.Value)
            .ToList();
        var removedPlayerBags = beforePlayerBags.Keys
            .Where(key => !afterPlayerBags.ContainsKey(key))
            .ToList();

        var retainerChanges = new List<RetainerInventoryDelta>();
        foreach (var afterRetainer in after.Retainers)
        {
            if (!beforeRetainers.TryGetValue(afterRetainer.RetainerId, out var beforeRetainer))
            {
                retainerChanges.Add(new RetainerInventoryDelta
                {
                    RetainerId = afterRetainer.RetainerId,
                    Replacement = afterRetainer,
                });
                continue;
            }

            if (!TryBuildRetainerDelta(beforeRetainer, afterRetainer, out var change, out var reason))
                return Full(reason);
            if (change is not null)
                retainerChanges.Add(change);
        }

        var removedRetainerIds = beforeRetainers.Keys
            .Where(id => !afterRetainers.ContainsKey(id))
            .ToList();
        var replacePlayerGil = before.PlayerGil != after.PlayerGil;
        var replacePlayerStorage = !Equivalent(before.PlayerStorage, after.PlayerStorage);
        var replaceRetainerManagement = !EquivalentRetainerManagement(before.RetainerManagement, after.RetainerManagement);

        if (!replacePlayerGil &&
            upsertedPlayerBags.Count == 0 &&
            removedPlayerBags.Count == 0 &&
            !replacePlayerStorage &&
            retainerChanges.Count == 0 &&
            removedRetainerIds.Count == 0 &&
            !replaceRetainerManagement)
        {
            return new(InventoryDeltaBuildDisposition.Unchanged, null);
        }

        return new(
            InventoryDeltaBuildDisposition.Delta,
            new InventoryReportDelta
            {
                BaseSnapshotId = baseSnapshotId,
                Metadata = after.Metadata,
                CharacterName = after.CharacterName,
                HomeWorld = after.HomeWorld,
                ServiceAccountKey = after.ServiceAccountKey,
                ServiceAccountNumber = after.ServiceAccountNumber,
                Timestamp = after.Timestamp,
                ReplacePlayerGil = replacePlayerGil,
                PlayerGil = replacePlayerGil ? after.PlayerGil : null,
                UpsertedPlayerBags = upsertedPlayerBags,
                RemovedPlayerBags = removedPlayerBags,
                ReplacePlayerStorage = replacePlayerStorage,
                PlayerStorage = replacePlayerStorage ? after.PlayerStorage : null,
                RetainerChanges = retainerChanges,
                RemovedRetainerIds = removedRetainerIds,
                ReplaceRetainerManagement = replaceRetainerManagement,
                RetainerManagement = replaceRetainerManagement ? after.RetainerManagement : null,
            });
    }

    private static bool TryBuildRetainerDelta(
        RetainerReport before,
        RetainerReport after,
        out RetainerInventoryDelta? delta,
        out string? reason)
    {
        delta = null;
        reason = null;
        if (!TryIndexBags(before.Bags, out var beforeBags) ||
            !TryIndexBags(after.Bags, out var afterBags))
        {
            reason = $"Retainer {after.RetainerId} has ambiguous bag identity.";
            return false;
        }

        var upsertedBags = afterBags
            .Where(entry => !beforeBags.TryGetValue(entry.Key, out var oldBag) || !EquivalentBagContents(oldBag, entry.Value))
            .Select(entry => entry.Value)
            .ToList();
        var removedBags = beforeBags.Keys.Where(key => !afterBags.ContainsKey(key)).ToList();
        var header = RetainerDeltaHeader.From(after);
        var headerChanged =
            !string.Equals(before.RetainerName, after.RetainerName, StringComparison.Ordinal) ||
            !string.Equals(before.OwnerCharacterName, after.OwnerCharacterName, StringComparison.Ordinal) ||
            !string.Equals(before.OwnerHomeWorld, after.OwnerHomeWorld, StringComparison.Ordinal) ||
            before.Gil != after.Gil ||
            !Equivalent(before.Storage, after.Storage);
        var listingsChanged = !Equivalent(before.MarketListings, after.MarketListings);

        if (!headerChanged && upsertedBags.Count == 0 && removedBags.Count == 0 && !listingsChanged)
            return true;

        delta = new RetainerInventoryDelta
        {
            RetainerId = after.RetainerId,
            Header = header,
            UpsertedBags = upsertedBags,
            RemovedBags = removedBags,
            ReplaceMarketListings = listingsChanged,
            MarketListings = listingsChanged ? after.MarketListings : [],
        };
        return true;
    }

    private static bool SameIdentity(InventoryReport before, InventoryReport after) =>
        string.Equals(before.CharacterName, after.CharacterName, StringComparison.Ordinal) &&
        string.Equals(before.HomeWorld, after.HomeWorld, StringComparison.Ordinal) &&
        string.Equals(before.ServiceAccountKey, after.ServiceAccountKey, StringComparison.Ordinal) &&
        before.ServiceAccountNumber == after.ServiceAccountNumber;

    private static bool TryIndexBags(
        IReadOnlyList<InventoryBag> bags,
        out Dictionary<InventoryBagKey, InventoryBag> indexed)
    {
        indexed = [];
        foreach (var bag in bags)
        {
            if (string.IsNullOrWhiteSpace(bag.BagName) || !indexed.TryAdd(InventoryBagKey.From(bag), bag))
                return false;
        }

        return true;
    }

    private static bool TryIndexRetainers(
        IReadOnlyList<RetainerReport> retainers,
        out Dictionary<ulong, RetainerReport> indexed)
    {
        indexed = [];
        foreach (var retainer in retainers)
        {
            if (retainer.RetainerId == 0 || !indexed.TryAdd(retainer.RetainerId, retainer))
                return false;
        }

        return true;
    }

    private static bool Equivalent<T>(T left, T right) =>
        JsonSerializer.Serialize(left, ComparisonOptions) == JsonSerializer.Serialize(right, ComparisonOptions);

    private static bool EquivalentRetainerManagement(
        QuartermasterStowageReport? left,
        QuartermasterStowageReport? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        return Equivalent(
            left with { ProviderInstanceId = string.Empty, Revision = 0 },
            right with { ProviderInstanceId = string.Empty, Revision = 0 });
    }

    private static bool EquivalentBagContents(InventoryBag left, InventoryBag right) =>
        string.Equals(left.BagName, right.BagName, StringComparison.Ordinal) &&
        string.Equals(left.Location, right.Location, StringComparison.Ordinal) &&
        Equivalent(left.Items, right.Items);

    private static InventoryDeltaBuildResult Full(string? reason) =>
        new(InventoryDeltaBuildDisposition.FullSnapshotRequired, null, reason);
}

public sealed class InventoryDeltaConflictException(string message) : InvalidOperationException(message);

public static class InventoryReportDeltaApplier
{
    public static InventoryReport Apply(
        string expectedBaseSnapshotId,
        InventoryReport before,
        InventoryReportDelta delta)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.SchemaVersion != 1)
            throw new InventoryDeltaConflictException($"Unsupported inventory delta schema {delta.SchemaVersion}.");
        if (!string.Equals(expectedBaseSnapshotId, delta.BaseSnapshotId, StringComparison.Ordinal))
            throw new InventoryDeltaConflictException("The inventory delta does not target the loaded base snapshot.");
        if (!SameIdentity(before, delta))
            throw new InventoryDeltaConflictException("The inventory delta owner does not match its base snapshot.");
        if (before.Metadata.SchemaVersion != delta.Metadata.SchemaVersion)
            throw new InventoryDeltaConflictException("The inventory delta report schema does not match its base snapshot.");

        var playerBags = ApplyBags(before.PlayerInventory, delta.UpsertedPlayerBags, delta.RemovedPlayerBags);
        var retainers = IndexRetainers(before.Retainers);
        foreach (var removedId in delta.RemovedRetainerIds)
        {
            if (!retainers.Remove(removedId))
                throw new InventoryDeltaConflictException($"Retainer {removedId} cannot be removed because it is absent from the base snapshot.");
        }

        foreach (var change in delta.RetainerChanges)
        {
            if (change.Replacement is not null)
            {
                if (change.Replacement.RetainerId != change.RetainerId || !retainers.TryAdd(change.RetainerId, change.Replacement))
                    throw new InventoryDeltaConflictException($"Retainer replacement {change.RetainerId} conflicts with the base snapshot.");
                continue;
            }

            if (!retainers.TryGetValue(change.RetainerId, out var existing) || change.Header is null)
                throw new InventoryDeltaConflictException($"Retainer patch {change.RetainerId} has no matching base retainer or header.");
            var header = change.Header;
            retainers[change.RetainerId] = existing with
            {
                RetainerName = header.RetainerName,
                OwnerCharacterName = header.OwnerCharacterName,
                OwnerHomeWorld = header.OwnerHomeWorld,
                LastUpdated = header.LastUpdated,
                Gil = header.Gil,
                GilObservedAtUtc = header.GilObservedAtUtc,
                ListingsObservedAtUtc = header.ListingsObservedAtUtc,
                Storage = header.Storage,
                Bags = ApplyBags(existing.Bags, change.UpsertedBags, change.RemovedBags),
                MarketListings = change.ReplaceMarketListings ? change.MarketListings : existing.MarketListings,
            };
        }

        return before with
        {
            Metadata = delta.Metadata,
            Timestamp = delta.Timestamp,
            PlayerGil = delta.ReplacePlayerGil ? delta.PlayerGil : before.PlayerGil,
            PlayerInventory = playerBags,
            PlayerStorage = delta.ReplacePlayerStorage
                ? delta.PlayerStorage ?? new StorageSourceEvidence()
                : before.PlayerStorage,
            Retainers = before.Retainers
                .Where(retainer => retainers.ContainsKey(retainer.RetainerId))
                .Select(retainer => retainers[retainer.RetainerId])
                .Concat(retainers.Values.Where(retainer => before.Retainers.All(old => old.RetainerId != retainer.RetainerId)))
                .ToList(),
            RetainerManagement = delta.ReplaceRetainerManagement ? delta.RetainerManagement : before.RetainerManagement,
        };
    }

    private static List<InventoryBag> ApplyBags(
        IReadOnlyList<InventoryBag> before,
        IReadOnlyList<InventoryBag> upserts,
        IReadOnlyList<InventoryBagKey> removals)
    {
        var bags = IndexBags(before);
        foreach (var removal in removals)
        {
            if (!bags.Remove(removal))
                throw new InventoryDeltaConflictException($"Bag '{removal.BagName}' cannot be removed because it is absent from the base snapshot.");
        }
        foreach (var upsert in upserts)
            bags[InventoryBagKey.From(upsert)] = upsert;

        return before
            .Where(bag => bags.ContainsKey(InventoryBagKey.From(bag)))
            .Select(bag => bags[InventoryBagKey.From(bag)])
            .Concat(bags.Values.Where(bag => before.All(old => InventoryBagKey.From(old) != InventoryBagKey.From(bag))))
            .ToList();
    }

    private static bool SameIdentity(InventoryReport before, InventoryReportDelta delta) =>
        string.Equals(before.CharacterName, delta.CharacterName, StringComparison.Ordinal) &&
        string.Equals(before.HomeWorld, delta.HomeWorld, StringComparison.Ordinal) &&
        string.Equals(before.ServiceAccountKey, delta.ServiceAccountKey, StringComparison.Ordinal) &&
        before.ServiceAccountNumber == delta.ServiceAccountNumber;

    private static Dictionary<ulong, RetainerReport> IndexRetainers(IReadOnlyList<RetainerReport> retainers)
    {
        var indexed = new Dictionary<ulong, RetainerReport>();
        foreach (var retainer in retainers)
        {
            if (retainer.RetainerId == 0 || !indexed.TryAdd(retainer.RetainerId, retainer))
                throw new InventoryDeltaConflictException("The base snapshot has ambiguous retainer identity.");
        }

        return indexed;
    }

    private static Dictionary<InventoryBagKey, InventoryBag> IndexBags(IReadOnlyList<InventoryBag> bags)
    {
        var indexed = new Dictionary<InventoryBagKey, InventoryBag>();
        foreach (var bag in bags)
        {
            if (string.IsNullOrWhiteSpace(bag.BagName) || !indexed.TryAdd(InventoryBagKey.From(bag), bag))
                throw new InventoryDeltaConflictException("The base snapshot has ambiguous bag identity.");
        }

        return indexed;
    }
}
