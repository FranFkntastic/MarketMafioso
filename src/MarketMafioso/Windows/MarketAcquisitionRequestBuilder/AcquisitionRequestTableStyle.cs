using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketMafioso.Windows;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

internal static class AcquisitionRequestTableStyle
{
    public const float ScrollableBodyRowCount = 5f;

    public const ImGuiTableFlags LineTableFlags =
        ImGuiUi.InteractiveTableFlags |
        ImGuiTableFlags.ScrollX |
        ImGuiTableFlags.ScrollY;

    public const ImGuiTableFlags ClaimedBatchLineTableFlags = LineTableFlags;

    public static bool UsesHouseInteractiveTableFlags =>
        (LineTableFlags & ImGuiUi.InteractiveTableFlags) == ImGuiUi.InteractiveTableFlags;

    public static bool UsesScrollableRegion =>
        LineTableFlags.HasFlag(ImGuiTableFlags.ScrollX) &&
        LineTableFlags.HasFlag(ImGuiTableFlags.ScrollY);

    public static bool ClaimedBatchLinesUseHouseInteractiveTableFlags =>
        (ClaimedBatchLineTableFlags & ImGuiUi.InteractiveTableFlags) == ImGuiUi.InteractiveTableFlags;

    public static bool ClaimedBatchLinesUseScrollableRegion =>
        ClaimedBatchLineTableFlags.HasFlag(ImGuiTableFlags.ScrollX) &&
        ClaimedBatchLineTableFlags.HasFlag(ImGuiTableFlags.ScrollY);

    public static Vector2 FiveLineTableSize() =>
        new(0, (ImGui.GetTextLineHeightWithSpacing() * (ScrollableBodyRowCount + 1f)) + 8f);
}
