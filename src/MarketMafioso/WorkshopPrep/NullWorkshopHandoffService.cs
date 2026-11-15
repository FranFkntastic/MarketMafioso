using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.WorkshopPrep;

/// <summary>Handoff service used when Workshop Host delivery is not set up.</summary>
public sealed class NullWorkshopHandoffService : IWorkshopHandoffService
{
    private const string Reason = "Workshop Host delivery is not set up.";

    public bool IsAvailable => false;
    public string? UnavailableReason => Reason;
    public IReadOnlyList<WorkshopAssembler> KnownAssemblers => [];
    public IReadOnlyList<WorkshopHandoffView> Outgoing => [];
    public IReadOnlyList<WorkshopHandoffView> Incoming => [];

    public Task<WorkshopHandoffResult> OfferAsync(
        Guid sourceJobId,
        string jobName,
        WorkshopAssembler target,
        IReadOnlyList<WorkshopPrepQueueItem> items,
        CancellationToken cancellationToken) => Task.FromResult(WorkshopHandoffResult.Fail(Reason));

    public Task<WorkshopHandoffResult> WithdrawAsync(Guid handoffId, DateTimeOffset? expectedUpdatedAt, CancellationToken cancellationToken) =>
        Task.FromResult(WorkshopHandoffResult.Fail(Reason));

    public Task<WorkshopHandoffResult> AcceptAsync(Guid handoffId, DateTimeOffset? expectedUpdatedAt, CancellationToken cancellationToken) =>
        Task.FromResult(WorkshopHandoffResult.Fail(Reason));

    public Task<WorkshopHandoffResult> DeclineAsync(Guid handoffId, DateTimeOffset? expectedUpdatedAt, CancellationToken cancellationToken) =>
        Task.FromResult(WorkshopHandoffResult.Fail(Reason));

    public void RequestRefresh()
    {
    }
}
