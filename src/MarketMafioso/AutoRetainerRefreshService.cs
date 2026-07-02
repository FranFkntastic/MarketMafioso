using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MarketMafioso.Automation.Retainers;

namespace MarketMafioso;

public sealed class AutoRetainerRefreshService : IDisposable
{
    private const string PluginName = "MarketMafioso";

    private const string AutoRetainerInternalName = "AutoRetainer";
    private const string AutoRetainerInit = "AutoRetainer.Init";
    private const string OnRetainerListTaskButtonsDraw = "AutoRetainer.OnRetainerListTaskButtonsDraw";
    private const string OnRetainerListCustomTask = "AutoRetainer.OnRetainerListCustomTask";
    private const string OnRetainerAdditionalTask = "AutoRetainer.OnRetainerAdditionalTask";
    private const string RequestRetainerPostprocess = "AutoRetainer.RequestPostprocess";
    private const string OnRetainerReadyForPostprocess = "AutoRetainer.OnRetainerReadyForPostprocess";
    private const string FinishRetainerPostprocessRequest = "AutoRetainer.FinishPostprocessRequest";

    private const string RetainerListAddon = RetainerInventoryAddonNames.RetainerList;
    private const string SelectStringAddon = RetainerInventoryAddonNames.SelectString;
    private const string SelectYesNoAddon = "SelectYesno";
    private const string RetainerInventoryLargeAddon = RetainerInventoryAddonNames.InventoryLarge;
    private const string RetainerInventorySmallAddon = RetainerInventoryAddonNames.InventorySmall;
    private const string RetainerTaskAddon = "RetainerTask";
    private const string RetainerTaskAskAddon = "RetainerTaskAsk";
    private const string RetainerTaskListAddon = "RetainerTaskList";
    private const string RetainerTaskResultAddon = "RetainerTaskResult";
    private const string RetainerTaskSupplyAddon = "RetainerTaskSupply";

    private static readonly string[] RetainerUiStateAddons =
    [
        RetainerListAddon,
        SelectStringAddon,
        SelectYesNoAddon,
        RetainerInventoryLargeAddon,
        RetainerInventorySmallAddon,
        RetainerTaskAddon,
        RetainerTaskAskAddon,
        RetainerTaskListAddon,
        RetainerTaskResultAddon,
        RetainerTaskSupplyAddon,
    ];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly IGameGui gameGui;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly RetainerCacheManager retainerCache;
    private readonly HttpReporter reporter;
    private readonly RetainerUiStateReader retainerUiStateReader;

    private bool isRefreshing;
    private bool isStartQueued;
    private bool skipPostprocessUntilManualStart;
    private int expectedRetainers;
    private int processedRetainers;
    private string lastStatus = "AutoRetainer refresh has not run.";

    public AutoRetainerRefreshService(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IGameGui gameGui,
        IObjectTable objectTable,
        IDataManager dataManager,
        RetainerCacheManager retainerCache,
        HttpReporter reporter)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.gameGui = gameGui;
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.retainerCache = retainerCache;
        this.reporter = reporter;
        retainerUiStateReader = new RetainerUiStateReader(gameGui);

        pluginInterface.GetIpcSubscriber<object>(OnRetainerListTaskButtonsDraw).Subscribe(DrawRetainerListButton);
        pluginInterface.GetIpcSubscriber<string, object>(OnRetainerAdditionalTask).Subscribe(OnRetainerAdditionalTaskReceived);
        pluginInterface.GetIpcSubscriber<string, string, object>(OnRetainerReadyForPostprocess).Subscribe(OnRetainerReadyForPostprocessReceived);
    }

    public bool IsRefreshing => isRefreshing;
    public bool IsStartQueued => isStartQueued;
    public int ExpectedRetainers => expectedRetainers;
    public int ProcessedRetainers => processedRetainers;
    public string LastStatus => lastStatus;
    public bool IsLoaded => pluginInterface.InstalledPlugins.Any(plugin =>
        plugin.IsLoaded &&
        string.Equals(plugin.InternalName, AutoRetainerInternalName, StringComparison.OrdinalIgnoreCase));

    public bool IsAvailable
    {
        get
        {
            try
            {
                pluginInterface.GetIpcSubscriber<object>(AutoRetainerInit).InvokeAction();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool CanStartRefresh => IsAvailable && IsRetainerListReady();

    public void StartFullRefresh()
    {
        if (isRefreshing)
        {
            lastStatus = "Retainer cache refresh is already running.";
            return;
        }

        if (!IsAvailable)
        {
            lastStatus = "AutoRetainer is not available.";
            return;
        }

        if (!IsRetainerListReady())
        {
            lastStatus = "Open the retainer list before starting a full refresh.";
            return;
        }

        expectedRetainers = GetExpectedRetainerCount();
        if (expectedRetainers <= 0)
        {
            lastStatus = "No retainers are available to refresh.";
            return;
        }

        skipPostprocessUntilManualStart = false;
        isStartQueued = true;
        lastStatus = "Retainer cache refresh queued. Keep the retainer list open.";
    }

    private void StartFullRefreshFromAutoRetainerOverlay()
    {
        try
        {
            processedRetainers = 0;
            isStartQueued = false;
            isRefreshing = true;
            retainerCache.BeginBatchRefresh();
            lastStatus = $"Refreshing retainer cache: 0/{expectedRetainers}.";

            pluginInterface.GetIpcSubscriber<string, object>(OnRetainerListCustomTask).InvokeAction(PluginName);
        }
        catch (Exception ex)
        {
            FailRefresh("Unable to start AutoRetainer custom task.", ex);
        }
    }

    private void DrawRetainerListButton()
    {
        try
        {
            var disableButton = isRefreshing || isStartQueued;
            if (disableButton)
                ImGui.BeginDisabled();

            ImGui.SameLine();
            if (ImGuiComponents.IconButton("MarketMafiosoRefresh", FontAwesomeIcon.BookOpen))
                StartFullRefresh();

            if (disableButton)
                ImGui.EndDisabled();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Refresh all retainer inventory caches and send one inventory report.");

            if (isStartQueued)
                StartFullRefreshFromAutoRetainerOverlay();
        }
        catch (Exception ex)
        {
            log.Error(ex, "[MarketMafioso] Error drawing AutoRetainer refresh button");
        }
    }

    private void OnRetainerAdditionalTaskReceived(string retainerName)
    {
        if (skipPostprocessUntilManualStart)
            return;

        try
        {
            pluginInterface.GetIpcSubscriber<string, object>(RequestRetainerPostprocess).InvokeAction(PluginName);
        }
        catch (Exception ex)
        {
            FailRefresh($"Unable to request postprocess for {retainerName}.", ex);
        }
    }

    private void OnRetainerReadyForPostprocessReceived(string pluginName, string retainerName)
    {
        if (pluginName != PluginName)
            return;

        _ = RefreshCurrentRetainerAsync(retainerName, isRefreshing);
    }

    private async Task RefreshCurrentRetainerAsync(string retainerName, bool partOfFullRefresh)
    {
        var phase = "starting refresh";
        try
        {
            lastStatus = partOfFullRefresh
                ? $"Refreshing {retainerName} ({processedRetainers}/{expectedRetainers})."
                : $"Piggyback refreshing {retainerName}.";

            phase = "active retainer session detection";
            await WaitForRetainerSessionAsync(retainerName).ConfigureAwait(false);

            phase = "retainer command menu recognition";
            await WaitForRetainerCommandMenuAsync(retainerName).ConfigureAwait(false);

            phase = "retainer inventory command selection";
            await RunOnFrameworkTickAsync(SelectEntrustOrWithdrawItems).ConfigureAwait(false);

            phase = "retainer inventory open";
            await WaitForAddonAsync("retainer inventory", RetainerInventoryLargeAddon, RetainerInventorySmallAddon).ConfigureAwait(false);

            phase = "retainer inventory close command";
            await RunOnFrameworkTickAsync(CloseRetainerInventory).ConfigureAwait(false);

            phase = "retainer inventory close confirmation";
            await WaitForRetainerInventoryClosedAsync().ConfigureAwait(false);

            phase = "refresh progress update";
            if (partOfFullRefresh)
            {
                processedRetainers++;
                lastStatus = $"Refreshing retainer cache: {processedRetainers}/{expectedRetainers}.";
            }
            else
            {
                lastStatus = $"Piggyback refreshed {retainerName}.";
            }

            phase = "AutoRetainer postprocess release";
            pluginInterface.GetIpcSubscriber<object>(FinishRetainerPostprocessRequest).InvokeAction();

            if (partOfFullRefresh && processedRetainers >= expectedRetainers)
            {
                phase = "batch report send";
                await FinishBatchAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            FailRefresh($"Retainer cache refresh failed during {phase} for {retainerName}.", ex);
        }
    }

    private async Task FinishBatchAsync()
    {
        isRefreshing = false;
        retainerCache.EndBatchRefresh();
        lastStatus = $"Retainer cache refresh complete: {processedRetainers}/{expectedRetainers}. Sending report...";

        await reporter.SendReportAsync().ConfigureAwait(false);
        lastStatus = $"Retainer cache refresh complete: {processedRetainers}/{expectedRetainers}. Report sent.";
    }

    private void FailRefresh(string message, Exception ex)
    {
        var wasFullRefresh = isRefreshing || isStartQueued;
        log.Error(ex, $"[MarketMafioso] {message}");
        isRefreshing = false;
        isStartQueued = false;
        skipPostprocessUntilManualStart = wasFullRefresh;
        retainerCache.EndBatchRefresh();
        lastStatus = message;

        try
        {
            pluginInterface.GetIpcSubscriber<object>(FinishRetainerPostprocessRequest).InvokeAction();
        }
        catch (Exception finishEx)
        {
            log.Warning(finishEx, "[MarketMafioso] Unable to release AutoRetainer postprocess lock after refresh failure");
        }
    }

    private unsafe int GetExpectedRetainerCount()
    {
        var retainerManager = RetainerManager.Instance();
        if (retainerManager == null)
            return 0;

        var count = 0;
        var retainers = retainerManager->Retainers;
        for (var i = 0; i < retainerManager->GetRetainerCount(); i++)
        {
            if (retainers[i].Available)
                count++;
        }

        return count;
    }

    private unsafe bool IsRetainerListReady()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(RetainerListAddon, 1);
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private async Task RunOnFrameworkTickAsync(System.Action action)
    {
        await Plugin.Framework.RunOnTick(action);
    }

    private async Task<T> RunOnFrameworkTickAsync<T>(Func<T> func)
    {
        return await Plugin.Framework.RunOnTick(func);
    }

    private async Task WaitForAddonAsync(string stateName, params string[] addonNames)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (await RunOnFrameworkTickAsync(() => IsAnyAddonReady(addonNames)).ConfigureAwait(false))
                return;

            await Plugin.Framework.DelayTicks(1);
        }

        var uiState = await RunOnFrameworkTickAsync(DescribeRetainerUiState).ConfigureAwait(false);
        throw new InvalidOperationException($"Timed out waiting for {stateName}. {uiState}");
    }

    private async Task WaitForRetainerCommandMenuAsync(string retainerName)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (await RunOnFrameworkTickAsync(IsRetainerCommandMenuReady).ConfigureAwait(false))
                return;

            await Plugin.Framework.DelayTicks(1);
        }

        var uiState = await RunOnFrameworkTickAsync(DescribeRetainerUiState).ConfigureAwait(false);
        if (await RunOnFrameworkTickAsync(IsRetainerCommandMenuReady).ConfigureAwait(false))
            return;

        throw new InvalidOperationException($"Timed out waiting for the retainer command menu for {retainerName}. {uiState}");
    }

    private async Task WaitForRetainerSessionAsync(string retainerName)
    {
        for (var attempt = 0; attempt < 240; attempt++)
        {
            if (await RunOnFrameworkTickAsync(() => IsRetainerSessionReady(retainerName) || IsRetainerCommandMenuReady()).ConfigureAwait(false))
                return;

            await Plugin.Framework.DelayTicks(1);
        }

        var uiState = await RunOnFrameworkTickAsync(DescribeRetainerUiState).ConfigureAwait(false);
        throw new InvalidOperationException($"Timed out waiting for active retainer session for {retainerName}. {uiState}");
    }

    private async Task WaitForRetainerInventoryClosedAsync()
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (!await RunOnFrameworkTickAsync(() => IsAnyAddonReady(RetainerInventoryLargeAddon, RetainerInventorySmallAddon)).ConfigureAwait(false))
                return;

            await Plugin.Framework.DelayTicks(1);
        }

        var uiState = await RunOnFrameworkTickAsync(DescribeRetainerUiState).ConfigureAwait(false);
        throw new InvalidOperationException($"Timed out waiting for retainer inventory to close. {uiState}");
    }

    private unsafe bool IsAnyAddonReady(params string[] addonNames)
    {
        foreach (var addonName in addonNames)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon != null && addon->IsReady && addon->IsVisible)
                return true;
        }

        return false;
    }

    private unsafe bool IsRetainerCommandMenuReady()
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return false;

        return TryGetEntrustOrWithdrawIndex(addon, out _);
    }

    private unsafe string DescribeRetainerUiState()
    {
        return retainerUiStateReader.DescribeRetainerUiState(RetainerUiStateAddons, GetVisibleRetainerObjectNames);
    }

    private bool IsRetainerSessionReady(string retainerName)
    {
        foreach (var gameObject in objectTable)
        {
            if (!gameObject.IsValid() || gameObject.ObjectKind != ObjectKind.Retainer)
                continue;

            if (string.Equals(gameObject.Name.TextValue, retainerName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private List<string> GetVisibleRetainerObjectNames()
    {
        var names = new List<string>();
        foreach (var gameObject in objectTable)
        {
            if (!gameObject.IsValid() || gameObject.ObjectKind != ObjectKind.Retainer)
                continue;

            var name = gameObject.Name.TextValue;
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return names;
    }

    private unsafe void SelectEntrustOrWithdrawItems()
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            throw new InvalidOperationException("Retainer command menu is not ready.");

        if (!TryGetEntrustOrWithdrawIndex(addon, out var index))
            throw new InvalidOperationException($"Retainer menu entry not found: {GetEntrustOrWithdrawText()}. {DescribeRetainerUiState()}");

        addon->AtkUnitBase.FireCallbackInt(index);
    }

    private unsafe bool TryGetEntrustOrWithdrawIndex(AddonSelectString* addon, out int index)
    {
        var targetText = GetEntrustOrWithdrawText();
        var popup = addon->PopupMenu.PopupMenu;

        for (var i = 0; i < popup.EntryCount; i++)
        {
            var entry = popup.EntryNames[i].ToString();
            if (!RetainerUiAutomationText.IsSelectStringEntryMatch(entry, targetText))
                continue;

            index = i;
            return true;
        }

        index = -1;
        return false;
    }

    private string GetEntrustOrWithdrawText()
    {
        var targetText = dataManager.GetExcelSheet<Addon>().GetRow(2378).Text.ExtractText();
        return targetText;
    }

    private unsafe void CloseRetainerInventory()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(RetainerInventoryLargeAddon, 1);
        if (addon == null)
            addon = gameGui.GetAddonByName<AtkUnitBase>(RetainerInventorySmallAddon, 1);

        if (addon == null || !addon->IsReady || !addon->IsVisible)
            throw new InvalidOperationException("Retainer inventory is not ready to close.");

        addon->Close(true);
    }

    public void Dispose()
    {
        pluginInterface.GetIpcSubscriber<object>(OnRetainerListTaskButtonsDraw).Unsubscribe(DrawRetainerListButton);
        pluginInterface.GetIpcSubscriber<string, object>(OnRetainerAdditionalTask).Unsubscribe(OnRetainerAdditionalTaskReceived);
        pluginInterface.GetIpcSubscriber<string, string, object>(OnRetainerReadyForPostprocess).Unsubscribe(OnRetainerReadyForPostprocessReceived);
    }
}
