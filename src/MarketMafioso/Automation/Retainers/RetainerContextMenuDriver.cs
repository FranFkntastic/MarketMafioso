using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.Automation.Safety;

namespace MarketMafioso.Automation.Retainers;

public sealed class RetainerContextMenuDriver
{
    private readonly IGameGui gameGui;

    public RetainerContextMenuDriver(IGameGui gameGui)
    {
        this.gameGui = gameGui;
    }

    public unsafe AutomationOperationResult SelectContextMenuEntry(string targetText, uint itemId)
    {
        var contextMenu = gameGui.GetAddonByName<AtkUnitBase>(RetainerInventoryAddonNames.ContextMenu, 1);
        if (contextMenu == null || !contextMenu->IsReady || !contextMenu->IsVisible)
        {
            return AutomationOperationResult.Fail(
                AutomationFailureKind.MissingAddon,
                $"Retainer item context menu did not open for item {itemId}.");
        }

        var agent = AgentInventoryContext.Instance();
        var labels = ReadContextMenuLabels(agent);
        var menuState = DescribeContextMenuState(agent, labels);
        var index = RetainerUiAutomationText.FindContextMenuLabelIndex(labels, targetText);
        if (index is null)
        {
            return AutomationOperationResult.Fail(
                AutomationFailureKind.VerificationFailed,
                $"Retainer context menu entry not found for item {itemId}: {targetText}. {menuState}");
        }

        var callbackResult = FireContextMenuSelect(contextMenu, index.Value);
        if (!callbackResult)
        {
            return AutomationOperationResult.Fail(
                AutomationFailureKind.VerificationFailed,
                $"Retainer context menu callback returned false for item {itemId}: index={index.Value}, target=\"{targetText}\". {menuState}");
        }

        return AutomationOperationResult.Success(
            $"Selected retainer context menu entry for item {itemId}: index={index.Value}, target=\"{targetText}\", callbackResult={callbackResult}.");
    }

    private static unsafe IReadOnlyList<string> ReadContextMenuLabels(AgentInventoryContext* agent)
    {
        var labels = new List<string>();
        foreach (var parameter in agent->EventParams)
        {
            if (parameter.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString))
                continue;

            labels.Add(parameter.GetValueAsString());
        }

        return labels;
    }

    private static unsafe string DescribeContextMenuState(
        AgentInventoryContext* agent,
        IReadOnlyList<string> labels)
    {
        var entries = new List<string>();
        if (agent->ContextCallbackInfos != null)
        {
            var count = Math.Min(agent->ContextItemCount, labels.Count);
            for (var index = 0; index < count; index++)
            {
                var info = agent->ContextCallbackInfos + index;
                entries.Add(
                    $"[{index}] label=\"{labels[index]}\" labelId={info->LabelId} callbackParam={info->CallbackParam} " +
                    $"disabled={agent->IsContextItemDisabled(index)} handler={(info->Handler == null ? "null" : "set")}");
            }
        }

        var entryText = entries.Count == 0
            ? string.Join(" | ", labels.Select((label, index) => $"[{index}] label=\"{label}\""))
            : string.Join(" | ", entries);

        return
            $"ContextMenuState target={agent->TargetInventoryId}/{agent->TargetInventorySlotId}, flags={agent->TargetInventoryFlags}, " +
            $"ownerAddon={agent->OwnerAddonId}, start={agent->ContexItemStartIndex}, count={agent->ContextItemCount}, " +
            $"disabledMask=0x{agent->ContextItemDisabledMask:X}, entries=[{entryText}].";
    }

    private static unsafe bool FireContextMenuSelect(AtkUnitBase* contextMenu, int index)
    {
        var values = stackalloc AtkValue[5];
        values[0] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        values[1] = new AtkValue { Type = AtkValueType.Int, Int = index };
        values[2] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        values[3] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        values[4] = new AtkValue { Type = AtkValueType.Int, Int = 0 };

        return contextMenu->FireCallback(5, values, true);
    }
}
