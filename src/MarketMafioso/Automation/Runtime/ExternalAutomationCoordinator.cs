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

public sealed class ExternalAutomationCoordinator : IDisposable
{
    private const string TextAdvanceStopRequests = "TextAdvance.StopRequests";
    private const string YesAlreadyStopRequests = "YesAlready.StopRequests";
    private const string DropboxStopRequests = "Dropbox.StopRequests";
    private const string StopRequestOwner = "MarketMafioso";
    private const string PandoraAutoSelectTurnin = "AutoSelectTurnin";

    private readonly IPluginDataStore pluginDataStore;
    private readonly IPluginLog log;
    private readonly IPandoraFeatureControl? pandoraFeatureControl;
    private bool textAdvanceSuppressed;
    private bool tradeAutoConfirmSuppressed;
    private bool dropboxAutoAcceptSuppressed;
    private bool pandoraAutoSelectTurninSuppressed;

    internal ExternalAutomationCoordinator(
        IPluginDataStore pluginDataStore,
        IPluginLog log,
        IPandoraFeatureControl? pandoraFeatureControl = null)
    {
        this.pluginDataStore = pluginDataStore;
        this.log = log;
        this.pandoraFeatureControl = pandoraFeatureControl;
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
        if (!pluginDataStore.TryGetData<HashSet<string>>(YesAlreadyStopRequests, out var stopRequests) || stopRequests == null)
            return;

        if (stopRequests.Add(StopRequestOwner))
        {
            tradeAutoConfirmSuppressed = true;
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

        if (stopRequests.Add(StopRequestOwner))
        {
            dropboxAutoAcceptSuppressed = true;
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
        RestoreTextAdvance();
        RestoreWorkshopRequestAutomation();
        RestoreTradeAutoConfirm();
        RestoreDropboxAutoAccept();
    }
}
