using System.Numerics;
using Franthropy.Dalamud.UI.Styling;

namespace MarketMafioso.Windows.Main;

internal static class MarketMafiosoUiTheme
{
    public static readonly DalamudUiPalette Palette = new(
        new(0.38f, 0.73f, 1.00f, 1f),
        new(0.45f, 0.90f, 0.55f, 1f),
        new(1.00f, 0.75f, 0.35f, 1f),
        new(1.00f, 0.40f, 0.40f, 1f),
        new(0.92f, 0.92f, 0.91f, 1f),
        new(0.62f, 0.64f, 0.62f, 1f),
        new(0.07f, 0.08f, 0.075f, 0.94f),
        new(0.15f, 0.16f, 0.15f, 0.96f),
        new(0.30f, 0.31f, 0.29f, 1f));

    public static readonly Vector4 Header = Palette.Accent;
    public static readonly Vector4 Success = Palette.Success;
    public static readonly Vector4 Error = Palette.Error;
    public static readonly Vector4 Warning = Palette.Warning;
    public static readonly Vector4 Muted = Palette.Muted;
    public static readonly Vector4 Link = new(0.35f, 0.68f, 1.00f, 1f);
}
