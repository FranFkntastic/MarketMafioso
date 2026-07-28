using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MarketMafioso.Windows.Main;

internal readonly record struct UtilityStatusFact(string Label, string Value, Vector4? Color = null);

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
            if (fact.Color is { } color)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.TextWrapped(fact.Value);
                ImGui.PopStyleColor();
            }
            else
                ImGui.TextWrapped(fact.Value);
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
