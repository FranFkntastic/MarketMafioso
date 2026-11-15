using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Tables;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows;

/// <summary>
/// Split mode of the Saved Jobs browser: one click-to-edit quantity column per assembler, built
/// from the Trade Queue panel's status strip, selection bar, and quantity cell. Draws a
/// <see cref="WorkshopJobSplitPlan"/>; all allocation rules live there.
/// </summary>
internal sealed class WorkshopJobSplitPanel
{
    private const string SurfaceId = "workshop-split";
    private const float BulkQuantityWidth = 64f;
    private const float TargetComboWidth = 136f;
    private const float QtyColumnWidth = 52f;
    private const float AllottedColumnWidth = 104f;

    private static readonly AgentBridgeActionArgumentSchema QuantitySchema = new(
        [new("quantity", AgentBridgeActionArgumentKind.Integer, Minimum: 0, Maximum: WorkshopJobSplitPlan.MaxCellQuantity)]);
    private static readonly AgentBridgeActionArgumentSchema AssemblerSchema = new(
    [
        new("characterName", AgentBridgeActionArgumentKind.String),
        new("world", AgentBridgeActionArgumentKind.String),
    ]);
    private static readonly AgentBridgeActionArgumentSchema ToggleSchema = new(
        [new("enabled", AgentBridgeActionArgumentKind.Boolean)]);

    /// <summary>Assembler accents: distinct from the theme's success/warning/error meanings.</summary>
    private static readonly Vector4[] AccentPalette =
    [
        new(0.38f, 0.73f, 1.00f, 1f), // sky (theme header)
        new(0.74f, 0.60f, 1.00f, 1f), // violet
        new(0.36f, 0.86f, 0.80f, 1f), // teal
        new(0.98f, 0.56f, 0.80f, 1f), // rose
        new(0.86f, 0.80f, 0.60f, 1f), // sand
        new(0.56f, 0.64f, 1.00f, 1f), // periwinkle
    ];

    private readonly WorkshopJobSplitPlan plan;
    private readonly Func<uint, string> projectName;
    private readonly Func<IReadOnlyList<WorkshopAssembler>> knownAssemblers;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly TableSelectionModel<int> selection = new();
    private DalamudTableProjection<SplitRow>? table;
    private string tableSignature = string.Empty;
    private string filter = string.Empty;
    private bool unbalancedOnly;
    private int targetColumn;
    private int bulkQuantity = 1;
    private string newAssemblerInput = string.Empty;
    private (int Row, int Column)? editingCell;
    private int editingValue;
    private bool editorNeedsFocus;
    private (int Row, int Column)? advanceFrom;

    public WorkshopJobSplitPanel(
        WorkshopJobSplitPlan plan,
        Func<uint, string> projectName,
        Func<IReadOnlyList<WorkshopAssembler>> knownAssemblers,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.plan = plan;
        this.projectName = projectName;
        this.knownAssemblers = knownAssemblers;
        this.reviewRegistry = reviewRegistry;
    }

    public WorkshopJobSplitPlan Plan => plan;

    public static Vector4 Accent(int slot) => AccentPalette[((slot % AccentPalette.Length) + AccentPalette.Length) % AccentPalette.Length];

    /// <summary>Draws the surface. <paramref name="commit"/> returns a failure message or null.</summary>
    public void Draw(bool canEdit, Func<string?> commit, Action cancel)
    {
        var rows = Enumerable.Range(0, plan.RowCount)
            .Select(index => new SplitRow(index, projectName(plan.ProjectId(index)), plan.PlanQuantity(index)))
            .ToArray();
        var visible = FilterRows(rows);
        selection.Retain(rows.Select(row => row.Index));
        targetColumn = Math.Clamp(targetColumn, 0, Math.Max(0, plan.Columns.Count - 1));

        DrawStatusStrip();
        ImGui.Spacing();
        DrawFilterRow();
        if (plan.Columns.Count > 0)
            DrawSelectionBar(visible);
        DrawTable(rows, visible);
        ImGui.Spacing();
        DrawFooter(canEdit, commit, cancel);
    }

    private IReadOnlyList<SplitRow> FilterRows(IReadOnlyList<SplitRow> rows)
    {
        var query = filter.Trim();
        return rows
            .Where(row =>
                (!unbalancedOnly || plan.Difference(row.Index) != 0) &&
                (query.Length == 0 || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    // ---- status strip -------------------------------------------------------------------

    private void DrawStatusStrip()
    {
        var planTotal = plan.PlanTotal;
        var allotted = plan.AllottedTotal;
        var over = plan.OverTotal;
        var unassigned = plan.UnassignedTotal;
        var state = over > 0 ? MarketMafiosoUiTheme.Error : unassigned > 0 ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Success;
        var detailWidth = AllottedDetailWidth(over, unassigned);
        var allottedValue = $"{allotted:N0} / {planTotal:N0}";
        var allottedWidth = Math.Max(
            150f,
            ImGui.CalcTextSize(allottedValue).X +
            (detailWidth > 0f ? detailWidth + ImGui.GetStyle().ItemSpacing.X : 0f) +
            (ImGui.GetStyle().CellPadding.X * 2f) + 12f);

        var facts = new List<UtilityStatusFact>
        {
            new(
                "Allotted",
                allottedValue,
                state,
                detailWidth > 0f ? () => DrawAllottedDetail(over, unassigned) : null,
                detailWidth,
                Fill: planTotal <= 0 ? 0f : over > 0 ? 1f : (float)allotted / planTotal,
                FillColor: state,
                FixedWidth: allottedWidth),
        };

        for (var column = 0; column < plan.Columns.Count; column++)
        {
            var entry = plan.Columns[column];
            var total = plan.ColumnTotal(column);
            var accent = Accent(entry.AccentSlot);
            var captured = column;
            facts.Add(new(
                $"{Label(entry.Assembler)}##split-tile-{Slug(entry.Assembler)}",
                total == 1 ? "1 part" : $"{total:N0} parts",
                total > 0 ? accent : MarketMafiosoUiTheme.Muted,
                () => DrawAssemblerMenu(captured),
                ImGui.GetFrameHeight(),
                Accent: accent,
                Tooltip: entry.Assembler.Key));
        }

        var addWidth = ImGui.CalcTextSize("+ Assembler").X + (ImGui.GetStyle().FramePadding.X * 2f);
        facts.Add(new(
            "##split-add-tile",
            string.Empty,
            DrawAction: DrawAddAssembler,
            ActionWidth: addWidth,
            FixedWidth: addWidth + (ImGui.GetStyle().ItemSpacing.X * 2f) + 4f));

        UtilityWorkspaceUi.DrawStatusStrip("##workshopSplitStatus", facts);
    }

    private static float AllottedDetailWidth(int over, int unassigned)
    {
        var text = AllottedDetailText(over, unassigned);
        return text.Length == 0 ? 0f : ImGui.CalcTextSize(text).X + 4f;
    }

    private static string AllottedDetailText(int over, int unassigned) =>
        (over > 0, unassigned > 0) switch
        {
            (true, true) => $"{over:N0} over · {unassigned:N0} unassigned",
            (true, false) => $"{over:N0} over",
            (false, true) => $"{unassigned:N0} unassigned",
            _ => string.Empty,
        };

    private static void DrawAllottedDetail(int over, int unassigned)
    {
        if (over > 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Error, $"{over:N0} over");
            if (unassigned > 0)
            {
                ImGui.SameLine(0f, 4f);
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "·");
                ImGui.SameLine(0f, 4f);
            }
        }

        if (unassigned > 0)
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, $"{unassigned:N0} unassigned");
    }

    private void DrawAssemblerMenu(int column)
    {
        var entry = plan.Columns[column];
        var slug = Slug(entry.Assembler);
        var popupId = $"split-tile-menu-{slug}";
        if (ImGui.Button($"...##split-tile-menu-{slug}", new Vector2(ImGui.GetFrameHeight(), 0f)))
            ImGui.OpenPopup(popupId);
        RegisterLastAction(
            $"{SurfaceId}.assembler.{slug}.remove",
            $"Remove assembler {entry.Assembler.Key}",
            enabled: true,
            $"parts={plan.ColumnTotal(column):N0}",
            arguments: null,
            _ =>
            {
                var index = plan.IndexOf(entry.Assembler);
                if (index < 0)
                    return AgentBridgeUiActionResult.Fail($"{entry.Assembler.Key} is not an assembler in this split.");
                RemoveAssembler(index);
                return AgentBridgeUiActionResult.Ok($"Removed {entry.Assembler.Key} from the split.");
            });

        if (!ImGui.BeginPopup(popupId))
            return;

        ImGui.TextColored(Accent(entry.AccentSlot), entry.Assembler.CharacterName);
        ImGui.SameLine(0f, 4f);
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"@{entry.Assembler.World}");
        ImGui.Separator();
        if (ImGui.Selectable($"Remove {entry.Assembler.CharacterName}"))
        {
            RemoveAssembler(column);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void DrawAddAssembler()
    {
        const string popupId = "split-add-assembler";
        if (ImGui.Button("+ Assembler"))
        {
            newAssemblerInput = string.Empty;
            ImGui.OpenPopup(popupId);
        }

        // The button sits at the strip's right edge; open the picker leftward so it stays inside the window.
        var buttonMax = ImGui.GetItemRectMax();
        RegisterLastAction(
            $"{SurfaceId}.add-assembler",
            "Add an assembler to the split",
            enabled: true,
            string.Join(", ", plan.Columns.Select(column => column.Assembler.Key)),
            AssemblerSchema,
            arguments =>
            {
                var assembler = new WorkshopAssembler(
                    arguments!.Value.GetProperty("characterName").GetString()!.Trim(),
                    arguments.Value.GetProperty("world").GetString()!.Trim());
                if (assembler.CharacterName.Length == 0 || assembler.World.Length == 0)
                    return AgentBridgeUiActionResult.Fail("Assembler needs both a character name and a world.");
                return AddAssembler(assembler)
                    ? AgentBridgeUiActionResult.Ok($"Added {assembler.Key} with 0 parts.")
                    : AgentBridgeUiActionResult.Fail($"{assembler.Key} is already an assembler in this split.");
            });

        ImGui.SetNextWindowPos(new Vector2(buttonMax.X, buttonMax.Y), ImGuiCond.Appearing, new Vector2(1f, 0f));
        if (!ImGui.BeginPopup(popupId))
            return;

        var candidates = AddCandidates();
        foreach (var candidate in candidates)
        {
            if (ImGui.Selectable($"{candidate.CharacterName}##split-add-{Slug(candidate)}"))
            {
                AddAssembler(candidate);
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine(0f, 4f);
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"@{candidate.World}");
        }

        if (candidates.Count > 0)
            ImGui.Separator();
        if (ImGui.IsWindowAppearing() && candidates.Count == 0)
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(200f);
        if (ImGui.InputTextWithHint("##split-add-name-world", "Name@World", ref newAssemblerInput, 64, ImGuiInputTextFlags.EnterReturnsTrue) &&
            WorkshopJobSplitPlan.TryParseAssembler(newAssemblerInput, out var typed))
        {
            AddAssembler(typed);
            newAssemblerInput = string.Empty;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private IReadOnlyList<WorkshopAssembler> AddCandidates()
    {
        var candidates = new List<WorkshopAssembler>();
        foreach (var assembler in knownAssemblers().Prepend(plan.CurrentCharacter))
        {
            if (!plan.Contains(assembler) && !candidates.Any(existing => WorkshopJobSplitPlan.SameCharacter(existing, assembler)))
                candidates.Add(assembler);
        }

        return candidates;
    }

    private bool AddAssembler(WorkshopAssembler assembler)
    {
        var index = plan.Add(assembler);
        if (index < 0)
            return false;
        targetColumn = index;
        return true;
    }

    private void RemoveAssembler(int column)
    {
        plan.RemoveAt(column);
        editingCell = null;
        advanceFrom = null;
        if (targetColumn >= column)
            targetColumn = Math.Max(0, targetColumn - 1);
    }

    // ---- filter + selection bar ----------------------------------------------------------

    private void DrawFilterRow()
    {
        var checkboxWidth = ImGui.CalcTextSize("Unbalanced only").X + ImGui.GetFrameHeight() + (ImGui.GetStyle().ItemInnerSpacing.X * 2f);
        ImGui.SetNextItemWidth(Math.Max(200f, ImGui.GetContentRegionAvail().X - checkboxWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint("##workshopSplitFilter", "Filter projects...", ref filter, 120);
        ImGui.SameLine();
        ImGui.Checkbox("Unbalanced only##workshopSplitUnbalanced", ref unbalancedOnly);
        reviewRegistry.Register(
            $"{SurfaceId}.unbalanced-only",
            "Show only rows whose allotment differs from the plan",
            AgentBridgeUiControlKind.Toggle,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled: true,
            selected: unbalancedOnly,
            unbalancedOnly ? "enabled" : "disabled",
            ToggleSchema,
            SurfaceId,
            mutating: false,
            completionOperationKind: null,
            arguments =>
            {
                unbalancedOnly = arguments!.Value.GetProperty("enabled").GetBoolean();
                return AgentBridgeUiActionResult.Ok(unbalancedOnly ? "Showing unbalanced rows only." : "Showing every row.");
            });
    }

    private void DrawSelectionBar(IReadOnlyList<SplitRow> visible)
    {
        var style = ImGui.GetStyle();
        var count = selection.Count;
        var hasSelection = count > 0;
        var canSplitEvenly = plan.Columns.Count > 1;
        var countLabel = hasSelection ? $"{count:N0} row{(count == 1 ? string.Empty : "s")} selected" : "No rows selected";
        var actionWidth = SelectionActionWidth(canSplitEvenly);
        var innerWidth = ImGui.GetContentRegionAvail().X - 16f;
        var wraps = ImGui.CalcTextSize(countLabel).X + style.ItemSpacing.X + actionWidth > innerWidth;
        var barHeight = ImGui.GetFrameHeightWithSpacing() * (wraps ? 2f : 1f) + 10f;

        var header = MarketMafiosoUiTheme.Header;
        var background = hasSelection ? Scale(header, 0.20f, 0.96f) : Scale(header, 0.10f, 0.96f);
        var border = hasSelection ? Scale(header, 0.65f, 1f) : Scale(header, 0.38f, 1f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, background);
        ImGui.PushStyleColor(ImGuiCol.Border, border);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 5f));
        var open = ImGui.BeginChild(
            "WorkshopSplitSelectionBar",
            new Vector2(0f, barHeight),
            true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (open)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(hasSelection ? header : MarketMafiosoUiTheme.Muted, countLabel);
            if (wraps)
                ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - actionWidth));
            else
                ImGuiUi.SameLineRight(actionWidth);
            DrawSelectionActions(visible, hasSelection, canSplitEvenly);
        }

        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private void DrawSelectionActions(IReadOnlyList<SplitRow> visible, bool hasSelection, bool canSplitEvenly)
    {
        var canSelectVisible = visible.Count > 0;
        if (ImGuiUi.Button("Select visible", canSelectVisible))
            SelectVisible(visible);
        RegisterLastAction(
            $"{SurfaceId}.select-visible",
            "Select every visible split row",
            canSelectVisible,
            $"visible={visible.Count:N0}; selected={selection.Count:N0}",
            arguments: null,
            _ =>
            {
                SelectVisible(visible);
                return AgentBridgeUiActionResult.Ok($"Selected {visible.Count:N0} visible row(s).");
            });

        ImGui.SameLine();
        DrawTargetCombo();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(BulkQuantityWidth);
        ImGui.InputInt("##workshop-split-bulk-quantity", ref bulkQuantity, 0, 0);
        bulkQuantity = Math.Clamp(bulkQuantity, 0, WorkshopJobSplitPlan.MaxCellQuantity);

        var target = plan.Columns[targetColumn].Assembler;
        ImGui.SameLine();
        if (ImGuiUi.Button("Set quantity", hasSelection))
            plan.SetQuantity(SelectedRows(), targetColumn, bulkQuantity);
        RegisterLastAction(
            $"{SurfaceId}.set-quantity",
            "Set the target assembler's quantity on the selected rows",
            hasSelection,
            $"selected={selection.Count:N0}; target={target.Key}; quantity={bulkQuantity:N0}",
            QuantitySchema,
            arguments =>
            {
                var quantity = arguments!.Value.GetProperty("quantity").GetInt32();
                if (!TrySelected(out var failure))
                    return failure;
                plan.SetQuantity(SelectedRows(), targetColumn, quantity);
                return AgentBridgeUiActionResult.Ok(
                    $"Set {plan.Columns[targetColumn].Assembler.Key} to {Math.Clamp(quantity, 0, WorkshopJobSplitPlan.MaxCellQuantity):N0} on {selection.Count:N0} row(s).");
            });

        ImGui.SameLine();
        if (ImGuiUi.PrimaryButton("Assign all", hasSelection))
            plan.AssignAll(SelectedRows(), targetColumn);
        RegisterLastAction(
            $"{SurfaceId}.assign-all",
            "Give the target assembler every unit of the selected rows",
            hasSelection,
            $"selected={selection.Count:N0}; target={target.Key}",
            arguments: null,
            _ =>
            {
                if (!TrySelected(out var failure))
                    return failure;
                plan.AssignAll(SelectedRows(), targetColumn);
                return AgentBridgeUiActionResult.Ok(
                    $"Assigned {selection.Count:N0} row(s) entirely to {plan.Columns[targetColumn].Assembler.Key}.");
            });

        ImGui.SameLine();
        if (ImGuiUi.Button("Unassign", hasSelection))
            plan.Unassign(SelectedRows(), targetColumn);
        RegisterLastAction(
            $"{SurfaceId}.unassign",
            "Zero the target assembler on the selected rows",
            hasSelection,
            $"selected={selection.Count:N0}; target={target.Key}",
            arguments: null,
            _ =>
            {
                if (!TrySelected(out var failure))
                    return failure;
                plan.Unassign(SelectedRows(), targetColumn);
                return AgentBridgeUiActionResult.Ok(
                    $"Unassigned {plan.Columns[targetColumn].Assembler.Key} from {selection.Count:N0} row(s).");
            });

        if (canSplitEvenly)
        {
            ImGui.SameLine();
            if (ImGuiUi.Button("Split evenly", hasSelection))
                plan.SplitEvenly(SelectedRows());
            RegisterLastAction(
                $"{SurfaceId}.split-evenly",
                "Deal the selected rows' units round-robin across every assembler",
                hasSelection,
                $"selected={selection.Count:N0}; assemblers={plan.Columns.Count:N0}",
                arguments: null,
                _ =>
                {
                    if (!TrySelected(out var failure))
                        return failure;
                    if (plan.Columns.Count < 2)
                        return AgentBridgeUiActionResult.Fail("Split evenly needs at least two assemblers.");
                    plan.SplitEvenly(SelectedRows());
                    return AgentBridgeUiActionResult.Ok(
                        $"Split {selection.Count:N0} row(s) evenly across {plan.Columns.Count:N0} assemblers.");
                });
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Clear row selection", hasSelection))
            selection.Clear();
        RegisterLastAction(
            $"{SurfaceId}.clear-row-selection",
            "Clear split row selection",
            hasSelection,
            $"selected={selection.Count:N0}",
            arguments: null,
            _ =>
            {
                selection.Clear();
                return AgentBridgeUiActionResult.Ok("Cleared split row selection.");
            });
    }

    private void DrawTargetCombo()
    {
        var current = plan.Columns[targetColumn];
        ImGui.SetNextItemWidth(TargetComboWidth);
        ImGui.PushStyleColor(ImGuiCol.Text, Accent(current.AccentSlot));
        var open = ImGui.BeginCombo("##workshop-split-target", Label(current.Assembler));
        ImGui.PopStyleColor();
        if (open)
        {
            for (var column = 0; column < plan.Columns.Count; column++)
            {
                var entry = plan.Columns[column];
                ImGui.PushStyleColor(ImGuiCol.Text, Accent(entry.AccentSlot));
                if (ImGui.Selectable($"{Label(entry.Assembler)}##split-target-{Slug(entry.Assembler)}", column == targetColumn))
                    targetColumn = column;
                ImGui.PopStyleColor();
            }

            ImGui.EndCombo();
        }

        reviewRegistry.Register(
            $"{SurfaceId}.target",
            "Choose the assembler the selection-bar actions target",
            AgentBridgeUiControlKind.Input,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled: true,
            selected: false,
            current.Assembler.Key,
            AssemblerSchema,
            SurfaceId,
            mutating: false,
            completionOperationKind: null,
            arguments =>
            {
                var requested = new WorkshopAssembler(
                    arguments!.Value.GetProperty("characterName").GetString()!.Trim(),
                    arguments.Value.GetProperty("world").GetString()!.Trim());
                var index = plan.IndexOf(requested);
                if (index < 0)
                    return AgentBridgeUiActionResult.Fail($"{requested.Key} is not an assembler in this split.");
                targetColumn = index;
                return AgentBridgeUiActionResult.Ok($"Selection-bar actions now target {requested.Key}.");
            });
    }

    private static float SelectionActionWidth(bool includeSplitEvenly)
    {
        var style = ImGui.GetStyle();
        var labels = new List<string> { "Select visible", "Set quantity", "Assign all", "Unassign", "Clear row selection" };
        if (includeSplitEvenly)
            labels.Add("Split evenly");
        return TargetComboWidth + BulkQuantityWidth +
               labels.Sum(label => ImGui.CalcTextSize(label).X + (style.FramePadding.X * 2f)) +
               ((labels.Count + 1) * style.ItemSpacing.X);
    }

    private void SelectVisible(IReadOnlyList<SplitRow> visible)
    {
        foreach (var row in visible)
            selection.SetSelected(row.Index, true);
    }

    private IReadOnlyList<int> SelectedRows() => selection.SelectedKeys.Order().ToArray();

    private bool TrySelected(out AgentBridgeUiActionResult failure)
    {
        failure = AgentBridgeUiActionResult.Fail("No split rows are selected.");
        return selection.Count > 0 && plan.Columns.Count > 0;
    }

    // ---- table ---------------------------------------------------------------------------

    private void DrawTable(IReadOnlyList<SplitRow> rows, IReadOnlyList<SplitRow> visible)
    {
        var projection = EnsureTable();
        var height = Math.Max(160f, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() - (ImGui.GetStyle().ItemSpacing.Y * 2f));
        if (!projection.Begin("WorkshopSplitTable", height))
            return;

        DrawColumnAccents();
        var ordered = projection.Apply(visible, ImGui.TableGetSortSpecs());
        if (advanceFrom is { } from)
        {
            advanceFrom = null;
            var position = ordered.ToList().FindIndex(row => row.Index == from.Row);
            if (position >= 0 && ordered.Count > 0 && from.Column < plan.Columns.Count)
                BeginEdit(ordered[(position + 1) % ordered.Count].Index, from.Column);
        }

        if (ordered.Count == 0)
        {
            projection.DrawMessageRow(
                rows.Count == 0 ? "No projects." : "No matching projects.",
                textColor: MarketMafiosoUiTheme.Muted);
        }

        projection.DrawClippedRows(
            ordered,
            (row, index) => projection.DrawSaneRow(
                ordered,
                index,
                $"workshop-split-row-{row.Index}",
                background: plan.Difference(row.Index) > 0 ? WithAlpha(MarketMafiosoUiTheme.Error, 0.10f) : null));
        projection.End();
    }

    private DalamudTableProjection<SplitRow> EnsureTable()
    {
        var signature = string.Join('|', plan.Columns.Select(column => $"{column.Assembler.Key}:{column.AccentSlot}"));
        if (table != null && signature == tableSignature)
            return table;

        var columns = new List<DalamudTableColumn<SplitRow>>
        {
            new(
                "Project",
                1f,
                row => row.Name,
                row => row.Name,
                ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide,
                Draw: DrawProjectCell,
                Id: "project"),
            new(
                "Qty",
                QtyColumnWidth,
                row => row.Plan.ToString("N0"),
                row => row.Plan,
                ImGuiTableColumnFlags.WidthFixed,
                Draw: row => DrawRightAligned(row.Plan.ToString("N0"), null),
                Id: "qty"),
            new(
                "Allotted",
                AllottedColumnWidth,
                row => plan.Allotted(row.Index).ToString("N0"),
                row => plan.Difference(row.Index),
                ImGuiTableColumnFlags.WidthFixed,
                Draw: DrawAllottedCell,
                Id: "allotted"),
        };

        var available = Math.Max(400f, ImGui.GetContentRegionAvail().X);
        var assemblerWidth = plan.Columns.Count == 0
            ? 96f
            : Math.Clamp((available - 280f - QtyColumnWidth - AllottedColumnWidth) / plan.Columns.Count, 72f, 128f);
        foreach (var entry in plan.Columns)
        {
            var assembler = entry.Assembler;
            columns.Add(new(
                $"{Label(assembler)}##split-col-{Slug(assembler)}",
                assemblerWidth,
                row => CellValue(row, assembler).ToString("N0"),
                row => CellValue(row, assembler),
                ImGuiTableColumnFlags.WidthFixed,
                Draw: row => DrawAssemblerCell(row, assembler),
                Id: $"assembler:{assembler.Key.ToLowerInvariant()}",
                HeaderTooltip: assembler.Key,
                SelectionTargetFraction: 0.5f));
        }

        tableSignature = signature;
        table = new DalamudTableProjection<SplitRow>(
            columns,
            DalamudTableSelection<SplitRow>.Multi(selection, row => row.Index));
        return table;
    }

    private void DrawColumnAccents()
    {
        for (var column = 0; column < plan.Columns.Count; column++)
        {
            if (!ImGui.TableSetColumnIndex(3 + column))
                continue;
            UtilityWorkspaceUi.DrawAccentCap(
                ImGui.GetCursorScreenPos(),
                ImGui.GetContentRegionAvail().X,
                Accent(plan.Columns[column].AccentSlot));
        }
    }

    private int CellValue(SplitRow row, WorkshopAssembler assembler)
    {
        var column = plan.IndexOf(assembler);
        return column < 0 ? 0 : plan.Get(row.Index, column);
    }

    private void DrawProjectCell(SplitRow row)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(row.Name);
        var selected = selection.IsSelected(row.Index);
        RegisterLastAction(
            $"{SurfaceId}.row-{row.Index}",
            selected ? $"Unselect {row.Name}" : $"Select {row.Name}",
            enabled: true,
            selected ? "selected" : "not selected",
            arguments: null,
            _ =>
            {
                var next = !selection.IsSelected(row.Index);
                selection.SetSelected(row.Index, next);
                return AgentBridgeUiActionResult.Ok(next ? $"Selected {row.Name}." : $"Unselected {row.Name}.");
            });
    }

    private void DrawAllottedCell(SplitRow row)
    {
        var allotted = plan.Allotted(row.Index);
        var difference = plan.Difference(row.Index);
        var color = difference > 0 ? MarketMafiosoUiTheme.Error : difference < 0 ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Muted;
        var value = allotted.ToString("N0");
        if (difference == 0)
        {
            DrawRightAligned(value, color);
            return;
        }

        var chip = difference > 0 ? $"+{difference:N0}" : $"-{-difference:N0}";
        const float chipPadding = 5f;
        const float gap = 6f;
        var chipSize = ImGui.CalcTextSize(chip) + new Vector2(chipPadding * 2f, 0f);
        var valueWidth = ImGui.CalcTextSize(value).X;
        var start = ImGui.GetCursorPosX();
        var available = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(start + Math.Max(0f, available - valueWidth - gap - chipSize.X));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(color, value);
        ImGui.SameLine(0f, gap);
        var origin = ImGui.GetCursorScreenPos();
        var frameOffset = (ImGui.GetFrameHeight() - chipSize.Y) * 0.5f;
        var min = new Vector2(origin.X, origin.Y + Math.Max(0f, frameOffset));
        var max = min + chipSize;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(WithAlpha(color, 0.18f)), chipSize.Y * 0.5f);
        drawList.AddText(new Vector2(min.X + chipPadding, min.Y), ImGui.GetColorU32(color), chip);
        ImGui.Dummy(new Vector2(chipSize.X, ImGui.GetFrameHeight()));
    }

    private static void DrawRightAligned(string text, Vector4? color)
    {
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - width));
        ImGui.AlignTextToFramePadding();
        if (color is { } tint)
            ImGui.TextColored(tint, text);
        else
            ImGui.TextUnformatted(text);
    }

    private void DrawAssemblerCell(SplitRow row, WorkshopAssembler assembler)
    {
        var column = plan.IndexOf(assembler);
        if (column < 0)
            return;

        var accent = Accent(plan.Columns[column].AccentSlot);
        var value = plan.Get(row.Index, column);
        var editWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X * 0.5f);
        if (editingCell != (row.Index, column))
        {
            var style = ImGui.GetStyle();
            var idle = style.Colors[(int)ImGuiCol.FrameBg];
            ImGui.PushStyleColor(ImGuiCol.Button, value > 0 ? WithAlpha(accent, 0.14f) : WithAlpha(idle, idle.W * 0.72f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, value > 0 ? WithAlpha(accent, 0.28f) : style.Colors[(int)ImGuiCol.FrameBgHovered]);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, value > 0 ? WithAlpha(accent, 0.40f) : style.Colors[(int)ImGuiCol.FrameBgActive]);
            ImGui.PushStyleColor(ImGuiCol.Text, value > 0 ? accent : MarketMafiosoUiTheme.Muted);
            ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0f, 0.5f));
            var activated = ImGui.Button(
                $"{value:N0}##workshop-split-cell-{row.Index}-{Slug(assembler)}",
                new Vector2(editWidth, ImGui.GetFrameHeight()));
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (activated)
                BeginEdit(row.Index, column);
            RegisterCellInput(row, assembler, value);
            return;
        }

        ImGui.SetNextItemWidth(editWidth);
        if (editorNeedsFocus)
        {
            ImGui.SetKeyboardFocusHere();
            editorNeedsFocus = false;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, accent);
        ImGui.InputInt(
            $"##workshop-split-cell-edit-{row.Index}-{Slug(assembler)}",
            ref editingValue,
            0,
            0,
            "%d",
            ImGuiInputTextFlags.AutoSelectAll);
        ImGui.PopStyleColor();
        var interaction = ImGui.IsItemActive() || ImGui.IsItemDeactivated();
        var deactivated = ImGui.IsItemDeactivated();
        if (interaction && ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            editingCell = null;
        }
        else if (interaction && ImGui.IsKeyPressed(ImGuiKey.Tab))
        {
            plan.Set(row.Index, column, editingValue);
            editingCell = null;
            advanceFrom = (row.Index, column);
        }
        else if (interaction && ImGui.IsKeyPressed(ImGuiKey.Enter) || deactivated)
        {
            plan.Set(row.Index, column, editingValue);
            editingCell = null;
        }

        RegisterCellInput(row, assembler, value);
    }

    private void BeginEdit(int row, int column)
    {
        editingCell = (row, column);
        editingValue = plan.Get(row, column);
        editorNeedsFocus = true;
    }

    private void RegisterCellInput(SplitRow row, WorkshopAssembler assembler, int value) =>
        reviewRegistry.Register(
            $"{SurfaceId}.row-{row.Index}.{Slug(assembler)}.quantity",
            $"Set {assembler.Key}'s quantity for {row.Name}",
            AgentBridgeUiControlKind.Input,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled: true,
            selected: false,
            value.ToString(),
            QuantitySchema,
            SurfaceId,
            mutating: true,
            completionOperationKind: null,
            arguments =>
            {
                var column = plan.IndexOf(assembler);
                if (column < 0)
                    return AgentBridgeUiActionResult.Fail($"{assembler.Key} is not an assembler in this split.");
                var requested = arguments!.Value.GetProperty("quantity").GetInt32();
                plan.Set(row.Index, column, requested);
                editingCell = null;
                return AgentBridgeUiActionResult.Ok(
                    $"Set {assembler.Key} to {plan.Get(row.Index, column):N0} for {row.Name}.");
            });

    // ---- footer --------------------------------------------------------------------------

    private void DrawFooter(bool canEdit, Func<string?> commit, Action cancel)
    {
        var canCommit = canEdit && plan.CanCommit;
        if (ImGuiUi.PrimaryButton("Commit Split", canCommit))
            commit();
        RegisterLastAction(
            $"{SurfaceId}.commit",
            "Commit the split and deliver each share",
            canCommit,
            $"allotted={plan.AllottedTotal:N0}/{plan.PlanTotal:N0}; over={plan.OverTotal:N0}; unassigned={plan.UnassignedTotal:N0}",
            arguments: null,
            _ =>
            {
                if (!canEdit)
                    return AgentBridgeUiActionResult.Fail("Saved jobs cannot change while an assembly run is active.");
                if (!plan.IsBalanced)
                {
                    return AgentBridgeUiActionResult.Fail(
                        $"Split is not balanced: {plan.OverTotal:N0} over, {plan.UnassignedTotal:N0} unassigned.");
                }

                if (!plan.CanCommit)
                    return AgentBridgeUiActionResult.Fail("No character other than the current one holds a part.");
                var failure = commit();
                return failure == null
                    ? AgentBridgeUiActionResult.Ok("Committed the split.")
                    : AgentBridgeUiActionResult.Fail(failure);
            });

        ImGui.SameLine();
        if (ImGui.Button("Cancel##workshopSplitCancel"))
            cancel();
        RegisterLastAction(
            $"{SurfaceId}.cancel",
            "Leave split mode without changing the saved job",
            enabled: true,
            value: null,
            arguments: null,
            _ =>
            {
                cancel();
                return AgentBridgeUiActionResult.Ok("Left split mode; nothing changed.");
            });
    }

    // ---- helpers -------------------------------------------------------------------------

    private void RegisterLastAction(
        string id,
        string label,
        bool enabled,
        string? value,
        AgentBridgeActionArgumentSchema? arguments,
        Func<System.Text.Json.JsonElement?, AgentBridgeUiActionResult> invoke) =>
        reviewRegistry.Register(
            id,
            label,
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            selected: false,
            value,
            arguments,
            surfaceId: SurfaceId,
            mutating: true,
            completionOperationKind: null,
            invoke);

    /// <summary>Character name, plus the world when another assembler in this split shares the name.</summary>
    private string Label(WorkshopAssembler assembler) =>
        plan.Columns.Count(entry => string.Equals(entry.Assembler.CharacterName, assembler.CharacterName, StringComparison.OrdinalIgnoreCase)) > 1
            ? $"{assembler.CharacterName} @{assembler.World}"
            : assembler.CharacterName;

    internal static string Slug(WorkshopAssembler assembler)
    {
        // Readable part for agent-bridge ids, plus a stable FNV-1a hash of the exact key so names
        // that differ only in punctuation (Ja'ra vs Ja-ra) never share an ImGui or action id.
        var key = assembler.Key.ToLowerInvariant();
        var builder = new StringBuilder();
        foreach (var character in key)
            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        var hash = 2166136261u;
        foreach (var character in key)
            hash = (hash ^ character) * 16777619u;
        return builder.Append('-').Append((hash & 0xFFFFFF).ToString("x6")).ToString();
    }

    private static Vector4 WithAlpha(Vector4 color, float alpha) => new(color.X, color.Y, color.Z, alpha);

    private static Vector4 Scale(Vector4 color, float scale, float alpha) =>
        new(color.X * scale, color.Y * scale, color.Z * scale, alpha);

    private sealed record SplitRow(int Index, string Name, int Plan);
}
