using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

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
    /// <summary>Written before MMF turns Pandora Auto-select Turn-ins off and deleted once it is back on.</summary>
    internal const string PandoraSuppressionMarkerFile = "pandora-auto-select-turnin-suppressed.marker";

    private readonly IPluginDataStore pluginDataStore;
    private readonly IPluginLog log;
    private readonly IPandoraFeatureControl? pandoraFeatureControl;
    private readonly ITextAdvanceExternalControl? textAdvanceControl;
    private bool textAdvanceCutsceneSkipHeld;
    private bool textAdvanceSuppressed;
    private bool tradeAutoConfirmSuppressed;
    private bool retainTradeSuppressionOnDispose;
    private bool dropboxAutoAcceptSuppressed;
    private bool pandoraAutoSelectTurninSuppressed;
    // Every coordinator shares the "MarketMafioso" owner entry, so count live holders across instances.
    private static int liveTradeAutoConfirmHolders;
    private static int liveDropboxHolders;
    private static int liveTextAdvanceHolders;
    private static readonly object TextAdvanceStopRequestSync = new();
    private readonly string? pandoraMarkerPath;

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
        pandoraMarkerPath = string.IsNullOrWhiteSpace(stateDirectory) ? null : Path.Combine(stateDirectory, PandoraSuppressionMarkerFile);
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
        if (!pluginDataStore.TryGetData<HashSet<string>>(TextAdvanceStopRequests, out var stopRequests) || stopRequests == null)
            return;

        lock (TextAdvanceStopRequestSync)
        {
            if (!textAdvanceSuppressed)
            {
                textAdvanceSuppressed = true;
                liveTextAdvanceHolders++;
            }
            if (stopRequests.Add(StopRequestOwner))
                log.Debug("[MarketMafioso] Temporarily paused TextAdvance during workshop material request.");
        }
    }

    public void RestoreTextAdvance()
    {
        if (!textAdvanceSuppressed)
            return;

        lock (TextAdvanceStopRequestSync)
        {
            textAdvanceSuppressed = false;
            liveTextAdvanceHolders = Math.Max(0, liveTextAdvanceHolders - 1);
            if (liveTextAdvanceHolders > 0)
                return;
            if (pluginDataStore.TryGetData<HashSet<string>>(TextAdvanceStopRequests, out var stopRequests) && stopRequests?.Remove(StopRequestOwner) == true)
                log.Debug("[MarketMafioso] Restored TextAdvance after workshop material request.");
        }
    }

    /// <summary>
    /// Removes a "MarketMafioso" TextAdvance stop request left behind by an earlier load. An entry a live
    /// coordinator of this load still holds is left alone.
    /// </summary>
    public void ReleaseStaleTextAdvanceSuppression()
    {
        lock (TextAdvanceStopRequestSync)
        {
            if (liveTextAdvanceHolders == 0 &&
                pluginDataStore.TryGetData<HashSet<string>>(TextAdvanceStopRequests, out var stopRequests) &&
                stopRequests?.Remove(StopRequestOwner) == true)
                log.Warning("[MarketMafioso] Released a TextAdvance pause left over from an earlier MarketMafioso load.");
        }
    }

    /// <summary>
    /// An earlier load turned Pandora Auto-select Turn-ins off and never turned it back on (its marker is still
    /// there): turn it back on now, unless this coordinator is itself holding it off for a hand-in.
    /// </summary>
    public void RestoreStalePandoraSuppression()
    {
        if (pandoraFeatureControl == null || pandoraAutoSelectTurninSuppressed || !PandoraMarkerExists())
            return;
        try
        {
            pandoraFeatureControl.SetEnabled(PandoraAutoSelectTurnin, true);
        }
        catch (Exception ex)
        {
            // Pandora may load after MMF: the marker stays, and the next hand-in adopts and restores the suppression.
            log.Warning(ex, "[MarketMafioso] An earlier MarketMafioso load left Pandora Auto-select Turn-ins turned off and it could not be turned back on yet ({Marker}).", pandoraMarkerPath ?? string.Empty);
            return;
        }
        DeletePandoraMarker();
        log.Warning("[MarketMafioso] Turned Pandora Auto-select Turn-ins back on: an earlier MarketMafioso load turned it off for a workshop hand-in and never restored it.");
    }

    private bool PandoraMarkerExists()
    {
        try
        {
            return pandoraMarkerPath is not null && File.Exists(pandoraMarkerPath);
        }
        catch
        {
            return false;
        }
    }

    private void WritePandoraMarker()
    {
        if (pandoraMarkerPath is null)
            return;
        try
        {
            File.WriteAllText(pandoraMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] Could not write the Pandora Auto-select Turn-ins marker {Marker}; an unload that fails to restore it would leave it off.", pandoraMarkerPath);
        }
    }

    private void DeletePandoraMarker()
    {
        if (pandoraMarkerPath is null)
            return;
        try
        {
            File.Delete(pandoraMarkerPath);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] Could not delete the Pandora Auto-select Turn-ins marker {Marker}.", pandoraMarkerPath);
        }
    }

    public void SuppressWorkshopRequestAutomation()
    {
        if (pandoraFeatureControl == null || pandoraAutoSelectTurninSuppressed)
            return;

        bool? enabled;
        try
        {
            enabled = pandoraFeatureControl.IsEnabled(PandoraAutoSelectTurnin);
        }
        catch (Exception ex)
        {
            // Pandora is optional. An unavailable IPC provider means there is no competing feature to coordinate.
            log.Verbose(ex, "[MarketMafioso] Pandora Auto-select Turn-ins coordination is unavailable.");
            return;
        }

        if (enabled != true)
        {
            // Off while our marker is still there: an earlier load turned it off and never restored it. Adopt that
            // suppression so this hand-in's restore turns it back on.
            if (enabled == false && PandoraMarkerExists())
            {
                pandoraAutoSelectTurninSuppressed = true;
                log.Warning("[MarketMafioso] Adopted a Pandora Auto-select Turn-ins suppression left by an earlier MarketMafioso load; it is turned back on when this hand-in ends.");
            }
            return;
        }

        // The marker is durable before the feature goes off, so a crash or a failed unload is repaired on load.
        WritePandoraMarker();
        try
        {
            pandoraFeatureControl.SetEnabled(PandoraAutoSelectTurnin, false);
        }
        catch (Exception ex)
        {
            DeletePandoraMarker();
            throw new InvalidOperationException(
                "Could not acquire workshop Request-window ownership from Pandora Auto-select Turn-ins.",
                ex);
        }

        pandoraAutoSelectTurninSuppressed = true;
        log.Debug("[MarketMafioso] Temporarily paused Pandora Auto-select Turn-ins while MMF owns the workshop request window.");
    }

    public void RestoreWorkshopRequestAutomation()
    {
        if (!pandoraAutoSelectTurninSuppressed || pandoraFeatureControl == null)
            return;

        try
        {
            pandoraFeatureControl.SetEnabled(PandoraAutoSelectTurnin, true);
            pandoraAutoSelectTurninSuppressed = false;
            DeletePandoraMarker();
            log.Debug("[MarketMafioso] Restored Pandora Auto-select Turn-ins after MMF released the workshop request window.");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] Could not restore Pandora Auto-select Turn-ins after workshop request ownership ended.");
        }
    }

    public void SuppressTradeAutoConfirm()
    {
        SuppressDropboxAutoAccept();
        if (!pluginDataStore.TryGetData<HashSet<string>>(YesAlreadyStopRequests, out var stopRequests) || stopRequests == null)
            return;

        if (!tradeAutoConfirmSuppressed)
            System.Threading.Interlocked.Increment(ref liveTradeAutoConfirmHolders);
        tradeAutoConfirmSuppressed = true;
        if (stopRequests.Add(StopRequestOwner))
        {
            log.Debug("[MarketMafioso] Temporarily paused YesAlready during Trade Queue execution.");
        }
    }

    public void RestoreTradeAutoConfirm()
    {
        if (!tradeAutoConfirmSuppressed)
            return;

        if (pluginDataStore.TryGetData<HashSet<string>>(YesAlreadyStopRequests, out var stopRequests) && stopRequests?.Remove(StopRequestOwner) == true)
            log.Debug("[MarketMafioso] Restored YesAlready after Trade Queue execution.");

        tradeAutoConfirmSuppressed = false;
        System.Threading.Interlocked.Decrement(ref liveTradeAutoConfirmHolders);
    }

    public void SuppressDropboxAutoAccept()
    {
        if (!pluginDataStore.TryGetData<HashSet<string>>(DropboxStopRequests, out var stopRequests) || stopRequests == null)
            return;

        if (!dropboxAutoAcceptSuppressed)
            System.Threading.Interlocked.Increment(ref liveDropboxHolders);
        dropboxAutoAcceptSuppressed = true;
        if (stopRequests.Add(StopRequestOwner))
        {
            log.Debug("[MarketMafioso] Paused Dropbox while MMF owns trade auto-accept.");
        }
    }

    public void RestoreDropboxAutoAccept()
    {
        if (!dropboxAutoAcceptSuppressed)
            return;

        if (pluginDataStore.TryGetData<HashSet<string>>(DropboxStopRequests, out var stopRequests) && stopRequests?.Remove(StopRequestOwner) == true)
            log.Debug("[MarketMafioso] Restored Dropbox trade auto-accept ownership.");

        dropboxAutoAcceptSuppressed = false;
        System.Threading.Interlocked.Decrement(ref liveDropboxHolders);
    }

    /// <summary>
    /// Removes a "MarketMafioso" YesAlready/Dropbox stop request left behind by an earlier load, even though this
    /// instance never set it. An entry a live coordinator still holds is left alone.
    /// </summary>
    public void ReleaseStaleTradeSuppression()
    {
        if (System.Threading.Volatile.Read(ref liveTradeAutoConfirmHolders) == 0 &&
            pluginDataStore.TryGetData<HashSet<string>>(YesAlreadyStopRequests, out var yesAlready) &&
            yesAlready?.Remove(StopRequestOwner) == true)
            log.Warning("[MarketMafioso] Released a YesAlready pause left over from an earlier MarketMafioso load.");
        if (System.Threading.Volatile.Read(ref liveDropboxHolders) == 0 &&
            pluginDataStore.TryGetData<HashSet<string>>(DropboxStopRequests, out var dropbox) &&
            dropbox?.Remove(StopRequestOwner) == true)
            log.Warning("[MarketMafioso] Released a Dropbox pause left over from an earlier MarketMafioso load.");
    }

    public void Dispose()
    {
        // Each restore runs whatever the one before it threw.
        Step(ReleaseTextAdvanceCutsceneSkip, "TextAdvance external control");
        Step(RestoreTextAdvance, "the TextAdvance pause");
        Step(RestoreWorkshopRequestAutomation, "Pandora Auto-select Turn-ins");
        if (!retainTradeSuppressionOnDispose)
        {
            Step(RestoreTradeAutoConfirm, "the YesAlready pause");
            Step(RestoreDropboxAutoAccept, "the Dropbox pause");
        }

        void Step(Action restore, string what)
        {
            try
            {
                restore();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[MarketMafioso] Could not release {What} while unloading.", what);
            }
        }
    }

    public void RetainTradeSuppressionOnDispose() => retainTradeSuppressionOnDispose = true;
}
