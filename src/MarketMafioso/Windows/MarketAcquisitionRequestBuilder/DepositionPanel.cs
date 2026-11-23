using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
/// cached view and edits are queued to it; nothing here calls IPC.
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
    // Bumped whenever the ticked destinations change, so the matrix layout is rebuilt.
    private int tickVersion;

    private enum Filter
    {
        ThisPlan,
        AllItems,
    }

    /// <summary>A matrix row: the item and NqOnly, HqOnly or Any (a buy-list line of either quality).</summary>
    private readonly record struct RowKey(uint ItemId, string Quality)
    {
        public string Id => $"{ItemId}.{Quality}";
    }

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
    }

    /// <summary>The tab exists only while Quartermaster offers placement editing over IPC.</summary>
    public bool IsAvailable => placement.HasPlacementEdit;

    /// <summary>Shows these items' rows (the buy list's when opened from the Acquisition tab).</summary>
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
        EnsureDefaultDestinations(view, document);
        var canEdit = CanEdit(context, view);

        DrawFilterRow(context, document);
        var statusHeight = ImGui.GetTextLineHeightWithSpacing();
        var height = Math.Max(150f, ImGui.GetContentRegionAvail().Y - statusHeight);
        var groups = BuildRailGroups(view);
        if (groups.Count > 0)
        {
            rail.Draw("DepositionDestinations", groups, config.MarketAcquisitionDepositionRailCollapsed, SetRailCollapsed, height);
            ImGui.SameLine();
        }
        if (ImGui.BeginChild("DepositionMain", new Vector2(0, height), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var current = CurrentLayout(view, document);
            selection.Retain(current.Keys);
            DrawSelectionBar(current, canEdit);
            DrawMatrix(current, canEdit);
        }
        ImGui.EndChild();
        DrawStatusLine(view);
    }

    // ---- Gating and status ----------------------------------------------------------------------

    private bool CanEdit(MarketAcquisitionRequestBuilderContext context, PlacementView view) =>
        placement.HasPlacementEdit && view.OwnerMatches && view.Version > 0 && !context.IsRouteActive;

    private void DrawStatusLine(PlacementView view)
    {
        if (placement.Status is { } status)
            ImGui.TextColored(status.Problem ? MainWindow.ColWarning : MainWindow.ColMuted, status.Text);
        else if (placement.ReadProblem is { } problem)
            ImGui.TextColored(MainWindow.ColWarning, problem);
        else if (view.Version > 0 && !view.OwnerMatches)
            ImGui.TextColored(MainWindow.ColMuted,
                $"Quartermaster's placement is for {view.Owner.CharacterName ?? "another character"}.");
        else
            ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight()));
    }

    // ---- Filter row -------------------------------------------------------------------------------

    private Filter CurrentFilter(MarketAcquisitionRequestDocument document) =>
        filter ?? (document.Lines.Any(line => line.ItemId > 0) ? Filter.ThisPlan : Filter.AllItems);

    private void DrawFilterRow(MarketAcquisitionRequestBuilderContext context, MarketAcquisitionRequestDocument document)
    {
        var current = CurrentFilter(document);
        var counts = layout is { } cached ? (cached.PlanCount, cached.AllCount) : (0, 0);
        DrawSegment($"This plan ({counts.Item1:N0})###DepositionThisPlan", current == Filter.ThisPlan, () => filter = Filter.ThisPlan, "filter.plan", "Show this plan's items");
        ImGui.SameLine(0, 0);
        DrawSegment($"All items ({counts.Item2:N0})###DepositionAllItems", current == Filter.AllItems, () => filter = Filter.AllItems, "filter.all", "Show every reserve item");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Min(220f, Math.Max(120f, ImGui.GetContentRegionAvail().X * .3f)));
        ImGui.InputTextWithHint("##DepositionSearch", "Search items", ref search, 128);
        RegisterLastArgument("search", "Filter Deposition rows by item name", AgentBridgeUiControlKind.Input, true, search, value =>
        {
            search = value.Trim();
            return AgentBridgeUiActionResult.Ok("Search set.");
        });

        // Delivered goods go to these destinations only while stowing is on; the setting is the buy list's own.
        var stow = document.StowAfterDelivery;
        var stowEnabled = !context.IsBusy && !context.IsRouteActive && !builder.IsSynchronizing;
        DalamudUiLayout.SameLineIfFits("Stow after delivery" + "  ");
        ImGui.BeginDisabled(!stowEnabled);
        if (ImGui.Checkbox("Stow after delivery##DepositionStow", ref stow))
            builder.SetStowAfterDelivery(stow);
        ImGui.EndDisabled();
        reviewRegistry.RegisterLastItem(
            $"{AgentPrefix}.stow",
            "Stow after delivery",
            AgentBridgeUiControlKind.Toggle,
            stowEnabled,
            document.StowAfterDelivery,
            document.StowAfterDelivery ? "On" : "Off",
            () => builder.SetStowAfterDelivery(!builder.CurrentDocument.StowAfterDelivery));
    }

    private void DrawSegment(string label, bool active, Action choose, string agentPart, string agentLabel)
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
        reviewRegistry.RegisterLastItem($"{AgentPrefix}.{agentPart}", agentLabel, AgentBridgeUiControlKind.Toggle,
            true, active, active ? "Shown" : "Hidden", choose);
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
        if (!changed)
            return;
        config.MarketAcquisitionDepositionDestinations = set.Select(destination => destination.Persisted).Order(StringComparer.Ordinal).ToList();
        config.Save();
        tickVersion++;
    }

    /// <summary>First use: the owner's retainers, plus every destination already holding a home of a buy-list or reserve item.</summary>
    private void EnsureDefaultDestinations(PlacementView view, MarketAcquisitionRequestDocument document)
    {
        if (config.MarketAcquisitionDepositionDestinations is not null || !view.OwnerMatches || view.Version == 0 ||
            view.Characters.FirstOrDefault(character => character.IsOwner) is not { } owner)
            return;
        var defaults = owner.Retainers.Where(retainer => retainer.Assigned)
            .Select(retainer => new Destination(owner.LocalContentId, retainer.RetainerId))
            .ToHashSet();
        foreach (var item in view.Items.Values)
        {
            defaults.UnionWith(item.LocalHomes.Select(id => new Destination(owner.LocalContentId, id)));
            if (item.AnySpace)
                defaults.Add(new Destination(owner.LocalContentId, 0));
            if (item.Remote is { Character.LocalContentId: { } remote })
                defaults.UnionWith(item.Remote.Homes.Select(id => new Destination(remote, id)));
        }
        config.MarketAcquisitionDepositionDestinations = defaults.Select(destination => destination.Persisted).Order(StringComparer.Ordinal).ToList();
        config.Save();
        ticked = null;
        tickVersion++;
    }

    private List<CharacterTreeGroup> BuildRailGroups(PlacementView view)
    {
        var groups = new List<CharacterTreeGroup>();
        var set = Ticked();
        foreach (var character in view.Characters)
        {
            var name = string.IsNullOrWhiteSpace(character.CharacterName) ? "Character" : character.CharacterName;
            var leaves = new List<CharacterTreeLeaf>();
            foreach (var retainer in character.Retainers)
            {
                var destination = new Destination(character.LocalContentId, retainer.RetainerId);
                var label = RetainerLabel(retainer);
                var on = set.Contains(destination);
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
                leaves.Add(new CharacterTreeLeaf("any", "Any space", on, true, true, null, null,
                    $"Show {name} · Any space as a destination column", on ? "Shown" : "Hidden", value => SetTicked([any], value)));
            }
            var all = leaves.Select((_, index) => index < character.Retainers.Length
                ? new Destination(character.LocalContentId, character.Retainers[index].RetainerId)
                : new Destination(character.LocalContentId, 0)).ToArray();
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

    // ---- Selection bar ----------------------------------------------------------------------------

    private void DrawSelectionBar(Layout current, bool canEdit)
    {
        var selected = current.Rows.Where(row => selection.IsSelected(row.Key)).ToList();
        selectionBar.Draw(selection.Count, MainWindow.ColHeader, hasSelection =>
        {
            DalamudUiLayout.SameLineIfFits("Select visible");
            ImGui.BeginDisabled(current.Rows.IsDefaultOrEmpty);
            if (ImGui.Button("Select visible##DepositionSelectVisible"))
                SelectVisible(current);
            ImGui.EndDisabled();
            RegisterLast("selectVisible", "Select every visible Deposition row", !current.Rows.IsDefaultOrEmpty, false, null,
                () => SelectVisible(current));

            DrawTargetPicker(current);
            var chosen = Target(current);
            var editable = canEdit && hasSelection;
            var withTarget = editable && chosen is not null;
            DrawToolbarButton("Make first", "makeFirst", withTarget, primary: true,
                () => QueueToolbarEdit(current, selected, QuartermasterPlacementOperations.MakeFirst, "Make first"));
            DrawToolbarButton("Add as fallback", "addFallback", withTarget, primary: false,
                () => QueueToolbarEdit(current, selected, QuartermasterPlacementOperations.AddFallback, "Add as fallback"));
            DrawToolbarButton("Remove", "remove", withTarget, primary: false,
                () => QueueToolbarEdit(current, selected, QuartermasterPlacementOperations.Remove, "Remove"));
            DrawToolbarButton("Use current holders", "useCurrentHolders", editable, primary: false,
                () => QueueToolbarEdit(current, selected, QuartermasterPlacementOperations.UseCurrentHolders, "Use current holders"));
            DrawToolbarButton("Clear destinations", "clearDestinations", editable, primary: false,
                () => QueueToolbarEdit(current, selected, QuartermasterPlacementOperations.ClearDestinations, "Clear destinations"));

            DalamudUiLayout.SameLineRight(DalamudUiLayout.ButtonWidth("Clear row selection"));
            ImGui.BeginDisabled(!hasSelection);
            if (ImGui.Button("Clear row selection##DepositionClearSelection"))
                selection.Clear();
            ImGui.EndDisabled();
            RegisterLast("clearSelection", "Clear the Deposition row selection", hasSelection, false, null, selection.Clear);
        });
    }

    private void SelectVisible(Layout current)
    {
        foreach (var key in current.Keys)
            selection.SetSelected(key, true);
    }

    private void DrawToolbarButton(string label, string agentPart, bool enabled, bool primary, Action apply)
    {
        DalamudUiLayout.SameLineIfFits(label);
        var clicked = primary ? ImGuiUi.PrimaryButton($"{label}##Deposition{agentPart}", enabled) : ImGuiUi.Button($"{label}##Deposition{agentPart}", enabled);
        if (clicked && enabled)
            apply();
        RegisterLast(agentPart, $"{label} for the selected Deposition rows", enabled, false, null, apply);
    }

    private Column? Target(Layout current) =>
        target is { } chosen && current.Columns.FirstOrDefault(column => column.Key == chosen) is { } match
            ? match
            : current.Columns.FirstOrDefault();

    private void DrawTargetPicker(Layout current)
    {
        var chosen = Target(current);
        DalamudUiLayout.SameLineIfFits(150f);
        ImGui.SetNextItemWidth(150f);
        ImGui.BeginDisabled(current.Columns.IsDefaultOrEmpty);
        if (ImGui.BeginCombo("##DepositionTarget", chosen is null ? string.Empty : PickerLabel(current, chosen)))
        {
            foreach (var column in current.Columns)
            {
                var isChosen = chosen?.Key == column.Key;
                if (ImGui.Selectable($"{PickerLabel(current, column)}##{column.Key.Id}", isChosen))
                    target = column.Key;
                if (isChosen)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        var options = current.Columns.Select(column => PickerLabel(current, column)).ToArray();
        RegisterLastArgument("target", $"Destination for Make first, Add as fallback and Remove: {string.Join(", ", options)}",
            AgentBridgeUiControlKind.Select, !current.Columns.IsDefaultOrEmpty, chosen is null ? null : PickerLabel(current, chosen), value =>
            {
                var match = current.Columns.FirstOrDefault(column => string.Equals(PickerLabel(current, column), value, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                    return AgentBridgeUiActionResult.Fail($"Choose one of: {string.Join(", ", options)}.");
                target = match.Key;
                return AgentBridgeUiActionResult.Ok($"{PickerLabel(current, match)} chosen.");
            });
    }

    private static string PickerLabel(Layout current, Column column) =>
        column.Key.IsAnySpace ? "Any space" : current.Banded ? $"{column.Character.CharacterName} · {column.Label}" : column.Label;

    // ---- Edits ------------------------------------------------------------------------------------

    private void QueueToolbarEdit(Layout current, IReadOnlyList<Row> rows, string operation, string label)
    {
        if (rows.Count == 0)
            return;
        var chosen = Target(current);
        var owner = current.Owner;
        var character = chosen?.Character ?? owner;
        if (character is null)
            return;
        var destination = chosen is null
            ? null
            : chosen.Key.IsAnySpace ? QuartermasterPlacementDestination.Any : QuartermasterPlacementDestination.Retainer(chosen.Key.RetainerId);
        if (QuartermasterPlacementOperations.NeedsDestination(operation) && destination is null)
            return;
        var shownLabel = chosen is null || !QuartermasterPlacementOperations.NeedsDestination(operation)
            ? $"{label} on {character.CharacterName}"
            : $"{label}: {PickerLabel(current, chosen)}";
        placement.RequestEdit(new PlacementEditRequest(operation, shownLabel, character, destination, EditItems(rows)));
    }

    /// <summary>One cell click: adds or removes that destination for the row's rules.</summary>
    private void QueueCellEdit(Layout current, Row row, Column column, Cell cell)
    {
        // Rules of an either-quality line that disagree on this destination are made to agree by removing it.
        var operation = cell.Mixed ? QuartermasterPlacementOperations.Remove : QuartermasterPlacementOperations.Toggle;
        var destination = column.Key.IsAnySpace ? QuartermasterPlacementDestination.Any : QuartermasterPlacementDestination.Retainer(column.Key.RetainerId);
        placement.RequestEdit(new PlacementEditRequest(operation, PickerLabel(current, column), column.Character, destination, EditItems([row])));
    }

    private static List<QuartermasterPlacementEditItem> EditItems(IEnumerable<Row> rows) =>
        rows.SelectMany(row => row.Rules.Select(rule => new QuartermasterPlacementEditItem(rule.ItemId, row.ItemName, rule.Quality)))
            .DistinctBy(item => (item.ItemId, item.Quality))
            .ToList();

    // ---- Agent bridge -----------------------------------------------------------------------------

    private void RegisterLast(string part, string label, bool enabled, bool selected, string? value, Action invoke) =>
        reviewRegistry.RegisterLastItem($"{AgentPrefix}.{part}", label, AgentBridgeUiControlKind.Button, enabled, selected, value, invoke);

    private void RegisterLastArgument(
        string part,
        string label,
        AgentBridgeUiControlKind kind,
        bool enabled,
        string? value,
        Func<string, AgentBridgeUiActionResult> apply) =>
        reviewRegistry.Register(
            $"{AgentPrefix}.{part}",
            label,
            kind,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            false,
            value,
            ValueSchema,
            arguments => arguments is { ValueKind: JsonValueKind.Object } argument &&
                         argument.TryGetProperty("value", out var text) &&
                         text.ValueKind == JsonValueKind.String
                ? apply(text.GetString() ?? string.Empty)
                : AgentBridgeUiActionResult.Fail("A string 'value' argument is required."));

    // ---- Stack sizes ------------------------------------------------------------------------------

    private int StackSize(uint itemId)
    {
        if (stackSizes.TryGetValue(itemId, out var cached))
            return cached;
        uint lumina = 0;
        try { lumina = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId)?.StackSize ?? 0; }
        catch (Exception) { lumina = 0; }
        var size = Math.Max(1, ItemStackRules.ResolveMaxStack(itemId, lumina));
        stackSizes[itemId] = size;
        return size;
    }
}
