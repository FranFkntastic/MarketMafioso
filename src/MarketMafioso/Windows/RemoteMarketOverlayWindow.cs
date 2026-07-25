using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using MarketMafioso.MarketAcquisition.RemoteMarket;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows;

public sealed class RemoteMarketOverlayWindow : Window
{
    private static readonly Vector2 MinimumSize = new(480, 280);

    private readonly RemoteMarketController controller;
    private readonly HashSet<ulong> selectedListingIds = [];
    private bool confirmArmed;
    private int cheapestTarget = 1;
    private bool pinned = true;

    internal RemoteMarketOverlayWindow(RemoteMarketController controller)
        : base(
            "MMF Remote Market##MarketMafiosoRemoteMarketOverlay",
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = MinimumSize,
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public override void Update() => IsOpen = true;

    public override bool DrawConditions() => controller.IsAvailable && controller.IsMarketBoardResultVisible();

    public override void PreDraw()
    {
        if (pinned && controller.TryGetResultAnchor(out var anchor))
        {
            ImGui.SetNextWindowPos(anchor, ImGuiCond.Always);
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowSizeConstraints(
                MinimumSize,
                new Vector2(float.MaxValue, Math.Max(MinimumSize.Y, (viewport.WorkPos.Y + viewport.WorkSize.Y) - anchor.Y - 8f)));
        }
    }

    public override void Draw()
    {
        var view = controller.GetView();
        var batchActive = view.Batch is not null;

        if (view.Listings.Count == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Search an item in the market board window to load listings here.");
            return;
        }

        selectedListingIds.RemoveWhere(id => view.Listings.All(listing => listing.ListingId != id) ||
            view.Listings.First(listing => listing.ListingId == id).AlreadyPurchased);

        DrawHeader(view);
        ImGui.Separator();
        DrawListingsTable(view, batchActive);
        ImGui.Separator();

        if (batchActive)
        {
            DrawBatch(view.Batch!);
            return;
        }

        DrawSelectionFooter(view);
    }

    private void DrawHeader(RemoteMarketView view)
    {
        var first = view.Listings[0];
        var icon = ResolveItemIcon(first.ItemId);
        if (icon is not null)
        {
            ImGui.Image(icon.Handle, new Vector2(28, 28));
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, $"{first.ItemName}{(first.IsHighQuality ? " (HQ)" : string.Empty)}");
        var gilText = view.GilOnHand is { } gil ? $"{gil:N0} gil on hand" : "gil unavailable";
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{view.Listings.Count} listings · {gilText}");
        ImGui.EndGroup();

        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 34f);
        if (ImGui.SmallButton(pinned ? "Unpin" : "Pin"))
            pinned = !pinned;
    }

    private void DrawListingsTable(RemoteMarketView view, bool batchActive)
    {
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Sortable;
        var tableHeight = Math.Min(460f, Math.Max(140f, ImGui.GetContentRegionAvail().Y - 110f));
        if (!ImGui.BeginTable("##RemoteMarketListings", 6, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 48f);
        ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, 84f);
        ImGui.TableSetupColumn("Fee", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 104f);
        ImGui.TableSetupColumn("Mat", ImGuiTableColumnFlags.WidthFixed, 36f);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableHeadersRow();

        var rows = SortListings(view.Listings, ImGui.TableGetSortSpecs());
        foreach (var listing in rows)
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
            Cell(listing.TotalTax.ToString("N0"), muted);
            Cell(listing.TotalGil.ToString("N0"), muted);
            Cell(listing.MateriaCount > 0 ? listing.MateriaCount.ToString() : string.Empty, muted);

            ImGui.TableNextColumn();
            if (status == RemoteMarketBatchItemStatus.Failed)
                ImGui.TextColored(MarketMafiosoUiTheme.Error, "FAILED");
            else if (status == RemoteMarketBatchItemStatus.Sending)
                ImGui.TextColored(MarketMafiosoUiTheme.Header, "sending");
            else if (listing.AlreadyPurchased)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "purchased");
            else if (status is not null)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, status.Value.ToString().ToLowerInvariant());
        }
        ImGui.EndTable();
    }

    private void DrawBatch(RemoteMarketBatchView batch)
    {
        ImGui.ProgressBar(batch.TotalCount == 0 ? 0f : (float)batch.CompletedCount / batch.TotalCount, new Vector2(-1, 0),
            $"{batch.CompletedCount}/{batch.TotalCount}");
        if (batch.FailedCount > 0)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, $"{batch.FailedCount} failed");
        if (batch.Active && ImGui.Button("Cancel remaining"))
            controller.CancelBatch();
        if (!batch.Active && ImGui.Button("Clear batch"))
            controller.CancelBatch();
        if (controller.GetView().LastOutcome is { } outcome)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, outcome);
    }

    private void DrawSelectionFooter(RemoteMarketView view)
    {
        if (ImGui.SmallButton("All"))
        {
            foreach (var listing in view.Listings.Where(listing => !listing.AlreadyPurchased))
                selectedListingIds.Add(listing.ListingId);
            confirmArmed = false;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("None"))
        {
            selectedListingIds.Clear();
            confirmArmed = false;
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        ImGui.InputInt("##cheapestTarget", ref cheapestTarget, 0, 0);
        cheapestTarget = Math.Clamp(cheapestTarget, 1, 99999);
        ImGui.SameLine();
        if (ImGui.SmallButton("Select cheapest for N items"))
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
        var affordable = view.GilOnHand is null || view.GilOnHand.Value >= totalGil;
        var label = selectedListings.Length == 1
            ? $"Buy {selectedListings[0].Quantity}x {selectedListings[0].ItemName} — {totalGil:N0} gil"
            : $"Buy {selectedListings.Length} listings ({totalQuantity:N0} items) — {totalGil:N0} gil";
        if (!affordable)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, $"Insufficient gil for this selection ({view.GilOnHand:N0} on hand).");

        if (!confirmArmed)
        {
            if (ImGuiUi.Button(label, view.Available && view.ContextBlockReason is null && affordable))
                confirmArmed = true;
            if (view.ContextBlockReason is not null)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, view.ContextBlockReason);
            return;
        }

        ImGui.TextWrapped(label);
        if (ImGuiUi.Button("Confirm purchase", view.Available && view.ContextBlockReason is null && affordable))
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

    private unsafe IReadOnlyList<RemoteMarketListingView> SortListings(IReadOnlyList<RemoteMarketListingView> listings, ImGuiTableSortSpecsPtr sortSpecs)
    {
        if (sortSpecs.Handle == null || sortSpecs.SpecsCount == 0)
            return listings;
        var spec = sortSpecs.Specs;
        IEnumerable<RemoteMarketListingView> sorted = spec.ColumnIndex switch
        {
            0 => listings.OrderBy(listing => listing.Quantity, Comparer<uint>.Default),
            1 => listings.OrderBy(listing => listing.UnitPrice, Comparer<uint>.Default),
            2 => listings.OrderBy(listing => listing.TotalTax, Comparer<uint>.Default),
            3 => listings.OrderBy(listing => listing.TotalGil, Comparer<ulong>.Default),
            4 => listings.OrderBy(listing => listing.MateriaCount, Comparer<byte>.Default),
            _ => listings,
        };
        if (spec.SortDirection == ImGuiSortDirection.Descending)
            sorted = sorted.Reverse();
        return sorted.ToArray();
    }

    private IDalamudTextureWrap? ResolveItemIcon(uint itemId)
    {
        try
        {
            var item = Plugin.DataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
            if (item is null)
                return null;
            return Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(item.Value.Icon)).GetWrapOrEmpty();
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
