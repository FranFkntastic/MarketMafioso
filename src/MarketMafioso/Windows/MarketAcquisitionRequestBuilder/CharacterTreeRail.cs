using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Layout;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

/// <summary>One tickable entry under a character: a container, a retainer, or Any space.</summary>
internal sealed record CharacterTreeLeaf(
    string IdPart,
    string Label,
    bool Checked,
    bool Enabled,
    bool Available,
    string? RightText,
    string? CheckTooltip,
    string AgentLabel,
    string AgentValue,
    Action<bool> Set);

/// <summary>
/// One character in a <see cref="CharacterTreeRail"/>: a tri-state checkbox over its leaves, then an expandable name.
/// SectionBefore draws a muted divider label above it (e.g. "Other accounts").
/// </summary>
internal sealed record CharacterTreeGroup(
    ulong LocalContentId,
    uint HomeWorldId,
    string Name,
    string? HomeWorldName,
    Vector4? Tint,
    string? SectionBefore,
    bool Expanded,
    Action<bool> SetExpanded,
    bool Toggleable,
    string AgentLabel,
    Action<bool> SetAll,
    string? CollapsedRightText,
    IReadOnlyList<CharacterTreeLeaf> Leaves);

/// <summary>
/// The bordered character tree beside a Market Acquisition table ("Count stock from", "Destinations"). Sized to its
/// longest visible label within <see cref="MinWidth"/>–<see cref="MaxWidth"/>, or to one button when collapsed.
/// Agent controls: <c>{prefix}.rail</c>, <c>{prefix}.{lcid}.{world}</c> (+ <c>.expand</c>), and
/// <c>{prefix}.{lcid}.{world}.{leaf}</c>.
/// </summary>
internal sealed class CharacterTreeRail
{
    public const float MinWidth = 180f;
    public const float MaxWidth = 300f;

    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly string title;
    private readonly string agentPrefix;
    private readonly string expandNoun;
    private readonly string railId;
    private readonly string collapseLabel;
    private readonly string expandLabel;
    // Ids, labels and agent callbacks per group and leaf object: a caller that keeps its group list between frames
    // draws the rail without building any of them again.
    private readonly ConditionalWeakTable<CharacterTreeGroup, GroupText> groupText = new();
    private readonly ConditionalWeakTable<CharacterTreeLeaf, LeafText> leafText = new();
    private Action<bool>? toggleTarget;
    private Action collapseRail = static () => { }, expandRail = static () => { };

    private sealed class GroupText(CharacterTreeRail rail, CharacterTreeGroup group)
    {
        public readonly string ControlId = $"{rail.agentPrefix}.{group.LocalContentId}.{group.HomeWorldId}";
        public readonly string ExpandId = $"{rail.agentPrefix}.{group.LocalContentId}.{group.HomeWorldId}.expand";
        public readonly string CollapseLabel = $"Collapse {group.Name}'s {rail.expandNoun}";
        public readonly string ExpandLabel = $"Expand {group.Name}'s {rail.expandNoun}";
        public readonly string? Tooltip = string.IsNullOrWhiteSpace(group.HomeWorldName) ? null : $"{group.Name} · {group.HomeWorldName}";
        public readonly int Ticked = CountTicked(group.Leaves);
        public readonly Action ToggleAll = () => group.SetAll(!(group.Leaves.Count > 0 && CountTicked(group.Leaves) == group.Leaves.Count));
        public readonly Action ToggleExpanded = () => group.SetExpanded(!group.Expanded);
        public string? Value;
        public float NodeRoom = -1f;
        public string NodeLabel = string.Empty;
    }

    private sealed class LeafText(string groupControlId, CharacterTreeLeaf leaf)
    {
        public readonly string ControlId = $"{groupControlId}.{leaf.IdPart}";
        public readonly Action Toggle = () => leaf.Set(!leaf.Checked);
        public float Room = -1f;
        public string Shown = leaf.Label;
    }

    private static int CountTicked(IReadOnlyList<CharacterTreeLeaf> leaves)
    {
        var ticked = 0;
        for (var index = 0; index < leaves.Count; index++)
            if (leaves[index].Checked)
                ticked++;
        return ticked;
    }

    /// <param name="expandNoun">What a character's children are, for agent labels ("stock sources", "destinations").</param>
    public CharacterTreeRail(AgentBridgeUiReviewRegistry reviewRegistry, string title, string agentPrefix, string expandNoun)
    {
        this.reviewRegistry = reviewRegistry;
        this.title = title;
        this.agentPrefix = agentPrefix;
        this.expandNoun = expandNoun;
        railId = $"{agentPrefix}.rail";
        collapseLabel = $"Collapse {title}";
        expandLabel = $"Expand {title}";
    }

    public static float CollapsedWidth => DalamudUiLayout.ButtonWidth(">") + (ImGui.GetStyle().WindowPadding.X * 2f);

    /// <summary>The rail's width: its longest visible row, clamped; one toggle button when collapsed.</summary>
    public float Width(IReadOnlyList<CharacterTreeGroup> groups, bool collapsed)
    {
        if (collapsed)
            return CollapsedWidth;
        var style = ImGui.GetStyle();
        var check = ImGui.GetFrameHeight() + style.ItemSpacing.X;
        var widest = ImGui.CalcTextSize(title).X + style.ItemSpacing.X + DalamudUiLayout.ButtonWidth("<");
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            var right = group.Expanded || group.CollapsedRightText is null ? 0f : ImGui.CalcTextSize(group.CollapsedRightText).X + style.ItemSpacing.X;
            widest = Math.Max(widest, check + ImGui.GetTreeNodeToLabelSpacing() + ImGui.CalcTextSize(group.Name).X + right);
            if (group.SectionBefore is { } section)
                widest = Math.Max(widest, ImGui.CalcTextSize(section).X);
            if (!group.Expanded)
                continue;
            for (var leafIndex = 0; leafIndex < group.Leaves.Count; leafIndex++)
            {
                var leaf = group.Leaves[leafIndex];
                widest = Math.Max(widest, ImGui.GetTreeNodeToLabelSpacing() + check + ImGui.CalcTextSize(leaf.Label).X +
                                          (leaf.RightText is null ? 0f : ImGui.CalcTextSize(leaf.RightText).X + style.ItemSpacing.X));
            }
        }
        // Room for the child's padding and a vertical scrollbar when the tree outgrows the rail.
        return Math.Clamp(widest + (style.WindowPadding.X * 2f) + style.ScrollbarSize, MinWidth, MaxWidth);
    }

    public void Draw(string childId, IReadOnlyList<CharacterTreeGroup> groups, bool collapsed, Action<bool> setCollapsed, float height)
    {
        if (ImGui.BeginChild(childId, new Vector2(Width(groups, collapsed), height), true))
        {
            if (collapsed)
            {
                DrawToggle(collapse: false, setCollapsed);
            }
            else
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(MainWindow.ColHeader, title);
                DalamudUiLayout.SameLineRight(DalamudUiLayout.ButtonWidth("<"));
                DrawToggle(collapse: true, setCollapsed);
                ImGui.Separator();
                for (var index = 0; index < groups.Count; index++)
                {
                    var group = groups[index];
                    if (group.SectionBefore is { } section)
                    {
                        ImGui.Spacing();
                        ImGui.Separator();
                        ImGui.TextDisabled(section);
                    }
                    DrawGroup(group);
                }
            }
        }
        ImGui.EndChild();
    }

    private void DrawToggle(bool collapse, Action<bool> setCollapsed)
    {
        if (!ReferenceEquals(toggleTarget, setCollapsed))
        {
            toggleTarget = setCollapsed;
            collapseRail = () => setCollapsed(true);
            expandRail = () => setCollapsed(false);
        }
        if (ImGui.Button(collapse ? "<##RailToggle" : ">##RailToggle"))
            setCollapsed(collapse);
        if (!collapse && ImGui.IsItemHovered())
            ImGui.SetTooltip(title);
        reviewRegistry.RegisterLastItem(
            railId,
            collapse ? collapseLabel : expandLabel,
            AgentBridgeUiControlKind.Button,
            enabled: true,
            selected: collapse,
            collapse ? "Expanded" : "Collapsed",
            collapse ? collapseRail : expandRail);
    }

    private void DrawGroup(CharacterTreeGroup group)
    {
        var text = groupText.GetValue(group, created => new GroupText(this, created));
        var controlId = text.ControlId;
        ImGui.PushID(controlId);
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var ticked = text.Ticked;
        var all = group.Leaves.Count > 0 && ticked == group.Leaves.Count;
        var mixed = ticked > 0 && !all;
        text.Value ??= all ? "All" : mixed ? $"{ticked:N0} of {group.Leaves.Count:N0}" : "None";

        var check = all;
        ImGui.BeginDisabled(!group.Toggleable);
        if (mixed)
            ImGuiP.PushItemFlag(ImGuiItemFlags.MixedValue, true);
        var toggled = ImGui.Checkbox("##Character", ref check);
        if (mixed)
            ImGuiP.PopItemFlag();
        ImGui.EndDisabled();
        if (toggled)
            group.SetAll(!all);
        reviewRegistry.RegisterLastItem(
            controlId,
            group.AgentLabel,
            AgentBridgeUiControlKind.Toggle,
            group.Toggleable,
            all,
            text.Value,
            text.ToggleAll);

        var rightText = group.Expanded ? null : group.CollapsedRightText;
        ImGui.SameLine();
        var room = rightEdge - ImGui.GetCursorPosX() - ImGui.GetTreeNodeToLabelSpacing() -
                   (rightText is null ? 0f : ImGui.CalcTextSize(rightText).X + ImGui.GetStyle().ItemSpacing.X);
        room = Math.Max(0f, room);
        if (room != text.NodeRoom)
        {
            text.NodeRoom = room;
            text.NodeLabel = $"{DalamudUiLayout.ElideText(group.Name, room)}##Node";
        }
        if (group.Tint is { } tint)
            ImGui.PushStyleColor(ImGuiCol.Text, tint);
        ImGui.SetNextItemOpen(group.Expanded, ImGuiCond.Always);
        var open = ImGui.TreeNodeEx(text.NodeLabel, ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.NoTreePushOnOpen);
        if (group.Tint is not null)
            ImGui.PopStyleColor();
        if (open != group.Expanded)
            group.SetExpanded(open);
        if (text.Tooltip is { } tooltip && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        reviewRegistry.RegisterLastItem(
            text.ExpandId,
            open ? text.CollapseLabel : text.ExpandLabel,
            AgentBridgeUiControlKind.Button,
            enabled: true,
            selected: open,
            open ? "Expanded" : "Collapsed",
            text.ToggleExpanded);
        if (rightText is not null)
        {
            ImGui.SameLine(rightEdge - ImGui.CalcTextSize(rightText).X);
            ImGui.TextColored(MainWindow.ColMuted, rightText);
        }

        if (open)
        {
            ImGui.Indent(ImGui.GetTreeNodeToLabelSpacing());
            for (var index = 0; index < group.Leaves.Count; index++)
                DrawLeaf(controlId, group.Leaves[index]);
            ImGui.Unindent(ImGui.GetTreeNodeToLabelSpacing());
        }
        ImGui.PopID();
    }

    private void DrawLeaf(string groupControlId, CharacterTreeLeaf leaf)
    {
        var text = leafText.GetValue(leaf, created => new LeafText(groupControlId, created));
        ImGui.PushID(leaf.IdPart);
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var check = leaf.Checked;
        ImGui.BeginDisabled(!leaf.Enabled);
        var toggled = ImGui.Checkbox("##Leaf", ref check);
        ImGui.EndDisabled();
        if (toggled)
            leaf.Set(check);
        if (leaf.CheckTooltip is { } tooltip && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        reviewRegistry.RegisterLastItem(
            text.ControlId,
            leaf.AgentLabel,
            AgentBridgeUiControlKind.Toggle,
            leaf.Enabled,
            leaf.Checked,
            leaf.AgentValue,
            text.Toggle);

        ImGui.SameLine();
        var room = rightEdge - ImGui.GetCursorPosX() -
                   (leaf.RightText is null ? 0f : ImGui.CalcTextSize(leaf.RightText).X + ImGui.GetStyle().ItemSpacing.X);
        room = Math.Max(0f, room);
        if (room != text.Room)
        {
            text.Room = room;
            text.Shown = DalamudUiLayout.ElideText(leaf.Label, room);
        }
        var shown = text.Shown;
        if (leaf.Available)
            ImGui.TextUnformatted(shown);
        else
            ImGui.TextColored(MainWindow.ColMuted, shown);
        if (shown.Length != leaf.Label.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(leaf.Label);
        if (leaf.RightText is not null)
        {
            ImGui.SameLine(rightEdge - ImGui.CalcTextSize(leaf.RightText).X);
            ImGui.TextColored(MainWindow.ColMuted, leaf.RightText);
        }
        ImGui.PopID();
    }
}
