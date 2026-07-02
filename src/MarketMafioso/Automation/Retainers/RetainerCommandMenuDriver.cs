using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using MarketMafioso.Automation.Safety;

namespace MarketMafioso.Automation.Retainers;

public sealed class RetainerCommandMenuDriver
{
    private const uint EntrustOrWithdrawItemsRow = 2378;
    private const uint QuitRetainerRow = 2383;

    private readonly IGameGui gameGui;
    private readonly Func<uint, string> resolveAddonText;
    private readonly Func<string> describeUiState;

    public RetainerCommandMenuDriver(
        IGameGui gameGui,
        Func<uint, string> resolveAddonText,
        Func<string> describeUiState)
    {
        this.gameGui = gameGui;
        this.resolveAddonText = resolveAddonText;
        this.describeUiState = describeUiState;
    }

    public unsafe bool IsCommandMenuReady()
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(RetainerInventoryAddonNames.SelectString, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return false;

        return TryGetSelectStringIndex(addon, resolveAddonText(EntrustOrWithdrawItemsRow), out _);
    }

    public AutomationOperationResult SelectEntrustOrWithdrawItems()
    {
        return SelectCommand(
            resolveAddonText(EntrustOrWithdrawItemsRow),
            successMessage: "Selected retainer inventory command.",
            missingAddonMessage: "Retainer command menu is not ready.",
            missingEntryMessagePrefix: "Retainer menu entry not found");
    }

    public AutomationOperationResult SelectQuitRetainer()
    {
        return SelectCommand(
            resolveAddonText(QuitRetainerRow),
            successMessage: "Selected retainer quit command.",
            missingAddonMessage: "Retainer command menu is not ready for quit.",
            missingEntryMessagePrefix: "Retainer quit entry not found");
    }

    private unsafe AutomationOperationResult SelectCommand(
        string targetText,
        string successMessage,
        string missingAddonMessage,
        string missingEntryMessagePrefix)
    {
        var addon = gameGui.GetAddonByName<AddonSelectString>(RetainerInventoryAddonNames.SelectString, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return AutomationOperationResult.Fail(AutomationFailureKind.MissingAddon, missingAddonMessage);

        if (!TryGetSelectStringIndex(addon, targetText, out var index))
        {
            return AutomationOperationResult.Fail(
                AutomationFailureKind.VerificationFailed,
                $"{missingEntryMessagePrefix}: {targetText}. {describeUiState()}");
        }

        addon->AtkUnitBase.FireCallbackInt(index);
        return AutomationOperationResult.Success(successMessage);
    }

    private static unsafe bool TryGetSelectStringIndex(AddonSelectString* addon, string targetText, out int index)
    {
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
}
