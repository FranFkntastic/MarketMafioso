using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MarketMafioso.Windows.Main;

internal readonly record struct UtilityStatusFact(
    string Label,
    string Value,
    Vector4? Color = null,
    Action? DrawAction = null,
    float ActionWidth = 0f);

internal static class UtilityWorkspaceUi
{
    public static void DrawStatusStrip(string id, IReadOnlyList<UtilityStatusFact> facts)
    {
        if (facts.Count == 0)
            return;

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame;
        if (!ImGui.BeginTable(id, facts.Count, flags))
            return;

        foreach (var fact in facts)
            ImGui.TableSetupColumn(fact.Label, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        ImGui.TableNextRow();
        foreach (var fact in facts)
        {
            ImGui.TableNextColumn();
            var cellStartX = ImGui.GetCursorPosX();
            var valueWidth = fact.DrawAction == null
                ? 0f
                : Math.Max(1f, ImGui.GetContentRegionAvail().X - fact.ActionWidth - ImGui.GetStyle().ItemSpacing.X);
            if (fact.DrawAction != null)
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + valueWidth);
            if (fact.Color is { } color)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.TextWrapped(fact.Value);
                ImGui.PopStyleColor();
            }
            else
                ImGui.TextWrapped(fact.Value);
            if (fact.DrawAction != null)
            {
                ImGui.PopTextWrapPos();
                ImGui.SameLine();
                ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), cellStartX + valueWidth + ImGui.GetStyle().ItemSpacing.X));
                fact.DrawAction();
            }
        }

        ImGui.EndTable();
    }

    public static void DrawModuleHeader(string title, string summary)
    {
        ImGui.Spacing();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, title);
        ImGui.TextWrapped(summary);
        ImGui.Spacing();
    }

    public static float RemainingTableHeight(float minimum = 180f, float reserved = 0f) =>
        Math.Max(minimum, ImGui.GetContentRegionAvail().Y - reserved);
}
