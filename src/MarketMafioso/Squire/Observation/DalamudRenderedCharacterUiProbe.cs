using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.AgentBridge;
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
    RenderedArmouryDifferentialProgress CaptureArmouryDifferentialSnapshot();
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
        "ArmouryBoard",
        "ItemDetail",
        "_TextError",
        "SelectYesno",
        "SelectString",
        "SelectIconString",
        "Talk",
        "ItemSearch",
        "ItemSearchResult",
        "MarketBoard",
        "Shop",
        "RetainerSell",
        "ContextMenu",
    ];

    private static readonly string[] RetainerAddonNames =
    [
        "RetainerList",
        "RetainerCharacter",
        "SelectString",
        "SelectIconString",
        "Talk",
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
    private string? lastGearsetSelectionDiagnostic;

    public DalamudRenderedCharacterUiProbe(IGameGui gameGui)
    {
        this.gameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));
        renderedTextActions = new(gameGui);
    }

    public AgentBridgeUiAutomationCapabilities Capabilities => new(
        "rendered-native-item-tooltip-request",
        MovesOperatingSystemCursor: false,
        ActivatesGameWindow: false,
        RequiresGameForeground: false,
        RequiresVisibleCharacterAddon: true,
        UsesRenderedTooltipAsAuthority: true,
        SupportsDeterministicReplay: false,
        "Equipment slots request the game's own ItemDetail tooltip through AtkTooltipManager with no cursor, focus, input, or event injection. Character and Item Detail rendered output remains the only runtime authority.");

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
        var closed = false;
        var gearSetList = gameGui.GetAddonByName<AtkUnitBase>("GearSetList", 1);
        if (gearSetList != null && gearSetList->RootNode != null && gearSetList->RootNode->IsVisible())
        {
            gearSetList->Close(true);
            closed = true;
        }
        var addon = gameGui.GetAddonByName<AtkUnitBase>("Character", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
            return closed;
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

    public unsafe bool TryCloseRetainerUi()
    {
        foreach (var addonName in new[] { "RetainerCharacter", "SelectString", "RetainerList" })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
                continue;
            addon->Close(true);
            return true;
        }
        return false;
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

    public bool TryActivateRenderedSummoningBell()
        => renderedTextActions.TryConfirmUniqueText("_TargetInfoMainTarget", "Summoning Bell").Success;

    public Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TryOpenRenderedRetainer(string retainerName)
    {
        if (string.IsNullOrWhiteSpace(retainerName))
            return new(false, "InvalidRenderedRetainer", "A retainer name is required.", null, null);

        var gear = renderedTextActions.CaptureVisibleText("RetainerCharacter");
        if (gear.Available)
            return new(true, "RenderedRetainerGearVisible", "The rendered retainer attributes and gear surface is already visible.", "RetainerCharacter", null);

        var menu = renderedTextActions.CaptureVisibleText("SelectString");
        if (menu.Available)
        {
            var expectedIdentity = $"Retainer: {retainerName.Trim()}";
            if (!menu.TextNodes.Any(value => value.Text.Contains(expectedIdentity, StringComparison.OrdinalIgnoreCase)))
                return new(false, "RenderedRetainerIdentityMismatch", "The visible retainer menu does not identify the requested retainer.", "SelectString", null);
            return renderedTextActions.TryActivateUniqueSelectStringText("View retainer attributes and gear.");
        }

        return renderedTextActions.TryActivateUniqueRetainerListRowText(retainerName);
    }

    public RenderedGatheringStatsObservation CaptureGatheringStats() =>
        gatheringStatsStabilizer.Observe(RenderedCharacterStatsParser.Parse(Capture()));

    public RenderedEquipmentScanProgress BeginEquipmentScan()
    {
        return equipmentScan.Begin(Capture());
    }

    public RenderedEquipmentScanStepResult AdvanceEquipmentScan()
    {
        var progress = equipmentScan.Snapshot();
        if (progress.Status == RenderedEquipmentScanStatus.ReadyToHover && progress.CurrentTarget is { } target)
        {
            if (!TryRequestEquipmentTooltip(target, out var reason))
                return new(false, progress, reason);
            progress = equipmentScan.MarkHoverStarted(target.NodePath, DateTimeOffset.UtcNow);
            return new(progress.Status == RenderedEquipmentScanStatus.Observing, progress, progress.Diagnostic);
        }
        if (progress.Status == RenderedEquipmentScanStatus.Observing)
        {
            progress = equipmentScan.Observe(Capture(), DateTimeOffset.UtcNow);
            if (progress.Status is RenderedEquipmentScanStatus.Complete or RenderedEquipmentScanStatus.Failed)
                HideEquipmentTooltip();
            return new(true, progress, progress.Diagnostic);
        }
        return new(false, progress, "The rendered equipment scan is not waiting for an advance step.");
    }

    public RenderedEquipmentScanProgress CancelEquipmentScan()
    {
        HideEquipmentTooltip();
        return equipmentScan.Cancel();
    }

    public unsafe bool TryOpenArmouryBoard()
    {
        var existing = gameGui.GetAddonByName<AtkUnitBase>("ArmouryBoard", 1);
        if (existing != null && existing->RootNode != null && existing->RootNode->IsVisible())
            return false;
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.ArmouryBoard);
        if (agent == null)
            return false;
        agent->Show();
        return true;
    }

    public unsafe bool TryCloseArmouryBoard()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("ArmouryBoard", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
            return false;
        HideArmouryTooltip();
        addon->Close(true);
        return true;
    }

    private static readonly (string Keyword, FFXIVClientStructs.FFXIV.Client.Game.InventoryType Type)[] ArmouryTabKeywords =
    [
        ("main hand", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryMainHand),
        ("off-hand", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryOffHand),
        ("off hand", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryOffHand),
        ("head", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryHead),
        ("body", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryBody),
        ("hands", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryHands),
        ("legs", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryLegs),
        ("feet", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryFeets),
        ("ear", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryEar),
        ("neck", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryNeck),
        ("wrist", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryWrist),
        ("bracelet", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryWrist),
        ("ring", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmoryRings),
        ("soul", FFXIVClientStructs.FFXIV.Client.Game.InventoryType.ArmorySoulCrystal),
    ];

    /// <summary>
    /// AgentItemDetail.TypeOrId values for armoury containers are the raw InventoryType
    /// enum values (proven live on Primary for ArmoryMainHand=3500 and ArmoryHead=3201:
    /// the agent accepts the enum numbering for these containers, unlike the small
    /// 48-51/69-72 scheme documented for player inventory and saddlebags).
    /// Mechanism only — rendered tooltip output remains the sole authority.
    /// </summary>
    private static uint ArmouryTooltipTypeOrId(FFXIVClientStructs.FFXIV.Client.Game.InventoryType inventoryType) =>
        (uint)inventoryType;

    public unsafe Franthropy.Dalamud.AgentBridge.RenderedUiTextActionResult TryShowArmourySlotTooltip(string target)
    {
        // Diagnostic experiment override: 'ArmoryMainHand:0:exp=<typeOrId>,<flag1>,<kind>' forces raw tooltip args.
        uint? experimentType = null;
        byte? experimentFlag = null;
        byte? experimentKind = null;
        var targetCore = target ?? string.Empty;
        const string ExpMarker = ":exp=";
        var expIndex = targetCore.IndexOf(ExpMarker, StringComparison.OrdinalIgnoreCase);
        if (expIndex >= 0)
        {
            var parts = targetCore[(expIndex + ExpMarker.Length)..].Split(',');
            if (parts.Length == 3 && uint.TryParse(parts[0], out var parsedType) &&
                byte.TryParse(parts[1], out var parsedFlag) && byte.TryParse(parts[2], out var parsedKind))
            {
                experimentType = parsedType;
                experimentFlag = parsedFlag;
                experimentKind = parsedKind;
            }
            targetCore = targetCore[..expIndex];
        }
        var separatorIndex = targetCore.LastIndexOf(':');
        if (separatorIndex <= 0 || !short.TryParse(targetCore[(separatorIndex + 1)..], out var slotIndex))
            return new(false, "InvalidArmouryTarget", "Target must be '<InventoryType>:<slotIndex>', for example ArmoryMainHand:0.", "ArmouryBoard", null);
        var inventoryTypeName = targetCore[..separatorIndex];
        if (!Enum.TryParse<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>(inventoryTypeName, true, out var inventoryType) ||
            ArmouryTabKeywords.All(value => value.Type != inventoryType))
            return new(false, "InvalidArmouryContainer", $"'{inventoryTypeName}' is not a supported armoury container.", "ArmouryBoard", null);
        if (slotIndex is < 0 or >= 50)
            return new(false, "InvalidArmourySlot", "Armoury slot index must be between 0 and 49.", "ArmouryBoard", null);

        var addon = gameGui.GetAddonByName<AtkUnitBase>("ArmouryBoard", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
            return new(false, "RenderedAddonUnavailable", "The rendered Armoury Board is unavailable; open it first.", "ArmouryBoard", null);

        var board = (AddonArmouryBoard*)addon;
        if (!TrySelectArmouryTab(board, inventoryType, out var tabDiagnostic))
            return new(false, "ArmouryTabCycling", tabDiagnostic, "ArmouryBoard", null);

        var slotNode = ResolveArmourySlotNode(addon, slotIndex);
        if (slotNode == null)
            return new(false, "ArmourySlotUnavailable", $"Rendered armoury slot {slotIndex} does not resolve on the current tab.", "ArmouryBoard", null);

        Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.HideTooltip(addon->Id);
        if (experimentType is { } rawType)
        {
            if (!TryShowArmourySlotTooltipRaw(addon, slotNode, rawType, experimentFlag ?? 0, experimentKind ?? 2, slotIndex))
                return new(false, "ArmouryTooltipRejected", $"The game rejected the experimental tooltip request (type={rawType}, flag={experimentFlag ?? 0}, kind={experimentKind ?? 2}) for slot {slotIndex}.", "ArmouryBoard", null);
        }
        else if (!Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.TryShowInventoryItemTooltip(addon->Id, slotNode, ArmouryTooltipTypeOrId(inventoryType), slotIndex))
            return new(false, "ArmouryTooltipRejected", $"The game rejected the ItemDetail tooltip request for {inventoryType} slot {slotIndex}.", "ArmouryBoard", null);

        var observation = RenderedItemDetailParser.Parse(Capture());
        return observation.Status == RenderedItemDetailStatus.Complete
            ? new(true, "RenderedArmouryTooltipObserved", $"Rendered Item Detail observed: {observation.Name}.", "ArmouryBoard", null)
            : new(true, "ArmouryTooltipDispatched", "The armoury tooltip request was dispatched; the rendered Item Detail settles on the next frame and can be read with get-item-detail-ui.", "ArmouryBoard", null);
    }

    private static unsafe bool TryShowArmourySlotTooltipRaw(AtkUnitBase* addon, AtkResNode* slotNode, uint typeOrId, byte flag1, byte kind, short slotIndex)
    {
        var stage = AtkStage.Instance();
        if (stage == null)
            return false;
        var args = stackalloc AtkTooltipManager.AtkTooltipArgs[1];
        args->Ctor();
        args->ItemArgs.InventoryType = (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)typeOrId;
        args->ItemArgs.Flag1 = flag1;
        args->ItemArgs.BuyQuantity = -1;
        args->ItemArgs.Slot = slotIndex;
        args->ItemArgs.Kind = (FFXIVClientStructs.FFXIV.Client.Enums.DetailKind)kind;
        stage->TooltipManager.ShowTooltip(AtkTooltipType.Item, addon->Id, slotNode, args);
        return true;
    }

    private unsafe bool TrySelectArmouryTab(AddonArmouryBoard* board, FFXIVClientStructs.FFXIV.Client.Game.InventoryType target, out string diagnostic)
    {
        // Tab text updates on the frame after NextTab, so this advances at most one tab per
        // call and reports cycling; the caller retries until the target tab is rendered.
        var label = board->CategoryLabelNode != null
            ? board->CategoryLabelNode->NodeText.ExtractText().Trim()
            : string.Empty;
        var matched = ArmouryTabKeywords
            .Where(value => label.Contains(value.Keyword, StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Type)
            .FirstOrDefault();
        if (label.Length > 0 && matched == target)
        {
            diagnostic = string.Empty;
            return true;
        }
        board->NextTab(0);
        diagnostic = $"Cycling armoury tabs; currently on '{label}'.";
        return false;
    }

    private static unsafe AtkResNode* ResolveArmourySlotNode(AtkUnitBase* addon, short slotIndex)
    {
        var cells = new List<(float X, float Y, nint Node)>();
        CollectDragDropCells(&addon->UldManager, cells);
        var ordered = cells.OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray();
        return slotIndex < ordered.Length ? (AtkResNode*)ordered[slotIndex].Node : null;
    }

    private static unsafe void CollectDragDropCells(AtkUldManager* manager, List<(float X, float Y, nint Node)> cells)
    {
        if (manager == null || manager->NodeList == null)
            return;
        for (var index = 0u; index < manager->NodeListCount; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !IsEffectivelyVisible(node))
                continue;
            var componentNode = node->GetAsAtkComponentNode();
            if (componentNode != null && componentNode->Component != null)
            {
                if (componentNode->Component->GetComponentType() == ComponentType.DragDrop)
                {
                    var x = 0f;
                    var y = 0f;
                    node->GetPositionFloat(&x, &y);
                    cells.Add((x, y, (nint)node));
                }
                CollectDragDropCells(&componentNode->Component->UldManager, cells);
            }
        }
    }

    private unsafe void HideArmouryTooltip()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("ArmouryBoard", 1);
        if (addon == null)
            return;
        Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.HideTooltip(addon->Id);
    }

    private readonly RenderedArmouryDifferentialCoordinator armouryDifferential = new();
    private Func<string, RenderedItemDetailObservation, uint?>? armouryNameResolver;
    private DateTimeOffset armouryTooltipDispatchedAt;
    private string? armouryCandidateSignature;
    private DateTimeOffset armouryCandidateStartedAt;
    private string armouryOccupancyCheckedContainer = string.Empty;
    private int armouryDispatchAttempts;

    public RenderedArmouryDifferentialProgress BeginArmouryDifferential(
        IReadOnlyList<AgentBridgeInventoryStructItem> structBaseline,
        Func<string, RenderedItemDetailObservation, uint?> nameResolver)
    {
        armouryNameResolver = nameResolver ?? throw new ArgumentNullException(nameof(nameResolver));
        armouryCandidateSignature = null;
        armouryOccupancyCheckedContainer = string.Empty;
        armouryDispatchAttempts = 0;
        armouryDifferential.Begin(structBaseline);
        TryOpenArmouryBoard();
        return armouryDifferential.Snapshot();
    }

    public RenderedArmouryDifferentialProgress CancelArmouryDifferential() => armouryDifferential.Cancel();

    public RenderedArmouryDifferentialProgress CaptureArmouryDifferentialSnapshot() => armouryDifferential.Snapshot();

    public unsafe RenderedArmouryDifferentialProgress AdvanceArmouryDifferential()
    {
        var current = armouryDifferential.Current;
        if (current is null || armouryNameResolver is null)
            return armouryDifferential.Snapshot();
        var addon = gameGui.GetAddonByName<AtkUnitBase>("ArmouryBoard", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
            return FailArmouryDifferential("The rendered Armoury Board closed during the differential proof.");
        var board = (AddonArmouryBoard*)addon;
        if (!Enum.TryParse<FFXIVClientStructs.FFXIV.Client.Game.InventoryType>(current.Value.Container, out var inventoryType))
            return FailArmouryDifferential($"Unsupported armoury container '{current.Value.Container}'.");
        if (!TrySelectArmouryTab(board, inventoryType, out var tabDiagnostic))
            return armouryDifferential.Snapshot() with { Diagnostic = tabDiagnostic };

        if (!string.Equals(armouryOccupancyCheckedContainer, current.Value.Container, StringComparison.Ordinal))
        {
            var iconCount = CountRenderedIconCells(addon);
            armouryDifferential.RecordOccupancyCount(current.Value.Container, armouryDifferential.StructCountFor(current.Value.Container), iconCount);
            armouryOccupancyCheckedContainer = current.Value.Container;
        }

        if (armouryCandidateSignature is null)
        {
            var slotNode = ResolveArmourySlotNode(addon, (short)current.Value.SlotIndex);
            if (slotNode == null)
                return FailArmouryDifferential($"Rendered armoury slot {current.Value.SlotIndex} on {current.Value.Container} does not resolve.");
            // No per-slot Hide: ShowTooltip replaces the previous tooltip, and the
            // equipment scan proves replacement is reliable; Hide+Show races drop requests.
            if (!Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.TryShowInventoryItemTooltip(
                    addon->Id, slotNode, ArmouryTooltipTypeOrId(inventoryType), (short)current.Value.SlotIndex))
                return FailArmouryDifferential($"The game rejected the armoury tooltip request for {current.Value.Container}:{current.Value.SlotIndex}.");
            armouryTooltipDispatchedAt = DateTimeOffset.UtcNow;
            armouryCandidateSignature = string.Empty;
            armouryCandidateStartedAt = DateTimeOffset.UtcNow;
            return armouryDifferential.Snapshot();
        }

        var now = DateTimeOffset.UtcNow;
        if (now - armouryTooltipDispatchedAt < TimeSpan.FromMilliseconds(300))
            return armouryDifferential.Snapshot();
        if (now - armouryTooltipDispatchedAt > TimeSpan.FromSeconds(3))
        {
            // A silent dispatch drop must not be misread as a data disagreement: re-dispatch
            // up to three times before declaring that nothing rendered.
            if (armouryDispatchAttempts < 3)
            {
                armouryDispatchAttempts++;
                armouryCandidateSignature = null;
                return armouryDifferential.Snapshot();
            }
            var timedOut = armouryDifferential.RecordRenderedObservation(current.Value.Container, current.Value.SlotIndex, null, null, null);
            armouryCandidateSignature = null;
            armouryDispatchAttempts = 0;
            return timedOut;
        }
        var observation = RenderedItemDetailParser.Parse(Capture());
        string signature;
        if (observation.Status == RenderedItemDetailStatus.Complete)
        {
            var resolvedId = armouryNameResolver(observation.Name!, observation);
            signature = resolvedId is null
                ? $"unresolved:{observation.Name}"
                : $"resolved:{resolvedId}:{observation.Quality}";
        }
        else
        {
            return armouryDifferential.Snapshot();
        }
        if (signature != armouryCandidateSignature)
        {
            armouryCandidateSignature = signature;
            armouryCandidateStartedAt = now;
            return armouryDifferential.Snapshot();
        }
        if (now - armouryCandidateStartedAt < TimeSpan.FromMilliseconds(300))
            return armouryDifferential.Snapshot();

        var renderedId = observation.Status == RenderedItemDetailStatus.Complete
            ? armouryNameResolver(observation.Name!, observation)
            : null;
        var result = armouryDifferential.RecordRenderedObservation(
            current.Value.Container,
            current.Value.SlotIndex,
            renderedId,
            observation.Status == RenderedItemDetailStatus.Complete ? observation.Quality == RenderedItemQuality.High : null,
            observation.Status == RenderedItemDetailStatus.Complete ? observation.Name : null);
        armouryCandidateSignature = null;
        armouryDispatchAttempts = 0;
        return result;
    }

    private RenderedArmouryDifferentialProgress FailArmouryDifferential(string message)
    {
        armouryCandidateSignature = null;
        return armouryDifferential.Fail(message);
    }

    private static unsafe int CountRenderedIconCells(AtkUnitBase* addon)
    {
        var count = 0;
        var manager = &addon->UldManager;
        CountIconCells(manager, ref count);
        return count;
    }

    private static unsafe void CountIconCells(AtkUldManager* manager, ref int count)
    {
        if (manager == null || manager->NodeList == null)
            return;
        for (var index = 0u; index < manager->NodeListCount; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !IsEffectivelyVisible(node))
                continue;
            var dragDrop = node->GetAsAtkComponentDragDrop();
            if (dragDrop != null && dragDrop->GetIconId() != 0)
                count++;
            var componentNode = node->GetAsAtkComponentNode();
            if (componentNode != null && componentNode->Component != null)
                CountIconCells(&componentNode->Component->UldManager, ref count);
        }
    }

    /// <summary>
    /// Maps a rendered Character slot position to its EquippedItems container index.
    /// The container retains the legacy belt slot at index 5, so later slots shift by one.
    /// Rings are a symmetric pair for scanning purposes; the upper rendered ring maps to
    /// container index 11 and the lower to 12.
    /// </summary>
    private static int EquippedContainerIndex(string positionKey) => positionKey switch
    {
        "main-hand" => 0,
        "off-hand" => 1,
        "head" => 2,
        "body" => 3,
        "hands" => 4,
        "legs" => 6,
        "feet" => 7,
        "ears" => 8,
        "neck" => 9,
        "wrists" => 10,
        "ring-left" => 11,
        "ring-right" => 12,
        _ => -1,
    };

    private unsafe bool TryRequestEquipmentTooltip(RenderedEquipmentSlotTarget target, out string reason)
    {
        reason = string.Empty;
        var containerIndex = EquippedContainerIndex(target.PositionKey);
        if (containerIndex < 0)
        {
            reason = $"Rendered equipment slot {target.PositionKey} has no supported equipped-container index.";
            return false;
        }
        var addon = gameGui.GetAddonByName<AtkUnitBase>("Character", 1);
        if (addon == null || addon->RootNode == null || !addon->RootNode->IsVisible())
        {
            reason = "The rendered Character addon is unavailable; the equipment tooltip request fails closed.";
            return false;
        }
        var node = ResolveCharacterNodeByPath(addon, target.NodePath);
        if (node == null || !IsEffectivelyVisible(node))
        {
            reason = $"The rendered node {target.NodePath} no longer resolves; the equipment tooltip request fails closed.";
            return false;
        }
        if (!Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.TryShowEquippedItemTooltip(
                addon->Id, node, (short)containerIndex))
        {
            reason = $"The game rejected the ItemDetail tooltip request for {target.PositionKey}; equipment observation fails closed.";
            return false;
        }
        return true;
    }

    private unsafe void HideEquipmentTooltip()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("Character", 1);
        if (addon == null)
            return;
        Franthropy.Dalamud.Automation.Ui.RenderedItemDetailTooltipRequest.HideTooltip(addon->Id);
    }

    private static unsafe AtkResNode* ResolveCharacterNodeByPath(AtkUnitBase* addon, string nodePath)
    {
        var segments = nodePath.Split('/');
        if (segments.Length < 2 || !string.Equals(segments[0], "Character", StringComparison.Ordinal))
            return null;
        var manager = &addon->UldManager;
        AtkResNode* current = null;
        for (var depth = 1; depth < segments.Length; depth++)
        {
            if (!uint.TryParse(segments[depth], out var nodeId) || manager == null || manager->NodeList == null)
                return null;
            current = null;
            for (var index = 0u; index < manager->NodeListCount; index++)
            {
                var candidate = manager->NodeList[index];
                if (candidate != null && candidate->NodeId == nodeId)
                {
                    current = candidate;
                    break;
                }
            }
            if (current == null)
                return null;
            if (depth + 1 < segments.Length)
            {
                var componentNode = current->GetAsAtkComponentNode();
                manager = componentNode != null && componentNode->Component != null
                    ? &componentNode->Component->UldManager
                    : null;
            }
        }
        return current;
    }

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
