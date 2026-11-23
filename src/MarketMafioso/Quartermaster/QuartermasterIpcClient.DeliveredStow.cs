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
}

public interface IQuartermasterDeliveredStowAdapter
{
    bool HasSubmitDeliveredStow { get; }
    bool HasGetDeliveredStow { get; }
    string SubmitDeliveredStow(string requestJson);
    string GetDeliveredStow(string requestId);
    bool HasCancelRequestedOperation { get; }
    string CancelRequestedOperation(string requestJson);
}

/// <summary>Status: cancelled, stopping, already_terminal, cancel_queued or rejected (ErrorCode names why).</summary>
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

    public bool IsRejected => string.Equals(Status, "rejected", StringComparison.OrdinalIgnoreCase);
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
        pluginInterface.GetIpcSubscriber<string, string>(QuartermasterIpcClient.CancelRequestedOperationChannel));

    public bool HasSubmitDeliveredStow => deliveredStowGates?.Submit.HasFunction == true;
    public bool HasGetDeliveredStow => deliveredStowGates?.Get.HasFunction == true;
    public string SubmitDeliveredStow(string requestJson) => deliveredStowGates!.Submit.InvokeFunc(requestJson);
    public string GetDeliveredStow(string requestId) => deliveredStowGates!.Get.InvokeFunc(requestId);
    public bool HasCancelRequestedOperation => deliveredStowGates?.Cancel.HasFunction == true;
    public string CancelRequestedOperation(string requestJson) => deliveredStowGates!.Cancel.InvokeFunc(requestJson);

    private sealed record DeliveredStowGates(
        ICallGateSubscriber<string, string> Submit,
        ICallGateSubscriber<string, string> Get,
        ICallGateSubscriber<string, string> Cancel);
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
            SetStatus($"Quartermaster returned an invalid delivered-stow acknowledgement; acceptance is unknown. {error}");
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
