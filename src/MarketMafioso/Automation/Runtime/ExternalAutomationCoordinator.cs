using System;
using System.Collections.Generic;
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

    internal ExternalAutomationCoordinator(
        IPluginDataStore pluginDataStore,
        IPluginLog log,
        IPandoraFeatureControl? pandoraFeatureControl = null,
        ITextAdvanceExternalControl? textAdvanceControl = null)
    {
        this.pluginDataStore = pluginDataStore;
        this.log = log;
        this.pandoraFeatureControl = pandoraFeatureControl;
        this.textAdvanceControl = textAdvanceControl;
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

    public void SuppressTextAdvance()
    {
        if (!pluginDataStore.TryGetData<HashSet<string>>(TextAdvanceStopRequests, out var stopRequests) || stopRequests == null)
            return;

        if (stopRequests.Add(StopRequestOwner))
        {
            textAdvanceSuppressed = true;
            log.Debug("[MarketMafioso] Temporarily paused TextAdvance during workshop material request.");
        }
    }

    public void RestoreTextAdvance()
    {
        if (!textAdvanceSuppressed)
            return;

        if (pluginDataStore.TryGetData<HashSet<string>>(TextAdvanceStopRequests, out var stopRequests) && stopRequests?.Remove(StopRequestOwner) == true)
            log.Debug("[MarketMafioso] Restored TextAdvance after workshop material request.");

        textAdvanceSuppressed = false;
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
            return;

        try
        {
            pandoraFeatureControl.SetEnabled(PandoraAutoSelectTurnin, false);
        }
        catch (Exception ex)
        {
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
    }

    public void SuppressDropboxAutoAccept()
    {
        if (!pluginDataStore.TryGetData<HashSet<string>>(DropboxStopRequests, out var stopRequests) || stopRequests == null)
            return;

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
    }

    public void Dispose()
    {
        ReleaseTextAdvanceCutsceneSkip();
        RestoreTextAdvance();
        RestoreWorkshopRequestAutomation();
        if (!retainTradeSuppressionOnDispose)
        {
            RestoreTradeAutoConfirm();
            RestoreDropboxAutoAccept();
        }
    }

    public void RetainTradeSuppressionOnDispose() => retainTradeSuppressionOnDispose = true;
}
