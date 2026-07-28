using System.Numerics;
using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Franthropy.Dalamud.UI.Styling;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows;

internal static class ImGuiUi
{
    public const ImGuiTableFlags InteractiveTableFlags =
        ImGuiTableFlags.BordersOuter |
        ImGuiTableFlags.BordersInnerH |
        ImGuiTableFlags.RowBg |
        ImGuiTableFlags.Resizable |
        ImGuiTableFlags.Reorderable |
        ImGuiTableFlags.Hideable;

    public static void SectionHeader(string text, Vector4 color)
    {
        DalamudUiChrome.DrawSectionHeading(
            text,
            null,
            MarketMafiosoUiTheme.Palette with { Accent = color });
    }

    public static void SectionHeaderWithActions(string text, Vector4 color, Action drawActions, float actionWidth = 0)
    {
        DalamudUiChrome.DrawSectionHeading(
            text,
            null,
            MarketMafiosoUiTheme.Palette with { Accent = color },
            drawActions,
            actionWidth);
    }

    public static void SameLineRight(float width)
    {
        ImGui.SameLine();
        var rightAlignedX = ImGui.GetWindowContentRegionMax().X - width;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), rightAlignedX));
    }

    public static void TableTextRightAligned(string text)
    {
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width));
        ImGui.TextUnformatted(text);
    }

    public static void TableTextRightAligned(string text, Vector4 color)
    {
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - width));
        ImGui.TextColored(color, text);
    }

    public static bool Button(string label, bool enabled)
    {
        return Button(label, Vector2.Zero, enabled);
    }

    public static bool Button(string label, Vector2 size, bool enabled)
    {
        if (!enabled)
            ImGui.BeginDisabled();

        var clicked = size == Vector2.Zero
            ? ImGui.Button(label)
            : ImGui.Button(label, size);

        if (!enabled)
            ImGui.EndDisabled();

        return clicked;
    }

    public static bool PrimaryButton(string label, bool enabled)
    {
        using var style = DalamudUiChrome.PushButton(MarketMafiosoUiTheme.Palette);
        return Button(label, enabled);
    }

    public static bool MenuItem(string label, bool enabled)
    {
        if (!enabled)
            ImGui.BeginDisabled();

        var clicked = ImGui.MenuItem(label);

        if (!enabled)
            ImGui.EndDisabled();

        return clicked;
    }

    public static bool MenuButton(
        string label,
        bool enabled = true,
        bool primary = false)
    {
        if (!enabled)
            ImGui.BeginDisabled();

        using var style = DalamudUiChrome.PushButton(
            MarketMafiosoUiTheme.Palette,
            quiet: !primary);
        var clicked = ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ChevronDown, label);

        if (!enabled)
            ImGui.EndDisabled();

        return clicked;
    }
}
