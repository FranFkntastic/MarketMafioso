using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.UI.Inputs;
using MarketMafioso.Quartermaster;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.WorkshopLogistics;

/// <summary>
/// The Acquisition cell of a workshop material line: retainer/vendor plan, run status, vendor
/// selection controls and the per-row Actions... menu (single-line restock, Market Acquisition staging).
/// Shared by the Materials panel and the split-share handover table.
/// </summary>
internal sealed class WorkshopAcquisitionCell
{
    private static readonly AgentBridgeActionArgumentSchema QuantityArguments = new(
        [new("quantity", AgentBridgeActionArgumentKind.Integer, Minimum: 0)]);
    private readonly Configuration config;
    private readonly WorkshopVendorRestockRunner runner;
    private readonly Func<QuartermasterOwnerScope> getOwnerScope;
    private readonly Func<bool> canStageMarketAcquisition;
    private readonly Func<WorkshopMaterialProcurement, string> stageMarketAcquisition;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly string controlIdPrefix;
    private readonly string imGuiIdPrefix;
    private readonly Action<uint, bool> startSingleLineRestock;
    private readonly Action<string> setStatus;

    /// <param name="controlIdPrefix">Agent bridge id prefix for the vendor-item controls.</param>
    /// <param name="imGuiIdPrefix">ImGui id prefix for buttons and popups.</param>
    /// <param name="startSingleLineRestock">(itemId, buyFromVendor) for the Actions... menu.</param>
    public WorkshopAcquisitionCell(
        Configuration config,
        WorkshopVendorRestockRunner runner,
        Func<QuartermasterOwnerScope> getOwnerScope,
        Func<bool> canStageMarketAcquisition,
        Func<WorkshopMaterialProcurement, string> stageMarketAcquisition,
        AgentBridgeUiReviewRegistry reviewRegistry,
        string controlIdPrefix,
        string imGuiIdPrefix,
        Action<uint, bool> startSingleLineRestock,
        Action<string> setStatus)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.getOwnerScope = getOwnerScope ?? throw new ArgumentNullException(nameof(getOwnerScope));
        this.canStageMarketAcquisition = canStageMarketAcquisition ??
                                         throw new ArgumentNullException(nameof(canStageMarketAcquisition));
        this.stageMarketAcquisition = stageMarketAcquisition ??
                                      throw new ArgumentNullException(nameof(stageMarketAcquisition));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        this.controlIdPrefix = controlIdPrefix;
        this.imGuiIdPrefix = imGuiIdPrefix;
        this.startSingleLineRestock = startSingleLineRestock ??
                                      throw new ArgumentNullException(nameof(startSingleLineRestock));
        this.setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
    }

    public void Draw(WorkshopMaterialProcurement line, WorkshopVendorRestockLineView? activeLine)
    {
        if (WorkshopMaterialPanel.ResolveStockState(line) == WorkshopMaterialStockState.Ready)
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

        if (line.Availability.RetrievalNotice is { } notice)
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, notice);
        if (line.RetainerPlannedQuantity > 0)
        {
            ImGui.TextColored(
                MarketMafiosoUiTheme.Warning,
                $"Retrieve {line.RetainerPlannedQuantity:N0} from retainers");
            if (line.VendorNeed <= 0)
            {
                DrawActionButton(line, activeLine);
                return;
            }
        }

        var candidate = line.SelectedCandidate ?? line.Candidates.FirstOrDefault();
        if (candidate is null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Craft / gather / market");
            DrawActionButton(line, activeLine);
            return;
        }

        ImGui.TextUnformatted($"{candidate.Offer.NpcName} · {candidate.Offer.UnitPriceGil:N0} gil each");
        DrawActionButton(line, activeLine);
        if (!line.CanBuyAutomatically)
        {
            DrawAccessState(candidate);
            return;
        }

        DrawVendorControls(line);
    }

    public void DrawContextMenu(WorkshopMaterialProcurement line, WorkshopVendorRestockLineView? activeLine)
    {
        if (!HasActions(line, activeLine))
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
            startSingleLineRestock(line.Availability.ItemId, false);
        }

        var candidate = line.SelectedCandidate ?? line.Candidates.FirstOrDefault();
        if (candidate is not null && line.VendorNeed > 0)
        {
            if (line.Selected &&
                line.ApprovedVendorQuantity > 0 &&
                ImGuiUi.MenuItem(
                    $"Buy {line.ApprovedVendorQuantity:N0} now",
                    runAvailable && line.CanBuyAutomatically))
            {
                startSingleLineRestock(line.Availability.ItemId, true);
            }
        }

        if (line.VendorNeed > 0 &&
            ImGuiUi.MenuItem(
                $"Add {line.VendorNeed:N0} to Market Acquisition",
                canStageMarketAcquisition()))
        {
            setStatus(stageMarketAcquisition(line));
        }
    }

    internal static string RunStatusForLine(
        WorkshopVendorRestockLineView line,
        WorkshopVendorRestockRunView? run) =>
        run?.Phase == WorkshopVendorRestockPhase.Failed &&
        line.Status.Equals("Waiting", StringComparison.OrdinalIgnoreCase)
            ? "Not bought"
            : line.Status;

    private void DrawActionButton(WorkshopMaterialProcurement line, WorkshopVendorRestockLineView? activeLine)
    {
        if (!HasActions(line, activeLine))
            return;
        var itemId = line.Availability.ItemId;
        ImGui.SameLine();
        if (ImGui.SmallButton($"Actions...##{imGuiIdPrefix}-actions-{itemId}"))
            ImGui.OpenPopup($"{imGuiIdPrefix}Actions{itemId}");
        if (!ImGui.BeginPopup($"{imGuiIdPrefix}Actions{itemId}"))
            return;
        DrawContextMenu(line, activeLine);
        ImGui.EndPopup();
    }

    private static bool HasActions(WorkshopMaterialProcurement line, WorkshopVendorRestockLineView? activeLine) =>
        activeLine is null &&
        WorkshopMaterialPanel.ResolveStockState(line) != WorkshopMaterialStockState.Ready &&
        (line.RetainerPlannedQuantity > 0 ||
         line.SelectedCandidate is not null ||
         line.Candidates.Count > 0 ||
         line.VendorNeed > 0);

    private void DrawVendorControls(WorkshopMaterialProcurement line)
    {
        var selected = line.Selected;
        var selectionEnabled = !runner.IsRunning &&
                               runner.ActiveRun?.Phase != WorkshopVendorRestockPhase.Paused;
        ImGui.BeginDisabled(!selectionEnabled);
        if (ImGui.Checkbox($"Buy##{imGuiIdPrefix}-selected-{line.Availability.ItemId}", ref selected))
            SetSelected(line.Availability.ItemId, selected);
        ImGui.EndDisabled();
        reviewRegistry.Register(
            $"{controlIdPrefix}.vendor-item.{line.Availability.ItemId}.selected",
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
        if (DalamudGroupedIntegerInput.InputGroupedInt($"##{imGuiIdPrefix}-quantity-{line.Availability.ItemId}", ref quantity, 0, 0))
        {
            quantity = Math.Clamp(quantity, 0, line.VendorNeed);
            SetQuantity(line.Availability.ItemId, quantity, line.VendorNeed);
        }
        ImGui.EndDisabled();
        reviewRegistry.Register(
            $"{controlIdPrefix}.vendor-item.{line.Availability.ItemId}.quantity",
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

    private static System.Numerics.Vector4 LineStatusColor(string status) =>
        status.Contains("Verified", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Ready", StringComparison.OrdinalIgnoreCase)
            ? MarketMafiosoUiTheme.Success
            : status.Contains("Remaining", StringComparison.OrdinalIgnoreCase) ||
              status.Contains("Ceiling", StringComparison.OrdinalIgnoreCase) ||
              status.Contains("No accessible vendor", StringComparison.OrdinalIgnoreCase)
                ? MarketMafiosoUiTheme.Warning
                : MarketMafiosoUiTheme.Muted;
}
