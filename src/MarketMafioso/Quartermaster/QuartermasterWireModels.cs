using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketMafioso.Quartermaster;

public sealed record QuartermasterCapabilities(
    string ProviderInstanceId,
    long Revision,
    DateTimeOffset? GeneratedAtUtc)
{
    public ImmutableArray<string> Capabilities { get; init; } = [];
}

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
    public QuartermasterStockCoverageFlags? StockCoverage { get; init; }
    public ImmutableArray<string> RequestedSources { get; init; } = [];
    public ImmutableArray<string> ObservedSources { get; init; } = [];
    public DateTimeOffset? GilObservedAtUtc { get; init; }
    public DateTimeOffset? ListingsObservedAtUtc { get; init; }
}

public sealed record QuartermasterStockCoverageFlags
{
    public bool? Ordinary { get; init; }
    public bool? Crystals { get; init; }
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
    public ImmutableArray<QuartermasterStowagePlanSnapshot> StowagePlans { get; init; } = [];
    public bool HasStowageEvidence { get; init; }
}

public sealed record QuartermasterStowagePlanSnapshot(
    Guid Id,
    long Revision,
    QuartermasterOwner Owner,
    string Name,
    bool Enabled,
    int Priority,
    ImmutableArray<QuartermasterStowageRuleSnapshot> Rules);

public sealed record QuartermasterStowageRuleSnapshot(
    Guid Id,
    uint ItemId,
    string? ItemName,
    int DesiredPlayerQuantity,
    string Quality,
    bool Enabled,
    ImmutableArray<ulong> PreferredRetainerIds,
    string RoutingMode,
    string Overflow,
    string Action,
    int ActionQuantity,
    int PlayerQuantity);

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

/// <summary>
/// One shortage line. SourceRetainerIds (shortageSourceRetainers.v1, immediate requests only) limits retrieval to
/// those retainers of the owner, plus the saddlebag when SourceSaddlebag; null retrieves from any of them.
/// </summary>
public sealed record QuartermasterShortageTarget(
    uint ItemId,
    string ItemName,
    int TargetQuantity,
    int ShortageQuantity,
    IReadOnlyList<ulong>? SourceRetainerIds = null,
    bool SourceSaddlebag = false);

public sealed record QuartermasterShortageRequest(
    string RequestId,
    string OperationId,
    DateTimeOffset SubmittedAtUtc,
    QuartermasterOwner Owner,
    bool ExecuteImmediately,
    ImmutableArray<QuartermasterShortageTarget> Items);

public sealed record QuartermasterElementalDepositTarget(
    uint ItemId,
    string ItemName,
    int MaximumQuantity);

public sealed record QuartermasterElementalDepositRequest(
    string RequestId,
    string OperationId,
    DateTimeOffset SubmittedAtUtc,
    QuartermasterOwner Owner,
    ImmutableArray<QuartermasterElementalDepositTarget> Items);

public sealed record QuartermasterAcknowledgement(
    string RequestId,
    string OperationId,
    string? ProviderInstanceId,
    long? Revision,
    bool Accepted,
    string Status,
    string? Message)
{
    public bool ExecuteImmediately { get; init; }

    /// <summary>Why a rejected request was refused, when Quartermaster said.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The item a rejection is about, when it is about one.</summary>
    public uint? ItemId { get; init; }

    /// <summary>The retainer a rejection is about, when it is about one.</summary>
    public ulong? RetainerId { get; init; }
}

public sealed record QuartermasterOperationStatus(
    string OperationId,
    string? RequestId,
    string? ProviderInstanceId,
    long? Revision,
    bool? ExecuteImmediately,
    QuartermasterOwner Owner,
    string Status,
    string? Message,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    ImmutableArray<QuartermasterOperationReceipt> Receipts)
{
    public string? Code { get; init; }
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
    string? Kind,
    string? OperationId);

internal sealed class QuartermasterCapabilitiesWire
{
    public string? Schema { get; init; }
    public string? ProviderInstanceId { get; init; }
    public long Revision { get; init; }
    public string? GeneratedAtUtc { get; init; }
    public List<string>? Capabilities { get; init; }
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
    public JsonElement? StowagePlans { get; init; }
}

internal sealed class QuartermasterStowageEnvelopeWire
{
    public string? Schema { get; init; }
    public List<QuartermasterStowagePlanWire>? Plans { get; init; }
}

internal sealed class QuartermasterStowagePlanWire
{
    public Guid Id { get; init; }
    public long Revision { get; init; }
    public QuartermasterOwnerWire? Owner { get; init; }
    public string? Name { get; init; }
    public bool Enabled { get; init; }
    public int Priority { get; init; }
    public List<QuartermasterStowageRuleWire>? Rules { get; init; }
}

internal sealed class QuartermasterStowageRuleWire
{
    public Guid Id { get; init; }
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public int DesiredPlayerQuantity { get; init; }
    public string? Quality { get; init; }
    public bool Enabled { get; init; }
    public QuartermasterStowageRoutingWire? Routing { get; init; }
    public QuartermasterStowageEvaluationWire? Evaluated { get; init; }
}

internal sealed class QuartermasterStowageRoutingWire
{
    public JsonElement? Mode { get; init; }
    public List<ulong>? PreferredRetainerIds { get; init; }
    public JsonElement? Overflow { get; init; }
}

internal sealed class QuartermasterStowageEvaluationWire
{
    public string? Action { get; init; }
    public int Quantity { get; init; }
    public int PlayerQuantity { get; init; }
    public int DesiredPlayerQuantity { get; init; }
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
    public QuartermasterStockCoverageFlags? StockCoverage { get; init; }
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
    public bool? ExecuteImmediately { get; init; }
    public List<QuartermasterShortageTargetWire> Items { get; init; } = [];
}

internal sealed class QuartermasterShortageTargetWire
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int TargetQuantity { get; init; }
    public int ShortageQuantity { get; init; }
    public List<ulong>? SourceRetainerIds { get; init; }
    public bool? SourceSaddlebag { get; init; }
}

internal sealed class QuartermasterElementalDepositRequestWire
{
    public string Schema { get; init; } = string.Empty;
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public string SubmittedAtUtc { get; init; } = string.Empty;
    public QuartermasterOwnerWire Owner { get; init; } = new();
    public bool ExecuteImmediately { get; init; }
    public List<QuartermasterElementalDepositTargetWire> Items { get; init; } = [];
}

internal sealed class QuartermasterElementalDepositTargetWire
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int MaximumQuantity { get; init; }
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
    public string? ErrorCode { get; init; }
    public uint? ItemId { get; init; }
    public ulong? RetainerId { get; init; }
}

internal sealed class QuartermasterOperationStatusWire
{
    public string? Code { get; init; }
    public string? Schema { get; init; }
    public string? OperationId { get; init; }
    public string? RequestId { get; init; }
    public string? ProviderInstanceId { get; init; }
    public long? Revision { get; init; }
    public bool? ExecuteImmediately { get; init; }
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
    public string? Kind { get; init; }
    public string? OperationId { get; init; }
}

public sealed record QuartermasterCourierCharacter(
    ulong LocalContentId,
    uint HomeWorldId,
    string CharacterName,
    string HomeWorldName)
{
    public bool IsSameCharacter(ulong localContentId, uint homeWorldId) =>
        LocalContentId != 0 && LocalContentId == localContentId && HomeWorldId == homeWorldId;
}

public sealed record QuartermasterCourierLoggedIn(
    ulong LocalContentId,
    uint HomeWorldId,
    string CharacterName,
    string HomeWorldName,
    string? RegionId,
    string? RegionName,
    ulong CarriedGil,
    int FreeBagSlots,
    bool HasMeetingBell,
    string? MeetingBellLabel,
    bool? IsPairedWithCarrier = null);

public sealed record QuartermasterCourierCarrier(
    bool Configured,
    bool Online,
    ulong LocalContentId,
    uint HomeWorldId,
    string? CharacterName,
    string? HomeWorldName,
    string? RegionId,
    string? RegionName,
    ulong CarriedGil,
    int FreeBagSlots,
    bool? CarrierOptIn = null,
    bool? CanSendOwnStock = null);

public sealed record QuartermasterCourierBridge(
    uint WorldId,
    string? WorldName,
    uint TerritoryId,
    string? Label);

public sealed record QuartermasterCourierRosterEntry(
    ulong LocalContentId,
    uint HomeWorldId,
    string CharacterName,
    string HomeWorldName,
    string? RegionId,
    string? RegionName,
    bool HasMeetingBell,
    string? MeetingBellLabel,
    bool IsCarrier,
    bool? IsPairedWithCarrier = null);

public sealed record QuartermasterCourierRun(
    Guid? RunId,
    string? RequestId,
    string State,
    string? OwnerPhase,
    string? CarrierPhase,
    int Trip,
    int Trips,
    string? Code,
    string? Detail,
    bool Active,
    bool CanResume,
    bool CanClear,
    long CustodyUnits,
    long CustodyGil,
    QuartermasterCourierAutoResume? AutoResume = null)
{
    public bool IsRunning => State.Equals("Running", StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => State.Equals("Completed", StringComparison.OrdinalIgnoreCase);
    public bool IsFailure => State.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                             State.Equals("Interrupted", StringComparison.OrdinalIgnoreCase) ||
                             State.Equals("Indeterminate", StringComparison.OrdinalIgnoreCase) ||
                             State.Equals("Refused", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Quartermaster resuming a halted courier run on its own, under the same run id. Null when no automatic resume
/// applies to the run, and always null from a Quartermaster that predates the field.
/// </summary>
/// <param name="Attempt">The pending attempt; otherwise the attempts made since the run last progressed.</param>
/// <param name="DueInSeconds">Seconds until the pending attempt is sent; 0 while it waits for the client to be ready.</param>
public sealed record QuartermasterCourierAutoResume(
    bool Pending,
    int Attempt,
    int MaxAttempts,
    int? DueInSeconds,
    string? Code,
    string? ExhaustedCode,
    string? ExhaustedDetail,
    int HistoryCount);

public sealed record QuartermasterCourierStatus(
    string ProviderInstanceId,
    QuartermasterCourierLoggedIn? LoggedIn,
    QuartermasterCourierCarrier? Carrier,
    QuartermasterCourierBridge? Bridge,
    ImmutableArray<QuartermasterCourierRosterEntry> Roster,
    QuartermasterCourierRun? Run);

/// <summary>RetainerId (courierTransferToRetainer.v1): deposit these units into that retainer of the destination; null keeps them in its bags.</summary>
public sealed record QuartermasterCourierItem(
    uint ItemId,
    string ItemName,
    bool IsHq,
    int Quantity,
    ulong? RetainerId = null);

public sealed record QuartermasterCourierTransferRequest(
    string RequestId,
    QuartermasterCourierCharacter Source,
    QuartermasterCourierCharacter Destination,
    int GilAmount,
    ImmutableArray<QuartermasterCourierItem> Items,
    bool ReturnOwner,
    bool ReturnDestinationHome,
    bool ReturnCarrier);

public static class QuartermasterCourierAckStatuses
{
    public const string Accepted = "accepted";
    public const string AlreadyRunning = "already_running";
    public const string AlreadyCompleted = "already_completed";
    public const string Rejected = "rejected";
    public const string Stopping = "stopping";
    public const string Idle = "idle";
    /// <summary>ClearCourierRun accepted: Quartermaster is forgetting the run.</summary>
    public const string Clearing = "clearing";
}

/// <summary>
/// BlockingRequestId and BlockingSource (optional; older Quartermaster builds omit them): for a refusal caused by
/// another run, the request that started it and its source: delivered-stow, ipc or manual.
/// </summary>
public sealed record QuartermasterCourierAcknowledgement(
    string? ProviderInstanceId,
    string? RequestId,
    Guid? RunId,
    string Status,
    string? ErrorCode,
    string? Message,
    string? BlockingRequestId = null,
    string? BlockingSource = null)
{
    /// <summary>"delivered-stow forward X", "ipc request X", "manual", ...; null when Quartermaster named neither.</summary>
    public string? DescribeBlocking() => (BlockingSource, BlockingRequestId) switch
    {
        (null or "", null or "") => null,
        ("delivered-stow", { Length: > 0 } id) => $"delivered-stow forward {id}",
        ({ Length: > 0 } source, { Length: > 0 } id) => $"{source} request {id}",
        ({ Length: > 0 } source, _) => source,
        (_, var id) => $"request {id}",
    };

    /// <summary>The run named by the request exists: newly accepted, deduplicated, or already finished.</summary>
    public bool IsAccepted => Status is QuartermasterCourierAckStatuses.Accepted or
        QuartermasterCourierAckStatuses.AlreadyRunning or QuartermasterCourierAckStatuses.AlreadyCompleted;
}

internal sealed class QuartermasterCourierStatusWire
{
    public string? Schema { get; init; }
    public string? ProviderInstanceId { get; init; }
    public string? Error { get; init; }
    public QuartermasterCourierLoggedInWire? LoggedIn { get; init; }
    public QuartermasterCourierCarrierWire? Carrier { get; init; }
    public QuartermasterCourierBridgeWire? Bridge { get; init; }
    public List<QuartermasterCourierRosterEntryWire>? Roster { get; init; }
    public QuartermasterCourierRunWire? Run { get; init; }
}

internal sealed class QuartermasterCourierLoggedInWire
{
    public ulong LocalContentId { get; init; }
    public uint HomeWorldId { get; init; }
    public string? CharacterName { get; init; }
    public string? HomeWorldName { get; init; }
    public JsonElement? RegionId { get; init; }
    public string? RegionName { get; init; }
    public ulong CarriedGil { get; init; }
    public int FreeBagSlots { get; init; }
    public bool HasMeetingBell { get; init; }
    public string? MeetingBellLabel { get; init; }
    public bool? IsPairedWithCarrier { get; init; }
}

internal sealed class QuartermasterCourierCarrierWire
{
    public bool Configured { get; init; }
    public bool Online { get; init; }
    public ulong? LocalContentId { get; init; }
    public uint? HomeWorldId { get; init; }
    public string? CharacterName { get; init; }
    public string? HomeWorldName { get; init; }
    public JsonElement? RegionId { get; init; }
    public string? RegionName { get; init; }
    public ulong? CarriedGil { get; init; }
    public int? FreeBagSlots { get; init; }
    public bool? CarrierOptIn { get; init; }
    public bool? CanSendOwnStock { get; init; }
}

internal sealed class QuartermasterCourierBridgeWire
{
    public uint WorldId { get; init; }
    public string? WorldName { get; init; }
    public uint TerritoryId { get; init; }
    public string? Label { get; init; }
}

internal sealed class QuartermasterCourierRosterEntryWire
{
    public ulong LocalContentId { get; init; }
    public uint HomeWorldId { get; init; }
    public string? CharacterName { get; init; }
    public string? HomeWorldName { get; init; }
    public JsonElement? RegionId { get; init; }
    public string? RegionName { get; init; }
    public bool HasMeetingBell { get; init; }
    public string? MeetingBellLabel { get; init; }
    public bool IsCarrier { get; init; }
    public bool? IsPairedWithCarrier { get; init; }
}

internal sealed class QuartermasterCourierRunWire
{
    public Guid? RunId { get; init; }
    public string? RequestId { get; init; }
    public string? State { get; init; }
    public string? OwnerPhase { get; init; }
    public string? CarrierPhase { get; init; }
    public int Trip { get; init; }
    public int Trips { get; init; }
    public string? Code { get; init; }
    public string? Detail { get; init; }
    public bool Active { get; init; }
    public bool CanResume { get; init; }
    public bool CanClear { get; init; }
    public long CustodyUnits { get; init; }
    public long CustodyGil { get; init; }
    public QuartermasterCourierAutoResumeWire? AutoResume { get; init; }
}

internal sealed class QuartermasterCourierAutoResumeWire
{
    public bool Pending { get; init; }
    public int Attempt { get; init; }
    public int MaxAttempts { get; init; }
    public int? DueInSeconds { get; init; }
    public string? Code { get; init; }
    public QuartermasterCourierAutoResumeExhaustedWire? Exhausted { get; init; }
    public List<JsonElement>? History { get; init; }
}

internal sealed class QuartermasterCourierAutoResumeExhaustedWire
{
    public string? Code { get; init; }
    public string? Detail { get; init; }
}

internal sealed class QuartermasterCourierCharacterWire
{
    public ulong LocalContentId { get; init; }
    public uint HomeWorldId { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public string HomeWorldName { get; init; } = string.Empty;
}

internal sealed class QuartermasterCourierItemWire
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public bool IsHq { get; init; }
    public int Quantity { get; init; }
    // Omitted when null, so a bag-only request is the payload (and request hash) it always was.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? RetainerId { get; init; }
}

internal sealed class QuartermasterCourierTransferRequestWire
{
    public string Schema { get; init; } = string.Empty;
    public string ProviderInstanceId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public QuartermasterCourierCharacterWire Source { get; init; } = new();
    public QuartermasterCourierCharacterWire Destination { get; init; } = new();
    public int GilAmount { get; init; }
    public List<QuartermasterCourierItemWire> Items { get; init; } = [];
    public bool ReturnOwner { get; init; }
    public bool ReturnDestinationHome { get; init; }
    public bool ReturnCarrier { get; init; }
}

internal sealed class QuartermasterCourierAcknowledgementWire
{
    public string? Schema { get; init; }
    public string? ProviderInstanceId { get; init; }
    public string? RequestId { get; init; }
    public Guid? RunId { get; init; }
    public string? Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public string? BlockingRequestId { get; init; }
    public string? BlockingSource { get; init; }
}
