using System.Numerics;
using System;
using Dalamud.Bindings.ImGui;

namespace MarketMafioso.Windows;

internal static class ImGuiUi
{
    public const ImGuiTableFlags InteractiveTableFlags =
        ImGuiTableFlags.Borders |
        ImGuiTableFlags.RowBg |
        ImGuiTableFlags.Resizable |
        ImGuiTableFlags.Reorderable |
        ImGuiTableFlags.Hideable;

    public static void SectionHeader(string text, Vector4 color)
    {
        ImGui.TextColored(color, text);
        ImGui.Separator();
    }

    public static void SectionHeaderWithActions(string text, Vector4 color, Action drawActions)
    {
        ImGui.TextColored(color, text);
        ImGui.SameLine();
        drawActions();
        ImGui.Separator();
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

    public static bool MenuItem(string label, bool enabled)
    {
        if (!enabled)
            ImGui.BeginDisabled();

        var clicked = ImGui.MenuItem(label);

        if (!enabled)
            ImGui.EndDisabled();

        return clicked;
    }
}
