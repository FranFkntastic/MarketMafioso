using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.UI.Settings;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class SquireSettingsPages
{
    private readonly Configuration config;
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private uint? pendingExclusionRemoval;

    public SquireSettingsPages(Configuration config, IPlayerState playerState, IDataManager dataManager)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.playerState = playerState ?? throw new ArgumentNullException(nameof(playerState));
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        Descriptors =
        [
            new("squire.policy", "Squire / Cleanup Policy", DrawPolicy, 20,
                searchTerms: ["player-signed", "future job leveling", "blue purple rarity", "materia retrieval risk"]),
            new("squire.exclusions", "Squire / Cleanup Exclusions", DrawExclusions, 21,
                searchTerms: ["blacklist", "whitelist", "excluded items", "character protection", "allow cleanup"]),
            new("squire.duplicates", "Squire / Duplicate Retention", DrawDuplicateRetention, 22,
                searchTerms: ["duplicates", "copies", "minimum", "retainers", "hand me down", "reserve"]),
            new("squire.recovery", "Squire / Execution Recovery", DrawRecovery, 23,
                searchTerms: ["knocked out", "combat", "duty", "GatherBuddy", "Questionable", "Artisan", "menus", "pause automation"]),
        ];
    }

    public IReadOnlyList<SettingsPageDescriptor> Descriptors { get; }

    private void DrawPolicy(SettingsPageContext context)
    {
        DrawCheckbox(context, "Protect player-signed gear",
            "Default off. Enable this only if a player signature should prevent Squire from proposing otherwise-obsolete gear for cleanup.",
            () => config.Squire.ProtectPlayerSignedGear, value => config.Squire.ProtectPlayerSignedGear = value);
        DrawCheckbox(context, "Protect gear for future job leveling",
            "Default off. When enabled, gear above an unlocked job's current level is retained for that job to grow into.",
            () => config.Squire.ProtectFutureLevelingGearOptIn, value => config.Squire.ProtectFutureLevelingGearOptIn = value);
        DrawCheckbox(context, "Protect blue and purple gear",
            "Default on. Disable this only for deeper cleanup; character-specific cleanup exclusions remain protected.",
            () => config.Squire.ProtectBlueAndPurpleGear, value => config.Squire.ProtectBlueAndPurpleGear = value);
        DrawCheckbox(context, "Allow materia retrieval with loss risk",
            "Default off. Retrieval can fail and destroy materia; Squire protects melded gear unless this risk is explicitly accepted.",
            () => config.Squire.AllowRiskyMateriaRetrieval, value => config.Squire.AllowRiskyMateriaRetrieval = value);
    }

    private void DrawExclusions(SettingsPageContext context)
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, "Log into a character to review that character's cleanup exclusions.");
            return;
        }

        var key = playerState.ContentId.ToString();
        if (!config.Squire.ExcludedItemIdsByCharacter.TryGetValue(key, out var exclusions) || exclusions.Count == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{playerState.CharacterName} has no Squire cleanup exclusions.");
            return;
        }

        ImGui.TextWrapped($"These item types are always protected from Squire cleanup for {playerState.CharacterName}, even when blue and purple protection is disabled.");
        ImGui.Spacing();
        foreach (var itemId in exclusions.Distinct().OrderBy(ResolveItemName, StringComparer.OrdinalIgnoreCase).ThenBy(id => id).ToArray())
        {
            ImGui.PushID((int)itemId);
            ImGui.TextUnformatted($"{ResolveItemName(itemId)} ({itemId})");
            ImGui.SameLine();
            if (pendingExclusionRemoval == itemId)
            {
                if (ImGui.Button("Confirm removal"))
                {
                    exclusions.RemoveAll(id => id == itemId);
                    pendingExclusionRemoval = null;
                    config.Save();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) pendingExclusionRemoval = null;
            }
            else if (ImGui.Button("Allow cleanup evaluation"))
            {
                pendingExclusionRemoval = itemId;
            }
            ImGui.PopID();
        }
    }

    private void DrawRecovery(SettingsPageContext context)
    {
        DrawCheckbox(context, "Return after being knocked out",
            "Default on. Squire confirms the normal Return prompt, waits for the new area to settle, and then revalidates the batch before continuing.",
            () => config.Squire.RecoverFromKnockout, value => config.Squire.RecoverFromKnockout = value);
        DrawCheckbox(context, "Wait for combat to end",
            "Default on. Squire waits without attempting to control combat; when disabled, combat stops the run immediately.",
            () => config.Squire.WaitForCombatToEnd, value => config.Squire.WaitForCombatToEnd = value);

        var timeout = Math.Clamp(config.Squire.CombatRecoveryTimeoutSeconds, 10, 600);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
        if (ImGui.InputInt("Combat wait timeout (seconds)", ref timeout, 10, 30))
        {
            config.Squire.CombatRecoveryTimeoutSeconds = Math.Clamp(timeout, 10, 600);
            config.Save();
        }
        ImGui.TextWrapped("Squire stops explicitly when combat outlasts this limit; it never attacks, flees, or changes jobs to recover.");
        ImGui.Spacing();

        DrawCheckbox(context, "Leave a duty to execute cleanup",
            "Default off. This is an extreme recovery policy. When disabled, any active duty blocks cleanup without changing the duty state.",
            () => config.Squire.LeaveDutyToExecute, value => config.Squire.LeaveDutyToExecute = value);
        DrawCheckbox(context, "Pause GatherBuddy Reborn",
            "Default on. Squire requests a cooperative pause and later releases only its own request; it never disables AutoGather or clears its plan. When disabled, active AutoGather blocks cleanup.",
            () => config.Squire.PauseGatherBuddyReborn, value => config.Squire.PauseGatherBuddyReborn = value);
        DrawCheckbox(context, "Pause Questionable",
            "Default on. Squire waits for Questionable to reach a safe task boundary and later releases only its own pause request. When disabled, active quest execution blocks cleanup.",
            () => config.Squire.PauseQuestionable, value => config.Squire.PauseQuestionable = value);
        DrawCheckbox(context, "Pause Artisan processing",
            "Default on. Squire uses Artisan's stop-request contract and resumes it only when Squire created the request. When disabled, active processing blocks cleanup.",
            () => config.Squire.PauseArtisan, value => config.Squire.PauseArtisan = value);
        DrawCheckbox(context, "Close compatible user menus",
            "Default on. Squire closes known ordinary menus through their normal UI callbacks, but never accepts an unrelated confirmation or dismisses an unknown modal.",
            () => config.Squire.CloseSafeUserMenus, value => config.Squire.CloseSafeUserMenus = value);
    }

    private void DrawDuplicateRetention(SettingsPageContext context)
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, "Log into a character to review that character's duplicate retention rules.");
            return;
        }

        var key = playerState.ContentId.ToString();
        if (!config.Squire.DuplicateRetentionByCharacter.TryGetValue(key, out var rules) || rules.Count == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{playerState.CharacterName} has no explicit duplicate retention rules.");
            ImGui.TextWrapped("Set a minimum from an item's Squire detail panel when copies are being held for later retainer hand-me-downs.");
            return;
        }

        ImGui.TextWrapped("Each rule keeps a minimum number of copies on this character for one item ID and quality. Saved gearsets and all other Squire protections remain independent.");
        foreach (var rule in rules
                     .Where(value => value.ItemId != 0 && value.MinimumCopies > 0)
                     .OrderBy(value => ResolveItemName(value.ItemId), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.IsHighQuality)
                     .ToArray())
        {
            ImGui.PushID($"{rule.ItemId}:{rule.IsHighQuality}");
            ImGui.TextUnformatted($"{ResolveItemName(rule.ItemId)} ({(rule.IsHighQuality ? "HQ" : "normal quality")})");
            ImGui.SameLine();
            var minimum = rule.MinimumCopies;
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 6);
            if (ImGui.InputInt("Minimum", ref minimum))
            {
                minimum = Math.Clamp(minimum, 0, 99);
                if (minimum == 0)
                    rules.Remove(rule);
                else
                    rule.MinimumCopies = minimum;
                config.Save();
            }
            ImGui.SameLine();
            if (ImGui.Button("Remove"))
            {
                rules.Remove(rule);
                config.Save();
            }
            ImGui.PopID();
        }
    }

    private string ResolveItemName(uint itemId)
    {
        var name = dataManager.GetExcelSheet<LuminaItem>()?.GetRowOrDefault(itemId)?.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? $"Item {itemId}" : name;
    }

    private void DrawCheckbox(SettingsPageContext context, string label, string description, Func<bool> getter, Action<bool> setter) =>
        SettingsPageUi.DrawConfigCheckbox(config, context, label, description, getter, setter);
}
