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
        Action<bool> setter) =>
        DrawConfigCheckbox(config.Save, context, label, description, getter, setter);

    public static void DrawConfigCheckbox(
        Action save,
        SettingsPageContext context,
        string label,
        string description,
        Func<bool> getter,
        Action<bool> setter)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (!context.Matches(label, description))
            return;

        var value = getter();
        if (ImGui.Checkbox(label, ref value))
        {
            setter(value);
            save();
        }
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, description);
        ImGui.Spacing();
    }
}

