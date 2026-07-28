using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.UI.Styling;
using Franthropy.Dalamud.UI.Tables;
using MarketMafioso.Quartermaster;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.WorkshopLogistics;

internal sealed class WorkshopMaterialPanel
{
    private static readonly AgentBridgeActionArgumentSchema QuantityArguments = new(
        [new("quantity", AgentBridgeActionArgumentKind.Integer, Minimum: 0)]);
    private readonly Configuration config;
    private readonly QuartermasterIpcClient quartermaster;
    private readonly WorkshopVendorProcurementPlanner planner;
    private readonly WorkshopVendorRestockRunner runner;
    private readonly Func<IReadOnlyList<WorkshopMaterialAvailability>> getAvailability;
    private readonly Func<QuartermasterOwnerScope> getOwnerScope;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Action drawMaterialActions;
    private readonly DalamudTableProjection<MaterialTableRow> coverageTable;
    private readonly DalamudTableProjection<MaterialTableRow> vendorTable;
    private string searchText = string.Empty;
    private bool shortagesOnly = true;
    private bool showReceiptDetails;
    private WorkshopVendorRestockPhase? previousPhase;
    private string? actionStatus;

    public WorkshopMaterialPanel(
        Configuration config,
        QuartermasterIpcClient quartermaster,
        WorkshopVendorProcurementPlanner planner,
        WorkshopVendorRestockRunner runner,
        Func<IReadOnlyList<WorkshopMaterialAvailability>> getAvailability,
        Func<QuartermasterOwnerScope> getOwnerScope,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Action drawMaterialActions)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.quartermaster = quartermaster ?? throw new ArgumentNullException(nameof(quartermaster));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.getAvailability = getAvailability ?? throw new ArgumentNullException(nameof(getAvailability));
        this.getOwnerScope = getOwnerScope ?? throw new ArgumentNullException(nameof(getOwnerScope));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        this.drawMaterialActions = drawMaterialActions ?? throw new ArgumentNullException(nameof(drawMaterialActions));
        coverageTable = BuildTableProjection(showBuyControls: false);
        vendorTable = BuildTableProjection(showBuyControls: true);
    }

    public void Draw(IReadOnlyList<WorkshopMaterialAvailability>? availability = null)
    {
        availability ??= getAvailability();
        var review = BuildReview(availability);
        var run = runner.ActiveRun;
        if (run?.Phase == WorkshopVendorRestockPhase.Completed &&
            previousPhase != WorkshopVendorRestockPhase.Completed)
        {
            shortagesOnly = true;
            showReceiptDetails = false;
        }
        previousPhase = run?.Phase;

        DalamudUiChrome.DrawSectionHeading(
            "Materials",
            run?.Phase == WorkshopVendorRestockPhase.Completed
                ? run.Receipts.Count > 0
                    ? "Completed vendor run"
                    : "Completed restock"
                : null,
            MarketMafiosoUiTheme.Palette);

        if (review.RetainerUnits > 0 &&
            !string.IsNullOrWhiteSpace(quartermaster.LastStatus) &&
            !quartermaster.LastStatus.Contains("has not been queried", StringComparison.OrdinalIgnoreCase))
        {
            ImGui.TextColored(GetQuartermasterStatusColor(), DescribeQuartermasterStatus());
        }
        var visibleStatus = run is null
            ? actionStatus
            : WorkshopVendorRestockPresentation.Describe(run, review);
        if (run?.Phase == WorkshopVendorRestockPhase.Completed && run.Receipts.Count > 0)
            DrawCompletionReceipt(run, visibleStatus ?? string.Empty);
        else if (!string.IsNullOrWhiteSpace(visibleStatus))
            ImGui.TextColored(RunStatusColor(run), visibleStatus);

        var automatic = run is not null && (runner.IsRunning || run.Phase == WorkshopVendorRestockPhase.Paused)
            ? run.AutomaticallyBuyVendorMaterials
            : config.AutomaticallyBuyWorkshopVendorMaterials;
        var automaticLabelWidth = ImGui.CalcTextSize("Automatically buy vendor materials").X + 32f;
        var shortageLabelWidth = ImGui.CalcTextSize("Shortages only  00 / 00").X + 32f;
        ImGui.SetNextItemWidth(
            Math.Max(
                220f,
                ImGui.GetContentRegionAvail().X -
                automaticLabelWidth -
                shortageLabelWidth -
                ImGui.GetStyle().ItemSpacing.X * 3f));
        using (DalamudUiChrome.PushInput(MarketMafiosoUiTheme.Palette))
        {
            ImGui.InputTextWithHint("##workshopMaterialSearch", "Filter materials...", ref searchText, 128);
        }
        ImGui.SameLine();
        ImGui.Checkbox("Shortages only", ref shortagesOnly);
        var filtered = review.Materials
            .Where(line =>
                (!shortagesOnly || line.Availability.Shortage > 0) &&
                (string.IsNullOrWhiteSpace(searchText) ||
                 line.Availability.ItemName.Contains(searchText.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        ImGui.SameLine();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{filtered.Count:N0} / {review.Materials.Count:N0}");

        ImGui.SameLine();
        ImGui.BeginDisabled(runner.IsRunning || run?.Phase == WorkshopVendorRestockPhase.Paused);
        if (ImGui.Checkbox("Automatically buy vendor materials", ref automatic))
        {
            config.AutomaticallyBuyWorkshopVendorMaterials = automatic;
            config.Save();
        }
        ImGui.EndDisabled();
        reviewRegistry.Register(
            "workshop-logistics.vendor-autobuy",
            "Automatically buy vendor materials",
            AgentBridgeUiControlKind.Toggle,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            !runner.IsRunning && run?.Phase != WorkshopVendorRestockPhase.Paused,
            automatic,
            automatic ? "Enabled" : "Disabled",
            () =>
            {
                config.AutomaticallyBuyWorkshopVendorMaterials = !config.AutomaticallyBuyWorkshopVendorMaterials;
                config.Save();
            });

        var showBuyControls = review.Materials.Any(
            line => line.VendorNeed > 0 && line.SelectedCandidate is not null);
        DrawTable(filtered, review.Materials.Count, review.QueueSignature, showBuyControls);
        DrawFooter(review, run, automatic);
    }

    public WorkshopVendorRestockReview BuildReview(
        IReadOnlyList<WorkshopMaterialAvailability>? availability = null) =>
        planner.Build(
            availability ?? getAvailability(),
            config.WorkshopVendorApprovedQuantities,
            config.WorkshopVendorExcludedItems.ToHashSet());

    private unsafe void DrawTable(
        IReadOnlyList<WorkshopMaterialProcurement> filtered,
        int totalCount,
        string queueSignature,
        bool showBuyControls)
    {
        var activeRun = runner.ActiveRun;
        var rows = filtered
            .Select(line => new MaterialTableRow(
                line,
                activeRun is not null &&
                string.Equals(activeRun.QueueSignature, queueSignature, StringComparison.Ordinal)
                    ? activeRun.Lines.FirstOrDefault(item => item.ItemId == line.Availability.ItemId)
                    : null,
                activeRun))
            .ToArray();
        var projection = showBuyControls ? vendorTable : coverageTable;
        var tableId = showBuyControls
            ? "WorkshopPrepMaterialsVendorBuyV2"
            : "WorkshopPrepMaterialsCoverageV2";
        using var style = DalamudUiChrome.PushTable(MarketMafiosoUiTheme.Palette);
        if (!projection.Begin(
                tableId,
                DalamudTableLayout.FitContent(ImGuiUi.InteractiveTableFlags)))
            return;

        if (rows.Length == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(
                MarketMafiosoUiTheme.Muted,
                totalCount == 0
                    ? "No workshop materials yet. Add projects to the prep queue."
                    : "No materials match the current filter.");
            projection.End();
            return;
        }

        foreach (var row in projection.Apply(rows, ImGui.TableGetSortSpecs()))
        {
            ImGui.PushID(checked((int)row.Line.Availability.ItemId));
            projection.DrawRow(row);
            ImGui.PopID();
        }

        projection.End();
    }

    private DalamudTableProjection<MaterialTableRow> BuildTableProjection(bool showBuyControls)
    {
        var columns = new List<DalamudTableColumn<MaterialTableRow>>();
        if (showBuyControls)
        {
            columns.Add(
                new(
                    string.Empty,
                    28f,
                    row => row.Line.Selected ? "Included" : "Excluded",
                    Flags: ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort,
                    Draw: row => DrawSelection(row.Line)));
        }

        columns.Add(
            new(
                "Item",
                1f,
                row => row.Line.Availability.ItemName,
                Flags: ImGuiTableColumnFlags.WidthStretch));
        columns.Add(NumberColumn("Required", 68f, row => row.Line.Availability.Required));
        columns.Add(
            NumberColumn(
                "Player",
                64f,
                row => row.ActiveLine?.LivePlayerQuantity ?? row.Line.Availability.PlayerInventory));
        columns.Add(NumberColumn("Retainers", 72f, row => row.Line.RetainerPlannedQuantity));
        columns.Add(
            NumberColumn(
                "Need",
                70f,
                row => row.Line.VendorNeed,
                row => row.Line.VendorNeed > 0
                    ? MarketMafiosoUiTheme.Error
                    : MarketMafiosoUiTheme.Success));
        columns.Add(
            new(
                "Source",
                1f,
                row => DescribeSource(row.Line),
                Flags: ImGuiTableColumnFlags.WidthStretch,
                Draw: row => DrawSource(row.Line)));
        if (showBuyControls)
        {
            columns.Add(
                new(
                    "Buy",
                    72f,
                    row => row.Line.ApprovedVendorQuantity.ToString("N0"),
                    row => row.Line.ApprovedVendorQuantity,
                    Draw: row => DrawQuantity(row.Line),
                    Alignment: DalamudTableCellAlignment.Right));
        }

        columns.Add(
            new(
                showBuyControls ? "Cost / Status" : "Coverage",
                140f,
                DescribeCoverage,
                row => DescribeCoverage(row),
                Draw: DrawCoverage));
        return new DalamudTableProjection<MaterialTableRow>(columns);
    }

    private static DalamudTableColumn<MaterialTableRow> NumberColumn(
        string label,
        float width,
        Func<MaterialTableRow, int> value,
        Func<MaterialTableRow, System.Numerics.Vector4?>? color = null) =>
        new(
            label,
            width,
            row => value(row).ToString("N0"),
            row => value(row),
            TextColor: color,
            Alignment: DalamudTableCellAlignment.Right);

    private void DrawSelection(WorkshopMaterialProcurement line)
    {
        if (!line.CanBuyAutomatically)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
            return;
        }

        var selected = line.Selected;
        var enabled = config.AutomaticallyBuyWorkshopVendorMaterials &&
                      !runner.IsRunning &&
                      runner.ActiveRun?.Phase != WorkshopVendorRestockPhase.Paused;
        ImGui.BeginDisabled(!enabled);
        if (ImGui.Checkbox("##selected", ref selected))
        {
            SetExcluded(line.Availability.ItemId, !selected);
        }
        ImGui.EndDisabled();
        reviewRegistry.Register(
            $"workshop-logistics.vendor-item.{line.Availability.ItemId}.selected",
            $"Include {line.Availability.ItemName} in vendor restock",
            AgentBridgeUiControlKind.Toggle,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            selected,
            selected ? "Included" : "Excluded",
            () => SetExcluded(line.Availability.ItemId, line.Selected));
    }

    private void DrawQuantity(WorkshopMaterialProcurement line)
    {
        if (line.SelectedCandidate is null || line.VendorNeed <= 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
            return;
        }

        var quantity = line.ApprovedVendorQuantity;
        var enabled = config.AutomaticallyBuyWorkshopVendorMaterials &&
                      line.Selected &&
                      !runner.IsRunning &&
                      runner.ActiveRun?.Phase != WorkshopVendorRestockPhase.Paused;
        ImGui.BeginDisabled(!enabled);
        ImGui.SetNextItemWidth(-1);
        using (DalamudUiChrome.PushInput(MarketMafiosoUiTheme.Palette))
        {
            if (ImGui.InputInt("##quantity", ref quantity, 0))
                SetQuantity(line.Availability.ItemId, quantity, line.VendorNeed);
        }
        ImGui.EndDisabled();
        reviewRegistry.Register(
            $"workshop-logistics.vendor-item.{line.Availability.ItemId}.quantity",
            $"Set {line.Availability.ItemName} vendor quantity",
            AgentBridgeUiControlKind.Input,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            false,
            line.ApprovedVendorQuantity.ToString(),
            QuantityArguments with
            {
                Properties =
                [
                    new(
                        "quantity",
                        AgentBridgeActionArgumentKind.Integer,
                        Minimum: 0,
                        Maximum: line.VendorNeed),
                ],
            },
            arguments =>
            {
                var requested = arguments!.Value.GetProperty("quantity").GetInt32();
                SetQuantity(line.Availability.ItemId, requested, line.VendorNeed);
                return AgentBridgeUiActionResult.Ok(
                    $"{line.Availability.ItemName} vendor quantity set to {requested:N0}.");
            });
    }

    private static string DescribeSource(WorkshopMaterialProcurement line)
    {
        var candidate = line.SelectedCandidate ?? line.Candidates.FirstOrDefault();
        return candidate is null
            ? "Craft / gather / market"
            : $"{candidate.Offer.NpcName} · {candidate.Offer.UnitPriceGil:N0} gil";
    }

    private static void DrawSource(WorkshopMaterialProcurement line)
    {
        var candidate = line.SelectedCandidate ?? line.Candidates.FirstOrDefault();
        if (candidate is null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Craft / gather / market");
            return;
        }
        ImGui.TextUnformatted($"{candidate.Offer.NpcName} · {candidate.Offer.UnitPriceGil:N0} gil");
        ImGui.TextColored(
            candidate.Access.IsEligible ? MarketMafiosoUiTheme.Muted : MarketMafiosoUiTheme.Warning,
            candidate.Access.State switch
            {
                GilVendorAccessState.Verified => "Access verified",
                GilVendorAccessState.Probeable => "Route available",
                GilVendorAccessState.Unavailable => "No accessible route",
                _ => "Access unknown",
            });
    }

    private static string DescribeCoverage(MaterialTableRow row)
    {
        if (row.ActiveLine is not null)
            return RunStatusForLine(row.ActiveLine, row.Run);
        if (row.Line.SelectedCandidate is not null)
            return $"{row.Line.ApprovedGil:N0} gil";
        return row.Line.VendorNeed > 0 ? "Manual" : "-";
    }

    private static void DrawCoverage(MaterialTableRow row)
    {
        if (row.ActiveLine is not null)
        {
            var status = RunStatusForLine(row.ActiveLine, row.Run);
            ImGui.TextColored(LineStatusColor(status), status);
            return;
        }

        if (row.Line.SelectedCandidate is not null)
        {
            ImGui.TextUnformatted($"{row.Line.ApprovedGil:N0} gil");
            return;
        }

        if (row.Line.VendorNeed > 0)
        {
            DalamudUiChrome.DrawBadge(
                "Manual",
                MarketMafiosoUiTheme.Palette,
                DalamudUiTone.Neutral);
            return;
        }

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
    }

    private void DrawRestockActions(
        WorkshopVendorRestockReview review,
        bool automatic)
    {
        var active = runner.ActiveRun;
        if (runner.IsRunning)
        {
            if (ImGuiUi.PrimaryButton("Pause Restock", true))
                runner.Pause();
            RegisterSimpleAction(
                "workshop-logistics.vendor-restock.pause",
                "Pause workshop restock",
                true,
                () => runner.Pause());
            ImGui.SameLine();
            if (ImGui.Button("Stop"))
                runner.Stop();
            RegisterSimpleAction(
                "workshop-logistics.vendor-restock.stop",
                "Stop workshop restock",
                true,
                () => runner.Stop());
            return;
        }
        if (active?.Phase == WorkshopVendorRestockPhase.Paused)
        {
            if (ImGuiUi.PrimaryButton("Resume Restock", true) &&
                !runner.Resume(getOwnerScope(), review.QueueSignature, out var resumeError))
            {
                actionStatus = resumeError;
            }
            reviewRegistry.Register(
                "workshop-logistics.vendor-restock.resume",
                "Resume workshop restock",
                AgentBridgeUiControlKind.Button,
                ImGui.GetItemRectMin(),
                ImGui.GetItemRectMax(),
                true,
                false,
                active.Message,
                () =>
                {
                    if (!runner.Resume(getOwnerScope(), review.QueueSignature, out var error))
                        throw new InvalidOperationException(error);
                });
            ImGui.SameLine();
            if (ImGui.Button("Stop"))
                runner.Stop();
            RegisterSimpleAction(
                "workshop-logistics.vendor-restock.stop",
                "Stop workshop restock",
                true,
                () => runner.Stop());
            return;
        }

        var canStart = getOwnerScope().IsAvailable &&
                       (review.RetainerUnits > 0 || (automatic && review.VendorUnits > 0));
        var label = WorkshopVendorRestockPresentation.BuildStartActionLabel(review, automatic);
        if (!canStart || string.IsNullOrWhiteSpace(label))
            return;

        if (ImGuiUi.PrimaryButton(label, canStart) &&
            !runner.TryStart(review, getOwnerScope(), automatic, out var startError))
        {
            actionStatus = startError;
        }
        reviewRegistry.Register(
            "workshop-logistics.vendor-restock.start",
            label,
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            canStart,
            false,
            $"{review.QueueSignature} · {review.MaximumGil:N0} gil · {review.Stops.Count:N0} stop(s)",
            () =>
            {
                if (!runner.TryStart(review, getOwnerScope(), automatic, out var error))
                    throw new InvalidOperationException(error);
            });
    }

    private void RegisterSimpleAction(
        string id,
        string label,
        bool enabled,
        Func<bool> invoke)
    {
        reviewRegistry.Register(
            id,
            label,
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            false,
            null,
            () => invoke());
    }

    private void SetExcluded(uint itemId, bool excluded)
    {
        config.WorkshopVendorExcludedItems.RemoveAll(candidate => candidate == itemId);
        if (excluded)
            config.WorkshopVendorExcludedItems.Add(itemId);
        config.Save();
    }

    private void SetQuantity(uint itemId, int quantity, int maximum)
    {
        config.WorkshopVendorApprovedQuantities[itemId] = Math.Clamp(quantity, 0, maximum);
        config.Save();
    }

    private void DrawCompletionReceipt(
        PersistedWorkshopVendorRestockRun run,
        string visibleStatus)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MarketMafiosoUiTheme.Success, "Vendor purchase complete.");
        ImGui.SameLine();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, visibleStatus);
        var label = showReceiptDetails
            ? "Hide receipts"
            : $"View {run.Receipts.Count:N0} receipts";
        var width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGuiUi.SameLineRight(width);
        using (DalamudUiChrome.PushButton(
                   MarketMafiosoUiTheme.Palette,
                   DalamudUiTone.Success,
                   quiet: true))
        {
            if (ImGui.SmallButton(label))
                showReceiptDetails = !showReceiptDetails;
        }

        if (showReceiptDetails)
        {
            ImGui.TextColored(
                MarketMafiosoUiTheme.Muted,
                WorkshopVendorRestockPresentation.DescribeReceiptDetails(run));
        }
    }

    private void DrawFooter(
        WorkshopVendorRestockReview review,
        PersistedWorkshopVendorRestockRun? run,
        bool automatic)
    {
        ImGui.Spacing();
        var actionLabel = WorkshopVendorRestockPresentation.BuildStartActionLabel(review, automatic);
        var actionWidth = actionLabel is null
            ? 180f
            : Math.Clamp(ImGui.CalcTextSize(actionLabel).X + 190f, 420f, 620f);
        var tone = review.Materials.Any(line => line.VendorNeed > 0)
            ? DalamudUiTone.Warning
            : DalamudUiTone.Success;
        DalamudUiChrome.DrawStatusBand(
            "##workshopMaterialsFooter",
            MarketMafiosoUiTheme.Palette,
            tone,
            () =>
            {
                var flags = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoPadOuterX;
                if (!ImGui.BeginTable("WorkshopMaterialsFooterLayout", 3, flags))
                    return;

                ImGui.TableSetupColumn("Disclosure", ImGuiTableColumnFlags.WidthFixed, 150);
                ImGui.TableSetupColumn("Summary", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, actionWidth);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                if (run?.Phase == WorkshopVendorRestockPhase.Completed && run.Receipts.Count > 0)
                {
                    var purchasedLines = run.Lines.Count(line => line.PurchasedQuantity > 0);
                    using var buttonStyle = DalamudUiChrome.PushButton(
                        MarketMafiosoUiTheme.Palette,
                        tone,
                        quiet: true);
                    if (ImGui.SmallButton(
                            shortagesOnly
                                ? $"Show purchased ({purchasedLines:N0})"
                                : "Hide purchased"))
                    {
                        shortagesOnly = !shortagesOnly;
                    }
                }

                ImGui.TableNextColumn();
                if (run?.Phase == WorkshopVendorRestockPhase.Completed || actionLabel is null)
                {
                    ImGui.TextUnformatted(WorkshopVendorRestockPresentation.DescribeRemaining(review));
                    if (review.Materials.Any(line => line.VendorNeed > 0))
                    {
                        ImGui.TextColored(
                            MarketMafiosoUiTheme.Muted,
                            "No automatic source is available for the remaining materials.");
                    }
                }
                else
                {
                    ImGui.TextUnformatted(BuildReviewSummary(review, automatic));
                }

                ImGui.TableNextColumn();
                if (runner.IsRunning || run?.Phase == WorkshopVendorRestockPhase.Paused)
                {
                    DrawRestockActions(review, automatic);
                }
                else
                {
                    drawMaterialActions();
                    if (actionLabel is not null)
                    {
                        ImGui.SameLine();
                        DrawRestockActions(review, automatic);
                    }
                }

                ImGui.EndTable();
            },
            54f);
    }

    private static string BuildReviewSummary(
        WorkshopVendorRestockReview review,
        bool automatic)
    {
        var parts = new List<string>();
        if (review.RetainerUnits > 0)
            parts.Add($"{review.RetainerUnits:N0} retainer units");
        if (automatic && review.VendorUnits > 0)
        {
            parts.Add($"{review.VendorUnits:N0} vendor units");
            parts.Add($"{review.MaximumGil:N0} gil maximum");
            parts.Add($"{review.Stops.Count:N0} vendor {(review.Stops.Count == 1 ? "stop" : "stops")}");
        }

        return string.Join(" · ", parts);
    }

    private string DescribeQuartermasterStatus() =>
        GetQuartermasterStatusColor() == MarketMafiosoUiTheme.Error
            ? "Retainer retrieval is temporarily unavailable. Vendor restock can still continue."
            : quartermaster.LastStatus;

    private static string RunStatusForLine(
        PersistedWorkshopVendorRestockLine line,
        PersistedWorkshopVendorRestockRun? run)
    {
        if (run?.Phase == WorkshopVendorRestockPhase.Completed &&
            line.PurchasedQuantity > 0 &&
            line.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
        {
            return "Purchased";
        }

        return run?.Phase == WorkshopVendorRestockPhase.Failed &&
               line.Status.Equals("Waiting", StringComparison.OrdinalIgnoreCase)
            ? "Not bought"
            : line.Status;
    }

    private System.Numerics.Vector4 GetQuartermasterStatusColor() =>
        quartermaster.LastStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
        quartermaster.LastStatus.Contains("not loaded", StringComparison.OrdinalIgnoreCase) ||
        quartermaster.LastStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        quartermaster.LastStatus.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            ? MarketMafiosoUiTheme.Error
            : MarketMafiosoUiTheme.Muted;

    private static System.Numerics.Vector4 RunStatusColor(PersistedWorkshopVendorRestockRun? run) =>
        run?.Phase switch
        {
            WorkshopVendorRestockPhase.Completed => MarketMafiosoUiTheme.Success,
            WorkshopVendorRestockPhase.Failed or
            WorkshopVendorRestockPhase.Indeterminate => MarketMafiosoUiTheme.Error,
            WorkshopVendorRestockPhase.Paused => MarketMafiosoUiTheme.Warning,
            _ => MarketMafiosoUiTheme.Muted,
        };

    private static System.Numerics.Vector4 LineStatusColor(string status) =>
        status.Contains("Verified", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Ready", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Purchased", StringComparison.OrdinalIgnoreCase)
            ? MarketMafiosoUiTheme.Success
            : status.Contains("Remaining", StringComparison.OrdinalIgnoreCase) ||
              status.Contains("Ceiling", StringComparison.OrdinalIgnoreCase)
                ? MarketMafiosoUiTheme.Warning
                : MarketMafiosoUiTheme.Muted;

    private sealed record MaterialTableRow(
        WorkshopMaterialProcurement Line,
        PersistedWorkshopVendorRestockLine? ActiveLine,
        PersistedWorkshopVendorRestockRun? Run);
}
