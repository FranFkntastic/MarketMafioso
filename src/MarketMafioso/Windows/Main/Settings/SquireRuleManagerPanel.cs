using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.UI.Items;
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
    private readonly Action requestAnalysisRefresh;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly SquireCleanupRuleStore store;
    private string filter = string.Empty;
    private string? selectedRuleId;
    private SquireCleanupRule? draft;
    private SquireCleanupRule? original;
    private readonly IReadOnlyList<DalamudItemOption> itemOptions;
    private readonly DalamudItemAutocompleteState itemSearch = new();
    private string? pendingDeleteId;
    private bool confirmDiscard;
    private bool scrollToImpact;
    private bool scrollToTop;
    private string status = string.Empty;

    public SquireRuleManagerPanel(
        Configuration config,
        IPlayerState playerState,
        IDataManager dataManager,
        Func<SquireAnalysis?> currentAnalysis,
        Action requestAnalysisRefresh,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.playerState = playerState;
        this.dataManager = dataManager;
        this.currentAnalysis = currentAnalysis;
        this.requestAnalysisRefresh = requestAnalysisRefresh;
        this.reviewRegistry = reviewRegistry;
        itemOptions = DalamudItemAutocompleteRenderer.LoadItemOptions(dataManager);
        store = new SquireCleanupRuleStore(config);
    }

    public void Draw(SettingsPageContext context)
    {
        var contentId = playerState.IsLoaded && playerState.ContentId != 0 ? playerState.ContentId : (ulong?)null;
        if (currentAnalysis() is null)
            requestAnalysisRefresh();
        if (draft is not null)
        {
            DrawEditor(contentId);
            return;
        }

        DrawRuleList(contentId);
    }

    private void DrawRuleList(ulong? contentId)
    {
        ImGui.TextWrapped("Rules decide which observed equipment Squire protects, authorizes, retains, and sends to each cleanup route.");
        if (ImGui.CollapsingHeader("How cleanup rules work"))
        {
            ImGui.TextWrapped("Every populated condition must match, while multiple values inside one condition are alternatives. Higher priority decides protection and route; equal-priority protection wins conservatively, conflicting routes fail evaluation, and retention minima and authorizations accumulate.");
            DrawWrappedColored(MarketMafiosoUiTheme.Muted, "Hard safeguards for incomplete observations, equipped items, required gearset copies, soul crystals, and failed equipment analysis cannot be overridden.");
        }
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
    }

    private void DrawRuleTable(IReadOnlyList<SquireCleanupRule> rules)
    {
        var compact = ImGui.GetContentRegionAvail().X < 820;
        var columnCount = compact ? 4 : 6;
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingStretchProp;
        var tableHeight = Math.Max(220, ImGui.GetContentRegionAvail().Y);
        if (!ImGui.BeginTable("##SquireCleanupRulesV2", columnCount, flags, new(0, tableHeight)))
            return;
        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 42);
        if (!compact)
            ImGui.TableSetupColumn("Priority", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthStretch, compact ? 0.62f : 0.48f);
        if (!compact)
            ImGui.TableSetupColumn("Scope", ImGuiTableColumnFlags.WidthFixed, 125);
        ImGui.TableSetupColumn("Effect", ImGuiTableColumnFlags.WidthStretch, compact ? 0.38f : 0.52f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 72);
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
            if (!compact)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(rule.Priority.ToString());
            }
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{rule.Name}##Select", selectedRuleId == rule.Id))
                Select(rule);
            RegisterLast($"squire.rule.{ControlId(rule.Id)}.select", $"Edit cleanup rule {rule.Name}", AgentBridgeUiControlKind.Select, true, selectedRuleId == rule.Id, rule.Id, () => Select(rule));
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted($"{(rule.Origin == SquireCleanupRuleOrigin.BuiltIn ? "Built-in" : "User")} | priority {rule.Priority} | {FormatScope(rule)}");
                ImGui.TextWrapped($"When {FormatCondition(rule.Condition)}");
                if (!string.IsNullOrWhiteSpace(rule.Note))
                    ImGui.TextWrapped(rule.Note);
                ImGui.EndTooltip();
            }
            if (!compact)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatScope(rule));
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatEffect(rule.Effect));
            ImGui.TableNextColumn();
            var errors = rule.Validate();
            ImGui.TextColored(errors.Count == 0 ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Warning,
                errors.Count == 0 ? "Valid" : "Invalid");
            if (errors.Count > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join(" ", errors));
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawEditor(ulong? contentId)
    {
        var activeDraft = draft;
        if (activeDraft is null)
            return;

        DrawEditorToolbar(activeDraft);
        if (draft is null)
            return;
        if (confirmDiscard)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, "Discard your unsaved changes and return to the rule list?");
            if (ImGui.Button("Discard changes"))
            {
                ClearSelection();
                return;
            }
            RegisterLast("squire.rules.discard-changes", "Discard unsaved cleanup rule changes", AgentBridgeUiControlKind.Button, true, false, draft.Id, ClearSelection);
            ImGui.SameLine();
            if (ImGui.Button("Keep editing"))
                confirmDiscard = false;
            RegisterLast("squire.rules.keep-editing", "Keep editing the cleanup rule", AgentBridgeUiControlKind.Button, true, false, draft.Id, () => confirmDiscard = false);
        }
        ImGui.Separator();

        if (!ImGui.BeginChild("##SquireRuleEditorScroll", new(0, 0), false))
        {
            ImGui.EndChild();
            return;
        }
        if (scrollToTop)
        {
            ImGui.SetScrollY(0);
            scrollToTop = false;
        }
        if (draft.Origin == SquireCleanupRuleOrigin.BuiltIn)
        {
            DrawBuiltInEditor(draft, contentId);
            ImGui.EndChild();
            return;
        }

        DrawSectionHeading("Rule details");
        DrawFieldLabel("Name");
        var name = draft.Name;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##RuleName", ref name, 160))
            draft = draft with { Name = name };

        var wide = ImGui.GetContentRegionAvail().X >= 840;
        if (ImGui.BeginTable("##RuleMetadata", wide ? 3 : 1, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            DrawFieldLabel("State");
            var enabled = draft.Enabled;
            if (ImGui.Checkbox("Enabled##RuleEditor", ref enabled))
                draft = draft with { Enabled = enabled };

            ImGui.TableNextColumn();
            DrawFieldLabel("Priority");
            var priority = draft.Priority;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt("##RulePriority", ref priority, 10, 100))
                draft = draft with { Priority = Math.Clamp(priority, 0, 10_000) };

            ImGui.TableNextColumn();
            DrawFieldLabel("Applies to");
            DrawScopeEditor(contentId);
            ImGui.EndTable();
        }

        ImGui.Spacing();
        DrawSectionHeading("Match conditions");
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Every populated condition must match. Leave a field at Any to ignore it.");
        DrawConditionEditor();

        ImGui.Spacing();
        DrawSectionHeading("Outcome");
        DrawEffectEditor();

        ImGui.Spacing();
        DrawSectionHeading("Notes");
        DrawFieldLabel("Optional explanation");
        var note = draft.Note;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##RuleNote", "Why this rule exists", ref note, 300))
            draft = draft with { Note = note };

        var errors = draft.Validate();
        if (errors.Count > 0)
        {
            ImGui.Spacing();
            foreach (var error in errors)
                DrawWrappedColored(MarketMafiosoUiTheme.Warning, error);
        }

        ImGui.Spacing();
        DrawMatchPreview(draft);
        ImGui.EndChild();
    }

    private void DrawEditorToolbar(SquireCleanupRule activeDraft)
    {
        if (ImGui.Button("< Rule list"))
        {
            RequestCloseEditor();
            if (draft is null)
                return;
        }
        RegisterLast("squire.rules.back", "Return to the cleanup rule list", AgentBridgeUiControlKind.Button, true, false, activeDraft.Id, RequestCloseEditor);
        ImGui.SameLine();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, activeDraft.Origin == SquireCleanupRuleOrigin.BuiltIn ? "Built-in rule" : "Edit cleanup rule");
        ImGui.SameLine();
        if (ImGui.Button("Live impact"))
            scrollToImpact = true;
        RegisterLast("squire.rules.live-impact", "Jump to the cleanup rule's live impact", AgentBridgeUiControlKind.Button, true, false, activeDraft.Id, () => scrollToImpact = true);

        if (activeDraft.Origin == SquireCleanupRuleOrigin.BuiltIn)
            return;

        ImGui.SameLine();
        var errors = activeDraft.Validate();
        var canSave = errors.Count == 0;
        if (!canSave)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save"))
            SaveDraft();
        RegisterLast("squire.rules.save", "Save the edited cleanup rule", AgentBridgeUiControlKind.Button, canSave, false, activeDraft.Id, SaveDraft);
        if (!canSave)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Duplicate"))
            DuplicateDraft();
        RegisterLast("squire.rules.duplicate", "Duplicate the edited cleanup rule", AgentBridgeUiControlKind.Button, true, false, activeDraft.Id, DuplicateDraft);
        if (store.GetAll().Any(rule => rule.Origin == SquireCleanupRuleOrigin.User && rule.Id == activeDraft.Id))
        {
            ImGui.SameLine();
            DrawDelete(activeDraft);
            if (draft is null)
                return;
        }
        if (!string.IsNullOrWhiteSpace(status))
            ImGui.TextColored(MarketMafiosoUiTheme.Success, status);
    }

    private void RequestCloseEditor()
    {
        if (draft?.Origin == SquireCleanupRuleOrigin.User && draft != original)
        {
            confirmDiscard = true;
            return;
        }
        ClearSelection();
    }

    private void DuplicateDraft()
    {
        if (draft is null)
            return;
        Select(draft with
        {
            Id = $"user.{Guid.NewGuid():N}",
            Name = $"Copy of {draft.Name}",
            Origin = SquireCleanupRuleOrigin.User,
        }, isNew: true);
    }

    private void CopyBuiltIn(string ruleId, ulong? contentId)
    {
        Select(store.CopyBuiltIn(ruleId, contentId));
        status = "Copy created.";
        NotifyRulesChanged();
    }

    private void DrawBuiltInEditor(SquireCleanupRule rule, ulong? contentId)
    {
        DrawSectionHeading(rule.Name);
        ImGui.TextWrapped($"When {FormatCondition(rule.Condition)}, this rule will {FormatEffect(rule.Effect).ToLowerInvariant()}.");
        DrawWrappedColored(MarketMafiosoUiTheme.Muted, "Built-in definitions stay upgradeable. Their state and priority can be changed here, or copied into a fully editable user rule.");
        ImGui.Spacing();
        DrawFieldLabel("State");
        var enabled = rule.Enabled;
        if (ImGui.Checkbox("Enabled##BuiltIn", ref enabled))
        {
            store.SetBuiltInOverride(rule.Id, enabled: enabled);
            NotifyRulesChanged();
            Select(store.GetApplicable(contentId).Single(value => value.Id == rule.Id));
        }
        DrawFieldLabel("Priority");
        var priority = rule.Priority;
        ImGui.SetNextItemWidth(Math.Min(220, ImGui.GetContentRegionAvail().X));
        if (ImGui.InputInt("##BuiltInPriority", ref priority, 10, 100))
        {
            store.SetBuiltInOverride(rule.Id, priority: priority);
            NotifyRulesChanged();
            Select(store.GetApplicable(contentId).Single(value => value.Id == rule.Id));
        }
        ImGui.Spacing();
        if (ImGui.Button("Copy as global user rule"))
            CopyBuiltIn(rule.Id, null);
        RegisterLast("squire.rules.copy-built-in-global", "Copy this built-in cleanup rule into a global user rule", AgentBridgeUiControlKind.Button, true, false, rule.Id, () => CopyBuiltIn(rule.Id, null));
        if (contentId is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Copy for this character"))
                CopyBuiltIn(rule.Id, contentId);
            RegisterLast("squire.rules.copy-built-in-character", "Copy this built-in cleanup rule for the current character", AgentBridgeUiControlKind.Button, true, false, rule.Id, () => CopyBuiltIn(rule.Id, contentId));
        }
        ImGui.Spacing();
        DrawMatchPreview(rule);
    }

    private void DrawConditionEditor()
    {
        var condition = draft!.Condition;
        var columns = ImGui.GetContentRegionAvail().X >= 840 ? 2 : 1;
        if (ImGui.BeginTable("##RuleConditionGroups", columns, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Header, "Item identity");
            DrawFieldLabel("Specific items");
            if (DalamudItemAutocompleteRenderer.DrawMultiSelect(
                    "SquireRuleItems",
                    itemOptions,
                    itemSearch,
                    condition.ItemIds,
                    MarketMafiosoUiTheme.Muted,
                    MarketMafiosoUiTheme.Success,
                    MarketMafiosoUiTheme.Error,
                    out var selectedItemIds))
            {
                condition = condition with { ItemIds = selectedItemIds };
            }

            DrawFieldLabel("Quality");
            ImGui.SetNextItemWidth(-1);
            condition = condition with { Quality = EnumCombo("##RuleQuality", condition.Quality) };
            DrawFieldLabel("Rarity");
            ImGui.SetNextItemWidth(-1);
            condition = condition with { Rarities = DrawEnumSet("##RuleRarities", condition.Rarities, Enum.GetValues<EquipmentRarity>().Where(value => value != EquipmentRarity.Unknown)) };
            DrawEquipLevelRange(ref condition);

            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Header, "Observed state");
            condition = condition with { IsEquipment = DrawTriStateField("Equipment", "##RuleEquipment", condition.IsEquipment) };
            condition = condition with { IsPlayerSigned = DrawTriStateField("Player signed", "##RulePlayerSigned", condition.IsPlayerSigned) };
            condition = condition with { IsArmoireEligible = DrawTriStateField("Armoire eligible", "##RuleArmoire", condition.IsArmoireEligible) };
            condition = condition with { HasMateria = DrawTriStateField("Has attached materia", "##RuleMateria", condition.HasMateria) };
            condition = condition with { HasFutureLevelingUse = DrawTriStateField("Has future leveling use", "##RuleFutureUse", condition.HasFutureLevelingUse) };
            DrawFieldLabel("Equipment evaluation");
            ImGui.SetNextItemWidth(-1);
            condition = condition with { UseStatuses = DrawEnumSet("##RuleUseStatuses", condition.UseStatuses, Enum.GetValues<EquipmentUseStatus>()) };
            DrawFieldLabel("Available cleanup routes");
            ImGui.SetNextItemWidth(-1);
            condition = condition with { SupportedDispositions = DrawEnumSet("##RuleSupportedRoutes", condition.SupportedDispositions,
                Enum.GetValues<SquireDisposition>().Where(value => value is not (SquireDisposition.Keep or SquireDisposition.Unsupported))) };
            ImGui.EndTable();
        }
        draft = draft with { Condition = condition };
    }

    private void DrawEffectEditor()
    {
        var effect = draft!.Effect;
        var columns = ImGui.GetContentRegionAvail().X >= 840 ? 2 : 1;
        if (ImGui.BeginTable("##RuleOutcomeGroups", columns, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Header, "Decision and route");
            DrawFieldLabel("Cleanup decision");
            ImGui.SetNextItemWidth(-1);
            effect = effect with { Decision = EnumCombo("##RuleDecision", effect.Decision) };
            DrawFieldLabel("Preferred cleanup route");
            ImGui.SetNextItemWidth(-1);
            var routes = new SquireDisposition?[] { null, SquireDisposition.ExpertDelivery, SquireDisposition.Desynthesize, SquireDisposition.VendorSell, SquireDisposition.Discard };
            if (ImGui.BeginCombo("##RulePreferredRoute", effect.PreferredDisposition is null ? "No route preference" : FormatDisposition(effect.PreferredDisposition.Value)))
            {
                foreach (var route in routes)
                {
                    if (ImGui.Selectable(route is null ? "No route preference" : FormatDisposition(route.Value), effect.PreferredDisposition == route))
                        effect = effect with { PreferredDisposition = route };
                }
                ImGui.EndCombo();
            }
            DrawFieldLabel("Minimum copies to keep");
            var minimum = effect.MinimumCopies;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt("##RuleMinimumCopies", ref minimum))
                effect = effect with { MinimumCopies = Math.Clamp(minimum, 0, 99) };

            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Header, "Explicit authorizations");
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Allow matching items past the selected protection checks.");
            var authorizations = effect.Authorizations;
            foreach (var value in Enum.GetValues<SquireCleanupAuthorization>().Where(value => value != SquireCleanupAuthorization.None))
            {
                var selected = authorizations.HasFlag(value);
                if (ImGui.Checkbox($"{FormatAuthorization(value)}##Authorize{value}", ref selected))
                    authorizations = selected ? authorizations | value : authorizations & ~value;
            }
            effect = effect with { Authorizations = authorizations };
            ImGui.EndTable();
        }
        draft = draft with { Effect = effect };
    }

    private void DrawScopeEditor(ulong? contentId)
    {
        var scope = draft!.Scope;
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##RuleScope", scope == SquireCleanupRuleScope.Global ? "All characters" : CurrentCharacterLabel()))
            return;
        if (ImGui.Selectable("All characters", scope == SquireCleanupRuleScope.Global))
            draft = draft with { Scope = SquireCleanupRuleScope.Global, CharacterContentId = null };
        if (contentId is not null && ImGui.Selectable(CurrentCharacterLabel(), scope == SquireCleanupRuleScope.Character))
            draft = draft with { Scope = SquireCleanupRuleScope.Character, CharacterContentId = contentId };
        ImGui.EndCombo();
    }

    private void DrawMatchPreview(SquireCleanupRule rule)
    {
        DrawSectionHeading("Live impact");
        if (scrollToImpact)
        {
            ImGui.SetScrollHereY(0);
            scrollToImpact = false;
        }
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
        ImGui.TextWrapped($"The conditions above match {matches.Length} of {analysis.Candidates.Count} observed equipment items. These are their current assessments; saving the rule may change them.");
        if (matches.Length > 0 && ImGui.BeginTable("##SquireRuleImpactSummary", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("Candidates");
            ImGui.TableSetupColumn("Protected");
            ImGui.TableSetupColumn("Failures");
            ImGui.TableSetupColumn("Other");
            ImGui.TableHeadersRow();
            ImGui.TableNextRow();
            Cell(matches.Count(candidate => candidate.Assessment == SquireAssessment.Candidate).ToString());
            Cell(matches.Count(candidate => candidate.Assessment == SquireAssessment.Protected).ToString());
            Cell(matches.Count(candidate => candidate.Assessment == SquireAssessment.EvaluationFailure).ToString());
            Cell(matches.Count(candidate => candidate.Assessment is SquireAssessment.Unsupported).ToString());
            ImGui.EndTable();
        }
        if (matches.Length == 0)
            return;
        if (!ImGui.CollapsingHeader($"Matching items ({matches.Length})##RuleMatchingItems"))
            return;
        if (!ImGui.BeginTable("##SquireRulePreview", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY, new(0, Math.Min(240, 26 + matches.Length * 22))))
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
            RegisterLast("squire.rules.delete", "Request deletion of this cleanup rule", AgentBridgeUiControlKind.Button, true, false, rule.Id, () => pendingDeleteId = rule.Id);
            return;
        }
        if (ImGui.Button("Confirm delete"))
        {
            store.Remove(rule.Id);
            NotifyRulesChanged();
            ClearSelection();
            status = $"Deleted {rule.Name}.";
        }
        RegisterLast("squire.rules.confirm-delete", "Permanently delete this cleanup rule", AgentBridgeUiControlKind.Button, true, false, rule.Id, () =>
        {
            store.Remove(rule.Id);
            NotifyRulesChanged();
            ClearSelection();
            status = $"Deleted {rule.Name}.";
        });
        ImGui.SameLine();
        if (ImGui.Button("Keep rule"))
            pendingDeleteId = null;
        RegisterLast("squire.rules.keep-rule", "Cancel cleanup rule deletion", AgentBridgeUiControlKind.Button, true, false, rule.Id, () => pendingDeleteId = null);
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
            new(Decision: SquireCleanupDecision.Protect)), isNew: true);
    }

    private void Select(SquireCleanupRule rule, bool isNew = false)
    {
        selectedRuleId = rule.Id;
        draft = rule;
        original = isNew ? null : rule;
        itemSearch.SearchBuffer = string.Empty;
        itemSearch.SelectedItem = null;
        pendingDeleteId = null;
        confirmDiscard = false;
        scrollToImpact = false;
        scrollToTop = true;
        status = string.Empty;
    }

    private void ClearSelection()
    {
        selectedRuleId = null;
        draft = null;
        original = null;
        itemSearch.SearchBuffer = string.Empty;
        itemSearch.SelectedItem = null;
        pendingDeleteId = null;
        confirmDiscard = false;
        scrollToImpact = false;
        scrollToTop = false;
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
        NotifyRulesChanged();
    }

    private void SetEnabled(SquireCleanupRule rule, bool enabled)
    {
        if (rule.Origin == SquireCleanupRuleOrigin.BuiltIn)
            store.SetBuiltInOverride(rule.Id, enabled: enabled);
        else
            store.Replace(rule with { Enabled = enabled });
        NotifyRulesChanged();
        if (selectedRuleId == rule.Id)
            Select(store.GetAll().Single(value => value.Id == rule.Id));
    }

    private void NotifyRulesChanged() => requestAnalysisRefresh();

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

    private string FormatScope(SquireCleanupRule rule) => rule.Scope == SquireCleanupRuleScope.Global
        ? "All characters"
        : rule.CharacterContentId == playerState.ContentId && playerState.IsLoaded
            ? CurrentCharacterLabel()
            : $"Character {rule.CharacterContentId}";

    private string FormatCondition(SquireCleanupRuleCondition condition)
    {
        var values = new List<string>();
        if (condition.ItemIds is { Count: > 0 })
            values.Add(string.Join(" or ", condition.ItemIds.Select(ResolveItemName)));
        if (condition.Quality != SquireRuleQuality.Any) values.Add(FormatEnum(condition.Quality));
        if (condition.Rarities is { Count: > 0 }) values.Add($"rarity {string.Join(" or ", condition.Rarities.Select(FormatEnum))}");
        if (condition.UseStatuses is { Count: > 0 }) values.Add($"evaluation {string.Join(" or ", condition.UseStatuses.Select(FormatEnum))}");
        AddTri(values, "equipment", condition.IsEquipment);
        AddTri(values, "player signed", condition.IsPlayerSigned);
        AddTri(values, "Armoire eligible", condition.IsArmoireEligible);
        AddTri(values, "has materia", condition.HasMateria);
        AddTri(values, "future-leveling use", condition.HasFutureLevelingUse);
        if (condition.MinimumEquipLevel is not null) values.Add($"equip level >= {condition.MinimumEquipLevel}");
        if (condition.MaximumEquipLevel is not null) values.Add($"equip level <= {condition.MaximumEquipLevel}");
        if (condition.SupportedDispositions is { Count: > 0 }) values.Add($"supports {string.Join(" or ", condition.SupportedDispositions.Select(FormatDisposition))}");
        return values.Count == 0 ? "every observed item" : string.Join(" and ", values);
    }

    private static string FormatEffect(SquireCleanupRuleEffect effect)
    {
        var values = new List<string>();
        if (effect.Decision != SquireCleanupDecision.NoChange) values.Add(FormatEnum(effect.Decision));
        if (effect.PreferredDisposition is not null) values.Add($"Prefer {FormatDisposition(effect.PreferredDisposition.Value)}");
        if (effect.MinimumCopies > 0) values.Add($"Keep at least {effect.MinimumCopies} cop{(effect.MinimumCopies == 1 ? "y" : "ies")}");
        if (effect.Authorizations != SquireCleanupAuthorization.None)
            values.Add($"Authorize {string.Join(", ", Enum.GetValues<SquireCleanupAuthorization>().Where(value => value != SquireCleanupAuthorization.None && effect.Authorizations.HasFlag(value)).Select(FormatAuthorization))}");
        return values.Count == 0 ? "no effect" : string.Join("; ", values);
    }

    private void DrawEquipLevelRange(ref SquireCleanupRuleCondition condition)
    {
        DrawFieldLabel("Equip level range");
        if (!ImGui.BeginTable("##RuleEquipLevelRange", 2, ImGuiTableFlags.SizingStretchSame))
            return;
        ImGui.TableNextColumn();
        var minEnabled = condition.MinimumEquipLevel is not null;
        if (ImGui.Checkbox("Minimum##MinimumEquipLevelEnabled", ref minEnabled))
            condition = condition with { MinimumEquipLevel = minEnabled ? 1 : null };
        if (minEnabled)
        {
            var value = condition.MinimumEquipLevel ?? 1;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt("##MinimumEquipLevel", ref value))
                condition = condition with { MinimumEquipLevel = Math.Max(0, value) };
        }
        ImGui.TableNextColumn();
        var maxEnabled = condition.MaximumEquipLevel is not null;
        if (ImGui.Checkbox("Maximum##MaximumEquipLevelEnabled", ref maxEnabled))
            condition = condition with { MaximumEquipLevel = maxEnabled ? 100 : null };
        if (maxEnabled)
        {
            var value = condition.MaximumEquipLevel ?? 100;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt("##MaximumEquipLevel", ref value))
                condition = condition with { MaximumEquipLevel = Math.Max(0, value) };
        }
        ImGui.EndTable();
    }

    private bool? DrawTriStateField(string label, string id, bool? value)
    {
        DrawFieldLabel(label);
        ImGui.SetNextItemWidth(-1);
        return TriStateCombo(id, value);
    }

    private void DrawResolvedItemNames(IReadOnlySet<uint>? itemIds)
    {
        if (itemIds is not { Count: > 0 })
            return;
        var names = itemIds.Order().Select(ResolveItemName).ToArray();
        DrawWrappedColored(MarketMafiosoUiTheme.Muted, string.Join(", ", names));
    }

    private static void DrawSectionHeading(string label)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, label);
        ImGui.Separator();
    }

    private static void DrawFieldLabel(string label) => ImGui.TextColored(MarketMafiosoUiTheme.Muted, label);

    private static void DrawWrappedColored(System.Numerics.Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private string CurrentCharacterLabel()
    {
        var name = playerState.CharacterName.ToString();
        return string.IsNullOrWhiteSpace(name) ? "Current character" : name;
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

    private static string FormatEnum<T>(T value) where T : struct, Enum => value switch
    {
        EquipmentRarity.Normal => "Common (white)",
        EquipmentRarity.Uncommon => "Uncommon (green)",
        EquipmentRarity.Rare => "Rare (blue)",
        EquipmentRarity.Relic => "Relic (purple)",
        EquipmentUseStatus.Obsolete => "Strictly dominated by retained gear",
        EquipmentUseStatus.FutureUse => "Potential future use",
        EquipmentUseStatus.BaselineNotBetter => "Not proven obsolete",
        EquipmentUseStatus.NoObtainedEligibleJob => "No obtained job can use it",
        EquipmentUseStatus.LikelyCosmetic => "Likely cosmetic",
        EquipmentUseStatus.SpecialPurpose => "Special-purpose equipment",
        EquipmentUseStatus.EvaluationFailure => "Evaluation failed",
        SquireRuleQuality.Any => "Any quality",
        SquireRuleQuality.NormalQuality => "Normal quality",
        SquireRuleQuality.HighQuality => "High quality",
        SquireCleanupDecision.NoChange => "Leave decision unchanged",
        SquireCleanupDecision.Protect => "Protect from cleanup",
        SquireCleanupDecision.AllowCleanup => "Allow cleanup",
        SquireDisposition disposition => FormatDisposition(disposition),
        _ => System.Text.RegularExpressions.Regex.Replace(value.ToString(), "([a-z])([A-Z])", "$1 $2"),
    };

    private static string FormatDisposition(SquireDisposition value) => value switch
    {
        SquireDisposition.Keep => "Keep",
        SquireDisposition.ExpertDelivery => "Expert Delivery",
        SquireDisposition.Desynthesize => "Desynthesis",
        SquireDisposition.VendorSell => "Vendor sale",
        SquireDisposition.Discard => "Discard",
        _ => "Unsupported",
    };

    private static string FormatAuthorization(SquireCleanupAuthorization value) => value switch
    {
        SquireCleanupAuthorization.HighRarity => "Blue or purple gear",
        SquireCleanupAuthorization.MateriaRetrievalRisk => "Attached materia retrieval",
        SquireCleanupAuthorization.PlayerSignature => "Player-signed gear",
        SquireCleanupAuthorization.ArmoireEligible => "Armoire-eligible gear",
        SquireCleanupAuthorization.FutureLevelingUse => "Future leveling use",
        _ => FormatEnum(value),
    };

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
