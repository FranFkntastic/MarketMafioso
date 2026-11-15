using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using MarketMafioso.TradeQueue;

namespace MarketMafioso.WorkshopPrep;

/// <summary>
/// Holdings lookups for one share's assembler. <paramref name="Result"/> is the last successful lookup (kept
/// through later failures); <paramref name="LastError"/> names why the most recent lookup failed, if it did.
/// </summary>
public sealed record WorkshopHoldingsLookup(
    bool Loading,
    WorkshopHoldingsResult? Result,
    string? LastError,
    DateTimeOffset RequestedAt);

/// <summary>
/// Split-share material handover: the recipient-bound Trade Queue guard, the per-share handed-over ledger
/// (credited from verified Trade Queue batches), the assembler holdings cache, and relaying the ledger to the
/// Workshop Host. Host calls run off the framework thread; <see cref="Pump"/> applies their results.
/// </summary>
public sealed class WorkshopHandoverTracker : IDisposable
{
    public static readonly TimeSpan HoldingsFreshness = TimeSpan.FromHours(24);
    private static readonly TimeSpan ReportRetryInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HoldingsFailureRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HoldingsRefreshAfter = TimeSpan.FromMinutes(10);

    private readonly Configuration config;
    private readonly Func<IWorkshopHandoffService> service;
    private readonly Func<WorkshopFrozenQueue, IReadOnlyCollection<uint>> materialIds;
    private readonly Func<WorkshopAssembler?> currentCharacter;
    private readonly IPluginLog log;
    private readonly ConcurrentQueue<Action> completed = new();
    private readonly Dictionary<Guid, WorkshopHoldingsLookup> holdings = [];
    private readonly HashSet<Guid> reporting = [];
    private readonly Dictionary<Guid, string> reportErrors = [];
    private readonly CancellationTokenSource lifetime = new();
    private DateTimeOffset nextRetryAt;

    public WorkshopHandoverTracker(
        Configuration config,
        Func<IWorkshopHandoffService> service,
        Func<WorkshopFrozenQueue, IReadOnlyCollection<uint>> materialIds,
        Func<WorkshopAssembler?> currentCharacter,
        IPluginLog log)
    {
        this.config = config;
        this.service = service;
        this.materialIds = materialIds;
        this.currentCharacter = currentCharacter;
        this.log = log;
    }

    public TradeQueueRecipientBinding? Binding => config.TradeQueueBinding;

    /// <summary>Wire to <see cref="TradeQueueRunner.RecipientGuard"/>.</summary>
    public string? CheckRecipient(TradeQueuePartner partner) => config.TradeQueueBinding?.Mismatch(partner);

    // ---- ledger --------------------------------------------------------------------------

    /// <summary>
    /// Wire to <see cref="TradeQueueRunner.BatchVerified"/>. Credits the bound share's ledger with the batch's
    /// material units. The runner saves the configuration right after this returns.
    /// </summary>
    public void OnBatchVerified(TradeQueuePartner partner, TradeQueueBatch batch)
    {
        if (config.TradeQueueBinding is not { } binding)
            return;
        if (!binding.Matches(partner))
        {
            log.Warning(
                "[MarketMafioso] Handover ledger not credited: {Mismatch}",
                binding.Mismatch(partner) ?? "recipient mismatch");
            return;
        }

        var share = config.FrozenWorkshopQueues.FirstOrDefault(queue => queue.Id == binding.ShareJobId);
        if (share == null)
        {
            log.Warning(
                "[MarketMafioso] Handover ledger not credited: bound share {Share} ({ShareId}) no longer exists.",
                binding.ShareName,
                binding.ShareJobId);
            return;
        }

        var needed = materialIds(share);
        var credited = 0;
        foreach (var group in batch.Lines
                     .Where(line => line.ItemId != TradeQueuePlanner.GilItemId && needed.Contains(line.ItemId))
                     .GroupBy(line => line.ItemId))
        {
            var quantity = group.Sum(line => line.Quantity);
            if (quantity <= 0)
                continue;
            var entry = share.HandedOver.FirstOrDefault(item => item.ItemId == group.Key);
            if (entry == null)
                share.HandedOver.Add(new WorkshopMaterialLedgerEntry { ItemId = group.Key, Quantity = quantity });
            else
                entry.Quantity = checked(entry.Quantity + quantity);
            credited += quantity;
        }

        if (credited == 0)
            return;

        log.Information(
            "[MarketMafioso] Handover ledger credited share={Share} recipient={Recipient} units={Units} total={Total}.",
            share.Name,
            binding.RecipientKey,
            credited,
            share.HandedOver.Sum(item => item.Quantity));
        // Pending even before the share is delivered: the first report after delivery carries the ledger.
        share.HandoverReportPending = true;
        RequestReport(share);
    }

    /// <summary>Marks the share's ledger and total for relay to the host and sends it now when possible.</summary>
    public void MarkForReport(WorkshopFrozenQueue share)
    {
        share.HandoverReportPending = true;
        RequestReport(share);
    }

    public bool IsReporting(Guid shareId) => reporting.Contains(shareId);

    /// <summary>Why the last relay of this share's ledger failed, or null.</summary>
    public string? ReportError(Guid shareId) => reportErrors.GetValueOrDefault(shareId);

    public void RequestReport(WorkshopFrozenQueue share)
    {
        if (share.HandoffId is not { } handoffId || !share.HandoverReportPending || reporting.Contains(share.Id))
            return;

        var host = service();
        if (!host.IsAvailable)
        {
            reportErrors[share.Id] = host.UnavailableReason ?? "Workshop Host is unavailable.";
            return;
        }

        var shareId = share.Id;
        var ledger = share.HandedOver
            .Where(item => item.Quantity > 0)
            .Select(item => new WorkshopMaterialCount(item.ItemId, item.Quantity))
            .ToArray();
        var total = share.HandoverTotal;
        var signature = LedgerSignature(ledger, total);
        reporting.Add(shareId);
        var token = lifetime.Token;
        _ = Task.Run(async () =>
        {
            WorkshopHandoffResult result;
            try
            {
                result = await host.ReportHandoverAsync(handoffId, ledger, total, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                result = WorkshopHandoffResult.Fail($"Workshop Host handover report failed: {ex.Message}");
            }

            completed.Enqueue(() => ApplyReport(shareId, signature, result));
        });
    }

    private void ApplyReport(Guid shareId, string signature, WorkshopHandoffResult result)
    {
        reporting.Remove(shareId);
        var share = config.FrozenWorkshopQueues.FirstOrDefault(queue => queue.Id == shareId);
        if (share == null)
            return;

        if (!result.Success)
        {
            if (reportErrors.GetValueOrDefault(shareId) != result.Message)
            {
                log.Warning(
                    "[MarketMafioso] Handover report for {Share} failed; ledger kept and retried. {Reason}",
                    share.Name,
                    result.Message);
            }

            reportErrors[shareId] = result.Message;
            return;
        }

        reportErrors.Remove(shareId);
        var current = LedgerSignature(
            share.HandedOver.Where(item => item.Quantity > 0).Select(item => new WorkshopMaterialCount(item.ItemId, item.Quantity)),
            share.HandoverTotal);
        if (current != signature)
        {
            // The ledger moved while the report was in flight; send the newer totals.
            RequestReport(share);
            return;
        }

        share.HandoverReportPending = false;
        config.Save();
        service().RequestRefresh();
    }

    private static string LedgerSignature(IEnumerable<WorkshopMaterialCount> ledger, int total) =>
        string.Join(',', ledger.OrderBy(item => item.ItemId).Select(item => $"{item.ItemId}:{item.Quantity}")) + $"|{total}";

    // ---- holdings ------------------------------------------------------------------------

    public WorkshopHoldingsLookup? Holdings(Guid shareId) => holdings.GetValueOrDefault(shareId);

    /// <summary>
    /// Fetches the share assembler's holdings when forced, when nothing is cached, after a failure (every 30s),
    /// or when the cached lookup is older than 10 minutes. Cheap to call every frame.
    /// </summary>
    public void RequestHoldings(WorkshopFrozenQueue share, bool force)
    {
        if (WorkshopSavedJobHandoff.Target(share) is not { } target)
            return;
        var now = DateTimeOffset.UtcNow;
        if (holdings.TryGetValue(share.Id, out var existing))
        {
            if (existing.Loading)
                return;
            var due = existing.Result == null || existing.LastError != null ? HoldingsFailureRetry : HoldingsRefreshAfter;
            if (!force && now - existing.RequestedAt < due)
                return;
        }

        var host = service();
        if (!host.IsAvailable)
        {
            holdings[share.Id] = new(false, existing?.Result, host.UnavailableReason ?? "Workshop Host is unavailable.", now);
            return;
        }

        var shareId = share.Id;
        var itemIds = materialIds(share).ToArray();
        holdings[shareId] = new(true, existing?.Result, existing?.LastError, now);
        var token = lifetime.Token;
        _ = Task.Run(async () =>
        {
            WorkshopHoldingsResult result;
            try
            {
                result = await host.GetHoldingsAsync(target, itemIds, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                result = WorkshopHoldingsResult.Fail($"Workshop Host holdings lookup failed: {ex.Message}");
            }

            completed.Enqueue(() =>
            {
                var previous = holdings.GetValueOrDefault(shareId)?.Result;
                holdings[shareId] = result.Success
                    ? new(false, result, null, now)
                    : new(false, previous, result.Message, now);
            });
        });
    }

    // ---- frame pump ----------------------------------------------------------------------

    /// <summary>Applies finished host calls and retries unacknowledged ledgers. Framework thread, every frame.</summary>
    public void Pump()
    {
        while (completed.TryDequeue(out var apply))
            apply();

        var now = DateTimeOffset.UtcNow;
        if (now < nextRetryAt)
            return;
        nextRetryAt = now + ReportRetryInterval;

        // Only the sender may report a share's ledger; another character on this install leaves it pending.
        if (currentCharacter() is not { } current)
            return;
        foreach (var share in config.FrozenWorkshopQueues)
        {
            if (share.HandoverReportPending && share.HandoffId != null && IsSentBy(share, current))
                RequestReport(share);
        }
    }

    private static bool IsSentBy(WorkshopFrozenQueue share, WorkshopAssembler character) =>
        string.Equals(share.HandoffSenderName, character.CharacterName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(share.HandoffSenderWorld, character.World, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

/// <summary>One material row of a share's handover.</summary>
public sealed record WorkshopHandoverRow(
    uint ItemId,
    string ItemName,
    int Needed,
    int? TheyHold,
    int HandedOver,
    int InBags)
{
    /// <summary>What the assembler needs from the sender for this item: Needed less what it holds (when counted).</summary>
    public int Due(bool countTheirs) => Math.Max(0, Needed - (countTheirs ? TheyHold ?? 0 : 0));

    public int Owed(bool countTheirs) => Math.Max(0, Due(countTheirs) - HandedOver);

    public int Short(bool countTheirs) => Math.Max(0, Owed(countTheirs) - InBags);

    /// <summary>What "Hand over" queues for this item.</summary>
    public int Give(bool countTheirs) => Math.Min(Owed(countTheirs), Math.Max(0, InBags));
}

public static class WorkshopHandoverCalculator
{
    public static IReadOnlyList<WorkshopHandoverRow> Build(
        IReadOnlyList<WorkshopMaterialRequirement> requirements,
        IReadOnlyList<WorkshopMaterialLedgerEntry> handedOver,
        IReadOnlyDictionary<uint, int>? theyHold,
        IReadOnlyDictionary<uint, int> bags)
    {
        var ledger = handedOver
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => Math.Max(0, item.Quantity)));
        return requirements
            .Select(requirement => new WorkshopHandoverRow(
                requirement.ItemId,
                requirement.ItemName,
                requirement.Quantity,
                theyHold == null ? null : theyHold.GetValueOrDefault(requirement.ItemId),
                ledger.GetValueOrDefault(requirement.ItemId),
                bags.GetValueOrDefault(requirement.ItemId)))
            .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Units the sender owes in total before any handover: Σ max(0, Needed − TheyHold-if-counted).</summary>
    public static int HandoverTotal(IReadOnlyList<WorkshopHandoverRow> rows, bool countTheirs) =>
        rows.Sum(row => row.Due(countTheirs));
}
