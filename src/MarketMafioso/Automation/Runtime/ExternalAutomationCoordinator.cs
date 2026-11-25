using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation;
using Franthropy.Dalamud.Runtime;

namespace MarketMafioso.Automation.Runtime;

internal interface IPluginDataStore
{
    bool TryGetData<T>(string key, out T? data)
        where T : class;
}

internal sealed class DalamudPluginDataStore(IDalamudPluginInterface pluginInterface) : IPluginDataStore
{
    public bool TryGetData<T>(string key, out T? data)
        where T : class =>
        pluginInterface.TryGetData(key, out data);
}

internal interface IPandoraFeatureControl
{
    bool? IsEnabled(string internalFeatureName);

    void SetEnabled(string internalFeatureName, bool enabled);
}

internal sealed class DalamudPandoraFeatureControl(IDalamudPluginInterface pluginInterface) : IPandoraFeatureControl
{
    private readonly ICallGateSubscriber<string, bool?> getFeatureEnabled =
        pluginInterface.GetIpcSubscriber<string, bool?>("PandorasBox.GetFeatureEnabledInternal");
    private readonly ICallGateSubscriber<string, bool, object> setFeatureEnabled =
        pluginInterface.GetIpcSubscriber<string, bool, object>("PandorasBox.SetFeatureEnabledInternal");

    public bool? IsEnabled(string internalFeatureName) => getFeatureEnabled.InvokeFunc(internalFeatureName);

    public void SetEnabled(string internalFeatureName, bool enabled) =>
        setFeatureEnabled.InvokeAction(internalFeatureName, enabled);
}

// Mirror of TextAdvance's ExternalTerritoryConfig; Dalamud IPC converts it by field
// name. true/false forces a feature while MMF holds control, null keeps the user's.
internal sealed class TextAdvanceExternalTerritoryConfig
{
    public bool? EnableQuestAccept;
    public bool? EnableQuestComplete;
    public bool? EnableRewardPick;
    public bool? EnableRequestHandin;
    public bool? EnableCutsceneEsc;
    public bool? EnableCutsceneSkipConfirm;
    public bool? EnableTalkSkip;
    public bool? EnableRequestFill;
    public bool? EnableAutoInteract;
}

internal interface ITextAdvanceExternalControl
{
    bool Enable(string requester, TextAdvanceExternalTerritoryConfig config);

    bool Disable(string requester);
}

internal sealed class DalamudTextAdvanceExternalControl(IDalamudPluginInterface pluginInterface) : ITextAdvanceExternalControl
{
    private readonly ICallGateSubscriber<string, TextAdvanceExternalTerritoryConfig, bool> enable =
        pluginInterface.GetIpcSubscriber<string, TextAdvanceExternalTerritoryConfig, bool>("TextAdvance.EnableExternalControl");
    private readonly ICallGateSubscriber<string, bool> disable =
        pluginInterface.GetIpcSubscriber<string, bool>("TextAdvance.DisableExternalControl");

    public bool Enable(string requester, TextAdvanceExternalTerritoryConfig config) => enable.InvokeFunc(requester, config);

    public bool Disable(string requester) => disable.InvokeFunc(requester);
}

public sealed class ExternalAutomationCoordinator : IDisposable
{
    // TextAdvance skips the cutscene (Esc, then confirms the skip prompt) and does
    // nothing else, whatever the user's own TextAdvance territory setup says.
    internal static TextAdvanceExternalTerritoryConfig CutsceneSkipOnly => new()
    {
        EnableCutsceneEsc = true,
        EnableCutsceneSkipConfirm = true,
        EnableQuestAccept = false,
        EnableQuestComplete = false,
        EnableRewardPick = false,
        EnableRequestHandin = false,
        EnableRequestFill = false,
        EnableTalkSkip = false,
        EnableAutoInteract = false,
    };

    private const string TextAdvanceStopRequests = "TextAdvance.StopRequests";
    private const string YesAlreadyStopRequests = "YesAlready.StopRequests";
    private const string DropboxStopRequests = "Dropbox.StopRequests";
    private const string StopRequestOwner = "MarketMafioso";
    private const string PandoraAutoSelectTurnin = "AutoSelectTurnin";
    /// <summary>Names the marker written before MMF turns Pandora Auto-select Turn-ins off and deleted once it is back on.</summary>
    internal const string PandoraSuppressionMarkerName = "pandora-auto-select-turnin";
    /// <summary>The marker builds before the shared marker wrote; still restored at load so an upgrade loses none.</summary>
    internal const string LegacyPandoraSuppressionMarkerFile = "pandora-auto-select-turnin-suppressed.marker";

    private readonly IPluginDataStore pluginDataStore;
    private readonly IPluginLog log;
    private readonly IPandoraFeatureControl? pandoraFeatureControl;
    private readonly ITextAdvanceExternalControl? textAdvanceControl;
    private bool textAdvanceCutsceneSkipHeld;
    // Every coordinator writes the same "MarketMafioso" owner entry, so each set counts its live holders across
    // coordinators and only the last release removes the entry.
    private static readonly SharedStopRequest TextAdvancePause = new(TextAdvanceStopRequests);
    private static readonly SharedStopRequest YesAlreadyPause = new(YesAlreadyStopRequests);
    private static readonly SharedStopRequest DropboxPause = new(DropboxStopRequests);
    // This coordinator's hold on each set: taken at most once, so a repeated or stray release cannot drop another's.
    private bool textAdvanceSuppressed;
    private bool tradeAutoConfirmSuppressed;
    private bool dropboxHeld;
    // Why this coordinator wants Dropbox paused: trade auto-accept ownership (per-frame) and/or a trade-confirm run.
    private bool dropboxWantedForAutoAccept;
    private bool dropboxWantedForTradeConfirm;
    private bool retainTradeSuppressionOnDispose;
    // Pandora coordination needs a state directory for its marker; only the workshop assembly coordinator has both.
    private readonly PersistedSuppressionMarker? pandoraMarker;
    private readonly string? legacyPandoraMarkerPath;
    /// <summary>Held while MMF keeps Pandora Auto-select Turn-ins off for a hand-in.</summary>
    private PersistedSuppressionMarker.Scope? pandoraSuppression;
    /// <summary>A hand-in's restore failed: later restores (and unload) retry it, whether or not the marker was written.</summary>
    private bool pandoraRestorePending;
    /// <summary>The marker's last restore action threw (the marker swallows and logs it).</summary>
    private bool lastPandoraRestoreFailed;

    internal ExternalAutomationCoordinator(
        IPluginDataStore pluginDataStore,
        IPluginLog log,
        IPandoraFeatureControl? pandoraFeatureControl = null,
        ITextAdvanceExternalControl? textAdvanceControl = null,
        string? stateDirectory = null)
    {
        this.pluginDataStore = pluginDataStore;
        this.log = log;
        this.pandoraFeatureControl = pandoraFeatureControl;
        this.textAdvanceControl = textAdvanceControl;
        if (pandoraFeatureControl is not null && !string.IsNullOrWhiteSpace(stateDirectory))
        {
            pandoraMarker = new PersistedSuppressionMarker(
                stateDirectory,
                PandoraSuppressionMarkerName,
                suppress: () => pandoraFeatureControl.SetEnabled(PandoraAutoSelectTurnin, false),
                restore: () =>
                {
                    lastPandoraRestoreFailed = true;
                    pandoraFeatureControl.SetEnabled(PandoraAutoSelectTurnin, true);
                    lastPandoraRestoreFailed = false;
                },
                warning: message => log.Warning("[MarketMafioso] Pandora Auto-select Turn-ins: {Message}", message),
                diagnostic: message => log.Debug("[MarketMafioso] Pandora Auto-select Turn-ins: {Message}", message));
            legacyPandoraMarkerPath = Path.Combine(stateDirectory, LegacyPandoraSuppressionMarkerFile);
        }
    }

    // Takes TextAdvance external control so it skips workshop cutscenes. Safe to
    // call again while held (re-asserts control). Returns why it could not, or null.
    public string? AcquireTextAdvanceCutsceneSkip()
    {
        if (textAdvanceControl == null)
            return "TextAdvance control is not available in this build.";

        bool granted;
        try
        {
            granted = textAdvanceControl.Enable(StopRequestOwner, CutsceneSkipOnly);
        }
        catch (Exception ex)
        {
            textAdvanceCutsceneSkipHeld = false;
            log.Verbose(ex, "[MarketMafioso] TextAdvance external control IPC is unavailable.");
            return "TextAdvance is not loaded. Install or enable it in the plugin installer.";
        }

        if (!granted)
        {
            textAdvanceCutsceneSkipHeld = false;
            return "another plugin is controlling TextAdvance right now. Let it finish first.";
        }

        if (!textAdvanceCutsceneSkipHeld)
            log.Debug("[MarketMafioso] Took TextAdvance external control to skip workshop cutscenes.");
        textAdvanceCutsceneSkipHeld = true;
        return null;
    }

    public void ReleaseTextAdvanceCutsceneSkip()
    {
        if (!textAdvanceCutsceneSkipHeld || textAdvanceControl == null)
            return;

        textAdvanceCutsceneSkipHeld = false;
        try
        {
            if (textAdvanceControl.Disable(StopRequestOwner))
                log.Debug("[MarketMafioso] Released TextAdvance external control.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] Could not release TextAdvance external control.");
        }
    }

    /// <summary>
    /// Pauses TextAdvance under the shared "MarketMafioso" stop request. Several coordinators share that entry, so
    /// each counts as a holder and the entry is removed only when the last holder restores.
    /// </summary>
    public void SuppressTextAdvance()
    {
        if (TextAdvancePause.Acquire(pluginDataStore, ref textAdvanceSuppressed))
            log.Debug("[MarketMafioso] Temporarily paused TextAdvance during workshop automation.");
    }

    public void RestoreTextAdvance()
    {
        if (TextAdvancePause.Release(pluginDataStore, ref textAdvanceSuppressed))
            log.Debug("[MarketMafioso] Restored TextAdvance after workshop automation.");
    }

    /// <summary>
    /// Removes a "MarketMafioso" TextAdvance stop request left behind by an earlier load. An entry a live
    /// coordinator of this load still holds is left alone.
    /// </summary>
    public void ReleaseStaleTextAdvanceSuppression()
    {
        if (TextAdvancePause.ReleaseLeftover(pluginDataStore))
            log.Warning("[MarketMafioso] Released a TextAdvance pause left over from an earlier MarketMafioso load.");
    }

    /// <summary>
    /// An earlier load turned Pandora Auto-select Turn-ins off and never turned it back on (its marker is still
    /// there): turn it back on now, unless this coordinator is itself holding it off for a hand-in.
    /// </summary>
    public void RestoreStalePandoraSuppression()
    {
        if (pandoraMarker is not { } marker || pandoraSuppression is not null)
            return;
        if (LegacyPandoraMarkerExists())
        {
            try
            {
                pandoraFeatureControl!.SetEnabled(PandoraAutoSelectTurnin, true);
            }
            catch (Exception ex)
            {
                // Pandora may load after MMF: the marker stays, and the next hand-in adopts and restores the suppression.
                log.Warning(ex, "[MarketMafioso] An earlier MarketMafioso load left Pandora Auto-select Turn-ins turned off and it could not be turned back on yet ({Marker}).", legacyPandoraMarkerPath!);
                return;
            }
            DeleteLegacyPandoraMarker();
            log.Warning("[MarketMafioso] Turned Pandora Auto-select Turn-ins back on: an earlier MarketMafioso load turned it off for a workshop hand-in and never restored it.");
        }
        // Logs SuppressionLeftoverRestored at Warning, or SuppressionRestoreFailed (Pandora not loaded yet) and keeps
        // the marker for the next hand-in to adopt.
        marker.RestoreLeftovers();
    }

    private bool LegacyPandoraMarkerExists()
    {
        try
        {
            return legacyPandoraMarkerPath is not null && File.Exists(legacyPandoraMarkerPath);
        }
        catch
        {
            return false;
        }
    }

    private void DeleteLegacyPandoraMarker()
    {
        if (legacyPandoraMarkerPath is null)
            return;
        try
        {
            File.Delete(legacyPandoraMarkerPath);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] Could not delete the Pandora Auto-select Turn-ins marker {Marker}.", legacyPandoraMarkerPath);
        }
    }

    public void SuppressWorkshopRequestAutomation()
    {
        if (pandoraMarker is not { } marker || pandoraSuppression is not null)
            return;

        bool? enabled;
        try
        {
            enabled = pandoraFeatureControl!.IsEnabled(PandoraAutoSelectTurnin);
        }
        catch (Exception ex)
        {
            // Pandora is optional. An unavailable IPC provider means there is no competing feature to coordinate.
            log.Verbose(ex, "[MarketMafioso] Pandora Auto-select Turn-ins coordination is unavailable.");
            return;
        }

        if (enabled != true)
        {
            // Off while our marker is still there: an earlier load (or a failed restore) turned it off and never
            // restored it. Adopt that suppression so this hand-in's restore turns it back on. Suppressing again is a
            // no-op on Pandora's side; it only rewrites the marker in the current format.
            if (enabled == false && (marker.IsMarked || LegacyPandoraMarkerExists()))
            {
                try
                {
                    pandoraSuppression = marker.Acquire();
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[MarketMafioso] Could not adopt a Pandora Auto-select Turn-ins suppression left by an earlier MarketMafioso load.");
                    return;
                }
                pandoraRestorePending = false;
                DeleteLegacyPandoraMarker();
                log.Warning("[MarketMafioso] Adopted a Pandora Auto-select Turn-ins suppression left by an earlier MarketMafioso load; it is turned back on when this hand-in ends.");
            }
            return;
        }

        // The marker is durable before the feature goes off, so a crash or a failed unload is repaired on load.
        try
        {
            pandoraSuppression = marker.Acquire();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not acquire workshop Request-window ownership from Pandora Auto-select Turn-ins.",
                ex);
        }

        pandoraRestorePending = false;
        // The shared marker covers this hold now; a legacy marker left from a failed load-time restore would otherwise
        // make a later load force Pandora back on although the user may have changed it since.
        DeleteLegacyPandoraMarker();
        log.Debug("[MarketMafioso] Temporarily paused Pandora Auto-select Turn-ins while MMF owns the workshop request window.");
    }

    public void RestoreWorkshopRequestAutomation()
    {
        if (pandoraMarker is not { } marker)
            return;

        if (pandoraSuppression is { } suppression)
        {
            pandoraSuppression = null;
            // Restores and deletes the marker; a failed restore is logged (SuppressionRestoreFailed) and keeps it.
            // Pending follows the restore itself, not the marker, which may never have been written.
            lastPandoraRestoreFailed = false;
            suppression.Dispose();
            pandoraRestorePending = lastPandoraRestoreFailed || marker.IsMarked;
            if (!pandoraRestorePending)
            {
                DeleteLegacyPandoraMarker();
                log.Debug("[MarketMafioso] Restored Pandora Auto-select Turn-ins after MMF released the workshop request window.");
            }
            return;
        }

        if (!pandoraRestorePending)
            return;
        // An earlier restore of this load failed: retry it, through the marker when it was written.
        if (marker.IsMarked)
        {
            if (!marker.RestoreLeftovers())
                return;
        }
        else
        {
            try
            {
                pandoraFeatureControl!.SetEnabled(PandoraAutoSelectTurnin, true);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[MarketMafioso] PandoraRestoreRetryFailed: Pandora Auto-select Turn-ins is still off after a workshop hand-in; retried on the next release or unload.");
                return;
            }
        }
        pandoraRestorePending = false;
        DeleteLegacyPandoraMarker();
    }

    /// <summary>Pauses YesAlready and Dropbox while MMF confirms trades itself; <see cref="RestoreTradeAutoConfirm"/> releases both.</summary>
    public void SuppressTradeAutoConfirm()
    {
        dropboxWantedForTradeConfirm = true;
        SyncDropboxHold();
        if (YesAlreadyPause.Acquire(pluginDataStore, ref tradeAutoConfirmSuppressed))
            log.Debug("[MarketMafioso] Temporarily paused YesAlready while MMF confirms trades.");
    }

    public void RestoreTradeAutoConfirm()
    {
        try
        {
            if (YesAlreadyPause.Release(pluginDataStore, ref tradeAutoConfirmSuppressed))
                log.Debug("[MarketMafioso] Restored YesAlready after MMF finished confirming trades.");
        }
        finally
        {
            dropboxWantedForTradeConfirm = false;
            SyncDropboxHold();
        }
    }

    /// <summary>Per-frame: pauses Dropbox while MMF owns trade auto-accept. Leaves a trade-confirm hold in place.</summary>
    public void SuppressDropboxAutoAccept()
    {
        dropboxWantedForAutoAccept = true;
        SyncDropboxHold();
    }

    /// <summary>Per-frame: drops the auto-accept reason. Dropbox stays paused while this or another coordinator still needs it.</summary>
    public void RestoreDropboxAutoAccept()
    {
        dropboxWantedForAutoAccept = false;
        SyncDropboxHold();
    }

    private void SyncDropboxHold()
    {
        if (dropboxWantedForAutoAccept || dropboxWantedForTradeConfirm)
        {
            if (DropboxPause.Acquire(pluginDataStore, ref dropboxHeld))
                log.Debug("[MarketMafioso] Paused Dropbox while MMF owns trade acceptance.");
        }
        else if (DropboxPause.Release(pluginDataStore, ref dropboxHeld))
        {
            log.Debug("[MarketMafioso] Restored Dropbox trade auto-accept.");
        }
    }

    /// <summary>
    /// Removes a "MarketMafioso" YesAlready/Dropbox stop request left behind by an earlier load, even though this
    /// instance never set it. An entry a live coordinator still holds is left alone.
    /// </summary>
    public void ReleaseStaleTradeSuppression()
    {
        if (YesAlreadyPause.ReleaseLeftover(pluginDataStore))
            log.Warning("[MarketMafioso] Released a YesAlready pause left over from an earlier MarketMafioso load.");
        if (DropboxPause.ReleaseLeftover(pluginDataStore))
            log.Warning("[MarketMafioso] Released a Dropbox pause left over from an earlier MarketMafioso load.");
    }

    public void Dispose()
    {
        // Each restore runs whatever the one before it threw.
        var steps = new List<(string, Action)>
        {
            ("TextAdvance external control", ReleaseTextAdvanceCutsceneSkip),
            ("the TextAdvance pause", RestoreTextAdvance),
            ("Pandora Auto-select Turn-ins", RestoreWorkshopRequestAutomation),
        };
        if (!retainTradeSuppressionOnDispose)
        {
            steps.Add(("the YesAlready pause", RestoreTradeAutoConfirm));
            steps.Add(("the Dropbox pause", RestoreDropboxAutoAccept));
        }

        DisposalSteps.DisposeAll(
            (what, ex) => log.Warning(ex, "[MarketMafioso] Could not release {What} while unloading.", what),
            [.. steps]);
    }

    public void RetainTradeSuppressionOnDispose() => retainTradeSuppressionOnDispose = true;

    /// <summary>
    /// A stop-request set another plugin shares through Dalamud data, where every MMF coordinator writes the same
    /// "MarketMafioso" entry. Each coordinator holds at most once (its own flag); the entry goes only when the last
    /// holder of this load releases, so a release from a coordinator that isn't holding never lifts another's pause.
    /// </summary>
    private sealed class SharedStopRequest(string dataKey)
    {
        private readonly object sync = new();
        private int holders;

        /// <summary>Takes the caller's hold once and makes sure the entry is set. True when the entry was newly added.</summary>
        public bool Acquire(IPluginDataStore store, ref bool held)
        {
            // The owning plugin isn't loaded: nothing to pause and nothing to hold.
            if (!store.TryGetData<HashSet<string>>(dataKey, out var stopRequests) || stopRequests == null)
                return false;

            lock (sync)
            {
                if (!held)
                {
                    held = true;
                    holders++;
                }
                return stopRequests.Add(StopRequestOwner);
            }
        }

        /// <summary>Drops the caller's hold, if it has one. True when that was the last hold and the entry was removed.</summary>
        public bool Release(IPluginDataStore store, ref bool held)
        {
            lock (sync)
            {
                if (!held)
                    return false;
                held = false;
                holders = Math.Max(0, holders - 1);
                if (holders > 0)
                    return false;
                return store.TryGetData<HashSet<string>>(dataKey, out var stopRequests) &&
                    stopRequests?.Remove(StopRequestOwner) == true;
            }
        }

        /// <summary>Removes an entry an earlier load left behind, when nothing of this load holds it.</summary>
        public bool ReleaseLeftover(IPluginDataStore store)
        {
            lock (sync)
            {
                return holders == 0 &&
                    store.TryGetData<HashSet<string>>(dataKey, out var stopRequests) &&
                    stopRequests?.Remove(StopRequestOwner) == true;
            }
        }
    }
}
