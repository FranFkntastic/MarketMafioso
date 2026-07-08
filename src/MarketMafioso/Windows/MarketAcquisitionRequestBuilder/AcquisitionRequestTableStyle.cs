using Dalamud.Bindings.ImGui;
using MarketMafioso.Windows;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

internal static class AcquisitionRequestTableStyle
{
    public const ImGuiTableFlags LineTableFlags =
        ImGuiUi.InteractiveTableFlags |
        ImGuiTableFlags.ScrollX |
        ImGuiTableFlags.ScrollY;

    public static bool UsesHouseInteractiveTableFlags =>
        (LineTableFlags & ImGuiUi.InteractiveTableFlags) == ImGuiUi.InteractiveTableFlags;

    public static bool UsesScrollableRegion =>
        LineTableFlags.HasFlag(ImGuiTableFlags.ScrollX) &&
        LineTableFlags.HasFlag(ImGuiTableFlags.ScrollY);
}
