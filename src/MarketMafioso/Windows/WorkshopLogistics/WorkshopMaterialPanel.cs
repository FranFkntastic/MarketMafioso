using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.UI.Tables;
using MarketMafioso.MarketAcquisition;
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
    private readonly Func<bool> canStageMarketAcquisition;
    private readonly Func<WorkshopMaterialProcurement, string> stageMarketAcquisition;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly DalamudTableProjection<MaterialTableRow> materialTable;
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
        var active = runner.ActiveRun;
        var rows = OrderForDisplay(filtered)
            .Select(line => new MaterialTableRow(
                line,
                active is not null &&
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

        foreach (var row in visible)
        {
            materialTable.DrawRow(
                row,
                ResolveRowBackground(row.Line),
                id: $"workshop-material-{row.Line.Availability.ItemId}");
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
                Draw: DrawAcquisitionCell,
                DrawContextMenu: DrawAcquisitionContextMenu),
        ]);

    internal static WorkshopMaterialStockState ResolveStockState(WorkshopMaterialProcurement line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Availability.PlayerInventory >= line.Availability.Required)
            return WorkshopMaterialStockState.Ready;
        return ResolveAccessibleStock(line) >= line.Availability.Required
            ? WorkshopMaterialStockState.RetainerRequired
            : WorkshopMaterialStockState.Missing;
    }

    internal static long ResolveAccessibleStock(WorkshopMaterialProcurement line) =>
        checked((long)line.Availability.PlayerInventory + line.Availability.QuartermasterStock);

    internal static long ResolveStockDifferential(WorkshopMaterialProcurement line) =>
        checked(ResolveAccessibleStock(line) - line.Availability.Required);

    internal static Tuple<int, long> ResolveStockSortKey(WorkshopMaterialProcurement line) =>
        Tuple.Create((int)ResolveStockState(line), ResolveStockDifferential(line));

    internal static string BuildStockText(WorkshopMaterialProcurement line) =>
        $"{ResolveAccessibleStock(line):N0} / {line.Availability.Required:N0}";

    internal static IReadOnlyList<WorkshopMaterialProcurement> OrderForDisplay(
        IEnumerable<WorkshopMaterialProcurement> lines) =>
        lines
            .OrderBy(ResolveStockSortKey)
            .ThenBy(line => line.Availability.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static int ResolveDisplayedPlayerQuantity(
        WorkshopMaterialProcurement line,
        PersistedWorkshopVendorRestockLine? activeLine)
    {
        _ = activeLine;
        return line.Availability.PlayerInventory;
    }

    internal static string BuildAcquisitionFilterText(
        WorkshopMaterialProcurement line,
        PersistedWorkshopVendorRestockLine? activeLine)
    {
        if (ResolveStockState(line) == WorkshopMaterialStockState.Ready)
            return string.Empty;
        if (activeLine is not null)
            return RunStatusForLine(activeLine, null);

        var parts = new List<string>();
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

    private void DrawAcquisitionCell(MaterialTableRow row) =>
        DrawAcquisition(row);

    private void DrawAcquisition(
        MaterialTableRow row)
    {
        var line = row.Line;
        var activeLine = row.ActiveLine;
        if (ResolveStockState(line) == WorkshopMaterialStockState.Ready)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
            return;
        }

        if (activeLine is not null)
        {
            var status = RunStatusForLine(activeLine, runner.ActiveRun);
            ImGui.TextColored(LineStatusColor(status), status);
            return;
        }

        if (line.RetainerPlannedQuantity > 0)
        {
            ImGui.TextColored(
                MarketMafiosoUiTheme.Warning,
                $"Retrieve {line.RetainerPlannedQuantity:N0} from retainers");
            if (line.VendorNeed <= 0)
            {
                DrawAcquisitionActionButton(row);
                return;
            }
        }

        var candidate = line.SelectedCandidate ?? line.Candidates.FirstOrDefault();
        if (candidate is null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Craft / gather / market");
            DrawAcquisitionActionButton(row);
            return;
        }

        ImGui.TextUnformatted($"{candidate.Offer.NpcName} · {candidate.Offer.UnitPriceGil:N0} gil each");
        DrawAcquisitionActionButton(row);
        if (!line.CanBuyAutomatically)
        {
            DrawAccessState(candidate);
            return;
        }

        DrawVendorControls(line);
    }

    private void DrawAcquisitionActionButton(MaterialTableRow row)
    {
        if (!HasAcquisitionActions(row))
            return;
        ImGui.SameLine();
        if (ImGui.SmallButton($"Actions...##workshop-material-actions-{row.Line.Availability.ItemId}"))
            ImGui.OpenPopup($"WorkshopMaterialActions{row.Line.Availability.ItemId}");
        if (!ImGui.BeginPopup($"WorkshopMaterialActions{row.Line.Availability.ItemId}"))
            return;
        DrawAcquisitionContextMenu(row);
        ImGui.EndPopup();
    }

    private bool HasAcquisitionActions(MaterialTableRow row) =>
        row.ActiveLine is null &&
        ResolveStockState(row.Line) != WorkshopMaterialStockState.Ready &&
        (row.Line.RetainerPlannedQuantity > 0 ||
         row.Line.SelectedCandidate is not null ||
         row.Line.Candidates.Count > 0 ||
         row.Line.VendorNeed > 0);

    private void DrawAcquisitionContextMenu(MaterialTableRow row)
    {
        var line = row.Line;
        if (!HasAcquisitionActions(row))
        {
            ImGui.TextDisabled("No acquisition action");
            return;
        }

        var runAvailable = !runner.IsRunning &&
                           runner.ActiveRun?.Phase != WorkshopVendorRestockPhase.Paused &&
                           getOwnerScope().IsAvailable;
        if (line.RetainerPlannedQuantity > 0 &&
            ImGuiUi.MenuItem($"Retrieve {line.RetainerPlannedQuantity:N0} now", runAvailable))
        {
            StartSingleLineRestock(line.Availability.ItemId, automaticallyBuyVendorMaterials: false);
        }

        var candidate = line.SelectedCandidate ?? line.Candidates.FirstOrDefault();
        if (candidate is not null && line.VendorNeed > 0)
        {
            var selectionEnabled = !runner.IsRunning &&
                                   runner.ActiveRun?.Phase != WorkshopVendorRestockPhase.Paused;
            if (ImGuiUi.MenuItem(
                    line.Selected ? "Exclude vendor purchase" : "Include vendor purchase",
                    selectionEnabled))
            {
                SetSelected(line.Availability.ItemId, !line.Selected);
            }

            if (line.Selected &&
                line.ApprovedVendorQuantity > 0 &&
                ImGuiUi.MenuItem(
                    $"Buy {line.ApprovedVendorQuantity:N0} now",
                    runAvailable && line.CanBuyAutomatically))
            {
                StartSingleLineRestock(line.Availability.ItemId, automaticallyBuyVendorMaterials: true);
            }
        }

        if (line.VendorNeed > 0 &&
            ImGuiUi.MenuItem(
                $"Add {line.VendorNeed:N0} to Market Acquisition",
                canStageMarketAcquisition()))
        {
            actionStatus = stageMarketAcquisition(line);
        }
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

    private void DrawVendorControls(WorkshopMaterialProcurement line)
    {
        var selected = line.Selected;
        var selectionEnabled = config.AutomaticallyBuyWorkshopVendorMaterials &&
                               !runner.IsRunning &&
                               runner.ActiveRun?.Phase != WorkshopVendorRestockPhase.Paused;
        ImGui.BeginDisabled(!selectionEnabled);
        if (ImGui.Checkbox("Buy##selected", ref selected))
            SetSelected(line.Availability.ItemId, selected);
        ImGui.EndDisabled();
        reviewRegistry.Register(
            $"workshop-logistics.vendor-item.{line.Availability.ItemId}.selected",
            $"Include {line.Availability.ItemName} in vendor restock",
            AgentBridgeUiControlKind.Toggle,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            selectionEnabled,
            selected,
            selected ? "Included" : "Excluded",
            () => SetSelected(line.Availability.ItemId, !line.Selected));

        ImGui.SameLine();
        var quantity = line.ApprovedVendorQuantity;
        var quantityEnabled = selectionEnabled && selected;
        ImGui.BeginDisabled(!quantityEnabled);
        ImGui.SetNextItemWidth(78f);
        if (ImGui.InputInt("##quantity", ref quantity, 0))
        {
            quantity = Math.Clamp(quantity, 0, line.VendorNeed);
            SetQuantity(line.Availability.ItemId, quantity, line.VendorNeed);
        }
        ImGui.EndDisabled();
        reviewRegistry.Register(
            $"workshop-logistics.vendor-item.{line.Availability.ItemId}.quantity",
            $"Set {line.Availability.ItemName} vendor quantity",
            AgentBridgeUiControlKind.Input,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            quantityEnabled,
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

        ImGui.SameLine();
        var displayQuantity = Math.Clamp(quantity, 0, line.VendorNeed);
        var approvedGil = checked((ulong)displayQuantity * line.SelectedCandidate!.Offer.UnitPriceGil);
        ImGui.TextColored(
            line.IsCraftable ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Muted,
            line.IsCraftable
                ? $"{approvedGil:N0} gil · Craftable {(selected ? "override" : "— review price")}"
                : $"{approvedGil:N0} gil");
    }

    private static void DrawAccessState(WorkshopVendorCandidate candidate)
    {
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

    private void SetSelected(uint itemId, bool selected)
    {
        config.WorkshopVendorExcludedItems.RemoveAll(candidate => candidate == itemId);
        config.WorkshopVendorIncludedItems.RemoveAll(candidate => candidate == itemId);
        if (selected)
            config.WorkshopVendorIncludedItems.Add(itemId);
        else
            config.WorkshopVendorExcludedItems.Add(itemId);
        config.Save();
    }

    private void SetQuantity(uint itemId, int quantity, int maximum)
    {
        config.WorkshopVendorApprovedQuantities[itemId] = Math.Clamp(quantity, 0, maximum);
        config.Save();
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

    private static string RunStatusForLine(
        PersistedWorkshopVendorRestockLine line,
        PersistedWorkshopVendorRestockRun? run) =>
        run?.Phase == WorkshopVendorRestockPhase.Failed &&
        line.Status.Equals("Waiting", StringComparison.OrdinalIgnoreCase)
            ? "Not bought"
            : line.Status;

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
        status.Equals("Ready", StringComparison.OrdinalIgnoreCase)
            ? MarketMafiosoUiTheme.Success
            : status.Contains("Remaining", StringComparison.OrdinalIgnoreCase) ||
              status.Contains("Ceiling", StringComparison.OrdinalIgnoreCase)
                ? MarketMafiosoUiTheme.Warning
                : MarketMafiosoUiTheme.Muted;

    private sealed record MaterialTableRow(
        WorkshopMaterialProcurement Line,
        PersistedWorkshopVendorRestockLine? ActiveLine);
}

internal enum WorkshopMaterialStockState
{
    Missing,
    RetainerRequired,
    Ready,
}
