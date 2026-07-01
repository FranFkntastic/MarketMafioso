using MudBlazor;

namespace MarketMafioso.Dashboard.Services;

public sealed class DashboardThemeService
{
    public DashboardThemeService()
    {
        Palette = DashboardThemePalette.WarmDark();
        Theme = BuildTheme(Palette);
        RootStyle = BuildRootStyle(Palette);
    }

    public DashboardThemePalette Palette { get; }

    public MudTheme Theme { get; }

    public string RootStyle { get; }

    private static MudTheme BuildTheme(DashboardThemePalette palette)
    {
        return new MudTheme
        {
            PaletteDark = new PaletteDark
            {
                Primary = palette.Accent,
                Secondary = palette.AccentMuted,
                Surface = palette.Surface,
                Background = palette.PageBackground,
                BackgroundGray = palette.SurfaceMuted,
                AppbarBackground = palette.AppBarBackground,
                AppbarText = palette.TextPrimary,
                DrawerBackground = palette.Surface,
                TextPrimary = palette.TextPrimary,
                TextSecondary = palette.TextMuted,
                LinesDefault = palette.Border,
                TableLines = palette.Border,
                TableStriped = palette.TableStripe,
                ActionDefault = palette.TextMuted,
                ActionDisabled = palette.Disabled,
                Error = palette.Danger,
                Info = palette.Info,
                Success = palette.Success,
                Warning = palette.Warning,
            },
        };
    }

    private static string BuildRootStyle(DashboardThemePalette palette)
    {
        var variables = new Dictionary<string, string>
        {
            ["--mmf-page-bg"] = palette.PageBackground,
            ["--mmf-app-bg"] = palette.AppBarBackground,
            ["--mmf-surface"] = palette.Surface,
            ["--mmf-surface-raised"] = palette.SurfaceRaised,
            ["--mmf-surface-muted"] = palette.SurfaceMuted,
            ["--mmf-field-bg"] = palette.FieldBackground,
            ["--mmf-table-header"] = palette.TableHeader,
            ["--mmf-table-stripe"] = palette.TableStripe,
            ["--mmf-border"] = palette.Border,
            ["--mmf-border-strong"] = palette.BorderStrong,
            ["--mmf-text"] = palette.TextPrimary,
            ["--mmf-text-muted"] = palette.TextMuted,
            ["--mmf-text-subtle"] = palette.TextSubtle,
            ["--mmf-text-on-accent"] = palette.TextOnAccent,
            ["--mmf-accent"] = palette.Accent,
            ["--mmf-accent-hover"] = palette.AccentHover,
            ["--mmf-accent-muted"] = palette.AccentMuted,
            ["--mmf-info"] = palette.Info,
            ["--mmf-info-bg"] = palette.InfoBackground,
            ["--mmf-success"] = palette.Success,
            ["--mmf-warning"] = palette.Warning,
            ["--mmf-danger"] = palette.Danger,
            ["--mmf-danger-bg"] = palette.DangerBackground,
            ["--mmf-disabled"] = palette.Disabled,
        };

        return string.Join(string.Empty, variables.Select(pair => $"{pair.Key}: {pair.Value};"));
    }
}

public sealed record DashboardThemePalette(
    string PageBackground,
    string AppBarBackground,
    string Surface,
    string SurfaceRaised,
    string SurfaceMuted,
    string FieldBackground,
    string TableHeader,
    string TableStripe,
    string Border,
    string BorderStrong,
    string TextPrimary,
    string TextMuted,
    string TextSubtle,
    string TextOnAccent,
    string Accent,
    string AccentHover,
    string AccentMuted,
    string Info,
    string InfoBackground,
    string Success,
    string Warning,
    string Danger,
    string DangerBackground,
    string Disabled)
{
    public static DashboardThemePalette WarmDark()
    {
        return new DashboardThemePalette(
            PageBackground: "#0d0f10",
            AppBarBackground: "#14120f",
            Surface: "#171512",
            SurfaceRaised: "#1e1a15",
            SurfaceMuted: "#12100e",
            FieldBackground: "#0f1112",
            TableHeader: "#211d17",
            TableStripe: "#17130f",
            Border: "#3a3329",
            BorderStrong: "#5a4a35",
            TextPrimary: "#f8f1e7",
            TextMuted: "#c6b8a5",
            TextSubtle: "#8e8170",
            TextOnAccent: "#16110b",
            Accent: "#d2a247",
            AccentHover: "#e0b65e",
            AccentMuted: "#8e6734",
            Info: "#77b8d9",
            InfoBackground: "#10212a",
            Success: "#45d18a",
            Warning: "#f0c15b",
            Danger: "#ff796f",
            DangerBackground: "#331919",
            Disabled: "#6f675c");
    }
}
