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

    /// <summary>A destination column; FreeSlots null while unknown. Span counts the character's columns (band row).</summary>
    private sealed record Column(
        Destination Key,
        QuartermasterPlacementCharacter Character,
        string Label,
        int? FreeSlots,
        bool StockObserved,
        bool FirstOfCharacter,
        int Span);

    /// <summary>
    /// Rank: 1 for the first home, 2+ for fallbacks, null when not a home. Mixed: an either-quality row whose two rules
    /// disagree on this destination. Held: units there (null: unknown; Any space has none). NextRank: rank an add takes.
    /// </summary>
    private readonly record struct Cell(int? Rank, bool Mixed, int? Held, int NextRank);

    /// <summary>
    /// A matrix row. Arriving is null when unknown; Slots is the stack count it needs. ShortHome/ShortFree name the first
    /// home those slots overflow with nothing behind it to absorb them.
    /// </summary>
    private sealed record Row(
        RowKey Key,
        string ItemName,
        ImmutableArray<PlacementRuleKey> Rules,
        ulong? Arriving,
        int? Slots,
        Destination? ShortHome,
        int? ShortFree,
        ImmutableArray<Cell> Cells);

    private sealed record Layout(
        int Signature,
        QuartermasterPlacementCharacter? Owner,
        ImmutableArray<Column> Columns,
        ImmutableArray<Row> Rows,
        ImmutableArray<RowKey> Keys,
        bool Banded,
        bool ThisPlan,
        int PlanCount,
        int AllCount);

    // ---- Layout (rebuilt only when the view, buy list, filter, search or destinations change) ----

    private Layout CurrentLayout(PlacementView view, MarketAcquisitionRequestDocument document)
    {
        var current = CurrentFilter(document);
        var hash = new HashCode();
        hash.Add(view.Version);
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
        var columns = BuildColumns(view);
        var banded = columns.Select(column => column.Key.LocalContentId).Distinct().Count() > 1;

        // This plan: one row per item and quality on the buy list, rule or not; Arriving sums its lines.
        var planOrder = new List<RowKey>();
        var plan = new Dictionary<RowKey, (string Name, ImmutableArray<PlacementRuleKey> Rules, ulong? Arriving)>();
        for (var index = 0; index < document.Lines.Count; index++)
        {
            var line = document.Lines[index];
            if (line.ItemId == 0)
                continue;
            var rules = MarketAcquisitionPlacementService.RulesFor(line.ItemId, line.HqPolicy);
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
        if (!string.IsNullOrWhiteSpace(search))
            rows.RemoveAll(row => !row.ItemName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

        return new Layout(
            signature,
            view.Characters.FirstOrDefault(character => character.IsOwner),
            columns,
            [.. rows],
            [.. rows.Select(row => row.Key)],
            banded,
            current == Filter.ThisPlan,
            planOrder.Count,
            view.Rules.Length);
    }

    /// <summary>Ticked destinations per character in Quartermaster's order: retainers in game order, then Any space.</summary>
    private ImmutableArray<Column> BuildColumns(PlacementView view)
    {
        var set = Ticked();
        var columns = ImmutableArray.CreateBuilder<Column>();
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
        return columns.ToImmutable();
    }

    /// <summary>Any space can use every retainer of the character, so its room is theirs together; unknown if any is.</summary>
    private static int? AnySpaceFree(QuartermasterPlacementCharacter character) =>
        character.Retainers.IsDefaultOrEmpty || character.Retainers.Any(retainer => retainer.FreeSlots is null)
            ? null
            : character.Retainers.Sum(retainer => retainer.FreeSlots!.Value);

    private Row BuildRow(
        PlacementView view,
        IReadOnlyList<Column> columns,
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
        var cells = columns.Select(column => BuildCell(items, column)).ToImmutableArray();
        int? slots = showArriving && arriving is > 0 ? (int)Math.Min(int.MaxValue, (arriving.Value + (ulong)StackSize(key.ItemId) - 1) / (ulong)StackSize(key.ItemId)) : null;
        var (shortHome, shortFree) = slots is { } needed ? Shortfall(view, items, needed) : (null, null);
        return new Row(key, itemName, rules, showArriving ? arriving : null, slots, shortHome, shortFree, cells);
    }

    private static Cell BuildCell(IReadOnlyList<QuartermasterPlacementItem?> items, Column column)
    {
        int? rank = null;
        var members = 0;
        var held = 0;
        var heldKnown = column.StockObserved;
        var nextRank = 1;
        foreach (var item in items)
        {
            if (item is null)
            {
                heldKnown = false;
                continue;
            }
            if (Rank(item, column) is { } itemRank)
            {
                members++;
                rank ??= itemRank;
            }
            nextRank = Math.Max(nextRank, HomesOn(item, column.Key.LocalContentId, column.Character.IsOwner) + 1);
            if (!column.Key.IsAnySpace)
                held += item.Held.FirstOrDefault(entry => entry.LocalContentId == column.Key.LocalContentId)?.Retainers
                    .GetValueOrDefault(column.Key.RetainerId) ?? 0;
        }
        return new Cell(rank, members > 0 && members < items.Count, column.Key.IsAnySpace || !heldKnown ? null : held, nextRank);
    }

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

    private static int HomesOn(QuartermasterPlacementItem item, ulong localContentId, bool owner) =>
        owner
            ? item.LocalHomes.Length + (item.AnySpace ? 1 : 0)
            : item.Remote is { } remote && remote.Character.LocalContentId == localContentId ? remote.Homes.Length : 0;

    /// <summary>
    /// The first home, when the slots arriving overflow it and no fallback or Any space can take the rest. Unknown free
    /// space anywhere behind the first home is not called short.
    /// </summary>
    private static (Destination? Home, int? Free) Shortfall(PlacementView view, IReadOnlyList<QuartermasterPlacementItem?> items, int slots)
    {
        var item = items.FirstOrDefault(candidate => candidate is not null &&
                                                     (!candidate.LocalHomes.IsDefaultOrEmpty || candidate.AnySpace || candidate.Remote is { Homes.IsDefaultOrEmpty: false }));
        if (item is null || item.AnySpace)
            return (null, null);
        var owner = view.Characters.FirstOrDefault(character => character.IsOwner);
        var homes = new List<Destination>();
        if (owner is not null)
            homes.AddRange(item.LocalHomes.Select(id => new Destination(owner.LocalContentId, id)));
        if (item.Remote is { Character.LocalContentId: { } remote })
            homes.AddRange(item.Remote.Homes.Select(id => new Destination(remote, id)));
        if (homes.Count == 0)
            return (null, null);
        var free = homes.Select(home => view.Character(home.LocalContentId)?.Retainers
            .FirstOrDefault(retainer => retainer.RetainerId == home.RetainerId)?.FreeSlots).ToList();
        if (free[0] is not { } first || first >= slots || free.Any(value => value is null) || free.Sum(value => value!.Value) >= slots)
            return (null, null);
        return (homes[0], first);
    }

    // ---- Drawing ----------------------------------------------------------------------------------

    private void DrawMatrix(Layout current, bool canEdit)
    {
        if (current.Rows.IsDefaultOrEmpty)
        {
            ImGui.TextColored(MainWindow.ColMuted, "No matching items.");
            return;
        }
        var style = ImGui.GetStyle();
        var lineHeight = ImGui.GetTextLineHeight();
        var wide = ImGui.GetContentRegionAvail().X >= 1300f;
        var nameWidth = wide ? 280f : 220f;
        var cellWidth = wide ? 64f : 50f;
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
            ImGui.TableSetupColumn($"##{column.Key.Id}", ImGuiTableColumnFlags.WidthFixed, cellWidth);

        var sin = MathF.Sin(HeaderAngleDegrees * MathF.PI / 180f);
        var cos = MathF.Cos(HeaderAngleDegrees * MathF.PI / 180f);
        var labelRoom = (MaxHeaderHeight - 8f - (lineHeight * cos)) / sin;
        var widest = current.Columns.Select(column => Math.Min(labelRoom, ImGui.CalcTextSize(column.Label).X)).DefaultIfEmpty(0f).Max();
        var headerHeight = Math.Clamp((widest * sin) + (lineHeight * cos) + 8f, 2f * lineHeight, MaxHeaderHeight);

        if (current.Banded)
            DrawBandRow(current, frozen, cellWidth);
        DrawHeaderRow(current, frozen, cellWidth, headerHeight, labelRoom);
        DrawFreeSlotsRow(current, frozen);

        var contentHeight = current.ThisPlan ? (lineHeight * 2f) + style.ItemSpacing.Y : ImGui.GetFrameHeight();
        var rowHeight = contentHeight + (style.CellPadding.Y * 2f);
        DalamudVirtualizedRows.Draw(current.Rows, (row, index) => DrawRow(current, row, index, frozen, rowHeight, contentHeight, canEdit), rowHeight);
        DalamudTableSelectionRenderer.EndRows(selection);
        ImGui.EndTable();
    }

    /// <summary>Character names over their columns, when more than one character has columns.</summary>
    private void DrawBandRow(Layout current, int frozen, float cellWidth)
    {
        var padding = ImGui.GetStyle().CellPadding;
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(frozen - 1);
        var frozenRight = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X + padding.X;
        var windowRight = ImGui.GetWindowPos().X + ImGui.GetWindowWidth();
        var drawList = ImGui.GetWindowDrawList();
        for (var index = 0; index < current.Columns.Length; index++)
        {
            var column = current.Columns[index];
            ImGui.TableSetColumnIndex(frozen + index);
            if (!column.FirstOfCharacter)
            {
                ImGui.Dummy(new Vector2(1f, ImGui.GetTextLineHeight()));
                continue;
            }
            var min = ImGui.GetCursorScreenPos();
            var span = (column.Span * (cellWidth + (padding.X * 2f))) - (padding.X * 2f);
            var name = DalamudUiLayout.ElideText(column.Character.CharacterName, span);
            drawList.PushClipRect(new Vector2(Math.Max(frozenRight, min.X), min.Y - padding.Y),
                new Vector2(Math.Min(windowRight, min.X + span), min.Y + ImGui.GetTextLineHeight() + padding.Y), false);
            drawList.AddText(min, ImGui.GetColorU32(column.Character.IsOwner ? CurrentCharacterTint : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]), name);
            drawList.AddLine(new Vector2(min.X, min.Y + ImGui.GetTextLineHeight() + 1f), new Vector2(min.X + span, min.Y + ImGui.GetTextLineHeight() + 1f),
                ImGui.GetColorU32(AnySeparatorColor));
            drawList.PopClipRect();
            ImGui.Dummy(new Vector2(1f, ImGui.GetTextLineHeight()));
        }
    }

    private void DrawHeaderRow(Layout current, int frozen, float cellWidth, float headerHeight, float labelRoom)
    {
        var padding = ImGui.GetStyle().CellPadding;
        var lineHeight = ImGui.GetTextLineHeight();
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
            var label = DalamudUiLayout.ElideText(column.Label, labelRoom);
            // Angled labels run up and right over later columns, so they may leave their cell but never cover the frozen ones.
            drawList.PushClipRect(new Vector2(frozenRight, top - padding.Y), new Vector2(windowRight, top + headerHeight), false);
            DalamudDrawListPrimitives.AddRotatedText(drawList, new Vector2(min.X + (cellWidth * .5f) + (lineHeight * .35f), bottom), radians,
                textColor, label, lineHeight);
            drawList.PopClipRect();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(current.Banded ? $"{column.Character.CharacterName} · {column.Label}" : column.Label);
        }
    }

    private static void DrawAtBottom(string text, float bottom)
    {
        var position = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(position.X, bottom - ImGui.GetTextLineHeight()));
        ImGui.TextUnformatted(text);
    }

    private void DrawFreeSlotsRow(Layout current, int frozen)
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
            DrawRightAligned(column.FreeSlots is { } free ? $"{free:N0}" : "—", MainWindow.ColMuted);
        }
    }

    private void DrawRow(Layout current, Row row, int index, int frozen, float rowHeight, float contentHeight, bool canEdit)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
        ImGui.PushID(row.Key.Id);
        var pending = row.Rules.Any(placement.IsPending);

        ImGui.TableSetColumnIndex(0);
        var start = ImGui.GetCursorScreenPos();
        DalamudTableSelectionRenderer.DrawRow(selection, current.Keys, index, "##Select", new Vector2(ImGui.GetContentRegionAvail().X, contentHeight));
        var selected = selection.IsSelected(row.Key);
        reviewRegistry.RegisterLastItem(
            $"{AgentPrefix}.row.{row.Key.Id}.select",
            $"Select {row.ItemName}",
            AgentBridgeUiControlKind.Toggle,
            true,
            selected,
            selected ? "Selected" : "Not selected",
            () => selection.SetSelected(row.Key, !selection.IsSelected(row.Key)));
        var nameRoom = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorScreenPos(start);
        var shown = DalamudUiLayout.ElideText(row.ItemName, nameRoom);
        if (pending)
            ImGui.TextColored(MainWindow.ColMuted, shown);
        else
            ImGui.TextUnformatted(shown);
        if (shown.Length != row.ItemName.Length && ImGui.IsMouseHoveringRect(start, start + new Vector2(nameRoom, contentHeight)))
            ImGui.SetTooltip(row.ItemName);

        if (current.ThisPlan)
        {
            ImGui.TableSetColumnIndex(1);
            DrawArriving(row);
        }

        for (var column = 0; column < current.Columns.Length; column++)
        {
            ImGui.TableSetColumnIndex(frozen + column);
            DrawCell(current, row, current.Columns[column], row.Cells[column], canEdit && !pending, pending, contentHeight, rowHeight);
        }
        ImGui.PopID();
    }

    private static void DrawArriving(Row row)
    {
        if (row.Arriving is not { } arriving)
        {
            DrawRightAligned("—", MainWindow.ColMuted);
            return;
        }
        if (arriving == 0)
            return;
        DrawRightAligned($"{arriving:N0}", ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        if (row.Slots is not { } slots)
            return;
        var unit = slots == 1 ? "slot" : "slots";
        if (row.ShortFree is { } free)
            DrawRightAligned($"{slots:N0} {unit} · {free:N0} free", AmberColor);
        else
            DrawRightAligned($"{slots:N0} {unit}", MainWindow.ColMuted);
    }

    private static void DrawRightAligned(string text, Vector4 color)
    {
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - width));
        ImGui.TextColored(color, text);
    }

    private void DrawCell(Layout current, Row row, Column column, Cell cell, bool enabled, bool pending, float contentHeight, float rowHeight)
    {
        var min = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(1f, ImGui.GetContentRegionAvail().X), contentHeight);
        var clicked = ImGui.InvisibleButton($"##{column.Key.Id}", size);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var padding = ImGui.GetStyle().CellPadding;
        var cellMin = min - padding;
        var cellMax = min + size + padding;
        if (row.ShortHome == column.Key)
            drawList.AddRectFilled(cellMin, cellMax, ImGui.GetColorU32(AmberColor with { W = .22f }));
        if (hovered && enabled)
            drawList.AddRectFilled(cellMin, cellMax, ImGui.GetColorU32(HoverCellColor));
        if (column.Key.IsAnySpace)
            DrawAnySeparator(min, rowHeight);

        var lineHeight = ImGui.GetTextLineHeight();
        if (cell.Rank is { } rank)
        {
            var text = rank.ToString();
            var fontSize = ImGui.GetFontSize() * .85f;
            var textSize = ImGui.CalcTextSize(text) * .85f;
            var badge = new Vector2(Math.Max(textSize.X + 6f, fontSize + 2f), fontSize + 2f);
            var position = new Vector2(min.X, min.Y + Math.Max(0f, (lineHeight - badge.Y) * .5f));
            var fill = pending ? RankColor : rank == 1 ? FirstRankColor : RankColor;
            drawList.AddRectFilled(position, position + badge, ImGui.GetColorU32(fill), 3f);
            if (cell.Mixed)
                drawList.AddRect(position, position + badge, ImGui.GetColorU32(MainWindow.ColMuted), 3f);
            drawList.AddText(ImGui.GetFont(), fontSize, position + new Vector2((badge.X - textSize.X) * .5f, 1f),
                ImGui.GetColorU32(pending ? MainWindow.ColMuted : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]), text);
        }
        var heldText = column.Key.IsAnySpace ? null : cell.Held is { } held ? held == 0 ? null : $"{held:N0}" : "—";
        if (heldText is not null)
        {
            var width = ImGui.CalcTextSize(heldText).X;
            drawList.AddText(new Vector2(min.X + size.X - width, min.Y), ImGui.GetColorU32(MainWindow.ColMuted), heldText);
        }

        var label = PickerLabel(current, column);
        if (hovered)
            ImGui.SetTooltip(CellTooltip(row, column, cell, label));
        if (clicked && enabled)
            QueueCellEdit(current, row, column, cell);
        reviewRegistry.RegisterLastItem(
            $"{AgentPrefix}.cell.{row.Key.Id}.{column.Key.Id}",
            $"{row.ItemName} · {label}",
            AgentBridgeUiControlKind.Toggle,
            enabled,
            cell.Rank is not null,
            cell.Rank is { } shownRank ? $"#{shownRank}" : "Not a destination",
            () => QueueCellEdit(current, row, column, cell));
    }

    private static string CellTooltip(Row row, Column column, Cell cell, string label)
    {
        var parts = new List<string> { row.ItemName, label };
        if (!column.Key.IsAnySpace)
            parts.Add(cell.Held is { } held ? $"holds {held:N0}" : "holds —");
        parts.Add(column.FreeSlots is { } free ? $"{free:N0} free slots" : "free slots unknown");
        if (cell.Rank is { } rank)
            parts.Add($"#{rank}");
        parts.Add(cell.Rank is not null || cell.Mixed ? "remove" : $"add as #{cell.NextRank}");
        return string.Join(" · ", parts);
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
