using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Tables;
using Lumina.Excel.Sheets;
using MarketMafioso.MarketAcquisition.MarketBoard;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows;

public sealed class MarketListingOverlayWindow : Window
{
    private static readonly Vector2 MinimumSize = new(480, 280);

    private readonly MarketBoardAcquisitionController controller;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly HashSet<ulong> selectedListingIds = [];
    private bool confirmArmed;
    private int cheapestTarget = 1;
    private bool pinned = true;
    private long observedRevision = -1;
    private uint cachedIconItemId;
    private ISharedImmediateTexture? cachedIcon;
    private IReadOnlyList<MarketListingRowView> projectedListings = Array.Empty<MarketListingRowView>();
    private long projectedRevision = -1;
    private readonly string[] projectedFilters = new string[7];
    private int projectedSortColumn = -1;
    private ImGuiSortDirection projectedSortDirection;
    private bool presentationActive;
    private MarketListingRowView[] selectedListings = [];
    private long selectedQuantity;
    private ulong selectedGil;

    private static readonly DalamudTableProjection<MarketListingRowView> tableProjection = new(
    [
        new("Qty", 48f, listing => listing.Quantity.ToString(), listing => listing.Quantity),
        new("Unit", 84f, listing => listing.UnitPrice.ToString("N0"), listing => listing.UnitPrice, ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort),
        new("Fee", 64f, listing => listing.TotalTax.ToString("N0"), listing => listing.TotalTax),
        new("Total", 104f, listing => listing.TotalGil.ToString("N0"), listing => listing.TotalGil),
        new("Mat", 36f, listing => listing.MateriaCount > 0 ? listing.MateriaCount.ToString() : string.Empty, listing => listing.MateriaCount),
        new("Retainer", 96f, listing => listing.RetainerName, listing => listing.RetainerName),
        new("State", 84f, listing => listing.AlreadyPurchased ? "purchased" : listing.BatchStatus?.ToString().ToLowerInvariant() ?? string.Empty),
    ]);

    internal MarketListingOverlayWindow(
        MarketBoardAcquisitionController controller,
        AgentBridgeUiReviewRegistry reviewRegistry)
        : base(
            "MMF Market Listings##MarketMafiosoMarketListingOverlay",
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumSize,
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public override bool DrawConditions() => controller.ShouldPresentOverlay();

    internal void SynchronizePresentationLifetime()
    {
        var active = controller.ShouldPresentOverlay();
        if (active && !presentationActive)
            IsOpen = true;
        presentationActive = active;
    }

    public override void PreDraw()
    {
        if (pinned && controller.TryGetResultBounds(out var anchor, out var maxHeight))
        {
            ImGui.SetNextWindowPos(anchor, ImGuiCond.Always);
            ImGui.SetNextWindowSizeConstraints(
                MinimumSize,
                new Vector2(float.MaxValue, Math.Max(MinimumSize.Y, maxHeight)));
        }
    }

    public override void Draw()
    {
        var view = controller.GetView();
        if (observedRevision != view.Revision)
            SynchronizeView(view);
        var batchActive = view.Batch is not null;

        if (view.Listings.Count == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, view.BrowseMessage);
            return;
        }

        DrawHeader(view);
        DrawEconomicsStrip(view);
        ImGui.Separator();
        DrawListingsTable(view, batchActive);
        ImGui.Separator();

        if (batchActive)
        {
            DrawBatch(view);
            return;
        }

        if (view.Verification is not null)
        {
            DrawPurchaseVerification(view.Verification);
            return;
        }

        DrawSelectionFooter(view);
    }

    private void SynchronizeView(MarketListingView view)
    {
        observedRevision = view.Revision;
        var buyableIds = view.Listings
            .Where(listing => !listing.AlreadyPurchased)
            .Select(listing => listing.ListingId)
            .ToHashSet();
        selectedListingIds.RemoveWhere(id => !buyableIds.Contains(id));
        foreach (var listingId in controller.ConsumePendingSelectionRestoreIds())
        {
            if (buyableIds.Contains(listingId))
                selectedListingIds.Add(listingId);
        }

        if (view.Listings.Count > 0 && cachedIconItemId != view.Listings[0].ItemId)
        {
            cachedIconItemId = view.Listings[0].ItemId;
            cachedIcon = ResolveItemIcon(cachedIconItemId);
        }

        if (controller.ConsumePendingSelectionMaxPrice() is { } maxPrice)
        {
            selectedListingIds.Clear();
            foreach (var listing in view.Listings)
            {
                if (!listing.AlreadyPurchased && (maxPrice == 0 || listing.UnitPrice <= maxPrice))
                    selectedListingIds.Add(listing.ListingId);
            }
            confirmArmed = false;
        }

        RebuildSelection(view);
    }

    private void DrawHeader(MarketListingView view)
    {
        var first = view.Listings[0];
        if (cachedIcon?.TryGetWrap(out var icon, out _) == true)
        {
            ImGui.Image(icon.Handle, new Vector2(28, 28));
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, $"{first.ItemName}{(first.IsHighQuality ? " (HQ)" : string.Empty)}");
        var gilText = view.GilOnHand is { } gil ? $"{gil:N0} gil on hand" : "gil unavailable";
        var listingCountText = view.Listings.Count < view.ExpectedListingCount
            ? $"{view.Listings.Count} of {view.ExpectedListingCount} listings"
            : $"{view.Listings.Count} listings";
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{listingCountText} · {gilText}");
        ImGui.EndGroup();

        if (view.MarketContextSummary is { } marketContextSummary)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, marketContextSummary);

        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 34f);
        if (ImGui.SmallButton(pinned ? "Unpin" : "Pin"))
            pinned = !pinned;
    }

    private static void DrawEconomicsStrip(MarketListingView view)
    {
        if (view.Economics is not { } economics)
            return;

        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            $"Cheapest {economics.CheapestUnitPrice:N0}p · Median {economics.MedianUnitPrice:N0}p · Mean {economics.MeanUnitPrice:N0}p");

        if (economics.TrendDelta is not { } delta || Math.Abs(delta) < 0.005)
            return;
        ImGui.SameLine();
        ImGui.TextColored(
            delta > 0 ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Muted,
            delta > 0 ? $"▲ {delta:P0} above sale avg" : $"▼ {-delta:P0} below sale avg");
    }

    private void DrawListingsTable(MarketListingView view, bool batchActive)
    {
        var tableHeight = Math.Min(460f, Math.Max(140f, ImGui.GetContentRegionAvail().Y - 110f));
        if (!tableProjection.Begin("##RMCListingGrid", tableHeight))
            return;

        tableProjection.DrawFilterRow();
        tableProjection.DrawClippedRows(
            GetProjectedListings(view, ImGui.TableGetSortSpecs()),
            (listing, _) => DrawListingRow(listing, view, batchActive));
        tableProjection.End();
    }

    private void DrawListingRow(MarketListingRowView listing, MarketListingView view, bool batchActive)
    {
        var status = listing.BatchStatus;
        var selected = selectedListingIds.Contains(listing.ListingId);
        var selectable = !listing.AlreadyPurchased && status is null && !batchActive;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (!selectable)
            ImGui.BeginDisabled();
        if (ImGui.Selectable($"##rmsel{listing.ListingId}", selected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap) && selectable)
        {
            if (selected)
                selectedListingIds.Remove(listing.ListingId);
            else
                selectedListingIds.Add(listing.ListingId);
            confirmArmed = false;
            RebuildSelection(view);
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(listing.Quantity.ToString());
        if (!selectable)
            ImGui.EndDisabled();

        var muted = listing.AlreadyPurchased || status is MarketListingBatchStatus.Confirmed or MarketListingBatchStatus.Skipped;
        Cell(listing.UnitPrice.ToString("N0"), muted);
        Cell(listing.TotalTax.ToString("N0"), muted);
        Cell(listing.TotalGil.ToString("N0"), muted);
        Cell(listing.MateriaCount > 0 ? listing.MateriaCount.ToString() : string.Empty, muted);
        Cell(listing.RetainerName, muted);

        ImGui.TableNextColumn();
        if (status == MarketListingBatchStatus.Failed)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "FAILED");
        else if (status == MarketListingBatchStatus.Sending)
            ImGui.TextColored(MarketMafiosoUiTheme.Header, "sending");
        else if (listing.AlreadyPurchased)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "purchased");
        else if (status is not null)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, status.Value.ToString().ToLowerInvariant());
    }

    private unsafe IReadOnlyList<MarketListingRowView> GetProjectedListings(
        MarketListingView view,
        ImGuiTableSortSpecsPtr sortSpecs)
    {
        var sortColumn = -1;
        var sortDirection = (ImGuiSortDirection)0;
        if (sortSpecs.Handle != null && sortSpecs.SpecsCount > 0)
        {
            sortColumn = sortSpecs.Specs.ColumnIndex;
            sortDirection = sortSpecs.Specs.SortDirection;
        }

        var changed = projectedRevision != view.Revision ||
                      projectedSortColumn != sortColumn ||
                      projectedSortDirection != sortDirection;
        for (var index = 0; index < projectedFilters.Length; index++)
        {
            if (!string.Equals(projectedFilters[index], tableProjection.Filters[index], StringComparison.Ordinal))
                changed = true;
        }

        if (!changed)
            return projectedListings;

        projectedListings = tableProjection.Apply(view.Listings, sortSpecs);
        projectedRevision = view.Revision;
        projectedSortColumn = sortColumn;
        projectedSortDirection = sortDirection;
        for (var index = 0; index < projectedFilters.Length; index++)
            projectedFilters[index] = tableProjection.Filters[index];
        return projectedListings;
    }

    private void DrawBatch(MarketListingView view)
    {
        var batch = view.Batch!;
        ImGui.ProgressBar(batch.TotalCount == 0 ? 0f : (float)batch.CompletedCount / batch.TotalCount, new Vector2(-1, 0),
            $"{batch.CompletedCount}/{batch.TotalCount}");
        if (batch.FailedCount > 0)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, $"{batch.FailedCount} failed");
        if (batch.Active && ImGui.Button("Cancel remaining"))
            controller.CancelBatch();
        if (!batch.Active && ImGui.Button("Clear batch"))
            controller.CancelBatch();
        if (view.LastOutcome is { } outcome)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, outcome);
    }

    private static void DrawPurchaseVerification(MarketListingVerificationView verification)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Verifying prices...");
        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            verification.ListingCount == 1
                ? $"{verification.Quantity:N0} item · {verification.TotalGil:N0} gil"
                : $"{verification.ListingCount} listings · {verification.Quantity:N0} items · {verification.TotalGil:N0} gil");
    }

    private void DrawSelectionFooter(MarketListingView view)
    {
        if (ImGui.SmallButton("All"))
        {
            foreach (var listing in view.Listings)
            {
                if (!listing.AlreadyPurchased)
                    selectedListingIds.Add(listing.ListingId);
            }
            confirmArmed = false;
            RebuildSelection(view);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("None"))
        {
            selectedListingIds.Clear();
            confirmArmed = false;
            RebuildSelection(view);
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        ImGui.InputInt("##cheapestTarget", ref cheapestTarget, 0, 0);
        cheapestTarget = Math.Clamp(cheapestTarget, 1, 99999);
        ImGui.SameLine();
        var canSelectCheapest = view.Listings.Any(listing => !listing.AlreadyPurchased);
        if (!canSelectCheapest)
            ImGui.BeginDisabled();
        var selectCheapestClicked = ImGui.SmallButton("Select cheapest for N items");
        if (!canSelectCheapest)
            ImGui.EndDisabled();
        if (selectCheapestClicked && canSelectCheapest)
            SelectCheapest(view);
        RegisterLastReviewedAction(
            "market-listings.select-cheapest",
            "Select the cheapest current listing",
            canSelectCheapest,
            () => SelectCheapest(view),
            $"item={view.Listings[0].ItemName}; targetQuantity={cheapestTarget}");

        if (selectedListings.Length == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Select listings to buy");
            confirmArmed = false;
            return;
        }

        var affordable = view.GilOnHand is null || view.GilOnHand.Value >= selectedGil;
        var label = selectedListings.Length == 1
            ? $"Buy {selectedListings[0].Quantity}x {selectedListings[0].ItemName} - {selectedGil:N0} gil"
            : $"Buy {selectedListings.Length} listings ({selectedQuantity:N0} items) - {selectedGil:N0} gil";
        if (!affordable)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, $"Insufficient gil for this selection ({view.GilOnHand:N0} on hand).");

        if (!confirmArmed)
        {
            var canArmPurchase = view.Available && view.ContextBlockReason is null && affordable;
            if (ImGuiUi.Button(label, canArmPurchase))
                confirmArmed = true;
            RegisterLastReviewedAction(
                "market-listings.arm-purchase",
                label,
                canArmPurchase,
                () => confirmArmed = true,
                FormatSelectionReviewValue());
            if (view.ContextBlockReason is not null)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, view.ContextBlockReason);
            return;
        }

        ImGui.TextWrapped(label);
        var canConfirmPurchase = view.Available && view.ContextBlockReason is null && affordable;
        if (ImGuiUi.Button("Confirm purchase", canConfirmPurchase))
            ConfirmSelection(view);
        RegisterLastReviewedAction(
            "market-listings.confirm-purchase",
            "Confirm the reviewed listing purchase",
            canConfirmPurchase,
            () => ConfirmSelection(view),
            FormatSelectionReviewValue());
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            confirmArmed = false;
    }

    private void RebuildSelection(MarketListingView view)
    {
        selectedListings = view.Listings
            .Where(listing => selectedListingIds.Contains(listing.ListingId))
            .ToArray();
        selectedQuantity = selectedListings.Sum(listing => (long)listing.Quantity);
        selectedGil = selectedListings.Aggregate(0UL, (sum, listing) => sum + listing.TotalGil);
    }

    private void SelectCheapest(MarketListingView view)
    {
        selectedListingIds.Clear();
        var accumulated = 0L;
        foreach (var listing in view.Listings
                     .Where(listing => !listing.AlreadyPurchased)
                     .OrderBy(listing => listing.UnitPrice)
                     .ThenByDescending(listing => listing.Quantity))
        {
            if (accumulated >= cheapestTarget)
                break;
            selectedListingIds.Add(listing.ListingId);
            accumulated += listing.Quantity;
        }
        confirmArmed = false;
        RebuildSelection(view);
    }

    private void ConfirmSelection(MarketListingView view)
    {
        confirmArmed = false;
        var error = controller.BeginBatch(selectedListingIds);
        if (error is not null)
            Plugin.ChatGui.PrintError($"[MMF] Market listings: {error}");
        else
            selectedListingIds.Clear();
        RebuildSelection(view);
    }

    private string FormatSelectionReviewValue() =>
        $"listingIds={string.Join(",", selectedListings.Select(listing => listing.ListingId))}; " +
        $"quantity={selectedQuantity}; totalGil={selectedGil}";

    private void RegisterLastReviewedAction(
        string id,
        string label,
        bool enabled,
        System.Action invoke,
        string? value) =>
        reviewRegistry.Register(
            id,
            label,
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            selected: false,
            value,
            arguments: null,
            surfaceId: "market-listings",
            mutating: true,
            completionOperationKind: null,
            _ =>
            {
                invoke();
                return AgentBridgeUiActionResult.Ok("Market-listing control was invoked.");
            });

    private static ISharedImmediateTexture? ResolveItemIcon(uint itemId)
    {
        try
        {
            var item = Plugin.DataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
            if (item is null)
                return null;
            return Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.Value.Icon));
        }
        catch
        {
            return null;
        }
    }

    private static void Cell(string text, bool muted)
    {
        ImGui.TableNextColumn();
        if (muted)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, text);
        else
            ImGui.TextUnformatted(text);
    }
}
