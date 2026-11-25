using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace MarketMafioso.Quartermaster;

/// <summary>
/// The Quartermaster calls a cross-region run needs around courier custody: retrieving counted stock from
/// the logged-in character's retainers into its bags, and stowing delivered goods into their home retainers.
/// </summary>
public interface IQuartermasterRunCustodyClient
{
    /// <summary>True when Quartermaster advertises automatic retrieval. Never throws.</summary>
    bool HasAutomaticRetrieval { get; }
    /// <summary>True when Quartermaster advertises deliveredStow.v1 and exposes its channels. Never throws.</summary>
    bool HasDeliveredStow { get; }
    /// <summary>
    /// True when Quartermaster advertises automatic retrieval and shortageSourceRetainers.v1, so an immediate
    /// retrieval can be limited to chosen retainers. Never throws.
    /// </summary>
    bool HasShortageSourceRetainers { get; }
    bool TrySubmitShortages(QuartermasterShortageRequest request, out QuartermasterAcknowledgement? acknowledgement, out string error);
    bool TryGetOperation(string operationId, QuartermasterOwnerScope expectedOwner, out QuartermasterOperationStatus? operationStatus, out string error);
    bool TrySubmitDeliveredStow(QuartermasterDeliveredStowRequest request, out QuartermasterDeliveredStowStatus? acknowledgement, out string error);
    bool TryGetDeliveredStow(string requestId, out QuartermasterDeliveredStowStatus? status, out string error);
    /// <summary>
    /// Withdraws a retrieval or stow this plugin submitted: not started = cancelled; running = stops after the
    /// current retainer. Returns false when the call could not be made or was rejected.
    /// </summary>
    bool TryCancelRequestedOperation(string requestId, string reason, out QuartermasterCancelAcknowledgement? acknowledgement, out string error);

    /// <summary>
    /// Cancels the courier forward of a delivered stow this plugin submitted (deliveredStowForwardCancel.v1): a
    /// pending forward never starts; one with a courier run answers stopping with its RunId. False when the call could
    /// not be made, was rejected, or Quartermaster does not advertise the capability (the error then says so).
    /// </summary>
    bool TryCancelDeliveredStowForward(string requestId, string reason, out QuartermasterCancelAcknowledgement? acknowledgement, out string error)
    {
        acknowledgement = null;
        error = $"Quartermaster does not advertise {QuartermasterIpcClient.DeliveredStowForwardCancelCapability}.";
        return false;
    }
}

public interface IQuartermasterDeliveredStowAdapter
{
    bool HasSubmitDeliveredStow { get; }
    bool HasGetDeliveredStow { get; }
    string SubmitDeliveredStow(string requestJson);
    string GetDeliveredStow(string requestId);
    bool HasCancelRequestedOperation { get; }
    string CancelRequestedOperation(string requestJson);
    bool HasCancelDeliveredStowForward => false;
    string CancelDeliveredStowForward(string requestJson) => throw new NotSupportedException();
}

/// <summary>
/// Status: cancelled, stopping, already_terminal, cancel_queued, not_found or rejected (ErrorCode names why). A
/// forward cancel's stopping ack carries the courier RunId Quartermaster is stopping. cancel_queued is not settled:
/// Quartermaster holds no persisted operation for the id and keeps the cancel only in memory, so it is re-sent.
/// </summary>
public sealed record QuartermasterCancelAcknowledgement
{
    public string Schema { get; init; } = string.Empty;
    public string? ProviderInstanceId { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public string? OperationId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? OperationStatus { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    /// <summary>The forward's courier run (forward cancel only); a GUID string.</summary>
    public string? RunId { get; init; }

    public bool IsRejected => string.Equals(Status, "rejected", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// cancelled, already_terminal or not_found: nothing more to do. stopping is re-checked until terminal;
    /// cancel_queued is re-sent (Quartermaster keeps it in memory only); rejected is retried.
    /// </summary>
    public bool IsSettled => Status.ToLowerInvariant() is "cancelled" or "already_terminal" or "not_found";

    public Guid? ParsedRunId => Guid.TryParse(RunId, out var run) && run != Guid.Empty ? run : null;
}

/// <summary>
/// Quantity: units this delivery put into the owner's bags. SentDirect (deliveredStowForward.v1): units already delivered
/// straight to the item's homes on another character, never forwarded again; Quantity may then be 0.
/// </summary>
public sealed record QuartermasterDeliveredStowItem(uint ItemId, string ItemName, bool IsHq, int Quantity, int SentDirect = 0);

/// <summary>AllowForward (deliveredStowForward.v1): Quartermaster carries what the owner's homes leave to the item's homes on another character.</summary>
public sealed record QuartermasterDeliveredStowRequest(
    string RequestId,
    QuartermasterOwner Owner,
    IReadOnlyList<QuartermasterDeliveredStowItem> Items,
    bool AllowForward = false);

/// <summary>A character of this account as delivered-stow status names it.</summary>
public sealed record QuartermasterStowCharacter
{
    public ulong LocalContentId { get; init; }
    public uint HomeWorldId { get; init; }
    public string CharacterName { get; init; } = string.Empty;
    public string HomeWorldName { get; init; } = string.Empty;
}

public sealed record QuartermasterStowHome
{
    public ulong RetainerId { get; init; }
    /// <summary>Null while that retainer has not been observed under its character.</summary>
    public string? RetainerName { get; init; }
}

/// <summary>
/// The part of a stow line bound for another character's homes, carried there by Quartermaster's courier after the
/// owner's own stow. Code: Pending, Running, Delivered, PartlyDelivered, NotDelivered, NoRoom, NothingLeft, SentDirect,
/// or a refusal/outcome code (Message says why). Unknown codes read as not delivered.
/// </summary>
public sealed record QuartermasterStowForwardLine
{
    public QuartermasterStowCharacter? Character { get; init; }
    public IReadOnlyList<QuartermasterStowHome> Homes { get; init; } = [];
    public int Wanted { get; init; }
    public int Planned { get; init; }
    public int? Delivered { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    [JsonIgnore]
    public bool IsOpen => Code is "Pending" or "Running";
    /// <summary>Nothing was meant to move (homes full, nothing left over, or already sent directly).</summary>
    [JsonIgnore]
    public bool IsNeutral => Code is "NoRoom" or "NothingLeft" or "SentDirect";
}

/// <summary>The request's courier forward as a whole. State: pending, running or settled.</summary>
public sealed record QuartermasterStowForward
{
    public string State { get; init; } = string.Empty;
    public string? RunId { get; init; }
    public string? Code { get; init; }
    public string Message { get; init; } = string.Empty;

    [JsonIgnore]
    public bool IsSettled => string.Equals(State, "settled", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Code: Planned, NoHome, NoRoom, NotCarried, Stowed, PartlyStowed, NotStowed.</summary>
public sealed record QuartermasterDeliveredStowLine
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public bool IsHq { get; init; }
    public int Requested { get; init; }
    /// <summary>Of Requested, what was in the bags at planning; null from older Quartermaster builds.</summary>
    public int? Carried { get; init; }
    public int Planned { get; init; }
    public int Stowed { get; init; }
    public string Code { get; init; } = string.Empty;
    public IReadOnlyList<string> Destinations { get; init; } = [];
    /// <summary>Present only for an item with homes on another character (deliveredStowForward.v1).</summary>
    public QuartermasterStowForwardLine? Forward { get; init; }
    /// <summary>
    /// One part per character when the item's homes span several other characters (arrivalPlan.v2); null from older
    /// Quartermaster builds, which send the single <see cref="Forward"/>.
    /// </summary>
    public IReadOnlyList<QuartermasterStowForwardLine>? Forwards { get; init; }

    /// <summary>Every part of this line bound for another character: Forwards when sent, else Forward, else none.</summary>
    [JsonIgnore]
    public IReadOnlyList<QuartermasterStowForwardLine> AllForwards =>
        Forwards is { Count: > 0 } forwards ? forwards : Forward is { } forward ? [forward] : [];

    [JsonIgnore]
    public bool HasForward => AllForwards.Count > 0;

    /// <summary>This line with these forward parts (the first also kept as Forward for single-forward readers).</summary>
    public QuartermasterDeliveredStowLine WithForwards(IReadOnlyList<QuartermasterStowForwardLine> forwards) =>
        this with { Forward = forwards.Count > 0 ? forwards[0] : null, Forwards = forwards.Count > 0 ? forwards : null };
}

public sealed record QuartermasterDeliveredStowStatus
{
    public string Schema { get; init; } = string.Empty;
    public string? ProviderInstanceId { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public string? OperationId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<QuartermasterDeliveredStowLine> Lines { get; init; } = [];
    /// <summary>With a forward: the owner's own stow operation alone (null when there is none).</summary>
    public string? DepositStatus { get; init; }
    public QuartermasterStowForward? Forward { get; init; }

    public bool IsRejected => Is("rejected");
    public bool IsNothingToStow => Is(QuartermasterIpcClient.DeliveredStowNothingToStow);
    public bool IsNotFound => Is("not_found");
    public bool IsTerminal => IsRejected || IsNothingToStow || IsNotFound ||
                              Is("succeeded") || Is("partially_succeeded") || Is("indeterminate") || Is("failed") || Is("cancelled");

    /// <summary>
    /// The owner's own stow is over: the whole request is terminal, or a forward follows and the local operation is
    /// terminal or was never needed. The forward may then wait a long time for the courier; it never holds the run.
    /// </summary>
    public bool IsLocalSettled => IsTerminal || Forward is not null &&
        (OperationId is null || DepositStatus is { } deposit && IsTerminalStatus(deposit));

    /// <summary>The local outcome: the deposit operation's status when a forward follows, else the request's.</summary>
    public string LocalStatus => Forward is null ? Status
        : DepositStatus is { Length: > 0 } deposit ? deposit
        : OperationId is null ? QuartermasterIpcClient.DeliveredStowNothingToStow
        : Status;

    private static bool IsTerminalStatus(string status) => status.ToLowerInvariant() is
        "succeeded" or "partially_succeeded" or "indeterminate" or "failed" or "cancelled" or "rejected";

    private bool Is(string status) => string.Equals(Status, status, StringComparison.OrdinalIgnoreCase);
}

public sealed partial class DalamudQuartermasterIpcAdapter : IQuartermasterDeliveredStowAdapter
{
    private DeliveredStowGates? deliveredStowGates;

    private void InitializeDeliveredStow(IDalamudPluginInterface pluginInterface) => deliveredStowGates = new(
        pluginInterface.GetIpcSubscriber<string, string>(QuartermasterIpcClient.SubmitDeliveredStowChannel),
        pluginInterface.GetIpcSubscriber<string, string>(QuartermasterIpcClient.GetDeliveredStowChannel),
        pluginInterface.GetIpcSubscriber<string, string>(QuartermasterIpcClient.CancelRequestedOperationChannel),
        pluginInterface.GetIpcSubscriber<string, string>(QuartermasterIpcClient.CancelDeliveredStowForwardChannel));

    public bool HasSubmitDeliveredStow => deliveredStowGates?.Submit.HasFunction == true;
    public bool HasGetDeliveredStow => deliveredStowGates?.Get.HasFunction == true;
    public string SubmitDeliveredStow(string requestJson) => deliveredStowGates!.Submit.InvokeFunc(requestJson);
    public string GetDeliveredStow(string requestId) => deliveredStowGates!.Get.InvokeFunc(requestId);
    public bool HasCancelRequestedOperation => deliveredStowGates?.Cancel.HasFunction == true;
    public string CancelRequestedOperation(string requestJson) => deliveredStowGates!.Cancel.InvokeFunc(requestJson);
    public bool HasCancelDeliveredStowForward => deliveredStowGates?.CancelForward.HasFunction == true;
    public string CancelDeliveredStowForward(string requestJson) => deliveredStowGates!.CancelForward.InvokeFunc(requestJson);

    private sealed record DeliveredStowGates(
        ICallGateSubscriber<string, string> Submit,
        ICallGateSubscriber<string, string> Get,
        ICallGateSubscriber<string, string> Cancel,
        ICallGateSubscriber<string, string> CancelForward);
}

public sealed partial class QuartermasterIpcClient : IQuartermasterRunCustodyClient
{
    public const string DeliveredStowCapability = "deliveredStow.v1";
    public const string DeliveredStowForwardCapability = "deliveredStowForward.v1";
    public const string SubmitDeliveredStowChannel = "Quartermaster.v1.SubmitDeliveredStow";
    public const string GetDeliveredStowChannel = "Quartermaster.v1.GetDeliveredStow";
    public const string DeliveredStowRequestSchema = "gooseworks-quartermaster-delivered-stow/v1";
    public const string DeliveredStowNothingToStow = "nothing_to_stow";
    public const string RequestedOperationCancelCapability = "requestedOperationCancel.v1";
    public const string CancelRequestedOperationChannel = "Quartermaster.v1.CancelRequestedOperation";
    public const string CancelRequestSchema = "gooseworks-quartermaster-cancel-request/v1";
    public const string CancelAckSchema = "gooseworks-quartermaster-cancel-request-ack/v1";
    public const string DeliveredStowForwardCancelCapability = "deliveredStowForwardCancel.v1";
    public const string CancelDeliveredStowForwardChannel = "Quartermaster.v1.CancelDeliveredStowForward";
    /// <summary>The forward cancel's request schema (Quartermaster rejects any other schema it is given).</summary>
    public const string ForwardCancelRequestSchema = "gooseworks-quartermaster-delivered-stow-forward-cancel/v1";
    /// <summary>The forward cancel's acknowledgement schema.</summary>
    public const string ForwardCancelAckSchema = "gooseworks-quartermaster-delivered-stow-forward-cancel-ack/v1";
    /// <summary>A generic forward ack schema, still accepted alongside <see cref="ForwardCancelAckSchema"/> and <see cref="CancelAckSchema"/>.</summary>
    public const string CancelForwardAckSchema = "gooseworks-quartermaster-cancel-forward-ack/v1";

    /// <summary>True when a failed call's error says Quartermaster is loaded but lacks the capability.</summary>
    public static bool IsCapabilityMissing(string? error) =>
        error?.Contains("does not advertise", StringComparison.OrdinalIgnoreCase) == true;

    public bool TryCancelDeliveredStowForward(string requestId, string reason, out QuartermasterCancelAcknowledgement? acknowledgement, out string error)
    {
        acknowledgement = null;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            error = "Forward cancel needs the delivered-stow request ID.";
            return false;
        }
        if (!TryGetCapabilities(out var capabilities, out error))
            return false;
        if (!capabilities!.Capabilities.Contains(DeliveredStowForwardCancelCapability, StringComparer.Ordinal) ||
            adapter is not IQuartermasterDeliveredStowAdapter { HasCancelDeliveredStowForward: true } cancel)
        {
            error = $"Quartermaster does not advertise {DeliveredStowForwardCancelCapability}.";
            return false;
        }
        var requestJson = JsonSerializer.Serialize(new
        {
            schema = ForwardCancelRequestSchema,
            providerInstanceId = capabilities.ProviderInstanceId,
            requestId,
            reason,
        }, JsonOptions);
        string json;
        try { json = cancel.CancelDeliveredStowForward(requestJson); }
        catch (Exception ex)
        {
            error = $"Quartermaster forward cancel call failed: {ex.Message}";
            return false;
        }
        try { acknowledgement = JsonSerializer.Deserialize<QuartermasterCancelAcknowledgement>(json, JsonOptions); }
        catch (JsonException ex)
        {
            error = $"Quartermaster forward cancel acknowledgement is not valid JSON: {ex.Message}";
            return false;
        }
        if (acknowledgement is null || string.IsNullOrWhiteSpace(acknowledgement.Status) ||
            !(string.Equals(acknowledgement.Schema, ForwardCancelAckSchema, StringComparison.Ordinal) ||
              string.Equals(acknowledgement.Schema, CancelAckSchema, StringComparison.Ordinal) ||
              string.Equals(acknowledgement.Schema, CancelForwardAckSchema, StringComparison.Ordinal)))
        {
            acknowledgement = null;
            error = "Quartermaster forward cancel acknowledgement used an unsupported schema.";
            return false;
        }
        if (acknowledgement.IsRejected)
        {
            // The acknowledgement is kept: a caller tells a rejection (retried, warned once) from an unreachable Quartermaster.
            error = $"Quartermaster rejected the forward cancel ({acknowledgement.ErrorCode ?? acknowledgement.Status}): {acknowledgement.Message}";
            return false;
        }
        error = string.Empty;
        return true;
    }

    public bool TryCancelRequestedOperation(string requestId, string reason, out QuartermasterCancelAcknowledgement? acknowledgement, out string error)
    {
        acknowledgement = null;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            error = "Cancel needs the request ID.";
            return false;
        }
        if (!TryGetCapabilities(out var capabilities, out error))
            return false;
        if (!capabilities!.Capabilities.Contains(RequestedOperationCancelCapability, StringComparer.Ordinal) ||
            adapter is not IQuartermasterDeliveredStowAdapter { HasCancelRequestedOperation: true } cancel)
        {
            error = $"Quartermaster does not advertise {RequestedOperationCancelCapability}.";
            return false;
        }
        var requestJson = JsonSerializer.Serialize(new
        {
            schema = CancelRequestSchema,
            providerInstanceId = capabilities.ProviderInstanceId,
            requestId,
            reason,
        }, JsonOptions);
        string json;
        try { json = cancel.CancelRequestedOperation(requestJson); }
        catch (Exception ex)
        {
            error = $"Quartermaster cancel call failed: {ex.Message}";
            return false;
        }
        try { acknowledgement = JsonSerializer.Deserialize<QuartermasterCancelAcknowledgement>(json, JsonOptions); }
        catch (JsonException ex)
        {
            error = $"Quartermaster cancel acknowledgement is not valid JSON: {ex.Message}";
            return false;
        }
        if (acknowledgement is null || !string.Equals(acknowledgement.Schema, CancelAckSchema, StringComparison.Ordinal))
        {
            acknowledgement = null;
            error = "Quartermaster cancel acknowledgement used an unsupported schema.";
            return false;
        }
        if (acknowledgement.IsRejected)
        {
            // The acknowledgement is kept: a caller tells a rejection (retried, warned once) from an unreachable Quartermaster.
            error = $"Quartermaster rejected the cancel ({acknowledgement.ErrorCode ?? acknowledgement.Status}): {acknowledgement.Message}";
            return false;
        }
        error = string.Empty;
        return true;
    }

    public bool HasAutomaticRetrieval
    {
        get
        {
            try
            {
                return adapter.HasSubmitShortages && adapter.HasOperation &&
                       TryGetCapabilities(out var capabilities, out _) &&
                       capabilities!.Capabilities.Contains(AutomaticRetrievalCapability, StringComparer.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool HasShortageSourceRetainers
    {
        get
        {
            try
            {
                return HasAutomaticRetrieval &&
                       TryGetCapabilities(out var capabilities, out _) &&
                       capabilities!.Capabilities.Contains(ShortageSourceRetainersCapability, StringComparer.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool HasDeliveredStow
    {
        get
        {
            try
            {
                return adapter is IQuartermasterDeliveredStowAdapter { HasSubmitDeliveredStow: true, HasGetDeliveredStow: true } &&
                       TryGetCapabilities(out var capabilities, out _) &&
                       capabilities!.Capabilities.Contains(DeliveredStowCapability, StringComparer.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TrySubmitDeliveredStow(QuartermasterDeliveredStowRequest request, out QuartermasterDeliveredStowStatus? acknowledgement, out string error)
    {
        acknowledgement = null;
        if (request is null || string.IsNullOrWhiteSpace(request.RequestId) || request.Owner.LocalContentId == 0 ||
            request.Owner.HomeWorldId == 0 || request.Items is not { Count: > 0 } ||
            request.Items.Any(item => item.ItemId == 0 || item.Quantity < 0 || item.SentDirect < 0 || item.Quantity == 0 && item.SentDirect == 0))
        {
            error = "Delivered-stow request needs a request ID, the owner, and positive item quantities.";
            SetStatus(error);
            return false;
        }
        if (!TryGetCapabilities(out var capabilities, out error))
            return false;
        if (!capabilities!.Capabilities.Contains(DeliveredStowCapability, StringComparer.Ordinal) ||
            adapter is not IQuartermasterDeliveredStowAdapter { HasSubmitDeliveredStow: true } stow)
        {
            error = $"Quartermaster does not advertise {DeliveredStowCapability}.";
            SetStatus(error);
            return false;
        }
        if ((request.AllowForward || request.Items.Any(item => item.SentDirect > 0)) &&
            !capabilities.Capabilities.Contains(DeliveredStowForwardCapability, StringComparer.Ordinal))
        {
            // An older Quartermaster would ignore sentDirect and read the item as a bag delivery.
            error = $"Quartermaster does not advertise {DeliveredStowForwardCapability}; nothing was submitted.";
            SetStatus(error);
            return false;
        }

        var requestJson = JsonSerializer.Serialize(new
        {
            schema = DeliveredStowRequestSchema,
            providerInstanceId = capabilities.ProviderInstanceId,
            requestId = request.RequestId,
            owner = new { localContentId = request.Owner.LocalContentId, homeWorldId = request.Owner.HomeWorldId, characterName = request.Owner.CharacterName },
            // sentDirect and allowForward are omitted unless used, so such a request is the payload it always was.
            items = request.Items.Select(item => new
            {
                itemId = item.ItemId,
                itemName = item.ItemName,
                isHq = item.IsHq,
                quantity = item.Quantity,
                sentDirect = item.SentDirect > 0 ? item.SentDirect : (int?)null,
            }),
            allowForward = request.AllowForward ? true : (bool?)null,
        }, JsonOptions);
        string json;
        try { json = stow.SubmitDeliveredStow(requestJson); }
        catch (Exception ex)
        {
            error = $"Quartermaster delivered-stow submission failed; acceptance is unknown: {ex.Message}";
            SetStatus(error);
            return false;
        }
        if (!TryParseDeliveredStow(json, request.RequestId, capabilities.ProviderInstanceId, out acknowledgement, out error))
        {
            error = $"Quartermaster returned an invalid delivered-stow acknowledgement; acceptance is unknown. {error}";
            SetStatus(error);
            return false;
        }
        SetStatus($"Quartermaster delivered stow {request.RequestId}: {acknowledgement!.Status}.");
        return true;
    }

    public bool TryGetDeliveredStow(string requestId, out QuartermasterDeliveredStowStatus? status, out string error)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            error = "Delivered-stow request ID is required.";
            return false;
        }
        if (!TryGetCapabilities(out var capabilities, out error))
            return false;
        if (adapter is not IQuartermasterDeliveredStowAdapter { HasGetDeliveredStow: true } stow)
        {
            error = $"Quartermaster does not expose {GetDeliveredStowChannel}.";
            return false;
        }
        string json;
        try { json = stow.GetDeliveredStow(requestId); }
        catch (Exception ex)
        {
            error = $"Quartermaster delivered-stow status call failed: {ex.Message}";
            return false;
        }
        return TryParseDeliveredStow(json, requestId, capabilities!.ProviderInstanceId, out status, out error);
    }

    private static bool TryParseDeliveredStow(
        string json,
        string requestId,
        string providerInstanceId,
        out QuartermasterDeliveredStowStatus? status,
        out string error)
    {
        status = null;
        try { status = JsonSerializer.Deserialize<QuartermasterDeliveredStowStatus>(json, JsonOptions); }
        catch (JsonException ex)
        {
            error = $"Delivered-stow response is not valid JSON: {ex.Message}";
            return false;
        }
        if (status is null || string.IsNullOrWhiteSpace(status.Status))
        {
            error = "Delivered-stow response has no status.";
            status = null;
            return false;
        }
        if (!string.Equals(status.RequestId, requestId, StringComparison.Ordinal) && !status.IsRejected)
        {
            error = $"Delivered-stow response names request '{status.RequestId}', expected '{requestId}'.";
            status = null;
            return false;
        }
        if (!string.IsNullOrWhiteSpace(status.ProviderInstanceId) &&
            !string.Equals(status.ProviderInstanceId, providerInstanceId, StringComparison.Ordinal))
        {
            error = "Quartermaster provider changed while the delivered-stow response was being read.";
            status = null;
            return false;
        }
        error = string.Empty;
        return true;
    }
}
