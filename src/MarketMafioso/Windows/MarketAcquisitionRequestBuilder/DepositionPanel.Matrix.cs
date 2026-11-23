using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Drawing;
using Franthropy.Dalamud.UI.Layout;
using Franthropy.Dalamud.UI.Tables;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed partial class DepositionPanel
{
    private const float MaxHeaderHeight = 160f;
    private const float WideMatrixWidth = 1300f;
    private const float NarrowCellWidth = 50f;
    private const float WideCellWidth = 64f;

    /// <summary>A destination column with its drawn and agent-facing text. FreeSlots null while unknown.</summary>
    private sealed record Column(
        Destination Key,
        QuartermasterPlacementCharacter Character,
        string Label,
        int? FreeSlots,
        bool StockObserved,
        bool FirstOfCharacter,
        int Span)
    {
        public string PickerLabel { get; init; } = Label;
        public string SelectableLabel { get; init; } = Label;
        public string HeaderText { get; init; } = Label;
        public string HeaderTooltip { get; init; } = Label;
        public string FreeText { get; init; } = "—";
        public string BandNarrow { get; init; } = string.Empty;
        public string BandWide { get; init; } = string.Empty;
    }

    /// <summary>
    /// One cell. Rank is the first rule's rank there (1 first home, 2+ fallbacks); Mixed when the row's rules disagree,
    /// on membership or rank, and Badge then shows each ("1/2", "1/–"). Held is null when unknown or for Any space.
    /// </summary>
    private sealed class Cell
    {
        public int? Rank { get; init; }
        public bool Mixed { get; init; }
        public bool FirstRank { get; init; }
        public string? Badge { get; init; }
        public string? HeldText { get; init; }
        public required string Tooltip { get; init; }
        public required string TooltipBase { get; init; }
        public required string AgentId { get; init; }
        public required string AgentLabel { get; init; }
        public required string AgentValue { get; init; }
        public Action Invoke { get; set; } = static () => { };
    }

    /// <summary>
    /// A matrix row. ArrivingText null: nothing arriving (blank). SubText is the slots line, amber when the arrival
    /// cannot be absorbed by its destinations; ShortHome is then the first home to tint.
    /// </summary>
    private sealed record Row(
        RowKey Key,
        string Id,
        string ItemName,
        ImmutableArray<PlacementRuleKey> Rules,
        IReadOnlyList<QuartermasterPlacementEditItem> EditItems,
        string? ArrivingText,
        bool ArrivingMuted,
        string? SubText,
        bool SubAmber,
        Destination? ShortHome,
        Cell[] Cells)
    {
        public string SelectAgentId { get; } = $"{AgentPrefix}.row.{Id}.select";
        public string SelectAgentLabel { get; } = $"Select {ItemName}";
        public Action ToggleSelect { get; set; } = static () => { };
    }

    private sealed record Layout(
        int Signature,
        QuartermasterPlacementCharacter? Owner,
        ImmutableArray<Column> Columns,
        ImmutableArray<Row> Rows,
        ImmutableArray<RowKey> Keys,
        bool Banded,
        bool ThisPlan,
        float HeaderHeight,
        string ThisPlanLabel,
        string AllItemsLabel,
        string TargetAgentLabel,
        string OwnerNote);

    /// <summary>What a shortfall check found: the home to tint, the slots still needed after partial stacks, and the free slots it had.</summary>
    private readonly record struct Shortfall(Destination Home, int NeededSlots, int FreeSlots);

    // Row drawing state for the cached virtualised-row delegate.
    private readonly Action<Row, int> drawRow;
    private int frameFrozen;
    private float frameRowHeight;
    private float frameContentHeight;

    // ---- Layout (rebuilt only when the view, buy list, filter, search or destinations change) ----

    private Layout CurrentLayout(PlacementView view, MarketAcquisitionRequestDocument document)
    {
        var current = CurrentFilter(document);
        var hash = new HashCode();
        hash.Add(view.Version);
        hash.Add(view.OwnerMatches);
        hash.Add(current);
        hash.Add(tickVersion);
        hash.Add(search, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < document.Lines.Count; index++)
        {
            var line = document.Lines[index];
            hash.Add(line.ItemId);
            hash.Add(line.HqPolicy);
            hash.Add(line.ItemName);
            hash.Add(builder.ArrivingQuantity(index));
        }
        var signature = hash.ToHashCode();
        if (layout is { } cached && cached.Signature == signature)
            return cached;
        layout = BuildLayout(view, document, current, signature);
        return layout;
    }

    private Layout BuildLayout(PlacementView view, MarketAcquisitionRequestDocument document, Filter current, int signature)
    {
        var lineHeight = ImGui.GetTextLineHeight();
        var sin = MathF.Sin(HeaderAngleDegrees * MathF.PI / 180f);
        var cos = MathF.Cos(HeaderAngleDegrees * MathF.PI / 180f);
        var labelRoom = (MaxHeaderHeight - 8f - (lineHeight * cos)) / sin;
        var columns = BuildColumns(view, labelRoom);
        var widest = columns.Select(column => Math.Min(labelRoom, ImGui.CalcTextSize(column.Label).X)).DefaultIfEmpty(0f).Max();
        var headerHeight = Math.Clamp((widest * sin) + (lineHeight * cos) + 8f, 2f * lineHeight, MaxHeaderHeight);

        // This plan: one row per item and quality on the buy list, rule or not; Arriving sums its lines.
        var planOrder = new List<RowKey>();
        var plan = new Dictionary<RowKey, (string Name, ImmutableArray<PlacementRuleKey> Rules, ulong? Arriving)>();
        for (var index = 0; index < document.Lines.Count; index++)
        {
            var line = document.Lines[index];
            if (line.ItemId == 0)
                continue;
            var rules = placement.RulesFor(line.ItemId, line.HqPolicy);
            var key = new RowKey(line.ItemId, rules.Length > 1 ? "Any" : rules[0].Quality);
            var arriving = builder.ArrivingQuantity(index) is { } units ? (ulong?)units : null;
            if (plan.TryGetValue(key, out var existing))
            {
                plan[key] = existing with { Arriving = existing.Arriving is { } sum && arriving is { } more ? sum + more : null };
                continue;
            }
            planOrder.Add(key);
            plan[key] = (line.ItemName, rules, arriving);
        }

        var rows = new List<Row>();
        var needle = search.Trim();
        if (current == Filter.ThisPlan)
        {
            foreach (var key in planOrder)
            {
                var (name, rules, arriving) = plan[key];
                rows.Add(BuildRow(view, columns, key, name, rules, arriving, showArriving: true));
            }
        }
        else
        {
            foreach (var rule in view.Rules)
                rows.Add(BuildRow(view, columns, new RowKey(rule.ItemId, rule.Quality), string.Empty, [rule], null, showArriving: false));
        }
        if (needle.Length > 0)
            rows.RemoveAll(row => !row.ItemName.Contains(needle, StringComparison.OrdinalIgnoreCase));
        foreach (var row in rows)
        {
            var key = row.Key;
            row.ToggleSelect = () => selection.SetSelected(key, !selection.IsSelected(key));
            for (var index = 0; index < columns.Length; index++)
            {
                var (cellRow, column, cell) = (row, columns[index], row.Cells[index]);
                cell.Invoke = () => QueueCellEdit(cellRow, column, cell);
            }
        }

        var options = string.Join(", ", columns.Select(column => column.PickerLabel));
        var ownerName = string.IsNullOrWhiteSpace(view.Owner.CharacterName) ? "another character" : view.Owner.CharacterName;
        return new Layout(
            signature,
            view.Characters.FirstOrDefault(character => character.IsOwner),
            columns,
            [.. rows],
            [.. rows.Select(row => row.Key)],
            columns.Select(column => column.Key.LocalContentId).Distinct().Count() > 1,
            current == Filter.ThisPlan,
            headerHeight,
            $"This plan ({planOrder.Count:N0})###DepositionThisPlan",
            $"All items ({view.Rules.Length:N0})###DepositionAllItems",
            $"Destination for Make first, Add as fallback and Remove: {options}",
            $"Quartermaster's placement is for {ownerName}.");
    }

    /// <summary>Ticked destinations per character in Quartermaster's order: retainers in game order, then Any space.</summary>
    private ImmutableArray<Column> BuildColumns(PlacementView view, float labelRoom)
    {
        var set = Ticked();
        var columns = new List<Column>();
        foreach (var character in view.Characters)
        {
            var mine = new List<(Destination Key, string Label, int? Free, bool Observed)>();
            foreach (var retainer in character.Retainers)
            {
                var key = new Destination(character.LocalContentId, retainer.RetainerId);
                if (set.Contains(key))
                    mine.Add((key, RetainerLabel(retainer), retainer.FreeSlots, retainer.StockObserved));
            }
            var any = new Destination(character.LocalContentId, 0);
            if (character.OffersAnySpace && set.Contains(any))
                mine.Add((any, "Any space", AnySpaceFree(character), false));
            for (var index = 0; index < mine.Count; index++)
                columns.Add(new Column(mine[index].Key, character, mine[index].Label, mine[index].Free, mine[index].Observed, index == 0, mine.Count));
        }

        var banded = columns.Select(column => column.Key.LocalContentId).Distinct().Count() > 1;
        var padding = ImGui.GetStyle().CellPadding.X * 2f;
        return [.. columns.Select(column =>
        {
            var picker = column.Key.IsAnySpace ? "Any space" : banded ? $"{column.Character.CharacterName} · {column.Label}" : column.Label;
            return column with
            {
                PickerLabel = picker,
                SelectableLabel = $"{picker}##{column.Key.Id}",
                HeaderText = DalamudUiLayout.ElideText(column.Label, labelRoom),
                HeaderTooltip = banded ? $"{column.Character.CharacterName} · {column.Label}" : column.Label,
                FreeText = column.FreeSlots is { } free ? $"{free:N0}" : "—",
                BandNarrow = DalamudUiLayout.ElideText(column.Character.CharacterName, (column.Span * (NarrowCellWidth + padding)) - padding),
                BandWide = DalamudUiLayout.ElideText(column.Character.CharacterName, (column.Span * (WideCellWidth + padding)) - padding),
            };
        })];
    }

    /// <summary>
    /// Any space can use every retainer assigned to the character, so its room is theirs together; unknown only when an
    /// assigned retainer's is.
    /// </summary>
    private static int? AnySpaceFree(QuartermasterPlacementCharacter character)
    {
        var total = 0;
        foreach (var retainer in character.Retainers)
        {
            if (!retainer.Assigned)
                continue;
            if (retainer.FreeSlots is not { } free)
                return null;
            total += free;
        }
        return total;
    }

    private Row BuildRow(
        PlacementView view,
        ImmutableArray<Column> columns,
        RowKey key,
        string name,
        ImmutableArray<PlacementRuleKey> rules,
        ulong? arriving,
        bool showArriving)
    {
        var items = rules.Select(rule => view.Items.GetValueOrDefault(rule)).ToArray();
        var itemName = !string.IsNullOrWhiteSpace(name)
            ? name
            : items.Select(item => item?.ItemName).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? $"Item {key.ItemId}";
        var id = $"{key.ItemId}.{key.Quality}";
        var cells = new Cell[columns.Length];
        for (var index = 0; index < columns.Length; index++)
            cells[index] = BuildCell(id, itemName, rules, items, columns[index]);

        string? arrivingText = null, subText = null;
        var muted = false;
        var amber = false;
        Destination? shortHome = null;
        if (showArriving)
        {
            if (arriving is not { } units)
            {
                arrivingText = "—";
                muted = true;
            }
            else if (units > 0)
            {
                arrivingText = $"{units:N0}";
                var stack = (ulong)StackSize(key.ItemId);
                var slots = (int)Math.Min(int.MaxValue, (units + stack - 1) / stack);
                if (FindShortfall(view, items, units, stack) is { } shortfall)
                {
                    amber = true;
                    shortHome = shortfall.Home;
                    subText = $"{shortfall.NeededSlots:N0} {Slots(shortfall.NeededSlots)} · {shortfall.FreeSlots:N0} free";
                }
                else
                {
                    subText = $"{slots:N0} {Slots(slots)}";
                }
            }
        }
        var editItems = rules.Select(rule => new QuartermasterPlacementEditItem(rule.ItemId, itemName, rule.Quality)).ToList();
        return new Row(key, id, itemName, rules, editItems, arrivingText, muted, subText, amber, shortHome, cells);

        static string Slots(int count) => count == 1 ? "slot" : "slots";
    }

    private static Cell BuildCell(
        string rowId,
        string itemName,
        ImmutableArray<PlacementRuleKey> rules,
        IReadOnlyList<QuartermasterPlacementItem?> items,
        Column column)
    {
        var ranks = new int?[items.Count];
        var next = new int[items.Count];
        var members = 0;
        var held = 0;
        var heldKnown = column.StockObserved;
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not { } item)
            {
                heldKnown = false;
                next[index] = 1;
                continue;
            }
            ranks[index] = Rank(item, column);
            if (ranks[index] is not null)
                members++;
            next[index] = HomesOn(item, column) + 1;
            if (!column.Key.IsAnySpace)
                held += HeldAt(item, column.Key);
        }
        var shownRanks = ranks.Where(rank => rank is not null).Distinct().Count();
        var mixed = members > 0 && (members < items.Count || shownRanks > 1);
        var rank = ranks.FirstOrDefault(value => value is not null);
        var badge = mixed ? string.Join("/", ranks.Select(value => value?.ToString() ?? "–")) : rank?.ToString();
        int? heldUnits = column.Key.IsAnySpace || !heldKnown ? null : held;

        var parts = new List<string> { itemName, column.PickerLabel };
        if (!column.Key.IsAnySpace)
            parts.Add(heldUnits is { } units ? $"holds {units:N0}" : "holds —");
        parts.Add(column.FreeSlots is { } free ? $"{free:N0} free slots" : "free slots unknown");
        if (mixed)
            parts.Add(string.Join(" · ", rules.Select((rule, index) => $"{QualityName(rule.Quality)} {(ranks[index] is { } value ? $"#{value}" : "–")}")));
        else if (rank is { } shown)
            parts.Add($"#{shown}");
        var tooltipBase = string.Join(" · ", parts);
        var action = members > 0
            ? "remove"
            : next.Distinct().Count() == 1
                ? $"add as #{next[0]}"
                : $"add as {string.Join(" · ", rules.Select((rule, index) => $"{QualityName(rule.Quality)} #{next[index]}"))}";

        return new Cell
        {
            Rank = rank,
            Mixed = mixed,
            FirstRank = ranks.Any(value => value == 1),
            Badge = badge,
            HeldText = column.Key.IsAnySpace ? null : heldUnits is { } count ? count == 0 ? null : $"{count:N0}" : "—",
            TooltipBase = tooltipBase,
            Tooltip = $"{tooltipBase} · {action}",
            AgentId = $"{AgentPrefix}.cell.{rowId}.{column.Key.Id}",
            AgentLabel = $"{itemName} · {column.PickerLabel}",
            AgentValue = mixed ? badge! : rank is { } value ? $"#{value}" : "Not a destination",
        };
    }

    private static string QualityName(string quality) => quality == "HqOnly" ? "HQ" : "NQ";

    private static int? Rank(QuartermasterPlacementItem item, Column column)
    {
        if (column.Character.IsOwner)
        {
            if (column.Key.IsAnySpace)
                return item.AnySpace ? item.LocalHomes.Length + 1 : null;
            var local = item.LocalHomes.IndexOf(column.Key.RetainerId);
            return local >= 0 ? local + 1 : null;
        }
        if (item.Remote is not { } remote || remote.Character.LocalContentId != column.Key.LocalContentId)
            return null;
        var index = remote.Homes.IndexOf(column.Key.RetainerId);
        return index >= 0 ? index + 1 : null;
    }

    /// <summary>Homes the rule has before a new one on this column's character would be added.</summary>
    private static int HomesOn(QuartermasterPlacementItem item, Column column) =>
        column.Character.IsOwner
            ? item.LocalHomes.Length
            : item.Remote is { } remote && remote.Character.LocalContentId == column.Key.LocalContentId ? remote.Homes.Length : 0;

    private static int HeldAt(QuartermasterPlacementItem item, Destination destination)
    {
        foreach (var entry in item.Held)
            if (entry.LocalContentId == destination.LocalContentId)
                return entry.Retainers.GetValueOrDefault(destination.RetainerId);
        return 0;
    }

    /// <summary>
    /// Whether the arrival cannot be absorbed where it would go: every rule's homes in rank order (first homes, then
    /// fallbacks, other character's homes after the owner's), then Any space (the owner's other assigned retainers).
    /// Each destination takes its free slots plus the room left in partial stacks of the item it already holds, which
    /// Quartermaster fills first. Never short while a figure it needs is unknown: the walk stops at the first unknown.
    /// </summary>
    private static Shortfall? FindShortfall(PlacementView view, IReadOnlyList<QuartermasterPlacementItem?> items, ulong arriving, ulong stack)
    {
        if (items.Any(item => item is null))
            return null;
        var rules = items.Select(item => item!).ToArray();
        var owner = view.Characters.FirstOrDefault(character => character.IsOwner);
        var homes = new List<Destination>();
        if (owner is not null)
            for (var rank = 0; rank < rules.Max(rule => rule.LocalHomes.Length); rank++)
                foreach (var rule in rules)
                    if (rank < rule.LocalHomes.Length)
                        AddOnce(homes, new Destination(owner.LocalContentId, rule.LocalHomes[rank]));
        for (var rank = 0; rank < rules.Max(rule => rule.Remote?.Homes.Length ?? 0); rank++)
            foreach (var rule in rules)
                if (rule.Remote is { Character.LocalContentId: { } remote } && rank < rule.Remote.Homes.Length)
                    AddOnce(homes, new Destination(remote, rule.Remote.Homes[rank]));
        var anySpace = owner is not null && rules.Any(rule => rule.AnySpace);
        if (anySpace)
            foreach (var retainer in owner!.Retainers)
                if (retainer.Assigned)
                    AddOnce(homes, new Destination(owner.LocalContentId, retainer.RetainerId));
        if (homes.Count == 0)
            return null;

        var remaining = arriving;
        ulong partialRoom = 0;
        var free = 0;
        foreach (var home in homes)
        {
            var retainer = view.Character(home.LocalContentId)?.Retainers.FirstOrDefault(candidate => candidate.RetainerId == home.RetainerId);
            if (retainer is not { StockObserved: true, FreeSlots: { } slots })
                return null;
            ulong partial = 0;
            foreach (var rule in rules)
            {
                var held = (ulong)Math.Max(0, HeldAt(rule, home));
                partial += (stack - (held % stack)) % stack;
            }
            var room = ((ulong)Math.Max(0, slots) * stack) + partial;
            free += Math.Max(0, slots);
            partialRoom += partial;
            if (room >= remaining)
                return null;
            remaining -= room;
        }
        var needed = (int)Math.Min(int.MaxValue, (arriving - Math.Min(arriving, partialRoom) + stack - 1) / stack);
        var first = homes[0];
        // With no ranked home, the arrival's only destination is Any space: tint that column.
        if (rules.All(rule => rule.LocalHomes.IsDefaultOrEmpty && rule.Remote is not { Homes.IsDefaultOrEmpty: false }) && owner is not null)
            first = new Destination(owner.LocalContentId, 0);
        return new Shortfall(first, needed, free);

        static void AddOnce(List<Destination> list, Destination destination)
        {
            if (!list.Contains(destination))
                list.Add(destination);
        }
    }

    // ---- Drawing ----------------------------------------------------------------------------------

    private void DrawMatrix(Layout current)
    {
        if (current.Rows.IsDefaultOrEmpty)
        {
            ImGui.TextColored(MainWindow.ColMuted, "No matching items.");
            return;
        }
        var style = ImGui.GetStyle();
        var lineHeight = ImGui.GetTextLineHeight();
        var wide = ImGui.GetContentRegionAvail().X >= WideMatrixWidth;
        var nameWidth = wide ? 280f : 220f;
        var cellWidth = wide ? WideCellWidth : NarrowCellWidth;
        var arrivingWidth = Math.Max(ImGui.CalcTextSize("Arriving").X, ImGui.CalcTextSize("999 slots · 99 free").X);
        var frozen = current.ThisPlan ? 2 : 1;
        var headerRows = current.Banded ? 3 : 2;
        var flags = ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersOuter;
        if (!ImGui.BeginTable("DepositionMatrix", frozen + current.Columns.Length, flags, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
            return;
        ImGui.TableSetupScrollFreeze(frozen, headerRows);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, nameWidth);
        if (current.ThisPlan)
            ImGui.TableSetupColumn("Arriving", ImGuiTableColumnFlags.WidthFixed, arrivingWidth);
        foreach (var column in current.Columns)
            ImGui.TableSetupColumn(column.SelectableLabel, ImGuiTableColumnFlags.WidthFixed, cellWidth);

        if (current.Banded)
            DrawBandRow(current, frozen, cellWidth, wide);
        DrawHeaderRow(current, frozen, cellWidth);
        DrawFreeSlotsRow(current, frozen);

        frameFrozen = frozen;
        frameContentHeight = current.ThisPlan ? (lineHeight * 2f) + style.ItemSpacing.Y : ImGui.GetFrameHeight();
        frameRowHeight = frameContentHeight + (style.CellPadding.Y * 2f);
        DalamudVirtualizedRows.Draw(current.Rows, drawRow, frameRowHeight);
        DalamudTableSelectionRenderer.EndRows(selection);
        ImGui.EndTable();
    }

    /// <summary>Character names over their columns, when more than one character has columns.</summary>
    private static void DrawBandRow(Layout current, int frozen, float cellWidth, bool wide)
    {
        var padding = ImGui.GetStyle().CellPadding;
        var lineHeight = ImGui.GetTextLineHeight();
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(frozen - 1);
        var frozenRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X + padding.X;
        var windowRight = ImGui.GetWindowPos().X + ImGui.GetWindowWidth();
        var drawList = ImGui.GetWindowDrawList();
        for (var index = 0; index < current.Columns.Length; index++)
        {
            var column = current.Columns[index];
            ImGui.TableSetColumnIndex(frozen + index);
            if (column.FirstOfCharacter)
            {
                var min = ImGui.GetCursorScreenPos();
                var span = (column.Span * (cellWidth + (padding.X * 2f))) - (padding.X * 2f);
                drawList.PushClipRect(new Vector2(Math.Max(frozenRight, min.X), min.Y - padding.Y),
                    new Vector2(Math.Min(windowRight, min.X + span), min.Y + lineHeight + padding.Y), false);
                drawList.AddText(min, ImGui.GetColorU32(column.Character.IsOwner ? CurrentCharacterTint : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]),
                    wide ? column.BandWide : column.BandNarrow);
                drawList.AddLine(new Vector2(min.X, min.Y + lineHeight + 1f), new Vector2(min.X + span, min.Y + lineHeight + 1f),
                    ImGui.GetColorU32(AnySeparatorColor));
                drawList.PopClipRect();
            }
            ImGui.Dummy(new Vector2(1f, lineHeight));
        }
    }

    private static void DrawHeaderRow(Layout current, int frozen, float cellWidth)
    {
        var padding = ImGui.GetStyle().CellPadding;
        var lineHeight = ImGui.GetTextLineHeight();
        var headerHeight = current.HeaderHeight;
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, headerHeight);
        ImGui.TableSetColumnIndex(0);
        var top = ImGui.GetCursorScreenPos().Y;
        var bottom = top + headerHeight - (padding.Y * 2f);
        DrawAtBottom("Item", bottom);
        if (current.ThisPlan)
        {
            ImGui.TableSetColumnIndex(1);
            DrawAtBottom("Arriving", bottom);
        }
        ImGui.TableSetColumnIndex(frozen - 1);
        var frozenRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X + padding.X;
        var windowRight = ImGui.GetWindowPos().X + ImGui.GetWindowWidth();
        var drawList = ImGui.GetWindowDrawList();
        var textColor = ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        var radians = -HeaderAngleDegrees * MathF.PI / 180f;
        for (var index = 0; index < current.Columns.Length; index++)
        {
            var column = current.Columns[index];
            ImGui.TableSetColumnIndex(frozen + index);
            var min = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(cellWidth, headerHeight - (padding.Y * 2f)));
            if (column.Key.IsAnySpace)
                DrawAnySeparator(min, headerHeight);
            // Angled labels run up and right over later columns, so they may leave their cell but never cover the frozen ones.
            drawList.PushClipRect(new Vector2(frozenRight, top - padding.Y), new Vector2(windowRight, top + headerHeight), false);
            DalamudDrawListPrimitives.AddRotatedText(drawList, new Vector2(min.X + (cellWidth * .5f) + (lineHeight * .35f), bottom), radians,
                textColor, column.HeaderText, lineHeight);
            drawList.PopClipRect();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(column.HeaderTooltip);
        }
    }

    private static void DrawAtBottom(string text, float bottom)
    {
        var position = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(position.X, bottom - ImGui.GetTextLineHeight()));
        ImGui.TextUnformatted(text);
    }

    private static void DrawFreeSlotsRow(Layout current, int frozen)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(0);
        ImGui.TextColored(MainWindow.ColMuted, "Free slots");
        var rowHeight = ImGui.GetTextLineHeight() + (ImGui.GetStyle().CellPadding.Y * 2f);
        for (var index = 0; index < current.Columns.Length; index++)
        {
            var column = current.Columns[index];
            ImGui.TableSetColumnIndex(frozen + index);
            if (column.Key.IsAnySpace)
                DrawAnySeparator(ImGui.GetCursorScreenPos(), rowHeight);
            DrawRightAligned(column.FreeText, MainWindow.ColMuted);
        }
    }

    private void DrawRow(Row row, int index)
    {
        if (layout is not { } current)
            return;
        ImGui.TableNextRow(ImGuiTableRowFlags.None, frameRowHeight);
        ImGui.PushID(row.Id);
        var pending = IsPending(row);

        ImGui.TableSetColumnIndex(0);
        var start = ImGui.GetCursorScreenPos();
        DalamudTableSelectionRenderer.DrawRow(selection, current.Keys, index, "##Select", new Vector2(ImGui.GetContentRegionAvail().X, frameContentHeight));
        var selected = selection.IsSelected(row.Key);
        reviewRegistry.RegisterLastItem(row.SelectAgentId, row.SelectAgentLabel, AgentBridgeUiControlKind.Toggle, true, selected,
            selected ? "Selected" : "Not selected", row.ToggleSelect);
        var nameRoom = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorScreenPos(start);
        var shown = DalamudUiLayout.ElideText(row.ItemName, nameRoom);
        if (pending)
            ImGui.TextColored(MainWindow.ColMuted, shown);
        else
            ImGui.TextUnformatted(shown);
        if (!ReferenceEquals(shown, row.ItemName) && shown.Length != row.ItemName.Length &&
            ImGui.IsMouseHoveringRect(start, start + new Vector2(nameRoom, frameContentHeight)))
            ImGui.SetTooltip(row.ItemName);

        if (current.ThisPlan)
        {
            ImGui.TableSetColumnIndex(1);
            if (row.ArrivingText is { } arriving)
                DrawRightAligned(arriving, row.ArrivingMuted ? MainWindow.ColMuted : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            if (row.SubText is { } sub)
                DrawRightAligned(sub, row.SubAmber ? AmberColor : MainWindow.ColMuted);
        }

        var blocked = editBlocked ?? (pending ? "Updating" : null);
        for (var column = 0; column < current.Columns.Length; column++)
        {
            ImGui.TableSetColumnIndex(frameFrozen + column);
            ImGui.PushID(column);
            DrawCell(row, current.Columns[column], row.Cells[column], blocked, pending);
            ImGui.PopID();
        }
        ImGui.PopID();
    }

    private static void DrawRightAligned(string text, Vector4 color)
    {
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - width));
        ImGui.TextColored(color, text);
    }

    private void DrawCell(Row row, Column column, Cell cell, string? blocked, bool pending)
    {
        var min = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(1f, ImGui.GetContentRegionAvail().X), frameContentHeight);
        var clicked = ImGui.InvisibleButton("##Cell", size);
        var hovered = ImGui.IsItemHovered();
        var enabled = blocked is null;
        var drawList = ImGui.GetWindowDrawList();
        var padding = ImGui.GetStyle().CellPadding;
        var cellMin = min - padding;
        var cellMax = min + size + padding;
        if (row.ShortHome == column.Key)
            drawList.AddRectFilled(cellMin, cellMax, ImGui.GetColorU32(AmberColor with { W = .22f }));
        if (hovered && enabled)
            drawList.AddRectFilled(cellMin, cellMax, ImGui.GetColorU32(HoverCellColor));
        if (column.Key.IsAnySpace)
            DrawAnySeparator(min, frameRowHeight);

        var lineHeight = ImGui.GetTextLineHeight();
        if (cell.Badge is { } text)
        {
            var fontSize = ImGui.GetFontSize() * .85f;
            var textSize = ImGui.CalcTextSize(text) * .85f;
            var badge = new Vector2(Math.Max(textSize.X + 6f, fontSize + 2f), fontSize + 2f);
            var position = new Vector2(min.X, min.Y + Math.Max(0f, (lineHeight - badge.Y) * .5f));
            var fill = !pending && cell.FirstRank ? FirstRankColor : RankColor;
            drawList.AddRectFilled(position, position + badge, ImGui.GetColorU32(fill), 3f);
            if (cell.Mixed)
                drawList.AddRect(position, position + badge, ImGui.GetColorU32(MainWindow.ColMuted), 3f);
            drawList.AddText(ImGui.GetFont(), fontSize, position + new Vector2((badge.X - textSize.X) * .5f, 1f),
                ImGui.GetColorU32(pending ? MainWindow.ColMuted : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]), text);
        }
        if (cell.HeldText is { } heldText)
        {
            var width = ImGui.CalcTextSize(heldText).X;
            drawList.AddText(new Vector2(min.X + size.X - width, min.Y), ImGui.GetColorU32(MainWindow.ColMuted), heldText);
        }

        if (hovered)
            ImGui.SetTooltip(enabled ? cell.Tooltip : $"{cell.TooltipBase} · {blocked}");
        if (clicked && enabled)
            cell.Invoke();
        reviewRegistry.RegisterLastItem(cell.AgentId, cell.AgentLabel, AgentBridgeUiControlKind.Toggle, enabled, cell.Rank is not null,
            cell.AgentValue, cell.Invoke);
    }

    /// <summary>The 2px rule that sets Any space apart from the retainer columns before it.</summary>
    private static void DrawAnySeparator(Vector2 contentMin, float rowHeight)
    {
        var padding = ImGui.GetStyle().CellPadding;
        var x = contentMin.X - padding.X - 1f;
        ImGui.GetWindowDrawList().AddLine(new Vector2(x, contentMin.Y - padding.Y), new Vector2(x, contentMin.Y - padding.Y + rowHeight),
            ImGui.GetColorU32(AnySeparatorColor), 2f);
    }
}
