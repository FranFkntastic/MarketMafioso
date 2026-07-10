using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.Automation.Diagnostics;
using MarketMafioso.Automation.Retainers;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.Main;

internal sealed class AutomationDiagnosticProbeFactory
{
    private readonly AutoRetainerRefreshService autoRetainerRefresh;
    private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;

    public AutomationDiagnosticProbeFactory(
        AutoRetainerRefreshService autoRetainerRefresh,
        VIWIWorkshoppaIpc viwiWorkshoppaIpc)
    {
        this.autoRetainerRefresh = autoRetainerRefresh ?? throw new ArgumentNullException(nameof(autoRetainerRefresh));
        this.viwiWorkshoppaIpc = viwiWorkshoppaIpc ?? throw new ArgumentNullException(nameof(viwiWorkshoppaIpc));
    }

    public IReadOnlyList<IAutomationDiagnosticProbe> Create()
    {
        return
        [
            new AutomationDiagnosticProbe("Retainer UI", RunRetainerUiDiagnosticProbe),
            new AutomationDiagnosticProbe("Market Board UI", RunMarketBoardUiDiagnosticProbe),
            new AutomationDiagnosticProbe("External Helpers", RunExternalHelperDiagnosticProbe),
        ];
    }

    private static AutomationDiagnosticProbeResult RunRetainerUiDiagnosticProbe()
    {
        var state = new RetainerUiStateReader(Plugin.GameGui).DescribeRetainerUiState(
            [
                RetainerInventoryAddonNames.RetainerList,
                RetainerInventoryAddonNames.SelectString,
                RetainerInventoryAddonNames.InventoryLarge,
                RetainerInventoryAddonNames.InventorySmall,
                RetainerInventoryAddonNames.InputNumeric,
            ]);

        return new AutomationDiagnosticProbeResult(
            "Retainer UI",
            IsSuccess: true,
            state,
            new Dictionary<string, string?>
            {
                ["retainerList"] = DescribeAddon(RetainerInventoryAddonNames.RetainerList),
                ["selectString"] = DescribeAddon(RetainerInventoryAddonNames.SelectString),
                ["inventoryLarge"] = DescribeAddon(RetainerInventoryAddonNames.InventoryLarge),
                ["inventorySmall"] = DescribeAddon(RetainerInventoryAddonNames.InventorySmall),
                ["inputNumeric"] = DescribeAddon(RetainerInventoryAddonNames.InputNumeric),
            });
    }

    private static AutomationDiagnosticProbeResult RunMarketBoardUiDiagnosticProbe()
    {
        var details = new Dictionary<string, string?>
        {
            ["itemSearch"] = DescribeAddon("ItemSearch"),
            ["itemSearchResult"] = DescribeAddon("ItemSearchResult"),
            ["itemDetail"] = DescribeAddon("ItemDetail"),
        };
        var isAnyMarketBoardAddonVisible = details.Values.Any(value => value?.Contains("visible", StringComparison.OrdinalIgnoreCase) == true);

        return new AutomationDiagnosticProbeResult(
            "Market Board UI",
            isAnyMarketBoardAddonVisible,
            isAnyMarketBoardAddonVisible
                ? "At least one tracked market-board addon is visible."
                : "No tracked market-board addon is visible.",
            details);
    }

    private AutomationDiagnosticProbeResult RunExternalHelperDiagnosticProbe()
    {
        var autoRetainerAvailable = autoRetainerRefresh.IsLoaded;
        var viwiAvailable = viwiWorkshoppaIpc.IsAvailable;
        return new AutomationDiagnosticProbeResult(
            "External Helpers",
            autoRetainerAvailable || viwiAvailable,
            "External helper availability probe completed.",
            new Dictionary<string, string?>
            {
                ["autoRetainer"] = autoRetainerAvailable ? "loaded" : "not loaded",
                ["viwiWorkshoppa"] = viwiAvailable ? "loaded" : "not loaded",
            });
    }

    private static unsafe string DescribeAddon(string addonName)
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (addon == null)
            return "not present";

        return $"{(addon->IsReady ? "ready" : "not ready")}, {(addon->IsVisible ? "visible" : "hidden")}";
    }
}
