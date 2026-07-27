using System;
using Franthropy.Dalamud.Automation.Retainers;

namespace MarketMafioso.MarketDiagnostics;

internal sealed class AutoRetainerSuppressionLease : IDisposable
{
    private readonly IAutoRetainerIpc autoRetainer;
    private readonly bool restoreUnsuppressed;
    private bool disposed;

    private AutoRetainerSuppressionLease(
        IAutoRetainerIpc autoRetainer,
        bool available,
        bool initiallySuppressed,
        bool changed)
    {
        this.autoRetainer = autoRetainer;
        Available = available;
        InitiallySuppressed = initiallySuppressed;
        Changed = changed;
        restoreUnsuppressed = available && changed && !initiallySuppressed;
    }

    public bool Available { get; }
    public bool InitiallySuppressed { get; }
    public bool Changed { get; }
    public bool Restored { get; private set; }
    public string? RestoreError { get; private set; }

    public static bool TryAcquire(
        IAutoRetainerIpc autoRetainer,
        out AutoRetainerSuppressionLease? lease,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(autoRetainer);
        lease = null;

        if (!autoRetainer.IsAvailable)
        {
            lease = new(autoRetainer, available: false, initiallySuppressed: false, changed: false);
            message = "AutoRetainer is unavailable; no suppression is required.";
            return true;
        }

        var initialStateKnown = false;
        var initiallySuppressed = false;
        try
        {
            if (autoRetainer.IsBusy)
            {
                message = "AutoRetainer is busy. Wait for it to become idle before running a remote bell probe.";
                return false;
            }

            initiallySuppressed = autoRetainer.IsSuppressed;
            initialStateKnown = true;
            if (!initiallySuppressed)
            {
                autoRetainer.SetSuppressed(true);
                if (!autoRetainer.IsSuppressed)
                {
                    TryRestoreAfterFailedAcquire(autoRetainer);
                    message = "AutoRetainer did not confirm suppression; no bell interaction was sent.";
                    return false;
                }
            }

            lease = new(autoRetainer, available: true, initiallySuppressed, changed: !initiallySuppressed);
            message = initiallySuppressed
                ? "AutoRetainer was already suppressed."
                : "AutoRetainer suppression acquired.";
            return true;
        }
        catch (Exception ex)
        {
            if (initialStateKnown && !initiallySuppressed)
                TryRestoreAfterFailedAcquire(autoRetainer);
            message = $"Unable to suppress AutoRetainer; no bell interaction was sent. {ex.Message}";
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        if (!restoreUnsuppressed)
        {
            Restored = true;
            return;
        }

        try
        {
            autoRetainer.SetSuppressed(false);
            Restored = true;
        }
        catch (Exception ex)
        {
            RestoreError = ex.Message;
        }
    }

    private static void TryRestoreAfterFailedAcquire(IAutoRetainerIpc autoRetainer)
    {
        try { autoRetainer.SetSuppressed(false); }
        catch { }
    }
}
