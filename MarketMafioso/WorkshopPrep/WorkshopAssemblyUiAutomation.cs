using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopAssemblyUiAutomation
{
    private const string SelectStringAddon = "SelectString";
    private const string RequestAddon = "Request";
    private const string SelectYesNoAddon = "SelectYesno";
    private const string CompanyCraftRecipeNoteBookAddon = "CompanyCraftRecipeNoteBook";
    private const string SubmarinePartsMenuAddon = "SubmarinePartsMenu";

    private readonly IGameGui gameGui;

    public WorkshopAssemblyUiAutomation(IGameGui gameGui)
    {
        this.gameGui = gameGui;
    }

    public bool IsFabricationStationUiReady()
    {
        return IsAddonReady(CompanyCraftRecipeNoteBookAddon) ||
               IsAddonReady(SubmarinePartsMenuAddon) ||
               IsAddonReady(SelectStringAddon);
    }

    public WorkshopAssemblyActionResult TryOpenProject(WorkshopAssemblyQueueEntry entry)
    {
        return new(false, $"Workshop project {entry.ProjectName} cannot be opened because the live project-selection callback has not been mapped. {DescribeUiState()}");
    }

    public WorkshopAssemblyActionResult TrySubmitNextMaterial(WorkshopAssemblyQueueEntry entry)
    {
        return new(false, $"Workshop material for {entry.ProjectName} cannot be submitted because the live material-request callback has not been mapped. {DescribeUiState()}");
    }

    public WorkshopAssemblyActionResult TryConfirmContribution()
    {
        return new(false, $"Workshop material contribution cannot be confirmed because the live confirmation callback has not been mapped. {DescribeUiState()}");
    }

    public unsafe string DescribeUiState()
    {
        var trackedAddons = new[]
        {
            SelectStringAddon,
            RequestAddon,
            SelectYesNoAddon,
            CompanyCraftRecipeNoteBookAddon,
            SubmarinePartsMenuAddon,
        };

        var activeAddons = new List<string>();
        foreach (var addonName in trackedAddons)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon == null)
                continue;

            activeAddons.Add($"{addonName}({(addon->IsReady ? "ready" : "not ready")}, {(addon->IsVisible ? "visible" : "hidden")})");
        }

        return activeAddons.Count == 0
            ? "Workshop UI state: no tracked addons present."
            : $"Workshop UI state: {string.Join(", ", activeAddons)}.";
    }

    private unsafe bool IsAddonReady(string addonName)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        return addon != null && addon->IsReady && addon->IsVisible;
    }
}
