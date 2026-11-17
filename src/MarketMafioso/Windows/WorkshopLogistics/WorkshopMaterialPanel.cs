using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using Franthropy.Dalamud.UI.Tables;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Quartermaster;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.WorkshopLogistics;

internal sealed class WorkshopMaterialPanel
{
    private readonly Configuration config;
    private readonly QuartermasterIpcClient quartermaster;
    private readonly WorkshopVendorProcurementPlanner planner;
    private readonly WorkshopVendorRestockRunner runner;
    private readonly Func<IReadOnlyList<WorkshopMaterialAvailability>> getAvailability;
    private readonly Func<QuartermasterOwnerScope> getOwnerScope;
    private readonly Func<bool> canStageMarketAcquisition;
    private readonly Func<WorkshopMaterialProcurement, string> stageMarketAcquisition;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly DalamudTableProjection<MaterialTableRow> materialTable;
    private readonly WorkshopAcquisitionCell acquisitionCell;
    private bool shortagesOnly;
    private string? actionStatus;

    public WorkshopMaterialPanel(
        Configuration config,
        QuartermasterIpcClient quartermaster,
        WorkshopVendorProcurementPlanner planner,
        WorkshopVendorRestockRunner runner,
        Func<IReadOnlyList<WorkshopMaterialAvailability>> getAvailability,
        Func<QuartermasterOwnerScope> getOwnerScope,
        Func<bool> canStageMarketAcquisition,
        Func<WorkshopMaterialProcurement, string> stageMarketAcquisition,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.quartermaster = quartermaster ?? throw new ArgumentNullException(nameof(quartermaster));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.getAvailability = getAvailability ?? throw new ArgumentNullException(nameof(getAvailability));
        this.getOwnerScope = getOwnerScope ?? throw new ArgumentNullException(nameof(getOwnerScope));
        this.canStageMarketAcquisition = canStageMarketAcquisition ??
                                         throw new ArgumentNullException(nameof(canStageMarketAcquisition));
        this.stageMarketAcquisition = stageMarketAcquisition ??
                                      throw new ArgumentNullException(nameof(stageMarketAcquisition));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        acquisitionCell = new WorkshopAcquisitionCell(
            config,
            runner,
            getOwnerScope,
            canStageMarketAcquisition,
            stageMarketAcquisition,
            reviewRegistry,
            "workshop-logistics",
            "workshop-material",
            (itemId, buy) => StartSingleLineRestock(itemId, buy),
            status => actionStatus = status);
        materialTable = CreateMaterialTable();
    }

    public void Draw(IReadOnlyList<WorkshopMaterialAvailability>? availability = null)
    {
        availability ??= getAvailability();
        var review = BuildReview(availability);
        ImGuiUi.SectionHeader("Materials", MarketMafiosoUiTheme.Header);

        var run = runner.ActiveRun;
        if (GetQuartermasterStatusColor() == MarketMafiosoUiTheme.Error)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, DescribeQuartermasterStatus());
        var visibleStatus = run is null
            ? actionStatus
            : WorkshopVendorRestockPresentation.Describe(run, review);
        if (!string.IsNullOrWhiteSpace(visibleStatus))
            ImGui.TextColored(RunStatusColor(run), visibleStatus);

        if (runner.CanAcknowledgeUnresolvedPurchase && runner.UnresolvedPurchaseMessage is { } unresolved)
        {
            var unresolvedRun = config.ActiveWorkshopVendorBuyRun!;
            var unresolvedRunId = unresolvedRun.RunId;
            ImGui.TextWrapped(unresolved);
            ImGui.TextWrapped(unresolvedRun.Phase == GilVendorBuyPhase.Indeterminate
                ? "The purchase couldn't be confirmed automatically. Check your items and gil in game before clearing it. Clearing allows a new restock to buy again."
                : "Let the purchase finish verifying, or check your items and gil in game before clearing it. Clearing allows a new restock to buy again.");
            // Human-only: deliberately not registered with the agent bridge, so an agent can never clear purchase protection.
            if (ImGuiUi.Button("I checked in game - clear this", runner.CanAcknowledgeUnresolvedPurchase))
                runner.AcknowledgeUnresolvedPurchase(unresolvedRunId, out actionStatus);
        }

        var automatic = run is not null && (runner.IsRunning || run.Phase == WorkshopVendorRestockPhase.Paused)
            ? run.AutomaticallyBuyVendorMaterials
            : config.AutomaticallyBuyWorkshopVendorMaterials;
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
        var actionWidth = GetActionWidth(review);
        if (actionWidth > 0)
        {
            ImGuiUi.SameLineRight(actionWidth);
            DrawHeaderActions(review);
        }

        ImGui.Checkbox("Shortages only", ref shortagesOnly);
        var filtered = review.Materials
            .Where(line => !shortagesOnly || line.Availability.Shortage > 0)
            .ToList();

        DrawTable(filtered, review.Materials.Count, review.QueueSignature);
    }

    public WorkshopVendorRestockReview BuildReview(
        IReadOnlyList<WorkshopMaterialAvailability>? availability = null) =>
        planner.Build(
            availability ?? getAvailability(),
            config.WorkshopVendorApprovedQuantities,
            config.WorkshopVendorIncludedItems.ToHashSet(),
            config.WorkshopVendorExcludedItems.ToHashSet());

    private void DrawTable(
        IReadOnlyList<WorkshopMaterialProcurement> filtered,
        int totalCount,
        string queueSignature)
    {
        if (!materialTable.Begin(
                "WorkshopPrepMaterialsVendorV4",
                DalamudTableLayout.FitContent(DalamudTableLayout.DefaultFlags)))
            return;

        materialTable.DrawFilterRow();
        var active = runner.ActiveRun is { } candidate &&
                     (runner.IsRunning || candidate.Phase == WorkshopVendorRestockPhase.Paused)
            ? candidate
            : null;
        var rows = OrderForDisplay(filtered)
            .Select(line => new MaterialTableRow(
                line,
                active is not null &&
                active.DemandShareJobId is null &&
                string.Equals(active.QueueSignature, queueSignature, StringComparison.Ordinal)
                    ? active.Lines.FirstOrDefault(item => item.ItemId == line.Availability.ItemId)
                    : null))
            .ToArray();
        var visible = materialTable.Apply(rows, ImGui.TableGetSortSpecs());

        if (visible.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(
                MarketMafiosoUiTheme.Muted,
                totalCount == 0
                    ? "No workshop materials yet. Add projects to the prep queue."
                    : "No materials match the current filter.");
            materialTable.End();
            return;
        }

        for (var index = 0; index < visible.Count; index++)
        {
            var row = visible[index];
            materialTable.DrawSaneRow(
                visible,
                index,
                $"workshop-material-{row.Line.Availability.ItemId}",
                background: ResolveRowBackground(row.Line));
        }

        materialTable.End();
    }

    private DalamudTableProjection<MaterialTableRow> CreateMaterialTable() =>
        new(
        [
            new(
                "Item",
                1.05f,
                row => row.Line.Availability.ItemName,
                row => row.Line.Availability.ItemName,
                ImGuiTableColumnFlags.WidthStretch),
            new(
                "Stock",
                125f,
                row => BuildStockText(row.Line),
                row => ResolveStockSortKey(row.Line),
                ImGuiTableColumnFlags.WidthFixed |
                ImGuiTableColumnFlags.DefaultSort |
                ImGuiTableColumnFlags.PreferSortAscending,
                TextColor: row => ResolveStockTextColor(row.Line),
                Alignment: DalamudTableCellAlignment.Right),
            new(
                "On hand",
                100f,
                row => ResolveDisplayedPlayerQuantity(row.Line, row.ActiveLine).ToString("N0"),
                row => ResolveDisplayedPlayerQuantity(row.Line, row.ActiveLine),
                ImGuiTableColumnFlags.WidthFixed,
                Alignment: DalamudTableCellAlignment.Right),
            new(
                "Acquisition",
                1.55f,
                row => BuildAcquisitionFilterText(row.Line, row.ActiveLine),
                row => BuildAcquisitionFilterText(row.Line, row.ActiveLine),
                ImGuiTableColumnFlags.WidthStretch,
                Draw: row => acquisitionCell.Draw(row.Line, row.ActiveLine),
                DrawContextMenu: row => acquisitionCell.DrawContextMenu(row.Line, row.ActiveLine)),
        ], DalamudTableSelection<MaterialTableRow>.None);

    internal static WorkshopMaterialStockState ResolveStockState(WorkshopMaterialProcurement line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Availability.PlayerInventory >= line.Availability.Required)
            return WorkshopMaterialStockState.Ready;
        return ResolveAccessibleStock(line) >= line.Availability.Required
            ? WorkshopMaterialStockState.RetainerRequired
            : line.Availability.StockUnverified
                ? WorkshopMaterialStockState.Unverified
                : WorkshopMaterialStockState.Missing;
    }

    internal static long ResolveAccessibleStock(WorkshopMaterialProcurement line) =>
        checked((long)line.Availability.PlayerInventory + line.Availability.QuartermasterStock);

    internal static long ResolveStockDifferential(WorkshopMaterialProcurement line) =>
        checked(ResolveAccessibleStock(line) - line.Availability.Required);

    internal static Tuple<int, long> ResolveStockSortKey(WorkshopMaterialProcurement line) =>
        Tuple.Create((int)ResolveStockState(line), ResolveStockDifferential(line));

    internal static string BuildStockText(WorkshopMaterialProcurement line) =>
        line.Availability.StockUnverified
            ? $"{ResolveAccessibleStock(line):N0}+ / {line.Availability.Required:N0} (stock unverified)"
            : $"{ResolveAccessibleStock(line):N0} / {line.Availability.Required:N0}";

    internal static IReadOnlyList<WorkshopMaterialProcurement> OrderForDisplay(
        IEnumerable<WorkshopMaterialProcurement> lines) =>
        lines
            .OrderBy(ResolveStockSortKey)
            .ThenBy(line => line.Availability.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static int ResolveDisplayedPlayerQuantity(
        WorkshopMaterialProcurement line,
        WorkshopVendorRestockLineView? activeLine)
    {
        _ = activeLine;
        return line.Availability.PlayerInventory;
    }

    internal static string BuildAcquisitionFilterText(
        WorkshopMaterialProcurement line,
        WorkshopVendorRestockLineView? activeLine)
    {
        if (ResolveStockState(line) == WorkshopMaterialStockState.Ready)
            return string.Empty;
        if (activeLine is not null)
            return WorkshopAcquisitionCell.RunStatusForLine(activeLine, null);

        var parts = new List<string>();
        if (line.Availability.RetrievalNotice is { } notice)
            parts.Add(notice);
        if (line.RetainerPlannedQuantity > 0)
            parts.Add($"Retrieve {line.RetainerPlannedQuantity:N0} from retainers");
        if (line.VendorNeed > 0)
        {
            var candidate = line.SelectedCandidate ?? line.Candidates.FirstOrDefault();
            parts.Add(candidate is null
                ? "Craft / gather / market"
                : $"{candidate.Offer.NpcName} {candidate.Offer.UnitPriceGil:N0} gil");
        }

        return string.Join(" ", parts);
    }

    internal static MarketAcquisitionRequestLineDocument CreateMarketAcquisitionLine(
        WorkshopMaterialProcurement line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.VendorNeed <= 0)
            throw new ArgumentOutOfRangeException(nameof(line), "The workshop material has no uncovered quantity.");
        return new()
        {
            ItemId = line.Availability.ItemId,
            ItemName = line.Availability.ItemName,
            ItemKind = "Workshop material",
            QuantityMode = "TargetQuantity",
            TargetQuantity = checked((uint)line.VendorNeed),
            HqPolicy = "Either",
        };
    }

    internal static WorkshopVendorRestockReview BuildSingleLineReview(
        WorkshopVendorRestockReview review,
        uint itemId)
    {
        ArgumentNullException.ThrowIfNull(review);
        var materials = review.Materials
            .Where(line => line.Availability.ItemId == itemId)
            .ToArray();
        var stops = review.Stops
            .Select(stop => stop with
            {
                Lines = stop.Lines
                    .Where(line => line.Availability.ItemId == itemId)
                    .ToArray(),
            })
            .Where(stop => stop.Lines.Count > 0)
            .ToArray();
        return review with
        {
            Materials = materials,
            Stops = stops,
        };
    }

    private static Vector4 ResolveStockTextColor(WorkshopMaterialProcurement line) =>
        ResolveStockState(line) switch
        {
            WorkshopMaterialStockState.Ready => MarketMafiosoUiTheme.Success,
            WorkshopMaterialStockState.RetainerRequired => MarketMafiosoUiTheme.Warning,
            WorkshopMaterialStockState.Unverified => MarketMafiosoUiTheme.Warning,
            _ => MarketMafiosoUiTheme.Error,
        };

    private static Vector4 ResolveRowBackground(WorkshopMaterialProcurement line)
    {
        var color = ResolveStockTextColor(line);
        var alpha = ResolveStockState(line) == WorkshopMaterialStockState.Missing
            ? 0.18f
            : 0.12f;
        return new(color.X, color.Y, color.Z, alpha);
    }

    private void StartSingleLineRestock(
        uint itemId,
        bool automaticallyBuyVendorMaterials)
    {
        var review = BuildSingleLineReview(BuildReview(), itemId);
        if (review.Materials.Count == 0)
        {
            actionStatus = "The selected workshop material is no longer in the active queue.";
            return;
        }

        if (!runner.TryStart(
                review,
                getOwnerScope(),
                automaticallyBuyVendorMaterials,
                out var error))
        {
            actionStatus = error;
            return;
        }

        var line = review.Materials[0];
        actionStatus = automaticallyBuyVendorMaterials
            ? $"Buying {line.ApprovedVendorQuantity:N0} {line.Availability.ItemName}."
            : $"Retrieving {line.RetainerPlannedQuantity:N0} {line.Availability.ItemName}.";
    }

    private void DrawHeaderActions(WorkshopVendorRestockReview review)
    {
        var active = runner.ActiveRun;
        if (runner.IsRunning)
        {
            if (ImGuiUi.PrimaryButton("Pause", true))
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
            if (ImGuiUi.PrimaryButton("Resume", true) &&
                !runner.Resume(getOwnerScope(), runner.ResolveDemandSignature(() => review.QueueSignature), out var resumeError))
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
                    if (!runner.Resume(getOwnerScope(), runner.ResolveDemandSignature(() => review.QueueSignature), out var error))
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

        var automatic = config.AutomaticallyBuyWorkshopVendorMaterials;
        var canStart = getOwnerScope().IsAvailable &&
                       (review.RetainerUnits > 0 || (automatic && review.VendorUnits > 0));
        var label = BuildActionLabel(review, automatic);
        if (string.IsNullOrWhiteSpace(label))
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

    internal static string BuildActionLabel(WorkshopVendorRestockReview review, bool automatic)
    {
        if (review.RetainerUnits > 0 && automatic && review.VendorUnits > 0)
            return $"Restock {review.RetainerUnits + review.VendorUnits:N0}";
        if (automatic && review.VendorUnits > 0)
            return $"Buy {review.VendorUnits:N0}";
        if (review.RetainerUnits > 0)
            return $"Retrieve {review.RetainerUnits:N0}";
        return string.Empty;
    }

    private float GetActionWidth(WorkshopVendorRestockReview review)
    {
        var active = runner.ActiveRun;
        if (runner.IsRunning)
            return ButtonWidth("Pause") + ImGui.GetStyle().ItemSpacing.X + ButtonWidth("Stop");
        if (active?.Phase == WorkshopVendorRestockPhase.Paused)
            return ButtonWidth("Resume") + ImGui.GetStyle().ItemSpacing.X + ButtonWidth("Stop");

        var label = BuildActionLabel(review, config.AutomaticallyBuyWorkshopVendorMaterials);
        return string.IsNullOrWhiteSpace(label) ? 0 : ButtonWidth(label);
    }

    private static float ButtonWidth(string label) =>
        ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2);

    private string DescribeQuartermasterStatus() =>
        GetQuartermasterStatusColor() == MarketMafiosoUiTheme.Error
            ? "Retainer retrieval is temporarily unavailable. Vendor restock can still continue."
            : quartermaster.LastStatus;

    private System.Numerics.Vector4 GetQuartermasterStatusColor() =>
        quartermaster.LastStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
        quartermaster.LastStatus.Contains("not loaded", StringComparison.OrdinalIgnoreCase) ||
        quartermaster.LastStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        quartermaster.LastStatus.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            ? MarketMafiosoUiTheme.Error
            : MarketMafiosoUiTheme.Muted;

    private static System.Numerics.Vector4 RunStatusColor(WorkshopVendorRestockRunView? run) =>
        run?.Phase switch
        {
            WorkshopVendorRestockPhase.Completed => MarketMafiosoUiTheme.Success,
            WorkshopVendorRestockPhase.Failed or
            WorkshopVendorRestockPhase.Indeterminate => MarketMafiosoUiTheme.Error,
            WorkshopVendorRestockPhase.Paused => MarketMafiosoUiTheme.Warning,
            _ => MarketMafiosoUiTheme.Muted,
        };

    private sealed record MaterialTableRow(
        WorkshopMaterialProcurement Line,
        WorkshopVendorRestockLineView? ActiveLine);
}

internal enum WorkshopMaterialStockState
{
    Missing,
    Unverified,
    RetainerRequired,
    Ready,
}
