using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Layout;
using Franthropy.Dalamud.UI.Tables;
using MarketMafioso.Automation.Items;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

/// <summary>
/// The Deposition tab: Quartermaster's placement matrix for the buy list (This plan) or every reserve rule (All
/// items), with destinations chosen in the side rail. Reads come from <see cref="MarketAcquisitionPlacementService"/>'s
/// cached view and edits are queued to it; nothing here calls IPC. Labels, ids and actions are built with the layout
/// and rail model, not per frame.
/// </summary>
public sealed partial class DepositionPanel
{
    private const string AgentPrefix = "acquisition.deposition";
    private const float HeaderAngleDegrees = 48f;
    private static readonly Vector4 CurrentCharacterTint = new(.56f, .70f, .85f, 1f);
    private static readonly Vector4 AmberColor = new(1f, .7f, .3f, 1f);
    private static readonly Vector4 RankColor = new(.18f, .18f, .18f, 1f);
    private static readonly Vector4 FirstRankColor = new(.18f, .36f, .47f, 1f);
    private static readonly Vector4 HoverCellColor = new(.14f, .19f, .23f, 1f);
    private static readonly Vector4 AnySeparatorColor = new(.23f, .23f, .23f, 1f);
    private static readonly AgentBridgeActionArgumentSchema ValueSchema = new(
        [new("value", AgentBridgeActionArgumentKind.String)]);

    private sealed record ToolbarButton(string Label, string Operation, bool NeedsTarget, bool Primary)
    {
        public string ImGuiLabel { get; } = $"{Label}##Deposition{Operation}";
        public string AgentId { get; } = $"{AgentPrefix}.{Operation}";
        public string AgentLabel { get; } = $"{Label} for the selected Deposition rows";
    }

    private static readonly ToolbarButton[] ToolbarButtons =
    [
        new("Make first", QuartermasterPlacementOperations.MakeFirst, NeedsTarget: true, Primary: true),
        new("Add as fallback", QuartermasterPlacementOperations.AddFallback, NeedsTarget: true, Primary: false),
        new("Remove", QuartermasterPlacementOperations.Remove, NeedsTarget: true, Primary: false),
        new("Use current holders", QuartermasterPlacementOperations.UseCurrentHolders, NeedsTarget: false, Primary: false),
        new("Clear destinations", QuartermasterPlacementOperations.ClearDestinations, NeedsTarget: false, Primary: false),
    ];

    private readonly Configuration config;
    private readonly IDataManager dataManager;
    private readonly MarketAcquisitionRequestBuilderPanel builder;
    private readonly MarketAcquisitionPlacementService placement;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly CharacterTreeRail rail;
    private readonly TableSelectionModel<RowKey> selection = new();
    private readonly DalamudSelectionBar selectionBar = new("DepositionSelectionBar")
    {
        EmptyText = "No rows selected",
        FormatCount = count => $"{count:N0} selected",
    };
    private readonly Dictionary<uint, int> stackSizes = new();
    private Filter? filter;
    private string search = string.Empty;
    private Destination? target;
    private Layout? layout;
    private HashSet<Destination>? ticked;
    // Bumped whenever the ticked destinations change, so the matrix layout and rail are rebuilt.
    private int tickVersion;
    // Bumped when a rail character is expanded or collapsed.
    private int railVersion;
    private List<CharacterTreeGroup>? railGroups;
    private (long View, int Ticks, int Rail) railKey;

    // Why editing is off this frame (null: it is on); read by the cached toolbar and cell actions.
    private string? editBlocked;

    private readonly Action<bool> drawToolbar;
    private readonly Action selectVisible;
    private readonly Action clearSelection;
    private readonly Action[] toolbarActions;
    private readonly Action chooseThisPlan;
    private readonly Action chooseAllItems;
    private readonly Action toggleStow;
    private readonly Action<bool> setRailCollapsed;
    private readonly Func<JsonElement?, AgentBridgeUiActionResult> searchControl;
    private readonly Func<JsonElement?, AgentBridgeUiActionResult> targetControl;

    private enum Filter
    {
        ThisPlan,
        AllItems,
    }

    /// <summary>A matrix row: the item and NqOnly, HqOnly or Any (a buy-list line of either quality).</summary>
    private readonly record struct RowKey(uint ItemId, string Quality);

    /// <summary>A destination column: a retainer of a character, or (retainer 0) the owner's Any space.</summary>
    private readonly record struct Destination(ulong LocalContentId, ulong RetainerId)
    {
        public bool IsAnySpace => RetainerId == 0;

        public string Persisted => $"{LocalContentId}:{RetainerId}";

        public string Id => $"{LocalContentId}.{(IsAnySpace ? "any" : RetainerId.ToString())}";

        public static Destination? Parse(string text)
        {
            var parts = text.Split(':');
            return parts.Length == 2 && ulong.TryParse(parts[0], out var character) && character != 0 &&
                   ulong.TryParse(parts[1], out var retainer)
                ? new Destination(character, retainer)
                : null;
        }
    }

    public DepositionPanel(
        Configuration config,
        IDataManager dataManager,
        MarketAcquisitionRequestBuilderPanel builder,
        MarketAcquisitionPlacementService placement,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config;
        this.dataManager = dataManager;
        this.builder = builder;
        this.placement = placement;
        this.reviewRegistry = reviewRegistry;
        rail = new CharacterTreeRail(reviewRegistry, "Destinations", $"{AgentPrefix}.destinations", "destinations");
        drawToolbar = DrawToolbar;
        selectVisible = SelectVisible;
        clearSelection = selection.Clear;
        toolbarActions = ToolbarButtons.Select(button => (Action)(() => QueueToolbarEdit(button))).ToArray();
        chooseThisPlan = () => filter = Filter.ThisPlan;
        chooseAllItems = () => filter = Filter.AllItems;
        toggleStow = () => builder.SetStowAfterDelivery(!builder.CurrentDocument.StowAfterDelivery);
        setRailCollapsed = SetRailCollapsed;
        searchControl = arguments => WithValue(arguments, value =>
        {
            search = value.Trim();
            return AgentBridgeUiActionResult.Ok("Search set.");
        });
        targetControl = arguments => WithValue(arguments, ChooseTarget);
        drawRow = DrawRow;
    }

    /// <summary>The tab exists only while Quartermaster offers placement editing over IPC.</summary>
    public bool IsAvailable => placement.HasPlacementEdit;

    /// <summary>Shows the buy list's rows (opened from the Acquisition tab's Placement button).</summary>
    public void ShowPlan()
    {
        filter = Filter.ThisPlan;
        search = string.Empty;
    }

    public void Draw(MarketAcquisitionRequestBuilderContext context)
    {
        placement.MarkVisible();
        var view = placement.View;
        var document = builder.CurrentDocument;
        EnsureDefaultDestinations(view);
        editBlocked = EditBlockedReason(context, view);
        var current = CurrentLayout(view, document);
        selection.Retain(current.Keys);

        DrawFilterRow(context, document, current);
        var statusHeight = ImGui.GetTextLineHeightWithSpacing();
        var height = Math.Max(150f, ImGui.GetContentRegionAvail().Y - statusHeight);
        var groups = RailGroups(view);
        if (groups.Count > 0)
        {
            rail.Draw("DepositionDestinations", groups, config.MarketAcquisitionDepositionRailCollapsed, setRailCollapsed, height);
            ImGui.SameLine();
        }
        if (ImGui.BeginChild("DepositionMain", new Vector2(0, height), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            selectionBar.Draw(selection.Count, MainWindow.ColHeader, drawToolbar);
            DrawMatrix(current);
        }
        ImGui.EndChild();
        DrawStatusLine(view);
    }

    // ---- Gating and status ----------------------------------------------------------------------

    /// <summary>The precondition editing is waiting on, named as the disabled controls' tooltip; null when editable.</summary>
    private string? EditBlockedReason(MarketAcquisitionRequestBuilderContext context, PlacementView view) =>
        !placement.HasPlacementEdit ? "Quartermaster unavailable"
        : view.Version == 0 ? placement.ReadProblem is null ? "Placement not read yet" : "Quartermaster unavailable"
        : !view.OwnerMatches ? "Logged in as another character"
        : context.IsRouteActive ? "Route in progress"
        : null;

    /// <summary>A read failure or refused edit outranks a success message; the owner mismatch shows only when nothing else does.</summary>
    private void DrawStatusLine(PlacementView view)
    {
        if (placement.ReadProblem is { } problem)
            ImGui.TextColored(MainWindow.ColWarning, problem);
        else if (placement.Status is { } status)
            ImGui.TextColored(status.Problem ? MainWindow.ColWarning : MainWindow.ColMuted, status.Text);
        else if (view.Version > 0 && !view.OwnerMatches)
            ImGui.TextColored(MainWindow.ColMuted, layout?.OwnerNote ?? string.Empty);
        else
            ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight()));
    }

    // ---- Filter row -------------------------------------------------------------------------------

    private Filter CurrentFilter(MarketAcquisitionRequestDocument document) =>
        filter ?? (document.Lines.Any(line => line.ItemId > 0) ? Filter.ThisPlan : Filter.AllItems);

    private void DrawFilterRow(MarketAcquisitionRequestBuilderContext context, MarketAcquisitionRequestDocument document, Layout current)
    {
        DrawSegment(current.ThisPlanLabel, current.ThisPlan, chooseThisPlan, $"{AgentPrefix}.filter.plan", "Show this plan's items");
        ImGui.SameLine(0, 0);
        DrawSegment(current.AllItemsLabel, !current.ThisPlan, chooseAllItems, $"{AgentPrefix}.filter.all", "Show every reserve item");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Min(220f, Math.Max(120f, ImGui.GetContentRegionAvail().X * .3f)));
        ImGui.InputTextWithHint("##DepositionSearch", "Search items", ref search, 128);
        reviewRegistry.Register($"{AgentPrefix}.search", "Filter Deposition rows by item name", AgentBridgeUiControlKind.Input,
            ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), true, false, search, ValueSchema, searchControl);

        // Delivered goods go to these destinations only while stowing is on; the setting is the buy list's own.
        var stow = document.StowAfterDelivery;
        var stowBlocked = builder.StowDisabledReason(context);
        DalamudUiLayout.SameLineIfFits("Stow after delivery  ");
        ImGui.BeginDisabled(stowBlocked is not null);
        if (ImGui.Checkbox("Stow after delivery##DepositionStow", ref stow))
            builder.SetStowAfterDelivery(stow);
        ImGui.EndDisabled();
        if (stowBlocked is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(stowBlocked);
        reviewRegistry.RegisterLastItem(
            $"{AgentPrefix}.stow",
            "Stow after delivery",
            AgentBridgeUiControlKind.Toggle,
            stowBlocked is null,
            document.StowAfterDelivery,
            document.StowAfterDelivery ? "On" : "Off",
            toggleStow);
    }

    private void DrawSegment(string label, bool active, Action choose, string agentId, string agentLabel)
    {
        if (active)
        {
            var accent = MainWindow.ColHeader;
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(accent.X * .45f, accent.Y * .45f, accent.Z * .45f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(accent.X * .55f, accent.Y * .55f, accent.Z * .55f, 1f));
        }
        if (ImGui.Button(label))
            choose();
        if (active)
            ImGui.PopStyleColor(2);
        reviewRegistry.RegisterLastItem(agentId, agentLabel, AgentBridgeUiControlKind.Toggle, true, active, active ? "Shown" : "Hidden", choose);
    }

    // ---- Destinations rail -----------------------------------------------------------------------

    private void SetRailCollapsed(bool collapsed)
    {
        if (config.MarketAcquisitionDepositionRailCollapsed == collapsed)
            return;
        config.MarketAcquisitionDepositionRailCollapsed = collapsed;
        config.Save();
    }

    private HashSet<Destination> Ticked() =>
        ticked ??= (config.MarketAcquisitionDepositionDestinations ?? [])
            .Select(Destination.Parse)
            .OfType<Destination>()
            .ToHashSet();

    private void SetTicked(IEnumerable<Destination> destinations, bool on)
    {
        var set = Ticked();
        var changed = false;
        foreach (var destination in destinations)
            changed |= on ? set.Add(destination) : set.Remove(destination);
        if (changed)
            SaveTicked(set);
    }

    private void SaveTicked(HashSet<Destination> set)
    {
        config.MarketAcquisitionDepositionDestinations = set.Select(destination => destination.Persisted).Order(StringComparer.Ordinal).ToList();
        config.Save();
        tickVersion++;
    }

    /// <summary>
    /// The first time a character is the owner: tick its assigned retainers, plus every destination already holding a
    /// home of a buy-list or reserve item. Only ever adds ticks, so earlier choices for other characters stay.
    /// </summary>
    private void EnsureDefaultDestinations(PlacementView view)
    {
        if (!view.OwnerMatches || view.Version == 0 || view.Characters.FirstOrDefault(character => character.IsOwner) is not { } owner)
            return;
        var defaulted = config.MarketAcquisitionDepositionDefaultedOwners ??= [];
        if (defaulted.Contains(owner.LocalContentId))
            return;
        var set = Ticked();
        foreach (var retainer in owner.Retainers)
            if (retainer.Assigned)
                set.Add(new Destination(owner.LocalContentId, retainer.RetainerId));
        foreach (var item in view.Items.Values)
        {
            foreach (var home in item.LocalHomes)
                set.Add(new Destination(owner.LocalContentId, home));
            if (item.AnySpace)
                set.Add(new Destination(owner.LocalContentId, 0));
            if (item.Remote is { Character.LocalContentId: { } remote })
                foreach (var home in item.Remote.Homes)
                    set.Add(new Destination(remote, home));
        }
        defaulted.Add(owner.LocalContentId);
        SaveTicked(set);
    }

    /// <summary>The rail model, rebuilt only when the view, the ticks or a character's expansion change.</summary>
    private List<CharacterTreeGroup> RailGroups(PlacementView view)
    {
        var key = (view.Version, tickVersion, railVersion);
        if (railGroups is not null && railKey == key)
            return railGroups;
        railKey = key;
        railGroups = BuildRailGroups(view);
        return railGroups;
    }

    private List<CharacterTreeGroup> BuildRailGroups(PlacementView view)
    {
        var groups = new List<CharacterTreeGroup>();
        var set = Ticked();
        foreach (var character in view.Characters)
        {
            var name = string.IsNullOrWhiteSpace(character.CharacterName) ? "Character" : character.CharacterName;
            var leaves = new List<CharacterTreeLeaf>();
            var all = new List<Destination>();
            foreach (var retainer in character.Retainers)
            {
                var destination = new Destination(character.LocalContentId, retainer.RetainerId);
                var label = RetainerLabel(retainer);
                var on = set.Contains(destination);
                all.Add(destination);
                leaves.Add(new CharacterTreeLeaf(
                    retainer.RetainerId.ToString(),
                    label,
                    on,
                    Enabled: true,
                    Available: retainer.Assigned,
                    retainer.FreeSlots is { } free ? $"{free:N0} free" : null,
                    null,
                    $"Show {name} · {label} as a destination column",
                    on ? "Shown" : "Hidden",
                    value => SetTicked([destination], value)));
            }
            if (character.OffersAnySpace)
            {
                var any = new Destination(character.LocalContentId, 0);
                var on = set.Contains(any);
                all.Add(any);
                leaves.Add(new CharacterTreeLeaf("any", "Any space", on, true, true, null, null,
                    $"Show {name} · Any space as a destination column", on ? "Shown" : "Hidden", value => SetTicked([any], value)));
            }
            var id = character.LocalContentId;
            groups.Add(new CharacterTreeGroup(
                character.LocalContentId,
                character.HomeWorldId,
                name,
                character.HomeWorldName,
                character.IsOwner ? CurrentCharacterTint : null,
                null,
                config.MarketAcquisitionDepositionExpanded.TryGetValue(id, out var open) ? open : character.IsOwner,
                expanded =>
                {
                    config.MarketAcquisitionDepositionExpanded[id] = expanded;
                    config.Save();
                    railVersion++;
                },
                leaves.Count > 0,
                $"Show all of {name}'s destinations",
                value => SetTicked(all, value),
                null,
                leaves));
        }
        return groups;
    }

    private static string RetainerLabel(QuartermasterPlacementRetainer retainer) =>
        retainer.RetainerName ?? $"Retainer {retainer.RetainerId}";

    // ---- Toolbar ----------------------------------------------------------------------------------

    private void DrawToolbar(bool hasSelection)
    {
        if (layout is not { } current)
            return;
        var empty = current.Rows.IsDefaultOrEmpty;
        DalamudUiLayout.SameLineIfFits("Select visible");
        ImGui.BeginDisabled(empty);
        if (ImGui.Button("Select visible##DepositionSelectVisible"))
            SelectVisible();
        ImGui.EndDisabled();
        if (empty && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("No rows shown");
        reviewRegistry.RegisterLastItem($"{AgentPrefix}.selectVisible", "Select every visible Deposition row", AgentBridgeUiControlKind.Button,
            !empty, false, null, selectVisible);

        DrawTargetPicker(current);
        var chosen = Target(current);
        for (var index = 0; index < ToolbarButtons.Length; index++)
        {
            var button = ToolbarButtons[index];
            var blocked = editBlocked
                          ?? (!hasSelection ? "No rows selected"
                              : button.NeedsTarget && chosen is null ? "No destination columns shown"
                              : null);
            DalamudUiLayout.SameLineIfFits(button.Label);
            var clicked = button.Primary ? ImGuiUi.PrimaryButton(button.ImGuiLabel, blocked is null) : ImGuiUi.Button(button.ImGuiLabel, blocked is null);
            if (blocked is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(blocked);
            if (clicked && blocked is null)
                toolbarActions[index]();
            reviewRegistry.RegisterLastItem(button.AgentId, button.AgentLabel, AgentBridgeUiControlKind.Button, blocked is null, false, null, toolbarActions[index]);
        }

        DalamudUiLayout.SameLineRight(DalamudUiLayout.ButtonWidth("Clear row selection"));
        ImGui.BeginDisabled(!hasSelection);
        if (ImGui.Button("Clear row selection##DepositionClearSelection"))
            selection.Clear();
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastItem($"{AgentPrefix}.clearSelection", "Clear the Deposition row selection", AgentBridgeUiControlKind.Button,
            hasSelection, false, null, clearSelection);
    }

    private void SelectVisible()
    {
        if (layout is not { } current)
            return;
        foreach (var key in current.Keys)
            selection.SetSelected(key, true);
    }

    private Column? Target(Layout current)
    {
        if (current.Columns.IsDefaultOrEmpty)
            return null;
        if (target is { } chosen)
            foreach (var column in current.Columns)
                if (column.Key == chosen)
                    return column;
        return current.Columns[0];
    }

    private void DrawTargetPicker(Layout current)
    {
        var chosen = Target(current);
        DalamudUiLayout.SameLineIfFits(150f);
        ImGui.SetNextItemWidth(150f);
        ImGui.BeginDisabled(chosen is null);
        if (ImGui.BeginCombo("##DepositionTarget", chosen?.PickerLabel ?? string.Empty))
        {
            foreach (var column in current.Columns)
            {
                var isChosen = chosen?.Key == column.Key;
                if (ImGui.Selectable(column.SelectableLabel, isChosen))
                    target = column.Key;
                if (isChosen)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        if (chosen is null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("No destination columns shown");
        reviewRegistry.Register($"{AgentPrefix}.target", current.TargetAgentLabel, AgentBridgeUiControlKind.Select,
            ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), chosen is not null, false, chosen?.PickerLabel, ValueSchema, targetControl);
    }

    private AgentBridgeUiActionResult ChooseTarget(string value)
    {
        if (layout is not { } current)
            return AgentBridgeUiActionResult.Fail("The Deposition matrix is not shown.");
        foreach (var column in current.Columns)
        {
            if (!string.Equals(column.PickerLabel, value, StringComparison.OrdinalIgnoreCase))
                continue;
            target = column.Key;
            return AgentBridgeUiActionResult.Ok($"{column.PickerLabel} chosen.");
        }
        return AgentBridgeUiActionResult.Fail($"Choose one of: {string.Join(", ", current.Columns.Select(column => column.PickerLabel))}.");
    }

    // ---- Edits ------------------------------------------------------------------------------------

    private void QueueToolbarEdit(ToolbarButton button)
    {
        if (editBlocked is not null || layout is not { } current)
            return;
        var rows = current.Rows.Where(row => selection.IsSelected(row.Key)).ToList();
        if (rows.Count == 0)
            return;
        var chosen = Target(current);
        var character = chosen?.Character ?? current.Owner;
        if (character is null)
            return;
        var destination = chosen is null
            ? null
            : chosen.Key.IsAnySpace ? QuartermasterPlacementDestination.Any : QuartermasterPlacementDestination.Retainer(chosen.Key.RetainerId);
        if (button.NeedsTarget && destination is null)
            return;
        var shownLabel = chosen is null || !button.NeedsTarget
            ? $"{button.Label} on {character.CharacterName}"
            : $"{button.Label}: {chosen.PickerLabel}";
        placement.RequestEdit(new PlacementEditRequest(button.Operation, shownLabel, character, destination,
            rows.SelectMany(row => row.EditItems).DistinctBy(item => (item.ItemId, item.Quality)).ToList()));
    }

    /// <summary>One cell click: adds or removes that destination for every rule the row maps to.</summary>
    private void QueueCellEdit(Row row, Column column, Cell cell)
    {
        if (editBlocked is not null || IsPending(row))
            return;
        // Rules of an either-quality row that disagree on this destination are made to agree by removing it.
        var operation = cell.Mixed ? QuartermasterPlacementOperations.Remove : QuartermasterPlacementOperations.Toggle;
        var destination = column.Key.IsAnySpace ? QuartermasterPlacementDestination.Any : QuartermasterPlacementDestination.Retainer(column.Key.RetainerId);
        placement.RequestEdit(new PlacementEditRequest(operation, column.PickerLabel, column.Character, destination, row.EditItems));
    }

    private bool IsPending(Row row)
    {
        foreach (var rule in row.Rules)
            if (placement.IsPending(rule))
                return true;
        return false;
    }

    // ---- Agent bridge -----------------------------------------------------------------------------

    private static AgentBridgeUiActionResult WithValue(JsonElement? arguments, Func<string, AgentBridgeUiActionResult> apply) =>
        arguments is { ValueKind: JsonValueKind.Object } argument &&
        argument.TryGetProperty("value", out var text) &&
        text.ValueKind == JsonValueKind.String
            ? apply(text.GetString() ?? string.Empty)
            : AgentBridgeUiActionResult.Fail("A string 'value' argument is required.");

    // ---- Stack sizes ------------------------------------------------------------------------------

    private int StackSize(uint itemId)
    {
        if (stackSizes.TryGetValue(itemId, out var cached))
            return cached;
        uint lumina;
        try { lumina = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId)?.StackSize ?? 0; }
        catch (Exception) { lumina = 0; }
        var size = Math.Max(1, ItemStackRules.ResolveMaxStack(itemId, lumina));
        stackSizes[itemId] = size;
        return size;
    }
}
