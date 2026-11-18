using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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

    private static readonly Regex HostStatus = new(@"Workshop Host HTTP (\d{3})", RegexOptions.CultureInvariant);
    private static readonly Regex HostState = new(@"\bis (\w+); material handover requires", RegexOptions.CultureInvariant);

    private readonly Configuration config;
    private readonly Func<IWorkshopHandoffService> service;
    private readonly Func<WorkshopFrozenQueue, IReadOnlyCollection<uint>> materialIds;
    private readonly Func<WorkshopAssembler?> currentCharacter;
    private readonly Func<bool> tradeActive;
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
        Func<bool> tradeActive,
        IPluginLog log)
    {
        this.config = config;
        this.service = service;
        this.materialIds = materialIds;
        this.currentCharacter = currentCharacter;
        this.tradeActive = tradeActive;
        this.log = log;
    }

    public TradeQueueRecipientBinding? Binding => config.TradeQueueBinding;

    /// <summary>True while the Trade Queue runner is trading (an in-flight record then belongs to the live batch).</summary>
    public bool TradeActive => tradeActive();

    /// <summary>
    /// The binding and its share when the binding applies to the logged-in character. A binding whose share
    /// no longer exists is cleared here and logged (once, since it is then gone).
    /// </summary>
    private (TradeQueueRecipientBinding Binding, WorkshopFrozenQueue Share)? ActiveBinding(string context)
    {
        if (config.TradeQueueBinding is not { } binding)
            return null;
        var current = currentCharacter();
        if (!binding.IsActiveFor(current?.CharacterName, current?.World))
            return null;

        var share = config.FrozenWorkshopQueues.FirstOrDefault(queue => queue.Id == binding.ShareJobId);
        if (share != null)
            return (binding, share);

        log.Warning(
            "[MarketMafioso] Trade Queue binding cleared ({Context}): bound share {Share} ({ShareId}) no longer exists; trades to {Recipient} are no longer restricted or credited.",
            context,
            binding.ShareName,
            binding.ShareJobId,
            binding.RecipientKey);
        config.TradeQueueBinding = null;
        config.Save();
        return null;
    }

    /// <summary>Wire to <see cref="TradeQueueRunner.RecipientGuard"/>.</summary>
    public string? CheckRecipient(TradeQueuePartner partner)
    {
        if (ActiveBinding("trade start") is not { } active)
            return null;
        var (binding, share) = active;
        ClearUnlockedInFlight(share);
        if (share.HandoverInFlight is { } inFlight)
            return $"{UnverifiedTradeText(inFlight)} for {share.Name}; count it as handed over or discard it before trading again.";
        return binding.Mismatch(partner);
    }

    /// <summary>"Unverified trade: N unit(s) may have been handed over to X".</summary>
    public static string UnverifiedTradeText(WorkshopHandoverInFlight inFlight) =>
        $"Unverified trade: {inFlight.Units:N0} unit(s) may have been handed over to {inFlight.Recipient}";

    // ---- ledger --------------------------------------------------------------------------

    /// <summary>
    /// Wire to <see cref="TradeQueueRunner.BatchStarting"/>. Persists the batch's material lines on the bound share
    /// before the trade opens, so a batch that is traded but never verified is not silently lost.
    /// </summary>
    public void OnBatchStarting(TradeQueuePartner partner, TradeQueueBatch batch)
    {
        if (ActiveBinding("batch start") is not { } active || !active.Binding.Matches(partner))
            return;
        var (binding, share) = active;

        var items = MaterialLines(batch, materialIds(share));
        share.HandoverInFlight = items.Count == 0
            ? null
            : new WorkshopHandoverInFlight { Items = items, StartedAt = DateTimeOffset.UtcNow, Recipient = binding.RecipientKey };
        config.Save();
    }

    /// <summary>
    /// Wire to <see cref="TradeQueueRunner.BatchLocking"/>. Marks the in-flight record as possibly traded and saves
    /// before the Ready click, so a reload can tell a batch that never locked from one that might have traded.
    /// </summary>
    public void OnBatchLocking(TradeQueuePartner partner, TradeQueueBatch batch)
    {
        if (ActiveBinding("batch locking") is not { } active || !active.Binding.Matches(partner))
            return;
        if (active.Share.HandoverInFlight is not { Locked: false } inFlight)
            return;
        inFlight.Locked = true;
        config.Save();
    }

    /// <summary>Wire to <see cref="TradeQueueRunner.BatchAbandoned"/>. Drops a record whose batch never locked.</summary>
    public void OnBatchAbandoned(TradeQueuePartner partner, TradeQueueBatch batch)
    {
        if (ActiveBinding("batch abandoned") is { } active)
            ClearUnlockedInFlight(active.Share);
    }

    /// <summary>
    /// Clears an in-flight record whose batch never reached this client's Ready click (nothing can have traded),
    /// unless the Trade Queue is still running it. Returns true when a record was cleared.
    /// </summary>
    public bool ClearUnlockedInFlight(WorkshopFrozenQueue share)
    {
        if (share.HandoverInFlight is not { Locked: false } inFlight || tradeActive())
            return false;
        share.HandoverInFlight = null;
        config.Save();
        log.Information(
            "[MarketMafioso] Cleared unlocked handover batch share={Share} recipient={Recipient} units={Units} startedAt={StartedAt:O}: Ready was never clicked, so nothing traded.",
            share.Name,
            inFlight.Recipient,
            inFlight.Units,
            inFlight.StartedAt);
        return true;
    }

    /// <summary>
    /// Wire to <see cref="TradeQueueRunner.BatchVerified"/>. Credits the bound share's ledger with the batch's
    /// material units and clears its in-flight record. The runner saves the configuration right after this returns.
    /// </summary>
    public void OnBatchVerified(TradeQueuePartner partner, TradeQueueBatch batch)
    {
        var bound = config.TradeQueueBinding;
        if (ActiveBinding("batch verified") is not { } active)
        {
            if (bound != null && config.TradeQueueBinding == bound)
            {
                log.Information(
                    "[MarketMafioso] Handover ledger not credited: the Trade Queue binding belongs to {Sender}, not the logged-in character.",
                    bound.SenderKey ?? "another character");
            }

            return;
        }

        var (binding, share) = active;
        if (!binding.Matches(partner))
        {
            log.Warning(
                "[MarketMafioso] Handover ledger not credited: {Mismatch}",
                binding.Mismatch(partner) ?? "recipient mismatch");
            return;
        }

        share.HandoverInFlight = null;
        var credited = Credit(share, MaterialLines(batch, materialIds(share)));
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

    /// <summary>
    /// Resolves a share's unverified in-flight batch: <paramref name="count"/> credits its lines to the ledger
    /// and marks the report pending; otherwise the record is discarded. Refused while the Trade Queue trades.
    /// </summary>
    public (bool Success, string Message) ResolveInFlight(WorkshopFrozenQueue share, bool count)
    {
        if (share.HandoverInFlight is not { } inFlight)
            return (false, $"{share.Name} has no unverified trade.");
        if (tradeActive())
            return (false, "The Trade Queue is trading; stop it before resolving the unverified trade.");

        share.HandoverInFlight = null;
        if (!count)
        {
            config.Save();
            log.Information(
                "[MarketMafioso] Unverified handover discarded share={Share} recipient={Recipient} units={Units} startedAt={StartedAt:O}.",
                share.Name,
                inFlight.Recipient,
                inFlight.Units,
                inFlight.StartedAt);
            return (true, $"Discarded the unverified trade of {inFlight.Units:N0} unit(s) for {share.Name}.");
        }

        var credited = Credit(share, inFlight.Items);
        share.HandoverReportPending = true;
        config.Save();
        log.Information(
            "[MarketMafioso] Unverified handover counted share={Share} recipient={Recipient} units={Units} startedAt={StartedAt:O}.",
            share.Name,
            inFlight.Recipient,
            credited,
            inFlight.StartedAt);
        RequestReport(share);
        return (true, $"Counted {credited:N0} unit(s) as handed over to {inFlight.Recipient} for {share.Name}.");
    }

    private static List<WorkshopMaterialLedgerEntry> MaterialLines(TradeQueueBatch batch, IReadOnlyCollection<uint> needed) =>
        batch.Lines
            .Where(line => line.ItemId != TradeQueuePlanner.GilItemId && needed.Contains(line.ItemId))
            .GroupBy(line => line.ItemId)
            .Select(group => new WorkshopMaterialLedgerEntry { ItemId = group.Key, Quantity = group.Sum(line => line.Quantity) })
            .Where(entry => entry.Quantity > 0)
            .ToList();

    private static int Credit(WorkshopFrozenQueue share, IEnumerable<WorkshopMaterialLedgerEntry> lines)
    {
        var credited = 0;
        foreach (var line in lines)
        {
            if (line.Quantity <= 0)
                continue;
            var entry = share.HandedOver.FirstOrDefault(item => item.ItemId == line.ItemId);
            if (entry == null)
                share.HandedOver.Add(new WorkshopMaterialLedgerEntry { ItemId = line.ItemId, Quantity = line.Quantity });
            else
                entry.Quantity = checked(entry.Quantity + line.Quantity);
            credited = checked(credited + line.Quantity);
        }

        return credited;
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

    /// <summary>
    /// What the host is told: each item's handed units capped at its due, and the total due. The receiver only
    /// sums, so an over-delivered item must not mask another item's shortfall. Shares without a per-item due
    /// (earlier builds, until the window re-syncs it) report the raw ledger and stored total.
    /// </summary>
    public static (WorkshopMaterialCount[] Ledger, int Total) ReportPayload(WorkshopFrozenQueue share)
    {
        var handed = share.HandedOver
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => Math.Max(0, item.Quantity)));
        if (share.HandoverDue is not { } dueEntries)
        {
            return (handed.Where(item => item.Value > 0).Select(item => new WorkshopMaterialCount(item.Key, item.Value)).ToArray(),
                share.HandoverTotal);
        }

        var due = dueEntries
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => Math.Max(0, item.Quantity)));
        var ledger = due
            .Select(item => new WorkshopMaterialCount(item.Key, Math.Min(handed.GetValueOrDefault(item.Key), item.Value)))
            .Where(item => item.Quantity > 0)
            .ToArray();
        return (ledger, due.Values.Sum());
    }

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
        var (ledger, total) = ReportPayload(share);
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
            if (FinalRefusal(share, result.Message) is { } refusal)
            {
                reportErrors.Remove(shareId);
                share.HandoverReportPending = false;
                if (share.HandoverReportRefusal != refusal)
                {
                    log.Warning(
                        "[MarketMafioso] Handover report for {Share} refused permanently; local ledger kept, not retried. {Refusal} Host: {Reason}",
                        share.Name,
                        refusal,
                        result.Message);
                }

                share.HandoverReportRefusal = refusal;
                config.Save();
                return;
            }

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
        share.HandoverReportRefusal = null;
        var (ledger, total) = ReportPayload(share);
        if (LedgerSignature(ledger, total) != signature)
        {
            // The ledger moved while the report was in flight; send the newer totals.
            RequestReport(share);
            return;
        }

        share.HandoverReportPending = false;
        config.Save();
        service().RequestRefresh();
    }

    /// <summary>
    /// A report the host will never accept: HTTP 404 (share unknown) or 409 (not Offered/Accepted), or, when the
    /// status is not in the message, a share the host has withdrawn or declined. Null for retryable failures.
    /// </summary>
    private static string? FinalRefusal(WorkshopFrozenQueue share, string message)
    {
        var statusMatch = HostStatus.Match(message);
        int? status = statusMatch.Success ? int.Parse(statusMatch.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        var localState = share.HandoffState is WorkshopHandoffState.Withdrawn or WorkshopHandoffState.Declined
            ? share.HandoffState.ToString()
            : null;
        var stateMatch = HostState.Match(message);
        return status switch
        {
            // Only the store's own 404 is final; a bare 404 means a host without the handover route (not yet
            // updated), which must keep retrying so the ledger is relayed once the host is upgraded.
            404 when message.Contains("Unknown handoff", StringComparison.Ordinal) =>
                "Host no longer accepts handover for this share (unknown to this host login).",
            409 => $"Host no longer accepts handover for this share ({(stateMatch.Success ? stateMatch.Groups[1].Value : localState ?? "not Offered or Accepted")}).",
            null when localState != null => $"Host no longer accepts handover for this share ({localState}).",
            _ => null,
        };
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

    /// <summary>Handed units that count toward this item's due: min(HandedOver, Due).</summary>
    public int Credited(bool countTheirs) => Math.Min(Math.Max(0, HandedOver), Due(countTheirs));

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

    /// <summary>What the assembler is counted as holding: its bags, plus observed retainers when included.</summary>
    public static IReadOnlyDictionary<uint, int>? CombineHoldings(
        IReadOnlyList<WorkshopMaterialRequirement> requirements,
        IReadOnlyDictionary<uint, int>? bags,
        IReadOnlyDictionary<uint, int>? retainers,
        bool includeRetainers)
    {
        if (bags == null || !includeRetainers)
            return bags;
        return requirements
            .Select(requirement => requirement.ItemId)
            .Distinct()
            .ToDictionary(itemId => itemId, itemId =>
                (int)Math.Min(int.MaxValue, (long)bags.GetValueOrDefault(itemId) + (retainers?.GetValueOrDefault(itemId) ?? 0)));
    }

    /// <summary>Units the sender owes in total before any handover: Σ max(0, Needed − TheyHold-if-counted).</summary>
    public static int HandoverTotal(IReadOnlyList<WorkshopHandoverRow> rows, bool countTheirs) =>
        rows.Sum(row => row.Due(countTheirs));
}
