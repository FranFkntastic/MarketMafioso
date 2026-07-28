using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Vendors;
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
    private string searchText = string.Empty;
    private bool shortagesOnly;
    private string? actionStatus;

    public WorkshopMaterialPanel(
        Configuration config,
        QuartermasterIpcClient quartermaster,
        WorkshopVendorProcurementPlanner planner,
        WorkshopVendorRestockRunner runner,
        Func<IReadOnlyList<WorkshopMaterialAvailability>> getAvailability,
        Func<QuartermasterOwnerScope> getOwnerScope,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.quartermaster = quartermaster ?? throw new ArgumentNullException(nameof(quartermaster));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.getAvailability = getAvailability ?? throw new ArgumentNullException(nameof(getAvailability));
        this.getOwnerScope = getOwnerScope ?? throw new ArgumentNullException(nameof(getOwnerScope));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
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

        ImGui.SetNextItemWidth(Math.Max(220f, ImGui.GetContentRegionAvail().X - 300f));
        ImGui.InputTextWithHint("##workshopMaterialSearch", "Filter materials...", ref searchText, 128);
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
        if (!ImGui.BeginTable("WorkshopPrepMaterialsVendorV2", 4, ImGuiUi.InteractiveTableFlags))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, 118);
        ImGui.TableSetupColumn("Stock", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("Acquisition", ImGuiTableColumnFlags.WidthStretch, 1.9f);
        ImGui.TableHeadersRow();

        if (filtered.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(
                MarketMafiosoUiTheme.Muted,
                totalCount == 0
                    ? "No workshop materials yet. Add projects to the prep queue."
                    : "No materials match the current filter.");
            ImGui.EndTable();
            return;
        }

        foreach (var line in filtered)
        {
            var activeLine = runner.ActiveRun is { } active &&
                             string.Equals(
                                 active.QueueSignature,
                                 queueSignature,
                                 StringComparison.Ordinal)
                ? active.Lines.FirstOrDefault(item => item.ItemId == line.Availability.ItemId)
                : null;
            ImGui.PushID(checked((int)line.Availability.ItemId));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Availability.ItemName);
            ImGui.TableNextColumn();
            DrawNeed(line);
            ImGui.TableNextColumn();
            DrawStock(line, activeLine);
            ImGui.TableNextColumn();
            DrawAcquisition(line, activeLine);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static void DrawNeed(WorkshopMaterialProcurement line)
    {
        ImGui.TextUnformatted($"{line.Availability.Required:N0} required");
        ImGui.TextColored(
            line.VendorNeed > 0 ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Success,
            line.VendorNeed > 0 ? $"{line.VendorNeed:N0} missing" : "Covered");
    }

    private static void DrawStock(
        WorkshopMaterialProcurement line,
        PersistedWorkshopVendorRestockLine? activeLine)
    {
        var playerQuantity = activeLine?.LivePlayerQuantity ?? line.Availability.PlayerInventory;
        ImGui.TextUnformatted($"{playerQuantity:N0} on hand");
        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            $"{line.Availability.QuartermasterStock:N0} on retainers");
    }

    private void DrawAcquisition(
        WorkshopMaterialProcurement line,
        PersistedWorkshopVendorRestockLine? activeLine)
    {
        var candidate = line.SelectedCandidate ?? line.Candidates.FirstOrDefault();
        if (candidate is null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Craft / gather / market");
            return;
        }

        ImGui.TextUnformatted($"{candidate.Offer.NpcName} · {candidate.Offer.UnitPriceGil:N0} gil each");
        if (activeLine is not null)
        {
            var status = RunStatusForLine(activeLine, runner.ActiveRun);
            ImGui.TextColored(LineStatusColor(status), status);
            return;
        }

        if (!line.CanBuyAutomatically)
        {
            DrawAccessState(candidate);
            return;
        }

        DrawVendorControls(line);
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
}
