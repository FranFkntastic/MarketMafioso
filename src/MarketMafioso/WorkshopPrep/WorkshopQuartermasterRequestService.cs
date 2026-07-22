using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopQuartermasterRequestService : IDisposable
{
    private readonly Configuration config;
    private readonly QuartermasterIpcClient client;
    private readonly Action saveConfiguration;
    private readonly Func<DateTimeOffset> utcNow;
    private DateTimeOffset nextOperationPollAtUtc;
    private string? activeScopeKey;
    private bool disposed;

    public WorkshopQuartermasterRequestService(
        Configuration config,
        QuartermasterIpcClient client,
        Action saveConfiguration,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.saveConfiguration = saveConfiguration ?? throw new ArgumentNullException(nameof(saveConfiguration));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        client.Changed += OnQuartermasterChanged;
    }

    public string LastStatus { get; private set; } = "No Quartermaster workshop request submitted.";
    public QuartermasterAcknowledgement? LastAcknowledgement { get; private set; }
    public QuartermasterOperationStatus? LastOperation { get; private set; }

    public bool Submit(QuartermasterOwnerScope ownerScope, IReadOnlyList<WorkshopMaterialAvailability> availability)
    {
        ArgumentNullException.ThrowIfNull(ownerScope);
        ArgumentNullException.ThrowIfNull(availability);
        if (!ownerScope.IsAvailable || string.IsNullOrWhiteSpace(ownerScope.CharacterName))
        {
            LastStatus = "Quartermaster request requires current character and home-world identity.";
            return false;
        }
        if (availability.Any(item => item.Shortage > 0 && (item.ItemId == 0 || string.IsNullOrWhiteSpace(item.ItemName))))
        {
            LastStatus = "Quartermaster request requires a resolved item name and stable internal ID for every shortage.";
            return false;
        }

        var items = availability
            .Where(item => item.Shortage > 0)
            .GroupBy(item => item.ItemId)
            .Select(group => new QuartermasterShortageTarget(
                group.Key,
                group.Select(item => item.ItemName).First(name => !string.IsNullOrWhiteSpace(name)),
                group.Sum(item => item.Required),
                group.Sum(item => item.Shortage)))
            .OrderBy(item => item.ItemId)
            .ToImmutableArray();
        if (items.IsDefaultOrEmpty)
        {
            LastStatus = "No workshop shortages need a Quartermaster request.";
            return false;
        }

        var key = ScopeKey(ownerScope);
        var signature = BuildSignature(ownerScope, items);
        config.QuartermasterWorkshopRequests.TryGetValue(key, out var persisted);
        var hasPersistedIdentity = persisted is not null &&
                                   !string.IsNullOrWhiteSpace(persisted.RequestId) &&
                                   !string.IsNullOrWhiteSpace(persisted.OperationId) &&
                                   persisted.SubmittedAtUtc != default;
        var replayingPersistedRequest = hasPersistedIdentity &&
                                        !IsTerminalStatus(persisted!.Status) &&
                                        string.Equals(persisted.Signature, signature, StringComparison.Ordinal);
        if (hasPersistedIdentity &&
            !IsTerminalStatus(persisted!.Status) &&
            !string.Equals(persisted.Signature, signature, StringComparison.Ordinal))
        {
            var operationStatus = string.IsNullOrWhiteSpace(persisted.Status)
                ? "in an uncertain state"
                : $"still {persisted.Status}";
            LastStatus = $"Quartermaster operation {persisted.OperationId} is {operationStatus}; wait for terminal status before replacing its shortages.";
            return false;
        }
        var needsIdentity = !hasPersistedIdentity ||
                            !string.Equals(persisted!.Signature, signature, StringComparison.Ordinal) ||
                            IsTerminalStatus(persisted.Status);
        if (needsIdentity)
        {
            var candidate = new QuartermasterWorkshopRequestState
            {
                LocalContentId = ownerScope.LocalContentId!.Value,
                HomeWorldId = ownerScope.HomeWorldId!.Value,
                CharacterName = ownerScope.CharacterName!,
                HomeWorldName = ownerScope.HomeWorldName,
                Signature = signature,
                RequestId = Guid.NewGuid().ToString("N"),
                OperationId = Guid.NewGuid().ToString("N"),
                SubmittedAtUtc = utcNow().UtcDateTime,
            };
            config.QuartermasterWorkshopRequests[key] = candidate;
            try
            {
                saveConfiguration();
                persisted = candidate;
            }
            catch (Exception ex)
            {
                if (persisted is null)
                    config.QuartermasterWorkshopRequests.Remove(key);
                else
                    config.QuartermasterWorkshopRequests[key] = persisted;
                LastStatus = $"Unable to persist Quartermaster request identity; request was not sent. {ex.Message}";
                return false;
            }
        }

        var request = new QuartermasterShortageRequest(
            persisted!.RequestId,
            persisted.OperationId,
            new DateTimeOffset(DateTime.SpecifyKind(persisted.SubmittedAtUtc, DateTimeKind.Utc)),
            new QuartermasterOwner(
                ownerScope.LocalContentId!.Value,
                ownerScope.HomeWorldId!.Value,
                ownerScope.CharacterName!,
                ownerScope.HomeWorldName),
            items);
        if (!client.TrySubmitShortages(request, out var acknowledgement, out var error))
        {
            LastStatus = error;
            return false;
        }

        var acceptedState = Clone(persisted!);
        acceptedState.OperationId = acknowledgement!.OperationId;
        acceptedState.ProviderInstanceId = acknowledgement.ProviderInstanceId ?? persisted.ProviderInstanceId;
        acceptedState.Revision = replayingPersistedRequest
            ? persisted.Revision
            : acknowledgement.Revision is { } acknowledgementRevision &&
              (persisted.Revision is null || acknowledgementRevision > persisted.Revision)
                ? acknowledgementRevision
                : persisted.Revision;
        acceptedState.Status = replayingPersistedRequest ? persisted.Status : acknowledgement.Status;
        acceptedState.Message = replayingPersistedRequest ? persisted.Message : acknowledgement.Message;
        config.QuartermasterWorkshopRequests[key] = acceptedState;
        try
        {
            saveConfiguration();
        }
        catch (Exception ex)
        {
            config.QuartermasterWorkshopRequests[key] = persisted!;
            LastStatus = $"Quartermaster accepted request {acknowledgement.RequestId}, but MMF could not persist its operation receipt. {ex.Message}";
            return false;
        }

        LastAcknowledgement = acknowledgement;
        LastOperation = null;
        activeScopeKey = key;
        LastStatus = acknowledgement.Accepted
            ? $"Quartermaster request accepted: {acknowledgement.Status}. {acknowledgement.Message}".TrimEnd()
            : $"Quartermaster request rejected: {acknowledgement.Message ?? acknowledgement.Status}";
        nextOperationPollAtUtc = DateTimeOffset.MinValue;
        return acknowledgement.Accepted;
    }

    public void PollOperationIfDue(QuartermasterOwnerScope ownerScope)
    {
        if (disposed || ownerScope is null || !ownerScope.IsAvailable)
            return;
        var key = ScopeKey(ownerScope);
        if (!string.Equals(activeScopeKey, key, StringComparison.Ordinal))
        {
            activeScopeKey = key;
            LastOperation = null;
        }
        if (!config.QuartermasterWorkshopRequests.TryGetValue(key, out var persisted) ||
            string.IsNullOrWhiteSpace(persisted.OperationId) ||
            utcNow() < nextOperationPollAtUtc || IsTerminalStatus(persisted.Status))
            return;

        nextOperationPollAtUtc = utcNow().AddSeconds(2);
        if (!client.TryGetOperation(persisted.OperationId, ownerScope, out var operation, out var error))
        {
            LastStatus = error;
            return;
        }
        if (!string.Equals(operation!.Status, "not_found", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(operation.RequestId, persisted.RequestId, StringComparison.Ordinal))
        {
            LastStatus = "Quartermaster operation response did not match persisted request identity.";
            return;
        }
        if (persisted.Revision is { } persistedRevision)
        {
            if (operation.Revision is not { } operationRevision || operationRevision < persistedRevision)
            {
                LastStatus = "Quartermaster operation response regressed its persisted revision.";
                return;
            }
            if (operationRevision == persistedRevision &&
                (!string.Equals(persisted.Status, operation.Status, StringComparison.Ordinal) ||
                 !string.Equals(persisted.Message, operation.Message, StringComparison.Ordinal) ||
                 !ReceiptsEquivalent(persisted.Receipts, operation.Receipts)))
            {
                LastStatus = "Quartermaster operation response changed state without advancing its revision.";
                return;
            }
        }
        var candidate = Clone(persisted);
        if (!TryMergeReceipts(candidate, operation, out var receiptsChanged, out error))
        {
            LastStatus = error;
            return;
        }

        var operationStatus = operation.Status.Equals("not_found", StringComparison.OrdinalIgnoreCase)
            ? "Quartermaster no longer has this operation in current owner scope; submit shortages again if still needed."
            : $"Quartermaster request {operation.Status}: {operation.Message}".TrimEnd();
        var changed = !string.Equals(persisted.ProviderInstanceId, operation.ProviderInstanceId, StringComparison.Ordinal) ||
                      persisted.Revision != operation.Revision ||
                      !string.Equals(persisted.Status, operation.Status, StringComparison.Ordinal) ||
                      !string.Equals(persisted.Message, operation.Message, StringComparison.Ordinal);
        candidate.ProviderInstanceId = operation.ProviderInstanceId ?? persisted.ProviderInstanceId;
        candidate.Revision = operation.Revision ?? persisted.Revision;
        candidate.Status = operation.Status;
        candidate.Message = operation.Message;
        if (changed || receiptsChanged)
        {
            config.QuartermasterWorkshopRequests[key] = candidate;
            try
            {
                saveConfiguration();
            }
            catch (Exception ex)
            {
                config.QuartermasterWorkshopRequests[key] = persisted;
                LastStatus = $"{operationStatus} MMF could not persist latest Quartermaster operation evidence: {ex.Message}";
                nextOperationPollAtUtc = DateTimeOffset.MinValue;
                return;
            }
        }
        LastOperation = operation;
        LastStatus = operationStatus;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        client.Changed -= OnQuartermasterChanged;
    }

    private void OnQuartermasterChanged(QuartermasterChanged changed)
    {
        if (string.IsNullOrWhiteSpace(changed.OperationId) ||
            config.QuartermasterWorkshopRequests.Values.Any(state => string.Equals(state.OperationId, changed.OperationId, StringComparison.Ordinal)))
            nextOperationPollAtUtc = DateTimeOffset.MinValue;
    }

    private static bool TryMergeReceipts(
        QuartermasterWorkshopRequestState persisted,
        QuartermasterOperationStatus operation,
        out bool changed,
        out string error)
    {
        changed = false;
        if (operation.Receipts.IsDefaultOrEmpty)
        {
            error = string.Empty;
            return true;
        }
        var existing = persisted.Receipts.ToDictionary(receipt => receipt.Revision);
        foreach (var receipt in operation.Receipts)
        {
            if (existing.TryGetValue(receipt.Revision, out var prior) &&
                !ReceiptMatches(prior, receipt))
            {
                error = $"Quartermaster operation receipt revision {receipt.Revision} changed after publication.";
                return false;
            }
            if (!existing.ContainsKey(receipt.Revision))
            {
                persisted.Receipts.Add(ToPersistedReceipt(receipt));
                existing.Add(receipt.Revision, persisted.Receipts[^1]);
                changed = true;
            }
        }
        persisted.Receipts = persisted.Receipts.OrderBy(receipt => receipt.Revision).ToList();
        error = string.Empty;
        return true;
    }

    private static bool ReceiptsEquivalent(
        IReadOnlyList<QuartermasterWorkshopOperationReceipt> persisted,
        ImmutableArray<QuartermasterOperationReceipt> reported)
    {
        if (persisted.Count != reported.Length)
            return false;
        var existing = persisted.ToDictionary(receipt => receipt.Revision);
        var matched = new HashSet<long>();
        return reported.All(receipt =>
            matched.Add(receipt.Revision) &&
            existing.TryGetValue(receipt.Revision, out var prior) &&
            ReceiptMatches(prior, receipt));
    }

    private static bool ReceiptMatches(
        QuartermasterWorkshopOperationReceipt persisted,
        QuartermasterOperationReceipt reported) =>
        persisted.OccurredAtUtc == reported.OccurredAtUtc.UtcDateTime &&
        string.Equals(persisted.Status, reported.Status, StringComparison.Ordinal) &&
        string.Equals(persisted.Code, reported.Code, StringComparison.Ordinal) &&
        string.Equals(persisted.Message, reported.Message, StringComparison.Ordinal) &&
        persisted.ItemId == reported.ItemId &&
        persisted.RetainerId == reported.RetainerId &&
        persisted.Quantity == reported.Quantity;

    private static QuartermasterWorkshopOperationReceipt ToPersistedReceipt(QuartermasterOperationReceipt receipt) => new()
    {
        Revision = receipt.Revision,
        OccurredAtUtc = receipt.OccurredAtUtc.UtcDateTime,
        Status = receipt.Status,
        Code = receipt.Code,
        Message = receipt.Message,
        ItemId = receipt.ItemId,
        RetainerId = receipt.RetainerId,
        Quantity = receipt.Quantity,
    };

    private static QuartermasterWorkshopRequestState Clone(QuartermasterWorkshopRequestState state) => new()
    {
        LocalContentId = state.LocalContentId,
        HomeWorldId = state.HomeWorldId,
        CharacterName = state.CharacterName,
        HomeWorldName = state.HomeWorldName,
        Signature = state.Signature,
        RequestId = state.RequestId,
        OperationId = state.OperationId,
        SubmittedAtUtc = state.SubmittedAtUtc,
        ProviderInstanceId = state.ProviderInstanceId,
        Revision = state.Revision,
        Status = state.Status,
        Message = state.Message,
        Receipts = state.Receipts.Select(receipt => new QuartermasterWorkshopOperationReceipt
        {
            Revision = receipt.Revision,
            OccurredAtUtc = receipt.OccurredAtUtc,
            Status = receipt.Status,
            Code = receipt.Code,
            Message = receipt.Message,
            ItemId = receipt.ItemId,
            RetainerId = receipt.RetainerId,
            Quantity = receipt.Quantity,
        }).ToList(),
    };

    private static bool IsTerminalStatus(string status) =>
        status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("partially_succeeded", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("indeterminate", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("rejected", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("not_found", StringComparison.OrdinalIgnoreCase);

    private static string ScopeKey(QuartermasterOwnerScope owner) =>
        $"{owner.LocalContentId!.Value.ToString(CultureInfo.InvariantCulture)}:{owner.HomeWorldId!.Value.ToString(CultureInfo.InvariantCulture)}";

    private static string BuildSignature(QuartermasterOwnerScope owner, IEnumerable<QuartermasterShortageTarget> items)
    {
        var canonical = string.Join('|', new[]
        {
            owner.LocalContentId!.Value.ToString(CultureInfo.InvariantCulture),
            owner.HomeWorldId!.Value.ToString(CultureInfo.InvariantCulture),
        }.Concat(items.Select(item =>
            $"{item.ItemId.ToString(CultureInfo.InvariantCulture)}:{item.ItemName}:{item.TargetQuantity.ToString(CultureInfo.InvariantCulture)}:{item.ShortageQuantity.ToString(CultureInfo.InvariantCulture)}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
