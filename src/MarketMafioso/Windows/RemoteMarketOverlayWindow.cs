using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MarketMafioso.MarketAcquisition.RemoteMarket;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows;

public sealed class RemoteMarketOverlayWindow : Window
{
    private readonly RemoteMarketController controller;
    private readonly HashSet<ulong> selectedListingIds = [];
    private bool confirmArmed;

    internal RemoteMarketOverlayWindow(RemoteMarketController controller)
        : base(
            "##MarketMafiosoRemoteMarketOverlay",
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoSavedSettings)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        IsOpen = true;
    }

    public override bool DrawConditions() => controller.IsAvailable && controller.IsMarketBoardResultVisible();

    public override void PreDraw()
    {
        if (controller.TryGetResultAnchor(out var anchor))
            ImGui.SetNextWindowPos(anchor, ImGuiCond.Always);
    }

    public override void Draw()
    {
        var view = controller.GetView();
        var batchActive = view.Batch is not null;

        if (view.Listings.Count == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Search an item to extend the listings");
            return;
        }

        selectedListingIds.RemoveWhere(id => view.Listings.All(listing => listing.ListingId != id) ||
            view.Listings.First(listing => listing.ListingId == id).AlreadyPurchased);

        var itemName = view.Listings[0].ItemName;
        ImGui.TextColored(MarketMafiosoUiTheme.Header, $"{itemName}{(view.Listings[0].IsHighQuality ? " (HQ)" : string.Empty)} — {view.Listings.Count} listings");

        var tableHeight = Math.Min(420f, (view.Listings.Count * ImGui.GetTextLineHeightWithSpacing()) + ImGui.GetTextLineHeightWithSpacing() + 8f);
        if (ImGui.BeginTable("##RemoteMarketListings", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit, new Vector2(360f, tableHeight)))
        {
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 44f);
            ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 76f);
            ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 96f);
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 30f);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 84f);
            ImGui.TableHeadersRow();

            foreach (var listing in view.Listings)
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
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(listing.Quantity.ToString());
                if (!selectable)
                    ImGui.EndDisabled();

                var muted = listing.AlreadyPurchased || status is RemoteMarketBatchItemStatus.Confirmed or RemoteMarketBatchItemStatus.Skipped;
                Cell(listing.UnitPrice.ToString("N0"), muted);
                Cell(listing.TotalGil.ToString("N0"), muted);
                Cell(listing.IsHighQuality ? "HQ" : string.Empty, muted);
                var state = listing.AlreadyPurchased
                    ? "purchased"
                    : status switch
                    {
                        RemoteMarketBatchItemStatus.Queued => "queued",
                        RemoteMarketBatchItemStatus.Sending => "sending",
                        RemoteMarketBatchItemStatus.Confirmed => "confirmed",
                        RemoteMarketBatchItemStatus.Failed => "FAILED",
                        RemoteMarketBatchItemStatus.Skipped => "skipped",
                        _ => string.Empty,
                    };
                if (status == RemoteMarketBatchItemStatus.Failed)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextColored(MarketMafiosoUiTheme.Error, state);
                }
                else if (status == RemoteMarketBatchItemStatus.Sending)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextColored(MarketMafiosoUiTheme.Header, state);
                }
                else
                {
                    Cell(state, muted);
                }
            }
            ImGui.EndTable();
        }

        ImGui.Separator();
        if (batchActive)
        {
            var batch = view.Batch!;
            ImGui.TextUnformatted($"Batch: {batch.CompletedCount}/{batch.TotalCount} done{(batch.FailedCount > 0 ? $", {batch.FailedCount} failed" : string.Empty)}");
            if (batch.Active && ImGui.Button("Cancel remaining"))
                controller.CancelBatch();
            if (!batch.Active && ImGui.Button("Clear batch"))
                controller.CancelBatch();
            if (view.LastOutcome is not null)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, view.LastOutcome);
            return;
        }

        var selectedListings = view.Listings.Where(listing => selectedListingIds.Contains(listing.ListingId)).ToArray();
        if (selectedListings.Length == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Select listings to buy");
            confirmArmed = false;
            return;
        }

        var totalQuantity = selectedListings.Sum(listing => (long)listing.Quantity);
        var totalGil = selectedListings.Aggregate(0UL, (sum, listing) => sum + listing.TotalGil);
        var label = selectedListings.Length == 1
            ? $"Buy {selectedListings[0].Quantity}x {selectedListings[0].ItemName} — {totalGil:N0} gil"
            : $"Buy {selectedListings.Length} listings ({totalQuantity:N0} items) — {totalGil:N0} gil";

        if (!confirmArmed)
        {
            if (ImGuiUi.Button(label, view.Available && view.ContextBlockReason is null))
                confirmArmed = true;
            if (view.ContextBlockReason is not null)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, view.ContextBlockReason);
            return;
        }

        ImGui.TextWrapped(label);
        if (ImGuiUi.Button("Confirm purchase", view.Available && view.ContextBlockReason is null))
        {
            confirmArmed = false;
            var error = controller.BeginBatch(selectedListingIds);
            if (error is not null)
                Plugin.ChatGui.PrintError($"[MMF] Remote market: {error}");
            selectedListingIds.Clear();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            confirmArmed = false;
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
