using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
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
        "ItemDetail",
    ];

    private readonly IGameGui gameGui;
    private readonly RenderedGatheringStatsStabilizer gatheringStatsStabilizer = new(TimeSpan.FromSeconds(3));
    private readonly RenderedCharacterEquipmentScanCoordinator equipmentScan = new();
    private readonly object cursorSync = new();
    private NativePoint? cursorRestorePosition;

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
                return new(false, progress, "FFXIV must already be foreground and the rendered equipment slot must still be available.");
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
    /// Moves the real cursor over a currently rendered Character node so the game itself renders
    /// the authoritative ItemDetail tooltip. The caller must subsequently call <see cref="RestoreCursor"/>.
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

        lock (cursorSync)
        {
            var gameWindow = Process.GetCurrentProcess().MainWindowHandle;
            if (gameWindow == nint.Zero || GetForegroundWindow() != gameWindow)
                return false;

            if (cursorRestorePosition == null)
            {
                if (!GetCursorPos(out var original))
                    return false;
                cursorRestorePosition = original;
            }

            var origin = GetGameClientOrigin(gameWindow);
            var x = origin.X + target.Left + ((target.Right - target.Left) / 2);
            var y = origin.Y + target.Top + ((target.Bottom - target.Top) / 2);
            var moved = SetCursorPos(x, y);
            var clientX = x - origin.X;
            var clientY = y - origin.Y;
            PostMessage(gameWindow, 0x0200, nint.Zero, (nint)((clientY << 16) | (clientX & 0xffff)));
            return moved;
        }
    }

    public bool RestoreCursor()
    {
        lock (cursorSync)
        {
            if (cursorRestorePosition is not { } position)
                return false;
            cursorRestorePosition = null;
            return SetCursorPos(position.X, position.Y);
        }
    }

    private unsafe AgentBridgeRenderedAddonSnapshot CaptureAddon(string addonName)
        => CaptureAddon(addonName, gameGui.GetAddonByName<AtkUnitBase>(addonName, 1));

    private static NativePoint GetGameClientOrigin(nint handle)
    {
        if (handle == nint.Zero)
            return default;
        var origin = new NativePoint();
        return ClientToScreen(handle, ref origin) ? origin : default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint windowHandle, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint windowHandle, uint message, nint wParam, nint lParam);


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
                (node->NodeFlags & NodeFlags.RespondToMouse) != 0));

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
