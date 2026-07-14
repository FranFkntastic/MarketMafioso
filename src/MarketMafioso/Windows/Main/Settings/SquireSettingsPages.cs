using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Settings;
using MarketMafioso.Squire;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class SquireSettingsPages
{
    private readonly Configuration config;
    private readonly SquireRuleManagerPanel ruleManager;

    public SquireSettingsPages(
        Configuration config,
        IPlayerState playerState,
        IDataManager dataManager,
        Func<SquireAnalysis?> currentAnalysis,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        ruleManager = new SquireRuleManagerPanel(config, playerState, dataManager, currentAnalysis, reviewRegistry);
        Descriptors =
        [
            new("squire.rules", "Squire / Cleanup Rules", ruleManager.Draw, 20,
                searchTerms: ["policy", "rules", "rarity", "signed", "future leveling", "materia", "Armoire", "duplicates", "disposition", "Expert Delivery", "desynthesis"]),
            new("squire.recovery", "Squire / Execution Recovery", DrawRecovery, 23,
                searchTerms: ["knocked out", "combat", "duty", "GatherBuddy", "Questionable", "Artisan", "menus", "pause automation"]),
        ];
    }

    public IReadOnlyList<SettingsPageDescriptor> Descriptors { get; }

    private void DrawRecovery(SettingsPageContext context)
    {
        DrawCheckbox(context, "Return after being knocked out",
            "Default on. Squire confirms the normal Return prompt, waits for the new area to settle, and then revalidates the batch before continuing.",
            () => config.Squire.RecoverFromKnockout, value => config.Squire.RecoverFromKnockout = value);
        DrawCheckbox(context, "Wait for combat to end",
            "Default on. Squire waits without attempting to control combat; when disabled, combat stops the run immediately.",
            () => config.Squire.WaitForCombatToEnd, value => config.Squire.WaitForCombatToEnd = value);

        var timeout = Math.Clamp(config.Squire.CombatRecoveryTimeoutSeconds, 10, 600);
        Dalamud.Bindings.ImGui.ImGui.SetNextItemWidth(Dalamud.Bindings.ImGui.ImGui.GetFontSize() * 12);
        if (Dalamud.Bindings.ImGui.ImGui.InputInt("Combat wait timeout (seconds)", ref timeout, 10, 30))
        {
            config.Squire.CombatRecoveryTimeoutSeconds = Math.Clamp(timeout, 10, 600);
            config.Save();
        }
        Dalamud.Bindings.ImGui.ImGui.TextWrapped("Squire stops explicitly when combat outlasts this limit; it never attacks, flees, or changes jobs to recover.");
        Dalamud.Bindings.ImGui.ImGui.Spacing();

        DrawCheckbox(context, "Leave a duty to execute cleanup",
            "Default off. When disabled, any active duty blocks cleanup without changing duty state.",
            () => config.Squire.LeaveDutyToExecute, value => config.Squire.LeaveDutyToExecute = value);
        DrawCheckbox(context, "Pause GatherBuddy Reborn",
            "Default on. Squire requests a cooperative pause and later releases only its own request.",
            () => config.Squire.PauseGatherBuddyReborn, value => config.Squire.PauseGatherBuddyReborn = value);
        DrawCheckbox(context, "Pause Questionable",
            "Default on. Squire waits for Questionable to reach a safe task boundary and later releases only its own pause request.",
            () => config.Squire.PauseQuestionable, value => config.Squire.PauseQuestionable = value);
        DrawCheckbox(context, "Pause Artisan processing",
            "Default on. Squire uses Artisan's stop-request contract and resumes it only when Squire created the request.",
            () => config.Squire.PauseArtisan, value => config.Squire.PauseArtisan = value);
        DrawCheckbox(context, "Close compatible user menus",
            "Default on. Squire closes known ordinary menus through normal UI callbacks, but never accepts an unrelated confirmation or dismisses an unknown modal.",
            () => config.Squire.CloseSafeUserMenus, value => config.Squire.CloseSafeUserMenus = value);
    }

    private void DrawCheckbox(SettingsPageContext context, string label, string description, Func<bool> getter, Action<bool> setter) =>
        SettingsPageUi.DrawConfigCheckbox(config, context, label, description, getter, setter);
}
