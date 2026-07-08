using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed record AcquisitionRequestLineColumn(
    string Label,
    ImGuiTableColumnFlags Flags,
    float Width);

public static class AcquisitionRequestLineLayout
{
    public const float MinimumModeComboWidth = 178f;
    public const float MinimumHqComboWidth = 120f;

    public static IReadOnlyList<AcquisitionRequestLineColumn> Columns { get; } =
    [
        new("Item", ImGuiTableColumnFlags.WidthStretch, 1.6f),
        new("Mode", ImGuiTableColumnFlags.WidthFixed, MinimumModeComboWidth),
        new("Qty", ImGuiTableColumnFlags.WidthFixed, 92f),
        new("Unit Ceiling", ImGuiTableColumnFlags.WidthFixed, 124f),
        new("Spend Ceiling", ImGuiTableColumnFlags.WidthFixed, 128f),
        new("HQ", ImGuiTableColumnFlags.WidthFixed, MinimumHqComboWidth),
        new(string.Empty, ImGuiTableColumnFlags.WidthFixed, 86f),
    ];
}
