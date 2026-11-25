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
    /// One cell. Rank is the first rule's global rank there (1 fills first across every character, 2+ after it); Mixed
    /// when the row's rules disagree on membership, rank or cap, and Badge then shows each ("1/2", "1/–"). HeldText is
    /// the plain held count of an uncapped home; a capped one shows held against cap instead (CapText, or CapCompact
    /// where that does not fit).
    /// </summary>
    private sealed class Cell
    {
        public int? Rank { get; init; }
        public bool Mixed { get; init; }
        public bool FirstRank { get; init; }
        public string? Badge { get; init; }
        public string? HeldText { get; init; }
        public string? CapText { get; init; }
        public string? CapCompact { get; init; }
        /// <summary>Every rule of the row has this home.</summary>
        public bool AllAssigned { get; init; }
        public bool AnyAssigned { get; init; }
        /// <summary>Every rule of the row ranks this home first.</summary>
        public bool AllFirst { get; init; }
        public bool AnyCap { get; init; }
        /// <summary>The cap every rule agrees on here; null when uncapped or the rules disagree.</summary>
        public int? SharedCap { get; init; }
        public required string Header { get; init; }
        public required string Tooltip { get; init; }
        public required string TooltipBase { get; init; }
        public required string AgentId { get; init; }
        public required string AgentLabel { get; init; }
        public required string AgentValue { get; init; }
        public Action Invoke { get; set; } = static () => { };
    }

    /// <summary>
    /// A matrix row. ArrivingText null: nothing arriving (blank). SubText is the slots line, amber when the arrival
    /// cannot be absorbed by its destinations (out of slots, or held back by caps); ShortHome is then the home to tint.
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

    /// <summary>
    /// What a shortfall check found: the home to tint, the slots still needed after partial stacks, the free slots it
    /// had, and the units left in the bags. AtCap: the homes had the slots, their caps held the arrival back.
    /// </summary>
    private readonly record struct Shortfall(Destination Home, int NeededSlots, int FreeSlots, bool AtCap, ulong Left);

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
        hash.Add(placement.HasGlobalChain);
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
        var capHint = placement.HasGlobalChain;
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
                rows.Add(BuildRow(view, columns, key, name, rules, arriving, showArriving: true, capHint));
            }
        }
        else
        {
            foreach (var rule in view.Rules)
                rows.Add(BuildRow(view, columns, new RowKey(rule.ItemId, rule.Quality), string.Empty, [rule], null, showArriving: false, capHint));
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
            $"Destination for Make first, Add as fallback, Remove and Set cap: {options}",
            $"Quartermaster's placement is for {ownerName}.");
    }

    /// <summary>Ticked destinations per character in Quartermaster's order: retainers in game order.</summary>
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
            for (var index = 0; index < mine.Count; index++)
                columns.Add(new Column(mine[index].Key, character, mine[index].Label, mine[index].Free, mine[index].Observed, index == 0, mine.Count));
        }

        var banded = columns.Select(column => column.Key.LocalContentId).Distinct().Count() > 1;
        var padding = ImGui.GetStyle().CellPadding.X * 2f;
        return [.. columns.Select(column =>
        {
            var picker = banded ? $"{column.Character.CharacterName} · {column.Label}" : column.Label;
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

    private Row BuildRow(
        PlacementView view,
        ImmutableArray<Column> columns,
        RowKey key,
        string name,
        ImmutableArray<PlacementRuleKey> rules,
        ulong? arriving,
        bool showArriving,
        bool capHint)
    {
        var items = rules.Select(rule => view.Items.GetValueOrDefault(rule)).ToArray();
        var itemName = !string.IsNullOrWhiteSpace(name)
            ? name
            : items.Select(item => item?.ItemName).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? $"Item {key.ItemId}";
        var id = $"{key.ItemId}.{key.Quality}";
        var cells = new Cell[columns.Length];
        for (var index = 0; index < columns.Length; index++)
            cells[index] = BuildCell(id, itemName, rules, items, columns[index], capHint);

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
                    subText = shortfall.AtCap
                        ? $"At cap · {shortfall.Left:N0} left"
                        : $"{shortfall.NeededSlots:N0} {Slots(shortfall.NeededSlots)} · {shortfall.FreeSlots:N0} free";
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
        Column column,
        bool capHint)
    {
        var homes = new QuartermasterPlacementHome?[items.Count];
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
            homes[index] = Home(item, column.Key);
            if (homes[index] is not null)
                members++;
            // The chain is global: a new home on any character goes after every existing one.
            next[index] = item.Homes.Length + 1;
            held += HeldAt(item, column.Key);
        }
        var ranks = homes.Select(home => home?.Rank).ToArray();
        var caps = homes.Where(home => home is not null).Select(home => home!.MaxUnits).Distinct().ToArray();
        var shownRanks = ranks.Where(rank => rank is not null).Distinct().Count();
        var mixed = members > 0 && (members < items.Count || shownRanks > 1 || caps.Length > 1);
        var rank = ranks.FirstOrDefault(value => value is not null);
        var badge = mixed ? string.Join("/", ranks.Select(value => value?.ToString() ?? "–")) : rank?.ToString();
        int? heldUnits = heldKnown ? held : null;
        var anyCap = homes.Any(home => home?.MaxUnits is not null);
        var sharedCap = caps.Length == 1 ? caps[0] : null;

        var header = $"{itemName} · {column.Character.CharacterName} · {column.Label}";
        var parts = new List<string> { header };
        var holds = heldUnits is { } units ? $"holds {units:N0}" : "holds —";
        if (sharedCap is { } shared)
            holds += heldUnits is { } known
                ? $" / cap {shared:N0} ({Math.Max(0L, (long)shared - known):N0} room)"
                : $" / cap {shared:N0}";
        parts.Add(holds);
        parts.Add(column.FreeSlots is { } free ? $"{free:N0} free slots" : "free slots unknown");
        if (mixed)
            parts.Add(string.Join(" · ", rules.Select((rule, index) => $"{QualityName(rule.Quality)} {RuleState(homes[index])}")));
        else if (rank is { } shown)
            parts.Add($"#{shown}");
        var tooltipBase = string.Join(" · ", parts);
        var action = members > 0
            ? "click to remove"
            : next.Distinct().Count() == 1
                ? $"click to add as #{next[0]}"
                : $"click to add as {string.Join(" · ", rules.Select((rule, index) => $"{QualityName(rule.Quality)} #{next[index]}"))}";
        if (capHint)
            action += " · right-click to set cap";

        string? heldText = null, capText = null, capCompact = null;
        if (sharedCap is { } cap)
        {
            (capText, capCompact) = heldUnits switch
            {
                null => ($"—/{cap:N0}", $"—/{Compact(cap)}"),
                { } count => ($"{count:N0}/{cap:N0}", $"{Compact(count)}/{Compact(cap)}"),
            };
        }
        else
        {
            heldText = heldUnits is { } count ? count == 0 ? null : $"{count:N0}" : "—";
        }

        return new Cell
        {
            Rank = rank,
            Mixed = mixed,
            FirstRank = ranks.Any(value => value == 1),
            Badge = badge,
            HeldText = heldText,
            CapText = capText,
            CapCompact = capCompact,
            AllAssigned = members == items.Count,
            AnyAssigned = members > 0,
            AllFirst = members == items.Count && ranks.All(value => value == 1),
            AnyCap = anyCap,
            SharedCap = sharedCap,
            Header = header,
            TooltipBase = tooltipBase,
            Tooltip = $"{tooltipBase} · {action}",
            AgentId = $"{AgentPrefix}.cell.{rowId}.{column.Key.Id}",
            AgentLabel = $"{itemName} · {column.PickerLabel}",
            AgentValue = mixed ? badge!
                : rank is { } value ? sharedCap is { } limit ? $"#{value} · cap {limit:N0}" : $"#{value}"
                : "Not a destination",
        };

        static string RuleState(QuartermasterPlacementHome? home) =>
            home is null ? "–" : home.MaxUnits is { } cap ? $"#{home.Rank} cap {cap:N0}" : $"#{home.Rank} uncapped";
    }

    private static string QualityName(string quality) => quality == "HqOnly" ? "HQ" : "NQ";

    /// <summary>A count in at most four characters, rounded down so a home short of its cap never reads as at it: 9.2k, 10k, 1.2M.</summary>
    private static string Compact(long value)
    {
        if (value < 1_000)
            return value.ToString("N0");
        if (value < 10_000)
            return $"{Math.Floor(value / 100d) / 10d:0.#}k";
        if (value < 1_000_000)
            return $"{value / 1_000:N0}k";
        return value < 10_000_000 ? $"{Math.Floor(value / 100_000d) / 10d:0.#}M" : $"{value / 1_000_000:N0}M";
    }

    /// <summary>The rule's home at this destination, matched by character and retainer; null when it is not one.</summary>
    private static QuartermasterPlacementHome? Home(QuartermasterPlacementItem item, Destination destination)
    {
        foreach (var home in item.Homes)
            if (home.Is(destination.LocalContentId, destination.RetainerId))
                return home;
        return null;
    }

    private static int HeldAt(QuartermasterPlacementItem item, Destination destination)
    {
        foreach (var entry in item.Held)
            if (entry.LocalContentId == destination.LocalContentId)
                return entry.Retainers.GetValueOrDefault(destination.RetainerId);
        return 0;
    }

    /// <summary>
    /// Whether the arrival cannot be absorbed where it would go, with the math Quartermaster plans by: the global home
    /// chain in rank order across characters (an either-quality row walks both rules' rank 1, then rank 2, ...), each
    /// home taking min(physical room, cap room). Physical room is its free slots plus the room left in partial stacks
    /// of the item it already holds; cap room is MaxUnits less what the rule holds there (unlimited when uncapped).
    /// Never short while a figure it needs is unknown: the walk stops at the first unobserved home. Short with enough
    /// slots overall means caps held the arrival back (AtCap, first binding cap tinted); otherwise it is out of slots.
    /// </summary>
    private static Shortfall? FindShortfall(PlacementView view, IReadOnlyList<QuartermasterPlacementItem?> items, ulong arriving, ulong stack)
    {
        if (items.Any(item => item is null))
            return null;
        var rules = items.Select(item => item!).ToArray();
        var homes = new List<Destination>();
        for (var rank = 0; rank < rules.Max(rule => rule.Homes.Length); rank++)
            foreach (var rule in rules)
                if (rank < rule.Homes.Length && rule.Homes[rank].Character.LocalContentId is { } character)
                    AddOnce(homes, new Destination(character, rule.Homes[rank].RetainerId));
        if (homes.Count == 0)
            return null;

        var remaining = arriving;
        ulong partialRoom = 0, physicalTotal = 0;
        var free = 0;
        Destination? capped = null;
        foreach (var home in homes)
        {
            var retainer = view.Character(home.LocalContentId)?.Retainers.FirstOrDefault(candidate => candidate.RetainerId == home.RetainerId);
            if (retainer is not { StockObserved: true, FreeSlots: { } slots })
                return null;
            ulong partial = 0, capRoom = 0;
            var uncapped = false;
            foreach (var rule in rules)
            {
                var held = (ulong)Math.Max(0, HeldAt(rule, home));
                partial += (stack - (held % stack)) % stack;
                if (Home(rule, home) is not { } entry)
                    continue;
                if (entry.MaxUnits is { } cap)
                    capRoom += (ulong)Math.Max(0L, cap - (long)held);
                else
                    uncapped = true;
            }
            var physical = ((ulong)Math.Max(0, slots) * stack) + partial;
            var room = uncapped ? physical : Math.Min(physical, capRoom);
            free += Math.Max(0, slots);
            partialRoom += partial;
            physicalTotal += physical;
            if (room >= remaining)
                return null;
            if (room < physical)
                capped ??= home;
            remaining -= room;
        }
        if (capped is { } atCap && physicalTotal >= arriving)
            return new Shortfall(atCap, 0, free, AtCap: true, remaining);
        var needed = (int)Math.Min(int.MaxValue, (arriving - Math.Min(arriving, partialRoom) + stack - 1) / stack);
        return new Shortfall(homes[0], needed, free, AtCap: false, remaining);

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
        var arrivingWidth = Math.Max(ImGui.CalcTextSize("Arriving").X,
            Math.Max(ImGui.CalcTextSize("999 slots · 99 free").X, ImGui.CalcTextSize("At cap · 99,999 left").X));
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
                    ImGui.GetColorU32(BandRuleColor));
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
        for (var index = 0; index < current.Columns.Length; index++)
        {
            var column = current.Columns[index];
            ImGui.TableSetColumnIndex(frozen + index);
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
            DrawCell(row, current.Columns[column], row.Cells[column], blocked ?? GlobalChainBlocked(current.Columns[column]), pending);
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
        var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
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

        var lineHeight = ImGui.GetTextLineHeight();
        var badgeWidth = 0f;
        if (cell.Badge is { } text)
        {
            var fontSize = ImGui.GetFontSize() * .85f;
            var textSize = ImGui.CalcTextSize(text) * .85f;
            var badge = new Vector2(Math.Max(textSize.X + 6f, fontSize + 2f), fontSize + 2f);
            badgeWidth = badge.X + 3f;
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
        else if (cell.CapText is { } capText)
        {
            // Held against cap, right-aligned beside the badge: exact when it fits, else compact, else compact and smaller.
            var room = size.X - badgeWidth;
            var shown = ImGui.CalcTextSize(capText).X <= room ? capText : cell.CapCompact!;
            var scale = ImGui.CalcTextSize(shown).X <= room ? 1f : .85f;
            var width = ImGui.CalcTextSize(shown).X * scale;
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale,
                new Vector2(min.X + size.X - width, min.Y + ((1f - scale) * lineHeight * .5f)),
                ImGui.GetColorU32(pending ? MainWindow.ColMuted : CapColor), shown);
        }

        if (hovered)
            ImGui.SetTooltip(enabled ? cell.Tooltip : $"{cell.TooltipBase} · {blocked}");
        if (clicked && enabled)
            cell.Invoke();
        if (rightClicked && enabled && placement.HasGlobalChain)
            OpenCapEditor(row, column, cell, cellMin + new Vector2(0f, cellMax.Y - cellMin.Y));
        reviewRegistry.RegisterLastItem(cell.AgentId, cell.AgentLabel, AgentBridgeUiControlKind.Toggle, enabled, cell.Rank is not null,
            cell.AgentValue, cell.Invoke);
    }
}
