using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using MarketMafioso.TradeQueue;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows;

/// <summary>Split-share material handover: the sender's handover step and the receiver's progress tile.</summary>
public sealed partial class WorkshopFrozenQueueBrowserWindow
{
    private static readonly TimeSpan BagsRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly AgentBridgeActionArgumentSchema HandoverToggleSchema = new(
        [new("enabled", AgentBridgeActionArgumentKind.Boolean)]);

    private readonly Dictionary<Guid, (string Signature, IReadOnlyList<WorkshopMaterialRequirement> Requirements)> requirementCache = [];
    private IReadOnlyDictionary<uint, int>? tradeableBags;
    private DateTimeOffset tradeableBagsAt;

    private static Vector4 SenderAccent => WorkshopJobSplitPanel.Accent(0);
    private static Vector4 TargetAccent => WorkshopJobSplitPanel.Accent(1);

    private enum HoldingsSource
    {
        None,
        Live,
        Frozen,
    }

    private sealed record HandoverView(
        IReadOnlyList<WorkshopHandoverRow> Rows,
        bool CountTheirs,
        HoldingsSource Source,
        DateTimeOffset? CapturedAt,
        bool Loading,
        string? HoldingsProblem,
        bool BagsKnown)
    {
        public int Due => WorkshopHandoverCalculator.HandoverTotal(Rows, CountTheirs);

        /// <summary>Handed units counted toward progress: Σ min(handed, due) per item, so surplus never hides a shortfall.</summary>
        public int Handed => Rows.Sum(row => row.Credited(CountTheirs));

        /// <summary>Handed units as traded, surplus included.</summary>
        public int HandedRaw => Rows.Sum(row => row.HandedOver);
        public int Owed => Rows.Sum(row => row.Owed(CountTheirs));
        public int Short => Rows.Sum(row => row.Short(CountTheirs));
        public int Give => Rows.Sum(row => row.Give(CountTheirs));
        public int Counted => CountTheirs ? Rows.Sum(row => Math.Min(row.Needed, row.TheyHold ?? 0)) : 0;
    }

    private WorkshopFrozenQueue? SelectedSenderShare()
    {
        if (selectedQueueId is not { } id)
            return null;
        var queue = config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == id);
        return queue != null && WorkshopSavedJobHandoff.IsHandoff(queue) && IsSender(queue) ? queue : null;
    }

    private void DrawProjectSummary(IReadOnlyList<WorkshopPrepQueueItem> items)
    {
        var names = ProjectNames();
        var summary = string.Join(
            " · ",
            items.Where(item => item.Quantity > 0).Select(item =>
                $"{(names.TryGetValue(item.WorkshopItemId, out var name) ? name : $"Unknown project {item.WorkshopItemId}")} ×{item.Quantity:N0}"));
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(ColMuted, summary);
        ImGui.PopTextWrapPos();
    }

    // ---- sender --------------------------------------------------------------------------

    private void DrawHandover(WorkshopFrozenQueue queue)
    {
        var tracker = actions.Handover;
        // A record that never reached the Ready click (e.g. left over from a reload) cannot have traded.
        tracker.ClearUnlockedInFlight(queue);
        tracker.RequestHoldings(queue, force: false);
        var view = BuildHandoverView(queue);
        SyncHandoverTotal(queue, view);

        ImGui.Spacing();
        DrawHandoverStrip(queue, view);
        DrawUnverifiedTrade(queue);
        DrawHandoverOptions(queue, view);
        DrawHandoverTable(view);

        if (queue.HandoverReportRefusal is { } refusal)
        {
            ImGui.TextColored(ColWarning, refusal);
        }
        else if (queue.HandoverReportPending && queue.HandoffId != null &&
                 !tracker.IsReporting(queue.Id) && tracker.ReportError(queue.Id) is { } reportError)
        {
            ImGui.TextColored(ColWarning, $"Handover not relayed to the Workshop Host: {reportError} Retrying.");
        }

        if (!view.BagsKnown)
            ImGui.TextColored(ColWarning, "Bags are not readable right now; In bags shows 0.");
    }

    private void DrawHandoverStrip(WorkshopFrozenQueue queue, HandoverView view)
    {
        var target = WorkshopSavedJobHandoff.Target(queue)!;
        var due = view.Due;
        var owed = view.Owed;
        var shortUnits = view.Short;
        var done = owed == 0 && view.Rows.Count > 0;

        var shortChip = shortUnits > 0 ? $"{shortUnits:N0} short" : null;
        var inventoryChip = view.CountTheirs ? $"{view.Counted:N0} counted" : "ignored";
        var (inventoryValue, inventoryColor, inventoryTooltip) = DescribeHoldings(view);

        UtilityWorkspaceUi.DrawStatusStrip(
            "##workshopHandoverStatus",
            [
                new(
                    "Assembler",
                    target.Key,
                    TargetAccent,
                    Accent: TargetAccent,
                    Tooltip: queue.HandoffId is { } handoffId ? $"Handoff {handoffId}" : "Not delivered to the Workshop Host yet"),
                new(
                    "Handed over",
                    $"{view.Handed:N0} / {due:N0}",
                    done ? MarketMafiosoUiTheme.Success : ColHeader,
                    Accent: done ? MarketMafiosoUiTheme.Success : null,
                    Fill: due <= 0 ? 1f : Math.Clamp((float)view.Handed / due, 0f, 1f),
                    FillColor: done ? MarketMafiosoUiTheme.Success : ColHeader,
                    Tooltip: view.HandedRaw > view.Handed
                        ? $"{view.HandedRaw:N0} traded; {view.HandedRaw - view.Handed:N0} beyond what is due is not counted."
                        : null),
                new(
                    "Owed",
                    done ? "Nothing" : owed.ToString("N0"),
                    done ? MarketMafiosoUiTheme.Success : ColHeader,
                    shortChip == null ? null : () => DrawChip(shortChip, MarketMafiosoUiTheme.Error),
                    shortChip == null ? 0f : ChipWidth(shortChip),
                    Accent: shortChip == null ? null : MarketMafiosoUiTheme.Error,
                    Tooltip: shortChip == null ? null : ShortTooltip(view)),
                new(
                    "Their inventory",
                    inventoryValue,
                    inventoryColor,
                    () => DrawChip(inventoryChip, view.CountTheirs ? ColHeader : ColWarning),
                    ChipWidth(inventoryChip),
                    Tooltip: inventoryTooltip),
            ]);
    }

    private static (string Value, Vector4 Color, string? Tooltip) DescribeHoldings(HandoverView view)
    {
        if (view.Loading && view.Source == HoldingsSource.None)
            return ("Checking...", ColMuted, null);
        if (view.Source == HoldingsSource.None)
            return (view.HoldingsProblem == null ? "No report" : "Unavailable", ColWarning, view.HoldingsProblem ?? "The Workshop Host has no inventory report for this character.");

        if (view.Source == HoldingsSource.Frozen && view.CapturedAt == null)
            return ("Not counted", ColWarning, "Unavailable when the handover started; nothing is subtracted.");

        var age = view.CapturedAt is { } captured ? DateTimeOffset.UtcNow - captured : (TimeSpan?)null;
        var stale = age == null || age > WorkshopHandoverTracker.HoldingsFreshness;
        var value = age == null ? "Unknown age" : $"as of {FormatAge(age.Value)}";
        string? tooltip = view.Source == HoldingsSource.Frozen
            ? $"Frozen when the handover started ({view.CapturedAt?.ToLocalTime():g})."
            : view.CapturedAt?.ToLocalTime().ToString("g");
        if (stale)
            tooltip = $"Older than {WorkshopHandoverTracker.HoldingsFreshness.TotalHours:N0}h. {tooltip}";
        if (view.HoldingsProblem != null)
            tooltip = $"{tooltip} Refresh failed: {view.HoldingsProblem}";
        return (value, stale ? ColWarning : ColMuted, tooltip);
    }

    private static string ShortTooltip(HandoverView view) =>
        string.Join(
            "\n",
            view.Rows
                .Where(row => row.Short(view.CountTheirs) > 0)
                .Select(row => $"{row.ItemName}: {row.Short(view.CountTheirs):N0} short"));

    private void DrawHandoverOptions(WorkshopFrozenQueue queue, HandoverView view)
    {
        var count = config.WorkshopHandoverCountTheirInventory;
        if (ImGui.Checkbox("Count their inventory##workshopHandoverCount", ref count))
            SetCountTheirInventory(count);
        reviewRegistry.Register(
            $"{SurfaceId}.handover.count-their-inventory",
            "Subtract what the assembler already holds from what is owed",
            AgentBridgeUiControlKind.Toggle,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled: true,
            selected: count,
            count ? "enabled" : "disabled",
            HandoverToggleSchema,
            SurfaceId,
            mutating: true,
            completionOperationKind: null,
            arguments =>
            {
                var enabled = arguments!.Value.GetProperty("enabled").GetBoolean();
                SetCountTheirInventory(enabled);
                return AgentBridgeUiActionResult.Ok(enabled
                    ? "Handover subtracts the assembler's reported inventory."
                    : "Handover ignores the assembler's inventory.");
            });

        // Once units are handed over the holdings baseline is frozen, so a refresh would change nothing.
        if (view.Source == HoldingsSource.Frozen)
            return;

        ImGui.SameLine();
        var loading = view.Loading;
        if (ImGuiUi.Button(loading ? "Checking...##workshopHandoverRefresh" : "Refresh their inventory##workshopHandoverRefresh", !loading))
            actions.Handover.RequestHoldings(queue, force: true);
        RegisterLastAction(
            $"{SurfaceId}.handover.refresh-holdings",
            $"Re-read {queue.HandoffTargetName}'s holdings from the Workshop Host",
            !loading,
            view.CapturedAt?.ToString("O"),
            _ =>
            {
                actions.Handover.RequestHoldings(queue, force: true);
                return AgentBridgeUiActionResult.Ok($"Requested {queue.HandoffTargetName}'s holdings from the Workshop Host.");
            });
    }

    private void SetCountTheirInventory(bool enabled)
    {
        if (config.WorkshopHandoverCountTheirInventory == enabled)
            return;
        config.WorkshopHandoverCountTheirInventory = enabled;
        config.Save();
    }

    private void DrawHandoverTable(HandoverView view)
    {
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY;
        var rowHeight = ImGui.GetFrameHeight() + (ImGui.GetStyle().CellPadding.Y * 2f);
        var desired = rowHeight * (Math.Max(1, view.Rows.Count) + 1) + 4f;
        var available = ImGui.GetContentRegionAvail().Y - (ImGui.GetFrameHeightWithSpacing() * 3f);
        var height = Math.Min(desired, Math.Max(rowHeight * 4f, available));
        if (!ImGui.BeginTable("WorkshopHandoverTable", 7, flags, new Vector2(0f, height)))
            return;

        var numberWidth = Math.Max(ImGui.CalcTextSize("99,999").X, ImGui.CalcTextSize("They hold").X) + 8f;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Material", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Needed", ImGuiTableColumnFlags.WidthFixed, numberWidth);
        ImGui.TableSetupColumn("They hold", ImGuiTableColumnFlags.WidthFixed, numberWidth);
        ImGui.TableSetupColumn("Handed over", ImGuiTableColumnFlags.WidthFixed, numberWidth + 48f);
        ImGui.TableSetupColumn("Owed", ImGuiTableColumnFlags.WidthFixed, numberWidth);
        ImGui.TableSetupColumn("In bags", ImGuiTableColumnFlags.WidthFixed, numberWidth);
        ImGui.TableSetupColumn("Short", ImGuiTableColumnFlags.WidthFixed, numberWidth);
        ImGui.TableHeadersRow();

        if (view.Rows.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(ColMuted, "No workshop materials resolved for this share.");
        }

        var count = view.CountTheirs;
        foreach (var row in view.Rows)
        {
            var owed = row.Owed(count);
            var due = row.Due(count);
            var rowDone = owed == 0;
            Vector4? text = rowDone ? ColMuted : null;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(text ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Text], row.ItemName);

            ImGui.TableNextColumn();
            DrawNumber(row.Needed.ToString("N0"), text);

            ImGui.TableNextColumn();
            if (row.TheyHold is not { } held)
                DrawNumber("—", ColMuted);
            else if (!count)
                DrawNumber(held.ToString("N0"), ColMuted, strike: true);
            else
                DrawNumber(held.ToString("N0"), held == 0 ? ColMuted : text);

            ImGui.TableNextColumn();
            DrawHandedOverCell(row.HandedOver, due, text);

            ImGui.TableNextColumn();
            if (rowDone)
                DrawCheck(MarketMafiosoUiTheme.Success);
            else
                DrawNumber(owed.ToString("N0"), text);

            ImGui.TableNextColumn();
            DrawNumber(row.InBags.ToString("N0"), text ?? (row.InBags == 0 ? ColMuted : null));

            ImGui.TableNextColumn();
            var shortUnits = row.Short(count);
            if (shortUnits > 0)
                DrawNumber(shortUnits.ToString("N0"), MarketMafiosoUiTheme.Error);
            else
                DrawNumber("—", ColMuted);
        }

        ImGui.EndTable();
    }

    private void DrawHandOverButton(WorkshopFrozenQueue queue)
    {
        var view = BuildHandoverView(queue);
        var give = view.Give;
        var blocked = UnverifiedBlock(queue);
        var enabled = give > 0 && blocked == null;
        var label = give > 0 ? $"Hand over {give:N0}##workshopHandOver" : "Hand over##workshopHandOver";
        if (ImGuiUi.PrimaryButton(label, enabled))
        {
            var result = HandOver(queue, confirmed: false);
            if (!result.Success && result.Message.Length > 0)
                SetShareStatus(result.Message, isError: true);
        }

        if (!enabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(blocked ?? NothingToHandOver(queue, view));
        RegisterLastAction(
            $"{SurfaceId}.handoff.hand-over",
            $"Fill the Trade Queue with {queue.Name}'s owed materials and bind it to {queue.HandoffTargetName}",
            enabled,
            blocked ?? $"give={give:N0}; owed={view.Owed:N0}; short={view.Short:N0}",
            _ =>
            {
                var result = HandOver(queue, confirmed: false);
                return result.Message.Length == 0
                    ? AgentBridgeUiActionResult.Fail(
                        $"The Trade Queue holds other items; invoke {SurfaceId}.handoff.hand-over.confirm to replace it.")
                    : ToActionResult(result);
            });

        if (actions.BoundShareId() == queue.Id)
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(TargetAccent, "Trade Queue bound");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"The Trade Queue only trades to {queue.HandoffTargetName} @{queue.HandoffTargetWorld}.");
        }
    }

    /// <summary>
    /// Fills the Trade Queue with min(Owed, In bags) per material and binds it to this share's assembler.
    /// Returns an empty failure message when it is waiting for the replace confirmation.
    /// </summary>
    private (bool Success, string Message) HandOver(WorkshopFrozenQueue queue, bool confirmed)
    {
        if (WorkshopSavedJobHandoff.Target(queue) is not { } target || !IsSender(queue) ||
            actions.CurrentCharacter() is not { } sender)
            return (false, $"{queue.Name} is not a share this character sent.");
        if (UnverifiedBlock(queue) is { } blocked)
            return (false, blocked);

        var view = BuildHandoverView(queue, refreshBags: true);
        var items = view.Rows
            .Select(row => new TradeQueueItem { ItemId = row.ItemId, ItemName = row.ItemName, Quantity = row.Give(view.CountTheirs) })
            .Where(item => item.Quantity > 0)
            .ToList();
        if (items.Count == 0)
            return (false, NothingToHandOver(queue, view));

        if (!confirmed && actions.TradeQueueHasItems() && actions.BoundShareId() != queue.Id)
        {
            pendingTradeReplaceQueueId = queue.Id;
            return (false, string.Empty);
        }

        var binding = new TradeQueueRecipientBinding
        {
            ShareJobId = queue.Id,
            HandoffId = queue.HandoffId,
            ShareName = queue.Name,
            RecipientName = target.CharacterName,
            RecipientWorld = target.World,
            SenderName = sender.CharacterName,
            SenderWorld = sender.World,
        };
        if (!actions.HandOver(items, binding, out var message))
        {
            SetShareStatus(message, isError: true);
            return (false, message);
        }

        // Until a unit is credited, re-baseline on the holdings this handover was planned from. Freeze even
        // when holdings were unavailable (an empty baseline): later reports include traded units, and using
        // them live alongside the ledger would subtract those units twice.
        if (!queue.HandedOver.Any(entry => entry.Quantity > 0))
        {
            queue.HandoverBaseline = view.Rows
                .Where(row => row.TheyHold is > 0)
                .Select(row => new WorkshopMaterialLedgerEntry { ItemId = row.ItemId, Quantity = row.TheyHold!.Value })
                .ToList();
            queue.HandoverBaselineCapturedAt = view.CapturedAt;
        }

        queue.HandoverTotal = view.Due;
        queue.HandoverDue = DueEntries(view);
        config.Save();
        actions.Handover.MarkForReport(queue);
        pendingTradeReplaceQueueId = null;
        SetShareStatus(message, isError: false);
        actions.ShowTradeQueue();
        return (true, message);
    }

    private static string NothingToHandOver(WorkshopFrozenQueue queue, HandoverView view)
    {
        if (view.Rows.Count == 0)
            return $"No workshop materials resolved for {queue.Name}.";
        if (view.Owed == 0)
            return $"Nothing owed: {queue.HandoffTargetName} has everything {queue.Name} needs.";
        var missing = view.Rows
            .Where(row => row.Short(view.CountTheirs) > 0)
            .Select(row => $"{row.ItemName} {row.Short(view.CountTheirs):N0}");
        return $"Nothing to hand over: {view.Owed:N0} owed but none in bags ({string.Join(", ", missing)} short).";
    }

    /// <summary>Keeps the reported per-item due and total in step once a handover has started.</summary>
    private void SyncHandoverTotal(WorkshopFrozenQueue queue, HandoverView view)
    {
        if ((queue.HandoverTotal <= 0 && queue.HandoverDue == null) || view.Loading || view.Rows.Count == 0)
            return;
        var due = DueEntries(view);
        if (view.Due == queue.HandoverTotal && queue.HandoverDue is { } stored && DueSignature(stored) == DueSignature(due))
            return;
        queue.HandoverTotal = view.Due;
        queue.HandoverDue = due;
        config.Save();
        actions.Handover.MarkForReport(queue);
    }

    private static List<WorkshopMaterialLedgerEntry> DueEntries(HandoverView view) =>
        view.Rows
            .Select(row => new WorkshopMaterialLedgerEntry { ItemId = row.ItemId, Quantity = row.Due(view.CountTheirs) })
            .Where(entry => entry.Quantity > 0)
            .OrderBy(entry => entry.ItemId)
            .ToList();

    private static string DueSignature(IEnumerable<WorkshopMaterialLedgerEntry> due) =>
        string.Join(',', due.OrderBy(entry => entry.ItemId).Select(entry => $"{entry.ItemId}:{entry.Quantity}"));

    /// <summary>Why Hand over is blocked by an unresolved or still-trading batch, or null.</summary>
    private string? UnverifiedBlock(WorkshopFrozenQueue queue)
    {
        if (queue.HandoverInFlight is not { } inFlight)
            return null;
        return actions.Handover.TradeActive
            ? $"The Trade Queue is trading {inFlight.Units:N0} unit(s) to {inFlight.Recipient}; wait for it to verify."
            : $"{WorkshopHandoverTracker.UnverifiedTradeText(inFlight)}; count it as handed over or discard it first.";
    }

    /// <summary>A bound batch that started but was never verified (reload, failure or stop mid-batch).</summary>
    private void DrawUnverifiedTrade(WorkshopFrozenQueue queue)
    {
        if (queue.HandoverInFlight is not { } inFlight || actions.Handover.TradeActive)
            return;

        var names = Requirements(queue).ToDictionary(requirement => requirement.ItemId, requirement => requirement.ItemName);
        var detail = string.Join(
                         "\n",
                         inFlight.Items.Select(item =>
                             $"{(names.TryGetValue(item.ItemId, out var name) ? name : $"Item {item.ItemId}")} ×{item.Quantity:N0}")) +
                     $"\nBatch started {inFlight.StartedAt.ToLocalTime():g}; its inventory change was never verified.";
        var value = $"units={inFlight.Units:N0}; recipient={inFlight.Recipient}; startedAt={inFlight.StartedAt:O}";

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(ColWarning, $"{WorkshopHandoverTracker.UnverifiedTradeText(inFlight)}.");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(detail);

        ImGui.SameLine();
        if (ImGuiUi.Button("Count as handed over##workshopHandoverInFlightCount", true))
            ResolveUnverifiedTrade(queue, count: true);
        RegisterLastAction(
            $"{SurfaceId}.handover.unverified.count",
            $"Credit {queue.Name}'s unverified trade to the handed-over ledger",
            enabled: true,
            value,
            _ => ToActionResult(ResolveUnverifiedTrade(queue, count: true)));

        ImGui.SameLine();
        if (ImGuiUi.Button("Discard##workshopHandoverInFlightDiscard", true))
            ResolveUnverifiedTrade(queue, count: false);
        RegisterLastAction(
            $"{SurfaceId}.handover.unverified.discard",
            $"Discard {queue.Name}'s unverified trade without crediting it",
            enabled: true,
            value,
            _ => ToActionResult(ResolveUnverifiedTrade(queue, count: false)));
    }

    private (bool Success, string Message) ResolveUnverifiedTrade(WorkshopFrozenQueue queue, bool count)
    {
        var result = actions.Handover.ResolveInFlight(queue, count);
        SetShareStatus(result.Message, isError: !result.Success);
        return result;
    }

    private HandoverView BuildHandoverView(WorkshopFrozenQueue queue, bool refreshBags = false)
    {
        var requirements = Requirements(queue);
        var bags = TradeableBags(refreshBags);
        var lookup = actions.Handover.Holdings(queue.Id);
        var loading = lookup?.Loading == true;
        var problem = lookup?.LastError;
        if (problem == null && lookup?.Result is { CapturedAt: null })
            problem = "The Workshop Host has no inventory report for this character.";

        IReadOnlyDictionary<uint, int>? theyHold = null;
        DateTimeOffset? capturedAt = null;
        var source = HoldingsSource.None;
        var baseline = queue.HandoverBaseline?.ToDictionary(entry => entry.ItemId, entry => entry.Quantity);
        if (baseline != null && queue.HandedOver.Any(entry => entry.Quantity > 0))
        {
            (theyHold, capturedAt, source) = (baseline, queue.HandoverBaselineCapturedAt, HoldingsSource.Frozen);
        }
        else if (lookup?.Result is { CapturedAt: { } captured } live)
        {
            (theyHold, capturedAt, source) = (live.Quantities, captured, HoldingsSource.Live);
        }
        else if (baseline != null)
        {
            (theyHold, capturedAt, source) = (baseline, queue.HandoverBaselineCapturedAt, HoldingsSource.Frozen);
        }

        var rows = WorkshopHandoverCalculator.Build(
            requirements,
            queue.HandedOver,
            theyHold,
            bags ?? new Dictionary<uint, int>());
        return new HandoverView(
            rows,
            config.WorkshopHandoverCountTheirInventory,
            source,
            capturedAt,
            loading,
            problem,
            bags != null);
    }

    private IReadOnlyList<WorkshopMaterialRequirement> Requirements(WorkshopFrozenQueue queue)
    {
        var signature = string.Join(',', queue.Items.Select(item => $"{item.WorkshopItemId}:{item.Quantity}"));
        if (requirementCache.TryGetValue(queue.Id, out var cached) && cached.Signature == signature)
            return cached.Requirements;
        var requirements = workshopCatalog.BuildRequirements(queue.Items);
        requirementCache[queue.Id] = (signature, requirements);
        return requirements;
    }

    private IReadOnlyDictionary<uint, int>? TradeableBags(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - tradeableBagsAt < BagsRefreshInterval)
            return tradeableBags;
        tradeableBagsAt = now;
        // Keep the last readable inventory through brief unreadable frames (zoning, loading).
        tradeableBags = actions.TradeableBags() ?? tradeableBags;
        return tradeableBags;
    }

    // ---- receiver ------------------------------------------------------------------------

    private void DrawReceivedProgress(WorkshopFrozenQueue queue)
    {
        var sender = WorkshopSavedJobHandoff.Sender(queue)!;
        var service = actions.Handoff();
        var view = queue.HandoffId is { } id ? service.Received.FirstOrDefault(handoff => handoff.HandoffId == id) : null;

        UtilityStatusFact received;
        if (view == null)
        {
            received = new(
                "Materials received",
                "Not reported",
                ColMuted,
                Tooltip: service.IsAvailable
                    ? "The Workshop Host lists no accepted handoff with this id."
                    : service.UnavailableReason ?? "Workshop Host is unavailable.");
        }
        else if (view.HandoverTotal <= 0)
        {
            received = new("Materials received", view.HandedOverUnits > 0 ? $"{view.HandedOverUnits:N0}" : "Not started", ColMuted, Tooltip: ReceivedTooltip(queue, view));
        }
        else
        {
            var complete = view.HandedOverUnits >= view.HandoverTotal;
            var color = complete ? MarketMafiosoUiTheme.Success : ColHeader;
            received = new(
                "Materials received",
                $"{view.HandedOverUnits:N0} / {view.HandoverTotal:N0}",
                color,
                Accent: complete ? MarketMafiosoUiTheme.Success : null,
                Fill: (float)view.HandedOverUnits / view.HandoverTotal,
                FillColor: color,
                Tooltip: ReceivedTooltip(queue, view));
        }

        UtilityWorkspaceUi.DrawStatusStrip(
            "##workshopReceivedStatus",
            [
                new("Sender", sender.Key, SenderAccent, Accent: SenderAccent),
                received,
            ]);
    }

    private string? ReceivedTooltip(WorkshopFrozenQueue queue, WorkshopHandoffView view)
    {
        if (view.HandedOverItems.Count == 0)
            return view.HandoverUpdatedAt is { } updated ? $"Updated {updated.ToLocalTime():g}" : null;
        var names = Requirements(queue).ToDictionary(requirement => requirement.ItemId, requirement => requirement.ItemName);
        var lines = view.HandedOverItems
            .Where(item => item.Quantity > 0)
            .Select(item => $"{(names.TryGetValue(item.ItemId, out var name) ? name : $"Item {item.ItemId}")} ×{item.Quantity:N0}");
        var text = string.Join("\n", lines);
        return view.HandoverUpdatedAt is { } at ? $"{text}\nUpdated {at.ToLocalTime():g}" : text;
    }

    // ---- drawing helpers -----------------------------------------------------------------

    private static AgentBridgeUiActionResult ToActionResult((bool Success, string Message) result) =>
        result.Success ? AgentBridgeUiActionResult.Ok(result.Message) : AgentBridgeUiActionResult.Fail(result.Message);

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromHours(48))
            return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }

    private const float ChipPadding = 5f;

    private static float ChipWidth(string text) => ImGui.CalcTextSize(text).X + (ChipPadding * 2f) + 2f;

    private static void DrawChip(string text, Vector4 color)
    {
        var size = ImGui.CalcTextSize(text) + new Vector2(ChipPadding * 2f, 0f);
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.18f)), 2f);
        drawList.AddText(new Vector2(min.X + ChipPadding, min.Y), ImGui.GetColorU32(color), text);
        ImGui.Dummy(size);
    }

    private static void DrawNumber(string text, Vector4? color, bool strike = false)
    {
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - width));
        ImGui.AlignTextToFramePadding();
        if (color is { } tint)
            ImGui.TextColored(tint, text);
        else
            ImGui.TextUnformatted(text);
        if (!strike)
            return;
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var y = (min.Y + max.Y) * 0.5f;
        ImGui.GetWindowDrawList().AddLine(new Vector2(min.X - 1f, y), new Vector2(max.X + 1f, y), ImGui.GetColorU32(color ?? ColMuted));
    }

    private static void DrawHandedOverCell(int handed, int due, Vector4? color)
    {
        const float barWidth = 40f;
        const float gap = 6f;
        var text = handed.ToString("N0");
        var textWidth = ImGui.CalcTextSize(text).X;
        var start = ImGui.GetCursorPosX();
        var available = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(start + Math.Max(0f, available - textWidth - gap - barWidth));
        ImGui.AlignTextToFramePadding();
        if (color is { } tint)
            ImGui.TextColored(tint, text);
        else
            ImGui.TextUnformatted(text);

        ImGui.SameLine(0f, gap);
        var origin = ImGui.GetCursorScreenPos();
        var top = origin.Y + (ImGui.GetFrameHeight() * 0.5f) - 2f;
        var drawList = ImGui.GetWindowDrawList();
        var success = MarketMafiosoUiTheme.Success;
        drawList.AddRectFilled(new Vector2(origin.X, top), new Vector2(origin.X + barWidth, top + 4f), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.08f)), 2f);
        var fill = due <= 0 ? 1f : Math.Clamp((float)handed / due, 0f, 1f);
        if (fill > 0f)
            drawList.AddRectFilled(new Vector2(origin.X, top), new Vector2(origin.X + (barWidth * fill), top + 4f), ImGui.GetColorU32(success), 2f);
        ImGui.Dummy(new Vector2(barWidth, ImGui.GetFrameHeight()));
    }

    /// <summary>A drawn check mark, right-aligned (the game font has no reliable ✓ glyph).</summary>
    private static void DrawCheck(Vector4 color)
    {
        var size = ImGui.GetTextLineHeight() * 0.8f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, ImGui.GetContentRegionAvail().X - size));
        var origin = ImGui.GetCursorScreenPos();
        var top = origin.Y + ((ImGui.GetFrameHeight() - size) * 0.5f);
        var drawList = ImGui.GetWindowDrawList();
        var u32 = ImGui.GetColorU32(color);
        var a = new Vector2(origin.X + (size * 0.10f), top + (size * 0.55f));
        var b = new Vector2(origin.X + (size * 0.40f), top + (size * 0.85f));
        var c = new Vector2(origin.X + (size * 0.95f), top + (size * 0.15f));
        drawList.AddLine(a, b, u32, 2f);
        drawList.AddLine(b, c, u32, 2f);
        ImGui.Dummy(new Vector2(size, ImGui.GetFrameHeight()));
    }
}
