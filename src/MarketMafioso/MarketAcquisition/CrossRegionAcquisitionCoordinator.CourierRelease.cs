using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

/// <summary>
/// Clear's Quartermaster half: the courier run this session started (or was blocked by) is cleared in Quartermaster
/// too, so the next request is not refused by a run nobody is following. MarketMafioso's own session is dropped
/// whatever Quartermaster answers; when Quartermaster cannot clear the run, the note says so plainly.
/// </summary>
public sealed partial class CrossRegionAcquisitionCoordinator
{
    private const string CodeRunNotCurrent = "RunNotCurrent";
    private const string CodeCourierRunUnresolved = "CourierRunUnresolved";
    private static readonly TimeSpan CourierReleaseTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CourierReleasePollInterval = TimeSpan.FromSeconds(1);

    private PendingCourierRelease? pendingCourierRelease;
    private DateTimeOffset nextCourierReleasePollUtc = DateTimeOffset.MinValue;

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
    /// Clears this session's run in Quartermaster. Returns the log text; sets <see cref="CourierReleaseNote"/>.
    /// Never throws and never blocks Clear: a run still stopping is cleared from <see cref="Tick"/>.
    /// </summary>
    private string ReleaseCourierRun(IReadOnlyList<Guid> known, string requestId)
    {
        pendingCourierRelease = null;
        SetReleaseNote(null, attention: false);
        if (known.Count == 0)
            return "No Quartermaster courier run was started.";
        try
        {
            var targets = known;
            var running = false;
            if (courier.TryGetCourierStatus(out var status, out _))
            {
                if (status!.Run?.RunId is not { } current || !known.Contains(current))
                    return $"Quartermaster holds none of this session's courier runs ({Runs(known)}).";
                targets = [current];
                running = status.Run.IsRunning;
            }
            foreach (var run in targets)
            {
                if (running)
                    return StopThenRelease(run, requestId);
                if (!courier.TryClearCourierRun(run.ToString("D"), requestId, out var ack, out var error))
                    return Unreleased(run, error, certain: false);
                if (ack!.Status == QuartermasterCourierAckStatuses.Clearing)
                {
                    pendingCourierRelease = new(run, requestId, utcNow() + CourierReleaseTimeout) { ClearAccepted = true };
                    SetReleaseNote($"Quartermaster is clearing courier run {run:D}.", attention: false);
                    return $"Quartermaster is clearing courier run {run:D}.";
                }
                if (ack.ErrorCode == CodeRunNotCurrent)
                    continue;
                if (ack.ErrorCode == CodeCourierAlreadyRunning)
                    return StopThenRelease(run, requestId);
                return Unreleased(run, $"{ack.ErrorCode ?? ack.Status}: {ack.Message}", certain: true);
            }
            return $"Quartermaster holds none of this session's courier runs ({Runs(known)}).";
        }
        catch (Exception ex)
        {
            log("Clearing the Quartermaster courier run failed.", ex);
            return Unreleased(known[^1], ex.Message, certain: false);
        }
    }

    private string StopThenRelease(Guid run, string requestId)
    {
        var stop = courier.TryStopCourierRun(run.ToString("D"), out var ack, out var error)
            ? ack?.Message ?? ack?.Status ?? "stop requested"
            : $"stop not confirmed: {error}";
        pendingCourierRelease = new(run, requestId, utcNow() + CourierReleaseTimeout);
        nextCourierReleasePollUtc = utcNow() + CourierReleasePollInterval;
        SetReleaseNote($"Stopping Quartermaster courier run {run:D}; it is cleared once it stops.", attention: false);
        return $"Quartermaster courier run {run:D} was running; stopping it before clearing ({stop}).";
    }

    private string Unreleased(Guid run, string reason, bool certain)
    {
        pendingCourierRelease = null;
        var holds = certain ? "still holds" : "may still hold";
        SetReleaseNote($"Quartermaster {holds} courier run {run:D} ({reason}); clear it in Quartermaster's Courier pane.", attention: true);
        return CourierReleaseNote!;
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

    /// <summary>Status text for a submission refused because an earlier, different run blocks Quartermaster.</summary>
    private string? NoteBlockingRun(CourierLeg leg, QuartermasterCourierAcknowledgement ack)
    {
        if (ack.ErrorCode != CodeCourierRunUnresolved || ack.RunId is not { } blocking || blocking == Guid.Empty || blocking == RunIdOf(leg))
            return null;
        session.BlockingCourierRunId = blocking;
        return $" Quartermaster is held by unresolved courier run {blocking:D}; Clear releases it.";
    }

    private static string Runs(IEnumerable<Guid> runs) => string.Join(", ", runs.Select(run => run.ToString("D")));
}
