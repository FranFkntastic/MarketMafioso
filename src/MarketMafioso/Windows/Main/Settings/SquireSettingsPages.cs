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

    private string ResolveItemName(uint itemId)
    {
        var name = dataManager.GetExcelSheet<LuminaItem>()?.GetRowOrDefault(itemId)?.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? $"Item {itemId}" : name;
    }

    private void DrawCheckbox(SettingsPageContext context, string label, string description, Func<bool> getter, Action<bool> setter) =>
        SettingsPageUi.DrawConfigCheckbox(config, context, label, description, getter, setter);
}
