using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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

    /// <param name="expandNoun">What a character's children are, for agent labels ("stock sources", "destinations").</param>
    public CharacterTreeRail(AgentBridgeUiReviewRegistry reviewRegistry, string title, string agentPrefix, string expandNoun)
    {
        this.reviewRegistry = reviewRegistry;
        this.title = title;
        this.agentPrefix = agentPrefix;
        this.expandNoun = expandNoun;
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
        foreach (var group in groups)
        {
            var right = group.Expanded || group.CollapsedRightText is null ? 0f : ImGui.CalcTextSize(group.CollapsedRightText).X + style.ItemSpacing.X;
            widest = Math.Max(widest, check + ImGui.GetTreeNodeToLabelSpacing() + ImGui.CalcTextSize(group.Name).X + right);
            if (group.SectionBefore is { } section)
                widest = Math.Max(widest, ImGui.CalcTextSize(section).X);
            if (!group.Expanded)
                continue;
            foreach (var leaf in group.Leaves)
                widest = Math.Max(widest, ImGui.GetTreeNodeToLabelSpacing() + check + ImGui.CalcTextSize(leaf.Label).X +
                                          (leaf.RightText is null ? 0f : ImGui.CalcTextSize(leaf.RightText).X + style.ItemSpacing.X));
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
                foreach (var group in groups)
                {
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
        if (ImGui.Button(collapse ? "<##RailToggle" : ">##RailToggle"))
            setCollapsed(collapse);
        if (!collapse && ImGui.IsItemHovered())
            ImGui.SetTooltip(title);
        reviewRegistry.RegisterLastItem(
            $"{agentPrefix}.rail",
            collapse ? $"Collapse {title}" : $"Expand {title}",
            AgentBridgeUiControlKind.Button,
            enabled: true,
            selected: collapse,
            collapse ? "Expanded" : "Collapsed",
            () => setCollapsed(collapse));
    }

    private void DrawGroup(CharacterTreeGroup group)
    {
        var controlId = $"{agentPrefix}.{group.LocalContentId}.{group.HomeWorldId}";
        ImGui.PushID($"{group.LocalContentId}:{group.HomeWorldId}");
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var ticked = group.Leaves.Count(leaf => leaf.Checked);
        var all = group.Leaves.Count > 0 && ticked == group.Leaves.Count;
        var mixed = ticked > 0 && !all;

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
            all ? "All" : mixed ? $"{ticked:N0} of {group.Leaves.Count:N0}" : "None",
            () => group.SetAll(!all));

        var rightText = group.Expanded ? null : group.CollapsedRightText;
        ImGui.SameLine();
        var room = rightEdge - ImGui.GetCursorPosX() - ImGui.GetTreeNodeToLabelSpacing() -
                   (rightText is null ? 0f : ImGui.CalcTextSize(rightText).X + ImGui.GetStyle().ItemSpacing.X);
        var shown = DalamudUiLayout.ElideText(group.Name, Math.Max(0f, room));
        if (group.Tint is { } tint)
            ImGui.PushStyleColor(ImGuiCol.Text, tint);
        ImGui.SetNextItemOpen(group.Expanded, ImGuiCond.Always);
        var open = ImGui.TreeNodeEx($"{shown}##Node", ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.NoTreePushOnOpen);
        if (group.Tint is not null)
            ImGui.PopStyleColor();
        if (open != group.Expanded)
            group.SetExpanded(open);
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(group.HomeWorldName))
            ImGui.SetTooltip($"{group.Name} · {group.HomeWorldName}");
        reviewRegistry.RegisterLastItem(
            $"{controlId}.expand",
            $"{(open ? "Collapse" : "Expand")} {group.Name}'s {expandNoun}",
            AgentBridgeUiControlKind.Button,
            enabled: true,
            selected: open,
            open ? "Expanded" : "Collapsed",
            () => group.SetExpanded(!group.Expanded));
        if (rightText is not null)
        {
            ImGui.SameLine(rightEdge - ImGui.CalcTextSize(rightText).X);
            ImGui.TextColored(MainWindow.ColMuted, rightText);
        }

        if (open)
        {
            ImGui.Indent(ImGui.GetTreeNodeToLabelSpacing());
            foreach (var leaf in group.Leaves)
                DrawLeaf(controlId, leaf);
            ImGui.Unindent(ImGui.GetTreeNodeToLabelSpacing());
        }
        ImGui.PopID();
    }

    private void DrawLeaf(string groupControlId, CharacterTreeLeaf leaf)
    {
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
            $"{groupControlId}.{leaf.IdPart}",
            leaf.AgentLabel,
            AgentBridgeUiControlKind.Toggle,
            leaf.Enabled,
            leaf.Checked,
            leaf.AgentValue,
            () => leaf.Set(!leaf.Checked));

        ImGui.SameLine();
        var room = rightEdge - ImGui.GetCursorPosX() -
                   (leaf.RightText is null ? 0f : ImGui.CalcTextSize(leaf.RightText).X + ImGui.GetStyle().ItemSpacing.X);
        var shown = DalamudUiLayout.ElideText(leaf.Label, Math.Max(0f, room));
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
