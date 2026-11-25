using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketMafioso.Quartermaster;

/// <summary>What a pending cancel withdraws in Quartermaster.</summary>
public enum QuartermasterCancelKind
{
    /// <summary>A retrieval, elemental deposit or stow this plugin requested (Quartermaster.v1.CancelRequestedOperation).</summary>
    RequestedOperation,
    /// <summary>The courier forward of a delivered stow (Quartermaster.v1.CancelDeliveredStowForward).</summary>
    DeliveredStowForward,
    /// <summary>A courier run this plugin abandoned: stopped once if it is running, then cleared.</summary>
    CourierRun,
}

/// <summary>One cancel Quartermaster has not confirmed yet. Persisted, so it survives Clear, session deletion and reload.</summary>
public sealed record QuartermasterPendingCancel
{
    public QuartermasterCancelKind Kind { get; init; }
    /// <summary>The request id (requested operation, forward) or the courier run id.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>The request id Quartermaster's clear is issued under (courier runs only).</summary>
    public string? ClearRequestId { get; init; }
    public string Reason { get; init; } = string.Empty;
    /// <summary>What created it, e.g. "cross-regional session …" or "workshop restock for …".</summary>
    public string Owner { get; init; } = string.Empty;
    public DateTimeOffset FirstAttemptUtc { get; init; }
    public DateTimeOffset NextAttemptUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    /// <summary>Quartermaster answered stopping: kept until a later answer shows the operation or run terminal.</summary>
    public bool Stopping { get; set; }
    /// <summary>Courier runs: the one stop was sent (never repeated, so a run someone resumed is not stopped again).</summary>
    public bool StopSent { get; set; }
    /// <summary>Courier runs: Quartermaster accepted the clear; kept until the run is gone from its status.</summary>
    public bool ClearAccepted { get; set; }
    /// <summary>
    /// Courier runs this plugin did not start (a foreign run Clear abandons while it is parked): never stopped. Found
    /// running (someone resumed it), the entry is dropped and the run left alone.
    /// </summary>
    public bool NeverStop { get; set; }
    /// <summary>Quartermaster's last answer (cancelled, stopping, cancel_queued, rejected, ...); null until one arrives.</summary>
    public string? LastStatus { get; set; }
    /// <summary>Consecutive cancel_queued answers, and when the first and the latest of them arrived.</summary>
    public int QueuedAnswers { get; set; }
    public DateTimeOffset? FirstQueuedUtc { get; set; }
    public DateTimeOffset? LastQueuedUtc { get; set; }
    /// <summary>A rejected answer was logged once at Warning; later rejections of this entry are not.</summary>
    public bool RejectedWarned { get; set; }
}

/// <summary>The answer to a cancel attempt. Settled: nothing more to do. Queued: kept in the outbox and retried.</summary>
public sealed record QuartermasterCancelOutcome(string Status, string Message, bool Settled, bool Queued, Guid? RunId = null);

/// <summary>
/// Durable cancels for Quartermaster work this plugin gave up on. A give-up path calls <see cref="Cancel"/>: it tries
/// once at once and, unless Quartermaster settled it, keeps retrying on <see cref="Tick"/> with backoff until the answer
/// is cancelled, already_terminal, not_found or (after stopping) terminal. An entry is dropped after
/// <see cref="EntryLifetime"/> with a Warning naming it; Quartermaster expires unstarted requested operations itself.
/// A Quartermaster that lacks the capability keeps today's behaviour: nothing is retried and a Warning names it once.
/// </summary>
public sealed class QuartermasterCancelOutbox
{
    public const string FileName = "quartermaster-cancel-outbox.json";
    public static readonly TimeSpan EntryLifetime = TimeSpan.FromHours(2);
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StoppingPollInterval = TimeSpan.FromSeconds(5);
    /// <summary>
    /// Two cancel_queued answers at least this far apart mean Quartermaster neither holds nor is still persisting
    /// anything under the id: its persistence queue drains on the framework thread, and an operation persisted after
    /// the first answer is cancelled by that queued cancel and then answers already_terminal, not cancel_queued.
    /// </summary>
    private static readonly TimeSpan QueuedConfirmSpacing = TimeSpan.FromSeconds(5);
    private const string StatusCancelQueued = "cancel_queued";
    private const string CodeRunNotCurrent = "RunNotCurrent";
    private const string CodeCourierAlreadyRunning = "CourierAlreadyRunning";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IQuartermasterRunCustodyClient custody;
    private readonly IQuartermasterCourierClient courier;
    private readonly string? path;
    private readonly Action<string> warn;
    private readonly Action<string> info;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly object sync = new();
    private readonly HashSet<string> warnedMissing = new(StringComparer.Ordinal);
    /// <summary>How the latest entries settled (this load only), so a run waiting on a cancel learns how it ended.</summary>
    private readonly Dictionary<(QuartermasterCancelKind Kind, string Id), string> settledStatuses = new();
    private readonly Queue<(QuartermasterCancelKind Kind, string Id)> settledOrder = new();
    private const int SettledMemory = 64;
    private List<QuartermasterPendingCancel> entries = [];
    private DateTimeOffset nextDueUtc = DateTimeOffset.MaxValue;
    private bool ticking;

    public QuartermasterCancelOutbox(
        IQuartermasterRunCustodyClient custody,
        IQuartermasterCourierClient courier,
        string? stateDirectory,
        Action<string>? warn = null,
        Action<string>? info = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.custody = custody ?? throw new ArgumentNullException(nameof(custody));
        this.courier = courier ?? throw new ArgumentNullException(nameof(courier));
        this.warn = warn ?? (_ => { });
        this.info = info ?? (_ => { });
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        path = string.IsNullOrWhiteSpace(stateDirectory) ? null : Path.Combine(stateDirectory, FileName);
        Load();
    }

    public IReadOnlyList<QuartermasterPendingCancel> Pending
    {
        get
        {
            lock (sync)
                return entries.ToList();
        }
    }

    /// <summary>True when a cancel for this kind and id is still waiting on Quartermaster.</summary>
    public bool IsPending(QuartermasterCancelKind kind, string id)
    {
        lock (sync)
            return entries.Any(entry => entry.Kind == kind && string.Equals(entry.Id, id, StringComparison.Ordinal));
    }

    /// <summary>
    /// True when nothing can run in Quartermaster under this id any more as far as this outbox knows: no cancel is
    /// pending for it, or Quartermaster has answered cancel_queued at least twice <see cref="QueuedConfirmSpacing"/>
    /// apart (it holds no operation under the id and is not persisting one). The queued cancel is still re-sent.
    /// </summary>
    public bool IsWithdrawn(QuartermasterCancelKind kind, string id)
    {
        lock (sync)
            return entries.FirstOrDefault(entry => entry.Kind == kind && string.Equals(entry.Id, id, StringComparison.Ordinal)) is not { } entry ||
                   QueuedConfirmed(entry);
    }

    /// <summary>
    /// How Quartermaster settled the latest cancel of this kind and id during this load (cancelled, already_terminal,
    /// not_found, unsupported, ...); null when none settled or it is pending again.
    /// </summary>
    public string? SettledStatus(QuartermasterCancelKind kind, string id)
    {
        lock (sync)
            return settledStatuses.TryGetValue((kind, id), out var status) ? status : null;
    }

    /// <summary>The pending entry for this kind and id, as a copy; null when none is pending.</summary>
    public QuartermasterPendingCancel? Find(QuartermasterCancelKind kind, string id)
    {
        lock (sync)
            return entries.FirstOrDefault(entry => entry.Kind == kind && string.Equals(entry.Id, id, StringComparison.Ordinal)) is { } entry
                ? entry with { }
                : null;
    }

    /// <summary>Retries a pending entry within <see cref="FirstRetryDelay"/> instead of its backoff: something now waits on it.</summary>
    public void Expedite(QuartermasterCancelKind kind, string id)
    {
        var soon = utcNow() + FirstRetryDelay;
        lock (sync)
        {
            if (entries.FirstOrDefault(entry => entry.Kind == kind && string.Equals(entry.Id, id, StringComparison.Ordinal)) is not { } entry ||
                entry.NextAttemptUtc <= soon)
                return;
            entry.NextAttemptUtc = soon;
            if (soon < nextDueUtc)
                nextDueUtc = soon;
        }
    }

    private static bool QueuedConfirmed(QuartermasterPendingCancel entry) =>
        entry.LastStatus == StatusCancelQueued && entry.QueuedAnswers >= 2 &&
        entry is { FirstQueuedUtc: { } first, LastQueuedUtc: { } last } && last - first >= QueuedConfirmSpacing;

    /// <summary>
    /// Withdraws <paramref name="id"/> in Quartermaster now and keeps retrying it unless Quartermaster settled it.
    /// Never throws. A courier run the caller already asked to stop passes <paramref name="stopSent"/>.
    /// </summary>
    public QuartermasterCancelOutcome Cancel(QuartermasterCancelKind kind, string id, string reason, string owner,
        string? clearRequestId = null, bool stopSent = false, bool clearAccepted = false, bool neverStop = false)
    {
        if (string.IsNullOrWhiteSpace(id))
            return new("skipped", "Nothing to cancel: no id.", Settled: true, Queued: false);
        QuartermasterPendingCancel entry;
        var now = utcNow();
        lock (sync)
        {
            entry = entries.FirstOrDefault(existing => existing.Kind == kind && string.Equals(existing.Id, id, StringComparison.Ordinal))
                    ?? new QuartermasterPendingCancel
                    {
                        Kind = kind,
                        Id = id,
                        ClearRequestId = clearRequestId,
                        Reason = reason,
                        Owner = owner,
                        FirstAttemptUtc = now,
                        NextAttemptUtc = now,
                        StopSent = stopSent,
                        ClearAccepted = clearAccepted,
                        NeverStop = neverStop,
                    };
            if (!entries.Contains(entry))
                entries.Add(entry);
            settledStatuses.Remove((kind, id));
        }
        var outcome = Attempt(entry, now);
        Settle(entry, outcome);
        Save();
        return outcome;
    }

    /// <summary>Retries every due entry. Call from the framework tick.</summary>
    public void Tick()
    {
        if (ticking || utcNow() < nextDueUtc)
            return;
        ticking = true;
        try
        {
            var now = utcNow();
            List<QuartermasterPendingCancel> due;
            lock (sync)
                due = entries.Where(entry => now >= entry.NextAttemptUtc || now - entry.FirstAttemptUtc >= EntryLifetime).ToList();
            if (due.Count == 0)
                return;
            foreach (var entry in due)
            {
                if (now - entry.FirstAttemptUtc >= EntryLifetime)
                {
                    Remove(entry);
                    warn($"[MarketMafioso] QuartermasterCancelExpired: gave up withdrawing {Describe(entry)} after {EntryLifetime.TotalHours:N0} hours " +
                         $"({entry.Attempts} attempt(s)); last answer: {entry.LastStatus ?? "none"}; last error: {entry.LastError ?? "none"}." +
                         (entry.LastStatus == StatusCancelQueued
                             ? " Quartermaster never held an operation under this id."
                             : entry.Kind == QuartermasterCancelKind.CourierRun
                                 ? " Clear it in Quartermaster's Courier pane if it is still there."
                                 : " Quartermaster expires a requested operation that never starts."));
                    continue;
                }
                QuartermasterCancelOutcome outcome;
                try { outcome = Attempt(entry, now); }
                catch (Exception ex) { outcome = new("failed", ex.Message, Settled: false, Queued: true); entry.LastError = ex.Message; }
                Settle(entry, outcome);
            }
            Save();
        }
        finally
        {
            ticking = false;
        }
    }

    private void Settle(QuartermasterPendingCancel entry, QuartermasterCancelOutcome outcome)
    {
        var now = utcNow();
        entry.LastStatus = outcome.Status;
        if (outcome.Status == StatusCancelQueued)
        {
            entry.QueuedAnswers++;
            entry.FirstQueuedUtc ??= now;
            entry.LastQueuedUtc = now;
        }
        else
        {
            entry.QueuedAnswers = 0;
            entry.FirstQueuedUtc = null;
            entry.LastQueuedUtc = null;
        }
        if (outcome.Settled)
        {
            lock (sync)
            {
                var key = (entry.Kind, entry.Id);
                if (!settledStatuses.ContainsKey(key))
                {
                    settledOrder.Enqueue(key);
                    if (settledOrder.Count > SettledMemory)
                        settledStatuses.Remove(settledOrder.Dequeue());
                }
                settledStatuses[key] = outcome.Status;
            }
            if (Remove(entry) && entry.Attempts > 1)
                info($"[MarketMafioso] Quartermaster settled the queued cancel of {Describe(entry)}: {outcome.Status}. {outcome.Message}");
            return;
        }
        entry.Attempts++;
        var delay = entry.Stopping || entry.ClearAccepted || entry.StopSent && entry.Kind == QuartermasterCancelKind.CourierRun
            ? StoppingPollInterval
            : TimeSpan.FromTicks(Math.Min(MaxRetryDelay.Ticks, FirstRetryDelay.Ticks << Math.Min(entry.Attempts - 1, 10)));
        entry.NextAttemptUtc = now + delay;
    }

    private QuartermasterCancelOutcome Attempt(QuartermasterPendingCancel entry, DateTimeOffset now) => entry.Kind switch
    {
        QuartermasterCancelKind.RequestedOperation => AttemptRequestedOperation(entry),
        QuartermasterCancelKind.DeliveredStowForward => AttemptForward(entry),
        _ => AttemptCourierRun(entry),
    };

    private QuartermasterCancelOutcome AttemptRequestedOperation(QuartermasterPendingCancel entry)
    {
        // Re-asking is idempotent: a running operation answers stopping until it ends, then already_terminal.
        if (!custody.TryCancelRequestedOperation(entry.Id, entry.Reason, out var ack, out var error))
            return ack is { IsRejected: true }
                ? Rejected(entry, ack)
                : Failed(entry, error, QuartermasterIpcClient.RequestedOperationCancelCapability);
        entry.LastError = null;
        var status = ack!.Status.ToLowerInvariant();
        if (status == "stopping")
        {
            entry.Stopping = true;
            return new(status, ack.Message ?? "Stopping after the current retainer.", Settled: false, Queued: true);
        }
        if (status == StatusCancelQueued)
            return Queued(entry, ack);
        // cancelled, already_terminal, not_found.
        return new(status, ack.Message ?? status, Settled: true, Queued: false);
    }

    /// <summary>
    /// cancel_queued: Quartermaster holds no persisted operation under the id and keeps the cancel in memory only (it
    /// is lost on a Quartermaster reload), so it is not settled: the cancel is re-sent on backoff until a settled answer
    /// or the entry's expiry.
    /// </summary>
    private static QuartermasterCancelOutcome Queued(QuartermasterPendingCancel entry, QuartermasterCancelAcknowledgement ack)
    {
        entry.LastError = null;
        return new(StatusCancelQueued, ack.Message ?? "Quartermaster queued the cancel; it is re-sent until Quartermaster settles it.",
            Settled: false, Queued: true);
    }

    /// <summary>rejected: kept with Quartermaster's reason, logged once at Warning, retried on backoff until the entry expires.</summary>
    private QuartermasterCancelOutcome Rejected(QuartermasterPendingCancel entry, QuartermasterCancelAcknowledgement ack)
    {
        entry.LastError = $"rejected ({ack.ErrorCode ?? "no code"}): {ack.Message}";
        if (!entry.RejectedWarned)
        {
            entry.RejectedWarned = true;
            warn($"[MarketMafioso] QuartermasterCancelRejected: Quartermaster rejected withdrawing {Describe(entry)} ({ack.ErrorCode ?? "no code"}): " +
                 $"{ack.Message} It is retried until it settles or expires after {EntryLifetime.TotalHours:N0} hours; further rejections are not logged.");
        }
        return new("rejected", entry.LastError, Settled: false, Queued: true);
    }

    private QuartermasterCancelOutcome AttemptForward(QuartermasterPendingCancel entry)
    {
        if (!custody.TryCancelDeliveredStowForward(entry.Id, entry.Reason, out var ack, out var error))
            return ack is { IsRejected: true }
                ? Rejected(entry, ack)
                : Failed(entry, error, QuartermasterIpcClient.DeliveredStowForwardCancelCapability);
        entry.LastError = null;
        var status = ack!.Status.ToLowerInvariant();
        if (status == StatusCancelQueued)
            return Queued(entry, ack);
        if (status == "stopping")
        {
            // Quartermaster stops the forward's run itself; the run is then cleared through the courier channel.
            if (ack.ParsedRunId is { } run)
            {
                Cancel(QuartermasterCancelKind.CourierRun, run.ToString("D"), entry.Reason, entry.Owner,
                    clearRequestId: $"mmf-forward-cancel-{entry.Id}", stopSent: true);
                return new(status, ack.Message ?? $"Quartermaster is stopping courier run {run:D}.", Settled: true, Queued: false, run);
            }
            entry.Stopping = true;
            return new(status, ack.Message ?? "Quartermaster is stopping the forward.", Settled: false, Queued: true);
        }
        return new(status, ack.Message ?? status, Settled: true, Queued: false, ack.ParsedRunId);
    }

    private QuartermasterCancelOutcome AttemptCourierRun(QuartermasterPendingCancel entry)
    {
        if (!Guid.TryParse(entry.Id, out var runId))
            return new("invalid", $"'{entry.Id}' is not a courier run id.", Settled: true, Queued: false);
        if (!courier.TryGetCourierStatus(out var status, out var statusError))
            return Failed(entry, $"courier status unavailable: {statusError}", capability: null);
        var run = status!.Run;
        if (run?.RunId != runId)
            return new("gone", $"Quartermaster no longer holds courier run {runId:D}.", Settled: true, Queued: false);
        if (run.IsRunning)
        {
            if (entry.NeverStop)
            {
                // Not this plugin's run, and someone resumed it: it is left running and nothing more is done.
                info($"[MarketMafioso] Courier run {runId:D} ({entry.Owner}) is running again; it was not started by MarketMafioso, so it was left running.");
                return new("left_running", $"Courier run {runId:D} is running; it was not started by MarketMafioso and was left running.", Settled: true, Queued: false);
            }
            if (entry.StopSent)
            {
                entry.LastError = "the run is still running";
                return new("running", $"Courier run {runId:D} is still running; it is cleared once it stops.", Settled: false, Queued: true);
            }
            entry.StopSent = true;
            if (!courier.TryStopCourierRun(runId.ToString("D"), out var stopAck, out var stopError))
                return Failed(entry, $"stop not confirmed: {stopError}", capability: null);
            entry.LastError = null;
            return new("stopping", stopAck?.Message ?? $"Stopping courier run {runId:D} before clearing it.", Settled: false, Queued: true);
        }
        if (entry.ClearAccepted)
            return new("clearing", $"Quartermaster is clearing courier run {runId:D}.", Settled: false, Queued: true);
        if (!courier.TryClearCourierRun(runId.ToString("D"), entry.ClearRequestId ?? $"mmf-cancel-{runId:N}", out var ack, out var error))
            return Failed(entry, error, capability: null);
        if (ack!.Status == QuartermasterCourierAckStatuses.Clearing)
        {
            entry.ClearAccepted = true;
            entry.LastError = null;
            return new("clearing", $"Quartermaster is clearing courier run {runId:D}.", Settled: false, Queued: true);
        }
        if (ack.ErrorCode == CodeRunNotCurrent)
            return new("gone", $"Courier run {runId:D} is no longer current in Quartermaster.", Settled: true, Queued: false);
        if (entry.NeverStop && ack.ErrorCode == CodeCourierAlreadyRunning)
            return new("left_running", $"Courier run {runId:D} is running; it was not started by MarketMafioso and was left running.", Settled: true, Queued: false);
        entry.LastError = $"{ack.ErrorCode ?? ack.Status}: {ack.Message}" +
                          (ack.DescribeBlocking() is { } blocking ? $" (blocked by {blocking})" : string.Empty);
        return new(ack.ErrorCode == CodeCourierAlreadyRunning ? "running" : "refused", entry.LastError, Settled: false, Queued: true);
    }

    private QuartermasterCancelOutcome Failed(QuartermasterPendingCancel entry, string error, string? capability)
    {
        entry.LastError = error;
        if (capability is not null && QuartermasterIpcClient.IsCapabilityMissing(error))
        {
            // An older Quartermaster: keep today's behaviour (no cancel) and say so once per capability.
            bool first;
            lock (sync)
                first = warnedMissing.Add(capability);
            if (first)
                warn($"[MarketMafioso] QuartermasterCapabilityMissing: Quartermaster does not advertise {capability}; " +
                     $"{Describe(entry)} is not withdrawn there (update Quartermaster). Further cases are not logged.");
            return new("unsupported", error, Settled: true, Queued: false);
        }
        return new("failed", error, Settled: false, Queued: true);
    }

    private bool Remove(QuartermasterPendingCancel entry)
    {
        lock (sync)
            return entries.Remove(entry);
    }

    public static string Describe(QuartermasterPendingCancel entry) => entry.Kind switch
    {
        QuartermasterCancelKind.RequestedOperation => $"requested operation {entry.Id}",
        QuartermasterCancelKind.DeliveredStowForward => $"delivered-stow forward {entry.Id}",
        _ => $"courier run {entry.Id}",
    } + (string.IsNullOrWhiteSpace(entry.Owner) ? string.Empty : $" ({entry.Owner})");

    private void Load()
    {
        if (path is null)
            return;
        try
        {
            if (!File.Exists(path))
                return;
            var json = File.ReadAllText(path);
            var loaded = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<QuartermasterPendingCancel>>(json, JsonOptions);
            lock (sync)
                entries = (loaded ?? []).Where(entry => !string.IsNullOrWhiteSpace(entry.Id)).ToList();
            // A reload retries at once: whatever was pending is reconciled before new work starts.
            foreach (var entry in entries)
                entry.NextAttemptUtc = DateTimeOffset.MinValue;
            nextDueUtc = entries.Count == 0 ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue;
            if (entries.Count > 0)
                warn($"[MarketMafioso] {entries.Count} Quartermaster cancel(s) from an earlier load are still pending: " +
                     string.Join("; ", entries.Select(Describe)) + ". They are retried now.");
        }
        catch (Exception ex)
        {
            warn($"[MarketMafioso] The Quartermaster cancel outbox {path} could not be read; earlier pending cancels are lost: {ex.Message}");
        }
    }

    private void Save()
    {
        List<QuartermasterPendingCancel> snapshot;
        lock (sync)
        {
            snapshot = entries.ToList();
            nextDueUtc = snapshot.Count == 0
                ? DateTimeOffset.MaxValue
                : snapshot.Min(entry => entry.NextAttemptUtc < entry.FirstAttemptUtc + EntryLifetime ? entry.NextAttemptUtc : entry.FirstAttemptUtc + EntryLifetime);
        }
        if (path is null)
            return;
        try
        {
            if (snapshot.Count == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            warn($"[MarketMafioso] The Quartermaster cancel outbox could not be saved; pending cancels are kept in memory: {ex.Message}");
        }
    }
}
