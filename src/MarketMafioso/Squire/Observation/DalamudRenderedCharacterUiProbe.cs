using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.AgentBridge;

namespace MarketMafioso.Squire.Observation;

/// <summary>
/// Opens and inspects only the rendered Character UI. It intentionally does not
/// consult PlayerState, InventoryManager, agents, or gearset modules.
/// </summary>
public sealed class DalamudRenderedCharacterUiProbe
{
    private static readonly string[] AddonNames =
    [
        "Character",
        "CharacterProfile",
        "CharacterClass",
        "CharacterRepute",
    ];

    private readonly IGameGui gameGui;
    private readonly RenderedGatheringStatsStabilizer gatheringStatsStabilizer = new(TimeSpan.FromSeconds(3));

    public DalamudRenderedCharacterUiProbe(IGameGui gameGui)
    {
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));
    }

    public unsafe void Open()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("Character", 1);
        if (addon != null && addon->RootNode != null && addon->RootNode->IsVisible())
            return;
        Chat.ExecuteCommand("/character");
    }

    public unsafe bool TryCloseBlockingSelectString()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("SelectString", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
            return false;
        addon->Close(true);
        return true;
    }

    public bool TrySwitchCalibrationJob(string target)
    {
        if (target is not ("Miner" or "Botanist" or "Blacksmith"))
            return false;
        gatheringStatsStabilizer.Reset();
        Chat.ExecuteCommand($"/gearset change \"{target}\"");
        return true;
    }

    public unsafe AgentBridgeRenderedUiSnapshot Capture()
    {
        var addons = new List<AgentBridgeRenderedAddonSnapshot>(AddonNames.Length + 4);
        var capturedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var addonName in AddonNames)
        {
            addons.Add(CaptureAddon(addonName));
            capturedNames.Add(addonName);
        }

        var character = gameGui.GetAddonByName<AtkUnitBase>("Character", 1);
        var stage = AtkStage.Instance();
        var unitManager = stage == null ? null : (AtkUnitManager*)stage->RaptureAtkUnitManager;
        if (character != null && unitManager != null)
        {
            var loaded = &unitManager->AllLoadedUnitsList;
            for (var index = 0; index < loaded->Count; index++)
            {
                AtkUnitBase* addon = loaded->Entries[index];
                if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
                    continue;
                var addonName = addon->NameString;
                if (string.IsNullOrWhiteSpace(addonName) || capturedNames.Contains(addonName) ||
                    (addon->HostId != character->Id && !addonName.Contains("Character", StringComparison.Ordinal)))
                    continue;
                addons.Add(CaptureAddon(addonName, addon));
                capturedNames.Add(addonName);
            }
        }
        return new AgentBridgeRenderedUiSnapshot(DateTimeOffset.UtcNow, addons);
    }

    public RenderedGatheringStatsObservation CaptureGatheringStats() =>
        gatheringStatsStabilizer.Observe(RenderedCharacterStatsParser.Parse(Capture()));

    private unsafe AgentBridgeRenderedAddonSnapshot CaptureAddon(string addonName)
        => CaptureAddon(addonName, gameGui.GetAddonByName<AtkUnitBase>(addonName, 1));

    private static unsafe AgentBridgeRenderedAddonSnapshot CaptureAddon(string addonName, AtkUnitBase* addon)
    {
        try
        {
            if (addon == null)
                return new(addonName, false, false, false, 0, []);

            var visible = addon->RootNode != null && addon->RootNode->IsVisible();
            var nodeCount = addon->UldManager.NodeListCount;
            var textNodes = new List<AgentBridgeRenderedTextNode>();
            if (visible && addon->UldManager.NodeList != null)
                CaptureManager(&addon->UldManager, addonName, textNodes, new HashSet<nint>());

            return new(addonName, true, addon->IsReady, visible, nodeCount, textNodes);
        }
        catch (Exception ex)
        {
            return new(addonName, true, false, false, 0, [], ex.Message);
        }
    }

    private static unsafe void CaptureManager(
        AtkUldManager* manager,
        string path,
        ICollection<AgentBridgeRenderedTextNode> textNodes,
        ISet<nint> visitedManagers)
    {
        if (manager == null || manager->NodeList == null || textNodes.Count >= 512 ||
            !visitedManagers.Add((nint)manager))
            return;

        for (var index = 0u; index < manager->NodeListCount && textNodes.Count < 512; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !node->IsVisible())
                continue;

            var nodePath = $"{path}/{node->NodeId}";
            var componentNode = node->GetAsAtkComponentNode();
            if (componentNode != null && componentNode->Component != null)
                CaptureManager(&componentNode->Component->UldManager, nodePath, textNodes, visitedManagers);

            var textNode = node->GetAsAtkTextNode();
            if (textNode != null)
            {
                var text = textNode->NodeText.ExtractText().Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var x = 0f;
                    var y = 0f;
                    node->GetPositionFloat(&x, &y);
                    textNodes.Add(new(
                        nodePath,
                        node->NodeId,
                        (ushort)node->Type,
                        text,
                        x,
                        y,
                        node->Width,
                        node->Height));
                }
            }

        }
    }
}
