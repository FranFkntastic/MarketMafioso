using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace MarketMafioso.Quartermaster;

public interface IQuartermasterIpcAdapter
{
    bool HasCapabilities { get; }
    bool HasSnapshot { get; }
    bool HasSubmitShortages { get; }
    bool HasOperation { get; }
    string GetCapabilities();
    string GetSnapshot();
    string SubmitShortages(string requestJson);
    string GetOperation(string operationId);
    void SubscribeChanged(Action<string> handler);
    void UnsubscribeChanged(Action<string> handler);
}

public sealed class DalamudQuartermasterIpcAdapter : IQuartermasterIpcAdapter
{
    private readonly ICallGateSubscriber<string> capabilities;
    private readonly ICallGateSubscriber<string> snapshot;
    private readonly ICallGateSubscriber<string, string> submitShortages;
    private readonly ICallGateSubscriber<string, string> operation;
    private readonly ICallGateSubscriber<string, object> changed;

    public DalamudQuartermasterIpcAdapter(IDalamudPluginInterface pluginInterface)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        capabilities = pluginInterface.GetIpcSubscriber<string>(QuartermasterIpcClient.GetCapabilitiesChannel);
        snapshot = pluginInterface.GetIpcSubscriber<string>(QuartermasterIpcClient.GetSnapshotChannel);
        submitShortages = pluginInterface.GetIpcSubscriber<string, string>(QuartermasterIpcClient.SubmitShortagesChannel);
        operation = pluginInterface.GetIpcSubscriber<string, string>(QuartermasterIpcClient.GetOperationChannel);
        changed = pluginInterface.GetIpcSubscriber<string, object>(QuartermasterIpcClient.ChangedChannel);
    }

    public bool HasCapabilities => capabilities.HasFunction;
    public bool HasSnapshot => snapshot.HasFunction;
    public bool HasSubmitShortages => submitShortages.HasFunction;
    public bool HasOperation => operation.HasFunction;
    public string GetCapabilities() => capabilities.InvokeFunc();
    public string GetSnapshot() => snapshot.InvokeFunc();
    public string SubmitShortages(string requestJson) => submitShortages.InvokeFunc(requestJson);
    public string GetOperation(string operationId) => operation.InvokeFunc(operationId);
    public void SubscribeChanged(Action<string> handler) => changed.Subscribe(handler);
    public void UnsubscribeChanged(Action<string> handler) => changed.Unsubscribe(handler);
}

public sealed class QuartermasterIpcClient : IDisposable
{
    public const string GetCapabilitiesChannel = "Quartermaster.v1.GetCapabilities";
    public const string GetSnapshotChannel = "Quartermaster.v1.GetSnapshot";
    public const string SubmitShortagesChannel = "Quartermaster.v1.SubmitShortages";
    public const string GetOperationChannel = "Quartermaster.v1.GetOperation";
    public const string ChangedChannel = "Quartermaster.v1.Changed";
    public const string CapabilitiesSchema = "gooseworks-quartermaster-capabilities/v1";
    public const string SnapshotSchema = "gooseworks-quartermaster-snapshot/v1";
    public const string ShortageRequestSchema = "gooseworks-quartermaster-shortages/v1";
    public const string AcknowledgementSchema = "gooseworks-quartermaster-shortages-acknowledgement/v1";
    public const string OperationSchema = "gooseworks-quartermaster-operation/v1";
    public const string ChangedSchema = "gooseworks-quartermaster-changed/v1";
    public const string AutomaticRetrievalCapability = "automaticRetrieval";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object sync = new();
    private readonly IQuartermasterIpcAdapter adapter;
    private QuartermasterSnapshot? cachedSnapshot;
    private string? observedProviderInstanceId;
    private long invalidatedThroughRevision = -1;
    private bool subscribed;
    private bool disposed;
    private string lastStatus = "Quartermaster has not been queried.";

    public QuartermasterIpcClient(IQuartermasterIpcAdapter adapter)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        try
        {
            adapter.SubscribeChanged(OnChanged);
            subscribed = true;
        }
        catch (Exception ex)
        {
            lastStatus = $"Quartermaster change notifications are unavailable: {ex.Message}";
        }
    }

    public event Action<QuartermasterChanged>? Changed;

    public string LastStatus
    {
        get
        {
            lock (sync)
                return lastStatus;
        }
    }

    public bool IsChangedSubscribed
    {
        get
        {
            lock (sync)
                return subscribed && !disposed;
        }
    }

    public bool HasCachedSnapshot
    {
        get
        {
            lock (sync)
                return cachedSnapshot is not null && !disposed;
        }
    }

    public bool TryGetCapabilities(out QuartermasterCapabilities? capabilities, out string error)
    {
        capabilities = null;
        error = string.Empty;
        if (!EnsureUsable(out error))
            return false;

        bool available;
        try
        {
            available = adapter.HasCapabilities;
        }
        catch (Exception ex)
        {
            return FailUnavailable($"Quartermaster availability check failed: {ex.Message}", out error);
        }

        if (!available)
            return FailUnavailable("Quartermaster is not loaded or does not expose capabilities v1.", out error);

        string json;
        try
        {
            json = adapter.GetCapabilities();
        }
        catch (Exception ex)
        {
            return FailUnavailable($"Quartermaster capabilities call failed: {ex.Message}", out error);
        }

        if (!TryParseCapabilities(json, out capabilities, out error))
        {
            ClearSnapshot(error);
            return false;
        }

        lock (sync)
        {
            if (!string.Equals(observedProviderInstanceId, capabilities!.ProviderInstanceId, StringComparison.Ordinal))
            {
                observedProviderInstanceId = capabilities.ProviderInstanceId;
                cachedSnapshot = null;
                invalidatedThroughRevision = -1;
            }
            else if (cachedSnapshot is not null && capabilities.Revision < cachedSnapshot.Revision)
            {
                cachedSnapshot = null;
                lastStatus = error = "Quartermaster revision regressed within one provider instance.";
                return false;
            }

            if (capabilities.Revision < invalidatedThroughRevision)
            {
                lastStatus = error = "Quartermaster capabilities have not reached the latest Changed revision.";
                return false;
            }

            lastStatus = $"Quartermaster available (revision {capabilities.Revision}).";
        }

        return true;
    }

    public bool TryGetSnapshot(out QuartermasterSnapshot? snapshot, out string error)
    {
        snapshot = null;
        error = string.Empty;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!TryGetCapabilities(out var before, out error))
                return false;

            lock (sync)
            {
                if (cachedSnapshot is not null &&
                    cachedSnapshot.ProviderInstanceId == before!.ProviderInstanceId &&
                    cachedSnapshot.Revision == before.Revision &&
                    cachedSnapshot.Revision >= invalidatedThroughRevision)
                {
                    snapshot = cachedSnapshot;
                    return true;
                }
            }

            bool snapshotAvailable;
            try
            {
                snapshotAvailable = adapter.HasSnapshot;
            }
            catch (Exception ex)
            {
                return FailUnavailable($"Quartermaster snapshot availability check failed: {ex.Message}", out error);
            }

            if (!snapshotAvailable)
                return FailUnavailable("Quartermaster is loaded but does not expose snapshot v1.", out error);

            string json;
            try
            {
                json = adapter.GetSnapshot();
            }
            catch (Exception ex)
            {
                return FailUnavailable($"Quartermaster snapshot call failed: {ex.Message}", out error);
            }

            if (!TryParseSnapshot(json, out var candidate, out error))
            {
                ClearSnapshot(error);
                return false;
            }

            if (!TryGetCapabilities(out var after, out error))
                return false;

            if (!string.Equals(candidate!.ProviderInstanceId, before!.ProviderInstanceId, StringComparison.Ordinal) ||
                !string.Equals(candidate.ProviderInstanceId, after!.ProviderInstanceId, StringComparison.Ordinal) ||
                candidate.Revision < before.Revision ||
                candidate.Revision != after.Revision)
            {
                ClearSnapshot("Quartermaster changed while its snapshot was being read; retrying.");
                continue;
            }

            lock (sync)
            {
                if (candidate.Revision < invalidatedThroughRevision)
                    continue;
                cachedSnapshot = candidate;
                lastStatus = $"Quartermaster snapshot available (revision {candidate.Revision}, {candidate.Retainers.Length} retainers).";
                snapshot = candidate;
                return true;
            }
        }

        error = "Quartermaster changed repeatedly while its snapshot was being read.";
        ClearSnapshot(error);
        return false;
    }

    public bool TrySubmitShortages(
        QuartermasterShortageRequest request,
        out QuartermasterAcknowledgement? acknowledgement,
        out string error)
    {
        acknowledgement = null;
        error = string.Empty;
        if (!ValidateRequest(request, out error))
        {
            SetStatus(error);
            return false;
        }
        if (!TryGetCapabilities(out var capabilities, out error))
            return false;
        return TrySubmitShortagesCore(request, capabilities!, out acknowledgement, out error);
    }

    internal bool TrySubmitShortages(
        QuartermasterShortageRequest request,
        QuartermasterCapabilities capabilities,
        out QuartermasterAcknowledgement? acknowledgement,
        out string error)
    {
        acknowledgement = null;
        error = string.Empty;
        if (!ValidateRequest(request, out error))
        {
            SetStatus(error);
            return false;
        }
        ArgumentNullException.ThrowIfNull(capabilities);
        return TrySubmitShortagesCore(request, capabilities, out acknowledgement, out error);
    }

    private bool TrySubmitShortagesCore(
        QuartermasterShortageRequest request,
        QuartermasterCapabilities capabilities,
        out QuartermasterAcknowledgement? acknowledgement,
        out string error)
    {
        acknowledgement = null;
        error = string.Empty;
        bool available;
        try
        {
            available = adapter.HasSubmitShortages;
        }
        catch (Exception ex)
        {
            return FailUnavailable($"Quartermaster shortage availability check failed: {ex.Message}", out error);
        }
        if (!available)
            return FailUnavailable("Quartermaster does not expose shortage submission v1.", out error);

        var executeImmediately = request.ExecuteImmediately &&
                                 capabilities.Capabilities.Contains(AutomaticRetrievalCapability, StringComparer.Ordinal);
        var requestJson = JsonSerializer.Serialize(ToWire(request, capabilities.ProviderInstanceId, executeImmediately), JsonOptions);
        string acknowledgementJson;
        try
        {
            acknowledgementJson = adapter.SubmitShortages(requestJson);
        }
        catch (Exception ex)
        {
            error = $"Quartermaster shortage submission failed; acceptance is unknown: {ex.Message}";
            SetStatus(error);
            return false;
        }

        if (!TryParseAcknowledgement(acknowledgementJson, request.RequestId, request.OperationId, out acknowledgement, out error))
        {
            SetStatus($"Quartermaster returned an invalid shortage acknowledgement; acceptance is unknown. {error}");
            return false;
        }
        if (!string.IsNullOrWhiteSpace(acknowledgement!.ProviderInstanceId) &&
            !string.Equals(acknowledgement.ProviderInstanceId, capabilities.ProviderInstanceId, StringComparison.Ordinal))
        {
            error = "Quartermaster provider changed during shortage submission; acceptance is unknown.";
            SetStatus(error);
            acknowledgement = null;
            return false;
        }

        acknowledgement = acknowledgement with { ExecuteImmediately = executeImmediately };

        SetStatus(acknowledgement.Accepted
            ? acknowledgement.ExecuteImmediately
                ? $"Quartermaster immediate retrieval requested as operation {acknowledgement.OperationId}."
                : $"Quartermaster request {acknowledgement.RequestId} accepted as operation {acknowledgement.OperationId}."
            : acknowledgement.ExecuteImmediately
                ? $"Quartermaster rejected immediate retrieval: {acknowledgement.Message ?? acknowledgement.Status}"
                : $"Quartermaster rejected request {acknowledgement.RequestId}: {acknowledgement.Message ?? acknowledgement.Status}");
        return true;
    }

    public bool TryGetOperation(
        string operationId,
        QuartermasterOwnerScope expectedOwner,
        out QuartermasterOperationStatus? operationStatus,
        out string error)
    {
        operationStatus = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(operationId))
        {
            error = "Quartermaster operation ID is required.";
            return false;
        }
        if (expectedOwner is null || !expectedOwner.IsAvailable)
        {
            error = "Quartermaster operation owner scope is required.";
            return false;
        }
        if (!TryGetCapabilities(out var capabilities, out error))
            return false;

        bool available;
        try
        {
            available = adapter.HasOperation;
        }
        catch (Exception ex)
        {
            return FailUnavailable($"Quartermaster operation availability check failed: {ex.Message}", out error);
        }
        if (!available)
            return FailUnavailable("Quartermaster does not expose operation status v1.", out error);

        string json;
        try
        {
            json = adapter.GetOperation(operationId);
        }
        catch (Exception ex)
        {
            error = $"Quartermaster operation call failed: {ex.Message}";
            SetStatus(error);
            return false;
        }

        if (!TryParseOperation(json, operationId, out operationStatus, out error))
        {
            SetStatus(error);
            return false;
        }
        if (!string.IsNullOrWhiteSpace(operationStatus!.ProviderInstanceId) &&
            !string.Equals(operationStatus.ProviderInstanceId, capabilities!.ProviderInstanceId, StringComparison.Ordinal))
        {
            error = "Quartermaster provider changed while operation status was being read.";
            SetStatus(error);
            operationStatus = null;
            return false;
        }
        if (!expectedOwner.Matches(operationStatus.Owner))
        {
            error = "Quartermaster operation response owner does not match the current character.";
            SetStatus(error);
            operationStatus = null;
            return false;
        }

        SetStatus($"Quartermaster operation {operationId}: {operationStatus.Status}. {operationStatus.Message}".TrimEnd());
        return true;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
        }

        if (subscribed)
        {
            try
            {
                adapter.UnsubscribeChanged(OnChanged);
            }
            catch
            {
                // Disposal must not prevent the rest of the plugin from unloading.
            }
        }

        lock (sync)
        {
            subscribed = false;
            cachedSnapshot = null;
        }
    }

    private void OnChanged(string json)
    {
        if (!TryParseChanged(json, out var changed, out var error))
        {
            ClearSnapshot($"Quartermaster sent an invalid Changed notification. {error}");
            return;
        }

        lock (sync)
        {
            if (disposed)
                return;
            if (!string.Equals(observedProviderInstanceId, changed!.ProviderInstanceId, StringComparison.Ordinal))
            {
                observedProviderInstanceId = changed.ProviderInstanceId;
                invalidatedThroughRevision = changed.Revision;
            }
            else
            {
                invalidatedThroughRevision = Math.Max(invalidatedThroughRevision, changed.Revision);
            }
            cachedSnapshot = null;
            lastStatus = $"Quartermaster changed at revision {changed.Revision}; snapshot invalidated.";
        }

        try
        {
            Changed?.Invoke(changed!);
        }
        catch
        {
            // Consumers cannot compromise IPC invalidation.
        }
    }

    private bool EnsureUsable(out string error)
    {
        lock (sync)
        {
            if (!disposed)
            {
                error = string.Empty;
                return true;
            }
        }

        error = "Quartermaster IPC client is disposed.";
        return false;
    }

    private bool FailUnavailable(string message, out string error)
    {
        error = message;
        ClearSnapshot(message);
        return false;
    }

    private void ClearSnapshot(string status)
    {
        lock (sync)
        {
            cachedSnapshot = null;
            lastStatus = status;
        }
    }

    private void SetStatus(string status)
    {
        lock (sync)
            lastStatus = status;
    }

    private static bool TryParseCapabilities(
        string json,
        out QuartermasterCapabilities? capabilities,
        out string error)
    {
        capabilities = null;
        if (!TryDeserialize(json, out QuartermasterCapabilitiesWire? wire, out error))
            return false;
        if (!string.Equals(wire!.Schema, CapabilitiesSchema, StringComparison.Ordinal))
        {
            error = $"Unsupported Quartermaster capabilities schema '{wire.Schema ?? "(missing)"}'.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(wire.ProviderInstanceId) || wire.Revision < 0)
        {
            error = "Quartermaster capabilities omitted a valid provider instance or revision.";
            return false;
        }

        DateTimeOffset? generatedAt = null;
        if (!string.IsNullOrWhiteSpace(wire.GeneratedAtUtc))
        {
            if (!TryParseTimestamp(wire.GeneratedAtUtc, out var parsed))
            {
                error = "Quartermaster capabilities contained an invalid generatedAtUtc timestamp.";
                return false;
            }
            generatedAt = parsed;
        }

        capabilities = new(wire.ProviderInstanceId, wire.Revision, generatedAt)
        {
            Capabilities = NormalizeStrings(wire.Capabilities),
        };
        return true;
    }

    private static bool TryParseSnapshot(
        string json,
        out QuartermasterSnapshot? snapshot,
        out string error)
    {
        snapshot = null;
        if (!TryDeserialize(json, out QuartermasterSnapshotWire? wire, out error))
            return false;
        if (!string.Equals(wire!.Schema, SnapshotSchema, StringComparison.Ordinal))
        {
            error = $"Unsupported Quartermaster snapshot schema '{wire.Schema ?? "(missing)"}'.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(wire.ProviderInstanceId) || wire.Revision < 0)
        {
            error = "Quartermaster snapshot omitted a valid provider instance or revision.";
            return false;
        }
        if (!TryParseTimestamp(wire.GeneratedAtUtc, out var generatedAt))
        {
            error = "Quartermaster snapshot omitted a valid generatedAtUtc timestamp.";
            return false;
        }

        var ownerWire = wire.Owner ?? wire.CurrentOwner;
        if (ownerWire is null || ownerWire.LocalContentId == 0 || ownerWire.HomeWorldId == 0 ||
            string.IsNullOrWhiteSpace(ownerWire.CharacterName))
        {
            error = "Quartermaster snapshot omitted a valid current owner.";
            return false;
        }
        var owner = new QuartermasterOwner(
            ownerWire.LocalContentId,
            ownerWire.HomeWorldId,
            ownerWire.CharacterName,
            ownerWire.HomeWorldName);

        var retainers = ImmutableArray.CreateBuilder<QuartermasterRetainerSnapshot>();
        foreach (var retainerWire in wire.Retainers ?? [])
        {
            if (retainerWire.RetainerId == 0 || string.IsNullOrWhiteSpace(retainerWire.RetainerName) ||
                !TryParseTimestamp(retainerWire.ObservedAtUtc ?? retainerWire.LastUpdated, out var observedAt))
            {
                error = "Quartermaster snapshot contained a retainer without stable identity or observation time.";
                return false;
            }

            if (!TryParseOptionalTimestamp(retainerWire.GilObservedAtUtc, out var gilObservedAt) ||
                !TryParseOptionalTimestamp(retainerWire.ListingsObservedAtUtc, out var listingsObservedAt))
            {
                error = "Quartermaster snapshot contained an invalid optional-source timestamp.";
                return false;
            }
            var bags = ImmutableArray.CreateBuilder<QuartermasterBagSnapshot>();
            foreach (var bag in retainerWire.Bags ?? [])
            {
                if (!TryParseOptionalTimestamp(bag.ObservedAtUtc, out var bagObservedAt))
                {
                    error = "Quartermaster snapshot contained an invalid bag observation timestamp.";
                    return false;
                }
                bags.Add(new QuartermasterBagSnapshot(
                    bag.BagName ?? string.Empty,
                    bag.Location,
                    (bag.Items ?? []).Where(item => item.ItemId != 0 && item.Quantity != 0).Select(item =>
                    new QuartermasterItemSnapshot(
                        item.ItemId,
                        item.ItemName,
                        item.ItemType,
                        item.Quantity,
                        item.IsHq,
                        item.Condition,
                        item.ContainerKey,
                        item.SlotIndex,
                        item.ConditionPercent,
                        item.Equipped)).ToImmutableArray())
                {
                    ObservedAtUtc = bagObservedAt,
                });
            }
            var listingWires = retainerWire.Listings ?? retainerWire.MarketListings ?? [];
            var listings = listingWires
                .Where(listing => listing.ItemId != 0 && listing.Quantity != 0)
                .Select(listing => new QuartermasterListingSnapshot(
                    listing.ItemId,
                    listing.ItemName,
                    listing.ItemType,
                    listing.Quantity,
                    listing.IsHq,
                    listing.Condition,
                    listing.ContainerKey,
                    listing.SlotIndex,
                    listing.ConditionPercent,
                    listing.UnitPrice ?? listing.Price,
                    listing.ListedAt))
                .ToImmutableArray();
            retainers.Add(new(
                retainerWire.RetainerId,
                retainerWire.RetainerName,
                observedAt,
                retainerWire.Gil,
                bags.ToImmutable(),
                listings)
            {
                RequestedSources = NormalizeSources(retainerWire.RequestedSources),
                ObservedSources = NormalizeSources(retainerWire.ObservedSources),
                GilObservedAtUtc = gilObservedAt,
                ListingsObservedAtUtc = listingsObservedAt,
            });
        }

        snapshot = new(wire.ProviderInstanceId, wire.Revision, generatedAt, owner, retainers.ToImmutable())
        {
            PlayerRequestedSources = NormalizeSources(wire.PlayerStorage?.RequestedSources),
            PlayerObservedSources = NormalizeSources(wire.PlayerStorage?.ObservedSources),
        };
        return true;
    }

    private static ImmutableArray<string> NormalizeSources(IEnumerable<string>? sources) =>
        (sources ?? [])
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(source => source, StringComparer.Ordinal)
            .ToImmutableArray();

    private static ImmutableArray<string> NormalizeStrings(IEnumerable<string>? values) =>
        (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();

    private static bool TryParseAcknowledgement(
        string json,
        string expectedRequestId,
        string expectedOperationId,
        out QuartermasterAcknowledgement? acknowledgement,
        out string error)
    {
        acknowledgement = null;
        if (!TryDeserialize(json, out QuartermasterAcknowledgementWire? wire, out error))
            return false;
        if (!string.Equals(wire!.Schema, AcknowledgementSchema, StringComparison.Ordinal))
        {
            error = $"Unsupported Quartermaster shortage acknowledgement schema '{wire.Schema ?? "(missing)"}'.";
            return false;
        }
        if (!string.Equals(wire.RequestId, expectedRequestId, StringComparison.Ordinal) ||
            !string.Equals(wire.OperationId, expectedOperationId, StringComparison.Ordinal))
        {
            error = "Quartermaster shortage acknowledgement did not match the request identity.";
            return false;
        }

        var status = string.IsNullOrWhiteSpace(wire.Status)
            ? wire.Accepted == false ? "Rejected" : "Accepted"
            : wire.Status;
        var accepted = status.Equals("Accepted", StringComparison.OrdinalIgnoreCase) ||
                       status.Equals("Queued", StringComparison.OrdinalIgnoreCase);
        var rejected = status.Equals("Rejected", StringComparison.OrdinalIgnoreCase);
        if (!accepted && !rejected)
        {
            error = $"Quartermaster shortage acknowledgement returned unknown status '{status}'.";
            return false;
        }
        if (wire.Accepted is not null && wire.Accepted != accepted)
        {
            error = "Quartermaster shortage acknowledgement status conflicts with its accepted flag.";
            return false;
        }
        acknowledgement = new(
            wire.RequestId!,
            wire.OperationId!,
            wire.ProviderInstanceId,
            wire.Revision,
            accepted,
            status,
            wire.Message);
        return true;
    }

    private static bool TryParseOperation(
        string json,
        string expectedOperationId,
        out QuartermasterOperationStatus? operation,
        out string error)
    {
        operation = null;
        if (!TryDeserialize(json, out QuartermasterOperationStatusWire? wire, out error))
            return false;
        if (!string.Equals(wire!.Schema, OperationSchema, StringComparison.Ordinal))
        {
            error = $"Unsupported Quartermaster operation schema '{wire.Schema ?? "(missing)"}'.";
            return false;
        }
        if (!string.Equals(wire.OperationId, expectedOperationId, StringComparison.Ordinal))
        {
            error = "Quartermaster operation response did not match the requested operation ID.";
            return false;
        }
        var status = wire.Status ?? wire.State;
        if (string.IsNullOrWhiteSpace(status) || wire.Revision is null or < 0)
        {
            error = "Quartermaster operation response omitted status or revision.";
            return false;
        }
        if (!TryParseOptionalTimestamp(wire.UpdatedAtUtc, out var updatedAt) ||
            !TryParseOptionalTimestamp(wire.CompletedAtUtc, out var completedAt))
        {
            error = "Quartermaster operation response contained an invalid timestamp.";
            return false;
        }
        if (wire.Owner is null || wire.Owner.LocalContentId == 0 || wire.Owner.HomeWorldId == 0)
        {
            error = "Quartermaster operation response omitted stable owner scope.";
            return false;
        }
        var owner = new QuartermasterOwner(
            wire.Owner.LocalContentId,
            wire.Owner.HomeWorldId,
            wire.Owner.CharacterName ?? string.Empty,
            wire.Owner.HomeWorldName);
        var receipts = ImmutableArray.CreateBuilder<QuartermasterOperationReceipt>();
        foreach (var receipt in wire.Receipts ?? [])
        {
            if (receipt.Revision <= 0 || !TryParseTimestamp(receipt.OccurredAtUtc, out var occurredAt) ||
                string.IsNullOrWhiteSpace(receipt.Status) || string.IsNullOrWhiteSpace(receipt.Code) || receipt.Message is null)
            {
                error = "Quartermaster operation response contained an invalid immutable receipt.";
                return false;
            }
            receipts.Add(new(
                receipt.Revision,
                occurredAt,
                receipt.Status,
                receipt.Code,
                receipt.Message,
                receipt.ItemId,
                receipt.RetainerId,
                receipt.Quantity));
        }

        operation = new(
            wire.OperationId!,
            wire.RequestId,
            wire.ProviderInstanceId,
            wire.Revision,
            owner,
            status,
            wire.Message,
            updatedAt,
            completedAt,
            receipts.ToImmutable());
        return true;
    }

    private static bool TryParseChanged(
        string json,
        out QuartermasterChanged? changed,
        out string error)
    {
        changed = null;
        if (!TryDeserialize(json, out QuartermasterChangedWire? wire, out error))
            return false;
        if (!string.Equals(wire!.Schema, ChangedSchema, StringComparison.Ordinal))
        {
            error = $"Unsupported Quartermaster Changed schema '{wire.Schema ?? "(missing)"}'.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(wire.ProviderInstanceId) || wire.Revision < 0)
        {
            error = "Quartermaster Changed notification omitted provider identity or revision.";
            return false;
        }

        changed = new(wire.ProviderInstanceId, wire.Revision, wire.OperationId);
        return true;
    }

    private static bool ValidateRequest(QuartermasterShortageRequest request, out string error)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.OperationId))
        {
            error = "Quartermaster request and operation IDs are required.";
            return false;
        }
        if (request.Owner.LocalContentId == 0 || request.Owner.HomeWorldId == 0 ||
            string.IsNullOrWhiteSpace(request.Owner.CharacterName))
        {
            error = "Quartermaster request requires current owner identity.";
            return false;
        }
        if (request.SubmittedAtUtc == default)
        {
            error = "Quartermaster request submission time is required.";
            return false;
        }
        if (request.Items.IsDefaultOrEmpty || request.Items.Any(item =>
                item.ItemId == 0 || string.IsNullOrWhiteSpace(item.ItemName) || item.TargetQuantity <= 0 ||
                item.ShortageQuantity <= 0 || item.ShortageQuantity > item.TargetQuantity))
        {
            error = "Quartermaster request items require names, stable internal IDs, and valid target and shortage quantities.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static QuartermasterShortageRequestWire ToWire(
        QuartermasterShortageRequest request,
        string providerInstanceId,
        bool executeImmediately) => new()
    {
        Schema = ShortageRequestSchema,
        ProviderInstanceId = providerInstanceId,
        RequestId = request.RequestId,
        OperationId = request.OperationId,
        SubmittedAtUtc = request.SubmittedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        Owner = new QuartermasterOwnerWire
        {
            LocalContentId = request.Owner.LocalContentId,
            HomeWorldId = request.Owner.HomeWorldId,
            CharacterName = request.Owner.CharacterName,
            HomeWorldName = request.Owner.HomeWorldName,
        },
        ExecuteImmediately = executeImmediately ? true : null,
        Items = request.Items.Select(item => new QuartermasterShortageTargetWire
        {
            ItemId = item.ItemId,
            ItemName = item.ItemName,
            TargetQuantity = item.TargetQuantity,
            ShortageQuantity = item.ShortageQuantity,
        }).ToList(),
    };

    private static bool TryDeserialize<T>(string json, out T? value, out string error)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Quartermaster returned an empty JSON response.";
            return false;
        }
        try
        {
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (value is null)
            {
                error = "Quartermaster returned JSON null.";
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Quartermaster returned malformed JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);

    private static bool TryParseOptionalTimestamp(string? value, out DateTimeOffset? timestamp)
    {
        timestamp = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (!TryParseTimestamp(value, out var parsed))
            return false;
        timestamp = parsed;
        return true;
    }
}
