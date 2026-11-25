using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

/// <summary>
/// Clear's Quartermaster half: the courier run this session started (or was blocked by) is cleared in Quartermaster
/// too, so the next request is not refused by a run nobody is following. MarketMafioso's own session is dropped
/// whatever Quartermaster answers; what Quartermaster has not confirmed is kept in the durable cancel outbox and
/// retried after the session is gone, and the note says plainly what is still held.
/// </summary>
public sealed partial class CrossRegionAcquisitionCoordinator
{
    private const string CodeRunNotCurrent = "RunNotCurrent";
    private const string CodeCourierRunUnresolved = "CourierRunUnresolved";
    private const string CodeCarrierRunConflict = "CarrierRunConflict";
    private const string BlockingSourceDeliveredStow = "delivered-stow";
    private static readonly TimeSpan CourierReleaseTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CourierReleasePollInterval = TimeSpan.FromSeconds(1);

    private PendingCourierRelease? pendingCourierRelease;
    private DateTimeOffset nextCourierReleasePollUtc = DateTimeOffset.MinValue;
    /// <summary>The courier run the last release acted on (stopped or cleared); null when it touched none.</summary>
    private Guid? releasedCourierRun;
    /// <summary>What the last release saw of the foreign run that blocked the session.</summary>
    private BlockingRunState blockingRunState;

    private enum BlockingRunState
    {
        /// <summary>No foreign blocking run, or its state could not be read.</summary>
        Unknown,
        /// <summary>Quartermaster holds it, not running (unresolved, stopped or parked), or no longer holds it at all.</summary>
        NotRunning,
        /// <summary>Running, and not this session's own: Clear left it running.</summary>
        LeftRunning,
        /// <summary>Running, and this session's own delivered-stow forward or request: Clear may stop it.</summary>
        RunningOwn,
    }

    /// <summary>What happened to the cleared session's Quartermaster run, shown while no session is open; null when nothing to say.</summary>
    public string? CourierReleaseNote { get; private set; }

    /// <summary>True when <see cref="CourierReleaseNote"/> asks the user to act in Quartermaster.</summary>
    public bool CourierReleaseNeedsAttention { get; private set; }

    /// <summary>A clear still in flight: waiting for the run to stop, or for Quartermaster to report it gone.</summary>
    private sealed class PendingCourierRelease(Guid run, string requestId, DateTimeOffset deadlineUtc)
    {
        public Guid Run { get; } = run;
        public string RequestId { get; } = requestId;
        public DateTimeOffset DeadlineUtc { get; } = deadlineUtc;
        public bool ClearAccepted { get; set; }
        /// <summary>The cancel outbox drives this release; the tick only reports what it settles.</summary>
        public bool Durable { get; set; }
    }

    /// <summary>Every courier run this session knows of, including an earlier run Quartermaster said blocks it.</summary>
    private List<Guid> KnownCourierRuns() =>
        new[] { session.FundRunId, session.HaulRunId, session.ShopperCounted?.RunId, session.BlockingCourierRunId }
            .Concat(session.Collections.Select(collection => collection.RunId))
            .OfType<Guid>()
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

    /// <summary>
    /// The request-id prefix every courier leg of this session is sent under (fund, every haul and collect attempt),
    /// so a run accepted while its acceptance was unknown (no run id stored) is still found by its request id.
    /// </summary>
    private string? CourierRequestPrefix() =>
        session.SessionId == Guid.Empty ? null : $"mmf-xr-{session.SessionId:N}-";

    private static bool IsOwnCourierRequest(string? requestId, string? prefix) =>
        prefix is not null && requestId is not null && requestId.StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>Quartermaster's courier request id for a delivered stow's forward run.</summary>
    private const string DeliveredStowCourierPrefix = "delivered-stow:";

    /// <summary>The run is the foreign run that blocked this session: neither one of its own legs nor sent under its request ids.</summary>
    private bool IsForeignBlockingRun(Guid run, string? courierRequestId) =>
        session.BlockingCourierRunId == run && !OwnCourierRuns().Contains(run) &&
        !IsOwnCourierRequest(courierRequestId, CourierRequestPrefix());

    /// <summary>
    /// A foreign run Clear may stop while it is running: this session's own delivered-stow forward (or a request under
    /// its id prefix). Anything else running is someone's live work and is never stopped by Clear.
    /// </summary>
    private bool ForeignRunIsOwnForward(string? courierRequestId)
    {
        var prefix = CourierRequestPrefix();
        if (prefix is null)
            return false;
        var blockingRequest = session.BlockingCourierRequestId;
        if (IsOwnCourierRequest(blockingRequest, prefix))
            return true;
        return courierRequestId is not null &&
               courierRequestId.StartsWith(DeliveredStowCourierPrefix, StringComparison.Ordinal) &&
               IsOwnCourierRequest(courierRequestId[DeliveredStowCourierPrefix.Length..], prefix);
    }

    /// <summary>Who started the foreign blocking run, as Quartermaster named it.</summary>
    private string BlockingSourceLabel()
    {
        var requestId = session.BlockingCourierRequestId;
        return session.BlockingCourierSource switch
        {
            BlockingSourceDeliveredStow when requestId is not null => $"delivered-stow forward {requestId}",
            BlockingSourceDeliveredStow => "a delivered-stow forward",
            "ipc" when requestId is not null => $"IPC request {requestId}",
            "ipc" => "an IPC request",
            "manual" => "started in Quartermaster",
            { Length: > 0 } other when requestId is not null => $"{other} request {requestId}",
            { Length: > 0 } other => other,
            _ when requestId is not null => $"request {requestId}",
            _ => "not started by this session",
        };
    }

    /// <summary>A running foreign run is named and left alone; the result says so.</summary>
    private string LeaveForeignRunRunning(Guid run)
    {
        blockingRunState = BlockingRunState.LeftRunning;
        releasedCourierRun = null;
        pendingCourierRelease = null;
        var note = $"Quartermaster is running courier run {run:D} ({BlockingSourceLabel()}); it was left running.";
        SetReleaseNote(note, attention: false);
        log($"Cross-regional clear: {note}", null);
        return note;
    }

    /// <summary>
    /// Clears this session's run in Quartermaster. Returns the log text; sets <see cref="CourierReleaseNote"/>.
    /// Never throws and never blocks Clear: a run still stopping is cleared from <see cref="Tick"/> and, when the
    /// cancel outbox is available, durably after the session is gone.
    /// </summary>
    private string ReleaseCourierRun(IReadOnlyList<Guid> known, string requestId, string owner)
    {
        pendingCourierRelease = null;
        releasedCourierRun = null;
        blockingRunState = BlockingRunState.Unknown;
        SetReleaseNote(null, attention: false);
        var prefix = CourierRequestPrefix();
        if (known.Count == 0 && prefix is null)
            return "No Quartermaster courier run was started.";
        try
        {
            var targets = known;
            var running = false;
            if (courier.TryGetCourierStatus(out var status, out var statusError))
            {
                var current = status!.Run;
                if (current?.RunId is not { } currentId ||
                    !known.Contains(currentId) && !IsOwnCourierRequest(current.RequestId, prefix))
                {
                    // Quartermaster's only current run is someone else's (or none): a foreign blocking run is not running.
                    if (session.BlockingCourierRunId is not null)
                        blockingRunState = BlockingRunState.NotRunning;
                    return known.Count == 0
                        ? "No Quartermaster courier run was started for this session."
                        : $"Quartermaster holds none of this session's courier runs ({Runs(known)}).";
                }
                if (!known.Contains(currentId))
                    log($"Quartermaster courier run {currentId:D} carries this session's request {current.RequestId} without a recorded run id; clearing it too.", null);
                if (IsForeignBlockingRun(currentId, current.RequestId))
                {
                    if (current.IsRunning && !ForeignRunIsOwnForward(current.RequestId))
                        return LeaveForeignRunRunning(currentId);
                    blockingRunState = current.IsRunning ? BlockingRunState.RunningOwn : BlockingRunState.NotRunning;
                }
                targets = [currentId];
                running = current.IsRunning;
            }
            else if (known.Count == 0)
                return $"Quartermaster's courier status could not be read ({statusError}); no courier run of this session was confirmed.";
            foreach (var run in targets)
            {
                releasedCourierRun = run;
                if (running)
                    return StopThenRelease(run, requestId, owner);
                if (!courier.TryClearCourierRun(run.ToString("D"), requestId, out var ack, out var error))
                    return Unreleased(run, error, certain: false, requestId, owner);
                if (ack!.Status == QuartermasterCourierAckStatuses.Clearing)
                {
                    pendingCourierRelease = new(run, requestId, utcNow() + CourierReleaseTimeout)
                    {
                        ClearAccepted = true,
                        Durable = Mirror(run, requestId, owner, stopSent: false, clearAccepted: true),
                    };
                    SetReleaseNote($"Quartermaster is clearing courier run {run:D}.", attention: false);
                    return $"Quartermaster is clearing courier run {run:D}.";
                }
                if (ack.ErrorCode == CodeRunNotCurrent)
                {
                    releasedCourierRun = null;
                    continue;
                }
                if (ack.ErrorCode == CodeCourierAlreadyRunning)
                    return IsForeignBlockingRun(run, null) && !ForeignRunIsOwnForward(null)
                        ? LeaveForeignRunRunning(run)
                        : StopThenRelease(run, requestId, owner);
                return Unreleased(run, $"{ack.ErrorCode ?? ack.Status}: {ack.Message}", certain: true, requestId, owner);
            }
            return $"Quartermaster holds none of this session's courier runs ({Runs(known)}).";
        }
        catch (Exception ex)
        {
            log("Clearing the Quartermaster courier run failed.", ex);
            return known.Count == 0 && releasedCourierRun is null
                ? $"Clearing the Quartermaster courier run failed: {ex.Message}"
                : Unreleased(releasedCourierRun ?? known[^1], ex.Message, certain: false, requestId, owner);
        }
    }

    private string StopThenRelease(Guid run, string requestId, string owner)
    {
        var stop = courier.TryStopCourierRun(run.ToString("D"), out var ack, out var error)
            ? ack?.Message ?? ack?.Status ?? "stop requested"
            : $"stop not confirmed: {error}";
        pendingCourierRelease = new(run, requestId, utcNow() + CourierReleaseTimeout)
        {
            Durable = Mirror(run, requestId, owner, stopSent: true, clearAccepted: false),
        };
        nextCourierReleasePollUtc = utcNow() + CourierReleasePollInterval;
        SetReleaseNote($"Stopping Quartermaster courier run {run:D}; it is cleared once it stops.", attention: false);
        return $"Quartermaster courier run {run:D} was running; stopping it before clearing ({stop}).";
    }

    private string Unreleased(Guid run, string reason, bool certain, string? requestId = null, string? owner = null)
    {
        pendingCourierRelease = null;
        var holds = certain ? "still holds" : "may still hold";
        if (requestId is not null && owner is not null &&
            Mirror(run, requestId, owner, stopSent: false, clearAccepted: false,
                neverStop: IsForeignBlockingRun(run, null) && !ForeignRunIsOwnForward(null)))
        {
            // The outbox retries the clear; the note stays until it settles.
            if (cancels!.IsPending(QuartermasterCancelKind.CourierRun, run.ToString("D")))
            {
                pendingCourierRelease = new(run, requestId, utcNow() + CourierReleaseTimeout) { Durable = true };
                SetReleaseNote($"Quartermaster {holds} courier run {run:D} ({reason}); MarketMafioso keeps trying to clear it for up to " +
                               $"{QuartermasterCancelOutbox.EntryLifetime.TotalHours:N0} hours, or clear it in Quartermaster's Courier pane.", attention: true);
                return CourierReleaseNote!;
            }
            SetReleaseNote($"Quartermaster cleared courier run {run:D}.", attention: false);
            return CourierReleaseNote!;
        }
        SetReleaseNote($"Quartermaster {holds} courier run {run:D} ({reason}); clear it in Quartermaster's Courier pane.", attention: true);
        return CourierReleaseNote!;
    }

    /// <summary>
    /// Hands the run to the durable cancel outbox; false when there is none (the in-memory release then drives it).
    /// A foreign run is handed over with <paramref name="neverStop"/>: it is cleared while parked, never stopped.
    /// </summary>
    private bool Mirror(Guid run, string requestId, string owner, bool stopSent, bool clearAccepted, bool neverStop = false)
    {
        if (cancels is null)
            return false;
        var outcome = cancels.Cancel(QuartermasterCancelKind.CourierRun, run.ToString("D"), "Cleared from MarketMafioso.", owner,
            clearRequestId: requestId, stopSent: stopSent, clearAccepted: clearAccepted, neverStop: neverStop);
        log($"Courier run {run:D} handed to the cancel outbox: {outcome.Status}. {outcome.Message}", null);
        return true;
    }

    private void SetReleaseNote(string? note, bool attention)
    {
        CourierReleaseNote = note;
        CourierReleaseNeedsAttention = note is not null && attention;
    }

    /// <summary>Drives a clear that had to wait for its run to stop, then confirms Quartermaster dropped it.</summary>
    private void TickCourierRelease()
    {
        if (pendingCourierRelease is not { } pending)
            return;
        var now = utcNow();
        if (now < nextCourierReleasePollUtc)
            return;
        nextCourierReleasePollUtc = now + CourierReleasePollInterval;
        if (pending.Durable && cancels is not null)
        {
            // The outbox stops (once) and clears; this only keeps the note current.
            var id = pending.Run.ToString("D");
            if (!cancels.IsPending(QuartermasterCancelKind.CourierRun, id))
            {
                pendingCourierRelease = null;
                SetReleaseNote($"Quartermaster cleared courier run {pending.Run:D}.", attention: false);
                log($"Quartermaster cleared courier run {pending.Run:D} for the cleared cross-regional session.", null);
            }
            else if (now >= pending.DeadlineUtc && !CourierReleaseNeedsAttention)
            {
                var last = cancels.Pending.FirstOrDefault(entry => entry.Kind == QuartermasterCancelKind.CourierRun && entry.Id == id)?.LastError;
                SetReleaseNote($"Quartermaster still holds courier run {pending.Run:D}{(last is null ? string.Empty : $" ({last})")}; MarketMafioso keeps " +
                               $"trying to clear it for up to {QuartermasterCancelOutbox.EntryLifetime.TotalHours:N0} hours, or clear it in Quartermaster's Courier pane.",
                    attention: true);
                log($"Cross-regional clear: {CourierReleaseNote}", null);
            }
            return;
        }
        if (!courier.TryGetCourierStatus(out var status, out var statusError))
        {
            if (now >= pending.DeadlineUtc)
                Finish(Unreleased(pending.Run, $"status unavailable: {statusError}", certain: false));
            return;
        }
        var run = status!.Run;
        if (run?.RunId != pending.Run)
        {
            pendingCourierRelease = null;
            SetReleaseNote($"Quartermaster cleared courier run {pending.Run:D}.", attention: false);
            log($"Quartermaster cleared courier run {pending.Run:D} for the cleared cross-regional session.", null);
            return;
        }
        if (now >= pending.DeadlineUtc)
        {
            Finish(Unreleased(pending.Run, pending.ClearAccepted
                ? "the clear did not land"
                : run.IsRunning ? "it did not stop" : $"{run.Code ?? run.State}", certain: true));
            return;
        }
        if (pending.ClearAccepted || run.IsRunning)
            return;
        if (!courier.TryClearCourierRun(pending.Run.ToString("D"), pending.RequestId, out var ack, out var error))
        {
            Finish(Unreleased(pending.Run, error, certain: true));
            return;
        }
        if (ack!.Status == QuartermasterCourierAckStatuses.Clearing)
        {
            pending.ClearAccepted = true;
            SetReleaseNote($"Quartermaster is clearing courier run {pending.Run:D}.", attention: false);
        }
        else if (ack.ErrorCode is not (CodeCourierAlreadyRunning or CodeRunNotCurrent))
            Finish(Unreleased(pending.Run, $"{ack.ErrorCode ?? ack.Status}: {ack.Message}", certain: true));

        void Finish(string message) => log($"Cross-regional clear: {message}", null);
    }

    /// <summary>
    /// Status text for a submission refused because an earlier, different run holds Quartermaster. The run and the
    /// request that owns it are recorded, so Clear abandons it and names exactly what it abandoned.
    /// </summary>
    private string? NoteBlockingRun(CourierLeg leg, QuartermasterCourierAcknowledgement ack)
    {
        if (ack.ErrorCode is not (CodeCourierRunUnresolved or CodeCarrierRunConflict or CodeCourierAlreadyRunning) ||
            ack.RunId is not { } blocking || blocking == Guid.Empty || blocking == RunIdOf(leg))
            return null;
        RecordBlockingRun(blocking, ack);
        var what = ack.ErrorCode == CodeCourierRunUnresolved ? "held by unresolved courier run" : "busy with courier run";
        var clear = ack.ErrorCode == CodeCourierRunUnresolved
            ? "Clear abandons it"
            : "Clear abandons it once it is not running (a running run that this session did not start is left running)";
        return $" Quartermaster is {what} {blocking:D}{(ack.DescribeBlocking() is { } owner ? $" ({owner})" : string.Empty)}; {clear}.";
    }

    private void RecordBlockingRun(Guid blocking, QuartermasterCourierAcknowledgement ack)
    {
        session.BlockingCourierRunId = blocking;
        session.BlockingCourierRequestId = ack.BlockingRequestId;
        session.BlockingCourierSource = ack.BlockingSource;
    }

    /// <summary>The blocking run in words: the request that owns it when Quartermaster named one, else the run id.</summary>
    private string DescribeBlockingRun()
    {
        var run = session.BlockingCourierRunId;
        var requestId = session.BlockingCourierRequestId;
        var source = session.BlockingCourierSource;
        if (string.Equals(source, BlockingSourceDeliveredStow, StringComparison.Ordinal) && requestId is not null)
            return $"delivered-stow forward {requestId}{ForwardCargoNote(requestId)}";
        if (requestId is null && source is null)
            return $"courier run {run:D}";
        var owner = requestId is null ? source : source is null ? $"request {requestId}" : $"{source} request {requestId}";
        return $"{owner} (courier run {run:D})";
    }

    /// <summary>" carrying N item(s)" for a forward this plugin follows, else empty.</summary>
    private string ForwardCargoNote(string requestId)
    {
        if (forwardWatches.FirstOrDefault(watch => watch.RequestId == requestId) is not { } watch)
            return string.Empty;
        var units = watch.Lines.SelectMany(line => line.AllForwards)
            .Where(forward => !forward.IsNeutral)
            .Sum(forward => (long)Math.Max(0, forward.Planned - (forward.Delivered ?? 0)));
        return units > 0 ? $" carrying {units:N0} item(s)" : string.Empty;
    }

    /// <summary>
    /// Clear abandons a foreign run that blocked this session only while it is not running (unresolved or parked), or
    /// when it is this session's own delivered-stow forward: a delivered-stow forward is cancelled in Quartermaster
    /// (its run is then cleared there), any other run is released with this session's. A running foreign run is left
    /// running and named by the release. Returns the sentence naming what was abandoned, or null when nothing was.
    /// </summary>
    private string? AbandonBlockingRun(string owner)
    {
        if (session.BlockingCourierRunId is not { } blocking || OwnCourierRuns().Contains(blocking))
            return null;
        if (blockingRunState == BlockingRunState.LeftRunning)
            return null;
        var description = DescribeBlockingRun();
        // Cancelling a forward stops its run when it is running, so it is cancelled only when the run is known not to
        // run, or is this session's own forward. Otherwise the parked-only clear handed to the outbox covers it.
        if (string.Equals(session.BlockingCourierSource, BlockingSourceDeliveredStow, StringComparison.Ordinal) &&
            session.BlockingCourierRequestId is { } forwardRequest &&
            (blockingRunState is BlockingRunState.NotRunning or BlockingRunState.RunningOwn || ForeignRunIsOwnForward(null)))
        {
            var outcome = CancelForward(forwardRequest, "Cleared from MarketMafioso: the forward blocked this session's courier request.", owner);
            if (outcome is { Status: "cancelled" or "stopping" })
                return $"Also abandoned {description}.";
            if (outcome is { Queued: true })
                return $"Also abandoning {description}; Quartermaster has not confirmed it yet and the cancel is retried.";
        }
        return releasedCourierRun == blocking ? $"Also abandoned {description}." : null;
    }

    private IEnumerable<Guid> OwnCourierRuns() =>
        new[] { session.FundRunId, session.HaulRunId, session.ShopperCounted?.RunId }
            .Concat(session.Collections.Select(collection => collection.RunId))
            .OfType<Guid>();

    private static string Runs(IEnumerable<Guid> runs) => string.Join(", ", runs.Select(run => run.ToString("D")));
}
