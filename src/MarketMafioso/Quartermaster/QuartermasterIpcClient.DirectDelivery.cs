using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace MarketMafioso.Quartermaster;

/// <summary>
/// Delivering goods straight to their homes on another character: where arriving goods would go (PlanArrival), and
/// whether Quartermaster can take a courier transfer into a named retainer and forward what a delivered stow leaves.
/// Any capability missing means the feature is absent and deliveries go to the owner as before.
/// </summary>
public interface IQuartermasterDirectDeliveryClient
{
    /// <summary>
    /// True when Quartermaster advertises reserveRemoteHomes.v1, arrivalPlan.v1, courierTransferToRetainer.v1 and
    /// deliveredStowForward.v1 and exposes PlanArrival. Never throws.
    /// </summary>
    bool HasDirectDelivery { get; }
    /// <summary>True when Quartermaster advertises deliveredStowForward.v1 with delivered stow. Never throws.</summary>
    bool HasDeliveredStowForward { get; }
    bool TryPlanArrival(QuartermasterOwner owner, IReadOnlyList<QuartermasterArrivalRequestItem> items, out QuartermasterArrivalPlan? plan, out string error);
}

public interface IQuartermasterArrivalPlanAdapter
{
    bool HasPlanArrival { get; }
    string PlanArrival(string requestJson);
}

public sealed record QuartermasterArrivalRequestItem(uint ItemId, bool IsHq, int Quantity);

public sealed record QuartermasterArrivalRetainer
{
    public ulong RetainerId { get; init; }
    public string? RetainerName { get; init; }
    public int Units { get; init; }
}

public sealed record QuartermasterArrivalLocal
{
    public int Units { get; init; }
    public IReadOnlyList<QuartermasterArrivalRetainer> Retainers { get; init; } = [];
}

public sealed record QuartermasterArrivalRemote
{
    public QuartermasterStowCharacter? Character { get; init; }
    public int Units { get; init; }
    public IReadOnlyList<QuartermasterArrivalRetainer> Retainers { get; init; } = [];
}

/// <summary>Local homes take what fits first, then Remote (null when the item has no homes on another character).</summary>
public sealed record QuartermasterArrivalItem
{
    public uint ItemId { get; init; }
    public bool IsHq { get; init; }
    public int Quantity { get; init; }
    public QuartermasterArrivalLocal? Local { get; init; }
    public QuartermasterArrivalRemote? Remote { get; init; }
    public int Unplaced { get; init; }
    /// <summary>NoHome, NoRoom, or null.</summary>
    public string? UnplacedReason { get; init; }
}

/// <summary>A plan against room as last observed, not a promise; the delivered stow re-plans against the room it finds.</summary>
public sealed record QuartermasterArrivalPlan
{
    public string Schema { get; init; } = string.Empty;
    public string? ProviderInstanceId { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public QuartermasterStowCharacter? Owner { get; init; }
    public IReadOnlyList<QuartermasterArrivalItem> Items { get; init; } = [];
}

public sealed partial class DalamudQuartermasterIpcAdapter : IQuartermasterArrivalPlanAdapter
{
    private ICallGateSubscriber<string, string>? planArrival;

    private void InitializeDirectDelivery(IDalamudPluginInterface pluginInterface) =>
        planArrival = pluginInterface.GetIpcSubscriber<string, string>(QuartermasterIpcClient.PlanArrivalChannel);

    public bool HasPlanArrival => planArrival?.HasFunction == true;
    public string PlanArrival(string requestJson) => planArrival!.InvokeFunc(requestJson);
}

public sealed partial class QuartermasterIpcClient : IQuartermasterDirectDeliveryClient
{
    public const string ReserveRemoteHomesCapability = "reserveRemoteHomes.v1";
    public const string ArrivalPlanCapability = "arrivalPlan.v1";
    public const string CourierTransferToRetainerCapability = "courierTransferToRetainer.v1";
    public const string PlanArrivalChannel = "Quartermaster.v1.PlanArrival";
    public const string ArrivalPlanRequestSchema = "gooseworks-quartermaster-arrival-plan-request/v1";
    public const string ArrivalPlanSchema = "gooseworks-quartermaster-arrival-plan/v1";

    private static readonly string[] DirectDeliveryCapabilities =
    [
        ReserveRemoteHomesCapability, ArrivalPlanCapability, CourierTransferToRetainerCapability, DeliveredStowForwardCapability,
    ];

    public bool HasDirectDelivery
    {
        get
        {
            try
            {
                return adapter is IQuartermasterArrivalPlanAdapter { HasPlanArrival: true } &&
                       HasDeliveredStow &&
                       TryGetCapabilities(out var capabilities, out _) &&
                       DirectDeliveryCapabilities.All(capability => capabilities!.Capabilities.Contains(capability, StringComparer.Ordinal));
            }
            catch
            {
                return false;
            }
        }
    }

    public bool HasDeliveredStowForward
    {
        get
        {
            try
            {
                return HasDeliveredStow &&
                       TryGetCapabilities(out var capabilities, out _) &&
                       capabilities!.Capabilities.Contains(DeliveredStowForwardCapability, StringComparer.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool TryPlanArrival(QuartermasterOwner owner, IReadOnlyList<QuartermasterArrivalRequestItem> items, out QuartermasterArrivalPlan? plan, out string error)
    {
        plan = null;
        if (owner.LocalContentId == 0 || owner.HomeWorldId == 0 || items is not { Count: > 0 } ||
            items.Any(item => item.ItemId == 0 || item.Quantity <= 0) ||
            items.GroupBy(item => (item.ItemId, item.IsHq)).Any(group => group.Count() > 1))
        {
            error = "Arrival plan needs the owner and positive item quantities, once per quality.";
            return false;
        }
        if (!TryGetCapabilities(out var capabilities, out error))
            return false;
        if (!capabilities!.Capabilities.Contains(ArrivalPlanCapability, StringComparer.Ordinal) ||
            adapter is not IQuartermasterArrivalPlanAdapter { HasPlanArrival: true } arrival)
        {
            error = $"Quartermaster does not advertise {ArrivalPlanCapability}.";
            return false;
        }
        var requestJson = JsonSerializer.Serialize(new
        {
            schema = ArrivalPlanRequestSchema,
            owner = new { localContentId = owner.LocalContentId, homeWorldId = owner.HomeWorldId, characterName = owner.CharacterName },
            items = items.Select(item => new { itemId = item.ItemId, isHq = item.IsHq, quantity = item.Quantity }),
        }, JsonOptions);
        string json;
        try { json = arrival.PlanArrival(requestJson); }
        catch (Exception ex)
        {
            error = $"Quartermaster arrival plan call failed: {ex.Message}";
            return false;
        }
        if (!TryDeserialize<QuartermasterArrivalPlan>(json, out var parsed, out error))
            return false;
        if (!string.Equals(parsed!.Schema, ArrivalPlanSchema, StringComparison.Ordinal))
        {
            error = "Quartermaster arrival plan used an unsupported schema.";
            return false;
        }
        if (!string.Equals(parsed.Status, "planned", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Quartermaster did not plan the arrival ({parsed.ErrorCode ?? parsed.Status}): {parsed.Message}";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(parsed.ProviderInstanceId) &&
            !string.Equals(parsed.ProviderInstanceId, capabilities.ProviderInstanceId, StringComparison.Ordinal))
        {
            error = "Quartermaster provider changed while the arrival plan was being read.";
            return false;
        }
        if (parsed.Owner is { } planned && (planned.LocalContentId != owner.LocalContentId || planned.HomeWorldId != owner.HomeWorldId))
        {
            error = "Quartermaster planned the arrival for a different character.";
            return false;
        }
        // A plan that does not add up is no plan: the caller delivers to the owner as before.
        foreach (var asked in items)
        {
            var answers = parsed.Items.Where(item => item.ItemId == asked.ItemId && item.IsHq == asked.IsHq).ToArray();
            if (answers.Length != 1 || answers[0] is not { } answer ||
                answer.Local is { Units: < 0 } || answer.Unplaced < 0 ||
                answer.Remote is { } remote && (remote.Units < 0 || remote.Character is not { LocalContentId: > 0, HomeWorldId: > 0 } ||
                                                 remote.Retainers.Any(retainer => retainer.RetainerId == 0 || retainer.Units <= 0) ||
                                                 remote.Retainers.Sum(retainer => retainer.Units) != remote.Units) ||
                (answer.Local?.Units ?? 0) + (answer.Remote?.Units ?? 0) > asked.Quantity)
            {
                error = $"Quartermaster's arrival plan for item {asked.ItemId} is inconsistent.";
                return false;
            }
        }
        plan = parsed;
        error = string.Empty;
        return true;
    }
}
