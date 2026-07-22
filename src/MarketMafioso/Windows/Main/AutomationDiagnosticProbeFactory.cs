using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.Automation.Diagnostics;
using MarketMafioso.Quartermaster;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.Main;

internal sealed class AutomationDiagnosticProbeFactory
{
    private readonly QuartermasterIpcClient quartermaster;
    private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;

    public AutomationDiagnosticProbeFactory(
        QuartermasterIpcClient quartermaster,
        VIWIWorkshoppaIpc viwiWorkshoppaIpc)
    {
        this.quartermaster = quartermaster ?? throw new ArgumentNullException(nameof(quartermaster));
        this.viwiWorkshoppaIpc = viwiWorkshoppaIpc ?? throw new ArgumentNullException(nameof(viwiWorkshoppaIpc));
    }

    public IReadOnlyList<IAutomationDiagnosticProbe> Create()
    {
        return
        [
            new AutomationDiagnosticProbe("Market Board UI", RunMarketBoardUiDiagnosticProbe),
            new AutomationDiagnosticProbe("External Helpers", RunExternalHelperDiagnosticProbe),
        ];
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
        var quartermasterAvailable = quartermaster.TryGetCapabilities(out var capabilities, out var quartermasterError);
        var viwiAvailable = viwiWorkshoppaIpc.IsAvailable;
        return new AutomationDiagnosticProbeResult(
            "External Helpers",
            quartermasterAvailable || viwiAvailable,
            "External helper availability probe completed.",
            new Dictionary<string, string?>
            {
                ["quartermaster"] = quartermasterAvailable
                    ? $"loaded; provider {capabilities!.ProviderInstanceId}; revision {capabilities.Revision}"
                    : quartermasterError,
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
