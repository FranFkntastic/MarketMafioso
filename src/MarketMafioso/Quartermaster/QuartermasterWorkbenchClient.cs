using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace MarketMafioso.Quartermaster;

/// <summary>Quartermaster's stock-source and reserve-item channels used by the acquisition Workbench.</summary>
public interface IQuartermasterWorkbenchIpcAdapter
{
    bool HasStockSources { get; }
    bool HasReserveItems { get; }
    bool HasOpenPlacement { get; }
    bool HasSetReserveLevel { get; }
    string GetStockSources(string requestJson);
    string GetReserveItems();
    string OpenPlacement(string requestJson);
    string SetReserveLevel(string requestJson);
}

public sealed class DalamudQuartermasterWorkbenchIpcAdapter : IQuartermasterWorkbenchIpcAdapter
{
    private readonly ICallGateSubscriber<string, string> stockSources;
    private readonly ICallGateSubscriber<string> reserveItems;
    private readonly ICallGateSubscriber<string, string> openPlacement;
    private readonly ICallGateSubscriber<string, string> setReserveLevel;

    public DalamudQuartermasterWorkbenchIpcAdapter(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        stockSources = pluginInterface.GetIpcSubscriber<string, string>(QuartermasterWorkbenchClient.GetStockSourcesChannel);
        reserveItems = pluginInterface.GetIpcSubscriber<string>(QuartermasterWorkbenchClient.GetReserveItemsChannel);
        openPlacement = pluginInterface.GetIpcSubscriber<string, string>(QuartermasterWorkbenchClient.OpenPlacementChannel);
        setReserveLevel = pluginInterface.GetIpcSubscriber<string, string>(QuartermasterWorkbenchClient.SetReserveLevelChannel);
    }

    public bool HasStockSources => stockSources.HasFunction;
    public bool HasReserveItems => reserveItems.HasFunction;
    public bool HasOpenPlacement => openPlacement.HasFunction;
    public bool HasSetReserveLevel => setReserveLevel.HasFunction;
    public string GetStockSources(string requestJson) => stockSources.InvokeFunc(requestJson);
    public string GetReserveItems() => reserveItems.InvokeFunc();
    public string OpenPlacement(string requestJson) => openPlacement.InvokeFunc(requestJson);
    public string SetReserveLevel(string requestJson) => setReserveLevel.InvokeFunc(requestJson);
}

public sealed record QuartermasterStockItem(uint ItemId, bool Hq, long Quantity);

public sealed record QuartermasterStockBags(DateTimeOffset ObservedAtUtc, ImmutableArray<QuartermasterStockItem> Items);

public sealed record QuartermasterStockRetainer(
    ulong RetainerId,
    string RetainerName,
    DateTimeOffset ObservedAtUtc,
    ImmutableArray<QuartermasterStockItem> Items);

/// <summary>
/// One character's stock as Quartermaster knows it. Bags or Retainers is null when that container
/// was never fully observed: unknown, never zero.
/// </summary>
public sealed record QuartermasterStockCharacter(
    ulong LocalContentId,
    uint HomeWorldId,
    string CharacterName,
    string HomeWorldName,
    bool IsCurrent,
    bool OtherAccount,
    QuartermasterStockBags? Bags,
    ImmutableArray<QuartermasterStockRetainer>? Retainers);

public sealed record QuartermasterStockSources(
    string ProviderInstanceId,
    long Revision,
    ImmutableArray<uint> ItemIds,
    ImmutableArray<QuartermasterStockCharacter> Characters);

public sealed record QuartermasterHomeRetainer(ulong RetainerId, string RetainerName);

/// <summary>An enabled Quartermaster reserve rule. Quality is NqOnly or HqOnly; CountedUnits is null when unknown.</summary>
public sealed record QuartermasterReserveItem(
    uint ItemId,
    string ItemName,
    string Quality,
    int ReserveUnits,
    int? CountedUnits,
    ImmutableArray<ulong> Homes,
    bool AnySpace);

public sealed record QuartermasterReserveItems(
    string ProviderInstanceId,
    long Revision,
    QuartermasterOwnerScope Owner,
    ImmutableArray<QuartermasterHomeRetainer> Retainers,
    ImmutableArray<QuartermasterReserveItem> Items);

public sealed record QuartermasterReserveEditAcknowledgement(string RequestId, string Status, string? ErrorCode, string Message)
{
    public bool IsQueued => string.Equals(Status, "queued", StringComparison.Ordinal);
}

public sealed class QuartermasterWorkbenchClient
{
    public const string GetStockSourcesChannel = "Quartermaster.v1.GetStockSources";
    public const string GetReserveItemsChannel = "Quartermaster.v1.GetReserveItems";
    public const string OpenPlacementChannel = "Quartermaster.v1.OpenPlacement";
    public const string SetReserveLevelChannel = "Quartermaster.v1.SetReserveLevel";
    public const string StockSourcesCapability = "stockSources.v1";
    public const string ReserveItemsCapability = "reserveItems.v1";
    public const string PlacementEditorCapability = "placementEditor.v1";
    public const string ReserveLevelsCapability = "reserveLevels.v1";
    public const string StockSourcesRequestSchema = "gooseworks-quartermaster-stock-sources-request/v1";
    public const string StockSourcesSchema = "gooseworks-quartermaster-stock-sources/v1";
    public const string ReserveItemsSchema = "gooseworks-quartermaster-reserve-items/v1";
    public const string OpenPlacementSchema = "gooseworks-quartermaster-open-placement/v1";
    public const string ReserveLevelSchema = "gooseworks-quartermaster-reserve-level/v1";
    public const string ReserveEditAckSchema = "gooseworks-quartermaster-reserve-edit-ack/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IQuartermasterWorkbenchIpcAdapter adapter;

    public QuartermasterWorkbenchClient(IQuartermasterWorkbenchIpcAdapter adapter) =>
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    public bool TryGetStockSources(IReadOnlyCollection<uint> itemIds, out QuartermasterStockSources? sources, out string error)
    {
        sources = null;
        if (!Invoke(() => adapter.HasStockSources, () => adapter.GetStockSources(JsonSerializer.Serialize(
                new { schema = StockSourcesRequestSchema, itemIds }, JsonOptions)), "stock sources", out var json, out error))
            return false;
        StockSourcesWire? wire;
        try { wire = JsonSerializer.Deserialize<StockSourcesWire>(json!, JsonOptions); }
        catch (JsonException ex) { error = $"Quartermaster stock sources were malformed: {ex.Message}"; return false; }
        if (wire is null || !string.Equals(wire.Schema, StockSourcesSchema, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(wire.ProviderInstanceId))
        {
            error = "Quartermaster stock sources used an unsupported schema.";
            return false;
        }

        sources = new QuartermasterStockSources(
            wire.ProviderInstanceId,
            wire.Revision,
            itemIds.ToImmutableArray(),
            (wire.Characters ?? [])
                .Where(character => character.LocalContentId != 0 && !string.IsNullOrWhiteSpace(character.CharacterName))
                .Select(character => new QuartermasterStockCharacter(
                    character.LocalContentId,
                    character.HomeWorldId,
                    character.CharacterName!.Trim(),
                    character.HomeWorldName?.Trim() ?? string.Empty,
                    character.IsCurrent,
                    character.OtherAccount,
                    character.Bags is { } bags ? new QuartermasterStockBags(Utc(bags.ObservedAtUtc), Items(bags.Items)) : null,
                    character.Retainers?.Select(retainer => new QuartermasterStockRetainer(
                        retainer.RetainerId,
                        retainer.RetainerName?.Trim() ?? string.Empty,
                        Utc(retainer.ObservedAtUtc),
                        Items(retainer.Items))).ToImmutableArray()))
                .ToImmutableArray());
        return true;
    }

    public bool TryGetReserveItems(out QuartermasterReserveItems? reserves, out string error)
    {
        reserves = null;
        if (!Invoke(() => adapter.HasReserveItems, adapter.GetReserveItems, "reserve items", out var json, out error))
            return false;
        ReserveItemsWire? wire;
        try { wire = JsonSerializer.Deserialize<ReserveItemsWire>(json!, JsonOptions); }
        catch (JsonException ex) { error = $"Quartermaster reserve items were malformed: {ex.Message}"; return false; }
        if (wire is null || !string.Equals(wire.Schema, ReserveItemsSchema, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(wire.ProviderInstanceId))
        {
            error = "Quartermaster reserve items used an unsupported schema.";
            return false;
        }

        reserves = new QuartermasterReserveItems(
            wire.ProviderInstanceId,
            wire.Revision,
            new QuartermasterOwnerScope(wire.Owner?.LocalContentId, wire.Owner?.HomeWorldId, wire.Owner?.CharacterName, wire.Owner?.HomeWorldName),
            (wire.Retainers ?? [])
                .Where(retainer => retainer.RetainerId != 0)
                .Select(retainer => new QuartermasterHomeRetainer(retainer.RetainerId, retainer.RetainerName?.Trim() ?? string.Empty))
                .ToImmutableArray(),
            (wire.Items ?? [])
                .Where(item => item.ItemId != 0 && item.Quality is "NqOnly" or "HqOnly")
                .Select(item => new QuartermasterReserveItem(
                    item.ItemId,
                    item.ItemName?.Trim() ?? string.Empty,
                    item.Quality!,
                    Math.Max(0, item.ReserveUnits),
                    item.CountedUnits,
                    (item.Homes ?? []).ToImmutableArray(),
                    item.AnySpace))
                .ToImmutableArray());
        return true;
    }

    /// <summary>Opens Quartermaster's placement editor on these items of the current character. Quality is NqOnly or HqOnly.</summary>
    public bool TryOpenPlacement(
        string providerInstanceId,
        QuartermasterOwnerScope owner,
        IReadOnlyList<(uint ItemId, string ItemName, string Quality)> items,
        out QuartermasterReserveEditAcknowledgement? acknowledgement,
        out string error) =>
        TryEdit(() => adapter.HasOpenPlacement, adapter.OpenPlacement, new
        {
            schema = OpenPlacementSchema,
            providerInstanceId,
            requestId = Guid.NewGuid().ToString("N"),
            owner = Owner(owner),
            items = items.Select(item => new { itemId = item.ItemId, itemName = item.ItemName, quality = item.Quality }),
        }, "placement editor", out acknowledgement, out error);

    public bool TrySetReserveLevel(
        string providerInstanceId,
        QuartermasterOwnerScope owner,
        uint itemId,
        string itemName,
        string quality,
        int reserveUnits,
        out QuartermasterReserveEditAcknowledgement? acknowledgement,
        out string error) =>
        TryEdit(() => adapter.HasSetReserveLevel, adapter.SetReserveLevel, new
        {
            schema = ReserveLevelSchema,
            providerInstanceId,
            requestId = Guid.NewGuid().ToString("N"),
            owner = Owner(owner),
            itemId,
            itemName,
            quality,
            reserveUnits,
        }, "reserve level", out acknowledgement, out error);

    private static object Owner(QuartermasterOwnerScope owner) => new
    {
        localContentId = owner.LocalContentId ?? 0,
        homeWorldId = owner.HomeWorldId ?? 0,
        characterName = owner.CharacterName ?? string.Empty,
    };

    private bool TryEdit(
        Func<bool> available,
        Func<string, string> call,
        object request,
        string label,
        out QuartermasterReserveEditAcknowledgement? acknowledgement,
        out string error)
    {
        acknowledgement = null;
        var body = JsonSerializer.Serialize(request, JsonOptions);
        if (!Invoke(available, () => call(body), label, out var json, out error))
            return false;
        EditAckWire? wire;
        try { wire = JsonSerializer.Deserialize<EditAckWire>(json!, JsonOptions); }
        catch (JsonException ex) { error = $"Quartermaster {label} acknowledgement was malformed: {ex.Message}"; return false; }
        if (wire is null || !string.Equals(wire.Schema, ReserveEditAckSchema, StringComparison.Ordinal))
        {
            error = $"Quartermaster {label} acknowledgement used an unsupported schema.";
            return false;
        }
        acknowledgement = new(wire.RequestId ?? string.Empty, wire.Status ?? string.Empty, wire.ErrorCode, wire.Message ?? string.Empty);
        if (!acknowledgement.IsQueued)
        {
            error = $"Quartermaster rejected the {label} ({acknowledgement.ErrorCode ?? acknowledgement.Status}): {acknowledgement.Message}";
            return false;
        }
        return true;
    }

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

    private static ImmutableArray<QuartermasterStockItem> Items(List<StockItemWire>? items) =>
        (items ?? [])
            .Where(item => item.ItemId != 0 && item.Quantity > 0)
            .Select(item => new QuartermasterStockItem(item.ItemId, item.Hq, item.Quantity))
            .ToImmutableArray();

    // Quartermaster writes UTC DateTimes; a value without an offset is still UTC, never local time.
    private static DateTimeOffset Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
    };

    private sealed class StockSourcesWire
    {
        public string? Schema { get; init; }
        public string? ProviderInstanceId { get; init; }
        public long Revision { get; init; }
        public List<StockCharacterWire>? Characters { get; init; }
    }

    private sealed class StockCharacterWire
    {
        public ulong LocalContentId { get; init; }
        public uint HomeWorldId { get; init; }
        public string? CharacterName { get; init; }
        public string? HomeWorldName { get; init; }
        public bool IsCurrent { get; init; }
        public bool OtherAccount { get; init; }
        public StockBagsWire? Bags { get; init; }
        public List<StockRetainerWire>? Retainers { get; init; }
    }

    private sealed class StockBagsWire
    {
        public DateTime ObservedAtUtc { get; init; }
        public List<StockItemWire>? Items { get; init; }
    }

    private sealed class StockRetainerWire
    {
        public ulong RetainerId { get; init; }
        public string? RetainerName { get; init; }
        public DateTime ObservedAtUtc { get; init; }
        public List<StockItemWire>? Items { get; init; }
    }

    private sealed class StockItemWire
    {
        public uint ItemId { get; init; }
        public bool Hq { get; init; }
        public long Quantity { get; init; }
    }

    private sealed class ReserveItemsWire
    {
        public string? Schema { get; init; }
        public string? ProviderInstanceId { get; init; }
        public long Revision { get; init; }
        public OwnerWire? Owner { get; init; }
        public List<HomeRetainerWire>? Retainers { get; init; }
        public List<ReserveItemWire>? Items { get; init; }
    }

    private sealed class OwnerWire
    {
        public ulong? LocalContentId { get; init; }
        public uint? HomeWorldId { get; init; }
        public string? CharacterName { get; init; }
        public string? HomeWorldName { get; init; }
    }

    private sealed class HomeRetainerWire
    {
        public ulong RetainerId { get; init; }
        public string? RetainerName { get; init; }
    }

    private sealed class ReserveItemWire
    {
        public uint ItemId { get; init; }
        public string? ItemName { get; init; }
        public string? Quality { get; init; }
        public int ReserveUnits { get; init; }
        public int? CountedUnits { get; init; }
        public List<ulong>? Homes { get; init; }
        public bool AnySpace { get; init; }
    }

    private sealed class EditAckWire
    {
        public string? Schema { get; init; }
        public string? RequestId { get; init; }
        public string? Status { get; init; }
        public string? ErrorCode { get; init; }
        public string? Message { get; init; }
    }
}
