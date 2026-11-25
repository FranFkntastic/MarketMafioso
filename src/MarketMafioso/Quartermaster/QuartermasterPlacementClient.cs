using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace MarketMafioso.Quartermaster;

// Quartermaster's placement read/edit channels (placementEdit.v1, placementEdit.v2). Every wire shape for them lives in this file.

public interface IQuartermasterPlacementIpcAdapter
{
    bool HasGetPlacement { get; }
    bool HasEditPlacement { get; }
    string GetPlacement(string requestJson);
    string EditPlacement(string requestJson);
}

public sealed class DalamudQuartermasterPlacementIpcAdapter : IQuartermasterPlacementIpcAdapter
{
    private readonly ICallGateSubscriber<string, string> getPlacement;
    private readonly ICallGateSubscriber<string, string> editPlacement;

    public DalamudQuartermasterPlacementIpcAdapter(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        getPlacement = pluginInterface.GetIpcSubscriber<string, string>(QuartermasterPlacementClient.GetPlacementChannel);
        editPlacement = pluginInterface.GetIpcSubscriber<string, string>(QuartermasterPlacementClient.EditPlacementChannel);
    }

    public bool HasGetPlacement => getPlacement.HasFunction;
    public bool HasEditPlacement => editPlacement.HasFunction;
    public string GetPlacement(string requestJson) => getPlacement.InvokeFunc(requestJson);
    public string EditPlacement(string requestJson) => editPlacement.InvokeFunc(requestJson);
}

/// <summary>
/// One retainer column Quartermaster offers on a character. FreeSlots is null while its stock is unobserved;
/// RetainerName is null when Quartermaster has not seen it under that character.
/// </summary>
public sealed record QuartermasterPlacementRetainer(
    ulong RetainerId,
    string? RetainerName,
    bool Assigned,
    bool StockObserved,
    int? FreeSlots,
    DateTimeOffset? ObservedAtUtc);

/// <summary>
/// A character the placement editor can show: the owner first, then by name, once per content id (a character that
/// changed worlds appears under its current world). Retainers are in game order, then any extra retainer an item
/// names there.
/// </summary>
public sealed record QuartermasterPlacementCharacter(
    ulong LocalContentId,
    uint HomeWorldId,
    string CharacterName,
    string HomeWorldName,
    bool IsOwner,
    int? BagFreeSlots,
    ImmutableArray<QuartermasterPlacementRetainer> Retainers)
{
    /// <summary>Characters are matched by content id only, never by world or name.</summary>
    public bool Is(ulong localContentId) => LocalContentId == localContentId;
}

/// <summary>Units of an item one shown character holds: Bags null while unobserved; a retainer absent here holds none.</summary>
public sealed record QuartermasterPlacementHeld(
    ulong LocalContentId,
    int? Bags,
    ImmutableDictionary<ulong, int> Retainers);

/// <summary>
/// One home of an item: a retainer of any character of the account. Rank is the global priority across every
/// character (1 fills first, then 2, ...). MaxUnits caps what the item may stock there (null: as much as fits).
/// </summary>
public sealed record QuartermasterPlacementHome(int Rank, QuartermasterOwnerScope Character, ulong RetainerId, int? MaxUnits)
{
    public bool Is(ulong localContentId, ulong retainerId) => Character.LocalContentId == localContentId && RetainerId == retainerId;
}

/// <summary>
/// One reserve rule (or, requested by id, an item without one: HasRule false, no homes). Quality is NqOnly or HqOnly.
/// Homes are the item's ranked retainers across all characters, ranks 1..N.
/// </summary>
public sealed record QuartermasterPlacementItem(
    uint ItemId,
    string? ItemName,
    string Quality,
    bool HasRule,
    int ReserveUnits,
    ImmutableArray<QuartermasterPlacementHome> Homes,
    bool Consolidate,
    ImmutableArray<QuartermasterPlacementHeld> Held);

public sealed record QuartermasterPlacement(
    string ProviderInstanceId,
    long Revision,
    DateTimeOffset GeneratedAtUtc,
    QuartermasterOwnerScope Owner,
    ImmutableArray<QuartermasterPlacementCharacter> Characters,
    ImmutableArray<QuartermasterPlacementItem> Items);

/// <summary>Placement editor operations, as Quartermaster's toolbar names them.</summary>
public static class QuartermasterPlacementOperations
{
    public const string MakeFirst = "makeFirst";
    public const string AddFallback = "addFallback";
    public const string Remove = "remove";
    public const string Toggle = "toggle";
    /// <summary>placementEdit.v2: caps the item at a retainer (adding it as the last home if absent), or clears the cap.</summary>
    public const string SetCap = "setCap";
    public const string UseCurrentHolders = "useCurrentHolders";
    public const string ClearDestinations = "clearDestinations";

    public static bool NeedsDestination(string operation) =>
        operation is MakeFirst or AddFallback or Remove or Toggle or SetCap;
}

public sealed record QuartermasterPlacementEditItem(uint ItemId, string ItemName, string Quality);

/// <summary>A retainer on the edited character. Deposition homes are always explicit retainers.</summary>
public sealed record QuartermasterPlacementDestination(ulong RetainerId)
{
    public static QuartermasterPlacementDestination Retainer(ulong retainerId) => new(retainerId);
}

/// <summary>
/// What happened to one item. Outcome: applied, unchanged, refused or not_applied. Code: Applied, Unchanged,
/// InvalidItem, InvalidQuality, DuplicateItem, UnknownRetainer, RuleInvalid, NotApplied, or a newer refusal code.
/// </summary>
public sealed record QuartermasterPlacementEditOutcome(
    uint ItemId,
    string ItemName,
    string Quality,
    string Outcome,
    string Code,
    string Message,
    ulong? RetainerId,
    string? Field,
    bool RuleCreated,
    bool RuleRemoved)
{
    public bool IsRefused => string.Equals(Outcome, "refused", StringComparison.Ordinal);
}

/// <summary>Status: applied, partially_applied, unchanged or rejected (ErrorCode names why).</summary>
public sealed record QuartermasterPlacementEditAcknowledgement(
    string RequestId,
    string Status,
    string? ErrorCode,
    string Message,
    QuartermasterOwnerScope? Character,
    ImmutableArray<QuartermasterPlacementEditOutcome> Items)
{
    public bool IsRejected => string.Equals(Status, "rejected", StringComparison.Ordinal);
}

public sealed class QuartermasterPlacementClient
{
    public const string GetPlacementChannel = "Quartermaster.v1.GetPlacement";
    public const string EditPlacementChannel = "Quartermaster.v1.EditPlacement";
    public const string PlacementEditCapability = "placementEdit.v1";
    /// <summary>Global cross-character home ranks, per-home caps (setCap) and no Any space.</summary>
    public const string PlacementEditV2Capability = "placementEdit.v2";
    public const string PlacementRequestSchema = "gooseworks-quartermaster-placement-request/v1";
    public const string PlacementSchema = "gooseworks-quartermaster-placement/v1";
    public const string PlacementEditSchema = "gooseworks-quartermaster-placement-edit/v1";
    public const string PlacementEditAckSchema = "gooseworks-quartermaster-placement-edit-ack/v1";
    /// <summary>Most items one placement request may name.</summary>
    public const int MaxItems = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IQuartermasterPlacementIpcAdapter adapter;

    public QuartermasterPlacementClient(IQuartermasterPlacementIpcAdapter adapter) =>
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    public bool CanEdit
    {
        get
        {
            try { return adapter.HasGetPlacement && adapter.HasEditPlacement; }
            catch { return false; }
        }
    }

    /// <summary>
    /// The placement matrix: every enabled reserve rule of the logged-in owner when <paramref name="items"/> is null,
    /// else exactly those items (at most <see cref="MaxItems"/>; quality NqOnly or HqOnly).
    /// </summary>
    public bool TryGetPlacement(
        IReadOnlyCollection<(uint ItemId, string Quality)>? items,
        out QuartermasterPlacement? placement,
        out string error)
    {
        placement = null;
        var body = items is null
            ? JsonSerializer.Serialize(new { schema = PlacementRequestSchema }, JsonOptions)
            : JsonSerializer.Serialize(new
            {
                schema = PlacementRequestSchema,
                items = items.Select(item => new { itemId = item.ItemId, quality = item.Quality }),
            }, JsonOptions);
        if (!Invoke(() => adapter.HasGetPlacement, () => adapter.GetPlacement(body), "placement", out var json, out error))
            return false;
        PlacementWire? wire;
        try { wire = JsonSerializer.Deserialize<PlacementWire>(json!, JsonOptions); }
        catch (JsonException ex) { error = $"Quartermaster placement was malformed: {ex.Message}"; return false; }
        if (wire is null || !string.Equals(wire.Schema, PlacementSchema, StringComparison.Ordinal))
        {
            error = "Quartermaster placement used an unsupported schema.";
            return false;
        }
        if (!string.Equals(wire.Status, "ok", StringComparison.Ordinal))
        {
            error = $"Quartermaster refused the placement read ({wire.ErrorCode ?? wire.Status ?? "no status"}): {wire.Message}";
            return false;
        }
        if (string.IsNullOrWhiteSpace(wire.ProviderInstanceId))
        {
            error = "Quartermaster placement named no provider instance.";
            return false;
        }

        var owner = Scope(wire.Owner) ?? new QuartermasterOwnerScope(null, null, null, null);
        placement = new QuartermasterPlacement(
            wire.ProviderInstanceId,
            wire.Revision,
            Utc(wire.GeneratedAtUtc) ?? DateTimeOffset.UtcNow,
            owner,
            (wire.Characters ?? [])
                .Where(character => character.LocalContentId != 0 && character.HomeWorldId != 0)
                .DistinctBy(character => character.LocalContentId)
                .Select(character => new QuartermasterPlacementCharacter(
                    character.LocalContentId,
                    character.HomeWorldId,
                    character.CharacterName?.Trim() ?? string.Empty,
                    character.HomeWorldName?.Trim() ?? string.Empty,
                    character.IsOwner,
                    character.Bags is { Observed: true } bags ? bags.FreeSlots : null,
                    (character.Retainers ?? [])
                        .Where(retainer => retainer.RetainerId != 0)
                        .DistinctBy(retainer => retainer.RetainerId)
                        .Select(retainer => new QuartermasterPlacementRetainer(
                            retainer.RetainerId,
                            string.IsNullOrWhiteSpace(retainer.RetainerName) ? null : retainer.RetainerName.Trim(),
                            retainer.Assigned,
                            retainer.StockObserved,
                            retainer.FreeSlots,
                            Utc(retainer.ObservedAtUtc)))
                        .ToImmutableArray()))
                .ToImmutableArray(),
            (wire.Items ?? [])
                .Where(item => item.ItemId != 0 && item.Quality is "NqOnly" or "HqOnly")
                .Select(item => new QuartermasterPlacementItem(
                    item.ItemId,
                    string.IsNullOrWhiteSpace(item.ItemName) ? null : item.ItemName.Trim(),
                    item.Quality!,
                    item.HasRule,
                    Math.Max(0, item.ReserveUnits),
                    HomesOf(item, owner),
                    item.Local?.Consolidate == true,
                    (item.Held ?? [])
                        .Where(held => held.LocalContentId != 0)
                        .Select(held => new QuartermasterPlacementHeld(
                            held.LocalContentId,
                            held.Bags,
                            (held.Retainers ?? [])
                                .Where(entry => entry.RetainerId != 0 && entry.Quantity > 0)
                                .GroupBy(entry => entry.RetainerId)
                                .ToImmutableDictionary(group => group.Key, group => group.Sum(entry => entry.Quantity))))
                        .ToImmutableArray()))
                .ToImmutableArray());
        return true;
    }

    /// <summary>
    /// Applies one placement editor action to these items. <paramref name="character"/> names whose retainer
    /// <paramref name="destination"/> is (null: the owner's); the global operations (use current holders, clear
    /// destinations) take neither. <paramref name="maxUnits"/> is sent only with setCap, where null clears the cap.
    /// True whenever Quartermaster answered with an acknowledgement, whatever its status; false only when the call or
    /// its answer failed.
    /// </summary>
    public bool TryEditPlacement(
        string providerInstanceId,
        QuartermasterOwnerScope owner,
        string operation,
        IReadOnlyList<QuartermasterPlacementEditItem> items,
        (ulong LocalContentId, uint HomeWorldId)? character,
        QuartermasterPlacementDestination? destination,
        int? maxUnits,
        bool allOrNothing,
        out QuartermasterPlacementEditAcknowledgement? acknowledgement,
        out string error)
    {
        acknowledgement = null;
        var requestId = Guid.NewGuid().ToString("N");
        var request = JsonSerializer.SerializeToNode(new PlacementEditWire
        {
            Schema = PlacementEditSchema,
            ProviderInstanceId = providerInstanceId,
            RequestId = requestId,
            Owner = new OwnerRequestWire
            {
                LocalContentId = owner.LocalContentId ?? 0,
                HomeWorldId = owner.HomeWorldId ?? 0,
                CharacterName = owner.CharacterName ?? string.Empty,
            },
            Items = items.Select(item => new EditItemWire { ItemId = item.ItemId, ItemName = item.ItemName, Quality = item.Quality }).ToList(),
            Operation = operation,
            Character = character is { } shown ? new CharacterRefWire { LocalContentId = shown.LocalContentId, HomeWorldId = shown.HomeWorldId } : null,
            Destination = destination is null ? null : new DestinationWire { RetainerId = destination.RetainerId },
            AllOrNothing = allOrNothing,
        }, JsonOptions)!.AsObject();
        // setCap always names its cap, so a cleared cap is an explicit null rather than an absent field.
        if (string.Equals(operation, QuartermasterPlacementOperations.SetCap, StringComparison.Ordinal))
            request["maxUnits"] = maxUnits is { } cap ? JsonValue.Create(Math.Max(0, cap)) : null;
        var body = request.ToJsonString(JsonOptions);
        if (!Invoke(() => adapter.HasEditPlacement, () => adapter.EditPlacement(body), "placement edit", out var json, out error))
            return false;
        EditAckWire? wire;
        try { wire = JsonSerializer.Deserialize<EditAckWire>(json!, JsonOptions); }
        catch (JsonException ex) { error = $"Quartermaster placement edit acknowledgement was malformed: {ex.Message}"; return false; }
        if (wire is null || !string.Equals(wire.Schema, PlacementEditAckSchema, StringComparison.Ordinal))
        {
            error = "Quartermaster placement edit acknowledgement used an unsupported schema.";
            return false;
        }
        // An empty request id is Quartermaster's answer to a body it could not read at all.
        if (!string.IsNullOrEmpty(wire.RequestId) && !string.Equals(wire.RequestId, requestId, StringComparison.Ordinal))
        {
            error = "Quartermaster placement edit acknowledgement did not match the request.";
            return false;
        }
        acknowledgement = new QuartermasterPlacementEditAcknowledgement(
            wire.RequestId ?? string.Empty,
            wire.Status ?? string.Empty,
            string.IsNullOrWhiteSpace(wire.ErrorCode) ? null : wire.ErrorCode,
            wire.Message ?? string.Empty,
            Scope(wire.Character),
            (wire.Items ?? []).Select(item => new QuartermasterPlacementEditOutcome(
                item.ItemId,
                item.ItemName ?? $"Item {item.ItemId}",
                item.Quality ?? string.Empty,
                item.Outcome ?? string.Empty,
                item.Code ?? string.Empty,
                item.Message ?? string.Empty,
                item.RetainerId,
                item.Field,
                item.RuleCreated,
                item.RuleRemoved)).ToImmutableArray());
        return true;
    }

    /// <summary>
    /// The item's global home chain, ranked 1..N. placementEdit.v2 sends it as homes; an older Quartermaster's owner
    /// homes followed by its other character's homes read the same way (its Any space is not a home and is dropped).
    /// </summary>
    private static ImmutableArray<QuartermasterPlacementHome> HomesOf(PlacementItemWire item, QuartermasterOwnerScope owner)
    {
        var homes = item.Homes is { } global
            ? global.OrderBy(home => home.Rank).Select(home => (Character: Scope(home.Character), home.RetainerId, home.MaxUnits))
            : Legacy(item.Local?.Homes, owner).Concat(Legacy(item.Remote?.Homes, Scope(item.Remote?.Character)));
        return homes
            .Where(home => home.RetainerId != 0 && home.Character is { LocalContentId: > 0 })
            .DistinctBy(home => (home.Character!.LocalContentId, home.RetainerId))
            .Select((home, index) => new QuartermasterPlacementHome(index + 1, home.Character!, home.RetainerId,
                home.MaxUnits is { } cap ? Math.Max(0, cap) : null))
            .ToImmutableArray();

        static IEnumerable<(QuartermasterOwnerScope? Character, ulong RetainerId, int? MaxUnits)> Legacy(
            List<RankedHomeWire>? ranked, QuartermasterOwnerScope? character) =>
            (ranked ?? []).OrderBy(home => home.Rank).Select(home => (character, home.RetainerId, (int?)null));
    }

    private static QuartermasterOwnerScope? Scope(CharacterWire? wire) =>
        wire is null ? null : new QuartermasterOwnerScope(wire.LocalContentId, wire.HomeWorldId, wire.CharacterName?.Trim(), wire.HomeWorldName?.Trim());

    // Quartermaster writes UTC DateTimes; a value without an offset is still UTC, never local time.
    private static DateTimeOffset? Utc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Local } local => new DateTimeOffset(local.ToUniversalTime()),
        { } utc => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)),
    };

    private static bool Invoke(Func<bool> available, Func<string> call, string label, out string? json, out string error)
    {
        json = null;
        error = string.Empty;
        try
        {
            if (!available())
            {
                error = $"Quartermaster does not expose {label}.";
                return false;
            }
            json = call();
        }
        catch (Exception ex)
        {
            error = $"Quartermaster {label} call failed: {ex.Message}";
            return false;
        }
        if (string.IsNullOrWhiteSpace(json))
        {
            error = $"Quartermaster returned no {label}.";
            return false;
        }
        return true;
    }

    private sealed class PlacementWire
    {
        public string? Schema { get; init; }
        public string? ProviderInstanceId { get; init; }
        public string? Status { get; init; }
        public string? ErrorCode { get; init; }
        public string? Message { get; init; }
        public long Revision { get; init; }
        public DateTime? GeneratedAtUtc { get; init; }
        public CharacterWire? Owner { get; init; }
        public List<PlacementCharacterWire>? Characters { get; init; }
        public List<PlacementItemWire>? Items { get; init; }
    }

    private sealed class CharacterWire
    {
        public ulong? LocalContentId { get; init; }
        public uint? HomeWorldId { get; init; }
        public string? CharacterName { get; init; }
        public string? HomeWorldName { get; init; }
    }

    private sealed class PlacementCharacterWire
    {
        public ulong LocalContentId { get; init; }
        public uint HomeWorldId { get; init; }
        public string? CharacterName { get; init; }
        public string? HomeWorldName { get; init; }
        public bool IsOwner { get; init; }
        public BagsWire? Bags { get; init; }
        public List<PlacementRetainerWire>? Retainers { get; init; }
    }

    private sealed class BagsWire
    {
        public bool Observed { get; init; }
        public int? FreeSlots { get; init; }
    }

    private sealed class PlacementRetainerWire
    {
        public ulong RetainerId { get; init; }
        public string? RetainerName { get; init; }
        public bool Assigned { get; init; }
        public bool StockObserved { get; init; }
        public int? FreeSlots { get; init; }
        public DateTime? ObservedAtUtc { get; init; }
    }

    private sealed class PlacementItemWire
    {
        public uint ItemId { get; init; }
        public string? ItemName { get; init; }
        public string? Quality { get; init; }
        public bool HasRule { get; init; }
        public int ReserveUnits { get; init; }
        /// <summary>placementEdit.v2's global home chain; absent from an older Quartermaster.</summary>
        public List<GlobalHomeWire>? Homes { get; init; }
        public LocalWire? Local { get; init; }
        public RemoteWire? Remote { get; init; }
        public List<HeldWire>? Held { get; init; }
    }

    private sealed class LocalWire
    {
        public List<RankedHomeWire>? Homes { get; init; }
        public bool Consolidate { get; init; }
    }

    private sealed class GlobalHomeWire
    {
        public int Rank { get; init; }
        public CharacterWire? Character { get; init; }
        public ulong RetainerId { get; init; }
        public int? MaxUnits { get; init; }
    }

    private sealed class RemoteWire
    {
        public CharacterWire? Character { get; init; }
        public List<RankedHomeWire>? Homes { get; init; }
    }

    private sealed class RankedHomeWire
    {
        public int Rank { get; init; }
        public ulong RetainerId { get; init; }
    }

    private sealed class HeldWire
    {
        public ulong LocalContentId { get; init; }
        public int? Bags { get; init; }
        public List<HeldRetainerWire>? Retainers { get; init; }
    }

    private sealed class HeldRetainerWire
    {
        public ulong RetainerId { get; init; }
        public int Quantity { get; init; }
    }

    private sealed class PlacementEditWire
    {
        public string Schema { get; init; } = string.Empty;
        public string ProviderInstanceId { get; init; } = string.Empty;
        public string RequestId { get; init; } = string.Empty;
        public OwnerRequestWire? Owner { get; init; }
        public List<EditItemWire>? Items { get; init; }
        public string Operation { get; init; } = string.Empty;
        public CharacterRefWire? Character { get; init; }
        public DestinationWire? Destination { get; init; }
        public bool AllOrNothing { get; init; }
    }

    private sealed class OwnerRequestWire
    {
        public ulong LocalContentId { get; init; }
        public uint HomeWorldId { get; init; }
        public string CharacterName { get; init; } = string.Empty;
    }

    private sealed class EditItemWire
    {
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public string Quality { get; init; } = string.Empty;
    }

    private sealed class CharacterRefWire
    {
        public ulong LocalContentId { get; init; }
        public uint HomeWorldId { get; init; }
    }

    private sealed class DestinationWire
    {
        public ulong? RetainerId { get; init; }
    }

    private sealed class EditAckWire
    {
        public string? Schema { get; init; }
        public string? RequestId { get; init; }
        public string? Status { get; init; }
        public string? ErrorCode { get; init; }
        public string? Message { get; init; }
        public CharacterWire? Character { get; init; }
        public List<EditOutcomeWire>? Items { get; init; }
    }

    private sealed class EditOutcomeWire
    {
        public uint ItemId { get; init; }
        public string? ItemName { get; init; }
        public string? Quality { get; init; }
        public string? Outcome { get; init; }
        public string? Code { get; init; }
        public string? Message { get; init; }
        public ulong? RetainerId { get; init; }
        public string? Field { get; init; }
        public bool RuleCreated { get; init; }
        public bool RuleRemoved { get; init; }
    }
}
