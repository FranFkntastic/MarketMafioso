using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Settings;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class AdvancedSettingsPages
{
    private readonly Configuration config;
    private readonly Action stopMarketAcquisitionRoute;
    private string unlockKeyBuffer = string.Empty;
    private string unlockStatus = "Private module is hidden until unlocked.";
    private bool showUnlockKey;

    public AdvancedSettingsPages(Configuration config, Action stopMarketAcquisitionRoute)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.stopMarketAcquisitionRoute = stopMarketAcquisitionRoute ?? throw new ArgumentNullException(nameof(stopMarketAcquisitionRoute));
        Descriptors =
        [
            new("advanced.craft-quotes", "Advanced / Craft Quote Evidence", DrawCraftQuotes, 80,
                searchTerms: ["Workshop Host", "manual fallback", "craft cost", "appraisal"]),
            new("advanced.agent-bridge", "Advanced / Agent Bridge", DrawAgentBridge, 81,
                searchTerms: ["local agent test bridge", "named pipe", "review capture"]),
            new("advanced.private-features", "Advanced / Private Features", DrawPrivateFeatures, 82,
                searchTerms: ["unlock private module", "feature key", "lock feature"]),
        ];
    }

    public IReadOnlyList<SettingsPageDescriptor> Descriptors { get; }

    private void DrawCraftQuotes(SettingsPageContext context)
    {
        DrawCheckbox(context, "Enable Workshop Host craft quotes",
            "Uses the configured Workshop Host service for advisory craft-cost evidence when the host advertises craft.appraise.",
            () => config.EnableWorkshopHostCraftQuotes, value => config.EnableWorkshopHostCraftQuotes = value);
        DrawCheckbox(context, "Enable manual craft-cost fallback",
            "Default off. Workshop Host should be the normal quote path; manual craft cost entry is only for local troubleshooting.",
            () => config.EnableCraftArchitectManualFallback, value => config.EnableCraftArchitectManualFallback = value);
    }

    private void DrawAgentBridge(SettingsPageContext context) => DrawCheckbox(context, "Enable local agent test bridge",
        "Dev-only named-pipe bridge. It exposes reviewed state and semantic UI controls without directly controlling the game client.",
        () => config.EnableAgentBridge, value => config.EnableAgentBridge = value);

    private void DrawPrivateFeatures(SettingsPageContext context)
    {
        if (MarketAcquisitionUnlock.IsUnlocked(config))
        {
            var unlockedAt = config.MarketAcquisitionUnlockedAtUtc == null
                ? "enabled"
                : $"enabled {config.MarketAcquisitionUnlockedAtUtc.Value:yyyy-MM-dd HH:mm:ss} UTC";
            ImGui.TextColored(MarketMafiosoUiTheme.Success, $"Market Acquisition {unlockedAt}.");
            if (ImGui.Button("Lock Market Acquisition"))
            {
                stopMarketAcquisitionRoute();
                MarketAcquisitionUnlock.Lock(config);
                config.Save();
                unlockKeyBuffer = string.Empty;
                unlockStatus = "Private module locked.";
            }
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Locking hides the module and its settings. Existing drafts, inbox work, and execution history remain untouched.");
            return;
        }

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Private/internal modules are hidden by default.");
        ImGui.Text("Unlock key:");
        ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 82));
        var flags = showUnlockKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        ImGui.InputText("##marketAcquisitionUnlockKey", ref unlockKeyBuffer, 256, flags);
        ImGui.SameLine();
        if (ImGui.Button(showUnlockKey ? "Hide##marketAcquisitionUnlock" : "Show##marketAcquisitionUnlock", new Vector2(72, 0))) showUnlockKey = !showUnlockKey;
        if (ImGuiUi.Button("Unlock private module", !string.IsNullOrWhiteSpace(unlockKeyBuffer)))
        {
            if (MarketAcquisitionUnlock.TryUnlock(config, unlockKeyBuffer))
            {
                config.Save();
                unlockKeyBuffer = string.Empty;
                unlockStatus = "Private module unlocked.";
            }
            else unlockStatus = "Unlock key was not accepted.";
        }
        ImGui.TextColored(unlockStatus.Contains("not accepted", StringComparison.OrdinalIgnoreCase) ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Muted, unlockStatus);
    }

    private void DrawCheckbox(SettingsPageContext context, string label, string description, Func<bool> getter, Action<bool> setter) =>
        SettingsPageUi.DrawConfigCheckbox(config, context, label, description, getter, setter);
}
