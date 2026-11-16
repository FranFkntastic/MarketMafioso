using System;
using System.Collections.Generic;
using System.Linq;
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
    DateTimeOffset UpdatedAt,
    IReadOnlyList<WorkshopMaterialCount>? HandedOver = null,
    int HandoverTotal = 0,
    DateTimeOffset? HandoverUpdatedAt = null)
{
    /// <summary>Material units the sender has verified as traded to the target, per item.</summary>
    public IReadOnlyList<WorkshopMaterialCount> HandedOverItems => HandedOver ?? [];

    /// <summary>Units handed over so far, summed across items.</summary>
    public int HandedOverUnits => HandedOverItems.Sum(item => item.Quantity);
}

/// <summary>A quantity of one material item (game item id).</summary>
public sealed record WorkshopMaterialCount(uint ItemId, int Quantity);

/// <summary>
/// A character's material holdings from its latest Workshop Host inventory report.
/// CapturedAt is null when the host has no report for that character.
/// </summary>
public sealed record WorkshopHoldingsResult(
    bool Success,
    string Message,
    DateTimeOffset? CapturedAt,
    IReadOnlyDictionary<uint, int> Quantities,
    IReadOnlyDictionary<uint, int>? Retainers = null)
{
    public static WorkshopHoldingsResult Fail(string message) => new(false, message, null, new Dictionary<uint, int>());
}

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

    /// <summary>Shares addressed to this character in Accepted state, newest first (for material-received progress).</summary>
    IReadOnlyList<WorkshopHandoffView> Received { get; }

    /// <summary>Publishes or updates (idempotent on sourceJobId + target) a share.</summary>
    Task<WorkshopHandoffResult> OfferAsync(
        Guid sourceJobId,
        string jobName,
        WorkshopAssembler target,
        IReadOnlyList<WorkshopPrepQueueItem> items,
        CancellationToken cancellationToken);

    // expectedUpdatedAt is the version the user acted on; the host refuses the command if the
    // share changed since, so an accept never applies to items the user did not see.
    Task<WorkshopHandoffResult> WithdrawAsync(Guid handoffId, DateTimeOffset? expectedUpdatedAt, CancellationToken cancellationToken);

    /// <summary>Marks accepted on the host. The caller imports the returned handoff's items as a saved job.</summary>
    Task<WorkshopHandoffResult> AcceptAsync(Guid handoffId, DateTimeOffset? expectedUpdatedAt, CancellationToken cancellationToken);

    Task<WorkshopHandoffResult> DeclineAsync(Guid handoffId, DateTimeOffset? expectedUpdatedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Reads another known character's holdings of the given material items from its latest host inventory
    /// report (bags, saddlebag and retainers combined). Items the report does not contain are reported as 0.
    /// </summary>
    Task<WorkshopHoldingsResult> GetHoldingsAsync(
        WorkshopAssembler character,
        IReadOnlyCollection<uint> itemIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sender only. Replaces the share's handed-over ledger with the given totals (set semantics, so a retry
    /// is harmless) and the expected total units. Does not change the share's UpdatedAt version.
    /// </summary>
    Task<WorkshopHandoffResult> ReportHandoverAsync(
        Guid handoffId,
        IReadOnlyList<WorkshopMaterialCount> handedOver,
        int handoverTotal,
        CancellationToken cancellationToken);

    /// <summary>Requests an immediate background refresh of the cached lists.</summary>
    void RequestRefresh();
}
