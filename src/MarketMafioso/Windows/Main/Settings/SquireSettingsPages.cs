using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.UI.Settings;
using Franthropy.Dalamud.AgentBridge;
using MarketMafioso.Squire;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class SquireSettingsPages
{
    private readonly Configuration config;
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private readonly Func<SquireAnalysis?> currentAnalysis;
    private readonly SquireRuleStore ruleStore;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private Guid? pendingRuleRemoval;
    private string ruleFilter = string.Empty;

    public SquireSettingsPages(
        Configuration config,
        IPlayerState playerState,
        IDataManager dataManager,
        Func<SquireAnalysis?> currentAnalysis,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.playerState = playerState ?? throw new ArgumentNullException(nameof(playerState));
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        this.currentAnalysis = currentAnalysis ?? throw new ArgumentNullException(nameof(currentAnalysis));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        ruleStore = new SquireRuleStore(config);
        Descriptors =
        [
            new("squire.policy", "Squire / Cleanup Policy", DrawPolicy, 20,
                searchTerms: ["player-signed", "future job leveling", "blue purple rarity", "materia retrieval risk"]),
            new("squire.rules", "Squire / Rules", DrawRules, 21,
                searchTerms: ["blacklist", "excluded items", "character protection", "duplicates", "copies", "minimum", "retainers", "hand me down", "reserve"]),
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
            "Default on. Disable this only for deeper cleanup; character-specific item protection rules remain in force.",
            () => config.Squire.ProtectBlueAndPurpleGear, value => config.Squire.ProtectBlueAndPurpleGear = value);
        DrawCheckbox(context, "Allow materia retrieval with loss risk",
            "Default off. Retrieval can fail and destroy materia; Squire protects melded gear unless this risk is explicitly accepted.",
            () => config.Squire.AllowRiskyMateriaRetrieval, value => config.Squire.AllowRiskyMateriaRetrieval = value);
    }

    private void DrawRules(SettingsPageContext context)
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, "Log into a character to review that character's Squire rules.");
            return;
        }

        var contentId = playerState.ContentId;
        var allRules = ruleStore.Get(contentId);
        ImGui.TextWrapped($"Rules express explicit cleanup intent for {playerState.CharacterName}. Protection rules keep every copy of an item; retention rules keep a minimum for one quality. Built-in safety evaluation and saved-gearset protection remain independent.");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##SquireRuleFilter", "Filter item, behavior, quality, note, or rule ID", ref ruleFilter, 160);
        ImGui.Spacing();

        var rows = allRules.Where(MatchesRuleFilter).ToArray();
        if (allRules.Count == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{playerState.CharacterName} has no explicit Squire rules. Create one from an item's detail panel in Squire.");
            return;
        }
        if (rows.Length == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "No rules match the current filter.");
            return;
        }

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit |
                    ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable | ImGuiTableFlags.Sortable |
                    ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##SquireRuleManagerV2", 8, flags, new(0, Math.Max(260f, ImGui.GetContentRegionAvail().Y))))
            return;
        ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, 190);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 75);
        ImGui.TableSetupColumn("Behavior", ImGuiTableColumnFlags.WidthFixed, 175);
        ImGui.TableSetupColumn("Owned", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("Effective protection", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthFixed, 220);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 125);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        rows = SortRules(rows, ImGui.TableGetSortSpecs());
        foreach (var rule in rows)
        {
            ImGui.PushID(rule.Id.ToString("N"));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var owned = CountOwned(rule);
            var enabled = rule.Enabled;
            if (ImGui.Checkbox("##Enabled", ref enabled))
                ruleStore.Update(contentId, rule.Id, enabled: enabled);
            RegisterLastControl(
                $"squire.rule.{rule.Id:N}.enabled",
                $"{(rule.Enabled ? "Disable" : "Enable")} {ResolveItemName(rule.ItemId)} rule",
                AgentBridgeUiControlKind.Toggle,
                true,
                rule.Enabled,
                $"{rule.Kind}; owned={(owned?.ToString() ?? "unavailable")}; {FormatEffectiveProtection(rule, owned)}",
                () => ruleStore.Update(contentId, rule.Id, enabled: !rule.Enabled));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{ResolveItemName(rule.ItemId)} ({rule.ItemId})");
            if (!rule.IsValid(out var validationError))
            {
                ImGui.TextColored(MarketMafiosoUiTheme.Warning, validationError);
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatQuality(rule.Quality));
            ImGui.TableNextColumn();
            if (rule.Kind == SquireRuleKind.ProtectItem)
            {
                ImGui.TextUnformatted("Protect every copy");
            }
            else
            {
                ImGui.TextUnformatted("Keep at least");
                ImGui.SameLine();
                var minimum = rule.MinimumCopies;
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 4);
                if (ImGui.InputInt("##Minimum", ref minimum, 1, 5))
                    ruleStore.Update(contentId, rule.Id, minimumCopies: minimum);
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(owned?.ToString() ?? "—");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatEffectiveProtection(rule, owned));
            ImGui.TableNextColumn();
            var note = rule.Note;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##Note", ref note, 256))
                ruleStore.Update(contentId, rule.Id, note: note);
            ImGui.TableNextColumn();
            if (pendingRuleRemoval == rule.Id)
            {
                var confirmRemoval = ImGui.Button("Confirm");
                RegisterLastControl(
                    $"squire.rule.{rule.Id:N}.delete-confirm",
                    $"Confirm deletion of {ResolveItemName(rule.ItemId)} rule",
                    AgentBridgeUiControlKind.Button,
                    true,
                    false,
                    rule.Kind.ToString(),
                    () =>
                    {
                        ruleStore.Remove(contentId, rule.Id);
                        pendingRuleRemoval = null;
                    });
                if (confirmRemoval)
                {
                    ruleStore.Remove(contentId, rule.Id);
                    pendingRuleRemoval = null;
                }
                ImGui.SameLine();
                var cancelRemoval = ImGui.Button("Cancel");
                RegisterLastControl(
                    $"squire.rule.{rule.Id:N}.delete-cancel",
                    $"Cancel deletion of {ResolveItemName(rule.ItemId)} rule",
                    AgentBridgeUiControlKind.Button,
                    true,
                    false,
                    rule.Kind.ToString(),
                    () => pendingRuleRemoval = null);
                if (cancelRemoval)
                    pendingRuleRemoval = null;
            }
            else
            {
                var requestRemoval = ImGui.Button("Delete rule");
                RegisterLastControl(
                    $"squire.rule.{rule.Id:N}.delete",
                    $"Request deletion of {ResolveItemName(rule.ItemId)} rule",
                    AgentBridgeUiControlKind.Button,
                    true,
                    false,
                    rule.Kind.ToString(),
                    () => pendingRuleRemoval = rule.Id);
                if (requestRemoval)
                    pendingRuleRemoval = rule.Id;
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
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

    private bool MatchesRuleFilter(SquireRule rule)
    {
        if (string.IsNullOrWhiteSpace(ruleFilter))
            return true;
        var filter = ruleFilter.Trim();
        return ResolveItemName(rule.ItemId).Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               rule.ItemId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               rule.Id.ToString("N").Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               rule.Note.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               rule.Kind.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               FormatQuality(rule.Quality).Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private int? CountOwned(SquireRule rule)
    {
        var analysis = currentAnalysis();
        if (analysis?.Snapshot.Identity.Scope?.LocalContentId != playerState.ContentId)
            return null;
        return analysis.Snapshot.Instances.Count(instance =>
            instance.Fingerprint.ItemId == rule.ItemId &&
            (rule.Quality == SquireRuleQuality.Any ||
             instance.Fingerprint.IsHighQuality == (rule.Quality == SquireRuleQuality.HighQuality)));
    }

    private string FormatEffectiveProtection(SquireRule rule, int? owned)
    {
        if (!rule.Enabled)
            return "Disabled";
        if (rule.Kind == SquireRuleKind.ProtectItem)
            return "Every owned copy";
        var effective = currentAnalysis()?.Candidates
            .Where(candidate => candidate.Definition.ItemId == rule.ItemId &&
                                candidate.Instance.Fingerprint.IsHighQuality == (rule.Quality == SquireRuleQuality.HighQuality))
            .Select(candidate => candidate.DuplicateStatus?.EffectiveMinimumCopies ?? rule.MinimumCopies)
            .DefaultIfEmpty(rule.MinimumCopies)
            .Max() ?? rule.MinimumCopies;
        return owned is null ? $"Keep {effective}" : $"Keep {effective}; {Math.Max(0, owned.Value - effective)} removable";
    }

    private SquireRule[] SortRules(SquireRule[] rows, ImGuiTableSortSpecsPtr specs)
    {
        if (specs.SpecsCount == 0)
            return rows;
        var spec = specs.Specs;
        return spec.ColumnIndex switch
        {
            0 => SortBy(rows, rule => rule.Enabled, spec.SortDirection),
            1 => SortBy(rows, rule => ResolveItemName(rule.ItemId), spec.SortDirection),
            2 => SortBy(rows, rule => rule.Quality, spec.SortDirection),
            3 => SortBy(rows, rule => rule.Kind, spec.SortDirection),
            4 => SortBy(rows, rule => CountOwned(rule) ?? -1, spec.SortDirection),
            5 => SortBy(rows, rule => rule.Kind == SquireRuleKind.ProtectItem ? int.MaxValue : rule.MinimumCopies, spec.SortDirection),
            6 => SortBy(rows, rule => rule.Note, spec.SortDirection),
            _ => rows,
        };
    }

    private static SquireRule[] SortBy<TKey>(SquireRule[] rows, Func<SquireRule, TKey> selector, ImGuiSortDirection direction) =>
        (direction == ImGuiSortDirection.Descending ? rows.OrderByDescending(selector) : rows.OrderBy(selector))
        .ThenBy(rule => rule.ItemId)
        .ThenBy(rule => rule.Id)
        .ToArray();

    private static string FormatQuality(SquireRuleQuality quality) => quality switch
    {
        SquireRuleQuality.Any => "Any",
        SquireRuleQuality.NormalQuality => "Normal",
        SquireRuleQuality.HighQuality => "HQ",
        _ => $"Unknown ({(int)quality})",
    };

    private string ResolveItemName(uint itemId)
    {
        var name = dataManager.GetExcelSheet<LuminaItem>()?.GetRowOrDefault(itemId)?.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? $"Item {itemId}" : name;
    }

    private void DrawCheckbox(SettingsPageContext context, string label, string description, Func<bool> getter, Action<bool> setter) =>
        SettingsPageUi.DrawConfigCheckbox(config, context, label, description, getter, setter);

    private void RegisterLastControl(
        string id,
        string label,
        AgentBridgeUiControlKind kind,
        bool enabled,
        bool selected,
        string? value,
        Action invoke) =>
        reviewRegistry.Register(id, label, kind, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), enabled, selected, value, invoke);
}
