using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Ui;
using MarketMafioso.AgentBridge;

namespace MarketMafioso.Squire.Observation;

/// <summary>
/// Opens and inspects only the rendered Character UI. It intentionally does not
/// consult PlayerState, InventoryManager, agents, or gearset modules.
/// </summary>
public interface IRenderedCharacterAdvisorProbe
{
    void PrepareAdvisorObservation();
    bool Open();
    bool TryCloseCharacterUi();
    RenderedGatheringStatsObservation CaptureGatheringStats();
    RenderedEquipmentScanProgress BeginEquipmentScan();
    RenderedEquipmentScanStepResult AdvanceEquipmentScan();
    RenderedEquipmentScanProgress CancelEquipmentScan();
}

public sealed class DalamudRenderedCharacterUiProbe : IRenderedCharacterAdvisorProbe
{
    private static readonly string[] AddonNames =
    [
        "Character",
        "CharacterProfile",
        "CharacterClass",
        "CharacterRepute",
        "GearSetList",
        "ItemDetail",
        "_TextError",
        "SelectYesno",
    ];

    private static readonly string[] RetainerAddonNames =
    [
        "RetainerList",
        "RetainerCharacter",
        "ItemSearch",
        "ItemSearchResult",
        "_TargetInfo",
        "_TargetInfoMainTarget",
        "_NamePlate",
        "ItemDetail",
    ];

    private readonly IGameGui gameGui;
    private readonly Franthropy.Dalamud.AgentBridge.DalamudRenderedUiTextActionDispatcher renderedTextActions;
    private readonly RenderedGatheringStatsStabilizer gatheringStatsStabilizer = new(TimeSpan.FromSeconds(3));
    private readonly RenderedCharacterEquipmentScanCoordinator equipmentScan = new();
    private string? hoveredCharacterNodePath;
    private string? lastGearsetSelectionDiagnostic;

    public DalamudRenderedCharacterUiProbe(IGameGui gameGui)
    {
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));
        renderedTextActions = new(gameGui);
    }

    public AgentBridgeUiAutomationCapabilities Capabilities => new(
        "registered-node-ui-events",
        MovesOperatingSystemCursor: false,
        ActivatesGameWindow: false,
        RequiresGameForeground: false,
        RequiresVisibleCharacterAddon: true,
        UsesRenderedTooltipAsAuthority: true,
        SupportsDeterministicReplay: true,
        "Equipment slots are traversed by dispatching their registered drag/drop rollover UI events. Character and Item Detail rendered output remains the only runtime authority.");

    public unsafe bool Open()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("Character", 1);
        if (addon != null && addon->RootNode != null && addon->RootNode->IsVisible())
            return false;
        Chat.ExecuteCommand("/character");
        return true;
    }

    public unsafe bool TryCloseCharacterUi()
    {
        RestoreCursor();
        var addon = gameGui.GetAddonByName<AtkUnitBase>("Character", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
            return false;
        addon->Close(true);
        return true;
    }

    public void PrepareAdvisorObservation()
    {
        gatheringStatsStabilizer.Reset();
        CancelEquipmentScan();
    }

    public unsafe bool TryCloseBlockingSelectString()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("SelectString", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
            return false;
        addon->Close(true);
        return true;
    }

    public GearsetChangeCommand? TrySwitchCalibrationJob(string target)
    {
        if (!GearsetChangeCommand.TryCreate(target, out var command))
            return null;
        gatheringStatsStabilizer.Reset();
        Chat.ExecuteCommand(command.Command);
        return command;
    }

    public GearsetChangeCommand? TrySwitchGearsetSlot(string target)
    {
        if (!GearsetChangeCommand.TryCreateSlot(target, out var command))
            return null;
        gatheringStatsStabilizer.Reset();
        Chat.ExecuteCommand(command.Command);
        return command;
    }

    public Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TryOpenGearsetList()
    {
        unsafe
        {
            var existing = gameGui.GetAddonByName<AtkUnitBase>("GearSetList", 1);
            if (existing != null && existing->RootNode != null && existing->RootNode->IsVisible() && existing->IsReady)
                return new(true, "RenderedAddonAlreadyOpen", "The rendered Gear Set list is already open.", "GearSetList", null);
        }
        var result = renderedTextActions.TryClickUniqueControlImmediatelyLeftOfText("Character", "Gear Set");
        return result.Success
            ? result
            : result with
            {
                Message = $"{result.Message} Open the rendered Character UI first, then retry.",
            };
    }

    public Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TrySelectCalibrationGearset(string target)
    {
        if (!GearsetChangeCommand.TryCreate(target, out var command))
            return new(false, "InvalidCalibrationJob", "Target must be Miner, Botanist, or Blacksmith.", "GearSetList", null);
        var selected = renderedTextActions.TrySelectUniqueListRowText("GearSetList", command.JobName);
        lastGearsetSelectionDiagnostic = selected.Message;
        return selected;
    }

    public unsafe Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TryEquipSelectedGearset()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("GearSetList", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible() || !addon->IsReady)
            return new(false, "RenderedAddonUnavailable", "The rendered Gear Set list changed before equipping.", "GearSetList", null);
        var gearSetList = new AddonMaster.GearSetList(addon);
        if (!gearSetList.EquipSetButton->IsEnabled)
            return new(false, "RenderedEquipSetDisabled", $"The rendered Gear Set list has not enabled Equip Set. Selection evidence: {lastGearsetSelectionDiagnostic ?? "unavailable"}", "GearSetList", null);
        var equipped = renderedTextActions.TryActivateUniqueText("GearSetList", "Equip Set");
        if (equipped.Success)
            gatheringStatsStabilizer.Reset();
        return equipped;
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

    /// <summary>
    /// Captures already-rendered retainer surfaces without opening, closing, selecting, or focusing
    /// anything. This is fixture discovery only; node values are not interpreted here.
    /// </summary>
    public unsafe AgentBridgeRenderedUiSnapshot CaptureRetainerUi()
    {
        var addons = RetainerAddonNames.Select(CaptureAddon).ToList();
        var capturedNames = addons.Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        var stage = AtkStage.Instance();
        var unitManager = stage == null ? null : (AtkUnitManager*)stage->RaptureAtkUnitManager;
        if (unitManager != null)
        {
            var loaded = &unitManager->AllLoadedUnitsList;
            for (var index = 0; index < loaded->Count; index++)
            {
                AtkUnitBase* addon = loaded->Entries[index];
                if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
                    continue;
                var name = addon->NameString;
                if (string.IsNullOrWhiteSpace(name) || capturedNames.Contains(name) ||
                    (!name.Contains("NamePlate", StringComparison.OrdinalIgnoreCase) &&
                     !name.Contains("TargetInfo", StringComparison.OrdinalIgnoreCase)))
                    continue;
                addons.Add(CaptureAddon(name, addon));
                capturedNames.Add(name);
            }
        }
        return new(DateTimeOffset.UtcNow, addons);
    }

    public RenderedGatheringStatsObservation CaptureGatheringStats() =>
        gatheringStatsStabilizer.Observe(RenderedCharacterStatsParser.Parse(Capture()));

    public RenderedEquipmentScanProgress BeginEquipmentScan()
    {
        RestoreCursor();
        return equipmentScan.Begin(Capture());
    }

    public RenderedEquipmentScanStepResult AdvanceEquipmentScan()
    {
        var progress = equipmentScan.Snapshot();
        if (progress.Status == RenderedEquipmentScanStatus.ReadyToHover && progress.CurrentTarget is { } target)
        {
            if (!TryHoverCharacterNode(target.NodePath))
                return new(false, progress, "The rendered equipment slot did not accept a virtual UI rollover event.");
            progress = equipmentScan.MarkHoverStarted(target.NodePath, DateTimeOffset.UtcNow);
            return new(true, progress, progress.Diagnostic);
        }
        if (progress.Status == RenderedEquipmentScanStatus.Observing)
        {
            progress = equipmentScan.Observe(Capture(), DateTimeOffset.UtcNow);
            if (progress.Status is RenderedEquipmentScanStatus.Complete or RenderedEquipmentScanStatus.Failed)
                RestoreCursor();
            return new(true, progress, progress.Diagnostic);
        }
        return new(false, progress, "The rendered equipment scan is not waiting for an advance step.");
    }

    public RenderedEquipmentScanProgress CancelEquipmentScan()
    {
        RestoreCursor();
        return equipmentScan.Cancel();
    }

    /// <summary>
    /// Dispatches the drag/drop component's registered rollover event so the game itself renders the
    /// authoritative ItemDetail tooltip. This never moves the OS cursor or activates FFXIV.
    /// </summary>
    public bool TryHoverCharacterNode(string nodePath)
    {
        if (string.IsNullOrWhiteSpace(nodePath) ||
            !nodePath.StartsWith("Character/", StringComparison.Ordinal) ||
            nodePath.Length > 128)
            return false;

        var character = CaptureAddon("Character");
        if (!character.Present || !character.Ready || !character.Visible || character.Nodes == null)
            return false;

        var layout = RenderedCharacterEquipmentLayoutParser.Parse(
            new AgentBridgeRenderedUiSnapshot(DateTimeOffset.UtcNow, [character]));
        var target = layout.Slots.FirstOrDefault(value => string.Equals(value.NodePath, nodePath, StringComparison.Ordinal));
        if (layout.Status != RenderedEquipmentLayoutStatus.Complete || target == null ||
            target.Right <= target.Left || target.Bottom <= target.Top)
            return false;

        RestoreCursor();
        if (!DispatchCharacterNodeEvent(nodePath, AtkEventType.DragDropRollOver, target))
            return false;
        hoveredCharacterNodePath = nodePath;
        return true;
    }

    public bool RestoreCursor()
    {
        if (hoveredCharacterNodePath is not { } nodePath)
            return false;
        hoveredCharacterNodePath = null;
        return DispatchCharacterNodeEvent(nodePath, AtkEventType.DragDropRollOut, null);
    }

    private unsafe AgentBridgeRenderedAddonSnapshot CaptureAddon(string addonName)
        => CaptureAddon(addonName, gameGui.GetAddonByName<AtkUnitBase>(addonName, 1));

    private unsafe bool DispatchCharacterNodeEvent(
        string nodePath,
        AtkEventType eventType,
        RenderedEquipmentSlotTarget? target)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("Character", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible() || !addon->IsReady)
            return false;
        var node = FindRegisteredEventNode(addon, nodePath, eventType);
        if (node == null || !IsEffectivelyVisible(node))
            return false;

        var centerX = target is null ? 0 : Math.Clamp((target.Left + target.Right) / 2, short.MinValue, short.MaxValue);
        var centerY = target is null ? 0 : Math.Clamp((target.Top + target.Bottom) / 2, short.MinValue, short.MaxValue);
        var evt = new AtkEventDispatcher.Event
        {
            State = new AtkEventState { EventType = eventType },
            EventData = new AtkEventData
            {
                MouseData = new AtkEventData.AtkMouseData
                {
                    PosX = (short)centerX,
                    PosY = (short)centerY,
                },
            },
        };
        return node->DispatchEvent(&evt);
    }

    private static unsafe AtkResNode* FindRegisteredEventNode(
        AtkUnitBase* addon,
        string nodePath,
        AtkEventType eventType)
    {
        var separator = nodePath.Length;
        while (separator > "Character".Length)
        {
            var candidatePath = nodePath[..separator];
            var candidate = FindNodeByPath(addon, candidatePath);
            if (candidate != null && candidate->IsEventRegistered(eventType))
                return candidate;
            separator = nodePath.LastIndexOf('/', separator - 1);
            if (separator < 0)
                break;
        }
        return null;
    }

    private static unsafe AtkResNode* FindNodeByPath(AtkUnitBase* addon, string nodePath)
    {
        var segments = nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "Character", StringComparison.Ordinal))
            return null;
        var manager = &addon->UldManager;
        AtkResNode* node = null;
        for (var segmentIndex = 1; segmentIndex < segments.Length; segmentIndex++)
        {
            if (!uint.TryParse(segments[segmentIndex], out var nodeId) || manager == null || manager->NodeList == null)
                return null;
            node = null;
            for (var index = 0u; index < manager->NodeListCount; index++)
            {
                var candidate = manager->NodeList[index];
                if (candidate != null && candidate->NodeId == nodeId)
                {
                    node = candidate;
                    break;
                }
            }
            if (node == null)
                return null;
            if (segmentIndex + 1 < segments.Length)
            {
                var componentNode = node->GetAsAtkComponentNode();
                if (componentNode == null || componentNode->Component == null)
                    return null;
                manager = &componentNode->Component->UldManager;
            }
        }
        return node;
    }

    private static unsafe AgentBridgeRenderedAddonSnapshot CaptureAddon(string addonName, AtkUnitBase* addon)
    {
        try
        {
            if (addon == null)
                return new(addonName, false, false, false, 0, []);

            var visible = addon->RootNode != null && addon->RootNode->IsVisible();
            var nodeCount = addon->UldManager.NodeListCount;
            var textNodes = new List<AgentBridgeRenderedTextNode>();
            var nodes = new List<AgentBridgeRenderedNodeSnapshot>();
            if (visible && addon->UldManager.NodeList != null)
                CaptureManager(&addon->UldManager, addonName, textNodes, nodes, new HashSet<nint>());

            return new(addonName, true, addon->IsReady, visible, nodeCount, textNodes, Nodes: nodes);
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
        ICollection<AgentBridgeRenderedNodeSnapshot> nodes,
        ISet<nint> visitedManagers)
    {
        if (manager == null || manager->NodeList == null || textNodes.Count >= 512 ||
            !visitedManagers.Add((nint)manager))
            return;

        for (var index = 0u; index < manager->NodeListCount && textNodes.Count < 512; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !IsEffectivelyVisible(node))
                continue;

            var nodePath = $"{path}/{node->NodeId}";
            var componentNode = node->GetAsAtkComponentNode();
            ushort? componentType = null;
            if (componentNode != null && componentNode->Component != null)
            {
                componentType = (ushort)componentNode->Component->GetComponentType();
                CaptureManager(&componentNode->Component->UldManager, nodePath, textNodes, nodes, visitedManagers);
            }

            FFXIVClientStructs.FFXIV.Common.Math.Bounds bounds;
            node->GetBounds(&bounds);
            nodes.Add(new(
                nodePath,
                node->NodeId,
                (ushort)node->Type,
                componentType,
                bounds.Pos1.X,
                bounds.Pos1.Y,
                bounds.Pos2.X,
                bounds.Pos2.Y,
                (node->NodeFlags & NodeFlags.RespondToMouse) != 0,
                RegisteredEvents: GetRegisteredEvents(node)));

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

    private static unsafe IReadOnlyList<string>? GetRegisteredEvents(AtkResNode* node)
    {
        if ((node->NodeFlags & NodeFlags.RespondToMouse) == 0)
            return null;
        return Enum.GetValues<AtkEventType>()
            .Where(node->IsEventRegistered)
            .Select(value => value.ToString())
            .ToArray();
    }

    private static unsafe bool IsEffectivelyVisible(AtkResNode* node)
    {
        var current = node;
        for (var depth = 0; current != null && depth < 64; depth++, current = current->ParentNode)
        {
            if (!current->IsVisible())
                return false;
        }
        return current == null;
    }
}
