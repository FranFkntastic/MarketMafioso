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
    float ActionWidth = 0f,
    Vector4? Accent = null,
    float? Fill = null,
    Vector4? FillColor = null,
    float FixedWidth = 0f,
    string? Tooltip = null);

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
        {
            if (fact.FixedWidth > 0f)
                ImGui.TableSetupColumn(fact.Label, ImGuiTableColumnFlags.WidthFixed, fact.FixedWidth);
            else
                ImGui.TableSetupColumn(fact.Label, ImGuiTableColumnFlags.WidthStretch);
        }
        DrawStatusStripHeaders(facts);
        ImGui.TableNextRow();
        foreach (var fact in facts)
        {
            ImGui.TableNextColumn();
            var cellStartX = ImGui.GetCursorPosX();
            var cellWidth = ImGui.GetContentRegionAvail().X;
            var cellLeft = ImGui.GetCursorScreenPos().X;
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
            if (fact.Tooltip != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(fact.Tooltip);
            var valueBottom = ImGui.GetItemRectMax().Y;
            if (fact.DrawAction != null)
            {
                ImGui.PopTextWrapPos();
                ImGui.SameLine();
                ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), cellStartX + valueWidth + ImGui.GetStyle().ItemSpacing.X));
                fact.DrawAction();
            }

            if (fact.Fill is { } fill)
                DrawFillBar(cellLeft, cellWidth, valueBottom, fill, fact.FillColor ?? fact.Color ?? MarketMafiosoUiTheme.Header);
        }

        ImGui.EndTable();
    }

    private static void DrawStatusStripHeaders(IReadOnlyList<UtilityStatusFact> facts)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        for (var index = 0; index < facts.Count; index++)
        {
            if (!ImGui.TableSetColumnIndex(index))
                continue;

            var fact = facts[index];
            var start = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            ImGui.PushID(index);
            ImGui.TableHeader(fact.Label);
            ImGui.PopID();
            if (fact.Tooltip != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(fact.Tooltip);
            if (fact.Accent is { } accent)
                DrawAccentCap(start, width, accent);
        }
    }

    /// <summary>A slim colored cap along the top edge of a header cell.</summary>
    public static void DrawAccentCap(Vector2 cellContentStart, float cellContentWidth, Vector4 accent, float thickness = 2f)
    {
        var padding = ImGui.GetStyle().CellPadding;
        var min = new Vector2(cellContentStart.X - padding.X, cellContentStart.Y - padding.Y);
        var max = new Vector2(cellContentStart.X + cellContentWidth + padding.X, min.Y + thickness);
        ImGui.GetWindowDrawList().AddRectFilled(min, max, ImGui.GetColorU32(accent));
    }

    private static void DrawFillBar(float left, float width, float valueBottom, float fill, Vector4 color)
    {
        const float height = 3f;
        var top = valueBottom + 3f;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            new Vector2(left, top),
            new Vector2(left + width, top + height),
            ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.16f)),
            height);
        var clamped = Math.Clamp(fill, 0f, 1f);
        if (clamped > 0f)
        {
            drawList.AddRectFilled(
                new Vector2(left, top),
                new Vector2(left + width * clamped, top + height),
                ImGui.GetColorU32(color),
                height);
        }

        ImGui.Dummy(new Vector2(1f, height + 4f));
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
