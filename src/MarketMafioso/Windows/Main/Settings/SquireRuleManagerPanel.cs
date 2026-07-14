using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.UI.Settings;
using MarketMafioso.Squire;
using Newtonsoft.Json;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class SquireRuleManagerPanel
{
    private readonly IPlayerState playerState;
    private readonly IDataManager dataManager;
    private readonly Func<SquireAnalysis?> currentAnalysis;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly SquireCleanupRuleStore store;
    private string filter = string.Empty;
    private string? selectedRuleId;
    private SquireCleanupRule? draft;
    private string itemIdsText = string.Empty;
    private string? pendingDeleteId;
    private string status = string.Empty;

    public SquireRuleManagerPanel(
        Configuration config,
        IPlayerState playerState,
        IDataManager dataManager,
        Func<SquireAnalysis?> currentAnalysis,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.playerState = playerState;
        this.dataManager = dataManager;
        this.currentAnalysis = currentAnalysis;
        this.reviewRegistry = reviewRegistry;
        store = new SquireCleanupRuleStore(config);
    }

    public void Draw(SettingsPageContext context)
    {
        var contentId = playerState.IsLoaded && playerState.ContentId != 0 ? playerState.ContentId : (ulong?)null;
        ImGui.TextWrapped("Cleanup rules replace Squire's scattered policy toggles. Every populated condition must match; multiple values inside one condition are alternatives. Higher priority decides protection and route, equal-priority protection wins conservatively, conflicting routes fail evaluation, and retention minima and authorizations accumulate across matching rules.");
        ImGui.PushStyleColor(ImGuiCol.Text, MarketMafiosoUiTheme.Muted);
        ImGui.TextWrapped("Hard safeguards such as incomplete observations, currently equipped items, saved-gearset multiplicity, soul crystals, and failed equipment analysis are visible here but cannot be overridden.");
        ImGui.PopStyleColor();
        ImGui.Spacing();

        if (ImGui.Button("New global rule"))
            BeginNew(SquireCleanupRuleScope.Global, null);
        RegisterLast("squire.rules.new-global", "Create a global cleanup rule", AgentBridgeUiControlKind.Button, true, false, null,
            () => BeginNew(SquireCleanupRuleScope.Global, null));
        ImGui.SameLine();
        var canCreateCharacter = contentId is not null;
        if (!canCreateCharacter)
            ImGui.BeginDisabled();
        if (ImGui.Button("New character rule"))
            BeginNew(SquireCleanupRuleScope.Character, contentId);
        RegisterLast("squire.rules.new-character", "Create a cleanup rule for the current character", AgentBridgeUiControlKind.Button, canCreateCharacter, false, contentId?.ToString(),
            () => BeginNew(SquireCleanupRuleScope.Character, contentId));
        if (!canCreateCharacter)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Copy policy JSON"))
        {
            ImGui.SetClipboardText(JsonConvert.SerializeObject(store.GetAll(), Formatting.Indented));
            status = "Copied the complete resolved rule set to the clipboard.";
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            ImGui.SameLine();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, status);
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##SquireCleanupRuleFilter", "Filter name, scope, condition, effect, note, or ID", ref filter, 200);
        var rules = store.GetApplicable(contentId)
            .Where(MatchesFilter)
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.Origin)
            .ThenBy(rule => rule.Name)
            .ToArray();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted,
            $"{rules.Count(rule => rule.Origin == SquireCleanupRuleOrigin.BuiltIn)} built-in, " +
            $"{rules.Count(rule => rule is { Origin: SquireCleanupRuleOrigin.User, Scope: SquireCleanupRuleScope.Global })} global, " +
            $"{rules.Count(rule => rule is { Origin: SquireCleanupRuleOrigin.User, Scope: SquireCleanupRuleScope.Character })} current-character rule(s) shown.");
        DrawRuleTable(rules);
        DrawSelectedEditor(contentId);
    }

    private void DrawRuleTable(IReadOnlyList<SquireCleanupRule> rules)
    {
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable | ImGuiTableFlags.ScrollX |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("##SquireCleanupRulesV1", 8, flags, new(0, 270)))
            return;
        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 42);
        ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupColumn("Origin", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Scope", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthFixed, 210);
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 300);
        ImGui.TableSetupColumn("Then", ImGuiTableColumnFlags.WidthFixed, 260);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 180);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var rule in rules)
        {
            ImGui.PushID(rule.Id);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var enabled = rule.Enabled;
            if (ImGui.Checkbox("##Enabled", ref enabled))
                SetEnabled(rule, enabled);
            RegisterLast($"squire.rule.{ControlId(rule.Id)}.enabled", $"{(rule.Enabled ? "Disable" : "Enable")} cleanup rule {rule.Name}", AgentBridgeUiControlKind.Toggle, true, rule.Enabled, rule.Priority.ToString(), () => SetEnabled(rule, !rule.Enabled));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(rule.Priority.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(rule.Origin == SquireCleanupRuleOrigin.BuiltIn ? "Built-in" : "User");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatScope(rule));
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{rule.Name}##Select", selectedRuleId == rule.Id))
                Select(rule);
            RegisterLast($"squire.rule.{ControlId(rule.Id)}.select", $"Edit cleanup rule {rule.Name}", AgentBridgeUiControlKind.Select, true, selectedRuleId == rule.Id, rule.Id, () => Select(rule));
            ImGui.TableNextColumn();
            ImGui.TextWrapped(FormatCondition(rule.Condition));
            ImGui.TableNextColumn();
            ImGui.TextWrapped(FormatEffect(rule.Effect));
            ImGui.TableNextColumn();
            var errors = rule.Validate();
            ImGui.TextColored(errors.Count == 0 ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Warning,
                errors.Count == 0 ? "Valid" : string.Join(" ", errors));
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawSelectedEditor(ulong? contentId)
    {
        if (draft is null)
            return;
        ImGui.Separator();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, draft.Origin == SquireCleanupRuleOrigin.BuiltIn ? "Built-in rule" : "Rule editor");
        if (draft.Origin == SquireCleanupRuleOrigin.BuiltIn)
        {
            DrawBuiltInEditor(draft, contentId);
            return;
        }

        var name = draft.Name;
        ImGui.SetNextItemWidth(420);
        if (ImGui.InputText("Name", ref name, 160))
            draft = draft with { Name = name };
        var enabled = draft.Enabled;
        if (ImGui.Checkbox("Enabled##RuleEditor", ref enabled))
            draft = draft with { Enabled = enabled };
        ImGui.SameLine();
        var priority = draft.Priority;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Priority", ref priority, 10, 100))
            draft = draft with { Priority = Math.Clamp(priority, 0, 10_000) };
        ImGui.SameLine();
        DrawScopeEditor(contentId);

        ImGui.TextColored(MarketMafiosoUiTheme.Header, "When all populated conditions match");
        DrawConditionEditor();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Apply these effects");
        DrawEffectEditor();

        var note = draft.Note;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Note", ref note, 300))
            draft = draft with { Note = note };

        var errors = draft.Validate();
        if (errors.Count > 0)
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, string.Join(" ", errors));
        DrawMatchPreview(draft);

        var canSave = errors.Count == 0;
        if (!canSave)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save rule"))
            SaveDraft();
        RegisterLast("squire.rules.save", "Save the edited cleanup rule", AgentBridgeUiControlKind.Button, canSave, false, draft.Id, SaveDraft);
        if (!canSave)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel edit"))
        {
            ClearSelection();
            return;
        }
        RegisterLast("squire.rules.cancel", "Cancel cleanup rule editing", AgentBridgeUiControlKind.Button, true, false, draft.Id, ClearSelection);
        ImGui.SameLine();
        if (ImGui.Button("Duplicate rule"))
        {
            Select(draft with
            {
                Id = $"user.{Guid.NewGuid():N}",
                Name = $"Copy of {draft.Name}",
            });
            return;
        }
        RegisterLast("squire.rules.duplicate", "Duplicate the edited cleanup rule", AgentBridgeUiControlKind.Button, true, false, draft.Id, () => Select(draft with
        {
            Id = $"user.{Guid.NewGuid():N}",
            Name = $"Copy of {draft.Name}",
        }));
        if (store.GetAll().Any(rule => rule.Origin == SquireCleanupRuleOrigin.User && rule.Id == draft.Id))
        {
            ImGui.SameLine();
            DrawDelete(draft);
        }
    }

    private void DrawBuiltInEditor(SquireCleanupRule rule, ulong? contentId)
    {
        ImGui.TextWrapped($"{rule.Name}: when {FormatCondition(rule.Condition)}, {FormatEffect(rule.Effect)}.");
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Built-in definitions stay upgradeable. You may change their enabled state or priority, or copy one into a fully editable user rule.");
        var enabled = rule.Enabled;
        if (ImGui.Checkbox("Enabled##BuiltIn", ref enabled))
        {
            store.SetBuiltInOverride(rule.Id, enabled: enabled);
            Select(store.GetApplicable(contentId).Single(value => value.Id == rule.Id));
        }
        var priority = rule.Priority;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Priority##BuiltIn", ref priority, 10, 100))
        {
            store.SetBuiltInOverride(rule.Id, priority: priority);
            Select(store.GetApplicable(contentId).Single(value => value.Id == rule.Id));
        }
        if (ImGui.Button("Copy as global user rule"))
            Select(store.CopyBuiltIn(rule.Id));
        if (contentId is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Copy for this character"))
                Select(store.CopyBuiltIn(rule.Id, contentId));
        }
        DrawMatchPreview(rule);
    }

    private void DrawConditionEditor()
    {
        var condition = draft!.Condition;
        ImGui.SetNextItemWidth(420);
        if (ImGui.InputTextWithHint("Item IDs", "Blank means any; separate IDs with commas", ref itemIdsText, 400))
            condition = condition with { ItemIds = ParseItemIds(itemIdsText) };
        condition = condition with { Quality = EnumCombo("Quality", condition.Quality) };
        condition = condition with { IsEquipment = TriStateCombo("Equipment", condition.IsEquipment) };
        condition = condition with { IsPlayerSigned = TriStateCombo("Player signed", condition.IsPlayerSigned) };
        condition = condition with { IsArmoireEligible = TriStateCombo("Armoire eligible", condition.IsArmoireEligible) };
        condition = condition with { HasMateria = TriStateCombo("Has materia", condition.HasMateria) };
        condition = condition with { HasFutureLevelingUse = TriStateCombo("Has future-leveling use", condition.HasFutureLevelingUse) };

        var minEnabled = condition.MinimumEquipLevel is not null;
        if (ImGui.Checkbox("Minimum equip level", ref minEnabled))
            condition = condition with { MinimumEquipLevel = minEnabled ? 1 : null };
        if (minEnabled)
        {
            ImGui.SameLine();
            var value = condition.MinimumEquipLevel ?? 1;
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("##MinimumEquipLevel", ref value))
                condition = condition with { MinimumEquipLevel = Math.Max(0, value) };
        }
        var maxEnabled = condition.MaximumEquipLevel is not null;
        if (ImGui.Checkbox("Maximum equip level", ref maxEnabled))
            condition = condition with { MaximumEquipLevel = maxEnabled ? 100 : null };
        if (maxEnabled)
        {
            ImGui.SameLine();
            var value = condition.MaximumEquipLevel ?? 100;
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("##MaximumEquipLevel", ref value))
                condition = condition with { MaximumEquipLevel = Math.Max(0, value) };
        }

        condition = condition with { Rarities = DrawEnumSet("Rarities", condition.Rarities, Enum.GetValues<EquipmentRarity>().Where(value => value != EquipmentRarity.Unknown)) };
        condition = condition with { UseStatuses = DrawEnumSet("Equipment-use results", condition.UseStatuses, Enum.GetValues<EquipmentUseStatus>()) };
        condition = condition with { SupportedDispositions = DrawEnumSet("Supported routes", condition.SupportedDispositions,
            Enum.GetValues<SquireDisposition>().Where(value => value is not (SquireDisposition.Keep or SquireDisposition.Unsupported))) };
        draft = draft with { Condition = condition };
    }

    private void DrawEffectEditor()
    {
        var effect = draft!.Effect;
        effect = effect with { Decision = EnumCombo("Decision", effect.Decision) };
        var routes = new SquireDisposition?[] { null, SquireDisposition.ExpertDelivery, SquireDisposition.Desynthesize, SquireDisposition.VendorSell, SquireDisposition.Discard };
        if (ImGui.BeginCombo("Preferred route", effect.PreferredDisposition?.ToString() ?? "No change"))
        {
            foreach (var route in routes)
            {
                if (ImGui.Selectable(route?.ToString() ?? "No change", effect.PreferredDisposition == route))
                    effect = effect with { PreferredDisposition = route };
            }
            ImGui.EndCombo();
        }
        var minimum = effect.MinimumCopies;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Minimum retained copies", ref minimum))
            effect = effect with { MinimumCopies = Math.Clamp(minimum, 0, 99) };

        var authorizations = effect.Authorizations;
        foreach (var value in Enum.GetValues<SquireCleanupAuthorization>().Where(value => value != SquireCleanupAuthorization.None))
        {
            var selected = authorizations.HasFlag(value);
            if (ImGui.Checkbox($"Authorize {FormatEnum(value)}", ref selected))
                authorizations = selected ? authorizations | value : authorizations & ~value;
        }
        draft = draft with { Effect = effect with { Authorizations = authorizations } };
    }

    private void DrawScopeEditor(ulong? contentId)
    {
        var scope = draft!.Scope;
        if (!ImGui.BeginCombo("Scope", scope == SquireCleanupRuleScope.Global ? "All characters" : "Current character"))
            return;
        if (ImGui.Selectable("All characters", scope == SquireCleanupRuleScope.Global))
            draft = draft with { Scope = SquireCleanupRuleScope.Global, CharacterContentId = null };
        if (contentId is not null && ImGui.Selectable("Current character", scope == SquireCleanupRuleScope.Character))
            draft = draft with { Scope = SquireCleanupRuleScope.Character, CharacterContentId = contentId };
        ImGui.EndCombo();
    }

    private void DrawMatchPreview(SquireCleanupRule rule)
    {
        var analysis = currentAnalysis();
        if (analysis is null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Match preview is unavailable until Squire has a current analysis.");
            return;
        }
        var previewRule = rule with { Enabled = true };
        var matches = analysis.Candidates.Where(candidate =>
            candidate.UseAnalysis is not null && previewRule.Matches(SquireCandidateEvaluator.CreateRuleContext(
                analysis.Policy.CharacterContentId,
                candidate.Instance,
                candidate.Definition,
                candidate.UseAnalysis,
                candidate.SupportedDispositions))).ToArray();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, $"Live match preview: {matches.Length} of {analysis.Candidates.Count} observed items");
        if (matches.Length == 0)
            return;
        if (!ImGui.BeginTable("##SquireRulePreview", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp, new(0, Math.Min(180, 26 + matches.Length * 22))))
            return;
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Location");
        ImGui.TableSetupColumn("Current assessment");
        ImGui.TableSetupColumn("Current route");
        ImGui.TableHeadersRow();
        foreach (var candidate in matches.Take(30))
        {
            ImGui.TableNextRow();
            Cell(candidate.Definition.Name);
            Cell(Windows.Squire.SquirePresentation.FormatLocation(candidate.Instance.Fingerprint));
            Cell(Windows.Squire.SquirePresentation.FormatAssessment(candidate.Assessment));
            Cell(Windows.Squire.SquirePresentation.FormatDisposition(candidate.RecommendedDisposition));
        }
        ImGui.EndTable();
    }

    private void DrawDelete(SquireCleanupRule rule)
    {
        if (pendingDeleteId != rule.Id)
        {
            if (ImGui.Button("Delete rule"))
                pendingDeleteId = rule.Id;
            return;
        }
        if (ImGui.Button("Confirm delete"))
        {
            store.Remove(rule.Id);
            ClearSelection();
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep rule"))
            pendingDeleteId = null;
    }

    private void BeginNew(SquireCleanupRuleScope scope, ulong? contentId)
    {
        Select(new SquireCleanupRule(
            $"user.{Guid.NewGuid():N}",
            "New protection rule",
            SquireCleanupRuleOrigin.User,
            scope,
            scope == SquireCleanupRuleScope.Character ? contentId : null,
            true,
            700,
            new(IsEquipment: true),
            new(Decision: SquireCleanupDecision.Protect)));
    }

    private void Select(SquireCleanupRule rule)
    {
        selectedRuleId = rule.Id;
        draft = rule;
        itemIdsText = rule.Condition.ItemIds is null ? string.Empty : string.Join(", ", rule.Condition.ItemIds.Order());
        pendingDeleteId = null;
        status = string.Empty;
    }

    private void ClearSelection()
    {
        selectedRuleId = null;
        draft = null;
        itemIdsText = string.Empty;
        pendingDeleteId = null;
    }

    private void SaveDraft()
    {
        if (draft is null || draft.Validate().Count > 0)
            return;
        if (!store.Replace(draft))
            store.Add(draft);
        var savedName = draft.Name;
        Select(store.GetAll().Single(rule => rule.Id == draft.Id));
        status = $"Saved {savedName}.";
    }

    private void SetEnabled(SquireCleanupRule rule, bool enabled)
    {
        if (rule.Origin == SquireCleanupRuleOrigin.BuiltIn)
            store.SetBuiltInOverride(rule.Id, enabled: enabled);
        else
            store.Replace(rule with { Enabled = enabled });
        if (selectedRuleId == rule.Id)
            Select(store.GetAll().Single(value => value.Id == rule.Id));
    }

    private bool MatchesFilter(SquireCleanupRule rule)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        var value = filter.Trim();
        return rule.Id.Contains(value, StringComparison.OrdinalIgnoreCase) ||
               rule.Name.Contains(value, StringComparison.OrdinalIgnoreCase) ||
               rule.Note.Contains(value, StringComparison.OrdinalIgnoreCase) ||
               FormatScope(rule).Contains(value, StringComparison.OrdinalIgnoreCase) ||
               FormatCondition(rule.Condition).Contains(value, StringComparison.OrdinalIgnoreCase) ||
               FormatEffect(rule.Effect).Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatScope(SquireCleanupRule rule) => rule.Scope == SquireCleanupRuleScope.Global
        ? "All characters"
        : $"Character {rule.CharacterContentId}";

    private string FormatCondition(SquireCleanupRuleCondition condition)
    {
        var values = new List<string>();
        if (condition.ItemIds is { Count: > 0 })
            values.Add(string.Join(" or ", condition.ItemIds.Select(id => $"{ResolveItemName(id)} ({id})")));
        if (condition.Quality != SquireRuleQuality.Any) values.Add(condition.Quality.ToString());
        if (condition.Rarities is { Count: > 0 }) values.Add($"rarity {string.Join("/", condition.Rarities)}");
        if (condition.UseStatuses is { Count: > 0 }) values.Add($"use {string.Join("/", condition.UseStatuses)}");
        AddTri(values, "equipment", condition.IsEquipment);
        AddTri(values, "player signed", condition.IsPlayerSigned);
        AddTri(values, "Armoire eligible", condition.IsArmoireEligible);
        AddTri(values, "has materia", condition.HasMateria);
        AddTri(values, "future-leveling use", condition.HasFutureLevelingUse);
        if (condition.MinimumEquipLevel is not null) values.Add($"equip level >= {condition.MinimumEquipLevel}");
        if (condition.MaximumEquipLevel is not null) values.Add($"equip level <= {condition.MaximumEquipLevel}");
        if (condition.SupportedDispositions is { Count: > 0 }) values.Add($"supports {string.Join("/", condition.SupportedDispositions)}");
        return values.Count == 0 ? "every observed item" : string.Join(" and ", values);
    }

    private static string FormatEffect(SquireCleanupRuleEffect effect)
    {
        var values = new List<string>();
        if (effect.Decision != SquireCleanupDecision.NoChange) values.Add(effect.Decision.ToString());
        if (effect.PreferredDisposition is not null) values.Add($"route {effect.PreferredDisposition}");
        if (effect.MinimumCopies > 0) values.Add($"retain {effect.MinimumCopies}");
        if (effect.Authorizations != SquireCleanupAuthorization.None) values.Add($"authorize {effect.Authorizations}");
        return values.Count == 0 ? "no effect" : string.Join("; ", values);
    }

    private static IReadOnlySet<uint>? ParseItemIds(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var values = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => uint.TryParse(value, out var parsed) ? parsed : 0)
            .ToHashSet();
        return values;
    }

    private static T EnumCombo<T>(string label, T value) where T : struct, Enum
    {
        if (!ImGui.BeginCombo(label, FormatEnum(value)))
            return value;
        foreach (var option in Enum.GetValues<T>())
            if (ImGui.Selectable(FormatEnum(option), EqualityComparer<T>.Default.Equals(option, value)))
                value = option;
        ImGui.EndCombo();
        return value;
    }

    private static bool? TriStateCombo(string label, bool? value)
    {
        if (!ImGui.BeginCombo(label, value is null ? "Any" : value.Value ? "Yes" : "No"))
            return value;
        if (ImGui.Selectable("Any", value is null)) value = null;
        if (ImGui.Selectable("Yes", value == true)) value = true;
        if (ImGui.Selectable("No", value == false)) value = false;
        ImGui.EndCombo();
        return value;
    }

    private static IReadOnlySet<T>? DrawEnumSet<T>(string label, IReadOnlySet<T>? current, IEnumerable<T> options) where T : struct, Enum
    {
        var values = current is null ? new HashSet<T>() : new HashSet<T>(current);
        if (ImGui.BeginCombo(label, values.Count == 0 ? "Any" : string.Join(", ", values.Select(FormatEnum))))
        {
            foreach (var option in options)
            {
                var selected = values.Contains(option);
                if (ImGui.Selectable(FormatEnum(option), selected))
                {
                    if (!values.Add(option)) values.Remove(option);
                }
            }
            ImGui.EndCombo();
        }
        return values.Count == 0 ? null : values;
    }

    private string ResolveItemName(uint itemId)
    {
        var name = dataManager.GetExcelSheet<LuminaItem>()?.GetRowOrDefault(itemId)?.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? $"Item {itemId}" : name;
    }

    private static string FormatEnum<T>(T value) where T : struct, Enum =>
        System.Text.RegularExpressions.Regex.Replace(value.ToString(), "([a-z])([A-Z])", "$1 $2");

    private static void AddTri(List<string> values, string label, bool? value)
    {
        if (value is not null) values.Add(value.Value ? label : $"not {label}");
    }

    private static void Cell(string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    private static string ControlId(string id) => new string(id.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray()).ToLowerInvariant();

    private void RegisterLast(string id, string label, AgentBridgeUiControlKind kind, bool enabled, bool selected, string? value, Action invoke) =>
        reviewRegistry.Register(id, label, kind, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), enabled, selected, value, invoke);
}
