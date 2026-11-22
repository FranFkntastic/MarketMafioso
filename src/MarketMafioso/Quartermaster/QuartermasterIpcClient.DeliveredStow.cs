using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    bool TrySubmitShortages(QuartermasterShortageRequest request, out QuartermasterAcknowledgement? acknowledgement, out string error);
    bool TryGetOperation(string operationId, QuartermasterOwnerScope expectedOwner, out QuartermasterOperationStatus? operationStatus, out string error);
    bool TrySubmitDeliveredStow(QuartermasterDeliveredStowRequest request, out QuartermasterDeliveredStowStatus? acknowledgement, out string error);
    bool TryGetDeliveredStow(string requestId, out QuartermasterDeliveredStowStatus? status, out string error);
}

public interface IQuartermasterDeliveredStowAdapter
{
    bool HasSubmitDeliveredStow { get; }
    bool HasGetDeliveredStow { get; }
    string SubmitDeliveredStow(string requestJson);
    string GetDeliveredStow(string requestId);
}

public sealed record QuartermasterDeliveredStowItem(uint ItemId, string ItemName, bool IsHq, int Quantity);

public sealed record QuartermasterDeliveredStowRequest(
    string RequestId,
    QuartermasterOwner Owner,
    IReadOnlyList<QuartermasterDeliveredStowItem> Items);

/// <summary>Code: Planned, NoHome, NoRoom, NotCarried, Stowed, PartlyStowed, NotStowed.</summary>
public sealed record QuartermasterDeliveredStowLine
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public bool IsHq { get; init; }
    public int Requested { get; init; }
    public int Planned { get; init; }
    public int Stowed { get; init; }
    public string Code { get; init; } = string.Empty;
    public IReadOnlyList<string> Destinations { get; init; } = [];
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

    public bool IsRejected => Is("rejected");
    public bool IsNothingToStow => Is(QuartermasterIpcClient.DeliveredStowNothingToStow);
    public bool IsNotFound => Is("not_found");
    public bool IsTerminal => IsRejected || IsNothingToStow || IsNotFound ||
                              Is("succeeded") || Is("partially_succeeded") || Is("indeterminate") || Is("failed") || Is("cancelled");

    private bool Is(string status) => string.Equals(Status, status, StringComparison.OrdinalIgnoreCase);
}

public sealed partial class DalamudQuartermasterIpcAdapter : IQuartermasterDeliveredStowAdapter
{
    private DeliveredStowGates? deliveredStowGates;

    private void InitializeDeliveredStow(IDalamudPluginInterface pluginInterface) => deliveredStowGates = new(
        pluginInterface.GetIpcSubscriber<string, string>(QuartermasterIpcClient.SubmitDeliveredStowChannel),
        pluginInterface.GetIpcSubscriber<string, string>(QuartermasterIpcClient.GetDeliveredStowChannel));

    public bool HasSubmitDeliveredStow => deliveredStowGates?.Submit.HasFunction == true;
    public bool HasGetDeliveredStow => deliveredStowGates?.Get.HasFunction == true;
    public string SubmitDeliveredStow(string requestJson) => deliveredStowGates!.Submit.InvokeFunc(requestJson);
    public string GetDeliveredStow(string requestId) => deliveredStowGates!.Get.InvokeFunc(requestId);

    private sealed record DeliveredStowGates(ICallGateSubscriber<string, string> Submit, ICallGateSubscriber<string, string> Get);
}

public sealed partial class QuartermasterIpcClient : IQuartermasterRunCustodyClient
{
    public const string DeliveredStowCapability = "deliveredStow.v1";
    public const string SubmitDeliveredStowChannel = "Quartermaster.v1.SubmitDeliveredStow";
    public const string GetDeliveredStowChannel = "Quartermaster.v1.GetDeliveredStow";
    public const string DeliveredStowRequestSchema = "gooseworks-quartermaster-delivered-stow/v1";
    public const string DeliveredStowNothingToStow = "nothing_to_stow";

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
            request.Items.Any(item => item.ItemId == 0 || item.Quantity <= 0))
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

        var requestJson = JsonSerializer.Serialize(new
        {
            schema = DeliveredStowRequestSchema,
            providerInstanceId = capabilities.ProviderInstanceId,
            requestId = request.RequestId,
            owner = new { localContentId = request.Owner.LocalContentId, homeWorldId = request.Owner.HomeWorldId, characterName = request.Owner.CharacterName },
            items = request.Items.Select(item => new { itemId = item.ItemId, itemName = item.ItemName, isHq = item.IsHq, quantity = item.Quantity }),
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
