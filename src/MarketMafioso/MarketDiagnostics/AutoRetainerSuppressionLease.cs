using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Automation.Retainers;

namespace MarketMafioso.MarketDiagnostics;

/// <summary>
/// The cross-plugin AutoRetainer holder registry: Dalamud shared data under <see cref="Key"/>, a set of owner names
/// (Quartermaster, MarketMafioso) locked on itself. An owner name is in it while that plugin needs AutoRetainer
/// suppressed, and AutoRetainer is unsuppressed only by the last holder out, so no plugin lifts a suppression another
/// one still needs. Without a registry attached (tests, or a load that has not attached yet) every call is a no-op.
/// </summary>
internal static class AutoRetainerHolderRegistry
{
    public const string Key = "Gooseworks.AutoRetainerSuppression.Holders";
    public const string OwnerName = "MarketMafioso";

    private static HashSet<string>? registry;

    /// <summary>
    /// Load: a new load holds nothing, so an entry under this plugin's name is a dead load's leftover and is removed.
    /// AutoRetainer itself is not touched here; this plugin keeps no marker of a suppression it set.
    /// </summary>
    public static void Attach(HashSet<string> shared, Action<string>? warn)
    {
        registry = shared;
        if (Remove())
            warn?.Invoke($"[MarketMafioso] AutoRetainerHolderStale: removed '{OwnerName}' from the AutoRetainer holder registry, left by an earlier MarketMafioso load.");
    }

    /// <summary>Unload: drops this plugin's name and the reference to the shared set.</summary>
    public static void Detach()
    {
        Remove();
        registry = null;
    }

    /// <summary>Adds this plugin's owner name; true when another owner name was already present.</summary>
    public static bool Add()
    {
        if (registry is not { } set)
            return false;
        lock (set)
        {
            var others = set.Any(name => !string.Equals(name, OwnerName, StringComparison.Ordinal));
            set.Add(OwnerName);
            return others;
        }
    }

    /// <summary>Removes this plugin's owner name; true when it was present.</summary>
    public static bool Remove()
    {
        if (registry is not { } set)
            return false;
        lock (set)
            return set.Remove(OwnerName);
    }

    /// <summary>Owner names other than this plugin's; empty without a registry.</summary>
    public static string[] OtherHolders()
    {
        if (registry is not { } set)
            return [];
        lock (set)
            return [.. set.Where(name => !string.Equals(name, OwnerName, StringComparison.Ordinal))];
    }
}

/// <summary>
/// Keeps AutoRetainer suppressed while held. Leases are counted: this plugin's owner name is in the shared holder
/// registry while any lease is live, and the last lease out unsuppresses AutoRetainer only when this plugin set the
/// suppression (or shared it with another registry holder) and no other holder remains. A suppression the user set
/// with no holder registered is left as found.
/// </summary>
internal sealed class AutoRetainerSuppressionLease : IDisposable
{
    private static readonly object Gate = new();
    private static int liveLeases;
    /// <summary>This plugin set the suppression, or found it set by another registry holder: the last holder out lifts it.</summary>
    private static bool ownsSuppression;

    private readonly IAutoRetainerIpc autoRetainer;
    private readonly bool counted;
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
        counted = available;
    }

    public bool Available { get; }
    public bool InitiallySuppressed { get; }
    public bool Changed { get; }
    public bool Restored { get; private set; }
    public string? RestoreError { get; private set; }
    /// <summary>Set when the last lease left AutoRetainer suppressed because another plugin still holds it.</summary>
    public string? LeftToHolders { get; private set; }

    public static bool TryAcquire(
        IAutoRetainerIpc autoRetainer,
        out AutoRetainerSuppressionLease? lease,
        out string message,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(autoRetainer);
        lease = null;

        if (!autoRetainer.IsAvailable)
        {
            lease = new(autoRetainer, available: false, initiallySuppressed: false, changed: false);
            message = "AutoRetainer is unavailable; no suppression is required.";
            return true;
        }

        lock (Gate)
        {
            var initialStateKnown = false;
            var initiallySuppressed = false;
            var wasBusy = false;
            var registered = false;
            try
            {
                if (autoRetainer.IsBusy)
                {
                    wasBusy = true;
                    warn?.Invoke("[MarketMafioso] AutoRetainer is busy; suppressing it and continuing the remote bell probe.");
                }

                initiallySuppressed = autoRetainer.IsSuppressed;
                initialStateKnown = true;
                // Registered before suppressing, so another plugin finishing meanwhile never lifts it under us.
                var shared = false;
                if (liveLeases == 0)
                {
                    shared = AutoRetainerHolderRegistry.Add();
                    registered = true;
                }
                if (!initiallySuppressed)
                {
                    autoRetainer.SetSuppressed(true);
                    if (!autoRetainer.IsSuppressed)
                    {
                        UndoFailedAcquire(autoRetainer, registered, restore: true);
                        message = "AutoRetainer did not confirm suppression; no bell interaction was sent.";
                        return false;
                    }
                    ownsSuppression = true;
                }
                else if (shared)
                {
                    // Another Gooseworks plugin set it: it is shared now, and whichever holder leaves last lifts it.
                    ownsSuppression = true;
                }

                liveLeases++;
                lease = new(autoRetainer, available: true, initiallySuppressed, changed: !initiallySuppressed);
                message = initiallySuppressed
                    ? "AutoRetainer was already suppressed."
                    : "AutoRetainer suppression acquired.";
                if (wasBusy)
                    message = $"AutoRetainer was busy. {message}";
                return true;
            }
            catch (Exception ex)
            {
                UndoFailedAcquire(autoRetainer, registered, restore: initialStateKnown && !initiallySuppressed);
                message = $"Unable to suppress AutoRetainer; no bell interaction was sent. {ex.Message}";
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        if (!counted)
        {
            Restored = true;
            return;
        }

        lock (Gate)
        {
            liveLeases = Math.Max(0, liveLeases - 1);
            if (liveLeases > 0)
            {
                // Another lease of this plugin still needs it.
                Restored = true;
                return;
            }
            AutoRetainerHolderRegistry.Remove();
            if (!ownsSuppression)
            {
                Restored = true;
                return;
            }
            if (AutoRetainerHolderRegistry.OtherHolders() is { Length: > 0 } others)
            {
                // Another Gooseworks plugin still holds it: left suppressed; that plugin lifts it when it is done.
                ownsSuppression = false;
                LeftToHolders = string.Join(", ", others);
                Restored = true;
                return;
            }
            try
            {
                autoRetainer.SetSuppressed(false);
                ownsSuppression = false;
                Restored = true;
            }
            catch (Exception ex)
            {
                // Ownership is kept, so the next lease's release retries the restore instead of mistaking this
                // plugin's own suppression for one the user set.
                RestoreError = ex.Message;
            }
        }
    }

    /// <summary>Holding <see cref="Gate"/>: withdraws what a failed acquire did, never lifting another holder's suppression.</summary>
    private static void UndoFailedAcquire(IAutoRetainerIpc autoRetainer, bool registered, bool restore)
    {
        if (registered)
            AutoRetainerHolderRegistry.Remove();
        if (!restore || liveLeases > 0 || AutoRetainerHolderRegistry.OtherHolders().Length > 0)
            return;
        try { autoRetainer.SetSuppressed(false); }
        catch { }
    }
}
