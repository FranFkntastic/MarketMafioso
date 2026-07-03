using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.Automation.Retainers;

public sealed class RetainerUiStateReader
{
    private readonly IGameGui gameGui;

    public RetainerUiStateReader(IGameGui gameGui)
    {
        this.gameGui = gameGui;
    }

    public unsafe string DescribeRetainerUiState(
        IReadOnlyList<string> trackedAddonNames,
        Func<IReadOnlyList<string>>? getVisibleRetainerObjectNames = null)
    {
        var activeAddons = new List<string>();
        foreach (var addonName in trackedAddonNames)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon == null)
                continue;

            var readyState = addon->IsReady ? "ready" : "not ready";
            var visibleState = addon->IsVisible ? "visible" : "hidden";
            activeAddons.Add($"{addonName}({readyState}, {visibleState})");
        }

        var builder = new StringBuilder();
        builder.Append("Retainer UI state: ");
        builder.Append(activeAddons.Count > 0 ? string.Join(", ", activeAddons) : "no tracked addons present");

        if (TryGetSelectStringEntries(out var entries))
        {
            builder.Append("; SelectString entries: ");
            builder.Append(entries);
        }

        var visibleRetainers = getVisibleRetainerObjectNames?.Invoke();
        if (visibleRetainers is { Count: > 0 })
        {
            builder.Append("; visible retainer objects: ");
            builder.Append(string.Join(", ", visibleRetainers));
        }

        return builder.ToString();
    }

    private unsafe bool TryGetSelectStringEntries(out string entries)
    {
        entries = string.Empty;

        var addon = gameGui.GetAddonByName<AddonSelectString>(RetainerInventoryAddonNames.SelectString, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return false;

        var popup = addon->PopupMenu.PopupMenu;
        if (popup.EntryCount <= 0)
            return false;

        var entryNames = new List<string>();
        for (var i = 0; i < popup.EntryCount; i++)
        {
            var entry = popup.EntryNames[i].ToString();
            if (!string.IsNullOrWhiteSpace(entry))
                entryNames.Add($"[{i}] {entry}");
        }

        entries = string.Join(" | ", entryNames);
        return entries.Length > 0;
    }
}
