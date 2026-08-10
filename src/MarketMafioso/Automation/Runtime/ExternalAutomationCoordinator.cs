using System;
using System.Collections.Generic;
using Dalamud.Plugin;
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

public sealed class ExternalAutomationCoordinator : IDisposable
{
    private const string TextAdvanceStopRequests = "TextAdvance.StopRequests";
    private const string YesAlreadyStopRequests = "YesAlready.StopRequests";
    private const string DropboxStopRequests = "Dropbox.StopRequests";
    private const string StopRequestOwner = "MarketMafioso";

    private readonly IPluginDataStore pluginDataStore;
    private readonly IPluginLog log;
    private bool textAdvanceSuppressed;
    private bool tradeAutoConfirmSuppressed;
    private bool dropboxAutoAcceptSuppressed;

    internal ExternalAutomationCoordinator(IPluginDataStore pluginDataStore, IPluginLog log)
    {
        this.pluginDataStore = pluginDataStore;
        this.log = log;
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
        RestoreTradeAutoConfirm();
        RestoreDropboxAutoAccept();
    }
}
