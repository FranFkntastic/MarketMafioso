using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Settings;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class AdvancedSettingsPages
{
    private readonly Configuration config;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;

    public AdvancedSettingsPages(
        Configuration config,
        AgentBridgeUiReviewRegistry? reviewRegistry = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.reviewRegistry = reviewRegistry ?? new AgentBridgeUiReviewRegistry();
        Descriptors =
        [
            new("advanced.craft-quotes", "Advanced / Craft Quote Evidence", DrawCraftQuotes, 80,
                searchTerms: ["Workshop Host", "manual fallback", "craft cost", "appraisal"]),
            new("advanced.agent-bridge", "Advanced / Agent Bridge", DrawAgentBridge, 81,
                searchTerms: ["local agent test bridge", "named pipe", "review capture"]),
            new("advanced.testing", "Advanced / Testing", DrawTesting, 82,
                searchTerms: ["dry run", "market acquisition", "diagnostic package", "no purchase"]),
        ];
    }

    public IReadOnlyList<SettingsPageDescriptor> Descriptors { get; }

    private void DrawCraftQuotes(SettingsPageContext context)
    {
        DrawCheckbox(context, "Enable Craft Architect quotes",
            "Uses Craft Architect's hosted appraisal API for advisory craft-cost evidence when the server advertises craft.appraise.",
            () => config.EnableWorkshopHostCraftQuotes, value => config.EnableWorkshopHostCraftQuotes = value);
        DrawCheckbox(context, "Enable manual craft-cost fallback",
            "Default off. Workshop Host should be the normal quote path; manual craft cost entry is only for local troubleshooting.",
            () => config.EnableCraftArchitectManualFallback, value => config.EnableCraftArchitectManualFallback = value);
    }

    private void DrawAgentBridge(SettingsPageContext context) => DrawCheckbox(context, "Enable local agent test bridge",
        "Off by default. Local, current-user named-pipe bridge for agent testing. It exposes reviewed state and semantic UI controls without directly controlling the game client.",
        () => config.EnableAgentBridge, value => config.EnableAgentBridge = value);

    private void DrawTesting(SettingsPageContext context)
    {
        const string label = "Enable Market Acquisition dry-run tools";
        const string description = "Exposes a diagnostics-only route action that travels, searches, revalidates, and replans against rendered UI without opening purchase or confirmation controls. Every run creates a diagnostic package.";
        if (!context.Matches(label, description))
            return;

        var enabled = config.EnableMarketAcquisitionDryRunTools;
        if (ImGui.Checkbox(label, ref enabled))
            SetDryRunToolsEnabled(enabled);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        reviewRegistry.Register(
            "settings.advanced.testing.market-acquisition-dry-run",
            $"{(enabled ? "Disable" : "Enable")} Market Acquisition dry-run tools",
            AgentBridgeUiControlKind.Toggle,
            min,
            max,
            true,
            enabled,
            enabled ? "Enabled" : "Disabled",
            () => SetDryRunToolsEnabled(!config.EnableMarketAcquisitionDryRunTools));
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, description);
        ImGui.Spacing();
    }

    private void SetDryRunToolsEnabled(bool enabled)
    {
        config.EnableMarketAcquisitionDryRunTools = enabled;
        config.Save();
    }

    private void DrawCheckbox(SettingsPageContext context, string label, string description, Func<bool> getter, Action<bool> setter) =>
        SettingsPageUi.DrawConfigCheckbox(config, context, label, description, getter, setter);
}
