using System;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Settings;

namespace MarketMafioso.Windows.Main.Settings;

internal static class SettingsPageUi
{
    public static void DrawConfigCheckbox(
        Configuration config,
        SettingsPageContext context,
        string label,
        string description,
        Func<bool> getter,
        Action<bool> setter)
    {
        if (!context.Matches(label, description))
            return;

        var value = getter();
        if (ImGui.Checkbox(label, ref value))
        {
            setter(value);
            config.Save();
        }
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, description);
        ImGui.Spacing();
    }
}

