using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Settings;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class InventoryReporterSettingsPages
{
    private readonly Configuration config;
    private readonly Action restartTimer;

    public InventoryReporterSettingsPages(
        Configuration config,
        Action restartTimer,
        HttpReporter reporter,
        AutoRetainerRefreshService autoRetainerRefresh,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.restartTimer = restartTimer ?? throw new ArgumentNullException(nameof(restartTimer));
        Descriptors =
        [
            new InventoryReporterActionsSettingsPage(reporter, autoRetainerRefresh, reviewRegistry).Descriptor,
            new("inventory.capture", "Inventory Reporter / Capture", DrawCapture, 10,
                searchTerms: ["armoury chest", "crystal bag", "equipped gear", "saddlebag", "item names", "character world"]),
            new("inventory.scheduling", "Inventory Reporter / Scheduling", DrawScheduling, 11,
                searchTerms: ["auto-send", "retainer close", "automatic periodic sending", "send interval", "timer"]),
        ];
    }

    public IReadOnlyList<SettingsPageDescriptor> Descriptors { get; }

    private void DrawCapture(SettingsPageContext context)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Player inventory bags are always included. These options add other owned-item sources and identity fields.");
        ImGui.Spacing();
        DrawCheckbox(context, "Armoury Chest", "Include equipment stored in every Armoury Chest category.", () => config.IncludeArmoury, value => config.IncludeArmoury = value);
        DrawCheckbox(context, "Crystal bag", "Include crystals, shards, and clusters.", () => config.IncludeCrystals, value => config.IncludeCrystals = value);
        DrawCheckbox(context, "Equipped gear", "Include the character's currently equipped items.", () => config.IncludeEquipped, value => config.IncludeEquipped = value);
        DrawCheckbox(context, "Saddlebag (if subscribed)", "Include the chocobo saddlebag when its inventory is available.", () => config.IncludeSaddlebag, value => config.IncludeSaddlebag = value);
        DrawCheckbox(context, "Resolve item names via Lumina", "Write player-facing item names beside item IDs.", () => config.IncludeItemNames, value => config.IncludeItemNames = value);
        DrawCheckbox(context, "Include character name and world", "Identify the character and world that owns the report.", () => config.IncludeCharacterInfo, value => config.IncludeCharacterInfo = value);
    }

    private void DrawScheduling(SettingsPageContext context)
    {
        DrawCheckbox(context, "Auto-send on retainer window close",
            "Retainer data is cached when its window closes; visit each retainer once per session to populate the cache.",
            () => config.AutoSendOnRetainerClose, value => config.AutoSendOnRetainerClose = value);

        if (!context.Matches("Enable automatic periodic sending", "timer", "schedule", "interval")) return;
        var enabled = config.EnableAutoSendTimer;
        if (ImGui.Checkbox("Enable automatic periodic sending", ref enabled))
        {
            config.EnableAutoSendTimer = enabled;
            config.Save();
            restartTimer();
        }
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Sends the latest locally captured inventory report on a repeating timer.");
        if (!config.EnableAutoSendTimer) return;
        var interval = config.AutoSendIntervalMinutes;
        ImGui.SetNextItemWidth(120);
        if (!ImGui.InputInt("Send interval (minutes)", ref interval, 1, 5)) return;
        interval = Math.Max(1, interval);
        if (interval == config.AutoSendIntervalMinutes) return;
        config.AutoSendIntervalMinutes = interval;
        config.Save();
        restartTimer();
    }

    private void DrawCheckbox(SettingsPageContext context, string label, string description, Func<bool> getter, Action<bool> setter) =>
        SettingsPageUi.DrawConfigCheckbox(config, context, label, description, getter, setter);
}
