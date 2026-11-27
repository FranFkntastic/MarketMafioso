using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso;

/// <summary>
/// How long a burst of inventory notifications may coalesce: delivery waits for <see cref="Quiet"/> without a new
/// notification, but never longer than <see cref="MaxWait"/> after the first undelivered one.
/// </summary>
internal readonly record struct InventoryDeliveryWindow(TimeSpan Quiet, TimeSpan MaxWait);

/// <summary>
/// Coalesces inventory change notifications into deliveries. Ordinary play uses the interactive window; while an
/// automation run mutates inventory in a steady stream, the batch-run window trades a short delay for far fewer sends.
/// Deliveries carry the current state, so a deferred notification is only ever late, never lost.
/// </summary>
internal sealed class InventoryDeliveryCoalescer : IDisposable
{
    private readonly object gate = new();
    private readonly InventoryDeliveryWindow interactive;
    private readonly InventoryDeliveryWindow batchRun;
    private readonly Func<CancellationToken, Task> deliver;
    private readonly Action<string, Exception?> diagnostic;
    private readonly CancellationTokenSource lifetime = new();

    // The scheduled delivery that has not started yet. Once a delivery starts it is detached from this field,
    // so later notifications start a new batch and never cancel a send in progress.
    private CancellationTokenSource? scheduled;
    private int undelivered;
    private long firstUndeliveredAt;
    private long lastNotifiedAt;
    private string? lastReason;
    private bool batchRunActive;
    private bool disposed;

    public InventoryDeliveryCoalescer(
        InventoryDeliveryWindow interactive,
        InventoryDeliveryWindow batchRun,
        Func<CancellationToken, Task> deliver,
        Action<string, Exception?> diagnostic)
    {
        Validate(interactive, nameof(interactive));
        Validate(batchRun, nameof(batchRun));
        this.interactive = interactive;
        this.batchRun = batchRun;
        this.deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        this.diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
    }

    public void Notify(string reason)
    {
        Schedule schedule;
        lock (gate)
        {
            if (disposed)
                return;
            var now = Environment.TickCount64;
            if (undelivered == 0)
                firstUndeliveredAt = now;
            undelivered++;
            lastNotifiedAt = now;
            lastReason = reason;
            schedule = RescheduleLocked(now);
        }

        Start(schedule);
    }

    /// <summary>
    /// Switches between the interactive and batch-run windows. Pending changes are rescheduled under the new window,
    /// so when a run ends (completes, stops, fails, pauses or is cancelled) its final state is delivered promptly.
    /// </summary>
    public void SetBatchRunActive(bool active)
    {
        Schedule schedule;
        lock (gate)
        {
            if (disposed || batchRunActive == active)
                return;
            batchRunActive = active;
            if (undelivered == 0)
                return;
            schedule = RescheduleLocked(Environment.TickCount64);
        }

        Start(schedule);
    }

    private Schedule RescheduleLocked(long now)
    {
        var window = batchRunActive ? batchRun : interactive;
        var due = Math.Min(
            lastNotifiedAt + (long)window.Quiet.TotalMilliseconds,
            firstUndeliveredAt + (long)window.MaxWait.TotalMilliseconds);
        var superseded = scheduled;
        scheduled = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        // Cancelled under the lock, after the swap: its delivery task owns and disposes it, and once the lock is
        // released that task may already have seen it is superseded and disposed it.
        superseded?.Cancel();
        return new(scheduled, scheduled.Token, TimeSpan.FromMilliseconds(Math.Max(0, due - now)));
    }

    private void Start(Schedule schedule) =>
        _ = DeliverAfterDelayAsync(schedule.Owned, schedule.Token, schedule.Delay);

    private async Task DeliverAfterDelayAsync(CancellationTokenSource owned, CancellationToken token, TimeSpan delay)
    {
        var count = 0;
        string? reason = null;
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token).ConfigureAwait(false);

            double waitedSeconds;
            lock (gate)
            {
                if (!ReferenceEquals(scheduled, owned) || token.IsCancellationRequested)
                    return;
                scheduled = null;
                count = undelivered;
                reason = lastReason;
                waitedSeconds = (Environment.TickCount64 - firstUndeliveredAt) / 1000d;
                undelivered = 0;
                lastReason = null;
            }

            await deliver(token).ConfigureAwait(false);
            diagnostic(
                $"Processed coalesced inventory delivery: {count} notification(s) over {waitedSeconds:F1}s, last after {reason}.",
                null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            diagnostic(
                $"Failed to ship the coalesced inventory report for {count} notification(s), last after {reason}.",
                exception);
        }
        finally
        {
            owned.Dispose();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            scheduled = null;
        }

        // Cancels the scheduled delivery and any delivery still waiting for the framework thread; each delivery
        // disposes its own linked source when it unwinds.
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private static void Validate(InventoryDeliveryWindow window, string name)
    {
        if (window.Quiet < TimeSpan.Zero || window.MaxWait < window.Quiet)
            throw new ArgumentOutOfRangeException(name, "Quiet must be non-negative and no longer than MaxWait.");
    }

    private readonly record struct Schedule(
        CancellationTokenSource Owned,
        CancellationToken Token,
        TimeSpan Delay);
}
