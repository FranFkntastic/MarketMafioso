using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso;

internal sealed class InventoryDeliveryCoalescer : IDisposable
{
    private readonly object gate = new();
    private readonly TimeSpan delay;
    private readonly Func<CancellationToken, Task> deliver;
    private readonly Action<string, Exception?> diagnostic;
    private CancellationTokenSource? pending;
    private bool disposed;

    public InventoryDeliveryCoalescer(
        TimeSpan delay,
        Func<CancellationToken, Task> deliver,
        Action<string, Exception?> diagnostic)
    {
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));
        this.delay = delay;
        this.deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
        this.diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
    }

    public void Notify(string reason)
    {
        CancellationTokenSource cancellation;
        lock (gate)
        {
            if (disposed)
                return;
            pending?.Cancel();
            pending?.Dispose();
            pending = new CancellationTokenSource();
            cancellation = pending;
        }

        _ = DeliverAfterDelayAsync(reason, cancellation);
    }

    private async Task DeliverAfterDelayAsync(string reason, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
            await deliver(cancellation.Token).ConfigureAwait(false);
            diagnostic($"Processed coalesced inventory delivery after {reason}.", null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            diagnostic($"Failed to ship the coalesced inventory report after {reason}.", exception);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, cancellation))
                {
                    pending = null;
                    cancellation.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            pending?.Cancel();
            pending?.Dispose();
            pending = null;
        }
    }
}
