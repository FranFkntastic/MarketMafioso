using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MarketMafioso.Quartermaster;

public sealed record QuartermasterCapabilities(
    string ProviderInstanceId,
    long Revision,
    DateTimeOffset? GeneratedAtUtc);

public sealed record QuartermasterOwner(
    ulong LocalContentId,
    uint HomeWorldId,
    string CharacterName,
    string? HomeWorldName);

public sealed record QuartermasterItemSnapshot(
    uint ItemId,
    string? ItemName,
    string? ItemType,
    uint Quantity,
    bool IsHq,
    float Condition,
    string? ContainerKey,
    int? SlotIndex,
    float? ConditionPercent,
    bool? Equipped);

public sealed record QuartermasterBagSnapshot(
    string BagName,
    string? Location,
    ImmutableArray<QuartermasterItemSnapshot> Items)
{
    public DateTimeOffset? ObservedAtUtc { get; init; }
}

public sealed record QuartermasterListingSnapshot(
    uint ItemId,
    string? ItemName,
    string? ItemType,
    uint Quantity,
    bool IsHq,
    float Condition,
    string? ContainerKey,
    int? SlotIndex,
    float? ConditionPercent,
    uint? UnitPrice,
    string? ListedAt);

public sealed record QuartermasterRetainerSnapshot(
    ulong RetainerId,
    string RetainerName,
    DateTimeOffset ObservedAtUtc,
    ulong Gil,
    ImmutableArray<QuartermasterBagSnapshot> Bags,
    ImmutableArray<QuartermasterListingSnapshot> Listings)
{
    public ImmutableArray<string> RequestedSources { get; init; } = [];
    public ImmutableArray<string> ObservedSources { get; init; } = [];
    public DateTimeOffset? GilObservedAtUtc { get; init; }
    public DateTimeOffset? ListingsObservedAtUtc { get; init; }
}

public sealed record QuartermasterSnapshot(
    string ProviderInstanceId,
    long Revision,
    DateTimeOffset GeneratedAtUtc,
    QuartermasterOwner Owner,
    ImmutableArray<QuartermasterRetainerSnapshot> Retainers)
{
    public ImmutableArray<string> PlayerRequestedSources { get; init; } = [];
    public ImmutableArray<string> PlayerObservedSources { get; init; } = [];
}

public sealed record QuartermasterOwnerScope(
    ulong? LocalContentId,
    uint? HomeWorldId,
    string? CharacterName,
    string? HomeWorldName)
{
    public bool IsAvailable => LocalContentId is > 0 && HomeWorldId is > 0;

    public bool Matches(QuartermasterOwner owner) =>
        IsAvailable &&
        owner.LocalContentId == LocalContentId &&
        owner.HomeWorldId == HomeWorldId;
}

public sealed record QuartermasterShortageTarget(
    uint ItemId,
    string ItemName,
    int TargetQuantity,
    int ShortageQuantity);

public sealed record QuartermasterShortageRequest(
    string RequestId,
    string OperationId,
    DateTimeOffset SubmittedAtUtc,
    QuartermasterOwner Owner,
    ImmutableArray<QuartermasterShortageTarget> Items);

public sealed record QuartermasterAcknowledgement(
    string RequestId,
    string OperationId,
    string? ProviderInstanceId,
    long? Revision,
    bool Accepted,
    string Status,
    string? Message);

public sealed record QuartermasterOperationStatus(
    string OperationId,
    string? RequestId,
    string? ProviderInstanceId,
    long? Revision,
    QuartermasterOwner Owner,
    string Status,
    string? Message,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    ImmutableArray<QuartermasterOperationReceipt> Receipts)
{
    public bool IsTerminal => Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
                               Status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase) ||
                               Status.Equals("Partially_Succeeded", StringComparison.OrdinalIgnoreCase) ||
                               Status.Equals("Indeterminate", StringComparison.OrdinalIgnoreCase) ||
                               Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                               Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                               Status.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ||
                               Status.Equals("Not_Found", StringComparison.OrdinalIgnoreCase);
}

public sealed record QuartermasterOperationReceipt(
    long Revision,
    DateTimeOffset OccurredAtUtc,
    string Status,
    string Code,
    string Message,
    uint? ItemId,
    ulong? RetainerId,
    int? Quantity);

public sealed record QuartermasterChanged(
    string ProviderInstanceId,
    long Revision,
    string? OperationId);

internal sealed class QuartermasterCapabilitiesWire
{
    public string? Schema { get; init; }
    public string? ProviderInstanceId { get; init; }
    public long Revision { get; init; }
    public string? GeneratedAtUtc { get; init; }
}

internal sealed class QuartermasterSnapshotWire
{
    public string? Schema { get; init; }
    public string? ProviderInstanceId { get; init; }
    public long Revision { get; init; }
    public string? GeneratedAtUtc { get; init; }
    public QuartermasterOwnerWire? Owner { get; init; }
    public QuartermasterOwnerWire? CurrentOwner { get; init; }
    public QuartermasterStorageSourcesWire? PlayerStorage { get; init; }
    public List<QuartermasterRetainerWire>? Retainers { get; init; }
}

internal sealed class QuartermasterStorageSourcesWire
{
    public List<string>? RequestedSources { get; init; }
    public List<string>? ObservedSources { get; init; }
}

internal sealed class QuartermasterOwnerWire
{
    public ulong LocalContentId { get; init; }
    public uint HomeWorldId { get; init; }
    public string? CharacterName { get; init; }
    public string? HomeWorldName { get; init; }
}

internal sealed class QuartermasterRetainerWire
{
    public ulong RetainerId { get; init; }
    public string? RetainerName { get; init; }
    public string? ObservedAtUtc { get; init; }
    public string? LastUpdated { get; init; }
    public ulong Gil { get; init; }
    public string? GilObservedAtUtc { get; init; }
    public string? ListingsObservedAtUtc { get; init; }
    public List<string>? RequestedSources { get; init; }
    public List<string>? ObservedSources { get; init; }
    public List<QuartermasterBagWire>? Bags { get; init; }
    public List<QuartermasterListingWire>? Listings { get; init; }
    public List<QuartermasterListingWire>? MarketListings { get; init; }
}

internal sealed class QuartermasterBagWire
{
    public string? BagName { get; init; }
    public string? Location { get; init; }
    public string? ObservedAtUtc { get; init; }
    public List<QuartermasterItemWire>? Items { get; init; }
}

internal sealed class QuartermasterItemWire
{
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string? ItemType { get; init; }
    public uint Quantity { get; init; }
    public bool IsHq { get; init; }
    public float Condition { get; init; }
    public string? ContainerKey { get; init; }
    public int? SlotIndex { get; init; }
    public float? ConditionPercent { get; init; }
    public bool? Equipped { get; init; }
}

internal sealed class QuartermasterListingWire
{
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string? ItemType { get; init; }
    public uint Quantity { get; init; }
    public bool IsHq { get; init; }
    public float Condition { get; init; }
    public string? ContainerKey { get; init; }
    public int? SlotIndex { get; init; }
    public float? ConditionPercent { get; init; }
    public uint? UnitPrice { get; init; }
    public uint? Price { get; init; }
    public string? ListedAt { get; init; }
}

internal sealed class QuartermasterShortageRequestWire
{
    public string Schema { get; init; } = string.Empty;
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public string SubmittedAtUtc { get; init; } = string.Empty;
    public QuartermasterOwnerWire Owner { get; init; } = new();
    public List<QuartermasterShortageTargetWire> Items { get; init; } = [];
}

internal sealed class QuartermasterShortageTargetWire
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int TargetQuantity { get; init; }
    public int ShortageQuantity { get; init; }
}

internal sealed class QuartermasterAcknowledgementWire
{
    public string? Schema { get; init; }
    public string? RequestId { get; init; }
    public string? OperationId { get; init; }
    public string? ProviderInstanceId { get; init; }
    public long? Revision { get; init; }
    public bool? Accepted { get; init; }
    public string? Status { get; init; }
    public string? Message { get; init; }
}

internal sealed class QuartermasterOperationStatusWire
{
    public string? Schema { get; init; }
    public string? OperationId { get; init; }
    public string? RequestId { get; init; }
    public string? ProviderInstanceId { get; init; }
    public long? Revision { get; init; }
    public QuartermasterOwnerWire? Owner { get; init; }
    public string? State { get; init; }
    public string? Status { get; init; }
    public string? Message { get; init; }
    public string? UpdatedAtUtc { get; init; }
    public string? CompletedAtUtc { get; init; }
    public List<QuartermasterOperationReceiptWire>? Receipts { get; init; }
}

internal sealed class QuartermasterOperationReceiptWire
{
    public long Revision { get; init; }
    public string? OccurredAtUtc { get; init; }
    public string? Status { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public uint? ItemId { get; init; }
    public ulong? RetainerId { get; init; }
    public int? Quantity { get; init; }
}

internal sealed class QuartermasterChangedWire
{
    public string? Schema { get; init; }
    public string? ProviderInstanceId { get; init; }
    public long Revision { get; init; }
    public string? OperationId { get; init; }
}
