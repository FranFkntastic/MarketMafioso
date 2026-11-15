using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.WorkshopPrep;

/// <summary>A character that builds a share of a split workshop job in its own FC workshop.</summary>
public sealed record WorkshopAssembler(string CharacterName, string World)
{
    public string Key => $"{CharacterName}@{World}";
}

public enum WorkshopHandoffState
{
    Offered,
    Accepted,
    Declined,
    Withdrawn,
}

/// <summary>One share of a split job, as held by the Workshop Host.</summary>
public sealed record WorkshopHandoffView(
    Guid HandoffId,
    Guid SourceJobId,
    string JobName,
    WorkshopAssembler Sender,
    WorkshopAssembler Target,
    IReadOnlyList<WorkshopPrepQueueItem> Items,
    WorkshopHandoffState State,
    DateTimeOffset UpdatedAt);

public sealed record WorkshopHandoffResult(bool Success, string Message, WorkshopHandoffView? Handoff = null)
{
    public static WorkshopHandoffResult Fail(string message) => new(false, message);
}

/// <summary>
/// Workshop Host delivery of split-job shares. UI reads the cached lists every frame; the
/// implementation refreshes them in the background. Every failure message names its cause.
/// </summary>
public interface IWorkshopHandoffService
{
    /// <summary>False when host sync is not configured or the host is unreachable.</summary>
    bool IsAvailable { get; }

    /// <summary>Why delivery is unavailable, or null when available.</summary>
    string? UnavailableReason { get; }

    /// <summary>Characters the host knows for this host login, excluding none (the caller filters).</summary>
    IReadOnlyList<WorkshopAssembler> KnownAssemblers { get; }

    /// <summary>Shares this character sent, newest first.</summary>
    IReadOnlyList<WorkshopHandoffView> Outgoing { get; }

    /// <summary>Shares addressed to this character in Offered state, newest first.</summary>
    IReadOnlyList<WorkshopHandoffView> Incoming { get; }

    /// <summary>Publishes or updates (idempotent on sourceJobId + target) a share.</summary>
    Task<WorkshopHandoffResult> OfferAsync(
        Guid sourceJobId,
        string jobName,
        WorkshopAssembler target,
        IReadOnlyList<WorkshopPrepQueueItem> items,
        CancellationToken cancellationToken);

    Task<WorkshopHandoffResult> WithdrawAsync(Guid handoffId, CancellationToken cancellationToken);

    /// <summary>Marks accepted on the host. The caller imports the items as a saved job on success.</summary>
    Task<WorkshopHandoffResult> AcceptAsync(Guid handoffId, CancellationToken cancellationToken);

    Task<WorkshopHandoffResult> DeclineAsync(Guid handoffId, CancellationToken cancellationToken);

    /// <summary>Requests an immediate background refresh of the cached lists.</summary>
    void RequestRefresh();
}
